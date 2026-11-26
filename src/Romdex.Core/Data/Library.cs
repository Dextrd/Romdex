namespace Romdex.Core.Data;

// The library, filled by the core's scan. Only hashes are stored as fact; what's owned, missing and bad is worked out
// from them and the catalogue each time.

/// <summary>How a library file holds what's in it.</summary>
public enum Container
{
    Raw,
    Zip,
    SevenZip,
    Chd,
    Rvz,
}

/// <summary>A file in the library folder. Its path, size and modified time say whether it needs hashing again.</summary>
public class LibraryFile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Relative to the library folder, with forward slashes.</summary>
    public required string Path { get; set; }

    public long Size { get; set; }
    public DateTime ModifiedAt { get; set; }
    public Container Container { get; set; }

    /// <summary>When its contents were last hashed; null until they are.</summary>
    public DateTime? HashedAt { get; set; }

    /// <summary>Why it couldn't be read, such as a damaged archive or a missing tool.</summary>
    public string? Error { get; set; }

    /// <summary>What kind of failure <see cref="Error"/> is; null while it can be read.</summary>
    public ReadFailure? Failure { get; set; }

    /// <summary>
    /// When someone put off deciding what it is: it leaves Review, but it's still matched each time, so a DAT that adds
    /// it later finds it.
    /// </summary>
    public DateTime? DeferredAt { get; set; }

    /// <summary>
    /// When someone hid it: it stays in the library folder, but Romdex never matches it again and lists it nowhere, as
    /// if it weren't there.
    /// </summary>
    public DateTime? HiddenAt { get; set; }

    /// <summary>The connected app's ID for the game in it, such as RomM's, once the app has found it.</summary>
    public string? AppGameId { get; set; }

    /// <summary>The connector that gave <see cref="AppGameId"/>: another one's IDs mean nothing.</summary>
    public Guid? AppConnectorId { get; set; }

    public List<LibraryContent> Contents { get; set; } = [];
}

/// <summary>
/// What a DAT would describe as one file: a raw file itself, a file inside an archive, or a track or image inside a
/// CHD or RVZ, as the original data.
/// </summary>
public class LibraryContent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid LibraryFileId { get; set; }
    public LibraryFile? File { get; set; }

    /// <summary>Its name inside the container; null for a raw file, which is the content itself.</summary>
    public string? Entry { get; set; }

    public long Size { get; set; }
    public required string Crc32 { get; set; }
    public required string Md5 { get; set; }
    public required string Sha1 { get; set; }

    /// <summary>
    /// For a file with a copier or emulator header, such as an iNES header, its hashes without it: DATs mostly leave
    /// headers out.
    /// </summary>
    public long? HeaderlessSize { get; set; }

    public string? HeaderlessCrc32 { get; set; }
    public string? HeaderlessMd5 { get; set; }
    public string? HeaderlessSha1 { get; set; }

    public List<LibraryMatch> Matches { get; set; } = [];
}

/// <summary>
/// A content whose hashes are a dump file's. One content can be several dump files: discs of different regions often
/// share audio tracks.
/// </summary>
public class LibraryMatch
{
    public Guid ContentId { get; set; }
    public LibraryContent? Content { get; set; }
    public Guid DumpFileId { get; set; }
    public DumpFile? DumpFile { get; set; }

    /// <summary>Matched once its header was left out.</summary>
    public bool Headerless { get; set; }

    /// <summary>The link someone made that this match comes from; null for a match by the DAT's own hashes.</summary>
    public Guid? ManualMatchId { get; set; }

    public ManualMatch? ManualMatch { get; set; }
}
