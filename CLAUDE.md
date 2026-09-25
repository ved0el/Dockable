# CLAUDE.md — Dockable

A macOS-style **Dock for Windows 11**: a bottom bar that mirrors the taskbar (pinned + running
apps), magnifies icons on hover, opens the Start menu, auto-hides the real taskbar, supports
Light/Dark/Auto theming + a translucent/acrylic/liquid-glass bar + a Dock Preferences window,
replaces the window minimize/restore with a "suck", "scale", or "genie" warp into the dock, and is
**fully localized** (English, pt-BR, es, uk, zh-Hans). This file is the standing guide for any Claude
Code session here — read it first; it captures everything not obvious from the code.

---

## ⛔ Working agreement — do NOT visually test

- **The user does ALL visual/interaction testing.** Never take screenshots, launch the app "to see
  it", move the cursor, or add on-screen diagnostics for visual checks.
- **Your verification stops at: the code compiles** (`dotnet build` succeeds, 0 errors).
- Only run the app to debug a **non-visual** crash the user reports (read
  `%APPDATA%\Dockable\crash.log`), then stop it.
- It IS fine to inspect data for development (registry values, .lnk targets, window properties via
  one-off PowerShell) — that's not "visual testing".
- After changes: confirm a clean build, summarize what changed, ask the user to test. Tune blind
  constants (animation timings, sizes) only when the user reports how it feels.

---

## Environment

- **`dotnet` is NOT on PATH.** Always invoke it by full path:
  `& "C:\Program Files\dotnet\dotnet.exe"`. The Bash tool can't see dotnet; use the PowerShell tool.
- Only the **.NET 9 SDK (9.0.315)** is installed (no .NET 10). Target framework is
  `net9.0-windows10.0.22621.0`, **x64**, WPF, unpackaged.
- **Stop the running app before building** — the running `Dockable.exe` locks the output, so the
  build fails at the copy step (compilation still succeeds; look for `: error CS`, not MSB copy
  errors). **For Debug builds it's fine to kill the app yourself first** (standing permission from the
  user) so the copy step doesn't fail — just run the stop command before building.
  Stop command: `Get-Process Dockable -ErrorAction SilentlyContinue | Stop-Process -Force`.
  **After a successful Debug build, RESTART the app — but only if it was actually running before
  you killed it** (don't launch a dock the user didn't have open). Capture the state before the
  kill and relaunch detached after the build:
  ```powershell
  $wasRunning = [bool](Get-Process Dockable -ErrorAction SilentlyContinue)
  Get-Process Dockable -ErrorAction SilentlyContinue | Stop-Process -Force
  & "C:\Program Files\dotnet\dotnet.exe" build "src\Dockable\Dockable.csproj" -c Debug
  if ($wasRunning) { Start-Process "src\Dockable\bin\Debug\net9.0-windows10.0.22621.0\Dockable.exe" }
  ```
  Only one dock runs (single-instance Mutex). Force-killing is safe: a hidden `powershell.exe`
  **taskbar watchdog** (`Interop/TaskbarWatchdog`, spawned at startup) notices the kill, restores the
  taskbar's pre-launch state, and exits on its own — killing `Dockable` by name doesn't touch it
  (different image name), and a stray watchdog from a previous run skips the restore if a new dock is
  already running. Don't bother killing the watchdog; it self-terminates seconds after the app dies.
- **PowerShell is 5.1.** No `out var` / `var` in `Add-Type` C# (use explicit types). A script that
  combines `Remove-Item` with a `C:\Program...` literal is sandbox-blocked — split the steps or use
  `Clear-Content` / `Join-Path`.
- `Date.Now`-style nondeterminism is fine in app code (only workflow scripts forbid it).

## Build

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build "src\Dockable\Dockable.csproj" -c Debug
```

The user runs the built exe directly:
`src\Dockable\bin\Debug\net9.0-windows10.0.22621.0\Dockable.exe`.

### Release / Steam publish

The Steam build is a **self-contained x64** publish (runtime + WPF bundled; no install needed),
driven by the `SteamRelease` profile (`src/Dockable/Properties/PublishProfiles/SteamRelease.pubxml`):
self-contained + ReadyToRun, **not** single-file, **not** trimmed (WPF isn't trim-safe).

```powershell
pwsh -File steam\build-steam.ps1   # → src\Dockable\bin\Publish\Steam\win-x64\Dockable.exe
```

SteamPipe scripts + upload walkthrough live in `steam/` (`app_build.vdf`, `depot_build.vdf`,
`README.md`). No Steamworks SDK is integrated yet (Steam launches the plain exe). Bump
`<Version>` in `Dockable.csproj` per release.

### Portable single-file publish

A truly portable **one-file** `Dockable.exe` (~84 MB) for hand-out outside Steam, via the `Portable`
profile (`Properties/PublishProfiles/Portable.pubxml`): self-contained + **single-file** with
`IncludeNativeLibrariesForSelfExtract` (native libs embedded + self-extracted) + compression +
ReadyToRun; **not** trimmed (WPF isn't trim-safe). All assets are embedded so nothing sits beside the
exe — note the UI **sounds are WPF `Resource`s** (loaded via a `pack://` stream in `Sounds.cs`), not
`Content` copied to the output, precisely so the single-file build has no loose `.wav`s. Anything new
that must ship has to be an embedded resource (or it breaks the single-file guarantee).

```powershell
pwsh -File scripts\build-portable.ps1   # → src\Dockable\bin\Publish\Portable\win-x64\Dockable.exe
```

### CsWin32 P/Invoke workflow

Win32 interop is generated by **CsWin32** from `src/Dockable/NativeMethods.txt` (one API/type per
line). To add a Win32 call: add its name there, build, then call it as
`Windows.Win32.PInvoke.<Name>` (free functions) or via the generated type in its namespace.

To discover the exact generated signature (they vary — `in`/`out`/pointer/`Span`, friendly
overloads, namespaces), build once with the files emitted and read them:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build "src\Dockable\Dockable.csproj" -c Debug `
  -p:EmitCompilerGeneratedFiles=true --no-incremental
# then read: src\Dockable\obj\Debug\net9.0-windows10.0.22621.0\generated\Microsoft.Windows.CsWin32\...\*.g.cs
```

- CsWin32 types are `internal` — anything exposing them in a public signature must also be internal
  (e.g. `WindowFilter`).
- Some newer flags aren't in the metadata; cast the literal (e.g.
  `(PRINT_WINDOW_FLAGS)2` for `PW_RENDERFULLCONTENT`). `PKEY_AppUserModel_ID` isn't generated —
  construct the `PROPERTYKEY` (fmtid `{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}`, pid 5).
- A few APIs aren't in Win32 metadata at all and are hand-written `DllImport`/WinRT activation
  (the acrylic backdrop hosts a `Windows.UI.Composition` tree; the liquid-glass shader is compiled at
  runtime via `d3dcompiler_47`). Prefer CsWin32; note in a comment when you can't.

## Packages (`Dockable.csproj`)

- `CommunityToolkit.Mvvm` 8.4.0 — `[ObservableProperty]`, `[NotifyPropertyChangedFor]`.
- `H.NotifyIcon.Wpf` 2.3.0 — tray icon. **Quirk:** its `IconSource` only accepts URI-backed images;
  use `GeneratedIconSource` (a `BitmapSource` subclass) for an in-process glyph.
- `Microsoft.Windows.CsWin32` 0.3.183 — source-generated interop (PrivateAssets=all).
- `SharpVectors.Wpf` 1.8.5 — SVG → WPF drawing. Used by `Shell/SvgIcon` to render real icons for
  .svg/.svgz files (the shell has no SVG thumbnailer — it returns the generic association icon);
  hooked into `ShortcutService.LoadIcon`'s single funnel, so pinned SVG files, stack members, and
  fan rows all show the artwork. Rasterized square/centered at the requested size on the loader's
  worker thread (visual + RenderTargetBitmap created and frozen on that same thread).

---

## Per-file gotchas

The layout is what `ls src/Dockable` shows; these are the notes the code doesn't make obvious.

- **App.xaml.cs** — startup is wrapped in try/catch that logs + MessageBoxes + exits, so a failure is
  never a silent zombie; `DispatcherUnhandledException` is kept non-fatal; exit/crash restore the taskbar.
- **SettingsWindow** — settings search runs over `SettingsIndex` (panel tag + Loc name key + row
  resolver + English synonym tags; names match in the UI language, tags in English). Rows are found by
  walking up from the named control (`RowOf`). **New settings must be added to the index.**
- **WindowPreview** — a plain OPAQUE window (not a Popup, not AllowsTransparency): DWM refuses to mirror
  a thumbnail into a layered window. Cells are laid out by arithmetic so the same numbers go to DWM in
  physical px. `Interop/DwmThumbnail` works for occluded AND minimized windows (a BitBlt of either
  grabs the occluder or nothing).
- **UiBrushes** — use it for frozen brushes from hex + the shared palette constants; don't re-roll a
  private `Brush(hex)`.
- **MenuBuilder** — only for PLAIN menu items. Dynamic headers (Quit/Force Quit), Icons and
  sender-aware handlers stay hand-built at their call sites.
- **DialogChrome** — shared scaffolding for the code-built dialogs (ConfirmDialog, InputDialog); keep
  per-dialog content local.
- **Accessibility/A11y** — code-built click targets (fan/grid rows, search rows, menu-bar pills) use
  `InvokableRow`/`InvokableCell`, whose UIA Invoke replays `MouseLeftButtonUp`. `DockItemElement`'s peer
  names each dock item and routes Invoke → `DockWindow.ActivateItem`.
- **Shell/ShortcutService** — exes go through `PrivateExtractIcons`, which STRETCHES the frame it picks
  to the requested size, so `NativeIconWidth` reads the chosen RT_ICON's real width (piconid →
  LoadLibraryEx AS_DATAFILE → FindResource; PNG IHDR or BITMAPINFOHEADER) and re-extracts at that size
  when SMALLER. Downscales are left alone. Window captures never pass through `LoadIconAsync`.
- **Shell/PackagedApp** — `LargestLogo()` returns the biggest UNPLATED asset (unqualified / scale-* /
  *_altform-unplated; a plain targetsize-* can have the logo baked onto a solid square). `LoadIcon` reads
  it at NATIVE size when smaller than requested (the shell would upscale it; Teams ships 176px); bigger
  assets still go through the shell.
- **Shell/SvgIcon** — the visual + RenderTargetBitmap are created and frozen on the loader's worker thread.
- **Interop/WinEventHook** — every WinEvent watcher composes it (delegate lifetime, double-start guard,
  restart, pid scoping, the idObject==0 && idChild==0 filter). New watchers should too.
- **Interop/SystemTheme** — `IsDarkEffective` / `IsImmersiveColorChange` are shared by the dock's and
  menu bar's ApplyTheme/WndProc so the two never drift.
- **Interop/TaskbarApps** — per-window exe path + AUMID are cached by HWND (`IdentityCache`: pid-checked
  against handle recycling, empty AUMIDs retried ≤3× for late-setting apps, dead HWNDs evicted each
  enumeration). Titles stay live. The public `GetWindowExePath`/`GetWindowAumid` keep UNCACHED semantics
  for event-driven callers.
- **Interop/PinMatcher** — built matchers are cached per pin path (their inputs are permanently memoized).
- **Interop/MinimizeInterceptHook** — also owns the cursor-approach z-order raise: `RaiseZones` (bands
  along each docked edge, published by `DockWindow.RefreshRaiseZones` from `App.SyncDockMonitors`) +
  `CursorEnteredRaiseZone` → `App.KeepDocksOnTop`. A PiP player / tiling WM shares the topmost band, so
  last-raiser wins and the 1 s `KeepOnTop` backstop alone could leave the dock buried when a click lands.
- **Genie/OverlayAnimatorBase** — owns the velocity curve: `Emphasized` (front-loaded, zero velocity at
  both ends) is applied to PROGRESS and then mirrored for a restore, never the other way round
  (mirroring an eased value inverts the curve, so a restore would arrive at a hard stop). Restores run
  `RestoreDurationFactor` (1.15×) longer. Subclasses SHAPE the eased warp (the genie's per-row stagger)
  and must not re-ease it. `GenieAnimator.RefreshQuality` tears the overlay down so the next play
  rebuilds the mesh.

---

## Core architecture & conventions

- **Single window, WPF, MVVM-ish.** `DockWindow` is borderless, `AllowsTransparency=true`, topmost,
  `ShowInTaskbar=false`. The dock bar is a translucent rounded `Border`; icons are absolutely
  positioned in a `Canvas` (ItemsControl with a Canvas ItemsPanel; container style binds
  `Canvas.Left/Top/Width/Height` to the item VM's `X/Y/RenderSize`).
- **Window sizing**: binding `Window.Width/Height` to the VM is unreliable in WPF — the window
  **mirrors `DockViewModel.WindowWidth/Height` from code-behind** (`ApplyWindowSize`, via
  `PropertyChanged`). Don't reintroduce the XAML bindings.
- **Items are composed from sections** in `DockViewModel`: `Start + taskbar apps + separator (only
  if any minimized) + minimized tiles`, then `ReconcileItems` updates the ObservableCollection
  in place (move/insert/remove minimally) so the 1 s refresh doesn't churn the UI or reset
  magnification. Reused tile VMs are keyed (`_appByKey`).
- **Magnification** (`DockLayoutEngine.Update`, run each frame from `DockWindow`'s
  `CompositionTarget.Rendering` loop that self-detaches when idle): per-icon scale from cursor
  distance via a raised-cosine falloff, smoothed; cumulative centered cell layout with neighbour
  displacement; bottom-anchored growth. Each cell advances by `baseSize*scale`; the **icon renders
  at `IconFill` (0.84) of its cell**, centered, so icons sit smaller within the bar. Thin bar; icons
  overflow above it. `Recompute()` sets window/bar geometry on item/settings changes.
  - **`IconFill` only means one size because icons are trimmed to their artwork first**
    (`ShortcutService.TrimToArtwork`, on the `LoadIconAsync` funnel). `Stretch="Uniform"` fits the
    whole BITMAP, and Windows icons disagree wildly about their transparent margin — measured: .exe
    artwork fills 100% of its canvas, MSIX app-list assets ~70% (Teams 70.5%, Windows Terminal
    72.7%), so packaged apps used to render ~30% smaller than the pins beside them. Don't add a
    second size constant to "fix" a mismatch; the trim is what keeps them equal.
  - **Hover is geometry-driven, not WPF `MouseLeave`.** `MouseEnter` (reliable on the opaque bar) kicks
    the loop off, but each frame recomputes `_hovering` from the real cursor (`GetCursorPos` →
    `PointFromScreen`, tested against the window footprint). On a transparent layered window the cursor
    crossing a fully-transparent overflow pixel spuriously fires `MouseLeave`, which would drop
    magnification and make the dock + hover labels jitter — geometry is stable. (Skipped during
    drag/resize, which pin `_hovering` themselves.)
- **Conventions**: C# 12, nullable enabled, file-scoped namespaces, ImplicitUsings. Short XML docs on
  public members; inline comments only where intent is non-obvious (esp. *why* a Win32 call behaves a
  way). Keep per-frame work off WPF layout passes. Shell icon extraction must be off the UI thread
  and the bitmap `Freeze()`d; `IShellItemImageFactory.GetImage` returns **E_PENDING (0x8000000A)** for
  uncached items — retry briefly.
- **Settings**: persisted at `%APPDATA%\Dockable\settings.json`. **Never revert the user's
  settings.json**; if you seed test data, restore it. (`Items` is legacy/unused; `PinnedApps` is the
  live pin list.)

### Settings schema (`DockSettings`)

| Field | Default | Meaning |
|---|---|---|
| `Edge` | `Bottom` | Dock edge (`DockEdge`; only Bottom implemented). |
| `Language` | null | UI culture code (`en`/`pt-BR`/`es`/`uk`/`zh-Hans`). null = resolve from OS → en, then persist. |
| `Theme` | `System` | `DockTheme` (System/Light/Dark; "System" shown as "Auto"). |
| `GlassEffect` | `LiquidGlass` | `GlassEffect` bar background: Simple (translucent) / Acrylic / LiquidGlass. |
| `MinimizeEffect` | `Genie` | `MinimizeEffect`: Suck / Scale / Genie. |
| `EffectSpeed` | 1.0 | Minimize/restore speed multiplier (>1 faster, <1 slower). |
| `IconSize` | 48 | Base icon cell size (DIP). |
| `MaxIconSize` | 96 | Max magnified size. |
| `MagnificationRadius` | 160 | Cursor influence radius (DIP). |
| `MagnificationEnabled` | true | Fisheye magnification on/off. |
| `TaskbarVisibility` | `Never` | `TaskbarVisibility`: Always (visible) / Auto (native auto-hide, reveal on hover) / Never (fully hidden — default; the dock replaces the taskbar). Pre-launch state restored on exit/crash/kill (watchdog). |
| `ShowMenuBar` | true | Show the macOS-style top menu bar (reserves a strip at the top of the primary monitor). |
| `ShowDockOnAllMonitors` | true | A dock on **every** display vs. the main display only. Extra displays get their own `DockWindow` (`IsSecondary`) + `DockViewModel` over the **same** `DockSettings` instance; `App.SyncDockMonitors()` owns their lifetime. See the multi-monitor section. |
| `AutoHideDock` | false | "Automatically hide and show the Dock" (Preferences + the dock menu's "Turn Hiding On/Off"). Implemented: the dock slides off its edge when idle (`HideProgress` DP animates, `PositionDock` applies the offset), reveals when the cursor presses a 2px edge sliver (`_autoHideTimer` 120 ms watcher; "activity" = hover, drags, flyout, any menu via `Mouse.Captured`, in-flight warps), and the **AppBar strip stays UNRESERVED the whole time it's on** (`ReserveAppBarSpace` unregisters and bails). |
| `HideOnFullscreen` | true | "Hide on fullscreen apps and games" (Preferences → Dock, under Auto-hide): fully hide the dock + menu bar (windows `Hide()`n, AppBar strips released — not just slid off-screen) while a full-screen/borderless-fullscreen app owns their monitor. Off = they stay visible over full-screen content. Gated in both `UpdateFullscreenState`s; the toggle's `ApplyHideOnFullscreen()` restores a hidden window immediately (bypasses the own-process foreground guard). |
| `ShowRunningIndicators` | true | Show the running-dot under apps with open windows. |
| `AnimateOpeningApps` | true | Bounce an app's icon when it gains a new window (ONE hop; the bounce is an icon-only RenderTransform — `BounceX/Y` set by the engine per edge — so the running dot stays put). Also gates the **attention bounce**: 3 hops when a window flashes its taskbar button (shell hook: `RegisterShellHookWindow` → `HSHELL_FLASH` 0x8006 in WndProc; repeat flashes don't restart a playing bounce). |
| `MinimizeIntoIcon` | false | Minimize into the app's dock icon instead of a separate tile. |
| `Items` | [] | Legacy (old drag-drop pins); unused. |
| `PinnedApps` | null | Dock-owned ordered pin list (launch paths). null = seed from taskbar once. |
| `AskReplicateTaskbarPins` | true | Prompt to replicate newly-pinned taskbar shortcuts onto the dock. |
| `AskAddToStartup` | true | Prompt (once) to add Dockable to Windows startup. |
| `SeededPreferencesPin` | false | Whether the built-in Dock Preferences pin was seeded once (so removal sticks). |
| `SeededDownloadsPin` | false | Whether the default ~/Downloads folder pin was seeded once (so removal sticks). |
| `PinnedPaths` | [] | Files/folders pinned to the right section (`PinnedPath`: path + per-folder SortBy/DisplayAs/ViewContentAs). |
| `KnownTaskbarPins` | null | Taskbar pins already seen/offered, so only new ones prompt. |
| `PinNames` | null | Friendly display names captured per pinned launch path. |
| `PinIcons` | null | Custom icon per pinned launch path → file name in the `%APPDATA%\Dockable\icons` cache (`PinIconCache`); set via the pin's "Change Icon…" menu (.png/.svg picker), cleared by "Reset Icon". Also covers the Start tile (sentinel key `dockable://start` — custom icon replaces the vector glyph via `ShowStartGlyph`/`ShowIconArea`) and the Dock Preferences tile (`dockable://preferences`). |
| `ShowSettingsInDock` | true | Show the built-in Dock Preferences tile. MIRRORS whether the Preferences pseudo-app is pinned (`PinnedApps` is the source of truth, reconciled at load); the Preferences toggle and the tile's "Keep in Dock" menu both go through `DockViewModel.SetShowSettingsInDock` and cross-sync (`SyncFromSettings` / `RefreshTaskbarApps`). |

---

## Feature areas (detail)

> Menu-bar and taskbar-visibility notes live in `.claude/rules/menu-bar.md` and `.claude/rules/taskbar.md`;
> they load automatically when Claude touches those files.

### Taskbar mirror + dock-owned pins
- The dock shows **Start + taskbar apps (pinned + running) + minimized tiles**. App data from
  `Interop/TaskbarApps`; refreshed every 1 s (running state) plus a `FileSystemWatcher` on the
  pinned folder. Reconciled in place; `IsRunning` shows a dot (offset below the icon).
- **Running (unpinned) apps are ordered by open order, not Z-order.** `EnumerateAppWindows` returns
  windows in Z-order (changes on focus), so each app VM is stamped with a monotonic `SeenOrder` when
  first created (`DockViewModel._appSeq`), and the unpinned group is sorted by it — stable across
  focus changes; reopening an app gives it a higher `SeenOrder` (further right).
- **Pins are dock-owned** (`DockSettings.PinnedApps`), **seeded once** from the real taskbar order on
  first run, then owned by the dock. Drag a pin to reorder (`MovePin`); drag-and-hold-steady to remove
  (`UnpinApp`, see Live drag); drop an external Explorer file to pin (`PinApp`, via `OnDrop`);
  right-click → Unpin (`UnpinApp`); **any app tile with a launch path** — pinned or just running —
  also offers Rename (label → `PinNames`) and **Change Icon… / Reset Icon** (a .png/.svg picked in a
  file dialog → imported into
  the `PinIconCache`, mapped in `PinIcons`, applied via `DockItemViewModel.CustomIconPath` which
  short-circuits `LoadIconAsync` before extraction). Both maps are keyed by launch path and applied
  to every tile on creation, so they never needed the pin; a running-but-unpinned app is exactly the
  case with no other way to fix a bad icon. `IdentifyWindow` therefore consults `RecordedPinName` for
  running apps too — otherwise the 1 s refresh would overwrite a rename on the next tick. The Start
  tile and the Dock Preferences tile
  offer the same Change/Reset Icon menu — Start under the `dockable://start` sentinel key, reset
  returning to the vector glyph / bundled Preferences glyph). **The Windows taskbar is never modified** — Windows blocks
  programmatic taskbar pin/reorder (verb removed since Win10; Explorer owns the `Taskband\Favorites`
  blob), so we deliberately went dock-owned (user's choice).
- **Seeding reads pin order** from the `HKCU\…\Explorer\Taskband` → `Favorites` REG_BINARY:
  format is `[1 flag byte 0x00][DWORD pidl-size][pidl]` repeated; each PIDL resolved via
  `SHGetPathFromIDList` to its `.lnk`. Authoritative and includes pins under
  `User Pinned\ImplicitAppShortcuts\…` (e.g. Steam) that the `TaskBar` folder misses.
- **Matching running windows to pins** (`Interop/PinMatcher`) uses several strategies because no
  single one works: (1) exact exe path (Chrome/Brave); (2) window AUMID == pin AUMID — window AUMID
  via `SHGetPropertyStoreForWindow`+`PKEY_AppUserModel_ID`, pin AUMID via Shell.Application
  `System.AppUserModel.ID`; (3) window exe in a strict **subfolder** of the pin's app dir (excluding
  `%WINDIR%`) — Steam's `steamwebhelper.exe` under the Steam dir vs the `steam.exe` pin; (4) Explorer
  special-case — pin AUMID `Microsoft.Windows.Explorer` → match `explorer.exe` (Explorer windows
  report no AUMID and the pin has no exe target). Each window is claimed by the first matching pin.
- **Live drag** is a CUSTOM mouse-capture drag (NOT OS `DoDragDrop`, which is modal/can't animate):
  draggable items are taskbar apps + minimized tiles. `MaybeStartDrag` (movement) or a 500ms
  long-press (`OnDragSteadyElapsed`) → `StartDrag` → `BeginItemDrag` + `CaptureMouse`. While dragging,
  the **in-canvas tile is hidden** (`IsDragging` trigger sets Opacity 0) and a **free-roaming `DragGhost`
  popup** shows the icon following the cursor anywhere on screen (positioned via
  `HorizontalOffset/VerticalOffset` relative to `RootCanvas`); the engine still reserves a gap at
  `DragInsertIndex` so the others part. Magnification is suppressed during drag.
- **Hold-to-remove**: holding a dragged **pinned shortcut** steady for 500ms (`_dragSteadyTimer`,
  reset by motion past `SteadyEpsilon`) arms a red "Remove" tag in the ghost; releasing then unpins
  (`UnpinApp`). Only pinned shortcuts arm — the timer is gated by `IsRemovable`. On `OnDockMouseUp`:
  armed+pinned → `UnpinApp`; pinned dropped over the dock → `MovePin`; everything else (unpinned apps,
  minimized tiles, or dropped away) → no mutation → the tile **settles back** to its slot. `EndGhost`
  fades the ghost; `OnLostMouseCapture` cleans up an interrupted drag. External Explorer file drops
  still use OS `DragDrop` (`OnDragOver`/`OnDrop`).
- **Separator drag = resize** (`_separatorResize`): pressing a **separator** (cursor is `SizeNS` via a
  Grid style trigger) and dragging up/down changes `Settings.IconSize` (clamped to `[SizeMin,SizeMax]`
  = 12..64, same as the Dock Preferences slider). Uses `PointToScreen(...).Y` (screen px, immune to
  the bottom-anchored window moving up as it grows) → DIP via `GetDpi`; `RecomputeLayout` resizes
  live, `SettingsWindow.SyncSizeFromSettings()` keeps the open slider in sync, and `Save()` persists
  on release. Magnification + hover label are suppressed during the resize.
- **Drop a pinned shortcut on the Recycle Bin to remove it**: during the custom drag, hovering the
  Recycle Bin arms the "Remove" tag, and releasing there unpins (alongside the existing hold-steady
  hold-to-remove). Handled in `OnDockMouseUp` via `IsOverRecycleBin` (gated by `IsRemovable`).
- **Built-in "Dock Preferences" pseudo-app** (`DockItem.PreferencesLaunchPath` = `dockable://preferences`):
  a tile backed by the dock's own `SettingsWindow` rather than an external process. Seeded once as a
  pin to the right of the taskbar-seeded pins (`SeededPreferencesPin`), removable like any pin.
  `DockViewModel.UpdatePreferencesApp` injects it (pinned, or unpinned-while-open) into
  `RefreshTaskbarApps`; icon is `AppIcon.Preferences` (the bundled `Assets\settings.png`);
  `IsRunning` tracks `DockViewModel.PreferencesOpen` (set by the dock on open/close + a refresh).
  Click → `OpenDockPreferences` (open/focus) when closed; right-click → a dedicated `BuildPreferencesMenu`
  (Change/Reset Icon + Keep in Dock toggle — synced with `ShowSettingsInDock` — + Quit, which
  closes *only* the Preferences window). The window is `Topmost`.
- **Preferences minimizes into the dock** like any window (thumbnail tile or its own icon per
  `MinimizeIntoIcon`). The global minimize hook skips the own process, so `OpenDockPreferences`
  installs an `HwndSource` hook on the window and intercepts `SC_MINIMIZE`: it captures the window
  while still visible, minimizes instantly (transitions suppressed), then calls the shared
  `MinimizeToDock(hwnd, capture)` (the refactored core of `OnWindowMinimizing`). Because the tile
  tracks the real HWND (`DockViewModel.PreferencesHwnd` → its `Windows`), `FindAppForWindow`,
  into-icon landing, and click-to-restore all reuse the standard machinery; `ActivateOrLaunch`
  only routes to `OpenDockPreferences` when the window isn't open (`Windows.Count == 0`).
- Rough edges: UWP/Store pins may not match (no exe target); elevated apps' exe path is unreadable
  unless Dockable is elevated; a minimized app shows both a running dot AND a genie tile.

### Docking behavior + multi-monitor/DPI
- **Default: always visible** — `ApplyBehavior` registers an AppBar (`AppBarManager`) to reserve a
  strip at the configured `Edge` and positions the dock there. `PositionDock` centers on the
  monitor; when the taskbar is hidden the dock anchors to the **full monitor bottom** (not the
  work-area bottom).
- **Auto-hide (`AutoHideDock`, off by default)** — the macOS-style hide/reveal returned (it was
  removed once; don't trust older notes): when on, the dock slides off-screen along its edge after
  ~600 ms of no interaction and slides back when the cursor presses a **2-physical-px sliver** at
  that screen edge. Mechanics: `HideProgress` DP (0→1) animated by `SlideDock`, applied as an
  offset inside `PositionDock`; `_autoHideTimer` (120 ms) is both the idle watcher (activity =
  hover, drags, flyouts, ANY menu via `Mouse.Captured`, in-flight warps) and the edge watcher
  (GetCursorPos-based, so it works mid-OLE-drag). **The AppBar strip stays unreserved the whole
  time auto-hide is on** — even while revealed (`ReserveAppBarSpace` unregisters and bails); the
  dock overlays maximized windows like macOS. Toggled from Preferences or the dock menu's "Turn
  Hiding On/Off" → both call `DockWindow.ApplyAutoHide()` (internal).
- **The window's main-axis size is PINNED to the full monitor edge** (a fixed strip;
  `DockLayoutEngine.SetFixedMainExtent`, fed by `PositionDock` from `Monitors.ForWindow` on every
  reposition — monitor/DPI/edge changes are the only resizes). History: the size used to track the
  max-magnified content and step (then glide) on tile add/remove — but ANY per-frame window
  resize+recenter desyncs the native rect from the layered bitmap for a frame (visible jitter, even
  eased). With the strip pinned, tiles growing in/out animate purely in canvas coordinates, which
  render atomically. Transparent strip pixels are click-through (per-pixel layered hit-testing), so
  the wide window doesn't eat input — but **the cursor hover test can't be "inside the window"
  anymore**: the engine publishes `DockViewModel.HoverExtentMain` (the max-magnified content extent,
  centered) and `OnRendering` tests that footprint. The drag-gap width `_gapExtent` still eases; the
  unpinned glide path survives only for the pre-first-`PositionDock` window. Related invariants:
  `Recompute()` must NEVER reset `CurrentScale`s or step with a forced no-hover — it runs mid-hover
  (departed tile finalizing, 1 s refresh), and doing so blinked the magnification off/on under the
  cursor (width jitter); its nominal step reuses the engine's last-seen cursor state
  (`_lastMouseMain`/`_lastHovering`). `Recompute()` returns true while anything still needs frames
  and `DockViewModel.RecomputeLayout` raises `AnimationRequested` (→ `HookRendering`) so appear/gap
  eases finish even with no hover/bounce/drag. At **startup**, `StartTaskbarMirror` calls
  `ViewModel.SnapWindowSize()` + `SyncAcrylic()` after the first population so the dock and the
  glass capture rect are full-size before first paint.
- **AppBar reserves exactly the resting (un-magnified) dock**, not the taller window that holds
  magnified/overflowing icons. `ReserveAppBarSpace` reserves from the docked edge to the bar's **far**
  edge (`WindowHeight - BarTop` for Bottom, i.e. including the bar's small margin from the screen edge —
  *not* just `BarHeight`), so a maximized window abuts the dock with **no gap and no overlap** ("avoid
  the dock only and always, no margin on top"). It's **re-reserved whenever the dock is resized**: live
  on the Preferences Size slider (via `ApplyWindowSize`, which runs on every `WindowWidth/Height`
  change) and on the **drop** of a separator drag (`EndSeparatorResize`; deferred during the drag so
  maximized windows don't reflow each frame). The window stays full-size (so magnification can render
  above the bar) but is **clipped via `SetWindowRgn` down to the resting bar while idle**
  (`ApplyIdleRegion`) so the overflow area is click-through to windows underneath. Hovering clears
  the clip (`ClearWindowRegion`, on `OnMouseEnter`); it's re-applied when the render loop settles.
- **A dock per display** (`DockSettings.ShowDockOnAllMonitors`, **on by default**; Preferences → Dock).
  `App.SyncDockMonitors()` is the single owner: it pins the main dock to the main display and opens one
  extra `DockWindow { IsSecondary = true }` per other display (rebuilding them wholesale on change —
  it no-ops unless the display layout or the setting actually changed). It runs at startup, on the
  Preferences toggle, and on `SystemEvents.DisplaySettingsChanged` (marshalled to the UI thread).
  - `Monitors.All()` (`EnumDisplayMonitors`) returns every monitor's bounds in **physical px, main
    display first** — that ordering is the contract `SyncDockMonitors` relies on.
  - `DockWindow.PinToMonitor(rectPx)` moves the window with `SetWindowPos` (physical px, so it's exact
    under any per-monitor scaling) and **remembers the rect** (`_pinnedMonitorPx`). Every in-window
    monitor lookup goes through `MonitorInfo()` (pinned rect + live `GetDpiForWindow`) instead of
    `Monitors.ForWindow` — re-deriving the monitor from the window position could bounce a mixed-DPI
    dock between screens, since the monitor feeds the window size which repositions the window.
  - **`IsSecondary` owns nothing process-wide.** The tray icon, the minimize HOOKS, the thumbnail
    cache, taskbar visibility, the pinned-folder watcher, the one-time startup prompts,
    `SyncPreMinimizedWindows` and `Taskbar.Restore()` all stay on the main dock (`App.MainDock`, which
    is also what `SettingsWindow`/`MenuBarWindow` reach for). A secondary's `OpenDockPreferences`
    forwards to the main dock so Preferences stays single-instance.
  - **A window minimizes into the dock on ITS OWN display.** The process-wide hooks still land on the
    main dock, which then routes each window to `App.DockFor(hwnd)` (the dock whose pinned monitor
    rect contains that window's monitor centre; main dock as fallback). Every dock therefore owns its
    own `_busy` / `_iconMinimized` / `_minimizedSourcePx` / tiles / **pre-warmed animators** — two
    displays can warp at once — and `PruneStaleMinimized` + `RestoreAllMinimized` run per dock
    (a secondary closed by `SyncDockMonitors` releases its own windows via `OnClosed`).
    - **Restores go through `App.DockOwning(hwnd)`, not `DockFor`** — the dock actually holding the
      window (a window can be dragged to another display while it sits minimized, and with
      `MinimizeIntoIcon` *every* dock shows the same app icon). `RestoreQueueNext` resolves the owner
      per window and calls its `RestoreOwnedWindow`, so a tile/icon click, a hover-preview pick and
      Win+D's restore-all all warp out of the right screen.
    - `App.AnyDockHandles(hwnd)` is the guard against two docks claiming one window (which would leave
      two tiles for it); `App.AnyDockWarping()` drives the thumbnail cache's suspend, since every
      dock's warp renders on the one UI thread.
    - Still main-dock-only: `SyncPreMinimizedWindows` (an already-iconic window's `MonitorFromWindow`
      reports (-32000,-32000) → the primary, so startup adoption can't tell which display it belongs
      to; marked with a `ponytail:` comment naming `rcNormalPosition`/`MonitorFromRect` as the fix).
  - **Shared settings, per-display layout.** Each dock has its own `DockViewModel` (layout depends on
    the monitor's width/DPI) but they all point at the *same* `DockSettings` object via
    `DockViewModel.AttachShared` — which deliberately skips `Load()`'s store read and one-time seeding.
    `DockViewModel.Save()` raises `SettingsSaved`; `App.ScheduleDockReapply` **debounces it 150 ms**
    and then calls `DockWindow.ReapplySharedSettings()` on every dock (theme, glass, layout, docking,
    auto-hide). The debounce matters — Preferences sliders save on every tick, and
    `ApplyGlassEffect` restarts the backdrop capture thread (hence the extra `_appliedGlass`
    change-guard in `ReapplySharedSettings`). Pins / running state / pinned paths need no plumbing:
    every dock runs its own 1 s `RefreshTaskbarApps` tick.
  - **Capture-friendly mode is fanned out** (`App.SetDocksCaptureFriendly`): the Liquid Glass capture
    exclusion is a *per-window* display affinity, and the snip gesture hook + the 1 s exit poll only
    run on the main dock — without the fan-out a Win+Shift+S on display 2 would come out with a
    dock-shaped hole in it.
  - Perf note: with Liquid Glass on, **each** dock runs its own backdrop capture thread.
- Per-monitor-v2 DPI aware. Positioning is exact when the displays share a scale factor; **mixed-DPI**
  placement is still approximate (a known TODO — `ComputePlacement` converts the monitor rect to WPF
  DIPs with that monitor's own scale).

### Minimize / restore (Phase 3)
- Minimizing any normal window is replaced with a custom effect into a dock **thumbnail tile**
  (`DockItemKind.MinimizedWindow`, appended after a separator, not persisted). Clicking the tile
  reverses the effect and restores; a window minimized into the dock is also restored by clicking its
  app-group icon (`ActivateOrLaunch` handles iconic windows).
- **Two interception paths**:
  - **Pre-emptive (preferred, no flash) — `Interop/MinimizeInterceptHook`.** There's no pre-minimize OS
    event, so low-level hooks catch the *gesture* and paint frame 0 **before** the OS minimizes:
    `WH_MOUSE_LL` on the **minimize button** (`WM_NCHITTEST==HTMINBUTTON`, with a
    `DWMWA_CAPTION_BUTTON_BOUNDS` left-third fallback for custom title bars like File Explorer / Windows
    Terminal that report HTCAPTION/HTCLIENT) and `WH_KEYBOARD_LL` for **Win+Down** (minimizes even a
    maximized window — a single press, not the OS two-stage) and **Win+M**. The mouse hook **arms on
    button-down and acts on button-up, swallowing both** — swallowing only the up would starve a native
    caption button's modal press loop and leave the mouse captured (dock goes unresponsive). Releasing
    off the button cancels (drag-off). Hover/highlight still passes (only button events are swallowed).
    Custom title bars that report neither fall through to the reactive path. Callbacks come on the UI
    thread; the dock defers the real work via `Dispatcher.BeginInvoke` so the hook returns fast.
  - **Reactive fallback — `Interop/MinimizeHook`, `EVENT_SYSTEM_MINIMIZESTART`.** Fires *after* the OS
    minimized (taskbar/menu/programmatic minimizes), so it may show a brief OS animation before the warp.
- **Intercepted (`windowStillVisible`) flow** (`InterceptedMinimize`→`MinimizeOneAnimated`): ensure the
  target is foreground (so the capture isn't occluded — raise + `ForegroundSettleMs` wait if it wasn't),
  take a **fresh** `WindowCapture.Capture`, `ShowAtSource` (frame 0), wait for that overlay frame to
  actually render (`AfterRendered`, 2 `CompositionTarget.Rendering` ticks) **then** minimize the real
  window behind it via `MinimizeAndFocusNext` — `SW_MINIMIZE` alone often hands activation to the
  topmost dock, so we pick the next real app window first and `SetForegroundWindow` it (Win+Down's
  next-window focus, matching the OS). `MinimizeToDock`/`MinimizeOneAnimated` take `onDone` +
  `focusNext`; `MinimizeOneAnimated` also takes `raiseIfNeeded` (raise+settle a non-foreground window
  before capture, vs. capture it as-is).
- **Win+M = sequential, no focus changes** (`OnMinimizeAllRequested`→`MinimizeListSequential`): enumerate
  windows in **Z-order (top first)** once, then minimize each in turn (`raiseIfNeeded:false`,
  `focusNext:false`). Walking top-down means each window is already the top-most non-minimized one, so it
  captures cleanly without raising, and we deliberately **don't** focus-next — Win+M ends on the desktop,
  and a `SetForegroundWindow` from our (non-foreground) process would be rejected into a **taskbar-button
  flash** rather than a focus. (An earlier foreground-cascade that focused each next window caused exactly
  that flash.)
- **Concurrent minimizes** (rapid clicks, the cascade, etc.) share the single overlay; each animator's
  `FinishCurrent()` runs at the start of a new play to **finalize the in-flight one** (invoke its pending
  `onCompleted` so the previous window snaps to its tile and is freed from `_busy`) — otherwise the
  stomped animation's callback was lost and its tile stayed stuck in `_busy` (unresponsive).
- **Hover previews** (`WindowPreview` + `Interop/DwmThumbnail`): hovering an app icon that has open
  windows for `PreviewDwellMs` (450 ms) opens a flyout of LIVE window thumbnails above the dock window
  (above the whole window, not the icon — magnified icons + hover labels overflow well past the bar);
  clicking one raises that window, or restores it with the reverse warp when a dock holds it
  minimized (`ActivateWindow` → `App.DockOwning`, so it warps out of the display that holds it). Capped at 5 cells. Both the dwell check and the close poll (`PreviewWatchMs`, 150 ms) are
  **geometry-driven** (`IsCursorOverItem` + `WindowPreview.ContainsCursor`) for the usual reason plus a
  new one: the flyout is a separate window, so WPF sees no leave event for the gap between them —
  and closing needs 2 consecutive away-ticks or the cursor gets cut off mid-transit. The flyout counts
  as auto-hide activity, and any mouse-down on an icon closes it.
- **Stale representations are polled away** (`PruneStaleMinimized`, from the 1 s tick, on every dock):
  any tracked window that is gone (`!IsWindow` — an app closing while minimized raises no event we hook,
  so its tile used to linger until clicked) or no longer iconic (a missed `EVENT_SYSTEM_MINIMIZEEND`)
  has its tile / `_iconMinimized` entry dropped. `_busy`-guarded so an in-flight warp isn't pruned.
- **External restore sync:** `MinimizeHook` also raises `WindowUnminimized` on `EVENT_SYSTEM_MINIMIZEEND`;
  when a tracked window is restored by the taskbar/Alt+Tab/the app itself, `OnWindowUnminimized` drops the
  now-stale tile/tracking (no reverse warp — transitions are suppressed so the OS restore is instant,
  which is what those gestures should look like). Guarded by `_busy` so our own click-to-restore is unaffected.
- **Adopt pre-existing minimized windows at startup** (`SyncPreMinimizedWindows`, after the first
  refresh): windows already minimized before launch get the per-setting representation — a tile, or
  (in `MinimizeIntoIcon` mode) owned by their app icon. They can't be captured, so the **app icon stands
  in** for the missing thumbnail. (Startup-only; windows that *start* minimized later aren't adopted.)
- **Restore-all on exit:** `RestoreAllMinimized` (first thing in `OnClosed`) un-minimizes every
  dock-minimized window (`RestoreNoForeground`) and re-enables the transitions we suppressed, so the
  user isn't left with windows stranded behind a gone dock. (A hard force-kill skips it.)
- **Three effects**, chosen by `DockSettings.MinimizeEffect` (`Suck`/`Scale`/`Genie`, set via the
  settings window's "Minimize windows using" combo). `DockWindow.MinimizeAnimator` maps them to two
  pre-warmed `IMinimizeAnimator`s: **Scale** → `ScaleAnimator` (capture scales down/translates to the
  tile); **Suck** and **Genie** → `GenieAnimator` (a 3D mesh warp) with its `Style` set to the
  hard-funnel (Suck) or staggered-flow (Genie) curve. `EffectSpeed` is applied as the animator's
  `SpeedMultiplier`. Both overlays are pre-warmed at startup. Optionally, `MinimizeIntoIcon` makes a
  window minimize into its app's dock icon instead of a separate thumbnail tile (falls back to a tile
  when the app has no dock icon).
- **Capture timing is the crux:** capturing at minimize is too late (window already minimized →
  black sliver). `Genie/WindowThumbnailCache` proactively keeps a recent full capture of each window
  **while it's visible** (`EVENT_SYSTEM_FOREGROUND` + ~1.2 s refresh; capture debounced ~180 ms so
  the window settles on top first, else occluders get grabbed). Capture is a `BitBlt(CAPTUREBLT)`
  screen-grab (`Genie/WindowCapture`) — NOT `PrintWindow`, which returns a black client area for
  composited apps. The grab uses the window's `DWMWA_EXTENDED_FRAME_BOUNDS` (not `GetWindowRect`) so
  the drop-shadow / invisible-border margin is excluded, and bakes an antialiased rounded-corner alpha
  mask (≈8 DIP, DPI-scaled) into a `Bgra32` bitmap so the warp follows the window's actual shape. Both
  animators render on transparent overlays (`AmbientLight` + `DiffuseMaterial` / `Image`), so the
  transparent corners composite through. The **restore** warp must size the bitmap to the *same*
  captured (extended-frame) rect — `DockWindow` stashes it in `_minimizedSourcePx` at minimize and
  reuses it instead of `GetWindowPlacement`'s `rcNormalPosition` (which is larger by the invisible
  border, and would make the window look enlarged just before it lands).
- **OS animation suppression:** the cache disables `DWMWA_TRANSITIONS_FORCEDISABLED` on the current
  foreground window (re-enabling the previous) so minimize is instant and the genie doesn't race the
  OS shrink. Side effect: the focused window's maximize/snap are also instant while focused.
- `Genie/GenieAnimator` is a **pre-warmed, reused** overlay (one click-through, topmost,
  transparent WPF window resized to the active monitor per play; shown via Visibility) — building a
  fresh WPF+3D window per minimize cost 10s–100s ms and was the visible "blink". It maps the capture
  onto an animated `MeshGeometry3D` grid (Viewport3D + orthographic camera) warping into the dock;
  `reverse:true` for restore. Only one genie at a time (shared overlay).
- **The warp lands just above the ICON'S TOP EDGE, not at its centre** (`LandingY` =
  `tileTop - LandingLiftDip`, used by both `TileScreenCenter` and `TileRestingScreenCenter`, so
  minimize and restore share one anchor): the window is swallowed at the mouth of the dock instead of
  being buried inside the icon. The anchor is the icon's top, **not the bar's** — magnification grows
  icons upward out of the bar, so a hovered icon's top sits well above `BarTop` and a bar-anchored
  landing would end inside a zoomed icon. `TileScreenCenter` reads the LIVE (magnified) `tile.Y`;
  `TileRestingScreenCenter` derives the top from `RestingCenterOf` minus `TileWidthOf/2` (a tile still
  growing its slot in renders ~2 DIP tall). X still tracks the tile. Bottom-edge only.
- **Every effect dissolves over its last stretch** (`OverlayAnimatorBase.FadeStartWarp` 0.88 →
  opacity 0 at warp 1, applied by `ApplyFrameAndFade`). All of them end with their geometry collapsed
  onto one landing point, so the final frames are a degenerate flat sliver and the hide after it is a
  hard cut — both read as a glitch, and no choice of end width fixes either (tile-width read as
  splayed, 2 DIP read as a needle). Opacity is a function of warp ALONE, so a restore mirrors for
  free (it emerges from the dock). **Every frame-painting path must go through `ApplyFrameAndFade`** —
  a play that ended faded out would otherwise leave the next `ShowAtSource` invisible, and that frame
  is what covers the real window while it minimizes behind it.
- **A NECK, not a cone** — the shape trap this warp keeps falling into. `rowWidth` interpolating on
  the same eased progress as the descent makes the whole path taper evenly, which reads as a pointed
  funnel no matter what end width you pick. `WidthPinchPower` (2.5) raises the width's parameter
  (`wE = e^2.5`) so the body keeps nearly its full width for most of the travel and pinches only in
  the last stretch, at the dock — the Dock's short neck under a full-width body. The sideways slide
  (`rowCenterX`) deliberately stays on plain `e`. Neck width is `NeckWidthFactor` 0.85 of the tile
  (floor 10 DIP): about the icon's own width, since the Dock swallows the sheet into something
  icon-sized rather than converging to a tip. `TileWidthOf` also feeds **ScaleAnimator**, where its note
  still matters: a just-added tile is still growing its slot in (AppearScale ≈ 0), so
  its live RenderWidth reads ~2 DIP at play time — `TileWidthOf` divides by AppearScale mid-grow to
  project the settled width.
- Orchestration in `DockWindow` (`InterceptedMinimize`/`MinimizeOneAnimated`/`MinimizeToDock` for
  minimize; `OnWindowMinimizing` reactive fallback; `RestoreMinimized`/`RestoreWindowAnimated` for
  restore); `_busy` set guards re-entrancy. Rough edges: minimizes that fall through to the reactive
  path (taskbar/menu/programmatic, or custom title bars the button-hit-test misses) can still flash the
  OS animation briefly; DRM/protected windows capture black; pre-minimized/elevated windows have no
  thumbnail (app icon stands in); 
  a window already minimized BEFORE launch is adopted onto the main dock regardless of its display.

### Pinned files & folders (macOS right-section stacks) — Grid/List TODO
- **Files and folders pin to the dock's right section** (after the apps separator; the section
  orders minimized thumbnails FIRST, then the pinned files/folders, then the Recycle Bin).
  They can't live among the app shortcuts. Persisted as `DockSettings.PinnedPaths`
  (`Models/PinnedPath`: path + per-folder `SortBy`/`DisplayAs`/`ViewContentAs` enums, macOS defaults
  Date Added / Stack / Automatic); kinds `DockItemKind.PinnedFolder` / `PinnedFile`
  (folder-ness = `Directory.Exists` at VM creation).
- **Pinning**: an Explorer drop routes by type (`DockWindow.IsAppLike`): directories + documents →
  `PinPath` (inserted at the previewed right-section slot); launchable extensions
  (.exe/.com/.bat/.cmd/.scr/.msi/.appref-ms/.url) → the existing `PinApp` app-pin path. The
  **drop-preview gap is section-aware**: `ExternalDragTargetsPathSection` classifies the payload
  once per drag (cached — DragOver fires continuously; any app-like item → left/apps gap, else
  right/paths gap), threaded through `UpdateExternalDrop(main, pathSection)` to the engine, and
  `ComputeDropPathIndex` makes the drop land exactly where the gap showed. Remove via context menu →
  `UnpinPath` (tile shrinks out through the shared `_departing` machinery). **Path tiles are full
  drag candidates**: reorder within the right section (the engine's drag gap goes path-aware —
  `PathsBeforeCursor`/`PathGapSlot`, `DragInsertIndex` then means an index into `PinnedPaths` and
  `MovePinnedPath` commits), hold-steady "Remove" + drop-on-Recycle-Bin unpin (removes the PIN, not
  the file). **Fan/grid rows drag out as real files** (`AttachFlyoutDrag`: OS `DoDragDrop` with
  FileDrop): dropping on the dock pins to the right section as-is (`_flyoutDragPath` flags the
  in-process drag so OnDrop skips the app-vs-path routing), Explorer copies, the dock's bin
  recycles; tail cells don't drag; the flyout retracts automatically (the OS drag steals the
  light-dismiss capture). **Flyouts follow their tile** (`_fanAnchorFollow`: PropertyChanged on the
  tile's X/RenderWidth re-anchors `FanPopup.HorizontalOffset` as magnification drifts icons); the
  grid balloon has a **bottom-center callout triangle** in its tint aiming at the tile. GOTCHA:
  `OnMouseMove` must `ClearWindowRegion()` (flag-guarded) — while a flyout holds capture, the dock
  never gets MouseEnter, so the idle clip couldn't be cleared and magnified icons rendered cut off
  at the bar top.
- **Click opens via shell** (`DockItemViewModel.Activate`): folders in File Explorer, files with
  their default app.
- **Context menus** (`BuildFolderMenu`/`BuildFileMenu`): folders get macOS's full menu — gray
  section headers (disabled `MenuItem`s) with single-select check-marked choices for **Sort by**
  (Name/Date Added/Date Modified/Date Created/Kind), **Display as** (Folder/Stack), **View content
  as** (Fan/Grid/List/Automatic), then Options ▶ (Remove from Dock / **"Show in File Explorer"** —
  user-specified wording, key `Menu_ShowInFileExplorer`, distinct from the app menu's
  `Menu_ShowInExplorer`) and `Open "Name"` (`Menu_OpenNamed`, a `string.Format` template). Files get
  just Options ▶ + Open.
- **Stack tile** (`Display as: Stack`, the default): `Shell/StackIcon.RenderAsync` composes the
  folder's top-10 items' icons into one `RenderTargetBitmap` — a bottom-anchored cascade, top-of-sort
  item in front, each behind it peeking ~6px (of 256) higher (front icon ≈80% of the tile, near
  dock-icon size). Ordering comes from
  `Shell/FolderContents.GetSorted` (files+subfolders, hidden/system skipped): Name = culture-aware
  alphabetical on the display name; **Date Added ≈ creation time** (NTFS has no true macOS
  "date added"; copies re-stamp creation, same-volume moves don't) newest-first; Date Modified /
  Created newest-first; **Kind** = the shell's friendly type name (`SHGetFileInfo` SHGFI_TYPENAME,
  CsWin32) alphabetically, then name within each kind. The stack recomposes on Sort by / Display as
  menu picks and when the folder's **mtime** drifts (cheap probe on the 1 s refresh — catches direct
  child add/remove/rename, NOT a child's content edit, so a Date Modified stack can go stale).
  `.svg`/`.svgz` items render their actual artwork via `Shell/SvgIcon` (SharpVectors) instead of the
  shell's generic icon.
- **Fan** (`View content as: Fan`): clicking the folder opens `FanPopup` (its own hwnd — immune to
  the dock's idle `SetWindowRgn` clip; `StaysOpen=False` dismisses on outside click) instead of
  Explorer; built/animated in code (`OpenFolderFan`). Slot 0 = bottom = top of the stack; entries
  rise from the tile bottom-up with an 18 ms stagger + subtle rightward arc, **always fading in as
  they start** (the 160 ms fade finishes while the 280 ms rise still travels). Each row (icon + label together) is
  **progressively rotated** — 0° at the bottom up to the arc's own tangent
  (`atan(2·FanArcPerSlot·k / FanSpacing)`) at the top, pivoting on the icon center; the rise
  translation applies after the rotation so entries travel straight up. Labels sit to the **LEFT of
  the icons** — rows are right-anchored on the icon column (all rows measured up front; the widest
  label sets the canvas extent + popup offset so icons stay on the arc), rotation pivots on the icon
  at the row's right end, and `FanTopPad` gives headroom for rotated labels swinging up. Labels show
  the **real file name with extension** (folders have none) in a **fully-rounded pill** (fixed
  `FanPillHeight` 26, radius = half) whose background is the theme's `LabelBgBrush` colour at ~80%
  alpha (resolved per open, so theme switches stick). Tail entry = a semi-transparent
  theme-following disc (`LabelBgBrush` @ ~80% fill, `LabelBorderBrush` ring, `LabelTextBrush` ↗) +
  "N more in File Explorer" (`Fan_MoreInExplorer`; `Fan_OpenInExplorer` when everything fit),
  which opens the folder. Click an entry → shell-launch + retract. **Dismissal is animated**
  (`BeginCloseFan`): the opening mirrored — entries sink back into the tile top-rows-first
  (ease-in, from their CURRENT offset so an interrupted opening reverses mid-flight) and **always
  fade out at the end**, vanishing as they land; the popup only really closes (`IsOpen=false`) on a
  timer after the longest retraction. To make that possible light-dismiss is **manual**: `StaysOpen=True` + a
  `CaptureMode.SubTree` mouse capture on `FanCanvas` (taken at Input priority after open); any
  outside mouse-down hits `OnFanOutsideClick` → retract, and a stolen capture
  (`OnFanLostCapture` — dock drag, other popup, Alt-Tab) retracts too. `BeginCloseFan` releases the
  capture immediately (retraction is purely visual); `_fanClosing` guards re-entry and is reset in
  `OnFanClosed`. Side effect of the capture: dock hover/magnification doesn't react while a fan is
  open (accepted).
- **Grid** (`View content as: Grid`): same flyout popup/dismissal machinery (`_fanIsGrid` flags the
  mode) but the content is a **balloon** — `Border` CornerRadius 24, theme-tinted translucent bg,
  holding a `ScrollViewer`+`WrapPanel` of **ALL** items (not top-10) in sort order, 94px icons with
  the name (extension incl., ≤2 lines + ellipsis) beneath, plus an "Open in File Explorer" tail cell
  (transparent circle, 2px `LabelTextBrush` ring, ↗). Columns = `ceil(sqrt(n))` capped at 8 (5 files
  + tail → 3×2); ≥7 rows scroll (6 visible max, also capped to the space above the dock). Opens by
  **scaling out of the folder tile** (ScaleTransform 0.15→1, origin bottom-center + quick fade) and
  closes with the mirror shrink. NOTE: `ShortcutService.ConvertHBitmap` reads pixels via
  **GetDIBits with an explicit top-down target** — never infer row order from the source DIB's
  biHeight sign (some shell paths mislabel it; PNG/ICO thumbnails + folder icons came out upside
  down). The scrollbar drag steals the subtree capture — `OnFanLostCapture`
  ignores capture moving to a `FanCanvas` descendant and `OnFanPreviewMouseUp` retakes it on
  release (otherwise dragging the scrollbar would dismiss the balloon).
- **Automatic** (`View content as`, the default): fan for ≤9 items, grid for 10+ —
  `OpenFolderFlyout` enumerates once then dispatches to `ShowFolderFan`/`ShowFolderGrid`. **List**
  (`OpenFolderListMenu`): the folder opens as a plain `ContextMenu` above the tile — one row per
  item (20px icon + full name; a `TextBlock` header so "_" stays literal instead of becoming an
  access key), then Separator / Options ▶ (the shared `AddFolderConfigSections`: Sort by,
  Display as, View content as — same sections as the right-click menu, minus its Options + Open) /
  Open in File Explorer. WPF auto-scrolls menus taller than the screen. The user's **Downloads folder is seeded as a pin on first run**
  with Sort by **Date Added** + View content as **Fan** (Display as keeps the Stack default);
  `SeededDownloadsPin` flag makes removal stick; resolved via `Interop/KnownFolders` →
  `SHGetKnownFolderPath(FOLDERID_Downloads)` since Downloads can be relocated. While the fan is open the folder's
  tile swaps to the **open-stack indicator** (a cached rendered bitmap: semi-transparent rounded
  square + dark downward chevron, `FanOpenTileIcon`), with the real icon stashed/restored on close
  (`_fanPrevIcon` — a stack recompose landing mid-fan would be stomped by the restore; rare,
  accepted). The toggle-click guard matters:
  StaysOpen=False closes the popup on the tile click's mouse-DOWN, so the mouse-UP checks
  `_fanLastClosed`/400 ms to not instantly reopen. Grid / List / Automatic still open Explorer —
  **user will direct those next**; fan math is Bottom-edge-only (like hover labels).

---

## Known decisions

- **All context menus are Win11-styled via `Themes/ModernMenu.xaml`** (merged in App.xaml —
  implicit ContextMenu/MenuItem/Separator styles restyle every code-built menu app-wide: dock,
  tray, menu bar, folder List view). Rounded 8px surface with an outer Margin for the drop shadow
  (needs the popup's transparency — `HasDropShadow=True`), one MenuItem template for both
  ContextMenu roles (check glyph and the Icon share the leading column; chevron + popup only for
  SubmenuHeader), scroll arrows via the `MenuScrollViewer` ComponentResourceKey. The `PopupMenu*`
  brushes are swapped at **Application scope** from `DockWindow.ApplyTheme` (near-opaque tint —
  real acrylic isn't possible on WPF popups; a deliberate approximation). The two cross-process
  menus (Tier-1 hosted HMENU app menus, the title-bar system menu) can't be styled and stay native.
- **The dock's empty-space / separator menu** (`BuildSeparatorMenu`) is macOS's Dock menu: Task
  Manager | sep | Turn Hiding On/Off (`AutoHideDock`), Turn Magnification On/Off (re-enabling bumps
  a stale `MaxIconSize` to 2× so "on" is never a no-op), Position on Screen ▶ Left/Bottom/Right
  (checkmarked), Minimize Using ▶ Genie/Suck/Scale (checkmarked) | sep | Preferences, About | sep |
  Quit. Picks sync an open Preferences window via `SettingsWindow.SyncFromSettings()`. The menu
  bar's empty-space right-click shows the four-item variant (Task Manager/Prefs/About/Quit).

- **Bar styling follows a Light/Dark theme** (`DockSettings.Theme` = `System`/`Light`/`Dark`,
  default System; **"System" is shown to the user as "Auto"**). Two pickers: the tray "Theme"
  submenu (Light/Dark/Auto) and the settings window's **Appearance** section (Light/Dark/Auto
  illustration tiles; the Auto tile is split half-light/half-dark). The settings window applies via
  the `Action<DockTheme>` callback (`DockWindow.SetTheme`) passed into it. The bar (`DockBackground`) and its
  theme-dependent elements bind to swappable `DynamicResource` brushes
  (`BarBackgroundBrush`/`BarBorderBrush`/`SeparatorBrush`/`RunningDotBrush`/`FallbackBgBrush`/
  `FallbackTextBrush`) plus the named `BarShadow` effect; `DockWindow.ApplyTheme()` sets them.
  `CornerRadius=24` both themes.
  - **Light** (`.macos-dock-light`): bg `#66FFFFFF`, border `#33FFFFFF`, shadow opacity `0.15`;
    dark dependents (separator `#33000000`, running-dot `#B3000000` = rgba(0,0,0,0.7),
    fallback `#1F000000`/`#CC000000`).
  - **Dark** (`.macos-dock-dark`): bg `#66242424`, border `#14FFFFFF`, shadow opacity `0.4`;
    light dependents (separator `#40FFFFFF`, running-dot `#CCFFFFFF` = rgba(255,255,255,0.8),
    fallback `#33FFFFFF`/white).
  - Running-dot (`.app-indicator`) is a 4px circle. It does NOT hop with the launch/attention
    bounce — the bounce is an icon-only RenderTransform (`BounceX/Y`), the container stays put.
  - Separator lines stop **`DockLayoutEngine.SeparatorEndInset` (9 DIP) short of each bar edge**,
    computed in the engine (orientation-agnostic — top/bottom on a horizontal dock, left/right on a
    vertical one). No XAML margins; tune the one constant.
  - **System** mode reads `Interop/SystemTheme.IsLight()` (registry `AppsUseLightTheme`) and
    re-applies on OS theme change via `WM_SETTINGCHANGE`/`ImmersiveColorSet` in `WndProc`.
  - The **settings window stays light** regardless (it models the light macOS Settings screenshot).
- **Icons cast a dual drop-shadow** (`filter: drop-shadow(0 4 8 /.18) drop-shadow(0 10 20 /.12)`).
  WPF allows one effect per element, so the icon `Image` is wrapped in a `Grid`: the outer Grid
  carries the wide soft shadow, the inner `Image` the tighter one. Shadow params are fixed DIP (not
  scaled with magnification). Watch perf: two GPU effects per icon, re-rendered each magnify frame.
- **Glass / backdrop blur is now implemented** as a **separate non-layered backdrop window**
  (`Interop/AcrylicBackdrop`), z-ordered just below the dock's layered icon window and clipped to the
  bar's rounded rect — this is the way around the known conflict (DWM accent-blur fills the whole
  window rect and ignores `SetWindowRgn`, which we rely on for the idle clip; the modern DWM
  system-backdrop needs a non-layered window). `DockSettings.GlassEffect` selects **Simple**
  (translucent, no backdrop window — lightest), **Acrylic** (Composition host-backdrop blur), or
  **LiquidGlass** (a runtime pixel shader — `Genie/RefractionEffect` — that does **rim refraction**
  (`DistortionAmount`, via a displacement map) plus a **frosted-glass blur** (`BlurRadius`, a 3×3
  weighted tap kernel stepped by real device pixels via WPF's `DdxUvDdyUvRegisterIndex`) over the
  captured backdrop; HLSL compiled at runtime by `Interop/ShaderCompiler`; falls back to Acrylic where
  unsupported).
- **Liquid Glass hides the dock from screen capture** (`SetCaptureExclusion` →
  `WDA_EXCLUDEFROMCAPTURE`, set while refraction is on) so the `BackdropCapturer`'s screen BitBlt
  doesn't refract the dock itself. That affinity is global to ALL capture APIs (Snipping Tool too),
  so a **capture-friendly mode** lifts it while the user is capturing: entered on Win+Shift+S /
  PrintScreen (observe-only watch in `MinimizeInterceptHook.ScreenSnipRequested` — must fire BEFORE
  the snip overlay grabs the screen, hence Send priority) or a snipping app (SnippingTool /
  ScreenClippingHost / ScreenSketch) coming foreground; exits via the 1 s tick once no visible,
  non-cloaked snipping-app window remains (the packaged Snipping Tool lingers SUSPENDED with cloaked
  windows — a process-exists test would never exit) + a 3 s minimum hold. While in the mode,
  `SetCaptureExclusion(true)` requests are downgraded (so ApplyGlassEffect re-runs can't re-hide),
  and the fullscreen hide ignores the snip overlay (it covers the whole monitor and would otherwise
  hide the dock with `HideOnFullscreen` on). The capturer itself (`EnterCaptureFriendly`) PROBES
  whether a plain SRCCOPY blit (no CAPTUREBLT) omits layered windows on this Windows build (three
  back-to-back grabs: CAPTUREBLT/SRCCOPY/CAPTUREBLT; the repeat detects a mid-probe backdrop change):
  if yes → glass keeps updating LIVE with SRCCOPY (the layered dock stays out of its own capture);
  if no/inconclusive → it freezes on the last uploaded frame until exit. Freeze is the safe verdict —
  a wrong "live" would feed the glass its own rendering (runaway feedback).
- **Internationalization is in-code** (no `.resx`/satellite assemblies): `Localization/LocData`
  holds per-language string tables, `Loc` is the runtime service, and the `{loc:Loc Key=…}` markup
  extension binds XAML text to `Loc.Instance[Key]` so a language change updates **live**. Code-built
  UI uses `Loc.T(key)`; menus that are built once (the tray menu) rebuild on `Loc.LanguageChanged`.
  Add a new string by adding the key to **every** table in `LocData` (English is the fallback).
  Brand/tech names ("Dockable", C#, WPF, CsWin32, …) and the author's name stay untranslated.
- **Perf invariants from the 2026-07 optimization pass** — keep these when touching the hot paths:
  - **The render loop is allocation-free per frame.** `DockLayoutEngine.Update` refills a reusable
    `_placed` scratch list (it never escapes the call — keep it that way); `UpdateGlassShape`
    early-returns when both refraction effects are null and assigns the two fields directly (no temp
    array). Don't reintroduce per-frame `new`.
  - **DPI is cached** (`DockWindow._dpi` via the `Dpi` accessor, refreshed by `OnDpiChanged`) for the
    per-frame SyncAcrylic/PublishGlassRect/UpdateGlassClip path, and SyncAcrylic threads its
    already-projected bar top-left into `UpdateGlassClip(Point?)`. PublishGlassRect projects a
    DIFFERENT point — don't "unify" it. Non-per-frame code may keep calling
    `VisualTreeHelper.GetDpi` directly.
  - **A warp in flight (`_busy`) quiets everything that competes for its frames** — routed through
    `DockWindow.BeginWarp`/`EndWarp`, which are the only places `_busy` is mutated. They pause the
    dock's own render loop (`OnRendering` early-returns), the thumbnail cache's BitBlt
    (`ShouldSuspend`) and EVERY display's Liquid Glass capturer (`App.SetDocksGlassSuspended` →
    `BackdropCapturer.Suspended`, which parks the thread on its last uploaded frame). The capturers
    matter most: a warp repaints the whole screen every frame, so their diff never short-circuits and
    each monitor pushes a full-screen upload onto the one UI thread the warp renders on. `BeginWarp`
    also reveals an auto-hidden dock — the warp aims at the tile's RESTING position
    (`ComputePlacement` ignores `HideProgress`), so a slid-off dock isn't there to land on.
  - **TaskbarApps.EnumerateAppWindows must stay cheap** — it runs every ~1 s plus on demand. Window
    identity (exe/AUMID) comes from `IdentityCache`; anything newly per-window-per-tick needs the
    same treatment. The dock's UIA/screen-reader names, minimize bookkeeping helpers
    (`IsWindowRepresented`/`DropMinimizedTracking`), and `RestoreQueueNext` are the single owners of
    their invariants — extend them rather than re-inlining copies.
  - **OverlayAnimatorBase parity traps** (if you touch the minimize animators): `_playSeq` bumps in
    Play/ShowAtSource/AnimateTo only, after the first ApplyFrame and before showing; FinishCurrent
    runs in Play + ShowAtSource, never AnimateTo; the frame cap's `progress < 1.0` clause is the
    never-skip-the-final-frame guarantee; CompleteRestoreHold invokes `done` first and checks
    `_playSeq` twice; MonitorHeight is set only by Play/ShowAtSource.

## Open TODOs

Implement the non-Bottom **Position on screen** edges; exact secondary-monitor placement; suppress the
running dot for an app whose only window is minimized (vs. its tile); UWP/Store pin matching; reclaim
work-area space when the taskbar is hidden; tune blind animation/size constants per user feedback.
