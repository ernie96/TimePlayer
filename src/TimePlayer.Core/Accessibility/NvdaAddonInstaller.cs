using System.IO;
using System.Reflection;

namespace TimePlayer.Core.Accessibility;

public enum NvdaAddonInstallResult
{
    NvdaNotFound,
    AlreadyCurrent,
    /// <summary>Files were written; NVDA only loads add-ons at startup, so it must be restarted.</summary>
    InstalledRestartRequired,
    Failed
}

/// <summary>
/// Installs the TimePlayer braille add-on (Accessibility/NvdaAddon, embedded in this assembly)
/// into %APPDATA%\nvda\addons\timePlayerBraille.
///
/// NVDA has not loaded %APPDATA%\nvda\globalPlugins or \appModules since 2019.1 (only add-ons
/// and the developer scratchpad are loaded), so the add-on folder is the only reliable location.
/// </summary>
public static class NvdaAddonInstaller
{
    public const int WM_USER = 0x0400;

    /// <summary>Posted by the add-on on a cursor routing key: wParam = cell index, lParam = display cell count.</summary>
    public const int WM_TIMEPLAYER_BRAILLE_TOUCH = WM_USER + 777;

    /// <summary>Posted by the add-on when Ctrl+Shift+D is pressed while TimePlayer is in the foreground.</summary>
    public const int WM_TIMEPLAYER_TOGGLE_BRAILLE = WM_USER + 778;

    public const string AddonName = "timePlayerBraille";

    private static readonly (string ResourceName, string RelativePath)[] AddonFiles =
    [
        ("NvdaAddon/manifest.ini", "manifest.ini"),
        ("NvdaAddon/globalPlugins/timePlayerBraille.py", Path.Combine("globalPlugins", "timePlayerBraille.py")),
    ];

    // Files written to the ignored legacy plugin folders by earlier TimePlayer builds.
    private static readonly string[] LegacyFiles =
    [
        Path.Combine("globalPlugins", "timeplayer_bridge.py"),
        Path.Combine("appModules", "timeplayer.py"),
        Path.Combine("appModules", "timeplayer_wpf.py"),
    ];

    public static NvdaAddonInstallResult LastResult { get; private set; } = NvdaAddonInstallResult.NvdaNotFound;

    public static string? NvdaConfigDirectory
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
                return null;
            var dir = Path.Combine(appData, "nvda");
            return Directory.Exists(dir) ? dir : null;
        }
    }

    /// <summary>True when the add-on files are present (NVDA may still need a restart to load them).</summary>
    public static bool IsInstalled =>
        NvdaConfigDirectory is { } dir && File.Exists(Path.Combine(dir, "addons", AddonName, "manifest.ini"));

    public static NvdaAddonInstallResult EnsureInstalled()
    {
        LastResult = Install();
        return LastResult;
    }

    /// <summary>Removes the add-on. NVDA keeps using the loaded copy until it restarts.</summary>
    public static bool Uninstall()
    {
        if (NvdaConfigDirectory is not { } dir)
            return true;
        var addonDir = Path.Combine(dir, "addons", AddonName);
        try
        {
            if (Directory.Exists(addonDir))
                Directory.Delete(addonDir, recursive: true);
            LastResult = NvdaAddonInstallResult.NvdaNotFound;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static NvdaAddonInstallResult Install()
    {
        var nvdaDir = NvdaConfigDirectory;
        if (nvdaDir is null)
            return NvdaAddonInstallResult.NvdaNotFound;

        try
        {
            RemoveLegacyFiles(nvdaDir);

            var addonDir = Path.Combine(nvdaDir, "addons", AddonName);
            bool changed = false;
            foreach (var (resourceName, relativePath) in AddonFiles)
            {
                var content = ReadResource(resourceName);
                var target = Path.Combine(addonDir, relativePath);
                if (File.Exists(target) && File.ReadAllText(target) == content)
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, content);
                changed = true;
            }

            if (changed)
            {
                // Stale bytecode would otherwise survive an update of the same-sized source.
                var pycache = Path.Combine(addonDir, "globalPlugins", "__pycache__");
                if (Directory.Exists(pycache))
                    Directory.Delete(pycache, recursive: true);
            }

            return changed ? NvdaAddonInstallResult.InstalledRestartRequired : NvdaAddonInstallResult.AlreadyCurrent;
        }
        catch
        {
            return NvdaAddonInstallResult.Failed;
        }
    }

    internal static string ReadResource(string resourceName)
    {
        using var stream = typeof(NvdaAddonInstaller).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded NVDA add-on resource '{resourceName}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void RemoveLegacyFiles(string nvdaDir)
    {
        foreach (var relative in LegacyFiles)
        {
            try { File.Delete(Path.Combine(nvdaDir, relative)); } catch { }
        }

        // NVDA deletes these legacy folders itself at startup once they are empty.
        foreach (var folder in new[] { "globalPlugins", "appModules" })
        {
            var pycache = Path.Combine(nvdaDir, folder, "__pycache__");
            try
            {
                if (Directory.Exists(pycache))
                {
                    foreach (var file in Directory.GetFiles(pycache, "timeplayer*"))
                        File.Delete(file);
                    if (!Directory.EnumerateFileSystemEntries(pycache).Any())
                        Directory.Delete(pycache);
                }
            }
            catch { }
        }

        try { File.Delete(Path.Combine(Path.GetTempPath(), "timeplayer_braille.txt")); } catch { }
    }
}
