#Requires -Version 5.1
# UI automation for the right-click menu. ASCII-only on purpose.
# It moves the mouse and overwrites the clipboard; both are restored in finally.
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class UiNative {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    public struct POINT { public int X; public int Y; }
}
'@

$root = [System.Windows.Automation.AutomationElement]::RootElement
$exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'publish\TaskMgrLite.exe'
if (-not (Test-Path $exe)) { throw "not found: $exe (run scripts\build.ps1 first)" }

$oldClipboard = Get-Clipboard -Raw -ErrorAction SilentlyContinue
$oldCursor = New-Object 'UiNative+POINT'
[UiNative]::GetCursorPos([ref]$oldCursor) | Out-Null

$app = $null
$ping = $null
$script:appProcessId = 0
$script:appHwnd = [IntPtr]::Zero
$script:wsh = New-Object -ComObject WScript.Shell

function Find-ById($window, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Wait-Window([int]$processId) {
    for ($i = 0; $i -lt 100; $i++) {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
        $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if ($window) { return $window }
        Start-Sleep -Milliseconds 100
    }
    throw "window not found for pid $processId"
}

function Set-Search($window, [string]$text) {
    $search = Find-ById $window 'SearchBox'
    if (-not $search) { throw 'SearchBox not found' }
    $pattern = [System.Windows.Automation.ValuePattern]$search.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($text)
    Start-Sleep -Milliseconds 700
}

function Get-FirstRow($window) {
    $list = Find-ById $window 'ProcessList'
    if (-not $list) { throw 'ProcessList not found' }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::DataItem)
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    if ($items.Count -eq 0) { throw 'no data row' }
    return $items[0]
}

$menuCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::MenuItem)

$script:lastClickX = 0
$script:lastClickY = 0

function Right-Click([double]$x, [double]$y) {
    # Bring the app forward; the test runner and other desktop windows can
    # otherwise steal the foreground and the synthetic click goes elsewhere.
    try { $script:wsh.AppActivate($script:appProcessId) | Out-Null } catch { }
    [UiNative]::SetForegroundWindow($script:appHwnd) | Out-Null
    Start-Sleep -Milliseconds 150

    $script:lastClickX = $x
    $script:lastClickY = $y
    [UiNative]::SetCursorPos([int]$x, [int]$y) | Out-Null
    Start-Sleep -Milliseconds 100
    [UiNative]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
    [UiNative]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}

function Right-Click-Element($element) {
    try { $element.SetFocus() } catch { }
    $rect = $element.Current.BoundingRectangle
    Right-Click ($rect.Left + $rect.Width / 2) ($rect.Top + $rect.Height / 2)
}

function Get-VisibleItems($parent) {
    $found = $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, $menuCondition)
    $items = @()
    for ($i = 0; $i -lt $found.Count; $i++) {
        $info = $found[$i].Current
        if (-not $info.IsOffscreen -and $info.IsEnabled) {
            $items += $found[$i]
        }
    }
    return $items
}

function Get-ActiveMenu {
    $all = @(Get-VisibleItems $root)
    if ($all.Count -eq 0) { return @() }

    # Pick the visible menu item nearest to the click. Stale menus elsewhere on
    # the desktop can still be "visible" to UIA; proximity disambiguates them.
    $nearest = $null
    $best = [double]::MaxValue
    foreach ($item in $all) {
        $rect = $item.Current.BoundingRectangle
        if ($rect.IsEmpty) { continue }
        $dx = ($rect.Left + $rect.Width / 2) - $script:lastClickX
        $dy = ($rect.Top + $rect.Height / 2) - $script:lastClickY
        $distance = $dx * $dx + $dy * $dy
        if ($distance -lt $best) {
            $best = $distance
            $nearest = $item
        }
    }

    if (-not $nearest) { return @() }

    # The active popup is the vertical stack sharing the nearest item's left
    # edge. This excludes the shell's "System" menu items and stale popups.
    $anchor = $nearest.Current.BoundingRectangle
    $seen = @{}
    $active = @()

    foreach ($item in $all) {
        $rect = $item.Current.BoundingRectangle
        if ($rect.IsEmpty) { continue }
        if ([Math]::Abs($rect.Left - $anchor.Left) -gt 40) { continue }
        if ($rect.Top -lt $anchor.Top - 400 -or $rect.Top -gt $anchor.Top + 400) { continue }

        $runtimeId = [string]::Join(',', $item.GetRuntimeId())
        if ($seen.ContainsKey($runtimeId)) { continue }
        $seen[$runtimeId] = $true
        $active += $item
    }

    return @($active | Sort-Object { $_.Current.BoundingRectangle.Top })
}

function Wait-Menu([int]$minimum) {
    for ($i = 0; $i -lt 30; $i++) {
        $items = @(Get-ActiveMenu)
        if ($items.Count -ge $minimum) { return $items }
        Start-Sleep -Milliseconds 100
    }

    $debug = @("click=$script:lastClickX,$script:lastClickY", 'visible menu items:')
    foreach ($item in @(Get-VisibleItems $root)) {
        $info = $item.Current
        $debug += "$($info.Name)|off=$($info.IsOffscreen)|rect=$($info.BoundingRectangle)"
    }
    $debug | Set-Content (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\menu-debug.txt') -Encoding UTF8
    throw "context menu did not appear (expected at least $minimum items)"
}

function Invoke-MenuItem($items, [int]$index) {
    if ($items.Count -le $index) { throw "menu item index $index missing" }
    $pattern = [System.Windows.Automation.InvokePattern]$items[$index].GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Start-Sleep -Milliseconds 300
}

function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
}

try {
    $app = Start-Process -FilePath $exe -ArgumentList '--real' -PassThru
    $window = Wait-Window $app.Id
    $script:appProcessId = $app.Id
    $script:appHwnd = [IntPtr]$window.Current.NativeWindowHandle
    try { $script:wsh.AppActivate($app.Id) | Out-Null } catch { }
    [UiNative]::SetForegroundWindow($script:appHwnd) | Out-Null
    Start-Sleep -Seconds 2

    $appPath = (Get-Process -Id $app.Id).Path

    # Row menu: copy PID.
    Set-Search $window ([string]$app.Id)
    $row = Get-FirstRow $window
    Right-Click-Element $row
    $menu = @(Wait-Menu 5)
    Assert ($menu.Count -ge 5) 'row menu must have 5 items'
    Invoke-MenuItem $menu 1
    $clip = Get-Clipboard -Raw
    Assert ($clip -eq ([string]$app.Id)) "copy PID mismatch: [$clip]"
    Write-Host '[ui] PASS copy PID'

    # Row menu: copy path.
    $row = Get-FirstRow $window
    Right-Click-Element $row
    $menu = @(Wait-Menu 5)
    Invoke-MenuItem $menu 2
    $clip = Get-Clipboard -Raw
    Assert ($clip -eq $appPath) "copy path mismatch: [$clip]"
    Write-Host '[ui] PASS copy path'

    # Row menu: kill a controlled ping.
    $ping = Start-Process -FilePath 'ping.exe' -ArgumentList '127.0.0.1', '-n', '60' -PassThru
    Start-Sleep -Milliseconds 600
    Set-Search $window ([string]$ping.Id)
    $row = Get-FirstRow $window
    Right-Click-Element $row
    $menu = @(Wait-Menu 5)
    Invoke-MenuItem $menu 3
    Assert ($ping.WaitForExit(3000)) 'controlled ping did not exit after kill'
    Write-Host '[ui] PASS kill process from menu'

    # Blank menu: refresh when there are no rows.
    Set-Search $window '999999'
    $list = Find-ById $window 'ProcessList'
    $rect = $list.Current.BoundingRectangle
    Right-Click ($rect.Left + $rect.Width / 2) ($rect.Top + $rect.Height / 2)
    $menu = @(Wait-Menu 1)
    if ($menu.Count -ne 1) {
        $debug = @('menu items:')
        foreach ($item in $menu) {
            $info = $item.Current
            $debug += "$($info.Name)|off=$($info.IsOffscreen)|rect=$($info.BoundingRectangle)"
        }
        $debug += 'top windows:'
        $wins = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)
        for ($i = 0; $i -lt $wins.Count; $i++) {
            $info = $wins[$i].Current
            $debug += "$($info.ClassName)|$($info.Name)|off=$($info.IsOffscreen)|rect=$($info.BoundingRectangle)"
        }
        $debug | Set-Content (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\menu-debug.txt') -Encoding UTF8
    }
    Assert ($menu.Count -eq 1) "blank menu should have 1 item, got $($menu.Count)"
    Invoke-MenuItem $menu 0
    Write-Host '[ui] PASS blank refresh menu'

    Set-Search $window ''
    Write-Host '[ui] ALL PASS'
}
catch {
    Write-Host "[ui] FAIL: $($_.Exception.Message)"
    exit 1
}
finally {
    if ($ping -and -not $ping.HasExited) {
        try { Stop-Process -Id $ping.Id -Force } catch { }
    }

    if ($app -and -not $app.HasExited) {
        try { Stop-Process -Id $app.Id -Force } catch { }
    }

    if ($null -ne $oldClipboard) {
        try { Set-Clipboard -Value $oldClipboard } catch { }
    }

    try { [UiNative]::SetCursorPos($oldCursor.X, $oldCursor.Y) | Out-Null } catch { }
    try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($script:wsh) } catch { }
}

exit 0
