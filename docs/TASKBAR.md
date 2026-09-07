# Windows taskbar: styling and modes

`Taskbar/TaskbarStyler.cs` (accent styling, the Windows registry toggles, the auto-hide state,
backup/reset) and `Taskbar/TaskbarLock.cs` (win-only mode) between them own everything NNA
Wallpaper does to the system taskbar. Both are driven by `app.taskbar` in `app.json` and exposed
under `GET/POST /taskbar/*` (see App.xaml.cs `RegisterAppRoutes`).

## Modes (`app.taskbar.windows.mode`)

| `mode`      | Behaviour                                                                             |
|-------------|----------------------------------------------------------------------------------------|
| `"normal"`  | Default. Auto-hide follows the old `app.taskbar.windows.autoHide` toggle (`null` = leave the Windows setting alone, `true`/`false` = force it). |
| `"autohide"`| Plain Windows auto-hide (`ABM_SETSTATE`/`ABS_AUTOHIDE`) — the taskbar slides out on hover at the screen edge, same as turning it on by hand in Windows Settings. |
| `"win-only"`| Owner decision D11: the taskbar is hidden **always**, including on hover. It only appears together with the Start menu (tap Win, or Win, hold, release within 1s) and hides again once Start closes and the mouse leaves it. Shortcuts that also involve the Win key — Win+1..9, Win+E, Win+D, Win+L, etc. — do **not** show it, because those combinations hold another key down while Win is held, which the tap-detector treats as "not a bare Win tap" (see below). |

Back-compat: a config saved before `mode` existed has no `"mode"` key in `app.json`; deserializing
it leaves `Mode` at its `"normal"` default while `autoHide` may still be `true`. Reading `mode`
through `TaskbarWindowsSettings.EffectiveMode()` (not the raw `Mode` field) resolves that case to
`"autohide"`, so an old config keeps behaving the way it did — see `AppSettings.cs`.

Preset shape: `presets/taskbar/windows.json` ships `mode: "normal"`; `presets/taskbar/mac.json`
ships `mode: "win-only"` (the owner's current desktop is the mac preset, so this is what turns
D11 on for it).

## What "win-only" actually does

`TaskbarLock` (created and torn down by `TaskbarStyler.Apply`/`Reset`/`Dispose`, never constructed
directly anywhere else) does three things while it is running:

1. **Auto-hide** — the same `SHAppBarMessage(ABM_SETSTATE, ABS_AUTOHIDE)` call `TaskbarStyler` has
   always used, kept on continuously (including re-asserted after an Explorer restart or a
   config-driven re-apply).
2. **Shields** — one borderless, click-opaque, `WS_EX_TOPMOST` WPF window per monitor (or per
   monitor when `taskbar.secondary` is on, primary-only otherwise), 2px tall, sitting exactly over
   the taskbar's own hover-detection strip at the bottom of the screen. Painted in the wallpaper's
   own `theme.palette.bgPage` colour so it reads as "empty desktop edge" rather than a visible bar.
   Auto-hide alone only stops the taskbar from being permanently visible; Windows still slides it
   back out the instant the mouse touches that strip. The shield intercepts the mouse there first,
   so the hover-out trigger never reaches `Shell_TrayWnd`. A 1s watchdog re-asserts each shield's
   z-order above the taskbar (Explorer keeps re-topmosting `Shell_TrayWnd` on its own, so this is a
   standing tug-of-war, not a one-time settile) and notices when `Shell_TrayWnd`'s window handle
   changes (Explorer restarted) to re-apply everything two seconds later.
3. **A low-level keyboard hook (`WH_KEYBOARD_LL`)** — the only way to tell "the user tapped the
   bare Win key" apart from "the user pressed Win+E" is to see the *sequence* of key events, which
   Windows does not expose any other way (there is no public "Start button was pressed" message
   that excludes shortcut combinations). The owner (D11) approved this specifically under these
   conditions, all of which the implementation holds to:
   - it only ever looks at whether the event's virtual-key code is `VK_LWIN`/`VK_RWIN`, to update
     one boolean ("is Win currently held down with nothing else pressed yet") and one timestamp;
   - **it writes nothing to disk or to the log** — not a key code, not a scan code, not which key
     was pressed. The only thing that gets logged is the module's own lifecycle ("hook installed",
     "hook removed") and, at Info level, "Win tap detected, showing" with no key data attached;
   - it **never blocks a keystroke**: every code path ends in `CallNextHookEx`, unconditionally,
     including inside a `catch` — an exception in the hook callback cannot swallow a keypress;
   - it is **removed** whenever: the mode changes away from `win-only` (`TaskbarStyler.Apply`),
     `POST /taskbar/reset` is called, the app shuts down (`Dispose`), and while the wallpaper is
     paused (`POST /app/pause`, a fullscreen app, or a locked session — detected by polling
     `IHostApp.Monitors[].paused`, which is already true while any of those hold; TaskbarLock
     itself does not modify pause state, only reads it every 300ms tick). It is reinstalled as
     soon as `/app/resume` (or the equivalent unpause condition) is observed.

   On a Win-up within one second of Win-down, with no other key pressed in between: the shields
   come down, auto-hide is released just long enough for the real taskbar to slide out with Start
   (there is no supported way to open Start without letting the taskbar itself become visible —
   they are the same slide-out animation), and normal auto-hide/shielding resumes 700ms after the
   Start menu closes and the mouse has left the taskbar's rectangle (a 300ms timer polls both
   conditions; either one being true keeps the taskbar shown).

## What it does not do

- No key is ever blocked, remapped, or consumed. Win+1..9/E/D/L/Tab/R etc. keep their normal
  Windows behaviour; the hook is purely an observer.
- Nothing about a keystroke is persisted anywhere — not in `app.json`, not in the log files under
  `<data>/logs`, not in memory beyond the one boolean/one timestamp described above.
- It does not touch any window other than the taskbar windows it already manages
  (`Shell_TrayWnd`/`Shell_SecondaryTrayWnd`) and its own shield windows.
- It is not installed, and has no effect, in any mode other than `win-only`, nor while the
  wallpaper is paused.

## Rollback

- **One click / one call**: `POST /taskbar/reset` always removes the keyboard hook and closes
  every shield first (`TaskbarStyler.Reset()` calls `TaskbarLock.Stop()` unconditionally, even if
  no backup file exists yet), then restores the registry toggles and the auto-hide state from the
  backup `TaskbarStyler` wrote the first time `Apply()` ran (`<data>/taskbar-backup.json`).
- Switching `app.taskbar.windows.mode` away from `"win-only"` (via the settings UI, a preset, or a
  raw `PUT /config`) followed by `POST /taskbar/apply` has the same effect on `TaskbarLock` — it is
  stopped before the new mode's own auto-hide handling runs.
- If the taskbar is ever left in a bad state regardless (e.g. Explorer got into a state the 2s
  reapply watchdog has not caught up with yet), `POST /taskbar/restart-explorer` is the last
  resort the settings UI and `tests/taskbar-lock-probe.ps1` both fall back to.
- `GET /taskbar/status` reports `lock: { mode, hookActive, shields, trayVisible }` alongside the
  existing fields, so "is the hook actually gone" and "are there still shields up" are always
  directly checkable rather than inferred.

## Testing

`tests/taskbar-lock-probe.ps1` drives the mode end to end against a **running** instance (a
shield window, the real `Shell_TrayWnd`, and the real Start menu only exist on a real desktop
session — `--headless` creates no windows, so this cannot be exercised through the usual
`--headless --port <p> --data <tmp>` pattern other probes in this folder use). It is deliberately
written to be safe to point at the owner's live instance on 1618 — see the script's own header
comment for exactly what it captures up front and how the `finally` block restores it — and to
auto-skip the win-only-specific checks (only run `GET /taskbar/status`, `POST /taskbar/reset`, and
the restore-to-original-config path) against a build that predates this stage, detected by the
absence of `status.lock`. Run it again with no changes once a build with win-only support is on
the target to get the full PASS/FAIL set (bottom-edge hover held 3s stays hidden, Win tap shows
the taskbar and Start within 500ms, Esc hides it again within ~2s, Win+E opens Explorer without
showing the taskbar).
