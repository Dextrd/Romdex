using System.Text;
using System.Text.RegularExpressions;
using Romdex.Contracts.Plugins.Metadata;

namespace Romdex.Plugins.Wikipedia;

/// <summary>
/// The scores in a Wikipedia article's <c>{{Video game reviews}}</c> box, from its wikitext: each reviewer's, as the
/// article gives it, such as <c>MC = 88/100</c> for Metacritic.
/// </summary>
public static partial class Reviews
{
    // The box's own names for reviewers, as its documentation lists them, by what people call them.
    private static readonly Dictionary<string, string> Reviewers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MC"] = "Metacritic",
        ["GR"] = "GameRankings",
        ["OC"] = "OpenCritic",
        ["1Up"] = "1Up.com",
        ["4Players"] = "4Players",
        ["ACE"] = "ACE",
        ["AllGame"] = "AllGame",
        ["Atom"] = "Atomic Gamer",
        ["CGM"] = "Computer Gaming Magazine",
        ["CGW"] = "Computer Gaming World",
        ["CVG"] = "Computer and Video Games",
        ["Destruct"] = "Destructoid",
        ["Edge"] = "Edge",
        ["EGM"] = "Electronic Gaming Monthly",
        ["EuroG"] = "Eurogamer",
        ["Fam"] = "Famitsu",
        ["G4"] = "G4",
        ["GameFan"] = "GameFan",
        ["GamePro"] = "GamePro",
        ["GameRev"] = "Game Revolution",
        ["GB"] = "Giant Bomb",
        ["GI"] = "Game Informer",
        ["GSpot"] = "GameSpot",
        ["GSpy"] = "GameSpy",
        ["GT"] = "GameTrailers",
        ["GameZone"] = "GameZone",
        ["Hyper"] = "Hyper",
        ["IGN"] = "IGN",
        ["Joystiq"] = "Joystiq",
        ["MMore"] = "Mega Drive Mag",
        ["N64"] = "N64 Magazine",
        ["NGamer"] = "NGamer",
        ["NGC"] = "NGC Magazine",
        ["Next"] = "Next Generation",
        ["NLife"] = "Nintendo Life",
        ["NP"] = "Nintendo Power",
        ["NWR"] = "Nintendo World Report",
        ["OPM"] = "Official PlayStation Magazine",
        ["OPMUK"] = "Official PlayStation Magazine (UK)",
        ["OXM"] = "Official Xbox Magazine",
        ["OXMUK"] = "Official Xbox Magazine (UK)",
        ["PCGUK"] = "PC Gamer (UK)",
        ["PCGUS"] = "PC Gamer (US)",
        ["PCZone"] = "PC Zone",
        ["PSM"] = "PlayStation Magazine",
        ["Poly"] = "Polygon",
        ["RPS"] = "Rock Paper Shotgun",
        ["TouchArcade"] = "TouchArcade",
        ["VG"] = "VideoGamer.com",
        ["X-Play"] = "X-Play",
        ["XPlay"] = "X-Play",
    };

    // How many scores are kept: the aggregates first, then the rest in the article's order.
    private const int Most = 12;

    /// <summary>The scores in the article's review box; none when it has no box.</summary>
    public static IReadOnlyList<GameScore> Read(string wikitext)
    {
        if (Box(wikitext, "Video game reviews") is not { } box)
            return [];
        var parameters = Parameters(box);
        var scores = new List<GameScore>();
        foreach (var (name, value) in parameters)
        {
            // Reviewers of its own: revN for the name and revNScore for what they gave.
            if (OwnReviewer().Match(name) is { Success: true } own)
            {
                if (parameters.FirstOrDefault(p => p.Name.Equals($"rev{own.Groups[1].Value}Score", StringComparison.OrdinalIgnoreCase)).Value is { } given
                    && Text(value) is { Length: > 0 } reviewer && Text(given) is { Length: > 0 } score)
                    scores.Add(new GameScore(reviewer, score));
            }
            else if (Reviewers.TryGetValue(name, out var reviewer) && Text(value) is { Length: > 0 } score)
                scores.Add(new GameScore(reviewer, score));
        }
        return scores.OrderBy(s => s.Reviewer is "Metacritic" or "GameRankings" or "OpenCritic" ? 0 : 1).Take(Most).ToList();
    }

    /// <summary>The first <c>{{name ...}}</c> in the wikitext, braces and all, matched to its end; null when there's none.</summary>
    public static string? Box(string wikitext, string name)
    {
        // Its name may be followed by a comment, such as <!-- Aggregators -->, before its first parameter.
        var at = Regex.Match(wikitext, @"\{\{\s*" + Regex.Escape(name).Replace(@"\ ", @"[ _]") + @"\s*(?:[|}]|<!--)", RegexOptions.IgnoreCase);
        if (!at.Success)
            return null;
        var depth = 0;
        for (var i = at.Index; i < wikitext.Length - 1; i++)
        {
            if (wikitext[i] == '{' && wikitext[i + 1] == '{')
                depth++;
            else if (wikitext[i] == '}' && wikitext[i + 1] == '}')
                depth--;
            else
                continue;
            i++;
            if (depth == 0)
                return wikitext[at.Index..(i + 1)];
        }
        return null;
    }

    /// <summary>A template's named parameters, in order, split only where they're its own and not a link's or another template's.</summary>
    public static IReadOnlyList<(string Name, string Value)> Parameters(string template)
    {
        var inner = template[2..^2];
        var parts = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var two = i + 1 < inner.Length ? inner.Substring(i, 2) : "";
            if (two is "{{" or "[[")
            {
                depth++;
                current.Append(two);
                i++;
            }
            else if (two is "}}" or "]]")
            {
                depth = Math.Max(0, depth - 1);
                current.Append(two);
                i++;
            }
            else if (inner[i] == '|' && depth == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
                current.Append(inner[i]);
        }
        parts.Add(current.ToString());
        return parts.Skip(1)
            .Select(p => p.IndexOf('=') is var eq and > 0 ? (p[..eq].Trim(), p[(eq + 1)..].Trim()) : ("", p.Trim()))
            .Where(p => p.Item1.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Wikitext as plain text: references and comments dropped, links as their words, templates other than a list's
    /// dropped, and line breaks as <c> · </c>, so <c>PS2: 73/100&lt;br&gt;GC: 72/100</c> reads as one line.
    /// </summary>
    public static string Text(string wikitext)
    {
        var text = Comment().Replace(wikitext, "");
        text = Reference().Replace(text, "");
        text = Break().Replace(text, " · ");
        text = Bullet().Replace(text, " · ");
        // Links first, so their | doesn't split a list.
        text = Link().Replace(text, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value);
        text = ExternalLink().Replace(text, "$1");
        // A list's items, such as {{Unbulleted list|Jason Rubin|Bob Rafei}}, one after another.
        for (var pass = 0; pass < 5 && ListTemplate().IsMatch(text); pass++)
            text = ListTemplate().Replace(text, m => string.Join(" · ", m.Groups[1].Value.Split('|').Select(s => s.Trim(' ', '·')).Where(s => s.Length > 0 && !s.Contains('='))));
        // Short names with their long ones on hover, such as {{abbr|GC|GameCube}}, as the short.
        text = Abbreviation().Replace(text, m => m.Groups[1].Value.Trim());
        for (var pass = 0; pass < 5 && Template().IsMatch(text); pass++)
            text = Template().Replace(text, "");
        text = Tag().Replace(text, "");
        text = text.Replace("'''", "").Replace("''", "").Replace("&nbsp;", " ");
        return Spaces().Replace(text, " ").Trim(' ', '·').Trim();
    }

    [GeneratedRegex(@"^rev(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex OwnReviewer();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"<ref[^>/]*/>|<ref[^>]*>.*?</ref>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Reference();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex Break();

    [GeneratedRegex(@"\n\s*\*+\s*")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"\{\{\s*(?:Unbulleted list|Ubl|Plainlist|Flatlist|Plain list|hlist)\s*\|([^{}]*)\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex ListTemplate();

    [GeneratedRegex(@"\{\{\s*(?:abbr|abbreviation|tooltip|tooltip2)\s*\|([^|{}]*)(?:\|[^{}]*)?\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex Abbreviation();

    [GeneratedRegex(@"\{\{[^{}]*\}\}")]
    private static partial Regex Template();

    [GeneratedRegex(@"\[\[(?:[^|\]]*\|)?([^\]|]*)(?:\|([^\]]*))?\]\]")]
    private static partial Regex Link();

    [GeneratedRegex(@"\[https?://\S+\s+([^\]]*)\]")]
    private static partial Regex ExternalLink();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
