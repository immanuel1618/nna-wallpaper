<#
.SYNOPSIS
  End-to-end gate for the brand cursor scheme (CursorService): status, apply mark/line, backup
  create-once semantics, full-snapshot reset, and the token gate on the write routes.

.DESCRIPTION
  Starts the NNA Wallpaper host headless on -Port, drives GET /cursor/status and POST
  /cursor/apply|reset, and checks the live per-user registry key HKCU\Control Panel\Cursors
  alongside the host's own JSON responses. That registry key is shared with whatever cursor
  scheme is actually active on this machine (e.g. the owner's own instance on :1618) — applying
  "mark"/"line" here really does change the visible mouse cursor for a few seconds. This is
  accepted: the script captures the pre-test Arrow value up front and *always* runs
  POST /cursor/reset in a finally block, even if an assertion above throws, so the machine is
  left exactly as found.

.USAGE
  powershell -File tests\cursor-probe.ps1 -Port 1626 [-Data <tmp-dir>] [-Exe <path>]
#>
[CmdletBinding()]
param(
    [int]$Port = 1626,
    [string]$Data = (Join-Path $env:TEMP "nna-cursor-probe-$Port"),
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

# ── registry helpers: read the live HKCU\Control Panel\Cursors key the same way CursorService
#    does (kind-aware), so the reset-vs-backup comparison is a real apples-to-apples check. ──────

$CursorsRegPath = 'HKCU:\Control Panel\Cursors'

function Get-CursorRegSnapshot {
    $key = Get-Item -Path $CursorsRegPath
    $map = @{}
    foreach ($rawName in $key.GetValueNames()) {
        $kind = $key.GetValueKind($rawName)
        $value = $key.GetValue($rawName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $name = if ($rawName -eq '') { '(Default)' } else { $rawName }
        $map[$name] = [PSCustomObject]@{ Kind = $kind.ToString(); Value = $value }
    }
    return $map
}

function Compare-CursorRegSnapshot {
    param($Before, $After)
    $names = @($Before.Keys) + @($After.Keys) | Select-Object -Unique
    $diffs = @()
    foreach ($n in $names) {
        $b = $Before[$n]
        $a = $After[$n]
        if (-not $b) { $diffs += "extra now: $n"; continue }
        if (-not $a) { $diffs += "missing now: $n"; continue }
        if ($b.Kind -ne $a.Kind) { $diffs += "$n kind $($b.Kind) -> $($a.Kind)"; continue }
        $bv = if ($b.Value -is [array]) { ($b.Value -join ',') } else { "$($b.Value)" }
        $av = if ($a.Value -is [array]) { ($a.Value -join ',') } else { "$($a.Value)" }
        if ($bv -ne $av) { $diffs += "$n value '$bv' -> '$av'" }
    }
    return $diffs
}

# ── setup ─────────────────────────────────────────────────────────────────────────────────────

$exePath = (Resolve-Path $Exe -ErrorAction SilentlyContinue)
if (-not $exePath) { Write-Host "FAIL setup: exe not found: $Exe"; exit 2 }
$exePath = $exePath.Path

if (Test-Path $Data) { Remove-Item -Recurse -Force $Data -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $Data | Out-Null

# Capture the machine's cursor state *before* this probe touches anything, so we can prove it is
# unchanged at the very end (and for the reviewer's own manual `reg query` check afterwards).
$beforeSnapshot = if (Test-Path $CursorsRegPath) { Get-CursorRegSnapshot } else { @{} }
$beforeArrow = $beforeSnapshot['Arrow'].Value
Write-Host "BEFORE Arrow = $beforeArrow"

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

    $backupFile = Join-Path $Data 'cursors-backup.json'

    # ── 1. GET /cursor/status — 3 variants, 13 files each, no backup yet ────────────────────────
    $r = Invoke-Api -Method GET -Path '/cursor/status'
    $variants = @()
    if ($r.Json -and $r.Json.variants) { $variants = @($r.Json.variants) }
    $allThirteen = ($variants.Count -gt 0) -and (@($variants | Where-Object { $_.files -ne 13 }).Count -eq 0)
    if ($r.Status -eq 200 -and $variants.Count -eq 3 -and $allThirteen -and $r.Json.active -eq $null -and $r.Json.backup -eq $false) {
        Record 'status-initial' 'PASS' "variants=$(($variants | ForEach-Object { $_.id }) -join ',')"
    } else {
        Record 'status-initial' 'FAIL' "status=$($r.Status) body=$($r.Text)"
    }

    # ── 2. POST /cursor/apply {variant:"mark"} ───────────────────────────────────────────────────
    $r = Invoke-Api -Method POST -Path '/cursor/apply' -Body (@{ variant = 'mark' } | ConvertTo-Json -Compress) -Auth
    if ($r.Status -eq 200 -and $r.Json -and $r.Json.ok -eq $true -and $r.Json.active -eq 'mark' -and $r.Json.applied -eq 13) {
        Record 'apply-mark' 'PASS' "applied=$($r.Json.applied)"
    } else {
        Record 'apply-mark' 'FAIL' "status=$($r.Status) body=$($r.Text)"
    }

    $regAfterMark = (Get-ItemProperty -Path $CursorsRegPath -Name 'Arrow' -ErrorAction SilentlyContinue).Arrow
    $expectMarkFile = Join-Path $Data 'cursors\mark\arrow.cur'
    if ($regAfterMark -and ($regAfterMark -like "*\cursors\mark\arrow.cur") -and (Test-Path $expectMarkFile)) {
        Record 'registry-arrow-mark' 'PASS' $regAfterMark
    } else {
        Record 'registry-arrow-mark' 'FAIL' "Arrow=$regAfterMark fileExists=$(Test-Path $expectMarkFile)"
    }

    if (Test-Path $backupFile) {
        Record 'backup-created' 'PASS' $backupFile
        $backupTime1 = (Get-Item $backupFile).LastWriteTimeUtc
    } else {
        Record 'backup-created' 'FAIL' 'no backup file after first apply'
        $backupTime1 = $null
    }
    # Snapshot of what the backup actually captured — this is what /cursor/reset must restore to.
    $backupSnapshotForReset = if (Test-Path $backupFile) {
        $raw = Get-Content -Raw -Path $backupFile -Encoding UTF8 | ConvertFrom-Json
        $map = @{}
        foreach ($p in $raw.PSObject.Properties) { $map[$p.Name] = [PSCustomObject]@{ Kind = $p.Value.kind; Value = $p.Value.value } }
        $map
    } else { @{} }

    # ── 3. GET /cursor/status.active == mark ─────────────────────────────────────────────────────
    $r = Invoke-Api -Method GET -Path '/cursor/status'
    if ($r.Status -eq 200 -and $r.Json -and $r.Json.active -eq 'mark' -and $r.Json.backup -eq $true) {
        Record 'status-active-mark' 'PASS'
    } else {
        Record 'status-active-mark' 'FAIL' "status=$($r.Status) body=$($r.Text)"
    }

    # ── 4. POST /cursor/apply {variant:"line"} — Arrow moves to line, backup is NOT rewritten ────
    Start-Sleep -Milliseconds 50  # make sure a would-be rewrite would produce a detectably later mtime
    $r = Invoke-Api -Method POST -Path '/cursor/apply' -Body (@{ variant = 'line' } | ConvertTo-Json -Compress) -Auth
    if ($r.Status -eq 200 -and $r.Json -and $r.Json.ok -eq $true -and $r.Json.active -eq 'line' -and $r.Json.applied -eq 13) {
        Record 'apply-line' 'PASS' "applied=$($r.Json.applied)"
    } else {
        Record 'apply-line' 'FAIL' "status=$($r.Status) body=$($r.Text)"
    }

    $regAfterLine = (Get-ItemProperty -Path $CursorsRegPath -Name 'Arrow' -ErrorAction SilentlyContinue).Arrow
    if ($regAfterLine -and ($regAfterLine -like "*\cursors\line\arrow.cur")) {
        Record 'registry-arrow-line' 'PASS' $regAfterLine
    } else {
        Record 'registry-arrow-line' 'FAIL' "Arrow=$regAfterLine"
    }

    if ((Test-Path $backupFile) -and $backupTime1 -and ((Get-Item $backupFile).LastWriteTimeUtc -eq $backupTime1)) {
        Record 'backup-not-overwritten' 'PASS'
    } else {
        Record 'backup-not-overwritten' 'FAIL' "mtime before=$backupTime1 after=$((Get-Item $backupFile -ErrorAction SilentlyContinue).LastWriteTimeUtc)"
    }

    # ── 5. POST /cursor/reset — registry matches the backup snapshot exactly, active becomes null ─
    $r = Invoke-Api -Method POST -Path '/cursor/reset' -Auth
    if ($r.Status -eq 200 -and $r.Json -and $r.Json.ok -eq $true) {
        Record 'reset' 'PASS' "restored=$($r.Json.restored)"
    } else {
        Record 'reset' 'FAIL' "status=$($r.Status) body=$($r.Text)"
    }

    $afterResetSnapshot = Get-CursorRegSnapshot
    $diffs = Compare-CursorRegSnapshot -Before $backupSnapshotForReset -After $afterResetSnapshot
    if ($diffs.Count -eq 0) {
        Record 'reset-matches-backup' 'PASS' "$($backupSnapshotForReset.Count) keys compared"
    } else {
        Record 'reset-matches-backup' 'FAIL' ($diffs -join '; ')
    }

    $r = Invoke-Api -Method GET -Path '/cursor/status'
    if ($r.Status -eq 200 -and $r.Json -and $r.Json.active -eq $null -and $r.Json.backup -eq $false) {
        Record 'status-active-null-after-reset' 'PASS'
    } else {
        Record 'status-active-null-after-reset' 'FAIL' "status=$($r.Status) body=$($r.Text)"
    }

    # ── 6. reset again with no backup left -> ok:false ───────────────────────────────────────────
    $r = Invoke-Api -Method POST -Path '/cursor/reset' -Auth
    if ($r.Status -eq 200 -and $r.Json -and $r.Json.ok -eq $false -and $r.Json.error -eq 'no backup') {
        Record 'reset-again-no-backup' 'PASS'
    } else {
        Record 'reset-again-no-backup' 'FAIL' "status=$($r.Status) body=$($r.Text)"
    }

    # ── 7. POST without token -> 401/403 ─────────────────────────────────────────────────────────
    $r = Invoke-Api -Method POST -Path '/cursor/apply' -Body (@{ variant = 'mark' } | ConvertTo-Json -Compress)
    if ($r.Status -eq 401 -or $r.Status -eq 403) {
        Record 'apply-no-token' 'PASS' "status=$($r.Status)"
    } else {
        Record 'apply-no-token' 'FAIL' "status=$($r.Status) body=$($r.Text)"
    }

    $r = Invoke-Api -Method PUT -Path '/cursor/apply' -Body (@{ variant = 'mark' } | ConvertTo-Json -Compress)
    if ($r.Status -eq 401 -or $r.Status -eq 403) {
        Record 'put-no-token' 'PASS' "status=$($r.Status)"
    } else {
        Record 'put-no-token' 'FAIL' "status=$($r.Status) body=$($r.Text)"
    }
}
finally {
    # Always put the live registry back, even if an assertion above threw mid-test — this key is
    # shared with whatever cursor scheme is actually active on the machine right now.
    try {
        if ($script:BaseUrl -and $script:ApiToken) {
            Invoke-Api -Method POST -Path '/cursor/reset' -Auth | Out-Null
        }
    } catch { }

    if ($proc -and -not $proc.HasExited) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
    }

    $afterArrow = if (Test-Path $CursorsRegPath) { (Get-ItemProperty -Path $CursorsRegPath -Name 'Arrow' -ErrorAction SilentlyContinue).Arrow } else { $null }
    Write-Host "AFTER  Arrow = $afterArrow"
    if ("$beforeArrow" -eq "$afterArrow") {
        Record 'registry-restored-to-pretest-state' 'PASS'
    } else {
        Record 'registry-restored-to-pretest-state' 'FAIL' "before='$beforeArrow' after='$afterArrow'"
    }
}

Write-Host ''
Write-Host "RESULT: $script:passed passed, $script:failed failed"
if ($script:failed -gt 0) { exit 1 } else { exit 0 }
