using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TimePlayer.Core.Community;
using TimePlayer.Core.Configuration;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Wpf.Views;

/// <summary>
/// Google's own sign-in page, for reading your YouTube lists without Google Takeout. When the page has
/// signed you in to YouTube, the window hands the YouTube cookies over and closes by itself. It uses a
/// browser profile of its own, which <see cref="ForgetBrowser"/> deletes afterwards, so the Google
/// sign-in doesn't stay on the computer.
/// </summary>
public sealed class GoogleSignInWindow : Window
{
    private const string SignInUrl =
        "https://accounts.google.com/ServiceLogin?service=youtube&passive=true" +
        "&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26next%3D%252F";

    public static string BrowserFolder => Path.Combine(AppPaths.DataFolder, "signin-browser");

    private readonly Func<IReadOnlyList<SignInCookie>, bool> _accept;
    private readonly WebView2 _web = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private bool _busy;
    private string? _lastTried;

    private GoogleSignInWindow(Func<IReadOnlyList<SignInCookie>, bool> accept)
    {
        _accept = accept;
        Title = T("Sign in to YouTube");
        Width = 520;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetName(_web, T("Google sign-in page"));
        Content = _web;
        _poll.Tick += async (_, _) => await PollAsync();
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) =>
        {
            _poll.Stop();
            _web.Dispose();
        };
    }

    /// <summary>Shows the window; true once a YouTube sign-in was accepted.</summary>
    public static bool SignIn(Window owner, Func<IReadOnlyList<SignInCookie>, bool> accept)
    {
        ForgetBrowser(); // start from nothing, in case an earlier one was left behind
        var window = new GoogleSignInWindow(accept) { Owner = owner };
        var result = window.ShowDialog() == true;
        ForgetBrowser();
        return result;
    }

    /// <summary>Deletes the sign-in window's browser profile (cookies, history, saved form data).</summary>
    public static void ForgetBrowser()
    {
        // The browser process lets go of its files a moment after the window closes.
        for (var attempt = 0; attempt < 10 && Directory.Exists(BrowserFolder); attempt++)
        {
            try
            {
                Directory.Delete(BrowserFolder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }
    }

    private async Task StartAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, BrowserFolder);
            await _web.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, T("Signing in needs the Microsoft Edge WebView2 Runtime. Install it from Microsoft, then try again."),
                Title, MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        // Links that would open a new window (help pages, "Create account") open here instead.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };
        core.Navigate(SignInUrl);
        _web.Focus();
        _poll.Start();
    }

    private async Task PollAsync()
    {
        if (_busy || _web.CoreWebView2 is null)
            return;
        _busy = true;
        try
        {
            var cookies = (await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.youtube.com"))
                .Select(ToSignInCookie).ToList();
            if (!cookies.Any(c => c.Name is "SAPISID" or "__Secure-3PAPISID") || !cookies.Any(c => c.Name is "LOGIN_INFO" or "SID"))
                return;
            var key = string.Join(";", cookies.Select(c => c.Name + "=" + c.Value));
            if (key == _lastTried)
                return;
            _lastTried = key;
            if (_accept(cookies))
            {
                _poll.Stop();
                DialogResult = true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The page is between navigations; the next poll tries again.
        }
        finally
        {
            _busy = false;
        }
    }

    private static SignInCookie ToSignInCookie(CoreWebView2Cookie cookie) => new(
        cookie.Domain,
        cookie.Domain.StartsWith('.'),
        cookie.Path,
        cookie.IsSecure,
        cookie.IsSession ? 0 : new DateTimeOffset(cookie.Expires.ToUniversalTime()).ToUnixTimeSeconds(),
        cookie.Name,
        cookie.Value);
}
