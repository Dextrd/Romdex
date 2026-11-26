namespace Romdex.Client;

/// <summary>Where a title stands in the library. Worked out from hashes each time; never stored.</summary>
public enum TitleStatus
{
    /// <summary>A whole dump of it is in the library: every file, every disc.</summary>
    Owned,

    /// <summary>Some discs of a multi-disc release are, but not all.</summary>
    Partial,

    /// <summary>Not owned, and the library has files of it that don't verify: a disc with tracks missing or wrong, or a file with its name but other hashes.</summary>
    Bad,

    Missing,
}

/// <summary>Where one dump stands in the library.</summary>
public enum DumpStatus
{
    /// <summary>Every file verifies.</summary>
    Good,

    /// <summary>Some of its files verify, others are missing.</summary>
    Incomplete,

    /// <summary>A library file has one of its names but other hashes.</summary>
    Bad,

    Missing,
}

/// <summary>What a dump is beyond a retail game, as its name or DAT category says.</summary>
public enum DumpKind
{
    Demo,
    Beta,
    Proto,
    Pirate,
    Unlicensed,
    Aftermarket,
    Homebrew,
    Program,
    Debug,
    Bad,
    Alternate,
    Update,
    Dlc,
}

/// <summary>
/// 1G1R ("one game, one ROM") choices for a system: which of a title's dumps to prefer, and which titles to leave out
/// of what's missing.
/// </summary>
/// <param name="Regions">Regions in order of preference.</param>
/// <param name="Languages">Languages in order of preference, as two-letter codes such as <c>En</c>.</param>
/// <param name="OnlyListedRegions">Leave out titles with no dump in the listed regions, rather than only preferring them.</param>
/// <param name="Hidden">Kinds of dump left out: a title of only these isn't listed, unless it's owned.</param>
public sealed record SystemPreferences(
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> Languages,
    bool OnlyListedRegions,
    IReadOnlyList<DumpKind> Hidden)
{
    public static SystemPreferences Default { get; } = new(
        ["World", "Europe", "UK", "USA", "Australia", "Japan"],
        ["En"],
        false,
        [DumpKind.Demo, DumpKind.Beta, DumpKind.Proto, DumpKind.Pirate, DumpKind.Program, DumpKind.Debug, DumpKind.Bad]);
}

/// <summary>Body and answer of <c>GET/PUT /systems/{id}/preferences</c>.</summary>
/// <param name="Own">The system has preferences of its own; otherwise it follows everyone's default.</param>
public sealed record SystemPreferencesInfo(SystemPreferences Preferences, bool Own);

/// <summary>A system with how much of it is owned, after 1G1R.</summary>
/// <param name="Titles">Titles listed after the preferences: owned, partial, bad and missing together, without ignored ones.</param>
/// <param name="Ignored">Titles someone said never to list as missing.</param>
/// <param name="Bios">BIOS and firmware files the system's DATs list, and how many of them are owned.</param>
/// <param name="ShortName">What people call it for short, such as <c>PS2</c> for <c>Sony - PlayStation 2</c>.</param>
public sealed record SystemSummary(
    Guid Id,
    string Name,
    string Source,
    string DatVersion,
    DateTime? DatDate,
    int Titles,
    int Owned,
    int Partial,
    int Bad,
    int Missing,
    int Ignored,
    int Bios,
    int BiosOwned,
    bool OwnPreferences,
    string? ShortName = null)
{
    /// <summary>The share owned, from 0 to 1.</summary>
    public double Completeness => Titles == 0 ? 0 : Owned / (double)Titles;
}

/// <summary>One title in a system's list.</summary>
/// <param name="Preferred">The dump 1G1R picks, or the one someone chose instead.</param>
/// <param name="Have">The dump owned, when one is; the preferred one when that's owned.</param>
/// <param name="Cover">Where its cover is, relative to the app's base: 404 there when no metadata plugin has one, 503 while it's being looked up.</param>
public sealed record TitleSummary(Guid Id, Guid SystemId, string Name, TitleStatus Status, DumpRef Preferred, DumpRef? Have, bool Ignored, int Dumps,
    string? Cover = null);

/// <summary>A dump, named.</summary>
/// <param name="TitleId">Its title, where the answer gives it, such as under Review, to get the dump by.</param>
public sealed record DumpRef(Guid Id, string Name, IReadOnlyList<string> Regions, Guid? TitleId = null);

/// <summary>A page of a system's titles.</summary>
public sealed record TitlePage(IReadOnlyList<TitleSummary> Items, int Total);

/// <summary>
/// One game, on every system it's on: Search lists games, not each system's titles. Its titles are one game when their
/// names are the same but for punctuation, or a metadata plugin found the same game for them, such as IGDB's.
/// </summary>
/// <param name="Id">The title its name and cover are from: the one the library has, if any.</param>
/// <param name="Status">The best of its titles': owned when the library has it on any system.</param>
/// <param name="Titles">Its title on each system, best first, the first being <paramref name="Id"/>.</param>
public sealed record GameSummary(Guid Id, string Name, TitleStatus Status, string? Cover, IReadOnlyList<TitleSummary> Titles);

/// <summary>A page of games.</summary>
public sealed record GamePage(IReadOnlyList<GameSummary> Items, int Total);

/// <summary>The same game on another system, or the title itself, in a title's list of versions.</summary>
public sealed record TitleVersion(Guid Id, Guid SystemId, string System, string Name, TitleStatus Status);

/// <summary>A title with every dump the DAT lists for it.</summary>
/// <param name="ChosenDumpId">The dump someone chose to prefer over what 1G1R picks; null to let 1G1R pick.</param>
/// <param name="Cover">Where its cover is, relative to the app's base: 404 there when no metadata plugin has one, 503 while it's being looked up.</param>
/// <param name="KnownAs">Its name as people know it, from a metadata plugin, where that differs from the DAT's.</param>
/// <param name="Versions">The game on every system it's on, this one included, by short system name; just this one when it's on no other.</param>
public sealed record TitleDetail(
    Guid Id,
    Guid SystemId,
    string SystemName,
    string Name,
    TitleStatus Status,
    bool Ignored,
    Guid? ChosenDumpId,
    IReadOnlyList<DumpInfo> Dumps,
    string? Cover = null,
    string? KnownAs = null,
    IReadOnlyList<TitleVersion>? Versions = null);

/// <summary>
/// What a game is, from the metadata plugins, such as Hasheous and Wikipedia: what it's about, when it came out, who
/// made it, its reviews, and pages about it elsewhere. Every part is optional: what no plugin knows is empty.
/// </summary>
/// <param name="Summary">What it's about, in a paragraph or a few, as plain text; paragraphs are separated by a blank line.</param>
/// <param name="SummarySource">Who wrote it, to credit, such as <c>Wikipedia</c>.</param>
/// <param name="SummaryUrl">The page it's from, to read more.</param>
/// <param name="Released">When it came out, earliest first: on this title's system where the plugins say, else everywhere they know of.</param>
/// <param name="Scores">What reviewers gave it, the aggregates such as Metacritic first.</param>
/// <param name="Links">Pages about it elsewhere, such as IGDB's and Wikipedia's.</param>
/// <param name="Ratings">What age rating boards gave it, one each.</param>
/// <param name="Images">Pictures of it on this title's system, such as its title screen.</param>
/// <param name="InSeries">The other games in each of its series that the catalogue has, on any system, the most widely released first, as Search puts them.</param>
public sealed record GameInfo(
    string? Summary,
    string? SummarySource,
    string? SummaryUrl,
    IReadOnlyList<ReleaseDate> Released,
    IReadOnlyList<string> Developers,
    IReadOnlyList<string> Publishers,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Modes,
    IReadOnlyList<string> Series,
    IReadOnlyList<Credit> Credits,
    IReadOnlyList<ReviewScore> Scores,
    IReadOnlyList<GameLink> Links,
    IReadOnlyList<AgeRating> Ratings,
    IReadOnlyList<Screenshot> Images,
    IReadOnlyList<SeriesGames> InSeries)
{
    public static GameInfo Empty { get; } = new(null, null, null, [], [], [], [], [], [], [], [], [], [], [], []);

    /// <summary>Still being looked up: this is what's been found so far, and asking again gives more.</summary>
    public bool Loading { get; init; }
}

/// <summary>The games in a series that the catalogue has, such as the Sonic the Hedgehog games.</summary>
/// <param name="Name">The series' name.</param>
/// <param name="Games">Its games, earliest first, each on every system it's on.</param>
public sealed record SeriesGames(string Name, IReadOnlyList<GameSummary> Games);

/// <summary>What an age rating board gave a game.</summary>
/// <param name="Board">Which: <c>ESRB</c>, <c>PEGI</c>, <c>CERO</c>, <c>USK</c> or <c>ACB</c>.</param>
/// <param name="Rating">Its mark, lowercased, such as <c>e10</c> for ESRB's Everyone 10+ or <c>12</c> for PEGI 12.</param>
/// <param name="Name">As people read it, such as <c>Everyone 10+</c>.</param>
public sealed record AgeRating(string Board, string Rating, string Name);

/// <summary>A picture of a game.</summary>
/// <param name="Kind"><c>title</c> for its title screen, <c>screenshot</c> for play.</param>
/// <param name="Url">Where the core serves it, relative to the server.</param>
/// <param name="Source">Where it's from, to credit it.</param>
public sealed record Screenshot(string Kind, string Url, string Source);

/// <summary>When a game came out somewhere.</summary>
/// <param name="Date">As <c>yyyy-MM-dd</c>, <c>yyyy-MM</c> or <c>yyyy</c>, as precisely as it's known.</param>
/// <param name="Platform">On what, when it came out on several at different times.</param>
public sealed record ReleaseDate(string? Region, string Date, string? Platform);

/// <summary>Who made a game in one role, such as its director or composer.</summary>
public sealed record Credit(string Role, IReadOnlyList<string> Names);

/// <summary>What a reviewer gave a game, as they wrote it, such as <c>88/100</c>.</summary>
public sealed record ReviewScore(string Reviewer, string Score);

/// <summary>A page about a game on another site.</summary>
public sealed record GameLink(string Site, string Url);

/// <summary>One dump of a title, as the DAT lists it, and whether the library has it.</summary>
/// <param name="Preferred">The one 1G1R picks, or someone chose.</param>
/// <param name="Hidden">Left out by the system's preferences, such as a demo.</param>
public sealed record DumpInfo(
    Guid Id,
    string Name,
    string? Serial,
    IReadOnlyList<string> Regions,
    IReadOnlyList<string> Languages,
    IReadOnlyList<DumpKind> Kinds,
    string? Disc,
    DumpStatus Status,
    bool Preferred,
    bool Hidden,
    IReadOnlyList<DumpFileInfo> Files);

/// <summary>One file of a dump, with its hashes, and where the library has it.</summary>
/// <param name="Library">The library files that verify as it.</param>
public sealed record DumpFileInfo(string Name, long Size, string? Crc32, string? Md5, string? Sha1, IReadOnlyList<LibraryPlace> Library);

/// <summary>Where in the library something is: a file, and the entry inside it for an archive or a CHD.</summary>
public sealed record LibraryPlace(Guid FileId, string Path, string? Entry);

/// <summary>A system's BIOS and firmware.</summary>
/// <param name="Needed">What the system's emulator looks for, when it plays in the browser and uses a BIOS.</param>
public sealed record BiosList(IReadOnlyList<DumpInfo> Items, BiosNeed? Needed = null);

/// <summary>A library file, or a file inside one, that matches nothing in the catalogue, or couldn't be read.</summary>
/// <param name="LooksLike">A dump with a file of this name, whose hashes differ: likely a bad or modified dump.</param>
/// <param name="Error">Why it couldn't be read, when it couldn't.</param>
public sealed record UnknownFile(
    Guid FileId,
    string Path,
    string? Entry,
    long Size,
    string? Crc32,
    string? Md5,
    string? Sha1,
    DumpRef? LooksLike,
    string? Error);

/// <summary>A page of unknown files.</summary>
public sealed record UnknownPage(IReadOnlyList<UnknownFile> Items, int Total);

/// <summary>Body of <c>PUT /titles/{id}/preferred</c>.</summary>
/// <param name="DumpId">The dump to prefer; null to let 1G1R pick again.</param>
public sealed record ChoosePreferred(Guid? DumpId);

/// <summary>What the library holds, in total.</summary>
/// <param name="Unknown">Files, and files inside archives, that match nothing.</param>
/// <param name="Unreadable">Files that couldn't be read, such as damaged archives.</param>
/// <param name="ToReview">Everything waiting under Review's tabs: files, dumps and arrivals, not counting those set aside.</param>
public sealed record LibraryStatus(int Files, long Bytes, int Unknown, int Unreadable, DateTime? ScannedAt, DateTime? CatalogueUpdatedAt, int ToReview = 0);
