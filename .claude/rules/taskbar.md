---
paths:
  - "src/Dockable/Interop/Taskbar.cs"
  - "src/Dockable/Interop/TaskbarHideWatcher.cs"
  - "src/Dockable/Interop/TaskbarWatchdog.cs"
---

### Taskbar visibility + restore safety
- **Three states** (`DockSettings.TaskbarVisibility`, default **Never**), set from Dock Preferences →
  Taskbar (a combo: Always / Auto / Never) or the tray "Windows taskbar" submenu, applied by
  `DockWindow.SetTaskbarVisibility` → `Interop/Taskbar.SetVisibility`:
  - **Always** — `SW_SHOW` the tray windows + `ABM_SETSTATE, ABS_ALWAYSONTOP` (auto-hide off, visible).
  - **Auto** — `SW_SHOW` + `ABM_SETSTATE, ABS_AUTOHIDE`: the OS slides it away and reveals on edge hover
    (no custom timer).
  - **Never** — `ABS_AUTOHIDE` first, then `SW_HIDE` the tray windows (+ a 750 ms delayed re-hide:
    Explorer applies ABM_SETSTATE asynchronously and re-shows the tray while doing so, stomping the
    first SW_HIDE). It MUST be auto-hide, not always-on-top: an always-on-top taskbar keeps its
    work-area reservation even while SW_HIDDEN, so the shell stacked the dock's AppBar strip on a
    ghost taskbar-height strip and maximized windows floated ~48 px above the dock (measured; looked
    like "reserving for the magnified dock").
    **The hide only sticks because `Interop/TaskbarHideWatcher` re-asserts it** — auto-hide leaves an
    edge sensor, and the dock lives on that same edge, so Explorer re-shows the tray the first time the
    user reaches for the dock. The watcher **polls** (thread-pool `Timer`, 40 ms; `SW_HIDE` only when
    something is actually visible).
    **`Taskbar.cs` must never use `FindWindow` for the tray** — measured on Win11 25H2: an Explorer
    restart leaves a stale **0x0 `Shell_TrayWnd` behind from the dead instance's pid**, so there are
    two windows of that class and `FindWindow` (class match in z-order) hands back whichever is
    higher. Landing on the ghost made the probe report "nothing visible" while the real taskbar sat on
    screen, and it was never re-hidden. Every operation now sweeps `ForEachTrayWindow` (both classes —
    secondary bars do NOT reliably carry `Shell_SecondaryTrayWnd` on current builds); `PrimaryTray()`
    picks the first candidate with a real rect for the appbar-state messages. It used to be an `EVENT_OBJECT_SHOW` WinEvent
    hook scoped to Explorer; that was **measured not to fire** for these re-shows on Win11 25H2
    (build 26200) — the tray sat visible seconds after a forced `SW_SHOW`, and was visible again right
    after launch. Don't "optimize" it back into a hook without re-measuring. `Stop()` waits for an
    in-flight tick (`Timer.Dispose(WaitHandle)`) so switching to Always/Auto can't be stolen back by a
    stale re-hide. Consequence: the menu bar's **tray-overflow chevron** (`TrayOverflow`, Win+B) can't
    work in Never mode — it needs a visible taskbar to focus.
- **Restore on exit/crash/kill**: `Taskbar.CaptureOriginalState()` records the pre-launch auto-hide
  state; `Restore()` (clean exit via `DockWindow.OnClosed` + `App.OnExit`, and managed crash via
  `AppDomain.UnhandledException`) puts it back. **Hard kills** (Task Manager, `taskkill /F`,
  `Stop-Process`) skip all in-process handlers, so `App.OnStartup` also spawns the out-of-process
  `Interop/TaskbarWatchdog` (hidden `powershell.exe`, handed the captured state): it waits on the
  dock's PID and re-asserts that state (SW_SHOW all tray windows + ABM_SETSTATE) when the dock dies
  for ANY reason, then exits by itself. So even **Never** now survives a force-kill. The watchdog's
  restore after a clean exit is an idempotent no-op, and it skips the restore entirely if a new dock
  instance is already running by the time it wakes (quick-restart race, 750 ms grace).
- Note the **conflict to watch**: the dock also lives at the bottom, so revealing the native taskbar
  pops it up over/under the dock at the same edge. Accepted per user request (taskbar on demand).

- `TaskbarWatchdog`'s embedded Add-Type C# runs in PowerShell 5.1: keep it C# 5 (no `out var`, no `var`).
