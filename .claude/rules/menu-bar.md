---
paths:
  - "src/Dockable/MenuBarWindow.xaml"
  - "src/Dockable/MenuBarWindow.xaml.cs"
  - "src/Dockable/ViewModels/MenuBarViewModel.cs"
  - "src/Dockable/Interop/AppMenu.cs"
  - "src/Dockable/Interop/Win32AppMenu.cs"
  - "src/Dockable/Interop/UiaAppMenu.cs"
  - "src/Dockable/Interop/TitleWatcher.cs"
  - "src/Dockable/Interop/KeyboardLayouts.cs"
  - "src/Dockable/Interop/SystemActions.cs"
  - "src/Dockable/Interop/QuickSettings.cs"
  - "src/Dockable/Interop/Notifications.cs"
  - "src/Dockable/Interop/TrayOverflow.cs"
---

### macOS-style menu bar (top AppBar) — on by default (opt-out)
- Enabled via `DockSettings.ShowMenuBar` (Dock Preferences toggle or tray "Show menu bar"). **App owns
  the window's lifetime** (`App.SetMenuBarVisible`): the dock's `SetShowMenuBar` persists the setting and
  calls into `App`; the menu bar is created on first show and `Close()`d (which `Unregister()`s its AppBar)
  when toggled off. `ShutdownMode=OnExplicitShutdown`, so adding/closing this window is safe.
- `MenuBarWindow` is a sibling of `DockWindow` but **much simpler**: a flat full-width bar flush to the
  top of the **primary monitor** (window rect == bar rect, so **no `SetWindowRgn` clipping** and no
  magnification). Its own `AppBarManager(_hwnd, WM_USER+2)` reserves the top strip (`MenuBarHeight`, 28 DIP)
  via `ReserveEdge(DockEdge.Top, …)`; `WndProc` handles `ABN_POSCHANGED` (re-reserve) + `WM_SETTINGCHANGE`
  (re-theme when System). **Always acrylic**: reuses `AcrylicBackdrop` (corner radius 0), always shown
  (independent of the dock's Glass Effect setting). **No border.** `ApplyTheme()` paints the bar with the
  dock's own bar colours at **50% transparency** (light `#80FFFFFF`, dark `#80242424`) over the blur, and
  swaps `MenuTextBrush` to contrast per the Appearance theme (dark `#F2F2F2` / light `#1D1D1F`).
- Content: **leading** = the app's launcher glyph (`StartGlyphGeometry` in App.xaml — an ORIGINAL
  four-rounded-unequal-tiles mark, deliberately NOT the trademarked Windows flag, which third parties
  can't reproduce without a license; same geometry as the dock's Start tile, tinted with
  `MenuTextBrush`; click → an Apple-menu-style command `ContextMenu` built fresh each open:
  About This PC (`ms-settings:about`) / System Settings (`ms-settings:`) / Microsoft Store / **Recent
  Apps** submenu (open apps grouped by exe/AUMID; pick one → `WindowControl.ActivateAll` raises all its
  windows) / Force Quit \<focused app\> (`Process.Kill`) / Sleep / Restart… / Shut Down… / Lock Screen /
  Log Out \<user\>… — power/session items via `Interop/SystemActions`; the "…" ones confirm via
  `ConfirmDialog(showDoNotAskAgain:false)`) then the focused app's **friendly display name** — e.g.
  "Google Chrome" (not "chrome", not the window title), resolved by
  `DockViewModel.AppDisplayNameForWindow` (exposed as `MenuBarViewModel.AppDisplayName`) — **the same
  funnel that names the dock tiles**, so the bar and dock never disagree (a separate `Shell/ForegroundApp`
  resolver used to exist; it drifted — "Windows Terminal Host", raw "SnippingTool.exe" — and was deleted).
  Tile-first: a window represented by a dock tile returns the tile's label (which benefits from the
  identity cache's AUMID retries and remembered pin names); unrepresented windows derive the way tiles
  do: packaged AUMID → `shell:AppsFolder` name, else remembered pin name →
  `FileVersionInfo.FileDescription` → extension-less stem (never a raw "Foo.exe") → window title.
  The Recent Apps submenu and the startup seed use the same funnel. The bar tracks the last real app (`_appHwnd`, via `Interop/TitleWatcher`,
  skipping our own process — EXCEPT the Dock Preferences window, which is represented like any app
  under its dock-tile name `Window_DockPreferences`, with no mirrored menus: it's WPF/no HMENU, and
  UIA-scanning one's own process is deadlock-prone. The dock/menu-bar windows themselves stay
  skipped); at startup, when launching the dock made US foreground, it seeds from the top-most
  non-minimized app window in Z-order (`SeedFromTopmostAppWindow`) so the name + app menus show
  immediately instead of waiting for the first focus change; a represented window that dies (e.g.
  Preferences closed, focus fell to the dock) is dropped and re-seeded the same way. **Click the name** → the focused window's title-bar menu, reproduced by
  posting the non-client right-click messages (`WM_NCRBUTTONDOWN`/`WM_NCRBUTTONUP` with `HTCAPTION`) to
  the target so its **own** `DefWindowProc` shows the menu in its process (a cross-process
  `GetSystemMenu`+`TrackPopupMenu` doesn't work — the menu is owned by the other process; this also
  honours custom title-bar menus like Chrome's). `SetForegroundWindow(target)` first so it tracks/dismisses
  correctly. **Trailing** cluster (a clock
  `DispatcherTimer`, 1 s): a **tray-overflow chevron** (`TrayOverflow.Open` — synthesizes Win+B then Enter
  to open the "show hidden icons" flyout; reveals the auto-hidden taskbar), **Quick Settings** (`QuickSettings.Open` → Win+A),
  **Notifications** (`Notifications.Open` → Win+N), a clickable **keyboard layout** (`KeyboardLayouts`: shows the foreground
  thread's layout; click → a code-built `ContextMenu` of installed layouts → `Switch` posts
  `WM_INPUTLANGCHANGEREQUEST` to the foreground), and the **clock** (culture-aware, follows `Loc`).
- **Global app menus (two tiers):** after the app name, the bar mirrors the focused window's in-window
  menu ("File", "Edit", …) as clickable labels (`MenuBarViewModel.MenuEntries`, refreshed on foreground
  hwnd change with a `_menuGen` stale-guard). **Tier 1 (Win32/HMENU** — Notepad++, 7-Zip, most classic
  apps): `Interop/Win32AppMenu` reads the bar cross-process (`GetMenu`/`GetMenuString` — menus are shared
  USER objects, no injection) and a click hosts the app's REAL dropdown under the label:
  `WM_INITMENUPOPUP` is sent first (timeout-guarded, so lazily-populated menus are live), then
  `TrackPopupMenuEx(TPM_RETURNCMD)` tracks the foreign submenu from our window (foreground handoff +
  `WM_NULL` after, tray-menu style) and the picked id is posted back as `WM_COMMAND`. `MNS_NOTIFYBYPOS`
  menus track without `TPM_RETURNCMD` and relay `WM_MENUCOMMAND` instead; while a foreign popup is up,
  `MenuBarWindow.WndProc` relays nested-submenu `WM_INITMENUPOPUP`/`WM_UNINITMENUPOPUP` to the target
  (`Win32AppMenu.ForwardMenuMessage`). **Tier 2 (UIA fallback** — WPF/Electron/VS Code/Qt, no HMENU):
  `Interop/UiaAppMenu` finds a `MenuBar` control in the window's UIA tree (background thread; cached per
  HWND including "has none"; the non-client "System Menu Bar" — a lone "System" item, parented in the
  UIA TitleBar — is skipped since clicking the app's display name already opens that menu) and renders
  the same labels, but a click can only Expand/Invoke the app's
  OWN menu at its own location — UIA popups can't be re-anchored under our bar. No menu found →
  nothing rendered (Chrome/Edge/Office/UWP command-bar apps). Known limits: owner-drawn Win32 items
  render blank in a hosted popup (WM_DRAWITEM can't cross processes); elevated apps are UIPI-blocked;
  very long menus can overlap the trailing status cluster on narrow screens.
- **Right-click on empty bar space** shows the same dock-wide menu as the dock's empty space
  (Task Manager / Preferences / About / Quit) — `Bar_RightClick`, reaching the dock via
  `Application.Current.Windows.OfType<DockWindow>()` (`OpenDockPreferences` is internal for this).
- **Active-item pill highlight:** every interactive menu-bar item sits in a "pill" `Border` (fixed
  22px height, CornerRadius 11 = fully round; 8px padding offset by negative margins so the layout
  matches the padless positions exactly). `MenuHighlightBrush` (swapped in `ApplyTheme`: light
  `#17000000` almost-transparent black, dark `#26FFFFFF` almost-transparent white) is painted while
  an item is active: held open for menus we control (logo menu, keyboard layouts —
  `HighlightWhileOpen` clears on `ContextMenu.Closed`; Tier-1 app menus stay lit through the modal
  `TrackPopupMenuEx`), a ~350 ms `FlashPill` for actions whose flyout can't be tracked (OS flyouts,
  the cross-process title-bar menu, UIA menus). The Windows-logo pill is `StartPill` INSIDE the
  full-height `StartButton` hit area (edge-to-edge click target from the previous change).
- **Full-screen hide:** like the dock, the menu bar hides itself + its backdrop while a full-screen or
  borderless-fullscreen app (game/video) owns its monitor (`Interop/Fullscreen` test, re-checked on
  foreground change, the 1 s clock tick, and `ABN_FULLSCREENAPP`); it reappears when that window goes away.
  Two things make this robust (both windows): (1) while hidden, the **reserved AppBar strip is released**
  (`_appBar.Unregister()`, re-reserved on restore) — otherwise the game resizes to the work area to avoid
  our strip, stops covering the monitor, and the detection flip-flops; (2) `UpdateFullscreenState` ignores
  the case where **our own process is foreground** (`Fullscreen.IsForegroundOwnProcess`), so clicking the
  bar/dock over a game doesn't un-hide it.
- **Why there's no live system-tray icon replication:** a read-only spike on **Windows 11 25H2 (build
  26200)** found the classic notification-area path **gone** — `SysPager`/`ToolbarWindow32`/
  `NotifyIconOverflowWindow` don't exist, and the icons live in `explorer.exe` XAML islands that expose
  **0 invokable buttons** to UI Automation under `Shell_TrayWnd`. So cross-process `ToolbarWindow32` reads
  (and a UIA fallback) both yield nothing on current Windows. Per user decision, the tray area is instead
  the reliable, update-proof **Quick Settings + Notifications** flyout shortcuts above. (Probe scripts were
  one-off; not kept in the repo.) Caveat: the OS anchors those flyouts to the bottom-right tray — they
  can't be repositioned under the menu-bar icons.

- Files: `MenuBarWindow` (own `AppBarManager` WM_USER+2 + `AcrylicBackdrop` + `ApplyTheme`), `MenuBarViewModel`
  (shared DockSettings + live Title/KeyboardLabel/TimeText), `Interop/TitleWatcher`, `KeyboardLayouts`, `AppMenu`
  (AppMenuEntry model), `Win32AppMenu` (tier 1), `UiaAppMenu` (tier 2 — reads/invokes run OFF the UI thread, huge
  UIA trees are slow), `SystemActions`, `QuickSettings`/`Notifications` (synthesized Win+A / Win+N).
- `TrayOverflow.Open` must be called OFF the UI thread — it sleeps 200 ms between its Win+B and Enter chords.
