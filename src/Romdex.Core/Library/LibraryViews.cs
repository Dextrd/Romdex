using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Romdex.Client;
using Romdex.Core.Catalogue;
using Romdex.Core.Data;

namespace Romdex.Core.Library;

/// <summary>
/// One dump, with where it stands. A <see cref="Hidden"/> dump is one the system's preferences leave out; its
/// <see cref="Tier"/> is the best of its regions' in them.
/// </summary>
public sealed record DumpView(
    Guid Id,
    Guid TitleId,
    string Name,
    string Release,
    string? Disc,
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> Languages,
    string Revision,
    DumpKinds Kinds,
    DumpStatus Status,
    bool Hidden,
    RegionTier Tier = RegionTier.Fallback)
{
    public DumpRef Ref => new(Id, Name, Regions);
}

/// <summary>One title, with where it stands and the dump 1G1R prefers. <see cref="Shown"/> is false for a title the preferences leave out.</summary>
/// <param name="Fit">How well what the library has fits the region tiers; null when it has no whole release.</param>
/// <param name="WantedMissing">Wanted regions the game came out in that the library has no whole release of.</param>
public sealed record TitleView(Guid Id, string Name, TitleStatus Status, DumpView Preferred, DumpView? Have, bool Ignored, bool Shown, IReadOnlyList<DumpView> Dumps,
    RegionFit? Fit = null, IReadOnlyList<string>? WantedMissing = null);

/// <summary>A system worked out: its summary, its titles and its BIOS.</summary>
public sealed record SystemView(SystemSummary Summary, IReadOnlyList<TitleView> Titles, IReadOnlyList<DumpView> Bios)
{
    public IReadOnlyDictionary<Guid, TitleView> ById { get; } = Titles.ToDictionary(t => t.Id);
}

/// <summary>
/// Systems as last worked out, kept until something changes them: a scan, a catalogue update, a preference, an ignore.
/// Owned and missing are never stored as fact, so this is only a cache.
/// </summary>
public sealed class LibraryViews
{
    private readonly ConcurrentDictionary<Guid, SystemView> _systems = new();

    public SystemView? Get(Guid systemId) => _systems.GetValueOrDefault(systemId);

    public void Set(SystemView view) => _systems[view.Summary.Id] = view;

    public void Invalidate() => _systems.Clear();
}

/// <summary>
/// Works out a system from the catalogue and the library's matches. A dump is good when every file verifies; a title is
/// owned when a whole release of it is, every disc, whichever region; 1G1R picks the release it prefers by the system's
/// regions, languages and latest revision.
/// </summary>
public sealed class LibraryViewBuilder(RomdexDbContext db, LibraryViews cache, Preferences preferences)
{
    // Sheets that a CHD makes up its own of, so the DAT's can't be expected: a disc in a CHD is whole when its tracks are.
    private static readonly string[] Sheets = [".cue", ".gdi"];

    public async Task<IReadOnlyList<SystemView>> GetAllAsync(CancellationToken ct)
    {
        var ids = await db.Systems.Where(s => s.BiosForId == null).OrderBy(s => s.Name).Select(s => s.Id).ToListAsync(ct);
        var views = new List<SystemView>();
        foreach (var id in ids)
            if (await GetAsync(id, ct) is { } view)
                views.Add(view);
        return views;
    }

    public async Task<SystemView?> GetAsync(Guid systemId, CancellationToken ct)
    {
        if (cache.Get(systemId) is { } cached)
            return cached;
        var system = await db.Systems.AsNoTracking().FirstOrDefaultAsync(s => s.Id == systemId, ct);
        if (system is null)
            return null;
        var view = await BuildAsync(system, ct);
        cache.Set(view);
        return view;
    }

    private async Task<SystemView> BuildAsync(CatalogueSystem system, CancellationToken ct)
    {
        var (prefs, ownPreferences) = await preferences.GetAsync(system.Name, ct);
        var systemIds = await db.Systems.Where(s => s.Id == system.Id || s.BiosForId == system.Id).Select(s => s.Id).ToListAsync(ct);
        var dumps = await db.Dumps.AsNoTracking().Where(d => systemIds.Contains(d.SystemId)).ToListAsync(ct);
        var files = await db.DumpFiles.AsNoTracking().Where(f => systemIds.Contains(f.Dump!.SystemId))
            .Select(f => new { f.Id, f.DumpId, f.Name }).ToListAsync(ct);
        var matches = await db.LibraryMatches.AsNoTracking().Where(m => systemIds.Contains(m.DumpFile!.Dump!.SystemId))
            .Select(m => new { m.DumpFileId, m.ContentId, m.Content!.File!.Container }).ToListAsync(ct);
        // Files the library has by name but not by hash: likely bad or modified dumps of these.
        var names = files.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmatchedNames = await UnmatchedNamesAsync(ct);
        var badNames = unmatchedNames.Where(names.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matchesByFile = matches.ToLookup(m => m.DumpFileId);
        var filesByDump = files.ToLookup(f => f.DumpId);
        // Files someone said a dump can do without.
        var kept = (await db.KeptIncompletes.AsNoTracking().Where(k => systemIds.Contains(k.Dump!.SystemId)).ToListAsync(ct))
            .ToDictionary(k => k.DumpId, k => k.MissingNames);

        // First, which dumps are whole; then which contents those explain, so a track shared with a whole dump of
        // another region doesn't make that region look incomplete.
        bool Whole(Dump dump)
        {
            var own = filesByDump[dump.Id].ToList();
            if (own.Count == 0)
                return false;
            var fromChd = own.Any(f => matchesByFile[f.Id].Any(m => m.Container == Container.Chd));
            var doesWithout = kept.GetValueOrDefault(dump.Id);
            return own.Any(f => matchesByFile[f.Id].Any())
                && own.All(f => matchesByFile[f.Id].Any() || fromChd && IsSheet(f.Name) || doesWithout?.Contains(f.Name) == true);
        }
        var whole = dumps.Where(Whole).Select(d => d.Id).ToHashSet();
        var fileDump = files.ToDictionary(f => f.Id, f => f.DumpId);
        var explained = matches.Where(m => whole.Contains(fileDump[m.DumpFileId])).Select(m => m.ContentId).ToHashSet();

        DumpStatus StatusOf(Dump dump)
        {
            if (whole.Contains(dump.Id))
                return DumpStatus.Good;
            var own = filesByDump[dump.Id].ToList();
            if (own.Any(f => matchesByFile[f.Id].Any(m => !explained.Contains(m.ContentId))))
                return DumpStatus.Incomplete;
            return own.Any(f => badNames.Contains(f.Name)) ? DumpStatus.Bad : DumpStatus.Missing;
        }

        var hidden = DumpKindNames.ToFlags(prefs.Hidden);
        DumpView ViewOf(Dump dump)
        {
            var regions = Split(dump.Regions);
            // A dump only of regions never to be fetched is left out, as a demo is, unless it's owned.
            var tier = prefs.TierOf(regions);
            var isHidden = (dump.Kinds & hidden) != 0 || tier == RegionTier.Never;
            // Everything in a DAT of BIOS files only is a BIOS, whether or not the DAT marks each one.
            var kinds = dump.SystemId == system.Id ? dump.Kinds : dump.Kinds | DumpKinds.Bios;
            return new DumpView(dump.Id, dump.TitleId, dump.Name, dump.Release, dump.Disc, regions, Split(dump.Languages), dump.Revision, kinds,
                StatusOf(dump), isHidden, tier);
        }
        var views = dumps.Select(ViewOf).ToList();

        var titles = await db.Titles.AsNoTracking().Where(t => t.SystemId == system.Id).ToListAsync(ct);
        var dumpsByTitle = views.Where(v => (v.Kinds & DumpKinds.Bios) == 0).ToLookup(v => v.TitleId);
        var titleViews = new List<TitleView>();
        foreach (var title in titles)
        {
            var own = dumpsByTitle[title.Id].ToList();
            if (own.Count == 0)
                continue;
            // Whether a game is owned, and which release is preferred, goes by the game itself, not its updates and
            // DLC; a title of DLC alone, whose game the DAT doesn't have, goes by those.
            var games = own.Where(d => (d.Kinds & DumpKinds.AddOn) == 0).ToList() is { Count: > 0 } roms ? roms : own;
            var releases = games.GroupBy(d => d.Release).Select(g => g.OrderBy(d => d.Disc, StringComparer.OrdinalIgnoreCase).ToList()).ToList();
            var complete = releases.Where(r => r.All(d => d.Status == DumpStatus.Good)).ToList();
            var status = complete.Count > 0 ? TitleStatus.Owned
                : games.Any(d => d.Status == DumpStatus.Good) ? TitleStatus.Partial
                : games.Any(d => d.Status is DumpStatus.Incomplete or DumpStatus.Bad) ? TitleStatus.Bad
                : TitleStatus.Missing;
            var visible = releases.Where(r => !r[0].Hidden).ToList();
            var chosen = title.PreferredDumpId is { } chosenId ? releases.FirstOrDefault(r => r.Any(d => d.Id == chosenId)) : null;
            var preferred = chosen ?? Rank(visible.Count > 0 ? visible : releases, prefs).First();
            var have = complete.Contains(preferred) ? preferred : complete.Count > 0 ? Rank(complete, prefs).First() : null;
            var (fit, wantedMissing) = FitOf(games.Where(d => (d.Kinds & hidden) == 0).ToList(), complete.SelectMany(r => r).ToList(), prefs);
            titleViews.Add(new TitleView(title.Id, title.Name, status, preferred[0], have?[0], title.IgnoredAt is not null,
                visible.Count > 0 || status != TitleStatus.Missing, own, fit, wantedMissing));
        }
        titleViews.Sort((a, b) => string.Compare(DumpNames.SortKey(a.Name), DumpNames.SortKey(b.Name), StringComparison.OrdinalIgnoreCase));

        var bios = views.Where(v => (v.Kinds & DumpKinds.Bios) != 0).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var shown = titleViews.Where(t => t.Shown && !t.Ignored).ToList();
        var summary = new SystemSummary(system.Id, system.Name, system.Source, system.DatVersion, system.DatDate,
            shown.Count,
            shown.Count(t => t.Status == TitleStatus.Owned),
            shown.Count(t => t.Status == TitleStatus.Partial),
            shown.Count(t => t.Status == TitleStatus.Bad),
            shown.Count(t => t.Status == TitleStatus.Missing),
            titleViews.Count(t => t.Ignored),
            bios.Count,
            bios.Count(b => b.Status == DumpStatus.Good),
            ownPreferences,
            SystemNames.Short(system.Name));
        return new SystemView(summary, titleViews, bios);
    }

    /// <summary>
    /// The releases of a title, best first: the most preferred region by the tiers (World as good as the first wanted
    /// region), then language, then a release that isn't an
    /// alternate dump, then the latest revision, then retail before anything else.
    /// </summary>
    public static IEnumerable<List<DumpView>> Rank(IEnumerable<List<DumpView>> releases, SystemPreferences prefs) =>
        releases
            .OrderBy(r => prefs.RankOf(r[0].Regions))
            .ThenBy(r => RankIn(r[0].Languages, prefs.Languages))
            .ThenBy(r => (r[0].Kinds & DumpKinds.Alternate) != 0)
            .ThenByDescending(r => r[0].Revision, StringComparer.Ordinal)
            .ThenBy(r => System.Numerics.BitOperations.PopCount((uint)r[0].Kinds))
            .ThenBy(r => r[0].Name, StringComparer.Ordinal);

    /// <summary>
    /// How well whole releases the library has fit the region tiers. A dump covers the wanted regions it names, and a
    /// World dump every one. Gold is every wanted region the game came out in, or, out in none, a fallback as good as
    /// any there is; green is some wanted region; amber a fallback while there's better; red only never regions.
    /// </summary>
    /// <param name="released">The game's dumps, other than kinds the preferences leave out, such as demos.</param>
    /// <param name="owned">The dumps of releases the library has whole.</param>
    public static (RegionFit? Fit, IReadOnlyList<string> WantedMissing) FitOf(IReadOnlyList<DumpView> released, IReadOnlyList<DumpView> owned, SystemPreferences prefs)
    {
        IEnumerable<string> Wanted(DumpView d) =>
            d.Regions.Any(r => r.Equals(SystemPreferences.World, StringComparison.OrdinalIgnoreCase)) && prefs.TierOf(SystemPreferences.World) != RegionTier.Never
                ? prefs.Wanted
                : prefs.Wanted.Where(w => d.Regions.Contains(w, StringComparer.OrdinalIgnoreCase));
        // In the order they're wanted, the main one first.
        var covered = released.Concat(owned).SelectMany(Wanted).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = prefs.Wanted.Where(covered.Contains).ToList();
        var have = owned.SelectMany(Wanted).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = available.Where(r => !have.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
        if (owned.Count == 0)
            return (null, missing);
        var usable = owned.Where(d => d.Tier != RegionTier.Never).ToList();
        if (available.Count > 0)
            return (missing.Count == 0 ? RegionFit.Best : have.Count > 0 ? RegionFit.Some : usable.Count > 0 ? RegionFit.Fallback : RegionFit.Never, missing);
        // Out in no wanted region: the best fallback there is will do.
        if (usable.Count == 0)
            return (RegionFit.Never, missing);
        var best = released.Concat(owned).Where(d => d.Tier != RegionTier.Never).Min(d => prefs.RankOf(d.Regions));
        return (usable.Min(d => prefs.RankOf(d.Regions)) <= best ? RegionFit.Best : RegionFit.Fallback, missing);
    }

    private static int RankIn(IReadOnlyList<string> values, IReadOnlyList<string> order)
    {
        var best = int.MaxValue;
        foreach (var value in values)
        {
            for (var index = 0; index < order.Count && index < best; index++)
                if (order[index].Equals(value, StringComparison.OrdinalIgnoreCase))
                    best = index;
        }
        return best;
    }

    private async Task<List<string>> UnmatchedNamesAsync(CancellationToken ct)
    {
        var unmatched = await db.LibraryContents.AsNoTracking().Where(c => !c.Matches.Any() && c.File!.HiddenAt == null
                && !db.ManualMatches.Any(m => m.TitleId != null && (m.Sha1 == c.Sha1 || m.Sha1 == c.HeaderlessSha1)))
            .Select(c => new { c.Entry, c.File!.Path }).ToListAsync(ct);
        return unmatched.Select(c => Path.GetFileName(c.Entry ?? c.Path)).ToList();
    }

    internal static bool IsSheet(string name) => Sheets.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> Split(string value) => value.Length == 0 ? [] : value.Split(',');
}
