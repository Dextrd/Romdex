using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Romdex.Client;
using Romdex.Core.Api;
using Romdex.Core.Apps;
using Romdex.Core.Catalogue;
using Romdex.Core.Plugins;
using Romdex.Core.Play;
using Romdex.Core.Tests.Support;

namespace Romdex.Core.Tests;

/// <summary>
/// Playing games the library has in the browser, their covers, and opening them in RomM. EmulatorJS, Hasheous and RomM
/// are stand-ins; every dump is made up.
/// </summary>
public sealed class PlayTests : IDisposable
{
    private const string GameBoy = "Nintendo - Game Boy";
    private const string PlayStation = "Sony - PlayStation";
    private const string GameCube = "Nintendo - GameCube";
    private const string TestDisc = "Test - Disc With A Known BIOS";

    private readonly MadeUpDats _dats = new();
    private readonly FakeServices _services = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly RomdexClient _api;

    public PlayTests()
    {
        AppSync.Every = TimeSpan.FromMilliseconds(50);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Romdex:DataPath", _dats.Data)
            .UseSetting("Romdex:LibraryPath", _dats.Roms)
            .UseSetting("Romdex:DatPath", _dats.Dats)
            .UseSetting("Romdex:CatalogueRefreshHours", "0")
            .UseSetting("Romdex:DownloadCheckMinutes", "0")
            .UseSetting("Romdex:EmulatorUrl", "http://emulatorjs:8080")
            .ConfigureServices(services =>
            {
                services.Configure<PluginHttpOptions>(o =>
                {
                    o.PrimaryHandler = () => _services;
                    o.RateLimit = false;
                });
                services.AddHttpClient(PlayEndpoints.EmulatorClient).ConfigurePrimaryHttpMessageHandler(() => _services);
                services.AddHttpClient(ArtLookupTask.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _services);
            }));
        _api = new RomdexClient(_factory.CreateSignedInClient());
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dats.Dispose();
    }

    [Fact]
    public async Task Games_the_library_has_play_through_signed_links()
    {
        var pond = _dats.Dump(GameBoy, "Pixel Pond (USA)", [".gb"]);
        var harbor = _dats.Dump(PlayStation, "Harbor Lights (USA)", [".cue", ".bin", ".bin"]);
        var bios = _dats.Dump(PlayStation, "[BIOS] PlayStation (USA) (v4.1)", [".bin"]);
        var lights = _dats.Dump(TestDisc, "Harbor Lights (USA)", [".cue", ".bin", ".bin"]);
        // In a DAT of BIOS files of its own, as Redump's are, which doesn't mark it as one.
        var testBios = _dats.Dump(TestDisc + " - BIOS Images", "Test Disc (USA) (v1.0)", [".bin"]);
        Emulators.Register(TestDisc, new Emulator("psx", new EmulatorBios(false, new Dictionary<string, string> { [Md5(testBios[0].Data)] = "scph5501.bin" })));
        var garden = _dats.Dump(GameCube, "Glass Garden (USA)", [".iso"]);
        _dats.Dump(GameBoy, "Moss Maze (Japan)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("gb/Pixel Pond (USA).gb", pond[0].Data);
        foreach (var (name, data) in harbor)
            _dats.Put($"psx/Harbor Lights (USA)/{name}", data);
        _dats.Zip("psx/bios.zip", bios[0]);
        foreach (var (name, data) in lights)
            _dats.Put($"test/Harbor Lights (USA)/{name}", data);
        _dats.Zip("test/bios.zip", testBios[0]);
        _dats.Put("ngc/Glass Garden (USA).iso", garden[0].Data);
        await SetUpAsync();

        // A cartridge goes as its file, through a link that needs no sign-in, only its signature.
        var pondPlay = await PlayAsync("Pixel Pond");
        Assert.NotNull(pondPlay.PlayerUrl);
        Assert.Null(pondPlay.Bios);
        Assert.Null(pondPlay.App);
        using var anyone = _factory.CreateClient();
        var page = await anyone.GetStringAsync(pondPlay.PlayerUrl);
        Assert.Contains("EJS_core = \"gb\"", page);
        Assert.Contains("EJS_disableDatabases = true", page);
        Assert.Contains("EJS_pathtodata = \"../../../../emulator/data/\"", page);
        Assert.Contains("\"loader.js\"", page);
        var gameUrl = Between(page, "EJS_gameUrl = \"", "\"");
        Assert.Equal(pond[0].Data, await anyone.GetByteArrayAsync(Under(pondPlay.PlayerUrl!, gameUrl)));
        // The player asks its size first, to see whether the copy it kept is still the one.
        using (var head = await anyone.SendAsync(new HttpRequestMessage(HttpMethod.Head, Under(pondPlay.PlayerUrl!, gameUrl))))
            Assert.Equal(pond[0].Data.Length, head.Content.Headers.ContentLength);
        var forged = pondPlay.PlayerUrl!.Replace("sig=", "sig=x");
        Assert.Equal(HttpStatusCode.Forbidden, (await anyone.GetAsync(forged)).StatusCode);

        // A disc in several files goes as one zip, by the DAT's names. A BIOS no core knows by its MD5 isn't used, as
        // this made-up one isn't, whatever its DAT says: the core plays with its own stand-in.
        var harborPlay = await PlayAsync("Harbor Lights", PlayStation);
        Assert.Null(harborPlay.Bios);
        page = await anyone.GetStringAsync(harborPlay.PlayerUrl);
        using (var zip = new ZipArchive(new MemoryStream(await anyone.GetByteArrayAsync(Under(harborPlay.PlayerUrl!, Between(page, "EJS_gameUrl = \"", "\""))))))
            Assert.Equal(harbor.Select(f => f.Name).Order(), zip.Entries.Select(e => e.FullName).Order());
        Assert.Contains("EJS_biosUrl = \"\"", page);
        var system = (await _api.ListSystemsAsync()).Single(s => s.Name == PlayStation);
        var need = (await _api.ListBiosAsync(system.Id)).Needed;
        Assert.NotNull(need);
        Assert.False(need.Ready);
        Assert.False(need.Required);
        Assert.Contains("scph5501.bin", need.Files);

        // One it knows goes with the game, out of its zip, by the name the core looks for.
        var lightsPlay = await PlayAsync("Harbor Lights", TestDisc);
        Assert.Equal("scph5501.bin", lightsPlay.Bios);
        page = await anyone.GetStringAsync(lightsPlay.PlayerUrl);
        Assert.Equal(testBios[0].Data, await anyone.GetByteArrayAsync(Under(lightsPlay.PlayerUrl!, Between(page, "EJS_biosUrl = \"", "\""))));

        // The GameCube isn't the browser's to play, and nothing plays what the library hasn't got.
        Assert.Equal($"The browser's emulator doesn't play {GameCube} games.", (await PlayAsync("Glass Garden")).Unplayable);
        Assert.Equal("The library doesn't have all of it.", (await PlayAsync("Moss Maze")).Unplayable);

        // EmulatorJS's own files come from its container, through the core.
        Assert.Equal("// loader", await anyone.GetStringAsync("emulator/data/loader.js"));
    }

    [Fact]
    public async Task Covers_come_from_hash_lookups_and_stay()
    {
        var pond = _dats.Dump(GameBoy, "Pixel Pond (USA)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("gb/Pixel Pond (USA).gb", pond[0].Data);
        await SetUpAsync();
        _services.Hasheous[Md5(pond[0].Data)] = "Pixel Pond Deluxe";

        await _api.AddPluginAsync(new AddPlugin("romdex.index.hasheous"));
        var title = await WaitAsync(async () => (await _api.ListAllTitlesAsync(have: true)).Items.Single(), t => t.Cover is not null);

        var detail = (await _api.GetTitleAsync(title.Id))!;
        Assert.Equal("Pixel Pond Deluxe", detail.KnownAs);
        Assert.Equal(FakeServices.Png, await _factory.CreateSignedInClient().GetByteArrayAsync(detail.Cover));
        Assert.Equal(1, _services.Lookups);

        // Looked up once: another scan doesn't ask again.
        await RunAsync(await _api.ScanLibraryAsync());
        Assert.Equal(1, _services.Lookups);
    }

    [Fact]
    public async Task Games_open_in_romm_once_it_has_found_them()
    {
        var pond = _dats.Dump(GameBoy, "Pixel Pond (USA)", [".gb"]);
        _dats.WriteDats();
        _dats.Put("gb/Pixel Pond (USA).gb", pond[0].Data);
        await SetUpAsync();
        await _api.AddPluginAsync(new AddPlugin("romdex.connector.romm",
            Settings: JsonSerializer.SerializeToElement(new { url = "http://romm:8080", token = "rmm_test", publicUrl = "https://romm.example" })));

        var play = await PlayAsync("Pixel Pond");
        Assert.Equal("RomM", play.App);

        // RomM hasn't scanned it yet: it's asked to, and then has it.
        var link = await _api.OpenInAppAsync(play.DumpId);
        Assert.Null(link.Url);
        Assert.Contains("asked to scan", link.Waiting);
        Assert.Equal(["gb"], _services.Scanned);
        link = await _api.OpenInAppAsync(play.DumpId);
        Assert.Equal("https://romm.example/rom/7", link.Url);
        Assert.Equal("Bearer rmm_test", _services.RommAuthorization);
    }

    private async Task SetUpAsync()
    {
        await RunAsync(await _api.RefreshCatalogueAsync());
        await RunAsync(await _api.ScanLibraryAsync());
    }

    private async Task<PlayInfo> PlayAsync(string name, string? system = null)
    {
        var systems = (await _api.ListSystemsAsync()).ToDictionary(s => s.Id, s => s.Name);
        var title = (await _api.ListAllTitlesAsync(q: name)).Items.Single(t => system is null || systems[t.SystemId] == system);
        var detail = (await _api.GetTitleAsync(title.Id))!;
        return (await _api.GetPlayAsync(detail.Dumps[0].Id))!;
    }

    // A link on the player page, which is relative to the page.
    private static string Under(string playerUrl, string relative) =>
        playerUrl[..playerUrl.LastIndexOf('/')] + "/" + relative.Replace("\\u0026", "&");

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal) + start.Length;
        return text[from..text.IndexOf(end, from, StringComparison.Ordinal)];
    }

    private static string Md5(byte[] data) => Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(data));

    private static async Task<T> WaitAsync<T>(Func<Task<T>> get, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var value = await get();
            if (done(value))
                return value;
            Assert.True(DateTime.UtcNow < deadline, $"Still waiting: {value}");
            await Task.Delay(100);
        }
    }

    private async Task RunAsync(TaskInfo task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (task.State is TaskState.Queued or TaskState.Running && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            task = (await _api.GetTaskAsync(task.Id))!;
        }
        Assert.True(task.State == TaskState.Succeeded, $"{task.Kind}: {task.State} {task.Error} {task.Progress}");
    }

    /// <summary>EmulatorJS's container, Hasheous and RomM, as far as Romdex uses them.</summary>
    private sealed class FakeServices : HttpMessageHandler
    {
        public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

        /// <summary>Game names Hasheous knows, by MD5.</summary>
        public Dictionary<string, string> Hasheous { get; } = [];

        public int Lookups { get; private set; }
        public List<string> Scanned { get; } = [];
        public string? RommAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            var path = url.AbsolutePath;
            switch (url.Host)
            {
                case "emulatorjs":
                    return path switch
                    {
                        "/data/version.json" => Json("""{"version":"4.2.3"}"""),
                        "/data/loader.js" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("// loader", Encoding.UTF8, "text/javascript") },
                        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                    };
                case "hasheous.org" when path == "/api/v1/Lookup/ByHash":
                {
                    Lookups++;
                    var asked = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.GetProperty("md5").GetString()!;
                    return Hasheous.TryGetValue(asked, out var name)
                        ? Json(JsonSerializer.Serialize(new
                        {
                            name,
                            metadata = new[] { new { id = "42", status = "Mapped", source = "IGDB" } },
                            attributes = new object[] { new { attributeType = "ImageId", attributeName = "Logo", value = "ABC123" } },
                        }))
                        : new HttpResponseMessage(HttpStatusCode.NotFound);
                }
                case "hasheous.org" when path == "/api/v1/images/ABC123":
                    // Hasheous says JPEG for what's a PNG.
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) { Headers = { ContentType = new("image/jpeg") } } };
                case "romm":
                    RommAuthorization = request.Headers.Authorization?.ToString();
                    if (path == "/api/tasks/scan")
                    {
                        var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement;
                        Scanned.AddRange(body.GetProperty("platform_fs_slugs").EnumerateArray().Select(e => e.GetString()!));
                        return Json("""{"task_id":"1","status":"queued"}""", HttpStatusCode.Accepted);
                    }
                    if (path == "/api/platforms")
                        return Json("""[{"id":1,"fs_slug":"gb"}]""");
                    if (path == "/api/roms")
                        return Json(Scanned.Count == 0 ? """{"items":[]}""" : """{"items":[{"id":7,"full_path":"roms/gb/Pixel Pond (USA).gb"}]}""");
                    if (path == "/api/roms/by-hash")
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    break;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
