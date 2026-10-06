using System.Diagnostics;
using System.IO;
using System.Text;

namespace Dockable.Interop;

/// <summary>
/// Spawns a tiny out-of-process watchdog that waits for THIS process to exit and then restores the
/// taskbar to its pre-launch state. In-process exit/crash handlers can't run when the dock is
/// force-killed (Task Manager "End task", <c>taskkill /F</c>, <c>Stop-Process</c>) — only a separate
/// process survives that. A hidden <c>powershell.exe</c> is used rather than a second Dockable.exe:
/// its image name differs, so a kill-by-name of Dockable can't take it down with the app, and the
/// portable single-file build needs no extra shipped binary. The watchdog quits by itself right
/// after restoring — or without touching anything, if a new dock instance is already running and
/// owns the taskbar by then. Clean exits and managed crashes still restore in-process; the watchdog
/// re-asserting the same state afterwards is harmless (idempotent).
///
/// If the watchdog itself dies while the dock is alive (killed, PowerShell crashed), it is respawned
/// — a dock without a watchdog would leave the taskbar hidden on the next hard kill.
/// </summary>
internal static class TaskbarWatchdog
{
    private const int MaxRespawns = 5;

    private static bool _originalAutoHide;
    private static int _respawns;

    /// <summary>Starts the watchdog for the current process. Best-effort — never throws.</summary>
    /// <param name="originalAutoHide">The taskbar's pre-launch auto-hide state to restore.</param>
    public static void Start(bool originalAutoHide)
    {
        _originalAutoHide = originalAutoHide;
        Spawn();
    }

    private static void Spawn()
    {
        try
        {
            string exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "Dockable";
            string script = Script
                .Replace("__PID__", Environment.ProcessId.ToString())
                .Replace("__EXENAME__", exeName.Replace("'", "''"))
                .Replace("__STATEFILE__", Taskbar.StateFile.Replace("'", "''"))
                .Replace("__AUTOHIDE__", _originalAutoHide ? "$true" : "$false");

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden "
                    + "-EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return;
            // The watchdog only exits on its own AFTER we die, so an exit we get to observe means it
            // was killed or crashed — put a new one in its place (bounded, in case PowerShell is
            // being blocked outright and would just die again).
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                process.Dispose();
                if (Interlocked.Increment(ref _respawns) <= MaxRespawns)
                    Spawn();
            };
        }
        catch (Exception ex)
        {
            // The watchdog is a safety net on top of the in-process restore; never let it block startup.
            App.LogCrash(ex, "TaskbarWatchdog");
        }
    }

    // PowerShell 5.1-safe; the embedded C# must stay C# 5 (Add-Type uses the Framework compiler:
    // no interpolation, no out-var, no var). ABM_SETSTATE = 10, ABS_AUTOHIDE = 1,
    // ABS_ALWAYSONTOP = 2, SW_SHOW = 5 — mirrors Interop/Taskbar.SetAutoHide + ShowTrayWindows.
    // Like Taskbar.cs it never uses FindWindow for the tray: an Explorer restart leaves a 0x0 ghost
    // Shell_TrayWnd behind, and FindWindow may hand that one back — every tray window is swept.
    private const string Script = """
        # Dockable taskbar watchdog: waits for the dock (pid __PID__) to exit, then restores the
        # Windows taskbar to its pre-launch state in case the dock was killed without running its
        # own exit handlers. Exits immediately afterwards.

        # Compile BEFORE waiting: Add-Type spins up csc (~1-2 s), which would otherwise delay the
        # restore by that much after the dock dies.
        Add-Type -TypeDefinition @'
        using System;
        using System.Collections.Generic;
        using System.Runtime.InteropServices;
        using System.Text;

        public static class TaskbarRestore
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct RECT { public int left; public int top; public int right; public int bottom; }

            [StructLayout(LayoutKind.Sequential)]
            public struct APPBARDATA
            {
                public uint cbSize;
                public IntPtr hWnd;
                public uint uCallbackMessage;
                public uint uEdge;
                public RECT rc;
                public IntPtr lParam;
            }

            public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

            [DllImport("shell32.dll")]
            public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

            [DllImport("user32.dll")]
            public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

            [DllImport("user32.dll")]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll")]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            // Returns false while no real taskbar exists yet (Explorer down / restarting).
            public static bool Restore(bool autoHide)
            {
                List<IntPtr> trays = new List<IntPtr>();
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    StringBuilder cls = new StringBuilder(64);
                    GetClassNameW(hWnd, cls, cls.Capacity);
                    string name = cls.ToString();
                    if (name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd")
                        trays.Add(hWnd);
                    return true;
                }, IntPtr.Zero);

                // Undo a possible SW_HIDE (TaskbarVisibility.Never) on every monitor's taskbar, and
                // pick the first one with a real rect for the appbar message (skips the 0x0 ghost).
                IntPtr primary = IntPtr.Zero;
                foreach (IntPtr hWnd in trays)
                {
                    ShowWindow(hWnd, 5);
                    RECT rect;
                    if (primary == IntPtr.Zero && GetWindowRect(hWnd, out rect) && rect.right - rect.left > 0)
                        primary = hWnd;
                }
                if (primary == IntPtr.Zero)
                    return false;

                // Re-assert the pre-launch auto-hide state.
                APPBARDATA data = new APPBARDATA();
                data.cbSize = (uint)Marshal.SizeOf(typeof(APPBARDATA));
                data.hWnd = primary;
                data.lParam = (IntPtr)(autoHide ? 1 : 2);
                SHAppBarMessage(10, ref data);
                return true;
            }
        }
        '@

        try { Wait-Process -Id __PID__ -ErrorAction Stop } catch { }
        Start-Sleep -Milliseconds 750
        if (Get-Process -Name '__EXENAME__' -ErrorAction SilentlyContinue) { exit } # a new dock owns the taskbar now

        # Explorer may be down too (killed alongside, or restarting): keep trying for up to ~30 s.
        for ($i = 0; $i -lt 30; $i++) {
            if ([TaskbarRestore]::Restore(__AUTOHIDE__)) {
                # Explorer applies ABM_SETSTATE asynchronously and can re-hide while doing so; re-assert once.
                Start-Sleep -Milliseconds 1000
                [void][TaskbarRestore]::Restore(__AUTOHIDE__)
                Remove-Item -LiteralPath '__STATEFILE__' -ErrorAction SilentlyContinue
                break
            }
            Start-Sleep -Seconds 1
        }
        """;
}
