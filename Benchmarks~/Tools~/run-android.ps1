<#
.SYNOPSIS
  Install, run, and collect results from the OpenGlyph Android benchmark APK on a
  connected device (Quest 3S / Android phone) via adb.

.USAGE
  powershell -ExecutionPolicy Bypass -File Benchmarks~\Tools~\run-android.ps1
  (Windows PowerShell 5.1 compatible: ASCII only, no bash-style '&&' in adb shell.)

.PREREQUISITES
  - adb on PATH; device in developer mode + USB debugging; 'adb devices' shows it.
  - APK built: Benchmarks~\Build\Android\OpenGlyphBench.apk (or on D: scratch).
    Build: Unity -batchmode -nographics -projectPath Benchmarks~ ^
      -executeMethod OpenGlyph.Benchmarks.Editor.BenchBuild.BuildAndroidCI -logFile build.log

.PARAMETER Apk      Path to the APK. Default: D: scratch, else in-project Build dir.
.PARAMETER Package  App id. Default: com.openglyph.bench
.PARAMETER WaitSec  Seconds to wait for the benchmark to finish. Default: 240
#>
param(
  [string]$Apk = "",
  [string]$Package = "com.openglyph.bench",
  [int]$WaitSec = 240
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Proj = (Resolve-Path (Join-Path $ScriptDir "..")).Path
if ([string]::IsNullOrEmpty($Apk)) {
  $dApk = "D:\OpenGlyphWork\scratch\benchmarks\build\Android\OpenGlyphBench.apk"
  if (Test-Path $dApk) { $Apk = $dApk } else { $Apk = (Join-Path $Proj "Build\Android\OpenGlyphBench.apk") }
}
$Results = Join-Path $Proj "Results"
New-Item -ItemType Directory -Force -Path $Results | Out-Null

if (-not (Get-Command adb -ErrorAction SilentlyContinue)) { throw "adb not found on PATH. Install Android platform-tools." }
if (-not (Test-Path $Apk)) { throw "APK not found: $Apk  (build it first - see header)." }

Write-Host "Devices:"
adb devices
Write-Host ""
Write-Host "Installing $Apk ..."
adb install -r -g "$Apk"

Write-Host "Launching $Package ..."
adb shell monkey -p $Package -c android.intent.category.LAUNCHER 1 | Out-Null

$remote = "/sdcard/Android/data/$Package/files/openglyph_benchmark_results.json"
Write-Host "Waiting up to $WaitSec s for results at $remote ..."
$deadline = (Get-Date).AddSeconds($WaitSec)
$found = $false
while ((Get-Date) -lt $deadline) {
  # PS5.1-safe existence check: 'adb shell ls <path>' prints the path if present,
  # or 'No such file' if not. No bash '&&'/'||' in the remote string.
  $out = (adb shell ls $remote) 2>$null
  if ($out -and ($out -notmatch "No such file")) { $found = $true; break }
  Start-Sleep -Seconds 5
}

if (-not $found) {
  Write-Warning "Results file not found on device after $WaitSec s."
  Write-Warning "Check logs:  adb logcat -d -s Unity:* > $Results\android_logcat.txt"
  exit 1
}

$local = Join-Path $Results "android_il2cpp_results.json"
Write-Host "Pulling results -> $local"
adb pull "$remote" "$local"
Write-Host ""
Write-Host "Done. Android results: $local"
Write-Host "Device log:  adb logcat -d -s Unity:* > $Results\android_logcat.txt"
