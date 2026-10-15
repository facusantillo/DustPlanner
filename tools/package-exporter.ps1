<#
.SYNOPSIS
  Builds the HDT exporter plugin (Release) and packs it as DustPlanner.HdtExporter-v<version>.zip.
.DESCRIPTION
  Requires lib\hdt to be populated (tools\fetch-hdt-refs.ps1 -FromInstall). Output goes to artifacts\release\ by default.
  Pass -Install to also copy the plugin folder into %AppData%\HearthstoneDeckTracker\Plugins\DustPlanner (close HDT first).
#>
[CmdletBinding()]
param(
    [string]$OutputDir = "",
    [switch]$Install
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $root "artifacts\release" }
$project = Join-Path $root "src\DustPlanner.HdtExporter\DustPlanner.HdtExporter.csproj"
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
@"
DustPlanner Exporter v$version — Hearthstone Deck Tracker plugin
================================================================

1. Close Hearthstone Deck Tracker.
2. Copy this whole "DustPlanner" folder into %AppData%\HearthstoneDeckTracker\Plugins\
3. Start HDT, open Options > Tracker > Plugins and enable "DustPlanner Exporter".
4. Use the "DustPlanner" entry in the plugin menu to export now / export diagnostics / open the export folder.

Exports: %LocalAppData%\DustPlanner\inbox\<account>\ (collection.json, decks.json, rank.json).
Read-only plugin: no input automation, no memory reading of its own. Not affiliated with Blizzard Entertainment.
"@ | Set-Content -Path (Join-Path $pluginDir "INSTALL.txt") -Encoding UTF8

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$zip = Join-Path $OutputDir "DustPlanner.HdtExporter-v$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $pluginDir -DestinationPath $zip
(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() + "  " + (Split-Path $zip -Leaf) | Set-Content -Path "$zip.sha256"
Write-Host "Packaged $zip"

if ($Install) {
    $target = Join-Path $env:APPDATA "HearthstoneDeckTracker\Plugins\DustPlanner"
    if (Get-Process -Name "HearthstoneDeckTracker" -ErrorAction SilentlyContinue) { throw "Close Hearthstone Deck Tracker before installing." }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item (Join-Path $pluginDir "*") $target -Force
    Write-Host "Installed to $target — start HDT and enable the plugin under Options > Tracker > Plugins."
}
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
