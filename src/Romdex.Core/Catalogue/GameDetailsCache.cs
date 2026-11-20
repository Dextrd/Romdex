using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Romdex.Client;
using Romdex.Contracts.Plugins.Metadata;
using Romdex.Core.Data;
using Romdex.Core.Plugins;

namespace Romdex.Core.Catalogue;

/// <summary>
/// What each game is, for its page: the details the metadata plugins give by the IDs its dumps were found with, such
/// as Wikipedia's by the article Hasheous links it to, with the links and description the lookups found. Looked up the
/// first time a page asks, and kept; looked up again after a while, or once its dumps are known by other IDs.
/// </summary>
public sealed class GameDetailsCache(IServiceScopeFactory scopes, CoverCache covers, ILogger<GameDetailsCache> logger) : IDisposable
{
    /// <summary>How long details are kept before they're looked up again, and how long after a lookup failed.</summary>
    public static readonly TimeSpan Again = TimeSpan.FromDays(30);

    public static readonly TimeSpan FailedAgain = TimeSpan.FromDays(1);

    // Changed when what's kept of a game changes, such as when its series' games were added, so it's looked up again.
    private const int KeptVersion = 4;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, Task<GameInfo>> _titles = new();
    // What's been found of each game being looked up, so far.
    private readonly ConcurrentDictionary<Guid, GameInfo> _soFar = new();
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>The title's game, as far as the plugins know it; empty when none do or none are on.</summary>
    public Task<GameInfo> GetAsync(Guid titleId, CancellationToken ct) => _titles.GetOrAdd(titleId, FindOnceAsync).WaitAsync(ct);

    /// <summary>
    /// The title's game, if it's found within <paramref name="patience"/>; else what's been found so far, marked as
    /// <see cref="GameInfo.Loading"/>, with the lookup carrying on.
    /// </summary>
    public async Task<GameInfo> GetSoFarAsync(Guid titleId, TimeSpan patience, CancellationToken ct)
    {
        var finding = _titles.GetOrAdd(titleId, FindOnceAsync);
        if (await Task.WhenAny(finding, Task.Delay(patience, ct)) == finding)
            return await finding;
        return (_soFar.GetValueOrDefault(titleId) ?? GameInfo.Empty) with { Loading = true };
    }

    private async Task<GameInfo> FindOnceAsync(Guid titleId)
    {
        try
        {
            await Task.Yield();
            // Not cancelled with a request, so a page that moves on doesn't waste what was found.
            var ct = _stopping.Token;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RomdexDbContext>();
            var plugins = await scope.ServiceProvider.GetRequiredService<MetadataPlugins>().GetAllAsync(ct);
            if (plugins.Count == 0)
                return GameInfo.Empty;
            await covers.LookUpTitleAsync(titleId, ct);

            // What the lookups found, the best dump's first: one the library has, then one with a cover.
            var found = await db.DumpArt.AsNoTracking()
                .Join(db.Dumps.Where(d => d.TitleId == titleId), a => a.DumpId, d => d.Id, (a, d) => new { Art = a, d.Id })
                .OrderByDescending(x => db.LibraryMatches.Any(m => m.DumpFile!.DumpId == x.Id))
                .ThenByDescending(x => x.Art.CoverFile != null)
                .Select(x => x.Art)
                .ToListAsync(ct);
            var ids = Merge(found.Select(a => a.IdsJson));
            var links = Merge(found.Select(a => a.LinksJson));
            var description = found.Select(a => a.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));

            // Shown as it's found: what the lookups found, then each plugin's details as they come.
            var system = await SystemAsync(db, titleId, ct);
            _soFar[titleId] = Info(titleId, null, [], links, description, system, []);
            var (details, images) = await DetailsAsync(db, titleId, ids, plugins,
                soFar => _soFar[titleId] = Info(titleId, soFar, [], links, description, system, []), ct);
            var series = await SeriesAsync(scope.ServiceProvider.GetRequiredService<Games>(), titleId, details, ct);
            return Info(titleId, details, images, links, description, system, series);
        }
        catch (OperationCanceledException)
        {
            return GameInfo.Empty;
        }
        finally
        {
            _titles.TryRemove(titleId, out _);
            _soFar.TryRemove(titleId, out _);
        }
    }

    // The plugins' details and pictures, kept from last time unless they're due again, or the game is known by other
    // IDs, or other plugins are on; the first plugin to say each thing wins. The plugins are asked all at once, and
    // what they've said so far is told as each answers.
    private async Task<(GameDetails?, IReadOnlyList<GameImage>)> DetailsAsync(RomdexDbContext db, Guid titleId, IReadOnlyDictionary<string, string> ids,
        IReadOnlyList<(PluginInstance Instance, IMetadataPlugin Plugin)> plugins, Action<GameDetails?> found, CancellationToken ct)
    {
        var idsJson = JsonSerializer.Serialize(new
        {
            kept = KeptVersion,
            ids = ids.OrderBy(i => i.Key, StringComparer.Ordinal).ToDictionary(),
            plugins = plugins.Select(p => p.Instance.Id),
        });
        var about = await db.GameAbout.FindAsync([titleId], ct);
        var now = DateTime.UtcNow;
        if (about is not null && about.IdsJson == idsJson && about.ImagesJson is not null && about.LookedUpAt > now - (about.Error is null ? Again : FailedAgain))
            return (Read<GameDetails>(about.DetailsJson), Read<List<GameImage>>(about.ImagesJson) ?? []);

        about ??= db.GameAbout.Add(new GameAbout { TitleId = titleId }).Entity;
        var game = await ImageQueryAsync(db, titleId, ids, ct);
        // Each plugin's answer in its place, so the first plugin's still wins whichever answers first.
        var answers = new GameDetails?[plugins.Count];
        var pictures = new IReadOnlyList<GameImage>[plugins.Count];
        string? error = null;
        var gate = new object();
        await Task.WhenAll(plugins.Select(async (p, i) =>
        {
            try
            {
                if (ids.Count > 0 && await p.Plugin.DetailsAsync(ids, ct) is { } details)
                    lock (gate)
                    {
                        answers[i] = details;
                        found(Merge(answers.OfType<GameDetails>().ToList()));
                    }
                if (game is not null)
                    pictures[i] = await p.Plugin.ImagesAsync(game, ct);
            }
            catch (Exception e) when (e is HttpRequestException or MetadataUnavailableException or JsonException)
            {
                logger.LogInformation("Couldn't look up a game's details for now: {Message}", e.Message);
                error = e.Message;
            }
        }));
        var merged = Merge(answers.OfType<GameDetails>().ToList());
        var images = pictures.Where(p => p is not null).SelectMany(p => p).ToList();
        // What was found before is kept when the lookup failed, until it can be looked up again.
        if (error is not null)
        {
            if (merged is null && Read<GameDetails>(about.DetailsJson) is { } before)
                merged = before;
            if (images.Count == 0 && Read<List<GameImage>>(about.ImagesJson) is { Count: > 0 } pictured)
                images = pictured;
        }
        // Each kind from the first plugin that has it, which may have several, such as screenshots.
        images = images.GroupBy(i => i.Kind).SelectMany(kind => kind.Where(i => i.Source == kind.First().Source)).DistinctBy(i => i.Url).ToList();
        (about.LookedUpAt, about.IdsJson, about.Error) = (now, idsJson, error);
        about.DetailsJson = merged is null ? null : JsonSerializer.Serialize(merged, Json);
        about.ImagesJson = JsonSerializer.Serialize(images, Json);
        await db.SaveChangesAsync(ct);
        return (merged, images);
    }

    /// <summary>The title's pictures, as the plugins gave them, by their place in the list its page is given.</summary>
    public static async Task<GameImage?> ImageAsync(RomdexDbContext db, Guid titleId, int index, CancellationToken ct) =>
        await db.GameAbout.AsNoTracking().Where(a => a.TitleId == titleId).Select(a => a.ImagesJson).FirstOrDefaultAsync(ct) is { } json
        && Read<List<GameImage>>(json) is { } images && index >= 0 && index < images.Count
            ? images[index]
            : null;

    // The title's system and dumps, as its DAT names them: those the library has first, then the one preferred, so
    // pictures are of the version people have.
    private static async Task<GameImageQuery?> ImageQueryAsync(RomdexDbContext db, Guid titleId, IReadOnlyDictionary<string, string> ids, CancellationToken ct)
    {
        var title = await db.Titles.AsNoTracking().Where(t => t.Id == titleId)
            .Select(t => new { System = t.System!.Name, t.PreferredDumpId }).FirstOrDefaultAsync(ct);
        if (title is null)
            return null;
        var dumps = await db.Dumps.AsNoTracking().Where(d => d.TitleId == titleId)
            .OrderByDescending(d => db.LibraryMatches.Any(m => m.DumpFile!.DumpId == d.Id))
            .ThenByDescending(d => d.Id == title.PreferredDumpId)
            .ThenBy(d => d.Name)
            .Select(d => d.Name)
            .ToListAsync(ct);
        return new GameImageQuery(title.System, dumps, ids);
    }

    private static GameDetails? Merge(IReadOnlyList<GameDetails> all)
    {
        if (all.Count == 0)
            return null;
        IReadOnlyList<T>? First<T>(Func<GameDetails, IReadOnlyList<T>?> pick) => all.Select(pick).FirstOrDefault(l => l is { Count: > 0 });
        var summary = all.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Summary));
        return new GameDetails(summary?.Source ?? all[0].Source, summary?.SourceUrl ?? all[0].SourceUrl, summary?.Summary,
            First(d => d.Released), First(d => d.Developers), First(d => d.Publishers), First(d => d.Genres), First(d => d.Modes), First(d => d.Series),
            First(d => d.Credits), First(d => d.Scores), First(d => d.Ratings), First(d => d.SeriesGames));
    }

    private static T? Read<T>(string? json) where T : class
    {
        try
        {
            return json is null ? null : JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Each lookup's IDs or links together, the first to give each one winning.
    private static Dictionary<string, string> Merge(IEnumerable<string> json)
    {
        var merged = new Dictionary<string, string>();
        foreach (var one in json)
        {
            Dictionary<string, string>? found;
            try
            {
                found = JsonSerializer.Deserialize<Dictionary<string, string>>(one);
            }
            catch (JsonException)
            {
                continue;
            }
            foreach (var (key, value) in found ?? [])
                if (value.Length > 0)
                    merged.TryAdd(key, value);
        }
        return merged;
    }

    // The names the title's system goes by on a platform's list: as its DAT names it after the maker, such as
    // "PlayStation 2" for "Sony - PlayStation 2", for short, such as "PS2", and as reviewers write it, such as "PSX".
    private static async Task<IReadOnlyList<string>> SystemAsync(RomdexDbContext db, Guid titleId, CancellationToken ct)
    {
        if (await db.Titles.AsNoTracking().Where(t => t.Id == titleId).Select(t => t.System!.Name).FirstOrDefaultAsync(ct) is not { } name)
            return [];
        return SystemNames.Names(name);
    }

    // Whether a platform, or one of a list of them such as "Xbox, PS2", is the system.
    private static bool IsSystem(string platforms, IReadOnlyList<string> system) =>
        platforms.Split(',', StringSplitOptions.TrimEntries).Any(p => system.Contains(p, StringComparer.OrdinalIgnoreCase));

    // Magazines about one maker's systems, whose scores are for those even where they don't say so.
    private static readonly string[] Nintendo = ["GB", "GBC", "GBA", "NES", "FDS", "SNES", "N64", "64DD", "GC", "Wii", "Wii U", "DS", "DSi", "3DS", "VB"];
    private static readonly string[] PlayStation = ["PS1", "PS2", "PS3", "PSP", "PSV"];
    private static readonly Dictionary<string, string[]> MagazinesFor = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Nintendo Power"] = Nintendo,
        ["Nintendo Life"] = Nintendo,
        ["Nintendo World Report"] = Nintendo,
        ["NGamer"] = Nintendo,
        ["N64 Magazine"] = ["N64"],
        ["NGC Magazine"] = ["GC"],
        ["Official Xbox Magazine"] = ["XBOX", "X360"],
        ["Official Xbox Magazine (UK)"] = ["XBOX", "X360"],
        ["Official PlayStation Magazine"] = PlayStation,
        ["Official PlayStation Magazine (UK)"] = PlayStation,
        ["PlayStation Magazine"] = PlayStation,
    };

    /// <summary>A reviewer's score as given for the system, as <see cref="ScoreHere(string, IReadOnlyList{string})"/>; none from a magazine about others.</summary>
    public static string? ScoreHere(string reviewer, string score, IReadOnlyList<string> system) =>
        MagazinesFor.TryGetValue(reviewer, out var only) && !only.Any(s => system.Contains(s, StringComparer.OrdinalIgnoreCase))
            ? null
            : ScoreHere(score, system);

    /// <summary>
    /// A score as given for the system: of one given for each platform, such as <c>(GC &amp; Xbox) 4.8/10 · (PS2) 4.7/10</c>
    /// or <c>XBOX: 73/100 · GCN: 72/100</c>, the system's alone, and none when it's only for others; one given without
    /// platforms as it is.
    /// </summary>
    public static string? ScoreHere(string score, IReadOnlyList<string> system)
    {
        var parts = score.Split(" · ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(Tagged).ToList();
        if (parts.All(p => p.Platforms is null))
            return score;
        var mine = parts.Where(p => p.Platforms?.Any(t => system.Contains(t, StringComparer.OrdinalIgnoreCase)) == true).ToList();
        // Else what it gave without saying for which, which is more likely all of them than one it didn't name.
        if (mine.Count == 0)
            mine = parts.Where(p => p.Platforms is null).ToList();
        return mine.Count == 0 ? null : string.Join(" · ", mine.Select(p => p.Score));
    }

    // One platform's part of a score: "(GC & Xbox) 4.8/10", "4.8/10 (GC)" or "GC: 4.8/10", with its platforms; none
    // when it names none, such as "8/10 (Japan)".
    private static (IReadOnlyList<string>? Platforms, string Score) Tagged(string part)
    {
        foreach (var pattern in new[] { @"^\((?<on>[^)]+)\)\s*(?<score>.+)$", @"^(?<score>.+?)\s*\((?<on>[^)]+)\)$", @"^(?<on>[^:]{1,40}):\s*(?<score>.+)$" })
        {
            var match = Regex.Match(part, pattern);
            if (!match.Success)
                continue;
            var on = Regex.Split(match.Groups["on"].Value, @"\s*(?:&|,|/|\band\b)\s*").Where(p => p.Length > 0).ToList();
            if (on.Count > 0 && on.All(SystemNames.IsPlatform))
                return (on, match.Groups["score"].Value.Trim());
        }
        return (null, part);
    }

    private static GameInfo Info(Guid titleId, GameDetails? details, IReadOnlyList<GameImage> images, IReadOnlyDictionary<string, string> links,
        string? description, IReadOnlyList<string> system, IReadOnlyList<SeriesGames> series)
    {
        // The releases on this title's system, where the plugins say which were where.
        var released = details?.Released ?? [];
        var here = released.Where(r => r.Platform is { } platform && IsSystem(platform, system)).ToList();
        if (here.Count > 0)
            released = here;
        var (summary, source, url) = details?.Summary is { } text ? (text, details.Source, details.SourceUrl)
            : description is not null ? (description, "Hasheous, written by AI", (string?)null)
            : (null, null, null);
        var allLinks = links.Select(l => new GameLink(l.Key, l.Value)).ToList();
        // The page the details came from, in place of a link to the same site's, which may be written another way.
        if (details?.SourceUrl is { } page && allLinks.All(l => l.Url != page))
        {
            allLinks.RemoveAll(l => l.Site.Equals(details.Source, StringComparison.OrdinalIgnoreCase));
            allLinks.Add(new GameLink(details.Source, page));
        }
        return new GameInfo(summary, source, url,
            released.OrderBy(r => r.Date, StringComparer.Ordinal).Select(r => new ReleaseDate(r.Region, r.Date, here.Count > 0 ? null : r.Platform)).ToList(),
            details?.Developers ?? [], details?.Publishers ?? [], details?.Genres ?? [], details?.Modes ?? [], details?.Series ?? [],
            (details?.Credits ?? []).Select(c => new Credit(c.Role, c.Names)).ToList(),
            (details?.Scores ?? []).Select(s => ScoreHere(s.Reviewer, s.Score, system) is { } score ? new ReviewScore(s.Reviewer, score) : null).OfType<ReviewScore>().ToList(),
            allLinks.OrderBy(l => l.Site, StringComparer.OrdinalIgnoreCase).ToList(),
            (details?.Ratings ?? []).Select(r => new AgeRating(r.Board, r.Rating, r.Name)).ToList(),
            // Served by the core, which fetches and keeps each the first time it's shown.
            images.Select((image, i) => new Screenshot(image.Kind, $"api/v1/titles/{titleId}/images/{i}", image.Source)).ToList(),
            series);
    }

    // The games of each of the game's series that the catalogue has, by their names the same but for punctuation, as
    // Search joins games: on every system, without this one.
    private static async Task<IReadOnlyList<SeriesGames>> SeriesAsync(Games games, Guid titleId, GameDetails? details, CancellationToken ct)
    {
        if (details?.SeriesGames is not { Count: > 0 } all)
            return [];
        var byTitle = await games.GetAsync(ct);
        var byName = new Dictionary<string, IReadOnlyList<GameTitle>>();
        foreach (var game in byTitle.Values.Distinct())
            foreach (var title in game)
                byName.TryAdd(Games.Key(title.Title.Name), game);
        // Each game once: in the first series it's in, such as Sonic Adventure before the Sonic the Hedgehog games.
        var shown = new HashSet<IReadOnlyList<GameTitle>>();
        if (byTitle.GetValueOrDefault(titleId) is { } mine)
            shown.Add(mine);
        var found = new List<SeriesGames>();
        foreach (var series in all)
        {
            var listed = series.Games.Select(name => byName.GetValueOrDefault(Games.Key(name))).OfType<IReadOnlyList<GameTitle>>()
                .Where(shown.Add).Select(Api.CatalogueEndpoints.Game).ToList();
            if (listed.Count > 0)
                found.Add(new SeriesGames(series.Name, listed));
        }
        return found;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
