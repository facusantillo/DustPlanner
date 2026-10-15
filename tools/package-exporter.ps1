<#
.SYNOPSIS
  Builds the DustPlanner HDT plugin (Release), publishes the DustPlanner service next to it and packs both as
  DustPlanner-v<version>.zip: one folder the user drops into HDT's Plugins folder.
.DESCRIPTION
  Requires lib\hdt to be populated (tools\fetch-hdt-refs.ps1 -FromInstall). Output goes to artifacts\release\ by default.
  The service (dustplanner-host.exe: panel, JSON API, OBS overlay, automatic sync) is published self-contained for
  win-x64, so the user needs no .NET runtime. Pass -NoService to package the plugin alone (developer builds).
  Pass -Install to also copy the plugin folder into %AppData%\HearthstoneDeckTracker\Plugins\DustPlanner (close HDT first).
#>
[CmdletBinding()]
param(
    [string]$OutputDir = "",
    [switch]$Install,
    [switch]$NoService
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $root "artifacts\release" }
$project = Join-Path $root "src\DustPlanner.HdtExporter\DustPlanner.HdtExporter.csproj"
$hostProject = Join-Path $root "src\DustPlanner.Host\DustPlanner.Host.csproj"
$props = Get-Content (Join-Path $root "Directory.Build.props") -Raw
$version = if ($props -match "<Version>(.*?)</Version>") { $Matches[1] } else { "0.0.0" }

dotnet build $project -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$bin = (dotnet msbuild $project -getProperty:OutDir -p:Configuration=Release -nologo).Trim()
if (-not [System.IO.Path]::IsPathRooted($bin)) { $bin = Join-Path (Split-Path $project) $bin }

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("dustplanner-pack-" + [guid]::NewGuid().ToString("N"))
$pluginDir = Join-Path $stage "DustPlanner"
New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
foreach ($f in @("DustPlanner.HdtExporter.dll", "DustPlanner.HdtExporter.Logic.dll", "DustPlanner.Contracts.dll")) {
    Copy-Item (Join-Path $bin $f) (Join-Path $pluginDir $f)
}

if (-not $NoService) {
    $publishDir = Join-Path $stage "service-publish"
    dotnet publish $hostProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:GenerateDocumentationFile=false -o $publishDir -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "service publish failed" }
    $serviceDir = Join-Path $pluginDir "service"
    New-Item -ItemType Directory -Path $serviceDir -Force | Out-Null
    Copy-Item (Join-Path $publishDir "dustplanner-host.exe") (Join-Path $serviceDir "dustplanner-host.exe")
}

@"
DustPlanner v$version - Hearthstone Deck Tracker plugin
======================================================

1. Close Hearthstone Deck Tracker.
2. Copy this whole "DustPlanner" folder into %AppData%\HearthstoneDeckTracker\Plugins\
3. Start HDT, open Options > Tracker > Plugins and enable "DustPlanner".
4. The plugin exports your collection whenever you leave the game's Collection screen and starts the
   DustPlanner service (service\dustplanner-host.exe, stopped with HDT). The "DustPlanner" entry in the
   plugin menu opens the panel - what you are missing for a deck, what to disenchant, history, games -
   or asks about the deck selected in HDT.

Data: %LocalAppData%\DustPlanner\ (inbox\<account>\ exports, dustplanner.db, prefs.json, meta.txt).
Read-only plugin: no input automation, no memory reading of its own. Not affiliated with Blizzard Entertainment.
"@ | Set-Content -Path (Join-Path $pluginDir "INSTALL.txt") -Encoding UTF8

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$zip = Join-Path $OutputDir "DustPlanner-v$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $pluginDir -DestinationPath $zip
(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() + "  " + (Split-Path $zip -Leaf) | Set-Content -Path "$zip.sha256"
Write-Host "Packaged $zip"

if ($Install) {
    $target = Join-Path $env:APPDATA "HearthstoneDeckTracker\Plugins\DustPlanner"
    if (Get-Process -Name "HearthstoneDeckTracker", "Hearthstone Deck Tracker" -ErrorAction SilentlyContinue) { throw "Close Hearthstone Deck Tracker before installing." }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item (Join-Path $pluginDir "*") $target -Recurse -Force
    Write-Host "Installed to $target - start HDT and enable the plugin under Options > Tracker > Plugins."
}
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
