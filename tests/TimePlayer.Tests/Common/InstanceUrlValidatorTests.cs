using TimePlayer.Core.Common;
using Xunit;

namespace TimePlayer.Tests.Common;

public class InstanceUrlValidatorTests
{
    [Theory]
    [InlineData("https://pipedapi.kavin.rocks")]
    [InlineData("https://invidious.f5.si")]
    [InlineData("https://api.piped.privacy.com.de:8443")]
    public void IsValidInstanceUri_AcceptsValidHttpsUris(string url)
    {
        var uri = new Uri(url);
        var valid = InstanceUrlValidator.IsValidInstanceUri(uri, out var reason);

        Assert.True(valid);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("http://pipedapi.kavin.rocks", "Insecure scheme 'http'")]
    [InlineData("ftp://pipedapi.kavin.rocks", "Insecure scheme 'ftp'")]
    [InlineData("https://user:password@piped.example.com", "credentials")]
    [InlineData("https://127.0.0.1", "Loopback")]
    [InlineData("https://localhost:8080", "localhost")]
    public void IsValidInstanceUri_RejectsInsecureAndDangerousUris(string url, string expectedReasonKeyword)
    {
        InstanceUrlValidator.AllowLocalhostForTesting = false;
        var uri = new Uri(url);
        var valid = InstanceUrlValidator.IsValidInstanceUri(uri, out var reason);

        Assert.False(valid);
        Assert.NotNull(reason);
        Assert.Contains(expectedReasonKeyword, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("  https://pipedapi.kavin.rocks/  ", "https://pipedapi.kavin.rocks/")]
    [InlineData("https://inv.nadeko.net", "https://inv.nadeko.net/")]
    public void TrySanitizeBaseUri_TrimsAndNormalizesUris(string input, string expected)
    {
        var success = InstanceUrlValidator.TrySanitizeBaseUri(input, out var cleanUri, out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(cleanUri);
        Assert.Equal(new Uri(expected), cleanUri);
    }
}
