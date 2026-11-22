#Requires -Version 5.1
# One command to produce everything the reviewer (Claude) can read:
#   artifacts\startup-results.csv   - N cold/warm start samples
#   artifacts\last-startup.log      - stage breakdown of the last start
#   artifacts\stats.log             - S seconds of --stats --real output
# ASCII-only on purpose.
param(
    [int]$StartupRuns = 10,
    [int]$StatsSeconds = 120,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $root 'publish\TaskMgrLite.exe'
$art  = Join-Path $root 'artifacts'
$log  = Join-Path $env:TEMP 'TaskMgrLite\run.log'

New-Item -ItemType Directory -Force -Path $art | Out-Null

if (-not $SkipBuild) {
    Write-Host '== build ==' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'build.ps1')
}

if (-not (Test-Path $exe)) { throw "not found: $exe" }

# ---------- 1) startup samples ----------
Write-Host "`n== startup x$StartupRuns ==" -ForegroundColor Cyan
$csv = Join-Path $art 'startup-results.csv'
"run,ms" | Set-Content -Path $csv -Encoding ASCII

for ($i = 1; $i -le $StartupRuns; $i++) {
    Remove-Item $log -ErrorAction SilentlyContinue
    Start-Process -FilePath $exe -ArgumentList '--probe-exit','--real' -Wait | Out-Null

    $m = Select-String $log -Pattern 'first-frame-ms=([0-9.]+)' -ErrorAction SilentlyContinue |
         Select-Object -First 1
    if ($m) {
        $ms = [double]$m.Matches[0].Groups[1].Value
        Add-Content $csv "$i,$ms" -Encoding ASCII
        Write-Host ("startup {0,2}: {1,7:F0} ms" -f $i, $ms)
    }
    else {
        Write-Warning "startup ${i}: no result"
    }
}

Copy-Item $log (Join-Path $art 'last-startup.log') -Force

# ---------- 2) stats session ----------
Write-Host "`n== stats for $StatsSeconds s ==" -ForegroundColor Cyan
Remove-Item $log -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -ArgumentList '--stats','--real' -PassThru
Start-Sleep -Seconds $StatsSeconds

$null = $p.CloseMainWindow()
Start-Sleep -Seconds 2
if (-not $p.HasExited) { $p.Kill() }

Copy-Item $log (Join-Path $art 'stats.log') -Force

Write-Host "`n== artifacts ==" -ForegroundColor Green
Get-ChildItem $art | ForEach-Object { Write-Host ("  {0}  {1:N0} bytes" -f $_.Name, $_.Length) }
