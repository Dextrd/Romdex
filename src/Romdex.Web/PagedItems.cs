namespace Romdex.Web;

/// <summary>
/// A long list fetched a page at a time, as it's scrolled to, for a list that draws only what's near the screen. Each
/// page is fetched once, and the first is fetched before the list is drawn, which says how long it is.
/// </summary>
/// <param name="load">Fetches the items from one place, and how many there are in all.</param>
/// <param name="size">How many to fetch at a time.</param>
public sealed class PagedItems<T>(Func<int, int, Task<(IReadOnlyList<T> Items, int Total)>> load, int size = 100)
{
    private readonly Dictionary<int, Task<IReadOnlyList<T>>> _pages = [];

    /// <summary>How many there are in all; known once the first page is in.</summary>
    public int Total { get; private set; }

    /// <summary>Fetches the first page, which says how many there are.</summary>
    public Task StartAsync() => PageAsync(0);

    /// <summary>The items from one place, as many as there are up to a count.</summary>
    public async Task<IReadOnlyList<T>> GetAsync(int start, int count)
    {
        var end = Math.Min(start + count, Total);
        var items = new List<T>(Math.Max(0, end - start));
        for (var page = start / size; page * size < end; page++)
        {
            var got = await PageAsync(page);
            var from = Math.Max(start, page * size);
            var to = Math.Min(end, page * size + got.Count);
            for (var i = from; i < to; i++)
                items.Add(got[i - page * size]);
        }
        return items;
    }

    private Task<IReadOnlyList<T>> PageAsync(int page)
    {
        if (!_pages.TryGetValue(page, out var task) || task.IsFaulted)
            _pages[page] = task = LoadAsync(page);
        return task;
    }

    private async Task<IReadOnlyList<T>> LoadAsync(int page)
    {
        var (items, total) = await load(page * size, size);
        Total = total;
        return items;
    }
}
