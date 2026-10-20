using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Common;
using TimePlayer.Core.Models;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Core.Community;

/// <summary>One line of a video's captions, for reading them as a document.</summary>
public sealed record TranscriptLine(TimeSpan Start, string Text)
{
    /// <summary>The text first, then the time, so moving through the list reads like the video.</summary>
    public string AccessibleLabel => $"{Text}, {Accessibility.AccessibleTextFormatter.FormatTimeSpoken(Start)}";

    public override string ToString() => AccessibleLabel;
}

public interface ITranscriptService
{
    Task<Result<IReadOnlyList<TranscriptLine>>> LoadAsync(CaptionTrack track, CancellationToken cancellationToken = default);
}

/// <summary>
/// Downloads a caption track and turns it into readable lines. YouTube serves captions as WebVTT,
/// json3 or its timedtext XML depending on the source, so all of them are understood.
/// </summary>
public sealed class TranscriptService : ITranscriptService
{
    private readonly HttpClient _http;
    private readonly ILogger<TranscriptService> _logger;

    public TranscriptService(HttpClient http, ILogger<TranscriptService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<TranscriptLine>>> LoadAsync(CaptionTrack track, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var text = await _http.GetStringAsync(track.Url, timeout.Token);
            var lines = Parse(text);
            return lines.Count == 0
                ? Result<IReadOnlyList<TranscriptLine>>.Failure("EMPTY_TRANSCRIPT", T("These captions are empty."))
                : Result<IReadOnlyList<TranscriptLine>>.Success(lines);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogInformation("Could not load captions for the transcript: {Message}", ex.Message);
            return Result<IReadOnlyList<TranscriptLine>>.Failure(AppError.NetworkError(T("The captions could not be loaded.")));
        }
    }

    public static IReadOnlyList<TranscriptLine> Parse(string content)
    {
        var trimmed = content.TrimStart('﻿', ' ', '\r', '\n', '\t');
        IEnumerable<TranscriptLine> lines =
            trimmed.StartsWith("WEBVTT", StringComparison.Ordinal) ? ParseVtt(trimmed)
            : trimmed.StartsWith('{') ? ParseJson3(trimmed)
            : trimmed.StartsWith('<') ? ParseXml(trimmed)
            : Array.Empty<TranscriptLine>();
        return Tidy(lines);
    }

    // Automatic captions repeat each phrase as it rolls up; keep a line once, in time order.
    private static IReadOnlyList<TranscriptLine> Tidy(IEnumerable<TranscriptLine> lines)
    {
        var result = new List<TranscriptLine>();
        foreach (var line in lines.OrderBy(l => l.Start))
        {
            var text = Regex.Replace(WebUtility.HtmlDecode(line.Text), @"\s+", " ").Trim();
            if (text.Length == 0 || (result.Count > 0 && result[^1].Text == text))
                continue;
            result.Add(line with { Text = text });
        }
        return result;
    }

    private static readonly Regex VttTime = new(@"^(?<start>(?:\d+:)?\d{2}:\d{2}[.,]\d{3})\s+-->", RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);

    private static IEnumerable<TranscriptLine> ParseVtt(string vtt)
    {
        var blocks = vtt.Replace("\r\n", "\n").Split("\n\n");
        foreach (var block in blocks)
        {
            var rows = block.Split('\n');
            var timeRow = Array.FindIndex(rows, r => VttTime.IsMatch(r));
            if (timeRow < 0)
                continue;
            var start = ParseClock(VttTime.Match(rows[timeRow]).Groups["start"].Value);
            var rawRows = rows.Skip(timeRow + 1).Where(r => r.Trim().Length > 0).ToList();
            var textRows = rawRows.Select(r => Tags.Replace(r, string.Empty).Trim()).ToList();
            if (textRows.Count == 0)
                continue;
            // Automatic captions roll up: each cue repeats the previous row, and the row with word timings
            // (<00:00:01.234><c>) is the new words. Ordinary captions keep all their rows.
            var rolling = rawRows.Any(r => r.Contains("<c>", StringComparison.Ordinal));
            yield return new TranscriptLine(start, rolling ? textRows[^1] : string.Join(' ', textRows));
        }
    }

    private static IEnumerable<TranscriptLine> ParseJson3(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("events", out var events))
            yield break;
        foreach (var e in events.EnumerateArray())
        {
            if (!e.TryGetProperty("segs", out var segs) || !e.TryGetProperty("tStartMs", out var start))
                continue;
            var text = new StringBuilder();
            foreach (var seg in segs.EnumerateArray())
                if (seg.TryGetProperty("utf8", out var utf8))
                    text.Append(utf8.GetString());
            yield return new TranscriptLine(TimeSpan.FromMilliseconds(start.GetDouble()), text.ToString());
        }
    }

    private static IEnumerable<TranscriptLine> ParseXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        // srv3: <p t="1234" d="2000">, with words in <s> elements; older timedtext: <text start="1.23" dur="2">.
        foreach (var p in doc.Descendants("p"))
        {
            if (double.TryParse((string?)p.Attribute("t"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                yield return new TranscriptLine(TimeSpan.FromMilliseconds(ms), p.Value);
        }
        foreach (var t in doc.Descendants("text"))
        {
            if (double.TryParse((string?)t.Attribute("start"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                yield return new TranscriptLine(TimeSpan.FromSeconds(seconds), t.Value);
        }
    }

    private static TimeSpan ParseClock(string text)
    {
        var parts = text.Replace(',', '.').Split(':');
        var seconds = double.Parse(parts[^1], CultureInfo.InvariantCulture);
        var minutes = int.Parse(parts[^2], CultureInfo.InvariantCulture);
        var hours = parts.Length == 3 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        return new TimeSpan(hours, minutes, 0) + TimeSpan.FromSeconds(seconds);
    }
}
