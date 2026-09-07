# Opens a borderless window covering monitor 0 for a few seconds and checks that the host reports
# monitor 0 paused and the other monitors not paused. Usage: powershell -File tests\fullscreen-probe.ps1 -Port 1619
param([int]$Port = 1619, [int]$Monitor = 0)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$s = [System.Windows.Forms.Screen]::AllScreens[$Monitor]
$f = New-Object System.Windows.Forms.Form
$f.FormBorderStyle = 'None'
$f.StartPosition = 'Manual'
$f.TopMost = $true
$f.BackColor = [System.Drawing.Color]::Black
$f.Bounds = $s.Bounds
$f.Show()
$f.Activate()
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 2500
[System.Windows.Forms.Application]::DoEvents()
$h = Invoke-RestMethod "http://127.0.0.1:$Port/health"
$f.Close()
$fail = 0
$i = 0
foreach ($m in $h.monitors) {
  $covered = ($m.x -eq $s.Bounds.X -and $m.y -eq $s.Bounds.Y)
  "{0}: paused={1} (covered={2})" -f $m.id, $m.paused, $covered
  if ($covered -and -not $m.paused) { $fail = 1 }
  if (-not $covered -and $m.paused) { $fail = 1 }
  $i++
}
Start-Sleep -Milliseconds 1500
$h2 = Invoke-RestMethod "http://127.0.0.1:$Port/health"
$still = @($h2.monitors | Where-Object { $_.paused }).Count
"after closing: paused monitors = $still"
if ($still -gt 0) { $fail = 1 }
if ($fail -eq 0) { "PASS fullscreen pause"; exit 0 } else { "FAIL fullscreen pause"; exit 1 }
