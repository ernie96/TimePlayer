using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Updates;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Wpf.Views;

/// <summary>
/// The update conversation with the user: check, offer (with what's new), download, verify,
/// hand over to the installer helper, and close so it can run.
/// Startup checks stay silent unless there is something new the user has not declined.
/// </summary>
public sealed partial class UpdateFlow
{
    private readonly IUpdateService _updates;
    private readonly IConfigurationService _config;
    private readonly IAccessibilityService _accessibility;
    private readonly ILogger _logger;
    private bool _busy;

    public UpdateFlow(IUpdateService updates, IConfigurationService config, IAccessibilityService accessibility, ILogger logger)
    {
        _updates = updates;
        _config = config;
        _accessibility = accessibility;
        _logger = logger;
    }

    public async Task RunAsync(Window owner, bool manual)
    {
        if (_busy)
        {
            if (manual) _accessibility.Announce(T("Already checking for updates."), AnnouncementPriority.Medium);
            return;
        }
        _busy = true;
        try
        {
            if (manual) _accessibility.Announce(T("Checking for TimePlayer updates..."), AnnouncementPriority.Medium);
            var check = await _updates.CheckAsync();
            if (!check.IsSuccess)
            {
                _logger.LogWarning("Update check failed: {Error}", check.Error?.Message);
                if (manual) _accessibility.Announce(check.Error?.Message ?? T("Could not check for updates."), AnnouncementPriority.High);
                return;
            }

            if (check.Value is not { } update)
            {
                if (manual) _accessibility.Announce(T("TimePlayer is up to date, version {0}.", AppPaths.Version), AnnouncementPriority.Medium);
                return;
            }

            var versionText = update.Version.ToString(3);
            if (!manual && _config.CurrentConfig.Update.SkippedVersion == versionText)
                return;

            var notes = PlainText(update.ReleaseNotes);
            var answer = MessageBox.Show(owner,
                T("TimePlayer {0} is available. You have {1}.\n\n", versionText, AppPaths.Version) +
                (notes.Length > 0 ? T("What's new:\n{0}\n\n", notes) : string.Empty) +
                T("Download and install it now? TimePlayer closes and opens again by itself.") +
                (manual ? string.Empty : T("\n\nIf you choose No, you will not be reminded about this version. Settings has Check for updates.")),
                T("TimePlayer update"), MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);

            if (answer != MessageBoxResult.Yes)
            {
                if (!manual)
                    await SaveSkippedVersionAsync(versionText);
                return;
            }

            await DownloadAndInstallAsync(owner, update, versionText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update flow failed.");
            if (manual) _accessibility.Announce(T("Updating failed. The log has details."), AnnouncementPriority.High);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task DownloadAndInstallAsync(Window owner, UpdateInfo update, string versionText)
    {
        _accessibility.Announce(T("Downloading TimePlayer {0}...", versionText), AnnouncementPriority.Medium);
        bool halfwaySaid = false;
        var progress = new Progress<double>(fraction =>
        {
            if (!halfwaySaid && fraction >= 0.5)
            {
                halfwaySaid = true;
                _accessibility.Announce(T("Update download halfway."), AnnouncementPriority.Low);
            }
        });

        var download = await _updates.DownloadAsync(update, progress);
        if (!download.IsSuccess)
        {
            _accessibility.Announce(download.Error?.Message ?? T("The update could not be downloaded."), AnnouncementPriority.High);
            return;
        }

        var start = _updates.StartInstall(download.Value);
        if (!start.IsSuccess)
        {
            _accessibility.Announce(start.Error?.Message ?? T("The update could not be started."), AnnouncementPriority.High);
            return;
        }

        _accessibility.Announce(T("Installing TimePlayer {0}. It opens again in a moment.", versionText), AnnouncementPriority.High);
        await Task.Delay(1500); // let the announcement be heard
        owner.Close();           // saves the resume point and history
        Application.Current.Shutdown();
    }

    private async Task SaveSkippedVersionAsync(string version)
    {
        var current = _config.CurrentConfig;
        await _config.UpdateConfigAsync(current with { Update = current.Update with { SkippedVersion = version } });
    }

    /// <summary>Release notes are Markdown; read them as plain lines.</summary>
    public static string PlainText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        var text = markdown.Replace("\r", string.Empty);
        text = MarkdownLinkRegex().Replace(text, "$1");
        text = MarkdownSyntaxRegex().Replace(text, string.Empty);
        text = BulletRegex().Replace(text, "- ");
        text = BlankLinesRegex().Replace(text, "\n\n").Trim();
        return text.Length > 1500 ? text[..1500].TrimEnd() + "..." : text;
    }

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"(^#+\s*|\*\*|__|`)", RegexOptions.Multiline)]
    private static partial Regex MarkdownSyntaxRegex();

    [GeneratedRegex(@"^[ \t]*[*+-][ \t]+", RegexOptions.Multiline)]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();
}
