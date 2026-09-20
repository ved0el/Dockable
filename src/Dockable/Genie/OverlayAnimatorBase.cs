using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dockable.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable.Genie;

/// <summary>
/// Shared engine for the pre-warmed, reusable, click-through, topmost minimize/restore overlays
/// (<see cref="GenieAnimator"/>, <see cref="ScaleAnimator"/>): the overlay window lifecycle, the
/// render loop (with a frame-rate cap that never skips the final frame), in-flight finalization
/// (<see cref="FinishCurrent"/>), and the deferred restore hide (<see cref="CompleteRestoreHold"/>,
/// guarded by a play sequence so a newer play is never hidden). Subclasses provide the visual
/// content and the per-frame warp. Coordinates are DIP; rects are virtual-screen DIPs.
/// </summary>
public abstract class OverlayAnimatorBase : IMinimizeAnimator
{
    /// <summary>Speed multiplier; &gt;1 shortens the duration (faster), &lt;1 lengthens it (slower).</summary>
    public double SpeedMultiplier { get; set; } = 1.0;

    /// <summary>Restores run this much longer than minimizes. Both motion systems give an entrance
    /// more time than the matching exit: the thing arriving is what the eye follows, while the thing
    /// leaving has already been dismissed.</summary>
    private const double RestoreDurationFactor = 1.15;

    /// <summary>Warp at which the overlay starts fading out (and, mirrored, the point a restore has
    /// faded in by). Every effect ends with all of its geometry collapsed onto one landing point, so
    /// the last frames are a degenerate flat sliver and the hide that follows is a hard cut — both
    /// read as a glitch. Dissolving over the final stretch removes the sliver and the cut together,
    /// which is also what frees each effect to size its end geometry for the MIDDLE of the play.</summary>
    private const double FadeStartWarp = 0.88;

    /// <summary>Landed size at the dock (DIP); set from the actual tile width before each play.</summary>
    public double TargetTileWidth { get; set; } = 56;

    /// <summary>The current play's source rect (overlay-local DIP).</summary>
    protected Rect Src { get; private set; }

    /// <summary>The current play's landing point (overlay-local DIP).</summary>
    protected Point Target { get; private set; }

    /// <summary>The active monitor's height (DIP) — the genie's 3D Y-flip. Set only by
    /// Play/ShowAtSource (AnimateTo inherits it).</summary>
    protected double MonitorHeight { get; private set; }

    private Window? _overlay;
    private EventHandler? _rendering;
    private Point _monitorOrigin; // monitor top-left (DIP), to convert later AnimateTo targets
    private bool _reverse;
    private Action? _onCompleted;
    private TimeSpan _startTime;
    private TimeSpan _lastFrame; // last painted frame's RenderingTime, for the frame-rate cap
    // Bumped whenever new content is shown on the shared overlay. A deferred restore hide (see
    // CompleteRestoreHold) checks it so it never hides a frame that a newer animation now owns.
    private int _playSeq;

    // --- Subclass contract ---

    /// <summary>Builds the overlay's content once (the image canvas / the 3D viewport).</summary>
    protected abstract UIElement BuildOverlayContent();

    /// <summary>Per-monitor extras after the overlay is sized to it (the genie's camera). No-op default.</summary>
    protected virtual void OnOverlaySynced(Rect monitorDip) { }

    /// <summary>Loads a capture into the content (brush/image source + placement from <see cref="Src"/>).</summary>
    protected abstract void SetContent(BitmapSource bitmap);

    /// <summary>Per-play precompute, run once <see cref="Src"/>/<see cref="Target"/> are final
    /// (mesh invariants / the end scale).</summary>
    protected abstract void PreparePlay();

    /// <summary>Renders one frame at warp <paramref name="warp"/> in [0,1] (0 = at source, 1 = at
    /// the tile), already carrying the <see cref="Emphasized"/> velocity curve. Subclasses SHAPE the
    /// warp (the genie's per-row stagger), they don't re-ease it — easing an eased value compounds
    /// the curve.</summary>
    protected abstract void ApplyFrame(double warp);

    /// <summary>Nominal duration (ms) before <see cref="SpeedMultiplier"/>; read per frame (the
    /// genie's duration varies per style).</summary>
    protected abstract double BaseDurationMs { get; }

    /// <summary>Profiler session prefix ("Genie"/"Suck"/"Scale").</summary>
    protected abstract string ProfileName { get; }

    /// <summary>Builds the reusable overlay ahead of time so the first play is as fast as the rest.</summary>
    public void Prewarm() => EnsureOverlay();

    public void Play(BitmapSource bitmap, Rect sourceDip, Point targetDip, Rect monitorDip, bool reverse, Action? onCompleted)
    {
        EnsureOverlay();
        FinishCurrent(); // only one animation at a time on the shared overlay — finalize any in-flight one
        SyncOverlay(monitorDip);

        _monitorOrigin = new Point(monitorDip.Left, monitorDip.Top);
        Src = new Rect(sourceDip.Left - monitorDip.Left, sourceDip.Top - monitorDip.Top, sourceDip.Width, sourceDip.Height);
        Target = new Point(targetDip.X - monitorDip.Left, targetDip.Y - monitorDip.Top);
        MonitorHeight = monitorDip.Height;
        _reverse = reverse;
        _onCompleted = onCompleted;
        _startTime = TimeSpan.Zero;
        _lastFrame = TimeSpan.Zero;

        SetContent(bitmap);
        PreparePlay();
        ApplyFrameAndFade(reverse ? 1.0 : 0.0);
        _playSeq++;
        _overlay!.Visibility = Visibility.Visible;

        if (MinimizeProfiler.Enabled)
            MinimizeProfiler.BeginSession($"{ProfileName}/{(reverse ? "restore" : "min")}", Src.Width, Src.Height, TargetTileWidth);

        HookRenderLoop();
    }

    public void ShowAtSource(BitmapSource bitmap, Rect sourceDip, Rect monitorDip)
    {
        EnsureOverlay();
        FinishCurrent(); // finalize any in-flight animation so its window lands and frees up before this one
        SyncOverlay(monitorDip);

        _monitorOrigin = new Point(monitorDip.Left, monitorDip.Top);
        Src = new Rect(sourceDip.Left - monitorDip.Left, sourceDip.Top - monitorDip.Top, sourceDip.Width, sourceDip.Height);
        MonitorHeight = monitorDip.Height;
        Target = new Point(Src.Left + Src.Width / 2, Src.Top + Src.Height / 2); // until AnimateTo
        _reverse = false;
        _onCompleted = null;

        SetContent(bitmap);
        PreparePlay();
        ApplyFrameAndFade(0.0); // un-warped, fully opaque: the window exactly where it was
        _playSeq++;
        _overlay!.Visibility = Visibility.Visible;
        // No render loop / profiler session here — AnimateTo starts them when the warp begins.
    }

    public void AnimateTo(Point targetDip, bool reverse, Action? onCompleted)
    {
        EnsureOverlay();
        // (No FinishCurrent here: AnimateTo follows our own ShowAtSource, which already finalized any
        // prior play; ShowAtSource doesn't start the render loop, so nothing of ours is in flight.)
        Target = new Point(targetDip.X - _monitorOrigin.X, targetDip.Y - _monitorOrigin.Y);
        _reverse = reverse;
        _onCompleted = onCompleted;
        _startTime = TimeSpan.Zero;
        _lastFrame = TimeSpan.Zero;

        PreparePlay();
        ApplyFrameAndFade(reverse ? 1.0 : 0.0);
        _playSeq++;
        _overlay!.Visibility = Visibility.Visible;

        if (MinimizeProfiler.Enabled)
            MinimizeProfiler.BeginSession($"{ProfileName}/{(reverse ? "restore" : "min")}", Src.Width, Src.Height, TargetTileWidth);

        HookRenderLoop();
    }

    /// <summary>Renders one frame and keeps the overlay's opacity in step with it. Every path that
    /// paints a frame goes through here — a play that ended faded out must not leave the next
    /// <see cref="ShowAtSource"/> invisible (that overlay frame is what covers the real window while
    /// it minimizes behind it). Opacity is a function of warp alone, so a restore mirrors for free:
    /// it emerges from the dock instead of popping in.</summary>
    private void ApplyFrameAndFade(double warp)
    {
        ApplyFrame(warp);
        double over = (warp - FadeStartWarp) / (1 - FadeStartWarp);
        _overlay!.Opacity = over <= 0 ? 1.0 : over >= 1 ? 0.0 : 1 - over;
    }

    private void HookRenderLoop()
    {
        if (_rendering is null)
        {
            _rendering = OnRendering;
            CompositionTarget.Rendering += _rendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_startTime == TimeSpan.Zero)
            _startTime = now;

        double duration = BaseDurationMs * (_reverse ? RestoreDurationFactor : 1.0)
            / Math.Max(0.1, SpeedMultiplier);
        double progress = Math.Min(1.0, (now - _startTime).TotalMilliseconds / duration);
        // Ease PROGRESS, then mirror — never mirror the eased value. Reversing an asymmetric curve
        // inverts it (a decelerate becomes an accelerate), so a restore would arrive at a hard stop
        // instead of settling. Both directions must decelerate into their landing.
        double eased = Emphasized(progress);
        double warp = _reverse ? 1.0 - eased : eased;

        // Frame-rate cap: skip this frame's work if we painted too recently — but never skip the final
        // frame, so the animation always lands and its completion callback runs.
        if (progress < 1.0 && _lastFrame != TimeSpan.Zero
            && (now - _lastFrame).TotalMilliseconds < PerformanceProfile.MinFrameIntervalMs)
            return;
        _lastFrame = now;

        long ts = MinimizeProfiler.Enabled ? Stopwatch.GetTimestamp() : 0;
        ApplyFrameAndFade(warp);
        if (MinimizeProfiler.Enabled)
            MinimizeProfiler.Frame(now, Stopwatch.GetElapsedTime(ts).TotalMilliseconds);

        if (progress >= 1.0)
        {
            CompositionTarget.Rendering -= _rendering!;
            _rendering = null;
            MinimizeProfiler.EndSession();
            var done = _onCompleted;
            _onCompleted = null;
            if (_reverse)
            {
                // Restore: keep the final captured frame on the (topmost) overlay and let `done` bring the
                // real window up beneath it, then hide the capture only once the window has painted — so
                // there's no blank blink between the capture vanishing and the window appearing.
                CompleteRestoreHold(done);
            }
            else
            {
                _overlay!.Visibility = Visibility.Hidden;
                done?.Invoke();
            }
        }
    }

    /// <summary>
    /// Ends a restore by holding the final capture on the overlay while the real window is restored
    /// underneath, then hiding the capture after the window has had a couple of frames to paint. The hide
    /// is abandoned if a newer play has since taken over the shared overlay (tracked by <see cref="_playSeq"/>).
    /// </summary>
    private void CompleteRestoreHold(Action? done)
    {
        done?.Invoke(); // bring the real window up beneath the still-visible capture

        int seq = _playSeq;
        int ticks = 0;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (_playSeq != seq) // a newer animation now owns the overlay — this hide is stale
            {
                CompositionTarget.Rendering -= handler!;
                return;
            }
            if (++ticks < 2) // let the restored window paint for a frame or two first
                return;
            CompositionTarget.Rendering -= handler!;
            if (_playSeq == seq)
                _overlay!.Visibility = Visibility.Hidden;
        };
        CompositionTarget.Rendering += handler;
    }

    /// <summary>
    /// Ends any animation currently in flight by running its completion callback now (the previous
    /// window snaps to its tile and is freed) — the shared overlay can only show one at a time, so
    /// starting a new one must not silently drop the old one's onCompleted (which would leave it stuck).
    /// </summary>
    private void FinishCurrent()
    {
        if (_rendering is null)
            return;
        CompositionTarget.Rendering -= _rendering;
        _rendering = null;
        MinimizeProfiler.EndSession();
        var done = _onCompleted;
        _onCompleted = null;
        done?.Invoke();
    }

    /// <summary>Whether the reusable overlay has been built.</summary>
    protected bool HasOverlay => _overlay is not null;

    /// <summary>Finalizes any in-flight play and closes the overlay, so the next play rebuilds it —
    /// for quality changes (the genie's <c>RefreshQuality</c>).</summary>
    protected void TearDownOverlay()
    {
        FinishCurrent(); // finalize any in-flight play (should be none while idle) so nothing is stranded
        _overlay?.Close();
        _overlay = null;
    }

    private void EnsureOverlay()
    {
        if (_overlay is not null)
            return;

        _overlay = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Focusable = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = BuildOverlayContent(),
        };

        // Default to the primary screen; resized to the active monitor on each play.
        _overlay.Left = 0;
        _overlay.Top = 0;
        _overlay.Width = SystemParameters.PrimaryScreenWidth;
        _overlay.Height = SystemParameters.PrimaryScreenHeight;
        _overlay.Show();          // create the HWND
        MakeClickThrough(_overlay);
        _overlay.Visibility = Visibility.Hidden; // idle until a play begins
    }

    /// <summary>Sizes the overlay to the given monitor (and lets the subclass match, e.g. its camera).</summary>
    private void SyncOverlay(Rect monitorDip)
    {
        _overlay!.Left = monitorDip.Left;
        _overlay.Top = monitorDip.Top;
        _overlay.Width = monitorDip.Width;
        _overlay.Height = monitorDip.Height;
        OnOverlaySynced(monitorDip);
    }

    private static void MakeClickThrough(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        ex |= WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        PInvoke.SetWindowLongPtr((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)(uint)ex);
    }

    protected static double SmoothStep(double t) => t * t * (3 - 2 * t);

    /// <summary>
    /// The velocity curve both warps run on — the shape Apple's and Material 3's motion systems
    /// converge on for an object travelling to a target: zero velocity at both ends (no lurch, no
    /// hard stop), travel front-loaded so most of the distance is covered early, then a long
    /// decelerating settle. Measured against both references it sits between them — it tracks Apple's
    /// <c>easeOut</c> (<c>cubic-bezier(.25,.1,.25,1)</c>) through the first half and Material 3's
    /// "emphasized" (<c>cubic-bezier(.2,0,0,1)</c>) through the second (both ≈0.88 of the distance at
    /// the half-way mark) — built by skewing the symmetric smoothstep rather than solving a bezier.
    /// <para>The symmetric <see cref="SmoothStep"/> this replaced spent half the time on each half of
    /// the distance, which reads as a sluggish start and an abrupt arrival.</para>
    /// </summary>
    protected static double Emphasized(double t)
    {
        double s = 1 - SmoothStep(t);
        return 1 - s * s * s;
    }
}
