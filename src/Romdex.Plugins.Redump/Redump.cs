using System.IO.Compression;
using System.Text.Json;
using Romdex.Contracts.Plugins;
using Romdex.Contracts.Plugins.Index;
using Romdex.Plugins.DatFolder;

namespace Romdex.Plugins.Redump;

/// <summary>Entry point for the Redump index plugin.</summary>
public sealed class RedumpFactory : IPluginFactory
{
    public const string PluginId = "romdex.index.redump";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Redump",
        Version = "0.2.0",
        Type = PluginType.Index,
        ContractVersion = ContractVersions.Index.ToString(),
        Description = "Fetches Redump's DATs for the disc systems you choose, and their BIOS DATs where Redump has them, and checks for new "
                      + "versions every few days. With them, Retool's clone lists make one title of a game released under different names.",
        Homepage = "https://redump.info",
        Capabilities = [IndexCapabilities.FetchDats],
        Access = new PluginAccess { Network = ["redump.info", "raw.githubusercontent.com"] },
        MaxRequestsPerSecond = 1,
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "required": ["systems"],
              "properties": {
                "systems": {
                  "type": "array",
                  "default": ["PSX", "GC"],
                  "title": "Systems",
                  "description": "The disc systems to fetch DATs for. Cartridge systems' DATs are No-Intro's: download them from DAT-o-MATIC into the DAT folder.",
                  "items": {
                    "type": "string",
                    "oneOf": [
                      { "const": "PSX", "title": "PlayStation" },
                      { "const": "PS2", "title": "PlayStation 2" },
                      { "const": "PS3", "title": "PlayStation 3" },
                      { "const": "PS4", "title": "PlayStation 4" },
                      { "const": "PS5", "title": "PlayStation 5" },
                      { "const": "PSP", "title": "PlayStation Portable" },
                      { "const": "GC", "title": "GameCube" },
                      { "const": "WII", "title": "Wii" },
                      { "const": "WIIU", "title": "Wii U" },
                      { "const": "XBOX", "title": "Xbox" },
                      { "const": "XBOX360", "title": "Xbox 360" },
                      { "const": "XBOXONE", "title": "Xbox One" },
                      { "const": "XBOXSX", "title": "Xbox Series X" },
                      { "const": "MCD", "title": "Mega CD & Sega CD" },
                      { "const": "SS", "title": "Saturn" },
                      { "const": "DC", "title": "Dreamcast" },
                      { "const": "PCE", "title": "PC Engine CD & TurboGrafx CD" },
                      { "const": "PC-FX", "title": "PC-FX & PC-FXGA" },
                      { "const": "NGCD", "title": "Neo Geo CD" },
                      { "const": "3DO", "title": "3DO Interactive Multiplayer" }
                    ]
                  },
                  "uniqueItems": true
                },
                "bios": {
                  "type": "boolean",
                  "default": true,
                  "title": "BIOS DATs",
                  "description": "Fetch the BIOS DAT too, for systems Redump has one for."
                },
                "cloneLists": {
                  "type": "boolean",
                  "default": true,
                  "title": "Retool's clone lists",
                  "description": "Fetch Retool's clone list for each system too, which says which entries are one game under different names, such as a Japanese release with a title of its own. From github.com/unexpectedpanda/retool-clonelists-metadata."
                },
                "everyDays": {
                  "type": "integer",
                  "default": 7,
                  "minimum": 1,
                  "maximum": 90,
                  "title": "Check every (days)"
                },
                "baseUrl": {
                  "type": "string",
                  "default": "https://redump.info",
                  "title": "Address",
                  "description": "Where Redump is. Change it only if Redump moves again."
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context)
    {
        var settings = context.Settings;
        string Text(string name, string fallback) => settings.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : fallback;
        // A list of codes, or, as earlier versions kept them, the codes typed out with commas.
        var chosen = settings.TryGetProperty("systems", out var listed) && listed.ValueKind == JsonValueKind.Array
            ? listed.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
            : Text("systems", "PSX, GC").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var systems = chosen.Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0).Distinct().ToList();
        var bios = !settings.TryGetProperty("bios", out var b) || b.ValueKind != JsonValueKind.False;
        var days = settings.TryGetProperty("everyDays", out var d) && d.TryGetInt32(out var n) ? Math.Clamp(n, 1, 90) : 7;
        var cloneLists = !settings.TryGetProperty("cloneLists", out var c) || c.ValueKind != JsonValueKind.False ? CloneListsUrl : null;
        return new Redump(context.HttpClient, context.DataDirectory, Text("baseUrl", "https://redump.info").TrimEnd('/'), systems, bios, TimeSpan.FromDays(days),
            cloneLists);
    }

    /// <summary>Where Retool's clone lists are, each at <c>{this}/{DAT name} (Redump).json</c>.</summary>
    public const string CloneListsUrl = "https://raw.githubusercontent.com/unexpectedpanda/retool-clonelists-metadata/main/clonelists";
}

/// <summary>
/// Redump's DATs, kept in the plugin's own folder and read like a DAT folder. Redump hands each out as a zip, at
/// <c>/datfile/{code}</c>, and BIOS DATs at <c>/bios/{code}</c>; a download replaces what's kept only when it's a newer version.
/// Most come zipped, but some, such as the PlayStation 2's BIOS DAT, come as the DAT itself: each is kept as it came.
/// Retool's clone list for each, where there is one, is kept beside it, for the DAT folder to apply.
/// </summary>
public sealed class Redump(HttpClient http, string folder, string baseUrl, IReadOnlyList<string> systems, bool bios, TimeSpan every, string? cloneLists = null)
    : IIndexPlugin, IDatFetcher
{
    /// <summary>The systems Redump has a BIOS DAT for.</summary>
    public static readonly IReadOnlySet<string> BiosSystems = new HashSet<string>(["PSX", "PS2", "GC", "XBOX"]);

    private readonly DatFolder.DatFolder _dats = new(folder);
    private string StatePath => Path.Combine(folder, "state.json");

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (systems.Count == 0)
            return new HealthReport(HealthStatus.Unhealthy, "No systems chosen. Tick the ones to fetch DATs for in its settings.");
        var state = ReadState();
        var have = (await _dats.ListSystemsAsync(cancellationToken)).Count;
        if (state.Error is { } error)
            return new HealthReport(HealthStatus.Degraded, $"The last fetch failed: {error}. {Count(have)} kept from before.");
        if (state.FetchedAt is null)
        {
            // Not fetched yet: check Redump answers at all.
            using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"{baseUrl}/downloads/"), cancellationToken);
            return response.IsSuccessStatusCode
                ? new HealthReport(HealthStatus.Healthy, $"Redump answers. Its DATs for {string.Join(", ", systems)} are fetched with the next catalogue update.")
                : new HealthReport(HealthStatus.Unhealthy, $"Redump answered {(int)response.StatusCode} at {baseUrl}.");
        }
        return new HealthReport(HealthStatus.Healthy, $"{Count(have)}, last checked {state.FetchedAt:d MMM yyyy}.");
    }

    public Task<IReadOnlyList<IndexSystem>> ListSystemsAsync(CancellationToken cancellationToken) => _dats.ListSystemsAsync(cancellationToken);

    public Task<IndexSystemDetail?> GetSystemAsync(string systemId, CancellationToken cancellationToken) => _dats.GetSystemAsync(systemId, cancellationToken);

    public async Task<IReadOnlyList<IndexSystem>> FetchAsync(CancellationToken cancellationToken)
    {
        var state = ReadState();
        // Each kept as {name}.zip or {name}.dat, as it came.
        var wanted = systems.Select(s => (Url: $"{baseUrl}/datfile/{s}", Name: s))
            .Concat(bios ? systems.Where(BiosSystems.Contains).Select(s => (Url: $"{baseUrl}/bios/{s}", Name: $"{s}-bios")) : [])
            .ToList();
        var missing = wanted.Any(w => Kept(Path.Combine(folder, w.Name)) is null);
        // Clone lists turned on are fetched at once, not when the DATs are next due.
        if (!missing && state.CloneLists == (cloneLists is not null) && state.FetchedAt is { } last && DateTime.UtcNow - last < every)
            return [];

        // One that fails doesn't stop the rest; what failed is reported once all have been tried.
        var changed = new List<IndexSystem>();
        var errors = new List<Exception>();
        foreach (var (url, name) in wanted)
        {
            try
            {
                if (await FetchOneAsync(url, Path.Combine(folder, name), cancellationToken) is { } system)
                    changed.Add(system);
            }
            catch (Exception e) when (e is HttpRequestException or InvalidDataException or IOException)
            {
                errors.Add(e);
            }
        }
        if (cloneLists is not null)
            foreach (var code in systems)
            {
                try
                {
                    if (await FetchCloneListAsync(Path.Combine(folder, code), cancellationToken) is { } grouped && !changed.Any(s => s.Name == grouped.Name))
                        changed.Add(grouped);
                }
                catch (Exception e) when (e is HttpRequestException or InvalidDataException or IOException)
                {
                    errors.Add(e);
                }
            }
        WriteState(errors.Count == 0 ? new State(DateTime.UtcNow, null, cloneLists is not null) : state with { Error = string.Join("; ", errors.Select(e => e.Message)) });
        if (errors.Count > 0)
            throw errors.Count == 1 ? errors[0] : new AggregateException("Some DATs couldn't be fetched: " + string.Join("; ", errors.Select(e => e.Message)), errors);
        return changed;
    }

    // Retool's clone list for the DAT kept as {name}, beside it as {name}-clones.json. Returns the DAT's system when the
    // list is new or changed; nothing when Retool has none for it.
    private async Task<IndexSystem?> FetchCloneListAsync(string name, CancellationToken ct)
    {
        if (Kept(name) is not { } dat || ReadHeader(await File.ReadAllBytesAsync(dat, ct)) is not { } system)
            return null;
        using var response = await http.GetAsync($"{cloneLists}/{Uri.EscapeDataString(system.Name + " (Redump)")}.json", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Retool's clone list for {system.Name} answered {(int)response.StatusCode}");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (Retool.Read(new MemoryStream(bytes)) is null)
            throw new InvalidDataException($"Retool's clone list for {system.Name} isn't one.");
        var path = name + "-clones.json";
        if (File.Exists(path) && (await File.ReadAllBytesAsync(path, ct)).AsSpan().SequenceEqual(bytes))
            return null;
        await File.WriteAllBytesAsync(path + ".part", bytes, ct);
        File.Move(path + ".part", path, overwrite: true);
        return system;
    }

    // The file a DAT is kept in, zipped or not, if there is one.
    private static string? Kept(string name) => new[] { name + ".zip", name + ".dat" }.FirstOrDefault(File.Exists);

    // Downloads one DAT; keeps it, and returns its system, only when it's a version newer than the one kept.
    private async Task<IndexSystem?> FetchOneAsync(string url, string name, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{url} answered {(int)response.StatusCode}");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var fetched = ReadHeader(bytes) ?? throw new InvalidDataException($"{url} didn't send a DAT.");
        if (Kept(name) is { } old && ReadHeader(await File.ReadAllBytesAsync(old, ct)) is { } kept && string.CompareOrdinal(kept.Version, fetched.Version) >= 0)
            return null;
        var (path, other) = IsZip(bytes) ? (name + ".zip", name + ".dat") : (name + ".dat", name + ".zip");
        var partial = path + ".part";
        await File.WriteAllBytesAsync(partial, bytes, ct);
        File.Move(partial, path, overwrite: true);
        // One that came the other way before goes, so the folder never has two of it.
        File.Delete(other);
        return fetched;
    }

    private static bool IsZip(byte[] bytes) => bytes.AsSpan().StartsWith("PK\x03\x04"u8);

    private static IndexSystem? ReadHeader(byte[] bytes)
    {
        try
        {
            if (!IsZip(bytes))
                return Logiqx.ReadHeader(new MemoryStream(bytes));
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return null;
            using var stream = entry.Open();
            return Logiqx.ReadHeader(stream);
        }
        catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string Count(int n) => n == 1 ? "1 DAT" : $"{n} DATs";

    private sealed record State(DateTime? FetchedAt, string? Error, bool CloneLists = false);

    private State ReadState()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new State(null, null) : new State(null, null);
        }
        catch (JsonException)
        {
            return new State(null, null);
        }
    }

    private void WriteState(State state) => File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
}
