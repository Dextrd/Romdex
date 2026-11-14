using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romdex.Contracts.Plugins;
using Romdex.Contracts.Plugins.Retrieval;
using Romdex.Core.Data;

namespace Romdex.Core.Plugins;

/// <summary>
/// A retriever instance, reached over the HTTP contract (see <see cref="RetrievalJson"/>). Settings are sent with every
/// call, so the container keeps no setup of its own.
/// </summary>
public sealed class RetrievalClient(HttpClient http, JsonElement settings) : IPlugin
{
    public static async Task<PluginManifest> GetManifestAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.GetAsync("v1/manifest", ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PluginManifest>(RetrievalJson.Options, ct))!;
    }

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("v1/health", new HealthRequest(settings), RetrievalJson.Options, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<HealthReport>(RetrievalJson.Options, cancellationToken))!;
    }

    /// <summary>Runs one of the checks the manifest offers.</summary>
    public async Task<CheckResult> RunCheckAsync(string check, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"v1/checks/{Uri.EscapeDataString(check)}", new HealthRequest(settings), RetrievalJson.Options, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<CheckResult>(RetrievalJson.Options, cancellationToken))!;
    }

    public async Task<IReadOnlyList<Candidate>> SearchAsync(RetrievalTarget target, int limit, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("v1/search", new SearchRequest(settings, target, limit), RetrievalJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<SearchResponse>(RetrievalJson.Options, ct))!.Candidates;
    }

    public async Task<IReadOnlyList<Candidate>> ListAsync(CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("v1/list", new ListRequest(settings), RetrievalJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ListResponse>(RetrievalJson.Options, ct))!.Candidates;
    }

    /// <summary>Starts staging a candidate into <paramref name="stagingDirectory"/>, relative to the staging volume.</summary>
    public async Task<FetchStatus> FetchAsync(string jobId, RetrievalTarget? target, Candidate candidate, string stagingDirectory, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("v1/fetch", new FetchRequest(settings, jobId, target, candidate, stagingDirectory), RetrievalJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<FetchStatus>(RetrievalJson.Options, ct))!;
    }

    public async Task<FetchStatus> GetFetchAsync(string jobId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"v1/fetch/{Uri.EscapeDataString(jobId)}", ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<FetchStatus>(RetrievalJson.Options, ct))!;
    }

    public async Task CancelFetchAsync(string jobId, CancellationToken ct)
    {
        using var response = await http.DeleteAsync($"v1/fetch/{Uri.EscapeDataString(jobId)}", ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureAsync(response, ct);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        RetrievalError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<RetrievalError>(RetrievalJson.Options, ct);
        }
        catch (JsonException)
        {
        }
        throw new RetrievalException(error?.Error ?? $"The plugin answered {(int)response.StatusCode} {response.ReasonPhrase}", error?.Code, response.StatusCode);
    }
}

/// <summary>A retriever said no, with its reason.</summary>
public sealed class RetrievalException(string message, string? code, HttpStatusCode status) : Exception(message)
{
    public string? Code { get; } = code;
    public HttpStatusCode Status { get; } = status;
}

/// <summary>The retrievers the user set up.</summary>
public sealed class RetrievalPlugins(RomdexDbContext db, IHttpClientFactory httpFactory)
{
    public const string HttpClientName = "retrieval";

    public async Task<IReadOnlyList<(PluginInstance Instance, RetrievalClient Plugin)>> GetAllAsync(CancellationToken ct)
    {
        var instances = await db.PluginInstances.Where(p => p.Type == PluginType.Retrieval && p.Enabled && p.Endpoint != null)
            .OrderBy(p => p.Priority).ThenBy(p => p.CreatedAt).ToListAsync(ct);
        return instances.Select(i => (i, Create(i))).ToList();
    }

    /// <summary>What an instance's plugin said it can do, when it was set up or last changed.</summary>
    public static IReadOnlyList<string> CapabilitiesOf(PluginInstance instance) =>
        instance.ManifestJson is { } json ? JsonSerializer.Deserialize<PluginManifest>(json, RetrievalJson.Options)?.Capabilities ?? [] : [];

    /// <summary>The enabled instances that can do something, in the user's order.</summary>
    public async Task<IReadOnlyList<(PluginInstance Instance, RetrievalClient Plugin)>> WithAsync(string capability, CancellationToken ct) =>
        (await GetAllAsync(ct)).Where(p => CapabilitiesOf(p.Instance).Contains(capability)).ToList();

    public RetrievalClient Create(PluginInstance instance) =>
        new(Client(instance.Endpoint!), JsonDocument.Parse(instance.SettingsJson).RootElement.Clone());

    public HttpClient Client(string endpoint)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        http.BaseAddress = new Uri(endpoint.EndsWith('/') ? endpoint : endpoint + "/");
        return http;
    }
}
