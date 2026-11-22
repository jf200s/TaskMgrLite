#Requires -Version 5.1
# Build the local shell MCP server. ASCII-only.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'tools\ShellMcp\ShellMcp.csproj'
$out  = Join-Path $root 'tools\ShellMcp\out'

dotnet publish $proj -c Release -o $out -v minimal
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$exe = Join-Path $out 'ShellMcp.exe'
Write-Host "`nserver exe: $exe"
Write-Host "root arg  : $root"
