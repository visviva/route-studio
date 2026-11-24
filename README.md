# SC Farming Paths – Route Studio

Route Studio converts TomTom and Wowhead waypoints in the browser. The preview shows the Retail map for the supplied UI map ID and traces the route from start to finish in waypoint order. If the map is missing or its image fails to load, the route appears on a labeled coordinate grid.

```text
/way #2437 34.39 33.04 Necrohexxer Raz'ka
/way #2437 51.84 72.92 Skullcrusher Harak
/way #2437 50.86 65.17 Mrrlokk
```

Use **+** and **−** to zoom. When the map has focus, you can also zoom with the mouse wheel or the plus and minus keys, and pan with the arrow keys. You can drag a zoomed map with a mouse or touch. Choose **Crop** and drag to select a rectangle. **Full map** resets the zoom and crop. Cropping affects the preview only. Exported coordinates always refer to the full map.

Use the same map ID on every line. The parser accepts decimal points and commas and supports both `/way` and `/waypoint`. In the **Order** tab, drag the waypoint handles or use the move buttons to reorder the route. The **Text** tab, map preview, and next export use the updated order.

Choose a route shape and select **Optimize** to minimize straight-line map distance. **Shortest path** chooses both endpoints. **Loop** keeps the first waypoint as the start and counts the return leg, shown as a dashed line in the preview. The exported waypoint list contains each input waypoint once. The optimizer finds the exact solution for up to 14 waypoints and uses nearest neighbor and 2-opt for longer routes. Distances use the map image proportions when available.

The export includes the route name and coordinates in waypoint order. The preview shows waypoint labels. Paste the `!FP1!` string into the addon's **Import** feature. The format uses CBOR byte strings, raw DEFLATE, and Base64.

## Build and development

`RouteStudio.slnx` contains the standalone Blazor WebAssembly app in `RouteStudio/` and the CLI in `MapConverter/`. Run the commands from the repository root in PowerShell 7.

```powershell
dotnet --version
dotnet workload install wasm-tools
dotnet build RouteStudio.slnx -c Release
dotnet run --project RouteStudio/SCFarmingPathsConverter.csproj
```

`global.json` pins the SDK to 11.0.100. To preview game maps locally, include the compact map pack when publishing the static site as described below.

## Generate Retail maps

The CLI reads build-specific Wago Tools exports and resolves textures through CASC FileDataIDs. Building CascLib on Windows requires CMake and Visual Studio 2026 with the C++ tools installed. The DLL and the .NET process must use the same architecture. [CascLib](https://github.com/ladislav-zezula/CascLib)

```powershell
$build = '12.1.5.70961'
$wow = 'C:\Program Files (x86)\World of Warcraft'
Get-Content -LiteralPath (Join-Path $wow '.build.info')

git clone https://github.com/ladislav-zezula/CascLib.git .map-work/CascLib
git -C .map-work/CascLib checkout 6b889ec8f421cbc9baccec296c582362ab917887
cmake -S .map-work/CascLib -B .map-work/CascLib-build -G "Visual Studio 18 2026" -A x64 -D CMAKE_POLICY_VERSION_MINIMUM=3.5 -D CASC_BUILD_SHARED_LIB=ON -D CASC_UNICODE=ON
cmake --build .map-work/CascLib-build --config Release --parallel 8
$casc = '.map-work/CascLib-build/Release/CascLib.dll'

dotnet run --project MapConverter -c Release --no-build -- fetch-db2 --build $build --db2 ".map-work/db2-$build"
dotnet run --project MapConverter -c Release --no-build -- retail --build $build --db2 ".map-work/db2-$build" --wow $wow --casc-lib $casc --jobs 8 --out ".map-work/retail-$build"
dotnet run --project MapConverter -c Release --no-build -- retail --build $build --db2 ".map-work/db2-$build" --wow $wow --casc-lib $casc --ids 2437,291,997,179,55 --out .map-work/demo
```

If `cmake` is missing from `PATH`, use the `cmake.exe` installed with Visual Studio. `fetch-db2` downloads `UiMap`, `UiMapXMapArt`, `UiMapArt`, `UiMapArtStyleLayer`, `UiMapArtTile`, `WorldMapOverlay`, and `WorldMapOverlayTile`. Store each DB2 version in a separate directory. The CLI checks the requested build against `build.txt` and, for CASC, against the active `wow` product in `.build.info`. CASC uses the textures available in the installed client. The Zul’Aman map generated for the example contains German place names.

Use `--remote` to fetch missing files for the requested build through the Wago Tools CASC endpoint. If you omit `--wow` and `--casc-lib`, the CLI uses Wago Tools for all files. Run a single-map import to check the remote source before importing the full catalog over HTTP.

```powershell
dotnet run --project MapConverter -c Release --no-build -- retail --build $build --db2 ".map-work/db2-$build" --remote --ids 291 --out .map-work/remote-floor
```

`maps.json` is indexed by UiMapID. The coordinate origin is the upper-left corner of the full, uncropped map area. The renderer assembles raster tiles using the layer dimensions, rows, and columns. It then places unconditional exploration textures at their recorded offsets and preserves transparency. A FileDataID of 0 represents an empty tile. Conditional and mouseover-only overlays depend on game state, so the static map omits them. The base phase (0) comes first, followed by ascending phase ID, art ID, and layer. Multiple phases can reference the same image file.

`report.json` counts UiMapIDs inspected, IDs with linked map art, IDs rendered, IDs without map art, and errors by art and layer. Warnings include missing layer definitions and smaller tiles placed at their recorded positions. To resume a batch, reuse the same output directory. The CLI reuses completed PNGs and their verification records in `state/` and stores Wago downloads in `.map-work/cache/` by default. Exit code 2 means the CLI wrote a partial catalog after one or more maps or variants failed. Publishing copies only the images referenced by the current catalog.

### Offline import

Use a local manifest to import assembled maps, such as maps built from previously exported game textures. Add one entry per phase or layer. The `file` path is relative to the manifest. Omit `file` to create an entry that uses the coordinate grid. The CLI converts PNG, JPEG, and WebP input to PNG and copies self-contained static SVGs with the correct map proportions byte for byte.

```json
{
  "build": "12.1.5.70961",
  "maps": [
    {
      "uiMapId": 2437,
      "name": "Zul’Aman",
      "type": 3,
      "file": "zulaman.png",
      "width": 3840,
      "height": 2560,
      "source": "Blizzard Retail 12.1.5.70961, full export",
      "phaseId": 0,
      "artId": 1963,
      "layer": 0
    },
    { "uiMapId": 997, "name": "Tirisfal Glades", "type": 3 }
  ]
}
```

```powershell
dotnet run --project MapConverter -c Release --no-build -- offline --build $build --manifest .map-work/preexport/manifest.json --out .map-work/offline
```

### Results for 12.1.5.70961

The full local run read 85,199 textures from CASC. Of 1,967 UiMapIDs, 1,939 had linked map images and rendered successfully. The remaining 28 had no linked UI map. The output contained 1,765 unique PNGs and totaled **4,316,154,132 bytes** including `maps.json`. The report recorded zero failed declared layers and 12 warnings. Eleven textures in maps #179, #1804, #1888, and #2016 were smaller than their DB2 rectangles. Map #1550 also had tile data for a layer with missing dimensions. The `BkgAtlasID` for #2252 points to an Adventure Map background tile. Map #2252 was one of the 28 without map images. The referenced Retail textures were BLP raster data. SVG support was verified with a separate vector file.

The static build was tested with the Zul’Aman example route (#2437), a dungeon floor in The Deadmines (#291), a micro map (#55), and the coordinate grid (#997). The Zul’Aman data confirms all three example coordinates. [Wowhead · Skullcrusher Harak](https://www.wowhead.com/npc=242025/skullcrusher-harak)

## Generate a compact map pack

`pages-pack` takes the catalog and assembled images from the local CASC import and caps the longest edge at 624 pixels. It preserves the aspect ratio up to whole-pixel rounding. PNGs retain transparency and the full map area. SVGs are copied as-is. The catalog keeps its UiMapIDs, layers, and phases and records the new image dimensions. Route coordinates still refer to the full map area.

```powershell
dotnet run --project MapConverter -c Release --no-build -- pages-pack --build $build --input ".map-work/retail-$build" --out ".map-work/pages-pack-624-$build" --max-edge 624 --jobs 8 --zip .map-work/route-studio-maps.zip
```

`pack-report.json` records the size of the images and catalog. The ZIP archive contains the files needed by GitHub Pages. Adjust `--max-edge` for other builds as needed. Use separate output directories for new map imports or different target sizes.

The compact pack for 12.1.5.70961 contains 1,765 PNGs for 1,939 UiMapIDs. It totals 929,650,895 bytes including the catalog. The complete Pages build tested with this pack totaled 948,376,263 bytes.

## Publish the static site

```powershell
pwsh -NoProfile -File scripts/Publish-RouteStudio.ps1 -Output publish/subpath -BasePath /SCFarmingPathsConverter/ -MapPack ".map-work/pages-pack-624-$build" -Pages
New-Item -ItemType Directory -Force publish/serve | Out-Null
Copy-Item -LiteralPath publish/subpath/wwwroot -Destination publish/serve/SCFarmingPathsConverter -Recurse
python -m http.server 8080 --bind 127.0.0.1 --directory publish/serve
```

Open <http://127.0.0.1:8080/SCFarmingPathsConverter/>. Use `-BasePath /` when hosting at the root. The browser loads the image for the selected UiMapID and phase from the same host. The static site bundles its maps and works without runtime requests to Wago Tools.

### GitHub Pages

1. In the repository, open **Settings → Pages → Build and deployment** and select **GitHub Actions** as the source.
2. Once the map assets are approved for publication, upload the ZIP to the repository as a release asset named `route-studio-maps.zip`. For example, use the release tag `retail-12.1.5.70961-maps`.
3. Open **Actions → Deploy Route Studio to GitHub Pages → Run workflow** and enter the release tag in `map_pack_release`.

With GitHub CLI configured for this repository, use the following commands.

```powershell
gh release create retail-12.1.5.70961-maps .map-work/route-studio-maps.zip --title "Retail 12.1.5.70961 map pack" --notes "Compact PNG map pack for Route Studio."
gh workflow run pages.yml -f map_pack_release=retail-12.1.5.70961-maps
```

The workflow uses the hosting path supplied by `actions/configure-pages` and checks the size of the unpacked site, including the Blazor files, against the GitHub Pages 1 GB limit. Storing the map pack as a release asset keeps large binary files out of Git history. [GitHub Pages workflow](https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages) · [GitHub Pages limits](https://docs.github.com/en/pages/getting-started-with-github-pages/github-pages-limits) · [GitHub Releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)

## Map sources and publication

The map assets come from Blizzard's Retail client. Wago Tools provides the tables and serves textures by FileDataID on request. The implementation follows wow.export and the published WoW UI source for the BLP format and tile placement. The catalog records the source of separately imported maps. [wow.export BLP decoder](https://github.com/Marlamin/wow.export/blob/caf2bb0006930e151cd3810fba0f7d55e2e4b562/src/js/casc/blp.js) · [WoW UI map layer](https://github.com/Gethe/wow-ui-source/blob/live/Interface/AddOns/Blizzard_MapCanvas/Blizzard_MapCanvasDetailLayer.lua)

World of Warcraft® and its map assets belong to Blizzard Entertainment or their respective rights holders. Access through the game client or a third party does not grant permission to redistribute the assets publicly. The repository maintainer verifies applicable terms and permissions before publishing maps, including on a separate host. Generated maps stay in local output directories ignored by Git by default. [Blizzard End User License Agreement](https://www.blizzard.com/en-gb/legal/08b946df-660a-40e4-a072-1fbde65173b1/blizzard-end-user-license-agreement) · [Blizzard Legal FAQ](https://www.blizzard.com/en-sg/legal/c1ae32ac-7ff9-4ac3-a03b-fc04b8697010/blizzard-legal-faq)
