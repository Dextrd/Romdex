using System.Net;
using System.Text.Json;
using Romdex.Contracts.Plugins;
using Romdex.Contracts.Plugins.Players;

namespace Romdex.Plugins.EmulatorJs;

/// <summary>Entry point for the EmulatorJS player.</summary>
public sealed class EmulatorJsFactory : IPluginFactory
{
    public const string PluginId = "romdex.player.emulatorjs";

    public const string UrlSetting = "url";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "EmulatorJS",
        Version = "0.1.0",
        Type = PluginType.Player,
        ContractVersion = ContractVersions.Player.ToString(),
        Description = "Plays games in the browser with EmulatorJS's libretro cores, from the emulatorjs container: the NES to the PlayStation and the PSP.",
        Homepage = "https://emulatorjs.org",
        Access = new PluginAccess { Network = ["*"] },
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "required": ["url"],
              "properties": {
                "url": {
                  "type": "string",
                  "default": "http://emulatorjs:8080",
                  "title": "Address",
                  "description": "Where Romdex reaches the emulatorjs container. Browsers never go there: Romdex passes its files on."
                }
              }
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context)
    {
        var url = context.Settings.TryGetProperty(UrlSetting, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        return new EmulatorJs(context.HttpClient, url);
    }
}

/// <summary>
/// EmulatorJS: libretro cores built for the browser. It's GPL, so it runs from a container of its own, which only the
/// browser loads; this plugin only knows which core plays which system, and writes the page that starts one.
/// </summary>
public sealed class EmulatorJs(HttpClient http, string url) : IPlayer
{
    private readonly string _url = url.TrimEnd('/');

    public Uri? FilesUrl => Uri.TryCreate(_url + "/", UriKind.Absolute, out var files) ? files : null;

    public PlayerSupport? Supports(PlayerSystem system) => Cores.For(system) is { } core
        ? new PlayerSupport(core.Bios?.Files, core.Bios?.Required ?? false, core.Threads)
        : null;

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (FilesUrl is null)
            return new HealthReport(HealthStatus.Unhealthy, "Give the emulatorjs container's address, such as http://emulatorjs:8080.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await http.GetAsync($"{_url}/data/version.json", timeout.Token);
            return response.IsSuccessStatusCode
                ? HealthReport.Healthy()
                : new HealthReport(HealthStatus.Unhealthy, $"The emulatorjs container at {_url} answered {(int)response.StatusCode}.");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Nothing answers at {_url}. Is the emulatorjs container running?");
        }
    }

    /// <summary>EmulatorJS set up for one game, filling the window.</summary>
    public string WritePage(PlayerGame game)
    {
        var core = Cores.For(game.System) ?? throw new InvalidOperationException($"EmulatorJS doesn't play {game.System.Name}.");
        static string Js(string? value) => JsonSerializer.Serialize(value);
        var name = WebUtility.HtmlEncode(game.Name);
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{name}} · Romdex</title>
            <style>
              html, body { margin: 0; height: 100%; background: #000; color: #ddd; font: 14px system-ui, sans-serif; }
              body { display: flex; flex-direction: column; }
              header { display: flex; gap: 12px; align-items: center; padding: 8px 16px; background: #15171c; }
              header a { color: #8ab4ff; text-decoration: none; }
              header span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
              main { flex: 1; min-height: 0; }
              #game { width: 100%; height: 100%; }
              .unplayable { max-width: 36em; margin: 15vh auto; padding: 0 16px; line-height: 1.5; }
            </style>
            </head>
            <body>
            <header><a href="{{WebUtility.HtmlEncode(game.BackUrl)}}">← Back</a><span>{{name}}</span></header>
            <main><div id="game"></div></main>
            <script>
              EJS_player = "#game";
              EJS_core = {{Js(core.Name)}};
              EJS_gameName = {{Js(game.Name)}};
              EJS_gameUrl = {{Js(game.GameUrl)}};
              EJS_biosUrl = {{Js(game.BiosUrl ?? "")}};
              EJS_pathtodata = {{Js(game.FilesPath + "data/")}};
              EJS_startOnLoaded = true;
              // Its own cache of games is keyed by link, and each link here is signed afresh, so it would never be used:
              // it would only keep another copy of each game, in memory while it's stored, and on disk after.
              EJS_disableDatabases = true;
              EJS_threads = {{(core.Threads ? "true" : "false")}};
              // The emulator draws with WebGL: without it, it only says it failed to start, so say why instead.
              const canvas = document.createElement("canvas");
              if (canvas.getContext("webgl2") || canvas.getContext("webgl")) {
                const loader = document.createElement("script");
                loader.src = EJS_pathtodata + "loader.js";
                document.body.append(loader);
              } else {
                document.getElementById("game").outerHTML = '<p class="unplayable">This browser can\'t play games: the emulator draws with WebGL, '
                  + "which it doesn't have, or has turned off. Most browsers have it; check that hardware acceleration is on.</p>";
              }
            </script>
            </body>
            </html>
            """;
    }
}

/// <summary>
/// The BIOS files a core accepts. Each is known by its MD5, as the core's documentation lists it, so a library BIOS is
/// used only when it really is one of them, whatever its DAT calls it.
/// </summary>
/// <param name="Required">The core can't start a game without one; otherwise it falls back on its own stand-in.</param>
/// <param name="Files">The file name the core looks for, by the MD5 of what it should hold.</param>
internal sealed record CoreBios(bool Required, IReadOnlyDictionary<string, string> Files);

/// <summary>How EmulatorJS plays one system.</summary>
/// <param name="Name">EmulatorJS's name for the system, its <c>EJS_core</c>.</param>
/// <param name="Bios">The BIOS it uses, if any.</param>
/// <param name="Threads">The core needs threads, so a page that isn't cross-origin isolated can't run it.</param>
internal sealed record EmulatorCore(string Name, CoreBios? Bios = null, bool Threads = false);

/// <summary>Which EmulatorJS core plays each system, by Romdex's short names for them.</summary>
internal static class Cores
{
    // From the libretro cores' documentation of their system files.
    private static readonly CoreBios PlayStation = new(false, new Dictionary<string, string>
    {
        ["490f666e1afb15b7362b406ed1cea246"] = "scph5501.bin",
        ["32736f17079d0b2b7024407c39bd3050"] = "scph5502.bin",
        ["8dd7d5296a650fac7319bce665a6a53c"] = "scph5500.bin",
        ["924e392ed05558ffdb115408c263dccf"] = "scph1001.bin",
        ["1e68c231d0896b7eadcad1d7d8e76129"] = "scph7001.bin",
        ["6e3735ff4c7dc899ee98981385f6f3d0"] = "scph101.bin",
    });

    private static readonly CoreBios MegaCd = new(true, new Dictionary<string, string>
    {
        ["2efd74e3232ff260e371b99f84024f7f"] = "bios_CD_U.bin",
        ["e66fa1dc5820d254611fdcdba0662372"] = "bios_CD_E.bin",
        ["278a9397d192149e84e820ac621a8edd"] = "bios_CD_J.bin",
    });

    private static readonly CoreBios GameBoyAdvance = new(false, new Dictionary<string, string> { ["a860e8c0b6d573d191e4ec7db1b1e4f6"] = "gba_bios.bin" });

    private static readonly CoreBios DiskSystem = new(true, new Dictionary<string, string> { ["ca30b50f880eb660a320674ed365ef7a"] = "disksys.rom" });

    private static readonly Dictionary<string, EmulatorCore> ByShort = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NES"] = new("nes"),
        ["FDS"] = new("nes", DiskSystem),
        ["SNES"] = new("snes"),
        ["N64"] = new("n64"),
        ["GB"] = new("gb"),
        ["GBC"] = new("gb"),
        ["GBA"] = new("gba", GameBoyAdvance),
        ["VB"] = new("vb"),
        ["DS"] = new("nds"),
        ["SMS"] = new("segaMS"),
        ["GG"] = new("segaGG"),
        ["MD"] = new("segaMD"),
        ["32X"] = new("sega32x"),
        ["MCD"] = new("segaCD", MegaCd),
        ["PS1"] = new("psx", PlayStation),
        ["PSP"] = new("psp", Threads: true),
        ["PCE"] = new("pce"),
        ["NGP"] = new("ngp"),
        ["NGPC"] = new("ngp"),
        ["WS"] = new("ws"),
        ["WSC"] = new("ws"),
        ["2600"] = new("atari2600"),
        ["7800"] = new("atari7800"),
        ["Lynx"] = new("lynx"),
        ["Jaguar"] = new("jaguar"),
        ["CV"] = new("coleco"),
    };

    // Made-up systems the tests play, by their DAT names.
    private static readonly List<(string Name, EmulatorCore Core)> Registered = [];

    /// <summary>Adds a system by its DAT name, ahead of the rest, for tests, whose made-up BIOS files no core knows.</summary>
    internal static void Register(string name, EmulatorCore core) => Registered.Insert(0, (name, core));

    public static EmulatorCore? For(PlayerSystem system) =>
        Registered.Where(s => system.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase)
                              || system.Name.StartsWith(s.Name + " (", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Core).FirstOrDefault()
        ?? (system.Short is { } shortName ? ByShort.GetValueOrDefault(shortName) : null);
}
