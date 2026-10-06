using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Security.Authentication.Identity;

namespace Dockable.Interop;

/// <summary>
/// System power / session actions for the menu bar's Windows-logo menu. Sleep and Lock use Win32 APIs;
/// Restart / Shut Down / Log Out shell out to <c>shutdown.exe</c> (which avoids enabling the
/// SE_SHUTDOWN privilege ourselves).
/// </summary>
public static class SystemActions
{
    /// <summary>Puts the system to sleep. Args: hibernate=false (sleep), force=false, disableWakeEvents=false.</summary>
    public static void Sleep() => PInvoke.SetSuspendState(false, false, false);

    /// <summary>Locks the workstation (the lock screen).</summary>
    public static void Lock() => PInvoke.LockWorkStation();

    public static void Restart() => RunShutdown("/r /t 0");

    public static void ShutDown() => RunShutdown("/s /t 0");

    public static void LogOut() => RunShutdown("/l");

    /// <summary>
    /// The signed-in user's friendly display name (e.g. "Jane Doe") via GetUserNameEx(NameDisplay),
    /// which works for Microsoft/AzureAD/domain accounts. Falls back to the login name (e.g. "cfiel")
    /// for local accounts that have no display name.
    /// </summary>
    public static string CurrentUserDisplayName()
    {
        try
        {
            Span<char> buffer = stackalloc char[256];
            uint size = (uint)buffer.Length;
            if (PInvoke.GetUserNameEx(EXTENDED_NAME_FORMAT.NameDisplay, buffer, ref size))
            {
                int len = buffer.IndexOf('\0');
                if (len < 0)
                    len = (int)Math.Min(size, (uint)buffer.Length);
                string name = new string(buffer[..len]).Trim();
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
        }
        catch
        {
            // Fall back below.
        }
        return Environment.UserName;
    }

    private static void RunShutdown(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // Best-effort; nothing useful to do if it fails.
        }
    }
}
