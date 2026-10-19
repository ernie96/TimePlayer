using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Configuration;

namespace TimePlayer.Core.Community;

public enum SegmentCategory
{
    Sponsor,
    SelfPromotion,
    Interaction,
    Intro,
    Outro,
    Preview,
    Filler,
    NonMusic,
    Highlight,
    ExclusiveAccess
}

public enum SegmentKind
{
    /// <summary>A stretch to skip (or mute).</summary>
    Skip,
    /// <summary>A label for the whole video, such as "sponsored" or "exclusive access".</summary>
    WholeVideo,
    /// <summary>A single point: the highlight.</summary>
    Highlight
}

public sealed record SkipSegment(double Start, double End, SegmentCategory Category, SegmentKind Kind, string Uuid)
{
    public TimeSpan Length => TimeSpan.FromSeconds(Math.Max(0, End - Start));

    /// <summary>Spoken category name: "sponsor", "self-promotion", …</summary>
    public string Name => Category switch
    {
        SegmentCategory.Sponsor => "sponsor",
        SegmentCategory.SelfPromotion => "self-promotion",
        SegmentCategory.Interaction => "reminder to like or subscribe",
        SegmentCategory.Intro => "intro",
        SegmentCategory.Outro => "outro",
        SegmentCategory.Preview => "preview",
        SegmentCategory.Filler => "filler",
        SegmentCategory.NonMusic => "non-music part",
        SegmentCategory.Highlight => "highlight",
        SegmentCategory.ExclusiveAccess => "exclusive access",
        _ => Category.ToString()
    };
}

public interface ISponsorBlockService
{
    /// <summary>Segments of a video; an empty list when there are none or SponsorBlock can't be reached.</summary>
    Task<IReadOnlyList<SkipSegment>> GetSegmentsAsync(string videoId, CancellationToken cancellationToken = default);
}

/// <summary>
/// SponsorBlock lookups through the privacy-preserving endpoint: only the first four characters of the
/// SHA-256 of the video id are sent, and the matching video is picked out of the answer locally.
/// </summary>
public sealed class SponsorBlockService : ISponsorBlockService
{
    private static readonly string[] ApiCategories =
        { "sponsor", "selfpromo", "interaction", "intro", "outro", "preview", "filler", "music_offtopic", "poi_highlight", "exclusive_access" };
    private static readonly string[] ApiActions = { "skip", "mute", "full", "poi" };

    private readonly HttpClient _http;
    private readonly IConfigurationService _config;
    private readonly ILogger<SponsorBlockService> _logger;

    public SponsorBlockService(HttpClient http, IConfigurationService config, ILogger<SponsorBlockService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SkipSegment>> GetSegmentsAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var settings = _config.CurrentConfig.SponsorBlock;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(videoId))
            return Array.Empty<SkipSegment>();

        var prefix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(videoId)))[..4].ToLowerInvariant();
        var url = $"{settings.ServerUrl.TrimEnd('/')}/api/skipSegments/{prefix}" +
                  $"?categories={Uri.EscapeDataString(JsonSerializer.Serialize(ApiCategories))}" +
                  $"&actionTypes={Uri.EscapeDataString(JsonSerializer.Serialize(ApiActions))}";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.GetAsync(url, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Array.Empty<SkipSegment>(); // no video with this prefix has segments
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("SponsorBlock answered {Status}.", (int)response.StatusCode);
                return Array.Empty<SkipSegment>();
            }
            return Parse(await response.Content.ReadAsStringAsync(timeout.Token), videoId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            _logger.LogInformation("SponsorBlock lookup failed: {Message}", ex.Message);
            return Array.Empty<SkipSegment>();
        }
    }

    /// <summary>Picks this video's segments out of a hash-prefix answer.</summary>
    public static IReadOnlyList<SkipSegment> Parse(string json, string videoId)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<SkipSegment>();
        var segments = new List<SkipSegment>();
        foreach (var video in doc.RootElement.EnumerateArray())
        {
            if (!video.TryGetProperty("videoID", out var id) || id.GetString() != videoId ||
                !video.TryGetProperty("segments", out var list) || list.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var s in list.EnumerateArray())
            {
                if (!s.TryGetProperty("segment", out var range) || range.ValueKind != JsonValueKind.Array || range.GetArrayLength() < 2)
                    continue;
                var category = (s.TryGetProperty("category", out var c) ? c.GetString() : null) switch
                {
                    "sponsor" => SegmentCategory.Sponsor,
                    "selfpromo" => SegmentCategory.SelfPromotion,
                    "interaction" => SegmentCategory.Interaction,
                    "intro" => SegmentCategory.Intro,
                    "outro" => SegmentCategory.Outro,
                    "preview" => SegmentCategory.Preview,
                    "filler" => SegmentCategory.Filler,
                    "music_offtopic" => SegmentCategory.NonMusic,
                    "poi_highlight" => SegmentCategory.Highlight,
                    "exclusive_access" => SegmentCategory.ExclusiveAccess,
                    _ => (SegmentCategory?)null
                };
                if (category is null)
                    continue;
                var kind = (s.TryGetProperty("actionType", out var a) ? a.GetString() : "skip") switch
                {
                    "full" => SegmentKind.WholeVideo,
                    "poi" => SegmentKind.Highlight,
                    _ => SegmentKind.Skip
                };
                segments.Add(new SkipSegment(
                    range[0].GetDouble(), range[1].GetDouble(), category.Value, kind,
                    s.TryGetProperty("UUID", out var u) ? u.GetString() ?? string.Empty : string.Empty));
            }
        }
        return segments.OrderBy(s => s.Start).ToList();
    }
}

/// <summary>What the player should do at the current position.</summary>
public abstract record SegmentEvent
{
    public sealed record Skip(SkipSegment Segment) : SegmentEvent;
    public sealed record Announce(SkipSegment Segment) : SegmentEvent;
    public sealed record WholeVideo(SkipSegment Segment) : SegmentEvent;
    public sealed record HighlightAvailable(SkipSegment Segment) : SegmentEvent;
}

/// <summary>
/// Decides, as playback moves, when to skip or announce a segment. Each segment is handled once per
/// video: seeking back into one that was skipped plays it, which is what someone seeking there wants.
/// </summary>
public sealed class SegmentTracker
{
    /// <summary>Segments ending this close to the current position are left alone; skipping would gain nothing.</summary>
    private const double EndMargin = 0.5;

    private readonly HashSet<SkipSegment> _handled = new();
    private IReadOnlyList<SkipSegment> _segments = Array.Empty<SkipSegment>();
    private bool _labelsReported;

    public string? VideoId { get; private set; }
    public IReadOnlyList<SkipSegment> Segments => _segments;

    public void Reset(string? videoId, IReadOnlyList<SkipSegment>? segments = null)
    {
        VideoId = videoId;
        _segments = segments ?? Array.Empty<SkipSegment>();
        _handled.Clear();
        _labelsReported = false;
    }

    /// <summary>Playing the same video again: every segment is handled afresh.</summary>
    public void Rewind()
    {
        _handled.Clear();
        _labelsReported = false;
    }

    public static SegmentAction ActionFor(SponsorBlockConfig settings, SegmentCategory category) => category switch
    {
        SegmentCategory.Sponsor => settings.Sponsor,
        SegmentCategory.SelfPromotion => settings.SelfPromotion,
        SegmentCategory.Interaction => settings.Interaction,
        SegmentCategory.Intro => settings.Intro,
        SegmentCategory.Outro => settings.Outro,
        SegmentCategory.Preview => settings.Preview,
        SegmentCategory.Filler => settings.Filler,
        SegmentCategory.NonMusic => settings.NonMusic,
        _ => SegmentAction.Off
    };

    /// <summary>Events for this position, in the order they should be acted on.</summary>
    public IReadOnlyList<SegmentEvent> Update(double position, SponsorBlockConfig settings)
    {
        var events = new List<SegmentEvent>();
        if (!settings.Enabled || _segments.Count == 0)
            return events;

        if (!_labelsReported)
        {
            _labelsReported = true;
            if (settings.AnnounceWholeVideoLabels)
            {
                foreach (var label in _segments.Where(s => s.Kind == SegmentKind.WholeVideo).DistinctBy(s => s.Category))
                    events.Add(new SegmentEvent.WholeVideo(label));
            }
            if (settings.AnnounceHighlight && _segments.FirstOrDefault(s => s.Kind == SegmentKind.Highlight) is { } highlight)
                events.Add(new SegmentEvent.HighlightAvailable(highlight));
        }

        foreach (var segment in _segments)
        {
            if (segment.Kind != SegmentKind.Skip || _handled.Contains(segment))
                continue;
            if (position < segment.Start || position >= segment.End - EndMargin)
                continue;
            var action = ActionFor(settings, segment.Category);
            if (action == SegmentAction.Off)
                continue;
            _handled.Add(segment);
            events.Add(action == SegmentAction.Skip ? new SegmentEvent.Skip(segment) : new SegmentEvent.Announce(segment));
            break; // one at a time; a skip moves the position anyway
        }
        return events;
    }

    /// <summary>The segment playing now that isn't switched off, for the S key.</summary>
    public SkipSegment? SegmentAt(double position, SponsorBlockConfig settings) =>
        _segments.FirstOrDefault(s => s.Kind == SegmentKind.Skip && position >= s.Start && position < s.End - EndMargin &&
                                      ActionFor(settings, s.Category) != SegmentAction.Off);

    public SkipSegment? Highlight => _segments.FirstOrDefault(s => s.Kind == SegmentKind.Highlight);
}
