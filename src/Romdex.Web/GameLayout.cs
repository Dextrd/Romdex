using Microsoft.JSInterop;

namespace Romdex.Web;

/// <summary>
/// Whether lists of games are shown as a grid of covers or as a table, as someone last chose, kept for this browser.
/// One choice for every list of games, so changing it on one page changes them all.
/// </summary>
public sealed class GameLayout(IJSRuntime js)
{
    public const string Grid = "grid";
    public const string List = "list";

    public static readonly IReadOnlyList<(string Value, string Label, string? Hint)> Options =
        [(Grid, "Grid", "Covers in a grid"), (List, "List", "A table, with more about each")];

    private const string Key = "romdex.game-layout";
    private string? _current;

    public async Task<string> GetAsync()
    {
        if (_current is not null)
            return _current;
        try
        {
            _current = await js.InvokeAsync<string?>("localStorage.getItem", Key) is List ? List : Grid;
        }
        catch (JSException)
        {
            // Storage is turned off in this browser.
            _current = Grid;
        }
        return _current;
    }

    public async Task SetAsync(string layout)
    {
        _current = layout;
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", Key, layout);
        }
        catch (JSException)
        {
        }
    }
}
