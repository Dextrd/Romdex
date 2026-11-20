namespace Romdex.Core.Catalogue;

/// <summary>Short names for systems, as people write them, such as PS2 for <c>Sony - PlayStation 2</c>, for lists where the DAT's name is too long.</summary>
public static class SystemNames
{
    // Platforms no DAT here is for, which a game may still be listed on beside one that is.
    private static readonly string[] Others =
    [
        "PC", "Win", "Windows", "Mac", "OS X", "macOS", "Linux", "iOS", "Android", "Mobile", "NS2", "Switch 2", "Arcade", "Stadia",
    ];

    private static readonly HashSet<string> Every = new(
        KnownSystems.All.SelectMany(k => k.Dats.Select(Bare).Append(k.Short).Concat(k.Also ?? []))
            .Concat(Others), StringComparer.OrdinalIgnoreCase);

    private static string Bare(string dat) => dat[(dat.IndexOf(" - ", StringComparison.Ordinal) + 3)..];

    /// <summary>
    /// The names a system goes by: as its DAT names it without the maker, such as <c>PlayStation 2</c> for
    /// <c>Sony - PlayStation 2</c>, its short name, and the other ways people write it.
    /// </summary>
    public static IReadOnlyList<string> Names(string name)
    {
        var bare = name.Split(" (")[0];
        var full = bare[(bare.IndexOf(" - ", StringComparison.Ordinal) is var at and >= 0 ? at + 3 : 0)..];
        var known = KnownSystems.Find(name);
        return new[] { full, Short(name) }.Concat(known?.Also ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Whether the text is a platform's name, such as <c>GCN</c> or <c>PC</c>, rather than anything else.</summary>
    public static bool IsPlatform(string text) => Every.Contains(text.Trim());

    /// <summary>
    /// The system's short name: a known one, else the last part of the DAT's name, such as <c>Saturn</c> for
    /// <c>Sega - Saturn</c>, without anything in brackets.
    /// </summary>
    public static string Short(string name)
    {
        if (KnownSystems.Find(name) is { } known)
            return known.Short;
        var bare = name.Split(" (")[0];
        return bare[(bare.LastIndexOf(" - ", StringComparison.Ordinal) is var at and >= 0 ? at + 3 : 0)..].Trim();
    }
}
