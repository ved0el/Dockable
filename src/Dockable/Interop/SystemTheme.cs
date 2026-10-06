using System.Runtime.InteropServices;
using Dockable.Models;
using Microsoft.Win32;

namespace Dockable.Interop;

/// <summary>Reads the current Windows app light/dark theme preference.</summary>
public static class SystemTheme
{
    /// <summary>True if Windows is using the light app theme (defaults to light if unreadable).</summary>
    public static bool IsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v ? v != 0 : true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Windows' Settings → Personalization → Colors → "Transparency effects". Fluent materials
    /// render their opaque fallback while it's off (defaults to on if unreadable).</summary>
    public static bool TransparencyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is int v ? v != 0 : true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Resolves a <see cref="DockTheme"/> to effective dark (System follows Windows).</summary>
    public static bool IsDarkEffective(DockTheme theme) => theme switch
    {
        DockTheme.Dark => true,
        DockTheme.Light => false,
        _ => !IsLight(),
    };

    /// <summary>True when a WM_SETTINGCHANGE lParam announces the OS light/dark theme changed.</summary>
    public static bool IsImmersiveColorChange(IntPtr lParam)
        => Marshal.PtrToStringAuto(lParam) == "ImmersiveColorSet";
}
