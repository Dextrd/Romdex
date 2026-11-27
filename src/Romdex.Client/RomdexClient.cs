using System.Net;
using System.Net.Http.Json;

namespace Romdex.Client;

/// <summary>Typed client for the Romdex public API. Pass an HttpClient whose BaseAddress is the Romdex server.</summary>
public sealed class RomdexClient(HttpClient http)
{
    private const string Api = "api/v1";

    // ---- Tasks and the log ----

    public async Task<IReadOnlyList<TaskInfo>> ListTasksAsync(bool? active = null, int? limit = null, CancellationToken ct = default)
    {
        var query = string.Join('&', new[] { active is { } a ? $"active={a.ToString().ToLowerInvariant()}" : null, limit is { } l ? $"limit={l}" : null }.OfType<string>());
        return await http.GetFromJsonAsync<List<TaskInfo>>($"{Api}/tasks{(query.Length > 0 ? "?" + query : "")}", RomdexJson.Options, ct) ?? [];
    }

    /// <summary>What a task has done, row by row, changed after <paramref name="since"/>; null when its list isn't kept.</summary>
    public Task<TaskRowsPage?> GetTaskRowsAsync(Guid id, long since = 0, CancellationToken ct = default) =>
        GetOrNullAsync<TaskRowsPage>($"{Api}/tasks/{id}/rows?since={since}", ct);

    public Task<TaskInfo?> GetTaskAsync(Guid id, CancellationToken ct = default) =>
        http.GetFromJsonAsync<TaskInfo>($"{Api}/tasks/{id}", RomdexJson.Options, ct);

    public async Task<LogPage> GetLogAsync(long after = 0, int limit = 1000, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<LogPage>($"{Api}/log?after={after}&limit={limit}", RomdexJson.Options, ct) ?? new LogPage([], after);

    public async Task<LogSettings> GetLogSettingsAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<LogSettings>($"{Api}/settings/log", RomdexJson.Options, ct))!;

    public async Task SaveLogSettingsAsync(LogSettings settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/settings/log", settings, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    // ---- Plugins ----

    public async Task<IReadOnlyList<PluginInfo>> ListPluginsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<PluginInfo>>($"{Api}/plugins", RomdexJson.Options, ct) ?? [];

    public async Task<IReadOnlyList<AvailablePlugin>> ListAvailablePluginsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<AvailablePlugin>>($"{Api}/plugins/available", RomdexJson.Options, ct) ?? [];

    public async Task<PluginInfo> AddPluginAsync(AddPlugin plugin, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/plugins", plugin, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PluginInfo>(RomdexJson.Options, ct))!;
    }

    public async Task<PluginInfo> UpdatePluginAsync(Guid id, UpdatePlugin update, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/plugins/{id}", update, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PluginInfo>(RomdexJson.Options, ct))!;
    }

    public async Task OrderPluginsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/plugins/order", new PluginOrder(ids), RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<PluginCheckResult> RunPluginCheckAsync(Guid id, string check, RunPluginCheck body, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/plugins/{id}/checks/{Uri.EscapeDataString(check)}", body, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PluginCheckResult>(RomdexJson.Options, ct))!;
    }

    public async Task<PluginInfo> TestPluginAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/plugins/{id}/test", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<PluginInfo>(RomdexJson.Options, ct))!;
    }

    public async Task RemovePluginAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/plugins/{id}", ct);
        await EnsureAsync(response, ct);
    }

    // ---- Catalogue ----

    public async Task<IReadOnlyList<SystemSummary>> ListSystemsAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<SystemSummary>>($"{Api}/systems", RomdexJson.Options, ct) ?? [];

    public Task<SystemSummary?> GetSystemAsync(Guid id, CancellationToken ct = default) => GetOrNullAsync<SystemSummary>($"{Api}/systems/{id}", ct);

    /// <summary>Titles by name; with <c>have</c>, only those the library has some of: owned, partial or bad, ignored or not.</summary>
    /// <param name="fit">Only titles whose region dot is this.</param>
    public async Task<TitlePage> ListTitlesAsync(Guid systemId, TitleStatus? status = null, string? q = null, bool ignored = false, bool hidden = false,
        int skip = 0, int take = 100, bool have = false, RegionFit? fit = null, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<TitlePage>($"{Api}/systems/{systemId}/titles?{TitleQuery(status, q, ignored, hidden, have, skip, take, fit)}", RomdexJson.Options, ct))!;

    private static string TitleQuery(TitleStatus? status, string? q, bool ignored, bool hidden, bool have, int skip, int take, RegionFit? fit = null)
    {
        var query = new List<string> { $"skip={skip}", $"take={take}" };
        if (fit is { } f)
            query.Add($"fit={f}");
        if (status is { } s)
            query.Add($"status={s}");
        if (!string.IsNullOrWhiteSpace(q))
            query.Add($"q={Uri.EscapeDataString(q)}");
        if (ignored)
            query.Add("ignored=true");
        if (hidden)
            query.Add("hidden=true");
        if (have)
            query.Add("have=true");
        return string.Join('&', query);
    }

    /// <summary>Titles by name; with <c>have</c>, only those the library has some of: owned, partial or bad, ignored or not.</summary>
    public async Task<TitlePage> ListAllTitlesAsync(TitleStatus? status = null, string? q = null, bool ignored = false, int skip = 0, int take = 100,
        bool have = false, RegionFit? fit = null, bool starred = false, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<TitlePage>($"{Api}/titles?{TitleQuery(status, q, ignored, false, have, skip, take, fit)}{(starred ? "&starred=true" : "")}",
            RomdexJson.Options, ct))!;

    public async Task<IReadOnlyList<ItemMark>> ListMarksAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ItemMark>>($"{Api}/marks", RomdexJson.Options, ct) ?? [];

    /// <summary>Stars, unstars or rates a game; what's left of its mark, or null once it has neither.</summary>
    public async Task<ItemMark?> SetMarkAsync(MarkKind kind, Guid id, SetMark change, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/marks/{kind.ToString().ToLowerInvariant()}/{id}", change, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
        return response.StatusCode == System.Net.HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<ItemMark>(RomdexJson.Options, ct);
    }

    public async Task<BiosList> ListBiosAsync(Guid systemId, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<BiosList>($"{Api}/systems/{systemId}/bios", RomdexJson.Options, ct))!;

    public async Task<string> GetSystemReportAsync(Guid systemId, CancellationToken ct = default) =>
        await http.GetStringAsync($"{Api}/systems/{systemId}/report.csv", ct);

    public async Task<SystemPreferencesInfo> GetSystemPreferencesAsync(Guid systemId, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<SystemPreferencesInfo>($"{Api}/systems/{systemId}/preferences", RomdexJson.Options, ct))!;

    public async Task<SystemPreferencesInfo> SetSystemPreferencesAsync(Guid systemId, SystemPreferences preferences, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/systems/{systemId}/preferences", preferences, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<SystemPreferencesInfo>(RomdexJson.Options, ct))!;
    }

    public async Task<SystemPreferencesInfo> ResetSystemPreferencesAsync(Guid systemId, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/systems/{systemId}/preferences", ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<SystemPreferencesInfo>(RomdexJson.Options, ct))!;
    }

    public async Task<SystemPreferences> GetDefaultPreferencesAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<SystemPreferences>($"{Api}/preferences", RomdexJson.Options, ct))!;

    public async Task SetDefaultPreferencesAsync(SystemPreferences preferences, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/preferences", preferences, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public Task<TitleDetail?> GetTitleAsync(Guid id, CancellationToken ct = default) => GetOrNullAsync<TitleDetail>($"{Api}/titles/{id}", ct);

    /// <summary>What a title's game is, from the metadata plugins: looked up the first time it's asked for, which can take a few seconds.</summary>
    public Task<GameInfo?> GetGameInfoAsync(Guid titleId, CancellationToken ct = default) => GetOrNullAsync<GameInfo>($"{Api}/titles/{titleId}/about", ct);

    /// <summary>
    /// What a title's game is, as far as it's been looked up: waits a moment for the lookup to finish, then gives what's
    /// been found so far, marked <see cref="GameInfo.Loading"/> until there's no more to come.
    /// </summary>
    public Task<GameInfo?> GetGameInfoSoFarAsync(Guid titleId, CancellationToken ct = default) =>
        GetOrNullAsync<GameInfo>($"{Api}/titles/{titleId}/about?partial=true", ct);

    /// <summary>How to play a dump the library has: the player's link, or why it can't play in the browser.</summary>
    public Task<PlayInfo?> GetPlayAsync(Guid dumpId, CancellationToken ct = default) => GetOrNullAsync<PlayInfo>($"{Api}/play/{dumpId}", ct);

    /// <summary>Where a dump is in the connected app; asks the app to scan when it hasn't found it yet.</summary>
    public async Task<AppLink> OpenInAppAsync(Guid dumpId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/play/{dumpId}/app", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AppLink>(RomdexJson.Options, ct))!;
    }

    public async Task<TitleDetail> IgnoreTitleAsync(Guid id, bool ignore = true, CancellationToken ct = default)
    {
        using var response = ignore ? await http.PostAsync($"{Api}/titles/{id}/ignore", null, ct) : await http.DeleteAsync($"{Api}/titles/{id}/ignore", ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TitleDetail>(RomdexJson.Options, ct))!;
    }

    public async Task<TitleDetail> ChoosePreferredAsync(Guid id, Guid? dumpId, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/titles/{id}/preferred", new ChoosePreferred(dumpId), RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TitleDetail>(RomdexJson.Options, ct))!;
    }

    /// <summary>
    /// Every game the DATs list, in the library or not, whose name has every word of <paramref name="q"/>, closest first,
    /// each once with its title on every system; <paramref name="have"/> keeps to games the library has on some system
    /// (true) or on none (false).
    /// </summary>
    public async Task<GamePage> SearchCatalogueAsync(string q, Guid? systemId = null, bool? have = null, int skip = 0, int take = 50,
        CancellationToken ct = default)
    {
        var query = $"q={Uri.EscapeDataString(q)}&skip={skip}&take={take}";
        if (systemId is { } id)
            query += $"&system={id}";
        if (have is { } h)
            query += $"&have={(h ? "true" : "false")}";
        return (await http.GetFromJsonAsync<GamePage>($"{Api}/catalogue/search?{query}", RomdexJson.Options, ct))!;
    }

    public async Task<TaskInfo> RefreshCatalogueAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/catalogue/refresh", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TaskInfo>(RomdexJson.Options, ct))!;
    }

    // ---- Library ----

    public async Task<LibraryStatus> GetLibraryStatusAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<LibraryStatus>($"{Api}/library", RomdexJson.Options, ct))!;

    /// <summary>Looks up covers for every game in the library without a fresh one, as a task.</summary>
    public Task<TaskInfo> LookUpCoversAsync(CancellationToken ct = default) => PostForAsync<TaskInfo>($"{Api}/library/covers", null, ct);

    public async Task<TaskInfo> ScanLibraryAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/library/scan", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<TaskInfo>(RomdexJson.Options, ct))!;
    }

    public async Task<TitlePage> ListMissingAsync(int skip = 0, int take = 100, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<TitlePage>($"{Api}/library/missing?skip={skip}&take={take}", RomdexJson.Options, ct))!;

    public async Task<UnknownPage> ListUnknownAsync(int skip = 0, int take = 100, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<UnknownPage>($"{Api}/library/unknown?skip={skip}&take={take}", RomdexJson.Options, ct))!;

    // ---- Downloads ----

    public async Task<DownloadsPage> ListDownloadsAsync(DownloadState? state = null, int skip = 0, int take = 50, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<DownloadsPage>($"{Api}/downloads?skip={skip}&take={take}{(state is { } s ? $"&state={s}" : "")}", RomdexJson.Options, ct))!;

    public Task<DownloadInfo?> GetDownloadAsync(Guid id, CancellationToken ct = default) => GetOrNullAsync<DownloadInfo>($"{Api}/downloads/{id}", ct);

    public Task<DownloadInfo> AcceptDownloadAsync(Guid id, CancellationToken ct = default) => PostForAsync<DownloadInfo>($"{Api}/downloads/{id}/accept", null, ct);

    public Task<DownloadInfo> RejectDownloadAsync(Guid id, CancellationToken ct = default) => PostForAsync<DownloadInfo>($"{Api}/downloads/{id}/reject", null, ct);

    public Task<DownloadInfo> RetryDownloadAsync(Guid id, CancellationToken ct = default) => PostForAsync<DownloadInfo>($"{Api}/downloads/{id}/retry", null, ct);

    /// <summary>Asks the plugins that hand over what arrived by themselves for anything new, now.</summary>
    public Task CheckDownloadsAsync(CancellationToken ct = default) => PostAsync($"{Api}/downloads/check", null, ct);

    /// <summary>Asks every retriever that searches for dumps of a title.</summary>
    public Task<DownloadSearchResult> SearchDownloadsAsync(Guid titleId, Guid? dumpId = null, CancellationToken ct = default) =>
        PostForAsync<DownloadSearchResult>($"{Api}/titles/{titleId}/search", new SearchDownloads(dumpId), ct);

    /// <summary>Gets a title, or one of its dumps: fetches the best offer that wasn't turned away before.</summary>
    /// <param name="replaces">Library files it replaces, such as a bad dump: deleted once what's fetched is filed and verifies.</param>
    public Task<TitleFetch> FetchTitleAsync(Guid titleId, Guid? dumpId = null, IReadOnlyList<Guid>? replaces = null, CancellationToken ct = default) =>
        PostForAsync<TitleFetch>($"{Api}/titles/{titleId}/fetch", new SearchDownloads(dumpId, replaces), ct);

    /// <summary>Requests, newest first; with <paramref name="active"/>, only those still going; <paramref name="before"/> pages back.</summary>
    public async Task<IReadOnlyList<RequestInfo>> ListRequestsAsync(bool active = false, int limit = 100, DateTime? before = null, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<RequestInfo>>($"{Api}/requests?active={(active ? "true" : "false")}&limit={limit}" +
            (before is { } b ? $"&before={Uri.EscapeDataString(b.ToUniversalTime().ToString("O"))}" : ""), RomdexJson.Options, ct) ?? [];

    /// <summary>Pauses, resumes or cancels many tasks at once.</summary>
    public Task<BulkChanged> ChangeRequestsAsync(BulkChange change, CancellationToken ct = default) => PostForAsync<BulkChanged>($"{Api}/requests/bulk", change, ct);

    public Task<RequestInfo> PauseRequestAsync(Guid id, CancellationToken ct = default) => PostForAsync<RequestInfo>($"{Api}/requests/{id}/pause", null, ct);

    public Task<RequestInfo> ResumeRequestAsync(Guid id, CancellationToken ct = default) => PostForAsync<RequestInfo>($"{Api}/requests/{id}/resume", null, ct);

    public Task<RequestInfo> CancelRequestAsync(Guid id, CancellationToken ct = default) => PostForAsync<RequestInfo>($"{Api}/requests/{id}/cancel", null, ct);

    /// <summary>Moves an unfinished request to just before another, or to the end of the queue.</summary>
    public Task<RequestInfo> MoveRequestAsync(Guid id, Guid? before, CancellationToken ct = default) =>
        PostForAsync<RequestInfo>($"{Api}/requests/{id}/move", new MoveInQueue(before), ct);

    public async Task<FetchSettings> GetFetchSettingsAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<FetchSettings>($"{Api}/settings/fetch", RomdexJson.Options, ct))!;

    public async Task SaveFetchSettingsAsync(FetchSettings settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/settings/fetch", settings, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public Task<BulkChanged> ChangeTasksAsync(BulkChange change, CancellationToken ct = default) => PostForAsync<BulkChanged>($"{Api}/tasks/bulk", change, ct);

    public Task<RequestInfo?> GetRequestAsync(Guid id, CancellationToken ct = default) => GetOrNullAsync<RequestInfo>($"{Api}/requests/{id}", ct);

    /// <summary>Looks again for a request that ran out of offers, passing over what was turned away.</summary>
    public Task<RequestInfo> TryRequestAgainAsync(Guid id, CancellationToken ct = default) => PostForAsync<RequestInfo>($"{Api}/requests/{id}/try-again", null, ct);

    /// <summary>Dismisses a request that ran out of offers.</summary>
    public Task<RequestInfo> DismissRequestAsync(Guid id, CancellationToken ct = default) => PostForAsync<RequestInfo>($"{Api}/requests/{id}/dismiss", null, ct);

    /// <summary>Looks again for what an arrival was fetched for, passing over what was turned away; one that arrived by itself is retried.</summary>
    public Task<TitleFetch> TryDownloadAgainAsync(Guid id, CancellationToken ct = default) => PostForAsync<TitleFetch>($"{Api}/downloads/{id}/try-again", null, ct);

    /// <summary>Has a plugin stage a candidate it offered for a title, to be identified and filed.</summary>
    public Task<DownloadInfo> StartDownloadAsync(StartDownload fetch, CancellationToken ct = default) => PostForAsync<DownloadInfo>($"{Api}/downloads/fetch", fetch, ct);

    /// <summary>Takes a library file that matches nothing, or can't be read, out of the library and through Downloads again.</summary>
    public Task<DownloadInfo> ReclaimLibraryFileAsync(Guid fileId, CancellationToken ct = default) =>
        PostForAsync<DownloadInfo>($"{Api}/library/files/{fileId}/download", null, ct);

    // ---- Scheduled tasks ----

    public async Task<ScheduleStatus> GetSchedulesAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<ScheduleStatus>($"{Api}/schedules", RomdexJson.Options, ct))!;

    public async Task SaveSchedulesAsync(ScheduleSettings settings, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/schedules", settings, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<Wishlist> GetWishlistAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<Wishlist>($"{Api}/wishlist", RomdexJson.Options, ct))!;

    public Task AddToWishlistAsync(AddToWishlist add, CancellationToken ct = default) => PostAsync($"{Api}/wishlist", add, ct);

    /// <summary>Puts every game a system is missing on your wishlist.</summary>
    public Task<AddedToWishlist> WishForMissingAsync(Guid systemId, CancellationToken ct = default) =>
        PostForAsync<AddedToWishlist>($"{Api}/systems/{systemId}/wishlist", null, ct);

    public async Task RemoveFromWishlistAsync(Guid titleId, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/wishlist/{titleId}", ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Get them all: asks for everything on your wishlist.</summary>
    public Task<AskedAgain> GetWishlistItemsAsync(CancellationToken ct = default) => PostForAsync<AskedAgain>($"{Api}/wishlist/get", null, ct);

    /// <summary>Retry failed: looks again for every request that ran out of offers.</summary>
    public Task<AskedAgain> RetryFailedAsync(CancellationToken ct = default) => PostForAsync<AskedAgain>($"{Api}/requests/retry-failed", null, ct);

    // ---- Review ----

    public async Task<ReviewSummary> GetReviewSummaryAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<ReviewSummary>($"{Api}/review", RomdexJson.Options, ct))!;

    // Files sort by path (the default), name, size or modified; dumps by name (the default), system or missing; fetches
    // by date (newest first unless told otherwise) or name.

    public Task<ReviewFilePage> ListBadDumpsAsync(int skip = 0, int take = 100, string? sort = null, bool descending = false, CancellationToken ct = default) =>
        ReviewFilesAsync("bad-dumps", skip, take, sort, descending, ct);

    public Task<ReviewFilePage> ListUnmatchedAsync(int skip = 0, int take = 100, string? sort = null, bool descending = false, CancellationToken ct = default) =>
        ReviewFilesAsync("matching", skip, take, sort, descending, ct);

    public Task<ReviewFilePage> ListOutdatedAsync(int skip = 0, int take = 100, string? sort = null, bool descending = false, CancellationToken ct = default) =>
        ReviewFilesAsync("outdated", skip, take, sort, descending, ct);

    public Task<ReviewFilePage> ListCorruptedAsync(int skip = 0, int take = 100, string? sort = null, bool descending = false, CancellationToken ct = default) =>
        ReviewFilesAsync("corrupted", skip, take, sort, descending, ct);

    /// <summary>Files someone deferred, or with <paramref name="hidden"/>, hid.</summary>
    public async Task<ReviewFilePage> ListSetAsideAsync(bool hidden = false, int skip = 0, int take = 100, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<ReviewFilePage>($"{Api}/review/set-aside?hidden={(hidden ? "true" : "false")}&skip={skip}&take={take}", RomdexJson.Options, ct))!;

    public async Task<ReviewDumpPage> ListIncompleteAsync(int skip = 0, int take = 100, string? sort = null, bool descending = false, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<ReviewDumpPage>($"{Api}/review/incomplete?skip={skip}&take={take}{Sorted(sort, descending)}", RomdexJson.Options, ct))!;

    public async Task<CouldntFetchPage> ListCouldntFetchAsync(int skip = 0, int take = 50, string? sort = null, bool? descending = null, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<CouldntFetchPage>($"{Api}/review/couldnt-fetch?skip={skip}&take={take}{(sort is null ? "" : $"&sort={sort}")}{(descending is { } d ? $"&descending={(d ? "true" : "false")}" : "")}",
            RomdexJson.Options, ct))!;

    private async Task<ReviewFilePage> ReviewFilesAsync(string tab, int skip, int take, string? sort, bool descending, CancellationToken ct) =>
        (await http.GetFromJsonAsync<ReviewFilePage>($"{Api}/review/{tab}?skip={skip}&take={take}{Sorted(sort, descending)}", RomdexJson.Options, ct))!;

    private static string Sorted(string? sort, bool descending) =>
        (sort is null ? "" : $"&sort={Uri.EscapeDataString(sort)}") + (descending ? "&descending=true" : "");

    public Task DeferFileAsync(Guid fileId, CancellationToken ct = default) => PostAsync($"{Api}/review/files/{fileId}/defer", null, ct);

    public Task HideFileAsync(Guid fileId, CancellationToken ct = default) => PostAsync($"{Api}/review/files/{fileId}/hide", null, ct);

    public Task PutBackFileAsync(Guid fileId, CancellationToken ct = default) => PostAsync($"{Api}/review/files/{fileId}/put-back", null, ct);

    /// <summary>Deletes a file that matches nothing, or can't be read, from the library.</summary>
    public Task<DownloadInfo> DeleteLibraryFileAsync(Guid fileId, CancellationToken ct = default) => PostForAsync<DownloadInfo>($"{Api}/review/files/{fileId}/delete", null, ct);

    public async Task<IReadOnlyList<ManualMatchInfo>> ListLinksAsync(Guid? titleId = null, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ManualMatchInfo>>($"{Api}/review/links{(titleId is { } id ? $"?titleId={id}" : "")}", RomdexJson.Options, ct) ?? [];

    /// <summary>Says what a file is: a dump, or a version of a game no DAT lists.</summary>
    public Task<ManualMatchInfo> LinkFileAsync(LinkFile link, CancellationToken ct = default) => PostForAsync<ManualMatchInfo>($"{Api}/review/links", link, ct);

    public async Task UnlinkFileAsync(Guid linkId, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/review/links/{linkId}", ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Keeps an incomplete dump as it is, without the files it's missing now.</summary>
    public Task KeepIncompleteAsync(Guid dumpId, CancellationToken ct = default) => PostAsync($"{Api}/review/dumps/{dumpId}/keep", null, ct);

    public async Task UnkeepIncompleteAsync(Guid dumpId, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/review/dumps/{dumpId}/keep", ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Deletes what the library has of an incomplete dump.</summary>
    public async Task<IReadOnlyList<DownloadInfo>> DeleteIncompleteAsync(Guid dumpId, CancellationToken ct = default) =>
        await PostForAsync<List<DownloadInfo>>($"{Api}/review/dumps/{dumpId}/delete", null, ct);

    public async Task<FilingSettings> GetFilingAsync(Guid systemId, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<FilingSettings>($"{Api}/systems/{systemId}/filing", RomdexJson.Options, ct))!;

    public async Task<IReadOnlyList<StorageFormat>> ListStorageFormatsAsync(Guid systemId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<StorageFormat>>($"{Api}/systems/{systemId}/filing/formats", RomdexJson.Options, ct) ?? [];

    /// <summary>Which of a system's games aren't where the layout would put them. Nothing is moved.</summary>
    public async Task<AdoptionReport> GetAdoptionAsync(Guid systemId, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<AdoptionReport>($"{Api}/systems/{systemId}/filing/adoption", RomdexJson.Options, ct))!;

    public async Task<FilingSettings> SetFilingAsync(Guid systemId, FilingSettings filing, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/systems/{systemId}/filing", filing, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<FilingSettings>(RomdexJson.Options, ct))!;
    }

    public async Task<LibraryFormat> GetLibraryFormatAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<LibraryFormat>($"{Api}/library/format", RomdexJson.Options, ct))!;

    public async Task<LibraryFormat> SetLibraryFormatAsync(LibraryFormat format, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/library/format", format, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<LibraryFormat>(RomdexJson.Options, ct))!;
    }

    /// <summary>The layout the library's games are in now, as near as it can be told.</summary>
    public async Task<LayoutDetection> DetectLibraryFormatAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<LayoutDetection>($"{Api}/library/format/detect", RomdexJson.Options, ct))!;

    /// <summary>What formatting the library, or one system's games, would move. Nothing is moved.</summary>
    public async Task<AdoptionReport> PreviewLibraryFormatAsync(Guid? systemId = null, CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<AdoptionReport>($"{Api}/library/format/preview{(systemId is { } id ? $"?systemId={id}" : "")}", RomdexJson.Options, ct))!;

    /// <summary>Moves the library's games, or one system's, to where the layout puts them, as a task.</summary>
    public Task<TaskInfo> FormatLibraryAsync(Guid? systemId = null, CancellationToken ct = default) =>
        PostForAsync<TaskInfo>($"{Api}/library/format/apply", new FormatLibrary(systemId), ct);

    // ---- Stopping and ordering tasks ----

    public Task CancelTaskAsync(Guid id, CancellationToken ct = default) => PostAsync($"{Api}/tasks/{id}/cancel", null, ct);

    public Task PauseTaskAsync(Guid id, CancellationToken ct = default) => PostAsync($"{Api}/tasks/{id}/pause", null, ct);

    public Task ResumeTaskAsync(Guid id, CancellationToken ct = default) => PostAsync($"{Api}/tasks/{id}/resume", null, ct);

    public Task MoveTaskAsync(Guid id, Guid? before, CancellationToken ct = default) => PostAsync($"{Api}/tasks/{id}/move", new MoveInQueue(before), ct);

    private async Task PostAsync(string url, object? body, CancellationToken ct)
    {
        using var response = body is null ? await http.PostAsync(url, null, ct) : await http.PostAsJsonAsync(url, body, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    // ---- Access ----

    public async Task<AuthStatus> GetAuthStatusAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<AuthStatus>($"{Api}/auth/status", RomdexJson.Options, ct))!;

    /// <summary>Makes the admin account, the first time, and signs in with it.</summary>
    public async Task SetUpLoginAsync(SetUpLogin login, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/auth/setup", login, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task SignInAsync(SignIn signIn, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/auth/login", signIn, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/auth/logout", null, ct);
        await EnsureAsync(response, ct);
    }

    public async Task UpdateLoginAsync(UpdateLogin update, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/auth/account", update, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<ServerVersion> GetVersionAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<ServerVersion>($"{Api}/version", RomdexJson.Options, ct))!;

    public async Task<SetupStatus> GetSetupAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<SetupStatus>($"{Api}/setup", RomdexJson.Options, ct))!;

    /// <summary>Setup is finished or skipped: the web app stops showing its steps.</summary>
    public async Task FinishSetupAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/setup/done", null, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Resets Romdex as it was first installed. The admin only; Romdex restarts.</summary>
    public async Task FactoryResetAsync(string confirm, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/setup/reset", new FactoryResetRequest(confirm), RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Keeps how the web app looks with your account.</summary>
    public async Task SetAppearanceAsync(Appearance appearance, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/auth/appearance", appearance, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Go back to everyone's look; returns it.</summary>
    public async Task<Appearance> ClearAppearanceAsync(CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/auth/appearance", ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<Appearance>(RomdexJson.Options, ct))!;
    }

    public async Task<Appearance> GetDefaultAppearanceAsync(CancellationToken ct = default) =>
        (await http.GetFromJsonAsync<Appearance>($"{Api}/auth/appearance/default", RomdexJson.Options, ct))!;

    /// <summary>How Romdex looks for everyone who hasn't chosen their own look.</summary>
    public async Task SetDefaultAppearanceAsync(Appearance appearance, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/auth/appearance/default", appearance, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<IReadOnlyList<UserInfo>> ListUsersAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<UserInfo>>($"{Api}/users", RomdexJson.Options, ct) ?? [];

    public async Task<UserInfo> CreateUserAsync(CreateUser user, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/users", user, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<UserInfo>(RomdexJson.Options, ct))!;
    }

    public async Task<UserInfo> UpdateUserAsync(Guid id, UpdateUser update, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/users/{id}", update, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<UserInfo>(RomdexJson.Options, ct))!;
    }

    public async Task RemoveUserAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/users/{id}", ct);
        await EnsureAsync(response, ct);
    }

    public async Task<IReadOnlyList<ApiKeyInfo>> ListApiKeysAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<ApiKeyInfo>>($"{Api}/keys", RomdexJson.Options, ct) ?? [];

    /// <summary>Makes a key. Its <see cref="CreatedApiKey.Key"/> can't be had again, so show or keep it now.</summary>
    public async Task<CreatedApiKey> CreateApiKeyAsync(CreateApiKey key, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/keys", key, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<CreatedApiKey>(RomdexJson.Options, ct))!;
    }

    public async Task RevokeApiKeyAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/keys/{id}", ct);
        await EnsureAsync(response, ct);
    }

    // ---- Webhooks ----

    public async Task<IReadOnlyList<WebhookInfo>> ListWebhooksAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<WebhookInfo>>($"{Api}/webhooks", RomdexJson.Options, ct) ?? [];

    public async Task<WebhookInfo> CreateWebhookAsync(SaveWebhook webhook, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/webhooks", webhook, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<WebhookInfo>(RomdexJson.Options, ct))!;
    }

    public async Task<WebhookInfo> UpdateWebhookAsync(Guid id, SaveWebhook webhook, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Api}/webhooks/{id}", webhook, RomdexJson.Options, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<WebhookInfo>(RomdexJson.Options, ct))!;
    }

    public async Task<WebhookInfo> RotateWebhookSecretAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/webhooks/{id}/secret", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<WebhookInfo>(RomdexJson.Options, ct))!;
    }

    public async Task RemoveWebhookAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Api}/webhooks/{id}", ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Sends a ping now and says how it went.</summary>
    public async Task<WebhookDeliveryInfo> TestWebhookAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Api}/webhooks/{id}/test", null, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<WebhookDeliveryInfo>(RomdexJson.Options, ct))!;
    }

    public async Task<IReadOnlyList<WebhookDeliveryInfo>> ListWebhookDeliveriesAsync(Guid id, int limit = 50, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<WebhookDeliveryInfo>>($"{Api}/webhooks/{id}/deliveries?limit={limit}", RomdexJson.Options, ct) ?? [];

    /// <summary>Throws <see cref="RomdexApiException"/> with the server's own explanation for anything but success.</summary>
    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        string? message = null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(RomdexJson.Options, ct);
            if (problem.TryGetProperty("errors", out var errors) && errors.ValueKind == System.Text.Json.JsonValueKind.Object)
                message = string.Join(" ", errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetString())));
            else if (problem.TryGetProperty("detail", out var detail))
                message = detail.GetString();
            else if (problem.TryGetProperty("title", out var title))
                message = title.GetString();
        }
        catch (System.Text.Json.JsonException)
        {
        }
        throw new RomdexApiException(response.StatusCode, string.IsNullOrWhiteSpace(message) ? $"{(int)response.StatusCode} {response.ReasonPhrase}" : message);
    }

    private async Task<T> PostForAsync<T>(string url, object? body, CancellationToken ct)
    {
        using var response = body is null ? await http.PostAsync(url, null, ct) : await http.PostAsync(url, JsonContent.Create(body, body.GetType(), options: RomdexJson.Options), ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(RomdexJson.Options, ct))!;
    }

    private async Task<T?> GetOrNullAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(RomdexJson.Options, ct);
    }
}

/// <summary>The server refused a request, with its reason.</summary>
public sealed class RomdexApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
