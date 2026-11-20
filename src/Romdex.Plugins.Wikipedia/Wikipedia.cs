using System.Globalization;
using System.Net;
using System.Text.Json;
using Romdex.Contracts.Plugins;
using Romdex.Contracts.Plugins.Index;
using Romdex.Contracts.Plugins.Metadata;

namespace Romdex.Plugins.Wikipedia;

/// <summary>Entry point for the Wikipedia plugin.</summary>
public sealed class WikipediaFactory : IPluginFactory
{
    public const string PluginId = "romdex.metadata.wikipedia";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Wikipedia",
        Version = "0.1.0",
        Type = PluginType.Metadata,
        ContractVersion = ContractVersions.Metadata.ToString(),
        Description = "Adds what Wikipedia and Wikidata know about a game to its page: what it is, when it came out, who made it, its genres, its "
                      + "reviews, its age ratings and the other games in its series. It finds games through the Wikipedia articles another plugin, such as Hasheous, links them to, so it needs one of those. "
                      + "It needs no account, and sends only the names of the articles.",
        Homepage = "https://www.wikipedia.org",
        Access = new PluginAccess { Network = ["wikipedia.org", "www.wikidata.org", "query.wikidata.org"] },
        MaxRequestsPerSecond = 2,
        SettingsSchema = JsonDocument.Parse("""{ "type": "object", "properties": {} }""").RootElement,
    };

    public IPlugin Create(PluginContext context) => new Wikipedia(context.HttpClient);
}

/// <summary>
/// Wikipedia and Wikidata, for what a game is: the article's opening for what it's about, the scores in its review
/// box, and Wikidata's facts about it, such as its release dates, developer and genres. It looks games up by the
/// article another plugin found for them (the <c>wikipedia</c> ID); it can't find a game by its hashes. Wikipedia's
/// text is CC BY-SA: Romdex credits it, linking to the article.
/// </summary>
public sealed class Wikipedia(HttpClient http, string wikidata = "https://www.wikidata.org", string sparql = "https://query.wikidata.org/sparql")
    : IMetadataPlugin
{
    /// <summary>How many of a series' games are listed, at most, and of how many series, the ones it's part of included.</summary>
    public const int MostSeriesGames = 200, MostSeries = 3;

    // Wikimedia asks every client to say who it is.
    private const string UserAgent = "Romdex/1.0 (https://github.com/dextrd/Romdex)";

    // How much of the article's opening is kept: its first paragraphs, up to about this long. The page shows a few lines
    // of it and the rest when asked.
    private const int SummaryLength = 4000;

    // Wikidata's properties for what's shown: people by their role, then everything else.
    private static readonly (string Property, string Role)[] Roles =
    [
        ("P57", "Director"), ("P162", "Producer"), ("P287", "Designer"), ("P943", "Programmer"), ("P58", "Writer"), ("P86", "Composer"),
    ];

    // Age ratings: each board's property, and its ratings' items by the mark they're shown with.
    private static readonly (string Property, string Board, Dictionary<string, (string Rating, string Name)> Ratings)[] Boards =
    [
        ("P852", "ESRB", new()
        {
            ["Q14864327"] = ("ec", "Early Childhood"), ["Q14864328"] = ("e", "Everyone"), ["Q14864334"] = ("e", "Kids to Adults"),
            ["Q14864329"] = ("e10", "Everyone 10+"), ["Q14864330"] = ("t", "Teen"), ["Q14864331"] = ("m", "Mature 17+"),
            ["Q14864332"] = ("ao", "Adults Only 18+"), ["Q14864333"] = ("rp", "Rating Pending"),
        }),
        ("P908", "PEGI", new()
        {
            ["Q14915512"] = ("3", "PEGI 3"), ["Q14915514"] = ("7", "PEGI 7"), ["Q14915515"] = ("12", "PEGI 12"), ["Q14915516"] = ("16", "PEGI 16"),
            ["Q14915517"] = ("18", "PEGI 18"),
        }),
        ("P853", "CERO", new()
        {
            ["Q11389850"] = ("a", "CERO A (all ages)"), ["Q14870286"] = ("b", "CERO B (12 and up)"), ["Q14870289"] = ("c", "CERO C (15 and up)"),
            ["Q14870298"] = ("d", "CERO D (17 and up)"), ["Q14870299"] = ("z", "CERO Z (18 and up)"),
        }),
        ("P914", "USK", new()
        {
            ["Q14920387"] = ("0", "USK 0"), ["Q14920391"] = ("6", "USK 6"), ["Q14920392"] = ("12", "USK 12"), ["Q14920393"] = ("16", "USK 16"),
            ["Q14920394"] = ("18", "USK 18"),
        }),
        ("P3156", "ACB", new()
        {
            ["Q26708075"] = ("g", "G"), ["Q26708076"] = ("pg", "PG"), ["Q26708077"] = ("m", "M"), ["Q26708078"] = ("ma15", "MA 15+"),
            ["Q26708079"] = ("r18", "R 18+"),
        }),
    ];

    private const string Released = "P577", Developer = "P178", Publisher = "P123", Genre = "P136", Mode = "P404", Series = "P179",
        Score = "P444", Place = "P291", Platform = "P400", Reviewer = "P447";

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync("https://en.wikipedia.org/w/api.php?action=query&meta=siteinfo&format=json", cancellationToken);
            return response.IsSuccessStatusCode
                ? new HealthReport(HealthStatus.Healthy, "Wikipedia answers.")
                : new HealthReport(HealthStatus.Unhealthy, $"Wikipedia answered {(int)response.StatusCode}.");
        }
        catch (HttpRequestException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Couldn't reach Wikipedia: {e.Message}");
        }
    }

    /// <summary>Wikipedia can't find a game by its hashes: another plugin does, and links it to an article.</summary>
    public Task<DumpMetadata?> LookUpAsync(IndexDumpFile file, CancellationToken cancellationToken) => Task.FromResult<DumpMetadata?>(null);

    public async Task<GameDetails?> DetailsAsync(IReadOnlyDictionary<string, string> ids, CancellationToken cancellationToken)
    {
        if (!ids.TryGetValue("wikipedia", out var link) || Article(link) is not var (site, title))
            return null;

        // The article's Wikidata item, its opening as text, and its wikitext for the review box, in one request.
        using var page = await GetJsonAsync(
            $"https://{site}/w/api.php?action=query&format=json&formatversion=2&redirects=1&prop=pageprops|extracts|revisions"
            + $"&ppprop=wikibase_item&exintro=1&explaintext=1&rvprop=content&rvslots=main&titles={Uri.EscapeDataString(title)}", cancellationToken);
        if (page?.RootElement.TryGetProperty("query", out var query) != true || !query.TryGetProperty("pages", out var pages) || pages.GetArrayLength() == 0)
            return null;
        var article = pages[0];
        if (article.TryGetProperty("missing", out _))
            return null;

        var name = article.TryGetProperty("title", out var t) ? t.GetString() ?? title : title;
        var extract = article.TryGetProperty("extract", out var e) ? e.GetString() : null;
        var wikitext = article.TryGetProperty("revisions", out var revisions) && revisions.GetArrayLength() > 0
                       && revisions[0].TryGetProperty("slots", out var slots) && slots.TryGetProperty("main", out var main)
                       && main.TryGetProperty("content", out var content)
            ? content.GetString() ?? ""
            : "";
        var item = article.TryGetProperty("pageprops", out var props) && props.TryGetProperty("wikibase_item", out var q) ? q.GetString() : null;

        // The article's infobox first, as it's what the article says; Wikidata for what it leaves out.
        var infobox = Infobox.Read(wikitext);
        var facts = item is null ? null : await FactsAsync(item, site.Split('.')[0], cancellationToken);
        var scores = Reviews.Read(wikitext);
        IReadOnlyList<T>? Either<T>(IReadOnlyList<T>? first, IReadOnlyList<T>? then) => first is { Count: > 0 } ? first : then;
        var credits = Roles.Select(r => r.Role).Concat(infobox?.Credits?.Select(c => c.Role) ?? []).Distinct()
            .Select(role => new GameCredit(role, Either(infobox?.Credits?.FirstOrDefault(c => c.Role == role)?.Names,
                Roles.Where(r => r.Role == role).Select(r => facts?.Labels(r.Property)).FirstOrDefault()) ?? []))
            .Where(c => c.Names.Count > 0).ToList();
        return new GameDetails(
            "Wikipedia",
            $"https://{site}/wiki/{Uri.EscapeDataString(name.Replace(' ', '_')).Replace("%2F", "/").Replace("%28", "(").Replace("%29", ")")}",
            Summary(extract),
            Either(infobox?.Released, facts?.Released),
            Either(infobox?.Developers, facts?.Labels(Developer)),
            Either(infobox?.Publishers, facts?.Labels(Publisher)),
            Either(infobox?.Genres, facts?.Labels(Genre)),
            Either(infobox?.Modes, facts?.Labels(Mode)),
            Either(infobox?.Series, facts?.Labels(Series)),
            credits,
            scores.Count > 0 ? scores : facts?.Scores,
            facts?.Ratings,
            facts?.SeriesGames);
    }

    /// <summary>The site and title of a Wikipedia article's address, such as <c>en.wikipedia.org</c> and <c>Crash Bandicoot (video game)</c>; null for any other address.</summary>
    public static (string Site, string Title)? Article(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal))
            return null;
        var host = uri.Host.ToLowerInvariant().Replace(".m.wikipedia.org", ".wikipedia.org");
        // Without a language, as Hasheous sometimes gives it, Wikipedia sends you to the English one.
        if (host is "wikipedia.org" or "www.wikipedia.org")
            host = "en.wikipedia.org";
        else if (!host.EndsWith(".wikipedia.org", StringComparison.Ordinal))
            return null;
        var title = Uri.UnescapeDataString(uri.AbsolutePath["/wiki/".Length..]).Replace('_', ' ').Trim();
        return title.Length == 0 ? null : (host, title);
    }

    // The opening's first paragraphs, whole, up to about SummaryLength.
    private static string? Summary(string? extract)
    {
        if (string.IsNullOrWhiteSpace(extract))
            return null;
        var kept = new List<string>();
        foreach (var paragraph in extract.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (kept.Count > 0 && kept.Sum(p => p.Length) + paragraph.Length > SummaryLength)
                break;
            kept.Add(paragraph);
        }
        return string.Join("\n\n", kept);
    }

    // The item's claims, with the names of everything they point at.
    private async Task<Facts?> FactsAsync(string item, string language, CancellationToken ct)
    {
        using var entity = await GetJsonAsync($"{wikidata}/wiki/Special:EntityData/{Uri.EscapeDataString(item)}.json", ct);
        if (entity?.RootElement.TryGetProperty("entities", out var entities) != true || !entities.TryGetProperty(item, out var found)
            || !found.TryGetProperty("claims", out var claims))
            return null;

        var items = new Dictionary<string, List<string>>();
        var releases = new List<(string Date, string? Place, string? Platform)>();
        var scores = new List<(string Score, string? Reviewer)>();
        foreach (var property in Roles.Select(r => r.Property).Concat([Developer, Publisher, Genre, Mode, Series]))
            items[property] = Statements(claims, property).Select(ItemId).OfType<string>().Distinct().ToList();
        foreach (var statement in Statements(claims, Released))
            if (Date(statement) is { } date)
                releases.Add((date, Qualifier(statement, Place), Qualifier(statement, Platform)));
        foreach (var statement in Statements(claims, Score))
            if (Value(statement) is { ValueKind: JsonValueKind.String } score && score.GetString() is { Length: > 0 } given)
                scores.Add((given, Qualifier(statement, Reviewer)));

        // A board's best rating, where it has one it gives a mark to.
        var ratings = Boards.Select(b => Statements(claims, b.Property).Select(ItemId).OfType<string>()
                .Select(id => b.Ratings.TryGetValue(id, out var r) ? new GameRating(b.Board, r.Rating, r.Name) : null).FirstOrDefault(r => r is not null))
            .OfType<GameRating>().ToList();

        var wanted = items.Values.SelectMany(i => i).Concat(releases.SelectMany(r => new[] { r.Place, r.Platform })).Concat(scores.Select(s => s.Reviewer))
            .OfType<string>().Distinct().ToList();
        var labels = await LabelsAsync(wanted, language, ct);
        string? Label(string? id) => id is null ? null : labels.GetValueOrDefault(id);
        // Its series, then those they're part of, such as Sonic the Hedgehog for Sonic Adventure.
        var series = new List<GameSeries>();
        var mine = items[Series].Where(IsItem).Take(MostSeries).Select(id => (Id: id, Name: Label(id))).ToList();
        foreach (var (id, name) in mine.Concat(await ParentSeriesAsync(mine.Select(m => m.Id).ToList(), language, ct)).DistinctBy(s => s.Id).Take(MostSeries))
            if (name is not null && await SeriesGamesAsync(id, language, ct) is { Count: > 0 } games)
                series.Add(new GameSeries(name, games));
        return new Facts(
            items.ToDictionary(i => i.Key, i => (IReadOnlyList<string>)i.Value.Select(Label).OfType<string>().ToList()),
            releases.OrderBy(r => r.Date, StringComparer.Ordinal).Select(r => new GameRelease(Label(r.Place), r.Date, Label(r.Platform)))
                .DistinctBy(r => (r.Region, r.Date, r.Platform)).ToList(),
            scores.Select(s => new GameScore(Label(s.Reviewer) ?? "Review", s.Score)).ToList(),
            ratings,
            series);
    }

    // The series each of these is part of, by name.
    private async Task<IReadOnlyList<(string Id, string? Name)>> ParentSeriesAsync(IReadOnlyList<string> series, string language, CancellationToken ct)
    {
        if (series.Count == 0)
            return [];
        var query = $$"""
            SELECT ?parent ?parentLabel WHERE {
              VALUES ?series { {{string.Join(' ', series.Select(s => "wd:" + s))}} }
              ?series wdt:P179|wdt:P361 ?parent .
              SERVICE wikibase:label { bd:serviceParam wikibase:language "{{language}},en". }
            }
            """;
        return (await QueryAsync(query, ct))
            .Select(b => (Id: Binding(b, "parent")?.Split('/')[^1], Name: Binding(b, "parentLabel")))
            .Where(p => p.Id is not null && IsItem(p.Id) && p.Name != p.Id).Select(p => (p.Id!, p.Name)).ToList();
    }

    // The games Wikidata says are part of a series, or of a series within it, by name, earliest first. Without them if
    // Wikidata's query service can't answer: they're the least of what's shown.
    private async Task<IReadOnlyList<string>> SeriesGamesAsync(string series, string language, CancellationToken ct)
    {
        if (!IsItem(series))
            return [];
        var query = $$"""
            SELECT ?game ?gameLabel (MIN(?date) AS ?first) WHERE {
              ?game wdt:P179/(wdt:P179|wdt:P361)* wd:{{series}} .
              OPTIONAL { ?game wdt:P577 ?date }
              SERVICE wikibase:label { bd:serviceParam wikibase:language "{{language}},en". }
            } GROUP BY ?game ?gameLabel ORDER BY ?first LIMIT {{MostSeriesGames}}
            """;
        return (await QueryAsync(query, ct)).Select(b => Binding(b, "gameLabel"))
            // A game with no name in either language is labelled by its ID.
            .OfType<string>().Where(name => !IsItem(name)).Distinct().ToList();
    }

    // What Wikidata's query service answers, row by row; none when it can't.
    private async Task<IReadOnlyList<JsonElement>> QueryAsync(string query, CancellationToken ct)
    {
        try
        {
            using var found = await GetJsonAsync($"{sparql}?format=json&query={Uri.EscapeDataString(query)}", ct);
            return found?.RootElement.TryGetProperty("results", out var results) == true && results.TryGetProperty("bindings", out var bindings)
                ? bindings.EnumerateArray().Select(b => b.Clone()).ToList()
                : [];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or MetadataUnavailableException)
        {
            return [];
        }
    }

    private static string? Binding(JsonElement row, string name) =>
        row.TryGetProperty(name, out var binding) && binding.TryGetProperty("value", out var value) ? value.GetString() : null;

    private static bool IsItem(string id) => System.Text.RegularExpressions.Regex.IsMatch(id, @"^Q\d+$");

    private async Task<IReadOnlyDictionary<string, string>> LabelsAsync(IReadOnlyList<string> ids, string language, CancellationToken ct)
    {
        var labels = new Dictionary<string, string>();
        foreach (var chunk in ids.Chunk(50))
        {
            using var found = await GetJsonAsync($"{wikidata}/w/api.php?action=wbgetentities&format=json&props=labels"
                                                 + $"&languages={Uri.EscapeDataString(language)}|en&ids={string.Join('|', chunk)}", ct);
            if (found?.RootElement.TryGetProperty("entities", out var entities) != true)
                continue;
            foreach (var entity in entities.EnumerateObject())
                if (entity.Value.TryGetProperty("labels", out var names)
                    && (names.TryGetProperty(language, out var label) || names.TryGetProperty("en", out label))
                    && label.TryGetProperty("value", out var value) && value.GetString() is { Length: > 0 } text)
                    labels[entity.Name] = text;
        }
        return labels;
    }

    // A property's statements, best first, without those marked wrong.
    private static IEnumerable<JsonElement> Statements(JsonElement claims, string property) =>
        claims.TryGetProperty(property, out var statements) && statements.ValueKind == JsonValueKind.Array
            ? statements.EnumerateArray().Where(s => !(s.TryGetProperty("rank", out var rank) && rank.GetString() == "deprecated"))
                .OrderBy(s => s.TryGetProperty("rank", out var rank) && rank.GetString() == "preferred" ? 0 : 1)
            : [];

    private static JsonElement? Value(JsonElement snak) =>
        snak.TryGetProperty("mainsnak", out var main) ? Value(main) :
        snak.TryGetProperty("datavalue", out var data) && data.TryGetProperty("value", out var value) ? value : null;

    private static string? ItemId(JsonElement statement) =>
        Value(statement) is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("id", out var id) ? id.GetString() : null;

    private static string? Qualifier(JsonElement statement, string property) =>
        statement.TryGetProperty("qualifiers", out var qualifiers) && qualifiers.TryGetProperty(property, out var snaks) && snaks.GetArrayLength() > 0
            ? ItemId(snaks[0])
            : null;

    // A date as precisely as Wikidata has it: a day, a month or a year.
    private static string? Date(JsonElement statement)
    {
        if (Value(statement) is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("time", out var time)
            || time.GetString() is not { Length: > 11 } text || !text.StartsWith('+'))
            return null;
        var precision = value.TryGetProperty("precision", out var p) ? p.GetInt32() : 11;
        var date = text[1..11];
        if (!DateOnly.TryParseExact(date.Replace("-00", "-01"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return null;
        return precision switch { >= 11 => date, 10 => date[..7], _ => date[..4] };
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            throw new MetadataUnavailableException($"Wikipedia is busy ({(int)response.StatusCode}).");
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return http.SendAsync(request, ct);
    }

    private sealed record Facts(IReadOnlyDictionary<string, IReadOnlyList<string>> Items, IReadOnlyList<GameRelease> Released, IReadOnlyList<GameScore> Scores,
        IReadOnlyList<GameRating> Ratings, IReadOnlyList<GameSeries> SeriesGames)
    {
        public IReadOnlyList<string> Labels(string property) => Items.GetValueOrDefault(property) ?? [];
    }
}
