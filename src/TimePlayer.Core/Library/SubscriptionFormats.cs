using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TimePlayer.Core.Library;

public sealed record SubscriptionImportResult(
    string FormatName,
    IReadOnlyList<ChannelSubscription> Channels,
    int SkippedEntries);

/// <summary>
/// Reads and writes subscription lists in the formats other YouTube clients use:
/// NewPipe / LibreTube / Tubular JSON, Google Takeout CSV, OPML (RSS readers, FreeTube export)
/// and FreeTube's profiles.db.
/// </summary>
public static partial class SubscriptionFormats
{
    /// <summary>NewPipe's export header; NewPipe ignores the version on import.</summary>
    private const string NewPipeAppVersion = "0.27.6";
    private const int NewPipeAppVersionInt = 999;
    private const int YouTubeServiceId = 0;

    [GeneratedRegex(@"(UC[0-9A-Za-z_-]{22})")]
    private static partial Regex ChannelIdRegex();

    public static string? ExtractChannelId(string? urlOrId)
    {
        if (string.IsNullOrWhiteSpace(urlOrId))
            return null;
        var match = ChannelIdRegex().Match(urlOrId);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static SubscriptionImportResult Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var text = content.TrimStart('﻿', ' ', '\t', '\r', '\n');

        if (text.StartsWith('<'))
            return ParseOpml(text);
        if (text.StartsWith('{') || text.StartsWith('['))
        {
            // FreeTube profiles.db is one JSON object per line.
            return text.Contains("\"subscriptions\"") && text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length > 1 && !LooksLikeSingleJsonDocument(text)
                ? ParseFreeTubeDb(text)
                : ParseJson(text);
        }
        return ParseTakeoutCsv(text);
    }

    /// <summary>
    /// A Google Takeout (or other) zip: every CSV, JSON and OPML file inside is tried and the one
    /// with the most channels wins. Takeout translates its folder and file names into the account's
    /// language, so the subscriptions file cannot be found by name alone.
    /// </summary>
    public static SubscriptionImportResult ParseZip(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        SubscriptionImportResult? best = null;
        foreach (var entry in zip.Entries)
        {
            var ext = Path.GetExtension(entry.FullName).ToLowerInvariant();
            if (ext is not (".csv" or ".json" or ".opml" or ".xml") || entry.Length == 0 || entry.Length > 20_000_000)
                continue;
            try
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                var parsed = Parse(reader.ReadToEnd());
                if (best is null || parsed.Channels.Count > best.Channels.Count)
                    best = parsed with { FormatName = parsed.FormatName == "Google Takeout CSV" ? "Google Takeout" : parsed.FormatName };
            }
            catch (Exception ex) when (ex is FormatException or JsonException or System.Xml.XmlException or InvalidDataException)
            {
                // Not a subscriptions file; Takeout zips hold many other files.
            }
        }
        return best is { Channels.Count: > 0 }
            ? best
            : throw new FormatException("No YouTube subscriptions were found in this zip. In Google Takeout, make sure \"subscriptions\" is selected under YouTube and YouTube Music.");
    }

    private static bool LooksLikeSingleJsonDocument(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static SubscriptionImportResult ParseJson(string text)
    {
        var root = JsonNode.Parse(text);
        // NewPipe: { "subscriptions": [ { "service_id": 0, "url": ..., "name": ... } ] }
        // LibreTube "Piped" export: { "format": "Piped", "subscriptions": [ { "url", "name" } ] }
        // FreeTube single profile / Invidious: { "subscriptions": [ { "id", "name" } ] } or [ "UC..." ]
        var array = root switch
        {
            JsonArray a => a,
            JsonObject o when o["subscriptions"] is JsonArray a => a,
            _ => throw new FormatException("This JSON file has no \"subscriptions\" list.")
        };

        var channels = new List<ChannelSubscription>();
        int skipped = 0;
        foreach (var node in array)
        {
            string? id;
            string? name = null;
            if (node is JsonValue value && value.TryGetValue(out string? raw))
            {
                id = ExtractChannelId(raw);
            }
            else if (node is JsonObject obj)
            {
                // NewPipe also exports SoundCloud, PeerTube, ... subscriptions; only YouTube (0) applies.
                if (obj["service_id"] is JsonValue sid && sid.TryGetValue(out int serviceId) && serviceId != YouTubeServiceId)
                {
                    skipped++;
                    continue;
                }
                id = ExtractChannelId(Str(obj, "url")) ?? ExtractChannelId(Str(obj, "id")) ?? ExtractChannelId(Str(obj, "channel_id"));
                name = Str(obj, "name") ?? Str(obj, "title") ?? Str(obj, "author");
            }
            else
            {
                id = null;
            }

            if (id is null)
            {
                skipped++;
                continue;
            }
            channels.Add(new ChannelSubscription(id, string.IsNullOrWhiteSpace(name) ? id : name.Trim()));
        }

        var format = root is JsonObject ro && Str(ro, "app_version") is not null ? "NewPipe" : "JSON";
        return new SubscriptionImportResult(format, Dedupe(channels), skipped);
    }

    private static string? Str(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static SubscriptionImportResult ParseFreeTubeDb(string text)
    {
        var channels = new List<ChannelSubscription>();
        int skipped = 0;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (JsonException) { skipped++; continue; }

            if (node is JsonObject obj && obj["subscriptions"] is JsonArray subs)
            {
                var parsed = ParseJson(new JsonObject { ["subscriptions"] = subs.DeepClone() }.ToJsonString());
                channels.AddRange(parsed.Channels);
                skipped += parsed.SkippedEntries;
            }
        }
        return new SubscriptionImportResult("FreeTube", Dedupe(channels), skipped);
    }

    private static SubscriptionImportResult ParseOpml(string text)
    {
        var doc = XDocument.Parse(text);
        var channels = new List<ChannelSubscription>();
        int skipped = 0;
        foreach (var outline in doc.Descendants("outline"))
        {
            var url = (string?)outline.Attribute("xmlUrl") ?? (string?)outline.Attribute("htmlUrl");
            if (url is null)
                continue; // folder node
            var id = ExtractChannelId(url);
            if (id is null)
            {
                skipped++;
                continue;
            }
            var name = (string?)outline.Attribute("title") ?? (string?)outline.Attribute("text") ?? id;
            channels.Add(new ChannelSubscription(id, name.Trim()));
        }
        return new SubscriptionImportResult("OPML", Dedupe(channels), skipped);
    }

    private static SubscriptionImportResult ParseTakeoutCsv(string text)
    {
        // Google Takeout: "Channel Id,Channel Url,Channel Title" (header is localised; the id column is not).
        var channels = new List<ChannelSubscription>();
        int skipped = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            var fields = SplitCsvLine(line);
            var id = fields.Select(ExtractChannelId).FirstOrDefault(x => x is not null);
            if (id is null)
            {
                if (channels.Count > 0) skipped++; // the header row is not an error
                continue;
            }
            var name = fields.LastOrDefault(f => !string.IsNullOrWhiteSpace(f) && ExtractChannelId(f) is null && !f.StartsWith("http", StringComparison.OrdinalIgnoreCase));
            channels.Add(new ChannelSubscription(id, name?.Trim() ?? id));
        }
        if (channels.Count == 0)
            throw new FormatException("No YouTube channels were found. Supported files: NewPipe or LibreTube JSON, Google Takeout CSV, OPML and FreeTube profiles.");
        return new SubscriptionImportResult("Google Takeout CSV", Dedupe(channels), skipped);
    }

    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }

    private static List<ChannelSubscription> Dedupe(List<ChannelSubscription> channels) =>
        channels.GroupBy(c => c.ChannelId, StringComparer.Ordinal).Select(g => g.First()).ToList();

    /// <summary>NewPipe-compatible export, importable by NewPipe, Tubular, LibreTube and TimePlayer.</summary>
    public static string ToNewPipeJson(IEnumerable<ChannelSubscription> channels)
    {
        var subs = new JsonArray();
        foreach (var c in channels)
        {
            subs.Add(new JsonObject
            {
                ["service_id"] = YouTubeServiceId,
                ["url"] = c.Url,
                ["name"] = c.Name
            });
        }
        var root = new JsonObject
        {
            ["app_version"] = NewPipeAppVersion,
            ["app_version_int"] = NewPipeAppVersionInt,
            ["subscriptions"] = subs
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string ToOpml(IEnumerable<ChannelSubscription> channels)
    {
        var body = new XElement("body",
            new XElement("outline",
                new XAttribute("text", "YouTube Subscriptions"),
                new XAttribute("title", "YouTube Subscriptions"),
                channels.Select(c => new XElement("outline",
                    new XAttribute("text", c.Name),
                    new XAttribute("title", c.Name),
                    new XAttribute("type", "rss"),
                    new XAttribute("xmlUrl", $"https://www.youtube.com/feeds/videos.xml?channel_id={c.ChannelId}"),
                    new XAttribute("htmlUrl", c.Url)))));
        var doc = new XDocument(new XElement("opml", new XAttribute("version", "1.1"),
            new XElement("head", new XElement("title", "TimePlayer subscriptions")), body));
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine + doc;
    }
}
