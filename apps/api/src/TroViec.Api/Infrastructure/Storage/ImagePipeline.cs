using System.Security.Cryptography;
using SkiaSharp;
using TroViec.Api.Infrastructure.Problems;

namespace TroViec.Api.Infrastructure.Storage;

/// <param name="Webp">Re-encoded image that is stored and served (never the original bytes).</param>
/// <param name="Sha256">SHA-256 of the bytes the user uploaded: stable across encoder upgrades, so the same file
/// uploaded twice is recognised (image.duplicate, worker cache key).</param>
public sealed record ProcessedImage(byte[] Webp, byte[] Sha256, int Width, int Height);

/// <summary>
/// BR-06 and docs/07-security.md (Tệp tải lên): accept JPEG/PNG/WebP by magic bytes only, refuse more than 5 MB or
/// 40 megapixels before decoding, apply the EXIF orientation, downscale to a 2048 px long edge and re-encode as
/// WebP quality 82. Skia's encoder writes no metadata, so EXIF (GPS, camera), ICC and XMP are dropped.
/// </summary>
public static class ImagePipeline
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const long MaxPixels = 40_000_000;
    public const int MaxLongEdge = 2048;
    public const int WebpQuality = 82;

    public static async Task<ProcessedImage> ProcessAsync(Stream upload, CancellationToken ct)
    {
        var original = await ReadLimitedAsync(upload, MaxBytes, ct);
        if (!HasImageMagicBytes(original)) throw new ProblemException("image.unsupported_type");

        // SKCodec reads only the header here: decompression bombs are refused before pixels are allocated.
        using var data = SKData.CreateCopy(original);
        using var codec = SKCodec.Create(data) ?? throw new ProblemException("image.unsupported_type");
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels) throw new ProblemException("image.unsupported_type");

        using var decoded = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new ProblemException("image.unsupported_type");

        using var oriented = ApplyOrigin(decoded, codec.EncodedOrigin);
        using var resized = DownscaleToLongEdge(oriented, MaxLongEdge);
        using var image = SKImage.FromBitmap(resized);
        using var webp = image.Encode(SKEncodedImageFormat.Webp, WebpQuality)
            ?? throw new InvalidOperationException("WebP encoding failed");

        return new ProcessedImage(webp.ToArray(), SHA256.HashData(original), resized.Width, resized.Height);
    }

    /// <summary>JPEG FF D8 FF, PNG 89 50 4E 47, WebP "RIFF....WEBP". Extension and Content-Type are ignored.</summary>
    public static bool HasImageMagicBytes(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])
        || bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47])
        || (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8));

    /// <summary>Reads at most <paramref name="limit"/> bytes; one byte more means the upload is too large (413).</summary>
    private static async Task<byte[]> ReadLimitedAsync(Stream upload, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await upload.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit) throw new ProblemException("image.too_large");
        }
        return buffer.ToArray();
    }

    /// <summary>Draws the bitmap upright according to the EXIF orientation (the 8 cases of SKEncodedOrigin).</summary>
    private static SKBitmap ApplyOrigin(SKBitmap source, SKEncodedOrigin origin)
    {
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (w, h) = swap ? (source.Height, source.Width) : (source.Width, source.Height);
        var target = new SKBitmap(new SKImageInfo(w, h, source.ColorType, source.AlphaType));
        using var canvas = new SKCanvas(target);
        canvas.SetMatrix(OriginMatrix(origin, source.Width, source.Height));
        using var sourceImage = SKImage.FromBitmap(source);
        // Orientation maps whole pixels to whole pixels, so nearest sampling keeps them exact.
        canvas.DrawImage(sourceImage, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        return target;
    }

    private static SKMatrix OriginMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),     // mirror horizontally
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), // rotate 180
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // mirror vertically
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),       // transpose
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),     // rotate 90 clockwise
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), // transverse
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),   // rotate 90 counter-clockwise
        _ => SKMatrix.Identity,
    };

    private static SKBitmap DownscaleToLongEdge(SKBitmap source, int maxLongEdge)
    {
        var longEdge = Math.Max(source.Width, source.Height);
        if (longEdge <= maxLongEdge) return source.Copy();
        var scale = (double)maxLongEdge / longEdge;
        var info = new SKImageInfo(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)),
            source.ColorType, source.AlphaType);
        return source.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidOperationException("resize failed");
    }
}
