using Microsoft.EntityFrameworkCore;
using Romdex.Client;
using Romdex.Core.Data;
using Romdex.Core.Library;

namespace Romdex.Core.Downloads;

/// <summary>
/// Compares a system's games in the library with where Downloads would file them, for a library that was there before
/// Romdex. It only reports: nothing is moved.
/// </summary>
/// <remarks>
/// A game is where it would be filed when one copy of it is: an archive, CHD or RVZ named after the dump in the
/// system's folder, a lone file named as the DAT names it, or a game of several loose files in a folder of its own.
/// That's how the igir worker files them.
/// </remarks>
public sealed class Adoption(RomdexDbContext db, Filing filing, IFilingWorker worker)
{
    private sealed record Place(Guid DumpId, string DumpName, string Regions, string FileName, string Path, string? Entry, Container Container);

    public async Task<AdoptionReport> ReportAsync(CatalogueSystem system, CancellationToken ct)
    {
        var settings = await filing.GetAsync(system.Name, ct);
        var places = await db.LibraryMatches.AsNoTracking().Where(m => m.DumpFile!.Dump!.SystemId == system.Id)
            .Select(m => new Place(m.DumpFile!.DumpId, m.DumpFile.Dump!.Name, m.DumpFile.Dump.Regions, m.DumpFile.Name,
                m.Content!.File!.Path, m.Content.Entry, m.Content.File.Container))
            .ToListAsync(ct);
        var dumpIds = places.Select(p => p.DumpId).Distinct().ToList();
        var files = (await db.DumpFiles.AsNoTracking().Where(f => dumpIds.Contains(f.DumpId)).Select(f => new { f.DumpId, f.Name }).ToListAsync(ct))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToLookup(f => f.DumpId, f => f.Name);

        // Games only: whole, every file somewhere, but for the cue sheet of a disc in a CHD, which makes up its own.
        var whole = places.GroupBy(p => p.DumpId)
            .Where(g => files[g.Key].All(name => g.Any(p => p.FileName == name) || g.Any(p => p.Container == Container.Chd) && LibraryViewBuilder.IsSheet(name)))
            .ToList();
        if (whole.Count == 0)
            return new AdoptionReport(0, 0, []);

        // igir names the RomM folder, and only the worker has igir's names for consoles.
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var moves = new List<AdoptionMove>();
        var inPlace = 0;
        foreach (var game in whole)
        {
            var roms = files[game.Key].ToList();
            string folder;
            try
            {
                folder = await FolderAsync(system.Name, settings.Layout, roms, folders, ct);
            }
            catch (Exception e) when (e is FilingWorkerException or HttpRequestException)
            {
                return new AdoptionReport(whole.Count, 0, [], $"The igir worker couldn't name the folder: {e.Message}");
            }
            var copies = Copies(game.ToList(), roms).ToList();
            var expected = copies.Select(c => Expected(folder, game.First().DumpName, roms, c)).ToList();
            if (copies.Zip(expected).Any(pair => pair.First.SequenceEqual(pair.Second, StringComparer.Ordinal)))
            {
                inPlace++;
                continue;
            }
            var first = game.First();
            moves.Add(new AdoptionMove(new DumpRef(first.DumpId, first.DumpName, first.Regions.Length == 0 ? [] : first.Regions.Split(',')), copies[0], expected[0]));
        }
        return new AdoptionReport(whole.Count, inPlace, [.. moves.OrderBy(m => m.Dump.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    private async Task<string> FolderAsync(string system, LibraryLayout layout, List<string> roms, Dictionary<string, string> known, CancellationToken ct)
    {
        // igir goes by the DAT's name before the files', so one game's extensions stand for the rest with the same ones.
        var key = string.Join(",", roms.Select(r => Path.GetExtension(r).ToLowerInvariant()).Distinct().Order());
        if (known.TryGetValue(key, out var folder))
            return folder;
        folder = (await worker.FolderAsync(new FolderRequest(system, layout == LibraryLayout.Romm ? "romm" : "datName", roms), ct)).Folder;
        known[key] = folder;
        return folder;
    }

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
            return [$"{folder}/{Safe(dump)}{Path.GetExtension(copy[0]).ToLowerInvariant()}"];
        return roms.Count == 1
            ? [$"{folder}/{roms[0]}"]
            : [.. roms.Select(r => $"{folder}/{Safe(dump)}/{r}")];
    }

    // A dump's name as a file name, without the characters a path can't hold.
    private static string Safe(string name) => string.Concat(name.Select(c => c is '/' or '\\' or ':' ? '_' : c));
}
