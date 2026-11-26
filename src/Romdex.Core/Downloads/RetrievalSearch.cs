using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romdex.Client;
using Romdex.Contracts.Plugins.Retrieval;
using Romdex.Core.Data;
using Romdex.Core.Plugins;
using DownloadState = Romdex.Core.Data.DownloadState;

namespace Romdex.Core.Downloads;

/// <summary>
/// Finding a title through the retrievers: every one that searches is asked at once, and what they offer is ranked.
/// Getting a title fetches the best offer straight away, as Melodex does, passing over what was turned away before.
/// </summary>
public sealed class RetrievalSearch(RomdexDbContext db, RetrievalPlugins plugins, RetrievalTargets targets, DownloadService downloads)
{
    /// <summary>A plugin that doesn't say how sure it is counts as this sure.</summary>
    private const double UnsaidScore = 0.8;

    /// <summary>Every retriever's offers for a title, best first, with what was turned away before marked. Null for no such title.</summary>
    public async Task<DownloadSearchResult?> SearchAsync(Guid titleId, Guid? dumpId, CancellationToken ct)
    {
        if (await targets.ForAsync(titleId, dumpId, ct) is not { } target)
            return null;
        var searching = await plugins.WithAsync(RetrievalCapabilities.Search, ct);
        var answers = await Task.WhenAll(searching.Select(async p =>
        {
            try
            {
                return (p.Instance, Candidates: await p.Plugin.SearchAsync(target, 10, ct), Error: (string?)null);
            }
            catch (Exception e) when (e is RetrievalException or HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return (p.Instance, Candidates: (IReadOnlyList<Candidate>)[], Error: e.Message);
            }
        }));
        var turnedAway = await TurnedAwayAsync(titleId, ct);
        // The plugins come in your order, each with its best first; Rank keeps that order among equals.
        var offers = answers.SelectMany(a => a.Candidates.Select(c =>
            new DownloadOffer(a.Instance.Id, a.Instance.Name, c, turnedAway.GetValueOrDefault((a.Instance.Id, c.CandidateId))))).ToList();
        var problems = answers.Where(a => a.Error is not null).Select(a => $"{a.Instance.Name}: {a.Error}").ToList();
        if (searching.Count == 0)
            problems.Add("No retriever that searches is set up. Add one under Settings → Plugins.");
        return new DownloadSearchResult(Rank(offers, target), problems);
    }

    /// <summary>
    /// Gets a title, or one of its dumps: fetches the best offer that wasn't turned away before. Null for no such title;
    /// a result with nothing started says why.
    /// </summary>
    /// <param name="replaces">Library files what's fetched replaces, such as a bad dump: deleted once it's filed and verifies.</param>
    public async Task<FetchResult?> FetchBestAsync(Guid titleId, Guid? dumpId, CancellationToken ct, IReadOnlyList<Guid>? replaces = null)
    {
        if (await SearchAsync(titleId, dumpId, ct) is not { } found)
            return null;
        var problems = found.Problems.ToList();
        var best = found.Offers.FirstOrDefault(o => o.TurnedAway is null);
        if (best is null)
        {
            problems.Add(found.Offers.Count == 0 ? "Nothing was offered." : "Everything offered was turned away before.");
            return new FetchResult(null, null, problems);
        }
        var instance = (await db.PluginInstances.FindAsync([best.PluginId], ct))!;
        var target = await targets.ForAsync(titleId, dumpId, ct);
        var item = await downloads.FetchAsync(instance, best.Candidate, target, titleId, dumpId, ct, replaces);
        return new FetchResult(item, best, problems);
    }

    /// <summary>What's been fetched for a title and turned away, by plugin and candidate, with why.</summary>
    private async Task<Dictionary<(Guid, string), string>> TurnedAwayAsync(Guid titleId, CancellationToken ct) =>
        (await db.Downloads.AsNoTracking()
            .Where(d => d.TitleId == titleId && d.PluginInstanceId != null && (d.State == DownloadState.Failed || d.State == DownloadState.Rejected
                || d.State == DownloadState.NeedsReview && d.HeldFor == HoldReason.NotAskedFor))
            .OrderBy(d => d.UpdatedAt).Select(d => new { d.PluginInstanceId, d.CandidateId, d.Message }).ToListAsync(ct))
        .GroupBy(d => (d.PluginInstanceId!.Value, d.CandidateId)).ToDictionary(g => g.Key, g => g.Last().Message ?? "It was turned away before.");

    /// <summary>
    /// Offers in the order to try them: one the plugin says is the dump wanted, or the dump you'd want first, before the
    /// rest; then one whose checksums the source vouches for; then how sure the plugin is. Among equals, your plugins'
    /// order, and each plugin's own.
    /// </summary>
    public static IReadOnlyList<DownloadOffer> Rank(IReadOnlyList<DownloadOffer> offers, RetrievalTarget target)
    {
        var wanted = target.DumpName ?? target.Dumps.FirstOrDefault()?.Name;
        var listed = target.Dumps.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        return offers
            .OrderBy(o => o.Candidate.DumpName == wanted ? 0 : o.Candidate.DumpName is { } name && listed.Contains(name) ? 1 : 2)
            .ThenBy(o => o.Candidate.Verified == true ? 0 : 1)
            .ThenByDescending(o => o.Candidate.Score ?? UnsaidScore)
            .ToList();
    }
}

/// <summary>What getting a title started, if anything, and why not.</summary>
public sealed record FetchResult(Download? Item, DownloadOffer? Offer, IReadOnlyList<string> Problems);
