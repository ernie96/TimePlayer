using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Common;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Core.Community;

/// <summary>One line of a song. <see cref="Start"/> is null when the lyrics have no timing.</summary>
public sealed record LyricLine(TimeSpan? Start, string Text)
{
    public string AccessibleLabel => Start is { } start
        ? $"{Text}, {Accessibility.AccessibleTextFormatter.FormatTimeSpoken(start)}"
        : Text;

    public override string ToString() => AccessibleLabel;
}

public sealed record SongLyrics(string Artist, string Track, IReadOnlyList<LyricLine> Lines)
{
    /// <summary>Timed lines, so they can be read as they are sung.</summary>
    public bool IsSynced => Lines.Count > 0 && Lines.All(l => l.Start is not null);
}

public interface ILyricsService
{
    Task<Result<SongLyrics>> FindAsync(string title, string channel, TimeSpan duration, CancellationToken cancellationToken = default);
}

/// <summary>
/// Lyrics from LRCLIB (lrclib.net), a free, open lyrics database with timed lines. Only the song's
/// artist, title and length are sent.
/// </summary>
public sealed class LyricsService : ILyricsService
{
    private const string Api = "https://lrclib.net/api/";
    private readonly HttpClient _http;
    private readonly ILogger<LyricsService> _logger;

    public LyricsService(HttpClient http, ILogger<LyricsService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<Result<SongLyrics>> FindAsync(string title, string channel, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var (artist, track) = GuessSong(title, channel);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));

            // The exact match first (artist, title and length), then a search.
            var seconds = (int)Math.Round(duration.TotalSeconds);
            var exact = await GetAsync<LrcLibEntry>(
                $"get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(track)}" + (seconds > 0 ? $"&duration={seconds}" : ""),
                timeout.Token);
            var entry = exact is not null && HasLyrics(exact) ? exact : null;
            if (entry is null)
            {
                var found = await GetAsync<List<LrcLibEntry>>($"search?q={Uri.EscapeDataString($"{artist} {track}")}", timeout.Token) ?? new();
                entry = found.Where(HasLyrics)
                    .OrderBy(e => e.SyncedLyrics is null ? 1 : 0)
                    .ThenBy(e => seconds > 0 && e.Duration is { } d ? Math.Abs(d - seconds) : 0)
                    .FirstOrDefault(e => seconds <= 0 || e.Duration is null || Math.Abs(e.Duration.Value - seconds) <= 8);
            }
            if (entry is null)
                return Result<SongLyrics>.Failure("NO_LYRICS", T("No lyrics were found for this song."));

            var lines = entry.SyncedLyrics is { Length: > 0 } synced ? ParseLrc(synced) : ParsePlain(entry.PlainLyrics ?? string.Empty);
            return lines.Count == 0
                ? Result<SongLyrics>.Failure("NO_LYRICS", T("No lyrics were found for this song."))
                : Result<SongLyrics>.Success(new SongLyrics(entry.ArtistName ?? artist, entry.TrackName ?? track, lines));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogInformation("Lyrics lookup failed: {Message}", ex.Message);
            return Result<SongLyrics>.Failure(AppError.NetworkError(T("The lyrics service could not be reached.")));
        }
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + path);
        // LRCLIB asks apps to say who they are.
        request.Headers.TryAddWithoutValidation("User-Agent", "TimePlayer (https://github.com/ernie96/TimePlayer)");
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    private static bool HasLyrics(LrcLibEntry e) =>
        e.Instrumental != true && (!string.IsNullOrWhiteSpace(e.SyncedLyrics) || !string.IsNullOrWhiteSpace(e.PlainLyrics));

    // ----- Working out the song from a YouTube video -----

    private static readonly Regex Noise = new(
        @"\s*[\(\[][^\)\]]*\b(official|video|audio|lyrics?|lyric video|visuali[sz]er|music video|hd|4k|remaster(ed)?|mv|clip)\b[^\)\]]*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Featuring = new(@"\s*[\(\[]?\s*\b(ft\.?|feat\.?|featuring)\s+[^\)\]\-]+[\)\]]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"Artist - Title (Official Video)" or a "Artist - Topic" channel's plain title, to artist and title.</summary>
    public static (string Artist, string Track) GuessSong(string title, string channel)
    {
        var cleanTitle = Featuring.Replace(Noise.Replace(title, string.Empty), string.Empty).Trim();
        var cleanChannel = Regex.Replace(channel, @"\s*-\s*Topic$|VEVO$|\s*Official$", string.Empty, RegexOptions.IgnoreCase).Trim();
        foreach (var separator in new[] { " - ", " – ", " — ", " | " })
        {
            var at = cleanTitle.IndexOf(separator, StringComparison.Ordinal);
            if (at > 0)
                return (cleanTitle[..at].Trim(), cleanTitle[(at + separator.Length)..].Trim().Trim('"'));
        }
        return (cleanChannel, cleanTitle.Trim('"'));
    }

    // ----- LRC -----

    private static readonly Regex LrcStamp = new(@"\[(?<m>\d{1,3}):(?<s>\d{1,2}(?:[.,]\d{1,3})?)\]", RegexOptions.Compiled);

    public static IReadOnlyList<LyricLine> ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var stamps = LrcStamp.Matches(raw);
            if (stamps.Count == 0)
                continue;
            var text = LrcStamp.Replace(raw, string.Empty).Trim();
            if (text.Length == 0)
                continue; // instrumental breaks
            foreach (Match stamp in stamps)
            {
                var start = TimeSpan.FromMinutes(int.Parse(stamp.Groups["m"].Value, CultureInfo.InvariantCulture))
                    + TimeSpan.FromSeconds(double.Parse(stamp.Groups["s"].Value.Replace(',', '.'), CultureInfo.InvariantCulture));
                lines.Add(new LyricLine(start, text));
            }
        }
        return lines.OrderBy(l => l.Start).ToList();
    }

    private static IReadOnlyList<LyricLine> ParsePlain(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => new LyricLine(null, l)).ToList();

    private sealed record LrcLibEntry
    {
        [JsonPropertyName("trackName")] public string? TrackName { get; init; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; init; }
        [JsonPropertyName("duration")] public double? Duration { get; init; }
        [JsonPropertyName("instrumental")] public bool? Instrumental { get; init; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; init; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; init; }
    }
}
