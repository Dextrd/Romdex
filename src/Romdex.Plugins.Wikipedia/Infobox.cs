using System.Globalization;
using System.Text.RegularExpressions;
using Romdex.Contracts.Plugins.Metadata;

namespace Romdex.Plugins.Wikipedia;

/// <summary>
/// What a Wikipedia article's <c>{{Infobox video game}}</c> says: who made the game, its genre and modes, and when it
/// came out where, from <c>{{vgrelease}}</c>'s regions and dates. Often more than Wikidata has, such as a publisher
/// in each region.
/// </summary>
public static partial class Infobox
{
    private static readonly (string Parameter, string Role)[] Roles =
    [
        ("director", "Director"), ("producer", "Producer"), ("designer", "Designer"), ("programmer", "Programmer"), ("artist", "Artist"),
        ("writer", "Writer"), ("composer", "Composer"),
    ];

    // {{vgrelease}}'s regions, as its documentation lists them.
    private static readonly Dictionary<string, string> Regions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NA"] = "North America", ["EU"] = "Europe", ["PAL"] = "PAL regions", ["JP"] = "Japan", ["AU"] = "Australia", ["NZ"] = "New Zealand",
        ["UK"] = "United Kingdom", ["WW"] = "Worldwide", ["INT"] = "Worldwide", ["KO"] = "South Korea", ["KR"] = "South Korea", ["AS"] = "Asia",
        ["CN"] = "China", ["TW"] = "Taiwan", ["HK"] = "Hong Kong", ["BR"] = "Brazil", ["DE"] = "Germany", ["FR"] = "France", ["ES"] = "Spain",
        ["IT"] = "Italy", ["RU"] = "Russia", ["SEA"] = "Southeast Asia", ["NA/EU"] = "North America and Europe", ["AUS"] = "Australia",
        ["EUR"] = "Europe", ["JPN"] = "Japan", ["USA"] = "United States", ["US"] = "United States", ["KOR"] = "South Korea", ["CHN"] = "China",
    };

    private static readonly string[] DateFormats = ["MMMM d, yyyy", "MMMM d yyyy", "d MMMM yyyy", "MMM d, yyyy", "d MMM yyyy", "MMMM yyyy", "MMM yyyy", "yyyy"];

    /// <summary>What the article's infobox says, as details from Wikipedia; null when it has none.</summary>
    public static GameDetails? Read(string wikitext)
    {
        if (Reviews.Box(wikitext, "Infobox video game") is not { } box)
            return null;
        var parameters = Reviews.Parameters(box).GroupBy(p => p.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Value);
        // Each on a line of its own, or for a genre or mode, also separated by commas.
        IReadOnlyList<string> Names(string parameter, bool commas = false) =>
            parameters.TryGetValue(parameter, out var value)
                ? Reviews.Text(value).Split(" · ").SelectMany(n => commas ? n.Split(',') : [n]).Select(n => n.Trim(' ', ','))
                    .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        return new GameDetails(
            "Wikipedia",
            Released: parameters.TryGetValue("released", out var released) ? Releases(released) : null,
            Developers: Names("developer"),
            Publishers: Names("publisher"),
            Genres: Names("genre", commas: true),
            Modes: Names("modes", commas: true),
            Series: Names("series"),
            Credits: Roles.Select(r => new GameCredit(r.Role, Names(r.Parameter))).Where(c => c.Names.Count > 0).ToList());
    }

    /// <summary>
    /// The dates in a <c>released</c> parameter's <c>{{vgrelease}}</c>s, as region and date pairs. A date's note in
    /// brackets, such as <c>(Xbox, PS2)</c>, says what it came out on; else the platforms in bold before its
    /// <c>{{vgrelease}}</c>, where there's one for each group of them.
    /// </summary>
    public static IReadOnlyList<GameRelease> Releases(string released)
    {
        var text = Footnote().Replace(Comment().Replace(Reference().Replace(released, ""), ""), "");
        var releases = new List<GameRelease>();
        var after = 0;
        foreach (Match template in Release().Matches(text))
        {
            // The platforms in bold between this group's dates and the last's.
            var bold = string.Join(", ", Bold().Matches(text[after..template.Index]).Select(m => m.Groups[1].Value.Trim()));
            after = template.Index + template.Length;
            var parts = template.Groups[1].Value.Split('|').Select(p => p.Trim()).Where(p => !p.Contains('=')).ToList();
            for (var i = 0; i + 1 < parts.Count; i += 2)
            {
                var date = Reviews.Text(parts[i + 1]);
                var note = Note().Match(date);
                if (Date(note.Success ? date[..note.Index].Trim() : date) is not { } when)
                    continue;
                var region = Regions.GetValueOrDefault(parts[i]) ?? Reviews.Text(parts[i]);
                releases.Add(new GameRelease(region.Length > 0 ? region : null, when, note.Success ? note.Groups[1].Value.Trim() : bold.Length > 0 ? bold : null));
            }
        }
        return releases.DistinctBy(r => (r.Region, r.Date, r.Platform)).ToList();
    }

    // A date as precisely as it's written: a day, a month or a year.
    private static string? Date(string text)
    {
        text = text.Trim().TrimEnd(',', '.');
        foreach (var format in DateFormats)
            if (DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return format.Contains('d') ? date.ToString("yyyy-MM-dd") : format.Contains('M') ? date.ToString("yyyy-MM") : date.ToString("yyyy");
        return null;
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"<ref[^>/]*/>|<ref[^>]*>.*?</ref>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Reference();

    [GeneratedRegex(@"\{\{\s*(?:vgrelease|vgrelease new|video game release)\s*\|([^{}]*)\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex Release();

    [GeneratedRegex(@"\{\{\s*(?:efn|refn|sfn|r|cn|citation needed)\b[^{}]*\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex Footnote();

    [GeneratedRegex(@"'''([^']+)'''")]
    private static partial Regex Bold();

    [GeneratedRegex(@"\(([^)]*)\)\s*$")]
    private static partial Regex Note();
}
