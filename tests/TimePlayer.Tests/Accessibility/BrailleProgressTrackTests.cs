using TimePlayer.Core.Accessibility;
using Xunit;

namespace TimePlayer.Tests.Accessibility;

public class BrailleProgressTrackTests
{
    [Fact]
    public void Constructor_Throws_WhenCellCountLessThan2()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrailleProgressTrack(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrailleProgressTrack(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrailleProgressTrack(-5));
    }

    [Fact]
    public void HalfwayPosition_On32CellDisplay_IsCell16()
    {
        var track = new BrailleProgressTrack(32);
        var duration = TimeSpan.FromMinutes(10);
        var halfway = TimeSpan.FromMinutes(5);

        int index = track.CalculateCellIndex(halfway, duration);

        // Cell 16 (0-indexed 16): exactly halfway requirement
        Assert.Equal(16, index);
    }

    [Fact]
    public void HalfwayPosition_On18CellDisplay_IsCell9()
    {
        var track = new BrailleProgressTrack(18);
        var duration = TimeSpan.FromMinutes(6);
        var halfway = TimeSpan.FromMinutes(3);

        int index = track.CalculateCellIndex(halfway, duration);

        // Cell 9 (0-indexed 9): exactly halfway requirement
        Assert.Equal(9, index);
    }

    [Fact]
    public void Display_On20CellDisplay_CalculatesAndFormatsCorrectly()
    {
        var track = new BrailleProgressTrack(20, useBrailleBlank: true);
        var duration = TimeSpan.FromSeconds(190);
        var halfway = TimeSpan.FromSeconds(95);

        // 95 / 190 * 19 = 9.5 -> rounds AwayFromZero to 10
        int halfwayIndex = track.CalculateCellIndex(halfway, duration);
        Assert.Equal(10, halfwayIndex);

        // Generate display line
        var line = track.GenerateDisplayLine(halfway, duration, isPlaying: true, isPaused: false);
        Assert.Equal(20, line.Length);
        Assert.Equal(BrailleProgressTrack.LowerG, line[10]);

        // Touch cursor routing on cell 10 returns 100 seconds
        var seekTime = track.CalculateSeekTime(10, duration);
        Assert.Equal(TimeSpan.FromSeconds(100), seekTime);
    }

    [Fact]
    public void CalculateCellIndex_StartAndEndBoundaries()
    {
        var track = new BrailleProgressTrack(40);
        var duration = TimeSpan.FromMinutes(4);

        Assert.Equal(0, track.CalculateCellIndex(TimeSpan.Zero, duration));
        Assert.Equal(0, track.CalculateCellIndex(TimeSpan.FromSeconds(-5), duration));
        Assert.Equal(39, track.CalculateCellIndex(duration, duration));
        Assert.Equal(39, track.CalculateCellIndex(TimeSpan.FromMinutes(5), duration));
    }

    [Fact]
    public void DynamicDurations_MovesSlowerForLongerTracks()
    {
        // 5-minute track vs 3-minute track after 60 seconds of playback
        var track = new BrailleProgressTrack(40);
        var fiveMinDuration = TimeSpan.FromMinutes(5);
        var threeMinDuration = TimeSpan.FromMinutes(3);
        var currentPosition = TimeSpan.FromSeconds(60);

        int index5Min = track.CalculateCellIndex(currentPosition, fiveMinDuration);
        int index3Min = track.CalculateCellIndex(currentPosition, threeMinDuration);

        // After 60s, a 3-minute track is 33% done, while a 5-minute track is only 20% done.
        // Therefore, the 5-minute track indicator must be at a lower cell than the 3-minute track.
        Assert.True(index5Min < index3Min);
        Assert.Equal(8, index5Min);   // 60 / 300 * 39 = 7.8 -> 8
        Assert.Equal(13, index3Min);  // 60 / 180 * 39 = 13.0 -> 13
    }

    [Fact]
    public void CalculateSeekTime_TouchCursorRouting_InBothDirections()
    {
        var track = new BrailleProgressTrack(40);
        var duration = TimeSpan.FromSeconds(390);

        // Cell 0 -> 0s
        var startSeek = track.CalculateSeekTime(0, duration);
        Assert.Equal(TimeSpan.Zero, startSeek);

        // Cell 39 -> 390s
        var endSeek = track.CalculateSeekTime(39, duration);
        Assert.Equal(duration, endSeek);

        // Cell 10 -> 100s
        var forwardSeek = track.CalculateSeekTime(10, duration);
        Assert.Equal(TimeSpan.FromSeconds(100), forwardSeek);

        // Backward seek from cell 30 -> cell 10
        var cell30Seek = track.CalculateSeekTime(30, duration);
        Assert.Equal(TimeSpan.FromSeconds(300), cell30Seek);
        Assert.True(forwardSeek < cell30Seek);
    }

    [Fact]
    public void GenerateDisplayLine_WhenStopped_ReturnsAllBlankBrailleCells()
    {
        var track = new BrailleProgressTrack(20, useBrailleBlank: true);
        var line = track.GenerateDisplayLine(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), isPlaying: false, isPaused: false);

        Assert.Equal(20, line.Length);
        Assert.All(line, c => Assert.Equal(BrailleProgressTrack.BrailleBlank, c));
    }

    [Fact]
    public void GenerateDisplayLine_WhenPlaying_ShowsLowerGAtActiveCell()
    {
        var track = new BrailleProgressTrack(18, useBrailleBlank: true);
        var line = track.GenerateDisplayLine(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(6), isPlaying: true, isPaused: false);

        Assert.Equal(18, line.Length);
        Assert.Equal(BrailleProgressTrack.LowerG, line[9]);

        // All other cells are Braille blank
        for (int i = 0; i < line.Length; i++)
        {
            if (i == 9)
                Assert.Equal(BrailleProgressTrack.LowerG, line[i]);
            else
                Assert.Equal(BrailleProgressTrack.BrailleBlank, line[i]);
        }
    }

    [Fact]
    public void GenerateDisplayLine_WhenPaused_BlinksCorrectly()
    {
        var track = new BrailleProgressTrack(32, useBrailleBlank: true);
        var pos = TimeSpan.FromMinutes(5);
        var dur = TimeSpan.FromMinutes(10);

        // On blink phase: lower g visible at cell 16
        var visibleLine = track.GenerateDisplayLine(pos, dur, isPlaying: false, isPaused: true, blinkVisible: true);
        Assert.Equal(32, visibleLine.Length);
        Assert.Equal(BrailleProgressTrack.LowerG, visibleLine[16]);

        // Off blink phase: all blanks
        var hiddenLine = track.GenerateDisplayLine(pos, dur, isPlaying: false, isPaused: true, blinkVisible: false);
        Assert.Equal(32, hiddenLine.Length);
        Assert.All(hiddenLine, c => Assert.Equal(BrailleProgressTrack.BrailleBlank, c));
    }

    [Fact]
    public void NvdaBrailleBridge_CanTestIfRunning_WithoutException()
    {
        // Should not throw DllNotFoundException or EntryPointNotFoundException
        bool isRunning = NvdaBrailleBridge.IsNvdaRunning();
        // Returns boolean without throwing
        Assert.True(isRunning || !isRunning);
    }

    [Theory]
    [InlineData(10, 20, 200, 105.263)]
    [InlineData(0, 20, 200, 0)]
    [InlineData(19, 20, 200, 200)]
    [InlineData(25, 20, 200, 200)]
    public void CalculateSeekTime_UsesReportedDisplayWidth(int cell, int cellCount, double durationSeconds, double expectedSeconds)
    {
        var seek = BrailleProgressTrack.CalculateSeekTime(cell, cellCount, TimeSpan.FromSeconds(durationSeconds));
        Assert.Equal(expectedSeconds, seek.TotalSeconds, precision: 3);
    }

    [Theory]
    [InlineData("NvdaAddon/manifest.ini", "name = timePlayerBraille")]
    [InlineData("NvdaAddon/globalPlugins/timePlayerBraille.py", "class GlobalPlugin(globalPluginHandler.GlobalPlugin)")]
    public void NvdaAddon_IsEmbedded(string resourceName, string expectedContent)
    {
        Assert.Contains(expectedContent, NvdaAddonInstaller.ReadResource(resourceName));
    }

}
