using System.Collections.Concurrent;
using System.Net;

namespace Romdex.Core.Tests.Support;

/// <summary>Stands in for the app a webhook posts to.</summary>
public sealed class WebhookReceiver : HttpMessageHandler
{
    private readonly ConcurrentQueue<(Dictionary<string, string> Headers, string Body)> _received = new();

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public IReadOnlyList<(Dictionary<string, string> Headers, string Body)> Received => _received.ToList();

    public async Task<IReadOnlyList<(Dictionary<string, string> Headers, string Body)>> WaitAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (_received.Count < count && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.True(_received.Count >= count, $"Expected {count} deliveries, got {_received.Count}");
        return Received;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        _received.Enqueue((headers, await request.Content!.ReadAsStringAsync(ct)));
        return new HttpResponseMessage(Status);
    }
}
