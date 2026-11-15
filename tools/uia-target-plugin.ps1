<#
Fills the editor's "Select Target Plugin" save dialog through UI Automation, from outside the editor.

The dialog's file name box is a DirectUI element with no window handle, so the plugin cannot fill it and
keystrokes do not reach it. UI Automation works from this process: the box exposes no value pattern on this
build, so the target plugin is selected in the dialog's list instead, which is what tells the editor which
file to write. Nothing here is typed and nobody has to press anything.
#>
param(
    [string]$File = 'CKGardenNav.esp',
    [string]$Editor = 'CreationKit',
    [string]$Title = 'Select Target Plugin',
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type -Path (Join-Path $PSScriptRoot 'uia-windows.cs')

function Get-EditorId {
    $process = Get-Process -Name $Editor -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $process) { throw "no $Editor process is running" }
    return [uint32]$process.Id
}

function Get-Dialog {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $handles = @([UiaWindows]::Find((Get-EditorId), $Title) | ForEach-Object { [IntPtr]::new($_) })
        if ($handles.Count -gt 0) { return [System.Windows.Automation.AutomationElement]::FromHandle($handles[0]) }
        Start-Sleep -Milliseconds 300
    }

    throw "no window titled '$Title' appeared within $TimeoutSeconds s"
}

function Get-Descendants($element, $type) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
    return $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Select-Target($dialog, $name) {
    $list = Get-Descendants $dialog ([System.Windows.Automation.ControlType]::List) | Select-Object -First 1
    if (-not $list) { throw 'the dialog has no file list' }

    # The shell list is virtualised: bring the end of it into view, then look again.
    $scroll = $null
    if ($list.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$scroll)) {
        try { $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100) } catch { }
        Start-Sleep -Milliseconds 400
    }

    $items = Get-Descendants $dialog ([System.Windows.Automation.ControlType]::ListItem)
    foreach ($item in $items) {
        if ($item.Current.Name -eq $name) {
            $select = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $select.Select()
            return $true
        }
    }

    return $false
}

function Invoke-Save($dialog) {
    $buttons = Get-Descendants $dialog ([System.Windows.Automation.ControlType]::Button)
    foreach ($button in $buttons) {
        $caption = $button.Current.Name
        if ($caption -match '\bSave\b' -or $caption -match '^&Save') {
            $invoke = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $invoke.Invoke()
            return $true
        }
    }

    return $false
}

$dialog = Get-Dialog

# Constraint: the name box takes no value pattern and no list item is exposed for a plugin, so the dialog is
# brought to the front and the name is sent to the box it already focuses. No person presses a key.
Add-Type -AssemblyName System.Windows.Forms
$handle = [IntPtr]::new($dialog.Current.NativeWindowHandle)
[UiaWindows]::Foreground($handle.ToInt64()) | Out-Null
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait($File)
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Milliseconds 600

$still = @([UiaWindows]::Find((Get-EditorId), $Title) | ForEach-Object { [IntPtr]::new($_) })
if ($still.Count -eq 0) { Write-Output "typed $File"; exit 0 }

$chosen = Select-Target $dialog $File
if (-not $chosen) { Write-Output "no_list_item $File"; exit 2 }

$pressed = Invoke-Save $dialog
if (-not $pressed) { Write-Output 'no_save_button'; exit 3 }

Start-Sleep -Milliseconds 700

# Constraint: an existing plugin raises "File Overwrite", which is answered on that prompt's own window - the
# editor frame title never changes, so searching it finds nothing.
foreach ($confirmTitle in @('File Overwrite', 'Overwrite', 'Confirm Save As')) {
    $confirm = @([UiaWindows]::Find((Get-EditorId), $confirmTitle) | ForEach-Object { [IntPtr]::new($_) })
    foreach ($handle in $confirm) {
        $element = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        if (-not $element) { continue }

        $buttons = Get-Descendants $element ([System.Windows.Automation.ControlType]::Button)
        foreach ($button in $buttons) {
            if ($button.Current.Name -match '^&?Yes$') {
                $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Write-Output "confirm_yes $confirmTitle"
                Start-Sleep -Milliseconds 500
                break
            }
        }
    }
}

Write-Output "selected $File"
exit 0
