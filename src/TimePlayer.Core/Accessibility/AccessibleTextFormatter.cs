namespace TimePlayer.Core.Accessibility;

/// <summary>
/// Helper formatting utility for screen reader natural language announcements.
/// Avoids robotic shorthand like "03:45" which some screen readers mispronounce as dates or ratios.
/// </summary>
public static class AccessibleTextFormatter
{
    /// <summary>"3 minutes, 12 seconds"; zero parts are left out ("4 minutes", "1 hour, 5 seconds").</summary>
    public static string FormatTimeSpoken(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;

        static string Unit(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";

        var parts = new List<string>();
        var hours = (int)time.TotalHours;
        if (hours > 0) parts.Add(Unit(hours, "hour"));
        if (time.Minutes > 0) parts.Add(Unit(time.Minutes, "minute"));
        if (time.Seconds > 0 || parts.Count == 0) parts.Add(Unit(time.Seconds, "second"));
        return string.Join(", ", parts);
    }

    /// <summary>"just now", "5 minutes ago", "yesterday", "3 weeks ago", ...</summary>
    public static string FormatAgeSpoken(DateTimeOffset when, DateTimeOffset now)
    {
        var age = now - when;
        if (age < TimeSpan.FromMinutes(1))
            return "just now";

        static string Unit(int n, string unit) => n == 1 ? $"1 {unit} ago" : $"{n} {unit}s ago";

        if (age < TimeSpan.FromHours(1)) return Unit((int)age.TotalMinutes, "minute");
        if (age < TimeSpan.FromDays(1)) return Unit((int)age.TotalHours, "hour");
        if (age < TimeSpan.FromDays(2)) return "yesterday";
        if (age < TimeSpan.FromDays(7)) return Unit((int)age.TotalDays, "day");
        if (age < TimeSpan.FromDays(31)) return Unit((int)(age.TotalDays / 7), "week");
        if (age < TimeSpan.FromDays(365)) return Unit((int)(age.TotalDays / 30.4), "month");
        return Unit((int)(age.TotalDays / 365.25), "year");
    }

    public static string FormatVolumeSpoken(double volumePercent)
    {
        var clamped = Math.Clamp((int)Math.Round(volumePercent), 0, 100);
        return $"Volume {clamped} percent";
    }

    public static string FormatSpeedSpoken(double speed)
    {
        return $"Playback speed {speed:0.##}x";
    }

    public static string FormatSearchResultsSpoken(int count, string query)
    {
        if (count == 0)
            return $"No results for {query}.";
        return count == 1 ? "1 result." : $"{count} results.";
    }
}
