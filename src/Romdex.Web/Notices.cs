using Romdex.Client;

namespace Romdex.Web;

/// <summary>What a notification is about, each kind of which can be turned off under Appearance.</summary>
public enum NoticeKind
{
    /// <summary>A long task finishing: a scan, a catalogue update, an import, formatting.</summary>
    Task,

    /// <summary>Something you asked for finishing: in the library, to review, or failed.</summary>
    Download,

    /// <summary>Something you started that goes on elsewhere finishing, such as looking for a better copy.</summary>
    Action,
}

/// <param name="Key">Shown again with the same key, it replaces the one showing.</param>
/// <param name="Icon">The icon shown when there's no artwork.</param>
/// <param name="Art">The cover of what it's about, shown in place of the icon.</param>
/// <param name="Failed">It went wrong: it shows as an error.</param>
public sealed record Notice(string Key, NoticeKind Kind, string Title, string? Text = null, string? Href = null,
    string Icon = "done", string? Art = null, bool Failed = false)
{
    /// <summary>When it was first shown, for the order they stack in.</summary>
    public DateTime Shown { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// The notifications that slide in from the bottom right: the newest at the bottom. Each closes itself
/// after a few seconds, unless the pointer is over them, and can be closed sooner. Kinds the signed-in user turned off
/// aren't shown.
/// </summary>
public sealed class Notices(AuthState auth) : IDisposable
{
    /// <summary>How long each stays.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(8);

    private readonly List<Notice> _shown = [];
    private readonly Dictionary<string, Timer> _closing = [];
    private bool _held;

    /// <summary>Oldest first, so the newest is at the bottom.</summary>
    public IReadOnlyList<Notice> Shown => _shown;

    public event Action? Changed;

    /// <summary>Whether the signed-in user wants this kind.</summary>
    public bool Wants(NoticeKind kind) => auth.Status?.Appearance is not { } appearance || kind switch
    {
        NoticeKind.Task => appearance.TaskNotices,
        NoticeKind.Download => appearance.DownloadNotices,
        _ => appearance.ActionNotices,
    };

    /// <summary>Shows it, or updates the one with its key, keeping its place.</summary>
    /// <param name="always">Even if its kind is turned off: it answers something you just did, such as saying it went wrong.</param>
    public void Show(Notice notice, bool always = false)
    {
        if (!always && !Wants(notice.Kind))
            return;
        var at = _shown.FindIndex(n => n.Key == notice.Key);
        if (at >= 0)
            _shown[at] = notice with { Shown = _shown[at].Shown };
        else
            _shown.Add(notice);
        if (!_held)
            CloseLater(notice.Key);
        Changed?.Invoke();
    }

    public void Close(string key)
    {
        Forget(key);
        if (_shown.RemoveAll(n => n.Key == key) > 0)
            Changed?.Invoke();
    }

    /// <summary>The pointer is over them: none closes until it leaves.</summary>
    public void Hold()
    {
        _held = true;
        foreach (var key in _closing.Keys.ToList())
            Forget(key);
    }

    /// <summary>The pointer has left: each closes after a while again.</summary>
    public void Release()
    {
        _held = false;
        foreach (var notice in _shown)
            CloseLater(notice.Key);
    }

    private void CloseLater(string key)
    {
        Forget(key);
        _closing[key] = new Timer(_ => Close(key), null, Linger, Timeout.InfiniteTimeSpan);
    }

    private void Forget(string key)
    {
        if (_closing.Remove(key, out var timer))
            timer.Dispose();
    }

    public void Dispose()
    {
        foreach (var timer in _closing.Values)
            timer.Dispose();
    }
}
