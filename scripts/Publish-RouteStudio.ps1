[CmdletBinding()]
param(
    [string]$Output = 'publish/route-studio',
    [string]$BasePath = '/',
    [string]$MapPack,
    [switch]$Pages
)

$ErrorActionPreference = 'Stop'
$prefix = $BasePath.Trim('/')
if ($prefix -and ($prefix -notmatch '^[a-zA-Z0-9._~-]+(/[a-zA-Z0-9._~-]+)*$' -or
    $prefix.Split('/') -contains '.' -or $prefix.Split('/') -contains '..')) {
    throw 'BasePath must be a URL path, for example / or /SCFarmingPathsConverter/.'
}
$baseHref = if ($prefix) { "/$prefix/" } else { '/' }

$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if ((Test-Path -LiteralPath $outputPath) -and
    (!(Test-Path -LiteralPath $outputPath -PathType Container) -or (Get-ChildItem -LiteralPath $outputPath -Force))) {
    throw "Choose an empty output directory so previously published assets cannot be carried over: $outputPath"
}

$limit = 1000000000L
$files = @()
$mapBytes = 0L
if ($MapPack) {
    $packPath = (Resolve-Path -LiteralPath $MapPack).Path
    $catalogPath = Join-Path $packPath 'maps.json'
    $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json -AsHashtable
    if (!$catalog.Contains('maps')) { throw 'Map pack maps.json is missing its maps object.' }

    $imagePaths = @($catalog.maps.Values | ForEach-Object { $_.images } |
        ForEach-Object { $_.path } | Sort-Object -Unique)
    foreach ($relativePath in $imagePaths) {
        if ($relativePath -notmatch '^art/[a-zA-Z0-9_-]+\.(png|svg)$') {
            throw "Invalid art path in maps.json: $relativePath"
        }
        $files += Get-Item -LiteralPath (Join-Path $packPath $relativePath)
    }
    $mapBytes = (Get-Item -LiteralPath $catalogPath).Length
    foreach ($file in $files) { $mapBytes += $file.Length }
    if ($Pages -and $mapBytes -gt $limit) {
        throw "Map pack ($mapBytes bytes) exceeds the GitHub Pages 1 GB published site limit."
    }
}

$project = Join-Path $PSScriptRoot '../RouteStudio/SCFarmingPathsConverter.csproj'
dotnet publish $project -c Release -o $outputPath
if ($LASTEXITCODE -ne 0) { throw "dotnet publish exited with $LASTEXITCODE." }

$site = Join-Path $outputPath 'wwwroot'
$index = Join-Path $site 'index.html'
$html = Get-Content -LiteralPath $index -Raw
if (!$html.Contains('<base href="/">')) { throw 'Expected a root base element in index.html.' }
$html.Replace('<base href="/">', "<base href=`"$baseHref`">") | Set-Content -LiteralPath $index -NoNewline

$siteBytes = (Get-ChildItem -LiteralPath $site -File -Recurse -Force | Measure-Object -Property Length -Sum).Sum
if ($Pages -and ($siteBytes + $mapBytes) -gt $limit) {
    throw "Published site plus maps exceeds the GitHub Pages 1 GB limit ($($siteBytes + $mapBytes) bytes)."
}

if ($MapPack) {
    $maps = Join-Path $site 'maps'
    $art = Join-Path $maps 'art'
    New-Item -ItemType Directory -Force $art | Out-Null
    foreach ($file in $files) { Copy-Item -LiteralPath $file.FullName -Destination $art }
    Copy-Item -LiteralPath $catalogPath -Destination (Join-Path $maps 'maps.json')
    Remove-Item -LiteralPath (Join-Path $maps 'maps.json.br'), (Join-Path $maps 'maps.json.gz') -ErrorAction SilentlyContinue
}

$actualBytes = (Get-ChildItem -LiteralPath $site -File -Recurse -Force | Measure-Object -Property Length -Sum).Sum
Write-Host "Published $actualBytes bytes under $baseHref to $site"
