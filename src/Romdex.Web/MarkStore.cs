using Romdex.Client;

namespace Romdex.Web;

/// <summary>
/// Your stars and ratings, shared by every page, so a page can say what's starred: loaded once when first needed, and
/// again when another tab or an app changes them, as Melodex's are.
/// </summary>
public sealed class MarkStore
{
    private readonly RomdexClient _api;
    private Task? _loading;
    private Dictionary<(MarkKind, Guid), ItemMark> _marks = [];

    public MarkStore(RomdexClient api, ServerEvents events)
    {
        _api = api;
        events.Received += kind =>
        {
            if (kind is "marks" or "connected" && _loading is not null)
                _ = RefreshAsync();
        };
    }

    /// <summary>Raised whenever they're loaded or changed.</summary>
    public event Action? Changed;

    public bool IsStarred(MarkKind kind, Guid id) => _marks.TryGetValue((kind, id), out var mark) && mark.StarredAt is not null;

    public int RatingOf(MarkKind kind, Guid id) => _marks.TryGetValue((kind, id), out var mark) ? mark.Rating ?? 0 : 0;

    /// <summary>What's starred of one kind.</summary>
    public IReadOnlySet<Guid> Starred(MarkKind kind) => _marks.Values.Where(m => m.Kind == kind && m.StarredAt is not null).Select(m => m.Id).ToHashSet();

    public Task EnsureLoadedAsync() => _loading ??= RefreshAsync();

    public async Task RefreshAsync()
    {
        try
        {
            _marks = (await _api.ListMarksAsync()).ToDictionary(m => (m.Kind, m.Id));
        }
        catch (Exception e) when (e is HttpRequestException or RomdexApiException)
        {
            // A key without a user has none.
        }
        Changed?.Invoke();
    }

    public Task StarAsync(MarkKind kind, Guid id, bool starred) => SetAsync(kind, id, new SetMark(Starred: starred));

    /// <summary>Rates it 1 to 5; giving it the rating it has takes it away.</summary>
    public Task RateAsync(MarkKind kind, Guid id, int rating) => SetAsync(kind, id, new SetMark(Rating: RatingOf(kind, id) == rating ? 0 : rating));

    private async Task SetAsync(MarkKind kind, Guid id, SetMark change)
    {
        var mark = await _api.SetMarkAsync(kind, id, change);
        if (mark is null)
            _marks.Remove((kind, id));
        else
            _marks[(kind, id)] = mark;
        Changed?.Invoke();
    }
}
