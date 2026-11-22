using System.Net;
using System.Text;
using System.Text.Json;
using Romdex.Contracts.Plugins;
using Romdex.Contracts.Plugins.Index;
using Romdex.Core.Catalogue;
using Romdex.Plugins.Libretro;

namespace Romdex.Core.Tests;

/// <summary>Fetching No-Intro's DATs as libretro keeps them, and telling the newest of several DATs of one system.</summary>
public sealed class LibretroDatsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "romdex-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeLibretro _site = new();

    public LibretroDatsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Fetches_the_systems_ticked_keeps_only_newer_and_waits_until_its_due()
    {
        _site.Dats["Nintendo - Game Boy"] = Dat("Nintendo - Game Boy", "2026.10.08");
        _site.Dats["Sega - Game Gear"] = Dat("Sega - Game Gear", "2026.10.08");
        var libretro = Create(["Nintendo - Game Boy", "Sega - Game Gear"]);

        Assert.Equal(["Nintendo - Game Boy", "Sega - Game Gear"], (await libretro.FetchAsync(default)).Select(s => s.Name).Order());
        var systems = await libretro.ListSystemsAsync(default);
        Assert.All(systems, s => Assert.Equal(DatSources.NoIntro, s.Source));
        Assert.Equal(DatSources.NoIntro, (await libretro.GetSystemAsync("Nintendo - Game Boy", default))!.System.Source);
        Assert.Equal("Made Up (USA)", Assert.Single((await libretro.GetSystemAsync("Nintendo - Game Boy", default))!.Dumps).Name);
        Assert.Equal(HealthStatus.Healthy, (await libretro.CheckHealthAsync(default)).Status);

        // Not due yet: nothing is asked for.
        _site.Requests.Clear();
        Assert.Empty(await libretro.FetchAsync(default));
        Assert.Empty(_site.Requests);

        // Due: the same version changes nothing; a newer one replaces it.
        _site.Dats["Nintendo - Game Boy"] = Dat("Nintendo - Game Boy", "2026.11.14");
        var due = Create(["Nintendo - Game Boy", "Sega - Game Gear"], days: 0);
        Assert.Equal("Nintendo - Game Boy", Assert.Single(await due.FetchAsync(default)).Name);

        // One unticked isn't listed any more.
        Assert.Equal("Nintendo - Game Boy", Assert.Single(await Create(["Nintendo - Game Boy"]).ListSystemsAsync(default)).Name);
    }

    [Fact]
    public async Task Retools_clone_lists_are_fetched_for_its_systems_by_however_Retool_names_them()
    {
        _site.Dats["Atari - 2600"] = Dat("Atari - 2600", "2026.11.14", """
            game ( name "Kite Story (USA)" rom ( name "a.a26" size 1 crc 00000001 ) )
            game ( name "Tako no Monogatari (Japan)" rom ( name "b.a26" size 1 crc 00000002 ) )
            """);
        _site.Files["/clonelists"] = """
            [
              { "name": "Atari - Atari 2600 (No-Intro).json", "download_url": "https://libretro.test/lists/2600.json" },
              { "name": "Atari - Atari 2600 (Redump).json", "download_url": "https://libretro.test/lists/wrong.json" }
            ]
            """;
        _site.Files["/lists/2600.json"] = """
            { "description": { "name": "Atari - Atari 2600 (No-Intro)", "lastUpdated": "2026-01-02 11:07:19" },
              "variants": [ { "group": "Kite Story", "titles": [ { "searchTerm": "Kite Story" }, { "searchTerm": "Tako no Monogatari" } ] } ] }
            """;
        var libretro = Create(["Atari - 2600"], cloneLists: "https://libretro.test/clonelists");

        await libretro.FetchAsync(default);
        Assert.Equal("2026-01-02 11:07:19", Assert.Single(await libretro.ListSystemsAsync(default)).CloneList);
        var dumps = (await libretro.GetSystemAsync("Atari - 2600", default))!.Dumps.ToDictionary(d => d.Name, d => d.CloneOf);
        Assert.Equal("Kite Story (USA)", dumps["Tako no Monogatari (Japan)"]);

        // Retool's names and libretro's are of one system however each spells it.
        Assert.Equal(Romdex.Plugins.DatFolder.Retool.SystemKey("SNK - NeoGeo Pocket Color"), Romdex.Plugins.DatFolder.Retool.SystemKey("SNK - Neo Geo Pocket Color"));
        Assert.Equal(Romdex.Plugins.DatFolder.Retool.SystemKey("Magnavox - Odyssey 2"), Romdex.Plugins.DatFolder.Retool.SystemKey("Magnavox - Odyssey2"));
        Assert.NotEqual(Romdex.Plugins.DatFolder.Retool.SystemKey("Nintendo - Nintendo 3DS (Digital)"), Romdex.Plugins.DatFolder.Retool.SystemKey("Nintendo - Nintendo 3DS"));
    }

    [Fact]
    public async Task One_that_fails_doesnt_stop_the_rest()
    {
        _site.Dats["Nintendo - Game Boy"] = Dat("Nintendo - Game Boy", "2026.11.14");
        var libretro = Create(["Nintendo - Game Boy", "Nintendo - Nothing"]);

        await Assert.ThrowsAsync<HttpRequestException>(() => libretro.FetchAsync(default));
        Assert.Single(await libretro.ListSystemsAsync(default));
        Assert.Equal(HealthStatus.Degraded, (await libretro.CheckHealthAsync(default)).Status);
        Assert.Equal(HealthStatus.Unhealthy, (await Create([]).CheckHealthAsync(default)).Status);
    }

    [Fact]
    public void Each_groups_versions_read_as_dates_and_ones_DATs_are_one_system()
    {
        DateTimeOffset On(int day) => new(2026, 11, day, 0, 0, 0, TimeSpan.Zero);
        IndexSystem Dat(string version, DateTimeOffset? date = null) => new("x", "x", DatSources.Other, version, date);
        Assert.Equal(On(13).AddHours(7).AddMinutes(15).AddSeconds(30), DatVersions.DateOf(Dat("20261113-071530")));
        Assert.Equal(On(9).AddHours(22).AddMinutes(3).AddSeconds(17), DatVersions.DateOf(Dat("2026-11-09 22-03-17")));
        Assert.Equal(On(14), DatVersions.DateOf(Dat("2026.11.14")));
        Assert.Equal(On(2), DatVersions.DateOf(Dat("v1.2", On(2))));
        Assert.Null(DatVersions.DateOf(Dat("v1.2")));

        // Headered or not, and however it's punctuated, a DAT is of the same system; digital releases aren't.
        Assert.Equal(DatVersions.SystemKey("Nintendo - Nintendo Entertainment System (Headered)"), DatVersions.SystemKey("Nintendo - Nintendo Entertainment System"));
        Assert.Equal(DatVersions.SystemKey("NEC - PC Engine - TurboGrafx-16"), DatVersions.SystemKey("NEC - PC Engine - TurboGrafx 16"));
        Assert.Equal(DatVersions.SystemKey("Nintendo - Nintendo 3DS (Decrypted)"), DatVersions.SystemKey("Nintendo - Nintendo 3DS"));
        Assert.NotEqual(DatVersions.SystemKey("Nintendo - Nintendo 3DS (Digital)"), DatVersions.SystemKey("Nintendo - Nintendo 3DS"));
        Assert.Equal(DatVersions.SystemKey("Test - Console"), DatVersions.SystemKey("Test - Console"));
    }

    [Fact]
    public void Its_systems_are_ticked_from_a_list()
    {
        var schema = new LibretroDatsFactory().Manifest.SettingsSchema!.Value.GetProperty("properties").GetProperty("systems");
        var offered = schema.GetProperty("items").GetProperty("oneOf").EnumerateArray().ToList();
        Assert.Equal(LibretroDatsFactory.Offered.Count, offered.Count);
        Assert.Contains(offered, o => o.GetProperty("const").GetString() == "Nintendo - Game Boy" && o.GetProperty("title").GetString() == "Game Boy");
        Assert.All(schema.GetProperty("default").EnumerateArray(), d => Assert.Contains(d.GetString()!, LibretroDatsFactory.Offered));
    }

    private LibretroDats Create(string[] systems, int days = 7, string? cloneLists = null)
    {
        if (days == 0)
            File.Delete(Path.Combine(_folder, "state.json"));
        return (LibretroDats)new LibretroDatsFactory().Create(new PluginContext
        {
            Settings = JsonSerializer.SerializeToElement(new { systems, everyDays = Math.Max(days, 1), baseUrl = "https://libretro.test/no-intro", cloneLists = false }),
            HttpClient = new HttpClient(_site),
            LoggerFactory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            DataDirectory = _folder,
        }) is var plugin && cloneLists is null ? (LibretroDats)plugin
            : new LibretroDats(new HttpClient(_site), _folder, "https://libretro.test/no-intro", systems, TimeSpan.FromDays(7), cloneLists);
    }

    private static string Dat(string system, string version, string? games = null) => $"""
        clrmamepro (
        	name "{system}"
        	description "{system}"
        	version "{version}"
        	homepage "http://github.com/robloach/libretro-dats"
        )

        {games ?? """game ( name "Made Up (USA)" region "USA" rom ( name "Made Up (USA).gb" size 1 crc 00000001 ) )"""}
        """;

    private sealed class FakeLibretro : HttpMessageHandler
    {
        public Dictionary<string, string> Dats { get; } = [];
        public Dictionary<string, string> Files { get; } = [];
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            Requests.Add(path);
            var name = path.StartsWith("/no-intro/", StringComparison.Ordinal) && path.EndsWith(".dat", StringComparison.Ordinal) ? path[10..^4] : null;
            return Task.FromResult(name is not null && Dats.TryGetValue(name, out var body) || Files.TryGetValue(path, out body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
