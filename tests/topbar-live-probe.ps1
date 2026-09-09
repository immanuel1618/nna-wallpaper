<#
.SYNOPSIS
  Stage 8C live gate: runs tests/TopBarPreview (a real, composition-hosted TopBarWindow +
  PopupWindow on the owner's vertical monitor, pages read-only from the owner's already-running
  instance on :1618) and parses its output into PASS/FAIL lines.

.DESCRIPTION
  A real TopBarWindow/PopupWindow needs an interactive desktop and real WebView2 windows — it
  cannot be driven headlessly (that is what tests/topbar-probe.ps1 covers instead: static pages,
  popup screenshots under ?mock=1, JS syntax — see its own "NOT COVERED" line). This script is the
  live counterpart for the two things only a real run can prove:
    - clicking a topbar module that opens a popover (clock -> calendar, nna -> the NNA menu)
      actually results in a PopupWindow ("NNA Wallpaper Popup") appearing;
    - clicking the bar itself never makes it ("NNA Wallpaper Top Bar") the foreground window —
      the stage 8C focus-steal bug this stage's composition-hosting change fixes.
  It never touches the owner's live instance beyond ordinary GETs (same pattern as
  tests/WindowPreview) and never registers as an AppBar on the owner's monitor
  (TopBarSettings.ReserveSpace=false in the helper).

.USAGE
  powershell -File tests\topbar-live-probe.ps1
  powershell -File tests\topbar-live-probe.ps1 -Exe <path-to-TopBarPreview.exe>
#>
[CmdletBinding()]
param(
    [string]$Exe = '',
    [string]$ShotsDir = 'H:\night-runs\nna-wallpaper-2\shots'
)

if (-not $Exe) { $Exe = Join-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) ('TopBarPreview'+[char]92+'bin'+[char]92+'Release'+[char]92+'net8.0-windows10.0.19041.0'+[char]92+'win-x64'+[char]92+'TopBarPreview.exe') }
$ErrorActionPreference = 'Continue'
$script:passed = 0
$script:failed = 0

function Record {
    param([string]$Name, [string]$Status, [string]$Detail = '')
    $line = "$Status $Name"
    if ($Detail) { $line += ": $Detail" }
    Write-Host $line
    switch ($Status) {
        'PASS' { $script:passed++ }
        'FAIL' { $script:failed++ }
    }
}

if (-not (Test-Path $Exe)) {
    Write-Host "FAIL setup: -Exe not found: $Exe (build with: dotnet build tests\TopBarPreview\TopBarPreview.csproj -c Release)"
    exit 2
}

# TopBarPreview is a WinExe (GUI subsystem, needs a real desktop for its WPF windows) — Windows does
# not attach a console to it, so Console.WriteLine is invisible to a plain `& $Exe` capture even
# though the process runs fine; redirect stdout/stderr to files instead (Start-Process, not `&`/
# 2>&1 — see the PowerShell-tool note on native stderr redirection).
$stdout = Join-Path $env:TEMP 'topbar-live-probe-out.txt'
$stderr = Join-Path $env:TEMP 'topbar-live-probe-err.txt'
Remove-Item -Force $stdout, $stderr -ErrorAction SilentlyContinue
$proc = Start-Process -FilePath $Exe -ArgumentList @($ShotsDir) -PassThru -NoNewWindow `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr
$exited = $proc.WaitForExit(60000)
if (-not $exited) {
    Record 'TopBarPreview' 'FAIL' 'timed out after 60s — killing'
    try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
}
$output = @()
if (Test-Path $stdout) { $output += Get-Content $stdout }
if (Test-Path $stderr) { $output += Get-Content $stderr }
$output | ForEach-Object { Write-Host $_ }

$text = $output -join "`n"

# One PASS/FAIL line per popup: "RESULT <name>: popup=<True/False> bar-stole-focus=<True/False>"
$results = [regex]::Matches($text, 'RESULT ([^:]+): popup=(True|False) bar-stole-focus=(True|False)')
if ($results.Count -eq 0) {
    Record 'topbar-live-probe' 'FAIL' 'no RESULT lines in TopBarPreview output — see console output above'
}
foreach ($m in $results) {
    $name = $m.Groups[1].Value.Trim()
    $popupFound = $m.Groups[2].Value -eq 'True'
    $stoleFocus = $m.Groups[3].Value -eq 'True'
    if ($popupFound -and -not $stoleFocus) {
        Record "popup $name" 'PASS' 'popup opened, bar did not steal focus'
    }
    else {
        Record "popup $name" 'FAIL' "popup found=$popupFound bar-stole-focus=$stoleFocus"
    }
}

$shot = Join-Path $ShotsDir 'stage8c-popup-live.png'
if (Test-Path $shot) { Record 'screenshot' 'PASS' $shot }
else { Record 'screenshot' 'FAIL' "not found: $shot" }

# Not exit-code based: Start-Process's ExitCode reporting for a redirected, no-new-window WinExe
# child is unreliable in this PowerShell (observed empty even after HasExited=True and Refresh());
# the parsed RESULT lines above (and the trailing PASS/FAIL text line) are the authoritative signal
# — TopBarPreview's own Main() already ties its process exit code to the same outcome for any
# non-interactive caller that CAN read it (e.g. the Bash tool's console, or `cmd /c`).
if ($text -notmatch '(?m)^PASS\s*$') { Record 'TopBarPreview PASS line' 'FAIL' 'no trailing PASS line in output' }

Write-Host ''
Write-Host "RESULT: $script:passed passed, $script:failed failed"
if ($script:failed -gt 0) { exit 1 } else { exit 0 }
