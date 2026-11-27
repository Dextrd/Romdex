using Microsoft.EntityFrameworkCore;
using Romdex.Client;
using Romdex.Core.Access;
using Romdex.Core.Catalogue;
using Romdex.Core.Data;
using Romdex.Core.Library;
using Romdex.Core.Play;
using Romdex.Core.Tasks;

namespace Romdex.Core.Api;

/// <summary>Systems and titles with where each stands, 1G1R preferences, and what's ignored.</summary>
public static class CatalogueEndpoints
{
    public static RouteGroupBuilder MapCatalogueEndpoints(this RouteGroupBuilder api)
    {
        var systems = api.MapGroup("/systems").WithTags("Catalogue");

        systems.MapGet("", async (LibraryViewBuilder views, CancellationToken ct) =>
                (await views.GetAllAsync(ct)).Select(v => v.Summary).ToList())
            .WithName("ListSystems").WithSummary("Every system with how much of it is owned, after 1G1R, and its DAT's version.");

        systems.MapGet("/{id:guid}", async (Guid id, LibraryViewBuilder views, CancellationToken ct) =>
                await views.GetAsync(id, ct) is { } view ? Results.Ok(view.Summary) : Results.NotFound())
            .WithName("GetSystem")
            .WithSummary("A system: its DAT, and how much of it the library has, after its 1G1R preferences.")
            .Produces<SystemSummary>().Produces(StatusCodes.Status404NotFound);

        systems.MapGet("/{id:guid}/titles", async (Guid id, LibraryViewBuilder views, TitleStatus? status, string? q, bool? ignored, bool? hidden,
                bool? have, RegionFit? fit, int? skip, int? take, CancellationToken ct) =>
            {
                if (await views.GetAsync(id, ct) is not { } view)
                    return Results.NotFound();
                var titles = Filter(view.Titles, status, q, have == true ? null : ignored ?? false, hidden ?? false, have ?? false, fit);
                return Results.Ok(Page(titles.Select(t => Summary(id, t)), skip, take));
            })
            .WithName("ListTitles")
            .WithSummary("A system's titles, by name. By default the ones its preferences list and nobody ignored; ignored=true lists the ignored ones, hidden=true adds those the preferences leave out, have=true lists only what the library has some of, and fit lists only those whose region dot is that.")
            .Produces<TitlePage>().Produces(StatusCodes.Status404NotFound);

        systems.MapGet("/{id:guid}/bios", async (Guid id, LibraryViewBuilder views, RomdexDbContext db, PlayService play, CancellationToken ct) =>
                await views.GetAsync(id, ct) is { } view
                    ? Results.Ok(new BiosList(await DumpInfosAsync(db, view.Bios, null, ct), await play.BiosNeedAsync(view, ct)))
                    : Results.NotFound())
            .WithName("ListBios")
            .WithSummary("A system's BIOS and firmware files, which many of its games need to run, and the BIOS its emulator in the browser looks for.")
            .Produces<BiosList>().Produces(StatusCodes.Status404NotFound);

        systems.MapGet("/{id:guid}/preferences", async (Guid id, RomdexDbContext db, Preferences preferences, CancellationToken ct) =>
            {
                if (await db.Systems.FindAsync([id], ct) is not { } system)
                    return Results.NotFound();
                var (found, own) = await preferences.GetAsync(system.Name, ct);
                return Results.Ok(new SystemPreferencesInfo(found, own));
            })
            .WithName("GetSystemPreferences")
            .WithSummary("A system's 1G1R preferences: its own, or everyone's default when it has none.")
            .Produces<SystemPreferencesInfo>().Produces(StatusCodes.Status404NotFound);
        systems.MapPut("/{id:guid}/preferences", async (Guid id, SystemPreferences body, RomdexDbContext db, Preferences preferences, CancellationToken ct) =>
            {
                if (await db.Systems.FindAsync([id], ct) is not { } system)
                    return Results.NotFound();
                if (Preferences.Validate(body) is { Count: > 0 } errors)
                    return Results.ValidationProblem(errors);
                await preferences.SaveAsync(system.Name, body, ct);
                return Results.Ok(new SystemPreferencesInfo(body, true));
            })
            .RequireScope(Scopes.Download).WithName("SetSystemPreferences").WithSummary("Gives a system 1G1R preferences of its own.")
            .Produces<SystemPreferencesInfo>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound);
        systems.MapDelete("/{id:guid}/preferences", async (Guid id, RomdexDbContext db, Preferences preferences, CancellationToken ct) =>
            {
                if (await db.Systems.FindAsync([id], ct) is not { } system)
                    return Results.NotFound();
                await preferences.SaveAsync(system.Name, null, ct);
                return Results.Ok(new SystemPreferencesInfo(await preferences.GetDefaultAsync(ct), false));
            })
            .RequireScope(Scopes.Download).WithName("ResetSystemPreferences").WithSummary("Puts a system back on everyone's default preferences.")
            .Produces<SystemPreferencesInfo>().Produces(StatusCodes.Status404NotFound);

        systems.MapGet("/{id:guid}/report.csv", async (Guid id, LibraryViewBuilder views, RomdexDbContext db, CancellationToken ct) =>
                await views.GetAsync(id, ct) is { } view ? Results.Text(await IgirReport.WriteAsync(db, view, ct), "text/csv") : Results.NotFound())
            .WithName("GetSystemReport")
            .WithSummary("Every dump in the system's DAT and whether the library has it whole, in the shape of igir's report, to compare the two.")
            .Produces<string>(contentType: "text/csv").Produces(StatusCodes.Status404NotFound);

        api.MapGet("/preferences", async (Preferences preferences, CancellationToken ct) => await preferences.GetDefaultAsync(ct))
            .WithTags("Catalogue").WithName("GetDefaultPreferences").WithSummary("The 1G1R preferences of every system without its own.");
        api.MapPut("/preferences", async (SystemPreferences body, Preferences preferences, CancellationToken ct) =>
            {
                if (Preferences.Validate(body) is { Count: > 0 } errors)
                    return Results.ValidationProblem(errors);
                await preferences.SaveDefaultAsync(body, ct);
                return Results.Ok(body);
            })
            .RequireScope(Scopes.Download).WithTags("Catalogue").WithName("SetDefaultPreferences")
            .WithSummary("Sets the 1G1R preferences every system without its own follows.")
            .Produces<SystemPreferences>().ProducesValidationProblem();

        var titles = api.MapGroup("/titles").WithTags("Catalogue");

        titles.MapGet("", async (LibraryViewBuilder views, HttpContext http, Marks.MarkService marks, TitleStatus? status, string? q, bool? ignored,
                bool? hidden, bool? have, RegionFit? fit, bool? starred, int? skip, int? take, CancellationToken ct) =>
            {
                // Only what you've starred, as Melodex's Library star shows.
                var stars = starred == true && Access.Caller.Of(http).UserId is { } user ? await marks.StarredAsync(user, ct) : null;
                var found = new List<TitleSummary>();
                foreach (var view in await views.GetAllAsync(ct))
                    found.AddRange(Filter(view.Titles, status, q, have == true ? null : ignored ?? false, hidden ?? false, have ?? false, fit)
                        .Where(t => starred != true || stars?.Contains(t.Id) == true)
                        .Select(t => Summary(view.Summary.Id, t)));
                return Page(found.OrderBy(t => DumpNames.SortKey(t.Name), StringComparer.OrdinalIgnoreCase), skip, take);
            })
            .WithName("ListAllTitles")
            .WithSummary("Every system's titles together, by name, filtered as a system's are; with starred, only those you've starred.");

        titles.MapGet("/{id:guid}", async (Guid id, RomdexDbContext db, LibraryViewBuilder views, Games games, CancellationToken ct) =>
                await DetailAsync(id, db, views, ct, games) is { } detail ? Results.Ok(detail) : Results.NotFound())
            .WithName("GetTitle").WithSummary("A title with every dump of it, their hashes, which one 1G1R prefers, and where the library has them.")
            .Produces<TitleDetail>().Produces(StatusCodes.Status404NotFound);

        titles.MapGet("/{id:guid}/cover", async (Guid id, HttpContext http, CoverCache covers, CancellationToken ct) =>
            {
                // A cover not looked up yet can take a while, as sources are rate-limited. Browsers make only a few
                // requests at once to a server, so a page of covers on their way would hold up everything else: answer
                // 503 at once instead. The page asks after them all together (covers/status).
                var finding = covers.GetAsync(id, ct);
                if (await Task.WhenAny(finding, Task.Delay(CoverPatience, ct)) != finding)
                {
                    http.Response.Headers.RetryAfter = "2";
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
                if (await finding is not { } found)
                    return Results.NotFound();
                http.Response.Headers.CacheControl = "private, max-age=0, stale-while-revalidate=604800";
                return Results.File(found.Path, found.ContentType, lastModified: found.UpdatedAt,
                    entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{found.UpdatedAt.Ticks:x}\""));
            })
            .WithName("GetTitleCover")
            .WithSummary("The title's cover, from the metadata plugins, such as Hasheous, whether the library has the game or not: looked up the first time "
                         + "it's asked for, then kept. 404 when there's none; 503 with Retry-After while it's still being looked up.")
            .Produces(StatusCodes.Status200OK, contentType: "image/png").Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        titles.MapGet("/{id:guid}/images/{index:int}", async (Guid id, int index, HttpContext http, RomdexDbContext db, GameImages images, CancellationToken ct) =>
            {
                if (await GameDetailsCache.ImageAsync(db, id, index, ct) is not { } image || await images.GetAsync(image.Url, ct) is not { } found)
                    return Results.NotFound();
                http.Response.Headers.CacheControl = "private, max-age=604800";
                return Results.File(found.Path, found.ContentType);
            })
            .WithName("GetGameImage")
            .WithSummary("One of the pictures of the title's game listed with what it is (about): fetched from where the metadata plugin found it the first "
                         + "time it's asked for, then kept.")
            .Produces(StatusCodes.Status200OK, contentType: "image/png").Produces(StatusCodes.Status404NotFound);

        titles.MapGet("/{id:guid}/about", async (Guid id, bool? partial, RomdexDbContext db, GameDetailsCache details, CancellationToken ct) =>
                !await db.Titles.AnyAsync(t => t.Id == id, ct) ? Results.NotFound()
                : Results.Ok(partial == true ? await details.GetSoFarAsync(id, AboutPatience, ct) : await details.GetAsync(id, ct)))
            .WithName("GetGameInfo")
            .WithSummary("What the title's game is, from the metadata plugins, such as Hasheous and Wikipedia: what it's about, when it came out, who made it, "
                         + "its reviews, age ratings, pictures, the other games in its series and pages about it elsewhere. Looked up the first time it's "
                         + "asked for, which can take a few seconds, then kept. With partial=true, what's been found so far after a second at most, marked "
                         + "loading until there's no more to come.")
            .Produces<GameInfo>().Produces(StatusCodes.Status404NotFound);

        titles.MapPost("/covers/status", async (string[] urls, CoverCache covers) =>
            {
                // Each is looked for at once, and those still on their way after a moment are pending.
                var asked = urls.Distinct().Take(500).Select(url => (Url: url, Match: CoverUrl.Match(url))).ToList();
                var finding = asked.Where(a => a.Match.Success).ToDictionary(a => a.Url, a => covers.GetAsync(Guid.Parse(a.Match.Groups[1].Value), CancellationToken.None));
                await Task.WhenAny(Task.WhenAll(finding.Values), Task.Delay(CoverStatusPatience));
                return asked.ToDictionary(a => a.Url, a => !finding.TryGetValue(a.Url, out var found) ? "none"
                    : !found.IsCompleted ? "pending"
                    : found.IsCompletedSuccessfully && found.Result is not null ? "ready" : "none");
            })
            .WithName("GetCoverStatus")
            .WithSummary("For covers that answered 503, which are ready now, which there are none of, and which are still pending, by URL. "
                         + "One request for a page of them, rather than one each.");

        titles.MapPost("/{id:guid}/ignore", (Guid id, RomdexDbContext db, LibraryViews cache, LibraryViewBuilder views, CancellationToken ct) =>
                SetIgnoredAsync(id, true, db, cache, views, ct))
            .RequireScope(Scopes.Download).WithName("IgnoreTitle").WithSummary("Never list the title as missing. Doing it again changes nothing.")
            .Produces<TitleDetail>().Produces(StatusCodes.Status404NotFound);
        titles.MapDelete("/{id:guid}/ignore", (Guid id, RomdexDbContext db, LibraryViews cache, LibraryViewBuilder views, CancellationToken ct) =>
                SetIgnoredAsync(id, false, db, cache, views, ct))
            .RequireScope(Scopes.Download).WithName("UnignoreTitle").WithSummary("List the title again.")
            .Produces<TitleDetail>().Produces(StatusCodes.Status404NotFound);

        titles.MapPut("/{id:guid}/preferred", async (Guid id, ChoosePreferred body, RomdexDbContext db, LibraryViews cache, LibraryViewBuilder views, CancellationToken ct) =>
            {
                if (await db.Titles.FindAsync([id], ct) is not { } title)
                    return Results.NotFound();
                if (body.DumpId is { } dumpId && !await db.Dumps.AnyAsync(d => d.Id == dumpId && d.TitleId == id, ct))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["dumpId"] = ["That isn't a dump of this title."] });
                title.PreferredDumpId = body.DumpId;
                await db.SaveChangesAsync(ct);
                cache.Invalidate();
                return Results.Ok(await DetailAsync(id, db, views, ct));
            })
            .RequireScope(Scopes.Download).WithName("ChoosePreferredDump").WithSummary("Prefer one dump of a title over what 1G1R picks, or let 1G1R pick again.")
            .Produces<TitleDetail>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound);

        api.MapGet("/catalogue/search", async (string q, Games games, Guid? system, bool? have, int? skip, int? take, CancellationToken ct) =>
            {
                var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (words.Length == 0)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["q"] = ["Say what to search for."] });
                // A game matches when any of its titles does, as its name elsewhere may not be what was searched for. Its
                // titles are put in order again: owned first, as before, then the one named as searched for, which names it.
                var found = new List<(IReadOnlyList<GameTitle> Titles, int Rank)>();
                var seen = new HashSet<Guid>();
                foreach (var (_, game) in await games.GetAsync(ct))
                {
                    if (!seen.Add(game[0].Title.Id))
                        continue;
                    var ranked = game.Select(t => (Title: t, Rank: SearchRank(t.Title, q.Trim(), words) ?? int.MaxValue)).ToList();
                    var rank = ranked.Min(r => r.Rank);
                    if (rank == int.MaxValue)
                        continue;
                    found.Add((ranked.OrderBy(r => r.Title.Title.Status).ThenBy(r => r.Rank).Select(r => r.Title).ToList(), rank));
                }
                var matched = found
                    .Where(g => system is not { } id || g.Titles.Any(t => t.System.Summary.Id == id))
                    .Where(g => have is null || have == (g.Titles[0].Title.Status != TitleStatus.Missing))
                    .OrderBy(g => g.Rank).ThenByDescending(g => Games.Reach(g.Titles)).ThenByDescending(g => g.Titles.Count)
                    .ThenBy(g => DumpNames.SortKey(g.Titles[0].Title.Name), StringComparer.OrdinalIgnoreCase)
                    .Select(g => Game(g.Titles))
                    .ToList();
                return Results.Ok(new GamePage(matched.Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(take ?? 100, 1, 1000)).ToList(), matched.Count));
            })
            .WithTags("Catalogue").WithName("SearchCatalogue")
            .WithSummary("Every game the DATs list, in the library or not, whose name or a dump's name has every word searched for, once with its title on every "
                         + "system it's on. A game named just that comes first, then those whose names have the words, then those only a dump's name "
                         + "has; among each, the most widely released first, counting the regions it came out in on each system. system narrows it to "
                         + "games on one system, have to games the library has on some system (true) or on none (false).")
            .Produces<GamePage>().ProducesValidationProblem();

        api.MapPost("/catalogue/refresh", async (TaskQueue queue, TaskTitles titles, CancellationToken ct) =>
                titles.ToInfo(await queue.EnqueueAsync(CatalogueRefreshTask.TaskKind, null, ct)))
            .RequireScope(Scopes.Scan).WithTags("Catalogue").WithName("RefreshCatalogue")
            .WithSummary("Fetches new DATs where a plugin can, and imports every DAT that changed. Asking while one is queued returns that one.");

        return api;
    }

    // How long a cover is waited for before answering 503, and how long the status waits for those it's asked about.
    private static readonly TimeSpan CoverPatience = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CoverStatusPatience = TimeSpan.FromMilliseconds(250);

    // How long a game's page waits for its details before it's given what's been found so far.
    private static readonly TimeSpan AboutPatience = TimeSpan.FromSeconds(1);

    private static readonly System.Text.RegularExpressions.Regex CoverUrl =
        new(@"titles/([0-9a-fA-F-]{36})/cover", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static IEnumerable<TitleView> Filter(IEnumerable<TitleView> titles, TitleStatus? status, string? q, bool? ignored, bool hidden, bool have = false,
        RegionFit? fit = null) =>
        titles.Where(t => (hidden || t.Shown)
                          && (fit is null || t.Fit == fit)
                          && (!have || t.Status != TitleStatus.Missing)
                          && (ignored is null || t.Ignored == ignored)
                          && (status is null || t.Status == status)
                          && (string.IsNullOrWhiteSpace(q) || t.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)
                                                           || t.Dumps.Any(d => d.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))));

    // How close a title is to what was searched for, best first; null when it doesn't match. Its own name counts for
    // more than its dumps', which also name regions and versions. Where a name only starts with what was searched for
    // doesn't count: "zelda" is as much The Legend of Zelda as Zelda's Adventure.
    private static int? SearchRank(TitleView title, string q, string[] words)
    {
        bool Has(string name) => words.All(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (Games.Key(title.Name) == Games.Key(q))
            return 0;
        if (Has(title.Name))
            return 1;
        return title.Dumps.Any(d => Has(d.Name)) ? 2 : null;
    }

    public static TitleSummary Summary(Guid systemId, TitleView t) =>
        new(t.Id, systemId, t.Name, t.Status, t.Preferred.Ref, t.Have?.Ref, t.Ignored, t.Dumps.Count, CoverPath(t.Id), t.Fit, t.WantedMissing);

    // A game, named and pictured as its best title, the one the library has if any.
    public static GameSummary Game(IReadOnlyList<GameTitle> titles) =>
        new(titles[0].Title.Id, titles[0].Title.Name, titles[0].Title.Status, CoverPath(titles[0].Title.Id),
            titles.Select(t => Summary(t.System.Summary.Id, t.Title)).ToList());

    public static string CoverPath(Guid titleId) => $"api/v1/titles/{titleId}/cover";

    public static TitlePage Page(IEnumerable<TitleSummary> items, int? skip, int? take)
    {
        var all = items.ToList();
        return new TitlePage(all.Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(take ?? 100, 1, 1000)).ToList(), all.Count);
    }

    private static async Task<IResult> SetIgnoredAsync(Guid id, bool ignored, RomdexDbContext db, LibraryViews cache, LibraryViewBuilder views, CancellationToken ct)
    {
        if (await db.Titles.FindAsync([id], ct) is not { } title)
            return Results.NotFound();
        if (ignored != title.IgnoredAt is not null)
        {
            title.IgnoredAt = ignored ? DateTime.UtcNow : null;
            await db.SaveChangesAsync(ct);
            cache.Invalidate();
        }
        return Results.Ok(await DetailAsync(id, db, views, ct));
    }

    // Its versions on other systems are listed when games are given: they're worked out across the whole catalogue.
    private static async Task<TitleDetail?> DetailAsync(Guid id, RomdexDbContext db, LibraryViewBuilder views, CancellationToken ct, Games? games = null)
    {
        var title = await db.Titles.AsNoTracking().Include(t => t.System).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (title is null || await views.GetAsync(title.SystemId, ct) is not { } view || !view.ById.TryGetValue(id, out var titleView))
            return null;
        var preferred = titleView.Dumps.Where(d => d.Release == titleView.Preferred.Release).Select(d => d.Id).ToHashSet();
        var dumps = await DumpInfosAsync(db, titleView.Dumps.OrderBy(d => !preferred.Contains(d.Id)).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase), preferred, ct);
        var art = await CoverCache.BestAsync(db, id, ct);
        var knownAs = art?.Name is { } name && !Games.Key(name).Equals(Games.Key(title.Name), StringComparison.Ordinal) ? name : null;
        var versions = games is null || !(await games.GetAsync(ct)).TryGetValue(id, out var game) ? null
            : game.Select(t => new TitleVersion(t.Title.Id, t.System.Summary.Id, t.System.Summary.ShortName ?? t.System.Summary.Name, t.Title.Name, t.Title.Status)).ToList();
        return new TitleDetail(title.Id, title.SystemId, title.System!.Name, title.Name, titleView.Status, titleView.Ignored, title.PreferredDumpId, dumps,
            CoverPath(id), knownAs, versions, titleView.Fit, titleView.WantedMissing);
    }

    /// <summary>Dumps with their files, hashes and library places, in the order given.</summary>
    public static async Task<IReadOnlyList<DumpInfo>> DumpInfosAsync(RomdexDbContext db, IEnumerable<DumpView> dumps, IReadOnlySet<Guid>? preferred, CancellationToken ct)
    {
        var list = dumps.ToList();
        var ids = list.Select(d => d.Id).ToList();
        var files = await db.DumpFiles.AsNoTracking().Where(f => ids.Contains(f.DumpId)).ToListAsync(ct);
        var fileIds = files.Select(f => f.Id).ToList();
        var places = (await db.LibraryMatches.AsNoTracking().Where(m => fileIds.Contains(m.DumpFileId))
                .Select(m => new { m.DumpFileId, m.Content!.LibraryFileId, m.Content.File!.Path, m.Content.Entry })
                .ToListAsync(ct))
            .ToLookup(m => m.DumpFileId, m => new LibraryPlace(m.LibraryFileId, m.Path, m.Entry));
        var serials = await db.Dumps.AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Serial, ct);
        var byDump = files.ToLookup(f => f.DumpId);
        return list.Select(d => new DumpInfo(d.Id, d.Name, serials.GetValueOrDefault(d.Id), d.Regions, d.Languages, DumpKindNames.FromFlags(d.Kinds), d.Disc, d.Status,
                preferred?.Contains(d.Id) ?? false, d.Hidden,
                byDump[d.Id].OrderBy(f => f.Name, StringComparer.Ordinal)
                    .Select(f => new DumpFileInfo(f.Name, f.Size, f.Crc32, f.Md5, f.Sha1, places[f.Id].ToList())).ToList(), d.Tier))
            .ToList();
    }
}
