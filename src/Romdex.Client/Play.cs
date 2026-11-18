namespace Romdex.Client;

/// <summary>How to play a dump the library has, from <c>GET /play/{dumpId}</c>.</summary>
/// <param name="PlayerUrl">The player page, signed so it needs no sign-in, for a few hours. Null when it can't play here.</param>
/// <param name="Unplayable">Why it can't play here, when it can't: a system the emulator doesn't run, a missing BIOS, no emulator.</param>
/// <param name="Bios">The BIOS it plays with, as the emulator names it, when it uses one.</param>
/// <param name="App">The connected app it can open in instead, such as RomM, when there is one.</param>
public sealed record PlayInfo(Guid DumpId, Guid TitleId, string Name, string? PlayerUrl, string? Unplayable, string? Bios, string? App);

/// <summary>Where a game is in the connected app, from <c>POST /play/{dumpId}/app</c>.</summary>
/// <param name="Url">Its page there; null when the app hasn't found it yet.</param>
/// <param name="Waiting">Why there's no link yet, such as the app still scanning.</param>
public sealed record AppLink(string App, string? Url, string? Waiting);

/// <summary>The BIOS a system's emulator looks for.</summary>
/// <param name="Required">Games won't start without one; otherwise the emulator uses a stand-in, which plays most games.</param>
/// <param name="Files">The names it looks for, one per region.</param>
/// <param name="Ready">The library has one it can use.</param>
public sealed record BiosNeed(bool Required, IReadOnlyList<string> Files, bool Ready);
