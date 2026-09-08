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

    /// <summary>What one read of a package's manifest told us about an app: how to launch it
    /// (<paramref name="Aumid"/>) and where its icon artwork lives.</summary>
    /// <param name="LogoBase">The manifest's <c>Square44x44Logo</c> value — a LOGICAL path
    /// (<c>Images\Foo.png</c>); the files on disk are its scale-/targetsize-qualified variants.</param>
    private sealed record PackagedInfo(string Aumid, string ManifestDir, string? LogoBase);

    // A package's manifest can't change without a reinstall, so this is cached for the process
    // lifetime. Null is cached too — most exes aren't packaged and we don't want to re-walk for them.
    private static readonly ConcurrentDictionary<string, PackagedInfo?> Cache = new(StringComparer.OrdinalIgnoreCase);

    // The same entries keyed by AUMID, so an icon load can find the artwork from a
    // "shell:AppsFolder\<aumid>" path (what a running packaged app's tile carries) without re-walking.
    private static readonly ConcurrentDictionary<string, PackagedInfo> ByAumid = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the path sits inside a packaged-app (<c>WindowsApps</c>) folder.</summary>
    internal static bool IsPackagedPath(string? path)
        => !string.IsNullOrEmpty(path) && path.Contains(StoreFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The AUMID to launch and identify a packaged app by, or null when <paramref name="exePath"/>
    /// isn't a packaged app or its manifest can't be read (callers then keep using the raw path).
    /// </summary>
    internal static string? AumidForExe(string? exePath)
        => string.IsNullOrEmpty(exePath) ? null : Cache.GetOrAdd(exePath, Resolve)?.Aumid;

    private static PackagedInfo? Resolve(string exePath)
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
            if (string.IsNullOrEmpty(id))
                return null;

            var info = new PackagedInfo(family + "!" + id, manifestDir, LogoBaseOf(app!));
            ByAumid[info.Aumid] = info;
            return info;
        }
        catch
        {
            return null; // unreadable/unexpected manifest — the caller falls back to the raw path
        }
    }

    // Square44x44Logo is the app-list artwork (the icon Explorer and the taskbar show), declared on
    // the app's VisualElements element — whose namespace prefix varies by schema version.
    private static string? LogoBaseOf(XElement app)
        => app.Elements().Where(e => e.Name.LocalName == "VisualElements")
              .Select(e => (string?)e.Attribute("Square44x44Logo"))
              .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// The packaged app's largest icon asset ON DISK for <paramref name="path"/> — either a
    /// <c>WindowsApps</c> exe or a <c>shell:AppsFolder\{aumid}</c> parsing name — or null when it isn't
    /// a packaged app we've resolved, or ships no readable artwork.
    /// </summary>
    /// <remarks>
    /// Worth bypassing the shell for: <c>IShellItemImageFactory</c> SCALES the asset it picks to the size
    /// asked for, and plenty of packages top out below 256px (Teams' app-list art is 176px at its
    /// largest), so requesting 256 returns an upscale that WPF then shrinks again into the icon cell —
    /// two resamples, visibly aliased. Reading the file gives the artwork at its native size for a
    /// single, high-quality resample.
    /// </remarks>
    internal static string? LargestLogo(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        // ponytail: an AppsFolder pin saved in a previous session is cold until the app runs once (or is
        // pinned by exe path) — the caller then keeps the shell's upscale. Warming it would mean
        // enumerating WindowsApps, which a non-admin can't do.
        var info = path.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase)
            ? ByAumid.GetValueOrDefault(path[AppsFolderPrefix.Length..])
            : Cache.GetOrAdd(path, Resolve);
        if (info?.LogoBase is null)
            return null;

        try
        {
            // "Images\Foo.png" is logical: the real files are "Images\Foo.scale-400.png",
            // "Images\Foo.targetsize-96_altform-unplated.png", … Only unqualified, scale-* and
            // *_altform-unplated variants are considered — a plain targetsize-* can be PLATED (the logo
            // baked onto a solid square), which is exactly what the shell's SIIGBF_ICONONLY avoids.
            string dir = Path.Combine(info.ManifestDir, Path.GetDirectoryName(info.LogoBase) ?? string.Empty);
            string stem = Path.GetFileNameWithoutExtension(info.LogoBase);
            if (!Directory.Exists(dir))
                return null;

            string? best = null;
            int bestWidth = 0;
            foreach (string file in Directory.EnumerateFiles(dir, stem + "*.png"))
            {
                // The glob is a prefix match, so require what follows the stem to be a qualifier list
                // ("<stem>.scale-400") and not a longer name that merely starts the same way
                // ("<stem>Extra.scale-400" is a DIFFERENT asset).
                string qualifiers = Path.GetFileNameWithoutExtension(file)[stem.Length..];
                if (qualifiers.Length != 0 && qualifiers[0] != '.')
                    continue;
                bool usable = qualifiers.Length == 0
                    || qualifiers.Contains(".scale-", StringComparison.OrdinalIgnoreCase)
                    || qualifiers.Contains("altform-unplated", StringComparison.OrdinalIgnoreCase);
                if (!usable)
                    continue;

                int width = PixelWidth(file);
                if (width > bestWidth)
                    (best, bestWidth) = (file, width);
            }
            return best;
        }
        catch
        {
            return null; // unreadable asset folder — the caller falls back to the shell
        }
    }

    /// <summary>A PNG's width without decoding its pixels (header read only); 0 if unreadable.</summary>
    private static int PixelWidth(string file)
    {
        try
        {
            return System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri(file),
                System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                System.Windows.Media.Imaging.BitmapCacheOption.None).PixelWidth;
        }
        catch
        {
            return 0;
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
