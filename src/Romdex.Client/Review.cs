namespace Romdex.Client;

// Review: what the DATs don't explain, sorted by what can be done about it. Hashes decide what a file is, so nothing
// here is a guess at a match: it's the exceptions, and what someone decided about them.

/// <summary>Why someone linked a file to the catalogue themselves.</summary>
public enum ManualMatchReason
{
    /// <summary>It matched nothing, and someone searched for what it is.</summary>
    Searched,

    /// <summary>It's named as a dump is but its hashes differ, and someone said to treat it as that dump all the same.</summary>
    Approved,

    /// <summary>It matched a dump until a new DAT changed that dump's hashes, and someone kept it as it was.</summary>
    KeptOutdated,
}

/// <summary>Why a file couldn't be read, which says what can be done about it.</summary>
public enum ReadFailure
{
    /// <summary>It's damaged, or isn't what its extension says: only another copy helps.</summary>
    Damaged,

    /// <summary>
    /// Reading it needs a tool that isn't there, such as chdman, or the igir worker that has it. The file may well be
    /// fine: it's read again once the tool is.
    /// </summary>
    NoTool,

    /// <summary>Romdex isn't allowed to read it, or the disk failed reading it.</summary>
    Inaccessible,
}

/// <summary>Why an arrival waits for someone, which says where it waits under Review.</summary>
public enum HoldReason
{
    /// <summary>It matches nothing in the catalogue: under Matching.</summary>
    Unmatched,

    /// <summary>It holds part of a dump, but not all of it: under Incomplete.</summary>
    Incomplete,

    /// <summary>None of its files could be read: under Corrupted.</summary>
    Unreadable,

    /// <summary>It was fetched for one game and is another: under Couldn't fetch.</summary>
    NotAskedFor,

    /// <summary>Another dump of a game in the library, held before systems stopped holding them: under Couldn't fetch.</summary>
    Alternative,
}

/// <summary>How much waits under each of Review's tabs. Answer to <c>GET /review</c>.</summary>
/// <param name="BadDumps">Files named as a dump is, whose hashes differ.</param>
/// <param name="Matching">Files, and arrivals, that match nothing.</param>
/// <param name="Outdated">Files that matched a dump until a new DAT changed it.</param>
/// <param name="Corrupted">Files, and arrivals, that couldn't be read, other than for want of a tool.</param>
/// <param name="Incomplete">Dumps, and arrivals, missing some of their files.</param>
/// <param name="CouldntFetch">Arrivals that aren't what was asked for, and fetches that failed.</param>
/// <param name="Deferred">Files someone put off deciding about.</param>
/// <param name="Hidden">Files someone hid.</param>
/// <param name="NoTool">Files that can't be read until a tool is there, such as chdman; null when there are none.</param>
public sealed record ReviewSummary(
    int BadDumps,
    int Matching,
    int Outdated,
    int Corrupted,
    int Incomplete,
    int CouldntFetch,
    int Deferred,
    int Hidden,
    ToolNotice? NoTool);

/// <summary>Files that wait for a tool, said once rather than file by file.</summary>
/// <param name="Message">Why, as the reader said it for one of them.</param>
public sealed record ToolNotice(int Files, string Message);

/// <summary>A library file, or a file inside one, under Review.</summary>
/// <param name="Entry">The file inside an archive or CHD; null for the file itself, and for one that couldn't be read.</param>
/// <param name="LooksLike">
/// For a bad dump, the dump it's named as. For a file that couldn't be read, the dump its name suggests, to fetch
/// again. Null when no dump has its name.
/// </param>
/// <param name="Was">For an outdated file, the DAT match it had.</param>
/// <param name="Error">Why it couldn't be read, when it couldn't.</param>
public sealed record ReviewFile(
    Guid FileId,
    string Path,
    string? Entry,
    long Size,
    string? Crc32,
    string? Sha1,
    DateTime ModifiedAt,
    DumpRef? LooksLike,
    PastMatchInfo? Was,
    string? Error,
    ReadFailure? Failure,
    DateTime? DeferredAt,
    DateTime? HiddenAt);

/// <summary>A DAT match a file had before a new DAT changed it.</summary>
/// <param name="DatVersion">The version of the DAT it last matched in.</param>
/// <param name="Now">The dump of that name in the DAT now, when it's still there: with new hashes, to fetch or keep the file as.</param>
public sealed record PastMatchInfo(string System, string Dump, string File, string DatVersion, DateTime LastMatchedAt, DumpRef? Now);

/// <summary>A page of files under one of Review's tabs, with the arrivals that wait there too.</summary>
/// <param name="Total">Files under the tab, not counting arrivals.</param>
public sealed record ReviewFilePage(IReadOnlyList<ReviewFile> Items, int Total, IReadOnlyList<DownloadInfo> Arrivals);

/// <summary>A dump the library has part of.</summary>
/// <param name="Missing">Its files the library doesn't have, by the DAT's names.</param>
/// <param name="Have">Where the library has the rest.</param>
public sealed record ReviewDump(DumpRef Dump, Guid TitleId, string Title, Guid SystemId, string System, IReadOnlyList<string> Missing, IReadOnlyList<LibraryPlace> Have);

/// <summary>A page of incomplete dumps, with the incomplete arrivals.</summary>
public sealed record ReviewDumpPage(IReadOnlyList<ReviewDump> Items, int Total, IReadOnlyList<DownloadInfo> Arrivals);

/// <summary>
/// Body of <c>POST /review/links</c>: what a file, or a file inside one, is, as someone says. Give a dump, or one of
/// its files, for bytes that are that dump; or a title and a name, for a version of a game no DAT lists, such as a
/// translation.
/// </summary>
/// <param name="Entry">The file inside an archive or CHD; null for a file that is itself the content.</param>
/// <param name="DumpId">The dump it is. Its file is the one named as the content is, or its only file.</param>
/// <param name="DumpFileId">The dump file it is, where the dump has several and none is named as the content.</param>
/// <param name="TitleId">The game it's a version of, when it isn't one of the game's dumps.</param>
/// <param name="Name">What to call that version, such as <c>Tide Bell (Spanish translation)</c>.</param>
public sealed record LinkFile(Guid FileId, string? Entry = null, Guid? DumpId = null, Guid? DumpFileId = null, Guid? TitleId = null, string? Name = null);

/// <summary>A link someone made between some bytes and the catalogue.</summary>
/// <param name="Dump">The dump it makes the bytes, while the dump is in the DATs.</param>
/// <param name="TitleId">The game it makes them a version of, for an unlisted version.</param>
public sealed record ManualMatchInfo(Guid Id, string Sha1, string Name, ManualMatchReason Reason, DumpRef? Dump, Guid? TitleId, DateTime CreatedAt);
