using System.Buffers.Binary;
using System.Reflection;
using System.IO;
using Codex.VisualStudio.Extension;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Xml;
using Codex.VisualStudio.Contracts;
using Microsoft.VisualStudio.Extensibility.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class ArtifactPreviewCacheTests
{
    private static readonly ArtifactPreviewOwner OwnerA = new("owner-a", 1, 10);
    private static readonly ArtifactPreviewOwner OwnerB = new("owner-b", 2, 11);

    [STATestMethod]
    public void ValidPngAndJpegAreDecodedIntoGeneratedFragmentsAndCanBeRendered()
    {
        using var cache = new ArtifactPreviewCache();
        byte[] png = CreateEncodedImage(new PngBitmapEncoder());
        byte[] jpeg = CreateEncodedImage(new JpegBitmapEncoder());

        ArtifactPreviewResult pngResult = cache.Create(png, "image/png", 640, 420, OwnerA);
        ArtifactPreviewResult jpegResult = cache.Create(jpeg, "image/jpeg", 640, 420, OwnerA);

        Assert.IsNotNull(pngResult.Fragment, pngResult.FailureReason);
        Assert.IsNotNull(jpegResult.Fragment, jpegResult.FailureReason);
        Assert.IsNull(pngResult.FailureReason);
        Assert.IsNull(jpegResult.FailureReason);

        Image image = LoadGeneratedImage(pngResult.Fragment!);
        BitmapSource pngSource = image.Source as BitmapSource
            ?? throw new AssertFailedException("The generated PNG preview did not produce a bitmap source.");
        Assert.AreEqual(640, pngSource.PixelWidth);
        Assert.AreEqual(420, pngSource.PixelHeight);
        RenderTargetBitmap rendered = RenderImage(image);
        image.Source = null;

        Image jpegImage = LoadGeneratedImage(jpegResult.Fragment!);
        BitmapSource jpegSource = jpegImage.Source as BitmapSource
            ?? throw new AssertFailedException("The generated JPEG preview did not produce a bitmap source.");
        Assert.AreEqual(640, jpegSource.PixelWidth);
        Assert.AreEqual(420, jpegSource.PixelHeight);
        _ = RenderImage(jpegImage);
        jpegImage.Source = null;
        WriteUiPreviewArtifact(rendered);
    }

    [STATestMethod]
    public void MalformedOversizedAndMismatchedPayloadsFailBeforeCacheCreation()
    {
        using var cache = new ArtifactPreviewCache();
        byte[] valid = CreateEncodedImage(new PngBitmapEncoder());
        byte[] malformed = CreateHeaderOnlyPng(640, 420);
        byte[] oversized = new byte[10 * 1024 * 1024 + 1];

        ArtifactPreviewResult malformedResult = cache.Create(malformed, "image/png", 640, 420, OwnerA);
        ArtifactPreviewResult oversizedResult = cache.Create(oversized, "image/png", null, null, OwnerA);
        ArtifactPreviewResult mimeMismatch = cache.Create(valid, "image/jpeg", 640, 420, OwnerA);
        ArtifactPreviewResult dimensionMismatch = cache.Create(valid, "image/png", 641, 420, OwnerA);
        ArtifactPreviewResult excessiveDimensions = cache.Create(CreateHeaderOnlyPng(4097, 1), "image/png", 4097, 1, OwnerA);

        Assert.IsNull(malformedResult.Fragment);
        Assert.IsNotNull(malformedResult.FailureReason);
        Assert.IsNull(oversizedResult.Fragment);
        Assert.IsNotNull(oversizedResult.FailureReason);
        Assert.IsNull(mimeMismatch.Fragment);
        Assert.IsNotNull(mimeMismatch.FailureReason);
        Assert.IsNull(dimensionMismatch.Fragment);
        Assert.IsNotNull(dimensionMismatch.FailureReason);
        Assert.IsNull(excessiveDimensions.Fragment);
        Assert.IsNotNull(excessiveDimensions.FailureReason);
    }

    [STATestMethod]
    public void OwnerSwitchResetAndDisposeDeleteOnlyCurrentPreviewFiles()
    {
        var cache = new ArtifactPreviewCache();
        byte[] png = CreateEncodedImage(new PngBitmapEncoder());
        try
        {
            ArtifactPreviewResult first = cache.Create(png, "image/png", 640, 420, OwnerA);
            string firstPath = GetCachePath(first.Fragment!);
            Assert.IsTrue(File.Exists(firstPath));

            ArtifactPreviewResult second = cache.Create(png, "image/png", 640, 420, OwnerB);
            string secondPath = GetCachePath(second.Fragment!);
            Assert.IsTrue(File.Exists(secondPath));
            Assert.IsFalse(File.Exists(firstPath), "Switching owners must retire the previous owner cache.");

            cache.Reset(new ArtifactPreviewOwner("owner-c", 3, 12));
            Assert.IsFalse(File.Exists(secondPath), "Reset must delete active preview files.");

            ArtifactPreviewResult third = cache.Create(png, "image/png", 640, 420, new ArtifactPreviewOwner("owner-c", 3, 12));
            string thirdPath = GetCachePath(third.Fragment!);
            string cacheRoot = Directory.GetParent(Directory.GetParent(thirdPath)!.FullName)!.FullName;
            Assert.IsTrue(File.Exists(thirdPath));

            cache.Dispose();
            cache.Dispose();
            Assert.IsFalse(File.Exists(thirdPath));
            Assert.IsFalse(Directory.Exists(cacheRoot));
            Assert.IsNull(cache.Create(png, "image/png", 640, 420, OwnerB).Fragment);
        }
        finally
        {
            cache.Dispose();
        }
    }

    [STATestMethod]
    public void PerOwnerCacheRejectsDataPastTwentyMiB()
    {
        using var cache = new ArtifactPreviewCache();
        byte[] smallPng = CreateEncodedImage(new PngBitmapEncoder());
        byte[] paddedPng = AddPngTextChunk(smallPng, 7 * 1024 * 1024);
        Assert.IsTrue(paddedPng.Length <= 10 * 1024 * 1024);

        ArtifactPreviewResult first = cache.Create(paddedPng, "image/png", 640, 420, OwnerA);
        ArtifactPreviewResult second = cache.Create(paddedPng, "image/png", 640, 420, OwnerA);
        ArtifactPreviewResult third = cache.Create(paddedPng, "image/png", 640, 420, OwnerA);

        Assert.IsNotNull(first.Fragment, first.FailureReason);
        Assert.IsNotNull(second.Fragment, second.FailureReason);
        Assert.IsNull(third.Fragment);
        StringAssert.Contains(third.FailureReason ?? string.Empty, "limit");
    }

    private static byte[] CreateEncodedImage(BitmapEncoder encoder)
    {
        const int width = 640;
        const int height = 420;
        const int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * stride) + (x * 4);
                pixels[offset] = (byte)(255 * x / (width - 1));
                pixels[offset + 1] = (byte)(255 * y / (height - 1));
                pixels[offset + 2] = (byte)(255 - pixels[offset]);
                pixels[offset + 3] = 255;
            }
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static byte[] CreateHeaderOnlyPng(int width, int height)
    {
        var bytes = new byte[24];
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        signature.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), (uint)height);
        return bytes;
    }

    private static byte[] AddPngTextChunk(byte[] png, int targetLength)
    {
        const int headerLength = 33;
        int dataLength = targetLength - png.Length - 12;
        if (dataLength < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(targetLength));
        }

        byte[] data = new byte[dataLength];
        data[0] = (byte)'k';
        for (int index = 2; index < data.Length; index++)
        {
            data[index] = (byte)'x';
        }

        using var output = new MemoryStream(targetLength);
        output.Write(png.AsSpan(0, headerLength));
        WritePngChunk(output, "tEXt"u8, data);
        output.Write(png.AsSpan(headerLength));
        return output.ToArray();
    }

    private static void WritePngChunk(MemoryStream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        int crcStart = checked((int)output.Length);
        output.Write(type);
        output.Write(data);
        uint crc = CalculatePngCrc(output.GetBuffer().AsSpan(crcStart, type.Length + data.Length));
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        output.Write(checksum);
    }

    private static uint CalculatePngCrc(ReadOnlySpan<byte> input)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in input)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    private static Image LoadGeneratedImage(XamlFragment fragment)
    {
        string xaml = FindXaml(fragment);
        using XmlReader reader = XmlReader.Create(new StringReader(xaml));
        return (Image)XamlReader.Load(reader);
    }

    private static string FindXaml(XamlFragment fragment)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (PropertyInfo property in fragment.GetType().GetProperties(flags))
        {
            if (property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0)
            {
                string? value = property.GetValue(fragment) as string;
                if (value?.StartsWith("<Image ", StringComparison.Ordinal) == true)
                {
                    return value;
                }
            }
        }

        foreach (FieldInfo field in fragment.GetType().GetFields(flags))
        {
            if (field.FieldType == typeof(string) && field.GetValue(fragment) is string value
                && value.StartsWith("<Image ", StringComparison.Ordinal))
            {
                return value;
            }
        }

        throw new AssertFailedException("The generated XAML fragment did not expose its Image markup for STA rendering.");
    }

    private static string GetCachePath(XamlFragment fragment)
    {
        string xaml = FindXaml(fragment);
        var document = new XmlDocument { XmlResolver = null };
        document.LoadXml(xaml);
        string? source = document.DocumentElement?.GetAttribute("Source");
        Assert.IsTrue(Uri.TryCreate(source, UriKind.Absolute, out Uri? uri));
        return uri!.LocalPath;
    }

    private static RenderTargetBitmap RenderImage(Image image)
    {
        image.Measure(new Size(640, 420));
        image.Arrange(new Rect(0, 0, 640, 420));
        image.UpdateLayout();
        var rendered = new RenderTargetBitmap(640, 420, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(image);
        rendered.Freeze();
        return rendered;
    }

    private static void WriteUiPreviewArtifact(RenderTargetBitmap rendered)
    {
        string? repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        string root = repositoryRoot ?? throw new AssertFailedException("Could not locate the workspace root for the UI render artifact.");
        string outputPath = Path.Combine(root, "artifacts", "issue155", "ui-preview.png");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using FileStream output = File.Create(outputPath);
        encoder.Save(output);
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        DirectoryInfo? current = new(startDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && (Directory.Exists(Path.Combine(current.FullName, ".git"))
                    || File.Exists(Path.Combine(current.FullName, ".git"))))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
