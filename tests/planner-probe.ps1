<#
.SYNOPSIS
  End-to-end gate for the Planner module: starts the host headless, signs a Telegram Login Widget
  payload the same way the widget itself would, drives every /planner/* route, and cleans up after
  itself. Secrets (bot token, owner tg id) are read from H:\secrets only in memory — never printed,
  never written to a file, never logged.

.USAGE
  powershell -File tests\planner-probe.ps1 -Port 1628 -Data <tmp-dir> -Exe <path to NNA.Wallpaper.exe>
#>
[CmdletBinding()]
param(
    [int]$Port = 1618,
    [string]$Data = (Join-Path $env:TEMP 'nna-planner-probe'),
    [string]$Exe
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

# ── secrets (read-only, in memory only) ──────────────────────────────────────────────────────────

function Read-BotToken {
    $file = 'H:\secrets\tg-bot-nna-planner.md'
    if (-not (Test-Path $file)) { return $null }
    $text = Get-Content -Raw -Path $file -Encoding UTF8
    if ($text -match '\|\s*Token\s*\|\s*`([^`]+)`') { return $Matches[1] }
    return $null
}

function Read-OwnerId {
    $file = 'H:\secrets\tg-ids.md'
    if (-not (Test-Path $file)) { return $null }
    $text = Get-Content -Raw -Path $file -Encoding UTF8
    foreach ($line in ($text -split "`r?`n")) {
        if ($line -match 'Костя' -and $line -match '`(\d+)`') { return $Matches[1] }
    }
    return $null
}

# ── Telegram Login Widget signature — mirrors planner/edge/auth-telegram-widget/index.ts and
#    planner/test-widget-auth.mjs: secret = SHA256(bot_token) raw digest (not HMAC),
#    expected = HMAC_SHA256(key=secret, msg=data_check_string) hex. ─────────────────────────────────

function New-SignedWidgetPayload {
    param([string]$BotToken, [string]$OwnerId, [long]$AuthDate)

    $fields = [ordered]@{
        auth_date  = "$AuthDate"
        first_name = 'Konstantin'
        id         = "$OwnerId"
        username   = 'immanuel_1618'
    }
    $dataCheckString = (($fields.Keys | Sort-Object) | ForEach-Object { "$_=$($fields[$_])" }) -join "`n"

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try { $secretBytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($BotToken)) }
    finally { $sha256.Dispose() }

    $hmac = New-Object System.Security.Cryptography.HMACSHA256
    $hmac.Key = $secretBytes
    try { $hashBytes = $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($dataCheckString)) }
    finally { $hmac.Dispose() }

    $hashHex = -join ($hashBytes | ForEach-Object { $_.ToString('x2') })

    return [ordered]@{
        id         = "$OwnerId"
        first_name = 'Konstantin'
        username   = 'immanuel_1618'
        auth_date  = "$AuthDate"
        hash       = $hashHex
    }
}

# ── HTTP helper: PowerShell 5.1's Invoke-WebRequest throws System.Net.WebException on 4xx/5xx,
#    so pull the status + body back out of the exception instead of losing them. ────────────────────

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
            $resp = Invoke-WebRequest -Uri $uri -Method $Method -Headers $headers -Body $Body -ContentType 'application/json; charset=utf-8' -UseBasicParsing -TimeoutSec 25
        }
        else {
            $resp = Invoke-WebRequest -Uri $uri -Method $Method -Headers $headers -UseBasicParsing -TimeoutSec 25
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

$botToken = Read-BotToken
$ownerId = Read-OwnerId
if (-not $botToken -or -not $ownerId) {
    Write-Host "FAIL setup: could not read bot token / owner id from H:\secrets"
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
    if (-not (Test-Path $appJsonPath)) {
        Write-Host "FAIL setup: app.json not found at $appJsonPath"
        exit 2
    }
    $script:ApiToken = (Get-Content -Raw -Path $appJsonPath | ConvertFrom-Json).apiToken
    if (-not $script:ApiToken) {
        Write-Host "FAIL setup: could not read apiToken from app.json"
        exit 2
    }

    # ── 1. login via signed widget callback ──────────────────────────────────────────────────────
    $authDate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $payload = New-SignedWidgetPayload -BotToken $botToken -OwnerId $ownerId -AuthDate $authDate
    $query = (($payload.Keys) | ForEach-Object { "$_=$([Uri]::EscapeDataString([string]$payload[$_]))" }) -join '&'

    $r = Invoke-Api -Method GET -Path "/planner/callback?$query"
    if ($r.Status -eq 200 -and $r.Text -match 'Вход выполнен') {
        Record 'callback-login' 'PASS' 'HTTP 200, success page'
    }
    else {
        Record 'callback-login' 'FAIL' "status=$($r.Status) body=$($r.Text)"
    }

    # ── 2. status ────────────────────────────────────────────────────────────────────────────────
    $r = Invoke-Api -Method GET -Path '/planner/status'
    if ($r.Status -eq 200 -and $r.Json -and $r.Json.loggedIn -eq $true) {
        Record 'status-logged-in' 'PASS'
    }
    else {
        Record 'status-logged-in' 'FAIL' "status=$($r.Status) body=$($r.Text)"
    }
    $quotaBefore = 0
    if ($r.Json -and $r.Json.quota -and $null -ne $r.Json.quota.used) { $quotaBefore = [int]$r.Json.quota.used }

    # ── 3. today shape ───────────────────────────────────────────────────────────────────────────
    $r = Invoke-Api -Method GET -Path '/planner/today'
    $keys = @()
    if ($r.Json) { $keys = $r.Json.PSObject.Properties.Name }
    $hasKeys = ('tasks' -in $keys) -and ('meetings' -in $keys) -and ('habits' -in $keys) -and ('money' -in $keys) -and ('briefing' -in $keys)
    $briefingOk = $r.Json -and $r.Json.briefing -and ([string]$r.Json.briefing).Trim().Length -gt 0
    if ($r.Status -eq 200 -and $hasKeys -and $briefingOk) {
        Record 'today-shape' 'PASS' "briefing: $($r.Json.briefing)"
    }
    else {
        Record 'today-shape' 'FAIL' "status=$($r.Status) body=$($r.Text)"
    }

    # ── 4. capture (text) — creates one throwaway entry; the free-text AI parser may rewrite the
    #    title, so cleanup below uses the batch id (rpc/undo_batch, self-scoped) rather than title
    #    matching. The "NNA WALLPAPER TEST" prefix is still sent, and /planner/test-delete?id= still
    #    supports title-prefix cleanup as a fallback for whoever calls it that way. ───────────────────
    $testEntryId = $null
    $testBatchId = $null
    $captureBody = (@{ text = 'NNA WALLPAPER TEST задача' } | ConvertTo-Json -Compress)
    $r = Invoke-Api -Method POST -Path '/planner/capture' -Body $captureBody -Auth
    if ($r.Status -eq 429) {
        Record 'capture-text' 'SKIP' 'daily AI quota exhausted (429)'
    }
    elseif ($r.Status -eq 200 -and $r.Json -and $r.Json.ok -eq $true -and $r.Json.entries -and @($r.Json.entries).Count -gt 0) {
        $testEntryId = $r.Json.entries[0].id
        $testBatchId = $r.Json.batch_id
        Record 'capture-text' 'PASS' "created id=$testEntryId title=$($r.Json.entries[0].title)"
    }
    else {
        Record 'capture-text' 'FAIL' "status=$($r.Status) body=$($r.Text)"
    }

    # ── 5. done + verify it drops out of /planner/today ─────────────────────────────────────────────
    if ($testEntryId) {
        $r = Invoke-Api -Method POST -Path "/planner/done?id=$testEntryId&done=1" -Auth
        if ($r.Status -eq 200 -and $r.Json -and $r.Json.ok -eq $true) { Record 'done' 'PASS' }
        else { Record 'done' 'FAIL' "status=$($r.Status) body=$($r.Text)" }

        $r2 = Invoke-Api -Method GET -Path '/planner/today'
        $stillOpen = $false
        if ($r2.Json -and $r2.Json.tasks) {
            foreach ($t in @($r2.Json.tasks)) { if ($t.id -eq $testEntryId -and $t.state -ne 'done') { $stillOpen = $true } }
        }
        if (-not $stillOpen) { Record 'done-reflected-in-today' 'PASS' }
        else { Record 'done-reflected-in-today' 'FAIL' 'entry still listed as open in /planner/today' }
    }
    else {
        Record 'done' 'SKIP' 'no test entry (capture skipped)'
        Record 'done-reflected-in-today' 'SKIP' 'no test entry'
    }

    # ── 6. cleanup: undo the whole capture batch (rpc/undo_batch, self-scoped to our own profile —
    #    safe regardless of what title the AI parser assigned) ───────────────────────────────────────
    if ($testEntryId) {
        $r = Invoke-Api -Method POST -Path "/planner/test-delete?batch=$testBatchId" -Auth
        if ($r.Status -eq 200 -and $r.Json -and $r.Json.ok -eq $true) { Record 'test-delete' 'PASS' }
        else { Record 'test-delete' 'FAIL' "status=$($r.Status) body=$($r.Text)" }

        $r3 = Invoke-Api -Method GET -Path '/planner/today'
        $stillThere = $false
        if ($r3.Json -and $r3.Json.tasks) {
            foreach ($t in @($r3.Json.tasks)) { if ($t.id -eq $testEntryId) { $stillThere = $true } }
        }
        if (-not $stillThere) { Record 'cleanup-verified' 'PASS' }
        else { Record 'cleanup-verified' 'FAIL' 'entry still present after test-delete' }
    }
    else {
        Record 'test-delete' 'SKIP' 'no test entry'
        Record 'cleanup-verified' 'SKIP' 'no test entry'
    }

    # ── 7. capture (voice) — 2s of silence; must not come back as a provider error ──────────────────
    $fixture = Join-Path $PSScriptRoot 'fixtures\silence.webm'
    if (-not (Test-Path $fixture)) {
        Record 'capture-voice' 'FAIL' "fixture not found: $fixture"
    }
    else {
        $audioB64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($fixture))
        $voiceBody = (@{ audio_base64 = $audioB64; mime = 'audio/webm' } | ConvertTo-Json -Compress)
        $r = Invoke-Api -Method POST -Path '/planner/capture' -Body $voiceBody -Auth
        if ($r.Status -eq 503) {
            Record 'capture-voice' 'FAIL' "503 provider-error: $($r.Text)"
        }
        elseif ($r.Status -eq 429) {
            Record 'capture-voice' 'SKIP' 'daily AI quota exhausted (429)'
        }
        elseif ($r.Status -eq 200 -and $r.Json -and (
                (($r.Json.ok -eq $false) -and ($r.Json.reason -in @('empty', 'nothing-parsed'))) -or
                (($r.Json.ok -eq $true) -and (@($r.Json.entries).Count -eq 0))
            )) {
            Record 'capture-voice' 'PASS' "status=200 ok=$($r.Json.ok) reason=$($r.Json.reason)"
        }
        else {
            Record 'capture-voice' 'FAIL' "status=$($r.Status) body=$($r.Text)"
        }
    }

    # ── 8. quota regression: at most 2 AI calls happened above (text + voice) ───────────────────────
    $r = Invoke-Api -Method GET -Path '/planner/status'
    $quotaAfter = $quotaBefore
    if ($r.Json -and $r.Json.quota -and $null -ne $r.Json.quota.used) { $quotaAfter = [int]$r.Json.quota.used }
    if ($quotaAfter -le ($quotaBefore + 2)) { Record 'quota-bound' 'PASS' "used $quotaBefore -> $quotaAfter" }
    else { Record 'quota-bound' 'FAIL' "used $quotaBefore -> $quotaAfter (grew by more than 2)" }

    # ── 9. negative: logout then /planner/today must 401 ────────────────────────────────────────────
    $r = Invoke-Api -Method POST -Path '/planner/logout' -Auth
    if ($r.Status -eq 200 -and $r.Json -and $r.Json.ok -eq $true) { Record 'logout' 'PASS' }
    else { Record 'logout' 'FAIL' "status=$($r.Status) body=$($r.Text)" }

    $r = Invoke-Api -Method GET -Path '/planner/today'
    if ($r.Status -eq 401) { Record 'today-after-logout' 'PASS' }
    else { Record 'today-after-logout' 'FAIL' "status=$($r.Status) body=$($r.Text)" }
}
finally {
    if ($proc -and -not $proc.HasExited) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
}

Write-Host ''
Write-Host "RESULT: $script:passed passed, $script:failed failed, $script:skipped skipped"
if ($script:failed -gt 0) { exit 1 } else { exit 0 }
