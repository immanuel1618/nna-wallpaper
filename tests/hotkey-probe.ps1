<#
.SYNOPSIS
  Headless gate for the push-to-talk hotkey's host-side half (docs/PLANNER.md). --headless never
  runs WPF, so no real RegisterHotKey happens and no microphone can be driven from here — this
  probe only checks what headless CAN prove:
    (a) GET /planner/status carries a "hotkey" {registered, error} field, even while logged out,
        and reports registered=false (headless never constructs NNA.Wallpaper.Hotkeys).
    (b) POST /planner/capture-file {"path": "..."} — a TestEndpoints-only route that runs a WAV
        file through the exact same PlannerService.CaptureVoiceAsync a real hotkey release calls —
        never answers >=500. 401 (no session in this throwaway --data dir) or any other 4xx is an
        expected, passing outcome; only a 5xx (the capture pipeline itself blowing up) fails.
  A live hotkey press/release and the mic path are outside what a headless run can verify; see
  docs/PLANNER.md "Что не проверено" for the manual check on the owner's install.

.USAGE
  powershell -File tests\hotkey-probe.ps1 -Port 1635 -Exe <path to NNA.Wallpaper.exe>
#>
[CmdletBinding()]
param(
    [int]$Port = 1635,
    [string]$Data = (Join-Path $env:TEMP 'nna-hotkey-probe'),
    [string]$Exe,
    [string]$Voice = (Join-Path $PSScriptRoot 'fixtures\voice-test.wav')
)

$ErrorActionPreference = 'Stop'
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

function Invoke-Api {
    param(
        [string]$Method = 'GET',
        [string]$Path,
        [string]$Body = $null,
        [switch]$Auth
    )
    $uri = "$script:BaseUrl$Path"
    $headers = @{}
    if ($Auth) { $headers['X-Token'] = $script:ApiToken }

    $status = 0
    $text = ''
    try {
        if (-not [string]::IsNullOrEmpty($Body)) {
            $resp = Invoke-WebRequest -Uri $uri -Method $Method -Headers $headers -Body $Body -ContentType 'application/json; charset=utf-8' -UseBasicParsing -TimeoutSec 20
        }
        else {
            $resp = Invoke-WebRequest -Uri $uri -Method $Method -Headers $headers -UseBasicParsing -TimeoutSec 20
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
        }
        else {
            $text = $_.Exception.Message
        }
    }

    $json = $null
    if ($text) { try { $json = $text | ConvertFrom-Json -ErrorAction Stop } catch { } }
    return [PSCustomObject]@{ Status = $status; Text = $text; Json = $json }
}

# ── setup ─────────────────────────────────────────────────────────────────────────────────────────

if (-not $Exe -or -not (Test-Path $Exe)) {
    Write-Host "FAIL setup: -Exe not found: $Exe"
    exit 2
}

if (Test-Path $Data) { Remove-Item -Recurse -Force $Data -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $Data | Out-Null

$script:BaseUrl = "http://127.0.0.1:$Port"
$proc = $null

try {
    $proc = Start-Process -FilePath $Exe -ArgumentList @('--headless', '--port', "$Port", '--data', $Data) -PassThru -WindowStyle Hidden

    $healthy = $false
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        try {
            $h = Invoke-RestMethod -Uri "$script:BaseUrl/health" -Method Get -TimeoutSec 2
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

    $appJsonPath = Join-Path $Data 'config\app.json'
    if (Test-Path $appJsonPath) {
        $script:ApiToken = (Get-Content -Raw -Path $appJsonPath | ConvertFrom-Json).apiToken
    }

    # ── (a) /planner/status carries a "hotkey" field, even logged out ──────────────────────────────
    $r = Invoke-Api -Path '/planner/status'
    $hasHotkeyField = $r.Json -and ($r.Json.PSObject.Properties.Name -contains 'hotkey') `
        -and $r.Json.hotkey -and ($r.Json.hotkey.PSObject.Properties.Name -contains 'registered')
    if ($r.Status -eq 200 -and $hasHotkeyField) {
        Record 'status-has-hotkey' 'PASS' "registered=$($r.Json.hotkey.registered) error=$($r.Json.hotkey.error)"
    }
    else {
        Record 'status-has-hotkey' 'FAIL' "status=$($r.Status) body=$($r.Text)"
    }

    if ($hasHotkeyField -and $r.Json.hotkey.registered -eq $false) {
        Record 'headless-not-registered' 'PASS' 'expected: --headless never constructs NNA.Wallpaper.Hotkeys (no WPF)'
    }
    elseif ($hasHotkeyField) {
        Record 'headless-not-registered' 'FAIL' 'hotkey reports registered=true from a headless run'
    }
    else {
        Record 'headless-not-registered' 'SKIP' 'no hotkey field to check (see status-has-hotkey)'
    }

    # ── (b) /planner/capture-file must never 5xx ────────────────────────────────────────────────────
    if (-not (Test-Path $Voice)) {
        Record 'capture-file' 'SKIP' "fixture not found: $Voice"
    }
    else {
        $body = (@{ path = $Voice } | ConvertTo-Json -Compress)
        $r = Invoke-Api -Method POST -Path '/planner/capture-file' -Body $body -Auth
        if ($r.Status -ge 500) {
            Record 'capture-file' 'FAIL' "status=$($r.Status) body=$($r.Text)"
        }
        else {
            # No session exists in this throwaway --data dir, so 401 ("not logged in") is the
            # expected result; any other 2xx/4xx is also accepted here — only >=500 fails this step,
            # since what this probe checks is "the capture pipeline doesn't blow up", not the auth
            # outcome (planner-probe.ps1 already covers the signed-in capture path end to end).
            Record 'capture-file' 'PASS' "status=$($r.Status) body=$($r.Text)"
        }
    }

    # ── negative: capture-file is TestEndpoints-only; a --headless run always sets TestEndpoints
    #    (see App.xaml.cs: TestEndpoints = Args.Headless || Args.TestEngine), so the "route absent
    #    in a real, non-test run" case cannot be probed from a headless launch — documented gap,
    #    not a failure here. ──────────────────────────────────────────────────────────────────────
    Record 'route-test-only' 'SKIP' 'cannot probe a non-TestEndpoints run from --headless (always TestEndpoints=true)'
}
finally {
    if ($proc -and -not $proc.HasExited) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
}

Write-Host ''
Write-Host "RESULT: $script:passed passed, $script:failed failed, $script:skipped skipped"
if ($script:failed -gt 0) { exit 1 } else { exit 0 }
