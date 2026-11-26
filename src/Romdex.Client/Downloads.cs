using Romdex.Contracts.Plugins.Retrieval;

namespace Romdex.Client;

// Downloads: what's arriving through the retriever plugins, on its way to the library, and how each system files it.

public enum DownloadState
{
    /// <summary>The plugin is writing its files into staging.</summary>
    Fetching,

    /// <summary>Staged, waiting its turn.</summary>
    Queued,

    /// <summary>Being identified, converted or filed.</summary>
    Working,

    /// <summary>Held for someone to look at.</summary>
    NeedsReview,

    /// <summary>In the library.</summary>
    Filed,

    /// <summary>Not wanted, such as a copy of something already in the library. Its files are gone.</summary>
    Discarded,

    /// <summary>Someone said no. Its files are gone.</summary>
    Rejected,

    /// <summary>Something went wrong; it can be retried.</summary>
    Failed,
}

/// <summary>Something that arrived through a retriever.</summary>
/// <param name="Source">The plugin that brought it.</param>
/// <param name="Name">What the plugin called it.</param>
/// <param name="Message">What happened, or why it's held or failed.</param>
/// <param name="Title">The title it was fetched for, when someone asked for one.</param>
/// <param name="Dumps">The dumps it holds whole, once identified, and where each went.</param>
/// <param name="Files">The staged files and what each matched, once identified.</param>
/// <param name="Notes">The plugin's notes on it, such as a dumper's log.</param>
public sealed record DownloadInfo(
    Guid Id,
    string Source,
    string Name,
    DownloadState State,
    string? Message,
    DumpRef? Title,
    IReadOnlyList<DownloadDumpInfo> Dumps,
    IReadOnlyList<StagedFileInfo> Files,
    string? Notes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    HoldReason? HeldFor = null)
{
    /// <summary>Whether it can be accepted: held, with dumps to file.</summary>
    public bool CanAccept => State == DownloadState.NeedsReview && Dumps.Count > 0;

    /// <summary>Whether it can be rejected: anything not yet in the library and not working.</summary>
    public bool CanReject => State is DownloadState.NeedsReview or DownloadState.Failed or DownloadState.Queued or DownloadState.Fetching;

    public bool CanRetry => State is DownloadState.NeedsReview or DownloadState.Failed;
}

/// <summary>A dump a download holds whole.</summary>
/// <param name="Alternative">Another dump of a title already in the library.</param>
/// <param name="Filed">Where it went in the library, once filed.</param>
public sealed record DownloadDumpInfo(DumpRef Dump, Guid SystemId, string System, bool Alternative, IReadOnlyList<string> Filed);

/// <summary>A staged file, or a file inside a staged archive, with its hashes and what it matched.</summary>
/// <param name="Entry">Its name inside the archive, for one that's in an archive.</param>
/// <param name="Matches">The dump files it matched, as "dump: file".</param>
/// <param name="Error">Why it couldn't be read.</param>
public sealed record StagedFileInfo(string Path, string? Entry, long Size, string? Crc32, string? Sha1, IReadOnlyList<string> Matches, string? Error = null);

/// <summary>A page of downloads, newest first.</summary>
/// <param name="Waiting">Items held for review, whatever the page shows.</param>
public sealed record DownloadsPage(IReadOnlyList<DownloadInfo> Items, int Total, int Waiting);

/// <summary>Body of <c>POST /titles/{id}/search</c>.</summary>
/// <param name="DumpId">The one dump wanted; null for any dump of the title.</param>
/// <param name="Replaces">For <c>POST /titles/{id}/fetch</c>: library files what's fetched replaces, such as a bad dump, deleted once it's filed and verifies.</param>
public sealed record SearchDownloads(Guid? DumpId = null, IReadOnlyList<Guid>? Replaces = null);

/// <summary>What a plugin offered for a title.</summary>
/// <param name="PluginId">The plugin instance that offered it, for <c>POST /downloads/fetch</c>.</param>
/// <param name="TurnedAway">
/// Why it was turned away when it was fetched for this title before, such as being another game; null for one not
/// fetched for it, or that was fine.
/// </param>
public sealed record DownloadOffer(Guid PluginId, string Plugin, Candidate Candidate, string? TurnedAway = null);

/// <summary>Answer to <c>POST /titles/{id}/search</c>.</summary>
/// <param name="Offers">Every plugin's candidates, each plugin's best first, plugins in your order.</param>
/// <param name="Problems">Plugins that couldn't search, with why.</param>
public sealed record DownloadSearchResult(IReadOnlyList<DownloadOffer> Offers, IReadOnlyList<string> Problems);

/// <summary>Answer to <c>POST /titles/{id}/fetch</c> and <c>POST /downloads/{id}/try-again</c>.</summary>
/// <param name="Download">What was started; null when nothing could be.</param>
/// <param name="Problems">Why nothing was started, and plugins that couldn't search.</param>
public sealed record TitleFetch(DownloadInfo? Download, IReadOnlyList<string> Problems);

/// <summary>Body of <c>POST /downloads/fetch</c>: a candidate from <c>POST /titles/{id}/search</c>, to stage and file.</summary>
public sealed record StartDownload(Guid TitleId, Guid PluginId, Candidate Candidate, Guid? DumpId = null);

/// <summary>
/// How games are laid out in the library folder: each system's folder named as an app or frontend names it. A system
/// the layout has no name for gets a folder named as its DAT is.
/// </summary>
public enum LibraryLayout
{
    /// <summary>For a system: as the library's layout is, from <c>GET /library/format</c>.</summary>
    Library,

    /// <summary>A folder per platform, named as RomM expects, such as <c>gb</c> or <c>psx</c>.</summary>
    Romm,

    /// <summary>A folder per DAT, named as the DAT is, such as <c>Nintendo - Game Boy</c>: as RetroArch's playlists are.</summary>
    DatName,

    /// <summary>As ES-DE names them, such as <c>gb</c> or <c>gc</c>, which EmuDeck uses too.</summary>
    EsDe,

    /// <summary>As RetroDECK names them.</summary>
    RetroDeck,

    /// <summary>As Batocera names them, such as <c>gamecube</c>.</summary>
    Batocera,

    /// <summary>As MiSTer's cores look for them, such as <c>Gameboy</c>.</summary>
    Mister,

    /// <summary>For a system: a folder of your own naming, <see cref="FilingSettings.Folder"/>.</summary>
    Custom,
}

/// <summary>
/// The library's layout, which every system follows unless it has one of its own. Body and answer of
/// <c>GET/PUT /library/format</c>.
/// </summary>
/// <param name="Layout">How systems' folders are named; not <see cref="LibraryLayout.Library"/> or <see cref="LibraryLayout.Custom"/>.</param>
/// <param name="GamesFolder">The folder, inside the library folder, that systems' folders are in, such as <c>roms</c>; empty for the library folder itself.</param>
/// <param name="BiosFolder">Where BIOS files go, such as <c>bios</c>; empty to keep them with the system's games.</param>
/// <param name="BiosBySystem">Whether BIOS files go in a folder per system inside <paramref name="BiosFolder"/>, or all together in it.</param>
public sealed record LibraryFormat(LibraryLayout Layout = LibraryLayout.Romm, string GamesFolder = "", string BiosFolder = "", bool BiosBySystem = true)
{
    public static LibraryFormat Default { get; } = new();
}

/// <summary>A system whose games are in a folder the library's layout wouldn't name.</summary>
/// <param name="Folder">The folder its games are in, inside the games folder.</param>
public sealed record SystemFolder(Guid SystemId, string System, string Folder);

/// <summary>Answer to <c>GET /library/format/detect</c>: the layout the library's games are in now, as near as it can be told.</summary>
/// <param name="Format">The layout and folders that fit the most games, or null when there are none to go by.</param>
/// <param name="Systems">Systems the library has, by how many fit it.</param>
/// <param name="Fitting">Of those, how many have their games where the layout would put them.</param>
/// <param name="Others">Systems whose folder the layout names otherwise, which could keep theirs as a folder of their own.</param>
/// <param name="Problem">Why it couldn't be told, such as no igir worker to name folders.</param>
public sealed record LayoutDetection(LibraryFormat? Format, int Systems, int Fitting, IReadOnlyList<SystemFolder> Others, string? Problem = null);

/// <summary>How a system's games are stored. Anything but <see cref="Keep"/> is checked against the DAT again after converting.</summary>
public enum StorageFormat
{
    /// <summary>As they arrive.</summary>
    Keep,

    /// <summary>Loose files, out of any archive.</summary>
    Loose,

    /// <summary>A zip per game: for cartridges.</summary>
    Zip,

    /// <summary>A CHD per disc, with chdman: for CD and DVD systems.</summary>
    Chd,

    /// <summary>An RVZ per disc, with DolphinTool: for GameCube and Wii.</summary>
    Rvz,
}

/// <summary>What to do with another dump of a title that's already in the library.</summary>
public enum AlternativePolicy
{
    /// <summary>File it beside the one you have.</summary>
    File,

    /// <summary>Throw it away.</summary>
    Discard,
}

/// <summary>How a system's arrivals are filed. Body and answer of <c>GET/PUT /systems/{id}/filing</c>.</summary>
/// <param name="Layout">The library's layout, unless the system has one of its own.</param>
/// <param name="Folder">With <see cref="LibraryLayout.Custom"/>: the system's folder, inside the games folder.</param>
public sealed record FilingSettings(LibraryLayout Layout = LibraryLayout.Library, StorageFormat Storage = StorageFormat.Keep,
    AlternativePolicy Alternatives = AlternativePolicy.File, string? Folder = null)
{
    public static FilingSettings Default { get; } = new();
}

/// <summary>A game in the library that isn't where the layout would put it.</summary>
/// <param name="From">Where it is, relative to the library.</param>
/// <param name="To">Where it goes, file for file.</param>
/// <param name="Blocked">Why it can't move, such as another file already being there.</param>
public sealed record AdoptionMove(DumpRef Dump, IReadOnlyList<string> From, IReadOnlyList<string> To, string? Blocked = null);

/// <summary>
/// Answer to <c>GET /systems/{id}/filing/adoption</c> and <c>GET /library/format/preview</c>: how games in the library
/// compare with where the layout would put them. It only reports; formatting moves them.
/// </summary>
/// <param name="Checked">Games in the library, whole.</param>
/// <param name="InPlace">Those already where they'd be filed.</param>
/// <param name="Moves">The rest, and where each would go.</param>
/// <param name="Problem">Why it couldn't be worked out, such as no igir worker to name folders.</param>
public sealed record AdoptionReport(int Checked, int InPlace, IReadOnlyList<AdoptionMove> Moves, string? Problem = null);

/// <summary>Body of <c>POST /library/format/apply</c>: moves the library's games to where the layout puts them, as a task.</summary>
/// <param name="SystemId">Just this system's; null for every system.</param>
public sealed record FormatLibrary(Guid? SystemId = null);
