using System.Text.Json;
using Romdex.Contracts.Plugins;
using Romdex.Contracts.Plugins.Index;
using Romdex.Plugins.DatFolder;

namespace Romdex.Plugins.Libretro;

/// <summary>Entry point for the libretro DATs index plugin.</summary>
public sealed class LibretroDatsFactory : IPluginFactory
{
    public const string PluginId = "romdex.index.libretro-dats";

    /// <summary>The systems it offers, by their DATs' names, which are libretro's file names too.</summary>
    public static readonly IReadOnlyList<string> Offered =
    [
        "Nintendo - Nintendo Entertainment System", "Nintendo - Family Computer Disk System", "Nintendo - Super Nintendo Entertainment System",
        "Nintendo - Nintendo 64", "Nintendo - Nintendo 64DD", "Nintendo - Game Boy", "Nintendo - Game Boy Color", "Nintendo - Game Boy Advance",
        "Nintendo - Virtual Boy", "Nintendo - Nintendo DS", "Nintendo - Nintendo DSi", "Nintendo - Nintendo 3DS", "Nintendo - Pokemon Mini",
        "Sega - SG-1000", "Sega - Master System - Mark III", "Sega - Game Gear", "Sega - Mega Drive - Genesis", "Sega - 32X",
        "NEC - PC Engine - TurboGrafx 16", "NEC - PC Engine SuperGrafx", "SNK - Neo Geo Pocket", "SNK - Neo Geo Pocket Color",
        "Bandai - WonderSwan", "Bandai - WonderSwan Color", "Atari - 2600", "Atari - 5200", "Atari - 7800", "Atari - Lynx", "Atari - Jaguar",
        "Coleco - ColecoVision", "Mattel - Intellivision", "GCE - Vectrex", "Magnavox - Odyssey2", "Sony - PlayStation Vita",
    ];

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "libretro DATs",
        Version = "0.1.0",
        Type = PluginType.Index,
        ContractVersion = ContractVersions.Index.ToString(),
        Description = "Fetches No-Intro's DATs for the cartridge systems you choose, as libretro keeps them for RetroArch, and checks for new versions "
                      + "every few days. libretro updates them every month or two, so they can trail DAT-o-MATIC's: a newer DAT dropped in the DAT "
                      + "folder wins. With them, Retool's clone lists make one title of a game released under different names.",
        Homepage = "https://github.com/libretro/libretro-database",
        Capabilities = [IndexCapabilities.FetchDats],
        Access = new PluginAccess { Network = ["raw.githubusercontent.com", "api.github.com"] },
        MaxRequestsPerSecond = 2,
        SettingsSchema = JsonDocument.Parse($$"""
            {
              "type": "object",
              "required": ["systems"],
              "properties": {
                "systems": {
                  "type": "array",
                  "default": ["Nintendo - Nintendo Entertainment System", "Nintendo - Super Nintendo Entertainment System", "Nintendo - Game Boy",
                    "Nintendo - Game Boy Color", "Nintendo - Game Boy Advance", "Sega - Mega Drive - Genesis"],
                  "title": "Systems",
                  "description": "The cartridge systems to fetch DATs for. Disc systems' DATs are Redump's: the Redump catalogue fetches them.",
                  "items": {
                    "type": "string",
                    "oneOf": {{JsonSerializer.Serialize(Offered.Select(s => new { @const = s, title = s[(s.IndexOf(" - ", StringComparison.Ordinal) + 3)..] }))}}
                  },
                  "uniqueItems": true
                },
                "cloneLists": {
                  "type": "boolean",
                  "default": true,
                  "title": "Retool's clone lists",
                  "description": "Fetch Retool's clone list for each system too, which says which entries are one game under different names, such as a Japanese release with a title of its own. libretro's DATs leave out No-Intro's own. From github.com/unexpectedpanda/retool-clonelists-metadata."
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
                  "default": "{{DefaultUrl}}",
                  "title": "Address",
                  "description": "Where libretro's No-Intro DATs are. Change it only to use a mirror."
                }
              }
            }
            """).RootElement,
    };

    private const string DefaultUrl = "https://raw.githubusercontent.com/libretro/libretro-database/master/metadat/no-intro";

    /// <summary>Where Retool's clone lists are listed, each with where to download it.</summary>
    public const string CloneListsUrl = "https://api.github.com/repos/unexpectedpanda/retool-clonelists-metadata/contents/clonelists";

    public IPlugin Create(PluginContext context)
    {
        var settings = context.Settings;
        var systems = settings.TryGetProperty("systems", out var listed) && listed.ValueKind == JsonValueKind.Array
            ? listed.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0).Distinct().ToList()
            : [];
        var days = settings.TryGetProperty("everyDays", out var d) && d.TryGetInt32(out var n) ? Math.Clamp(n, 1, 90) : 7;
        var url = settings.TryGetProperty("baseUrl", out var u) && u.GetString() is { Length: > 0 } s ? s.TrimEnd('/') : DefaultUrl;
        var cloneLists = !settings.TryGetProperty("cloneLists", out var c) || c.ValueKind != JsonValueKind.False ? CloneListsUrl : null;
        return new LibretroDats(context.HttpClient, context.DataDirectory, url, systems, TimeSpan.FromDays(days), cloneLists);
    }
}

/// <summary>
/// No-Intro's DATs as libretro keeps them, in clrmamepro's format, one file per system at <c>{baseUrl}/{DAT name}.dat</c>.
/// They're kept in the plugin's own folder and read like a DAT folder; a download replaces what's kept only when it's a
/// newer version. libretro leaves out No-Intro's parent and clone links, so a game's regions are grouped by name, and
/// Retool's clone list for each, where there is one, is kept beside it for the DAT folder to apply.
/// </summary>
public sealed class LibretroDats(HttpClient http, string folder, string baseUrl, IReadOnlyList<string> systems, TimeSpan every, string? cloneLists = null)
    : IIndexPlugin, IDatFetcher
{
    private readonly DatFolder.DatFolder _dats = new(folder);
    private string StatePath => Path.Combine(folder, "state.json");

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (systems.Count == 0)
            return new HealthReport(HealthStatus.Unhealthy, "No systems chosen. Tick the ones to fetch DATs for in its settings.");
        var state = ReadState();
        var have = (await ListSystemsAsync(cancellationToken)).Count;
        if (state.Error is { } error)
            return new HealthReport(HealthStatus.Degraded, $"The last fetch failed: {error}. {Count(have)} kept from before.");
        if (state.FetchedAt is null)
        {
            // Not fetched yet: check libretro answers at all, with the first system's DAT, without downloading it.
            using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, UrlOf(systems[0])), cancellationToken);
            return response.IsSuccessStatusCode
                ? new HealthReport(HealthStatus.Healthy, $"libretro answers. Its DATs for {Count(systems.Count)} are fetched with the next catalogue update.")
                : new HealthReport(HealthStatus.Unhealthy, $"libretro answered {(int)response.StatusCode} for {systems[0]}.");
        }
        return new HealthReport(HealthStatus.Healthy, $"{Count(have)}, last checked {state.FetchedAt:d MMM yyyy}.");
    }

    // Only the systems still chosen: one unticked stays in the folder, but isn't listed.
    public async Task<IReadOnlyList<IndexSystem>> ListSystemsAsync(CancellationToken cancellationToken) =>
        (await _dats.ListSystemsAsync(cancellationToken)).Where(s => systems.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).Select(AsNoIntro).ToList();

    public async Task<IndexSystemDetail?> GetSystemAsync(string systemId, CancellationToken cancellationToken) =>
        await _dats.GetSystemAsync(systemId, cancellationToken) is { } detail ? detail with { System = AsNoIntro(detail.System) } : null;

    // The header names libretro's own repository, but what it lists is No-Intro's.
    private static IndexSystem AsNoIntro(IndexSystem system) => system with { Source = DatSources.NoIntro };

    public async Task<IReadOnlyList<IndexSystem>> FetchAsync(CancellationToken cancellationToken)
    {
        var state = ReadState();
        var missing = systems.Any(s => !File.Exists(PathOf(s)));
        // Clone lists turned on are fetched at once, not when the DATs are next due.
        if (!missing && state.CloneLists == (cloneLists is not null) && state.FetchedAt is { } last && DateTime.UtcNow - last < every)
            return [];

        // One that fails doesn't stop the rest; what failed is reported once all have been tried.
        var changed = new List<IndexSystem>();
        var errors = new List<Exception>();
        foreach (var system in systems)
        {
            try
            {
                if (await FetchOneAsync(system, cancellationToken) is { } fetched)
                    changed.Add(AsNoIntro(fetched));
            }
            catch (Exception e) when (e is HttpRequestException or InvalidDataException or IOException)
            {
                errors.Add(e);
            }
        }
        if (cloneLists is not null)
        {
            try
            {
                foreach (var grouped in await FetchCloneListsAsync(cancellationToken))
                    if (!changed.Any(s => s.Name == grouped))
                        changed.Add(AsNoIntro(new IndexSystem(grouped, grouped, DatSources.NoIntro, "")));
            }
            catch (Exception e) when (e is HttpRequestException or InvalidDataException or IOException or JsonException)
            {
                errors.Add(e);
            }
        }
        WriteState(errors.Count == 0 ? new State(DateTime.UtcNow, null, cloneLists is not null) : state with { Error = string.Join("; ", errors.Select(e => e.Message)) });
        if (errors.Count > 0)
            throw errors.Count == 1 ? errors[0] : new AggregateException("Some DATs couldn't be fetched: " + string.Join("; ", errors.Select(e => e.Message)), errors);
        return changed;
    }

    // Retool's clone list for each system chosen that it has one for, beside its DAT as {name}-clones.json. Retool names
    // its lists for No-Intro's DATs, which libretro's are, though not always as libretro writes them. Returns the systems
    // whose list is new or changed.
    private async Task<List<string>> FetchCloneListsAsync(CancellationToken ct)
    {
        using var listing = new HttpRequestMessage(HttpMethod.Get, cloneLists);
        listing.Headers.UserAgent.ParseAdd("Romdex");
        using var response = await http.SendAsync(listing, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Retool's clone lists answered {(int)response.StatusCode}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var lists = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in json.RootElement.EnumerateArray())
            if (file.TryGetProperty("name", out var n) && n.GetString() is { } name && name.EndsWith(" (No-Intro).json", StringComparison.Ordinal)
                && file.TryGetProperty("download_url", out var u) && u.GetString() is { } url)
                lists.TryAdd(Retool.SystemKey(name[..^" (No-Intro).json".Length]), url);

        var changed = new List<string>();
        foreach (var system in systems)
        {
            if (!lists.TryGetValue(Retool.SystemKey(system), out var url))
                continue;
            var bytes = await http.GetByteArrayAsync(url, ct);
            if (Retool.Read(new MemoryStream(bytes)) is null)
                throw new InvalidDataException($"Retool's clone list for {system} isn't one.");
            var path = Path.ChangeExtension(PathOf(system), null) + "-clones.json";
            if (File.Exists(path) && (await File.ReadAllBytesAsync(path, ct)).AsSpan().SequenceEqual(bytes))
                continue;
            await File.WriteAllBytesAsync(path + ".part", bytes, ct);
            File.Move(path + ".part", path, overwrite: true);
            changed.Add(system);
        }
        return changed;
    }

    private string UrlOf(string system) => $"{baseUrl}/{Uri.EscapeDataString(system)}.dat";

    private string PathOf(string system) => Path.Combine(folder, string.Concat(system.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".dat");

    // Downloads one DAT; keeps it, and returns its system, only when it's a version newer than the one kept.
    private async Task<IndexSystem?> FetchOneAsync(string system, CancellationToken ct)
    {
        var url = UrlOf(system);
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"libretro answered {(int)response.StatusCode} for {system}");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var fetched = ReadHeader(bytes) ?? throw new InvalidDataException($"libretro didn't send a DAT for {system}.");
        var path = PathOf(system);
        if (File.Exists(path) && ReadHeader(await File.ReadAllBytesAsync(path, ct)) is { } kept && string.CompareOrdinal(kept.Version, fetched.Version) >= 0)
            return null;
        await File.WriteAllBytesAsync(path + ".part", bytes, ct);
        File.Move(path + ".part", path, overwrite: true);
        return fetched;
    }

    private static IndexSystem? ReadHeader(byte[] bytes)
    {
        try
        {
            return Dats.ReadHeader(new MemoryStream(bytes));
        }
        catch (System.Xml.XmlException)
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
