using System.Globalization;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using RouteStudio.Maps;

namespace MapConverter;

internal record Tile(int Row, int Column, int FileId);
internal record Layer(int Index, int Width, int Height, int TileWidth, int TileHeight);
internal record Overlay(int Id, int Width, int Height, int X, int Y, int Flags, Tile[] Tiles);
internal record ArtLayer(int ArtId, Layer Layer, Tile[] Tiles, Overlay[] Overlays)
{
    public IEnumerable<TilePlacement> Placements()
    {
        var layer = Layer;
        return Place(Tiles, 0, 0, layer.Width, layer.Height, false).Concat(
            Overlays.SelectMany(overlay => Place(overlay.Tiles, overlay.X, overlay.Y, overlay.Width, overlay.Height, true)));

        IEnumerable<TilePlacement> Place(Tile[] tiles, int offsetX, int offsetY, int width, int height, bool blend)
        {
            foreach (var tile in tiles)
            {
                if (tile.FileId == 0) continue;
                int x = tile.Column * layer.TileWidth, y = tile.Row * layer.TileHeight;
                if (x < 0 || y < 0 || x >= width || y >= height)
                    throw new InvalidDataException($"FileDataID {tile.FileId}: tile outside recorded bounds.");
                yield return new TilePlacement(tile.FileId, offsetX + x, offsetY + y,
                    Math.Min(layer.TileWidth, width - x), Math.Min(layer.TileHeight, height - y), blend);
            }
        }
    }
}
internal record MapLink(int PhaseId, int ArtId);
internal record UiMap(int Id, string Name, int Type, MapLink[] Links);

internal sealed class RetailData
{
    public static readonly string[] Tables = [
        "UiMap", "UiMapXMapArt", "UiMapArt", "UiMapArtStyleLayer", "UiMapArtTile", "WorldMapOverlay", "WorldMapOverlayTile"
    ];

    public UiMap[] Maps { get; }
    public Dictionary<int, ArtLayer[]> Art { get; }
    public (int ArtId, int Layer)[] UndeclaredLayers { get; }

    public RetailData(string directory)
    {
        var links = Rows(directory, "UiMapXMapArt").ToLookup(row => row.Int("UiMapID"),
            row => new MapLink(row.Int("PhaseID"), row.Int("UiMapArtID")));
        Maps = Rows(directory, "UiMap").Select(row => new UiMap(row.Int("ID"), row["Name_lang"], row.Int("Type"),
            links[row.Int("ID")].Distinct().OrderBy(link => link.PhaseId).ThenBy(link => link.ArtId).ToArray()))
            .OrderBy(map => map.Id).ToArray();
        if (Maps.Select(map => map.Id).Distinct().Count() != Maps.Length)
            throw new InvalidDataException("Duplicate UiMap IDs in DB2 input.");

        var styles = Rows(directory, "UiMapArtStyleLayer").ToLookup(row => row.Int("UiMapArtStyleID"),
            row => new Layer(row.Int("LayerIndex"), row.Int("LayerWidth"), row.Int("LayerHeight"),
                row.Int("TileWidth"), row.Int("TileHeight")));
        var artTiles = Rows(directory, "UiMapArtTile").ToLookup(
            row => (row.Int("UiMapArtID"), row.Int("LayerIndex")), ToTile);
        var overlayTiles = Rows(directory, "WorldMapOverlayTile").ToLookup(
            row => (row.Int("WorldMapOverlayID"), row.Int("LayerIndex")), ToTile);
        // Exploration textures do not use a player-specific condition or mouse-over state in a static map.
        var overlays = Rows(directory, "WorldMapOverlay")
            .Where(row => row.Int("PlayerConditionID") == 0 && (row.Int("Flags") & 2) == 0)
            .ToLookup(row => row.Int("UiMapArtID"));

        var artStyles = Rows(directory, "UiMapArt").ToDictionary(row => row.Int("ID"), row => row.Int("UiMapArtStyleID"));
        UndeclaredLayers = artTiles.Select(group => group.Key)
            .Where(key => !artStyles.TryGetValue(key.Item1, out int style) || !styles[style].Any(layer => layer.Index == key.Item2))
            .ToArray();
        Art = artStyles.ToDictionary(pair => pair.Key, pair =>
        {
            int artId = pair.Key;
            return styles[pair.Value].OrderBy(layer => layer.Index).Select(layer =>
                new ArtLayer(artId, layer, Ordered(artTiles[(artId, layer.Index)]),
                    overlays[artId].Select(overlay => new Overlay(overlay.Int("ID"), overlay.Int("TextureWidth"),
                        overlay.Int("TextureHeight"), overlay.Int("OffsetX"), overlay.Int("OffsetY"),
                        overlay.Int("Flags"), Ordered(overlayTiles[(overlay.Int("ID"), layer.Index)])))
                    .Where(overlay => overlay.Tiles.Length > 0)
                    .OrderBy(overlay => overlay.Flags & 1).ThenBy(overlay => overlay.Id).ToArray()))
                .Where(art => art.Tiles.Length > 0 || art.Overlays.Length > 0).ToArray();
        });

        var unknown = Maps.SelectMany(map => map.Links).FirstOrDefault(link => !Art.ContainsKey(link.ArtId));
        if (unknown is not null) throw new InvalidDataException($"Missing UiMapArt row {unknown.ArtId}.");
    }

    private static Tile ToTile(Row row) => new(row.Int("RowIndex"), row.Int("ColIndex"), row.Int("FileDataID"));
    private static Tile[] Ordered(IEnumerable<Tile> tiles) => tiles.OrderBy(tile => tile.Row).ThenBy(tile => tile.Column).ToArray();

    private static IEnumerable<Row> Rows(string directory, string table)
    {
        using var parser = new TextFieldParser(Path.Combine(directory, table + ".csv"));
        parser.SetDelimiters(",");
        var header = parser.ReadFields() ?? throw new InvalidDataException($"{table}: missing CSV header.");
        var columns = header.Select((name, index) => (name, index)).ToDictionary(column => column.name, column => column.index);
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields()!;
            if (fields.Length != header.Length) throw new InvalidDataException($"{table}: malformed CSV row.");
            yield return new Row(columns, fields);
        }
    }

    private sealed record Row(Dictionary<string, int> Columns, string[] Fields)
    {
        public string this[string name] => Fields[Columns[name]];
        public int Int(string name) => int.Parse(this[name], CultureInfo.InvariantCulture);
    }
}

internal record MapImage(string Path, int Width, int Height, int PhaseId, int ArtId, int Layer, string Source);
internal record CatalogMap(string Name, int Type, MapImage[] Images);
internal record Catalog(string Build, SortedDictionary<int, CatalogMap> Maps);
internal record Failure(int ArtId, int Layer, int[] UiMapIds, string Reason);
internal record ImportReport(string Build, string Scope, int UiMapIdsSeen, int MapsWithUsableArt, int MapsRendered,
    int MapsWithoutArt, int MapsFailed, int MapsWithVariantFailures, int[] WithoutArtIds, int[] FailedMapIds,
    Failure[] Failures, Failure[] Warnings, long MapPackBytes, long LocalFiles, long RemoteFiles);

internal static class Output
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static void Write(string path, Action<string> write)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            write(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public static void Report(string directory, Catalog catalog, ImportReport report)
    {
        Write(Path.Combine(directory, "maps.json"), path => File.WriteAllText(path, JsonSerializer.Serialize(catalog, Json)));
        Write(Path.Combine(directory, "report.json"), path => File.WriteAllText(path, JsonSerializer.Serialize(report, Json)));
        Console.WriteLine($"Build {report.Build} ({report.Scope}): {report.UiMapIdsSeen} IDs, {report.MapsWithUsableArt} with art records, " +
            $"{report.MapsRendered} rendered, {report.MapsWithoutArt} without art, {report.MapsFailed} unavailable due to errors, " +
            $"{report.MapsWithVariantFailures} with failed variants/layers.");
        Console.WriteLine($"{report.MapPackBytes:N0} image bytes. Local CASC reads: {report.LocalFiles:N0}; remote: {report.RemoteFiles:N0}.");
        Console.WriteLine($"{report.Warnings.Length} rendering warnings.");
        Console.WriteLine(Path.GetFullPath(Path.Combine(directory, "report.json")));
    }

    public static long ImageBytes(string directory, Catalog catalog) => catalog.Maps.Values.SelectMany(map => map.Images)
        .Select(image => image.Path).Distinct().Sum(path => new FileInfo(Path.Combine(directory, path)).Length);
}
