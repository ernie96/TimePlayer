using System.Text.Json.Serialization;

namespace TimePlayer.Core.Backends.InnerTube;

public record InnerTubePlayerRequest
{
    [JsonPropertyName("context")]
    public required InnerTubeContext Context { get; init; }

    [JsonPropertyName("videoId")]
    public required string VideoId { get; init; }
}

public record InnerTubeNextRequest
{
    [JsonPropertyName("context")]
    public required InnerTubeContext Context { get; init; }

    [JsonPropertyName("videoId")]
    public required string VideoId { get; init; }
}

public record InnerTubeContext
{
    [JsonPropertyName("client")]
    public required InnerTubeClientInfo Client { get; init; }
}

public record InnerTubeClientInfo
{
    [JsonPropertyName("clientName")]
    public string ClientName { get; init; } = "WEB";

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; init; } = "2.20240101.00.00";

    [JsonPropertyName("hl")]
    public string Hl { get; init; } = "en";

    [JsonPropertyName("gl")]
    public string Gl { get; init; } = "US";
}

public record InnerTubePlayerResponse
{
    [JsonPropertyName("playabilityStatus")]
    public InnerTubePlayabilityStatus? PlayabilityStatus { get; init; }

    [JsonPropertyName("videoDetails")]
    public InnerTubeVideoDetails? VideoDetails { get; init; }

    [JsonPropertyName("streamingData")]
    public InnerTubeStreamingData? StreamingData { get; init; }

    [JsonPropertyName("captions")]
    public InnerTubeCaptions? Captions { get; init; }
}

public record InnerTubePlayabilityStatus
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public record InnerTubeVideoDetails
{
    [JsonPropertyName("videoId")]
    public string? VideoId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("lengthSeconds")]
    public string? LengthSeconds { get; init; }

    [JsonPropertyName("channelId")]
    public string? ChannelId { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("viewCount")]
    public string? ViewCount { get; init; }

}

public record InnerTubeStreamingData
{
    [JsonPropertyName("formats")]
    public List<InnerTubeFormat>? Formats { get; init; }

    [JsonPropertyName("adaptiveFormats")]
    public List<InnerTubeFormat>? AdaptiveFormats { get; init; }
}

public record InnerTubeFormat
{
    [JsonPropertyName("itag")]
    public int Itag { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; init; }

    [JsonPropertyName("bitrate")]
    public long? Bitrate { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }

    [JsonPropertyName("qualityLabel")]
    public string? QualityLabel { get; init; }

    [JsonPropertyName("audioQuality")]
    public string? AudioQuality { get; init; }

}

public record InnerTubeCaptions
{
    [JsonPropertyName("playerCaptionsTracklistRenderer")]
    public InnerTubeCaptionTracklist? PlayerCaptionsTracklistRenderer { get; init; }
}

public record InnerTubeCaptionTracklist
{
    [JsonPropertyName("captionTracks")]
    public List<InnerTubeCaptionTrack>? CaptionTracks { get; init; }
}

public record InnerTubeCaptionTrack
{
    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("name")]
    public InnerTubeTextHolder? Name { get; init; }

    [JsonPropertyName("languageCode")]
    public string? LanguageCode { get; init; }

}

public record InnerTubeTextHolder
{
    [JsonPropertyName("simpleText")]
    public string? SimpleText { get; init; }

    [JsonPropertyName("runs")]
    public List<InnerTubeRun>? Runs { get; init; }
}

public record InnerTubeRun
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }
}
