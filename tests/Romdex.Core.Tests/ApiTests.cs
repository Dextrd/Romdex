using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Romdex.Core.Api;
using Romdex.Core.Tests.Support;

namespace Romdex.Core.Tests;

public sealed class RomdexFactory : WebApplicationFactory<Program>
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "romdex-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("Romdex:DataPath", _dataPath).UseSetting("Romdex:CatalogueRefreshHours", "0");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }
}

public class ApiTests(RomdexFactory factory) : IClassFixture<RomdexFactory>
{
    private readonly HttpClient _client = factory.CreateSignedInClient();

    [Fact]
    public async Task Health_reports_healthy_with_a_migrated_database()
    {
        var health = await _client.GetFromJsonAsync<HealthResponse>("/api/v1/health");

        Assert.Equal("healthy", health!.Status);
        Assert.Equal("healthy", health.Checks["database"]);
    }

    [Fact]
    public async Task Version_lists_every_contract()
    {
        var version = await _client.GetFromJsonAsync<VersionResponse>("/api/v1/version");

        Assert.Equal("v1", version!.ApiVersion);
        Assert.Equal(["connector", "index", "metadata", "retrieval"], version.Contracts.Keys.Order());
    }

    [Fact]
    public async Task OpenApi_document_is_served_and_describes_the_v1_api()
    {
        var response = await _client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.StartsWith("3.1", document.RootElement.GetProperty("openapi").GetString());
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/v1/health", out _));

        // Every operation is named, said what it's for, grouped, and says what it answers, so the reference and the
        // clients made from it are whole.
        var gaps = new List<string>();
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        foreach (var operation in path.Value.EnumerateObject())
        {
            var o = operation.Value;
            var missing = new[] { "operationId", "summary", "tags", "responses" }.Where(k => !o.TryGetProperty(k, out var v) || Empty(v)).ToList();
            if (missing.Count > 0)
                gaps.Add($"{operation.Name.ToUpperInvariant()} {path.Name}: {string.Join(", ", missing)}");
        }
        Assert.Empty(gaps);

        static bool Empty(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Array => value.GetArrayLength() == 0,
            JsonValueKind.Object => !value.EnumerateObject().Any(),
            _ => true,
        };
    }
}
