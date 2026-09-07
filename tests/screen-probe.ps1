# Samples the screen colour at (200,200) relative to every monitor and prints it.
# Usage: powershell -File tests\screen-probe.ps1 [-Expect "#123456"]
# Exit code 0 when every monitor matches -Expect (if given), 1 otherwise.
param([string]$Expect = "")
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$fail = 0
foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
  $x = $s.Bounds.X + 200; $y = $s.Bounds.Y + 200
  $bmp = New-Object System.Drawing.Bitmap 1, 1
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
  $c = $bmp.GetPixel(0, 0)
  $hex = ("#{0:X2}{1:X2}{2:X2}" -f $c.R, $c.G, $c.B)
  $g.Dispose(); $bmp.Dispose()
  $status = ""
  if ($Expect -ne "") {
    if ($hex -ieq $Expect) { $status = "MATCH" } else { $status = "MISMATCH"; $fail = 1 }
  }
  "{0} at ({1},{2}) = {3} {4}" -f $s.DeviceName, $x, $y, $hex, $status
}
exit $fail
