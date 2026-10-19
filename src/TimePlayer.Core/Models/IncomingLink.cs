namespace TimePlayer.Core.Models;

/// <summary>
/// Links handed to TimePlayer by other apps: on the command line ("TimePlayer.exe https://youtu.be/..."),
/// or as a timeplayer:// link from a browser, the browser extension or a bookmark.
/// Any web page can open a timeplayer:// link, so only YouTube videos and playlists get through,
/// rebuilt from their ids; nothing else from the link is passed on.
/// </summary>
public static class IncomingLink
{
    public const string Scheme = "timeplayer";
    private const int MaxLength = 4096;

    /// <summary>"timeplayer://open?url=..." for a YouTube link.</summary>
    public static string ToProtocolLink(string youTubeLink) =>
        $"{Scheme}://open?url={Uri.EscapeDataString(youTubeLink)}";

    /// <summary>
    /// A clean YouTube link to open (a watch link, with its start time, or a playlist link), or null when
    /// <paramref name="argument"/> isn't a YouTube video or playlist.
    /// </summary>
    public static string? Normalize(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument) || argument.Length > MaxLength)
            return null;
        var text = argument.Trim().Trim('"');

        if (text.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase))
            text = FromProtocolLink(text[(Scheme.Length + 1)..]);

        var site = IsYouTubeMusic(text) ? MusicSite : "https://www.youtube.com";
        if (YouTubeUrl.IsPlaylistLink(text) && YouTubeUrl.TryGetPlaylistId(text, out var listId))
            return $"{site}/playlist?list={listId}";
        if (YouTubeUrl.TryParse(text, out var url))
            return site + url.NormalizedWatchUrl["https://www.youtube.com".Length..];
        return null;
    }

    private const string MusicSite = "https://music.youtube.com";

    /// <summary>A music.youtube.com link: TimePlayer opens it in Music.</summary>
    public static bool IsYouTubeMusic(string? link)
    {
        link = link?.Trim();
        if (link is not null && link.StartsWith("music.youtube.com/", StringComparison.OrdinalIgnoreCase))
            link = "https://" + link;
        return Uri.TryCreate(link, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
            uri.Host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string FromProtocolLink(string rest)
    {
        rest = rest.TrimStart('/');
        // timeplayer://open?url=<escaped link>, the form the extension and bookmark use.
        var query = rest.IndexOf('?');
        if (query >= 0 && rest[..query].TrimEnd('/').Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in rest[(query + 1)..].Split('&'))
            {
                if (part.StartsWith("url=", StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(part[4..].Replace('+', ' '));
            }
            return string.Empty;
        }
        // timeplayer://https://youtu.be/... or timeplayer:dQw4w9WgXcQ, typed by hand. Browsers sometimes
        // drop one slash of the inner "https://", so put it back.
        rest = Uri.UnescapeDataString(rest).TrimEnd('/');
        if (rest.StartsWith("https:/", StringComparison.OrdinalIgnoreCase) && !rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            rest = "https://" + rest["https:/".Length..];
        return rest;
    }
}
