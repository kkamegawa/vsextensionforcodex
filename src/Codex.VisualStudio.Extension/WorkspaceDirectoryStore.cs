using System.IO;
using System.Text.Json;

namespace Codex.VisualStudio.Extension;

/// <summary>
/// Persists the working directory the user chose for Codex when no solution, project, or
/// folder is open, so the choice does not have to be repeated on every session.
/// </summary>
public interface IWorkspaceDirectoryStore
{
    string? GetLastDirectory();

    void SetLastDirectory(string? directory);
}

/// <summary>
/// Stores the last chosen working directory as a small JSON file under
/// <c>%LOCALAPPDATA%\CodexForVisualStudio\workspace.json</c>. Reads tolerate a missing or
/// corrupt file by returning <see langword="null"/>; writes are best-effort.
/// </summary>
public sealed class WorkspaceDirectoryStore : IWorkspaceDirectoryStore
{
    private readonly string filePath;

    public WorkspaceDirectoryStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexForVisualStudio",
            "workspace.json"))
    {
    }

    public WorkspaceDirectoryStore(string filePath)
    {
        this.filePath = filePath;
    }

    public string? GetLastDirectory()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            string json = File.ReadAllText(filePath);
            WorkspaceDirectoryData? data = JsonSerializer.Deserialize<WorkspaceDirectoryData>(json);
            return string.IsNullOrWhiteSpace(data?.LastDirectory) ? null : data.LastDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void SetLastDirectory(string? directory)
    {
        try
        {
            string? directoryPart = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directoryPart))
            {
                Directory.CreateDirectory(directoryPart);
            }

            string json = JsonSerializer.Serialize(new WorkspaceDirectoryData { LastDirectory = directory });
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persistence is best-effort: failing to remember the choice should not break
            // the connection that is about to proceed with it.
        }
    }

    private sealed class WorkspaceDirectoryData
    {
        public string? LastDirectory { get; set; }
    }
}
