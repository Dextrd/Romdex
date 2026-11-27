using System.Text.Json;
using System.Text.Json.Serialization;

namespace Romdex.Client;

// Models of the public API, /api/v1. Enums are camelCase strings on the wire.

/// <summary>Serializer settings matching the API.</summary>
public static class RomdexJson
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return options;
    }
}

public enum TaskState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <param name="Title">What it's working on, such as "Library scan"; filled in by <c>GET /tasks</c>.</param>
/// <param name="Paused">Held back: it won't start until resumed.</param>
/// <param name="QueueOrder">Its place among queued tasks: lower runs first.</param>
public sealed record TaskInfo(Guid Id, string Kind, TaskState State, string? Progress, string? Error, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt,
    string? Title = null, bool Paused = false, double QueueOrder = 0)
{
    public bool Active => State is TaskState.Queued or TaskState.Running;

    /// <summary>When it last changed: finished, started or queued.</summary>
    public DateTime UpdatedAt => FinishedAt ?? StartedAt ?? CreatedAt;
}

/// <summary>What to do to many tasks, or requests, at once.</summary>
public enum BulkAction { Pause, Resume, Cancel }

/// <summary>Body of <c>POST /tasks/bulk</c>: one action for many at once.</summary>
/// <param name="Ids">Tasks to act on. Any already past it (cancelling a finished one, say) are left as they are.</param>
public sealed record BulkChange(BulkAction Action, IReadOnlyList<Guid> Ids);

/// <summary>How many of those given were changed.</summary>
public sealed record BulkChanged(int Changed);

/// <summary>Body of <c>POST /tasks/{id}/move</c>.</summary>
/// <param name="Before">Put it just before this one in the queue; null for the end.</param>
public sealed record MoveInQueue(Guid? Before);

public enum LogEntryLevel
{
    Information,
    Warning,
    Error,
    /// <summary>Information about something that finished well, such as a scan.</summary>
    Success,
}

/// <summary>One event in the activity log.</summary>
/// <param name="Seq">Increases by one per entry; pass the latest you have as <c>after</c> to get only newer ones.</param>
/// <param name="Source">The part of Romdex it came from, such as TaskWorker or PluginCatalog.</param>
/// <param name="Error">The underlying error's message, if there was one.</param>
public sealed record LogEntry(long Seq, DateTime Time, LogEntryLevel Level, string Source, string Message, string? Error);

/// <summary>How long the activity log is kept.</summary>
public sealed record LogSettings
{
    /// <summary>Days of entries the Log page shows, from 1 to 90.</summary>
    public int MemoryDays { get; init; } = 7;

    /// <summary>Days the backup files under <c>data/logs</c> are kept, from 1 to 3650; at least <see cref="MemoryDays"/>.</summary>
    public int FileDays { get; init; } = 30;

    /// <summary>Days a finished task's list (what a scan, format or catalogue update did, row by row) is kept, from 1 to 3650.</summary>
    public int TaskDays { get; init; } = 30;
}

/// <param name="Latest">The newest entry's sequence number, even when no entries are returned.</param>
public sealed record LogPage(IReadOnlyList<LogEntry> Entries, long Latest);

/// <summary>Where a row of a task is.</summary>
public enum TaskRowState
{
    /// <summary>Waiting its turn.</summary>
    Waiting,

    /// <summary>Being worked on.</summary>
    Working,

    /// <summary>Done, and it changed something.</summary>
    Done,

    /// <summary>Done with nothing to change, or left alone.</summary>
    Skipped,

    /// <summary>It couldn't be done; the detail says why.</summary>
    Failed,

    /// <summary>Done, and it waits for a decision under Review, where its link goes: a scanned file that matches nothing, as Melodex's scan list has.</summary>
    Review,
}

/// <summary>Something a task has done or is doing, such as a file it formatted, from <c>GET /tasks/{id}/rows</c>.</summary>
/// <param name="Seq">When it last changed: pass the highest seen as <c>since</c> to get only later changes.</param>
/// <param name="Order">Its place in the list, which it keeps as it changes.</param>
/// <param name="Key">What it's about, such as a file's path, unique within the task.</param>
/// <param name="Subtitle">A line under the title, such as where a file went.</param>
/// <param name="Detail">What happened, such as the game a file matched, or why it couldn't be done.</param>
/// <param name="Href">A page in the web app about it, relative to the app's base.</param>
public sealed record TaskRow(
    long Seq,
    long Order,
    string Key,
    TaskRowState State,
    string Title,
    string? Subtitle,
    string? Detail,
    string? Href,
    string? CoverUrl,
    DateTime At);

/// <summary>A task's rows that changed since a sequence number, from <c>GET /tasks/{id}/rows</c>.</summary>
/// <param name="Latest">The newest change's sequence number, to pass as <c>since</c> next time.</param>
/// <param name="Rows">In list order.</param>
public sealed record TaskRowsPage(Guid Task, long Latest, IReadOnlyList<TaskRow> Rows);
