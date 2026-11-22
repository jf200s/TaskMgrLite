#Requires -Version 5.1
# Round 0 build: normal Release build, then self-contained ReadyToRun publish.
# Script is intentionally ASCII-only so Windows PowerShell 5.1 and pwsh behave identically.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\TaskMgrLite\TaskMgrLite.csproj'
$out  = Join-Path $root 'publish'

Write-Host '== SDK ==' -ForegroundColor Cyan
dotnet --version
dotnet --list-sdks
dotnet --info | Select-String -Pattern 'RID:' | ForEach-Object { $_.Line }

Write-Host "`n== build (Release) ==" -ForegroundColor Cyan
dotnet build $proj -c Release -v minimal
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

Write-Host "`n== publish (win-x64, self-contained, R2R) ==" -ForegroundColor Cyan
# Clean first: dotnet publish does not delete stale files from an old output dir.
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

# No PublishSingleFile: single-file extraction on first run adds 1-2s to cold start.
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:GenerateDocumentationFile=false `
    -o $out -v minimal
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$exe = Join-Path $out 'TaskMgrLite.exe'
$size = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum

Write-Host "`n== done ==" -ForegroundColor Green
Write-Host "exe : $exe"
Write-Host ("size: {0:N1} MB" -f ($size / 1MB))
