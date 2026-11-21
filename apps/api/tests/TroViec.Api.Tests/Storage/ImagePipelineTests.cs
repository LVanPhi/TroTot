using System.Security.Cryptography;
using Shouldly;
using SkiaSharp;
using TroViec.Api.Infrastructure.Problems;
using TroViec.Api.Infrastructure.Storage;

namespace TroViec.Api.Tests.Storage;

/// <summary>BR-06 and the file cases in docs/09-testing.md (Kiểm thử API: Tệp tải lên).</summary>
public sealed class ImagePipelineTests
{
    private static Task<ProcessedImage> ProcessAsync(byte[] bytes) =>
        ImagePipeline.ProcessAsync(new MemoryStream(bytes), TestContext.Current.CancellationToken);

    private static async Task<string> ProblemCodeAsync(byte[] bytes) =>
        (await Should.ThrowAsync<ProblemException>(() => ProcessAsync(bytes))).Code;

    [Fact]
    public async Task Process_PngNamedJpg_IsAcceptedByMagicBytes()
    {
        // The pipeline never sees the file name; a PNG uploaded as "photo.jpg" is still a PNG.
        var result = await ProcessAsync(TestImages.Encode(64, 32, SKEncodedImageFormat.Png));

        (result.Width, result.Height).ShouldBe((64, 32));
    }

    [Fact]
    public async Task Process_PdfNamedJpg_Returns415()
    {
        var pdf = "%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\ntrailer\n%%EOF"u8.ToArray();

        (await ProblemCodeAsync(pdf)).ShouldBe("image.unsupported_type");
    }

    [Fact]
    public async Task Process_SixMegabytes_Returns413()
    {
        var big = new byte[6 * 1024 * 1024];
        TestImages.Encode(8, 8, SKEncodedImageFormat.Png).CopyTo(big, 0);

        (await ProblemCodeAsync(big)).ShouldBe("image.too_large");
    }

    [Fact]
    public async Task Process_12000x12000Pixels_Returns415BeforeDecoding()
    {
        // 144 megapixels > 40; the file is tiny because only the header exists.
        (await ProblemCodeAsync(TestImages.PngHeaderOnly(12_000, 12_000))).ShouldBe("image.unsupported_type");
    }

    [Fact]
    public async Task Process_JpegWithExifGps_StoresImageWithoutAnyMetadata()
    {
        var input = TestImages.JpegWithExif(40, 20, orientation: 1);
        input.AsSpan().IndexOf("Exif\0\0"u8).ShouldBeGreaterThan(0);  // sanity: the input does carry EXIF

        var result = await ProcessAsync(input);

        result.Webp.AsSpan().IndexOf("Exif"u8).ShouldBe(-1);
        result.Webp.AsSpan().IndexOf("EXIF"u8).ShouldBe(-1);   // WebP EXIF chunk id
        result.Webp.AsSpan().IndexOf("XMP "u8).ShouldBe(-1);
        result.Webp.AsSpan().IndexOf("ICCP"u8).ShouldBe(-1);
    }

    [Fact]
    public async Task Process_ExifOrientationRotate90_StoresUprightImage()
    {
        // Orientation 6: the camera was turned; the left (red) half of the stored pixels becomes the top.
        var result = await ProcessAsync(TestImages.JpegWithExif(40, 20, orientation: 6));

        (result.Width, result.Height).ShouldBe((20, 40));
        using var bitmap = SKBitmap.Decode(result.Webp);
        IsReddish(bitmap.GetPixel(10, 5)).ShouldBeTrue();
        IsReddish(bitmap.GetPixel(10, 35)).ShouldBeFalse();
    }

    [Fact]
    public async Task Process_LongEdgeOver2048_DownscalesKeepingAspectRatio()
    {
        var result = await ProcessAsync(TestImages.Encode(3000, 1000, SKEncodedImageFormat.Jpeg, quality: 70));

        (result.Width, result.Height).ShouldBe((2048, 683));
    }

    [Fact]
    public async Task Process_Always_OutputsWebp()
    {
        var result = await ProcessAsync(TestImages.Encode(16, 16, SKEncodedImageFormat.Jpeg));

        ImagePipeline.HasImageMagicBytes(result.Webp).ShouldBeTrue();
        result.Webp[8..12].ShouldBe("WEBP"u8.ToArray());
    }

    [Fact]
    public async Task Process_WebpInput_IsAccepted()
    {
        var result = await ProcessAsync(TestImages.Encode(30, 10, SKEncodedImageFormat.Webp));

        (result.Width, result.Height).ShouldBe((30, 10));
    }

    [Fact]
    public async Task Process_SameFileTwice_GivesSameSha256OfTheOriginalBytes()
    {
        var input = TestImages.Encode(32, 32, SKEncodedImageFormat.Png);

        var first = await ProcessAsync(input);
        var second = await ProcessAsync(input);

        first.Sha256.ShouldBe(second.Sha256);
        first.Sha256.ShouldBe(SHA256.HashData(input));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF })]                  // JPEG magic, nothing after it
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 })] // PNG magic, garbage after it
    public async Task Process_GarbageOrTruncated_Returns415(byte[] bytes) =>
        (await ProblemCodeAsync(bytes)).ShouldBe("image.unsupported_type");

    private static bool IsReddish(SKColor c) => c.Red > 150 && c.Blue < 100;
}

public sealed class ObjectKeysTests
{
    [Fact]
    public void Keys_AreServerGenerated()
    {
        var listing = Guid.NewGuid();
        var scan = Guid.NewGuid();

        ObjectKeys.ListingImage(listing).ShouldMatch($"^{listing:D}/[0-9a-f]{{32}}\\.webp$");
        ObjectKeys.ScanImage(scan, 3).ShouldBe($"{scan:D}/3.webp");
        ObjectKeys.OwnerDocument(42).ShouldMatch("^42/[0-9a-f]{32}\\.bin$");
        ObjectKeys.ListingImage(listing).ShouldNotBe(ObjectKeys.ListingImage(listing));
    }
}
