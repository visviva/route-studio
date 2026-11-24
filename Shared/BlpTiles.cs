using System.Buffers.Binary;
using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace RouteStudio.Maps;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct Pixel(byte R, byte G, byte B, byte A);

internal sealed class Raster(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public Pixel[] Pixels { get; } = new Pixel[checked(width * height)];
}

internal static class BlpTiles
{
    public static void Blit(Raster canvas, Raster image, int x, int y, int width, int height, bool blend)
    {
        int startX = Math.Max(0, -x), endX = Math.Min(width, canvas.Width - x);
        int startY = Math.Max(0, -y), endY = Math.Min(height, canvas.Height - y);
        if (endX <= startX || endY <= startY) return;

        for (int row = startY; row < endY; row++)
        {
            var input = image.Pixels.AsSpan(row * image.Width + startX, endX - startX);
            var output = canvas.Pixels.AsSpan((y + row) * canvas.Width + x + startX, input.Length);
            if (!blend)
            {
                input.CopyTo(output);
                continue;
            }

            for (int column = 0; column < input.Length; column++)
            {
                Pixel top = input[column];
                if (top.A == 0) continue;
                Pixel bottom = output[column];
                if (top.A == 255 || bottom.A == 0)
                {
                    output[column] = top;
                    continue;
                }
                int foreground = top.A * 255, background = bottom.A * (255 - top.A);
                int alpha = foreground + background;
                byte Channel(int t, int b) => (byte)((t * foreground + b * background + alpha / 2) / alpha);
                output[column] = new Pixel(Channel(top.R, bottom.R), Channel(top.G, bottom.G),
                    Channel(top.B, bottom.B), (byte)((alpha + 127) / 255));
            }
        }
    }

    public static Raster Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 148 || !bytes[..4].SequenceEqual("BLP2"u8))
            throw new InvalidDataException("Expected a BLP2 map tile.");
        if (BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]) != 1)
            throw new InvalidDataException("Unsupported BLP2 content type.");

        int encoding = bytes[8], depth = bytes[9], alphaEncoding = bytes[10];
        int width = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes[84..]);
        if (width is < 1 or > 4096 || height is < 1 or > 4096 ||
            offset < 148 || length <= 0 || offset > bytes.Length - length)
            throw new InvalidDataException("Invalid BLP2 dimensions or mip bounds.");
        var data = bytes.Slice(offset, length);
        int count = width * height;
        var raster = new Raster(width, height);

        if (encoding == 2)
        {
            var format = (depth, alphaEncoding) switch
            {
                (0, _) => CompressionFormat.Bc1,
                (1, _) => CompressionFormat.Bc1WithAlpha,
                (_, 1) => CompressionFormat.Bc2,
                (_, 7) => CompressionFormat.Bc3,
                _ => throw new InvalidDataException($"Unknown BLP2 alpha encoding {depth}/{alphaEncoding}.")
            };
            var decoder = new BcDecoder();
            decoder.Options.IsParallel = false;
            var pixels = decoder.DecodeRaw(data.ToArray(), width, height, format);
            MemoryMarshal.AsBytes(pixels.AsSpan()).CopyTo(MemoryMarshal.AsBytes(raster.Pixels.AsSpan()));
            return raster;
        }

        if (encoding == 3 && data.Length >= count * 4)
        {
            for (int i = 0; i < count; i++)
            {
                int pixel = i * 4;
                raster.Pixels[i] = new Pixel(data[pixel + 2], data[pixel + 1], data[pixel], data[pixel + 3]);
            }
            return raster;
        }

        if (encoding != 1 || depth is not (0 or 1 or 4 or 8) || offset < 1172 ||
            data.Length < count + (count * depth + 7) / 8)
            throw new InvalidDataException($"Unsupported or truncated BLP2 encoding {encoding}/{depth}.");

        var palette = bytes.Slice(148, 1024);
        var alphaData = data[count..];
        for (int i = 0; i < count; i++)
        {
            int color = data[i] * 4;
            byte alpha = depth switch
            {
                1 => (byte)(((alphaData[i / 8] >> (i % 8)) & 1) * 255),
                4 => (byte)(((alphaData[i / 2] >> ((i % 2) * 4)) & 15) * 17),
                8 => alphaData[i],
                _ => 255
            };
            raster.Pixels[i] = new Pixel(palette[color + 2], palette[color + 1], palette[color], alpha);
        }
        return raster;
    }
}
