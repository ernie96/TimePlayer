using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Community;
using TimePlayer.Core.Configuration;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Library;

public class YouTubeAccountTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"timeplayer-account-{Guid.NewGuid():N}");
    private string Store => Path.Combine(_folder, "signin.dat");

    public YouTubeAccountTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private const string CookiesTxt = "# Netscape HTTP Cookie File\n" +
        ".youtube.com\tTRUE\t/\tTRUE\t1999999999\tSAPISID\tabc123\n" +
        "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1999999999\tLOGIN_INFO\tlogin\n" +
        ".youtube.com\tTRUE\t/\tTRUE\t1999999999\t__Secure-3PAPISID\tthree\n" +
        ".google.com\tTRUE\t/\tTRUE\t1999999999\tSID\tgoogleSid\n" +
        ".bank.example\tTRUE\t/\tTRUE\t1999999999\tsession\tSECRET\n";

    private YouTubeAccount Create(AccountUse use = AccountUse.Full) =>
        new(new ConfigurationService(Options.Create(new AppConfig { Account = new AccountConfig { Use = use } }), Path.Combine(_folder, "settings.json")),
            new FakeYtDlpManager(), new FakeProcessRunner(), NullLogger<YouTubeAccount>.Instance, Store);

    private string WriteFile(string text)
    {
        var path = Path.Combine(_folder, "timeplayer-youtube-signin.txt");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task OnlyYouTubeAndGoogleCookies_AreKept_AndStoredEncrypted()
    {
        var account = Create();
        var result = await account.ImportFileAsync(WriteFile(CookiesTxt));

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Value);
        Assert.True(account.IsSignedIn);
        var stored = File.ReadAllBytes(Store);
        Assert.DoesNotContain("abc123", Encoding.UTF8.GetString(stored));          // not readable as text
        Assert.DoesNotContain("SECRET", Encoding.UTF8.GetString(
            ProtectedData.Unprotect(stored, Encoding.UTF8.GetBytes("TimePlayer YouTube sign-in"), DataProtectionScope.CurrentUser)));

        Assert.True(Create().IsSignedIn); // read back after a restart
    }

    [Fact]
    public async Task AFileWithoutASignIn_IsRefused()
    {
        var account = Create();
        var result = await account.ImportFileAsync(WriteFile(".youtube.com\tTRUE\t/\tTRUE\t0\tPREF\tf1=1\n"));
        Assert.False(result.IsSuccess);
        Assert.False(account.IsSignedIn);
    }

    [Fact]
    public async Task RequestHeaders_CarryOnlyYouTubeCookies_AndTheSapisidHash()
    {
        var account = Create();
        await account.ImportFileAsync(WriteFile(CookiesTxt));
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var headers = account.RequestHeaders(now).ToDictionary(h => h.Key, h => h.Value);

        Assert.Equal("SAPISID=abc123; LOGIN_INFO=login; __Secure-3PAPISID=three", headers["Cookie"]);
        var expected = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes("1700000000 abc123 https://www.youtube.com")));
        Assert.StartsWith($"SAPISIDHASH 1700000000_{expected}", headers["Authorization"]);
        Assert.Contains("SAPISID3PHASH 1700000000_", headers["Authorization"]);
        Assert.Equal("https://www.youtube.com", headers["Origin"]);
    }

    [Fact]
    public async Task SignOut_ErasesTheSignIn()
    {
        var account = Create();
        await account.ImportFileAsync(WriteFile(CookiesTxt));
        account.SignOut();
        Assert.False(account.IsSignedIn);
        Assert.False(File.Exists(Store));
        Assert.Empty(account.RequestHeaders(DateTimeOffset.UtcNow));
        Assert.Null(account.WriteTemporaryCookiesFile());
    }

    [Theory]
    [InlineData(AccountUse.Off, false, false)]
    [InlineData(AccountUse.RestrictedOnly, true, false)]
    [InlineData(AccountUse.Full, true, true)]
    public async Task TheChosenUse_DecidesWhatTheSignInIsFor(AccountUse use, bool restricted, bool features)
    {
        IYouTubeAccount account = Create(use);
        await account.ImportFileAsync(WriteFile(CookiesTxt));
        Assert.Equal(restricted, account.ForRestrictedVideos);
        Assert.Equal(features, account.ForAccountFeatures);
    }

    [Fact]
    public async Task TheTemporaryFileForYtDlp_HasTheKeptCookiesInNetscapeFormat()
    {
        var account = Create();
        await account.ImportFileAsync(WriteFile(CookiesTxt));
        var path = account.WriteTemporaryCookiesFile()!;
        try
        {
            var reread = YouTubeAccount.Parse(File.ReadAllText(path));
            Assert.Equal(4, reread.Count);
            Assert.DoesNotContain(reread, c => c.Domain.Contains("bank"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
