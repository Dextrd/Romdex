using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Romdex.Core.Data;
using Romdex.Core.Library;

namespace Romdex.Core.Downloads;

// The igir worker: the only thing that writes the library. It runs in its own container behind a small HTTP API
// (igir-worker/README.md), since igir is a Node tool under the GPL and so are chdman and DolphinTool. The core says
// exactly which dump the files are, as a DAT of one entry, so igir confirms a known target instead of guessing.

/// <summary>How igir writes a dump into the library.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FilingMode>))]
public enum FilingMode
{
    /// <summary>As it is: a loose file stays loose, an archive stays that archive, renamed.</summary>
    Move,

    /// <summary>Out of any archive, as loose files.</summary>
    Extract,

    /// <summary>Into a zip of its own.</summary>
    Zip,
}

/// <summary>Body of <c>POST /v1/file</c>.</summary>
/// <param name="StagingDirectory">Where the files are, relative to the staging volume.</param>
/// <param name="Dat">A Logiqx DAT of the one dump to file, named as the system's DAT is.</param>
/// <param name="Folder">The folder it goes in, relative to the library, from <see cref="LibraryFolders"/>.</param>
public sealed record FileRequest(string StagingDirectory, string Dat, string Folder, FilingMode Mode);

/// <param name="Files">Where each file went, relative to the library folder.</param>
public sealed record FileResult(IReadOnlyList<string> Files);

/// <summary>Body of <c>POST /v1/convert</c>.</summary>
/// <param name="StagingDirectory">Where the files are, relative to the staging volume.</param>
/// <param name="Format"><c>chd</c> or <c>rvz</c>.</param>
/// <param name="Source">The file to convert, relative to the staging directory: a cue sheet, gdi or iso.</param>
public sealed record ConvertRequest(string StagingDirectory, string Format, string Source);

/// <param name="File">The converted file, relative to the staging directory, beside the source. The source is kept.</param>
public sealed record ConvertResult(string File);

/// <summary>Body of <c>POST /v1/read</c>: what a CHD or RVZ holds, hashed, since only the worker has the tools.</summary>
/// <param name="Root"><c>staging</c> or <c>library</c>.</param>
/// <param name="Path">Relative to the root.</param>
public sealed record ReadRequest(string Root, string Path);

public sealed record ReadResult(IReadOnlyList<ContentHashes> Contents);

/// <summary>Body of <c>POST /v1/folder</c>: the folder a layout gives a system's games.</summary>
/// <param name="Layout"><c>datName</c>, or one of igir's console tokens, such as <c>romm</c>, <c>es</c> or <c>batocera</c>.</param>
/// <param name="Files">File names of one of its games, for a system igir knows only by extension.</param>
public sealed record FolderRequest(string DatName, string Layout, IReadOnlyList<string> Files);

/// <param name="Folder">Relative to the library.</param>
public sealed record FolderResult(string Folder);

/// <summary>Body of <c>POST /v1/stage</c>: moves a library file into staging, to go through Downloads again.</summary>
/// <param name="Path">Relative to the library.</param>
/// <param name="StagingDirectory">Relative to the staging volume.</param>
public sealed record StageRequest(string Path, string StagingDirectory);

/// <param name="Files">Where it went, relative to the staging directory.</param>
public sealed record StageResult(IReadOnlyList<string> Files);

/// <summary>Body of <c>POST /v1/move</c>: moves library files to other places in it, for formatting.</summary>
public sealed record MoveRequest(IReadOnlyList<LibraryMove> Moves);

/// <param name="From">Relative to the library.</param>
/// <param name="To">Relative to the library.</param>
public sealed record LibraryMove(string From, string To);

/// <param name="Error">Why it didn't move, such as another file being there.</param>
public sealed record FailedMove(string From, string To, string Error);

public sealed record MoveResult(IReadOnlyList<LibraryMove> Moved, IReadOnlyList<FailedMove> Failed);

/// <summary>Answer to <c>GET /v1/version</c>.</summary>
public sealed record WorkerVersion(string Version, string Igir, string? Chdman, string? DolphinTool);

/// <summary>The igir worker said no, with its reason.</summary>
public sealed class FilingWorkerException(string message) : Exception(message);

public interface IFilingWorker
{
    Task<WorkerVersion> GetVersionAsync(CancellationToken ct);
    Task<FileResult> FileAsync(FileRequest request, CancellationToken ct);
    Task<ConvertResult> ConvertAsync(ConvertRequest request, CancellationToken ct);
    Task<ReadResult> ReadAsync(ReadRequest request, CancellationToken ct);
    Task<StageResult> StageAsync(StageRequest request, CancellationToken ct);
    Task<FolderResult> FolderAsync(FolderRequest request, CancellationToken ct);
    Task<MoveResult> MoveAsync(MoveRequest request, CancellationToken ct);
}

/// <summary>The igir worker, reached at <see cref="RomdexOptions.WorkerUrl"/>.</summary>
public sealed class HttpFilingWorker(HttpClient http, IOptions<RomdexOptions> options) : IFilingWorker
{
    public const string HttpClientName = "igir-worker";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public Task<WorkerVersion> GetVersionAsync(CancellationToken ct) => SendAsync<WorkerVersion>(HttpMethod.Get, "v1/version", null, ct);

    public Task<FileResult> FileAsync(FileRequest request, CancellationToken ct) => SendAsync<FileResult>(HttpMethod.Post, "v1/file", request, ct);

    public Task<ConvertResult> ConvertAsync(ConvertRequest request, CancellationToken ct) => SendAsync<ConvertResult>(HttpMethod.Post, "v1/convert", request, ct);

    public Task<ReadResult> ReadAsync(ReadRequest request, CancellationToken ct) => SendAsync<ReadResult>(HttpMethod.Post, "v1/read", request, ct);

    public Task<StageResult> StageAsync(StageRequest request, CancellationToken ct) => SendAsync<StageResult>(HttpMethod.Post, "v1/stage", request, ct);

    public Task<FolderResult> FolderAsync(FolderRequest request, CancellationToken ct) => SendAsync<FolderResult>(HttpMethod.Post, "v1/folder", request, ct);

    public Task<MoveResult> MoveAsync(MoveRequest request, CancellationToken ct) => SendAsync<MoveResult>(HttpMethod.Post, "v1/move", request, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.WorkerUrl))
            throw new FilingWorkerException("No igir worker is set up: set Romdex__WorkerUrl to its address, such as http://igir-worker:8080.");
        var url = new Uri(new Uri(options.Value.WorkerUrl.TrimEnd('/') + "/"), path);
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new FilingWorkerException($"The igir worker at {options.Value.WorkerUrl} can't be reached: {e.Message}");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string? error = null;
                try
                {
                    error = (await response.Content.ReadFromJsonAsync<WorkerError>(Json, ct))?.Error;
                }
                catch (JsonException)
                {
                }
                throw new FilingWorkerException(error ?? $"The igir worker answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
        }
    }

    private sealed record WorkerError(string Error);
}

/// <summary>Writes the DAT of one dump that the worker files by.</summary>
public static class OneEntryDat
{
    public static string For(string systemName, Dump dump, IEnumerable<DumpFile> files)
    {
        var game = new XElement("game", new XAttribute("name", dump.Name), new XElement("description", dump.Description ?? dump.Name));
        foreach (var file in files.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var rom = new XElement("rom", new XAttribute("name", file.Name), new XAttribute("size", file.Size));
            if (file.Crc32 is not null)
                rom.Add(new XAttribute("crc", file.Crc32));
            if (file.Md5 is not null)
                rom.Add(new XAttribute("md5", file.Md5));
            if (file.Sha1 is not null)
                rom.Add(new XAttribute("sha1", file.Sha1));
            game.Add(rom);
        }
        var dat = new XDocument(new XElement("datafile",
            new XElement("header", new XElement("name", systemName), new XElement("description", systemName), new XElement("version", "romdex")),
            game));
        return dat.Declaration + dat.ToString();
    }
}
