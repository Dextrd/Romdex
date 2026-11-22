namespace Romdex.Contracts.Plugins.Players;

/// <summary>
/// Player contract (version 1.0): plays games the library has, in the browser. The core does everything about the
/// library: it finds the game's files, sends a disc in several files as one zip, picks the BIOS by its MD5, signs the
/// links, and serves the player's page on its own origin. The player says which systems it plays and what they need,
/// and writes the page that plays one game. Its own files, such as an emulator's, come from wherever
/// <see cref="FilesUrl"/> says, through the core, so the page loads everything from one origin.
/// </summary>
public interface IPlayer : IPlugin
{
    /// <summary>How it plays a system's games, or <c>null</c> when it can't. Called often: no I/O.</summary>
    PlayerSupport? Supports(PlayerSystem system);

    /// <summary>The page that plays one game, as HTML. Called only for a system <see cref="Supports"/> said it plays.</summary>
    string WritePage(PlayerGame game);

    /// <summary>
    /// Where the player's own files are, such as <c>http://emulatorjs:8080/</c>, which the core passes on to the page at
    /// <see cref="PlayerGame.FilesPath"/>. Null when the page needs none.
    /// </summary>
    Uri? FilesUrl { get; }
}

/// <summary>A system, as the core knows it.</summary>
/// <param name="Name">Its DAT's name, such as <c>Nintendo - Game Boy</c>.</param>
/// <param name="Short">
/// Romdex's short name for a system it knows, such as <c>NES</c>, <c>GBA</c>, <c>MD</c> or <c>PS1</c>, whichever DAT
/// it came from; null for one it doesn't. Players should go by this where there is one.
/// </param>
public sealed record PlayerSystem(string Name, string? Short);

/// <summary>What a player needs to play a system's games.</summary>
/// <param name="Bios">
/// The BIOS files it accepts, by the MD5 of what they hold (lower-case hex), each with the file name it looks for. The
/// core picks the library's, of the game's region where it can. Null when it uses none.
/// </param>
/// <param name="BiosRequired">It can't start a game without one of <paramref name="Bios"/>.</param>
/// <param name="Isolated">
/// Its page must be cross-origin isolated, as an emulator that uses threads needs: the core sends the headers for it.
/// </param>
public sealed record PlayerSupport(IReadOnlyDictionary<string, string>? Bios = null, bool BiosRequired = false, bool Isolated = false);

/// <summary>One game to play. Every link is relative to the page, and already signed.</summary>
/// <param name="Name">The dump's name, such as <c>Pixel Pond (USA)</c>.</param>
/// <param name="System">Its system.</param>
/// <param name="GameUrl">The game: one file as the library has it, such as a zip or a CHD, or a disc's files as one zip.</param>
/// <param name="GameFileName">The game's file name, whose extension says what it is.</param>
/// <param name="BiosUrl">The BIOS, when it needs one and the library has it.</param>
/// <param name="BiosFileName">The BIOS's file name, as the player looks for it.</param>
/// <param name="FilesPath">Where the player's own files are, ending in a slash: <see cref="IPlayer.FilesUrl"/>, through the core.</param>
/// <param name="BackUrl">The game's page in Romdex, to go back to.</param>
public sealed record PlayerGame(string Name, PlayerSystem System, string GameUrl, string GameFileName, string? BiosUrl, string? BiosFileName,
    string FilesPath, string BackUrl);
