using System.Net;
using System.Text.Json;
using Romdex.Contracts.Plugins;
using Romdex.Contracts.Plugins.Index;
using Romdex.Contracts.Plugins.Metadata;

namespace Romdex.Plugins.TheGamesDb;

/// <summary>Entry point for the TheGamesDB plugin.</summary>
public sealed class TheGamesDbFactory : IPluginFactory
{
    public const string PluginId = "romdex.metadata.thegamesdb";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "TheGamesDB",
        Version = "0.1.0",
        Type = PluginType.Metadata,
        ContractVersion = ContractVersions.Metadata.ToString(),
        Description = "Looks up games on TheGamesDB, by their hashes or the ID Hasheous links them to, for covers, what they are, their ESRB rating, a title "
                      + "screen and screenshots. It needs an API key of your own, which TheGamesDB gives out on its forum, and sends it the hashes of "
                      + "what's in your library. A key allows so many lookups a month.",
        Homepage = "https://thegamesdb.net",
        Access = new PluginAccess { Network = ["api.thegamesdb.net"] },
        MaxRequestsPerSecond = 1,
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "required": ["apiKey"],
              "properties": {
                "apiKey": {
                  "type": "string",
                  "writeOnly": true,
                  "title": "API key",
                  "description": "Your own key: make an account on forums.thegamesdb.net and ask for one in its API section."
                },
                "baseUrl": {
                  "type": "string",
                  "default": "https://api.thegamesdb.net",
                  "title": "Address",
                  "description": "Where TheGamesDB's API is. Change it only to use a mirror."
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context)
    {
        string? Text(string name) => context.Settings.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;
        return new TheGamesDb(context.HttpClient, context.DataDirectory, (Text("baseUrl") ?? "https://api.thegamesdb.net").TrimEnd('/'), Text("apiKey") ?? "");
    }
}

/// <summary>
/// TheGamesDB, a database of games people keep, which knows some dumps by their hashes. Each lookup counts against the
/// key's monthly allowance, so the names of genres, developers and publishers, which it gives by ID, are kept.
/// </summary>
public sealed class TheGamesDb(HttpClient http, string folder, string baseUrl, string apiKey) : IMetadataPlugin
{
    /// <summary>The scheme of its IDs, as Hasheous gives them too.</summary>
    public const string Scheme = "thegamesdb";

    /// <summary>How many screenshots a game's page shows, at most.</summary>
    public const int MostScreenshots = 6;

    private const string Source = "TheGamesDB";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (apiKey.Length == 0)
            return new HealthReport(HealthStatus.Unhealthy, "Add your TheGamesDB API key in this plugin's settings.");
        try
        {
            // Asking how much is left doesn't count against it.
            using var response = await http.GetAsync(Url("v1/API/Limit"), cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new HealthReport(HealthStatus.Unhealthy, "TheGamesDB refused the API key.");
            if (!response.IsSuccessStatusCode)
                return new HealthReport(HealthStatus.Unhealthy, $"TheGamesDB answered {(int)response.StatusCode}.");
            var limit = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)).RootElement;
            var left = Number(limit, "remaining_monthly_allowance") + Number(limit, "extra_allowance");
            return left > 0
                ? new HealthReport(HealthStatus.Healthy, $"TheGamesDB answers, with {left:N0} lookup{(left == 1 ? "" : "s")} left this month.")
                : new HealthReport(HealthStatus.Degraded, "This month's lookups are used up: TheGamesDB answers again next month.");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Couldn't reach TheGamesDB: {e.Message}");
        }
    }

    public async Task<DumpMetadata?> LookUpAsync(IndexDumpFile file, CancellationToken cancellationToken)
    {
        var (hash, type) = file.Md5 is { } md5 ? (md5, "md5") : file.Crc32 is { } crc ? (crc, "crc") : (null, null);
        if (hash is null || apiKey.Length == 0)
            return null;
        var found = await GetAsync($"v1/Games/ByGameHash?hash={hash}&filter%5Btype%5D={type}&include=boxart", cancellationToken);
        if (found is not { } root || Games(root).FirstOrDefault() is not { ValueKind: JsonValueKind.Object } game || Id(game) is not { } id)
            return null;
        return new DumpMetadata(Text(game, "game_title"), Boxart(root, id), new Dictionary<string, string> { [Scheme] = id },
            new Dictionary<string, string> { [Source] = Page(id) });
    }

    public async Task<GameDetails?> DetailsAsync(IReadOnlyDictionary<string, string> ids, CancellationToken cancellationToken)
    {
        if (!ids.TryGetValue(Scheme, out var id) || !IsId(id) || apiKey.Length == 0)
            return null;
        var found = await GetAsync($"v1/Games/ByGameID?id={id}&fields=players,publishers,genres,overview,rating,coop", cancellationToken);
        if (found is not { } root || Games(root).FirstOrDefault() is not { ValueKind: JsonValueKind.Object } game)
            return null;

        var modes = new List<string>();
        if (Number(game, "players") is var players and > 0)
            modes.Add(players == 1 ? "Single-player" : $"Up to {players} players");
        if (Text(game, "coop") == "Yes")
            modes.Add("Co-op");
        return new GameDetails(Source, Page(id), Text(game, "overview"),
            Released: Text(game, "release_date") is { Length: >= 4 } date ? [new GameRelease(null, date)] : null,
            Developers: await NamesAsync("developers", Ids(game, "developers"), cancellationToken),
            Publishers: await NamesAsync("publishers", Ids(game, "publishers"), cancellationToken),
            Genres: await NamesAsync("genres", Ids(game, "genres"), cancellationToken),
            Modes: modes,
            Ratings: Rating(Text(game, "rating")) is { } rating ? [rating] : null);
    }

    public async Task<IReadOnlyList<GameImage>> ImagesAsync(GameImageQuery game, CancellationToken cancellationToken)
    {
        if (!game.Ids.TryGetValue(Scheme, out var id) || !IsId(id) || apiKey.Length == 0)
            return [];
        var found = await GetAsync($"v1/Games/Images?games_id={id}&filter%5Btype%5D=titlescreen,screenshot", cancellationToken);
        if (found is not { } root || !root.TryGetProperty("data", out var data) || Text(data, "base_url", "original") is not { } baseImages
            || !data.TryGetProperty("images", out var all) || all.ValueKind != JsonValueKind.Object || !all.TryGetProperty(id, out var images)
            || images.ValueKind != JsonValueKind.Array)
            return [];
        var pictures = images.EnumerateArray().Select(i => (Type: Text(i, "type"), File: Text(i, "filename"))).Where(i => i.File is not null).ToList();
        return pictures.Where(p => p.Type == "titlescreen").Take(1).Select(p => new GameImage("title", baseImages + p.File, Source))
            .Concat(pictures.Where(p => p.Type == "screenshot").Take(MostScreenshots).Select(p => new GameImage("screenshot", baseImages + p.File, Source)))
            .ToList();
    }

    /// <summary>The rating TheGamesDB gives, which is ESRB's written out, such as <c>E10+ - Everyone 10+</c>; null for none or <c>Not Rated</c>.</summary>
    public static GameRating? Rating(string? rating)
    {
        var code = rating?.Split(" - ")[0].Trim().ToUpperInvariant();
        return code switch
        {
            "EC" => new GameRating("ESRB", "ec", "Early Childhood"),
            "E" or "K-A" => new GameRating("ESRB", "e", "Everyone"),
            "E10+" => new GameRating("ESRB", "e10", "Everyone 10+"),
            "T" => new GameRating("ESRB", "t", "Teen"),
            "M" => new GameRating("ESRB", "m", "Mature 17+"),
            "AO" => new GameRating("ESRB", "ao", "Adults Only 18+"),
            "RP" => new GameRating("ESRB", "rp", "Rating Pending"),
            _ => null,
        };
    }

    // The names of genres, developers or publishers by their IDs: kept, as they don't change, and asked only for those
    // not known yet. Genres come all at once; there are few.
    private async Task<IReadOnlyList<string>?> NamesAsync(string kind, IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return null;
        var path = Path.Combine(folder, $"{kind}.json");
        Dictionary<string, string> known = [];
        if (File.Exists(path))
            try
            {
                known = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path, ct), Json) ?? [];
            }
            catch (JsonException)
            {
            }
        var missing = ids.Where(i => !known.ContainsKey(i)).ToList();
        if (missing.Count > 0)
        {
            var asked = kind == "genres" ? "v1/Genres" : $"v1/{char.ToUpperInvariant(kind[0])}{kind[1..]}/By{char.ToUpperInvariant(kind[0])}{kind[1..^1]}ID?id={string.Join(',', missing)}";
            if (await GetAsync(asked, ct) is { } root && root.TryGetProperty("data", out var data) && data.TryGetProperty(kind, out var list)
                && list.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in list.EnumerateObject())
                    if (Text(entry.Value, "name") is { } name)
                        known[entry.Name] = name;
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(known, Json), ct);
            }
        }
        var names = ids.Select(i => known.GetValueOrDefault(i)).OfType<string>().ToList();
        return names.Count > 0 ? names : null;
    }

    // A call to the API, null when it found nothing. The key goes in the address, as TheGamesDB asks.
    private async Task<JsonElement?> GetAsync(string query, CancellationToken ct)
    {
        using var response = await http.GetAsync(Url(query), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        // It says 403 both for a key it doesn't know and for one whose allowance is spent.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new MetadataUnavailableException("TheGamesDB refused the API key, or this month's lookups are used up.");
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            throw new MetadataUnavailableException($"TheGamesDB is busy ({(int)response.StatusCode}).");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
    }

    private string Url(string query) => $"{baseUrl}/{query}{(query.Contains('?') ? '&' : '?')}apikey={Uri.EscapeDataString(apiKey)}";

    private static string Page(string id) => $"https://thegamesdb.net/game.php?id={id}";

    // The front of its box, from what the lookup included.
    private static string? Boxart(JsonElement root, string id)
    {
        if (!root.TryGetProperty("include", out var include) || !include.TryGetProperty("boxart", out var boxart)
            || Text(boxart, "base_url", "original") is not { } baseImages || !boxart.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(id, out var images) || images.ValueKind != JsonValueKind.Array)
            return null;
        return images.EnumerateArray().FirstOrDefault(i => Text(i, "side") == "front") is { ValueKind: JsonValueKind.Object } front && Text(front, "filename") is { } file
            ? baseImages + file
            : null;
    }

    private static IEnumerable<JsonElement> Games(JsonElement root) =>
        root.TryGetProperty("data", out var data) && data.TryGetProperty("games", out var games) && games.ValueKind == JsonValueKind.Array
            ? games.EnumerateArray()
            : [];

    private static string? Id(JsonElement game) => game.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null;

    private static bool IsId(string id) => id.Length > 0 && id.All(char.IsAsciiDigit);

    private static IReadOnlyList<string> Ids(JsonElement game, string name) =>
        game.TryGetProperty(name, out var ids) && ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Number).Select(i => i.GetRawText()).ToList()
            : [];

    private static int Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var i) ? i : 0;

    private static string? Text(JsonElement element, params string[] path)
    {
        foreach (var name in path)
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
                return null;
        return element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } s ? s.Trim() : null;
    }
}
