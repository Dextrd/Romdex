using System.Text.Json;
using System.Text.RegularExpressions;
using Romdex.Contracts.Plugins.Index;

namespace Romdex.Plugins.DatFolder;

/// <summary>
/// Retool's clone lists (github.com/unexpectedpanda/retool-clonelists-metadata, BSD-3), which say which of a DAT's
/// entries are one game when their names differ, such as a Japanese release under another name. Redump's DATs don't
/// say; No-Intro's standard DATs don't either. Each list is JSON, named for the DAT it's for, such as
/// <c>Sony - PlayStation 2 (Redump).json</c>.
/// </summary>
public static partial class Retool
{
    /// <summary>A clone list: the DAT it's for, its version, and its groups of names.</summary>
    /// <param name="System">The system's name as its DAT gives it, such as <c>Sony - PlayStation 2</c>.</param>
    public sealed record CloneList(string System, string Version, IReadOnlyList<Group> Groups);

    /// <summary>One game: the names its entries go by.</summary>
    public sealed record Group(string Name, IReadOnlyList<Term> Titles);

    /// <summary>A name to look for: an entry's name without its tags, or a pattern for its whole name.</summary>
    public sealed record Term(string Text, Regex? Pattern);

    /// <summary>Reads a clone list; null when it isn't one.</summary>
    public static CloneList? Read(Stream stream)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(stream);
        }
        catch (JsonException)
        {
            return null;
        }
        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("description", out var description)
                || description.ValueKind != JsonValueKind.Object || Text(description, "name") is not { } name
                || !root.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Array)
                return null;
            var groups = new List<Group>();
            foreach (var variant in variants.EnumerateArray())
            {
                if (variant.ValueKind != JsonValueKind.Object || Text(variant, "group") is not { } group)
                    continue;
                // A superset is the game with more in it, such as a Game of the Year edition: the same game. A
                // compilation has other games in it too, so it isn't.
                var terms = Terms(variant, "titles").Concat(Terms(variant, "supersets")).ToList();
                if (terms.Count > 0)
                    groups.Add(new Group(group, terms));
            }
            return new CloneList(SourceSuffix().Replace(name, ""), Text(description, "lastUpdated") ?? "", groups);
        }
    }

    /// <summary>
    /// The dumps with each group's entries made clones of one of them, so they're one title: the entry named as the
    /// group, if there is one, else the first. Entries the list doesn't name, and BIOS files, are left as they are.
    /// </summary>
    public static IReadOnlyList<IndexDump> Apply(IReadOnlyList<IndexDump> dumps, CloneList list)
    {
        var byShort = dumps.Where(d => !d.IsBios).ToLookup(d => Short(d.Name), StringComparer.OrdinalIgnoreCase);
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in list.Groups)
        {
            var members = group.Titles
                .SelectMany(t => t.Pattern is { } pattern ? dumps.Where(d => !d.IsBios && Matches(pattern, d.Name)) : byShort[t.Text])
                .DistinctBy(d => d.Name).ToList();
            if (members.Count < 2)
                continue;
            var parent = members.FirstOrDefault(d => Short(d.Name).Equals(group.Name, StringComparison.OrdinalIgnoreCase) && d.CloneOf is null)
                         ?? members.FirstOrDefault(d => d.CloneOf is null) ?? members[0];
            foreach (var member in members)
                if (member.Name != parent.Name)
                    parents.TryAdd(member.Name, parent.Name);
        }
        return parents.Count == 0 ? dumps : dumps.Select(d => parents.TryGetValue(d.Name, out var parent) ? d with { CloneOf = parent } : d).ToList();
    }

    /// <summary>An entry's name without its tags, as Retool's names are: <c>Crash Twinsanity (Europe, Australia) (En,Fr)</c> is <c>Crash Twinsanity</c>.</summary>
    public static string Short(string name) => Tags().Replace(name, "").Trim();

    private static bool Matches(Regex pattern, string name)
    {
        try
        {
            return pattern.IsMatch(name);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static IEnumerable<Term> Terms(JsonElement variant, string property)
    {
        if (!variant.TryGetProperty(property, out var titles) || titles.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var title in titles.EnumerateArray())
        {
            if (title.ValueKind != JsonValueKind.Object || Text(title, "searchTerm") is not { } term)
                continue;
            if (Text(title, "nameType") != "regex")
            {
                yield return new Term(term, null);
                continue;
            }
            Regex pattern;
            try
            {
                pattern = new Regex(term, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                continue;
            }
            yield return new Term(term, pattern);
        }
    }

    /// <summary>
    /// What makes a list's system and a DAT's one, as Retool and each DAT write them a little differently: without what
    /// says only how the files are kept, such as <c>(Headered)</c>, the maker said once, as in <c>Atari - Atari 2600</c>
    /// for <c>Atari - 2600</c>, and whatever the spacing and punctuation, as in <c>NeoGeo</c> for <c>Neo Geo</c>.
    /// </summary>
    public static string SystemKey(string name)
    {
        name = FormatOnly().Replace(name, "").Trim();
        var dash = name.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && name[(dash + 3)..].StartsWith(name[..dash] + " ", StringComparison.OrdinalIgnoreCase))
            name = name[..(dash + 3)] + name[(dash + 3 + dash + 1)..];
        return string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    [GeneratedRegex(@"\s*(\([^)]*\)|\[[^\]]*\])")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s*\((?:Headered|Headerless|Decrypted|Encrypted|Parent-Clone|Retool)\)", RegexOptions.IgnoreCase)]
    private static partial Regex FormatOnly();

    // The list names its DAT with who publishes it, such as "Sony - PlayStation 2 (Redump)".
    [GeneratedRegex(@"\s*\((Redump|No-Intro)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SourceSuffix();
}
