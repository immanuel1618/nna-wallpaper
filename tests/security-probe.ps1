<#
.SYNOPSIS
  Security gate for the local API's same-origin defenses (verdict #2 fixes): the API token only
  ever reaches a same-origin request, sensitive GETs refuse a cross-site Sec-Fetch-Site, every
  response carries X-Content-Type-Options: nosniff, opening the planner login window requires the
  API token, and /planner/callback refuses a request without a valid one-time state.

.DESCRIPTION
  Runs against an already-running --headless instance (same convention as api-contract.ps1 /
  no-reload-probe.ps1) — this script does not start or stop the host itself.

    - GET /config with Sec-Fetch-Site: cross-site        -> body has no "token" key
    - GET /config with no Sec-Fetch-Site/Origin/Referer    -> body has a non-empty "token"
    - GET /windows with Sec-Fetch-Site: cross-site         -> 403
    - GET /planner/login                                   -> 404 or 405 (POST-only now)
    - GET /planner/callback?hash=x (no state)               -> 403
    - every response above carries X-Content-Type-Options: nosniff
    - POST /planner/login with no X-Token                   -> 401 or 403

.PARAMETER Port
  Port the NNA Wallpaper host is listening on (a --headless test instance, never 1618).

.EXAMPLE
  powershell -File tests\security-probe.ps1 -Port 1637
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [int]$Port = 1637
)

$ErrorActionPreference = 'Stop'
$Base = "http://127.0.0.1:$Port"
$script:PassCount = 0
$script:FailCount = 0

function Write-Pass([string]$Name, [string]$Detail = '') {
    $script:PassCount++
    Write-Output ("PASS " + $Name + $(if ($Detail) { ": $Detail" } else { '' }))
}
function Write-Fail([string]$Name, [string]$Why) {
    $script:FailCount++
    Write-Output "FAIL ${Name}: $Why"
}

# Performs a request and returns @{ Status; Body (parsed JSON or $null); Headers (name -> value) }.
# PowerShell 5.1's Invoke-WebRequest throws on 4xx/5xx, so both branches pull the same three things
# back out — from the normal response object on success, from the WebException's response on error.
function Invoke-Probe {
    param(
        [string]$Path,
        [string]$Method = 'GET',
        [hashtable]$Headers = @{}
    )
    $uri = "$Base$Path"
    try {
        $resp = Invoke-WebRequest -Uri $uri -Method $Method -Headers $Headers -UseBasicParsing -TimeoutSec 8 -ErrorAction Stop
        $body = $null
        try { $body = $resp.Content | ConvertFrom-Json -ErrorAction Stop } catch { $body = $null }
        $hdrs = @{}
        foreach ($k in $resp.Headers.Keys) { $hdrs[$k] = $resp.Headers[$k] }
        return [PSCustomObject]@{ Status = [int]$resp.StatusCode; Body = $body; Headers = $hdrs }
    }
    catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($null -eq $r) { return [PSCustomObject]@{ Status = -1; Body = $null; Headers = @{} } }
        $status = [int]$r.StatusCode
        $text = ''
        try {
            $stream = $r.GetResponseStream()
            $reader = New-Object System.IO.StreamReader($stream)
            $text = $reader.ReadToEnd()
        } catch { }
        $body = $null
        try { $body = $text | ConvertFrom-Json -ErrorAction Stop } catch { $body = $null }
        $hdrs = @{}
        foreach ($k in $r.Headers.AllKeys) { $hdrs[$k] = $r.Headers[$k] }
        return [PSCustomObject]@{ Status = $status; Body = $body; Headers = $hdrs }
    }
}

function Test-Nosniff($res, [string]$Name) {
    $v = $res.Headers['X-Content-Type-Options']
    if ($v -eq 'nosniff') { Write-Pass "nosniff $Name" }
    else { Write-Fail "nosniff $Name" "X-Content-Type-Options='$v'" }
}

# ── 1. GET /config, cross-site -> no token ───────────────────────────────────────────────────────

$crossConfig = Invoke-Probe -Path '/config' -Headers @{ 'Sec-Fetch-Site' = 'cross-site' }
if ($crossConfig.Status -eq 200 -and $null -ne $crossConfig.Body -and -not ($crossConfig.Body.PSObject.Properties.Name -contains 'token')) {
    Write-Pass 'config cross-site: no token field'
} else {
    $tok = if ($null -ne $crossConfig.Body -and ($crossConfig.Body.PSObject.Properties.Name -contains 'token')) { $crossConfig.Body.token } else { '<absent>' }
    Write-Fail 'config cross-site: no token field' "status=$($crossConfig.Status) token=$tok"
}
Test-Nosniff $crossConfig 'config cross-site'

# ── 2. GET /config, no Sec-Fetch-Site/Origin/Referer (curl-like) -> token present ────────────────

$sameConfig = Invoke-Probe -Path '/config'
if ($sameConfig.Status -eq 200 -and $null -ne $sameConfig.Body -and ($sameConfig.Body.PSObject.Properties.Name -contains 'token') -and -not [string]::IsNullOrEmpty($sameConfig.Body.token)) {
    Write-Pass 'config no-header: token present'
} else {
    Write-Fail 'config no-header: token present' "status=$($sameConfig.Status) body=$($sameConfig.Body | ConvertTo-Json -Compress -Depth 3)"
}
Test-Nosniff $sameConfig 'config no-header'

# ── 3. GET /windows, cross-site -> 403 ───────────────────────────────────────────────────────────

$crossWindows = Invoke-Probe -Path '/windows' -Headers @{ 'Sec-Fetch-Site' = 'cross-site' }
if ($crossWindows.Status -eq 403) { Write-Pass 'windows cross-site: 403' }
else { Write-Fail 'windows cross-site: 403' "status=$($crossWindows.Status)" }
Test-Nosniff $crossWindows 'windows cross-site'

# ── 4. GET /planner/login -> 404/405 (route is POST-only now) ───────────────────────────────────

$getLogin = Invoke-Probe -Path '/planner/login'
if ($getLogin.Status -in 404, 405) { Write-Pass "planner GET /planner/login: $($getLogin.Status)" }
else { Write-Fail 'planner GET /planner/login: 404/405' "status=$($getLogin.Status)" }

# ── 5. GET /planner/callback?hash=x, no state -> 403 ─────────────────────────────────────────────

$callbackNoState = Invoke-Probe -Path '/planner/callback?hash=x'
if ($callbackNoState.Status -eq 403) { Write-Pass 'planner callback no state: 403' }
else { Write-Fail 'planner callback no state: 403' "status=$($callbackNoState.Status)" }
Test-Nosniff $callbackNoState 'planner callback no state'

# ── 6. POST /planner/login, no token -> 401/403 ──────────────────────────────────────────────────

$postLoginNoToken = Invoke-Probe -Path '/planner/login' -Method 'POST'
if ($postLoginNoToken.Status -in 401, 403) { Write-Pass "planner POST /planner/login no token: $($postLoginNoToken.Status)" }
else { Write-Fail 'planner POST /planner/login no token: 401/403' "status=$($postLoginNoToken.Status)" }

Write-Host ''
Write-Host "RESULT: $script:PassCount passed, $script:FailCount failed"
if ($script:FailCount -gt 0) { exit 1 } else { exit 0 }
