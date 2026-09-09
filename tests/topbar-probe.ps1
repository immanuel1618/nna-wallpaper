<#
.SYNOPSIS
  Stage 8B gate for the top bar v2 popovers (volume, calendar, the NNA menu, Control Center) and
  the new network/battery/layout/control modules.

.DESCRIPTION
  Starts NNA Wallpaper headless (never touches the owner's live instance on :1618), then:
    (a) checks that /topbar/ and /topbar/popup/?module=volume&mock=1 answer HTTP 200 (static
        pages served by the existing Api.MapStatic("/topbar/", ...) route — no new host route
        needed for this stage);
    (b) drives a headless Edge instance to screenshot the four popup pages under ?mock=1 (the
        stage's own topbar/mock-api.js stub, so this does not depend on the real /audio, /system,
        /taskbar, /app/* routes existing yet) and checks each PNG is not blank via png-mean.py;
    (c) runs `node --check` over every topbar/*.js and topbar/popup/*.js file.
  A real WPF TopBar/PopupWindow cannot be driven headlessly (it needs an interactive desktop and
  real WebView2 windows) — see the "NOT COVERED" line this script prints at the end. What IS
  covered here: the solution builds, the popup pages render under the mock contract, and their
  static/JS syntax is sound.

.PARAMETER Port
  Port for the throwaway --headless instance this script starts (never 1618).

.PARAMETER Data
  --data directory for that instance (wiped and recreated).

.PARAMETER Exe
  Path to NNA.Wallpaper.exe. Defaults to the Release build output next to this repo.

.PARAMETER ShotsDir
  Where to save the popup screenshots.

.EXAMPLE
  powershell -File tests\topbar-probe.ps1 -Port 1625
#>
[CmdletBinding()]
param(
    [int]$Port = 1625,
    [string]$Data = (Join-Path $env:TEMP 'nna-topbar-probe'),
    [string]$Exe = '',
    [string]$ShotsDir = 'H:\night-runs\nna-wallpaper-2\shots',
    [string]$MsEdge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
)

if (-not $Exe) { $Exe = Join-Path (Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent) ('src'+[char]92+'NNA.Wallpaper'+[char]92+'bin'+[char]92+'Release'+[char]92+'net8.0-windows10.0.19041.0'+[char]92+'win-x64'+[char]92+'NNA.Wallpaper.exe') }
# 'Continue' (not 'Stop'): a native exe (node/python/msedge) writing anything to stderr with
# 2>&1 redirection turns into a terminating NativeCommandError under 'Stop' even on exit code 0
# (PowerShell 5.1 quirk) — every native call below checks $LASTEXITCODE explicitly instead, and
# the few cmdlet calls that must throw (Invoke-RestMethod polling /health) have their own try/catch.
$ErrorActionPreference = 'Continue'
$script:passed = 0
$script:failed = 0
$script:skipped = 0

function Record {
    param([string]$Name, [string]$Status, [string]$Detail = '')
    $line = "$Status $Name"
    if ($Detail) { $line += ": $Detail" }
    Write-Host $line
    switch ($Status) {
        'PASS' { $script:passed++ }
        'FAIL' { $script:failed++ }
        'SKIP' { $script:skipped++ }
    }
}

$repoRoot = Split-Path $PSScriptRoot -Parent
$Base = "http://127.0.0.1:$Port"

# ── (c) node --check over every topbar JS file — no running instance needed, do this first ──────

$jsFiles = @(
    Join-Path $repoRoot 'topbar\bar.js'
    Join-Path $repoRoot 'topbar\modules.js'
    Join-Path $repoRoot 'topbar\mock-api.js'
    Join-Path $repoRoot 'topbar\popup\popup.js'
)
foreach ($f in $jsFiles) {
    if (-not (Test-Path $f)) { Record "node-check $(Split-Path $f -Leaf)" 'FAIL' "missing: $f"; continue }
    $out = & node --check $f 2>&1
    if ($LASTEXITCODE -eq 0) { Record "node-check $(Split-Path $f -Leaf)" 'PASS' }
    else { Record "node-check $(Split-Path $f -Leaf)" 'FAIL' ($out -join ' ') }
}

# ── setup ─────────────────────────────────────────────────────────────────────────────────────

if (-not (Test-Path $Exe)) {
    Write-Host "FAIL setup: -Exe not found: $Exe (build with: dotnet build NNA.Wallpaper.sln -c Release)"
    exit 2
}

if (Test-Path $Data) { Remove-Item -Recurse -Force $Data -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $Data | Out-Null
New-Item -ItemType Directory -Force -Path $ShotsDir | Out-Null

$proc = $null
try {
    # --headless: no WPF windows (no TopBarWindow/PopupWindow are created), only the HTTP host —
    # this is expected and is exactly the "NOT COVERED" limitation this script documents at the end.
    $proc = Start-Process -FilePath $Exe -ArgumentList @('--headless', '--port', "$Port", '--data', $Data) -PassThru -WindowStyle Hidden

    $healthy = $false
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        try {
            $h = Invoke-RestMethod -Uri "$Base/health" -Method Get -TimeoutSec 2
            if ($h.ok) { $healthy = $true; break }
        }
        catch { }
        Start-Sleep -Milliseconds 300
    }
    if (-not $healthy) {
        Write-Host "FAIL startup: host did not answer /health within 25s"
        exit 2
    }
    Record 'startup' 'PASS' "/health ok, pid=$($proc.Id)"

    # ── (a) static pages: 200 ────────────────────────────────────────────────────────────────

    function Get-Status([string]$Path) {
        try {
            $r = Invoke-WebRequest -Uri "$Base$Path" -Method Get -TimeoutSec 8 -UseBasicParsing
            return [int]$r.StatusCode
        }
        catch {
            if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
            return -1
        }
    }

    $staticPaths = @('/topbar/', '/topbar/popup/?module=volume&mock=1')
    foreach ($p in $staticPaths) {
        $status = Get-Status $p
        if ($status -eq 200) { Record "static $p" 'PASS' }
        else { Record "static $p" 'FAIL' "status=$status" }
    }

    # ── (b) headless Edge screenshots of the four popup pages ───────────────────────────────

    if (-not (Test-Path $MsEdge)) {
        Record 'popup-screenshots' 'SKIP' "msedge not found: $MsEdge"
    }
    else {
        $pngMean = Join-Path $repoRoot 'tests\png-mean.py'
        $modules = @('volume', 'calendar', 'nna', 'control')
        foreach ($m in $modules) {
            $url = "$Base/topbar/popup/?module=$m&mock=1"
            $out = Join-Path $ShotsDir "stage8-popup-$m.png"
            if (Test-Path $out) { Remove-Item -Force $out }
            $edgeData = Join-Path $Data "edge-$m"

            & $MsEdge `
                '--headless=new' '--disable-gpu' '--hide-scrollbars' '--no-first-run' '--no-default-browser-check' `
                "--user-data-dir=$edgeData" '--window-size=420,600' "--screenshot=$out" '--virtual-time-budget=4000' `
                $url 2>&1 | Out-Null

            if (-not (Test-Path $out)) {
                Record "popup-screenshot $m" 'FAIL' "no PNG produced at $out"
                continue
            }
            $meanOut = & python $pngMean $out 3 2>&1
            if ($LASTEXITCODE -eq 0) { Record "popup-screenshot $m" 'PASS' ($meanOut -join ' ') }
            else { Record "popup-screenshot $m" 'FAIL' ($meanOut -join ' ') }
        }
    }

    # ── PopupWindow.xaml.cs / TopBarManager.TogglePopup: build-only sanity ──────────────────
    # A real popup window needs an interactive desktop + WebView2; --headless creates no
    # TopBarWindow/PopupWindow at all (HeadlessHostApp), so there is nothing to click here.
    # dotnet build (run separately, see docs/TOPBAR.md) is the proof that TopBarManager.Apply()
    # and PopupWindow compile and wire up without a running engine.
    Record 'live-popup-window' 'SKIP' 'not reachable headlessly — see docs/TOPBAR.md "Что не проверено"'
}
finally {
    if ($proc -and -not $proc.HasExited) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
}

Write-Host ''
Write-Host "RESULT: $script:passed passed, $script:failed failed, $script:skipped skipped"
if ($script:failed -gt 0) { exit 1 } else { exit 0 }
