using System.Text.Json;
using Microsoft.Extensions.Options;
using Romdex.Client;

namespace Romdex.Core.Tasks;

/// <summary>
/// What finished tasks did, row by row, kept on disk under <c>data/tasks</c> so every task keeps its list: one file a
/// task, written once it's done. Files older than <see cref="LogSettings.TaskDays"/> are deleted with the log's.
/// </summary>
public sealed class TaskArchive(IOptions<RomdexOptions> options, ILogger<TaskArchive> logger)
{
    private static readonly JsonSerializerOptions Json = RomdexJson.Options;

    private string Directory => Path.Combine(options.Value.DataPath, "tasks");

    private string FileFor(Guid task, string what) => Path.Combine(Directory, $"{task:N}.{what}.json");

    /// <summary>Writes a task's rows, replacing any it had. A failure is logged: the task itself went fine.</summary>
    public void Save<T>(Guid task, string what, IReadOnlyList<T> rows)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var file = FileFor(task, what);
            var writing = file + ".tmp";
            using (var stream = File.Create(writing))
                JsonSerializer.Serialize(stream, rows, Json);
            File.Move(writing, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Couldn't keep the list of task {Task}", task);
        }
    }

    /// <summary>A finished task's rows; null when none were kept, or they've been deleted since.</summary>
    public List<T>? Load<T>(Guid task, string what)
    {
        var file = FileFor(task, what);
        try
        {
            if (!File.Exists(file))
                return null;
            using var stream = File.OpenRead(file);
            return JsonSerializer.Deserialize<List<T>>(stream, Json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(e, "Couldn't read the list of task {Task}", task);
            return null;
        }
    }

    /// <summary>Deletes lists written more than <paramref name="days"/> days ago.</summary>
    public void Tidy(int days)
    {
        if (!System.IO.Directory.Exists(Directory))
            return;
        var oldest = DateTime.UtcNow - TimeSpan.FromDays(days);
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
            if (File.GetLastWriteTimeUtc(file) < oldest)
                File.Delete(file);
    }
}
