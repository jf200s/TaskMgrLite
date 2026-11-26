#Requires -Version 5.1
# Clean, self-contained ReadyToRun release package.
# ASCII-only on purpose.
param(
    [string]$Version = (Get-Date -Format 'yyyyMMdd'),
    [switch]$SkipTests,
    [switch]$Ui
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

    foreach ($diag in @('--diag-round3', '--diag-round4', '--diag-perf')) {
        Write-Host "`n== $diag ==" -ForegroundColor Cyan
        $p = Start-Process -FilePath $exe -ArgumentList $diag -Wait -PassThru
        Write-Host "$diag exit=$($p.ExitCode)"
        if ($p.ExitCode -ne 0) { throw "$diag failed" }
    }
}

if ($Ui) {
    if ($SkipTests) { throw '-Ui cannot be combined with -SkipTests' }

    Write-Host "`n== ui: tree/flat selection ==" -ForegroundColor Cyan
    $selectionTest = Join-Path $PSScriptRoot 'test-tree-flat-selection.ps1'
    if (-not (Test-Path $selectionTest)) { throw "missing UI test: $selectionTest" }
    & $selectionTest -Exe $exe
    if ($LASTEXITCODE -ne 0) { throw 'tree/flat selection UI test failed' }

    Write-Host "`n== ui: context menu ==" -ForegroundColor Cyan
    $menuTest = Join-Path $PSScriptRoot 'test-context-menu.ps1'
    if (-not (Test-Path $menuTest)) { throw "missing UI test: $menuTest" }
    & $menuTest -Exe $exe
    if ($LASTEXITCODE -ne 0) { throw 'context menu UI test failed' }
}

Write-Host "`n== strip build artifacts ==" -ForegroundColor Cyan
# --selftest may create logs next to the exe; package must not ship them.
Get-ChildItem -Path $stage -Recurse -File -Force -Include *.pdb,*.xml,*.log,*.iobj,*.ipdb,*.lib,*.exp |
    Remove-Item -Force
$logs = Join-Path $stage 'logs'
if (Test-Path $logs) { Remove-Item $logs -Recurse -Force }

"TaskMgrLite {0}" -f $Version |
    Set-Content -Path (Join-Path $stage 'VERSION.txt') -Encoding ASCII

Write-Host "`n== add docs ==" -ForegroundColor Cyan
Copy-Item $readme (Join-Path $stage 'README.md') -Force
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE') -Force
$docs = Join-Path $root 'docs'
if (Test-Path $docs) {
    $publicDocs = Join-Path $stage 'docs'
    New-Item -ItemType Directory -Force -Path $publicDocs | Out-Null
    Copy-Item (Join-Path $docs '*.png') $publicDocs -Force
}

# 自包含包内含 .NET 运行时，分发时须附带它的许可与第三方声明。
Write-Host "`n== add dotnet notices ==" -ForegroundColor Cyan
$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    $src = Join-Path $dotnetRoot $name
    if (-not (Test-Path $src)) { throw "missing runtime notice: $src" }
    Copy-Item $src (Join-Path $stage $name) -Force
    Write-Host "  $name"
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
Write-Host "version: $Version"
Write-Host "folder : $stage"
Write-Host "exe    : $exe"
Write-Host "zip    : $zip"
Write-Host ("zip MB : {0:N1}" -f ((Get-Item $zip).Length / 1MB))
Write-Host ("files  : {0} ({1:N1} MB unpacked)" -f $files.Count, $sizeMb)
Write-Host "sha256 : $($hash.Hash)"
Write-Host "pdb/xml/log files in package:"
Get-ChildItem $stage -Recurse -File -Force -Include *.pdb,*.xml,*.log |
    Select-Object -ExpandProperty FullName
