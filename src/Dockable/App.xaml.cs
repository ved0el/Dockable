using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Dockable.Interop;
using Dockable.Localization;
using Dockable.Services;
using Dockable.ViewModels;

namespace Dockable;

public partial class App : Application
{
    public SettingsStore SettingsStore { get; } = new();
    public DockViewModel DockViewModel { get; private set; } = null!;

    private DockWindow? _dockWindow;
    private MenuBarWindow? _menuBarWindow;
    private Mutex? _singleInstanceMutex;
    private bool _relaunchOnExit;

    // One extra dock per non-main display, when Settings.ShowDockOnAllMonitors is on.
    private readonly List<DockWindow> _extraDocks = new();
    private string _monitorLayout = "";      // signature of the last applied display layout
    private DispatcherTimer? _reapplyTimer;  // coalesces the "settings changed" broadcast

    public static new App Current => (App)Application.Current;

    /// <summary>The dock on the main display — the one that owns the process-wide machinery.</summary>
    internal DockWindow? MainDock => _dockWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance: only the first copy runs a dock; later copies bow out immediately.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\Dockable.SingleInstance", out bool isNew);
        if (!isNew)
        {
            _singleInstanceMutex.Dispose(); // we don't own it; just let the running dock be
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogCrash(args.ExceptionObject as Exception, "AppDomain");
            Taskbar.Restore(); // put the taskbar back the way we found it before we go down
        };

        // Remember the taskbar's state now, before we change it, so we can restore it exactly.
        Taskbar.CaptureOriginalState();

        // Out-of-process safety net: a hidden watchdog (different image name, so a kill-by-name of
        // Dockable can't take it down too) restores that state even when we're force-killed and none
        // of our exit/crash handlers get to run. It exits on its own once we're gone.
        TaskbarWatchdog.Start(Taskbar.OriginalAutoHide ?? false);

        try
        {
            DockViewModel = new DockViewModel(SettingsStore);
            DockViewModel.Load();

            // Resolve the UI language (saved choice, else the Windows display language → English) and
            // persist the concrete code so the choice is sticky.
            DockViewModel.Settings.Language = Loc.Initialize(DockViewModel.Settings.Language);

            _dockWindow = new DockWindow { DataContext = DockViewModel };
            _dockWindow.Show();

            DockViewModel.SettingsSaved += ScheduleDockReapply;
            SyncDockMonitors(); // anchor to the main display + open the extra displays' docks
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // Opt-in macOS-style menu bar at the top of the primary monitor.
            if (DockViewModel.Settings.ShowMenuBar)
                SetMenuBarVisible(true);
        }
        catch (Exception ex)
        {
            // Without this, a startup failure leaves a running process with no visible dock and
            // no feedback. Surface it, log it, restore the taskbar, and exit.
            LogCrash(ex, "Startup");
            Taskbar.Restore();
            MessageBox.Show(
                string.Format(Loc.T("Error_StartupFailed"), ex.Message),
                "Dockable", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    // Fires on a resolution / scaling / monitor-hotplug change, off the UI thread.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(SyncDockMonitors);

    /// <summary>
    /// Anchors the main dock to the main display and opens (or closes) one extra dock per other
    /// display, per <see cref="Models.DockSettings.ShowDockOnAllMonitors"/>. Safe to call repeatedly —
    /// it no-ops when neither the display layout nor the setting changed. Call after toggling the
    /// setting and whenever the displays change.
    /// </summary>
    public void SyncDockMonitors()
    {
        if (_dockWindow is null)
            return;

        var monitors = Monitors.All();
        if (monitors.Count == 0)
            return; // no displays to place on (e.g. mid-hotplug); the next change re-runs this

        bool all = DockViewModel.Settings.ShowDockOnAllMonitors;
        _dockWindow.PinToMonitor(monitors[0]); // Monitors.All puts the main display first
        // Only the displays that actually have a dock get a cursor-approach raise band.
        _dockWindow.RefreshRaiseZones(all ? monitors : monitors.Take(1).ToList());

        string layout = string.Join("|", monitors) + "|" + all;
        if (layout == _monitorLayout)
            return;
        _monitorLayout = layout;

        // Rebuild the extra docks wholesale rather than diffing: display changes and toggles are rare,
        // and a dock is cheap to stand up next to the reconciliation code a diff would need.
        foreach (var dock in _extraDocks)
            dock.Close();
        _extraDocks.Clear();

        if (!all)
            return;

        foreach (var monitorPx in monitors.Skip(1))
        {
            // Its own view-model (layout is per-display) over the SAME settings object, so a change
            // made on any dock is a change on all of them.
            var vm = new DockViewModel(SettingsStore);
            vm.AttachShared(DockViewModel.Settings);
            vm.SettingsSaved += ScheduleDockReapply;

            var dock = new DockWindow { IsSecondary = true, DataContext = vm };
            dock.Show();
            dock.PinToMonitor(monitorPx);
            _extraDocks.Add(dock);
        }
    }

    /// <summary>Enters/leaves capture-friendly mode (the Snipping Tool lift) on every display's dock —
    /// the exclusion is a per-window display affinity, so all of them have to be lifted together.</summary>
    internal void SetDocksCaptureFriendly(bool on)
    {
        _dockWindow?.SetCaptureFriendly(on);
        foreach (var dock in _extraDocks)
            dock.SetCaptureFriendly(on);
    }

    /// <summary>Every dock, main display first.</summary>
    internal IEnumerable<DockWindow> Docks
    {
        get
        {
            if (_dockWindow is not null)
                yield return _dockWindow;
            foreach (var dock in _extraDocks)
                yield return dock;
        }
    }

    /// <summary>The dock a window should minimize into: the one on that window's own display, falling
    /// back to the main dock (which is also the only dock when "show on all displays" is off).</summary>
    internal DockWindow DockFor(IntPtr hwnd)
    {
        var monitorPx = Monitors.ForWindow(hwnd).MonitorPx;
        // Test the monitor's CENTRE rather than comparing rects: both come from the same shell APIs, but
        // a hotplug can hand back a stale pinned rect and containment still resolves sanely.
        var centre = new Point(monitorPx.Left + monitorPx.Width / 2, monitorPx.Top + monitorPx.Height / 2);
        foreach (var dock in Docks)
            if (dock.PinnedMonitorPx is { } px && px.Contains(centre))
                return dock;
        return _dockWindow!;
    }

    /// <summary>The dock currently holding a window's minimized representation (tile or stashed into its
    /// icon), or null when nothing is. Restores and external-restore cleanup go through this, not
    /// <see cref="DockFor"/> — a window can be moved to another display while it sits minimized.</summary>
    internal DockWindow? DockOwning(IntPtr hwnd) => Docks.FirstOrDefault(d => d.RepresentsWindow(hwnd));

    /// <summary>Whether any dock is already warping or representing this window — the guard against two
    /// docks both claiming it (which would leave two tiles for one window).</summary>
    internal bool AnyDockHandles(IntPtr hwnd) => Docks.Any(d => d.IsWarping(hwnd) || d.RepresentsWindow(hwnd));

    /// <summary>Whether a minimize/restore warp is in flight on any display.</summary>
    internal bool AnyDockWarping() => Docks.Any(d => d.HasWarpInFlight);

    /// <summary>Re-asserts every dock at the top of the topmost band. Fired as the cursor reaches for a
    /// dock, so a self-raising topmost window (picture-in-picture, a tiling WM) can't still be holding
    /// the top of the band when the click lands. Each dock keeps its own guards (own-process foreground,
    /// an open menu/flyout, a warp in flight), so this is safe to call often.</summary>
    internal void KeepDocksOnTop()
    {
        _dockWindow?.KeepOnTop();
        foreach (var dock in _extraDocks)
            dock.KeepOnTop();
    }

    /// <summary>Parks/resumes every display's Liquid Glass backdrop capturer while a minimize/restore
    /// warp animates. Each dock runs its own capture thread, and they all upload through the one UI
    /// thread the warp renders on — so a warp on display 1 has to quiet display 2's capturer too.</summary>
    internal void SetDocksGlassSuspended(bool on)
    {
        _dockWindow?.SetGlassSuspended(on);
        foreach (var dock in _extraDocks)
            dock.SetGlassSuspended(on);
    }

    /// <summary>Queues the "shared settings changed" broadcast. Debounced because every Preferences
    /// slider tick saves — re-applying per tick would restart each dock's glass capture thread.</summary>
    private void ScheduleDockReapply()
    {
        if (_reapplyTimer is null)
        {
            _reapplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _reapplyTimer.Tick += (_, _) =>
            {
                _reapplyTimer!.Stop();
                _dockWindow?.ReapplySharedSettings();
                foreach (var dock in _extraDocks)
                    dock.ReapplySharedSettings();
            };
        }
        _reapplyTimer.Stop();
        _reapplyTimer.Start();
    }

    /// <summary>Shows or hides the top menu bar window, creating it on first show. Owned by the app so
    /// its lifetime is independent of the dock window.</summary>
    public void SetMenuBarVisible(bool show)
    {
        if (show)
        {
            if (_menuBarWindow is not null)
                return;
            _menuBarWindow = new MenuBarWindow { DataContext = new ViewModels.MenuBarViewModel(DockViewModel) };
            _menuBarWindow.Closed += (_, _) => _menuBarWindow = null;
            _menuBarWindow.Show();
        }
        else
        {
            _menuBarWindow?.Close(); // OnClosed releases the reserved top strip
            _menuBarWindow = null;
        }
    }

    /// <summary>Re-applies the menu bar's theme colours (if it's open) — invoked when the dock's
    /// Light/Dark/Auto theme changes so the bar stays coordinated with the dock.</summary>
    public void RefreshMenuBarTheme() => _menuBarWindow?.RefreshTheme();

    /// <summary>Restarts Dockable: shuts this instance down and starts a fresh one on exit.</summary>
    public void Relaunch()
    {
        _relaunchOnExit = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Only the instance that actually started the dock should persist settings / restore the
        // taskbar — a bowing-out duplicate must not clobber either.
        if (_dockWindow is not null)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            DockViewModel?.Save();
            Taskbar.Restore(); // put the taskbar back to its pre-launch state
        }

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        // Relaunch AFTER the mutex is gone, so the new instance isn't rejected as a duplicate.
        // Starting it before this process fully dies also lets the taskbar watchdog see a live dock
        // and skip its restore (no taskbar flash mid-reload).
        if (_relaunchOnExit && Environment.ProcessPath is { } exe)
        {
            try { System.Diagnostics.Process.Start(exe); } catch { /* nothing sane to do on exit */ }
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception, "Dispatcher");
        // Keep the dock alive on non-fatal UI exceptions rather than dying silently.
        e.Handled = true;
    }

    private static void LogCrash(Exception? ex, string source)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockable");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:O}] ({source}) {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }
}
