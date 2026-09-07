# Cursors

Three brand cursor sets for Windows: **mark**, **line**, **mono**. Monochrome, no shadows, no
color gradients: matches the NNA1618 palette (White `#FFFFFF`, Base `#0B0B0B`, Steel `#808080`,
Signal `#8C1A25` used only as a tip/serif accent, never smaller than 3px at the 32px size).

## Layout

```
brand/cursors/
  shapes.json          # single source of truth: palette, per-variant per-role vector primitives + hotspot
  mark/*.svg            \
  line/*.svg              13 roles x 3 variants = 39 reference SVGs (viewBox 0 0 32 32,
  mono/*.svg            /  data-hotspot="x,y" attribute on the root <svg>)
build/
  cursors.py            # builds .cur / .ani + presets/cursors/<variant>.json
  cursors-check.py       # validates every built file + LoadCursorFromFileW
  out/cursors/<variant>/<role>.cur|.ani   # build output, gitignored, rebuilt on demand
presets/cursors/<variant>.json            # manifest: files + registry role mapping (committed)
```

13 roles per variant (fixed file names): `arrow`, `hand`, `ibeam`, `wait`, `busy`, `size_ns`,
`size_we`, `size_nwse`, `size_nesw`, `move`, `no`, `help`, `crosshair`.

## Building

```
python build/cursors.py
python build/cursors.py --preview H:\night-runs\nna-wallpaper-2\shots\stage11-cursors.png
python build/cursors-check.py
```

`cursors.py` has no dependency beyond Pillow. It picks a rasterizer in this order:

1. `cairosvg`, if importable **and** its native `cairo` library actually loads (checked with a
   throwaway 4x4 render, since `import cairosvg` can succeed while the DLL is still missing).
2. an external CLI rasterizer found on `PATH`: `resvg`, `rsvg-convert`, `inkscape`, `magick`.
3. fallback: read `brand/cursors/shapes.json` and draw the same vector primitives directly with
   `PIL.ImageDraw` (polygons, lines, circles, rects/roundrects, capsules, a JetBrains Mono glyph
   for the `help` "?", and `group`: several fill-only sub-shapes composited with one traced
   outline around their union, e.g. the `hand` role's palm + finger + knuckles + thumb, so
   overlapping parts don't each draw their own outline and leave interior seam lines). This is
   the path that actually runs today: this machine has `pip install cairosvg` but no system
   `cairo-2.dll`, and none of `resvg` / `rsvg-convert` / `inkscape` / `magick` are on `PATH`.

The SVG files under `brand/cursors/<variant>/` and `brand/cursors/shapes.json` are generated from
one shared geometry description so they never drift apart; the SVGs are the human-readable/
designer-facing source, `shapes.json` is what the Pillow fallback actually reads at build time.
If you hand-edit one, edit the other to match.

For each role, the script renders at 32/48/64px with 4x supersampling (draw at 4x the target size,
downsample with Lanczos) for clean antialiased edges, then writes:

- **static roles** (11): a single `.cur` per size trio, ICO type-2 container, 32-bit BGRA BMP
  frames + 1bpp AND mask (computed from the alpha channel), hotspot stored in the
  `ICONDIRENTRY.wPlanes` / `wBitCount` fields (that's the real CUR-format repurposing of those
  two WORDs: BMP over PNG frames because it's the more universally accepted cursor payload).
- **animated roles** (`wait`, `busy`, 2): a RIFF `ACON` `.ani`: `anih` (36-byte ANIHEADER,
  `AF_ICON | AF_SEQ` flags), `rate` (8 x 4 jiffies = 8 x 60ms), `seq` (0..7), then a `LIST fram`
  containing 8 `icon` chunks, each one a complete, independently valid `.cur` (all three sizes).

`--preview PATH` additionally renders a 1440x800 PNG on a Base background: three columns (mark /
line / mono), JetBrains Mono column headers, all 13 roles at 64px with Steel 13px labels, and a
bottom Ash-colored strip repeating `arrow`/`hand` for a light-background legibility check.

`presets/cursors/<variant>.json` is regenerated on every build and is committed to the repo (the
`.cur`/`.ani` binaries under `build/out/` are not: they're gitignored and rebuilt on demand).
Manifest shape:

```json
{
  "id": "mark",
  "name": { "ru": "Знак", "en": "Mark" },
  "description": "...",
  "canvas": 32,
  "sizes": [32, 48, 64],
  "files": [
    { "role": "arrow", "file": "arrow.cur", "registryRole": "Arrow" },
    { "role": "wait", "file": "wait.ani", "registryRole": "Wait" }
  ]
}
```

`registryRole` names match the value names under `HKCU\Control Panel\Cursors` (see below), so a
future installer stage can walk the manifest directly.

## Validating

```
python build/cursors-check.py
```

Checks, per file: `ICONDIR`/RIFF signatures, that all three sizes (32/48/64) are present, that the
DIB header (`BITMAPINFOHEADER`, 32bpp, height = 2x for the AND mask) is internally consistent with
the declared frame size, that the hotspot falls inside the frame, and for `.ani` files that there
are exactly 8 animation frames each wrapping a well-formed `.cur`. It then calls
`user32!LoadCursorFromFileW` via `ctypes` on every file and requires a non-null handle: this is
the same API Windows itself uses to load a cursor resource, so a pass here means Windows accepts
the file, not just that our own parser is happy with it.

## Installing through the app

`src/NNA.Wallpaper.Host/Services/CursorService.cs` installs a scheme from inside the running app:
no manual registry editing needed. The built `.cur`/`.ani` files ship inside the app itself: they
are copied from `build/out/cursors/<variant>/` into `presets/cursors/<variant>/` (committed to the
repo, picked up by the existing `presets\**\*` `Content Include` in `NNA.Wallpaper.csproj`, same as
`presets/taskbar`) so a normal build/install carries them without a separate asset step.

Routes (POST needs the usual `X-Token` header/`?t=` query, same as every other write route):

- **`GET /cursor/status`** → `{ active, size, variants, backup, scheme }`.
  `active` is `"mark"|"line"|"mono"|null`: determined by checking whether the live `Arrow` value
  under `HKCU\Control Panel\Cursors` points inside our own `<data>\cursors\<variant>\` folder, not
  by trusting the saved setting (so it reflects reality even if something else changed the
  registry since). `variants` lists all three manifests (`id`, `name`, `description`, `files`
  count). `backup` is whether a pre-install snapshot exists. `scheme` is the current
  `(Default)` value of the registry key (the scheme's display name).
- **`POST /cursor/apply`** `{ variant: "mark"|"line"|"mono", size?: 32|48|64 }` →
  `{ ok, active, applied }`. First call ever: snapshots **every** value currently under
  `HKCU\Control Panel\Cursors` (all 13 roles, `(Default)`, `Scheme Source`, and anything else
  present, e.g. `CursorBaseSize`, `NWPen`, `UpArrow`, vendor-specific values: whatever this
  machine actually has) into `<data>\cursors-backup.json`; a backup already on disk is never
  rewritten, so switching between `mark`/`line`/`mono` repeatedly always restores back to the
  *original* scheme, not the previous brand variant. Each call copies the 13 files for that
  variant into `<data>\cursors\<variant>\`, points all 13 registry roles at the copies, sets
  `(Default)` to `"NNA <variant>"` and `Scheme Source` to `1` (user scheme), sets
  `CursorBaseSize` to `size` only if that value already existed on this machine, then calls
  `SystemParametersInfo(SPI_SETCURSORS, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE)` so the change is
  live immediately, no logoff needed. The choice is persisted to `app.json` (`cursor.variant`,
  `cursor.size`) and silently re-applied on the next host startup if the files are missing or the
  registry no longer points at us (e.g. after a reinstall or another app/scheme taking over): if
  it already points at us, startup writes nothing.
- **`POST /cursor/reset`** → `{ ok, restored }` (or `{ ok: false, error: "no backup" }` if there is
  nothing to restore). Puts every backed-up value back exactly: role paths, `(Default)`,
  `Scheme Source`, and anything else that was captured: and deletes any value that didn't exist
  before we touched the key. Broadcasts `SPI_SETCURSORS` again, clears `cursor.variant` in
  `app.json`, and deletes the backup file itself, so a second reset with nothing left to restore
  correctly reports `{ ok: false, error: "no backup" }` instead of silently no-op'ing.

`tests/cursor-probe.ps1 -Port <p>` drives all three routes end-to-end against a headless instance,
including the token gate and a full key-by-key comparison between the backup and what `/cursor/reset`
actually restores; see the script's own header comment for why it is safe to run against a shared
`HKCU` key (it always resets in a `finally` block, even on failure).

## Installing manually

The app's own `/cursor/apply`+`/cursor/reset` routes above are the supported path and the only
one with a backup/restore story; the steps below are the same thing done by hand (e.g. to inspect
what the app does, or for a machine that will never run the app). Cursor schemes on Windows live
under the per-user registry key
`HKCU\Control Panel\Cursors`. Each value name below must point at an absolute `.cur`/`.ani` path;
after writing them, broadcast `WM_SETTINGCHANGE` so running apps (and the shell) pick up the
change without a logoff: that's what
[`SystemParametersInfo(SPI_SETCURSORS, ...)`](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-systemparametersinfoa)
does under the hood.

```powershell
$variant = "mark"          # or line / mono
$root    = "H:\projects\nna-wallpaper\build\out\cursors\$variant"

$map = @{
  Arrow       = "arrow.cur"
  Hand        = "hand.cur"        # link select
  IBeam       = "ibeam.cur"
  Wait        = "wait.ani"
  AppStarting = "busy.ani"
  SizeNS      = "size_ns.cur"
  SizeWE      = "size_we.cur"
  SizeNWSE    = "size_nwse.cur"
  SizeNESW    = "size_nesw.cur"
  SizeAll     = "move.cur"
  No          = "no.cur"
  Help        = "help.cur"
  Crosshair   = "crosshair.cur"   # precision select — not part of the stock Windows scheme,
                                    # only applied by apps that ask for IDC_CROSS explicitly
}

New-Item -Path "HKCU:\Control Panel\Cursors" -Force | Out-Null
foreach ($k in $map.Keys) {
  Set-ItemProperty -Path "HKCU:\Control Panel\Cursors" -Name $k -Value (Join-Path $root $map[$k])
}
Set-ItemProperty -Path "HKCU:\Control Panel\Cursors" -Name "" -Value $variant   # scheme name

Add-Type -Namespace Win32 -Name Native -MemberDefinition @"
[DllImport("user32.dll", SetLastError=true)]
public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);
"@
[Win32.Native]::SystemParametersInfo(0x0057 /* SPI_SETCURSORS */, 0, $null, 0) | Out-Null
```

Applying the scheme (writing the registry keys + `SPI_SETCURSORS`) is intentionally **not**
performed by `build/cursors.py`: that script only builds and validates the files. It is instead
handled by the running app, with a backup/restore the snippet above doesn't have (see "Installing
through the app" above).

## License

Original artwork (own vector geometry, no third-party assets): MIT, same as the rest of this
repository. See `LICENSE` at the repo root.
