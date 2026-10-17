using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Configuration;
using TimePlayer.Tests.Helpers;
using Xunit;

namespace TimePlayer.Tests.Backends;

public class YtDlpManagerTests
{
    private readonly FakeProcessRunner _processRunner = new();
    private readonly ConfigurationService _configService;

    public YtDlpManagerTests()
    {
        var config = new AppConfig();
        _configService = new ConfigurationService(Options.Create(config));
    }

    [Fact]
    public async Task GetVersionAsync_WhenBinaryExists_ReturnsTrimmedVersion()
    {
        _processRunner.NextResult = new()
        {
            ExitCode = 0,
            StandardOutput = "2026.03.01\n",
            StandardError = ""
        };

        var httpClient = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)));

        // Create a temporary file to act as the installed binary
        var tempFile = Path.GetTempFileName();
        try
        {
            await _configService.UpdateConfigAsync(new AppConfig
            {
                Backend = new BackendConfig { YtDlpCustomPath = tempFile }
            });

            var manager = new YtDlpManager(_processRunner, httpClient, _configService, NullLogger<YtDlpManager>.Instance);

            Assert.True(manager.IsInstalled);

            var versionResult = await manager.GetVersionAsync();

            Assert.True(versionResult.IsSuccess);
            Assert.Equal("2026.03.01", versionResult.Value);

            var invocation = Assert.Single(_processRunner.Invocations);
            Assert.Equal(tempFile, invocation.Executable);
            Assert.Contains("--version", invocation.Arguments);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task CheckAndUpdateAsync_InvokesUpdateCommand()
    {
        _processRunner.Handler = (exe, args, _) =>
        {
            if (args.Contains("--update"))
            {
                return new()
                {
                    ExitCode = 0,
                    StandardOutput = "yt-dlp is up to date (2026.03.01)",
                    StandardError = ""
                };
            }
            if (args.Contains("--version"))
            {
                return new()
                {
                    ExitCode = 0,
                    StandardOutput = "2026.03.01",
                    StandardError = ""
                };
            }
            return new() { ExitCode = 0, StandardOutput = "", StandardError = "" };
        };

        var httpClient = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)));
        var tempFile = Path.GetTempFileName();
        try
        {
            await _configService.UpdateConfigAsync(new AppConfig
            {
                Backend = new BackendConfig { YtDlpCustomPath = tempFile }
            });

            var manager = new YtDlpManager(_processRunner, httpClient, _configService, NullLogger<YtDlpManager>.Instance);

            var result = await manager.CheckAndUpdateAsync();

            Assert.True(result.IsSuccess);
            Assert.False(result.Value); // already up to date

            Assert.Contains(_processRunner.Invocations, inv => inv.Arguments.Contains("--update"));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
