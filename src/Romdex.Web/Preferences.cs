using System.Text.Json;
using Microsoft.JSInterop;

namespace Romdex.Web;

/// <summary>
/// Per-browser view preferences, such as which Activity sections are open, as Melodex's. Kept in local storage, so they survive
/// reloads but aren't shared between devices. Losing them only resets the view.
/// </summary>
public sealed class Preferences(IJSRuntime js)
{
    private const string Prefix = "romdex.";

    public async Task<T> GetAsync<T>(string key, T fallback)
    {
        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", Prefix + key);
            return json is null ? fallback : JsonSerializer.Deserialize<T>(json) ?? fallback;
        }
        catch (Exception e) when (e is JSException or JsonException)
        {
            return fallback;
        }
    }

    public async Task SetAsync<T>(string key, T value)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", Prefix + key, JsonSerializer.Serialize(value));
        }
        catch (JSException)
        {
        }
    }
}
