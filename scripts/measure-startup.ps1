#Requires -Version 5.1
# Cold-start measurement: process creation (kernel StartTime) -> first CompositionTarget.Rendering.
# Each run passes --probe-exit; the app logs "first-frame-ms=" (first frame AFTER the list is
# populated, not the empty window shell) and exits 3s later.
# ASCII-only on purpose (Windows PowerShell 5.1 reads BOM-less files as the system ANSI codepage).
param(
    [int]$Iterations = 10,
    [switch]$Fake,      # 默认用真实数据（500+ 行），加 -Fake 测 400 行假数据基线
    [string]$Exe
)

$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'publish\TaskMgrLite.exe' }
if (-not (Test-Path $Exe)) { throw "not found: $Exe - run scripts\build.ps1 first" }

$log = Join-Path $env:TEMP 'TaskMgrLite\run.log'
$results = New-Object System.Collections.Generic.List[double]

# 把每轮结果和最后一轮的完整日志留在项目目录里，方便事后复查
$outDir = Split-Path -Parent $PSScriptRoot
$csvPath = Join-Path $outDir 'startup-results.csv'
"run,ms" | Set-Content -Path $csvPath -Encoding ASCII

$runArgs = @('--probe-exit')
if ($Fake) { $runArgs += '--fake' }

for ($i = 1; $i -le $Iterations; $i++) {
    if (Test-Path $log) { Remove-Item $log -Force }

    $p = Start-Process -FilePath $Exe -ArgumentList $runArgs -PassThru
    if (-not $p.WaitForExit(20000)) {
        $p.Kill()
        Write-Warning "run ${i}: timed out"
        continue
    }

    $line = Select-String -Path $log -Pattern 'first-frame-ms=([0-9.]+)' -ErrorAction SilentlyContinue |
            Select-Object -First 1

    if ($line) {
        $ms = [double]$line.Matches[0].Groups[1].Value
        $results.Add($ms)
        Add-Content -Path $csvPath -Value "$i,$ms" -Encoding ASCII
        Write-Host ("run {0,2}: {1,7:F0} ms" -f $i, $ms)
    }
    else {
        Write-Warning "run ${i}: no first-frame-ms in log"
    }
}

if ($results.Count -eq 0) { throw 'no valid samples' }

$sorted = @($results | Sort-Object)
$n = $sorted.Count
$mid = [int]($n / 2)

if ($n % 2 -eq 1) { $median = $sorted[$mid] }
else { $median = ($sorted[$mid - 1] + $sorted[$mid]) / 2 }

Copy-Item $log (Join-Path $outDir 'last-startup.log') -Force
Add-Content -Path $csvPath -Value "median,$median" -Encoding ASCII

Write-Host ''
Write-Host ("median = {0:F0} ms   min = {1:F0}   max = {2:F0}   n = {3}" -f $median, $sorted[0], $sorted[$n - 1], $n) -ForegroundColor Green
Write-Host "results: $csvPath"
Write-Host 'Note: the very first run is cold (files not cached); later runs are warm. Report them separately.' -ForegroundColor DarkGray
