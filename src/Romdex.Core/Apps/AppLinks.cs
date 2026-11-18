using Microsoft.EntityFrameworkCore;
using Romdex.Client;
using Romdex.Contracts.Plugins.Connectors;
using Romdex.Core.Data;
using Romdex.Core.Plugins;

namespace Romdex.Core.Apps;

/// <summary>
/// The connected app, such as RomM: telling it about what's filed, and finding games in it. Romdex works fully without
/// one, so nothing here fails for want of it.
/// </summary>
public sealed class AppLinks(RomdexDbContext db, ConnectorPlugins connectors, ILogger<AppLinks> logger)
{
    /// <summary>The connected app's name, when it can open games.</summary>
    public async Task<string?> LaunchingAppAsync(CancellationToken ct) =>
        await connectors.GetActiveAsync(ct) is { Connector: ILaunchLink } active ? active.Instance.Name : null;

    /// <summary>Where a library file's game is in the app; asks the app to scan for it when it hasn't found it.</summary>
    public async Task<AppLink?> LinkAsync(Guid libraryFileId, CancellationToken ct)
    {
        if (await connectors.GetActiveAsync(ct) is not { Connector: ILaunchLink launch } active)
            return null;
        var name = active.Instance.Name;
        if (await db.LibraryFiles.FindAsync([libraryFileId], ct) is not { } file)
            return new AppLink(name, null, "The library no longer has it.");
        try
        {
            if (await ResolveAsync(active.Instance, active.Connector, file, ct) is { } id)
                return new AppLink(name, launch.LaunchUri(id).ToString(), null);
            await active.Connector.TriggerScanAsync([file.Path], ct);
            return new AppLink(name, null, $"{name} hasn't found it yet. It's been asked to scan for it, so try again in a minute.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Couldn't find {Path} in {App}: {Message}", file.Path, name, e.Message);
            return new AppLink(name, null, $"Couldn't reach {name}: {e.Message}");
        }
    }

    /// <summary>The app's ID for a file, as remembered or by asking the app; null when the app doesn't have it.</summary>
    public async Task<string?> ResolveAsync(PluginInstance instance, IConnector connector, LibraryFile file, CancellationToken ct)
    {
        if (file.AppConnectorId == instance.Id && file.AppGameId is { } known)
            return known;
        // The file's own hashes are a raw file's; for an archive or a CHD, those of what it holds, as the DAT gives them.
        var content = await db.LibraryContents.AsNoTracking().Where(c => c.LibraryFileId == file.Id).OrderBy(c => c.Entry)
            .Select(c => new { c.Crc32, c.Md5, c.Sha1, System = c.Matches.Select(m => m.DumpFile!.Dump!.Title!.System!.Name).FirstOrDefault() })
            .FirstOrDefaultAsync(ct);
        var id = await connector.ResolveGameAsync(new GameLookup(file.Path, content?.System, content?.Sha1, content?.Md5, content?.Crc32), ct);
        if (id is not null)
        {
            file.AppGameId = id;
            file.AppConnectorId = instance.Id;
            await db.SaveChangesAsync(ct);
        }
        return id;
    }
}

/// <summary>
/// After something's filed: asks the connected app to scan for it, then finds it there, so its link is ready. An app
/// takes a while to scan, so this asks again for a few minutes. It runs beside the task queue, not in it, so an app
/// that's slow, or never finds a game, holds nothing else up; a restart forgets what it was waiting for, and the Open
/// button asks again.
/// </summary>
public sealed class AppSync(IServiceScopeFactory scopes, ILogger<AppSync> logger) : BackgroundService
{
    /// <summary>How often, and how many times, to look for what was filed.</summary>
    public static TimeSpan Every { get; set; } = TimeSpan.FromSeconds(20);

    public const int Tries = 15;

    private readonly System.Threading.Channels.Channel<IReadOnlyList<string>> _filed =
        System.Threading.Channels.Channel.CreateUnbounded<IReadOnlyList<string>>();

    /// <summary>Tells the app about these library paths, when there's an app.</summary>
    public void Add(IReadOnlyList<string> paths) => _filed.Writer.TryWrite(paths);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var paths in _filed.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await SyncAsync(paths, stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Couldn't tell the connected app about {Paths}: {Message}", string.Join(", ", paths), e.Message);
            }
        }
    }

    private async Task SyncAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdexDbContext>();
        var links = scope.ServiceProvider.GetRequiredService<AppLinks>();
        if (await scope.ServiceProvider.GetRequiredService<ConnectorPlugins>().GetActiveAsync(ct) is not { } active)
            return;
        var name = active.Instance.Name;
        await active.Connector.TriggerScanAsync(paths, ct);

        var waiting = await db.LibraryFiles.Where(f => paths.Contains(f.Path)).ToListAsync(ct);
        for (var attempt = 1; waiting.Count > 0 && attempt <= Tries; attempt++)
        {
            await Task.Delay(Every, ct);
            // A scan that was already running when asked may have passed these platforms by: ask again now and then.
            if (attempt % 5 == 0)
                await active.Connector.TriggerScanAsync(waiting.Select(f => f.Path).ToList(), ct);
            foreach (var file in waiting.ToList())
                if (await links.ResolveAsync(active.Instance, active.Connector, file, ct) is not null)
                    waiting.Remove(file);
        }
        if (waiting.Count > 0)
            logger.LogWarning("{App} hasn't found {Paths} yet; it may still be scanning", name, string.Join(", ", waiting.Select(f => f.Path)));
        else
            logger.LogInformation("{App} has {Paths}", name, string.Join(", ", paths));
    }
}
