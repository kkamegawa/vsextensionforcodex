using System.Buffers.Binary;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

/// <summary>Keeps app-server paths behind opaque actions bound to one owner and connection generation.</summary>
internal sealed class DailyUseArtifactStore
{
    private const int MaximumEntries = 512;
    private const int MaximumPreviewEntries = 64;
    private const int MaximumPreviewStoreBytes = 20 * 1024 * 1024;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private int previewEntryCount;
    private int previewStoreBytes;

    public bool TryRegister(
        string? statePartitionFingerprint,
        long ownerGeneration,
        long connectionGeneration,
        ServerPath serverPath,
        out string actionId,
        IReadOnlyCollection<ArtifactActionKind>? allowedActions = null)
    {
        actionId = string.Empty;
        if (string.IsNullOrWhiteSpace(statePartitionFingerprint)
            || ownerGeneration <= 0
            || connectionGeneration <= 0
            || serverPath is null)
        {
            return false;
        }

        string candidate = Guid.NewGuid().ToString("N");
        lock (gate)
        {
            if (entries.Count >= MaximumEntries)
            {
                return false;
            }

            ArtifactActionKind[] actions = (allowedActions ?? new[] { ArtifactActionKind.Open, ArtifactActionKind.Reveal })
                .Distinct()
                .ToArray();
            if (actions.Length == 0
                || actions.Any(action => action is not (ArtifactActionKind.Preview or ArtifactActionKind.Open or ArtifactActionKind.Reveal)))
            {
                return false;
            }

            entries.Add(candidate, new Entry(statePartitionFingerprint, ownerGeneration, connectionGeneration,
                serverPath, null, actions));
        }

        actionId = candidate;
        return true;
    }

    /// <summary>Returns a server path only to the Worker after the complete captured identity matches.</summary>
    public bool TryResolve(
        string? activeStatePartitionFingerprint,
        long ownerGeneration,
        long connectionGeneration,
        string? actionId,
        out ServerPath serverPath)
        => TryResolve(activeStatePartitionFingerprint, ownerGeneration, connectionGeneration, actionId,
            ArtifactActionKind.Open, out serverPath);

    public bool TryResolve(
        string? activeStatePartitionFingerprint,
        long ownerGeneration,
        long connectionGeneration,
        string? actionId,
        ArtifactActionKind action,
        out ServerPath serverPath)
    {
        serverPath = null!;
        if (string.IsNullOrWhiteSpace(activeStatePartitionFingerprint)
            || ownerGeneration <= 0
            || connectionGeneration <= 0
            || string.IsNullOrWhiteSpace(actionId))
        {
            return false;
        }

        lock (gate)
        {
            if (!entries.TryGetValue(actionId, out Entry? entry)
                || entry.OwnerGeneration != ownerGeneration
                || entry.ConnectionGeneration != connectionGeneration
                || !entry.AllowedActions.Contains(action)
                || entry.ServerPath is null
                || !string.Equals(entry.StatePartitionFingerprint, activeStatePartitionFingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            serverPath = entry.ServerPath;
            return true;
        }
    }

    public bool TryRegisterPreview(
        string? statePartitionFingerprint,
        long ownerGeneration,
        long connectionGeneration,
        ReadOnlySpan<byte> bytes,
        string? mimeType,
        out string actionId)
    {
        actionId = string.Empty;
        if (string.IsNullOrWhiteSpace(statePartitionFingerprint)
            || ownerGeneration <= 0
            || connectionGeneration <= 0
            || !ArtifactPreviewValidator.TryValidate(bytes, mimeType, out ArtifactPreviewInfo preview))
        {
            return false;
        }

        string candidate = Guid.NewGuid().ToString("N");
        lock (gate)
        {
            if (entries.Count >= MaximumEntries
                || previewEntryCount >= MaximumPreviewEntries
                || previewStoreBytes + preview.Bytes.Length > MaximumPreviewStoreBytes)
            {
                return false;
            }

            entries.Add(candidate, new Entry(statePartitionFingerprint, ownerGeneration, connectionGeneration,
                null, preview, new[] { ArtifactActionKind.Preview }));
            previewEntryCount++;
            previewStoreBytes += preview.Bytes.Length;
        }

        actionId = candidate;
        return true;
    }

    public bool TryResolvePreview(
        string? activeStatePartitionFingerprint,
        long ownerGeneration,
        long connectionGeneration,
        string? actionId,
        out ArtifactPreviewInfo preview)
    {
        preview = default;
        if (string.IsNullOrWhiteSpace(activeStatePartitionFingerprint)
            || ownerGeneration <= 0
            || connectionGeneration <= 0
            || string.IsNullOrWhiteSpace(actionId))
        {
            return false;
        }

        lock (gate)
        {
            if (!entries.TryGetValue(actionId, out Entry? entry)
                || entry.OwnerGeneration != ownerGeneration
                || entry.ConnectionGeneration != connectionGeneration
                || !entry.AllowedActions.Contains(ArtifactActionKind.Preview)
                || entry.Preview is not { } stored
                || !string.Equals(entry.StatePartitionFingerprint, activeStatePartitionFingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            preview = stored with { Bytes = stored.Bytes.ToArray() };
            return true;
        }
    }

    public void RetireGeneration(long connectionGeneration)
    {
        if (connectionGeneration <= 0)
        {
            return;
        }

        lock (gate)
        {
            foreach (string actionId in entries
                .Where(pair => pair.Value.ConnectionGeneration == connectionGeneration)
                .Select(pair => pair.Key)
                .ToArray())
            {
                RemoveEntry(actionId);
            }
        }
    }

    public void RetireOwner(string? statePartitionFingerprint, long ownerGeneration)
    {
        if (string.IsNullOrWhiteSpace(statePartitionFingerprint) || ownerGeneration <= 0)
        {
            return;
        }

        lock (gate)
        {
            foreach (string actionId in entries
                .Where(pair => pair.Value.OwnerGeneration == ownerGeneration
                    && string.Equals(pair.Value.StatePartitionFingerprint, statePartitionFingerprint, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToArray())
            {
                RemoveEntry(actionId);
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            previewEntryCount = 0;
            previewStoreBytes = 0;
        }
    }

    private void RemoveEntry(string actionId)
    {
        if (entries.Remove(actionId, out Entry? entry) && entry.Preview is { } preview)
        {
            previewEntryCount--;
            previewStoreBytes -= preview.Bytes.Length;
        }
    }

    private sealed record Entry(
        string StatePartitionFingerprint,
        long OwnerGeneration,
        long ConnectionGeneration,
        ServerPath? ServerPath,
        ArtifactPreviewInfo? Preview,
        IReadOnlyCollection<ArtifactActionKind> AllowedActions);
}

/// <summary>Validates bounded PNG/JPEG bytes and dimensions before a renderer attempts decoding.</summary>
internal static class ArtifactPreviewValidator
{
    private static readonly uint[] PngCrcTable = CreatePngCrcTable();
    public const int MaximumBytes = 10 * 1024 * 1024;
    public const int MaximumDimension = 4096;
    public const long MaximumPixels = 16_000_000;

    public static bool TryValidate(
        ReadOnlySpan<byte> bytes,
        string? mimeType,
        out ArtifactPreviewInfo preview)
    {
        preview = default;
        if (bytes.Length == 0 || bytes.Length > MaximumBytes || string.IsNullOrWhiteSpace(mimeType))
        {
            return false;
        }

        int width;
        int height;
        string canonicalMime;
        if (string.Equals(mimeType, "image/png", StringComparison.OrdinalIgnoreCase)
            && IsPng(bytes)
            && TryReadPngDimensions(bytes, out width, out height))
        {
            canonicalMime = "image/png";
        }
        else if (string.Equals(mimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase)
            && IsJpeg(bytes)
            && TryReadJpegDimensions(bytes, out width, out height))
        {
            canonicalMime = "image/jpeg";
        }
        else
        {
            return false;
        }

        if (width <= 0 || height <= 0
            || width > MaximumDimension || height > MaximumDimension
            || (long)width * height > MaximumPixels)
        {
            return false;
        }

        preview = new ArtifactPreviewInfo(bytes.ToArray(), canonicalMime, width, height);
        return true;
    }

    private static bool IsPng(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;

    private static bool IsJpeg(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8
            && bytes[^2] == 0xFF && bytes[^1] == 0xD9;

    private static bool TryReadPngDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        int offset = 8;
        bool hasHeader = false;
        bool hasData = false;
        while (offset <= bytes.Length - 12)
        {
            uint chunkLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (chunkLength > int.MaxValue || chunkLength > (uint)(bytes.Length - offset - 12))
            {
                return false;
            }

            int length = (int)chunkLength;
            ReadOnlySpan<byte> type = bytes.Slice(offset + 4, 4);
            ReadOnlySpan<byte> data = bytes.Slice(offset + 8, length);
            uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + length, 4));
            if (ComputePngCrc(bytes.Slice(offset + 4, 4 + length)) != expectedCrc)
            {
                return false;
            }

            if (!hasHeader)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13)
                {
                    return false;
                }

                uint pngWidth = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0, 4));
                uint pngHeight = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
                if (pngWidth > int.MaxValue || pngHeight > int.MaxValue)
                {
                    return false;
                }

                width = (int)pngWidth;
                height = (int)pngHeight;
                hasHeader = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                hasData |= length > 0;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                return length == 0 && hasData && offset + 12 == bytes.Length;
            }
            else if (!IsKnownPngChunk(type) && type[0] is >= (byte)'A' and <= (byte)'Z')
            {
                return false;
            }

            offset += 12 + length;
        }

        return false;
    }

    private static bool TryReadJpegDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        int offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            if (bytes[offset++] != 0xFF)
            {
                return false;
            }

            while (offset < bytes.Length && bytes[offset] == 0xFF)
            {
                offset++;
            }

            if (offset >= bytes.Length)
            {
                return false;
            }

            byte marker = bytes[offset++];
            if (marker == 0xD9)
            {
                return false;
            }

            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (offset + 2 > bytes.Length)
            {
                return false;
            }

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > bytes.Length)
            {
                return false;
            }

            if (marker == 0xDA)
            {
                int scanLength = segmentLength - 2;
                return width > 0 && height > 0 && scanLength >= 6
                    && offset + segmentLength < bytes.Length - 2;
            }

            if (IsStartOfFrame(marker))
            {
                if (segmentLength < 7)
                {
                    return false;
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
            }

            offset += segmentLength;
        }

        return false;
    }

    private static bool IsKnownPngChunk(ReadOnlySpan<byte> type)
        => type.SequenceEqual("PLTE"u8)
            || type.SequenceEqual("tRNS"u8)
            || type.SequenceEqual("gAMA"u8)
            || type.SequenceEqual("cHRM"u8)
            || type.SequenceEqual("sRGB"u8)
            || type.SequenceEqual("iCCP"u8)
            || type.SequenceEqual("sBIT"u8)
            || type.SequenceEqual("bKGD"u8)
            || type.SequenceEqual("pHYs"u8)
            || type.SequenceEqual("tEXt"u8)
            || type.SequenceEqual("zTXt"u8)
            || type.SequenceEqual("iTXt"u8)
            || type.SequenceEqual("eXIf"u8);

    private static uint ComputePngCrc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc = PngCrcTable[(int)((crc ^ value) & 0xFF)] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] CreatePngCrcTable()
    {
        var table = new uint[256];
        for (int index = 0; index < table.Length; index++)
        {
            uint value = (uint)index;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    private static bool IsStartOfFrame(byte marker)
        => marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7
            or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;
}

internal readonly record struct ArtifactPreviewInfo(byte[] Bytes, string MimeType, int Width, int Height);
