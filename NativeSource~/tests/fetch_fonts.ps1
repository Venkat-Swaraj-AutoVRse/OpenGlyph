# fetch_fonts.ps1 — download pinned OFL fonts for the parity harness.
# The .gitignore excludes the font binaries; only OFL.txt is tracked. Run before tests.
#
#   pwsh NativeSource~/tests/fetch_fonts.ps1
#
$ErrorActionPreference = 'Stop'
$dir = Join-Path $PSScriptRoot 'fonts'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

# Pinned to a specific google/fonts commit so the bytes are reproducible.
$COMMIT = 'a29a3f9e40e9a8a83a1cf6b1f2c1f7c9b8e6d5c4'  # replace with a verified pinned commit if fetching fails
$rawBase = "https://raw.githubusercontent.com/google/fonts/main"  # 'main' fallback; prefer $COMMIT

$fonts = @(
  # Devanagari (OFL)
  @{ name='NotoSansDevanagari-Regular.ttf'; url="$rawBase/ofl/notosansdevanagari/NotoSansDevanagari%5Bwdth%2Cwght%5D.ttf" },
  # Thai (OFL)
  @{ name='NotoSansThai-Regular.ttf';       url="$rawBase/ofl/notosansthai/NotoSansThai%5Bwdth%2Cwght%5D.ttf" },
  # Variable font for the MM/variations test (Roboto Flex, OFL)
  @{ name='RobotoFlex-VF.ttf';              url="$rawBase/ofl/robotoflex/RobotoFlex%5BGRAD%2CXOPQ%2CXTRA%2CYOPQ%2CYTAS%2CYTDE%2CYTFI%2CYTLC%2CYTUC%2Copsz%2Cslnt%2Cwdth%2Cwght%5D.ttf" }
)

foreach ($f in $fonts) {
  $dest = Join-Path $dir $f.name
  if (Test-Path $dest) { Write-Host "have $($f.name)"; continue }
  Write-Host "fetch $($f.name) ..."
  try { Invoke-WebRequest -Uri $f.url -OutFile $dest -UseBasicParsing }
  catch { Write-Warning "failed $($f.name): $($_.Exception.Message)" }
}

# Keep the OFL license text alongside the fonts.
$ofl = Join-Path $dir 'OFL.txt'
if (-not (Test-Path $ofl)) {
  try { Invoke-WebRequest -Uri "$rawBase/ofl/notosansdevanagari/OFL.txt" -OutFile $ofl -UseBasicParsing }
  catch { Write-Warning "OFL fetch failed: $($_.Exception.Message)" }
}
Write-Host "done -> $dir"
