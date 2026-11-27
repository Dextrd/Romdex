using Romdex.Client;

namespace Romdex.Web;

/// <summary>
/// Your wishlist as the app knows it, shared by every page, so a game's star can say whether it's on it. Loaded once
/// when first needed, and again when the Wishlist view opens.
/// </summary>
public sealed class WishlistStore(RomdexClient api)
{
    private Task? _loading;
    private HashSet<Guid> _ids = [];

    /// <summary>Raised whenever it's loaded or changed.</summary>
    public event Action? Changed;

    public IReadOnlyList<WishlistItem> Items { get; private set; } = [];

    /// <summary>A retriever that searches is on; until it's known, it's taken to be.</summary>
    public bool CanFetch { get; private set; } = true;

    public bool Loaded { get; private set; }

    public bool Contains(Guid targetId) => _ids.Contains(targetId);

    public Task EnsureLoadedAsync() => _loading ??= RefreshAsync();

    public async Task RefreshAsync()
    {
        try
        {
            var wishlist = await api.GetWishlistAsync();
            Items = wishlist.Items;
            CanFetch = wishlist.CanFetch;
            _ids = wishlist.Items.Select(i => i.TitleId).ToHashSet();
            Loaded = true;
        }
        catch (Exception e) when (e is HttpRequestException or RomdexApiException)
        {
            // A key without a user, which has no wishlist: nothing's on it.
        }
        Changed?.Invoke();
    }

    public async Task AddAsync(Guid titleId, Guid? dumpId = null)
    {
        await api.AddToWishlistAsync(new AddToWishlist(titleId, dumpId));
        _ids.Add(titleId);
        await RefreshAsync();
    }

    public async Task RemoveAsync(Guid targetId)
    {
        await api.RemoveFromWishlistAsync(targetId);
        _ids.Remove(targetId);
        Items = Items.Where(i => i.TitleId != targetId).ToList();
        Changed?.Invoke();
    }
}
