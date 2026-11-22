#Requires -Version 5.1
# One command per iteration: build -> selftest -> one real startup sample.
# Exits non-zero as soon as a step fails, so the next thing to fix is obvious.
# ASCII-only on purpose.
param(
    [switch]$NoBuild,
    [switch]$Ui,
    [int]$StartupRuns = 1
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $root 'publish\TaskMgrLite.exe'

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not (Test-Path $exe)) { throw "not found: $exe" }

Write-Host "`n== selftest ==" -ForegroundColor Cyan
$p = Start-Process -FilePath $exe -ArgumentList '--selftest' -Wait -PassThru
$code = $p.ExitCode
Write-Host "selftest exit=$code"
if ($code -ne 0) { exit $code }

Write-Host "`n== diag-round3 ==" -ForegroundColor Cyan
$p = Start-Process -FilePath $exe -ArgumentList '--diag-round3' -Wait -PassThru
$code = $p.ExitCode
Write-Host "diag exit=$code"
if ($code -ne 0) { exit $code }

Write-Host "`n== diag-round4 ==" -ForegroundColor Cyan
$p = Start-Process -FilePath $exe -ArgumentList '--diag-round4' -Wait -PassThru
$code = $p.ExitCode
Write-Host "diag exit=$code"
if ($code -ne 0) { exit $code }

if ($Ui) {
    Write-Host "`n== ui tree/flat selection ==" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'test-tree-flat-selection.ps1') -Exe $exe
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host "`n== ui context menu ==" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'test-context-menu.ps1') -Exe $exe
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($StartupRuns -gt 0) {
    Write-Host "`n== startup x$StartupRuns ==" -ForegroundColor Cyan
    $log = Join-Path $env:TEMP 'TaskMgrLite\run.log'

    for ($i = 1; $i -le $StartupRuns; $i++) {
        Remove-Item $log -ErrorAction SilentlyContinue
        Start-Process -FilePath $exe -ArgumentList '--probe-exit','--real' -Wait | Out-Null

        $m = Select-String $log -Pattern 'first-frame-ms=([0-9.]+)' -ErrorAction SilentlyContinue |
             Select-Object -First 1

        if ($m) {
            Write-Host ("startup {0}: {1:F0} ms" -f $i, [double]$m.Matches[0].Groups[1].Value)
        }
        else {
            Write-Warning "startup ${i}: no result"
        }
    }
}

Write-Host "`n== OK ==" -ForegroundColor Green
