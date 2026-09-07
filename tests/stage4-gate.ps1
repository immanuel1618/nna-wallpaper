$ErrorActionPreference = 'Continue'
$repo = 'H:\projects\nna-wallpaper'
$exe = "$repo\src\NNA.Wallpaper\bin\Release\net8.0-windows10.0.19041.0\win-x64\NNA.Wallpaper.exe"
$data = 'C:\Users\imman\AppData\Local\Temp\claude\h--\177acbd5-5f45-4de3-8283-018df2861388\scratchpad\stage4-data'
$we = 'C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe'
$m = 'C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\projects\myprojects\'
$port = 1619
New-Item -ItemType Directory -Force $data | Out-Null
$result = @{}

"== 0 baseline screen (WE showing)"
& powershell -NoProfile -File "$repo\tests\screen-probe.ps1"

"== 1 close WE wallpapers (WE process stays)"
& $we -control closeWallpaper -monitor 0
& $we -control closeWallpaper -monitor 1
Start-Sleep -Seconds 3
& powershell -NoProfile -File "$repo\tests\screen-probe.ps1"

"== 2 start test engine"
$p = Start-Process -PassThru -FilePath $exe -ArgumentList "--test-engine --port $port --data `"$data`""
Start-Sleep -Seconds 8
try { $h = Invoke-RestMethod "http://127.0.0.1:$port/health" -TimeoutSec 5; "health: monitors=$($h.monitors.Count) visible=$(($h.monitors | % { $_.visible }) -join ',')" } catch { "health FAIL: $_" }

"== 3 screen-probe expect #123456"
& powershell -NoProfile -File "$repo\tests\screen-probe.ps1" -Expect '#123456'
$result.screen = $LASTEXITCODE

"== 4 click-probe monitor 0 (center)"
& powershell -NoProfile -File "$repo\tests\click-probe.ps1" -Port $port -Monitor 0
$result.click0 = $LASTEXITCODE
"== 4b click-probe monitor 1 (center)"
& powershell -NoProfile -File "$repo\tests\click-probe.ps1" -Port $port -Monitor 1
$result.click1 = $LASTEXITCODE

"== 5 fullscreen-probe (monitor 0 covered)"
& powershell -NoProfile -File "$repo\tests\fullscreen-probe.ps1" -Port $port -Monitor 0
$result.fullscreen = $LASTEXITCODE

"== 6 exit test engine"
$token = (Get-Content "$data\config\app.json" -Raw | ConvertFrom-Json).apiToken
try { Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$port/app/exit" -Headers @{ 'X-Token' = $token } -Body '' | Out-Null } catch { "exit call: $_" }
Start-Sleep -Seconds 3
if (-not $p.HasExited) { "process still alive, killing"; Stop-Process -Id $p.Id -Force }
"after exit:"
& powershell -NoProfile -File "$repo\tests\screen-probe.ps1" -Expect '#123456'
$result.afterExitStillBlue = ($LASTEXITCODE -eq 0)

"== 7 restore WE wallpapers"
& $we -control openWallpaper -file ($m + 'nothing-os-nna\project.json') -monitor 0
& $we -control openWallpaper -file ($m + 'nna-vertical\project.json') -monitor 1
Start-Sleep -Seconds 8
& powershell -NoProfile -File "$repo\tests\screen-probe.ps1" -Expect '#123456'
$result.afterRestoreStillBlue = ($LASTEXITCODE -eq 0)
$weProc = Get-Process wallpaper64 -ErrorAction SilentlyContinue
"wallpaper64 alive: $($weProc -ne $null) pid=$($weProc.Id)"
"helper: " + (Invoke-RestMethod "http://127.0.0.1:1618/health" -TimeoutSec 5 | ConvertTo-Json -Compress)

"== RESULT"
"screen=$($result.screen) click0=$($result.click0) click1=$($result.click1) fullscreen=$($result.fullscreen) afterExitStillBlue=$($result.afterExitStillBlue) afterRestoreStillBlue=$($result.afterRestoreStillBlue)"
"== engine log tail"
Get-Content "$data\logs\app.log" -Tail 25
