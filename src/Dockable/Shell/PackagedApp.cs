using System.Collections.Concurrent;
using System.IO;
using System.Xml.Linq;

namespace Dockable.Shell;

/// <summary>
/// Resolves a packaged (MSIX/Store) app's launchable AppUserModelID from an executable that lives in a
/// <c>WindowsApps</c> package folder, by reading the package's <c>AppxManifest.xml</c>.
/// </summary>
/// <remarks>
/// Two things make the raw exe path useless for a packaged app, and both need this AUMID:
/// <list type="bullet">
/// <item>launching it starts a process with no package identity — for some apps (Windows Terminal)
/// ShellExecute reports success and nothing happens at all;</item>
/// <item>its icon lives in the manifest's PNG assets, so extracting the exe's PE resource gives a
/// placeholder rather than the app's real artwork.</item>
/// </list>
/// A window's own advertised AUMID isn't a substitute: Teams reports
/// <c>MSTeams_8wekyb3d8bbwe!MSTeams.Work</c>, which isn't a registered app id at all — it's absent from
/// both the manifest and AppsFolder, so the shell can't resolve it.
/// </remarks>
internal static class PackagedApp
{
    /// <summary>Prefix that turns an AUMID into a shell parsing name (icons) / launch target.</summary>
    internal const string AppsFolderPrefix = @"shell:AppsFolder\";

    // Matched as a path segment rather than built from %ProgramFiles%: packages can be installed to
    // another volume (D:\WindowsApps\...).
    private const string StoreFolder = @"\WindowsApps\";
    private const string ManifestName = "AppxManifest.xml";

    // A package's manifest can't change without a reinstall, so this is cached for the process
    // lifetime. Null is cached too — most exes aren't packaged and we don't want to re-walk for them.
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the path sits inside a packaged-app (<c>WindowsApps</c>) folder.</summary>
    internal static bool IsPackagedPath(string? path)
        => !string.IsNullOrEmpty(path) && path.Contains(StoreFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The AUMID to launch and identify a packaged app by, or null when <paramref name="exePath"/>
    /// isn't a packaged app or its manifest can't be read (callers then keep using the raw path).
    /// </summary>
    internal static string? AumidForExe(string? exePath)
        => string.IsNullOrEmpty(exePath) ? null : Cache.GetOrAdd(exePath, Resolve);

    private static string? Resolve(string exePath)
    {
        // Tested in here rather than at the entry point so EVERY path is cached after first sight —
        // this runs per-window on the 1 s refresh, which CLAUDE.md keeps free of per-tick work.
        if (!IsPackagedPath(exePath))
            return null;

        try
        {
            string? manifestDir = FindPackageRoot(Path.GetDirectoryName(exePath));
            if (manifestDir is null || FamilyName(Path.GetFileName(manifestDir)) is not { } family)
                return null;

            var apps = XDocument.Load(Path.Combine(manifestDir, ManifestName))
                .Descendants().Where(e => e.Name.LocalName == "Application").ToList();

            // Prefer the entry for this exe, but only among app-list-visible ones: Teams declares four
            // <Application>s and two of them share ms-teams.exe — "MSTeams" is the real one and
            // "MSTeamsRemoteModuleContainer" is hidden (AppListEntry="none") and unresolvable.
            string exeName = Path.GetFileName(exePath);
            var app = apps.FirstOrDefault(a => IsVisible(a) && string.Equals(
                          Path.GetFileName((string?)a.Attribute("Executable")), exeName, StringComparison.OrdinalIgnoreCase))
                      ?? apps.FirstOrDefault(IsVisible);

            string? id = (string?)app?.Attribute("Id");
            return string.IsNullOrEmpty(id) ? null : family + "!" + id;
        }
        catch
        {
            return null; // unreadable/unexpected manifest — the caller falls back to the raw path
        }
    }

    // Matched by LocalName throughout: the manifest's namespaces vary by schema version.
    private static bool IsVisible(XElement app)
        => !app.Elements().Any(e => e.Name.LocalName == "VisualElements"
            && string.Equals((string?)e.Attribute("AppListEntry"), "none", StringComparison.OrdinalIgnoreCase));

    // The exe can sit in a subfolder of its package, so walk up to the folder holding the manifest.
    private static string? FindPackageRoot(string? startDir)
    {
        for (var dir = startDir; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, ManifestName)))
                return dir;
            if (string.Equals(Path.GetFileName(dir), "WindowsApps", StringComparison.OrdinalIgnoreCase))
                break; // reached the store root without finding one
        }
        return null;
    }

    // Package folder "Name_Version_Arch[_ResourceId]__PublisherId" → family name "Name_PublisherId".
    private static string? FamilyName(string packageFolder)
    {
        int first = packageFolder.IndexOf('_');
        int last = packageFolder.LastIndexOf('_');
        if (first <= 0 || last <= first || last == packageFolder.Length - 1)
            return null;
        return string.Concat(packageFolder.AsSpan(0, first), "_", packageFolder.AsSpan(last + 1));
    }
}
