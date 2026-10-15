<#
.SYNOPSIS
  Copies the HDT assemblies the exporter compiles against into lib/hdt/.

.DESCRIPTION
  Two sources, pick one:
    -FromInstall   copies from your local HDT installation (%LocalAppData%\HearthstoneDeckTracker\app-<version>),
                   so the plugin is built against the exact version you run. Recommended on your own machine.
    (default)      downloads the GitHub release named by -Version (1.55.6, the latest published there) and
                   verifies its SHA-256. Used by CI.

  Nothing in lib/hdt/ is ever committed.

.EXAMPLE
  tools\fetch-hdt-refs.ps1 -FromInstall
  tools\fetch-hdt-refs.ps1 -Version 1.55.6
#>
[CmdletBinding()]
param(
    [string]$Version = "1.55.6",
    [switch]$FromInstall
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "lib\hdt"
$files = @("Hearthstone Deck Tracker.exe", "HearthMirror.dll", "HearthDb.dll", "Newtonsoft.Json.dll")
$knownSha256 = @{ "1.55.6" = "3ebf12fa3d5e842f1fa8ce433758640e3ce2903a400dac5fb096822149d2bb60" }

New-Item -ItemType Directory -Force -Path $dest | Out-Null

if ($FromInstall) {
    $hdtRoot = Join-Path $env:LOCALAPPDATA "HearthstoneDeckTracker"
    $app = Get-ChildItem -Path $hdtRoot -Directory -Filter "app-*" -ErrorAction Stop |
        Sort-Object { [version]($_.Name -replace '^app-', '') } -Descending |
        Select-Object -First 1
    if (-not $app) { throw "No app-<version> folder found under $hdtRoot" }
    foreach ($f in $files) {
        $src = Join-Path $app.FullName $f
        if ($f -eq "Hearthstone Deck Tracker.exe" -and -not (Test-Path $src)) {
            # 1.59.x installs name the executable without spaces; the csproj HintPath uses the release-zip name.
            $src = Join-Path $app.FullName "HearthstoneDeckTracker.exe"
        }
        Copy-Item -Path $src -Destination (Join-Path $dest $f) -Force
    }
    ($app.Name -replace '^app-', '') | Set-Content -Path (Join-Path $dest "VERSION")
    Write-Host "Copied HDT $($app.Name) references from $($app.FullName) to $dest"
    return
}

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("hdt-refs-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $zip = Join-Path $tmp "hdt.zip"
    $url = "https://github.com/HearthSim/Hearthstone-Deck-Tracker/releases/download/v$Version/Hearthstone.Deck.Tracker-v$Version.zip"
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

    if ($knownSha256.ContainsKey($Version)) {
        $actual = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $knownSha256[$Version]) { throw "SHA-256 mismatch for ${Version}: expected $($knownSha256[$Version]), got $actual" }
    } else {
        Write-Warning "No known checksum for $Version; skipping verification"
    }

    Expand-Archive -Path $zip -DestinationPath (Join-Path $tmp "x") -Force
    $src = Join-Path $tmp "x\Hearthstone Deck Tracker"
    foreach ($f in $files) { Copy-Item -Path (Join-Path $src $f) -Destination (Join-Path $dest $f) -Force }
    $Version | Set-Content -Path (Join-Path $dest "VERSION")
    Write-Host "HDT $Version references copied to $dest"
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
