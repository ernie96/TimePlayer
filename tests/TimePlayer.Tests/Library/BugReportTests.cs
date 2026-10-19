using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TimePlayer.Core.Community;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Library;

public class BugReportTests : IDisposable
{
    private readonly string _logs = Path.Combine(Path.GetTempPath(), $"timeplayer-report-logs-{Guid.NewGuid():N}");

    public BugReportTests() => Directory.CreateDirectory(_logs);

    public void Dispose() => Directory.Delete(_logs, recursive: true);

    private BugReportService Create(MockHttpMessageHandler http) =>
        new(new HttpClient(http), NullLogger<BugReportService>.Instance, _logs);

    [Fact]
    public void TheLog_IsTheLatestFiles_WithoutTheUserName()
    {
        File.WriteAllText(Path.Combine(_logs, "timeplayer-20261018.log"), "old day");
        File.SetLastWriteTimeUtc(Path.Combine(_logs, "timeplayer-20261018.log"), DateTime.UtcNow.AddDays(-1));
        File.WriteAllText(Path.Combine(_logs, "timeplayer-20261019.log"), $@"Loaded C:\Users\{Environment.UserName}\AppData\x");

        var log = Create(MockHttpMessageHandler.Status(HttpStatusCode.OK)).ReadRecentLog();

        Assert.True(log.IndexOf("old day") < log.IndexOf("Loaded"), "newest should be last");
        Assert.Contains(@"C:\Users\<user>\AppData", log);
        Assert.DoesNotContain(Environment.UserName + @"\AppData", log);
    }

    [Fact]
    public void TheUserName_IsTakenOutOfPaths_AndAsAWholeWordOnly()
    {
        var user = Environment.UserName;
        var log = BugReportService.Anonymize($@"Opened C:\Users\Someone Else\Music\a.mp3; user {user} ran; {user}x stays");
        Assert.Contains(@"C:\Users\<user>\Music", log);
        Assert.Contains("user <user> ran", log);
        if (user.Length >= 3)
            Assert.Contains(user + "x stays", log);
    }

    [Fact]
    public async Task OtherAlphabets_AreSentAsThemselves()
    {
        string? json = null;
        long? length = null;
        var http = new MockHttpMessageHandler(request =>
        {
            json = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            length = request.Content.Headers.ContentLength;
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"number":1}""") };
        });
        await Create(http).SendAsync(new BugReport { Description = "Пісня не грає" });
        Assert.Contains("Пісня не грає", json);
        Assert.NotNull(length);
    }

    [Fact]
    public async Task Suggestions_SayTheyAreFeatureRequests()
    {
        string? json = null;
        var http = new MockHttpMessageHandler(request =>
        {
            json = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"number":3}""") };
        });
        var result = await Create(http).SendAsync(new BugReport { Kind = BugReport.Feature, Description = "Lyrics, please" });
        Assert.Equal(3, result.Value);
        Assert.Contains("\"kind\":\"feature\"", json);
        Assert.Contains("\"log\":null", json);
    }

    [Fact]
    public void NoLogs_IsAnEmptyLog() =>
        Assert.Equal(string.Empty, new BugReportService(new HttpClient(), NullLogger<BugReportService>.Instance, Path.Combine(_logs, "missing")).ReadRecentLog());

    [Fact]
    public async Task ASentReport_GivesItsNumber_AndCarriesWhatWasWritten()
    {
        string? json = null;
        var http = new MockHttpMessageHandler(request =>
        {
            json = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"number":12}""") };
        });
        var result = await Create(http).SendAsync(new BugReport { Description = "  Shuffle stops  ", Contact = " ", Log = "log text" });

        Assert.True(result.IsSuccess);
        Assert.Equal(12, result.Value);
        var request = Assert.Single(http.Requests);
        Assert.Equal(BugReportService.RelayUrl, request.RequestUri!.ToString());
        Assert.True(request.Headers.Contains("X-TimePlayer-Report"));
        Assert.Contains("\"description\":\"Shuffle stops\"", json);
        Assert.Contains("\"contact\":null", json);
        Assert.Contains("\"log\":\"log text\"", json);
    }

    [Fact]
    public async Task AnEmptyReport_IsntSent()
    {
        var http = MockHttpMessageHandler.Status(HttpStatusCode.Created);
        Assert.False((await Create(http).SendAsync(new BugReport { Description = "   " })).IsSuccess);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task TooManyReports_SaysToWait()
    {
        var result = await Create(MockHttpMessageHandler.StringResponse("""{"error":"x"}""", (HttpStatusCode)429)).SendAsync(new BugReport { Description = "x" });
        Assert.False(result.IsSuccess);
        Assert.Contains("Wait a minute", result.Error!.Message);
    }

    [Fact]
    public async Task NoConnection_SaysSo()
    {
        var result = await Create(MockHttpMessageHandler.Throws(new HttpRequestException("offline"))).SendAsync(new BugReport { Description = "x" });
        Assert.False(result.IsSuccess);
        Assert.Contains("internet connection", result.Error!.Message);
    }
}
