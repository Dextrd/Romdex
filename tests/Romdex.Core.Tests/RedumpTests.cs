using System.IO.Compression;
using System.Net;
using System.Text;
using Romdex.Contracts.Plugins;
using Romdex.Plugins.Redump;

namespace Romdex.Core.Tests;

/// <summary>Fetching Redump's DATs: from /datfile and /bios, zipped or not, kept only when newer, checked every few days.</summary>
public sealed class RedumpTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "romdex-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeRedump _site = new();

    public RedumpTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Fetches_each_system_and_its_BIOS_then_waits_until_its_due()
    {
        _site.Dats["/datfile/PSX"] = Zip("Sony - PlayStation", "2026-11-01 10-00-00");
        _site.Dats["/bios/PSX"] = Zip("Sony - PlayStation - BIOS Images", "2026-06-16 00-00-00");
        _site.Dats["/datfile/SS"] = Zip("Sega - Saturn", "2026-10-01 10-00-00");
        var redump = Create("PSX, ss", bios: true);

        var fetched = await redump.FetchAsync(default);
        Assert.Equal(["Sega - Saturn", "Sony - PlayStation", "Sony - PlayStation - BIOS Images"], fetched.Select(s => s.Name).Order());
        Assert.Equal(3, (await redump.ListSystemsAsync(default)).Count);
        Assert.Equal(HealthStatus.Healthy, (await redump.CheckHealthAsync(default)).Status);

        // Not due yet: nothing is asked for.
        _site.Requests.Clear();
        Assert.Empty(await redump.FetchAsync(default));
        Assert.Empty(_site.Requests);
    }

    [Fact]
    public async Task A_DAT_that_comes_unzipped_is_kept_as_it_came()
    {
        // As Redump sends the PlayStation 2's BIOS DAT.
        _site.Dats["/datfile/PS2"] = Zip("Sony - PlayStation 2", "2026-11-01 10-00-00");
        _site.Dats["/bios/PS2"] = Encoding.UTF8.GetBytes(Dat("Sony - PlayStation 2 - BIOS Images", "2026-10-10 00-00-00"));
        var redump = Create("PS2", bios: true);

        Assert.Equal(["Sony - PlayStation 2", "Sony - PlayStation 2 - BIOS Images"], (await redump.FetchAsync(default)).Select(s => s.Name).Order());
        Assert.Equal(2, (await redump.ListSystemsAsync(default)).Count);
        Assert.Equal(HealthStatus.Healthy, (await redump.CheckHealthAsync(default)).Status);

        // Zipped next time, it replaces the one kept, not sits beside it.
        _site.Dats["/bios/PS2"] = Zip("Sony - PlayStation 2 - BIOS Images", "2026-11-11 00-00-00");
        Assert.Single(await Create("PS2", days: 0, bios: true).FetchAsync(default));
        Assert.Equal(["PS2-bios.zip", "PS2.zip"], Directory.GetFiles(_folder, "PS2*").Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Retools_clone_list_makes_one_title_of_a_game_under_other_names()
    {
        _site.Dats["/datfile/PS2"] = Zip("Sony - PlayStation 2", "2026-11-01 10-00-00", """
            <game name="Crash Twinsanity (Europe, Australia) (En,Fr)"><rom name="a.iso" size="1" crc="00000001"/></game>
            <game name="Crash Bandicoot 5 - Eeee Crash to Cortex no Yabou! (Japan)"><rom name="b.iso" size="1" crc="00000002"/></game>
            <game name="Gadget Racers (Europe)"><rom name="c.iso" size="1" crc="00000003"/></game>
            <game name="Penny Racers (USA)"><rom name="d.iso" size="1" crc="00000004"/></game>
            <game name="Sonic Heroes (Europe)"><rom name="e.iso" size="1" crc="00000005"/></game>
            """);
        _site.Dats["/clonelists/Sony%20-%20PlayStation%202%20%28Redump%29.json"] = Encoding.UTF8.GetBytes("""
            {
              "description": { "name": "Sony - PlayStation 2 (Redump)", "lastUpdated": "2026-05-10 17:34:33" },
              "variants": [
                { "group": "Crash Twinsanity", "titles": [ { "searchTerm": "Crash Bandicoot 5 - Eeee Crash to Cortex no Yabou!" }, { "searchTerm": "Crash Twinsanity" } ] },
                { "group": "Penny Racers", "titles": [ { "searchTerm": "Gadget Racers \\(Europe\\).*", "nameType": "regex" }, { "searchTerm": "Penny Racers" } ] },
                { "group": "Metal Slug 2", "compilations": [ { "searchTerm": "Metal Slug Anthology" } ] }
              ]
            }
            """);
        var redump = new Redump(new HttpClient(_site), _folder, "https://redump.test", ["PS2"], bios: false, TimeSpan.FromDays(7), "https://redump.test/clonelists");

        await redump.FetchAsync(default);
        var system = Assert.Single(await redump.ListSystemsAsync(default));
        Assert.Equal("2026-05-10 17:34:33", system.CloneList);
        var dumps = (await redump.GetSystemAsync(system.Id, default))!.Dumps.ToDictionary(d => d.Name, d => d.CloneOf);
        Assert.Equal("Crash Twinsanity (Europe, Australia) (En,Fr)", dumps["Crash Bandicoot 5 - Eeee Crash to Cortex no Yabou! (Japan)"]);
        Assert.Equal("Penny Racers (USA)", dumps["Gadget Racers (Europe)"]);
        Assert.Null(dumps["Crash Twinsanity (Europe, Australia) (En,Fr)"]);
        Assert.Null(dumps["Sonic Heroes (Europe)"]);

        // A list unchanged isn't written again; without one, nothing is grouped.
        Assert.Empty(await new Redump(new HttpClient(_site), _folder, "https://redump.test", ["PS2"], false, TimeSpan.Zero, "https://redump.test/clonelists").FetchAsync(default));
        File.Delete(Path.Combine(_folder, "PS2-clones.json"));
        Assert.Null(Assert.Single(await redump.ListSystemsAsync(default)).CloneList);
    }

    [Fact]
    public async Task Keeps_a_download_only_when_its_a_newer_version()
    {
        _site.Dats["/datfile/PSX"] = Zip("Sony - PlayStation", "2026-11-01 10-00-00");
        Assert.Single(await Create("PSX", days: 0).FetchAsync(default));

        // The same version again changes nothing; a newer one replaces it.
        Assert.Empty(await Create("PSX", days: 0).FetchAsync(default));
        _site.Dats["/datfile/PSX"] = Zip("Sony - PlayStation", "2026-11-09 22-03-17");
        Assert.Single(await Create("PSX", days: 0).FetchAsync(default));
        Assert.Equal("2026-11-09 22-03-17", Assert.Single(await Create("PSX").ListSystemsAsync(default)).Version);
    }

    [Fact]
    public async Task A_page_that_isnt_a_DAT_fails_the_fetch_and_keeps_what_was_there()
    {
        _site.Dats["/datfile/PSX"] = Zip("Sony - PlayStation", "2026-11-01 10-00-00");
        await Create("PSX", days: 0).FetchAsync(default);
        _site.Dats["/datfile/PSX"] = Encoding.UTF8.GetBytes("<html>Maintenance</html>");

        var redump = Create("PSX", days: 0);
        await Assert.ThrowsAsync<InvalidDataException>(() => redump.FetchAsync(default));
        Assert.Single(await redump.ListSystemsAsync(default));
        Assert.Equal(HealthStatus.Degraded, (await redump.CheckHealthAsync(default)).Status);
    }

    private Redump Create(string systems, int days = 7, bool bios = false) =>
        (Redump)new RedumpFactory().Create(new PluginContext
        {
            Settings = System.Text.Json.JsonDocument.Parse($$"""{"systems":"{{systems}}","bios":{{(bios ? "true" : "false")}},"everyDays":{{Math.Max(days, 1)}},"baseUrl":"https://redump.test"}""").RootElement,
            HttpClient = new HttpClient(_site),
            LoggerFactory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            DataDirectory = _folder,
        }) is var plugin && days == 0 ? ForceDue(plugin) : (Redump)plugin;

    // Forgets when it last fetched, so the next fetch is due at once.
    private Redump ForceDue(IPlugin plugin)
    {
        File.Delete(Path.Combine(_folder, "state.json"));
        return (Redump)plugin;
    }

    private static byte[] Zip(string system, string version, string? games = null)
    {
        var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry($"{system} - Datfile (1) ({version}).dat").Open()))
            writer.Write(Dat(system, version, games));
        return memory.ToArray();
    }

    private static string Dat(string system, string version, string? games = null) => $"""
        <?xml version="1.0"?>
        <datafile><header><name>{system}</name><description>{system}</description><version>{version}</version><homepage>redump.info</homepage></header>
        {games ?? """<game name="Made Up (USA)"><category>Games</category><rom name="Made Up (USA).cue" size="1" crc="00000001"/></game>"""}</datafile>
        """;

    private sealed class FakeRedump : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Dats { get; } = [];
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Dats.TryGetValue(request.RequestUri.AbsolutePath, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
