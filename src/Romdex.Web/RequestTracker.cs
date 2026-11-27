using Romdex.Client;

namespace Romdex.Web;

/// <summary>
/// Requests, shared by every page so Get buttons can show what's already on its way: every one still going, however
/// many, and the newest of the rest, with earlier ones loaded as they're wanted (Activity's Earlier). Reloads when the
/// server says requests changed, polling only as a fallback. What's asked for here gets a notification when it finishes,
/// one for each lot asked for together: in the library, or failed.
/// </summary>
public sealed class RequestTracker : IDisposable
{
    private static readonly TimeSpan Busy = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(30);

    // How many finished ones come with each load, and each page of earlier ones.
    private const int Page = 200;

    // Asked for within this long of the last one, it joins its notification.
    private static readonly TimeSpan Together = TimeSpan.FromSeconds(3);

    private readonly RomdexClient api;
    private readonly ServerEvents events;
    private readonly Notices notices;

    // What was asked for here, a lot at a time, until it's all finished. Romdex's lots have a name, the game's, as its
    // requests are each a region of one game rather than things with names of their own; and what they replace.
    private sealed record Lot(string Key, List<RequestInfo> Asked, string? Title = null, string? Replaced = null)
    {
        public DateTime Last { get; set; } = DateTime.UtcNow;
    }

    private readonly List<Lot> _lots = [];

    public RequestTracker(RomdexClient api, ServerEvents events, Notices notices)
    {
        this.api = api;
        this.events = events;
        this.notices = notices;
        events.Received += OnEvent;
        events.EnsureStarted();
    }

    private void OnEvent(string type)
    {
        if (type is "requests" or "connected" && _used)
            _ = RefreshAsync();
    }

    private IReadOnlyList<RequestInfo> _requests = [];
    private Timer? _timer;
    private Task? _loading;

    // Kept current once anything has asked for the list, whether through EnsureLoadedAsync or RefreshAsync.
    private bool _used;

    // Earlier ones loaded on top of the newest, kept across reloads; and whether there are more before them.
    private IReadOnlyList<RequestInfo> _earlier = [];
    private bool _more = true;
    private Task? _loadingEarlier;

    /// <summary>Raised whenever the list is refreshed.</summary>
    public event Action? Changed;

    /// <summary>Requests, newest first: all of those still going, and the newest and any earlier loaded of the rest.</summary>
    public IReadOnlyList<RequestInfo> Requests => _requests;

    /// <summary>Whether there are requests before the earliest loaded, for <see cref="LoadEarlierAsync"/>.</summary>
    public bool HasEarlier => _more;

    /// <summary>Loads the page of requests before the earliest loaded.</summary>
    public Task LoadEarlierAsync() => _loadingEarlier ??= LoadEarlierCoreAsync();

    private async Task LoadEarlierCoreAsync()
    {
        try
        {
            if (!_more || _requests.Count == 0)
                return;
            var page = await api.ListRequestsAsync(limit: Page, before: _requests.Min(r => r.CreatedAt));
            _more = page.Count == Page;
            _earlier = _earlier.Concat(page).DistinctBy(r => r.Id).ToList();
            _requests = Merge(_requests, page);
            Changed?.Invoke();
        }
        catch (Exception e) when (e is HttpRequestException or RomdexApiException)
        {
        }
        finally
        {
            _loadingEarlier = null;
        }
    }

    // Newest first, each once: the later list's copy wins.
    private static List<RequestInfo> Merge(IEnumerable<RequestInfo> first, IEnumerable<RequestInfo> later) =>
        later.Concat(first).DistinctBy(r => r.Id).OrderByDescending(r => r.CreatedAt).ToList();

    public Task EnsureLoadedAsync() => _loading ??= RefreshAsync();

    /// <summary>The requests still fetching this game, one for each region being looked for.</summary>
    public IReadOnlyList<RequestInfo> Find(Guid titleId) => _requests.Where(r => r.Title.Id == titleId && r.Active).ToList();

    /// <summary>Asked for here some other way: it gets a notification when it finishes.</summary>
    public void Watch(RequestInfo asked)
    {
        var lot = _lots.LastOrDefault();
        if (lot is null || lot.Title is not null || DateTime.UtcNow - lot.Last > Together || lot.Asked.All(r => !r.Active))
            _lots.Add(lot = new Lot($"download:{asked.Id}", []));
        lot.Asked.Add(asked);
        lot.Last = DateTime.UtcNow;
    }

    /// <summary>
    /// Romdex's: says what getting a game asked for, a request for each region, and watches them as one lot, to say
    /// when they've all finished.
    /// </summary>
    /// <param name="title">What was asked for, such as the game's name.</param>
    /// <param name="replaced">What it replaces, from Review, said when it's done.</param>
    public void Asked(string title, TitleFetch fetch, string? replaced = null)
    {
        var requests = fetch.Requests ?? [];
        var key = $"download:{requests.FirstOrDefault()?.Id ?? Guid.NewGuid()}";
        var going = requests.Where(r => r.Active).ToList();
        if (going.Count == 0)
        {
            var why = string.Join(" ", fetch.Problems);
            notices.Show(new Notice(key, NoticeKind.Download, title, why.Length > 0 ? why : "Nothing could be fetched", "review/couldnt-fetch", "attention",
                Failed: requests.Count > 0), always: true);
            return;
        }
        // Another says when it's finished.
        notices.Show(new Notice(key, NoticeKind.Download, title, "Asked for: follow it in Activity", $"activity/fetches?open={going[0].Id}", "get"));
        _lots.Add(new Lot(key, [.. requests], title, replaced));
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        _used = true;
        IReadOnlyList<RequestInfo> latest;
        try
        {
            var going = api.ListRequestsAsync(active: true, limit: 10_000);
            var newest = await api.ListRequestsAsync(limit: Page);
            if (_earlier.Count == 0)
                _more = newest.Count == Page;
            // Earlier ones loaded before stay, as they were unless they're among those just loaded.
            latest = Merge(_earlier, newest.Concat(await going).DistinctBy(r => r.Id));
        }
        catch (Exception e) when (e is HttpRequestException or RomdexApiException)
        {
            Schedule(Idle);
            return;
        }
        _requests = latest;
        Finished();
        Schedule(latest.Any(r => r.Active) && !events.Connected ? Busy : Idle);
        Changed?.Invoke();
    }

    // Each lot all finished gets its say, then is forgotten.
    private void Finished()
    {
        foreach (var lot in _lots.ToList())
        {
            var now = lot.Asked.Select(r => _requests.FirstOrDefault(n => n.Id == r.Id) ?? r).ToList();
            if (now.Any(r => r.Active) || DateTime.UtcNow - lot.Last < TimeSpan.FromSeconds(1))
                continue;
            _lots.Remove(lot);
            var landed = now.Count(r => r.State == RequestState.Done);
            var failed = now.Count(r => r.State == RequestState.Failed);
            if (landed + failed == 0)
                continue;
            var key = lot.Key + ":done";
            if (now.Count == 1)
            {
                var one = now[0];
                var title = lot.Title ?? Name(one);
                notices.Show(one.State switch
                {
                    RequestState.Done => new Notice(key, NoticeKind.Download, title,
                        lot.Replaced is { } replaced ? $"In your library, in place of {replaced}" : "In your library", $"titles/{one.Title.Id}", "get"),
                    _ => new Notice(key, NoticeKind.Download, title, "Couldn't be fetched" + (one.Message is { } why ? $": {why}" : ""), Href(lot), "attention",
                        Failed: true),
                });
                continue;
            }
            var parts = new[] { (landed, "in your library"), (failed, "failed") }.Where(p => p.Item1 > 0).Select(p => $"{p.Item1} {p.Item2}");
            // Each wanted region of one game, or several games asked for together.
            var things = lot.Title is { } game ? $"{game}: {Format.Count(now.Count, "region", "regions")}" : Format.Count(now.Count, "game", "games");
            notices.Show(new Notice(key, NoticeKind.Download, things + " finished", string.Join(", ", parts), Href(lot),
                landed == 0 ? "attention" : "get", Failed: landed == 0));
        }
    }

    // A request by the dump it's for, or its game.
    private static string Name(RequestInfo request) => request.Dump?.Name ?? request.Title.Name;

    // To the first of them in Activity, flashed, so where they are is plain.
    private static string Href(Lot lot) => $"activity/fetches?open={lot.Asked[0].Id}";

    private void Schedule(TimeSpan delay)
    {
        _timer ??= new Timer(_ => _ = RefreshAsync());
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        events.Received -= OnEvent;
        _timer?.Dispose();
    }
}
