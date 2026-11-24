using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MapConverter;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    Console.WriteLine("""
        MapConverter fetch-db2 --build <version> --db2 <dir>
        MapConverter pages-pack --build <version> --input <map pack> --out <dir> [--max-edge 624] [--jobs <n>] [--zip <file>]
        MapConverter retail --build <version> --db2 <dir> --out <dir>
            [--wow <install> --casc-lib <library>] [--remote] [--cache <dir>] [--jobs <n>] [--ids 2437,291]
        MapConverter offline --build <version> --manifest <file> --out <dir>
        """);
    return 0;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancel.Cancel(); };
try
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i++)
    {
        string key = args[i];
        if (!key.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument: {key}");
        string value = key == "--remote" ? "" : ++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal)
            ? args[i] : throw new ArgumentException($"Missing value for {key}.");
        if (!options.TryAdd(key, value)) throw new ArgumentException($"Duplicate option {key}.");
    }

    string? Optional(string key) => options.GetValueOrDefault("--" + key);
    string Required(string key) => Optional(key) ?? throw new ArgumentException($"Missing --{key}.");
    void Allow(params string[] keys)
    {
        foreach (string option in options.Keys)
            if (option != "--build" && !keys.Contains(option[2..])) throw new ArgumentException($"Unknown option {option}.");
    }

    string build = Required("build");
    if (!Regex.IsMatch(build, @"^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$")) throw new ArgumentException("Use a full Retail build, e.g. 12.1.5.70961.");

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("SCFarmingPaths-RouteStudio/1.0");
    switch (args[0])
    {
        case "fetch-db2":
            Allow("db2");
            string db2 = Required("db2");
            Directory.CreateDirectory(db2);
            foreach (string table in RetailData.Tables)
            {
                var bytes = await Downloads.Get(http, $"https://wago.tools/db2/{table}/csv?build={build}", cancel.Token);
                Output.Write(Path.Combine(db2, table + ".csv"), path => File.WriteAllBytes(path, bytes));
            }
            _ = new RetailData(db2);
            Output.Write(Path.Combine(db2, "build.txt"), path => File.WriteAllText(path, build));
            Console.WriteLine($"Saved {RetailData.Tables.Length} DB2 tables from Wago Tools for {build}.");
            return 0;

        case "pages-pack":
            Allow("input", "out", "max-edge", "jobs", "zip");
            await PagesPack.Write(build, Required("input"), Required("out"), int.Parse(Optional("max-edge") ?? "624"),
                int.Parse(Optional("jobs") ?? Math.Min(6, Environment.ProcessorCount).ToString()), Optional("zip"), cancel.Token);
            return 0;

        case "retail":
            Allow("db2", "out", "wow", "casc-lib", "remote", "cache", "jobs", "ids");
            string dbDirectory = Required("db2");
            var data = ReadRetail(dbDirectory, build);
            string? install = Optional("wow"), library = Optional("casc-lib");
            bool remote = Optional("remote") is not null;
            if ((install is null) != (library is null) || (install is null && !remote))
                throw new ArgumentException("Set both --wow and --casc-lib, use --remote, or combine them.");
            using (var casc = install is null ? null : new Casc(library!, install, build))
            {
                var source = new TileSource(casc, remote ? http : null, build, Optional("cache") ?? ".map-work/cache");
                int jobs = Optional("jobs") is string jobsText ? int.Parse(jobsText) : Math.Min(4, Environment.ProcessorCount);
                if (jobs is < 1 or > 32) throw new ArgumentException("--jobs must be between 1 and 32.");
                var ids = Optional("ids")?.Split(',').Select(int.Parse).ToHashSet();
                return await RunRetail(build, data, source, Required("out"), jobs, ids, cancel.Token);
            }

        case "offline":
            Allow("out", "manifest");
            return OfflineImport.Run(build, Required("manifest"), Required("out"), cancel.Token);

        default:
            throw new ArgumentException($"Unknown command {args[0]}.");
    }
}
catch (OperationCanceledException) when (cancel.IsCancellationRequested)
{
    Console.Error.WriteLine("Cancelled. Completed images are kept for the next run.");
    return 130;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static RetailData ReadRetail(string directory, string build)
{
    if (File.ReadAllText(Path.Combine(directory, "build.txt")).Trim() != build)
        throw new InvalidDataException("DB2 build.txt does not match --build.");
    return new RetailData(directory);
}

static async Task<int> RunRetail(string build, RetailData data, TileSource source, string output, int jobs,
    HashSet<int>? ids, CancellationToken cancellation)
{
    var maps = data.Maps.Where(map => ids is null || ids.Contains(map.Id)).ToArray();
    if (ids is not null && maps.Length != ids.Count)
        throw new ArgumentException("One or more requested IDs are absent from the pinned UiMap table.");

    Directory.CreateDirectory(Path.Combine(output, "art"));
    Directory.CreateDirectory(Path.Combine(output, "state"));
    var artMaps = maps.SelectMany(map => map.Links.Select(link => (link.ArtId, map.Id)))
        .ToLookup(item => item.ArtId, item => item.Id);
    var layers = artMaps.SelectMany(group => data.Art[group.Key]).ToArray();
    var rendered = new ConcurrentDictionary<(int, int), RenderResult>();
    var failures = new ConcurrentBag<Failure>();
    int done = 0;
    await Parallel.ForEachAsync(layers, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = cancellation },
        async (art, token) =>
        {
            try
            {
                rendered[(art.ArtId, art.Layer.Index)] = await MapRenderer.Render(art, source, build, output, token);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or NotSupportedException)
            {
                failures.Add(new Failure(art.ArtId, art.Layer.Index, artMaps[art.ArtId].Distinct().Order().ToArray(), error.Message));
            }

            int count = Interlocked.Increment(ref done);
            if (count % 100 == 0 || count == layers.Length)
                Console.WriteLine($"Processed {count}/{layers.Length} art layers.");
        });

    var catalog = new Catalog(build, []);
    var withoutArt = new List<int>();
    var failedMaps = new List<int>();
    foreach (var map in maps)
    {
        var planned = map.Links.SelectMany(link => data.Art[link.ArtId].Select(art => (link, art))).ToArray();
        var images = planned.Select(item => rendered.TryGetValue((item.art.ArtId, item.art.Layer.Index), out var image)
            ? image.Image with { PhaseId = item.link.PhaseId } : null).OfType<MapImage>().ToArray();
        if (planned.Length == 0) withoutArt.Add(map.Id);
        else if (images.Length == 0) failedMaps.Add(map.Id);
        catalog.Maps[map.Id] = new CatalogMap(map.Name, map.Type, images);
    }
    var errors = failures.OrderBy(failure => failure.ArtId).ThenBy(failure => failure.Layer).ToArray();
    var warnings = rendered.SelectMany(item => item.Value.Warnings.Select(reason =>
        new Failure(item.Key.Item1, item.Key.Item2, artMaps[item.Key.Item1].Distinct().Order().ToArray(), reason)))
        .Concat(data.UndeclaredLayers.Where(item => artMaps.Contains(item.ArtId)).Select(item =>
            new Failure(item.ArtId, item.Layer, artMaps[item.ArtId].Distinct().Order().ToArray(),
                "Tile records have no layer definition for this art style. The client has no dimensions for this layer.")))
        .OrderBy(item => item.ArtId).ThenBy(item => item.Layer).ThenBy(item => item.Reason, StringComparer.Ordinal).ToArray();
    var report = new ImportReport(build, ids is null ? "retail" : "retail subset", maps.Length,
        maps.Length - withoutArt.Count, maps.Length - withoutArt.Count - failedMaps.Count, withoutArt.Count, failedMaps.Count,
        errors.SelectMany(error => error.UiMapIds).Distinct().Count(), withoutArt.ToArray(), failedMaps.ToArray(),
        errors, warnings, Output.ImageBytes(output, catalog), source.LocalFiles, source.RemoteFiles);
    Output.Report(output, catalog, report);
    return errors.Length == 0 ? 0 : 2;
}
