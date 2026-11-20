using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Romdex.Client;
using Romdex.Core.Catalogue;
using Romdex.Core.Data;
using Romdex.Core.Library;

namespace Romdex.Core.Downloads;

/// <summary>
/// Compares games in the library with where the layout would file them, for formatting a library that was there
/// before Romdex, or after the layout changes; and tells what layout a library is in already. It only reports:
/// <see cref="LibraryFormatTask"/> moves them.
/// </summary>
/// <remarks>
/// A game is where it would be filed when one copy of it is: an archive, CHD or RVZ named after the dump in the
/// system's folder, a lone file named as the DAT names it, or a game of several loose files in a folder of its own.
/// That's how the igir worker files them. Files that match nothing aren't games, and are never moved.
/// </remarks>
public sealed class Adoption(RomdexDbContext db, LibraryFolders folders, IOptions<RomdexOptions> options)
{
    private sealed record Place(Guid DumpId, string DumpName, string Regions, DumpKinds Kinds, string FileName, string Path, string? Entry, Container Container);

    /// <param name="Shared">The system's archives that hold more than one game.</param>
    private sealed record Game(CatalogueSystem System, List<Place> Places, List<string> Roms, IReadOnlySet<string> Shared)
    {
        public Place First => Places[0];
        public bool Bios => First.Kinds.HasFlag(DumpKinds.Bios) || System.BiosForId is not null;
    }

    public Task<AdoptionReport> ReportAsync(CatalogueSystem system, CancellationToken ct) => ReportAsync([system], ct);

    /// <summary>Where these systems' games would go; every system's, for null.</summary>
    public async Task<AdoptionReport> ReportAsync(IReadOnlyList<CatalogueSystem>? systems, CancellationToken ct)
    {
        systems ??= await db.Systems.AsNoTracking().Where(s => db.Dumps.Any(d => d.SystemId == s.Id && d.Files.Any(f => db.LibraryMatches.Any(m => m.DumpFileId == f.Id))))
            .ToListAsync(ct);
        var root = Path.GetFullPath(options.Value.LibraryPath);
        var moves = new List<AdoptionMove>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        int checkedGames = 0, inPlace = 0;
        foreach (var system in systems.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var games = await GamesAsync(system, ct);
            checkedGames += games.Count;
            foreach (var game in games)
            {
                string folder;
                try
                {
                    folder = await folders.ForAsync(system, game.Bios, game.Roms, ct);
                }
                catch (Exception e) when (e is FilingWorkerException or HttpRequestException)
                {
                    return new AdoptionReport(checkedGames, 0, [], $"The igir worker couldn't name the folder: {e.Message}");
                }
                var copies = Copies(game.Places, game.Roms).ToList();
                var expected = copies.Select(c => Expected(folder, game.First.DumpName, game.Roms, c)).ToList();
                if (copies.Zip(expected).Any(pair => pair.First.SequenceEqual(pair.Second, StringComparer.Ordinal)))
                {
                    inPlace++;
                    continue;
                }
                var (from, to) = (copies[0], expected[0]);
                moves.Add(new AdoptionMove(Ref(game.First), from, to, Blocked(game, from, to, root, claimed)));
            }
        }
        return new AdoptionReport(checkedGames, inPlace, [.. moves.OrderBy(m => m.Dump.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>The layout the library's games are in now: the one that names the most systems' folders as they are.</summary>
    public async Task<LayoutDetection> DetectAsync(CancellationToken ct)
    {
        var systems = await db.Systems.AsNoTracking().Where(s => db.Dumps.Any(d => d.SystemId == s.Id && d.Files.Any(f => db.LibraryMatches.Any(m => m.DumpFileId == f.Id))))
            .ToListAsync(ct);
        // Each system's folder, as the most of its games have it, split into where it is and its own name.
        var found = new List<(CatalogueSystem System, string Parent, string Name, int Games, List<string> Roms)>();
        var bios = new List<(CatalogueSystem Owner, string Folder)>();
        foreach (var system in systems)
        {
            var owner = await folders.OwnerAsync(system, ct);
            var games = await GamesAsync(system, ct);
            foreach (var game in games.Where(g => g.Bios))
                bios.Add((owner, FolderOf(game)));
            var places = games.Where(g => !g.Bios).Select(g => (Folder: FolderOf(g), g.Roms)).ToList();
            if (places.Count == 0 || owner != system)
                continue;
            var most = places.GroupBy(p => p.Folder).OrderByDescending(g => g.Count()).First();
            var cut = most.Key.LastIndexOf('/');
            found.Add((system, cut < 0 ? "" : most.Key[..cut], most.Key[(cut + 1)..], most.Count(), most.First().Roms));
        }
        if (found.Count == 0)
            return new LayoutDetection(null, 0, 0, []);

        var gamesFolder = found.GroupBy(f => f.Parent).OrderByDescending(g => g.Sum(f => f.Games)).First().Key;
        var names = new Dictionary<(LibraryLayout, Guid), string>();
        try
        {
            foreach (var layout in LibraryFolders.Presets)
                foreach (var f in found)
                    names[(layout, f.System.Id)] = await folders.NameAsync(f.System.Name, layout, f.Roms, ct);
        }
        catch (Exception e) when (e is FilingWorkerException or HttpRequestException)
        {
            return new LayoutDetection(null, found.Count, 0, [], $"The igir worker couldn't name folders: {e.Message}");
        }
        var inGames = found.Where(f => f.Parent == gamesFolder).ToList();
        var best = LibraryFolders.Presets
            .Select(layout => (Layout: layout, Fitting: inGames.Count(f => names[(layout, f.System.Id)] == f.Name)))
            .OrderByDescending(l => l.Fitting).First();

        // BIOS files: with their system's games, in a folder per system, or all in one folder.
        var votes = bios.Select(b =>
        {
            var system = found.Where(f => f.System.Id == b.Owner.Id).Select(f => (f.Parent, f.Name)).FirstOrDefault();
            if (system.Name is not null && b.Folder == LibraryFolders.Join(system.Parent, system.Name))
                return (Folder: "", BySystem: true);
            var own = system.Name ?? names.GetValueOrDefault((best.Layout, b.Owner.Id));
            var cut = b.Folder.LastIndexOf('/');
            if (cut >= 0 && b.Folder[(cut + 1)..] == own)
                return (Folder: b.Folder[..cut], BySystem: true);
            return (Folder: b.Folder, BySystem: false);
        }).GroupBy(v => v).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? (Folder: "", BySystem: true);

        var others = inGames.Where(f => names[(best.Layout, f.System.Id)] != f.Name)
            .Select(f => new SystemFolder(f.System.Id, f.System.Name, f.Name))
            .OrderBy(f => f.System, StringComparer.OrdinalIgnoreCase).ToList();
        return new LayoutDetection(new LibraryFormat(best.Layout, gamesFolder, votes.Folder, votes.BySystem), found.Count, best.Fitting, others);
    }

    // The folder a game's in, as its system's: a game of several loose files in a folder of its own is in the one above.
    private static string FolderOf(Game game)
    {
        var copy = Copies(game.Places, game.Roms).First();
        var folder = Parent(copy[0]);
        if (copy.Count > 1 && folder.Length > 0 && folder[(folder.LastIndexOf('/') + 1)..] == Safe(game.First.DumpName))
            folder = Parent(folder);
        return folder;
    }

    private static string Parent(string path) => path.LastIndexOf('/') is var cut and >= 0 ? path[..cut] : "";

    // A system's games in the library, whole: every file somewhere, but for the cue sheet of a disc in a CHD, which
    // makes up its own.
    private async Task<List<Game>> GamesAsync(CatalogueSystem system, CancellationToken ct)
    {
        var places = await db.LibraryMatches.AsNoTracking().Where(m => m.DumpFile!.Dump!.SystemId == system.Id)
            .Select(m => new Place(m.DumpFile!.DumpId, m.DumpFile.Dump!.Name, m.DumpFile.Dump.Regions, m.DumpFile.Dump.Kinds, m.DumpFile.Name,
                m.Content!.File!.Path, m.Content.Entry, m.Content.File.Container))
            .ToListAsync(ct);
        var dumpIds = places.Select(p => p.DumpId).Distinct().ToList();
        var files = (await db.DumpFiles.AsNoTracking().Where(f => dumpIds.Contains(f.DumpId)).Select(f => new { f.DumpId, f.Name }).ToListAsync(ct))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToLookup(f => f.DumpId, f => f.Name);
        var shared = places.Where(p => p.Container != Container.Raw).GroupBy(p => p.Path)
            .Where(g => g.Select(p => p.DumpId).Distinct().Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return places.GroupBy(p => p.DumpId)
            .Where(g => files[g.Key].All(name => g.Any(p => p.FileName == name) || g.Any(p => p.Container == Container.Chd) && LibraryViewBuilder.IsSheet(name)))
            .Select(g => new Game(system, g.ToList(), files[g.Key].ToList(), shared))
            .Where(g => Copies(g.Places, g.Roms).Any())
            .ToList();
    }

    // Why a game can't move: an archive that holds other games too, a file another game moves as well, or something
    // already where it'd go that isn't moving away.
    private static string? Blocked(Game game, IReadOnlyList<string> from, IReadOnlyList<string> to, string root, HashSet<string> claimed)
    {
        if (from.Count == 1 && game.Shared.Contains(from[0]))
            return "Its archive holds other games too.";
        foreach (var (source, target) in from.Zip(to))
        {
            if (source == target)
                continue;
            if (!claimed.Add(target))
                return $"Another game goes to {target} too.";
            if (!claimed.Add("from:" + source))
                return $"{source} is another game's too.";
            // Only a different case of the same name may be there: it's the same file, on some filesystems.
            if (File.Exists(Path.Combine(root, target)) && !string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                return $"Something's already at {target}.";
        }
        return null;
    }

    private static DumpRef Ref(Place place) => new(place.DumpId, place.DumpName, place.Regions.Length == 0 ? [] : place.Regions.Split(','));

    // Each copy of the game in the library, as the library files that make it up: an archive, CHD or RVZ holding it all,
    // or the loose files, if they're all there.
    private static IEnumerable<IReadOnlyList<string>> Copies(List<Place> places, List<string> roms)
    {
        foreach (var container in places.Where(p => p.Container != Container.Raw).GroupBy(p => p.Path))
            if (roms.All(r => container.Any(p => p.FileName == r) || container.First().Container == Container.Chd && LibraryViewBuilder.IsSheet(r)))
                yield return [container.Key];
        var loose = places.Where(p => p.Container == Container.Raw).ToList();
        if (roms.All(r => loose.Any(p => p.FileName == r)))
            yield return [.. roms.Select(r => loose.First(p => p.FileName == r).Path)];
    }

    private static IReadOnlyList<string> Expected(string folder, string dump, List<string> roms, IReadOnlyList<string> copy)
    {
        if (copy.Count == 1 && ContainerReader.ContainerOf(copy[0]) != Container.Raw)
            return [LibraryFolders.Join(folder, $"{Safe(dump)}{Path.GetExtension(copy[0]).ToLowerInvariant()}")];
        return roms.Count == 1
            ? [LibraryFolders.Join(folder, roms[0])]
            : [.. roms.Select(r => LibraryFolders.Join(folder, $"{Safe(dump)}/{r}"))];
    }

    // A dump's name as a file name, without the characters a path can't hold.
    private static string Safe(string name) => string.Concat(name.Select(c => c is '/' or '\\' or ':' ? '_' : c));
}
