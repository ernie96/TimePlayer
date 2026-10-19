using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Library;

/// <summary>A playlist found in a Takeout export: its name and video ids (Takeout has no titles).</summary>
public sealed record TakeoutPlaylist(string Name, IReadOnlyList<string> VideoIds);

public sealed record TakeoutContents(
    SubscriptionImportResult? Subscriptions,
    IReadOnlyList<TakeoutPlaylist> Playlists,
    IReadOnlyList<string> LikedVideoIds,
    IReadOnlyList<string> WatchLaterIds,
    IReadOnlyList<HistoryEntry> History)
{
    public bool IsEmpty =>
        (Subscriptions?.Channels.Count ?? 0) == 0 && Playlists.Count == 0 &&
        LikedVideoIds.Count == 0 && WatchLaterIds.Count == 0 && History.Count == 0;
}

/// <summary>
/// Everything account-like in a Google Takeout "YouTube and YouTube Music" zip:
/// subscriptions, playlists (playlists/*-videos.csv), Liked videos, Watch later and
/// watch history (history/watch-history.json, only when JSON was chosen in Takeout).
/// </summary>
public static partial class TakeoutImport
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex(@"[?&]v=([A-Za-z0-9_-]{11})")]
    private static partial Regex WatchUrlRegex();

    public static TakeoutContents ParseZip(Stream zipStream)
    {
        // Takeout zips can hold gigabytes of uploaded videos; read them in place, never into memory.
        if (!zipStream.CanSeek)
        {
            var temp = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            zipStream.CopyTo(temp);
            temp.Position = 0;
            using (temp)
                return ParseZip(temp);
        }

        var start = zipStream.Position;
        SubscriptionImportResult? subscriptions = null;
        try
        {
            subscriptions = SubscriptionFormats.ParseZip(zipStream);
        }
        catch (FormatException)
        {
            // No subscriptions in this zip; playlists or history may still be there.
        }

        zipStream.Position = start;
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var playlists = new List<TakeoutPlaylist>();
        var liked = new List<string>();
        var watchLater = new List<string>();
        var history = new List<HistoryEntry>();

        foreach (var entry in zip.Entries)
        {
            if (entry.Length == 0 || entry.Length > 100_000_000)
                continue;
            var ext = Path.GetExtension(entry.FullName).ToLowerInvariant();
            try
            {
                if (ext == ".csv")
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    var ids = ParsePlaylistCsv(reader.ReadToEnd());
                    if (ids.Count == 0)
                        continue;
                    var name = PlaylistNameFromFile(entry.Name);
                    if (name.Equals("Liked videos", StringComparison.OrdinalIgnoreCase) || name.Equals("Liked music", StringComparison.OrdinalIgnoreCase))
                        liked.AddRange(ids);
                    else if (name.Equals("Watch later", StringComparison.OrdinalIgnoreCase))
                        watchLater.AddRange(ids);
                    else
                        playlists.Add(new TakeoutPlaylist(name, ids));
                }
                else if (ext == ".json" && entry.Length > 2)
                {
                    using var stream = entry.Open();
                    var entries = ParseWatchHistoryJson(stream);
                    if (entries.Count > history.Count)
                        history = entries;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or FormatException)
            {
                // Takeout zips hold many other files; skip what isn't ours.
            }
        }

        return new TakeoutContents(
            subscriptions,
            playlists,
            liked.Distinct().ToList(),
            watchLater.Distinct().ToList(),
            history);
    }

    /// <summary>"Road trip-videos.csv" → "Road trip". Older exports use "Road trip.csv".</summary>
    public static string PlaylistNameFromFile(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (name.EndsWith("-videos", StringComparison.OrdinalIgnoreCase))
            name = name[..^"-videos".Length];
        return string.IsNullOrWhiteSpace(name) ? "Imported playlist" : name.Trim();
    }

    /// <summary>
    /// Video ids from a Takeout playlist CSV. The header is "Video ID,…" (newer exports) or a block of
    /// playlist details followed by a blank line and "Video Id,Time Added" (older ones). Files whose
    /// first column isn't video ids (subscriptions, playlists.csv, comments) give an empty list.
    /// </summary>
    public static IReadOnlyList<string> ParsePlaylistCsv(string content)
    {
        var lines = content.TrimStart('﻿').Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var header = lines.FindIndex(l => l.StartsWith("Video ID", StringComparison.OrdinalIgnoreCase) || l.StartsWith("Video Id", StringComparison.OrdinalIgnoreCase));
        if (header < 0)
            return Array.Empty<string>();
        var ids = new List<string>();
        foreach (var line in lines.Skip(header + 1))
        {
            var first = line.Split(',')[0].Trim().Trim('"');
            if (VideoIdRegex().IsMatch(first))
                ids.Add(first);
        }
        return ids;
    }

    /// <summary>
    /// history/watch-history.json: [{ "title": "Watched …", "titleUrl": "…watch?v=…", "subtitles": [{ "name", "url" }], "time" }].
    /// Newest first in the file; repeated views become a watch count.
    /// </summary>
    public static List<HistoryEntry> ParseWatchHistoryJson(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return new();

        var byId = new Dictionary<string, HistoryEntry>();
        var order = new List<string>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("titleUrl", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                continue;
            var match = WatchUrlRegex().Match(urlEl.GetString() ?? string.Empty);
            if (!match.Success)
                continue;
            var id = match.Groups[1].Value;

            if (byId.TryGetValue(id, out var existing))
            {
                byId[id] = existing with { WatchCount = existing.WatchCount + 1 };
                continue;
            }

            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? id : id;
            if (title.StartsWith("Watched ", StringComparison.Ordinal))
                title = title["Watched ".Length..];
            string channel = string.Empty;
            string? channelId = null;
            if (item.TryGetProperty("subtitles", out var subs) && subs.ValueKind == JsonValueKind.Array && subs.GetArrayLength() > 0)
            {
                var sub = subs[0];
                channel = sub.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                channelId = sub.TryGetProperty("url", out var u) ? SubscriptionFormats.ExtractChannelId(u.GetString()) : null;
            }
            var when = item.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.String &&
                       DateTimeOffset.TryParse(time.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.ToUniversalTime()
                : DateTimeOffset.MinValue;

            byId[id] = new HistoryEntry
            {
                VideoId = id,
                Title = title,
                ChannelTitle = channel,
                ChannelId = channelId,
                LastWatchedUtc = when
            };
            order.Add(id);
        }
        return order.Select(id => byId[id]).ToList();
    }

    /// <summary>Takeout gives only ids; this fills in titles and channels for display.</summary>
    public static SearchResult PlaceholderVideo(string videoId) => new()
    {
        Id = videoId,
        Title = $"Video {videoId}",
        ChannelTitle = string.Empty
    };
}
