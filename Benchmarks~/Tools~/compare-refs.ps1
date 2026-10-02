<#
.SYNOPSIS
  Compare two OpenGlyph benchmark result JSONs (main vs phase2) and report per-phase
  % deltas for each system x text set. Flags any phase >5% slower on phase2 as a
  regression finding. Valid rows only (both sides creationValid etc.).

.USAGE
  powershell -ExecutionPolicy Bypass -File compare-refs.ps1 -Main main.json -Phase2 phase2.json
#>
param(
  [Parameter(Mandatory=$true)][string]$Main,
  [Parameter(Mandatory=$true)][string]$Phase2,
  [double]$Threshold = 5.0
)
$ErrorActionPreference = "Stop"
$m = Get-Content $Main   -Raw | ConvertFrom-Json
$p = Get-Content $Phase2 -Raw | ConvertFrom-Json

function Key($r) { "$($r.system)|$($r.textSet)" }
$mi = @{}; foreach ($r in $m.perSystemText) { $mi[(Key $r)] = $r }

$findings = @()
"{0,-26} {1,-7} {2,10} {3,10} {4,8}  {5}" -f "system","set","main(ms)","phase2(ms)","delta%","phase" | Write-Host
foreach ($r in $p.perSystemText) {
  $k = Key $r
  if (-not $mi.ContainsKey($k)) { continue }
  $mm = $mi[$k]
  foreach ($ph in "objectCreation","fullRebuild","layout","meshRebuild") {
    $mv = [double]$mm.$ph.medianMs
    $pv = [double]$r.$ph.medianMs
    if ($mv -le 0) { continue }
    $delta = (($pv - $mv) / $mv) * 100.0
    $flag = ""
    if ($delta -gt $Threshold) { $flag = "  <== REGRESSION >$Threshold%"; $findings += "$($r.system) $($r.textSet) $ph +$([math]::Round($delta,1))%" }
    "{0,-26} {1,-7} {2,10:N1} {3,10:N1} {4,8:N1}  {5}{6}" -f $r.system,$r.textSet,$mv,$pv,$delta,$ph,$flag | Write-Host
  }
}
Write-Host ""
if ($findings.Count -eq 0) { Write-Host "No phase regressed more than $Threshold%." }
else { Write-Host "REGRESSIONS (>$Threshold%):"; $findings | ForEach-Object { Write-Host "  - $_" } }
