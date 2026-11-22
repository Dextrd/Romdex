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
using Romdex.Contracts.Plugins.Players;
using Romdex.Core.Play;
using Romdex.Core.Plugins;
using SharpCompress.Archives.SevenZip;

namespace Romdex.Core.Api;

/// <summary>Playing games the library has in the browser, and opening them in the connected app.</summary>
public static class PlayEndpoints
{
    /// <summary>The client players' own files are fetched with, to pass on to their pages.</summary>
    public const string PlayerFilesClient = "player-files";

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
                // A threaded emulator needs the page cross-origin isolated; every file it loads is Romdex's own, so it can be.
                if (plan.Support.Isolated)
                {
                    http.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
                    http.Response.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";
                }
                http.Response.Headers.CacheControl = "no-store";
                // Links are relative to the page, at api/v1/play/{id}/player, so Romdex works under a path of its own too.
                var query = service.Query(id);
                const string root = "../../../../";
                var page = plan.Player.WritePage(new PlayerGame(plan.Dump.Name, plan.System,
                    $"game/{Uri.EscapeDataString(plan.Game.Name)}?{query}", plan.Game.Name,
                    plan.Bios is { } bios ? $"bios/{Uri.EscapeDataString(bios.Name)}?{query}" : null, plan.Bios?.Name,
                    $"{root}players/{plan.Instance.Id}/", $"{root}titles/{plan.Dump.TitleId}"));
                return Results.Content(page, "text/html; charset=utf-8");
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
    /// Each player's own files, such as EmulatorJS's from the emulatorjs container, at <c>/players/{instance}/</c>: its
    /// page loads them from Romdex, so they're on the same origin as the game. They're public, as they are anywhere an
    /// emulator is hosted.
    /// </summary>
    public static IEndpointRouteBuilder MapPlayerFiles(this IEndpointRouteBuilder app)
    {
        app.MapGet("/players/{instance:guid}/{**path}", async (Guid instance, string? path, HttpContext http, IHttpClientFactory clients,
            PlayerPlugins players) =>
        {
            var files = (await players.GetAllAsync(http.RequestAborted)).FirstOrDefault(p => p.Instance.Id == instance).Player?.FilesUrl;
            if (files is null || path is null || path.Split('/').Contains(".."))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{files.AbsoluteUri.TrimEnd('/')}/{path}{http.Request.QueryString}");
            foreach (var header in new[] { "If-None-Match", "If-Modified-Since", "Range" })
                if (http.Request.Headers.TryGetValue(header, out var value))
                    request.Headers.TryAddWithoutValidation(header, value.ToArray());
            HttpResponseMessage response;
            try
            {
                response = await clients.CreateClient(PlayerFilesClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
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
