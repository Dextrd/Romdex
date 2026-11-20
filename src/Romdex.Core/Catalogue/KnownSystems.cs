using Romdex.Client;
using Romdex.Core.Play;

namespace Romdex.Core.Catalogue;

/// <summary>
/// A system Romdex knows: what it's called, and how its games are kept and played. Any system with a DAT works; these
/// get short names in lists, storage formats that suit them and, where EmulatorJS has a core, play in the browser.
/// </summary>
/// <param name="Short">How people write it for short, such as <c>PS2</c>.</param>
/// <param name="Dats">Its DATs' names, without what's in brackets, as No-Intro, Redump and libretro each write them.</param>
/// <param name="Also">Other ways it's written, such as in a review box.</param>
/// <param name="Play">How EmulatorJS plays it; null when it can't.</param>
/// <param name="Formats">The storage formats that suit it; null to go by its files' extensions.</param>
public sealed record KnownSystem(string Short, string[] Dats, string[]? Also = null, Emulator? Play = null, StorageFormat[]? Formats = null)
{
    /// <summary>Its DAT's name without the maker, such as <c>PlayStation 2</c>.</summary>
    public string Name => Dats[0][(Dats[0].IndexOf(" - ", StringComparison.Ordinal) + 3)..];
}

public static class KnownSystems
{
    // Discs that nothing plays from a CHD or RVZ: they're kept as they came.
    private static readonly StorageFormat[] AsTheyCome = [StorageFormat.Keep];

    public static readonly IReadOnlyList<KnownSystem> All =
    [
        // Nintendo
        new("NES", ["Nintendo - Nintendo Entertainment System"], ["Famicom"], new("nes")),
        new("FDS", ["Nintendo - Family Computer Disk System"], ["Famicom Disk System"], new("nes", Emulators.DiskSystem)),
        new("SNES", ["Nintendo - Super Nintendo Entertainment System"], ["Super NES", "SFC", "Super Famicom"], new("snes")),
        new("N64", ["Nintendo - Nintendo 64"], ["Nintendo 64"], new("n64")),
        new("64DD", ["Nintendo - Nintendo 64DD"]),
        new("GC", ["Nintendo - GameCube"], ["GCN", "NGC", "GameCube"]),
        new("Wii", ["Nintendo - Wii"]),
        new("Wii U", ["Nintendo - Wii U"], ["WiiU"], Formats: AsTheyCome),
        new("Switch", ["Nintendo - Nintendo Switch", "Nintendo - Switch"], ["NS", "Nintendo Switch"]),
        new("GB", ["Nintendo - Game Boy"], ["Game Boy"], new("gb")),
        new("GBC", ["Nintendo - Game Boy Color"], ["Game Boy Color", "Game Boy Colour"], new("gb")),
        new("GBA", ["Nintendo - Game Boy Advance"], ["Game Boy Advance"], new("gba", Emulators.GameBoyAdvance)),
        new("VB", ["Nintendo - Virtual Boy"], ["Virtual Boy"], new("vb")),
        new("DS", ["Nintendo - Nintendo DS"], ["NDS", "Nintendo DS"], new("nds")),
        new("DSi", ["Nintendo - Nintendo DSi"], ["Nintendo DSi"]),
        new("3DS", ["Nintendo - Nintendo 3DS", "Nintendo - New Nintendo 3DS"], ["N3DS", "Nintendo 3DS", "New Nintendo 3DS"]),

        // Sega
        new("SMS", ["Sega - Master System - Mark III", "Sega - Master System"], ["Master System", "Sega Master System"], new("segaMS")),
        new("GG", ["Sega - Game Gear"], ["Game Gear"], new("segaGG")),
        new("MD", ["Sega - Mega Drive - Genesis"], ["GEN", "SMD", "Genesis", "Mega Drive", "Sega Genesis", "Mega Drive/Genesis"], new("segaMD")),
        new("32X", ["Sega - 32X"], ["Sega 32X"], new("sega32x")),
        new("MCD", ["Sega - Mega CD & Sega CD", "Sega - Mega-CD - Sega CD"], ["SCD", "Sega CD", "Mega-CD", "Mega CD"], new("segaCD", Emulators.MegaCd)),
        new("SAT", ["Sega - Saturn"], ["Saturn", "Sega Saturn"]),
        new("DC", ["Sega - Dreamcast"], ["Dreamcast"]),

        // Sony
        new("PS1", ["Sony - PlayStation"], ["PS", "PSX", "PlayStation"], new("psx", Emulators.PlayStation)),
        new("PS2", ["Sony - PlayStation 2"], ["PlayStation 2"]),
        new("PS3", ["Sony - PlayStation 3"], ["PlayStation 3"], Formats: AsTheyCome),
        new("PS4", ["Sony - PlayStation 4"], ["PlayStation 4"], Formats: AsTheyCome),
        new("PS5", ["Sony - PlayStation 5"], ["PlayStation 5"], Formats: AsTheyCome),
        new("PSP", ["Sony - PlayStation Portable"], ["PlayStation Portable"], new("psp", Threads: true)),
        new("PSV", ["Sony - PlayStation Vita"], ["Vita", "PS Vita", "PlayStation Vita"]),

        // Microsoft: xemu and Xenia read discs as images, not CHDs.
        new("XBOX", ["Microsoft - Xbox"], ["Xbox"], Formats: AsTheyCome),
        new("X360", ["Microsoft - Xbox 360"], ["360", "Xbox 360"], Formats: AsTheyCome),
        new("XONE", ["Microsoft - Xbox One"], ["XB1", "Xbox One"], Formats: AsTheyCome),
        new("XSX", ["Microsoft - Xbox Series X", "Microsoft - Xbox Series X|S", "Microsoft - Xbox Series"],
            ["XSX|S", "Xbox Series X", "Xbox Series X/S", "Xbox Series X|S", "Xbox Series"], Formats: AsTheyCome),

        // NEC
        new("PCE", ["NEC - PC Engine - TurboGrafx-16"], ["TG16", "TG-16", "TurboGrafx-16", "PC Engine"], new("pce")),
        new("PCECD", ["NEC - PC Engine CD & TurboGrafx CD", "NEC - PC Engine CD - TurboGrafx-CD"], ["TurboGrafx-CD", "PC Engine CD", "TG-CD"]),
        new("PC-FX", ["NEC - PC-FX & PC-FXGA", "NEC - PC-FX"], ["PC-FX"]),

        // SNK
        new("NGCD", ["SNK - Neo Geo CD"], ["Neo Geo CD"]),
        new("NGP", ["SNK - NeoGeo Pocket"], ["Neo Geo Pocket"], new("ngp")),
        new("NGPC", ["SNK - NeoGeo Pocket Color"], ["Neo Geo Pocket Color"], new("ngp")),

        // Bandai
        new("WS", ["Bandai - WonderSwan"], ["WonderSwan"], new("ws")),
        new("WSC", ["Bandai - WonderSwan Color"], ["WonderSwan Color"], new("ws")),

        // Atari
        new("2600", ["Atari - 2600"], ["Atari 2600", "VCS"], new("atari2600")),
        new("7800", ["Atari - 7800"], ["Atari 7800"], new("atari7800")),
        new("Lynx", ["Atari - Lynx"], ["Atari Lynx"], new("lynx")),
        new("Jaguar", ["Atari - Jaguar"], ["Atari Jaguar"], new("jaguar")),

        // Others
        new("CV", ["Coleco - ColecoVision"], ["ColecoVision"], new("coleco")),
        new("3DO", ["Panasonic - 3DO Interactive Multiplayer"], ["3DO"]),
    ];

    private static readonly Dictionary<string, KnownSystem> ByDat =
        All.SelectMany(s => s.Dats.Select(d => (Key: Key(d), System: s))).DistinctBy(p => p.Key).ToDictionary(p => p.Key, p => p.System);

    /// <summary>
    /// The system a DAT is for, by its name: without what it adds in brackets, such as <c>(Headered)</c> or
    /// <c>(Decrypted)</c>, and whatever its spacing and punctuation, as each group writes it a little differently.
    /// </summary>
    public static KnownSystem? Find(string datName) => ByDat.GetValueOrDefault(Key(datName.Split(" (")[0]));

    private static string Key(string name) => string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
