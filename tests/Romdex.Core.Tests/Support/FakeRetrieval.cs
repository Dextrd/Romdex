using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Romdex.Contracts.Plugins;
using Romdex.Contracts.Plugins.Retrieval;
using Romdex.Core.Downloads;
using Romdex.Core.Library;

namespace Romdex.Core.Tests.Support;

/// <summary>
/// Stands in for the igir worker: finds the DAT's files in staging by SHA-1, with or without an iNES header, and files
/// them as igir would, under a folder for the layout. It doesn't check what igir would refuse.
/// </summary>
public sealed class FakeWorker(string staging, string library) : IFilingWorker
{
    public List<FileRequest> Filed { get; } = [];

    // What each "CHD" holds, by its full path: the fake converts by noting the tracks beside the sheet.
    private readonly Dictionary<string, List<ContentHashes>> _discs = [];

    public Task<WorkerVersion> GetVersionAsync(CancellationToken ct) => Task.FromResult(new WorkerVersion("test", "5.8.0", null, null));

    public Task<FileResult> FileAsync(FileRequest request, CancellationToken ct)
    {
        Filed.Add(request);
        var dat = XDocument.Parse(request.Dat);
        var system = dat.Root!.Element("header")!.Element("name")!.Value;
        var game = dat.Root.Element("game")!;
        var name = game.Attribute("name")!.Value;
        var roms = game.Elements("rom").ToDictionary(r => r.Attribute("sha1")!.Value, r => r.Attribute("name")!.Value);
        var directory = Path.Combine(staging, request.StagingDirectory);

        var found = new List<(string Rom, byte[] Data, string Source)>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (_discs.TryGetValue(path, out var tracks) && tracks.All(t => roms.ContainsKey(t.Sha1)))
            {
                var relative = Path.Combine(request.Layout == "romm" ? "romm" : system, name + ".chd");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(library, relative))!);
                File.Move(path, Path.Combine(library, relative));
                _discs[Path.Combine(library, relative)] = tracks;
                return Task.FromResult(new FileResult([relative.Replace('\\', '/')]));
            }
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(path);
                foreach (var entry in zip.Entries)
                {
                    using var stream = entry.Open();
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    if (Rom(memory.ToArray()) is { } rom)
                        found.Add((rom, memory.ToArray(), path));
                }
            }
            else if (Rom(File.ReadAllBytes(path)) is { } rom)
                found.Add((rom, File.ReadAllBytes(path), path));
        }
        if (found.Count < roms.Count)
            throw new FilingWorkerException($"igir found {found.Count} of {name}'s {roms.Count} files.");

        var folder = request.Layout == "romm" ? "romm" : system;
        if (roms.Count > 1)
            folder = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.Combine(library, folder));
        var written = new List<string>();
        if (request.Mode == FilingMode.Zip)
        {
            var relative = Path.Combine(folder, name + ".zip");
            using (var zip = ZipFile.Open(Path.Combine(library, relative), ZipArchiveMode.Create))
                foreach (var (rom, data, _) in found)
                {
                    using var stream = zip.CreateEntry(rom).Open();
                    stream.Write(data);
                }
            written.Add(relative.Replace('\\', '/'));
        }
        else
            foreach (var (rom, data, _) in found)
            {
                var relative = Path.Combine(folder, rom);
                File.WriteAllBytes(Path.Combine(library, relative), data);
                written.Add(relative.Replace('\\', '/'));
            }
        foreach (var source in found.Select(f => f.Source).Distinct())
            File.Delete(source);
        return Task.FromResult(new FileResult(written));

        string? Rom(byte[] data)
        {
            if (roms.TryGetValue(Convert.ToHexStringLower(SHA1.HashData(data)), out var rom))
                return rom;
            return data.Length > 16 && data.AsSpan(0, 4).SequenceEqual("NES\x1A"u8) && roms.TryGetValue(Convert.ToHexStringLower(SHA1.HashData(data.AsSpan(16))), out rom)
                ? rom : null;
        }
    }

    public List<ConvertRequest> Converted { get; } = [];

    /// <summary>Puts a "CHD" of these tracks in the library, as if converted before Romdex was there.</summary>
    public void PutChd(string relative, params byte[][] tracks)
    {
        var path = Path.Combine(library, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "a made-up CHD");
        _discs[path] = tracks
            .Select(d => new ContentHashes(null, d.Length, ContentHasher.Crc32Of(d), Convert.ToHexStringLower(MD5.HashData(d)), Convert.ToHexStringLower(SHA1.HashData(d))))
            .ToList();
    }

    /// <summary>Makes CHDs that can't be read back, as chdman does with a disc of a few sectors.</summary>
    public bool Unreadable { get; set; }

    public Task<ConvertResult> ConvertAsync(ConvertRequest request, CancellationToken ct)
    {
        Converted.Add(request);
        var source = Path.Combine(staging, request.StagingDirectory, request.Source);
        var output = Path.ChangeExtension(source, "." + request.Format);
        File.WriteAllText(output, "a made-up CHD");
        if (Unreadable)
            return Task.FromResult(new ConvertResult(Path.GetRelativePath(Path.Combine(staging, request.StagingDirectory), output)));
        _discs[output] = Directory.EnumerateFiles(Path.GetDirectoryName(source)!).Where(f => f != source && f != output).Order(StringComparer.Ordinal)
            .Select(f => File.ReadAllBytes(f))
            .Select(d => new ContentHashes(null, d.Length, ContentHasher.Crc32Of(d), Convert.ToHexStringLower(MD5.HashData(d)), Convert.ToHexStringLower(SHA1.HashData(d))))
            .ToList();
        return Task.FromResult(new ConvertResult(Path.GetRelativePath(Path.Combine(staging, request.StagingDirectory), output)));
    }

    public Task<FolderResult> FolderAsync(FolderRequest request, CancellationToken ct) =>
        Task.FromResult(new FolderResult(request.Layout == "romm" ? "romm" : request.DatName));

    public Task<StageResult> StageAsync(StageRequest request, CancellationToken ct)
    {
        var source = Path.Combine(library, request.Path);
        var target = Path.Combine(staging, request.StagingDirectory, Path.GetFileName(source));
        if (!File.Exists(source))
            throw new FilingWorkerException($"{request.Path} isn't a file in the library.");
        File.Move(source, target);
        return Task.FromResult(new StageResult([Path.GetFileName(target)]));
    }

    public Task<ReadResult> ReadAsync(ReadRequest request, CancellationToken ct)
    {
        var path = Path.Combine(request.Root == "staging" ? staging : library, request.Path);
        return _discs.TryGetValue(path, out var contents)
            ? Task.FromResult(new ReadResult(contents))
            : throw new FilingWorkerException($"The fake worker has no CHD at {path}.");
    }
}

/// <summary>
/// A retriever answering over HTTP, in process: it lists what's been dropped into it and offers the same for any
/// search, and stages a candidate by writing its files into the folder the core gives it.
/// </summary>
public sealed class FakeRetriever(string staging) : HttpMessageHandler
{
    public const string Endpoint = "http://fake-retriever/";

    private readonly Dictionary<string, (Candidate Candidate, List<(string Name, byte[] Data)> Files)> _items = [];

    /// <summary>Something that arrived, listed until it's staged.</summary>
    public void Drop(string name, params (string Name, byte[] Data)[] files) =>
        _items[name] = (new Candidate(name, name, "the test", Notes: $"{files.Length} files"), files.ToList());

    public IReadOnlyList<SearchRequest> Searches => _searches;

    private readonly List<SearchRequest> _searches = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        return path switch
        {
            "v1/manifest" => Json(new PluginManifest
            {
                Id = "test.retrieval.fake",
                Name = "Fake retriever",
                Version = "1.0.0",
                Type = PluginType.Retrieval,
                ContractVersion = "1.0",
                Capabilities = [RetrievalCapabilities.Search, RetrievalCapabilities.List],
            }),
            "v1/health" => Json(HealthReport.Healthy()),
            "v1/list" => Json(new ListResponse(_items.Values.Select(i => i.Candidate).ToList())),
            "v1/search" => Search(await Read<SearchRequest>(request, ct)),
            "v1/fetch" => Fetch(await Read<FetchRequest>(request, ct)),
            _ when path.StartsWith("v1/fetch/") => Json(new FetchStatus(path["v1/fetch/".Length..], FetchState.Completed)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private HttpResponseMessage Search(SearchRequest search)
    {
        _searches.Add(search);
        return Json(new SearchResponse(_items.Values.Select(i => i.Candidate).ToList()));
    }

    private HttpResponseMessage Fetch(FetchRequest fetch)
    {
        var (_, files) = _items[fetch.Candidate.CandidateId];
        _items.Remove(fetch.Candidate.CandidateId);
        foreach (var (name, data) in files)
        {
            var path = Path.Combine(staging, fetch.StagingDirectory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
        return Json(new FetchStatus(fetch.JobId, FetchState.Completed));
    }

    private static async Task<T> Read<T>(HttpRequestMessage request, CancellationToken ct) =>
        (await request.Content!.ReadFromJsonAsync<T>(RetrievalJson.Options, ct))!;

    private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: RetrievalJson.Options) };
}

/// <summary>Builds the bytes of a zip, for staging one.</summary>
public static class Zips
{
    public static byte[] Of(params (string Name, byte[] Data)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in files)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(data);
            }
        return memory.ToArray();
    }
}
