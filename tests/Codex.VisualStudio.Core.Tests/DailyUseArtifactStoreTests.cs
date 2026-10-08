using System.Buffers.Binary;
using System.IO.Compression;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class DailyUseArtifactStoreTests
{
    [TestMethod]
    public void TryResolveRequiresExactOwnerAndConnectionGeneration()
    {
        var store = new DailyUseArtifactStore();
        ServerPath expected = ServerPath.Create("/workspace/output.png");
        Assert.IsTrue(store.TryRegister("partition-a", 4, 9, expected, out string actionId));

        Assert.IsTrue(store.TryResolve("partition-a", 4, 9, actionId, out ServerPath resolved));
        Assert.AreEqual(expected, resolved);
        Assert.IsFalse(store.TryResolve("partition-b", 4, 9, actionId, out _));
        Assert.IsFalse(store.TryResolve("partition-a", 5, 9, actionId, out _));
        Assert.IsFalse(store.TryResolve("partition-a", 4, 10, actionId, out _));
    }

    [TestMethod]
    public void RetiringGenerationInvalidatesItsActions()
    {
        var store = new DailyUseArtifactStore();
        Assert.IsTrue(store.TryRegister("partition-a", 4, 9, ServerPath.Create("/workspace/a.txt"), out string actionId));

        store.RetireGeneration(9);

        Assert.IsFalse(store.TryResolve("partition-a", 4, 9, actionId, out _));
    }

    [TestMethod]
    public void RegisteringSamePathReusesActionInsteadOfExhaustingStore()
    {
        var store = new DailyUseArtifactStore();
        ServerPath path = ServerPath.Create("/workspace/output.png");
        Assert.IsTrue(store.TryRegister("partition-a", 4, 9, path, out string first));

        for (int index = 0; index < 1000; index++)
        {
            Assert.IsTrue(store.TryRegister("partition-a", 4, 9, path, out string repeated));
            Assert.AreEqual(first, repeated);
        }

        Assert.IsTrue(store.TryRegister("partition-a", 4, 9, ServerPath.Create("/workspace/other.png"), out string other));
        Assert.AreNotEqual(first, other);
        Assert.IsTrue(store.TryRegister("partition-a", 4, 9, path, out string previewable,
            new[] { ArtifactActionKind.Preview, ArtifactActionKind.Open }));
        Assert.AreNotEqual(first, previewable);
        Assert.IsTrue(store.TryRegister("partition-a", 4, 10, path, out string nextGeneration));
        Assert.AreNotEqual(first, nextGeneration);
    }

    [TestMethod]
    public void TurnErrorReasonIgnoresNonObjectErrorPayloads()
    {
        foreach (string json in new[] { "{\"error\":null}", "{\"error\":\"boom\"}", "{\"error\":[1]}", "[]" })
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            Assert.IsNull(CodexSessionService.ReadTurnErrorReason(document.RootElement), json);
        }

        using var known = System.Text.Json.JsonDocument.Parse("{\"error\":{\"codexErrorInfo\":\"flexUnavailable\"}}");
        Assert.AreEqual("flexUnavailable", CodexSessionService.ReadTurnErrorReason(known.RootElement));
    }

    [TestMethod]
    public void InlinePreviewActionIsOwnerBoundAndPreviewOnly()
    {
        byte[] png = CreatePng(1, 1);
        var store = new DailyUseArtifactStore();
        Assert.IsTrue(store.TryRegisterPreview("partition-a", 4, 9, png, "image/png", out string actionId));

        Assert.IsTrue(store.TryResolvePreview("partition-a", 4, 9, actionId, out ArtifactPreviewInfo preview));
        Assert.AreEqual("image/png", preview.MimeType);
        Assert.IsFalse(store.TryResolvePreview("partition-b", 4, 9, actionId, out _));
        Assert.IsFalse(store.TryResolve("partition-a", 4, 9, actionId, ArtifactActionKind.Open, out _));

        store.RetireOwner("partition-a", 4);
        Assert.IsFalse(store.TryResolvePreview("partition-a", 4, 9, actionId, out _));
    }

    [TestMethod]
    public void PreviewValidatorRequiresMatchingMimeSignatureAndSafeDimensions()
    {
        byte[] png = CreatePng(1, 1);
        Assert.IsTrue(ArtifactPreviewValidator.TryValidate(png, "image/png", out ArtifactPreviewInfo preview));
        Assert.AreEqual(1, preview.Width);
        Assert.AreEqual(1, preview.Height);
        Assert.AreEqual("image/png", preview.MimeType);
        Assert.IsFalse(ArtifactPreviewValidator.TryValidate(png, "image/jpeg", out _));
        Assert.IsFalse(ArtifactPreviewValidator.TryValidate(CreatePng(4097, 1), "image/png", out _));
        Assert.IsFalse(ArtifactPreviewValidator.TryValidate(CreatePng(4096, 4096), "image/png", out _));
        Assert.IsFalse(ArtifactPreviewValidator.TryValidate(new byte[33], "image/png", out _));
        png[29] ^= 0x01;
        Assert.IsFalse(ArtifactPreviewValidator.TryValidate(png, "image/png", out _));
        Assert.IsFalse(ArtifactPreviewValidator.TryValidate(new byte[ArtifactPreviewValidator.MaximumBytes + 1], "image/png", out _));
    }

    [TestMethod]
    public async Task FilePreviewReadRejectsMalformedAndOversizedFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Opened-handle path validation is implemented for the Windows host.");
        }

        string root = Path.Combine(Path.GetTempPath(), "artifact-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string validPath = Path.Combine(root, "valid.png");
        string malformedPath = Path.Combine(root, "malformed.png");
        string oversizedPath = Path.Combine(root, "oversized.png");
        byte[] png = CreatePng(1, 1);
        await File.WriteAllBytesAsync(validPath, png);
        await File.WriteAllBytesAsync(malformedPath, new byte[33]);
        await File.WriteAllBytesAsync(oversizedPath, new byte[ArtifactPreviewValidator.MaximumBytes + 1]);
        try
        {
            var boundary = new LocalPathBoundary();
            LocalPath localRoot = LocalPath.Create(root);
            ArtifactPreviewInfo? valid = await DailyUseArtifactFiles.TryReadPreviewAsync(localRoot,
                LocalPath.Create(validPath), boundary, "image/png", CancellationToken.None);
            ArtifactPreviewInfo? malformed = await DailyUseArtifactFiles.TryReadPreviewAsync(localRoot,
                LocalPath.Create(malformedPath), boundary, "image/png", CancellationToken.None);
            ArtifactPreviewInfo? oversized = await DailyUseArtifactFiles.TryReadPreviewAsync(localRoot,
                LocalPath.Create(oversizedPath), boundary, "image/png", CancellationToken.None);

            Assert.IsNotNull(valid);
            Assert.IsNull(malformed);
            Assert.IsNull(oversized);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(output, "IHDR"u8, header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(new byte[] { 0, 255, 0, 0, 255 });
        }

        WritePngChunk(output, "IDAT"u8, compressed.ToArray());
        WritePngChunk(output, "IEND"u8, Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WritePngChunk(MemoryStream output, ReadOnlySpan<byte> chunkType, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        int crcStart = checked((int)output.Length);
        output.Write(chunkType);
        output.Write(data);
        uint crc = PngCrc(output.GetBuffer().AsSpan(crcStart, chunkType.Length + data.Length));
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        output.Write(checksum);
    }

    private static uint PngCrc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }
}
