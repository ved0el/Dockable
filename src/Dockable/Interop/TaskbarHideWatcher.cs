using System.Threading;

namespace Dockable.Interop;

/// <summary>
/// Keeps the Windows taskbar hidden while the "Never" mode is active. A one-shot <c>SW_HIDE</c> doesn't
/// stick: the taskbar has to be left in native auto-hide (an always-on-top taskbar keeps its work-area
/// reservation even while hidden — see <see cref="Taskbar.SetVisibility"/>), so Explorer re-shows it
/// whenever the cursor reaches the screen edge, a window flashes for attention, or Win+M/Win+D runs.
/// The dock sits at that same edge, so without this the taskbar comes back the first time the user
/// reaches for the dock — and stays.
///
/// This polls rather than hooking. An <c>EVENT_OBJECT_SHOW</c> hook scoped to Explorer was the original
/// approach and measurably does NOT fire for those re-shows on Windows 11 25H2 (build 26200): the tray
/// stayed visible for seconds after a forced <c>SW_SHOW</c>, and was visible again moments after launch.
/// A short poll is simpler and self-healing (it survives an Explorer restart, which invalidates any
/// hook's handles). It runs on a thread-pool timer, never the dispatcher: the probe is two class
/// lookups and the re-hide is a cross-process <c>ShowWindow</c>, so nothing needs the UI thread and the
/// magnification render loop is never woken by it.
/// </summary>
public sealed class TaskbarHideWatcher : IDisposable
{
    // Fast enough that an edge-hover reveal is gone before it reads as more than a flicker; cheap
    // enough (two FindWindow calls) to run continuously while the taskbar is meant to be hidden.
    private const int PollMs = 40;

    private Timer? _timer;

    /// <summary>Starts re-hiding the taskbar whenever Explorer shows it. No-op if already running.</summary>
    public void Start()
    {
        if (_timer is not null)
            return;
        _timer = new Timer(_ =>
        {
            if (Taskbar.AnyTrayWindowVisible())
                Taskbar.Hide();
        }, null, dueTime: 0, period: PollMs);
    }

    /// <summary>Stops watching (the taskbar is left in whatever state it's in). Waits for an in-flight
    /// tick, so the caller can show the tray straight after without a stale re-hide stealing it back.</summary>
    public void Stop()
    {
        var timer = _timer;
        _timer = null;
        if (timer is null)
            return;
        using var drained = new ManualResetEvent(false);
        if (timer.Dispose(drained))
            drained.WaitOne(200);
    }

    public void Dispose() => Stop();
}
