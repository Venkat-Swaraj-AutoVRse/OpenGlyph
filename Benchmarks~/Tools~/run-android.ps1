<#
.SYNOPSIS
  Install, run, and collect results from the OpenGlyph Android benchmark APK on a
  connected device (Quest / Android phone). The agent cannot run this (no device
  in the build host) — it is left ready for you.

.PREREQUISITES
  - adb on PATH (Android platform-tools), device in developer mode + USB debugging.
  - APK already built: Benchmarks~/Build/Android/OpenGlyphBench.apk
    (build it with: pwsh Tools~/run-benchmarks.ps1 -Do android
     or directly: Unity -batchmode -nographics -projectPath Benchmarks~ \
       -executeMethod OpenGlyph.Benchmarks.Editor.BenchBuild.BuildAndroid -quit -logFile build.log)

.PARAMETER Apk      Path to the APK. Default: Build/Android/OpenGlyphBench.apk
.PARAMETER Package  App id. Default: com.openglyph.bench
.PARAMETER WaitSec  Seconds to wait for the benchmark to finish. Default: 180
#>
param(
  [string]$Apk = "",
  [string]$Package = "com.openglyph.bench",
  [int]$WaitSec = 180
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Proj = (Resolve-Path (Join-Path $ScriptDir "..")).Path
if ([string]::IsNullOrEmpty($Apk)) { $Apk = Join-Path $Proj "Build\Android\OpenGlyphBench.apk" }
$Results = Join-Path $Proj "Results"
New-Item -ItemType Directory -Force -Path $Results | Out-Null

if (-not (Get-Command adb -ErrorAction SilentlyContinue)) { throw "adb not found on PATH. Install Android platform-tools." }
if (-not (Test-Path $Apk)) { throw "APK not found: $Apk  (build it first — see header)." }

Write-Host "Devices:"; adb devices
Write-Host "`nInstalling $Apk ..."
adb install -r -g "$Apk"

Write-Host "Launching $Package ..."
adb shell monkey -p $Package -c android.intent.category.LAUNCHER 1 | Out-Null

Write-Host "Waiting up to $WaitSec s for the benchmark to finish..."
# The runner writes to Application.persistentDataPath =
#   /sdcard/Android/data/<pkg>/files/openglyph_benchmark_results.json
$remote = "/sdcard/Android/data/$Package/files/openglyph_benchmark_results.json"
$deadline = (Get-Date).AddSeconds($WaitSec)
$found = $false
while ((Get-Date) -lt $deadline) {
  $exists = (adb shell "[ -f $remote ] && echo yes || echo no").Trim()
  if ($exists -eq "yes") { $found = $true; break }
  Start-Sleep 5
}

if (-not $found) {
  Write-Warning "Results file not found on device after $WaitSec s."
  Write-Warning "Check logcat:  adb logcat -s Unity:* | Select-String Bench"
  exit 1
}

$local = Join-Path $Results "android_il2cpp_results.json"
Write-Host "Pulling results -> $local"
adb pull "$remote" "$local"
Write-Host "`nDone. Android results: $local"
Write-Host "Tip: for the device log, run:  adb logcat -d -s Unity:* > $Results\android_logcat.txt"
