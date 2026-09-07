# Stage 3A: verifies the settings window has no system title bar and that the custom BrandWindow
# chrome (min/max/close) is present and functional, against an ALREADY RUNNING instance (a window
# needs a real desktop session; this cannot run headless). Uses UI Automation, not pixel probing.
#
# Usage: powershell -File tests\settings-window-probe.ps1 -Port <p> [-Token <apiToken>] [-Data <dir>]
#
# Do NOT point -Port at the owner's live instance on 1618 unless it is already running the build
# that includes this stage's chrome changes — an older build has no BtnMinimize/BtnMaximize/BtnClose
# automation ids and every check below will FAIL.
[CmdletBinding()]
param(
    [int]$Port = 1619,
    [string]$Token = '',
    [string]$Data = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$script:failed = 0
function Check {
    param([bool]$Cond, [string]$Name, [string]$Detail = '')
    if ($Cond) { Write-Host "PASS $Name" }
    else { Write-Host "FAIL $Name $Detail"; $script:failed++ }
}

# ── resolve the API token if not given explicitly ──────────────────────────────────────────────
if (-not $Token) {
    $dataDir = $Data
    if (-not $dataDir) { $dataDir = Join-Path $env:LOCALAPPDATA 'NNA Wallpaper' }
    $appJson = Join-Path $dataDir 'config\app.json'
    if (Test-Path $appJson) {
        $cfg = Get-Content -Raw -Path $appJson | ConvertFrom-Json
        $Token = $cfg.apiToken
    }
}
if (-not $Token) { Write-Host "FAIL no apiToken found (pass -Token or -Data explicitly)"; exit 1 }

# ── open the settings window over the API ───────────────────────────────────────────────────────
$headers = @{ 'X-Token' = $Token }
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$Port/app/settings" -Headers $headers | Out-Null
Start-Sleep -Milliseconds 1500

# ── find the window via UI Automation ───────────────────────────────────────────────────────────
$root = [System.Windows.Automation.AutomationElement]::RootElement
# ClassName for a WPF app is generated and unstable; search top-level windows by Name instead,
# matching either the brand caption or (for an older build without this stage's chrome) the
# legacy localized title, so a mismatch here is reported as "window not found" rather than a
# false negative from a title that no longer matches.
$win = $null
$children = $root.FindAll([System.Windows.Automation.TreeScope]::Children,
    [System.Windows.Automation.Condition]::TrueCondition)
foreach ($c in $children) {
    if ($c.Current.Name -eq 'NNA WALLPAPER') { $win = $c; break }
}
if (-not $win) { Write-Host "FAIL settings window not found via UI Automation (Name~'NNA WALLPAPER')"; exit 1 }
Write-Host "found window: '$($win.Current.Name)'"

# ── no system title bar ─────────────────────────────────────────────────────────────────────────
$titleBarCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::TitleBar)
$titleBar = $win.FindFirst([System.Windows.Automation.TreeScope]::Children, $titleBarCond)
Check ($null -eq $titleBar) 'no system TitleBar element'

# ── brand chrome buttons present ────────────────────────────────────────────────────────────────
function FindById([string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
$btnMin = FindById 'BtnMinimize'
$btnMax = FindById 'BtnMaximize'
$btnClose = FindById 'BtnClose'
Check ($null -ne $btnMin) 'BtnMinimize present'
Check ($null -ne $btnMax) 'BtnMaximize present'
Check ($null -ne $btnClose) 'BtnClose present'

# ── maximize / restore toggle via the button ────────────────────────────────────────────────────
function InvokeButton($el) {
    $pattern = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}
function WindowState($el) {
    $wp = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    return $wp.Current.WindowVisualState
}

if ($btnMax) {
    InvokeButton $btnMax
    Start-Sleep -Milliseconds 500
    $state1 = WindowState $win
    Check ($state1 -eq [System.Windows.Automation.WindowVisualState]::Maximized) "maximize toggles WindowState" "(got $state1)"

    InvokeButton $btnMax
    Start-Sleep -Milliseconds 500
    $state2 = WindowState $win
    Check ($state2 -eq [System.Windows.Automation.WindowVisualState]::Normal) "restore toggles WindowState back" "(got $state2)"
}

# ── close via the button ────────────────────────────────────────────────────────────────────────
if ($btnClose) {
    InvokeButton $btnClose
    Start-Sleep -Milliseconds 500
    $stillThere = $false
    foreach ($c in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($c.Current.NativeWindowHandle -eq $win.Current.NativeWindowHandle) { $stillThere = $true }
    }
    Check (-not $stillThere) 'BtnClose closed the window'
}

if ($script:failed -eq 0) { "PASS settings-window-probe"; exit 0 }
else { "FAIL settings-window-probe ($script:failed check(s) failed)"; exit 1 }
