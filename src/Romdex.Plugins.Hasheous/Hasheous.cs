using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Romdex.Contracts.Plugins;
using Romdex.Contracts.Plugins.Index;
using Romdex.Contracts.Plugins.Metadata;

namespace Romdex.Plugins.Hasheous;

/// <summary>Entry point for the Hasheous plugin.</summary>
public sealed class HasheousFactory : IPluginFactory
{
    public const string PluginId = "romdex.metadata.hasheous";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Hasheous",
        Version = "0.1.0",
        Type = PluginType.Metadata,
        ContractVersion = ContractVersions.Metadata.ToString(),
        Description = "Looks up games on Hasheous by their hashes, for titles, cover art, a description and where else they're known, such as IGDB and Wikipedia. It needs no account, but sends Hasheous "
                      + "the hashes of what's in your library.",
        Homepage = "https://hasheous.org",
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
/// Hasheous, which maps the hashes in No-Intro's and Redump's DATs to games in IGDB and other databases. It answers only
/// what a dump is called and looks like, by hash. Lookups and its images need no key.
/// </summary>
public sealed class Hasheous(HttpClient http, string baseUrl) : IMetadataPlugin
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

    public async Task<DumpMetadata?> LookUpAsync(IndexDumpFile file, CancellationToken cancellationToken)
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
            throw new MetadataUnavailableException($"Hasheous is busy ({(int)response.StatusCode}).");
        response.EnsureSuccessStatusCode();
        var found = await response.Content.ReadFromJsonAsync<Lookup>(Json, cancellationToken);
        if (found is null)
            return null;

        // The cover is the picture Hasheous keeps as the game's logo: its box art, from IGDB.
        var cover = found.Attributes?.FirstOrDefault(a => a.AttributeType == "ImageId" && a.AttributeName == "Logo")?.Value is { ValueKind: JsonValueKind.String } image
            ? $"{baseUrl}/api/v1/images/{Uri.EscapeDataString(image.GetString()!)}"
            : null;
        var mapped = (found.Metadata ?? []).Where(m => m.Status == "Mapped" && !string.IsNullOrEmpty(m.Id) && m.Source is not null).ToList();
        var ids = mapped.GroupBy(m => m.Source!.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Id!);
        var links = mapped.Where(m => Uri.TryCreate(m.Link, UriKind.Absolute, out var link) && link.Scheme == Uri.UriSchemeHttps)
            .GroupBy(m => SourceName(m.Source!)).ToDictionary(g => g.Key, g => g.First().Link!);
        // Hasheous' own description, which it has written by AI, in Markdown: kept as plain text.
        var description = found.Attributes?.FirstOrDefault(a => a.AttributeName == "AIDescription")?.Value is { ValueKind: JsonValueKind.String } text
            ? PlainText(text.GetString()!)
            : null;
        return new DumpMetadata(found.Name, cover, ids, links, description);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Sites as people write them, where Hasheous runs the words together.
    private static string SourceName(string source) => source switch
    {
        "TheGamesDb" => "TheGamesDB",
        "GiantBomb" => "Giant Bomb",
        "SteamGridDb" => "SteamGridDB",
        "RetroAchievements" => "RetroAchievements",
        "ScreenScraper" => "ScreenScraper",
        "MobyGames" => "MobyGames",
        "EpicGameStore" => "Epic Games Store",
        _ => source,
    };

    // Markdown's headings, emphasis and links, as text.
    private static string? PlainText(string markdown)
    {
        var lines = markdown.Replace("\r", "").Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"^\s{0,3}(#+|[-*]|\d+\.)\s+", "").Trim())
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\[([^\]]*)\]\([^)]*\)", "$1"))
            .Select(l => l.Replace("**", "").Replace("__", ""));
        var text = System.Text.RegularExpressions.Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n").Trim();
        return text.Length > 0 ? text : null;
    }

    private sealed record Lookup(string? Name, List<Metadata>? Metadata, List<Attribute>? Attributes);

    private sealed record Metadata(string? Id, string? Status, string? Source, string? Link);

    private sealed record Attribute(string? AttributeType, string? AttributeName, JsonElement Value);
}
