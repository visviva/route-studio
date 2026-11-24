using System.Security.Cryptography;
using System.Text.Json;
using RouteStudio.Maps;

namespace MapConverter;

internal record RenderResult(MapImage Image, List<string> Warnings);

internal static class MapRenderer
{
    public static async Task<RenderResult> Render(ArtLayer art, TileSource source, string build, string output, CancellationToken cancellation)
    {
        var layer = art.Layer;
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { build, recipe = 3, art })))[..16];
        string relative = $"art/{art.ArtId}-{layer.Index}-{fingerprint}.png";
        string path = Path.Combine(output, relative);
        string state = Path.Combine(output, $"state/{Path.GetFileNameWithoutExtension(relative)}.json");
        if (File.Exists(path) && File.Exists(state))
        {
            var cached = JsonSerializer.Deserialize<RenderResult>(File.ReadAllText(state), Output.Json);
            if (cached is not null) return cached;
        }

        var warnings = new List<string>();
        var result = new RenderResult(new MapImage(relative, layer.Width, layer.Height, 0, art.ArtId, layer.Index,
            "Blizzard Retail UI map tiles"), warnings);
        var canvas = new Raster(layer.Width, layer.Height);
        foreach (var tile in art.Placements())
            await Place(tile);
        cancellation.ThrowIfCancellationRequested();
        Output.Write(path, temporary => RasterFiles.SavePng(canvas, temporary));
        Output.Write(state, temporary => File.WriteAllText(temporary, JsonSerializer.Serialize(result, Output.Json)));

        return result;

        async Task Place(TilePlacement tile)
        {
            var bytes = await source.Read(tile.FileId, cancellation);
            try
            {
                var image = BlpTiles.Decode(bytes);
                int drawWidth = tile.Width;
                int drawHeight = tile.Height;
                if (image.Width < drawWidth || image.Height < drawHeight)
                {
                    string message = $"FileDataID {tile.FileId}: tile is {image.Width}×{image.Height}; DB rectangle is {drawWidth}×{drawHeight}.";
                    warnings.Add(message + " Placed the available pixels at the recorded origin.");
                    drawWidth = Math.Min(drawWidth, image.Width);
                    drawHeight = Math.Min(drawHeight, image.Height);
                }
                BlpTiles.Blit(canvas, image, tile.X, tile.Y, drawWidth, drawHeight, tile.Blend);
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or NotSupportedException)
            {
                throw new InvalidDataException($"FileDataID {tile.FileId}: {error.Message}", error);
            }
        }
    }
}
