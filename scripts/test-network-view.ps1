#Requires -Version 5.1
# UIA regression for the network view: switch, virtualization, sort, search, and
# switching back without clearing the process collection. ASCII-only on purpose.
param([string]$Exe = '')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $exe = Join-Path $repo 'publish\TaskMgrLite.exe'
}
else {
    $exe = (Resolve-Path $Exe).Path
}
if (-not (Test-Path $exe)) { throw "not found: $exe (run scripts\build.ps1 first)" }

function Find-Window([int]$processId) {
    for ($i = 0; $i -lt 150; $i++) {
        Start-Sleep -Milliseconds 100
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
        $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
        for ($j = 0; $j -lt $windows.Count; $j++) {
            $window = $windows[$j]
            if ($window.Current.ClassName -ne 'Window') { continue }
            if ($null -eq (Find-ById $window 'ProcessList')) { continue }
            return $window
        }
    }
    throw 'main window not found'
}

function Find-ById($window, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Select-Element($element) {
    $pattern = [System.Windows.Automation.SelectionItemPattern]$element.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
}

function Get-DataItems($list) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::DataItem)
    return $list.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
}

function Get-RowFields($row) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $texts = $row.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $fields = @()
    for ($i = 0; $i -lt $texts.Count; $i++) { $fields += $texts[$i].Current.Name }
    return $fields
}

function Get-Headers($list) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::HeaderItem)
    return $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-Pattern($element) {
    $pattern = [System.Windows.Automation.InvokePattern]$element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Get-CountPair([string]$text) {
    $m = [regex]::Match($text, '(\d+)\s*/\s*(\d+)')
    if (-not $m.Success) { throw "count not found in [$text]" }
    return @([int]$m.Groups[1].Value, [int]$m.Groups[2].Value)
}

$app = $null
$exitCode = 0

try {
    $app = Start-Process -FilePath $exe -PassThru
    $window = Find-Window $app.Id
    Start-Sleep -Milliseconds 1400

    $processBefore = (Find-ById $window 'ProcessCount').Current.Name
    $pairBefore = Get-CountPair $processBefore
    Assert-True ($pairBefore[0] -gt 0 -and $pairBefore[1] -gt 0) "process list empty before switch: $processBefore"

    Select-Element (Find-ById $window 'NavNetwork')
    Start-Sleep -Milliseconds 2200

    $portList = Find-ById $window 'PortList'
    $status = Find-ById $window 'PortStatus'
    Assert-True ($null -ne $portList) 'network view did not open'
    Assert-True ($status.Current.Name -match '2Hz') "2Hz status missing: $($status.Current.Name)"

    $countText = (Find-ById $window 'PortCount').Current.Name
    $total = [int][regex]::Match($countText, '\d+').Value
    Assert-True ($total -gt 0) "no ports loaded: $countText"

    $headers = Get-Headers $portList
    Assert-True ($headers.Count -ge 6) "port headers missing: $($headers.Count)"
    $pidHeader = $headers[4]

    $rows = Get-DataItems $portList
    Assert-True ($rows.Count -gt 0) 'no realized port rows'
    Assert-True ($rows.Count -lt $total) "virtualization not active: realized=$($rows.Count) total=$total"

    Invoke-Pattern $pidHeader
    Start-Sleep -Milliseconds 600
    $rows = Get-DataItems (Find-ById $window 'PortList')
    $lastPid = -1
    for ($i = 0; $i -lt $rows.Count; $i++) {
        $fields = @(Get-RowFields $rows[$i])
        Assert-True ($fields.Count -ge 6) "row fields missing at $i"
        $rowPid = [int]$fields[4]
        Assert-True ($rowPid -ge $lastPid) "PID ascending sort broken at row $i"
        $lastPid = $rowPid
    }

    Invoke-Pattern $pidHeader
    Start-Sleep -Milliseconds 600
    $rows = Get-DataItems (Find-ById $window 'PortList')
    $lastPid = [int]::MaxValue
    for ($i = 0; $i -lt $rows.Count; $i++) {
        $fields = @(Get-RowFields $rows[$i])
        $rowPid = [int]$fields[4]
        Assert-True ($rowPid -le $lastPid) "PID descending sort broken at row $i"
        $lastPid = $rowPid
    }

    $search = Find-ById $window 'PortSearchBox'
    $value = [System.Windows.Automation.ValuePattern]$search.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    $value.SetValue('svchost')
    Start-Sleep -Milliseconds 800

    $filteredCount = (Find-ById $window 'PortCount').Current.Name
    $pairFiltered = Get-CountPair $filteredCount
    Assert-True ($pairFiltered[0] -gt 0 -and $pairFiltered[0] -lt $pairFiltered[1]) "search count invalid: $filteredCount"

    $rows = Get-DataItems (Find-ById $window 'PortList')
    Assert-True ($rows.Count -gt 0) 'search returned no realized rows'
    for ($i = 0; $i -lt $rows.Count; $i++) {
        $fields = @(Get-RowFields $rows[$i])
        Assert-True ($fields[5] -match 'svchost') "search mismatch at row ${i}: $($fields[5])"
    }

    Select-Element (Find-ById $window 'NavProcesses')
    Start-Sleep -Milliseconds 900
    Assert-True ($null -eq (Find-ById $window 'PortList')) 'network view still visible after switching back'

    $processAfter = (Find-ById $window 'ProcessCount').Current.Name
    $pairAfter = Get-CountPair $processAfter
    Assert-True ($pairAfter[0] -gt 0 -and $pairAfter[1] -gt 0) "process collection was cleared: $processAfter"

    Write-Host "PASS network view: ports=$total realized=$($rows.Count) search=$filteredCount"
    Write-Host "PASS process collection: before=[$processBefore] after=[$processAfter]"
}
catch {
    $exitCode = 1
    Write-Host "FAIL network view: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    if ($null -ne $app -and -not $app.HasExited) {
        $app.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 600
        if (-not $app.HasExited) { $app.Kill() }
    }
}

exit $exitCode
