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

    private const string FormatKey = "library:format";

    private static string KeyOf(string systemName) => $"filing:{systemName}";

    /// <summary>The library's layout, which systems follow unless they have their own.</summary>
    public async Task<LibraryFormat> GetFormatAsync(CancellationToken ct) =>
        await db.Settings.FindAsync([FormatKey], ct) is { } setting
            ? JsonSerializer.Deserialize<LibraryFormat>(setting.ValueJson, RomdexJson.Options) ?? LibraryFormat.Default
            : LibraryFormat.Default;

    public Task SaveFormatAsync(LibraryFormat value, CancellationToken ct) => SaveAsync(FormatKey, JsonSerializer.Serialize(value, RomdexJson.Options), ct);

    public async Task<FilingSettings> GetAsync(string systemName, CancellationToken ct) =>
        await db.Settings.FindAsync([KeyOf(systemName)], ct) is { } setting
            ? JsonSerializer.Deserialize<FilingSettings>(setting.ValueJson, RomdexJson.Options) ?? FilingSettings.Default
            : FilingSettings.Default;

    public Task SaveAsync(string systemName, FilingSettings value, CancellationToken ct) =>
        SaveAsync(KeyOf(systemName), JsonSerializer.Serialize(value, RomdexJson.Options), ct);

    private async Task SaveAsync(string key, string json, CancellationToken ct)
    {
        if (await db.Settings.FindAsync([key], ct) is { } setting)
            setting.ValueJson = json;
        else
            db.Settings.Add(new Setting { Key = key, ValueJson = json });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A folder inside the library as it's kept: forward slashes, none at either end. Null when it isn't one, such as
    /// one that climbs out with <c>..</c>.
    /// </summary>
    public static string? CleanFolder(string? folder)
    {
        var parts = (folder ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Any(p => p is "." or ".." || p.Contains(':') || p.Any(char.IsControl)))
            return null;
        return string.Join('/', parts);
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
