using System.Globalization;
using System.Text.RegularExpressions;
using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Core.Accessibility;

/// <summary>
/// Helper formatting utility for screen reader natural language announcements.
/// Avoids robotic shorthand like "03:45" which some screen readers mispronounce as dates or ratios.
/// Everything is said in the user's language, with the right plural forms.
/// </summary>
public static partial class AccessibleTextFormatter
{
    /// <summary>"3 minutes, 12 seconds"; zero parts are left out ("4 minutes", "1 hour, 5 seconds").</summary>
    public static string FormatTimeSpoken(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;

        var parts = new List<string>();
        var hours = (int)time.TotalHours;
        if (hours > 0) parts.Add(Plural(hours, "{0} hour", "{0} hours"));
        if (time.Minutes > 0) parts.Add(Plural(time.Minutes, "{0} minute", "{0} minutes"));
        if (time.Seconds > 0 || parts.Count == 0) parts.Add(Plural(time.Seconds, "{0} second", "{0} seconds"));
        return string.Join(", ", parts);
    }

    /// <summary>"just now", "5 minutes ago", "yesterday", "3 weeks ago", ...</summary>
    public static string FormatAgeSpoken(DateTimeOffset when, DateTimeOffset now)
    {
        var age = now - when;
        if (age < TimeSpan.FromMinutes(1))
            return T("just now");
        if (age < TimeSpan.FromHours(1)) return Ago((int)age.TotalMinutes, "minute");
        if (age < TimeSpan.FromDays(1)) return Ago((int)age.TotalHours, "hour");
        if (age < TimeSpan.FromDays(2)) return T("yesterday");
        if (age < TimeSpan.FromDays(7)) return Ago((int)age.TotalDays, "day");
        if (age < TimeSpan.FromDays(31)) return Ago((int)(age.TotalDays / 7), "week");
        if (age < TimeSpan.FromDays(365)) return Ago((int)(age.TotalDays / 30.4), "month");
        return Ago((int)(age.TotalDays / 365.25), "year");
    }

    /// <summary>"3 weeks ago" with the unit's plural form in the current language.</summary>
    public static string Ago(int count, string unit) => unit switch
    {
        "second" => Plural(count, "{0} second ago", "{0} seconds ago"),
        "minute" => Plural(count, "{0} minute ago", "{0} minutes ago"),
        "hour" => Plural(count, "{0} hour ago", "{0} hours ago"),
        "day" => Plural(count, "{0} day ago", "{0} days ago"),
        "week" => Plural(count, "{0} week ago", "{0} weeks ago"),
        "month" => Plural(count, "{0} month ago", "{0} months ago"),
        _ => Plural(count, "{0} year ago", "{0} years ago"),
    };

    [GeneratedRegex(@"^(?:(Streamed|Premiered)\s+)?(\d+)\s+(second|minute|hour|day|week|month|year)s?\s+ago$", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishAgeRegex();

    /// <summary>
    /// YouTube's English upload ages ("2 years ago", "Streamed 3 days ago") in the user's language.
    /// Anything else is returned unchanged.
    /// </summary>
    public static string? TranslateAge(string? english)
    {
        if (string.IsNullOrWhiteSpace(english) || Current == "en")
            return english;
        var m = EnglishAgeRegex().Match(english.Trim());
        if (!m.Success)
            return english;
        var ago = Ago(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Groups[3].Value.ToLowerInvariant());
        return m.Groups[1].Success
            ? (m.Groups[1].Value.Equals("Streamed", StringComparison.OrdinalIgnoreCase) ? T("streamed {0}", ago) : T("premiered {0}", ago))
            : ago;
    }

    public static string FormatVolumeSpoken(double volumePercent)
    {
        var clamped = Math.Clamp((int)Math.Round(volumePercent), 0, 100);
        return T("Volume {0} percent", clamped);
    }

    public static string FormatSpeedSpoken(double speed)
    {
        return T("Playback speed {0}x", speed.ToString("0.##", Culture));
    }

    public static string FormatSearchResultsSpoken(int count, string query)
    {
        if (count == 0)
            return T("No results for {0}.", query);
        return Plural(count, "{0} result.", "{0} results.");
    }

    /// <summary>"1.2 million views", "3 thousand views", "15 views".</summary>
    public static string FormatViewCount(long count) => count switch
    {
        >= 1_000_000_000 => T("{0} billion views", (count / 1_000_000_000.0).ToString("0.#", Culture)),
        >= 1_000_000 => T("{0} million views", (count / 1_000_000.0).ToString("0.#", Culture)),
        >= 1_000 => T("{0} thousand views", (count / 1_000.0).ToString("0.#", Culture)),
        _ => Plural(count, "{0} view", "{0} views")
    };
}
