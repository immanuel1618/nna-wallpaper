<#
.SYNOPSIS
  Contract test for the NNA Wallpaper host's local API.

.DESCRIPTION
  Compares the JSON shape (key names only, not values) returned by the C# host against the
  live Python helper on 127.0.0.1:1618 when it is reachable, or against a saved key-only
  snapshot under tests/contract/*.json when it is not (the Python helper is being retired).
  Every key present in the reference must also be present in the host response; extra keys on
  the host side are fine. Arrays are compared by their first element (an empty reference array
  only checks that the host value is an array).

.PARAMETER Port
  Port the NNA Wallpaper host is listening on (e.g. 1622 for a manual test run).

.EXAMPLE
  powershell -File tests\api-contract.ps1 1622
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [int]$Port
)

$ErrorActionPreference = 'Stop'

$HostBase = "http://127.0.0.1:$Port"
$RefBase = "http://127.0.0.1:1618"
$ContractDir = Join-Path $PSScriptRoot 'contract'

$script:PassCount = 0
$script:FailCount = 0
$script:SkipCount = 0

function Write-Pass([string]$Name) {
    $script:PassCount++
    Write-Output "PASS $Name"
}

function Write-Fail([string]$Name, [string]$Why) {
    $script:FailCount++
    Write-Output "FAIL ${Name}: $Why"
}

function Write-Skip([string]$Name, [string]$Why) {
    $script:SkipCount++
    Write-Output "SKIP ${Name}: $Why"
}

# Performs a GET/POST and returns @{ Reachable; Status; Body } — Body is the parsed JSON object,
# or $null if the response was not valid JSON. Reachable is $false only when the connection
# itself failed (refused, timed out, DNS, etc.) rather than an HTTP error status.
function Invoke-Api {
    param(
        [string]$Url,
        [string]$Method = 'GET'
    )
    try {
        $resp = Invoke-WebRequest -Uri $Url -Method $Method -TimeoutSec 8 -UseBasicParsing -ErrorAction Stop
        $body = $null
        try { $body = $resp.Content | ConvertFrom-Json -ErrorAction Stop } catch { $body = $null }
        return [PSCustomObject]@{ Reachable = $true; Status = [int]$resp.StatusCode; Body = $body }
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
            $body = $null
            try { $body = $text | ConvertFrom-Json -ErrorAction Stop } catch { $body = $null }
            return [PSCustomObject]@{ Reachable = $true; Status = $status; Body = $body }
        }
        return [PSCustomObject]@{ Reachable = $false; Status = -1; Body = $null }
    }
    catch {
        return [PSCustomObject]@{ Reachable = $false; Status = -1; Body = $null }
    }
}

# Recursively checks that every key present in $Ref also exists in $HostVal. Returns $null when
# the structure matches, or a short human-readable mismatch description otherwise. Values are
# never compared, only presence of keys and the object/array shape.
function Test-Structure {
    param(
        $Ref,
        $HostVal,
        [string]$Path = '$'
    )

    if ($null -eq $Ref) { return $null }

    if ($Ref -is [System.Management.Automation.PSCustomObject]) {
        if ($null -eq $HostVal -or -not ($HostVal -is [System.Management.Automation.PSCustomObject])) {
            return "expected an object at $Path"
        }
        foreach ($prop in $Ref.PSObject.Properties) {
            $key = $prop.Name
            if (-not ($HostVal.PSObject.Properties.Name -contains $key)) {
                return "missing key '$key' at $Path"
            }
            $mismatch = Test-Structure -Ref $prop.Value -HostVal $HostVal.$key -Path "$Path.$key"
            if ($null -ne $mismatch) { return $mismatch }
        }
        return $null
    }

    if ($Ref -is [System.Array]) {
        if ($null -eq $HostVal -or -not ($HostVal -is [System.Array])) {
            return "expected an array at $Path"
        }
        if ($Ref.Count -eq 0) { return $null }          # empty reference array: type check only
        if ($HostVal.Count -eq 0) { return $null }       # host has none to check further; not an error
        return Test-Structure -Ref $Ref[0] -HostVal $HostVal[0] -Path "$Path[0]"
    }

    # Scalar (string/number/bool) or explicit null in the reference: presence of the key was
    # already confirmed by the caller, nothing more to check.
    return $null
}

function Get-Snapshot([string]$Name) {
    $file = Join-Path $ContractDir "$Name.json"
    if (-not (Test-Path $file)) { return $null }
    try { return Get-Content -Path $file -Raw | ConvertFrom-Json -ErrorAction Stop } catch { return $null }
}

# One entry per checked GET endpoint. Snapshot is the tests/contract/<name>.json fallback used
# when the live Python helper (1618) is unreachable or does not implement the endpoint at all
# (Live = $false for /config and /widgets, which are new in the C# host).
$Endpoints = @(
    @{ Name = '/health';      Path = '/health';      Snapshot = 'health';      Live = $true;  SkipOn404 = $false }
    @{ Name = '/stats';       Path = '/stats';       Snapshot = 'stats';       Live = $true;  SkipOn404 = $false }
    @{ Name = '/media';       Path = '/media';       Snapshot = 'media';       Live = $true;  SkipOn404 = $false }
    @{ Name = '/launch/list'; Path = '/launch/list'; Snapshot = 'launch-list'; Live = $true;  SkipOn404 = $false }
    @{ Name = '/graph?src=h'; Path = '/graph?src=h'; Snapshot = 'graph';       Live = $true;  SkipOn404 = $true }
    @{ Name = '/weather';     Path = '/weather';     Snapshot = 'weather';     Live = $true;  SkipOn404 = $false }
    @{ Name = '/events';      Path = '/events';      Snapshot = 'events';     Live = $true;  SkipOn404 = $false }
    @{ Name = '/pins';        Path = '/pins';        Snapshot = 'pins';        Live = $true;  SkipOn404 = $false }
    @{ Name = '/config';      Path = '/config';      Snapshot = 'config';      Live = $false; SkipOn404 = $false }
    @{ Name = '/widgets';     Path = '/widgets';     Snapshot = 'widgets';     Live = $false; SkipOn404 = $false }
)

foreach ($ep in $Endpoints) {
    $hostResp = Invoke-Api -Url ($HostBase + $ep.Path)
    if (-not $hostResp.Reachable) {
        Write-Fail $ep.Name 'host unreachable'
        continue
    }

    if ($ep.SkipOn404 -and $hostResp.Status -eq 404) {
        Write-Skip $ep.Name 'no graph configured (404)'
        continue
    }

    if ($hostResp.Status -ne 200) {
        Write-Fail $ep.Name "host returned status $($hostResp.Status)"
        continue
    }
    if ($null -eq $hostResp.Body) {
        Write-Fail $ep.Name 'host response is not valid JSON'
        continue
    }

    $ref = $null
    $refSource = 'snapshot'
    if ($ep.Live) {
        $refResp = Invoke-Api -Url ($RefBase + $ep.Path)
        if ($refResp.Reachable -and $refResp.Status -eq 200 -and $null -ne $refResp.Body) {
            $ref = $refResp.Body
            $refSource = 'live 1618'
        }
    }
    if ($null -eq $ref) {
        $ref = Get-Snapshot $ep.Snapshot
    }
    if ($null -eq $ref) {
        Write-Skip $ep.Name 'no reference available (1618 down, no snapshot)'
        continue
    }

    $mismatch = Test-Structure -Ref $ref -HostVal $hostResp.Body
    if ($null -ne $mismatch) {
        Write-Fail $ep.Name $mismatch
    }
    else {
        Write-Pass "$($ep.Name) (ref: $refSource)"
    }
}

# --- Negative checks -------------------------------------------------------------------------

# A non-GET request without a token must be rejected regardless of whether the item exists.
$noTokenResp = Invoke-Api -Url ($HostBase + '/launch/item?id=steam') -Method 'POST'
if (-not $noTokenResp.Reachable) {
    Write-Fail 'POST /launch/item (no token)' 'host unreachable'
}
elseif ($noTokenResp.Status -eq 403) {
    Write-Pass 'POST /launch/item (no token)'
}
else {
    Write-Fail 'POST /launch/item (no token)' "expected 403, got $($noTokenResp.Status)"
}

# Path traversal outside /icon must never resolve to 200. HttpListener/Uri may already collapse
# "..", so this just asserts the final status is not 200 either way.
$traversalResp = Invoke-Api -Url ($HostBase + '/icon/../config.json')
if (-not $traversalResp.Reachable) {
    Write-Pass 'GET /icon/../config.json'
}
elseif ($traversalResp.Status -eq 200) {
    Write-Fail 'GET /icon/../config.json' 'got 200 (path traversal not blocked)'
}
else {
    Write-Pass 'GET /icon/../config.json'
}

# --- Regression: the old Python helper must still be answering (informational, skipped if gone) ---

$refHealth = Invoke-Api -Url ($RefBase + '/health')
if (-not $refHealth.Reachable) {
    Write-Skip 'GET 1618/health (regression)' '1618 unreachable'
}
elseif ($refHealth.Status -eq 200) {
    Write-Pass 'GET 1618/health (regression)'
}
else {
    Write-Fail 'GET 1618/health (regression)' "status $($refHealth.Status)"
}

# --- Summary ----------------------------------------------------------------------------------

Write-Output "RESULT: $script:PassCount passed, $script:FailCount failed, $script:SkipCount skipped"
if ($script:FailCount -eq 0) { exit 0 } else { exit 1 }
