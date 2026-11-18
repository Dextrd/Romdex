using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Romdex.Client;
using Romdex.Core.Access;
using Romdex.Core.Apps;
using Romdex.Core.Data;
using Romdex.Core.Library;
using Romdex.Core.Play;
using SharpCompress.Archives.SevenZip;

namespace Romdex.Core.Api;

/// <summary>Playing games the library has in the browser, and opening them in the connected app.</summary>
public static class PlayEndpoints
{
    public const string EmulatorClient = "emulator";

    public static RouteGroupBuilder MapPlayEndpoints(this RouteGroupBuilder api)
    {
        var play = api.MapGroup("/play").WithTags("Play");

        play.MapGet("/{id:guid}", async (Guid id, PlayService service, CancellationToken ct) =>
                await service.GetAsync(id, ct) is { } info ? Results.Ok(info) : Results.NotFound())
            .RequireScope(Scopes.Play).WithName("GetPlay")
            .WithSummary("How to play a dump: a signed link to the player page, good for a few hours, or why it can't play in the browser. "
                         + "Says which connected app it can open in too, if any.")
            .Produces<PlayInfo>().Produces(StatusCodes.Status404NotFound);

        play.MapPost("/{id:guid}/app", async (Guid id, RomdexDbContext db, AppLinks apps, CancellationToken ct) =>
            {
                var file = await db.LibraryMatches.AsNoTracking().Where(m => m.DumpFile!.DumpId == id)
                    .Select(m => (Guid?)m.Content!.LibraryFileId).FirstOrDefaultAsync(ct);
                if (file is not { } fileId)
                    return Results.NotFound();
                return await apps.LinkAsync(fileId, ct) is { } link
                    ? Results.Ok(link)
                    : Results.Problem("No connected app opens games. Connect one under Settings → Plugins.", statusCode: StatusCodes.Status409Conflict,
                        title: "No app to open it in");
            })
            .RequireScope(Scopes.Play).WithName("OpenInApp")
            .WithSummary("Where a dump the library has is in the connected app, such as RomM. When the app hasn't found it yet, it's asked to scan, "
                         + "and the answer says to try again.")
            .Produces<AppLink>().Produces(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        play.MapGet("/{id:guid}/player", async (Guid id, long? exp, string? sig, HttpContext http, RomdexDbContext db, PlayService service, CancellationToken ct) =>
            {
                if (!service.Verify(id, exp, sig))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                if (await PlanAsync(id, db, service, ct) is not { } plan)
                    return Results.NotFound();
                // Threaded cores need the page cross-origin isolated; every file it loads is Romdex's own, so it can be.
                if (plan.Emulator.Threads)
                {
                    http.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
                    http.Response.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";
                }
                http.Response.Headers.CacheControl = "no-store";
                return Results.Content(PlayerPage.Write(plan, service.Query(id)), "text/html; charset=utf-8");
            })
            .AllowAnyone().ExcludeFromDescription();

        // HEAD too: the player asks it first, to see whether the copy it kept last time is still the one.
        play.MapMethods("/{id:guid}/game/{name}", ["GET", "HEAD"], async (Guid id, string name, long? exp, string? sig, HttpContext http, RomdexDbContext db,
                PlayService service, IOptions<RomdexOptions> options, CancellationToken ct) =>
            {
                if (!service.Verify(id, exp, sig))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                if (await PlanAsync(id, db, service, ct) is not { } plan || plan.Game.Files.Select(f => InLibrary(options.Value, f.Path)).ToList() is not { } paths
                    || paths.Any(p => p is null || !File.Exists(p)))
                    return Results.NotFound();
                if (!plan.Game.Zipped)
                    return Results.File(paths[0]!, "application/octet-stream", plan.Game.Name, enableRangeProcessing: true);
                // A disc's zip isn't made just to say how big it would be; with no size to compare, the player fetches it.
                if (HttpMethods.IsHead(http.Request.Method))
                    return Results.Ok();
                // Stored, not compressed: the tracks are big and compress badly, and the player unpacks it at once. Made in a
                // file of its own, which goes when it's been sent, so the player can resume it.
                var temporary = new FileStream(Path.Combine(Path.GetTempPath(), $"romdex-play-{Guid.NewGuid():N}.zip"), FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                await using (var zip = new ZipArchive(temporary, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var (file, path) in plan.Game.Files.Zip(paths))
                    {
                        var entry = zip.CreateEntry(file.Name, CompressionLevel.NoCompression);
                        await using var to = await entry.OpenAsync(ct);
                        await using var from = File.OpenRead(path!);
                        await from.CopyToAsync(to, ct);
                    }
                }
                temporary.Position = 0;
                return Results.File(temporary, "application/zip", plan.Game.Name, enableRangeProcessing: true);
            })
            .AllowAnyone().WithName("GetPlayGame")
            .WithSummary("The game the player loads: its library file, or a disc's files together as one zip. Needs the signature from the player's link.")
            .Produces(StatusCodes.Status200OK).Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound);

        play.MapGet("/{id:guid}/bios/{name}", async (Guid id, string name, long? exp, string? sig, RomdexDbContext db, PlayService service,
                IOptions<RomdexOptions> options, CancellationToken ct) =>
            {
                if (!service.Verify(id, exp, sig))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                if (await PlanAsync(id, db, service, ct) is not { Bios: { } bios } || InLibrary(options.Value, bios.Path) is not { } path || !File.Exists(path))
                    return Results.NotFound();
                if (bios.Entry is null)
                    return Results.File(path, "application/octet-stream", bios.Name);
                return await EntryAsync(path, bios.Entry, ct) is { } bytes ? Results.File(bytes, "application/octet-stream", bios.Name) : Results.NotFound();
            })
            .AllowAnyone().WithName("GetPlayBios")
            .WithSummary("The BIOS the player loads with the game, named as the emulator looks for it. Needs the signature from the player's link.")
            .Produces(StatusCodes.Status200OK).Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status404NotFound);

        return api;
    }

    /// <summary>
    /// EmulatorJS's own files, from the emulatorjs container, at <c>/emulator/</c>: the player loads them from Romdex, so
    /// they're on the same origin as the game. They're public, as they are anywhere EmulatorJS is hosted.
    /// </summary>
    public static IEndpointRouteBuilder MapEmulator(this IEndpointRouteBuilder app)
    {
        app.MapGet("/emulator/{**path}", async (string? path, HttpContext http, IHttpClientFactory clients, IOptions<RomdexOptions> options) =>
        {
            var root = options.Value.EmulatorUrl.TrimEnd('/');
            if (root.Length == 0 || path is null || path.Split('/').Contains(".."))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{root}/{path}{http.Request.QueryString}");
            foreach (var header in new[] { "If-None-Match", "If-Modified-Since", "Range" })
                if (http.Request.Headers.TryGetValue(header, out var value))
                    request.Headers.TryAddWithoutValidation(header, value.ToArray());
            HttpResponseMessage response;
            try
            {
                response = await clients.CreateClient(EmulatorClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
            }
            catch (HttpRequestException)
            {
                http.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }
            using (response)
            {
                http.Response.StatusCode = (int)response.StatusCode;
                foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
                    if (name is "Content-Type" or "Content-Length" or "Content-Range" or "Accept-Ranges" or "ETag" or "Last-Modified" or "Cache-Control" or "Content-Encoding")
                        http.Response.Headers[name] = values.ToArray();
                http.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
                if (response.StatusCode != HttpStatusCode.NotModified)
                    await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
            }
        }).ExcludeFromDescription();
        return app;
    }

    private static async Task<PlayPlan?> PlanAsync(Guid id, RomdexDbContext db, PlayService service, CancellationToken ct) =>
        await db.Dumps.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) is { } dump ? (await service.ResolveAsync(dump, ct)).Plan : null;

    // The full path of a library-relative path, or null if it would leave the library.
    private static string? InLibrary(RomdexOptions options, string relative)
    {
        var root = Path.GetFullPath(options.LibraryPath);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    // A BIOS inside an archive: small, so read whole.
    private static async Task<byte[]?> EntryAsync(string path, string entry, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        if (ContainerReader.ContainerOf(path) == Container.Zip)
        {
            await using var zip = await ZipFile.OpenReadAsync(path, ct);
            if (zip.GetEntry(entry) is not { } found)
                return null;
            await using var stream = await found.OpenAsync(ct);
            await stream.CopyToAsync(memory, ct);
            return memory.ToArray();
        }
        using var archive = SevenZipArchive.OpenArchive(path);
        if (archive.Entries.FirstOrDefault(e => e.Key == entry) is not { } seven)
            return null;
        await using (var stream = seven.OpenEntryStream())
            await stream.CopyToAsync(memory, ct);
        return memory.ToArray();
    }
}

/// <summary>The player page: EmulatorJS set up for one game, filling the window.</summary>
public static class PlayerPage
{
    public static string Write(PlayPlan plan, string query)
    {
        static string Js(string? value) => JsonSerializer.Serialize(value);
        var name = WebUtility.HtmlEncode(plan.Dump.Name);
        // Links are relative to the page, at api/v1/play/{id}/player, so Romdex works under a path of its own too.
        var game = $"game/{Uri.EscapeDataString(plan.Game.Name)}?{query}";
        var bios = plan.Bios is { } b ? $"bios/{Uri.EscapeDataString(b.Name)}?{query}" : "";
        const string data = "../../../../emulator/data/";
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{name}} · Romdex</title>
            <style>
              html, body { margin: 0; height: 100%; background: #000; color: #ddd; font: 14px system-ui, sans-serif; }
              body { display: flex; flex-direction: column; }
              header { display: flex; gap: 12px; align-items: center; padding: 8px 16px; background: #15171c; }
              header a { color: #8ab4ff; text-decoration: none; }
              header span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
              main { flex: 1; min-height: 0; }
              #game { width: 100%; height: 100%; }
              .unplayable { max-width: 36em; margin: 15vh auto; padding: 0 16px; line-height: 1.5; }
            </style>
            </head>
            <body>
            <header><a href="../../../../titles/{{plan.Dump.TitleId}}">← Back</a><span>{{name}}</span></header>
            <main><div id="game"></div></main>
            <script>
              EJS_player = "#game";
              EJS_core = {{Js(plan.Emulator.Core)}};
              EJS_gameName = {{Js(plan.Dump.Name)}};
              EJS_gameUrl = {{Js(game)}};
              EJS_biosUrl = {{Js(bios)}};
              EJS_pathtodata = {{Js(data)}};
              EJS_startOnLoaded = true;
              // Its own cache of games is keyed by link, and each link here is signed afresh, so it would never be used:
              // it would only keep another copy of each game, in memory while it's stored, and on disk after.
              EJS_disableDatabases = true;
              EJS_threads = {{(plan.Emulator.Threads ? "true" : "false")}};
              // The emulator draws with WebGL: without it, it only says it failed to start, so say why instead.
              const canvas = document.createElement("canvas");
              if (canvas.getContext("webgl2") || canvas.getContext("webgl")) {
                const loader = document.createElement("script");
                loader.src = EJS_pathtodata + "loader.js";
                document.body.append(loader);
              } else {
                document.getElementById("game").outerHTML = '<p class="unplayable">This browser can\'t play games: the emulator draws with WebGL, '
                  + "which it doesn't have, or has turned off. Most browsers have it; check that hardware acceleration is on.</p>";
              }
            </script>
            </body>
            </html>
            """;
    }
}
