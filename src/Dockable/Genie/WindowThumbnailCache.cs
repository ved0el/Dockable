using System.Windows.Threading;
using Dockable.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Dockable.Genie;

/// <summary>
/// Keeps a recent full-window capture of each app window while it is visible, so that when
/// a window minimizes we already have its image and bounds — capturing at minimize time is
/// unreliable because the window is usually already gone by the time the event arrives.
///
/// Captures happen on foreground changes and on a light refresh timer (only the current
/// foreground window), keeping cost low while staying reasonably fresh.
/// </summary>
public sealed class WindowThumbnailCache : IDisposable
{
    private const int MaxEntries = 12;
    private const int RefreshMs = 1200;
    // Wait for a newly-focused window to actually be raised on top and repainted before
    // capturing it; capturing immediately on the focus event can grab the windows that
    // were still covering it.
    private const int FocusSettleMs = 180;

    private readonly Dictionary<IntPtr, WindowCapture.Result> _cache = new();
    private readonly LinkedList<IntPtr> _order = new(); // LRU: front = oldest
    private readonly WinEventHook _hook;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _settleTimer;
    private readonly uint _ownProcessId;

    public WindowThumbnailCache()
    {
        _hook = new WinEventHook(PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND,
            OnForeground, PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
        _ownProcessId = (uint)Environment.ProcessId;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RefreshMs) };
        _timer.Tick += (_, _) => CaptureForeground();
        _settleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FocusSettleMs) };
        _settleTimer.Tick += (_, _) => { _settleTimer.Stop(); CaptureForeground(); };
    }

    public void Start()
    {
        if (_hook.IsActive)
            return;
        _hook.Start();
        _timer.Start();
        CaptureForeground();
    }

    /// <summary>When set and returning true, the proactive refresh skips capturing — set by the dock while
    /// a minimize/restore warp is in flight, so a full-window BitBlt (tens of ms for a 4K window, on the UI
    /// thread) never lands mid-animation and stutters it. The warp already has the capture it needs.</summary>
    public Func<bool>? ShouldSuspend { get; set; }

    /// <summary>The most recent capture taken while <paramref name="hwnd"/> was visible, if any.</summary>
    public WindowCapture.Result? TryGet(IntPtr hwnd)
        => _cache.TryGetValue(hwnd, out var result) ? result : null;

    public void Remove(IntPtr hwnd)
    {
        if (_cache.Remove(hwnd))
            _order.Remove(hwnd);
    }

    private void OnForeground(HWND hwnd, uint @event)
    {
        // Capture is deferred (debounced) so the window has time to settle on top first. (OS
        // transitions are NOT touched here any more: the dock suppresses them just-in-time around its
        // own minimizes, so the focused window keeps the native maximize/snap animations.)
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    private void CaptureForeground()
    {
        if (ShouldSuspend?.Invoke() == true)
            return; // a warp is animating — don't stall its frames with a full-window BitBlt
        Capture(PInvoke.GetForegroundWindow());
    }

    private void Capture(HWND hwnd)
    {
        if (!WindowFilter.IsEligibleAppWindow(hwnd, _ownProcessId))
            return;

        var result = WindowCapture.Capture((IntPtr)hwnd);
        if (result is not null)
            Store((IntPtr)hwnd, result.Value);
    }


    private void Store(IntPtr hwnd, WindowCapture.Result result)
    {
        if (_cache.ContainsKey(hwnd))
            _order.Remove(hwnd);
        _cache[hwnd] = result;
        _order.AddLast(hwnd);

        while (_order.Count > MaxEntries)
        {
            IntPtr oldest = _order.First!.Value;
            _order.RemoveFirst();
            _cache.Remove(oldest);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _settleTimer.Stop();

        _hook.Dispose();
    }
}
