using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Romdex.Contracts.Plugins;
using Romdex.Contracts.Plugins.Index;
using Romdex.Contracts.Plugins.Metadata;

namespace Romdex.Plugins.Libretro;

/// <summary>Entry point for the libretro thumbnails plugin.</summary>
public sealed class LibretroThumbnailsFactory : IPluginFactory
{
    public const string PluginId = "romdex.metadata.libretro-thumbnails";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "libretro thumbnails",
        Version = "0.1.0",
        Type = PluginType.Metadata,
        ContractVersion = ContractVersions.Metadata.ToString(),
        Description = "Adds a title screen and a screenshot to a game's page, from libretro's thumbnails, which RetroArch uses. It finds them by the names "
                      + "No-Intro's and Redump's DATs give games, so it needs no account and sends nothing about your library: it reads the list of what "
                      + "libretro has for a system once a week.",
        Homepage = "https://github.com/libretro-thumbnails/libretro-thumbnails",
        Access = new PluginAccess { Network = ["thumbnails.libretro.com"] },
        MaxRequestsPerSecond = 2,
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "baseUrl": {
                  "type": "string",
                  "default": "https://thumbnails.libretro.com",
                  "title": "Address",
                  "description": "Where the thumbnails are. Change it only to use a mirror."
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context)
    {
        var baseUrl = context.Settings.TryGetProperty("baseUrl", out var v) && v.GetString() is { Length: > 0 } s ? s : "https://thumbnails.libretro.com";
        return new LibretroThumbnails(context.HttpClient, context.DataDirectory, baseUrl.TrimEnd('/'));
    }
}

/// <summary>
/// libretro's thumbnails: for each system, by the name its DAT gives a dump, a box, a title screen and a screenshot.
/// The list of what it has for a system is read once a week and kept, so a game is found without asking after it.
/// </summary>
public sealed partial class LibretroThumbnails(HttpClient http, string folder, string baseUrl) : IMetadataPlugin
{
    /// <summary>How long a system's list is kept before it's read again.</summary>
    public static readonly TimeSpan ListAgain = TimeSpan.FromDays(7);

    // What's shown, by libretro's folder for it.
    private static readonly (string Folder, string Kind)[] Kinds = [("Named_Titles", "title"), ("Named_Snaps", "screenshot")];

    private const string Source = "libretro thumbnails";

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync($"{baseUrl}/", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode
                ? new HealthReport(HealthStatus.Healthy, "libretro's thumbnails answer.")
                : new HealthReport(HealthStatus.Unhealthy, $"libretro's thumbnails answered {(int)response.StatusCode}.");
        }
        catch (HttpRequestException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Couldn't reach libretro's thumbnails: {e.Message}");
        }
    }

    /// <summary>The thumbnails are found by name, not by hash.</summary>
    public Task<DumpMetadata?> LookUpAsync(IndexDumpFile file, CancellationToken cancellationToken) => Task.FromResult<DumpMetadata?>(null);

    public async Task<IReadOnlyList<GameImage>> ImagesAsync(GameImageQuery game, CancellationToken cancellationToken)
    {
        var images = new List<GameImage>();
        foreach (var system in Systems(game.System))
        {
            foreach (var (kind, name) in Kinds)
            {
                var have = await ListAsync(system, kind, cancellationToken);
                // The first dump libretro has a picture of: the library's own, as they're listed first.
                if (game.DumpNames.Select(FileName).FirstOrDefault(have.Contains) is { } file)
                    images.Add(new GameImage(name, $"{baseUrl}/{Uri.EscapeDataString(system)}/{kind}/{Uri.EscapeDataString(file)}.png", Source));
            }
            if (images.Count > 0)
                break;
        }
        return images;
    }

    /// <summary>
    /// What libretro calls a system: as its DAT does, or without what the DAT adds in brackets, such as
    /// <c>(Headerless)</c> in No-Intro's NES.
    /// </summary>
    public static IEnumerable<string> Systems(string system)
    {
        yield return system;
        var bare = system.Split(" (")[0];
        if (bare != system)
            yield return bare;
    }

    /// <summary>A dump's name as libretro files its pictures: with <c>&amp;*/:`&lt;&gt;?\|"</c> as <c>_</c>.</summary>
    public static string FileName(string dumpName) => Unsafe().Replace(dumpName, "_");

    // The names of the pictures libretro has for a system, kept on disk and read again once a week.
    private async Task<HashSet<string>> ListAsync(string system, string kind, CancellationToken ct)
    {
        var path = Path.Combine(folder, $"{FileName(system)} - {kind}.txt");
        if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow - ListAgain)
            return [.. await File.ReadAllLinesAsync(path, ct)];

        using var response = await http.GetAsync($"{baseUrl}/{Uri.EscapeDataString(system)}/{kind}/", ct);
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            throw new MetadataUnavailableException($"libretro's thumbnails are busy ({(int)response.StatusCode}).");
        HashSet<string> names = [];
        // A system libretro doesn't have is kept as an empty list, so it isn't asked after again for a week.
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
            var listing = await response.Content.ReadAsStringAsync(ct);
            names = [.. Picture().Matches(listing).Select(m => WebUtility.HtmlDecode(Uri.UnescapeDataString(m.Groups[1].Value)))];
        }
        Directory.CreateDirectory(folder);
        await File.WriteAllLinesAsync(path, names.Order(StringComparer.Ordinal), ct);
        return names;
    }

    [GeneratedRegex(@"[&*/:`<>?\\|""]")]
    private static partial Regex Unsafe();

    // Each picture in the folder's listing, by its name without .png.
    [GeneratedRegex(@"href=""([^""/?]+)\.png""", RegexOptions.IgnoreCase)]
    private static partial Regex Picture();
}
