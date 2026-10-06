using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable.Interop;

/// <summary>
/// A separate, non-layered, click-through backdrop window that renders a live acrylic blur of the
/// desktop behind the dock, clipped to the dock bar's rounded rectangle and z-ordered just below the
/// dock's icon window. Hosts a <see cref="Windows.UI.Composition"/> tree on its own
/// <c>WS_EX_NOREDIRECTIONBITMAP</c> HWND.
///
/// The layers follow the Fluent (Windows 11) acrylic recipe, bottom-up: the system host-backdrop blur,
/// a luminosity layer, a tint layer (here) and a ~2% noise texture (the WPF <c>AcrylicNoise</c> border
/// on the bar). When Windows' "Transparency effects" is off the whole stack collapses to the opaque
/// fallback colour, as Fluent materials do.
/// </summary>
public sealed class AcrylicBackdrop : IDisposable
{
    private const string WindowClass = "DockableAcrylicBackdrop";

    // --- DispatcherQueue (required before creating a Compositor on this thread) ---
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;      // DQTYPE_THREAD_CURRENT = 2
        public int apartmentType;   // DQTAT_COM_STA = 2
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options,
        [MarshalAs(UnmanagedType.IUnknown)] out object? dispatcherQueueController);

    // QI'd off the Compositor to bind it to a desktop HWND.
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwndTarget, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr result);
    }

    private object? _dispatcherController; // kept alive for the lifetime of the compositor
    private WNDPROC? _wndProc;             // kept alive so the window class proc isn't collected
    private HWND _hwnd;
    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private SpriteVisual? _visual;
    private SpriteVisual? _luminosity;
    private SpriteVisual? _tint;
    private CompositionRoundedRectangleGeometry? _clipGeometry;
    private bool _ready;

    /// <summary>Creates the backdrop window and Composition tree. Safe to call once; no-op afterwards.</summary>
    public unsafe void Initialize()
    {
        if (_ready)
            return;

        EnsureDispatcherQueue();
        CreateHostWindow();

        _compositor = new Compositor();
        var interop = _compositor.As<ICompositorDesktopInterop>();
        interop.CreateDesktopWindowTarget((IntPtr)_hwnd, false, out IntPtr targetPtr);
        _target = DesktopWindowTarget.FromAbi(targetPtr);
        Marshal.Release(targetPtr);

        _visual = _compositor.CreateSpriteVisual();
        // Brush is assigned by SetEffect() (Acrylic vs Liquid Glass).

        // Luminosity + tint layers stretch over the blur (children inherit the root's rounded clip).
        _luminosity = _compositor.CreateSpriteVisual();
        _luminosity.RelativeSizeAdjustment = Vector2.One;
        _tint = _compositor.CreateSpriteVisual();
        _tint.RelativeSizeAdjustment = Vector2.One;
        _visual.Children.InsertAtTop(_luminosity);
        _visual.Children.InsertAtTop(_tint);

        _clipGeometry = _compositor.CreateRoundedRectangleGeometry();
        _visual.Clip = _compositor.CreateGeometricClip(_clipGeometry);

        _target.Root = _visual;
        _ready = true;
    }

    private void EnsureDispatcherQueue()
    {
        if (_dispatcherController is not null)
            return;
        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,    // DQTYPE_THREAD_CURRENT
            apartmentType = 2, // DQTAT_COM_STA (WPF UI thread is STA)
        };
        CreateDispatcherQueueController(options, out _dispatcherController);
    }

    private unsafe void CreateHostWindow()
    {
        var hmodule = PInvoke.GetModuleHandle((string?)null); // FreeLibrarySafeHandle
        var hinstance = (HINSTANCE)hmodule.DangerousGetHandle();
        _wndProc = WndProc;

        fixed (char* className = WindowClass)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _wndProc,
                hInstance = hinstance,
                lpszClassName = className,
            };
            PInvoke.RegisterClassEx(in wc); // harmless if the class already exists
        }

        // Click-through (TRANSPARENT/NOACTIVATE), out of Alt+Tab (TOOLWINDOW), no redirection surface
        // (NOREDIRECTIONBITMAP) so the host-backdrop brush samples the desktop, and topmost so it can
        // sit just under the (topmost) dock window.
        var exStyle = WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP
            | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
            | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
            | WINDOW_EX_STYLE.WS_EX_TRANSPARENT
            | WINDOW_EX_STYLE.WS_EX_TOPMOST;

        _hwnd = PInvoke.CreateWindowEx(exStyle, WindowClass, string.Empty, WINDOW_STYLE.WS_POPUP,
            0, 0, 0, 0, default, null, hmodule, null);

        int useHostBackdrop = 1;
        PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_USE_HOSTBACKDROPBRUSH,
            &useHostBackdrop, sizeof(int));
    }

    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
        => PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);

    /// <summary>
    /// Positions/sizes the backdrop to the dock bar (physical pixels), updates the rounded clip, and
    /// keeps it z-ordered directly beneath <paramref name="belowHwnd"/> (the dock window).
    /// </summary>
    public void SetBounds(int xPx, int yPx, int widthPx, int heightPx, float cornerRadiusPx, IntPtr belowHwnd)
    {
        if (!_ready || widthPx <= 0 || heightPx <= 0)
            return;

        var size = new Vector2(widthPx, heightPx);
        _visual!.Size = size;
        _clipGeometry!.Size = size;
        _clipGeometry.CornerRadius = new Vector2(cornerRadiusPx, cornerRadiusPx);

        // Place just below the dock window in the (topmost) z-order, without activating.
        PInvoke.SetWindowPos((HWND)_hwnd, (HWND)belowHwnd, xPx, yPx, widthPx, heightPx,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Applies the Fluent acrylic layers for the theme (Desktop acrylic, "AcrylicBackgroundFillColor
    /// Default": light tint #FCFCFC / luminosity 0.85, dark tint #2C2C2C @ 0.15 / luminosity 0.96), or
    /// the opaque fallback (#F9F9F9 / #2C2C2C) when <paramref name="transparency"/> is off.
    /// </summary>
    public void SetEffect(bool dark, bool transparency)
    {
        if (!_ready || _compositor is null || _visual is null || _luminosity is null || _tint is null)
            return;
        try
        {
            if (!transparency)
            {
                _visual.Brush = _compositor.CreateColorBrush(dark ? Rgb(0x2C, 0x2C, 0x2C) : Rgb(0xF9, 0xF9, 0xF9));
                _luminosity.IsVisible = _tint.IsVisible = false;
                return;
            }
            _visual.Brush = _compositor.CreateHostBackdropBrush();
            // ponytail: WinUI composites the luminosity layer with a Luminosity BLEND (keeps the
            // backdrop's hue); that needs a Composition effect graph, which crashed this process when
            // hand-built. A normal-blend layer at reduced alpha approximates it — swap in a real
            // BlendEffect if Win2D-style effects ever become available here.
            _luminosity.Brush = _compositor.CreateColorBrush(dark
                ? Windows.UI.Color.FromArgb(0x9C, 0x2C, 0x2C, 0x2C)   // ~0.61 ≈ 0.96 luminosity-blended
                : Windows.UI.Color.FromArgb(0x8C, 0xFC, 0xFC, 0xFC)); // ~0.55 ≈ 0.85 luminosity-blended
            _tint.Brush = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(dark ? (byte)0x26 : (byte)0x00, 0x2C, 0x2C, 0x2C));
            _luminosity.IsVisible = _tint.IsVisible = true;
        }
        catch
        {
            // Leave the existing brush; the dock still works.
        }
    }

    private static Windows.UI.Color Rgb(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(0xFF, r, g, b);

    public void Show()
    {
        if (_ready)
            PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
    }

    public void Hide()
    {
        if (_ready)
            PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
    }

    public void Dispose()
    {
        _tint?.Dispose();
        _luminosity?.Dispose();
        _visual?.Dispose();
        _target?.Dispose();
        _compositor?.Dispose();
        if (!_hwnd.IsNull)
            PInvoke.DestroyWindow(_hwnd);
        _hwnd = default;
        _ready = false;
    }
}
