using System.Text.RegularExpressions;
using Romdex.Client;

namespace Romdex.Web;

/// <summary>
/// Shows a notification when a long task ends, saying how: scans, catalogue updates and formatting. A scan that leaves
/// something to review says so, and goes to Review. What's queued after them on its own, such as finding covers,
/// doesn't pop up. Reads the running tasks when the server says tasks changed, and every few seconds while one runs.
/// </summary>
public sealed partial class TaskNotices : IDisposable
{
    private static readonly TimeSpan Busy = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(30);

    // The kinds that get one.
    // Romdex's: its catalogue update is "catalogue-refresh".
    private static readonly HashSet<string> Shown = ["library-scan", "catalogue-refresh", "library-format", "art-lookup"];

    private readonly RomdexClient _api;
    private readonly Notices _notices;
    private readonly ServerEvents _events;
    private readonly Dictionary<Guid, TaskInfo> _running = [];
    private Timer? _timer;
    private bool _started, _reading;

    public TaskNotices(RomdexClient api, Notices notices, ServerEvents events)
    {
        _api = api;
        _notices = notices;
        _events = events;
    }

    public static string KeyOf(Guid taskId) => $"task:{taskId}";

    public void EnsureStarted()
    {
        if (_started)
            return;
        _started = true;
        _events.Received += OnEvent;
        _events.EnsureStarted();
        _ = RefreshAsync();
    }

    /// <summary>A task just started here, watched at once so even a quick one gets its say.</summary>
    public void Started(TaskInfo task)
    {
        EnsureStarted();
        _running[task.Id] = task;
        Schedule(Busy);
    }

    private void OnEvent(string type)
    {
        if (type is "tasks" or "connected")
            _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_reading)
            return;
        _reading = true;
        try
        {
            var active = (await _api.ListTasksAsync(active: true)).Where(t => Shown.Contains(t.Kind)).ToList();
            foreach (var task in active.Where(t => t.State == TaskState.Running))
                _running[task.Id] = task;
            foreach (var id in _running.Keys.Except(active.Select(t => t.Id)).ToList())
            {
                _running.Remove(id);
                if (await _api.GetTaskAsync(id) is { } done)
                    Finished(done);
            }
            Schedule(_running.Count > 0 ? Busy : Idle);
        }
        catch (Exception e) when (e is HttpRequestException or RomdexApiException)
        {
            Schedule(Idle);
        }
        finally
        {
            _reading = false;
        }
    }

    private void Finished(TaskInfo task)
    {
        if (task.State == TaskState.Cancelled)
            return;
        var key = KeyOf(task.Id);
        var title = task.Title ?? task.Kind;
        var href = $"activity?open={task.Id}";
        if (task.State == TaskState.Failed)
        {
            _notices.Show(new Notice(key, NoticeKind.Task, title + " failed", task.Error, href, "attention", Failed: true));
            return;
        }
        // A scan that left something to review goes there.
        if (task.Kind == "library-scan" && ToReview().Match(task.Progress ?? "") is { Success: true } m && int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.AllowThousands) > 0)
        {
            title += $": {m.Groups[1].Value} to review";
            href = "review";
        }
        else
            title += " done";
        _notices.Show(new Notice(key, NoticeKind.Task, title, task.Progress, href, IconOf(task.Kind)));
    }

    private static string IconOf(string kind) => kind switch
    {
        "library-scan" => "search",
        "catalogue-refresh" => "import",
        "library-format" => "format",
        "art-lookup" => "image",
        _ => "done",
    };

    [GeneratedRegex(@"([\d,]+) to review")]
    private static partial Regex ToReview();

    private void Schedule(TimeSpan after)
    {
        // With events arriving, a look is only needed while something runs, for its progress.
        if (after == Idle && _events.Connected)
        {
            _timer?.Dispose();
            _timer = null;
            return;
        }
        _timer?.Dispose();
        _timer = new Timer(_ => _ = RefreshAsync(), null, after, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _events.Received -= OnEvent;
        _timer?.Dispose();
    }
}
