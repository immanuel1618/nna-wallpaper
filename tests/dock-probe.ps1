<#
.SYNOPSIS
  Stage 9 gate for the dock: /dock/* routes against a headless host, auth/origin guards, and a
  rendered screenshot of dock/index.html?mock=1.

.DESCRIPTION
  Starts NNA.Wallpaper.exe --headless on a throwaway --data dir with a small launch.json fixture
  (one plain-cmd item flagged "dock": true, so GET /dock/items has a deterministic pinned app
  without depending on what happens to be running on this machine), then drives:
    - GET  /dock/items    -> at least one item of kind app, separator, folder (x2), trash
    - GET  /dock/trash    -> {ok,empty,items,bytes}
    - GET  /dock/folder   -> entries[] for the configured Downloads folder
    - POST /dock/pin      -> 401/403 without a token
    - GET  /dock/items    -> 403 with a foreign Origin header
  then renders http://127.0.0.1:<port>/dock/?monitor=main&mock=1 with headless Edge and checks the
  screenshot is not blank (tests/png-mean.py).

.USAGE
  powershell -File tests\dock-probe.ps1 -Port 1627 [-Data <tmp-dir>] [-Exe <path>] [-Edge <path>]
#>
[CmdletBinding()]
param(
    [int]$Port = 1627,
    [string]$Data = (Join-Path $env:TEMP "nna-dock-probe-$Port"),
    [string]$Exe = "src\NNA.Wallpaper\bin\Release\net8.0-windows10.0.19041.0\win-x64\NNA.Wallpaper.exe",
    [string]$Edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    [string]$ShotDir = "H:\night-runs\nna-wallpaper-2\shots"
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

function Invoke-Api {
    param([string]$Method = 'GET', [string]$Path, [string]$Body = $null, [switch]$Auth, [hashtable]$ExtraHeaders)
    $uri = "$script:BaseUrl$Path"
    $headers = @{}
    if ($Auth) { $headers['X-Token'] = $script:ApiToken }
    if ($ExtraHeaders) { foreach ($k in $ExtraHeaders.Keys) { $headers[$k] = $ExtraHeaders[$k] } }
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
New-Item -ItemType Directory -Force -Path (Join-Path $Data 'config') | Out-Null

# launch.json fixture: one plain Win32 item flagged for the dock (deterministic pinned entry,
# independent of whatever happens to be running or of AppSettings.Dock.Pinned being empty).
$launchFixture = @{
    groups = @()
    items  = @{
        notepad = @{ label = 'Notepad'; cmd = 'notepad.exe'; dock = $true }
    }
} | ConvertTo-Json -Depth 6
Set-Content -Path (Join-Path $Data 'config\launch.json') -Value $launchFixture -Encoding utf8

$script:BaseUrl = "http://127.0.0.1:$Port"
$proc = $null

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

    # ── 1. GET /dock/items — at least one app, separator, folder x2, trash ─────────────────────
    $r = Invoke-Api -Method GET -Path '/dock/items'
    if ($r.Status -eq 200 -and $r.Json -and $r.Json.items) {
        $items = @($r.Json.items)
        $kinds = $items | Group-Object -Property kind | ForEach-Object { @{ $_.Name = $_.Count } }
        $appCount = @($items | Where-Object { $_.kind -eq 'app' }).Count
        $sepCount = @($items | Where-Object { $_.kind -eq 'separator' }).Count
        $folderCount = @($items | Where-Object { $_.kind -eq 'folder' }).Count
        $trashCount = @($items | Where-Object { $_.kind -eq 'trash' }).Count
        if ($appCount -ge 1) { Record 'items-app' 'PASS' "count=$appCount" } else { Record 'items-app' 'FAIL' "count=$appCount" }
        if ($sepCount -ge 1) { Record 'items-separator' 'PASS' } else { Record 'items-separator' 'FAIL' }
        if ($folderCount -ge 2) { Record 'items-folder' 'PASS' "count=$folderCount" } else { Record 'items-folder' 'FAIL' "count=$folderCount (Downloads/Desktop must exist on this machine)" }
        if ($trashCount -ge 1) { Record 'items-trash' 'PASS' } else { Record 'items-trash' 'FAIL' }
        $pinned = $items | Where-Object { $_.kind -eq 'app' -and $_.launchId -eq 'notepad' }
        if ($pinned) { Record 'items-pinned-fixture' 'PASS' } else { Record 'items-pinned-fixture' 'FAIL' 'notepad item (dock:true) not found' }
    } else {
        Record 'items-app' 'FAIL' "status=$($r.Status) body=$($r.Text)"
        Record 'items-separator' 'FAIL' 'no response'
        Record 'items-folder' 'FAIL' 'no response'
        Record 'items-trash' 'FAIL' 'no response'
        Record 'items-pinned-fixture' 'FAIL' 'no response'
    }

    # ── 2. GET /dock/trash — {ok,empty,items,bytes} ─────────────────────────────────────────────
    $r = Invoke-Api -Method GET -Path '/dock/trash'
    if ($r.Status -eq 200 -and $r.Json -and ($null -ne $r.Json.empty)) { Record 'trash-shape' 'PASS' "empty=$($r.Json.empty) items=$($r.Json.items)" }
    else { Record 'trash-shape' 'FAIL' "status=$($r.Status) body=$($r.Text)" }

    # ── 3. GET /dock/folder?path=<Downloads> — entries[] ────────────────────────────────────────
    $downloads = Join-Path $env:USERPROFILE 'Downloads'
    if (Test-Path $downloads) {
        $r = Invoke-Api -Method GET -Path ('/dock/folder?path=' + [uri]::EscapeDataString($downloads))
        if ($r.Status -eq 200 -and $r.Json -and ($null -ne $r.Json.entries)) { Record 'folder-entries' 'PASS' "count=$(@($r.Json.entries).Count)" }
        else { Record 'folder-entries' 'FAIL' "status=$($r.Status) body=$($r.Text)" }
    } else {
        Record 'folder-entries' 'FAIL' "Downloads not found at $downloads (fixture assumption)"
    }

    # ── 4. POST /dock/pin without a token — 401/403 ─────────────────────────────────────────────
    $r = Invoke-Api -Method POST -Path '/dock/pin' -Body (@{ launchId = 'notepad' } | ConvertTo-Json -Compress)
    if ($r.Status -eq 401 -or $r.Status -eq 403) { Record 'pin-no-token' 'PASS' "status=$($r.Status)" }
    else { Record 'pin-no-token' 'FAIL' "status=$($r.Status) body=$($r.Text)" }

    # ── 5. Foreign Origin — 403 on GET /dock/items too (LocalApi's Origin guard is global) ──────
    $r = Invoke-Api -Method GET -Path '/dock/items' -ExtraHeaders @{ Origin = 'http://evil.example' }
    if ($r.Status -eq 403) { Record 'foreign-origin' 'PASS' }
    else { Record 'foreign-origin' 'FAIL' "status=$($r.Status) body=$($r.Text)" }

    # ── 6. POST /dock/pin with the token — accepted, persisted, visible on the next /dock/items ─
    $r = Invoke-Api -Method POST -Path '/dock/pin' -Body (@{ launchId = 'notepad' } | ConvertTo-Json -Compress) -Auth
    if ($r.Status -eq 200 -and $r.Json -and $r.Json.ok -eq $true) { Record 'pin-with-token' 'PASS' }
    else { Record 'pin-with-token' 'FAIL' "status=$($r.Status) body=$($r.Text)" }

    # ── 7. headless Edge screenshot of dock/?monitor=main&mock=1 ────────────────────────────────
    New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null
    $shotPath = Join-Path $ShotDir 'stage9-dock-page.png'
    if (Test-Path $Edge) {
        $edgeArgs = @(
            '--headless=new', '--disable-gpu', '--hide-scrollbars',
            '--window-size=1400,300',
            ('--screenshot=' + $shotPath),
            "$script:BaseUrl/dock/?monitor=main&mock=1"
        )
        $edgeProc = Start-Process -FilePath $Edge -ArgumentList $edgeArgs -PassThru -Wait -WindowStyle Hidden
        Start-Sleep -Milliseconds 300
        if ((Test-Path $shotPath) -and (Get-Item $shotPath).Length -gt 0) {
            Record 'screenshot-taken' 'PASS' $shotPath
            $py = Get-Command python -ErrorAction SilentlyContinue
            if (-not $py) { $py = Get-Command python3 -ErrorAction SilentlyContinue }
            if ($py) {
                & $py.Source (Join-Path $PSScriptRoot 'png-mean.py') $shotPath 3 | Write-Host
                if ($LASTEXITCODE -eq 0) { Record 'screenshot-not-blank' 'PASS' } else { Record 'screenshot-not-blank' 'FAIL' 'mean brightness <= 3' }
            } else {
                Record 'screenshot-not-blank' 'FAIL' 'python not found on PATH — cannot run png-mean.py'
            }
        } else {
            Record 'screenshot-taken' 'FAIL' "msedge exit=$($edgeProc.ExitCode), no file at $shotPath"
            Record 'screenshot-not-blank' 'FAIL' 'no screenshot to check'
        }
    } else {
        Record 'screenshot-taken' 'FAIL' "msedge not found at $Edge"
        Record 'screenshot-not-blank' 'FAIL' 'no screenshot to check'
    }
}
finally {
    if ($proc -and -not $proc.HasExited) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
}

Write-Host ''
Write-Host "RESULT: $script:passed passed, $script:failed failed"
if ($script:failed -gt 0) { exit 1 } else { exit 0 }
