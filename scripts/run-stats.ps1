#Requires -Version 5.1
# Run with per-2s resource sampling: private working set / working set / private commit / Gen2 / handles.
# ASCII-only on purpose (Windows PowerShell 5.1 reads BOM-less files as the system ANSI codepage).
param(
    [switch]$Fake,      # use 400 fake rows instead of real process data
    [string]$Exe
)

$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'publish\TaskMgrLite.exe' }
if (-not (Test-Path $Exe)) { throw "not found: $Exe - run scripts\build.ps1 first" }

$cliArgs = @('--stats')
if ($Fake) { $cliArgs += '--fake' }

Write-Host "log file: $env:TEMP\TaskMgrLite\run.log" -ForegroundColor DarkGray
& $Exe @cliArgs
