using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romdex.Client;
using Romdex.Core.Data;

namespace Romdex.Core.Downloads;

/// <summary>
/// How each system's arrivals are filed: the folder layout, the storage format and what to do with alternatives. Kept
/// by system name, as preferences are, so they survive the system's DAT being replaced.
/// </summary>
public sealed class Filing(RomdexDbContext db)
{
    private static readonly string[] DiscExtensions = [".cue", ".gdi", ".iso", ".bin", ".img", ".chd"];

    private static string KeyOf(string systemName) => $"filing:{systemName}";

    public async Task<FilingSettings> GetAsync(string systemName, CancellationToken ct) =>
        await db.Settings.FindAsync([KeyOf(systemName)], ct) is { } setting
            ? JsonSerializer.Deserialize<FilingSettings>(setting.ValueJson, RomdexJson.Options) ?? FilingSettings.Default
            : FilingSettings.Default;

    public async Task SaveAsync(string systemName, FilingSettings value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, RomdexJson.Options);
        if (await db.Settings.FindAsync([KeyOf(systemName)], ct) is { } setting)
            setting.ValueJson = json;
        else
            db.Settings.Add(new Setting { Key = KeyOf(systemName), ValueJson = json });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The storage formats that make sense for a system: CHD for discs, RVZ for GameCube and Wii, zip for cartridges.</summary>
    public async Task<IReadOnlyList<StorageFormat>> FormatsForAsync(CatalogueSystem system, CancellationToken ct)
    {
        var names = await db.DumpFiles.Where(f => f.Dump!.SystemId == system.Id).Select(f => f.Name).Take(200).ToListAsync(ct);
        var discs = names.Count > 0 && names.Count(n => DiscExtensions.Contains(Path.GetExtension(n), StringComparer.OrdinalIgnoreCase)) * 2 >= names.Count;
        if (!discs)
            return [StorageFormat.Keep, StorageFormat.Loose, StorageFormat.Zip];
        return IsDolphin(system.Name)
            ? [StorageFormat.Keep, StorageFormat.Rvz]
            : [StorageFormat.Keep, StorageFormat.Chd];
    }

    // Dolphin's systems: their DATs list one iso per disc, which RVZ holds whole.
    private static bool IsDolphin(string name) =>
        name.Contains("GameCube", StringComparison.OrdinalIgnoreCase) || name.EndsWith(" - Wii", StringComparison.OrdinalIgnoreCase);
}
