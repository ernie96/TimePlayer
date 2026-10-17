namespace TimePlayer.Core.Models;

/// <summary>
/// Media stream candidate discovered from Piped, Invidious, or yt-dlp.
/// Represents audio-only, video-only (DASH), or progressive (combined) streams.
/// </summary>
public record StreamCandidate
{
    public required string Url { get; init; }
    public string Format { get; init; } = "mp4";
    public string? QualityLabel { get; init; }
    public int? Height { get; init; }
    public int? Width { get; init; }
    public int? Fps { get; init; }
    public long? Bitrate { get; init; }
    public long? AudioBitrate { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public bool IsAudioOnly { get; init; }
    public bool IsVideoOnly { get; init; }
    public bool IsProgressive { get; init; }
    public long? ContentLength { get; init; }

    /// <summary>
    /// Gets screen reader accessible label describing resolution, framerate, and codecs.
    /// </summary>
    public string AccessibleLabel
    {
        get
        {
            if (IsAudioOnly)
            {
                var kbps = (AudioBitrate ?? Bitrate).HasValue ? $" {(AudioBitrate ?? Bitrate)!.Value / 1000} kbps" : "";
                var codec = !string.IsNullOrEmpty(AudioCodec) ? $" ({AudioCodec})" : "";
                return $"Audio Only{kbps}{codec}";
            }

            var label = QualityLabel ?? (Height.HasValue ? $"{Height.Value}p" : "Standard");
            var fpsStr = (Fps.HasValue && Fps.Value > 30) ? $" {Fps.Value} frames per second" : "";
            var progStr = IsProgressive ? " with audio" : " video track";
            return $"{label}{fpsStr}{progStr}";
        }
    }

    public static StreamCandidate CreateAudio(string url, string format, long bitrate, string? codec = null) => new()
    {
        Url = url,
        Format = format,
        Bitrate = bitrate,
        AudioBitrate = bitrate,
        AudioCodec = codec,
        IsAudioOnly = true,
        IsVideoOnly = false,
        IsProgressive = false
    };

    public static StreamCandidate CreateVideo(string url, string format, int height, int width, int fps, long bitrate, string? codec = null, bool isProgressive = false) => new()
    {
        Url = url,
        Format = format,
        Height = height,
        Width = width,
        Fps = fps,
        Bitrate = bitrate,
        VideoCodec = codec,
        QualityLabel = $"{height}p{(fps > 30 ? fps : "")}",
        IsAudioOnly = false,
        IsVideoOnly = !isProgressive,
        IsProgressive = isProgressive
    };
}
