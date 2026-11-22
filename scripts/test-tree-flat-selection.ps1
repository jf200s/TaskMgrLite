#Requires -Version 5.1
# UIA regression: collapse a large part of the tree, select a child in an
# expanded branch, then switch to flat and assert the selection survives.
# ASCII-only on purpose.
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$expandedGlyph = [string][char]0x25BE   # down triangle
$collapsedGlyph = [string][char]0x25B8  # right triangle
$flatLabel = [string][char]0x5E73 + [string][char]0x94FA
$treeLabel = [string][char]0x6811 + [string][char]0x5F62

$root = [System.Windows.Automation.AutomationElement]::RootElement
$exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\TaskMgrLite\bin\Release\net11.0-windows\TaskMgrLite.exe'
if (-not (Test-Path $exe)) { throw "not found: $exe (build Release first)" }

function Find-MainWindow([int]$processId) {
    for ($i = 0; $i -lt 120; $i++) {
        Start-Sleep -Milliseconds 100
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
        $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
        for ($j = 0; $j -lt $windows.Count; $j++) {
            if ($windows[$j].Current.ClassName -ne 'Window') { continue }
            $listCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ProcessList')
            if ($null -ne $windows[$j].FindFirst(
                    [System.Windows.Automation.TreeScope]::Descendants, $listCond)) {
                return $windows[$j]
            }
        }
    }
    throw 'main window not found'
}

function Find-List($window) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ProcessList')
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-ById($window, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-Rows($list) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::DataItem)
    return $list.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
}

function Get-GlyphButton($list, $row) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $buttons = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $rr = $row.Current.BoundingRectangle

    for ($i = 0; $i -lt $buttons.Count; $i++) {
        $b = $buttons[$i]
        $name = $b.Current.Name
        if ($name -ne $expandedGlyph -and $name -ne $collapsedGlyph) { continue }

        $br = $b.Current.BoundingRectangle
        if ([Math]::Abs($br.Y - ($rr.Y + 4)) -lt 3 -and
            $br.X -ge $rr.X -and $br.X -lt ($rr.X + 160)) {
            return $b
        }
    }

    return $null
}

function Get-RowPid($row) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $texts = $row.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    for ($i = 0; $i -lt $texts.Count; $i++) {
        if ($texts[$i].Current.Name -match '^[0-9]+$') {
            return [int]$texts[$i].Current.Name
        }
    }
    return -1
}

function Get-DetailPid($window) {
    $detail = Find-ById $window 'DetailPid'
    if ($null -eq $detail) { return -1 }
    $text = $detail.Current.Name
    if ($text -match '^[0-9]+$') { return [int]$text }
    return -1
}

function Find-ModeButton($window) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    for ($i = 0; $i -lt $buttons.Count; $i++) {
        $name = $buttons[$i].Current.Name
        if ($name -eq $flatLabel -or $name -eq $treeLabel) { return $buttons[$i] }
    }
    return $null
}

function Invoke-Element($element) {
    $pattern = [System.Windows.Automation.InvokePattern]$element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Wait-DetailPid($window, [int]$expected) {
    for ($i = 0; $i -lt 30; $i++) {
        if ((Get-DetailPid $window) -eq $expected) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

$app = Start-Process -FilePath $exe -PassThru
try {
    $window = Find-MainWindow $app.Id
    Start-Sleep -Milliseconds 900
    $list = Find-List $window
    $rows = Get-Rows $list

    # Pick the first expanded parent with a visible child.
    $targetTop = -1.0
    $targetPid = -1
    $childPid = -1
    for ($i = 0; $i -lt $rows.Count - 1; $i++) {
        $glyph = Get-GlyphButton $list $rows[$i]
        if ($null -eq $glyph -or $glyph.Current.Name -ne $expandedGlyph) { continue }

        $targetTop = $rows[$i].Current.BoundingRectangle.Y
        $targetPid = Get-RowPid $rows[$i]
        $childPid = Get-RowPid $rows[$i + 1]
        break
    }
    if ($targetPid -lt 0) { throw 'no expanded parent with a child found' }
    Write-Host "target-pid=$targetPid child-pid=$childPid top=$targetTop"

    # Collapse expanded branches after the target so switching to flat has to
    # add a large block of rows (the ReplaceAll/Reset path).
    for ($pass = 0; $pass -lt 12; $pass++) {
        $rows = Get-Rows $list
        $collapsedOne = $false

        for ($i = 0; $i -lt $rows.Count; $i++) {
            if ($rows[$i].Current.BoundingRectangle.Y -le $targetTop + 1) { continue }

            $glyph = Get-GlyphButton $list $rows[$i]
            if ($null -eq $glyph -or $glyph.Current.Name -ne $expandedGlyph) { continue }

            Invoke-Element $glyph
            Start-Sleep -Milliseconds 300
            $collapsedOne = $true
            break
        }

        if (-not $collapsedOne) { break }
    }

    # Re-find the target by top position; its child is the next visible row.
    $rows = Get-Rows $list
    $targetIndex = -1
    for ($i = 0; $i -lt $rows.Count; $i++) {
        if ([Math]::Abs($rows[$i].Current.BoundingRectangle.Y - $targetTop) -lt 2) {
            $targetIndex = $i
            break
        }
    }
    if ($targetIndex -lt 0 -or $targetIndex + 1 -ge $rows.Count) {
        throw 'target row or child disappeared'
    }

    $child = $rows[$targetIndex + 1]
    $childPid = Get-RowPid $child
    if ($childPid -lt 0) { throw 'could not read child pid' }
    if ($null -eq (Get-Process -Id $childPid -ErrorAction SilentlyContinue)) {
        throw "child pid $childPid is not alive"
    }

    $selection = [System.Windows.Automation.SelectionItemPattern]$child.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    Start-Sleep -Milliseconds 300

    if ((Get-DetailPid $window) -ne $childPid) {
        throw "detail pid before switch is not $childPid"
    }

    $mode = Find-ModeButton $window
    if ($null -eq $mode) {
        $buttonCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
        $allButtons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCond)
        for ($i = 0; $i -lt $allButtons.Count; $i++) {
            $b = $allButtons[$i]
            $r = $b.Current.BoundingRectangle
            if ($r.Y -lt 120) {
                $codes = (($b.Current.Name.ToCharArray() | ForEach-Object { [int]$_ }) -join ',')
                Write-Host ("button name=[{0}] codes=[{1}] id=[{2}] rect={3},{4}" -f `
                    $b.Current.Name, $codes, $b.Current.AutomationId, $r.X, $r.Y)
            }
        }
        throw 'mode button not found'
    }
    if ($mode.Current.Name -ne $treeLabel) {
        $codes = (($mode.Current.Name.ToCharArray() | ForEach-Object { [int]$_ }) -join ',')
        throw "expected tree mode before switch, got codes [$codes]"
    }

    Invoke-Element $mode
    if (-not (Wait-DetailPid $window $childPid)) {
        throw "selection lost after switching to flat (expected pid $childPid)"
    }

    $mode = Find-ModeButton $window
    if ($null -eq $mode -or $mode.Current.Name -ne $flatLabel) {
        throw 'did not switch to flat'
    }

    if ($null -eq (Get-Process -Id $childPid -ErrorAction SilentlyContinue)) {
        throw "child pid $childPid exited during test; result is inconclusive"
    }

    Write-Host "TREE-FLAT SELECTION OK (pid=$childPid)"
}
finally {
    if (-not $app.HasExited) {
        $app.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 600
        if (-not $app.HasExited) { $app.Kill() }
    }
}
