<#
.SYNOPSIS
  Proves that repeated config edits are live-patched over /events instead of a full page reload.

.DESCRIPTION
  Sends 20 PUT /config requests (alternating theme.dim between 0.3 and 0.4) against a running
  NNA Wallpaper host, then checks:
    - GET /events/stats.sent grew (the 50 ms debounce in EventsService may coalesce a fast burst,
      so >= 10 is accepted; the actual delta is always printed),
    - no new line in the host log (GET /test/log, headless/test builds only) mentions a reload,
    - GET /health.uptime_s did not drop (the process did not restart).
  This does not prove the wallpaper *page* patched itself without flicker — that needs a live
  WebView2/Edge window (see tests/screen-probe.ps1 style checks); this script only proves the
  host side is not forcing anything and is actually emitting live-update messages.

.PARAMETER Port
  Port the NNA Wallpaper host is listening on (a --headless test instance, never 1618).

.EXAMPLE
  powershell -File tests\no-reload-probe.ps1 -Port 1619
#>
[CmdletBinding()]
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
    Write-Output ("PASS " + $Name + $(if ($Detail) { ": $Detail" } else { '' }))
}
function Write-Fail([string]$Name, [string]$Why) {
    $script:FailCount++
    Write-Output "FAIL ${Name}: $Why"
}

# ---- baseline ------------------------------------------------------------------------------

$cfg = Invoke-RestMethod -Uri "$Base/config?monitor=main" -Method GET -TimeoutSec 8
$token = $cfg.token
if (-not $token) { Write-Fail 'setup' 'no token from GET /config'; exit 1 }

$health0 = Invoke-RestMethod -Uri "$Base/health" -Method GET -TimeoutSec 8
$statsBefore = Invoke-RestMethod -Uri "$Base/events/stats" -Method GET -TimeoutSec 8
$logBefore = @()
try { $logBefore = (Invoke-RestMethod -Uri "$Base/test/log" -Method GET -TimeoutSec 8).lines } catch { $logBefore = @() }

Write-Output "baseline: uptime_s=$($health0.uptime_s) events.sent=$($statsBefore.sent) log lines=$($logBefore.Count)"

# ---- 20x PUT /config, alternating theme.dim -------------------------------------------------
# EventsService debounces 50 ms per "what" key (a burst of edits to the same thing collapses to
# one broadcast — see src\NNA.Wallpaper.Host\Services\EventsService.cs). A
# tight loop over loopback HTTP completes well under 1 ms per request, so without spacing all 20
# edits would land inside a single 50 ms window and "sent" would only grow by 1 — that would prove
# the debounce, not the no-reload behaviour. 60 ms > 50 ms puts each edit in its own window, which
# is also realistic: a user dragging a settings slider changes the value roughly every 50-100 ms.

for ($i = 0; $i -lt 20; $i++) {
    $dim = if ($i % 2 -eq 0) { 0.3 } else { 0.4 }
    $theme = $cfg.theme.PSObject.Copy()
    $theme.dim = $dim
    $body = @{ app = @{ theme = $theme } } | ConvertTo-Json -Depth 10 -Compress
    try {
        Invoke-RestMethod -Uri "$Base/config" -Method PUT -Headers @{ 'X-Token' = $token } `
            -ContentType 'application/json; charset=utf-8' -Body $body -TimeoutSec 8 | Out-Null
    }
    catch {
        Write-Fail "PUT /config #$i" $_.Exception.Message
    }
    Start-Sleep -Milliseconds 60
}

# Let the 50 ms debounce in EventsService settle for the last edit before reading stats.
Start-Sleep -Milliseconds 400

# ---- after -----------------------------------------------------------------------------------

$statsAfter = Invoke-RestMethod -Uri "$Base/events/stats" -Method GET -TimeoutSec 8
$health1 = Invoke-RestMethod -Uri "$Base/health" -Method GET -TimeoutSec 8
$logAfter = @()
try { $logAfter = (Invoke-RestMethod -Uri "$Base/test/log" -Method GET -TimeoutSec 8).lines } catch { $logAfter = @() }

$sentDelta = [int]$statsAfter.sent - [int]$statsBefore.sent
Write-Output "after: uptime_s=$($health1.uptime_s) events.sent=$($statsAfter.sent) (delta=$sentDelta) log lines=$($logAfter.Count)"

if ($sentDelta -ge 20) {
    Write-Pass 'events.stats.sent grew by >= 20 (no debounce collapse)' "delta=$sentDelta"
}
elseif ($sentDelta -ge 10) {
    Write-Pass 'events.stats.sent grew by >= 10 (debounce coalesced some of the 20 edits)' "delta=$sentDelta"
}
else {
    Write-Fail 'events.stats.sent growth' "delta=$sentDelta, expected >= 10"
}

# New log lines only (set difference against the baseline tail), looking for a reload mention.
$newLines = $logAfter | Where-Object { $logBefore -notcontains $_ }
$reloadLines = $newLines | Where-Object { $_ -match '(?i)reload' }
if ($reloadLines.Count -eq 0) {
    Write-Pass 'no reload mentioned in host log during the probe' "$($newLines.Count) new log line(s) checked"
}
else {
    Write-Fail 'no reload mentioned in host log during the probe' ("found: " + ($reloadLines -join ' | '))
}

if ([int]$health1.uptime_s -ge [int]$health0.uptime_s) {
    Write-Pass 'GET /health uptime_s did not reset (process did not restart)' "before=$($health0.uptime_s) after=$($health1.uptime_s)"
}
else {
    Write-Fail 'GET /health uptime_s did not reset' "before=$($health0.uptime_s) after=$($health1.uptime_s) (went backwards)"
}

Write-Output "---"
Write-Output "PASS=$script:PassCount FAIL=$script:FailCount"
if ($script:FailCount -gt 0) { exit 1 } else { exit 0 }
