using TimePlayer.Core.Backends.YtDlp;
using TimePlayer.Core.Common;
using TimePlayer.Core.Library;

namespace TimePlayer.Tests.Helpers;

public sealed class FakeDownloadService : IDownloadService
{
    public string DownloadFolder { get; set; } = @"C:\Users\test\Downloads\TimePlayer";
    public bool CanConvert { get; set; } = true;
    public List<(string Id, bool AudioOnly)> Requests { get; } = new();

    public Task<Result<string>> DownloadAsync(string videoId, bool audioOnly, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        Requests.Add((videoId, audioOnly));
        progress?.Report(new DownloadProgress { Percent = 60 });
        return Task.FromResult(Result<string>.Success(Path.Combine(DownloadFolder, videoId + (audioOnly ? ".mp3" : ".mp4"))));
    }
}
