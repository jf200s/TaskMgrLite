#Requires -Version 5.1
# Enforce file-size limits. New files must stay under the hard limit; existing
# oversized files live in tools/file-size-baseline.txt and may only shrink.
# ASCII-only on purpose.
$ErrorActionPreference = 'Stop'

$root = (Split-Path -Parent $PSScriptRoot).TrimEnd('\')
$baselinePath = Join-Path $root 'tools\file-size-baseline.txt'

function Get-RelativePath([string]$fullName) {
    return $fullName.Substring($root.Length + 1).Replace('\', '/')
}

function Get-HardLimit([string]$relativePath) {
    if ($relativePath -match '^src/.*\.cs$') {
        $fileName = $relativePath.Substring($relativePath.LastIndexOf('/') + 1)
        if ($fileName -like 'SelfTest*.cs') { return 800 }
        return 600
    }
    if ($relativePath -match '^src/.*\.xaml$') { return 500 }
    if ($relativePath -match '^scripts/.*\.ps1$') { return 600 }
    if ($relativePath -match '^tools/.*\.cs$') { return 600 }
    if ($relativePath -match '^(README|CHANGELOG|BACKLOG)\.md$') { return 600 }
    if ($relativePath -match '^docs/.*\.md$') {
        if ($relativePath -match '^docs/design/archive/') { return 0 }
        return 600
    }
    return 0
}

$baseline = @{}
if (Test-Path $baselinePath) {
    Get-Content $baselinePath | ForEach-Object {
        if ($_ -match '^\s*([^#][^=]*?)=(\d+)\s*$') {
            $baseline[$matches[1].Trim()] = [int]$matches[2]
        }
    }
}
$seenBaseline = @{}

$files = New-Object System.Collections.Generic.List[System.IO.FileInfo]
Get-ChildItem (Join-Path $root 'src\TaskMgrLite') -Recurse -File | Where-Object {
    $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Extension -in '.cs', '.xaml'
} | ForEach-Object { $files.Add($_) }
Get-ChildItem (Join-Path $root 'scripts') -Recurse -File -Filter '*.ps1' |
    ForEach-Object { $files.Add($_) }
Get-ChildItem (Join-Path $root 'tools') -Recurse -File | Where-Object {
    $_.FullName -notmatch '\\(bin|obj|out)\\' -and $_.Extension -eq '.cs'
} | ForEach-Object { $files.Add($_) }
Get-ChildItem (Join-Path $root 'docs') -Recurse -File -Filter '*.md' |
    ForEach-Object { $files.Add($_) }
Get-ChildItem $root -File -Filter '*.md' | ForEach-Object { $files.Add($_) }

$checked = 0
$failed = 0
$warnings = 0

foreach ($file in ($files | Sort-Object FullName -Unique)) {
    $relative = Get-RelativePath $file.FullName
    $limit = Get-HardLimit $relative
    if ($limit -le 0) { continue }

    $checked++
    $lines = [IO.File]::ReadAllLines($file.FullName).Count

    if ($baseline.ContainsKey($relative)) {
        $seenBaseline[$relative] = $true
        $allowed = [int]$baseline[$relative]

        if ($lines -gt $allowed) {
            Write-Host ("FAIL {0}: {1} lines > baseline {2} (hard limit {3})" -f $relative, $lines, $allowed, $limit) -ForegroundColor Red
            $failed++
        }
        elseif ($lines -lt $allowed) {
            Write-Host ("NOTE {0}: {1} lines, below baseline {2}; lower/remove baseline" -f $relative, $lines, $allowed)
            $warnings++
        }

        if ($lines -le $limit) {
            Write-Host ("NOTE {0}: now under hard limit {1}; remove baseline entry" -f $relative, $limit)
            $warnings++
        }
    }
    elseif ($lines -gt $limit) {
        Write-Host ("FAIL {0}: {1} lines > hard limit {2}" -f $relative, $lines, $limit) -ForegroundColor Red
        $failed++
    }
}

foreach ($key in $baseline.Keys) {
    if (-not $seenBaseline.ContainsKey($key)) {
        Write-Host ("WARN baseline entry not found in scan: {0}" -f $key) -ForegroundColor Yellow
        $warnings++
    }
}

Write-Host ("file size: checked={0} failures={1} warnings={2}" -f $checked, $failed, $warnings)
if ($failed -gt 0) { exit 1 }
exit 0
