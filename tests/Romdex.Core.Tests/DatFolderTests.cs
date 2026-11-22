using System.IO.Compression;
using Romdex.Contracts.Plugins;
using Romdex.Contracts.Plugins.Index;
using Romdex.Core.Tests.Support;
using Romdex.Plugins.DatFolder;

namespace Romdex.Core.Tests;

/// <summary>The built-in catalogue: Logiqx and clrmamepro DATs in a folder, plain or zipped.</summary>
public sealed class DatFolderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "romdex-tests", Guid.NewGuid().ToString("N"));
    private static readonly string Fixture = Path.Combine(TestRepository.Dats, "test-handheld.dat");

    public DatFolderTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_header_says_which_system_and_whose_DAT_it_is()
    {
        using var stream = File.OpenRead(Fixture);
        var system = Logiqx.ReadHeader(stream)!;

        Assert.Equal("Test - Handheld", system.Id);
        Assert.Equal(DatSources.NoIntro, system.Source);
        Assert.Equal("20261101-000000", system.Version);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), system.Date);
    }

    [Fact]
    public void Dumps_keep_their_hashes_clones_serials_and_BIOS()
    {
        using var stream = File.OpenRead(Fixture);
        var dumps = Logiqx.ReadDumps(stream, CancellationToken.None);

        Assert.Equal(3, dumps.Count);
        Assert.True(dumps[0].IsBios);
        var hopper = dumps[1];
        Assert.False(hopper.IsBios);
        Assert.Null(hopper.CloneOf);
        Assert.Null(hopper.Description);
        Assert.Equal("DMG-HHPW", hopper.Serial);
        var file = Assert.Single(hopper.Files);
        Assert.Equal(32768, file.Size);
        Assert.Equal("1234abcd", file.Crc32);
        Assert.Equal("89abcdef0123456789abcdef0123456789abcdef", file.Sha1);
        Assert.Equal("Homebrew Hopper (World)", dumps[2].CloneOf);
        // Hashes are kept lower case, whatever case the DAT wrote them in.
        Assert.Equal("0a1b2c3d", dumps[0].Files[0].Crc32);
    }

    // As libretro keeps No-Intro's DATs.
    private const string ClrMamePro = """
        clrmamepro (
        	name "Test - Handheld"
        	description "Test - Handheld"
        	version "2026.11.14"
        	homepage "http://github.com/robloach/libretro-dats"
        )

        game (
        	name "[BIOS] Test Boot (World)"
        	rom ( name "[BIOS] Test Boot (World).gb" size 256 crc 0A1B2C3D )
        )

        game (
        	name "Hopper Quest (Japan) (En,Ja)"
        	region "Japan"
        	language "En,Ja"
        	serial "DMG-HQJ"
        	releaseyear 2026
        	rom ( name "Hopper Quest (Japan) (En,Ja).gb" size 32768 crc 1234ABCD md5 0123456789ABCDEF0123456789ABCDEF sha1 89ABCDEF0123456789ABCDEF0123456789ABCDEF )
        )

        game (
        	name "Two Part (World)"
        	cloneof "Hopper Quest (Japan) (En,Ja)"
        	rom ( name "Two Part (World) (Disk 1).gb" size 1 crc 00000001 serial "TP-1" )
        	rom ( name "Two Part (World) (Disk 2).gb" size 2 crc 00000002 )
        )
        """;

    [Fact]
    public void A_clrmamepro_DAT_reads_like_a_Logiqx_one()
    {
        var system = Dats.ReadHeader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ClrMamePro)))!;
        Assert.Equal("Test - Handheld", system.Name);
        Assert.Equal("2026.11.14", system.Version);
        Assert.Equal(DatSources.Other, system.Source);

        var dumps = Dats.ReadDumps(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ClrMamePro)), default);
        Assert.Equal(["[BIOS] Test Boot (World)", "Hopper Quest (Japan) (En,Ja)", "Two Part (World)"], dumps.Select(d => d.Name));
        Assert.True(dumps[0].IsBios);
        var hopper = dumps[1];
        Assert.Equal("DMG-HQJ", hopper.Serial);
        Assert.Null(hopper.CloneOf);
        var file = Assert.Single(hopper.Files);
        Assert.Equal(32768, file.Size);
        Assert.Equal("1234abcd", file.Crc32);
        Assert.Equal("0123456789abcdef0123456789abcdef", file.Md5);
        Assert.Equal("89abcdef0123456789abcdef0123456789abcdef", file.Sha1);
        Assert.Equal("Hopper Quest (Japan) (En,Ja)", dumps[2].CloneOf);
        Assert.Equal([1L, 2L], dumps[2].Files.Select(f => f.Size));
        Assert.Equal("TP-1", dumps[2].Serial);

        // Neither format: not a DAT.
        Assert.Null(Dats.ReadHeader(new MemoryStream("Just some notes"u8.ToArray())));
    }

    [Fact]
    public async Task The_folder_reads_clrmamepro_DATs_too_and_the_newer_wins_whichever_format()
    {
        File.WriteAllText(Path.Combine(_folder, "libretro.dat"), ClrMamePro);
        var plugin = Create();
        Assert.Equal("2026.11.14", Assert.Single(await plugin.ListSystemsAsync(default)).Version);
        Assert.Equal(3, (await plugin.GetSystemAsync("Test - Handheld", default))!.Dumps.Count);

        // The same system in Logiqx, made on the 1st: libretro's, made on the 14th, is newer, though its version sorts first.
        File.Copy(Fixture, Path.Combine(_folder, "nointro.dat"));
        Assert.Equal("2026.11.14", Assert.Single(await plugin.ListSystemsAsync(default)).Version);
        File.WriteAllText(Path.Combine(_folder, "newer.dat"), File.ReadAllText(Fixture).Replace("20261101-000000", "20261115-000000"));
        Assert.Equal("20261115-000000", Assert.Single(await plugin.ListSystemsAsync(default)).Version);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task A_folder_Romdex_may_not_read_says_so_rather_than_looking_empty()
    {
        File.Copy(Fixture, Path.Combine(_folder, "handheld.dat"));
        File.SetUnixFileMode(_folder, UnixFileMode.None);
        try
        {
            var plugin = Create();
            var health = await plugin.CheckHealthAsync(default);
            Assert.Equal(HealthStatus.Unhealthy, health.Status);
            Assert.Contains("isn't allowed to read", health.Message);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => plugin.ListSystemsAsync(default));
        }
        finally
        {
            File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task The_folder_keeps_the_newest_DAT_per_system_zipped_or_not()
    {
        var plugin = Create();
        Assert.Equal(HealthStatus.Degraded, (await plugin.CheckHealthAsync(default)).Status);

        File.Copy(Fixture, Path.Combine(_folder, "old.dat"));
        var newer = File.ReadAllText(Fixture).Replace("20261101-000000", "20261108-000000").Replace("Homebrew Hopper (World) (Rev 1)", "Homebrew Hopper (World) (Rev 2)");
        using (var zip = ZipFile.Open(Path.Combine(_folder, "newer.zip"), ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("Test - Handheld (20261108-000000).dat").Open()))
            writer.Write(newer);
        File.WriteAllText(Path.Combine(_folder, "notes.xml"), "<notes>Not a DAT</notes>");

        var system = Assert.Single(await plugin.ListSystemsAsync(default));
        Assert.Equal("20261108-000000", system.Version);
        var detail = (await plugin.GetSystemAsync(system.Id, default))!;
        Assert.Contains(detail.Dumps, d => d.Name == "Homebrew Hopper (World) (Rev 2)");
        Assert.Null(await plugin.GetSystemAsync("Nothing - Here", default));

        var health = await plugin.CheckHealthAsync(default);
        Assert.Equal(HealthStatus.Degraded, health.Status);
        Assert.Contains("notes.xml", health.Message);
    }

    [Fact]
    public async Task A_missing_folder_is_unhealthy()
    {
        var health = await new DatFolder(Path.Combine(_folder, "nowhere")).CheckHealthAsync(default);
        Assert.Equal(HealthStatus.Unhealthy, health.Status);
    }

    private IIndexPlugin Create() => new DatFolder(_folder);
}
