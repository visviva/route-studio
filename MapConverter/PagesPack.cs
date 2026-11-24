using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MapConverter;

internal static class PagesPack
{
    public static async Task Write(string build, string input, string output, int maxEdge, int jobs,
        string? zip, CancellationToken cancellation)
    {
        if (maxEdge is < 64 or > 4096) throw new ArgumentOutOfRangeException(nameof(maxEdge), "Use 64–4096 pixels.");
        if (jobs is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(jobs), "Use 1–32 workers.");
        if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(input)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a separate output directory for the compact pack.");

        var source = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(Path.Combine(input, "maps.json")), Output.Json);
        if (source is null || source.Build != build)
            throw new InvalidDataException("The input catalog build must match --build.");

        var images = source.Maps.Values.SelectMany(map => map.Images).DistinctBy(image => image.Path).ToArray();
        var paths = new ConcurrentDictionary<string, (string File, int Width, int Height)>();
        Directory.CreateDirectory(Path.Combine(output, "art"));
        int done = 0;
        await Parallel.ForEachAsync(images, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = cancellation },
            (image, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (!Regex.IsMatch(image.Path, @"^art/[a-zA-Z0-9_-]+\.(png|svg)$") || image.Width <= 0 || image.Height <= 0)
                    throw new InvalidDataException($"Invalid map entry: {image.Path}");

                string from = Path.Combine(input, image.Path);
                bool vector = image.Path.EndsWith(".svg", StringComparison.Ordinal);
                double scale = Math.Min(1, (double)maxEdge / Math.Max(image.Width, image.Height));
                int width = vector ? image.Width : Math.Max(1, (int)Math.Round(image.Width * scale));
                int height = vector ? image.Height : Math.Max(1, (int)Math.Round(image.Height * scale));
                string relative = vector ? image.Path : $"art/{maxEdge}-{Path.GetFileName(image.Path)}";
                string to = Path.Combine(output, relative);
                if (!File.Exists(from)) throw new FileNotFoundException("Map image is missing.", from);
                if (!File.Exists(to))
                {
                    Output.Write(to, temporary =>
                    {
                        if (vector) File.Copy(from, temporary);
                        else RasterFiles.ResizePng(from, temporary, image.Width, image.Height, width, height);
                    });
                }
                paths[image.Path] = (relative, width, height);

                int count = Interlocked.Increment(ref done);
                if (count % 100 == 0 || count == images.Length) Console.WriteLine($"Optimized {count}/{images.Length} images.");
                return ValueTask.CompletedTask;
            });

        var catalog = new Catalog(build, []);
        foreach (var (id, map) in source.Maps)
        {
            catalog.Maps.Add(id, map with
            {
                Images = map.Images.Select(image =>
                {
                    var result = paths[image.Path];
                    return image with { Path = result.File, Width = result.Width, Height = result.Height };
                }).ToArray()
            });
        }
        var files = paths.Values.Select(image => image.File).Order(StringComparer.Ordinal).Prepend("maps.json").ToArray();
        Output.Write(Path.Combine(output, "maps.json"), path => File.WriteAllText(path, JsonSerializer.Serialize(catalog, Output.Json)));
        long packBytes = files.Sum(file => new FileInfo(Path.Combine(output, file)).Length);
        int withArt = catalog.Maps.Values.Count(map => map.Images.Length != 0);
        var report = new { build, maxEdge, mapIds = catalog.Maps.Count, mapsWithArt = withArt,
            mapsWithoutArt = catalog.Maps.Count - withArt, uniqueImages = paths.Count, packBytes };
        Output.Write(Path.Combine(output, "pack-report.json"), path => File.WriteAllText(path, JsonSerializer.Serialize(report, Output.Json)));
        Console.WriteLine($"{paths.Count:N0} unique images. Map pack including catalog: {packBytes:N0} bytes.");

        if (zip is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zip))!);
        Output.Write(zip, path =>
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(file, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var target = entry.Open();
                using var image = File.OpenRead(Path.Combine(output, file));
                image.CopyTo(target);
            }
        });
        Console.WriteLine($"Release archive: {Path.GetFullPath(zip)} ({new FileInfo(zip).Length:N0} bytes).");
    }
}
