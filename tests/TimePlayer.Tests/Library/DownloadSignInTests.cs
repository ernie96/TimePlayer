using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Common;
using TimePlayer.Core.Common.Process;
using TimePlayer.Core.Community;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Library;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Library;

/// <summary>Downloading a video YouTube only gives signed-in viewers (age-restricted): the sign-in is used, as for playback.</summary>
public class DownloadSignInTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"timeplayer-dl-{Guid.NewGuid():N}");

    public DownloadSignInTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class Account : IYouTubeAccount
    {
        public bool IsSignedIn { get; init; }
        public AccountUse Use { get; init; } = AccountUse.RestrictedOnly;
        public List<string> Written { get; } = new();
        public Task<Result<int>> ImportFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result<int>> ImportFromBrowserAsync(string browser, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Result<int> ImportCookies(IEnumerable<SignInCookie> cookies) => throw new NotSupportedException();
        public void SignOut() { }
        public IReadOnlyList<KeyValuePair<string, string>> RequestHeaders(DateTimeOffset now) => Array.Empty<KeyValuePair<string, string>>();

        public string? WriteTemporaryCookiesFile()
        {
            var path = Path.Combine(Path.GetTempPath(), $"timeplayer-yt-test-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, "# Netscape HTTP Cookie File\n");
            Written.Add(path);
            return path;
        }
    }

    private (DownloadService Service, FakeProcessRunner Runner) Create(IYouTubeAccount? account)
    {
        var config = new ConfigurationService(
            Options.Create(new AppConfig { Backend = new BackendConfig { YtDlpDownloadDirectory = _folder } }),
            Path.Combine(_folder, "settings.json"));
        var runner = new FakeProcessRunner
        {
            // Without cookies YouTube refuses, as it does for age-restricted videos; with them it works.
            Handler = (_, args, _) => args.Contains("--cookies")
                ? new ProcessExecutionResult { ExitCode = 0, StandardOutput = "[download] Destination: song.m4a", StandardError = string.Empty }
                : new ProcessExecutionResult { ExitCode = 1, StandardOutput = string.Empty, StandardError = "ERROR: [youtube] oVAj10cLw4k: Sign in to confirm your age. This video may be inappropriate for some users." }
        };
        var ytDlp = new YtDlpBackendProvider(new FakeYtDlpManager(), runner, config, NullLogger<YtDlpBackendProvider>.Instance);
        return (new DownloadService(ytDlp, config, account), runner);
    }

    [Fact]
    public async Task AgeRestricted_DownloadsWithTheSignIn_ThenDeletesTheCookiesFile()
    {
        var account = new Account { IsSignedIn = true };
        var (service, runner) = Create(account);

        var result = await service.DownloadAsync("oVAj10cLw4k", audioOnly: true, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, runner.Invocations.Count);
        Assert.DoesNotContain("--cookies", runner.Invocations[0].Arguments); // tried without it first
        var file = Assert.Single(account.Written);
        Assert.Contains(file, runner.Invocations[1].Arguments);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task NotSignedIn_SaysWhereToSignIn_AndTriesOnce()
    {
        var (service, runner) = Create(new Account { IsSignedIn = false });

        var result = await service.DownloadAsync("oVAj10cLw4k", audioOnly: true, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Settings, YouTube account", result.Error!.Message);
        Assert.Single(runner.Invocations);
    }
}
