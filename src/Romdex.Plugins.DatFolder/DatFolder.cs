using System.IO.Compression;
using System.Text.Json;
using Romdex.Contracts.Plugins;
using Romdex.Contracts.Plugins.Index;

namespace Romdex.Plugins.DatFolder;

/// <summary>Entry point for the DAT folder index plugin.</summary>
public sealed class DatFolderFactory : IPluginFactory
{
    public const string PluginId = "romdex.index.dat-folder";

    /// <summary>The setting that names the folder.</summary>
    public const string FolderSetting = "folder";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "DAT folder",
        Version = "0.2.0",
        Type = PluginType.Index,
        ContractVersion = ContractVersions.Index.ToString(),
        Description = "Reads No-Intro and Redump DATs from a folder, plain or zipped, as Logiqx XML or clrmamepro. Download them from DAT-o-MATIC and "
                      + "redump.info and drop them in; a newer DAT for a system replaces the older one. Retool's clone lists beside them, "
                      + "such as Sony - PlayStation 2 (Redump).json, make one title of a game released under different names.",
        Access = new PluginAccess { Files = ["DAT folder (read)"] },
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "required": ["folder"],
              "properties": {
                "folder": {
                  "type": "string",
                  "default": "/dats",
                  "title": "Folder",
                  "description": "Where the DATs are, as the Romdex server sees it. Subfolders are read too."
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context) => new DatFolder(
        context.Settings.TryGetProperty(FolderSetting, out var folder) && folder.GetString() is { Length: > 0 } path ? path : "/dats");
}

/// <summary>
/// The DATs in a folder. Each file is one system; where two describe the same system, the newer version wins. Only
/// headers are read to list systems, so listing stays quick however big the DATs are. A Retool clone list in the folder
/// (<see cref="Retool"/>) is applied to the DAT it's named for.
/// </summary>
public sealed class DatFolder(string folder) : IIndexPlugin
{
    private static readonly string[] Extensions = [".dat", ".xml", ".zip"];

    public Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder))
            return Task.FromResult(new HealthReport(HealthStatus.Unhealthy, $"There's no folder {folder}, or Romdex can't see it."));
        (Dictionary<string, (IndexSystem System, string File, Retool.CloneList? Clones)> Systems, List<string> Unreadable) read;
        try
        {
            read = Read(cancellationToken);
        }
        catch (UnauthorizedAccessException e)
        {
            return Task.FromResult(new HealthReport(HealthStatus.Unhealthy, e.Message));
        }
        var (systems, unreadable) = read;
        var skipped = unreadable.Count == 0 ? "" : $" {Count(unreadable.Count, "file")} couldn't be read as DATs: {string.Join(", ", unreadable.Take(3))}{(unreadable.Count > 3 ? "…" : "")}.";
        return Task.FromResult(systems.Count == 0
            ? new HealthReport(HealthStatus.Degraded, $"No DATs in {folder} yet. Drop No-Intro or Redump DATs in.{skipped}")
            : new HealthReport(unreadable.Count == 0 ? HealthStatus.Healthy : HealthStatus.Degraded, $"{Count(systems.Count, "system")} in {folder}{Grouped(systems.Values.Count(s => s.Clones is not null))}.{skipped}"));
    }

    public Task<IReadOnlyList<IndexSystem>> ListSystemsAsync(CancellationToken cancellationToken)
    {
        var (systems, _) = Read(cancellationToken);
        return Task.FromResult<IReadOnlyList<IndexSystem>>(systems.Values.Select(s => s.System).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public Task<IndexSystemDetail?> GetSystemAsync(string systemId, CancellationToken cancellationToken)
    {
        var (systems, _) = Read(cancellationToken);
        if (!systems.TryGetValue(systemId, out var found))
            return Task.FromResult<IndexSystemDetail?>(null);
        var dumps = ReadDat(found.File, s => Dats.ReadDumps(s, cancellationToken));
        if (found.Clones is { } clones)
            dumps = Retool.Apply(dumps, clones);
        return Task.FromResult<IndexSystemDetail?>(new IndexSystemDetail(found.System, dumps));
    }

    // Every DAT's header, the newest for each system, with its clone list if there is one, and the files that weren't DATs.
    private (Dictionary<string, (IndexSystem System, string File, Retool.CloneList? Clones)> Systems, List<string> Unreadable) Read(CancellationToken ct)
    {
        var (dats, unreadable) = ReadDats(ct);
        var clones = ReadCloneLists(ct);
        var systems = new Dictionary<string, (IndexSystem System, string File, Retool.CloneList? Clones)>(StringComparer.Ordinal);
        foreach (var (id, (system, file)) in dats)
        {
            var list = clones.GetValueOrDefault(Retool.SystemKey(system.Name));
            systems[id] = (list is null ? system : system with { CloneList = list.Version }, file, list);
        }
        return (systems, unreadable);
    }

    // The newest clone list for each system, by its key.
    private Dictionary<string, Retool.CloneList> ReadCloneLists(CancellationToken ct)
    {
        var lists = new Dictionary<string, Retool.CloneList>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder))
            return lists;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            ct.ThrowIfCancellationRequested();
            Retool.CloneList? list;
            try
            {
                using var stream = File.OpenRead(file);
                list = Retool.Read(stream);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                list = null;
            }
            if (list is not null && (!lists.TryGetValue(Retool.SystemKey(list.System), out var kept) || string.CompareOrdinal(list.Version, kept.Version) > 0))
                lists[Retool.SystemKey(list.System)] = list;
        }
        return lists;
    }

    private (Dictionary<string, (IndexSystem System, string File)> Systems, List<string> Unreadable) ReadDats(CancellationToken ct)
    {
        var systems = new Dictionary<string, (IndexSystem System, string File)>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        if (!Directory.Exists(folder))
            return (systems, unreadable);
        // Unreadable subfolders are skipped below, but an unreadable DAT folder would look empty.
        try
        {
            _ = Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException($"Romdex isn't allowed to read the DAT folder {folder}. Check its permissions, and on SELinux hosts its label.");
        }
        var files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            IndexSystem? system;
            try
            {
                system = ReadDat(file, Dats.ReadHeader);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
            {
                system = null;
            }
            if (system is null)
                unreadable.Add(Path.GetRelativePath(folder, file));
            else if (!systems.TryGetValue(system.Id, out var kept) || Dats.IsNewer(system, kept.System))
                systems[system.Id] = (system, file);
        }
        return (systems, unreadable);
    }

    // The DAT itself, or the first DAT inside a zip, which is how DAT-o-MATIC and Redump hand them out.
    private static T ReadDat<T>(string file, Func<Stream, T> read)
    {
        if (!Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var plain = File.OpenRead(file);
            return read(plain);
        }
        using var zip = ZipFile.OpenRead(file);
        var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("The zip has no DAT in it.");
        using var stream = entry.Open();
        return read(stream);
    }

    private static string Grouped(int n) => n == 0 ? "" : $", {n} with a Retool clone list";

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
