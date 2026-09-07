<#
.SYNOPSIS
  End-to-end gate for the window service: click-to-raise vs. Shift-click-to-duplicate.

.DESCRIPTION
  Starts the NNA Wallpaper host headless, adds one throwaway launch item ("paint" ->
  mspaint.exe — a plain Win32 top-level window, unlike the Windows 11 Store-packaged Notepad
  which can come back as an ApplicationFrameWindow container and would make this test less
  deterministic), then drives POST /launch/item, GET /windows, POST /windows/activate (via a
  second /launch/item call with newInstance not set), POST /windows/minimize-equivalent (real
  ShowWindow via P/Invoke, to simulate the user minimizing it by hand), newInstance:true, and
  POST /windows/close — printing PASS/FAIL per step.

.USAGE
  powershell -File tests\activate-probe.ps1 -Port 1621 [-Data <tmp-dir>] [-Exe <path>]
#>
[CmdletBinding()]
param(
    [int]$Port = 1621,
    [string]$Data = (Join-Path $env:TEMP "nna-activate-probe-$Port"),
    [string]$Exe = "src\NNA.Wallpaper\bin\Release\net8.0-windows10.0.19041.0\win-x64\NNA.Wallpaper.exe"
)

$ErrorActionPreference = 'Stop'
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

# ── Win32 helpers (foreground/minimize checks independent of the host's own API) ────────────────

$code = @'
using System;
using System.Runtime.InteropServices;
public static class Probe {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  public const int SW_MINIMIZE = 6;
}
'@
Add-Type -TypeDefinition $code

# ── HTTP helper: PowerShell 5.1's Invoke-WebRequest throws on 4xx/5xx, so pull status+body back
#    out of the exception instead of losing them (same pattern as planner-probe.ps1). ────────────

function Invoke-Api {
    param([string]$Method = 'GET', [string]$Path, [string]$Body = $null, [switch]$Auth)
    $uri = "$script:BaseUrl$Path"
    $headers = @{}
    if ($Auth) { $headers['X-Token'] = $script:ApiToken }
    $status = 0; $text = ''
    try {
        if (-not [string]::IsNullOrEmpty($Body)) {
            $resp = Invoke-WebRequest -Uri $uri -Method $Method -Headers $headers -Body $Body -ContentType 'application/json; charset=utf-8' -UseBasicParsing -TimeoutSec 15
        } else {
            $resp = Invoke-WebRequest -Uri $uri -Method $Method -Headers $headers -UseBasicParsing -TimeoutSec 15
        }
        $status = [int]$resp.StatusCode
        $text = $resp.Content
    }
    catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($r) {
            $status = [int]$r.StatusCode
            $stream = $r.GetResponseStream()
            $reader = New-Object System.IO.StreamReader($stream)
            $text = $reader.ReadToEnd()
            $reader.Dispose()
        } else { $text = $_.Exception.Message }
    }
    $json = $null
    if ($text) { try { $json = $text | ConvertFrom-Json -ErrorAction Stop } catch { } }
    return [PSCustomObject]@{ Status = $status; Text = $text; Json = $json }
}

# ── setup ─────────────────────────────────────────────────────────────────────────────────────

$exePath = (Resolve-Path $Exe -ErrorAction SilentlyContinue)
if (-not $exePath) { Write-Host "FAIL setup: exe not found: $Exe"; exit 2 }
$exePath = $exePath.Path

if (Test-Path $Data) { Remove-Item -Recurse -Force $Data -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $Data | Out-Null

$script:BaseUrl = "http://127.0.0.1:$Port"
$proc = $null
$paintPids = @()

try {
    $proc = Start-Process -FilePath $exePath -ArgumentList @('--headless', '--port', "$Port", '--data', $Data) -PassThru -WindowStyle Hidden

    $healthy = $false
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        try { $h = Invoke-RestMethod -Uri "$script:BaseUrl/health" -Method Get -TimeoutSec 2; if ($h.ok) { $healthy = $true; break } } catch { }
        Start-Sleep -Milliseconds 300
    }
    if (-not $healthy) { Write-Host "FAIL startup: host did not answer /health within 25s"; exit 2 }
    Record 'startup' 'PASS' "pid=$($proc.Id)"

    $appJsonPath = Join-Path $Data 'config\app.json'
    if (-not (Test-Path $appJsonPath)) { Write-Host "FAIL setup: app.json not found at $appJsonPath"; exit 2 }
    $script:ApiToken = (Get-Content -Raw -Path $appJsonPath | ConvertFrom-Json).apiToken
    if (-not $script:ApiToken) { Write-Host "FAIL setup: could not read apiToken"; exit 2 }

    # ── 1. seed one launch item via PUT /config (mspaint.exe: plain Win32 window, not a Store
    #    package, so no ApplicationFrameWindow indirection to worry about here) ──────────────────
    $launchBody = (@{ launch = @{ items = @{ paint = @{ label = 'Paint'; cmd = 'mspaint.exe' } } } } | ConvertTo-Json -Compress -Depth 5)
    $r = Invoke-Api -Method PUT -Path '/config' -Body $launchBody -Auth
    if ($r.Status -eq 200 -and $r.Json -and $r.Json.ok -eq $true) { Record 'seed-launch-item' 'PASS' }
    else { Write-Host "FAIL seed-launch-item: status=$($r.Status) body=$($r.Text)"; exit 2 }

    # ── 2. launch it — no window open yet, so this must start a fresh mspaint.exe ────────────────
    $r = Invoke-Api -Method POST -Path '/launch/item?id=paint' -Auth
    if ($r.Status -eq 200 -and $r.Json -and ($r.Json.launched -contains 'paint')) { Record 'launch-first' 'PASS' "activated=$($r.Json.activated -join ',')" }
    else { Record 'launch-first' 'FAIL' "status=$($r.Status) body=$($r.Text)" }

    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and -not (Get-Process -Name mspaint -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 250 }
    $procs = Get-Process -Name mspaint -ErrorAction SilentlyContinue
    if ($procs -and @($procs).Count -eq 1) { Record 'process-started' 'PASS' "pid=$($procs.Id)" }
    else { Record 'process-started' 'FAIL' "count=$(@($procs).Count)" }
    Start-Sleep -Milliseconds 1000  # let the window finish showing

    # ── 3. GET /windows must list it ─────────────────────────────────────────────────────────────
    $r = Invoke-Api -Method GET -Path '/windows'
    $win = $null
    if ($r.Status -eq 200 -and $r.Json -and $r.Json.windows) {
        $win = @($r.Json.windows) | Where-Object { $_.processName -eq 'mspaint' } | Select-Object -First 1
    }
    if ($win) { Record 'window-listed' 'PASS' "hwnd=$($win.hwnd) title=$($win.title)" }
    else { Record 'window-listed' 'FAIL' "body=$($r.Text)"; }
    if (-not $win) { throw "cannot continue without a window" }
    $hwnd = [IntPtr]$win.hwnd

    # ── 4. minimize it for real (independent of our own /windows/minimize route) ────────────────
    [Probe]::ShowWindow($hwnd, [Probe]::SW_MINIMIZE) | Out-Null
    Start-Sleep -Milliseconds 500
    if ([Probe]::IsIconic($hwnd)) { Record 'minimized' 'PASS' }
    else { Record 'minimized' 'FAIL' 'IsIconic false after ShowWindow(SW_MINIMIZE)' }

    # ── 5. launch again (newInstance not set) — must raise the existing (minimized) window,
    #    not start a second mspaint.exe ──────────────────────────────────────────────────────────
    $r = Invoke-Api -Method POST -Path '/launch/item?id=paint' -Auth
    Start-Sleep -Milliseconds 1000
    $procsAfter = Get-Process -Name mspaint -ErrorAction SilentlyContinue
    $stillOne = $procsAfter -and @($procsAfter).Count -eq 1
    $fg = [Probe]::GetForegroundWindow()
    $raised = ($fg -eq $hwnd) -and -not [Probe]::IsIconic($hwnd)
    if ($r.Status -eq 200 -and $stillOne -and $raised) { Record 'raise-existing' 'PASS' "activated=$($r.Json.activated -join ',')" }
    else { Record 'raise-existing' 'FAIL' "status=$($r.Status) procCount=$(@($procsAfter).Count) foreground=$($raised) body=$($r.Text)" }

    # ── 6. newInstance:true must start a second mspaint.exe alongside the first ──────────────────
    $r = Invoke-Api -Method POST -Path '/launch/item?id=paint' -Body (@{ newInstance = $true } | ConvertTo-Json -Compress) -Auth
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and @(Get-Process -Name mspaint -ErrorAction SilentlyContinue).Count -lt 2) { Start-Sleep -Milliseconds 250 }
    $procsTwo = Get-Process -Name mspaint -ErrorAction SilentlyContinue
    if ($r.Status -eq 200 -and $procsTwo -and @($procsTwo).Count -eq 2) { Record 'new-instance' 'PASS' "count=2" }
    else { Record 'new-instance' 'FAIL' "status=$($r.Status) count=$(@($procsTwo).Count) body=$($r.Text)" }
    $paintPids = @($procsTwo | Select-Object -ExpandProperty Id)

    # ── 7. close both via POST /windows/close (WM_CLOSE, not a kill) ────────────────────────────
    Start-Sleep -Milliseconds 500
    $r = Invoke-Api -Method GET -Path '/windows'
    $wins = @()
    if ($r.Status -eq 200 -and $r.Json -and $r.Json.windows) { $wins = @($r.Json.windows) | Where-Object { $_.processName -eq 'mspaint' } }
    foreach ($w in $wins) {
        Invoke-Api -Method POST -Path '/windows/close' -Body (@{ hwnd = $w.hwnd } | ConvertTo-Json -Compress) -Auth | Out-Null
    }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name mspaint -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 250 }
    $left = Get-Process -Name mspaint -ErrorAction SilentlyContinue
    if (-not $left) { Record 'close-both' 'PASS' "closed $($wins.Count) window(s)" }
    else { Record 'close-both' 'FAIL' "still running: $(@($left).Count)" }

    # ── 8. GET /windows must no longer list mspaint (past the route's 500ms cache TTL) ──────────
    Start-Sleep -Milliseconds 700
    $r = Invoke-Api -Method GET -Path '/windows'
    $stillListed = $false
    if ($r.Status -eq 200 -and $r.Json -and $r.Json.windows) { $stillListed = @($r.Json.windows | Where-Object { $_.processName -eq 'mspaint' }).Count -gt 0 }
    if (-not $stillListed) { Record 'windows-cleared' 'PASS' }
    else { Record 'windows-cleared' 'FAIL' 'mspaint still present in /windows' }
}
finally {
    # belt-and-braces: make sure no stray mspaint survives this probe even if a step failed.
    Get-Process -Name mspaint -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    if ($proc -and -not $proc.HasExited) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
}

Write-Host ''
Write-Host "RESULT: $script:passed passed, $script:failed failed"
if ($script:failed -gt 0) { exit 1 } else { exit 0 }
