using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Community;
using TimePlayer.Core.Configuration;
using TimePlayer.Tests.Accessibility;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Library;

[Collection("Language")]
public class RunCounterTests : IDisposable
{
    private readonly string _settingsFile = Path.Combine(Path.GetTempPath(), $"timeplayer-runs-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        TimePlayer.Core.Localization.Loc.Use("en");
        File.Delete(_settingsFile);
    }

    private (RunCounter Counter, ConfigurationService Config, MockHttpMessageHandler Http) Create(RunsConfig runs, string answer = """{"value":12345}""")
    {
        var config = new ConfigurationService(Options.Create(new AppConfig { Runs = runs }), _settingsFile);
        var http = MockHttpMessageHandler.StringResponse(answer);
        return (new RunCounter(new HttpClient(http), config, NullLogger<RunCounter>.Instance), config, http);
    }

    [Fact]
    public async Task EachStart_IsCountedAndSaved()
    {
        var (counter, config, _) = Create(new RunsConfig { Count = 41 });
        Assert.Equal(42, await counter.RecordRunAsync());
        Assert.Equal(42, config.CurrentConfig.Runs.Count);
        Assert.Contains("\"Count\": 42", File.ReadAllText(_settingsFile));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task NothingIsSent_UnlessThePersonAgreed(bool? share)
    {
        var (counter, _, http) = Create(new RunsConfig { Share = share });
        Assert.Null(await counter.SendRunAsync());
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task SharedStarts_AddOneAndReturnTheTotal()
    {
        var (counter, _, http) = Create(new RunsConfig { Share = true });
        Assert.Equal(12345, await counter.SendRunAsync());
        var request = Assert.Single(http.Requests);
        Assert.Equal(RunCounter.SharedCounterUrl, request.RequestUri!.ToString());
        Assert.Null(request.Content); // only the address: nothing about the person
    }

    [Fact]
    public async Task AnUnreachableCounter_IsQuiet()
    {
        var (counter, _, _) = Create(new RunsConfig { Share = true }, """{"error":"Too many requests"}""");
        Assert.Null(await counter.SendRunAsync());
    }

    [Fact]
    public void Spoken_ReadsNaturally()
    {
        TimePlayer.Core.Localization.Loc.Use("en");
        Assert.Equal("TimePlayer has been run 1 time on this computer.", RunCounter.Spoken(1, null));
        Assert.Equal("TimePlayer has been run 42 times on this computer. Everyone together: 12,345 times.", RunCounter.Spoken(42, 12345));
        TimePlayer.Core.Localization.Loc.Use("uk");
        Assert.Equal("TimePlayer запускали 3 рази на цьому комп’ютері.", RunCounter.Spoken(3, null));
        TimePlayer.Core.Localization.Loc.Use("ro");
        Assert.Equal("TimePlayer a fost pornit de 20 de ori pe acest computer.", RunCounter.Spoken(20, null));
    }
}
