using System.Globalization;
using System.Text.RegularExpressions;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Core.Models;

/// <summary>A named part of a video, from the timestamps in its description.</summary>
public sealed record Chapter(int Number, TimeSpan Start, string Title)
{
    /// <summary>"3, 4:10, Setting up" for lists.</summary>
    public string AccessibleLabel => T("{0}. {1}, {2}", Number, Title, Accessibility.AccessibleTextFormatter.FormatTimeSpoken(Start));

    public override string ToString() => AccessibleLabel;
}

/// <summary>
/// Reads chapters the way YouTube does: timestamps in the description ("0:00 Intro", "1:02:03 - Part two",
/// "Intro (0:00)"), starting at 0:00, at least three, in order. Works for any stream source.
/// </summary>
public static class Chapters
{
    private const string Stamp = @"(?<time>(?:\d{1,2}:)?\d{1,2}:\d{2})";
    private static readonly Regex TimeFirst = new(@"^\s*[\[(]?" + Stamp + @"[\])]?\s*(?:[-–—:|.•]\s*)?(?<title>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex TitleFirst = new(@"^\s*(?<title>.+?)\s*(?:[-–—:|]\s*)?[\[(]?" + Stamp + @"[\])]?\s*$", RegexOptions.Compiled);

    public static IReadOnlyList<Chapter> FromDescription(string? description, TimeSpan duration = default)
    {
        if (string.IsNullOrWhiteSpace(description))
            return Array.Empty<Chapter>();

        var found = new List<(TimeSpan Start, string Title)>();
        foreach (var rawLine in description.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.Length > 200)
                continue;
            var match = TimeFirst.Match(line);
            if (!match.Success || !HasLetter(match.Groups["title"].Value))
                match = TitleFirst.Match(line);
            if (!match.Success || !TryParseTime(match.Groups["time"].Value, out var start))
                continue;
            var title = match.Groups["title"].Value.Trim(' ', '-', '–', '—', ':', '|', '•');
            if (!HasLetter(title))
                continue;
            if (found.Count > 0 && start <= found[^1].Start)
            {
                // A second list (for example "Songs: 0:00 ..." after the chapters) starts over; keep the first.
                if (start == TimeSpan.Zero) break;
                continue;
            }
            if (duration > TimeSpan.Zero && start >= duration)
                break;
            found.Add((start, title));
        }

        if (found.Count < 3 || found[0].Start != TimeSpan.Zero)
            return Array.Empty<Chapter>();
        return found.Select((c, i) => new Chapter(i + 1, c.Start, c.Title)).ToList();
    }

    /// <summary>The chapter playing at <paramref name="position"/>.</summary>
    public static Chapter? At(IReadOnlyList<Chapter> chapters, TimeSpan position) =>
        chapters.LastOrDefault(c => c.Start <= position + TimeSpan.FromMilliseconds(250));

    /// <summary>Where "previous chapter" goes: the start of this one, or the one before if this just began.</summary>
    public static Chapter? Previous(IReadOnlyList<Chapter> chapters, TimeSpan position)
    {
        var current = At(chapters, position);
        if (current is null)
            return null;
        if (position - current.Start > TimeSpan.FromSeconds(3))
            return current;
        var index = current.Number - 2;
        return index >= 0 ? chapters[index] : current;
    }

    public static Chapter? Next(IReadOnlyList<Chapter> chapters, TimeSpan position) =>
        chapters.FirstOrDefault(c => c.Start > position + TimeSpan.FromMilliseconds(250));

    private static bool HasLetter(string text) => text.Any(char.IsLetter);

    private static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        var parts = text.Split(':');
        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        if (numbers[^1] >= 60 || (parts.Length == 3 && numbers[1] >= 60))
            return false;
        time = parts.Length == 3
            ? new TimeSpan(numbers[0], numbers[1], numbers[2])
            : new TimeSpan(0, numbers[0], numbers[1]);
        return true;
    }
}
