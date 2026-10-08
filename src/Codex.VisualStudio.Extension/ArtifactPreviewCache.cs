using System.IO;
using System.Buffers.Binary;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Codex.VisualStudio.Extension;

internal readonly record struct ArtifactPreviewOwner(
    string? StatePartitionFingerprint,
    long OwnerGeneration,
    long ConnectionGeneration);

internal sealed record ArtifactPreviewResult(XamlFragment? Fragment, string? FailureReason);

/// <summary>
/// Validates and renders preview bytes through a generated local cache file. Only the generated
/// cache URI is embedded in the Remote UI fragment; server paths and server-provided URIs never
/// reach the XAML surface.
/// </summary>
internal sealed class ArtifactPreviewCache : IDisposable
{
    private const int MaximumBytes = 10 * 1024 * 1024;
    private const int MaximumDimension = 4096;
    private const long MaximumPixels = 16_000_000;
    private const int MaximumPreviewsPerOwner = 50;
    private const long MaximumCachedBytesPerOwner = 20L * 1024 * 1024;
    private readonly object gate = new();
    private readonly Dictionary<string, int> previewFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly string cacheRoot = Path.Combine(Path.GetTempPath(), "Codex.VisualStudio", "ArtifactPreviews", Guid.NewGuid().ToString("N"));
    private ArtifactPreviewOwner? owner;
    private string? ownerDirectory;
    private long cachedBytes;
    private int disposed;

    public ArtifactPreviewResult Create(
        byte[]? bytes,
        string? mimeType,
        int? declaredWidth,
        int? declaredHeight,
        ArtifactPreviewOwner previewOwner)
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return new ArtifactPreviewResult(null, "Image preview is no longer available.");
            }

            SwitchOwner(previewOwner);
            if (bytes is null || bytes.Length == 0 || bytes.Length > MaximumBytes)
            {
                return new ArtifactPreviewResult(null, "Image preview exceeds the supported size limit.");
            }

            if (!TryGetExtension(bytes, mimeType, out string extension))
            {
                return new ArtifactPreviewResult(null, "Image preview is unavailable because its format could not be verified.");
            }

            if (!TryReadHeaderDimensions(bytes, extension, out int headerWidth, out int headerHeight)
                || !IsSupportedDimensions(headerWidth, headerHeight)
                || (declaredWidth is > 0 && declaredWidth != headerWidth)
                || (declaredHeight is > 0 && declaredHeight != headerHeight))
            {
                return new ArtifactPreviewResult(null, "Image preview dimensions do not match the verified image limits.");
            }

            int width;
            int height;
            try
            {
                using var input = new MemoryStream(bytes, writable: false);
                BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count != 1)
                {
                    return new ArtifactPreviewResult(null, "Animated or multi-frame image previews are not supported.");
                }

                BitmapFrame frame = decoder.Frames[0];
                width = frame.PixelWidth;
                height = frame.PixelHeight;
            }
            catch (Exception)
            {
                return new ArtifactPreviewResult(null, "Image preview is unavailable because the image could not be decoded.");
            }

            if (!IsSupportedDimensions(width, height)
                || width != headerWidth || height != headerHeight
                || (declaredWidth is > 0 && declaredWidth != width)
                || (declaredHeight is > 0 && declaredHeight != height))
            {
                return new ArtifactPreviewResult(null, "Image preview dimensions do not match the verified image limits.");
            }

            if (previewFiles.Count >= MaximumPreviewsPerOwner || cachedBytes + bytes.Length > MaximumCachedBytesPerOwner)
            {
                return new ArtifactPreviewResult(null, "The image preview limit for this connection has been reached.");
            }

            string? path = null;
            try
            {
                string directory = ownerDirectory ??= GetOwnerDirectory(previewOwner);
                EnsureSafeCacheDirectory(directory);
                Directory.CreateDirectory(directory);
                EnsureSafeCacheDirectory(directory);
                path = Path.Combine(directory, string.Concat(Guid.NewGuid().ToString("N"), extension));
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    if (!IsHandleWithinCacheRoot(output.SafeFileHandle))
                    {
                        throw new IOException("Preview file was redirected outside its generated cache.");
                    }

                    output.Write(bytes, 0, bytes.Length);
                    output.Flush(flushToDisk: true);
                }

                previewFiles.Add(path, bytes.Length);
                cachedBytes += bytes.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
            {
                if (path is not null)
                {
                    TryDelete(path);
                }
                return new ArtifactPreviewResult(null, "Image preview could not be saved for display.");
            }

            string uri = SecurityElement.Escape(new Uri(path, UriKind.Absolute).AbsoluteUri) ?? string.Empty;
            string xaml = $"<Image xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Source=\"{uri}\" MaxWidth=\"640\" MaxHeight=\"420\" Stretch=\"Uniform\" SnapsToDevicePixels=\"True\" AutomationProperties.Name=\"Verified image preview\" />";
            return new ArtifactPreviewResult(new XamlFragment(xaml), null);
        }
    }

    public void Reset(ArtifactPreviewOwner? nextOwner = null)
    {
        lock (gate)
        {
            DeletePreviewFiles();
            owner = nextOwner;
            ownerDirectory = nextOwner is { } value ? GetOwnerDirectory(value) : null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lock (gate)
        {
            DeletePreviewFiles();
            owner = null;
            ownerDirectory = null;
            TryDeleteDirectory(cacheRoot);
        }
    }

    private void SwitchOwner(ArtifactPreviewOwner nextOwner)
    {
        if (owner == nextOwner)
        {
            return;
        }

        DeletePreviewFiles();
        owner = nextOwner;
        ownerDirectory = GetOwnerDirectory(nextOwner);
    }

    private void DeletePreviewFiles()
    {
        foreach ((string path, int length) in previewFiles)
        {
            TryDelete(path);
            cachedBytes -= length;
        }

        previewFiles.Clear();
        cachedBytes = 0;
        if (ownerDirectory is not null)
        {
            try
            {
                Directory.Delete(ownerDirectory, recursive: false);
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private string GetOwnerDirectory(ArtifactPreviewOwner previewOwner)
    {
        string ownerMaterial = string.Concat(
            previewOwner.StatePartitionFingerprint ?? string.Empty, "\n",
            previewOwner.OwnerGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture), "\n",
            previewOwner.ConnectionGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string ownerKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerMaterial))).ToLowerInvariant();
        return Path.Combine(cacheRoot, ownerKey);
    }

    private static bool IsSupportedDimensions(int width, int height)
        => width > 0 && height > 0 && width <= MaximumDimension && height <= MaximumDimension && (long)width * height <= MaximumPixels;

    private static bool TryReadHeaderDimensions(byte[] bytes, string extension, out int width, out int height)
    {
        width = 0;
        height = 0;
        ReadOnlySpan<byte> data = bytes;
        if (extension == ".png")
        {
            if (data.Length < 24 || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
            {
                return false;
            }

            uint pngWidth = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
            uint pngHeight = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
            if (pngWidth > int.MaxValue || pngHeight > int.MaxValue)
            {
                return false;
            }

            width = (int)pngWidth;
            height = (int)pngHeight;
            return true;
        }

        if (extension != ".jpg")
        {
            return false;
        }

        int offset = 2;
        while (offset < data.Length)
        {
            while (offset < data.Length && data[offset] != 0xFF)
            {
                offset++;
            }
            while (offset < data.Length && data[offset] == 0xFF)
            {
                offset++;
            }
            if (offset >= data.Length)
            {
                return false;
            }

            byte marker = data[offset++];
            if (marker is 0xD8 or 0xD9 or 0x01 or >= 0xD0 and <= 0xD7)
            {
                continue;
            }
            if (offset + 2 > data.Length)
            {
                return false;
            }

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > data.Length)
            {
                return false;
            }

            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                if (segmentLength < 7)
                {
                    return false;
                }
                height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 5, 2));
                return true;
            }

            offset += segmentLength;
        }

        return false;
    }

    private void EnsureSafeCacheDirectory(string directory)
    {
        string fullRoot = Path.GetFullPath(cacheRoot);
        string fullDirectory = Path.GetFullPath(directory);
        if (!fullDirectory.StartsWith(string.Concat(fullRoot, Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Preview cache path escaped its generated root.");
        }

        string rootPath = Path.GetPathRoot(fullRoot)!;
        string? current = rootPath;
        string all = string.Concat(fullRoot, Path.DirectorySeparatorChar, fullDirectory[fullRoot.Length..].TrimStart(Path.DirectorySeparatorChar));
        foreach (string segment in all[rootPath.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current!, segment);
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Preview cache contains a redirected directory.");
            }
        }
    }

    private bool IsHandleWithinCacheRoot(SafeFileHandle fileHandle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using SafeFileHandle rootHandle = CreateFile(
            cacheRoot,
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            0x02000000,
            IntPtr.Zero);
        if (rootHandle.IsInvalid
            || !TryGetFinalPath(fileHandle, out string filePath)
            || !TryGetFinalPath(rootHandle, out string rootPath))
        {
            return false;
        }

        string expectedRootPath = ToExtendedPath(Path.GetFullPath(cacheRoot));
        return string.Equals(rootPath.TrimEnd('\\'), expectedRootPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            && filePath.StartsWith(string.Concat(rootPath.TrimEnd('\\'), "\\"), StringComparison.OrdinalIgnoreCase);
    }

    private static string ToExtendedPath(string path)
        => path.StartsWith("\\\\", StringComparison.Ordinal)
            ? string.Concat("\\\\?\\UNC\\", path[2..])
            : string.Concat("\\\\?\\", path);

    private static bool TryGetFinalPath(SafeFileHandle handle, out string path)
    {
        var buffer = new char[32768];
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            path = string.Empty;
            return false;
        }

        path = new string(buffer, 0, (int)length);
        return true;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [System.Runtime.InteropServices.Out] char[] path, uint pathLength, uint flags);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Cache cleanup is best effort; the generated path is never used as an artifact path.
        }
    }

    private static bool TryGetExtension(byte[] bytes, string? mimeType, out string extension)
    {
        extension = string.Empty;
        ReadOnlySpan<byte> data = bytes;
        bool isPng = data.Length >= 8
            && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        bool isJpeg = data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
        if (isPng && string.Equals(mimeType, "image/png", StringComparison.OrdinalIgnoreCase))
        {
            extension = ".png";
            return true;
        }

        if (isJpeg && string.Equals(mimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            extension = ".jpg";
            return true;
        }

        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A stale cache file is not allowed to affect preview ownership or other files.
        }
        catch (UnauthorizedAccessException)
        {
            // The cache directory is best-effort cleanup only.
        }
    }
}
