using Romdex.Client;

namespace Romdex.Core.Data;

// What someone decided about what the DATs don't explain, and what Romdex remembers so it can tell why. The DATs stay
// the only source of fact: these sit beside them, keyed on hashes and names that survive a rescan or a new DAT version.

/// <summary>
/// Someone's word that some bytes are a dump file, or a version of a game, that the DATs don't list. It's keyed on the
/// content's SHA-1, as it is or without its header, so it survives the file being moved, renamed or hashed again, and
/// ends when the bytes change. A DAT match always wins: the link only counts while the DATs match the bytes to nothing.
/// </summary>
public class ManualMatch
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Lower-case hex, as the library's hashes are.</summary>
    public required string Sha1 { get; set; }

    /// <summary>
    /// The dump file the bytes stand for: the library matches them to it as if the DAT did, so its dump can be owned.
    /// Null for an unlisted version of a game, such as a translation, or once the dump has left the DATs.
    /// </summary>
    public Guid? DumpFileId { get; set; }

    public DumpFile? DumpFile { get; set; }

    /// <summary>
    /// The game the bytes are a version of, when they aren't one of its dumps: shown on its page as its own, but never
    /// making the game owned. Null when <see cref="DumpFileId"/> says what they are.
    /// </summary>
    public Guid? TitleId { get; set; }

    public Title? Title { get; set; }

    /// <summary>
    /// What to call it: for an unlisted version, the name someone gave it; for a dump file, its dump's name when it was
    /// linked, which says what it was if the dump leaves the DATs.
    /// </summary>
    public required string Name { get; set; }

    public ManualMatchReason Reason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A DAT match the library has had, kept after the DATs change, so a file that stops matching can be told apart from
/// one that never did. Keyed on names rather than IDs, which go when a dump leaves the DATs. Kept while the library has
/// the bytes; forgotten when it doesn't.
/// </summary>
public class PastMatch
{
    /// <summary>The content's SHA-1 that matched: as it is, or without its header where that's how it matched.</summary>
    public required string Sha1 { get; set; }

    public required string SystemName { get; set; }
    public required string DumpName { get; set; }
    public required string FileName { get; set; }

    /// <summary>The system's DAT version when the bytes last matched.</summary>
    public required string DatVersion { get; set; }

    public DateTime FirstMatchedAt { get; set; }
    public DateTime LastMatchedAt { get; set; }
}

/// <summary>
/// A dump someone keeps without some of its files, such as a disc without its redundant audio tracks. It counts as
/// whole while only these are missing; if others go too, it's incomplete again.
/// </summary>
public class KeptIncomplete
{
    public Guid DumpId { get; set; }
    public Dump? Dump { get; set; }

    /// <summary>The dump files' names, '|'-separated.</summary>
    public required string Missing { get; set; }

    public DateTime KeptAt { get; set; } = DateTime.UtcNow;

    public IReadOnlySet<string> MissingNames => Missing.Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
}
