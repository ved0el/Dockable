using Dockable.Shell;
using System.Collections.Concurrent;
using System.IO;

namespace Dockable.Interop;

/// <summary>
/// Decides whether a running window belongs to a pinned app, using several strategies because
/// no single signal is reliable: exact exe path (Chrome, Brave), window==pin AppUserModelID
/// (apps that set one), the exe living in a subfolder of the pin's app folder (Steam's
/// <c>steamwebhelper.exe</c> under the Steam dir), and a special case for the File Explorer
/// shell pin (no exe target; AUMID <c>Microsoft.Windows.Explorer</c> → <c>explorer.exe</c>).
/// </summary>
public readonly struct PinMatcher
{
    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>Stable key for reusing the app's tile view-model across refreshes.</summary>
    public string Key { get; }

    private readonly string? _targetExe;
    private readonly string? _targetDir;
    private readonly string? _aumid;
    private readonly bool _explorerLike;

    private PinMatcher(string key, string? targetExe, string? targetDir, string? aumid, bool explorerLike)
    {
        Key = key;
        _targetExe = targetExe;
        _targetDir = targetDir;
        _aumid = aumid;
        _explorerLike = explorerLike;
    }

    // Built matchers are cached: For runs for every pin on the ~1 s taskbar refresh, and its inputs
    // (ResolveLinkTarget / GetLinkAumid) are themselves permanently memoized in TaskbarApps, so a
    // matcher for a given pin path never changes. No eviction — the pin set is small + user-curated.
    private static readonly ConcurrentDictionary<string, PinMatcher> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static PinMatcher For(string pinPath) => Cache.GetOrAdd(pinPath, Create);

    private static PinMatcher Create(string pinPath)
    {
        bool isLink = pinPath.EndsWith(".lnk", Ci);
        // "shell:AppsFolder\<aumid>" is what pinning a RUNNING packaged app stores — it's an AUMID,
        // not an exe path; treating it as one meant the pin never claimed its windows (Teams showed
        // pinned-but-idle plus a second running tile).
        bool isAppsFolder = pinPath.StartsWith(PackagedApp.AppsFolderPrefix, Ci);
        string? targetExe = isLink ? TaskbarApps.ResolveLinkTarget(pinPath) : isAppsFolder ? null : pinPath;
        string? aumid = isLink ? TaskbarApps.GetLinkAumid(pinPath)
            : isAppsFolder ? pinPath[PackagedApp.AppsFolderPrefix.Length..]
            : null;
        // A pinned WindowsApps exe carries its package version in the path; its AUMID survives updates.
        aumid ??= PackagedApp.AumidForExe(targetExe);
        string? targetDir = string.IsNullOrEmpty(targetExe) ? null : Path.GetDirectoryName(targetExe);
        bool explorerLike = string.Equals(aumid, "Microsoft.Windows.Explorer", Ci);

        string key = !string.IsNullOrEmpty(targetExe) ? targetExe!.ToLowerInvariant()
            : !string.IsNullOrEmpty(aumid) ? aumid!.ToLowerInvariant()
            : pinPath.ToLowerInvariant();

        return new PinMatcher(key, targetExe, targetDir, aumid, explorerLike);
    }

    public bool Matches(TaskbarApps.RunningWindow window)
    {
        if (!string.IsNullOrEmpty(_targetExe) && string.Equals(window.ExePath, _targetExe, Ci))
            return true;

        // Manifest AUMID first, same as DockViewModel.IdentifyWindow: Teams' windows advertise
        // "…!MSTeams.Work", which never equals the registered "…!MSTeams" a pin holds.
        if (!string.IsNullOrEmpty(_aumid)
            && (string.Equals(PackagedApp.AumidForExe(window.ExePath), _aumid, Ci)
                || string.Equals(window.Aumid, _aumid, Ci)))
            return true;

        if (_explorerLike && string.Equals(Path.GetFileName(window.ExePath), "explorer.exe", Ci))
            return true;

        // Window exe in a strict subfolder of the pin's app folder (e.g. Steam helpers). Skip
        // shared system folders, and require a deeper path so same-folder apps don't collide.
        if (!string.IsNullOrEmpty(_targetDir) && !_targetDir!.StartsWith(WindowsDir, Ci))
        {
            string windowDir = Path.GetDirectoryName(window.ExePath) ?? string.Empty;
            if (windowDir.StartsWith(_targetDir + Path.DirectorySeparatorChar, Ci))
                return true;
        }

        return false;
    }
}
