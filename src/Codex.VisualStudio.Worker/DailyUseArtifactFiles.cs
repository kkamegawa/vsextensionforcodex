using System.Runtime.InteropServices;
using Codex.VisualStudio.Contracts;
using Microsoft.Win32.SafeHandles;

namespace Codex.VisualStudio.Worker;

/// <summary>Maps and reads only existing artifacts that remain physically inside the configured local root.</summary>
internal static partial class DailyUseArtifactFiles
{
    public static bool TryMapExistingFile(
        ServerPath serverPath,
        RemotePathMapper mapper,
        ILocalPathBoundary localPathBoundary,
        out LocalPath localPath)
    {
        localPath = null!;
        return serverPath is not null
            && mapper is not null
            && localPathBoundary is not null
            && mapper.TryMapServerToLocal(serverPath, localPathBoundary, out localPath, out _)
            && File.Exists(localPath.Value)
            && !Directory.Exists(localPath.Value);
    }

    public static async Task<ArtifactPreviewInfo?> TryReadPreviewAsync(
        LocalPath localRoot,
        LocalPath localPath,
        ILocalPathBoundary localPathBoundary,
        string? mimeType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localRoot);
        ArgumentNullException.ThrowIfNull(localPath);
        ArgumentNullException.ThrowIfNull(localPathBoundary);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(localPath.Value, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!TryGetFinalPath(stream.SafeFileHandle, out LocalPath openedPath)
                || !localPathBoundary.IsWithinRoot(localRoot, openedPath))
            {
                return null;
            }

            int initialCapacity = stream.Length > ArtifactPreviewValidator.MaximumBytes
                ? ArtifactPreviewValidator.MaximumBytes
                : (int)Math.Max(stream.Length, 0);
            using var buffer = new MemoryStream(initialCapacity);
            byte[] chunk = new byte[64 * 1024];
            while (true)
            {
                int bytesUntilOversize = ArtifactPreviewValidator.MaximumBytes + 1 - checked((int)buffer.Length);
                int read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, bytesUntilOversize)), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
                if (buffer.Length > ArtifactPreviewValidator.MaximumBytes)
                {
                    return null;
                }
            }

            if (!TryGetFinalPath(stream.SafeFileHandle, out LocalPath finalOpenedPath)
                || !finalOpenedPath.Equals(openedPath)
                || !localPathBoundary.IsWithinRoot(localRoot, finalOpenedPath))
            {
                return null;
            }

            byte[] bytes = buffer.ToArray();
            return ArtifactPreviewValidator.TryValidate(bytes, mimeType, out ArtifactPreviewInfo preview)
                ? preview
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static unsafe bool TryGetFinalPath(SafeFileHandle handle, out LocalPath path)
    {
        path = null!;
        const int MaximumPathBuffer = 32768;
        int capacity = 512;
        while (capacity <= MaximumPathBuffer)
        {
            char[] buffer = new char[capacity];
            uint length;
            fixed (char* pointer = buffer)
            {
                length = GetFinalPathNameByHandle(handle, pointer, (uint)capacity, 0);
            }

            if (length == 0 || length > MaximumPathBuffer)
            {
                return false;
            }

            if (length < capacity)
            {
                return LocalPath.TryCreate(new string(buffer, 0, (int)length), out path);
            }

            capacity = checked((int)length + 1);
        }

        return false;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        char* lpszFilePath,
        uint cchFilePath,
        uint dwFlags);
}
