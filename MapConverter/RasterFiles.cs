using System.Runtime.InteropServices;
using SkiaSharp;
using RouteStudio.Maps;

namespace MapConverter;

internal static class RasterFiles
{
    private static SKImageInfo Info(Raster image) => new(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

    public static void SavePng(Raster image, string path)
    {
        var pin = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(Info(image), pin.AddrOfPinnedObject(), image.Width * 4);
            using var stream = File.Create(path);
            if (!pixmap.Encode(stream, new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 3)))
                throw new IOException("PNG encoding failed.");
        }
        finally { pin.Free(); }
    }

    public static void ResizePng(string from, string destination, int originalWidth, int originalHeight, int width, int height)
    {
        using var original = SKBitmap.Decode(from) ?? throw new InvalidDataException($"Could not decode {from}.");
        if (original.Width != originalWidth || original.Height != originalHeight)
            throw new InvalidDataException($"Image dimensions do not match the catalog for {from}.");
        using var resized = original.Resize(new SKImageInfo(width, height, original.ColorType, original.AlphaType, original.ColorSpace),
            new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? throw new IOException($"Could not resize {from}.");
        using var pixels = resized.PeekPixels();
        using var stream = File.Create(destination);
        if (!pixels.Encode(stream, new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6)))
            throw new IOException($"Could not encode {from} as PNG.");
    }

    public static Raster ReadImage(byte[] bytes, int width, int height)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("Unsupported raster image.");
        if (codec.Info.Width != width || codec.Info.Height != height || codec.FrameCount > 1)
            throw new InvalidDataException($"Expected a single {width}×{height} image.");

        var result = new Raster(width, height);
        var pin = GCHandle.Alloc(result.Pixels, GCHandleType.Pinned);
        try
        {
            if (codec.GetPixels(Info(result), pin.AddrOfPinnedObject()) != SKCodecResult.Success)
                throw new InvalidDataException("Raster decoding failed.");
        }
        finally { pin.Free(); }
        return result;
    }
}
