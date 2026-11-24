using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace MapConverter;

internal static class OfflineImport
{
    private sealed record Manifest(string Build, Entry[] Maps);
    private sealed record Entry(int UiMapId, string Name, int Type, string? File, int Width, int Height,
        string? Source, int PhaseId, int ArtId, int Layer);

    public static int Run(string build, string manifestPath, string output, CancellationToken cancellation)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), Output.Json)
            ?? throw new InvalidDataException("Empty offline manifest.");
        if (manifest.Build != build || manifest.Maps is null || manifest.Maps.Length == 0)
            throw new InvalidDataException("The offline manifest must contain map rows and the same build as --build.");

        Directory.CreateDirectory(Path.Combine(output, "art"));
        var catalog = new Catalog(build, []);
        var failures = new List<Failure>();
        var withoutArt = new List<int>();
        var failed = new List<int>();
        foreach (var group in manifest.Maps.GroupBy(entry => entry.UiMapId).OrderBy(group => group.Key))
        {
            cancellation.ThrowIfCancellationRequested();
            var first = group.First();
            if (group.Key <= 0 || string.IsNullOrWhiteSpace(first.Name) ||
                group.Any(entry => entry.Name != first.Name || entry.Type != first.Type) ||
                group.Where(entry => entry.File is not null).GroupBy(entry => (entry.PhaseId, entry.ArtId, entry.Layer)).Any(set => set.Count() > 1))
                throw new InvalidDataException($"Invalid or duplicate map metadata for UiMapID {group.Key}.");

            var images = new List<MapImage>();
            foreach (var entry in group.OrderBy(entry => entry.PhaseId).ThenBy(entry => entry.ArtId).ThenBy(entry => entry.Layer))
            {
                if (entry.File is null) continue;
                try { images.Add(Import(entry, directory, output)); }
                catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or NotSupportedException or XmlException)
                {
                    failures.Add(new Failure(entry.ArtId, entry.Layer, [entry.UiMapId], error.Message));
                }
            }

            if (group.All(entry => entry.File is null)) withoutArt.Add(group.Key);
            else if (images.Count == 0) failed.Add(group.Key);
            catalog.Maps[group.Key] = new CatalogMap(first.Name, first.Type, images.ToArray());
        }

        int total = catalog.Maps.Count;
        Output.Report(output, catalog, new ImportReport(build, "offline manifest", total, total - withoutArt.Count,
            total - withoutArt.Count - failed.Count, withoutArt.Count, failed.Count,
            failures.SelectMany(error => error.UiMapIds).Distinct().Count(), withoutArt.ToArray(), failed.ToArray(),
            failures.ToArray(), [], Output.ImageBytes(output, catalog), 0, 0));
        return failures.Count == 0 ? 0 : 2;
    }

    private static void ValidateSvgBounds(XElement root, Entry entry)
    {
        static double Dimension(string? value)
        {
            value = value?.Trim();
            if (value?.EndsWith("px", StringComparison.Ordinal) == true) value = value[..^2];
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : 0;
        }

        bool IsMapRatio(double width, double height) => width > 0 && height > 0 &&
            Math.Abs(width / height - (double)entry.Width / entry.Height) < 0.000001;

        double width = Dimension((string?)root.Attribute("width"));
        double height = Dimension((string?)root.Attribute("height"));
        string? viewBox = (string?)root.Attribute("viewBox");
        bool valid = viewBox is null ? IsMapRatio(width, height) :
            System.Text.RegularExpressions.Regex.Split(viewBox.Trim(), @"[\s,]+") is [_, _, var boxWidth, var boxHeight] &&
            IsMapRatio(Dimension(boxWidth), Dimension(boxHeight)) &&
            (width <= 0 || height <= 0 || IsMapRatio(width, height));
        if (!valid)
            throw new InvalidDataException($"{entry.File}: SVG bounds must match the declared {entry.Width}:{entry.Height} aspect ratio.");
    }

    private static MapImage Import(Entry entry, string directory, string output)
    {
        if (entry.Width <= 0 || entry.Height <= 0 || string.IsNullOrWhiteSpace(entry.Source))
            throw new InvalidDataException($"{entry.File}: width, height and source are required.");
        string path = Path.GetFullPath(entry.File!, directory);
        string relativeInput = Path.GetRelativePath(directory, path);
        if (Path.IsPathRooted(relativeInput) || relativeInput == ".." || relativeInput.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new InvalidDataException($"{entry.File}: use an image within the manifest directory.");

        string extension = Path.GetExtension(path).ToLowerInvariant();
        var bytes = File.ReadAllBytes(path);
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
        if (extension == ".svg")
        {
            using var stream = new MemoryStream(bytes);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            var svg = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            if (svg.Root?.Name != XName.Get("svg", "http://www.w3.org/2000/svg") ||
                svg.DescendantNodes().OfType<XProcessingInstruction>().Any() ||
                svg.Descendants().Any(element => element.Name.Namespace != svg.Root.Name.Namespace ||
                    element.Name.LocalName is "script" or "foreignObject" or "style" or "a" or "animate" or "set" or "animateTransform" or "animateMotion" ||
                    element.Attributes().Any(attribute =>
                        attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                        attribute.Name.LocalName == "href" && !attribute.Value.StartsWith('#') ||
                        attribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase) &&
                        !System.Text.RegularExpressions.Regex.IsMatch(attribute.Value, @"^url\(#[\w.-]+\)$"))))
                throw new InvalidDataException($"{entry.File}: use a standalone SVG with a static drawing and local references.");
            ValidateSvgBounds(svg.Root, entry);
            string svgPath = $"art/offline-{fingerprint}.svg";
            string svgOutput = Path.Combine(output, svgPath);
            if (!File.Exists(svgOutput)) Output.Write(svgOutput, temporary => File.WriteAllBytes(temporary, bytes));
            return new MapImage(svgPath, entry.Width, entry.Height, entry.PhaseId, entry.ArtId, entry.Layer, entry.Source);
        }

        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp"))
            throw new InvalidDataException($"{entry.File}: supported types are PNG, JPEG, WebP and SVG.");
        var image = RasterFiles.ReadImage(bytes, entry.Width, entry.Height);
        string pngPath = $"art/offline-{fingerprint}.png";
        string pngOutput = Path.Combine(output, pngPath);
        if (!File.Exists(pngOutput))
            Output.Write(pngOutput, temporary => RasterFiles.SavePng(image, temporary));
        return new MapImage(pngPath, entry.Width, entry.Height, entry.PhaseId, entry.ArtId, entry.Layer, entry.Source);
    }
}
