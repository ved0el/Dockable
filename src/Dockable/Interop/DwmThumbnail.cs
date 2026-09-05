using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace Dockable.Interop;

/// <summary>
/// One live DWM thumbnail — the same mechanism the taskbar's window previews use: the compositor
/// mirrors the source window's rendered frames into a rect of a destination window we own. That's why
/// the hover previews can show occluded and minimized windows at all: a screen BitBlt of either grabs
/// the occluder or nothing (which is the whole reason <see cref="Genie.WindowThumbnailCache"/> exists).
/// </summary>
internal sealed class DwmThumbnail : IDisposable
{
    // dwmapi.h DWM_TNP_* — not in the Win32 metadata, so they're spelled out here.
    private const uint TnpRectDestination = 0x1;
    private const uint TnpOpacity = 0x4;
    private const uint TnpVisible = 0x8;

    private nint _id;

    private DwmThumbnail(nint id) => _id = id;

    /// <summary>Registers a thumbnail of <paramref name="source"/> on <paramref name="destination"/>,
    /// or null if the window is gone / DWM refuses. The destination must NOT be a layered
    /// (WPF <c>AllowsTransparency</c>) window — DWM silently draws nothing into one.</summary>
    public static DwmThumbnail? Register(IntPtr destination, IntPtr source)
        => PInvoke.DwmRegisterThumbnail((HWND)destination, (HWND)source, out nint id).Succeeded
            ? new DwmThumbnail(id)
            : null;

    /// <summary>The source window's size, so the caller can aspect-fit its destination rect — DWM
    /// stretches the thumbnail to whatever rect it's given instead of letterboxing it.</summary>
    public (int Width, int Height)? SourceSize()
        => PInvoke.DwmQueryThumbnailSourceSize(_id, out var size).Succeeded && size.cx > 0 && size.cy > 0
            ? (size.cx, size.cy)
            : null;

    /// <summary>Shows the thumbnail in the given rect (client-area physical px of the destination).</summary>
    public void Show(int left, int top, int right, int bottom)
    {
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = TnpRectDestination | TnpOpacity | TnpVisible,
            rcDestination = new RECT { left = left, top = top, right = right, bottom = bottom },
            opacity = 255,
            fVisible = true,
        };
        PInvoke.DwmUpdateThumbnailProperties(_id, props);
    }

    public void Dispose()
    {
        if (_id == 0)
            return;
        PInvoke.DwmUnregisterThumbnail(_id);
        _id = 0;
    }
}
