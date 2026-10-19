using TimePlayer.Core.Models;
using Xunit;

namespace TimePlayer.Tests.Models;

public class StreamCandidateTests
{
    [Fact]
    public void CreateAudio_ConfiguresAudioCandidateCorrectly()
    {
        var candidate = StreamCandidate.CreateAudio(
            url: "https://stream.example.com/audio.m4a",
            format: "m4a",
            bitrate: 128_000,
            codec: "mp4a.40.2");

        Assert.True(candidate.IsAudioOnly);
        Assert.False(candidate.IsVideoOnly);
        Assert.False(candidate.IsProgressive);
        Assert.Equal(128_000, candidate.Bitrate);
        Assert.Equal("mp4a.40.2", candidate.AudioCodec);
        Assert.Contains("Audio Only 128 kbps (mp4a.40.2)", candidate.AccessibleLabel);
    }

    [Fact]
    public void CreateVideo_ConfiguresVideoCandidateCorrectly()
    {
        var progressive = StreamCandidate.CreateVideo(
            url: "https://stream.example.com/720p.mp4",
            format: "mp4",
            height: 720,
            width: 1280,
            fps: 30,
            bitrate: 2_500_000,
            codec: "avc1.4d401f",
            isProgressive: true);

        Assert.False(progressive.IsAudioOnly);
        Assert.False(progressive.IsVideoOnly);
        Assert.True(progressive.IsProgressive);
        Assert.Equal("720p", progressive.QualityLabel);
        Assert.Contains("720p with audio", progressive.AccessibleLabel);

        var adaptive60Fps = StreamCandidate.CreateVideo(
            url: "https://stream.example.com/1080p60.mp4",
            format: "mp4",
            height: 1080,
            width: 1920,
            fps: 60,
            bitrate: 5_000_000,
            codec: "avc1.64002a",
            isProgressive: false);

        Assert.True(adaptive60Fps.IsVideoOnly);
        Assert.Contains("1080p60", adaptive60Fps.AccessibleLabel);
        Assert.Contains("60 frames per second", adaptive60Fps.AccessibleLabel);
    }

    [Fact]
    public void StreamResolutionResult_StoresStreamSelection()
    {
        var videoStream = StreamCandidate.CreateVideo("https://stream/video.mp4", "mp4", 1080, 1920, 60, 4_000_000, "avc1", isProgressive: false);
        var audioStream = StreamCandidate.CreateAudio("https://stream/audio.m4a", "m4a", 160_000, "opus");

        var resolution = new StreamResolutionResult
        {
            VideoId = new YouTubeVideoId("dQw4w9WgXcQ"),
            SelectedVideoStream = videoStream,
            SelectedAudioStream = audioStream,
            DirectPlaybackUrl = videoStream.Url,
            AudioTrackUrl = audioStream.Url,
            SourceProvider = BackendProviderType.Invidious
        };

        Assert.Equal("dQw4w9WgXcQ", resolution.VideoId.Value);
        Assert.Equal(videoStream.Url, resolution.DirectPlaybackUrl);
        Assert.Equal(audioStream.Url, resolution.AudioTrackUrl);
        Assert.Equal(BackendProviderType.Invidious, resolution.SourceProvider);
    }
}
