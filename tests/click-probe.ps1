# Sends a real left click (SendInput) at a point on the desktop and checks that the running
# NNA Wallpaper host recorded it via /test/events (test-engine page) or logged it.
# Usage: powershell -File tests\click-probe.ps1 -Port 1619 [-X 1720 -Y 720] [-Monitor 0]
param([int]$Port = 1619, [int]$X = -1, [int]$Y = -1, [int]$Monitor = 0)
Add-Type -AssemblyName System.Windows.Forms
$code = @'
using System;
using System.Runtime.InteropServices;
public static class Probe {
  [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
  [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
  [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(80);
    var down = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = 0x0002 } };
    var up = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = 0x0004 } };
    SendInput(1, new[] { down }, Marshal.SizeOf(typeof(INPUT)));
    System.Threading.Thread.Sleep(60);
    SendInput(1, new[] { up }, Marshal.SizeOf(typeof(INPUT)));
  }
}
'@
Add-Type -TypeDefinition $code
$screens = [System.Windows.Forms.Screen]::AllScreens
$s = $screens[$Monitor]
if ($X -lt 0) { $X = $s.Bounds.X + [int]($s.Bounds.Width / 2) }
if ($Y -lt 0) { $Y = $s.Bounds.Y + [int]($s.Bounds.Height / 2) }
$before = (Invoke-RestMethod "http://127.0.0.1:$Port/test/events").Count
"click at ($X,$Y) on $($s.DeviceName); events before: $before"
[Probe]::Click($X, $Y)
Start-Sleep -Milliseconds 700
$events = Invoke-RestMethod "http://127.0.0.1:$Port/test/events"
$clicks = @($events | Where-Object { $_.type -eq "click" })
"events after: $($events.Count); clicks: $($clicks.Count)"
if ($events.Count -le $before) { "FAIL no new events after the click (before=$before after=$($events.Count))"; exit 1 }
if ($clicks.Count -gt 0) {
  $last = $clicks[-1]
  "last click: monitor=$($last.monitor) x=$($last.x) y=$($last.y)"
  if ($last.monitor -notlike "*$($s.DeviceName)*") { "FAIL click landed on another monitor: $($last.monitor)"; exit 1 }
  $relX = $X - $s.Bounds.X; $relY = $Y - $s.Bounds.Y
  if ([math]::Abs($last.x - $relX) -gt 40 -or [math]::Abs($last.y - $relY) -gt 40) { "FAIL click position off by more than 40 px (expected $relX,$relY)"; exit 1 }
  "PASS click reached the wallpaper page at the expected position"
  exit 0
}
"FAIL no click event recorded"
exit 1
