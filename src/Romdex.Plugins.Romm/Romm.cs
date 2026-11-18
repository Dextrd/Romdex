using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Romdex.Contracts.Plugins;
using Romdex.Contracts.Plugins.Connectors;

namespace Romdex.Plugins.Romm;

/// <summary>Entry point for the RomM connector.</summary>
public sealed class RommFactory : IPluginFactory
{
    public const string PluginId = "romdex.connector.romm";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "RomM",
        Version = "0.1.0",
        Type = PluginType.Connector,
        ContractVersion = ContractVersions.Connector.ToString(),
        Description = "Asks RomM to scan when a game is filed, and gives every game an Open in RomM button, with RomM's player, saves and states.",
        Homepage = "https://romm.app",
        Capabilities = [ConnectorCapabilities.LaunchLink, ConnectorCapabilities.SelectiveScan],
        Access = new PluginAccess { Network = ["*"] },
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "required": ["url"],
              "properties": {
                "url": {
                  "type": "string",
                  "title": "Address",
                  "description": "Where Romdex reaches RomM, such as http://romm:8080."
                },
                "token": {
                  "type": "string",
                  "writeOnly": true,
                  "title": "Client token",
                  "description": "A token made in RomM under your profile → Client API tokens, starting rmm_, with roms.read, platforms.read and tasks.run. Or leave it empty and give a user's name and password."
                },
                "username": {
                  "type": "string",
                  "title": "User name"
                },
                "password": {
                  "type": "string",
                  "writeOnly": true,
                  "title": "Password"
                },
                "publicUrl": {
                  "type": "string",
                  "title": "Address people open",
                  "description": "Where your browser opens RomM, if it's not the address above, such as https://romm.example.com."
                },
                "libraryFolder": {
                  "type": "string",
                  "default": "roms",
                  "title": "Library folder in RomM",
                  "description": "Where RomM sees Romdex's library, inside its own library folder. It's roms when Romdex's library is mounted at /romm/library/roms."
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context)
    {
        var settings = context.Settings;
        string? Text(string name) => settings.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;
        var url = (Text("url") ?? "").TrimEnd('/');
        var http = context.HttpClient;
        if (Text("token") is { } token)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        else if (Text("username") is { } user)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{Text("password") ?? ""}")));
        return new Romm(http, url, (Text("publicUrl") ?? url).TrimEnd('/'), (Text("libraryFolder") ?? "roms").Trim('/'));
    }
}

/// <summary>
/// RomM, through its API. Romdex's library is a folder of RomM's (its <c>roms</c> folder, by default), laid out as RomM
/// expects, one folder per platform: so a file is found in RomM by its path, or else by its hashes.
/// </summary>
public sealed class Romm(HttpClient http, string url, string publicUrl, string libraryFolder) : IConnector, ILaunchLink
{
    private Dictionary<string, int>? _platforms;

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (url.Length == 0)
            return new HealthReport(HealthStatus.Unhealthy, "Say where RomM is.");
        try
        {
            using var heartbeat = await http.GetAsync($"{url}/api/heartbeat", cancellationToken);
            if (!heartbeat.IsSuccessStatusCode)
                return new HealthReport(HealthStatus.Unhealthy, $"RomM answered {(int)heartbeat.StatusCode} at {url}.");
            var version = (await heartbeat.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
                .TryGetProperty("SYSTEM", out var system) && system.TryGetProperty("VERSION", out var v) ? v.GetString() : null;
            using var platforms = await http.GetAsync($"{url}/api/platforms", cancellationToken);
            if (platforms.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new HealthReport(HealthStatus.Unhealthy, $"RomM {version} refused the token or password ({(int)platforms.StatusCode}). It needs roms.read, platforms.read and tasks.run.");
            platforms.EnsureSuccessStatusCode();
            var count = (await platforms.Content.ReadFromJsonAsync<List<Platform>>(Json, cancellationToken))?.Count ?? 0;
            return new HealthReport(HealthStatus.Healthy, $"RomM {version}, with {(count == 1 ? "1 platform" : $"{count} platforms")}.");
        }
        catch (HttpRequestException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Couldn't reach RomM at {url}: {e.Message}");
        }
    }

    public async Task TriggerScanAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        // The platform folders the paths are in; RomM scans those for what's new, or every platform given none.
        var platforms = paths.Where(p => p.Contains('/')).Select(p => p.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
        using var response = await http.PostAsJsonAsync($"{url}/api/tasks/scan", new { type = "quick", platform_fs_slugs = platforms }, cancellationToken);
        // A scan already running picks up what's new on the platforms it hasn't reached; the core asks again if not.
        if (response.StatusCode != HttpStatusCode.Conflict)
            response.EnsureSuccessStatusCode();
        _platforms = null;
    }

    public async Task<string?> ResolveGameAsync(GameLookup lookup, CancellationToken cancellationToken)
    {
        if (lookup.Path is { } path && path.Contains('/'))
        {
            var platforms = _platforms ??= (await http.GetFromJsonAsync<List<Platform>>($"{url}/api/platforms", Json, cancellationToken) ?? [])
                .Where(p => p.FsSlug is not null).GroupBy(p => p.FsSlug!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
            if (platforms.TryGetValue(path.Split('/')[0], out var platform))
            {
                var full = libraryFolder.Length == 0 ? path : $"{libraryFolder}/{path}";
                var name = Path.GetFileNameWithoutExtension(path);
                var page = await http.GetFromJsonAsync<RomPage>(
                    $"{url}/api/roms?platform_ids={platform}&search_term={Uri.EscapeDataString(name)}&limit=50", Json, cancellationToken);
                // A game RomM keeps as a folder, such as a disc in several files, is found by the folder.
                if (page?.Items.FirstOrDefault(r => r.FullPath is { } p && (p == full || full.StartsWith(p + "/", StringComparison.Ordinal))) is { } rom)
                    return rom.Id.ToString();
            }
        }
        foreach (var (key, value) in new[] { ("sha1_hash", lookup.Sha1), ("md5_hash", lookup.Md5), ("crc_hash", lookup.Crc32) })
        {
            if (value is null)
                continue;
            using var response = await http.GetAsync($"{url}/api/roms/by-hash?{key}={value}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                continue;
            response.EnsureSuccessStatusCode();
            if (await response.Content.ReadFromJsonAsync<Rom>(Json, cancellationToken) is { } rom)
                return rom.Id.ToString();
        }
        return null;
    }

    public Uri LaunchUri(string gameId) => new($"{publicUrl}/rom/{Uri.EscapeDataString(gameId)}");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record Platform(int Id, string? FsSlug);

    private sealed record Rom(int Id, string? FullPath);

    private sealed record RomPage(List<Rom> Items);
}
