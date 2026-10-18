using System.Globalization;
using System.Xml.Linq;
using TimePlayer.Core.Models;

namespace TimePlayer.Core.Backends.TubeRepair;

/// <summary>
/// Robust XML Atom feed parser for legacy YouTube GData v2 feeds provided by TubeRepair-CSharp.
/// Supports search feeds, related videos, standard/trending feeds, and playlist feeds.
/// Uses XML local-name matching to be completely agnostic to server XML namespace variations
/// (e.g. tuberepair.uptimetrackers.com vs gdata.youtube.com).
/// </summary>
public static class TubeRepairFeedParser
{
    public static string SanitizeXml(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent)) return xmlContent;

        // 1. Escape unescaped ampersands (& not part of an existing valid XML entity)
        var sanitized = System.Text.RegularExpressions.Regex.Replace(
            xmlContent,
            @"&(?!(amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)",
            "&amp;");

        // 2. Remove duplicate attributes on XML elements, e.g. type='...' type='...' or type='...' type="..."
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            @"(type=(['""]).*?\2)\s+type=(['""]).*?\3",
            "$1",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 3. Ensure </feed> is closed if <feed is opened but not closed
        if (sanitized.Contains("<feed", StringComparison.OrdinalIgnoreCase) &&
            !sanitized.Contains("</feed>", StringComparison.OrdinalIgnoreCase))
        {
            sanitized = sanitized.TrimEnd() + "\n</feed>";
        }

        return sanitized;
    }

    public static List<SearchResult> ParseVideoFeed(string xmlContent)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrWhiteSpace(xmlContent))
            return results;

        try
        {
            var sanitized = SanitizeXml(xmlContent);
            var doc = XDocument.Parse(sanitized);
            var root = doc.Root;
            if (root != null)
            {
                var entries = root.ElementsLocal("entry");
                foreach (var entry in entries)
                {
                    var item = ParseVideoEntry(entry);
                    if (item != null && !string.IsNullOrWhiteSpace(item.Id))
                    {
                        results.Add(item);
                    }
                }
            }
        }
        catch
        {
            // Fallback: extract individual <entry> elements using Regex if root document fails
            try
            {
                var entryMatches = System.Text.RegularExpressions.Regex.Matches(
                    xmlContent,
                    @"<entry\b.*?</entry>",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                foreach (System.Text.RegularExpressions.Match match in entryMatches)
                {
                    var cleanEntry = SanitizeXml(match.Value);
                    var wrapped = $"<entry xmlns='http://www.w3.org/2005/Atom' xmlns:yt='http://tuberepair.uptimetrackers.com/schemas/2007' xmlns:media='http://search.yahoo.com/mrss/' xmlns:gd='http://schemas.google.com/g/2005' {cleanEntry[6..]}";
                    try
                    {
                        var entryElem = XElement.Parse(wrapped);
                        var item = ParseVideoEntry(entryElem);
                        if (item != null && !string.IsNullOrWhiteSpace(item.Id))
                        {
                            results.Add(item);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        return results;
    }

    public static SearchResult? ParseVideoEntry(XElement entry)
    {
        try
        {
            var mediaGroup = entry.ElementLocal("group");

            // Video ID: <yt:videoid> or inside <media:group><yt:videoid> or from <id>
            string? videoId = entry.ElementLocal("videoid")?.Value?.Trim()
                ?? mediaGroup.ElementLocal("videoid")?.Value?.Trim();

            if (string.IsNullOrWhiteSpace(videoId))
            {
                var idVal = entry.ElementLocal("id")?.Value;
                if (!string.IsNullOrEmpty(idVal))
                {
                    var lastColon = idVal.LastIndexOf(':');
                    if (lastColon >= 0 && lastColon < idVal.Length - 1)
                    {
                        videoId = idVal[(lastColon + 1)..].Trim();
                    }
                }
            }

            // Fallback: check /getvideo/{id} URLs in media:content
            if (string.IsNullOrWhiteSpace(videoId) && mediaGroup != null)
            {
                foreach (var content in mediaGroup.ElementsLocal("content"))
                {
                    var url = content.AttributeLocal("url");
                    if (!string.IsNullOrEmpty(url) && url.Contains("/getvideo/"))
                    {
                        var lastSlash = url.LastIndexOf('/');
                        if (lastSlash >= 0 && lastSlash < url.Length - 1)
                        {
                            var candidateId = url[(lastSlash + 1)..].Trim();
                            if (candidateId.Length == 11)
                            {
                                videoId = candidateId;
                                break;
                            }
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(videoId))
                return null;

            // Title
            string title = mediaGroup.ElementLocal("title")?.Value?.Trim()
                ?? entry.ElementLocal("title")?.Value?.Trim()
                ?? "Untitled Video";

            // Channel / Author
            var authorElem = entry.ElementLocal("author");
            string channelTitle = authorElem.ElementLocal("name")?.Value?.Trim()
                ?? mediaGroup.ElementLocal("credit")?.Value?.Trim()
                ?? "Unknown Channel";

            string? channelId = authorElem.ElementLocal("userId")?.Value?.Trim()
                ?? entry.ElementLocal("channelId")?.Value?.Trim()
                ?? mediaGroup.ElementLocal("uploaderId")?.Value?.Trim()
                ?? mediaGroup.ElementLocal("credit")?.Value?.Trim();

            // Duration
            TimeSpan? duration = null;
            var durationElem = mediaGroup.ElementLocal("duration")
                ?? entry.ElementLocal("duration");
            if (durationElem != null && int.TryParse(durationElem.AttributeLocal("seconds"), out var sec) && sec > 0)
            {
                duration = TimeSpan.FromSeconds(sec);
            }

            // View count
            long? viewCount = null;
            var statsElem = entry.ElementLocal("statistics")
                ?? mediaGroup.ElementLocal("statistics");
            if (statsElem != null && long.TryParse(statsElem.AttributeLocal("viewCount"), out var vc))
            {
                viewCount = vc;
            }

            // Upload date / published text
            string? uploadDateText = null;
            var publishedElem = entry.ElementLocal("published")
                ?? mediaGroup.ElementLocal("uploaded");
            if (publishedElem != null && DateTimeOffset.TryParse(publishedElem.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var pubDate))
            {
                uploadDateText = pubDate.ToString("yyyy-MM-dd");
            }

            // Thumbnail
            string? thumbnailUrl = null;
            if (mediaGroup != null)
            {
                var thumbs = mediaGroup.ElementsLocal("thumbnail").ToList();
                thumbnailUrl = thumbs.FirstOrDefault(t => t.AttributeLocal("name") == "hqdefault")?.AttributeLocal("url")
                    ?? thumbs.FirstOrDefault(t => t.AttributeLocal("name") == "mqdefault")?.AttributeLocal("url")
                    ?? thumbs.FirstOrDefault()?.AttributeLocal("url");
            }

            if (string.IsNullOrWhiteSpace(thumbnailUrl))
            {
                thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
            }

            // Description
            string? description = mediaGroup.ElementLocal("description")?.Value?.Trim()
                ?? entry.ElementLocal("content")?.Value?.Trim();

            return new SearchResult
            {
                Id = videoId,
                Title = title,
                ChannelTitle = channelTitle,
                ChannelId = channelId,
                Duration = duration,
                ViewCount = viewCount,
                UploadDateText = uploadDateText,
                ThumbnailUrl = thumbnailUrl,
                DescriptionSnippet = description
            };
        }
        catch
        {
            return null;
        }
    }

    private static XElement? ElementLocal(this XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> ElementsLocal(this XElement? parent, string localName) =>
        parent?.Elements().Where(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase)) ?? Enumerable.Empty<XElement>();

    private static string? AttributeLocal(this XElement? element, string localName) =>
        element?.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;
}
