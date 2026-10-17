using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace TimePlayer.Core.Models;

/// <summary>
/// Parser and normalizer for YouTube URLs and identifiers.
/// Supports standard watch URLs, mobile URLs, youtu.be short links, embed/v/shorts/live paths,
/// and timestamp extraction (e.g. ?t=1m30s or ?t=90).
/// </summary>
public sealed record YouTubeUrl
{
    private static readonly Regex WatchPattern = new(@"[?&]v=([a-zA-Z0-9_-]{11})", RegexOptions.Compiled);
    private static readonly Regex PathIdPattern = new(@"/(?:embed|v|shorts|live)/([a-zA-Z0-9_-]{11})", RegexOptions.Compiled);
    private static readonly Regex ShortUrlPattern = new(@"youtu\.be/([a-zA-Z0-9_-]{11})", RegexOptions.Compiled);
    private static readonly Regex TimeHmsPattern = new(@"^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public YouTubeVideoId VideoId { get; }
    public TimeSpan? StartTime { get; }
    public string OriginalInput { get; }
    public string NormalizedWatchUrl => StartTime.HasValue && StartTime.Value > TimeSpan.Zero
        ? $"https://www.youtube.com/watch?v={VideoId.Value}&t={(int)StartTime.Value.TotalSeconds}s"
        : $"https://www.youtube.com/watch?v={VideoId.Value}";

    private YouTubeUrl(YouTubeVideoId videoId, TimeSpan? startTime, string originalInput)
    {
        VideoId = videoId;
        StartTime = startTime;
        OriginalInput = originalInput;
    }

    public static bool TryParse(string? input, [NotNullWhen(true)] out YouTubeUrl? youtubeUrl)
    {
        youtubeUrl = null;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();

        // 1. Direct 11-char Video ID
        if (YouTubeVideoId.TryParse(trimmed, out var directId))
        {
            youtubeUrl = new YouTubeUrl(directId, null, trimmed);
            return true;
        }

        // 2. Parse as URI or URL-like string
        string idString = string.Empty;
        TimeSpan? startTime = null;

        // Try extracting time query parameter
        startTime = ExtractTimestamp(trimmed);

        // Check youtu.be/ID
        var shortMatch = ShortUrlPattern.Match(trimmed);
        if (shortMatch.Success)
        {
            idString = shortMatch.Groups[1].Value;
        }
        else
        {
            // Check watch?v=ID
            var watchMatch = WatchPattern.Match(trimmed);
            if (watchMatch.Success)
            {
                idString = watchMatch.Groups[1].Value;
            }
            else
            {
                // Check /embed/ID, /shorts/ID, /live/ID, /v/ID
                var pathMatch = PathIdPattern.Match(trimmed);
                if (pathMatch.Success)
                {
                    idString = pathMatch.Groups[1].Value;
                }
            }
        }

        if (!string.IsNullOrEmpty(idString) && YouTubeVideoId.TryParse(idString, out var parsedId))
        {
            youtubeUrl = new YouTubeUrl(parsedId, startTime, trimmed);
            return true;
        }

        return false;
    }

    public static YouTubeUrl Parse(string input)
    {
        if (TryParse(input, out var url))
            return url;

        throw new FormatException($"'{input}' is not a valid YouTube URL or video ID.");
    }

    public static bool TryExtractVideoId(string? input, out YouTubeVideoId videoId, out TimeSpan? startTime)
    {
        if (TryParse(input, out var parsed))
        {
            videoId = parsed.VideoId;
            startTime = parsed.StartTime;
            return true;
        }

        videoId = default;
        startTime = null;
        return false;
    }

    public static string Normalize(string input)
    {
        var url = Parse(input);
        return url.NormalizedWatchUrl;
    }

    private static TimeSpan? ExtractTimestamp(string url)
    {
        // Look for ?t= or &t= or ?start= or &start=
        var timeMatch = Regex.Match(url, @"[?&](?:t|start)=([^&#]+)", RegexOptions.IgnoreCase);
        if (!timeMatch.Success)
            return null;

        var rawTime = timeMatch.Groups[1].Value.Trim().TrimEnd('s');

        // Numeric seconds
        if (double.TryParse(rawTime, out var seconds) && seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        // HMS format: e.g. 1h2m3s or 2m15s
        var hmsMatch = TimeHmsPattern.Match(timeMatch.Groups[1].Value.Trim());
        if (hmsMatch.Success)
        {
            int h = hmsMatch.Groups[1].Success ? int.Parse(hmsMatch.Groups[1].Value) : 0;
            int m = hmsMatch.Groups[2].Success ? int.Parse(hmsMatch.Groups[2].Value) : 0;
            int s = hmsMatch.Groups[3].Success ? int.Parse(hmsMatch.Groups[3].Value) : 0;
            return new TimeSpan(h, m, s);
        }

        return null;
    }

    public override string ToString() => NormalizedWatchUrl;
}
