# Exercises --import (fresh install and forwarded-to-a-running-instance), single-instance
# forwarding, and --exit against a real NNA.Wallpaper.exe. All data lives under $env:TEMP; nothing
# is written to the repo and no Run key is touched (the exe only calls Autostart.Apply in full,
# non-headless mode, which this probe never starts).
# Usage: powershell -File tests\import-probe.ps1 -Exe <path to NNA.Wallpaper.exe> [-Source H:\brand\wallpaper]
param(
  [string]$Exe = "src\NNA.Wallpaper\bin\Release\net8.0-windows10.0.19041.0\win-x64\NNA.Wallpaper.exe",
  [string]$Source = "H:\brand\wallpaper"
)

$ErrorActionPreference = "Stop"
$failCount = 0

function Test-Result([bool]$ok, [string]$label) {
  if ($ok) { "PASS $label" } else { "FAIL $label"; $script:failCount++ }
}

function New-TempDir([string]$prefix) {
  $dir = Join-Path $env:TEMP ("nna-import-probe-" + $prefix + "-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  return $dir
}

$exePath = (Resolve-Path $Exe -ErrorAction SilentlyContinue)
if (-not $exePath) { "FAIL exe not found: $Exe"; exit 1 }
$exePath = $exePath.Path
"exe: $exePath"
"source: $Source"

# ---------------------------------------------------------------------------
# 1) Fresh --import into an empty data dir
# ---------------------------------------------------------------------------
$data1 = New-TempDir "fresh"
$p = Start-Process -FilePath $exePath -ArgumentList @("--import", $Source, "--data", $data1) -PassThru -Wait -WindowStyle Hidden
Test-Result ($p.ExitCode -eq 0) "fresh import exit code 0 (was $($p.ExitCode))"

$launchFile = Join-Path $data1 "config\launch.json"
if (Test-Path $launchFile) {
  $launch = Get-Content $launchFile -Raw | ConvertFrom-Json
  $groupCount = @($launch.groups).Count
  $itemCount = @($launch.items.PSObject.Properties).Count
  Test-Result ($groupCount -eq 6) "launch.json has 6 groups (was $groupCount)"
  Test-Result ($itemCount -eq 29) "launch.json has 29 items (was $itemCount)"
} else {
  Test-Result $false "config\launch.json exists"
  Test-Result $false "launch.json has 6 groups"
  Test-Result $false "launch.json has 29 items"
}

$eventsFile = Join-Path $data1 "config\events.json"
if (Test-Path $eventsFile) {
  $events = Get-Content $eventsFile -Raw | ConvertFrom-Json
  $eventCount = @($events.events).Count
  $dailyCount = @($events.daily).Count
  Test-Result ($eventCount -eq 2) "events.json has 2 events (was $eventCount)"
  Test-Result ($dailyCount -eq 3) "events.json has 3 daily (was $dailyCount)"
} else {
  Test-Result $false "config\events.json exists"
  Test-Result $false "events.json has 2 events"
  Test-Result $false "events.json has 3 daily"
}

$photosFile = Join-Path $data1 "config\widgets\photos.json"
if (Test-Path $photosFile) {
  $photos = Get-Content $photosFile -Raw | ConvertFrom-Json
  Test-Result ($photos.folder -and $photos.folder.EndsWith("block\pin")) "photos.json folder ends with block\pin (was '$($photos.folder)')"
} else {
  Test-Result $false "config\widgets\photos.json exists"
}

$iconsDir = Join-Path $data1 "data\icons"
if (Test-Path $iconsDir) {
  $iconCount = @(Get-ChildItem $iconsDir -Filter "*.png").Count
  Test-Result ($iconCount -ge 25) "icons copied >= 25 (was $iconCount)"
} else {
  Test-Result $false "data\icons exists with >= 25 icons"
}

# ---------------------------------------------------------------------------
# 2) --import against a non-existent source: exit 2, config untouched
# ---------------------------------------------------------------------------
$data2 = New-TempDir "missing"
$missingSource = Join-Path $env:TEMP ("nna-import-probe-no-such-dir-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$p2 = Start-Process -FilePath $exePath -ArgumentList @("--import", $missingSource, "--data", $data2) -PassThru -Wait -WindowStyle Hidden
Test-Result ($p2.ExitCode -eq 2) "missing-source import exit code 2 (was $($p2.ExitCode))"
$config2 = Join-Path $data2 "config"
$configEmpty = (-not (Test-Path $config2)) -or ((Get-ChildItem $config2 -Recurse -File -ErrorAction SilentlyContinue).Count -eq 0)
Test-Result $configEmpty "config untouched on missing source"

# ---------------------------------------------------------------------------
# 3) Single instance: second --headless launch on the same port exits fast with code 0
# ---------------------------------------------------------------------------
$data3 = New-TempDir "single"
$port = 1626
$first = Start-Process -FilePath $exePath -ArgumentList @("--headless", "--port", $port, "--data", $data3) -PassThru -WindowStyle Hidden
$healthUrl = "http://127.0.0.1:$port/health"
$upDeadline = (Get-Date).AddSeconds(10)
$up = $false
while ((Get-Date) -lt $upDeadline) {
  try { Invoke-RestMethod $healthUrl -TimeoutSec 1 | Out-Null; $up = $true; break } catch { Start-Sleep -Milliseconds 200 }
}
Test-Result $up "first --headless instance answers /health"

if ($up) {
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  $second = Start-Process -FilePath $exePath -ArgumentList @("--headless", "--port", $port, "--data", $data3) -PassThru -Wait -WindowStyle Hidden
  $sw.Stop()
  Test-Result ($second.ExitCode -eq 0) "second instance exit code 0 (was $($second.ExitCode))"
  Test-Result ($sw.Elapsed.TotalSeconds -lt 2) ("second instance returned in < 2s (was {0:N2}s)" -f $sw.Elapsed.TotalSeconds)

  # -------------------------------------------------------------------------
  # 4) --exit against the still-running first instance
  # -------------------------------------------------------------------------
  # --port must match: forwarding reads the *running* instance's port from its app.json, which
  # only reflects a --port override when the caller repeats it (app.json's own "apiPort" default,
  # 1618, is never touched by a one-off --port launch flag).
  $exitP = Start-Process -FilePath $exePath -ArgumentList @("--exit", "--port", $port, "--data", $data3) -PassThru -Wait -WindowStyle Hidden
  Test-Result ($exitP.ExitCode -eq 0) "--exit forwarded ok (exit code 0, was $($exitP.ExitCode))"

  $downDeadline = (Get-Date).AddSeconds(5)
  $down = $false
  while ((Get-Date) -lt $downDeadline) {
    try { Invoke-RestMethod $healthUrl -TimeoutSec 1 | Out-Null; Start-Sleep -Milliseconds 200 }
    catch { $down = $true; break }
  }
  Test-Result $down "first instance stopped answering /health within 5s after --exit"
} else {
  Test-Result $false "second instance exit code 0"
  Test-Result $false "second instance returned in < 2s"
  Test-Result $false "--exit forwarded ok"
  Test-Result $false "first instance stopped answering /health within 5s after --exit"
}

if (-not $first.HasExited) {
  try { $first.Kill() } catch { }
}

if ($failCount -eq 0) {
  "RESULT PASS"
  exit 0
} else {
  "RESULT FAIL ($failCount failing check(s))"
  exit 1
}
