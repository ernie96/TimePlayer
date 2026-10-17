namespace TimePlayer.Core.Accessibility;

/// <summary>
/// Helper formatting utility for screen reader natural language announcements.
/// Avoids robotic shorthand like "03:45" which some screen readers mispronounce as dates or ratios.
/// </summary>
public static class AccessibleTextFormatter
{
    public static string FormatTimeSpoken(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;

        var hours = (int)time.TotalHours;
        var minutes = time.Minutes;
        var seconds = time.Seconds;

        if (hours > 0)
        {
            var hStr = hours == 1 ? "1 hour" : $"{hours} hours";
            var mStr = minutes == 1 ? "1 minute" : $"{minutes} minutes";
            var sStr = seconds == 1 ? "1 second" : $"{seconds} seconds";
            return $"{hStr}, {mStr}, {sStr}";
        }

        if (minutes > 0)
        {
            var mStr = minutes == 1 ? "1 minute" : $"{minutes} minutes";
            var sStr = seconds == 1 ? "1 second" : $"{seconds} seconds";
            return $"{mStr}, {sStr}";
        }

        return seconds == 1 ? "1 second" : $"{seconds} seconds";
    }

    public static string FormatRelativeSeekSpoken(double deltaSeconds, TimeSpan newPosition)
    {
        var direction = deltaSeconds >= 0 ? "forward" : "backward";
        var absSeconds = Math.Abs((int)deltaSeconds);
        var secStr = absSeconds == 1 ? "1 second" : $"{absSeconds} seconds";
        return $"Seeked {direction} {secStr} to {FormatTimeSpoken(newPosition)}";
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
            return $"No results found for {query}.";

        var itemStr = count == 1 ? "result" : "results";
        return $"Found {count} {itemStr} for {query}. Use arrow keys to navigate.";
    }
}
