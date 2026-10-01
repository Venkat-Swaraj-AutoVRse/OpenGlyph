<#
.SYNOPSIS
  Drives the OpenGlyph benchmark Unity project: editor smoke, Windows IL2CPP
  standalone build+run, and (optional) Android IL2CPP APK build.

.NOTES
  Unity 6000.3.19f1 only. Run from anywhere; paths are resolved relative to this
  script. Results JSON lands in the player's persistentDataPath and (standalone)
  next to the exe; this script copies them into Benchmarks~/Results/.

.PARAMETER Unity
  Path to Unity.exe. Default: the 6000.3.19f1 Hub install.
.PARAMETER Do
  Comma list of steps: smoke, windows, android. Default: smoke,windows.
#>
param(
  [string]$Unity = "C:\Program Files\Unity\Hub\Editor\6000.3.19f1\Editor\Unity.exe",
  [string]$Do = "smoke,windows"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Proj = (Resolve-Path (Join-Path $ScriptDir "..")).Path   # Benchmarks~/
$Results = Join-Path $Proj "Results"
$Logs = Join-Path $Proj "Logs"
New-Item -ItemType Directory -Force -Path $Results, $Logs | Out-Null
$steps = $Do.Split(",") | ForEach-Object { $_.Trim().ToLower() }

if (-not (Test-Path $Unity)) { throw "Unity not found at $Unity" }
Write-Host "Unity : $Unity"
Write-Host "Proj  : $Proj"
Write-Host "Steps : $($steps -join ', ')"

function Invoke-Unity([string]$method, [string]$logName, [switch]$NoQuit) {
  $log = Join-Path $Logs $logName
  $args = @("-batchmode","-nographics","-projectPath",$Proj,
            "-executeMethod",$method,"-logFile",$log)
  if (-not $NoQuit) { $args += "-quit" }
  Write-Host "`n== Unity $method ==> $log"
  $p = Start-Process -FilePath $Unity -ArgumentList $args -NoNewWindow -PassThru -Wait
  Write-Host "   exit $($p.ExitCode)"
  return $p.ExitCode
}

# ---- editor smoke (NOT representative) ----
if ($steps -contains "smoke") {
  # Editor play-mode in batchmode. We can't use -quit (it would exit before play
  # mode runs); instead we run with a bounded timeout and detect the results file.
  $log = Join-Path $Logs "editor_smoke.log"
  $persist = Join-Path $env:USERPROFILE "AppData\LocalLow\OpenGlyph\OpenGlyphBench"
  $resJson = Join-Path $persist "openglyph_benchmark_results.json"
  if (Test-Path $resJson) { Remove-Item $resJson -Force }
  $args = @("-batchmode","-projectPath",$Proj,
            "-executeMethod","OpenGlyph.Benchmarks.Editor.BenchBuild.EditorSmoke","-logFile",$log)
  Write-Host "`n== Unity EditorSmoke (bounded) ==> $log"
  $p = Start-Process -FilePath $Unity -ArgumentList $args -NoNewWindow -PassThru
  $deadline = (Get-Date).AddMinutes(12)
  while (-not $p.HasExited -and (Get-Date) -lt $deadline) {
    if (Test-Path $resJson) { Start-Sleep 2; break }
    Start-Sleep 3
  }
  if (-not $p.HasExited) { Start-Sleep 3; if (-not $p.HasExited) { $p.Kill() } }
  if (Test-Path $resJson) {
    Copy-Item $resJson (Join-Path $Results "editor_smoke_results.json") -Force
    Write-Host "   editor smoke results captured."
  } else {
    Write-Warning "   editor smoke results NOT found (see $log)"
  }
}

# ---- Windows IL2CPP standalone build + run ----
if ($steps -contains "windows") {
  $ec = Invoke-Unity "OpenGlyph.Benchmarks.Editor.BenchBuild.BuildWindows" "build_windows.log"
  $exe = Join-Path $Proj "Build\Windows\OpenGlyphBench.exe"
  if ($ec -ne 0 -or -not (Test-Path $exe)) {
    Write-Warning "Windows build failed (exit $ec). See Logs\build_windows.log"
  } else {
    Write-Host "`n== Running Windows player (windowed; rendering needs a window) =="
    # Headless-ish: batchmode where possible; UI rendering may require a window.
    $pr = Start-Process -FilePath $exe -ArgumentList @("-batchmode","-logFile",(Join-Path $Logs "run_windows.log")) -PassThru
    $deadline = (Get-Date).AddMinutes(10)
    while (-not $pr.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep 3 }
    if (-not $pr.HasExited) { $pr.Kill() }
    $exeDirJson = Join-Path $Proj "Build\Windows\openglyph_benchmark_results.json"
    $persistJson = Join-Path $env:USERPROFILE "AppData\LocalLow\OpenGlyph\OpenGlyphBench\openglyph_benchmark_results.json"
    foreach ($src in @($exeDirJson,$persistJson)) {
      if (Test-Path $src) { Copy-Item $src (Join-Path $Results "windows_il2cpp_results.json") -Force; Write-Host "   windows results captured from $src"; break }
    }
  }
}

# ---- Android IL2CPP APK build (cannot run here; see run-android.ps1) ----
if ($steps -contains "android") {
  $ec = Invoke-Unity "OpenGlyph.Benchmarks.Editor.BenchBuild.BuildAndroid" "build_android.log"
  $apk = Join-Path $Proj "Build\Android\OpenGlyphBench.apk"
  if ($ec -ne 0 -or -not (Test-Path $apk)) {
    Write-Warning "Android build failed or incomplete (exit $ec). See Logs\build_android.log"
  } else {
    Write-Host "   APK built: $apk  — run on device with Tools~/run-android.ps1"
  }
}

Write-Host "`nDone. Results in: $Results"
