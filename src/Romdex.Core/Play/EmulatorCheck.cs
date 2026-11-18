using Microsoft.Extensions.Options;
using Romdex.Core.Api;

namespace Romdex.Core.Play;

/// <summary>Whether the emulatorjs container answers, asked at most once a minute.</summary>
public sealed class EmulatorCheck(IHttpClientFactory clients, IOptions<RomdexOptions> options)
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    private (DateTime At, bool Up)? _last;

    public async Task<bool> IsUpAsync(CancellationToken ct)
    {
        if (_last is { } last && DateTime.UtcNow - last.At < Every)
            return last.Up;
        bool up;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await clients.CreateClient(PlayEndpoints.EmulatorClient)
                .GetAsync($"{options.Value.EmulatorUrl.TrimEnd('/')}/data/version.json", timeout.Token);
            up = response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            up = false;
        }
        _last = (DateTime.UtcNow, up);
        return up;
    }
}
