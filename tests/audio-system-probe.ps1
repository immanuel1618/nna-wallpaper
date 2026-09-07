<#
.SYNOPSIS
  Probe for the NNA Wallpaper host's audio-control and system-info API (stage 8A: /audio/*, /system/*).

.DESCRIPTION
  Read-only smoke test against a running host (headless or engine). Exercises every GET route,
  round-trips /audio/volume by +/-1 and restores the original value (muted is never touched),
  and checks the negative cases (missing token, foreign Origin, POST /system/power without
  confirm). Device switching (PUT /audio/output) and monitor brightness (PUT /system/brightness)
  are read-only here on purpose — those touch the owner's live speakers/monitors, so only GET is
  exercised; PUT is expected to be verified by hand against a real desktop.

.PARAMETER Port
  Port the NNA Wallpaper host is listening on.

.EXAMPLE
  powershell -File tests\audio-system-probe.ps1 -Port 1624
#>
param(
    [Parameter(Mandatory = $true)]
    [int]$Port
)

$ErrorActionPreference = 'Stop'
$Base = "http://127.0.0.1:$Port"

$script:PassCount = 0
$script:FailCount = 0

function Write-Pass([string]$Name, [string]$Detail = '') {
    $script:PassCount++
    if ($Detail) { Write-Output "PASS ${Name}: $Detail" } else { Write-Output "PASS $Name" }
}

function Write-Fail([string]$Name, [string]$Why) {
    $script:FailCount++
    Write-Output "FAIL ${Name}: $Why"
}

# GET/PUT/POST helper: never throws on a non-2xx status — returns it so callers can assert on it.
function Invoke-Api {
    param(
        [string]$Path,
        [string]$Method = 'GET',
        [string]$Token = $null,
        $Body = $null,
        [hashtable]$ExtraHeaders = @{}
    )
    $uri = $Base + $Path
    $headers = @{} + $ExtraHeaders
    if ($Token) { $headers['X-Token'] = $Token }
    $params = @{ Uri = $uri; Method = $Method; Headers = $headers; TimeoutSec = 10; UseBasicParsing = $true }
    if ($null -ne $Body) {
        $params.Body = ($Body | ConvertTo-Json -Compress)
        $params.ContentType = 'application/json'
    }
    try {
        $resp = Invoke-WebRequest @params -ErrorAction Stop
        $json = $null
        try { $json = $resp.Content | ConvertFrom-Json -ErrorAction Stop } catch { $json = $null }
        return [PSCustomObject]@{ Reachable = $true; Status = [int]$resp.StatusCode; Body = $json }
    }
    catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($null -ne $r) {
            $status = [int]$r.StatusCode
            $text = ''
            try {
                $stream = $r.GetResponseStream()
                $reader = New-Object System.IO.StreamReader($stream)
                $text = $reader.ReadToEnd()
            } catch { }
            $json = $null
            try { $json = $text | ConvertFrom-Json -ErrorAction Stop } catch { $json = $null }
            return [PSCustomObject]@{ Reachable = $true; Status = $status; Body = $json }
        }
        return [PSCustomObject]@{ Reachable = $false; Status = -1; Body = $null }
    }
    catch {
        return [PSCustomObject]@{ Reachable = $false; Status = -1; Body = $null }
    }
}

# --- token -------------------------------------------------------------------------------------

$health = Invoke-Api -Path '/health'
if (-not $health.Reachable -or $health.Status -ne 200) {
    Write-Fail 'GET /health' 'host unreachable — is it running on this port?'
    Write-Output "RESULT: $script:PassCount passed, $script:FailCount failed"
    exit 1
}
Write-Pass 'GET /health'

$config = Invoke-Api -Path '/config'
$token = $null
if ($config.Reachable -and $config.Status -eq 200) { $token = $config.Body.token }
if (-not $token) {
    Write-Fail 'GET /config token' 'could not read API token — PUT/POST checks will be skipped'
}

# --- GET /audio/volume + PUT round trip (mute untouched) ---------------------------------------

$vol = Invoke-Api -Path '/audio/volume'
if ($vol.Reachable -and $vol.Status -eq 200 -and $null -ne $vol.Body.volume) {
    Write-Pass 'GET /audio/volume' "volume=$($vol.Body.volume) muted=$($vol.Body.muted) device=$($vol.Body.device.name)"
}
else {
    Write-Fail 'GET /audio/volume' "status=$($vol.Status)"
}

if ($token -and $vol.Reachable -and $vol.Status -eq 200) {
    $original = [int]$vol.Body.volume
    $bumped = if ($original -ge 99) { $original - 1 } else { $original + 1 }
    $up = Invoke-Api -Path '/audio/volume' -Method 'PUT' -Token $token -Body @{ volume = $bumped }
    $restored = Invoke-Api -Path '/audio/volume' -Method 'PUT' -Token $token -Body @{ volume = $original }
    if ($up.Reachable -and $up.Status -eq 200 -and [int]$up.Body.volume -eq $bumped -and
        $restored.Reachable -and $restored.Status -eq 200 -and [int]$restored.Body.volume -eq $original) {
        Write-Pass 'PUT /audio/volume round trip' "$original -> $bumped -> $($restored.Body.volume)"
    }
    else {
        Write-Fail 'PUT /audio/volume round trip' "up.status=$($up.Status) up.volume=$($up.Body.volume) restored.status=$($restored.Status) restored.volume=$($restored.Body.volume)"
    }
}
else {
    Write-Output "SKIP PUT /audio/volume round trip: no token or GET failed"
}

# --- GET /audio/outputs ------------------------------------------------------------------------

$outputs = Invoke-Api -Path '/audio/outputs'
$outputDevices = @($outputs.Body.devices)
$outputDefaults = @($outputDevices | Where-Object { $_.default -eq $true })
if ($outputs.Reachable -and $outputs.Status -eq 200 -and $outputDevices.Count -ge 1 -and $outputDefaults.Count -ge 1) {
    Write-Pass 'GET /audio/outputs' "$($outputDevices.Count) device(s), one default"
}
else {
    Write-Fail 'GET /audio/outputs' "status=$($outputs.Status) count=$($outputDevices.Count) defaults=$($outputDefaults.Count)"
}

# --- GET /audio/sessions (array, possibly empty) ------------------------------------------------

$sessions = Invoke-Api -Path '/audio/sessions'
if ($sessions.Reachable -and $sessions.Status -eq 200 -and $null -ne $sessions.Body.sessions) {
    Write-Pass 'GET /audio/sessions' "$(@($sessions.Body.sessions).Count) session(s)"
}
else {
    Write-Fail 'GET /audio/sessions' "status=$($sessions.Status)"
}

# --- GET /audio/mic ------------------------------------------------------------------------------

$mic = Invoke-Api -Path '/audio/mic'
if ($mic.Reachable -and $mic.Status -eq 200 -and $null -ne $mic.Body.volume) {
    Write-Pass 'GET /audio/mic' "volume=$($mic.Body.volume) muted=$($mic.Body.muted) device=$($mic.Body.device.name)"
}
else {
    Write-Fail 'GET /audio/mic' "status=$($mic.Status)"
}

# --- GET /system/network --------------------------------------------------------------------------

$net = Invoke-Api -Path '/system/network'
if ($net.Reachable -and $net.Status -eq 200 -and $net.Body.up -eq $true) {
    Write-Pass 'GET /system/network' "up=$($net.Body.up) kind=$($net.Body.kind) ipv4=$($net.Body.ipv4)"
}
else {
    Write-Fail 'GET /system/network' "status=$($net.Status) up=$($net.Body.up)"
}

# --- GET /system/battery ----------------------------------------------------------------------

$batt = Invoke-Api -Path '/system/battery'
if ($batt.Reachable -and $batt.Status -eq 200 -and ($batt.Body.present -is [bool])) {
    Write-Pass 'GET /system/battery' "present=$($batt.Body.present) percent=$($batt.Body.percent)"
}
else {
    Write-Fail 'GET /system/battery' "status=$($batt.Status)"
}

# --- GET /system/layout -----------------------------------------------------------------------

$layout = Invoke-Api -Path '/system/layout'
if ($layout.Reachable -and $layout.Status -eq 200 -and $layout.Body.lang -and $layout.Body.lang.Length -eq 2) {
    Write-Pass 'GET /system/layout' "lang=$($layout.Body.lang) hkl=$($layout.Body.hkl)"
}
else {
    Write-Fail 'GET /system/layout' "status=$($layout.Status) lang=$($layout.Body.lang)"
}

# --- GET /system/brightness (read-only; PUT is not exercised here) -----------------------------

$bright = Invoke-Api -Path '/system/brightness'
if ($bright.Reachable -and $bright.Status -eq 200 -and ($bright.Body.supported -is [bool])) {
    Write-Pass 'GET /system/brightness' "supported=$($bright.Body.supported) monitors=$(@($bright.Body.monitors).Count)"
}
else {
    Write-Fail 'GET /system/brightness' "status=$($bright.Status)"
}

# --- GET /system/dnd -----------------------------------------------------------------------------

$dnd = Invoke-Api -Path '/system/dnd'
if ($dnd.Reachable -and $dnd.Status -eq 200 -and ($dnd.Body.supported -is [bool])) {
    Write-Pass 'GET /system/dnd' "supported=$($dnd.Body.supported)"
}
else {
    Write-Fail 'GET /system/dnd' "status=$($dnd.Status)"
}

# --- POST /system/power restart without confirm -> 400 -----------------------------------------

if ($token) {
    $restart = Invoke-Api -Path '/system/power' -Method 'POST' -Token $token -Body @{ action = 'restart' }
    if ($restart.Reachable -and $restart.Status -eq 400) {
        Write-Pass 'POST /system/power restart (no confirm)' 'got 400 as expected'
    }
    else {
        Write-Fail 'POST /system/power restart (no confirm)' "expected 400, got $($restart.Status)"
    }
}
else {
    Write-Output "SKIP POST /system/power restart (no confirm): no token"
}

# --- negative: PUT without token -----------------------------------------------------------------

$noToken = Invoke-Api -Path '/audio/volume' -Method 'PUT' -Body @{ volume = 50 }
if ($noToken.Reachable -and ($noToken.Status -eq 401 -or $noToken.Status -eq 403)) {
    Write-Pass 'PUT /audio/volume (no token)' "status=$($noToken.Status)"
}
else {
    Write-Fail 'PUT /audio/volume (no token)' "expected 401/403, got $($noToken.Status)"
}

# --- negative: foreign Origin ---------------------------------------------------------------------

$foreignOrigin = Invoke-Api -Path '/audio/volume' -ExtraHeaders @{ Origin = 'http://evil.example' }
if ($foreignOrigin.Reachable -and $foreignOrigin.Status -eq 403) {
    Write-Pass 'GET /audio/volume (foreign Origin)' 'status=403'
}
else {
    Write-Fail 'GET /audio/volume (foreign Origin)' "expected 403, got $($foreignOrigin.Status)"
}

# --- summary ---------------------------------------------------------------------------------------

Write-Output "RESULT: $script:PassCount passed, $script:FailCount failed"
if ($script:FailCount -eq 0) { exit 0 } else { exit 1 }
