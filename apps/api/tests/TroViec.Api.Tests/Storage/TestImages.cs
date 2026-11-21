using System.Buffers.Binary;
using SkiaSharp;

namespace TroViec.Api.Tests.Storage;

/// <summary>Builds test images in memory (no binary fixtures in the repo).</summary>
public static class TestImages
{
    /// <summary>Left half red, right half blue, so orientation can be checked from pixel colours.</summary>
    public static byte[] Encode(int width, int height, SKEncodedImageFormat format, int quality = 90)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.DrawRect(0, 0, width / 2f, height, new SKPaint { Color = SKColors.Red });
            canvas.DrawRect(width / 2f, 0, width / 2f, height, new SKPaint { Color = SKColors.Blue });
        }
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, quality).ToArray();
    }

    /// <summary>JPEG with an APP1 EXIF segment carrying Orientation and a GPS IFD (latitude ref "N").</summary>
    public static byte[] JpegWithExif(int width, int height, ushort orientation)
    {
        var jpeg = Encode(width, height, SKEncodedImageFormat.Jpeg);
        var app1 = ExifApp1(orientation);
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }

    /// <summary>A PNG that only declares its size (IHDR) — enough for a header read, never decodable.</summary>
    public static byte[] PngHeaderOnly(int width, int height)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA
        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", ihdr), .. Chunk("IEND", [])];
    }

    public static readonly byte[] GpsMarker = "N\0"u8.ToArray();

    private static byte[] ExifApp1(ushort orientation)
    {
        // TIFF little-endian: IFD0 at 8 with Orientation + GPSInfo pointer; GPS IFD at 38 with GPSLatitudeRef = "N".
        var tiff = new byte[56];
        "II"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 2);
        Entry(tiff, 10, 0x0112, type: 3, count: 1, value: orientation);       // Orientation (SHORT)
        Entry(tiff, 22, 0x8825, type: 4, count: 1, value: 38);                // GPSInfo IFD offset (LONG)
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(38), 1);
        Entry(tiff, 40, 0x0001, type: 2, count: 2, value: 0x4E);              // GPSLatitudeRef "N\0" inline
        byte[] payload = [.. "Exif\0\0"u8, .. tiff];
        var length = (ushort)(payload.Length + 2);
        return [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload];
    }

    private static void Entry(byte[] tiff, int at, ushort tag, ushort type, uint count, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 8), value);
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
