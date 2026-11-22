#Requires -Version 5.1
# Clean, self-contained ReadyToRun release package.
# ASCII-only on purpose.
param(
    [string]$Version = (Get-Date -Format 'yyyyMMdd'),
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$root  = Split-Path -Parent $PSScriptRoot
$proj  = Join-Path $root 'src\TaskMgrLite\TaskMgrLite.csproj'
$dist  = Join-Path $root 'dist'
$stage = Join-Path $dist 'TaskMgrLite-win-x64'
$zip   = Join-Path $dist ("TaskMgrLite-win-x64-{0}.zip" -f ($Version -replace '[^0-9A-Za-z._-]', '-'))
$readme = Join-Path $root 'README.md'

if (-not (Test-Path $readme)) { throw "missing README.md: $readme" }

Write-Host '== clean staging ==' -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Write-Host "`n== publish (win-x64, self-contained, R2R) ==" -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:GenerateDocumentationFile=false `
    -o $stage -v minimal
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$exe = Join-Path $stage 'TaskMgrLite.exe'
if (-not (Test-Path $exe)) { throw "missing exe: $exe" }

if (-not $SkipTests) {
    Write-Host "`n== selftest ==" -ForegroundColor Cyan
    $p = Start-Process -FilePath $exe -ArgumentList '--selftest' -Wait -PassThru
    Write-Host "selftest exit=$($p.ExitCode)"
    if ($p.ExitCode -ne 0) { throw 'selftest failed' }
}

Write-Host "`n== strip build artifacts ==" -ForegroundColor Cyan
# --selftest may create logs next to the exe; package must not ship them.
Get-ChildItem -Path $stage -Recurse -File -Force -Include *.pdb,*.xml,*.log,*.iobj,*.ipdb,*.lib,*.exp |
    Remove-Item -Force
$logs = Join-Path $stage 'logs'
if (Test-Path $logs) { Remove-Item $logs -Recurse -Force }

Write-Host "`n== add docs ==" -ForegroundColor Cyan
Copy-Item $readme (Join-Path $stage 'README.md') -Force
$docs = Join-Path $root 'docs'
if (Test-Path $docs) {
    Copy-Item $docs (Join-Path $stage 'docs') -Recurse -Force
}

Write-Host "`n== zip ==" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal -Force
$hash = Get-FileHash $zip -Algorithm SHA256
$hashFile = "$zip.sha256"
"$($hash.Hash)  $([System.IO.Path]::GetFileName($zip))" |
    Set-Content -Path $hashFile -Encoding ASCII

$files = Get-ChildItem $stage -Recurse -File
$sizeMb = ($files | Measure-Object Length -Sum).Sum / 1MB

Write-Host "`n== done ==" -ForegroundColor Green
Write-Host "folder : $stage"
Write-Host "exe    : $exe"
Write-Host "zip    : $zip"
Write-Host ("zip MB : {0:N1}" -f ((Get-Item $zip).Length / 1MB))
Write-Host ("files  : {0} ({1:N1} MB unpacked)" -f $files.Count, $sizeMb)
Write-Host "sha256 : $($hash.Hash)"
Write-Host "pdb/xml/log files in package:"
Get-ChildItem $stage -Recurse -File -Force -Include *.pdb,*.xml,*.log |
    Select-Object -ExpandProperty FullName
