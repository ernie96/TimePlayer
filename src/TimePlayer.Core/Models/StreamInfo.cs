namespace TimePlayer.Core.Models;

/// <summary>
/// Media stream details for audio/video playback.
/// </summary>
public record StreamInfo
{
    public required string Url { get; init; }
    public string? QualityLabel { get; init; }
    public int? Height { get; init; }
    public int? Width { get; init; }
    public int? Fps { get; init; }
    public long? Bitrate { get; init; }
    public string? MimeType { get; init; }
    public string? Codecs { get; init; }
    public bool IsAudioOnly { get; init; }
    public bool IsVideoOnly { get; init; }

    /// <summary>
    /// Gets a clear, readable description of the stream for accessibility and UI selection.
    /// </summary>
    public string AccessibleName
    {
        get
        {
            if (IsAudioOnly)
            {
                var kbps = Bitrate.HasValue ? $" ({Bitrate.Value / 1000} kbps)" : "";
                return $"Audio Only{kbps}";
            }

            var label = QualityLabel ?? (Height.HasValue ? $"{Height.Value}p" : "Standard");
            var fpsStr = (Fps.HasValue && Fps.Value > 30) ? $" {Fps.Value}fps" : "";
            return $"{label}{fpsStr}";
        }
    }
}
