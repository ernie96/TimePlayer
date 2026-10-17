using TimePlayer.Core.Common;
using TimePlayer.Core.Playback.Mpv;

namespace TimePlayer.Tests.Helpers;

public class FakeMpvManager : IMpvManager
{
    public string ExecutablePath { get; set; } = @"C:\TestApp\bin\mpv\mpv.exe";
    public bool IsInstalled { get; set; } = true;
    public int EnsureInstalledCallCount { get; private set; }

    public Task<Result<string>> EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        EnsureInstalledCallCount++;
        return Task.FromResult(Result<string>.Success(ExecutablePath));
    }
}
