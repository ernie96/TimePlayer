namespace TimePlayer.Core.Common;

/// <summary>
/// Keeps personal details out of logs, which users share when reporting problems. Video stream
/// addresses carry the viewer's IP address and signed tokens in their query string.
/// </summary>
public static class LogRedaction
{
    /// <summary>"https://host/path?ip=…&amp;sig=…" → "https://host/path?…"</summary>
    public static string Url(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return string.Empty;
        var query = url.IndexOf('?');
        return query < 0 ? url : url[..query] + "?…";
    }
}
