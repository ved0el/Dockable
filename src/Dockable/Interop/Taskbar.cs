using System.Runtime.InteropServices;
using Dockable.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable.Interop;

/// <summary>
/// Controls the Windows taskbar's native auto-hide state via the shell AppBar API. Auto-hide is
/// far safer than force-hiding the tray window: even if Dockable is force-killed without running
/// its exit handlers, the taskbar is left in a normal, usable auto-hide mode (it reveals when the
/// cursor reaches the screen edge) rather than vanishing entirely.
/// </summary>
public static class Taskbar
{
    private const string PrimaryClass = "Shell_TrayWnd";
    private const string SecondaryClass = "Shell_SecondaryTrayWnd";

    // ABM_SETSTATE/ABM_GETSTATE flags (not surfaced as CsWin32 constants).
    private const int ABS_AUTOHIDE = 0x1;
    private const int ABS_ALWAYSONTOP = 0x2;

    // The taskbar's auto-hide state when the app first touched it, so we can restore it on exit
    // (rather than assuming a default). TODO: this will be driven by an app setting later.
    private static bool? _originalAutoHide;

    /// <summary>Whether the taskbar currently has native auto-hide enabled.</summary>
    public static bool IsAutoHide()
    {
        try
        {
            var data = new APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
                hWnd = PrimaryTray(),
            };
            uint state = (uint)PInvoke.SHAppBarMessage(PInvoke.ABM_GETSTATE, ref data);
            return (state & ABS_AUTOHIDE) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Remembers the taskbar's auto-hide state at startup (once) so <see cref="Restore"/> can put
    /// it back to whatever the user had, instead of assuming a default.
    /// </summary>
    public static void CaptureOriginalState() => _originalAutoHide ??= IsAutoHide();

    /// <summary>The captured pre-launch auto-hide state (null until <see cref="CaptureOriginalState"/>
    /// runs) — handed to the out-of-process watchdog so it can restore after a force-kill.</summary>
    public static bool? OriginalAutoHide => _originalAutoHide;

    /// <summary>Restores the taskbar to the state captured by <see cref="CaptureOriginalState"/>.</summary>
    public static void Restore()
    {
        if (_originalAutoHide is bool original)
            SetAutoHide(original);
    }

    /// <summary>
    /// Applies the chosen taskbar visibility: Always (shown, auto-hide off), Auto (native auto-hide —
    /// reveals on edge hover), or Never (fully hidden, no hover reveal). Best-effort; non-fatal on failure.
    /// </summary>
    public static void SetVisibility(TaskbarVisibility mode)
    {
        switch (mode)
        {
            case TaskbarVisibility.Always:
                EnsureTrayWindowsShown();
                SetState(autoHide: false);
                break;
            case TaskbarVisibility.Never:
                // Auto-hide FIRST: an always-on-top taskbar keeps its work-area reservation even
                // while SW_HIDDEN, so the shell would stack the dock's AppBar strip on top of a
                // ghost taskbar-height strip — maximized windows then float a taskbar-height gap
                // above the dock. Auto-hide reserves nothing; SW_HIDE then keeps it off-screen.
                SetState(autoHide: true);
                Hide();
                // Explorer processes the state change asynchronously and re-shows the tray windows
                // while applying it, stomping the SW_HIDE above — re-assert once it settles.
                Task.Delay(750).ContinueWith(_ => Hide());
                break;
            default: // Auto
                EnsureTrayWindowsShown();
                SetState(autoHide: true);
                break;
        }
    }

    /// <summary>
    /// Turns the taskbar's auto-hide on (it slides away and reveals on edge hover) or off
    /// (restores the normal always-visible taskbar). Best-effort; non-fatal on failure.
    /// </summary>
    public static void SetAutoHide(bool enable)
    {
        // Undo any force-hide (auto-hide only works on a shown window) and (re)assert the state.
        EnsureTrayWindowsShown();
        SetState(enable);
    }

    private static void SetState(bool autoHide)
    {
        try
        {
            HWND tray = PrimaryTray();
            if (tray.IsNull)
                return;

            var data = new APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
                hWnd = tray,
                lParam = new LPARAM(autoHide ? ABS_AUTOHIDE : ABS_ALWAYSONTOP),
            };
            PInvoke.SHAppBarMessage(PInvoke.ABM_SETSTATE, ref data);
        }
        catch
        {
            // Setting the appbar state is best-effort; never let it crash the app.
        }
    }

    /// <summary>
    /// Fully hides the taskbar windows (SW_HIDE), overriding auto-hide so the taskbar can't reveal
    /// itself — e.g. while the Start menu opened from the dock is up. Restore by calling
    /// <see cref="SetAutoHide"/> (it re-shows the windows first); a crash/exit also restores via
    /// <see cref="Restore"/>.
    /// </summary>
    public static void Hide()
    {
        try
        {
            ForEachTrayWindow(hwnd =>
            {
                PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_HIDE);
                return true; // keep sweeping
            });
        }
        catch
        {
            // Best-effort; never crash the app over taskbar visibility.
        }
    }

    /// <summary>
    /// True while any taskbar window is on screen — the cheap "does anything need re-hiding?" probe the
    /// hide watcher polls. It sweeps EVERY tray window rather than asking <c>FindWindow</c> for one:
    /// a restarted Explorer leaves a stale 0x0 <c>Shell_TrayWnd</c> behind (measured on Windows 11 25H2:
    /// two of that class, from two different explorer.exe pids), and FindWindow matches by class in
    /// z-order — so it hands back whichever one happens to be higher. Landing on the ghost made this
    /// report "nothing visible" while the real taskbar sat on screen, and the watcher never re-hid it.
    /// </summary>
    internal static bool AnyTrayWindowVisible()
    {
        bool visible = false;
        ForEachTrayWindow(hwnd =>
        {
            if (!PInvoke.IsWindowVisible(hwnd))
                return true; // keep looking
            visible = true;
            return false;    // found one — stop the sweep
        });
        return visible;
    }

    private static void EnsureTrayWindowsShown() => ForEachTrayWindow(hwnd =>
    {
        PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_SHOW);
        return true;
    });

    /// <summary>
    /// The real primary taskbar — the first tray window with an actual rect. Explorer restarts can leave
    /// a 0x0 ghost of the same class behind, and an appbar message aimed at that one is a no-op.
    /// </summary>
    private static HWND PrimaryTray()
    {
        HWND found = default;
        ForEachTrayWindow(hwnd =>
        {
            if (found.IsNull)
                found = hwnd; // fall back to the first one if every candidate is degenerate
            if (!PInvoke.GetWindowRect(hwnd, out var rect) || rect.right - rect.left <= 0)
                return true;
            found = hwnd;
            return false;
        });
        return found;
    }

    /// <summary>Runs <paramref name="action"/> over every taskbar window (primary plus the per-monitor
    /// secondaries) until it returns false. Note the secondary bars do NOT reliably carry the
    /// <c>Shell_SecondaryTrayWnd</c> class on current Windows 11 builds — both classes are swept.</summary>
    private static void ForEachTrayWindow(Func<HWND, bool> action) =>
        PInvoke.EnumWindows((hwnd, _) =>
        {
            var cls = GetClassName(hwnd);
            if (cls != PrimaryClass && cls != SecondaryClass)
                return true;
            return action(hwnd);
        }, default);

    private static unsafe string GetClassName(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        int length = PInvoke.GetClassName(hwnd, buffer);
        return length > 0 ? new string(buffer[..length]) : string.Empty;
    }
}
