using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Backends.InnerTube;
using TimePlayer.Core.Community;
using TimePlayer.Core.Models;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Wpf.ViewModels;

public enum AccountPage { Home, Subscriptions, History, Liked, WatchLater }

/// <summary>
/// The optional YouTube account. Off by default: TimePlayer's own library needs no account. Signed in, it can
/// play videos that need signing in and, if chosen, show the account's own pages (Control+9) and send likes
/// and subscriptions to YouTube.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly IYouTubeAccount? _account;
    // Used directly for the account's pages and for radios.
    private readonly InnerTubeBackendProvider? _innerTubeDirect;

    public IReadOnlyList<ChoiceOption<AccountUse>> AccountUseChoices { get; } = new[]
    {
        new ChoiceOption<AccountUse>(AccountUse.Off, T("Don't use my account")),
        new ChoiceOption<AccountUse>(AccountUse.RestrictedOnly, T("Only for videos that need signing in")),
        new ChoiceOption<AccountUse>(AccountUse.Full, T("Also my YouTube home, subscriptions, history and likes")),
    };

    public IReadOnlyList<ChoiceOption<string>> SignInBrowsers { get; } = new[]
    {
        new ChoiceOption<string>("firefox", "Firefox"),
        new ChoiceOption<string>("chrome", "Chrome"),
        new ChoiceOption<string>("edge", "Edge"),
        new ChoiceOption<string>("brave", "Brave"),
        new ChoiceOption<string>("opera", "Opera"),
        new ChoiceOption<string>("vivaldi", "Vivaldi"),
    };

    public IReadOnlyList<ChoiceOption<AccountPage>> AccountPages { get; } = new[]
    {
        new ChoiceOption<AccountPage>(AccountPage.Home, T("Home")),
        new ChoiceOption<AccountPage>(AccountPage.Subscriptions, T("Subscriptions")),
        new ChoiceOption<AccountPage>(AccountPage.History, T("History")),
        new ChoiceOption<AccountPage>(AccountPage.Liked, T("Liked videos")),
        new ChoiceOption<AccountPage>(AccountPage.WatchLater, T("Watch Later")),
    };

    [ObservableProperty] private AccountUse _accountUse;
    [ObservableProperty] private string _signInBrowser = "firefox";
    [ObservableProperty] private AccountPage _selectedAccountPage = AccountPage.Home;
    [ObservableProperty] private bool _isSigningIn;

    public bool IsSignedIn => _account?.IsSignedIn == true;
    public bool IsAccountSection => CurrentSection == BrowseSection.YouTube;
    private bool AccountFeaturesOn => _account?.ForAccountFeatures == true && _innerTubeDirect is not null;

    public string AccountStatus => !IsSignedIn
        ? T("Not signed in.")
        : AccountUse switch
        {
            AccountUse.Off => T("Signed in, but not used. Choose how to use it above."),
            AccountUse.RestrictedOnly => T("Signed in. Used only for videos that need it."),
            _ => T("Signed in. Control+9 shows your YouTube pages; likes and subscriptions are sent to YouTube too.")
        };

    public string SignInSteps =>
        T("TimePlayer works without an account. Signing in is only for what YouTube keeps behind its sign-in.") + "\n\n" +
        T("Easiest: in the Play in TimePlayer browser extension's options, choose \"Export my YouTube sign-in\" while signed in to YouTube. It saves a file to Downloads; choose \"Import a sign-in file\" here and pick it. TimePlayer deletes that file after reading it.") + "\n\n" +
        T("With Firefox, \"Read the sign-in from the browser\" works too. Chrome-based browsers now lock their cookies, so use the extension there.") + "\n\n" +
        T("The sign-in is stored encrypted for your Windows user, only sent to youtube.com, and never included in logs or problem reports. Sign out erases it. YouTube sometimes flags accounts used outside the browser, so a spare account is safest.");

    private void InitializeAccount()
    {
        _accountUse = _configService.CurrentConfig.Account.Use;
        RefreshAccountTab();
    }

    partial void OnAccountUseChanged(AccountUse value)
    {
        var current = _configService.CurrentConfig;
        if (current.Account.Use != value)
            _ = _configService.UpdateConfigAsync(current with { Account = current.Account with { Use = value } });
        OnPropertyChanged(nameof(AccountStatus));
        // The config is saved in the background; the tab follows the choice right away.
        RefreshAccountTab(value);
    }

    partial void OnSelectedAccountPageChanged(AccountPage value)
    {
        if (CurrentSection == BrowseSection.YouTube)
            _ = ShowSectionAsync(BrowseSection.YouTube, moveFocus: false);
    }

    /// <summary>Control+9, YouTube, is there only while the account's pages are in use.</summary>
    private void RefreshAccountTab(AccountUse? use = null)
    {
        if (MusicMode)
            return;
        var wanted = _account?.IsSignedIn == true && _innerTubeDirect is not null && (use ?? _configService.CurrentConfig.Account.Use) == AccountUse.Full;
        var tab = SectionTabs.FirstOrDefault(t => t.Section == BrowseSection.YouTube);
        if (wanted && tab is null)
            SectionTabs.Add(new SectionTab(BrowseSection.YouTube, T("YouTube"), T("Control+9")));
        else if (!wanted && tab is not null)
            SectionTabs.Remove(tab);
    }

    private async Task ShowAccountPageAsync(bool moveFocus, CancellationToken token)
    {
        var label = AccountPages.First(p => p.Value == SelectedAccountPage).Label;
        SectionTitle = T("YouTube: {0}", label);
        SectionEmptyText = T("Nothing here on YouTube yet.");
        if (!AccountFeaturesOn)
        {
            SectionEmptyText = T("Sign in to YouTube in Settings, YouTube account, and choose to use it for your home, subscriptions and history.");
            ShowItems(Array.Empty<SearchResult>(), moveFocus);
            return;
        }

        BeginLoading(T("Loading..."));
        var result = SelectedAccountPage switch
        {
            AccountPage.Home => await _innerTubeDirect!.GetAccountPageAsync("FEwhat_to_watch", token),
            AccountPage.Subscriptions => await _innerTubeDirect!.GetAccountPageAsync("FEsubscriptions", token),
            AccountPage.History => await _innerTubeDirect!.GetAccountPageAsync("FEhistory", token),
            AccountPage.Liked => await PlaylistVideos("LL"),
            _ => await PlaylistVideos("WL"),
        };
        token.ThrowIfCancellationRequested();
        if (!result.IsSuccess)
        {
            SectionEmptyText = result.Error?.Message ?? T("YouTube could not be reached. Press F5 to try again.");
            ShowItems(Array.Empty<SearchResult>(), moveFocus);
            return;
        }
        ShowItems(result.Value, moveFocus);

        async Task<Core.Common.Result<IReadOnlyList<SearchResult>>> PlaylistVideos(string id)
        {
            var page = await _innerTubeDirect!.GetAccountPlaylistAsync(id, token);
            return page.IsSuccess
                ? Core.Common.Result<IReadOnlyList<SearchResult>>.Success(page.Value.Videos)
                : Core.Common.Result<IReadOnlyList<SearchResult>>.Failure(page.Error!);
        }
    }

    // ----- Signing in -----

    /// <summary>Reads a cookies file; the extension's own export is deleted after reading, since it holds the sign-in.</summary>
    public async Task ImportSignInFileAsync(string path)
    {
        if (_account is null)
            return;
        IsSigningIn = true;
        try
        {
            var result = await _account.ImportFileAsync(path);
            if (result.IsSuccess && Path.GetFileName(path).StartsWith("timeplayer-youtube-signin", StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            AfterSignIn(result.IsSuccess, result.Error?.Message);
        }
        finally
        {
            IsSigningIn = false;
        }
    }

    [RelayCommand]
    public async Task SignInFromBrowserAsync()
    {
        if (_account is null || IsSigningIn)
            return;
        IsSigningIn = true;
        _accessibility.Announce(T("Reading the sign-in from the browser..."), AnnouncementPriority.Medium);
        try
        {
            var result = await _account.ImportFromBrowserAsync(SignInBrowser);
            AfterSignIn(result.IsSuccess, result.Error?.Message);
        }
        finally
        {
            IsSigningIn = false;
        }
    }

    private void AfterSignIn(bool success, string? error)
    {
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(AccountStatus));
        if (!success)
        {
            _accessibility.Announce(error ?? T("Signing in did not work."), AnnouncementPriority.High);
            return;
        }
        if (AccountUse == AccountUse.Off)
            AccountUse = AccountUse.RestrictedOnly; // signing in says they want it used; the smallest use
        RefreshAccountTab();
        _accessibility.Announce(T("Signed in to YouTube. {0}", AccountStatus), AnnouncementPriority.High);
    }

    [RelayCommand]
    public void SignOut()
    {
        _account?.SignOut();
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(AccountStatus));
        RefreshAccountTab();
        _accessibility.Announce(T("Signed out. Your sign-in is erased from this computer."), AnnouncementPriority.High);
    }

    // ----- Likes and subscriptions sent to YouTube too -----

    /// <summary>After a like changes in TimePlayer's library: the same on YouTube, when the account is used.</summary>
    private void SyncLikeToYouTube(string videoId, bool liked)
    {
        if (!AccountFeaturesOn)
            return;
        _ = SendToYouTubeAsync(() => _innerTubeDirect!.SetLikeAsync(videoId, liked));
    }

    private void SyncSubscriptionToYouTube(string? channelId, bool subscribed)
    {
        if (!AccountFeaturesOn || string.IsNullOrEmpty(channelId))
            return;
        _ = SendToYouTubeAsync(() => _innerTubeDirect!.SetSubscribedAsync(channelId, subscribed));
    }

    private async Task SendToYouTubeAsync(Func<Task<Core.Common.Result<bool>>> send)
    {
        try
        {
            var result = await send();
            if (!result.IsSuccess)
            {
                _logger.LogInformation("Sending to YouTube failed: {Message}", result.Error?.Message);
                _accessibility.Announce(T("Saved here, but YouTube didn't take it: {0}", result.Error?.Message), AnnouncementPriority.Low);
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Sending to YouTube failed.");
        }
    }
}
