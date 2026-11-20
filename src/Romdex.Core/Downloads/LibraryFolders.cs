using Romdex.Client;
using Romdex.Core.Data;

namespace Romdex.Core.Downloads;

/// <summary>
/// Where in the library a system's games, and its BIOS files, go: the one answer that filing and formatting share, so
/// what a retriever brings in lands where the library's layout keeps the rest.
/// </summary>
/// <remarks>
/// A game goes in the games folder, in its system's folder: named as the layout names it (only the igir worker has
/// igir's names for consoles), or as the system's own setting says. A BIOS goes in the BIOS folder, when there is one,
/// in a folder named as its system's games folder is or all together; a BIOS DAT's files count as its system's.
/// </remarks>
public sealed class LibraryFolders(RomdexDbContext db, Filing filing, IFilingWorker worker)
{
    /// <summary>The layouts a library can take, best known first: a detected layout that ties goes to the earlier.</summary>
    public static readonly LibraryLayout[] Presets =
        [LibraryLayout.Romm, LibraryLayout.EsDe, LibraryLayout.Batocera, LibraryLayout.RetroDeck, LibraryLayout.Mister, LibraryLayout.DatName];

    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, CatalogueSystem> _systems = [];
    private LibraryFormat? _format;

    /// <summary>The igir worker's name for a layout.</summary>
    public static string WorkerLayout(LibraryLayout layout) => layout switch
    {
        LibraryLayout.DatName => "datName",
        LibraryLayout.EsDe => "es",
        LibraryLayout.RetroDeck => "retrodeck",
        LibraryLayout.Batocera => "batocera",
        LibraryLayout.Mister => "mister",
        _ => "romm",
    };

    public async Task<LibraryFormat> FormatAsync(CancellationToken ct) => _format ??= await filing.GetFormatAsync(ct);

    /// <summary>The system a system's files are filed as: a BIOS DAT's are its system's.</summary>
    public async Task<CatalogueSystem> OwnerAsync(CatalogueSystem system, CancellationToken ct)
    {
        if (system.BiosForId is not { } parentId)
            return system;
        if (!_systems.TryGetValue(parentId, out var parent))
            _systems[parentId] = parent = await db.Systems.FindAsync([parentId], ct) ?? system;
        return parent;
    }

    /// <summary>The folder, relative to the library, that a dump of these files goes in.</summary>
    /// <param name="bios">Whether the dump is a BIOS; one in a BIOS DAT always is.</param>
    public async Task<string> ForAsync(CatalogueSystem system, bool bios, IReadOnlyList<string> roms, CancellationToken ct)
    {
        var owner = await OwnerAsync(system, ct);
        bios |= owner != system;
        var format = await FormatAsync(ct);
        var name = await SystemFolderAsync(owner.Name, roms, ct);
        if (bios && format.BiosFolder.Length > 0)
            return format.BiosBySystem ? Join(format.BiosFolder, name) : format.BiosFolder;
        return Join(format.GamesFolder, name);
    }

    /// <summary>A system's own folder, inside the games folder: its custom one, or as its layout or the library's names it.</summary>
    public async Task<string> SystemFolderAsync(string systemName, IReadOnlyList<string> roms, CancellationToken ct)
    {
        var settings = await filing.GetAsync(systemName, ct);
        if (settings.Layout == LibraryLayout.Custom && Filing.CleanFolder(settings.Folder) is { Length: > 0 } custom)
            return custom;
        var layout = settings.Layout is LibraryLayout.Library or LibraryLayout.Custom ? (await FormatAsync(ct)).Layout : settings.Layout;
        return await NameAsync(systemName, layout, roms, ct);
    }

    /// <summary>The folder a layout names a system's games, from the igir worker.</summary>
    public async Task<string> NameAsync(string systemName, LibraryLayout layout, IReadOnlyList<string> roms, CancellationToken ct)
    {
        // igir goes by the DAT's name before the files', so one game's extensions stand for the rest with the same ones.
        var key = $"{layout}|{systemName}|{string.Join(",", roms.Select(r => Path.GetExtension(r).ToLowerInvariant()).Distinct().Order())}";
        if (!_names.TryGetValue(key, out var folder))
            _names[key] = folder = (await worker.FolderAsync(new FolderRequest(systemName, WorkerLayout(layout), roms), ct)).Folder;
        return folder;
    }

    public static string Join(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";
}
