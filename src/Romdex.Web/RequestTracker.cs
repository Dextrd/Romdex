using Romdex.Client;

namespace Romdex.Web;

/// <summary>
/// Requests, as Melodex's tracker has them. For now, its notifications: what's asked for here gets one when it's asked
/// for, and another when it finishes, one for each lot asked for together: in the library, or failed. The rest of
/// Melodex's tracker, sharing what's on its way with every page and Activity's list of fetches, comes with Activity
/// (docs/plans/parity.md). Reads them again when the server says fetches changed, and every few seconds while any go.
/// </summary>
public sealed class RequestTracker : IDisposable
{
    private static readonly TimeSpan Busy = TimeSpan.FromSeconds(3);

    private readonly RomdexClient _api;
    private readonly Notices _notices;
    private readonly ServerEvents _events;
    private readonly List<Lot> _lots = [];
    private Timer? _timer;
    private bool _started, _reading;

    /// <summary>Requests asked for together, such as each wanted region of a game, said to be done together.</summary>
    private sealed record Lot(string Key, string Title, string? Replaced, List<RequestInfo> Asked);

    public RequestTracker(RomdexClient api, Notices notices, ServerEvents events)
    {
        _api = api;
        _notices = notices;
        _events = events;
    }

    /// <summary>
    /// Says what getting a game asked for, and watches it, to say when it finishes.
    /// </summary>
    /// <param name="title">What was asked for, such as the game's name.</param>
    /// <param name="replaced">What it replaces, from Review, said when it's done.</param>
    public void Asked(string title, TitleFetch fetch, string? replaced = null)
    {
        var requests = fetch.Requests ?? [];
        var key = $"asked:{requests.FirstOrDefault()?.Id ?? Guid.NewGuid()}";
        var going = requests.Where(r => r.State == RequestState.Active).ToList();
        if (going.Count == 0)
        {
            var why = string.Join(" ", fetch.Problems);
            _notices.Show(new Notice(key, NoticeKind.Download, title, why.Length > 0 ? why : "Nothing could be fetched", "review/couldnt-fetch", "attention",
                Failed: requests.Count > 0), always: true);
            return;
        }
        // Another says when it's finished.
        _notices.Show(new Notice(key, NoticeKind.Download, title, "Asked for: follow it in Activity", "activity/fetches", "get"));
        _lots.Add(new Lot(key, title, replaced, [.. requests]));
        EnsureStarted();
        Schedule(Busy);
    }

    private void EnsureStarted()
    {
        if (_started)
            return;
        _started = true;
        _events.Received += OnEvent;
        _events.EnsureStarted();
    }

    private void OnEvent(string type)
    {
        if (type is "downloads" or "connected" && _lots.Count > 0)
            _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_reading)
            return;
        _reading = true;
        try
        {
            foreach (var lot in _lots.ToList())
            {
                var now = new List<RequestInfo>();
                foreach (var asked in lot.Asked)
                    now.Add(await _api.GetRequestAsync(asked.Id) ?? asked);
                if (now.Any(r => r.State == RequestState.Active))
                    continue;
                _lots.Remove(lot);
                Finished(lot, now);
            }
        }
        catch (Exception e) when (e is HttpRequestException or RomdexApiException)
        {
        }
        finally
        {
            _reading = false;
            if (_lots.Count > 0)
                Schedule(Busy);
        }
    }

    private void Finished(Lot lot, IReadOnlyList<RequestInfo> now)
    {
        var landed = now.Count(r => r.State == RequestState.Done);
        var failed = now.Count(r => r.State == RequestState.Failed);
        if (landed + failed == 0)
            return;
        var key = lot.Key + ":done";
        if (now.Count == 1)
        {
            var one = now[0];
            _notices.Show(one.State == RequestState.Done
                ? new Notice(key, NoticeKind.Download, lot.Title, lot.Replaced is { } replaced ? $"In your library, in place of {replaced}" : "In your library",
                    $"titles/{one.Title.Id}", "get")
                : new Notice(key, NoticeKind.Download, lot.Title, "Couldn't be fetched" + (one.Message is { } why ? $": {why}" : ""), "review/couldnt-fetch",
                    "attention", Failed: true));
            return;
        }
        // Each wanted region of a game, said together.
        var parts = new[] { (landed, "in your library"), (failed, "failed") }.Where(p => p.Item1 > 0).Select(p => $"{p.Item1} {p.Item2}");
        _notices.Show(new Notice(key, NoticeKind.Download, $"{lot.Title}: {now.Count} regions finished", string.Join(", ", parts),
            failed > 0 ? "review/couldnt-fetch" : $"titles/{now[0].Title.Id}", landed == 0 ? "attention" : "get", Failed: landed == 0));
    }

    private void Schedule(TimeSpan after)
    {
        _timer?.Dispose();
        _timer = new Timer(_ => _ = RefreshAsync(), null, after, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _events.Received -= OnEvent;
        _timer?.Dispose();
    }
}
