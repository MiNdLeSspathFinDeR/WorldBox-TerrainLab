param(
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\worldbox",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$modSource = Join-Path $PSScriptRoot "TerrainLab"
$modTarget = Join-Path $GamePath "Mods\TerrainLab"
$loaderPath = Join-Path $GamePath "worldbox_Data\StreamingAssets\mods\NeoModLoader.dll"
$projectPath = Join-Path $modSource "TerrainLab.csproj"

if (-not (Test-Path (Join-Path $GamePath "worldbox.exe"))) {
    throw "WorldBox was not found at: $GamePath"
}

if (-not (Test-Path $loaderPath)) {
    throw "NeoModLoader.dll is missing at: $loaderPath"
}

if (-not $SkipBuild) {
    & dotnet build $projectPath -c Debug "-p:WorldBoxPath=$GamePath" --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "TerrainLab build failed."
    }
}

New-Item -ItemType Directory -Force -Path $modTarget | Out-Null
Copy-Item -LiteralPath (Join-Path $modSource "mod.json") -Destination $modTarget -Force
Copy-Item -LiteralPath (Join-Path $modSource "icon.png") -Destination $modTarget -Force

foreach ($directoryName in @("Code", "Locales")) {
    $sourceDirectory = Join-Path $modSource $directoryName
    $targetDirectory = Join-Path $modTarget $directoryName
    New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
    Get-ChildItem -LiteralPath $sourceDirectory -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $targetDirectory -Force
    }
}

Write-Host "TerrainLab installed to $modTarget"
