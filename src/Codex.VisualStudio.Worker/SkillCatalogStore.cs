using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

internal interface ISkillCatalogStore
{
    ValueTask<ListSkillsResult?> TryReadAsync(
        string workspace,
        string? codexVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask WriteAsync(
        string workspace,
        string? codexVersion,
        ListSkillsResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask DeleteAsync(string workspace, CancellationToken cancellationToken);
}

internal sealed class FileSkillCatalogStore : ISkillCatalogStore
{
    internal const int MaximumSkills = 200;
    internal const long MaximumWorkspaceBytes = 4L * 1024 * 1024;
    internal const long MaximumTotalBytes = 64L * 1024 * 1024;
    internal static readonly TimeSpan HardExpiry = TimeSpan.FromHours(24);

    private const int FormatVersion = 1;
    private const int MaximumNameLength = 128;
    private const int MaximumTextLength = 512;
    private const int MaximumPathLength = 1024;
    private const int MaximumScopeLength = 64;
    private readonly ISecretRedactor redactor;
    private readonly string rootDirectory;
    private readonly string mutexName;

    public FileSkillCatalogStore(ISecretRedactor redactor, string? rootDirectory = null)
    {
        this.redactor = redactor;
        this.rootDirectory = Path.GetFullPath(rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kkamegawa.CodexForVisualStudio",
            "skill-catalog",
            "v1"));
        mutexName = $"Local\\Kkamegawa.CodexForVisualStudio.SkillCatalog.{Hash(this.rootDirectory)}";
    }

    internal string RootDirectory => rootDirectory;

    public ValueTask<ListSkillsResult?> TryReadAsync(
        string workspace,
        string? codexVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string workspaceKey = ComputeWorkspaceKey(workspace);
        string cachePath = GetCachePath(workspaceKey);
        try
        {
            var info = new FileInfo(cachePath);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumWorkspaceBytes)
            {
                return ValueTask.FromResult<ListSkillsResult?>(null);
            }

            byte[] json = File.ReadAllBytes(cachePath);
            PersistedCatalog? catalog = JsonSerializer.Deserialize<PersistedCatalog>(json);
            if (catalog is null
                || catalog.Version != FormatVersion
                || !string.Equals(catalog.WorkspaceKey, workspaceKey, StringComparison.Ordinal)
                || !VersionsMatch(catalog.CodexVersion, codexVersion)
                || catalog.WrittenAtUtc > now
                || now - catalog.WrittenAtUtc >= HardExpiry
                || catalog.Skills is null
                || catalog.Skills.Count > MaximumSkills)
            {
                DeleteFile(cachePath);
                return ValueTask.FromResult<ListSkillsResult?>(null);
            }

            var skills = new List<SkillInfo>(catalog.Skills.Count);
            foreach (PersistedSkill persisted in catalog.Skills)
            {
                SkillInfo? skill = Revalidate(persisted);
                if (skill is null)
                {
                    DeleteFile(cachePath);
                    return ValueTask.FromResult<ListSkillsResult?>(null);
                }

                skills.Add(skill);
            }

            File.SetLastAccessTimeUtc(cachePath, now.UtcDateTime);
            return ValueTask.FromResult<ListSkillsResult?>(new ListSkillsResult
            {
                Skills = skills,
                IsTruncated = catalog.IsTruncated,
                IsStale = true,
            });
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or ArgumentException
            or CryptographicException)
        {
            return ValueTask.FromResult<ListSkillsResult?>(null);
        }
    }

    public ValueTask WriteAsync(
        string workspace,
        string? codexVersion,
        ListSkillsResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.IsSupported || result.IsStale || result.Skills.Count > MaximumSkills)
        {
            return ValueTask.CompletedTask;
        }

        string workspaceKey = ComputeWorkspaceKey(workspace);
        var catalog = new PersistedCatalog
        {
            Version = FormatVersion,
            WorkspaceKey = workspaceKey,
            CodexVersion = codexVersion,
            WrittenAtUtc = now,
            IsTruncated = result.IsTruncated,
            Skills = result.Skills.Select(ToPersisted).ToList(),
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(catalog);
        if (json.LongLength > MaximumWorkspaceBytes)
        {
            return ValueTask.CompletedTask;
        }

        Directory.CreateDirectory(rootDirectory);
        using var mutex = new Mutex(false, mutexName);
        bool acquired = false;
        string? temporaryPath = null;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new IOException("Timed out waiting for the skill catalog cache lock.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            temporaryPath = Path.Combine(rootDirectory, $".{workspaceKey}.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(temporaryPath, json);
            string cachePath = GetCachePath(workspaceKey);
            File.Move(temporaryPath, cachePath, overwrite: true);
            temporaryPath = null;
            File.SetLastAccessTimeUtc(cachePath, now.UtcDateTime);
            PruneLeastRecentlyUsed(cachePath);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                DeleteFile(temporaryPath);
            }

            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string workspace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteFile(GetCachePath(ComputeWorkspaceKey(workspace)));
        return ValueTask.CompletedTask;
    }

    internal static string ComputeWorkspaceKey(string workspace)
    {
        string canonical = Path.GetFullPath(workspace)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            canonical = canonical.ToUpperInvariant();
        }

        return Hash(canonical);
    }

    private string GetCachePath(string workspaceKey)
        => Path.Combine(rootDirectory, $"{workspaceKey}.json");

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool VersionsMatch(string? cached, string? current)
        => string.IsNullOrWhiteSpace(cached)
        || string.IsNullOrWhiteSpace(current)
        || string.Equals(cached, current, StringComparison.Ordinal);

    private PersistedSkill ToPersisted(SkillInfo skill) => new()
    {
        Name = skill.Name,
        Description = skill.Description,
        ShortDescription = skill.ShortDescription,
        DisplayName = skill.DisplayName,
        Scope = skill.Scope,
        Path = skill.Path,
        Enabled = skill.Enabled,
        BrandColor = skill.BrandColor,
    };

    private SkillInfo? Revalidate(PersistedSkill persisted)
    {
        string? name = NormalizeText(persisted.Name, MaximumNameLength, required: true);
        string? description = NormalizeText(persisted.Description, MaximumTextLength, required: true);
        string? scope = NormalizeText(persisted.Scope, MaximumScopeLength, required: true);
        string? path = NormalizePath(persisted.Path);
        if (name is null || description is null || scope is null || path is null)
        {
            return null;
        }

        string? brandColor = persisted.BrandColor;
        if (brandColor is not null
            && (brandColor.Length != 7
                || brandColor[0] != '#'
                || !brandColor.AsSpan(1).ToArray().All(Uri.IsHexDigit)))
        {
            return null;
        }

        return new SkillInfo
        {
            Name = name,
            Description = description,
            ShortDescription = NormalizeText(persisted.ShortDescription, MaximumTextLength, required: false),
            DisplayName = NormalizeText(persisted.DisplayName, MaximumTextLength, required: false),
            Scope = scope,
            Path = path,
            Enabled = persisted.Enabled,
            BrandColor = brandColor?.ToUpperInvariant(),
        };
    }

    private string? NormalizeText(string? value, int maximumLength, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return required ? null : value;
        }

        string redacted = redactor.Redact(value);
        string trimmed = redacted.Trim();
        return trimmed.Length <= maximumLength && trimmed.All(character => !char.IsControl(character))
            ? trimmed
            : null;
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= MaximumPathLength
            && trimmed.All(character => !char.IsControl(character))
            && Path.IsPathRooted(trimmed)
                ? trimmed
                : null;
    }

    private void PruneLeastRecentlyUsed(string currentPath)
    {
        FileInfo[] files = new DirectoryInfo(rootDirectory)
            .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(file => file.LastAccessTimeUtc)
            .ThenBy(file => file.LastWriteTimeUtc)
            .ToArray();
        long totalBytes = files.Sum(file => file.Length);
        foreach (FileInfo file in files)
        {
            if (totalBytes <= MaximumTotalBytes)
            {
                break;
            }

            if (string.Equals(file.FullName, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long length = file.Length;
            DeleteFile(file.FullName);
            totalBytes -= length;
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache cleanup is best effort. A later atomic write can replace the file.
        }
    }

    private sealed class PersistedCatalog
    {
        public int Version { get; set; }

        public string WorkspaceKey { get; set; } = string.Empty;

        public string? CodexVersion { get; set; }

        public DateTimeOffset WrittenAtUtc { get; set; }

        public bool IsTruncated { get; set; }

        public List<PersistedSkill>? Skills { get; set; }
    }

    private sealed class PersistedSkill
    {
        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string? ShortDescription { get; set; }

        public string? DisplayName { get; set; }

        public string Scope { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public bool Enabled { get; set; }

        public string? BrandColor { get; set; }
    }
}
