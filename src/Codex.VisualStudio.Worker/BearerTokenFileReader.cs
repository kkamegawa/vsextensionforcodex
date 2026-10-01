using System.Runtime.InteropServices;
using System.Text;
using Codex.VisualStudio.Contracts;
using Microsoft.Win32.SafeHandles;

namespace Codex.VisualStudio.Worker;

/// <summary>
/// Reads the remote bearer token immediately before each explicit handshake. Contents are never
/// cached or watched; rotation is observed on the next explicit reconnect. File ACLs are neither
/// inspected nor changed; keeping the local token file private is the operator's responsibility.
/// </summary>
public sealed partial class BearerTokenFileReader
{
    public const int MaxBytes = 16 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Func<string, DriveType?> driveTypeResolver;
    private readonly Func<SafeFileHandle, string?> finalPathResolver;

    public BearerTokenFileReader(
        Func<string, DriveType?>? driveTypeResolver = null,
        Func<SafeFileHandle, string?>? finalPathResolver = null)
    {
        this.driveTypeResolver = driveTypeResolver ?? ResolveDriveType;
        this.finalPathResolver = finalPathResolver ?? GetFinalPath;
    }

    public async Task<string> ReadAsync(string? path, CancellationToken cancellationToken)
    {
        string fullPath = RequireLocalPath(path);
        FileInfo file;
        try
        {
            file = new FileInfo(fullPath);
            if (Directory.Exists(fullPath))
            {
                throw Failure(RemoteConnectionFailure.TokenFileUnreadable);
            }

            if (!file.Exists)
            {
                throw Failure(RemoteConnectionFailure.TokenFileMissing);
            }

            // A symlink or junction may point anywhere; require the final target to be local too.
            if (file.LinkTarget is not null)
            {
                FileSystemInfo? target = file.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null || !target.Exists)
                {
                    throw Failure(RemoteConnectionFailure.TokenFileMissing);
                }

                fullPath = RequireLocalPath(target.FullName);
            }
        }
        catch (RemoteConnectionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw Failure(RemoteConnectionFailure.TokenFileUnreadable);
        }

        byte[] buffer = new byte[MaxBytes + 1];
        int total = 0;
        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            // A directory symlink or junction anywhere in the path can redirect the open to a
            // network share. Require the final path of the opened handle itself to stay local.
            string? finalPath = finalPathResolver(stream.SafeFileHandle);
            if (finalPath is null)
            {
                throw Failure(RemoteConnectionFailure.TokenFileUnreadable);
            }

            RequireLocalPath(finalPath);

            // The bound applies to the opened stream, not to a length read before opening.
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or RemoteConnectionException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            throw Failure(RemoteConnectionFailure.TokenFileMissing);
        }
        catch (DirectoryNotFoundException)
        {
            throw Failure(RemoteConnectionFailure.TokenFileMissing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Failure(RemoteConnectionFailure.TokenFileUnreadable);
        }

        try
        {
            if (total > MaxBytes)
            {
                throw Failure(RemoteConnectionFailure.TokenFileInvalid);
            }

            return Parse(buffer.AsSpan(0, total));
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    internal static string Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw Failure(RemoteConnectionFailure.TokenFileInvalid);
        }

        // Only outer whitespace is trimmed; embedded whitespace makes the value invalid.
        string token = text.Trim();
        if (!BearerTokenPolicy.IsValid(token))
        {
            throw Failure(RemoteConnectionFailure.TokenFileInvalid);
        }

        return token;
    }

    private string RequireLocalPath(string? path)
    {
        if (!TokenFilePathPolicy.IsSyntacticallyValid(path))
        {
            throw Failure(RemoteConnectionFailure.TokenFileUnreadable);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path!.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Failure(RemoteConnectionFailure.TokenFileUnreadable);
        }

        // GetFullPath can still produce a UNC or device form (for example from a "C:\..\" trick
        // on an unusual root); repeat the syntactic check on the normalized path.
        if (!TokenFilePathPolicy.IsSyntacticallyValid(fullPath))
        {
            throw Failure(RemoteConnectionFailure.TokenFileUnreadable);
        }

        DriveType? driveType = driveTypeResolver(fullPath);
        if (driveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
        {
            // Mapped network drives, optical media, and unknown roots are not local files.
            throw Failure(RemoteConnectionFailure.TokenFileUnreadable);
        }

        return fullPath;
    }

    private static DriveType? ResolveDriveType(string fullPath)
    {
        try
        {
            string? root = Path.GetPathRoot(fullPath);
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).DriveType;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Returns a drive-qualified path ("C:\...") for a local file, a "\\server\share" form for a
    // network file (rejected by the caller), or null when the final path is unavailable.
    internal static string? GetFinalPath(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        const int Capacity = 1024;
        string path;
        unsafe
        {
            char* buffer = stackalloc char[Capacity];
            uint length = GetFinalPathNameByHandle(handle, buffer, Capacity, 0);
            if (length == 0 || length >= Capacity)
            {
                return null;
            }

            path = new string(buffer, 0, (int)length);
        }

        const string UncPrefix = @"\\?\UNC\";
        const string LocalPrefix = @"\\?\";
        if (path.StartsWith(UncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[UncPrefix.Length..];
        }

        return path.StartsWith(LocalPrefix, StringComparison.Ordinal) ? path[LocalPrefix.Length..] : path;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);

    private static RemoteConnectionException Failure(RemoteConnectionFailure failure) => new(failure);
}
