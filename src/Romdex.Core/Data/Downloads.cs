namespace Romdex.Core.Data;

// What's arriving: each thing a retriever staged, or is staging, on its way to the library.

/// <summary>Where a download is on its way to the library.</summary>
public enum DownloadState
{
    /// <summary>The plugin is writing its files into staging.</summary>
    Fetching,

    /// <summary>In staging, waiting its turn to be identified and filed.</summary>
    Queued,

    /// <summary>Being identified, converted or filed.</summary>
    Working,

    /// <summary>Held for someone to look at: it matched nothing, is incomplete, or is an alternative the system holds.</summary>
    NeedsReview,

    /// <summary>In the library.</summary>
    Filed,

    /// <summary>Not wanted: a copy of something already in the library, or an alternative the system discards. Its files are gone.</summary>
    Discarded,

    /// <summary>Someone said no. Its files are gone.</summary>
    Rejected,

    /// <summary>Something went wrong; retrying starts it again.</summary>
    Failed,
}

/// <summary>Something that arrived through a retriever, from staging to the library.</summary>
public class Download
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The plugin that brought it. Kept when the plugin is removed, so the item can still be reviewed.</summary>
    public Guid? PluginInstanceId { get; set; }

    /// <summary>The plugin's name at the time, for people.</summary>
    public required string Source { get; set; }

    /// <summary>The plugin's own ID for what it staged, so a listed candidate is only staged once.</summary>
    public required string CandidateId { get; set; }

    /// <summary>What the plugin called it.</summary>
    public required string Name { get; set; }

    /// <summary>The candidate as the plugin offered it, as JSON.</summary>
    public required string CandidateJson { get; set; }

    /// <summary>The title it was fetched for, when someone asked for it; null for what arrived by itself.</summary>
    public Guid? TitleId { get; set; }

    /// <summary>The one dump asked for, when someone picked one.</summary>
    public Guid? WantedDumpId { get; set; }

    public DownloadState State { get; set; }

    /// <summary>Why it's held, while it's <see cref="DownloadState.NeedsReview"/>; meaningless in any other state.</summary>
    public HoldReason? HeldFor { get; set; }

    /// <summary>What happened, or why it's held or failed, for people.</summary>
    public string? Message { get; set; }

    /// <summary>Its folder in staging, relative to the staging volume. The core makes it, the plugin fills it.</summary>
    public required string StagingDirectory { get; set; }

    /// <summary>The staged files with their hashes and what they matched, as JSON, once identified.</summary>
    public string? FilesJson { get; set; }

    /// <summary>The dumps it holds whole, once identified.</summary>
    public List<DownloadDump> Dumps { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The state it was in when events were last looked for, so each change is announced once.</summary>
    public DownloadState? AnnouncedState { get; set; }
}

/// <summary>A dump a download holds whole, and what became of it.</summary>
public class DownloadDump
{
    public Guid DownloadId { get; set; }
    public Download? Item { get; set; }
    public Guid DumpId { get; set; }
    public Dump? Dump { get; set; }

    /// <summary>Another dump of a title already in the library.</summary>
    public bool Alternative { get; set; }

    /// <summary>Where it went in the library, relative to the library folder, once filed; '|'-separated when it's several files.</summary>
    public string? FiledPaths { get; set; }
}
