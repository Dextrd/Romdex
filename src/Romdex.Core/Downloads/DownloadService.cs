using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Romdex.Client;
using Romdex.Contracts.Plugins.Retrieval;
using Romdex.Core.Data;
using DownloadState = Romdex.Core.Data.DownloadState;
using Romdex.Core.Library;
using Romdex.Core.Plugins;
using Romdex.Core.Tasks;

namespace Romdex.Core.Downloads;

/// <summary>
/// Downloads from start to review: staging a candidate through its plugin, asking the plugins that list what arrived
/// by themselves, following fetches until they're staged, and accepting, rejecting and retrying.
/// </summary>
public sealed class DownloadService(
    RomdexDbContext db,
    RetrievalPlugins plugins,
    RetrievalTargets targets,
    TaskQueue queue,
    IFilingWorker worker,
    LibraryScanner scanner,
    IOptions<RomdexOptions> options,
    ILogger<DownloadService> logger)
{
    /// <summary>Asks a plugin to stage a candidate, and adds it as an item that's fetching.</summary>
    /// <param name="titleId">The title it's for, when someone asked for one.</param>
    public async Task<Download> FetchAsync(PluginInstance instance, Candidate candidate, RetrievalTarget? target, Guid? titleId, Guid? dumpId, CancellationToken ct)
    {
        var item = new Download
        {
            PluginInstanceId = instance.Id,
            Source = instance.Name,
            CandidateId = candidate.CandidateId,
            Name = candidate.Name,
            CandidateJson = JsonSerializer.Serialize(candidate, RetrievalJson.Options),
            TitleId = titleId,
            WantedDumpId = dumpId,
            State = DownloadState.Fetching,
            StagingDirectory = "",
        };
        item.StagingDirectory = $"downloads/{item.Id}";
        MakeStaging(item);
        db.Downloads.Add(item);
        await db.SaveChangesAsync(ct);
        try
        {
            var status = await plugins.Create(instance).FetchAsync(item.Id.ToString(), target, candidate, item.StagingDirectory, ct);
            await ApplyAsync(item, status, ct);
        }
        catch (Exception e) when (e is RetrievalException or HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            await FailAsync(item, $"{instance.Name} couldn't start: {e.Message}", ct);
        }
        return item;
    }

    /// <summary>
    /// Takes a library file out of the library and through Downloads again, such as one that matched nothing until the
    /// catalogue changed, or one filed in the wrong place. The worker moves it, since the core can't write the library.
    /// </summary>
    public async Task<Download> ReclaimAsync(LibraryFile file, CancellationToken ct)
    {
        var item = await TakeOutAsync(file, DownloadState.Queued, null, ct);
        db.Update(item);
        await QueueAsync(item, accept: false, ct);
        return item;
    }

    /// <summary>
    /// Deletes a file from the library, such as a bad dump or a damaged archive, from Review. The worker takes it out
    /// and it's thrown away; it's left on the Downloads page as rejected, so there's a record of it.
    /// </summary>
    public async Task<Download> DeleteFromLibraryAsync(LibraryFile file, CancellationToken ct)
    {
        var item = await TakeOutAsync(file, DownloadState.Rejected, "Deleted from the library under Review; its files are gone.", ct);
        DownloadPipeline.DeleteStaging(StagingPath(item));
        return item;
    }

    private async Task<Download> TakeOutAsync(LibraryFile file, DownloadState state, string? message, CancellationToken ct)
    {
        var item = new Download
        {
            Source = "Library",
            CandidateId = file.Path,
            Name = Path.GetFileNameWithoutExtension(file.Path),
            CandidateJson = JsonSerializer.Serialize(new Candidate(file.Path, Path.GetFileNameWithoutExtension(file.Path), $"Library: {file.Path}", Size: file.Size), RetrievalJson.Options),
            State = state,
            Message = message,
            StagingDirectory = "",
        };
        item.StagingDirectory = $"downloads/{item.Id}";
        MakeStaging(item);
        try
        {
            await worker.StageAsync(new StageRequest(file.Path, item.StagingDirectory), ct);
        }
        catch
        {
            DownloadPipeline.DeleteStaging(StagingPath(item));
            throw;
        }
        db.Downloads.Add(item);
        await db.SaveChangesAsync(ct);
        // It's gone from the library; the scan clears the change tracker, so the item is attached again after.
        await scanner.ScanPathsAsync([file.Path], ct);
        return item;
    }

    /// <summary>Asks every plugin that lists what arrived for anything new, and stages it. Returns how many were new.</summary>
    public async Task<int> CheckAsync(CancellationToken ct)
    {
        var added = 0;
        foreach (var (instance, client) in await plugins.WithAsync(RetrievalCapabilities.List, ct))
        {
            IReadOnlyList<Candidate> listed;
            try
            {
                listed = await client.ListAsync(ct);
            }
            catch (Exception e) when (e is RetrievalException or HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("{Plugin} couldn't list what arrived: {Reason}", instance.Name, e.Message);
                continue;
            }
            var ids = listed.Select(c => c.CandidateId).ToList();
            // Once is enough: one that failed or was rejected stays listed until someone deals with it in the plugin.
            var seen = await db.Downloads.Where(i => i.PluginInstanceId == instance.Id && ids.Contains(i.CandidateId))
                .Select(i => i.CandidateId).ToListAsync(ct);
            foreach (var candidate in listed.Where(c => !seen.Contains(c.CandidateId)))
            {
                await FetchAsync(instance, candidate, null, null, null, ct);
                added++;
            }
        }
        return added;
    }

    /// <summary>Asks the plugin how each fetching item is getting on, and queues those that are staged.</summary>
    public async Task FollowFetchesAsync(CancellationToken ct)
    {
        var fetching = await db.Downloads.Where(i => i.State == DownloadState.Fetching).ToListAsync(ct);
        foreach (var item in fetching)
        {
            var instance = item.PluginInstanceId is { } id ? await db.PluginInstances.FindAsync([id], ct) : null;
            if (instance?.Endpoint is null)
            {
                await FailAsync(item, "The plugin that was fetching it isn't set up any more.", ct);
                continue;
            }
            try
            {
                await ApplyAsync(item, await plugins.Create(instance).GetFetchAsync(item.Id.ToString(), ct), ct);
            }
            catch (RetrievalException e) when (e.Status == System.Net.HttpStatusCode.NotFound)
            {
                await FailAsync(item, $"{instance.Name} lost track of it, most likely by restarting. Retry to fetch it again.", ct);
            }
            catch (Exception e) when (e is RetrievalException or HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // A plugin that's down for a moment is asked again next time.
                logger.LogDebug("Couldn't ask {Plugin} about {Item}: {Reason}", instance.Name, item.Name, e.Message);
            }
        }
    }

    /// <summary>Files an item held for review anyway.</summary>
    public async Task<bool> AcceptAsync(Download item, CancellationToken ct)
    {
        if (item.State != DownloadState.NeedsReview || !await db.DownloadDumps.AnyAsync(d => d.DownloadId == item.Id, ct))
            return false;
        await QueueAsync(item, accept: true, ct);
        return true;
    }

    /// <summary>Throws an item's files away.</summary>
    public async Task<bool> RejectAsync(Download item, CancellationToken ct)
    {
        if (item.State is DownloadState.Working or DownloadState.Filed or DownloadState.Discarded or DownloadState.Rejected)
            return false;
        if (item.State == DownloadState.Fetching && item.PluginInstanceId is { } id && await db.PluginInstances.FindAsync([id], ct) is { Endpoint: not null } instance)
        {
            try
            {
                await plugins.Create(instance).CancelFetchAsync(item.Id.ToString(), ct);
            }
            catch (Exception e) when (e is RetrievalException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("{Plugin} couldn't stop fetching {Item}: {Reason}", instance.Name, item.Name, e.Message);
            }
        }
        DownloadPipeline.DeleteStaging(StagingPath(item));
        item.State = DownloadState.Rejected;
        item.Message = "Rejected; its files are gone.";
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Runs an item again: identified afresh if its files are staged, else fetched again.</summary>
    public async Task<bool> RetryAsync(Download item, CancellationToken ct)
    {
        if (item.State is not (DownloadState.NeedsReview or DownloadState.Failed))
            return false;
        var directory = StagingPath(item);
        if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any())
        {
            await QueueAsync(item, accept: false, ct);
            return true;
        }
        var instance = item.PluginInstanceId is { } id ? await db.PluginInstances.FindAsync([id], ct) : null;
        if (instance?.Endpoint is null)
        {
            item.Message = "Its files are gone and the plugin that brought it isn't set up any more, so it can't be fetched again.";
            await db.SaveChangesAsync(ct);
            return false;
        }
        var candidate = JsonSerializer.Deserialize<Candidate>(item.CandidateJson, RetrievalJson.Options)!;
        var target = item.TitleId is { } titleId ? await targets.ForAsync(titleId, item.WantedDumpId, ct) : null;
        MakeStaging(item);
        item.State = DownloadState.Fetching;
        item.Message = null;
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        try
        {
            await ApplyAsync(item, await plugins.Create(instance).FetchAsync(item.Id.ToString(), target, candidate, item.StagingDirectory, ct), ct);
        }
        catch (Exception e) when (e is RetrievalException or HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            await FailAsync(item, $"{instance.Name} couldn't start: {e.Message}", ct);
        }
        return true;
    }

    public string StagingPath(Download item) => Path.Combine(Path.GetFullPath(options.Value.StagingPath), item.StagingDirectory);

    private async Task ApplyAsync(Download item, FetchStatus status, CancellationToken ct)
    {
        switch (status.State)
        {
            case FetchState.Completed:
                await QueueAsync(item, accept: false, ct);
                break;
            case FetchState.Failed:
                await FailAsync(item, status.Error ?? "The plugin couldn't fetch it.", ct);
                break;
            case FetchState.Cancelled:
                await FailAsync(item, "The fetch was cancelled.", ct);
                break;
            default:
                var message = status.Progress is { } p ? $"Fetching: {p:P0}" : "Fetching";
                if (item.Message != message)
                {
                    item.Message = message;
                    item.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                break;
        }
    }

    private async Task QueueAsync(Download item, bool accept, CancellationToken ct)
    {
        item.State = DownloadState.Queued;
        item.Message = accept ? "Accepted; waiting to be filed" : "Waiting to be identified";
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(DownloadTask.TaskKind, DownloadTask.Payload(item, accept), ct);
    }

    private async Task FailAsync(Download item, string message, CancellationToken ct)
    {
        item.State = DownloadState.Failed;
        item.Message = message;
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogWarning("[{Item}] {Message}", item.Name, message);
    }

    // The plugins write as their own users, so the folder is open to all, as the staging volume is.
    private void MakeStaging(Download item)
    {
        var directory = StagingPath(item);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, (UnixFileMode)0b111_111_111);
    }
}

/// <summary>Takes one staged item through the pipeline, as a background task so it shows on the Activity page.</summary>
public sealed class DownloadTask(RomdexDbContext db, DownloadPipeline pipeline) : ITaskHandler
{
    public const string TaskKind = "download";

    private sealed record Body(Guid Id, string Name, bool Accept);

    public static string Payload(Download item, bool accept) => JsonSerializer.Serialize(new Body(item.Id, item.Name, accept), RomdexJson.Options);

    public string Kind => TaskKind;

    public string Title(string? payload) => payload is null ? "Download" : $"Download: {JsonSerializer.Deserialize<Body>(payload, RomdexJson.Options)!.Name}";

    public async Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct)
    {
        var body = JsonSerializer.Deserialize<Body>(task.PayloadJson!, RomdexJson.Options)!;
        var item = await db.Downloads.Include(i => i.Dumps).FirstOrDefaultAsync(i => i.Id == body.Id, ct)
            ?? throw new InvalidOperationException("The download is gone.");
        if (item.State != DownloadState.Queued)
            return;
        item.State = DownloadState.Working;
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        try
        {
            await pipeline.RunAsync(item, body.Accept, progress, ct);
            progress.Report($"{item.Name}: {item.Message}");
        }
        catch (OperationCanceledException)
        {
            // Stopped part way: whatever's still staged can be retried.
            await MarkAsync(item, DownloadState.Failed, "Stopped before it was done. Retry to carry on.");
            throw;
        }
        catch (Exception e)
        {
            await MarkAsync(item, DownloadState.Failed, e.Message);
            throw;
        }
    }

    private async Task MarkAsync(Download item, DownloadState state, string message)
    {
        db.ChangeTracker.Clear();
        await db.Downloads.Where(i => i.Id == item.Id).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.State, state)
            .SetProperty(i => i.Message, message)
            .SetProperty(i => i.UpdatedAt, DateTime.UtcNow));
    }
}

/// <summary>Follows fetches until they're staged, and asks the plugins that list what arrived every so often.</summary>
public sealed class DownloadWatcher(IServiceScopeFactory scopes, DownloadSignal signal, IOptions<RomdexOptions> options, ILogger<DownloadWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan FetchPoll = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCheck = DateTime.UtcNow.AddSeconds(30);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var downloads = scope.ServiceProvider.GetRequiredService<DownloadService>();
                await downloads.FollowFetchesAsync(stoppingToken);
                var every = options.Value.DownloadCheckMinutes;
                if (signal.TakeCheck() || every > 0 && DateTime.UtcNow >= nextCheck)
                {
                    var added = await downloads.CheckAsync(stoppingToken);
                    if (added > 0)
                        logger.LogInformation("{Count} new arrival{S} to stage", added, added == 1 ? "" : "s");
                    nextCheck = DateTime.UtcNow.AddMinutes(Math.Max(1, every));
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("Couldn't follow downloads: {Reason}", e.Message);
            }
            await signal.WaitAsync(FetchPoll, stoppingToken);
        }
    }
}

/// <summary>Wakes the <see cref="DownloadWatcher"/>, to check the plugins now.</summary>
public sealed class DownloadSignal
{
    private readonly TaskSignal _signal = new();
    private int _check;

    public void CheckNow()
    {
        Interlocked.Exchange(ref _check, 1);
        _signal.Wake();
    }

    public void Wake() => _signal.Wake();

    public bool TakeCheck() => Interlocked.Exchange(ref _check, 0) == 1;

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>What the plugins are told to find for a title: every dump the DAT lists for it, the one you'd want first.</summary>
public sealed class RetrievalTargets(RomdexDbContext db, LibraryViewBuilder views)
{
    public async Task<RetrievalTarget?> ForAsync(Guid titleId, Guid? dumpId, CancellationToken ct)
    {
        var title = await db.Titles.AsNoTracking().Include(t => t.System).FirstOrDefaultAsync(t => t.Id == titleId, ct);
        if (title is null)
            return null;
        var dumps = await db.Dumps.AsNoTracking().Include(d => d.Files).Where(d => d.TitleId == titleId).ToListAsync(ct);
        var preferred = (await views.GetAsync(title.System!.BiosForId ?? title.SystemId, ct))?.ById.GetValueOrDefault(titleId)?.Preferred;
        var ordered = dumps.OrderBy(d => d.Release != preferred?.Release).ThenBy(d => d.Name, StringComparer.Ordinal);
        var wanted = dumpId is { } id ? dumps.FirstOrDefault(d => d.Id == id)?.Name : null;
        return new RetrievalTarget(title.System.Name, title.Name, ordered.Select(d => new TargetDump(
                d.Name, d.Regions.Length == 0 ? [] : d.Regions.Split(','), d.Serial,
                d.Files.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => new TargetFile(f.Name, f.Size, f.Crc32, f.Md5, f.Sha1)).ToList()))
            .ToList(), wanted);
    }
}
