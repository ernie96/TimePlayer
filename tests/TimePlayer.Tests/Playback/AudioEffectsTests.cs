using TimePlayer.Core.Audio;
using Xunit;

namespace TimePlayer.Tests.Playback;

public class AudioEffectsTests
{
    [Fact]
    public void Flat_HasNoFilters()
    {
        Assert.Equal(string.Empty, AudioFilterChain.Build(new AudioEffectSettings()));
        Assert.Equal(string.Empty, AudioFilterChain.Build(AudioPresets.Find(AudioPresets.Flat)!.Settings));
        Assert.Equal(string.Empty, AudioFilterChain.Build(null));
    }

    [Fact]
    public void Build_UsesOnlyChangedBandsAndEndsWithALimiterAfterBoosts()
    {
        var chain = AudioFilterChain.Build(new AudioEffectSettings
        {
            EqualizerGains = new double[] { 0, 3, 0, 0, 0, 0, 0, -2.5, 0, 0 },
            Bass = 4
        });

        Assert.Equal(
            "@timeplayer:lavfi=[equalizer=f=62:t=o:w=1:g=3,equalizer=f=4000:t=o:w=1:g=-2.5,bass=g=4:f=110:w=0.6,alimiter=limit=0.97:level=0]",
            chain);
    }

    [Fact]
    public void Build_ClampsOutOfRangeValues()
    {
        var chain = AudioFilterChain.Build(new AudioEffectSettings { EqualizerGains = new double[] { 40 }, Width = 99, Room = 9 });

        Assert.Contains("equalizer=f=31:t=o:w=1:g=12", chain);
        Assert.Contains("extrastereo=m=2.5", chain);
        Assert.Contains("aecho=0.85:0.75:60|100|140", chain);
    }

    [Fact]
    public void Build_CutOnlySettings_NeedNoLimiter()
    {
        var chain = AudioFilterChain.Build(new AudioEffectSettings { EqualizerGains = new double[] { -6 }, Crossfeed = true });
        Assert.DoesNotContain("alimiter", chain);
        Assert.Contains("bs2b", chain);
    }

    [Fact]
    public void EveryPreset_BuildsAndIsListedWithCustom()
    {
        foreach (var preset in AudioPresets.All)
        {
            var chain = AudioFilterChain.Build(preset.Settings);
            Assert.True(preset.Name == AudioPresets.Flat ? chain.Length == 0 : chain.StartsWith("@timeplayer:lavfi=["), preset.Name);
            Assert.DoesNotContain(";", chain);
        }
        Assert.Equal(AudioPresets.Custom, AudioPresets.Names[^1]);
    }

    [Fact]
    public void Numbers_UseDotsWhateverTheLanguage()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            Assert.Contains("g=1.5", AudioFilterChain.Build(new AudioEffectSettings { EqualizerGains = new[] { 1.5 } }));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
