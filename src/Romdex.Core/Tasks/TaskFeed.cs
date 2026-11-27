using Romdex.Client;
using Romdex.Core.Data;

namespace Romdex.Core.Tasks;

/// <summary>
/// What tasks are doing, row by row, so the Activity page can show a task as it goes: a file being formatted, an artist's
/// catalogue being updated, a song's lyrics being looked for. A running task's rows are kept here; once it's done they
/// go to the <see cref="TaskArchive"/>, so every task keeps its list. Each row keeps the place it first took, and every
/// change gets a sequence number, so a page can ask for only what changed since it last looked. Scans use it too, where
/// Melodex's have a feed of their own: a file's row is the file, then the game it matched.
/// </summary>
public sealed class TaskFeed(TaskArchive archive)
{
    private const string What = "rows";

    /// <summary>Rows a run keeps at most: as many as a big library has files. Later rows aren't added.</summary>
    public const int MaxRows = 200_000;

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, TaskRows> _runs = [];
    private long _seq;

    /// <summary>The task's rows: the ones it had if it's carrying on after a pause, or none.</summary>
    public TaskRows Start(BackgroundTask task)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(task.Id, out var run))
                return run;
            return _runs[task.Id] = new TaskRows(this, task.Id);
        }
    }

    /// <summary>The task is done: its rows go to the archive, where they're kept with the log.</summary>
    public void Finish(Guid task)
    {
        List<TaskRow> rows;
        lock (_lock)
        {
            if (!_runs.TryGetValue(task, out var run))
                return;
            rows = run.Rows.Values.OrderBy(r => r.Order).ToList();
        }
        // Written before it leaves memory, so it's never missing from both.
        archive.Save(task, What, rows);
        lock (_lock)
            _runs.Remove(task);
    }

    /// <summary>The task's rows changed after <paramref name="since"/> (all of them for 0); null when it has none kept.</summary>
    public TaskRowsPage? Since(Guid task, long since)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(task, out var run))
                return new TaskRowsPage(task, _seq, run.Rows.Values.Where(r => r.Seq > since).OrderBy(r => r.Order).ToList());
        }
        return archive.Load<TaskRow>(task, What) is { } kept
            ? new TaskRowsPage(task, kept.Count == 0 ? 0 : kept.Max(r => r.Seq), kept.Where(r => r.Seq > since).ToList())
            : null;
    }

    internal void Put(TaskRows run, TaskRow row)
    {
        lock (_lock)
        {
            var order = run.Rows.TryGetValue(row.Key, out var old) ? old.Order : run.Rows.Count < MaxRows ? run.Rows.Count + 1 : 0;
            if (order > 0)
                run.Rows[row.Key] = row with { Seq = ++_seq, Order = order };
        }
    }
}

/// <summary>One run's rows in the <see cref="TaskFeed"/>, for its task to fill in.</summary>
public sealed class TaskRows(TaskFeed feed, Guid task)
{
    internal Guid Task { get; } = task;
    internal Dictionary<string, TaskRow> Rows { get; } = new(StringComparer.Ordinal);

    /// <summary>Adds the row, or changes it in its place if one has the key already.</summary>
    public void Put(string key, TaskRowState state, string title, string? subtitle = null, string? detail = null, string? href = null,
        string? coverUrl = null) =>
        feed.Put(this, new TaskRow(0, 0, key, state, title, subtitle, detail, href, coverUrl, DateTime.UtcNow));
}
