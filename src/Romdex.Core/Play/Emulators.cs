namespace Romdex.Core.Play;

/// <summary>
/// The BIOS files an emulator core accepts. Each is known by its MD5, as the core's documentation lists it, so a
/// library BIOS is used only when it really is one of them, whatever its DAT calls it.
/// </summary>
/// <param name="Required">The core can't start a game without one; otherwise it falls back on its own stand-in.</param>
/// <param name="Files">The file name the core looks for, by the MD5 of what it should hold.</param>
public sealed record EmulatorBios(bool Required, IReadOnlyDictionary<string, string> Files)
{
    /// <summary>The names the core looks for.</summary>
    public IReadOnlyList<string> Names => Files.Values.Distinct().ToList();
}

/// <summary>How EmulatorJS plays one system.</summary>
/// <param name="Core">EmulatorJS's name for the system, its <c>EJS_core</c>.</param>
/// <param name="Bios">The BIOS it uses, if any.</param>
/// <param name="Threads">The core needs threads, so a page that isn't cross-origin isolated can't run it.</param>
public sealed record Emulator(string Core, EmulatorBios? Bios = null, bool Threads = false);

/// <summary>
/// The systems EmulatorJS plays, as <see cref="Catalogue.KnownSystems"/> says. Systems it can't run, such as the
/// GameCube, only open in a connected app.
/// </summary>
public static class Emulators
{
    // From the libretro cores' documentation of their system files.
    internal static readonly EmulatorBios PlayStation = new(false, new Dictionary<string, string>
    {
        ["490f666e1afb15b7362b406ed1cea246"] = "scph5501.bin",
        ["32736f17079d0b2b7024407c39bd3050"] = "scph5502.bin",
        ["8dd7d5296a650fac7319bce665a6a53c"] = "scph5500.bin",
        ["924e392ed05558ffdb115408c263dccf"] = "scph1001.bin",
        ["1e68c231d0896b7eadcad1d7d8e76129"] = "scph7001.bin",
        ["6e3735ff4c7dc899ee98981385f6f3d0"] = "scph101.bin",
    });

    internal static readonly EmulatorBios MegaCd = new(true, new Dictionary<string, string>
    {
        ["2efd74e3232ff260e371b99f84024f7f"] = "bios_CD_U.bin",
        ["e66fa1dc5820d254611fdcdba0662372"] = "bios_CD_E.bin",
        ["278a9397d192149e84e820ac621a8edd"] = "bios_CD_J.bin",
    });

    internal static readonly EmulatorBios GameBoyAdvance = new(false, new Dictionary<string, string> { ["a860e8c0b6d573d191e4ec7db1b1e4f6"] = "gba_bios.bin" });

    internal static readonly EmulatorBios DiskSystem = new(true, new Dictionary<string, string> { ["ca30b50f880eb660a320674ed365ef7a"] = "disksys.rom" });

    // Made-up systems the tests play, by their DAT names.
    private static readonly List<(string Name, Emulator Emulator)> Systems = [];

    /// <summary>Adds a system ahead of the rest, for tests, whose made-up BIOS files no core knows.</summary>
    internal static void Register(string name, Emulator emulator) => Systems.Insert(0, (name, emulator));

    /// <summary>How a system plays, or null when EmulatorJS can't play it.</summary>
    public static Emulator? For(string systemName) =>
        Systems.Where(s => systemName.Equals(s.Name, StringComparison.OrdinalIgnoreCase)
                           || systemName.StartsWith(s.Name + " (", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Emulator).FirstOrDefault() ?? Catalogue.KnownSystems.Find(systemName)?.Play;
}
