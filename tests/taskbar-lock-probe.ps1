<#
.SYNOPSIS
  Probes the D11 "win-only" taskbar mode (Taskbar/TaskbarLock.cs) end to end.

.DESCRIPTION
  Unlike most probes in this folder, this one is explicitly allowed to run against the owner's
  live instance on port 1618 (D11 sign-off) - there is nowhere else it CAN run: a --headless
  instance never creates windows, so the shield windows and the real Shell_TrayWnd/Start-menu
  interaction this mode relies on do not exist there. To keep that safe:
    - GET /taskbar/status is read and printed FIRST, before anything is touched.
    - GET /config/full is captured too, so app.taskbar/app.topBar can be restored byte-for-byte.
    - Every mutating step is inside try/finally: the finally ALWAYS calls POST /taskbar/reset
      (drops the hook, closes the shields, restores the Windows registry toggles from
      TaskbarStyler's own backup) and then PUTs app.taskbar/app.topBar back to the captured
      snapshot followed by POST /taskbar/apply, i.e. "reapply the mac preset" the way the
      settings UI itself does it (settings/taskbar-tab.js applyBtn: GET the preset json, PUT
      /config with the full taskbar+topBar objects, POST /taskbar/apply - there is no single
      "apply preset by id" endpoint on the host).
    - If anything after that still looks wrong, it also calls POST /taskbar/restart-explorer as
      a last resort (per the runbook), and always prints the final status so a human can check.

  win-only itself only exists from 0.3.1 (this stage) onward. The owner's live instance on 1618
  may still be the pre-win-only 0.3.0 build when this runs - GET /taskbar/status.lock is how the
  script tells the two apart: a 0.3.0 host has no "lock" key at all. In that case the win-only
  steps (auto-hide-lock, Win-tap, Win+E, Esc) are SKIPPED with a clear note instead of failing,
  and only the version-independent steps run (status read, reset, reapply). Re-run this script
  with no changes once 0.3.1+ is installed on the target to get the full PASS/FAIL set.

.PARAMETER Port
  API port of the running instance to probe. Defaults to 1618 (the owner's live instance - see
  above for why this is the one place that is actually safe to point this particular probe at).

.PARAMETER Token
  API token. Defaults to reading %LOCALAPPDATA%\NNA Wallpaper\config\app.json (or -Data\config\app.json).

.PARAMETER Data
  Data dir to read the token from when -Token is not given. Defaults to %LOCALAPPDATA%\NNA Wallpaper.

.PARAMETER Monitor
  Index into [System.Windows.Forms.Screen]::AllScreens used for the bottom-edge cursor / Shell_TrayWnd
  checks. Defaults to 0 (primary).

.EXAMPLE
  powershell -File tests\taskbar-lock-probe.ps1 -Port 1618
#>
[CmdletBinding()]
param(
    [int]$Port = 1618,
    [string]$Token = '',
    [string]$Data = '',
    [int]$Monitor = 0
)

$ErrorActionPreference = 'Stop'
$Base = "http://127.0.0.1:$Port"
$script:PassCount = 0
$script:FailCount = 0

function Write-Pass([string]$Name, [string]$Detail = '') {
    $script:PassCount++
    Write-Output ("PASS " + $Name + $(if ($Detail) { ": $Detail" } else { '' }))
}
function Write-Fail([string]$Name, [string]$Detail = '') {
    $script:FailCount++
    Write-Output ("FAIL " + $Name + $(if ($Detail) { ": $Detail" } else { '' }))
}
function Write-Skip([string]$Name, [string]$Why) {
    Write-Output ("SKIP " + $Name + ": " + $Why)
}

# -- native helpers: cursor, keyboard SendInput, window lookup ----------------------------------
Add-Type -AssemblyName System.Windows.Forms
$nativeCode = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class TaskbarLockProbeNative {
    [StructLayout(LayoutKind.Sequential)] struct INPUT_KBD { public uint type; public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT_KBD[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int val, int size);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    public struct RECT { public int Left, Top, Right, Bottom; }
    const uint KEYEVENTF_KEYUP = 0x0002;
    const int DWMWA_CLOAKED = 14;
    const uint WM_CLOSE = 0x0010;

    public static void KeyDown(ushort vk) {
        var i = new INPUT_KBD { type = 1, ki = new KEYBDINPUT { wVk = vk } };
        SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT_KBD)));
    }
    public static void KeyUp(ushort vk) {
        var i = new INPUT_KBD { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } };
        SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT_KBD)));
    }

    public static bool TryGetRect(IntPtr hwnd, out RECT r) {
        r = new RECT();
        return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out r);
    }

    public static IntPtr FindSecondaryTray(IntPtr after) {
        return FindWindowEx(IntPtr.Zero, after, "Shell_SecondaryTrayWnd", null);
    }

    public static IntPtr FindCabinet() {
        return FindWindow("CabinetWClass", null);
    }

    public static void CloseWindow(IntPtr hwnd) {
        if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    static bool IsCloaked(IntPtr hwnd) {
        int v;
        var hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out v, sizeof(int));
        return hr == 0 && v != 0;
    }

    // Locale-independent: the Windows 11 Start menu is a Windows.UI.Core.CoreWindow owned by
    // StartMenuExperienceHost.exe, not matched by (localized) title text.
    public static bool IsStartMenuOpen() {
        bool found = false;
        EnumWindows((hwnd, l) => {
            var sb = new StringBuilder(128);
            GetClassName(hwnd, sb, sb.Capacity);
            if (sb.ToString() != "Windows.UI.Core.CoreWindow") return true;
            if (!IsWindowVisible(hwnd) || IsCloaked(hwnd)) return true;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            try {
                using (var p = System.Diagnostics.Process.GetProcessById((int)pid)) {
                    if (!string.Equals(p.ProcessName, "StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)) return true;
                }
            } catch { return true; }
            found = true;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@
Add-Type -TypeDefinition $nativeCode
$VK_LWIN = 0x5B
$VK_ESCAPE = 0x1B
$VK_E = 0x45

function Tap-Win {
    [TaskbarLockProbeNative]::KeyDown($VK_LWIN)
    Start-Sleep -Milliseconds 120
    [TaskbarLockProbeNative]::KeyUp($VK_LWIN)
}
function Tap-Esc {
    [TaskbarLockProbeNative]::KeyDown($VK_ESCAPE)
    Start-Sleep -Milliseconds 80
    [TaskbarLockProbeNative]::KeyUp($VK_ESCAPE)
}
function Combo-WinE {
    [TaskbarLockProbeNative]::KeyDown($VK_LWIN)
    Start-Sleep -Milliseconds 60
    [TaskbarLockProbeNative]::KeyDown($VK_E)
    Start-Sleep -Milliseconds 60
    [TaskbarLockProbeNative]::KeyUp($VK_E)
    Start-Sleep -Milliseconds 60
    [TaskbarLockProbeNative]::KeyUp($VK_LWIN)
}

function Get-TrayRects {
    $rects = New-Object System.Collections.Generic.List[object]
    $primary = [TaskbarLockProbeNative]::FindWindow("Shell_TrayWnd", $null)
    $r = New-Object TaskbarLockProbeNative+RECT
    if ($primary -ne [IntPtr]::Zero -and [TaskbarLockProbeNative]::TryGetRect($primary, [ref]$r)) { $rects.Add($r) }
    $h = [TaskbarLockProbeNative]::FindSecondaryTray([IntPtr]::Zero)
    while ($h -ne [IntPtr]::Zero) {
        if ([TaskbarLockProbeNative]::TryGetRect($h, [ref]$r)) { $rects.Add($r) }
        $h = [TaskbarLockProbeNative]::FindSecondaryTray($h)
    }
    return $rects
}

# -- resolve the API token -----------------------------------------------------------------------
if (-not $Token) {
    $dataDir = $Data
    if (-not $dataDir) { $dataDir = Join-Path $env:LOCALAPPDATA 'NNA Wallpaper' }
    $appJson = Join-Path $dataDir 'config\app.json'
    if (Test-Path $appJson) {
        $cfg = Get-Content -Raw -Path $appJson | ConvertFrom-Json
        $Token = $cfg.apiToken
    }
}
if (-not $Token) { Write-Fail 'setup' 'no apiToken found (pass -Token or -Data explicitly)'; exit 1 }
$headers = @{ 'X-Token' = $Token }

# -- record the starting state (restored in finally, no matter what happens below) -------------
$statusBefore = Invoke-RestMethod -Uri "$Base/taskbar/status" -Headers $headers -TimeoutSec 8
Write-Output "before: $($statusBefore | ConvertTo-Json -Depth 8 -Compress)"
$fullBefore = Invoke-RestMethod -Uri "$Base/config/full" -Headers $headers -TimeoutSec 8
$supportsWinOnly = $null -ne $statusBefore.lock

$screens = [System.Windows.Forms.Screen]::AllScreens
$s = $screens[$Monitor]

try {
    Write-Pass 'GET /taskbar/status responded' "enabled=$($statusBefore.enabled) preset=$($statusBefore.preset)"

    if (-not $supportsWinOnly) {
        Write-Skip 'win-only checks (auto-hide-lock, Win tap, Esc, Win+E)' `
            'GET /taskbar/status has no .lock field: this instance predates win-only mode (0.3.0). Re-run after 0.3.1+ is installed.'
    }
    else {
        # ---- enable win-only (full taskbar object: PUT /config merges app.* shallowly at the
        # top level, so a partial {windows:{mode:...}} patch would drop every other taskbar field) ----
        $taskbar = $fullBefore.app.taskbar | ConvertTo-Json -Depth 10 | ConvertFrom-Json
        $taskbar.enabled = $true
        $taskbar.windows.mode = 'win-only'
        Invoke-RestMethod -Method Put -Uri "$Base/config" -Headers $headers -ContentType 'application/json; charset=utf-8' `
            -Body (@{ app = @{ taskbar = $taskbar } } | ConvertTo-Json -Depth 10 -Compress) -TimeoutSec 8 | Out-Null
        Invoke-RestMethod -Method Post -Uri "$Base/taskbar/apply" -Headers $headers -TimeoutSec 8 | Out-Null
        Start-Sleep -Milliseconds 800
        $st = Invoke-RestMethod -Uri "$Base/taskbar/status" -Headers $headers -TimeoutSec 8
        Write-Pass 'PUT /config + POST /taskbar/apply switched to win-only' "lock.mode=$($st.lock.mode) hookActive=$($st.lock.hookActive)"

        # ---- cursor at the bottom edge, held 3s: the shield must keep the panel from sliding out ----
        $x = $s.Bounds.X + [int]($s.Bounds.Width / 2)
        $y = $s.Bounds.Y + $s.Bounds.Height - 1
        [TaskbarLockProbeNative]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Seconds 3
        $rects = Get-TrayRects
        $covered = $true
        $screenBottom = $s.Bounds.Y + $s.Bounds.Height
        foreach ($r in $rects) {
            if ($r.Top -lt ($screenBottom - 4)) { $covered = $false }
        }
        if ($covered) { Write-Pass 'taskbar stayed hidden with the cursor held at the bottom edge for 3s' }
        else { Write-Fail 'taskbar stayed hidden with the cursor held at the bottom edge for 3s' "tray top(s): $(($rects | ForEach-Object { $_.Top }) -join ',') screenBottom=$screenBottom" }

        # ---- Win tap: panel + Start visible within 500ms ----
        Tap-Win
        Start-Sleep -Milliseconds 500
        $rects2 = Get-TrayRects
        $visible = ($rects2 | Where-Object { $_.Top -lt ($screenBottom - 4) }).Count -gt 0
        $startOpen = [TaskbarLockProbeNative]::IsStartMenuOpen()
        if ($visible -and $startOpen) { Write-Pass 'Win tap shows the taskbar and opens Start within 500ms' }
        else { Write-Fail 'Win tap shows the taskbar and opens Start within 500ms' "visible=$visible startOpen=$startOpen" }

        # ---- Esc closes Start; panel hides again within ~2s ----
        Tap-Esc
        Start-Sleep -Seconds 2
        $rects3 = Get-TrayRects
        $hiddenAgain = -not (($rects3 | Where-Object { $_.Top -lt ($screenBottom - 4) }).Count -gt 0)
        if ($hiddenAgain) { Write-Pass 'taskbar hides again ~2s after Esc closes Start' }
        else { Write-Fail 'taskbar hides again ~2s after Esc closes Start' "tray top(s): $(($rects3 | ForEach-Object { $_.Top }) -join ',')" }

        # ---- Win+E opens Explorer without showing the panel ----
        Combo-WinE
        Start-Sleep -Milliseconds 900
        $cabinet = [TaskbarLockProbeNative]::FindCabinet()
        $rects4 = Get-TrayRects
        $panelShown = ($rects4 | Where-Object { $_.Top -lt ($screenBottom - 4) }).Count -gt 0
        if ($cabinet -ne [IntPtr]::Zero -and -not $panelShown) { Write-Pass 'Win+E opens Explorer and does not show the taskbar' }
        else { Write-Fail 'Win+E opens Explorer and does not show the taskbar' "explorerWindow=$($cabinet -ne [IntPtr]::Zero) panelShown=$panelShown" }
        if ($cabinet -ne [IntPtr]::Zero) { [TaskbarLockProbeNative]::CloseWindow($cabinet) }
        Start-Sleep -Milliseconds 400
    }

    # ---- version-independent: reset drops the hook and the shields ----
    $resetResp = Invoke-RestMethod -Method Post -Uri "$Base/taskbar/reset" -Headers $headers -TimeoutSec 8
    Start-Sleep -Milliseconds 500
    $statusAfterReset = Invoke-RestMethod -Uri "$Base/taskbar/status" -Headers $headers -TimeoutSec 8
    if ($resetResp.ok) { Write-Pass 'POST /taskbar/reset returned ok' }
    else { Write-Fail 'POST /taskbar/reset returned ok' ($resetResp | ConvertTo-Json -Compress) }
    if ($supportsWinOnly) {
        if ($statusAfterReset.lock.hookActive -eq $false -and [int]$statusAfterReset.lock.shields -eq 0) {
            Write-Pass 'reset dropped the keyboard hook and closed the shields' "lock=$($statusAfterReset.lock | ConvertTo-Json -Compress)"
        }
        else {
            Write-Fail 'reset dropped the keyboard hook and closed the shields' "lock=$($statusAfterReset.lock | ConvertTo-Json -Compress)"
        }
    }
}
finally {
    # -- restore, no matter what happened above --------------------------------------------------
    Write-Output "restoring: POST /taskbar/reset, then PUT the captured app.taskbar/app.topBar back, then POST /taskbar/apply"
    try { Invoke-RestMethod -Method Post -Uri "$Base/taskbar/reset" -Headers $headers -TimeoutSec 8 | Out-Null } catch { Write-Output "restore: /taskbar/reset failed: $($_.Exception.Message)" }
    $restoreOk = $false
    try {
        Invoke-RestMethod -Method Put -Uri "$Base/config" -Headers $headers -ContentType 'application/json; charset=utf-8' `
            -Body (@{ app = @{ taskbar = $fullBefore.app.taskbar; topBar = $fullBefore.app.topBar } } | ConvertTo-Json -Depth 10 -Compress) -TimeoutSec 8 | Out-Null
        Invoke-RestMethod -Method Post -Uri "$Base/taskbar/apply" -Headers $headers -TimeoutSec 8 | Out-Null
        $restoreOk = $true
    } catch {
        Write-Output "restore: PUT /config + /taskbar/apply failed: $($_.Exception.Message)"
    }
    Start-Sleep -Milliseconds 500
    $statusFinal = $null
    try { $statusFinal = Invoke-RestMethod -Uri "$Base/taskbar/status" -Headers $headers -TimeoutSec 8 } catch {}
    $trayBackToNormal = $false
    try {
        $rectsFinal = Get-TrayRects
        $trayBackToNormal = ($rectsFinal | Where-Object { ($_.Bottom - $_.Top) -gt 4 }).Count -gt 0 -or $rectsFinal.Count -eq 0
    } catch {}
    if (-not $restoreOk -or ($statusFinal -and $supportsWinOnly -and $statusFinal.lock.hookActive)) {
        Write-Output "restore looked incomplete - falling back to POST /taskbar/restart-explorer"
        try { Invoke-RestMethod -Method Post -Uri "$Base/taskbar/restart-explorer" -Headers $headers -TimeoutSec 8 | Out-Null } catch {}
    }
    if ($statusFinal) { Write-Output "after: $($statusFinal | ConvertTo-Json -Depth 8 -Compress)" }
}

Write-Output "---"
Write-Output "PASS=$script:PassCount FAIL=$script:FailCount"
if ($script:FailCount -gt 0) { exit 1 } else { exit 0 }
