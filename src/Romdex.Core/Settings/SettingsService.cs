using System.Text.Json;
using Romdex.Client;
using Romdex.Core.Data;

namespace Romdex.Core.Settings;

public sealed class SettingsService(RomdexDbContext db)
{
    private const string LogKey = "log";
    private const string ScheduleKey = "schedule";
    private const string ScheduleRunsKey = "schedule.runs";

    public Task<ScheduleSettings> GetScheduleAsync(CancellationToken ct) => GetAsync<ScheduleSettings>(ScheduleKey, ct);

    public Task SaveScheduleAsync(ScheduleSettings value, CancellationToken ct) => SaveAsync(ScheduleKey, value, ct);

    public Task<ScheduleRuns> GetScheduleRunsAsync(CancellationToken ct) => GetAsync<ScheduleRuns>(ScheduleRunsKey, ct);

    public Task SaveScheduleRunsAsync(ScheduleRuns runs, CancellationToken ct) => SaveAsync(ScheduleRunsKey, runs, ct);

    public Task<LogSettings> GetLogAsync(CancellationToken ct) => GetAsync<LogSettings>(LogKey, ct);

    public Task SaveLogAsync(LogSettings value, CancellationToken ct) => SaveAsync(LogKey, value, ct);

    private async Task<T> GetAsync<T>(string key, CancellationToken ct) where T : new()
    {
        var row = await db.Settings.FindAsync([key], ct);
        return row is null ? new T() : JsonSerializer.Deserialize<T>(row.ValueJson, RomdexJson.Options) ?? new T();
    }

    private async Task SaveAsync<T>(string key, T value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, RomdexJson.Options);
        var row = await db.Settings.FindAsync([key], ct);
        if (row is null)
            db.Settings.Add(new Setting { Key = key, ValueJson = json });
        else
            row.ValueJson = json;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>The scheduled run going, if one is, and each schedule's last.</summary>
public sealed record ScheduleRuns
{
    public ScheduleRunInfo? Current { get; init; }

    public Dictionary<ScheduleRepeat, ScheduleRunInfo> Last { get; init; } = [];

    /// <summary>The fetches the current run's retries have asked for, and what they've tried.</summary>
    public ScheduleDownloads Downloads { get; init; } = new();
}

/// <summary>Where a run's retries stand, as they keep a few fetches going.</summary>
/// <param name="Asked">The requests asked for, to tell how many are still going.</param>
/// <param name="Tried">What's been asked for already, a retried request's ID, so none is twice.</param>
public sealed record ScheduleDownloads(IReadOnlyList<Guid> Asked, IReadOnlyList<string> Tried)
{
    public ScheduleDownloads() : this([], []) { }
}
