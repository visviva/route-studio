# SC Farming Paths – Route Studio

Route Studio konvertiert TomTom- und Wowhead-Wegpunkte im Browser. Die Vorschau zeigt zu einer Retail-UI-Map-ID die passende Karte mit Start, Ziel und Route in Eingabereihenfolge. Wenn die Karte fehlt oder ihr Bild nicht geladen werden kann, bleibt die Route auf einem beschrifteten Koordinatenraster sichtbar.

```text
/way #2437 34.39 33.04 Necrohexxer Raz'ka
/way #2437 51.84 72.92 Skullcrusher Harak
/way #2437 50.86 65.17 Mrrlokk
```

Mit **+** und **−** lässt sich die Karte zoomen. Eine fokussierte Karte unterstützt auch Mausrad, Plus, Minus und die Pfeiltasten. Vergrößerte Karten können mit der Maus oder per Touch verschoben werden. **Zuschneiden** aktiviert eine rechteckige Auswahl per Ziehen. **Ganze Karte** setzt Zoom und Ausschnitt zurück. Der Ausschnitt gilt für die Vorschau. Die Exportkoordinaten beziehen sich weiterhin auf die vollständige Karte.

Jede Zeile braucht dieselbe Karten-ID. Dezimalpunkt und Dezimalkomma sowie `/way` und `/waypoint` werden unterstützt. Der Export enthält den Routennamen und die Koordinaten in Eingabereihenfolge. Wegpunktbeschriftungen erscheinen in der Vorschau. Das Addon übernimmt den `!FP1!`-String über seine Import-Funktion. Das bestehende Format verwendet CBOR-Byte-Strings, Raw Deflate und Base64.

## Build und Entwicklung

`RouteStudio.slnx` enthält die eigenständige Blazor-WebAssembly-App in `RouteStudio/` und die CLI in `MapConverter/`. Die Befehle verwenden PowerShell 7 im Projektverzeichnis.

```powershell
dotnet --version
dotnet workload install wasm-tools
dotnet build RouteStudio.slnx -c Release
dotnet run --project RouteStudio/SCFarmingPathsConverter.csproj
```

`global.json` setzt das SDK auf 11.0.100. Für eine Vorschau mit Spielkarten das kompakte Kartenpaket über den unten beschriebenen statischen Build einbinden.

## Retail-Karten erzeugen

Die CLI liest Build-spezifische Wago-Tools-Exporte und löst die Texturen über CASC-FileDataIDs auf. Für den Windows-Build von [CascLib](https://github.com/ladislav-zezula/CascLib) werden CMake und Visual Studio 2026 mit C++-Tools vorausgesetzt. Die DLL muss dieselbe Architektur wie der .NET-Prozess haben.

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

Falls `cmake` nicht im PATH liegt, kann die mit Visual Studio installierte `cmake.exe` verwendet werden. `fetch-db2` speichert `UiMap`, `UiMapXMapArt`, `UiMapArt`, `UiMapArtStyleLayer`, `UiMapArtTile`, `WorldMapOverlay` und `WorldMapOverlayTile`. Jede DB2-Version bekommt ein eigenes Verzeichnis. Die CLI gleicht den ausgewählten Build mit `build.txt` und bei CASC mit dem aktiven Produkt `wow` in `.build.info` ab. CASC verwendet die verfügbaren Texturen des installierten Clients. Die erzeugte Zul’Aman-Karte enthält deutsche Ortsnamen.

Mit `--remote` lädt die CLI fehlende Dateien für den angegebenen Build über die Wago-Tools-CASC-Schnittstelle. Ohne `--wow` und `--casc-lib` arbeitet sie ausschließlich mit Wago Tools. Ein kleiner Lauf prüft die Quelle, ohne einen vollständigen Import per HTTP auszulösen.

```powershell
dotnet run --project MapConverter -c Release --no-build -- retail --build $build --db2 ".map-work/db2-$build" --remote --ids 291 --out .map-work/remote-floor
```

`maps.json` ist nach UiMapID indiziert. Bezugspunkt ist immer die linke obere Ecke der unbeschnittenen Kartenfläche. Rasterdaten werden anhand der Layerabmessungen, Zeilen und Spalten zusammengesetzt. Die statische Ansicht legt unbedingte Erkundungstexturen anhand ihrer aufgezeichneten Offsets darüber. Transparenz bleibt erhalten. Dateireferenzen mit FileDataID 0 sind leere Kacheln. Bedingte und reine Mouseover-Overlays hängen vom Spielzustand ab und sind in der statischen Karte nicht enthalten. Die Grundphase (0) hat Vorrang, danach werden Phasen-ID, Art-ID und Layer aufsteigend sortiert. Mehrere Phasen dürfen dieselbe Bilddatei verwenden.

`report.json` zählt die betrachteten UiMapIDs, IDs mit verknüpften Bilddaten, gerenderte IDs, IDs ohne Bilddaten und Fehler pro Art und Layer. Warnungen dokumentieren unter anderem fehlende Layerdefinitionen und kleinere Kacheln an ihrer vorgesehenen Position. Der Batch kann mit demselben Ausgabeverzeichnis fortgesetzt werden. Fertige PNGs und ihre Prüfprotokolle in `state/` werden wiederverwendet, Wago-Downloads liegen standardmäßig in `.map-work/cache/`. Exitcode 2 kennzeichnet fehlgeschlagene Karten oder Varianten bei geschriebenem Teilkatalog. Die Veröffentlichung kopiert ausschließlich Bilder, die der aktuelle Katalog referenziert.

### Offline-Import

Für bereits zusammengesetzte Karten, etwa aus vorab exportierten Spieltexturen, kann ein lokales Manifest importiert werden. Pro Phase oder Layer kann eine Zeile angegeben werden. `file` ist relativ zum Manifest. Eine Zeile ohne `file` liefert einen Katalogeintrag für das Raster. Rasterdateien (PNG, JPEG, WebP) werden in PNG konvertiert. Selbständige, statische SVGs mit passenden Kartenproportionen werden bytegenau übernommen.

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
      "source": "Blizzard Retail 12.1.5.70961, vollständiger Export",
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

### Ergebnis für 12.1.5.70961

Der vollständige lokale Lauf las 85.199 Texturen aus CASC. Von 1.967 UiMapIDs haben 1.939 verknüpfte Kartenbilder und wurden gerendert. 28 haben keine verknüpfte UI-Karte. Das sind 1.765 eindeutige PNGs und **4.316.154.132 Bytes** einschließlich `maps.json`. Der Bericht enthält keine fehlgeschlagenen deklarierten Layer und zwölf Warnungen. Elf Texturen in den Karten #179, #1804, #1888 und #2016 sind kleiner als ihre DB2-Rechtecke. #1550 hat außerdem Kacheldaten für einen Layer ohne zugehörige Layerabmessungen. `BkgAtlasID` bei #2252 verweist auf eine Adventure-Map-Hintergrundkachel. Die 28 IDs ohne Kartenbild enthalten #2252. Die referenzierten Retail-Texturen waren BLP-Rasterdaten. Der SVG-Pfad wurde mit einer separaten Vektordatei geprüft.

Im statischen Build geprüft wurden Zul’Aman #2437 mit der Beispielroute, The Deadmines #291 als Dungeon-Ebene, #55 als Mikrokarte und #997 mit Raster. Die drei Beispielkoordinaten sind im Zul’Aman-Datensatz belegt. [Wowhead · Skullcrusher Harak](https://www.wowhead.com/npc=242025/skullcrusher-harak)

## Kompaktes Kartenpaket erzeugen

`pages-pack` verarbeitet den Katalog und die zusammengesetzten Bilder aus dem lokalen CASC-Import. Die längste Kante wird auf 624 Pixel begrenzt. Das Seitenverhältnis bleibt bis auf die Rundung auf ganze Pixel erhalten. Die PNGs behalten Transparenz und den vollständigen Kartenbereich. SVGs werden unverändert übernommen. Der Katalog behält seine UiMapIDs, Ebenen und Phasen und erhält die neuen Bildabmessungen. Die Route verwendet weiterhin Koordinaten bezogen auf die vollständige Karte.

```powershell
dotnet run --project MapConverter -c Release --no-build -- pages-pack --build $build --input ".map-work/retail-$build" --out ".map-work/pages-pack-624-$build" --max-edge 624 --jobs 8 --zip .map-work/route-studio-maps.zip
```

`pack-report.json` enthält die Größe der Bilder und des Katalogs. Der ZIP-Export enthält die Dateien, die Pages benötigt. `--max-edge` kann für andere Builds angepasst werden. Für einen neuen Kartenimport und eine andere Zielgröße eignen sich eigene Ausgabeverzeichnisse.

Das kompakte Paket für 12.1.5.70961 enthält 1.765 PNGs für 1.939 UiMapIDs. Es belegt 929.650.895 Bytes einschließlich Katalog. Der vollständige getestete Pages-Build belegt 948.376.263 Bytes.

## Statisch veröffentlichen

```powershell
pwsh -NoProfile -File scripts/Publish-RouteStudio.ps1 -Output publish/subpath -BasePath /SCFarmingPathsConverter/ -MapPack ".map-work/pages-pack-624-$build" -Pages
New-Item -ItemType Directory -Force publish/serve | Out-Null
Copy-Item -LiteralPath publish/subpath/wwwroot -Destination publish/serve/SCFarmingPathsConverter -Recurse
python -m http.server 8080 --bind 127.0.0.1 --directory publish/serve
```

Danach `http://127.0.0.1:8080/SCFarmingPathsConverter/` öffnen. Für Root-Hosting `-BasePath /` verwenden. Der Browser lädt das Bild der gewählten UiMapID und Phase vom selben Host. Ein separates Kartenhosting oder ein Abruf von Wago Tools zur Laufzeit ist nicht erforderlich.

### GitHub Pages

1. Im Repository unter **Settings → Pages → Build and deployment** die Quelle **GitHub Actions** auswählen.
2. Wenn die Veröffentlichung des Kartenmaterials freigegeben ist, das ZIP als Release-Anhang namens `route-studio-maps.zip` im Repository bereitstellen. Als Release-Tag lässt sich zum Beispiel `retail-12.1.5.70961-maps` verwenden.
3. **Actions → Deploy Route Studio to GitHub Pages → Run workflow** öffnen und den Release-Tag bei `map_pack_release` angeben.

Die Befehle für ein mit dem Repository verbundenes GitHub CLI lauten

```powershell
gh release create retail-12.1.5.70961-maps .map-work/route-studio-maps.zip --title "Retail 12.1.5.70961 map pack" --notes "Compact PNG map pack for Route Studio."
gh workflow run pages.yml -f map_pack_release=retail-12.1.5.70961-maps
```

Der Workflow verwendet den Hostingpfad aus `actions/configure-pages` und prüft die Größe des entpackten Sites einschließlich Blazor-Dateien gegen die Pages-Grenze von 1 GB. Ein Release-Anhang vermeidet große Binärdateien im Git-Verlauf. [GitHub Pages · Workflow](https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages) · [GitHub Pages · Limits](https://docs.github.com/en/pages/getting-started-with-github-pages/github-pages-limits) · [GitHub Releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)

## Herkunft und Veröffentlichung der Karten

Das Kartenmaterial stammt aus dem Blizzard-Retail-Client. Wago Tools stellt die Tabellen und bei Bedarf Texturen anhand von FileDataIDs bereit. Das BLP-Format und die Platzierung der Spieltexturen wurden anhand von wow.export und dem veröffentlichten WoW-UI-Code nachvollzogen. Separat importierte Karten führen ihre Herkunft im Katalog. [wow.export · BLP-Decoder](https://github.com/Marlamin/wow.export/blob/caf2bb0006930e151cd3810fba0f7d55e2e4b562/src/js/casc/blp.js) · [WoW-UI · Kartenlayer](https://github.com/Gethe/wow-ui-source/blob/live/Interface/AddOns/Blizzard_MapCanvas/Blizzard_MapCanvasDetailLayer.lua)

World of Warcraft® und das Kartenmaterial gehören Blizzard Entertainment bzw. den jeweiligen Rechteinhabern. Die technische Verfügbarkeit über Spielclient oder Drittanbieter erteilt keine Genehmigung zur öffentlichen Weiterverbreitung. Der Repository-Betreiber prüft die anwendbaren Bedingungen und erforderlichen Rechte vor einer Veröffentlichung von Karten, einschließlich über einen separaten Host. Generierte Karten bleiben standardmäßig in den ignorierten lokalen Ausgabeverzeichnissen. [Blizzard · Endnutzervereinbarung](https://www.blizzard.com/en-gb/legal/08b946df-660a-40e4-a072-1fbde65173b1/blizzard-end-user-license-agreement) · [Blizzard · Fansite-FAQ](https://www.blizzard.com/de-de/legal/28d5ebbf-c245-4408-8ba9-043dd5f056bf/faq-uber-rechtliches)
