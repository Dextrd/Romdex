using System.Net;
using System.Text.Json;
using Romdex.Contracts.Plugins;
using Romdex.Contracts.Plugins.Index;
using Romdex.Contracts.Plugins.Metadata;

namespace Romdex.Plugins.ScreenScraper;

/// <summary>Entry point for the ScreenScraper plugin.</summary>
public sealed class ScreenScraperFactory : IPluginFactory
{
    public const string PluginId = "romdex.metadata.screenscraper";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "ScreenScraper",
        Version = "0.1.0",
        Type = PluginType.Metadata,
        ContractVersion = ContractVersions.Metadata.ToString(),
        Description = "Looks up games on ScreenScraper, by their hashes or the ID Hasheous links them to, for covers, what they are, their age ratings, a "
                      + "title screen and a screenshot. ScreenScraper answers only programs with a developer ID, which it gives out on its forum; your own "
                      + "account is optional, and allows more lookups a day. It's sent the hashes and names of what's in your library.",
        Homepage = "https://www.screenscraper.fr",
        Access = new PluginAccess { Network = ["api.screenscraper.fr"] },
        MaxRequestsPerSecond = 1,
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "required": ["devId", "devPassword"],
              "properties": {
                "devId": {
                  "type": "string",
                  "title": "Developer ID",
                  "description": "ScreenScraper's ID for the program asking, which it gives out on its forum to people who make one."
                },
                "devPassword": {
                  "type": "string",
                  "writeOnly": true,
                  "title": "Developer password"
                },
                "username": {
                  "type": "string",
                  "title": "Your user name",
                  "description": "Optional: your account on screenscraper.fr, for more lookups a day, and to be let in when it's busy."
                },
                "password": {
                  "type": "string",
                  "writeOnly": true,
                  "title": "Your password"
                },
                "baseUrl": {
                  "type": "string",
                  "default": "https://api.screenscraper.fr/api2",
                  "title": "Address",
                  "description": "Where ScreenScraper's API is. Change it only to use a mirror."
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context)
    {
        string? Text(string name) => context.Settings.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;
        return new ScreenScraper(context.HttpClient, (Text("baseUrl") ?? "https://api.screenscraper.fr/api2").TrimEnd('/'),
            new ScreenScraperLogin(Text("devId") ?? "", Text("devPassword") ?? "", Text("username"), Text("password")));
    }
}

/// <summary>Who's asking: the program, by its developer ID, and the person, by their account if they gave one.</summary>
public sealed record ScreenScraperLogin(string DevId, string DevPassword, string? Username, string? Password);

/// <summary>
/// ScreenScraper, a database of games people keep, which knows most dumps in No-Intro's and Redump's DATs by their
/// hashes. Every call carries the developer ID, and the person's account when there is one: its pictures' addresses
/// carry them too, as it gives them.
/// </summary>
public sealed class ScreenScraper(HttpClient http, string baseUrl, ScreenScraperLogin login) : IMetadataPlugin
{
    /// <summary>The scheme of its IDs, as Hasheous gives them too.</summary>
    public const string Scheme = "screenscraper";

    private const string Source = "ScreenScraper";

    /// <summary>What the program's called, as ScreenScraper asks.</summary>
    public const string SoftName = "Romdex";

    // Regions, those games are most often known by first; "ss" is ScreenScraper's own name for a game.
    private static readonly string[] Regions = ["ss", "wor", "us", "eu", "uk", "jp"];

    private static readonly Dictionary<string, string> RegionNames = new()
    {
        ["wor"] = "Worldwide", ["us"] = "North America", ["eu"] = "Europe", ["jp"] = "Japan", ["uk"] = "United Kingdom", ["fr"] = "France",
        ["de"] = "Germany", ["es"] = "Spain", ["it"] = "Italy", ["au"] = "Australia", ["br"] = "Brazil", ["kr"] = "Korea", ["cn"] = "China",
        ["asi"] = "Asia",
    };

    private bool HasLogin => login.DevId.Length > 0 && login.DevPassword.Length > 0;

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (!HasLogin)
            return new HealthReport(HealthStatus.Unhealthy, "Add a ScreenScraper developer ID and password in this plugin's settings.");
        try
        {
            // With an account, what it allows; without one, whether the servers answer.
            using var response = await http.GetAsync(Url(login.Username is null ? "ssinfraInfos.php" : "ssuserInfos.php", []), cancellationToken);
            if (Problem(response.StatusCode) is { } problem)
                return new HealthReport(HealthStatus.Unhealthy, problem);
            if (!response.IsSuccessStatusCode)
                return new HealthReport(HealthStatus.Unhealthy, $"ScreenScraper answered {(int)response.StatusCode}.");
            var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)).RootElement;
            if (Text(root, "response", "ssuser", "maxrequestsperday") is { } most && int.TryParse(most, out var perDay)
                && int.TryParse(Text(root, "response", "ssuser", "requeststoday"), out var today))
                return new HealthReport(perDay > today ? HealthStatus.Healthy : HealthStatus.Degraded,
                    $"ScreenScraper answers, with {Math.Max(0, perDay - today):N0} of {perDay:N0} lookups left today.");
            return new HealthReport(HealthStatus.Healthy, "ScreenScraper answers.");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Couldn't reach ScreenScraper: {e.Message}");
        }
    }

    public async Task<DumpMetadata?> LookUpAsync(IndexDumpFile file, CancellationToken cancellationToken)
    {
        if (!HasLogin || (file.Crc32 ?? file.Md5 ?? file.Sha1) is null)
            return null;
        var query = new List<(string, string)> { ("romtype", "rom"), ("romnom", file.Name), ("romtaille", file.Size.ToString()) };
        if (file.Crc32 is { } crc)
            query.Add(("crc", crc));
        if (file.Md5 is { } md5)
            query.Add(("md5", md5));
        if (file.Sha1 is { } sha1)
            query.Add(("sha1", sha1));
        if (await GameAsync(query, cancellationToken) is not { } game || Text(game, "id") is not { } id)
            return null;
        return new DumpMetadata(ByRegion(game, "noms"), Media(game, "box-2D"), new Dictionary<string, string> { [Scheme] = id },
            new Dictionary<string, string> { [Source] = Page(id) });
    }

    public async Task<GameDetails?> DetailsAsync(IReadOnlyDictionary<string, string> ids, CancellationToken cancellationToken)
    {
        if (!ids.TryGetValue(Scheme, out var id) || !IsId(id) || !HasLogin || await GameAsync([("gameid", id)], cancellationToken) is not { } game)
            return null;

        var released = Array(game, "dates").Select(d => (Region: Text(d, "region"), Date: Text(d, "text")))
            .Where(d => d.Date is { Length: >= 4 })
            .Select(d => new GameRelease(d.Region is null ? null : RegionNames.GetValueOrDefault(d.Region, d.Region.ToUpperInvariant()), d.Date!))
            .DistinctBy(r => (r.Region, r.Date)).ToList();
        var genres = Array(game, "genres").Select(g => InLanguage(Array(g, "noms"))).OfType<string>().Distinct().ToList();
        var players = Text(game, "joueurs", "text");
        return new GameDetails(Source, Page(id), InLanguage(Array(game, "synopsis")),
            Released: released,
            Developers: Text(game, "developpeur", "text") is { } developer ? [developer] : null,
            Publishers: Text(game, "editeur", "text") is { } publisher ? [publisher] : null,
            Genres: genres,
            Modes: players is null ? null : [players == "1" ? "Single-player" : $"{players} players"],
            Ratings: Array(game, "classifications").Select(c => Rating(Text(c, "type"), Text(c, "text"))).OfType<GameRating>().DistinctBy(r => r.Board).ToList());
    }

    public async Task<IReadOnlyList<GameImage>> ImagesAsync(GameImageQuery game, CancellationToken cancellationToken)
    {
        if (!game.Ids.TryGetValue(Scheme, out var id) || !IsId(id) || !HasLogin || await GameAsync([("gameid", id)], cancellationToken) is not { } found)
            return [];
        var images = new List<GameImage>();
        if (Media(found, "sstitle") is { } title)
            images.Add(new GameImage("title", title, Source));
        if (Media(found, "ss") is { } screenshot)
            images.Add(new GameImage("screenshot", screenshot, Source));
        return images;
    }

    /// <summary>A rating as ScreenScraper gives it, by its board and its mark, such as <c>PEGI</c> and <c>12</c>; null for one Romdex hasn't a mark of.</summary>
    public static GameRating? Rating(string? board, string? rating)
    {
        var mark = rating?.Trim().ToLowerInvariant().Replace(" ", "");
        return board?.Trim().ToUpperInvariant() switch
        {
            "ESRB" => mark switch
            {
                "ec" => new GameRating("ESRB", "ec", "Early Childhood"),
                "e" or "k-a" => new GameRating("ESRB", "e", "Everyone"),
                "e10" or "e10+" => new GameRating("ESRB", "e10", "Everyone 10+"),
                "t" => new GameRating("ESRB", "t", "Teen"),
                "m" => new GameRating("ESRB", "m", "Mature 17+"),
                "ao" => new GameRating("ESRB", "ao", "Adults Only 18+"),
                "rp" => new GameRating("ESRB", "rp", "Rating Pending"),
                _ => null,
            },
            "PEGI" => mark?.TrimEnd('+') is "3" or "7" or "12" or "16" or "18" ? new GameRating("PEGI", mark.TrimEnd('+'), $"PEGI {mark.TrimEnd('+')}") : null,
            "USK" => mark?.TrimEnd('+') is "0" or "6" or "12" or "16" or "18" ? new GameRating("USK", mark.TrimEnd('+'), $"USK {mark.TrimEnd('+')}") : null,
            "CERO" => mark switch
            {
                "a" => new GameRating("CERO", "a", "CERO A (all ages)"),
                "b" => new GameRating("CERO", "b", "CERO B (12 and up)"),
                "c" => new GameRating("CERO", "c", "CERO C (15 and up)"),
                "d" => new GameRating("CERO", "d", "CERO D (17 and up)"),
                "z" => new GameRating("CERO", "z", "CERO Z (18 and up)"),
                _ => null,
            },
            "ACB" or "OFLC" => mark switch
            {
                "g" => new GameRating("ACB", "g", "G"),
                "pg" => new GameRating("ACB", "pg", "PG"),
                "m" => new GameRating("ACB", "m", "M"),
                "ma15" or "ma15+" => new GameRating("ACB", "ma15", "MA 15+"),
                "r18" or "r18+" => new GameRating("ACB", "r18", "R 18+"),
                _ => null,
            },
            _ => null,
        };
    }

    // A game, by its hashes or its ID; null when ScreenScraper doesn't know it.
    private async Task<JsonElement?> GameAsync(IReadOnlyList<(string Name, string Value)> query, CancellationToken ct)
    {
        using var response = await http.GetAsync(Url("jeuInfos.php", query), ct);
        // 404 for a game it doesn't know; 400 for a name it won't take, which is as good as not knowing it.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            return null;
        if (Problem(response.StatusCode) is { } problem)
            throw new MetadataUnavailableException(problem);
        response.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        return root.TryGetProperty("response", out var answer) && answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("jeu", out var game)
               && game.ValueKind == JsonValueKind.Object
            ? game
            : null;
    }

    // What its answers that aren't about a game mean, as its documentation has them.
    private static string? Problem(HttpStatusCode status) => (int)status switch
    {
        401 => "ScreenScraper is too busy for people without an account: give yours in this plugin's settings, or try later.",
        403 => "ScreenScraper refused the developer ID and password.",
        423 => "ScreenScraper's API is closed for now.",
        426 => "ScreenScraper won't answer this version of the plugin.",
        429 => "ScreenScraper asks for fewer lookups at once.",
        430 => "Today's ScreenScraper lookups are used up: it answers again tomorrow.",
        431 => "ScreenScraper has been asked about too many games it doesn't know today: it answers again tomorrow.",
        503 => "ScreenScraper is busy (503).",
        _ => null,
    };

    private string Url(string endpoint, IEnumerable<(string Name, string Value)> query)
    {
        var all = new List<(string, string)> { ("devid", login.DevId), ("devpassword", login.DevPassword), ("softname", SoftName), ("output", "json") };
        if (login.Username is { } user)
            all.AddRange([("ssid", user), ("sspassword", login.Password ?? "")]);
        all.AddRange(query);
        return $"{baseUrl}/{endpoint}?{string.Join('&', all.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}"))}";
    }

    private static string Page(string id) => $"https://www.screenscraper.fr/gameinfos.php?gameid={id}";

    // A picture of a kind, of the region games are most often known by.
    private static string? Media(JsonElement game, string type)
    {
        var medias = Array(game, "medias").Where(m => Text(m, "type") == type && Text(m, "url") is not null).ToList();
        return medias.OrderBy(m => Text(m, "region") is { } region && System.Array.IndexOf(Regions, region) is var at and >= 0 ? at : Regions.Length)
            .Select(m => Text(m, "url")).FirstOrDefault();
    }

    // A name or the like given for each region, the likeliest first.
    private static string? ByRegion(JsonElement game, string name)
    {
        var all = Array(game, name).Select(n => (Region: Text(n, "region"), Text: Text(n, "text"))).Where(n => n.Text is not null).ToList();
        return all.OrderBy(n => n.Region is { } region && System.Array.IndexOf(Regions, region) is var at and >= 0 ? at : Regions.Length)
            .Select(n => n.Text).FirstOrDefault();
    }

    // A text given in several languages, in English if there is one.
    private static string? InLanguage(IEnumerable<JsonElement> texts)
    {
        var all = texts.Select(t => (Language: Text(t, "langue"), Text: Text(t, "text"))).Where(t => t.Text is not null).ToList();
        return (all.FirstOrDefault(t => t.Language == "en").Text ?? all.FirstOrDefault().Text)?.Replace("\r", "");
    }

    private static bool IsId(string id) => id.Length > 0 && id.All(char.IsAsciiDigit);

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : [];

    private static string? Text(JsonElement element, params string[] path)
    {
        foreach (var name in path)
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
                return null;
        return element.ValueKind switch
        {
            JsonValueKind.String when element.GetString() is { Length: > 0 } s => s.Trim(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null,
        };
    }
}
