using System.Text.Json.Serialization;

namespace TimePlayer.Core.Backends.YtDlp;

/// <summary>
/// Root JSON object returned by yt-dlp for search queries or playlists (--dump-single-json).
/// </summary>
public record YtDlpSearchResult
{
    [JsonPropertyName("_type")]
    public string? Type { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("entries")]
    public List<YtDlpSearchEntry>? Entries { get; init; }
}

/// <summary>
/// A single item in the search results entries list.
/// </summary>
public record YtDlpSearchEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("uploader")]
    public string? Uploader { get; init; }

    [JsonPropertyName("uploader_id")]
    public string? UploaderId { get; init; }

    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; init; }

    [JsonPropertyName("duration")]
    public double? DurationSeconds { get; init; }

    [JsonPropertyName("view_count")]
    public long? ViewCount { get; init; }

    [JsonPropertyName("upload_date")]
    public string? UploadDate { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("thumbnails")]
    public List<YtDlpThumbnail>? Thumbnails { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

/// <summary>
/// Detailed JSON object returned by yt-dlp for a single video (--dump-single-json).
/// </summary>
public record YtDlpVideoInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("uploader")]
    public string? Uploader { get; init; }

    [JsonPropertyName("uploader_id")]
    public string? UploaderId { get; init; }

    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; init; }

    [JsonPropertyName("duration")]
    public double? DurationSeconds { get; init; }

    [JsonPropertyName("view_count")]
    public long? ViewCount { get; init; }

    [JsonPropertyName("like_count")]
    public long? LikeCount { get; init; }

    [JsonPropertyName("upload_date")]
    public string? UploadDate { get; init; }

    [JsonPropertyName("is_live")]
    public bool? IsLive { get; init; }

    [JsonPropertyName("thumbnails")]
    public List<YtDlpThumbnail>? Thumbnails { get; init; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; init; }

    [JsonPropertyName("formats")]
    public List<YtDlpFormat>? Formats { get; init; }

    [JsonPropertyName("subtitles")]
    public Dictionary<string, List<YtDlpSubtitleTrack>>? Subtitles { get; init; }

    [JsonPropertyName("automatic_captions")]
    public Dictionary<string, List<YtDlpSubtitleTrack>>? AutomaticCaptions { get; init; }
}

/// <summary>
/// Thumbnail metadata from yt-dlp.
/// </summary>
public record YtDlpThumbnail
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

/// <summary>
/// Audio or video stream format from yt-dlp.
/// </summary>
public record YtDlpFormat
{

    [JsonPropertyName("ext")]
    public string? Ext { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("fps")]
    public double? Fps { get; init; }

    [JsonPropertyName("vcodec")]
    public string? VCodec { get; init; }

    [JsonPropertyName("acodec")]
    public string? ACodec { get; init; }

    [JsonPropertyName("tbr")]
    public double? TotalBitrateKbps { get; init; }

    [JsonPropertyName("vbr")]
    public double? VideoBitrateKbps { get; init; }

    [JsonPropertyName("abr")]
    public double? AudioBitrateKbps { get; init; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }

    /// <summary>For YouTube audio tracks, their name: "English (United States) original (default)", "... descriptive".</summary>
    [JsonPropertyName("format_note")]
    public string? FormatNote { get; init; }

    [JsonIgnore]
    public bool IsAudioOnly => (VCodec == "none" || string.IsNullOrEmpty(VCodec)) && !string.IsNullOrEmpty(ACodec) && ACodec != "none";

    [JsonIgnore]
    public bool IsVideoOnly => (ACodec == "none" || string.IsNullOrEmpty(ACodec)) && !string.IsNullOrEmpty(VCodec) && VCodec != "none";

    [JsonIgnore]
    public bool IsProgressive => !string.IsNullOrEmpty(VCodec) && VCodec != "none" && !string.IsNullOrEmpty(ACodec) && ACodec != "none";
}

/// <summary>
/// Subtitle or closed-caption track entry from yt-dlp.
/// </summary>
public record YtDlpSubtitleTrack
{
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("ext")]
    public string? Ext { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
