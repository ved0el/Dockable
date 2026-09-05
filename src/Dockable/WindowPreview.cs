using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Dockable.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable;

/// <summary>
/// The hover preview flyout: live thumbnails of an app's open windows, shown above its dock icon.
/// One reused window (built once, hidden between opens — a fresh WPF window per hover is the same
/// 10s-of-ms "blink" the minimize overlays are pre-warmed to avoid).
///
/// It is deliberately NOT a WPF Popup and NOT transparent: the thumbnails are live DWM mirrors
/// (<see cref="DwmThumbnail"/>), and DWM refuses to draw into a layered window — so this is a plain
/// opaque window with Win11 rounded corners applied via DWM instead.
/// </summary>
internal sealed class WindowPreview : Window
{
    private const double CellWidth = 196;
    private const double ThumbHeight = 112;
    private const double TitleHeight = 20;
    private const double Pad = 8;
    private const double Gap = 6;
    private const int MaxCells = 5; // enough to be useful; more would run the flyout off the screen

    private readonly Canvas _canvas = new();
    private readonly List<DwmThumbnail> _thumbs = new();
    private Action<IntPtr>? _onPick;

    public WindowPreview()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false; // DWM won't mirror a window into a layered destination
        ShowActivated = false;      // opening it must not steal focus from the user's app
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = _canvas;
        // Create the hwnd up front so the first open can be positioned BEFORE it is shown (otherwise
        // it flashes at the default location first).
        new WindowInteropHelper(this).EnsureHandle();
    }

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = (HWND)Hwnd;
        // Never list the flyout in Alt+Tab.
        var exStyle = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
            (nint)(uint)(exStyle | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW));
        RoundCorners(hwnd);
    }

    /// <summary>Win11 rounded corners for a window that can't use WPF's CornerRadius (it isn't layered).
    /// DWMWA_WINDOW_CORNER_PREFERENCE (33) / DWMWCP_ROUND (2) aren't in the Win32 metadata — cast the
    /// literals, the way PW_RENDERFULLCONTENT is handled elsewhere.</summary>
    private static unsafe void RoundCorners(HWND hwnd)
    {
        int round = 2;
        PInvoke.DwmSetWindowAttribute(hwnd, (DWMWINDOWATTRIBUTE)33, &round, sizeof(int));
    }

    /// <summary>Opens (or re-targets, when already open on another icon) the preview for
    /// <paramref name="windows"/>, bottom-centered at the given screen point (physical px).</summary>
    public void Open(IReadOnlyList<IntPtr> windows, double anchorCenterXPx, double bottomYPx,
        Rect monitorPx, double scale, bool dark, Action<IntPtr> onPick)
    {
        _onPick = onPick;
        ReleaseThumbnails();
        _canvas.Children.Clear();

        var shown = windows.Count > MaxCells ? windows.Take(MaxCells).ToList() : windows;
        var surface = UiBrushes.Frozen(dark ? "#2B2B2B" : "#FFFFFF");
        var text = UiBrushes.Frozen(dark ? "#F2F2F2" : UiBrushes.InkHex);
        var hover = UiBrushes.Frozen(dark ? "#26FFFFFF" : "#14000000");
        var placeholder = UiBrushes.Frozen(dark ? "#1FFFFFFF" : "#0F000000");
        Background = surface;

        // Cells are laid out by arithmetic rather than by the layout pass, because the very same
        // numbers have to be handed to DWM in physical px — deriving both from one formula keeps the
        // mirrored frames exactly on top of their cells.
        double width = 2 * Pad + shown.Count * CellWidth + (shown.Count - 1) * Gap;
        double height = 2 * Pad + ThumbHeight + TitleHeight;
        Width = width;
        Height = height;
        _canvas.Width = width;
        _canvas.Height = height;

        for (int i = 0; i < shown.Count; i++)
        {
            double x = Pad + i * (CellWidth + Gap);
            _canvas.Children.Add(BuildCell(shown[i], x, placeholder, text, hover));
        }

        // Keep the flyout on the monitor, centered over the icon.
        double widthPx = width * scale;
        double heightPx = height * scale;
        double left = Math.Clamp(anchorCenterXPx - widthPx / 2,
            monitorPx.Left, Math.Max(monitorPx.Left, monitorPx.Right - widthPx));
        double top = Math.Max(monitorPx.Top, bottomYPx - heightPx);
        PInvoke.SetWindowPos((HWND)Hwnd, HWND.Null, (int)Math.Round(left), (int)Math.Round(top),
            (int)Math.Round(widthPx), (int)Math.Round(heightPx),
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);

        if (!IsVisible)
            Show();

        RegisterThumbnails(shown, scale);
    }

    private Border BuildCell(IntPtr hwnd, double x, Brush placeholder, Brush text, Brush hover)
    {
        var thumb = new Border
        {
            Width = CellWidth,
            Height = ThumbHeight,
            Background = placeholder, // shows through if DWM has no frame for this window
            CornerRadius = new CornerRadius(4),
        };
        var title = new TextBlock
        {
            Text = TaskbarApps.GetWindowTitle(hwnd),
            Foreground = text,
            FontSize = 11,
            Height = TitleHeight,
            Width = CellWidth,
            Padding = new Thickness(2, 3, 2, 0),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var stack = new StackPanel();
        stack.Children.Add(thumb);
        stack.Children.Add(title);

        var cell = new Border
        {
            Background = Brushes.Transparent, // hit-testable
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand,
            Child = stack,
        };
        cell.MouseEnter += (_, _) => cell.Background = hover;
        cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
        cell.MouseLeftButtonUp += (_, _) => _onPick?.Invoke(hwnd);
        Canvas.SetLeft(cell, x);
        Canvas.SetTop(cell, Pad);
        return cell;
    }

    /// <summary>Points DWM at each cell's thumbnail area (client px), aspect-fitted to the source.</summary>
    private void RegisterThumbnails(IReadOnlyList<IntPtr> windows, double scale)
    {
        for (int i = 0; i < windows.Count; i++)
        {
            var thumb = DwmThumbnail.Register(Hwnd, windows[i]);
            if (thumb is null)
                continue;
            _thumbs.Add(thumb);

            double x = (Pad + i * (CellWidth + Gap)) * scale;
            double y = Pad * scale;
            double w = CellWidth * scale;
            double h = ThumbHeight * scale;

            if (thumb.SourceSize() is { } size)
            {
                double fit = Math.Min(w / size.Width, h / size.Height);
                double fw = size.Width * fit, fh = size.Height * fit;
                x += (w - fw) / 2;
                y += (h - fh) / 2;
                w = fw;
                h = fh;
            }
            thumb.Show((int)Math.Round(x), (int)Math.Round(y),
                (int)Math.Round(x + w), (int)Math.Round(y + h));
        }
    }

    /// <summary>Hides the flyout and drops its live mirrors (the window itself is reused).</summary>
    public void Retract()
    {
        ReleaseThumbnails();
        if (IsVisible)
            Hide();
    }

    private void ReleaseThumbnails()
    {
        foreach (var thumb in _thumbs)
            thumb.Dispose();
        _thumbs.Clear();
    }

    /// <summary>Geometry-based hover test (the flyout and the dock are separate windows, so WPF's
    /// MouseLeave can't tell the dock whether the cursor merely crossed into the preview).</summary>
    public bool ContainsCursor()
    {
        if (!IsVisible || !PInvoke.GetCursorPos(out var cursor))
            return false;
        try
        {
            var p = PointFromScreen(new Point(cursor.X, cursor.Y));
            return p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight;
        }
        catch
        {
            return false; // mid-teardown: treat as "not hovered" so the caller can close it
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        ReleaseThumbnails();
        base.OnClosed(e);
    }
}
