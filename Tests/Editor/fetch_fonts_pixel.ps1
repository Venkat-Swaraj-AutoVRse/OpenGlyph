# fetch_fonts_pixel.ps1 — download the pinned OFL pixel font for Phase 1c pixel-font tests.
#
#   pwsh Tests/Editor/fetch_fonts_pixel.ps1
#
# Silkscreen (OFL) is a bitmap-style pixel font whose outlines lie on an 8-units-per-em grid
# (unitsPerEm=1000, cell=125). It is small (~32 KB) and OFL permits redistribution, so it is
# committed alongside its OFL.txt; this script exists to re-fetch/verify it. Fetched from the
# google/fonts repository.
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$fontsDir = Join-Path $dir 'Fonts'
New-Item -ItemType Directory -Force -Path $fontsDir | Out-Null

# google/fonts serves the OFL Silkscreen build under ofl/silkscreen.
$base = 'https://raw.githubusercontent.com/google/fonts/main/ofl/silkscreen'
$items = @(
  @{ name = 'Silkscreen-Regular.ttf'; url = "$base/Silkscreen-Regular.ttf" },
  @{ name = 'OFL.txt';                url = "$base/OFL.txt" }
)

foreach ($it in $items) {
  $dest = Join-Path $fontsDir $it.name
  if (Test-Path $dest) { Write-Host "have $($it.name)"; continue }
  Write-Host "fetch $($it.name) ..."
  try { Invoke-WebRequest -Uri $it.url -OutFile $dest -UseBasicParsing }
  catch { Write-Warning "failed $($it.name): $($_.Exception.Message)" }
}
Write-Host "done -> $fontsDir"
