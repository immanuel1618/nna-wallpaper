# Windows taskbar: styling and modes

`Taskbar/TaskbarStyler.cs` (accent styling, the Windows registry toggles, the auto-hide state,
backup/reset) and `Taskbar/TaskbarLock.cs` (win-only mode) between them own everything NNA
Wallpaper does to the system taskbar. Both are driven by `app.taskbar` in `app.json` and exposed
under `GET/POST /taskbar/*` (see App.xaml.cs `RegisterAppRoutes`).

## Modes (`app.taskbar.windows.mode`)

| `mode`      | Behaviour                                                                             |
|-------------|----------------------------------------------------------------------------------------|
| `"normal"`  | Default. Auto-hide follows the old `app.taskbar.windows.autoHide` toggle (`null` = leave the Windows setting alone, `true`/`false` = force it). |
| `"autohide"`| Plain Windows auto-hide (`ABM_SETSTATE`/`ABS_AUTOHIDE`): the taskbar slides out on hover at the screen edge, same as turning it on by hand in Windows Settings. |
| `"win-only"`| Owner decision D11: the taskbar is hidden **always**, including on hover. It only appears together with the Start menu (tap Win, or Win, hold, release within 1s) and hides again once Start closes and the mouse leaves it. Shortcuts that also involve the Win key: Win+1..9, Win+E, Win+D, Win+L, etc.: do **not** show it, because those combinations hold another key down while Win is held, which the tap-detector treats as "not a bare Win tap" (see below). |

Back-compat: a config saved before `mode` existed has no `"mode"` key in `app.json`; deserializing
it leaves `Mode` at its `"normal"` default while `autoHide` may still be `true`. Reading `mode`
through `TaskbarWindowsSettings.EffectiveMode()` (not the raw `Mode` field) resolves that case to
`"autohide"`, so an old config keeps behaving the way it did: see `AppSettings.cs`.

Preset shape: `presets/taskbar/windows.json` ships `mode: "normal"`; `presets/taskbar/mac.json`
ships `mode: "win-only"` (the owner's current desktop is the mac preset, so this is what turns
D11 on for it).

## What "win-only" actually does (branch B)

The first implementation (shields + `IsWindowVisible` on Start's `CoreWindow`) shipped in 0.3.x
and failed on the owner's real desktop (Windows 11 build 26200, XAML taskbar) both ways: the
taskbar still slid out on hover regardless of the 2px shield (it does not detect the screen edge
through "what window is under the cursor" the way the old, pre-XAML taskbar did), and it never
hid again after Esc (`StartMenuExperienceHost`'s `CoreWindow` kept reading as "visible" even after
Start visibly closed). Branch B (`H:\night-runs\nna-wallpaper-2\06-OPEN.md` B5) replaces both
mechanisms. `TaskbarLock` (created and torn down by `TaskbarStyler.Apply`/`Reset`/`Dispose`, never
constructed directly anywhere else in production) does three things while running:

1. **A 75ms watchdog** that keeps the tray off-screen using one of three strategies
   (`TrayHideStrategy`), re-asserted every tick rather than trusted to stay put:

   | Strategy | What it does to hide | Live measurement (build 26200, see below) |
   |---|---|---|
   | `EdgeOffset` (a) | `SetWindowPos(Shell_TrayWnd, ..., y = screenBottom - 2, ...)` - the same rect autohide's own "peeking" state sits at | **does not work**: the call returns `TRUE`, `GetWindowRect` never changes, not even for one frame across 500ms of 25ms-interval polling |
   | `FullyOff` (b) | `SetWindowPos(..., y = screenHeight, ...)` - fully past the bottom edge | **does not work**, same symptom as (a) |
   | `ShowWindow` (c) | `ShowWindow(SW_HIDE)` / `ShowWindow(SW_SHOWNA)` - does not move the window, just stops it being drawn/hit-tested | **works**: `IsWindowVisible` flips immediately and held `false` through 3s of the cursor sitting at the bottom edge, with no auto-reveal by Explorer; `SW_SHOWNA` reliably restored it, fully functional, every time |

   Conclusion: the modern (24H2+) XAML taskbar host appears to ignore `SetWindowPos` from other
   processes entirely - a known-style limitation for tools built around the older taskbar's
   position-based hide tricks. `TaskbarStyler.cs` wires `TaskbarLock` to `TrayHideStrategy.
   ShowWindow` for that reason. `EdgeOffset`/`FullyOff` are kept implemented, not deleted, so this
   can be re-measured with `tests/TaskbarLockPreview --variant a|b|c` rather than re-derived from
   scratch if a future Windows build changes the answer. Auto-hide (`ABM_SETSTATE`/`ABS_AUTOHIDE`)
   is left on underneath all three strategies, matching the owner's existing setup - it is not
   what actually hides the tray any more, ShowWindow is, but there is no reason to fight it too.
2. **`IAppVisibility` (documented COM API, `CLSID_AppVisibility`
   `{7E5FE3D9-985F-4908-91F9-EE19F9FD1514}`)**: `IsLauncherVisible` replaces the old CoreWindow-
   polling guess for "is Start currently open". `TaskbarLock` `CoCreateInstance`s it once in
   `Start()`, releases it (`Marshal.FinalReleaseComObject`) in `Stop()`. If creation fails for any
   reason the tray simply never counts Start as open on its own - the Win-tap hook below still
   shows it directly, it just will not stay up past the 700ms away-debounce once Start itself is
   not tracked.
3. **A low-level keyboard hook (`WH_KEYBOARD_LL`)**: the only way to tell "the user tapped the
   bare Win key" apart from "the user pressed Win+E" is to see the *sequence* of key events, which
   Windows does not expose any other way (there is no public "Start button was pressed" message
   that excludes shortcut combinations). The owner (D11) approved this specifically under these
   conditions, all of which the implementation holds to:
   - it only ever looks at whether the event's virtual-key code is `VK_LWIN`/`VK_RWIN`, to update
     one boolean ("is Win currently held down with nothing else pressed yet") and one timestamp;
   - **it writes nothing to disk or to the log**: not a key code, not a scan code, not which key
     was pressed. The only thing that gets logged is the module's own lifecycle ("hook installed",
     "hook removed"), never per-keystroke;
   - it **never blocks a keystroke**: every code path ends in `CallNextHookEx`, unconditionally,
     including inside a `catch`: an exception in the hook callback cannot swallow a keypress;
   - it is **removed** whenever: the mode changes away from `win-only` (`TaskbarStyler.Apply`),
     `POST /taskbar/reset` is called, the app shuts down (`Dispose`), and while the wallpaper is
     paused (`POST /app/pause`, a fullscreen app, or a locked session: detected by polling
     `IHostApp.Monitors[].paused` through a probe `TaskbarStyler` passes in, so `TaskbarLock`
     itself never needs a `HostContext`). It is reinstalled as soon as the unpause condition is
     observed.

   On a Win-up within one second of Win-down, with no other key pressed in between: the tray is
   shown immediately (whichever strategy is active). It hides again once **both** conditions hold
   for 700ms straight: `IAppVisibility.IsLauncherVisible` is false, and the cursor is not over the
   tray's rectangle (so clicking a pinned icon right after a Win-tap does not hide the tray out
   from under the pointer). As a failsafe, if keyboard focus visibly moves to an ordinary window
   (not Start's CoreWindow, not the tray itself) while the cursor is off the tray, that also counts
   toward hiding even if a stale Start-open read would otherwise have kept it up. Hovering the
   empty desktop edge while the tray is already hidden never shows it on its own - that gate
   (`overTray` only counts once already shown) is exactly what branch A's hover-reveal bug was.

## What it does not do

- No key is ever blocked, remapped, or consumed. Win+1..9/E/D/L/Tab/R etc. keep their normal
  Windows behaviour; the hook is purely an observer.
- Nothing about a keystroke is persisted anywhere: not in `app.json`, not in the log files under
  `<data>/logs`, not in memory beyond the one boolean/one timestamp described above.
- It does not touch any window other than the taskbar windows it already manages
  (`Shell_TrayWnd`/`Shell_SecondaryTrayWnd`) - branch B has no extra shield windows to leak.
- It is not installed, and has no effect, in any mode other than `win-only`, nor while the
  wallpaper is paused.

## Rollback

- **One click / one call**: `POST /taskbar/reset` always removes the keyboard hook and shows the
  tray again first (`TaskbarStyler.Reset()` calls `TaskbarLock.Stop()` unconditionally, even if no
  backup file exists yet - `Stop()` itself calls `Show()` if the tray was left hidden), then
  restores the registry toggles and the auto-hide state from the backup `TaskbarStyler` wrote the
  first time `Apply()` ran (`<data>/taskbar-backup.json`).
- Switching `app.taskbar.windows.mode` away from `"win-only"` (via the settings UI, a preset, or a
  raw `PUT /config`) followed by `POST /taskbar/apply` has the same effect on `TaskbarLock`: it is
  stopped before the new mode's own auto-hide handling runs.
- If the taskbar is ever left in a bad state regardless (e.g. Explorer got into a state the
  watchdog has not caught up with yet), `POST /taskbar/restart-explorer` is the last resort the
  settings UI and `tests/taskbar-lock-probe.ps1` both fall back to.
- `GET /taskbar/status` reports `lock: { mode, hookActive, shown, appVisibilityActive }` alongside
  the existing fields: `shown` is "is the tray currently shown" (per `TaskbarLock`'s own tracked
  state, not a fresh `IsWindowVisible` poll - see its class remarks), `appVisibilityActive` is "is
  the IAppVisibility COM object alive". Both are directly checkable rather than inferred.

## Testing

Two ways to exercise branch B, both only meaningful against a **real desktop session** - a shield
window (branch A) or a `ShowWindow`-hidden tray (branch B), the real `Shell_TrayWnd`, and the real
Start menu only exist there; `--headless` creates no windows at all.

`tests/TaskbarLockPreview` (not part of `NNA.Wallpaper.sln`) runs the actual `TaskbarLock` class
directly against the machine's live `Shell_TrayWnd`, independent of any running NNA.Wallpaper
instance - `TaskbarLock`'s constructor only needs a `Log` and a `Dispatcher` (see its class
remarks), not a `HostContext`, specifically so this tool does not need port 1618 or the owner's
config/registry backup at all. Usage:

```
TaskbarLockPreview.exe --dry             # read-only: CoCreateInstance(CLSID_AppVisibility),
                                          # IsLauncherVisible once, release - no hook, no window
                                          # touched, no cursor moved. Prints
                                          # "IAppVisibility ok, launcherVisible=<bool>".
TaskbarLockPreview.exe --variant a|b|c   # full live sequence (hover 3s, Win tap, Esc, Win+E,
                                          # hover 3s again) with the given TrayHideStrategy.
                                          # Default variant if omitted: a (EdgeOffset) - see the
                                          # measurement table above for why TaskbarStyler.cs itself
                                          # wires production to "c" instead.
```

`tests/taskbar-lock-probe.ps1 -UsePreview` runs the same live sequence through
`TaskbarLockPreview.exe` instead of driving a running instance's `/taskbar/*` API - useful when
nothing is listening on 1618, or to test a variant other than whatever `app.taskbar.windows.mode`
currently has configured. Without `-UsePreview` (the default) it instead drives the mode end to
end against a **running** NNA.Wallpaper instance (default: the owner's own on port 1618 - this is
the one probe in this folder explicitly allowed to point there, since there is nowhere else a
real `Shell_TrayWnd`/Start menu interaction can run). It is deliberately written to be safe to do
that: see the script's own header comment for exactly what it captures up front and how the
`finally` block restores it, and to auto-skip the win-only-specific checks (only run
`GET /taskbar/status`, `POST /taskbar/reset`, and the restore-to-original-config path) against a
build that predates this stage, detected by the absence of `status.lock`.

Either way, the full PASS/FAIL set is: bottom-edge hover held 3s stays hidden, Win tap shows the
taskbar and Start within 500ms, Esc hides it again within ~2s, Win+E opens Explorer without
showing the taskbar, a second 3s hover stays hidden. **Open item from the one full live run made
before the owner returned to the keyboard** (`--variant` was not yet wired up at that point, so
this ran the very first ShowWindow-only build): every check passed except "Win tap shows the tray
and Start" - `IAppVisibility.IsLauncherVisible` read `false` and the tray stayed hidden. A
follow-up isolated probe (SendInput/`keybd_event`, both with and without `KEYEVENTF_EXTENDEDKEY`)
confirmed the real Start menu never opened from any synthetic Win-key press on this build at all -
a known Windows behaviour: Explorer appears to ignore an injected (non-hardware) standalone Win
key for opening Start specifically, as an anti-"Start-jacking" measure. Win+E, by contrast, opened
Explorer fine via the same SendInput mechanism, and the hook correctly kept the tray hidden
through it - so the filtering is specific to the bare-Win-tap Start-menu path, not to synthetic
input in general. Whether `TaskbarLock`'s own `WH_KEYBOARD_LL` hook actually received the
synthetic Win down/up pair (and just didn't call `ShowNow()` for some other reason) or never saw
it at all was not resolved before live testing was paused - a real hardware Win tap should still
work correctly either way (physical input is never filtered), but this needs a real key press (or
a deeper look at the hook) to confirm, flagged for whoever runs `--variant` next.
