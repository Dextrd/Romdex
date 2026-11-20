namespace Romdex.Core.Catalogue;

/// <summary>Short names for systems, as people write them, such as PS2 for <c>Sony - PlayStation 2</c>, for lists where the DAT's name is too long.</summary>
public static class SystemNames
{
    // By the start of the DAT's name, as in Emulators: No-Intro adds what it leaves out of the files, such as "(Headered)".
    private static readonly (string Name, string Short)[] Known =
    [
        ("Nintendo - Game Boy", "GB"),
        ("Nintendo - Game Boy Color", "GBC"),
        ("Nintendo - Game Boy Advance", "GBA"),
        ("Nintendo - Nintendo Entertainment System", "NES"),
        ("Nintendo - Family Computer Disk System", "FDS"),
        ("Nintendo - Super Nintendo Entertainment System", "SNES"),
        ("Nintendo - Nintendo 64", "N64"),
        ("Nintendo - Nintendo 64DD", "64DD"),
        ("Nintendo - GameCube", "GC"),
        ("Nintendo - Wii U", "Wii U"),
        ("Nintendo - Wii", "Wii"),
        ("Nintendo - Nintendo DS", "DS"),
        ("Nintendo - Nintendo DSi", "DSi"),
        ("Nintendo - Nintendo 3DS", "3DS"),
        ("Nintendo - Virtual Boy", "VB"),
        ("Sega - Master System - Mark III", "SMS"),
        ("Sega - Game Gear", "GG"),
        ("Sega - Mega Drive - Genesis", "MD"),
        ("Sega - 32X", "32X"),
        ("Sega - Mega CD & Sega CD", "MCD"),
        ("Sega - Mega-CD - Sega CD", "MCD"),
        ("Sega - Saturn", "SAT"),
        ("Sega - Dreamcast", "DC"),
        ("Sony - PlayStation 2", "PS2"),
        ("Sony - PlayStation 3", "PS3"),
        ("Sony - PlayStation Portable", "PSP"),
        ("Sony - PlayStation Vita", "PSV"),
        ("Sony - PlayStation", "PS1"),
        ("Microsoft - Xbox 360", "X360"),
        ("Microsoft - Xbox", "XBOX"),
        ("NEC - PC Engine - TurboGrafx-16", "PCE"),
        ("NEC - PC Engine CD & TurboGrafx CD", "PCECD"),
        ("NEC - PC-FX & PC-FXGA", "PC-FX"),
        ("SNK - Neo Geo CD", "NGCD"),
        ("SNK - NeoGeo Pocket Color", "NGPC"),
        ("SNK - NeoGeo Pocket", "NGP"),
        ("Bandai - WonderSwan Color", "WSC"),
        ("Bandai - WonderSwan", "WS"),
        ("Atari - 2600", "2600"),
        ("Atari - 7800", "7800"),
        ("Atari - Lynx", "Lynx"),
        ("Atari - Jaguar", "Jaguar"),
        ("Coleco - ColecoVision", "CV"),
        ("Panasonic - 3DO Interactive Multiplayer", "3DO"),
    ];

    // Other ways a system is written where a game's out on several, such as "(GCN) 72/100" in a review box, by its
    // short name.
    private static readonly Dictionary<string, string[]> Also = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GB"] = ["Game Boy"],
        ["GBC"] = ["Game Boy Color"],
        ["GBA"] = ["Game Boy Advance"],
        ["NES"] = ["Famicom"],
        ["SNES"] = ["Super NES", "SFC", "Super Famicom"],
        ["N64"] = ["Nintendo 64"],
        ["GC"] = ["GCN", "NGC", "GameCube"],
        ["Wii U"] = ["WiiU"],
        ["DS"] = ["NDS", "Nintendo DS"],
        ["3DS"] = ["N3DS", "Nintendo 3DS"],
        ["SMS"] = ["Master System"],
        ["MD"] = ["GEN", "SMD", "Genesis", "Mega Drive", "Sega Genesis", "Mega Drive/Genesis"],
        ["MCD"] = ["SCD", "Sega CD", "Mega-CD", "Mega CD"],
        ["SAT"] = ["Saturn", "Sega Saturn"],
        ["DC"] = ["Dreamcast"],
        ["PS1"] = ["PS", "PSX", "PlayStation"],
        ["PS2"] = ["PlayStation 2"],
        ["PS3"] = ["PlayStation 3"],
        ["PSP"] = ["PlayStation Portable"],
        ["PSV"] = ["Vita", "PS Vita", "PlayStation Vita"],
        ["XBOX"] = ["Xbox"],
        ["X360"] = ["360", "Xbox 360"],
        ["PCE"] = ["TG16", "TG-16", "TurboGrafx-16", "PC Engine"],
        ["2600"] = ["Atari 2600"],
    };

    // Platforms no DAT here is for, which a game may still be listed on beside one that is.
    private static readonly string[] Others =
    [
        "PC", "Win", "Windows", "Mac", "OS X", "macOS", "Linux", "iOS", "Android", "Mobile", "PS4", "PS5", "XB1", "XONE",
        "Xbox One", "XSX", "Xbox Series X", "Xbox Series X/S", "NS", "Switch", "NS2", "Switch 2", "Arcade", "Stadia",
    ];

    private static readonly HashSet<string> Every = new(
        Known.SelectMany(k => new[] { k.Short, k.Name[(k.Name.IndexOf(" - ", StringComparison.Ordinal) + 3)..] })
            .Concat(Also.Values.SelectMany(a => a)).Concat(Others), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The names a system goes by: as its DAT names it without the maker, such as <c>PlayStation 2</c> for
    /// <c>Sony - PlayStation 2</c>, its short name, and the other ways people write it.
    /// </summary>
    public static IReadOnlyList<string> Names(string name)
    {
        var bare = name.Split(" (")[0];
        var full = bare[(bare.IndexOf(" - ", StringComparison.Ordinal) is var at and >= 0 ? at + 3 : 0)..];
        var abbreviation = Short(name);
        return new[] { full, abbreviation }.Concat(Also.GetValueOrDefault(abbreviation) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Whether the text is a platform's name, such as <c>GCN</c> or <c>PC</c>, rather than anything else.</summary>
    public static bool IsPlatform(string text) => Every.Contains(text.Trim());

    /// <summary>
    /// The system's short name: a known one, else the last part of the DAT's name, such as <c>Saturn</c> for
    /// <c>Sega - Saturn</c>, without anything in brackets.
    /// </summary>
    public static string Short(string name)
    {
        // Longest first, so the PlayStation 2 isn't taken for the PlayStation.
        foreach (var (known, abbreviation) in Known.OrderByDescending(k => k.Name.Length))
            if (name.Equals(known, StringComparison.OrdinalIgnoreCase) || name.StartsWith(known + " (", StringComparison.OrdinalIgnoreCase))
                return abbreviation;
        var bare = name.Split(" (")[0];
        return bare[(bare.LastIndexOf(" - ", StringComparison.Ordinal) is var at and >= 0 ? at + 3 : 0)..].Trim();
    }
}
