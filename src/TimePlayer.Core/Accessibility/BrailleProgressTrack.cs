namespace TimePlayer.Core.Accessibility;

/// <summary>
/// Mathematical model and string formatter for refreshable Braille displays acting as a track progress indicator.
///
/// Behavior:
/// - Display goes blank except for a "lower g" indicator (dots 2-3-5-6, Unicode \u2836).
/// - As the track progresses, the indicator moves evenly along the display.
///   (e.g., halfway through the track, indicator is on cell 16 of a 32-cell display and cell 9 of an 18-cell display).
/// - When paused, the lower g blinks slowly at the stopped cell.
/// - When stopped or ended, the display is blank.
/// - Touching/pressing cursor routing buttons maps directly to seeking to that position along the track.
/// </summary>
public sealed class BrailleProgressTrack
{
    /// <summary>
    /// Unicode Braille pattern for lower g: dots 2-3-5-6 (⠶, U+2836).
    /// </summary>
    public const char LowerG = '\u2836';

    /// <summary>
    /// Unicode Braille blank pattern: empty cell (⠀, U+2800).
    /// </summary>
    public const char BrailleBlank = '\u2800';

    /// <summary>
    /// Standard ASCII space for alternative screen reader compatibility.
    /// </summary>
    public const char SpaceBlank = ' ';

    public int CellCount { get; }
    public bool UseBrailleBlank { get; }

    public BrailleProgressTrack(int cellCount = 40, bool useBrailleBlank = true)
    {
        if (cellCount < 2)
            throw new ArgumentOutOfRangeException(nameof(cellCount), "Cell count must be at least 2.");

        CellCount = cellCount;
        UseBrailleBlank = useBrailleBlank;
    }

    /// <summary>
    /// Calculates the 0-indexed cell position for a given playback position and total duration.
    /// Halfway through on a 32-cell display: (0.5 * 31) = 15.5 -> round = 16.
    /// Halfway through on an 18-cell display: (0.5 * 17) = 8.5 -> round = 9.
    /// </summary>
    public int CalculateCellIndex(TimeSpan position, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || position <= TimeSpan.Zero)
            return 0;

        if (position >= duration)
            return CellCount - 1;

        double fraction = position.TotalSeconds / duration.TotalSeconds;
        int index = (int)Math.Round(fraction * (CellCount - 1), MidpointRounding.AwayFromZero);
        return Math.Clamp(index, 0, CellCount - 1);
    }

    /// <summary>
    /// Calculates the seek target time when a touch cursor button on cellIndex (0 to CellCount - 1) is pressed.
    /// </summary>
    public TimeSpan CalculateSeekTime(int cellIndex, TimeSpan duration) =>
        CalculateSeekTime(cellIndex, CellCount, duration);

    /// <summary>
    /// Seek target for a routing key on a display of <paramref name="cellCount"/> cells, which may differ
    /// from the configured width (the NVDA add-on reports the real hardware width).
    /// </summary>
    public static TimeSpan CalculateSeekTime(int cellIndex, int cellCount, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || cellCount < 2)
            return TimeSpan.Zero;

        int clamped = Math.Clamp(cellIndex, 0, cellCount - 1);
        double fraction = (double)clamped / (cellCount - 1);
        double targetSeconds = Math.Round(fraction * duration.TotalSeconds, 3);
        return TimeSpan.FromSeconds(targetSeconds);
    }

    /// <summary>
    /// Generates the Braille string for the current playback state and position.
    /// </summary>
    /// <param name="position">Current playback position.</param>
    /// <param name="duration">Total video duration.</param>
    /// <param name="isPlaying">True if playing; false if paused or stopped.</param>
    /// <param name="isPaused">True if paused (triggering slow blinking).</param>
    /// <param name="blinkVisible">Blink phase when paused (true = lower g visible, false = blank).</param>
    public string GenerateDisplayLine(TimeSpan position, TimeSpan duration, bool isPlaying, bool isPaused, bool blinkVisible = true)
    {
        char blankChar = UseBrailleBlank ? BrailleBlank : SpaceBlank;

        // When stopped or duration is invalid, display is completely blank
        if (!isPlaying && !isPaused)
        {
            return new string(blankChar, CellCount);
        }

        // When paused and in the "off" phase of blinking, display is temporarily blank
        if (isPaused && !blinkVisible)
        {
            return new string(blankChar, CellCount);
        }

        int activeIndex = CalculateCellIndex(position, duration);
        var cells = new char[CellCount];
        Array.Fill(cells, blankChar);
        cells[activeIndex] = LowerG;

        return new string(cells);
    }
}
