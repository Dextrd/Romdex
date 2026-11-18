using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Romdex.Contracts.Plugins;
using Romdex.Contracts.Plugins.Index;

namespace Romdex.Plugins.Hasheous;

/// <summary>Entry point for the Hasheous plugin.</summary>
public sealed class HasheousFactory : IPluginFactory
{
    public const string PluginId = "romdex.index.hasheous";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Hasheous",
        Version = "0.1.0",
        Type = PluginType.Index,
        ContractVersion = ContractVersions.Index.ToString(),
        Description = "Looks up the games you have on Hasheous by their hashes, for titles and cover art. It needs no account, but sends Hasheous "
                      + "the hashes of what's in your library.",
        Homepage = "https://hasheous.org",
        Capabilities = [IndexCapabilities.HashLookup],
        Access = new PluginAccess { Network = ["hasheous.org"] },
        MaxRequestsPerSecond = 1,
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "baseUrl": {
                  "type": "string",
                  "default": "https://hasheous.org",
                  "title": "Address",
                  "description": "Where Hasheous is. Change it only to use a copy you run yourself."
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context)
    {
        var baseUrl = context.Settings.TryGetProperty("baseUrl", out var v) && v.GetString() is { Length: > 0 } s ? s : "https://hasheous.org";
        return new Hasheous(context.HttpClient, baseUrl.TrimEnd('/'));
    }
}

/// <summary>
/// Hasheous, which maps the hashes in No-Intro's and Redump's DATs to games in IGDB and other databases. It lists no
/// systems of its own: it only answers what a dump is, by hash. Lookups and its images need no key.
/// </summary>
public sealed class Hasheous(HttpClient http, string baseUrl) : IIndexPlugin, IHashLookup
{
    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A hash nothing has: Hasheous answers 404 when it's up.
            using var response = await http.GetAsync($"{baseUrl}/api/v1/Lookup/ByHash/md5/00000000000000000000000000000000", cancellationToken);
            return response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.OK
                ? new HealthReport(HealthStatus.Healthy, "Hasheous answers.")
                : new HealthReport(HealthStatus.Unhealthy, $"Hasheous answered {(int)response.StatusCode} at {baseUrl}.");
        }
        catch (HttpRequestException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Couldn't reach Hasheous at {baseUrl}: {e.Message}");
        }
    }

    public Task<IReadOnlyList<IndexSystem>> ListSystemsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IndexSystem>>([]);

    public Task<IndexSystemDetail?> GetSystemAsync(string systemId, CancellationToken cancellationToken) => Task.FromResult<IndexSystemDetail?>(null);

    public async Task<HashLookupResult?> LookUpAsync(IndexDumpFile file, CancellationToken cancellationToken)
    {
        var hashes = new Dictionary<string, string>();
        if (file.Md5 is { } md5)
            hashes["md5"] = md5;
        if (file.Sha1 is { } sha1)
            hashes["sha1"] = sha1;
        if (file.Crc32 is { } crc)
            hashes["crc"] = crc;
        if (hashes.Count == 0)
            return null;

        using var response = await http.PostAsJsonAsync($"{baseUrl}/api/v1/Lookup/ByHash", hashes, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            throw new IndexUnavailableException($"Hasheous is busy ({(int)response.StatusCode}).");
        response.EnsureSuccessStatusCode();
        var found = await response.Content.ReadFromJsonAsync<Lookup>(Json, cancellationToken);
        if (found is null)
            return null;

        // The cover is the picture Hasheous keeps as the game's logo: its box art, from IGDB.
        var cover = found.Attributes?.FirstOrDefault(a => a.AttributeType == "ImageId" && a.AttributeName == "Logo")?.Value is { ValueKind: JsonValueKind.String } image
            ? $"{baseUrl}/api/v1/images/{Uri.EscapeDataString(image.GetString()!)}"
            : null;
        var ids = (found.Metadata ?? [])
            .Where(m => m.Status == "Mapped" && !string.IsNullOrEmpty(m.Id) && m.Source is not null)
            .GroupBy(m => m.Source!.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First().Id!);
        return new HashLookupResult(found.Name, cover, ids);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Lookup(string? Name, List<Metadata>? Metadata, List<Attribute>? Attributes);

    private sealed record Metadata(string? Id, string? Status, string? Source);

    private sealed record Attribute(string? AttributeType, string? AttributeName, JsonElement Value);
}
