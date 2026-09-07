# Detects the wallpaper hover-flicker bug from the stage 2A task: with "window" hosting, a wallpaper
# block driven by :hover (or any state InputBridge's forwarded WM_MOUSEMOVE toggles) flickers on real
# cursor movement, because Chromium's own TrackMouseEvent(TME_LEAVE) on the forwarded-to Chromium
# child window races against Windows resolving "window under the cursor" against the real, unclipped
# desktop (the icon list SysListView32), which fires WM_MOUSELEAVE right after every synthetic
# WM_MOUSEMOVE. Composition hosting (default, see src/NNA.Wallpaper/Engine/CompositionHost.cs) has no
# such child HWND for input, so this should never happen.
#
# Puts the real OS cursor at (X,Y), then 20 times (Seconds apart in total): jitters the cursor 1px so
# Windows generates genuine WM_MOUSEMOVE traffic (a script-only cursor warp without real movement
# would not exercise the TrackMouseEvent race), screenshots a 200x200 area centred on the point, and
# tracks the mean brightness of each frame. flicker=yes when the brightness jumps by more than
# -Threshold between two consecutive frames - i.e. the block's visible state changed mid-hover.
#
# Usage: powershell -File tests\hover-probe.ps1 -Port <p> -X <screenX> -Y <screenY> [-Seconds 2] [-OutDir <dir>]
# Prints: frames=20 maxDelta=<n> flicker=<yes|no>   Exit 0 (stable) / 1 (flicker detected).
#
# This only proves frame-to-frame screen stability; running it over a wallpaper block that sits
# behind the desktop icon layer on the owner's live desktop is the real-world check (deliberately not
# done here - this worktree must not touch the owner's running instance on port 1618).
param(
  [int]$Port = 1618,
  [Parameter(Mandatory = $true)][int]$X,
  [Parameter(Mandatory = $true)][int]$Y,
  [double]$Seconds = 2,
  [string]$OutDir = "$PSScriptRoot\..\hover-probe-shots",
  [double]$Threshold = 3.0
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$nativeCode = @'
using System;
using System.Runtime.InteropServices;
public static class HoverProbeNative {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
'@
Add-Type -TypeDefinition $nativeCode

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

try {
  $health = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 2
  "host reachable: app=$($health.app) version=$($health.version) monitors=$($health.monitors.Count)"
}
catch {
  "host not reachable on port $Port (continuing - this probe only needs the screen, not the API): $($_.Exception.Message)"
}

$half = 100
$left = $X - $half
$top = $Y - $half
$size = New-Object System.Drawing.Size(200, 200)

$frameCount = 20
$stepMs = [int](($Seconds * 1000) / $frameCount)
if ($stepMs -lt 10) { $stepMs = 10 }

function Get-MeanBrightness([System.Drawing.Bitmap]$bmp) {
  $total = 0.0; $count = 0
  for ($y = 0; $y -lt $bmp.Height; $y += 4) {
    for ($x = 0; $x -lt $bmp.Width; $x += 4) {
      $c = $bmp.GetPixel($x, $y)
      $total += ($c.R + $c.G + $c.B) / 3.0
      $count++
    }
  }
  if ($count -eq 0) { return 0 }
  return $total / $count
}

[HoverProbeNative]::SetCursorPos($X, $Y) | Out-Null
Start-Sleep -Milliseconds 150

$means = New-Object System.Collections.Generic.List[double]
$toggle = 0
for ($i = 0; $i -lt $frameCount; $i++) {
  # 1px jitter = genuine WM_MOUSEMOVE from the OS, not just a warp; this is what re-triggers the race.
  $jx = $X + $toggle
  [HoverProbeNative]::SetCursorPos($jx, $Y) | Out-Null
  $toggle = 1 - $toggle

  $bmp = New-Object System.Drawing.Bitmap $size.Width, $size.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($left, $top, 0, 0, $size)
  $g.Dispose()

  $path = Join-Path $OutDir ("frame-{0:D2}.png" -f $i)
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $means.Add((Get-MeanBrightness $bmp))
  $bmp.Dispose()

  Start-Sleep -Milliseconds $stepMs
}

$maxDelta = 0.0
for ($i = 1; $i -lt $means.Count; $i++) {
  $d = [math]::Abs($means[$i] - $means[$i - 1])
  if ($d -gt $maxDelta) { $maxDelta = $d }
}

$flicker = $maxDelta -gt $Threshold
$flickerText = if ($flicker) { "yes" } else { "no" }
"frames={0} maxDelta={1:F2} flicker={2}" -f $frameCount, $maxDelta, $flickerText
"means: " + (($means | ForEach-Object { "{0:F1}" -f $_ }) -join ", ")
"frames saved to: $OutDir"

if ($flicker) { exit 1 } else { exit 0 }
