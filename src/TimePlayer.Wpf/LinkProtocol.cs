using Microsoft.Win32;
using TimePlayer.Core.Models;

namespace TimePlayer.Wpf;

/// <summary>
/// Registers TimePlayer as the app for timeplayer:// links, for the current Windows user only (no
/// administrator needed). The installer adds the same entry; this keeps it pointing at the right
/// TimePlayer.exe (a portable copy may be moved) and removes it when the setting is turned off.
/// </summary>
internal static class LinkProtocol
{
    private const string KeyPath = @"Software\Classes\" + IncomingLink.Scheme;

    private static string Command(string exe) => $"\"{exe}\" \"%1\"";

    public static void Apply(bool enabled)
    {
        // The extension's direct connection follows the same setting.
        NativeMessagingHost.Apply(enabled);
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return;
        if (!enabled)
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
            return;
        }

        using (var existing = Registry.CurrentUser.OpenSubKey(KeyPath + @"\shell\open\command"))
        {
            if (existing?.GetValue(null) as string == Command(exe))
                return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(null, "URL:TimePlayer link");
        key.SetValue("URL Protocol", string.Empty);
        using (var icon = key.CreateSubKey("DefaultIcon"))
            icon.SetValue(null, $"\"{exe}\",0");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue(null, Command(exe));
    }
}
