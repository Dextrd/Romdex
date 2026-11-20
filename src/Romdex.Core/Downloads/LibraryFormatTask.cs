using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romdex.Client;
using Romdex.Core.Apps;
using Romdex.Core.Data;
using Romdex.Core.Plugins;
using Romdex.Core.Tasks;

namespace Romdex.Core.Downloads;

/// <summary>
/// Formats the library: moves games to where the layout puts them, as <see cref="Adoption"/> reports, through the igir
/// worker, which removes the folders that leaves empty. The library's records follow the files, so nothing is hashed
/// again, and the connected app is asked to scan for them where they are now.
/// </summary>
public sealed class LibraryFormatTask(Adoption adoption, IFilingWorker worker, RomdexDbContext db, ConnectorPlugins connectors, AppSync appSync,
    ILogger<LibraryFormatTask> logger) : ITaskHandler
{
    public const string TaskKind = "library-format";

    // Moves the worker is sent at once: enough to be quick, few enough that a stop isn't long in coming.
    private const int Batch = 200;

    public string Kind => TaskKind;

    public string Title(string? payload) => payload is null ? "Formatting the library" : $"Formatting {Payload(payload).System ?? "a system"}";

    public static string PayloadFor(Guid? systemId, string? system) =>
        JsonSerializer.Serialize(new FormatPayload(systemId, system), RomdexJson.Options);

    private static FormatPayload Payload(string payload) => JsonSerializer.Deserialize<FormatPayload>(payload, RomdexJson.Options) ?? new(null, null);

    private sealed record FormatPayload(Guid? SystemId, string? System);

    public async Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Working out where games go");
        var systemId = task.PayloadJson is { } payload ? Payload(payload).SystemId : null;
        IReadOnlyList<CatalogueSystem>? systems = systemId is { } id
            ? [await db.Systems.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new InvalidOperationException("That system is gone.")]
            : null;
        var report = await adoption.ReportAsync(systems, ct);
        if (report.Problem is { } problem)
            throw new InvalidOperationException(problem);
        var games = report.Moves.Where(m => m.Blocked is null).ToList();
        var moves = games.SelectMany(m => m.From.Zip(m.To, (from, to) => new LibraryMove(from, to))).Where(m => m.From != m.To).ToList();

        var moved = new List<LibraryMove>();
        var failed = new List<FailedMove>();
        foreach (var batch in moves.Chunk(Batch))
        {
            ct.ThrowIfCancellationRequested();
            progress.Report($"Moving files: {moved.Count + failed.Count:N0} of {moves.Count:N0}");
            var result = await worker.MoveAsync(new MoveRequest(batch), ct);
            await RecordAsync(result.Moved, ct);
            moved.AddRange(result.Moved);
            failed.AddRange(result.Failed);
        }
        foreach (var f in failed)
            logger.LogWarning("Couldn't move {From} to {To}: {Error}", f.From, f.To, f.Error);

        // The app finds them again where they are now.
        if (moved.Count > 0 && await connectors.GetActiveAsync(ct) is not null)
            appSync.Add([.. moved.Select(m => m.To)]);

        var notes = new List<string> { $"Moved {Count(games.Count, "game")} ({Count(moved.Count, "file")})" };
        if (report.InPlace > 0)
            notes.Add($"{Count(report.InPlace, "game")} already in place");
        if (report.Moves.Count - games.Count is var blocked and > 0)
            notes.Add($"{Count(blocked, "game")} left where {(blocked == 1 ? "it is" : "they are")}, as the preview says why");
        if (failed.Count > 0)
            notes.Add($"{Count(failed.Count, "file")} couldn't move, such as {failed[0].From}: {failed[0].Error}");
        progress.Report(string.Join("; ", notes));
    }

    // The library's records follow the files: the same file, so its hashes and matches still hold, but the app's ID for
    // it is the old place's.
    private async Task RecordAsync(IReadOnlyList<LibraryMove> moved, CancellationToken ct)
    {
        var to = moved.ToDictionary(m => m.From, m => m.To, StringComparer.Ordinal);
        var froms = to.Keys.ToList();
        foreach (var file in await db.LibraryFiles.Where(f => froms.Contains(f.Path)).ToListAsync(ct))
        {
            file.Path = to[file.Path];
            file.AppGameId = null;
            file.AppConnectorId = null;
        }
        await db.SaveChangesAsync(ct);
    }

    private static string Count(int n, string one) => n == 1 ? $"1 {one}" : $"{n:N0} {one}s";
}
