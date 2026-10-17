using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace TimePlayer.Core.Models;

/// <summary>
/// Strongly-typed 11-character YouTube video identifier.
/// Validates format [a-zA-Z0-9_-]{11} and provides standard URL generators.
/// </summary>
public readonly partial record struct YouTubeVideoId : IEquatable<YouTubeVideoId>
{
    private static readonly Regex ValidIdRegex = CreateRegex();

    [GeneratedRegex("^[a-zA-Z0-9_-]{11}$", RegexOptions.Compiled)]
    private static partial Regex CreateRegex();

    public string Value { get; }

    public YouTubeVideoId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Video ID cannot be null or whitespace.", nameof(value));

        value = value.Trim();
        if (!ValidIdRegex.IsMatch(value))
            throw new ArgumentException($"'{value}' is not a valid 11-character YouTube video ID.", nameof(value));

        Value = value;
    }

    public static bool TryParse([NotNullWhen(true)] string? input, out YouTubeVideoId videoId)
    {
        videoId = default;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();
        if (!ValidIdRegex.IsMatch(trimmed))
            return false;

        videoId = new YouTubeVideoId(trimmed);
        return true;
    }

    public static YouTubeVideoId Parse(string input) => new(input);

    public string ToWatchUrl() => $"https://www.youtube.com/watch?v={Value}";
    public string ToShortUrl() => $"https://youtu.be/{Value}";
    public string ToEmbedUrl() => $"https://www.youtube.com/embed/{Value}";

    public override string ToString() => Value;

    public static implicit operator string(YouTubeVideoId id) => id.Value;
    public static explicit operator YouTubeVideoId(string id) => Parse(id);
}
