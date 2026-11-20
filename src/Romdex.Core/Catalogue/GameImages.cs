using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Romdex.Core.Catalogue;

/// <summary>
/// Pictures of games, such as their title screens, fetched from where a metadata plugin found them the first time a
/// page shows them, and kept in the data folder's <c>art/images</c> folder by their address. As with covers, pages
/// only load them from the core, so the browser never contacts the sources.
/// </summary>
public sealed class GameImages(IHttpClientFactory clients, IOptions<RomdexOptions> options, ILogger<GameImages> logger)
{
    // Bigger than any screenshot; anything larger isn't one.
    private const long Largest = 10 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, Task<CachedCover?>> _fetching = new();

    private string Root => Path.Combine(options.Value.DataPath, "art", "images");

    /// <summary>The picture at the address, fetching it if it isn't kept yet. Null when it can't be had or isn't a picture.</summary>
    public async Task<CachedCover?> GetAsync(string url, CancellationToken ct)
    {
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32];
        if (Kept(name) is { } kept)
            return kept;
        // Not cancelled with a request, so a page that moves on doesn't waste what was fetched.
        return await _fetching.GetOrAdd(name, _ => FetchAsync(name, url)).WaitAsync(ct);
    }

    private CachedCover? Kept(string name)
    {
        foreach (var (extension, type) in Types)
        {
            var path = Path.Combine(Root, name + extension);
            if (File.Exists(path))
                return new CachedCover(path, type, File.GetLastWriteTimeUtc(path));
        }
        return null;
    }

    private async Task<CachedCover?> FetchAsync(string name, string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                return null;
            using var response = await clients.CreateClient(CoverCache.HttpClientName).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > Largest)
                return null;
            var bytes = await response.Content.ReadAsByteArrayAsync();
            // What it is by its own first bytes, whatever it's served as.
            var extension = bytes.AsSpan().StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47]) ? ".png"
                : bytes.AsSpan().StartsWith((byte[])[0xFF, 0xD8, 0xFF]) ? ".jpg"
                : bytes.Length > 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8) ? ".webp"
                : null;
            if (extension is null || bytes.Length > Largest)
                return null;
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, name + extension);
            await File.WriteAllBytesAsync(path, bytes);
            return new CachedCover(path, Types[extension], DateTime.UtcNow);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            logger.LogInformation("Couldn't fetch a picture of a game from {Host}: {Message}", Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "?", e.Message);
            return null;
        }
        finally
        {
            _fetching.TryRemove(name, out _);
        }
    }

    private static readonly Dictionary<string, string> Types = new() { [".png"] = "image/png", [".jpg"] = "image/jpeg", [".webp"] = "image/webp" };
}
