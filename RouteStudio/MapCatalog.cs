using System.Text.Json;
using System.Text.Json.Serialization;

namespace SCFarmingPathsConverter;

internal sealed record MapCatalog(string? Build, Dictionary<int, MapEntry>? Maps);
internal sealed record MapEntry(string Name, MapImage[] Images);
internal sealed record MapImage(string Path, int Width, int Height, int PhaseId, int ArtId, int Layer);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(MapCatalog))]
internal sealed partial class MapCatalogJson : JsonSerializerContext;
