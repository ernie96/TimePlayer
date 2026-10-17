using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Common;

namespace TimePlayer.Tests.Helpers;

public class FakeYtDlpManager : IYtDlpManager
{
    public string ExecutablePath { get; set; } = @"C:\TestApp\bin\yt-dlp.exe";
    public bool IsInstalled { get; set; } = true;
    public int EnsureCallCount { get; private set; }
    public int UpdateCallCount { get; private set; }

    public Task<Result<string>> EnsureInstalledOrUpdatedAsync(CancellationToken cancellationToken = default)
    {
        EnsureCallCount++;
        return Task.FromResult(Result<string>.Success("2026.03.01"));
    }

    public Task<Result<string>> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<string>.Success("2026.03.01"));
    }

    public Task<Result<bool>> CheckAndUpdateAsync(CancellationToken cancellationToken = default)
    {
        UpdateCallCount++;
        return Task.FromResult(Result<bool>.Success(false));
    }
}
