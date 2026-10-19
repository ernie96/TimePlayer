using System.Globalization;

namespace TimePlayer.Core.Audio;

/// <summary>
/// One set of audio effects. All of it runs inside TimePlayer's own player (mpv's FFmpeg filters),
/// so nothing changes for the rest of Windows.
/// </summary>
public sealed record AudioEffectSettings
{
    /// <summary>Equalizer band centres in hertz, one octave apart.</summary>
    public static readonly IReadOnlyList<int> BandFrequencies = new[] { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public const double MaxBandGain = 12;

    /// <summary>Gain per band in decibels, -12 to 12; missing bands count as 0.</summary>
    public IReadOnlyList<double> EqualizerGains { get; init; } = new double[10];
    /// <summary>Extra bass in decibels, 0 to 12.</summary>
    public double Bass { get; init; }
    /// <summary>Brightness and detail, 0 to 10.</summary>
    public double Clarity { get; init; }
    /// <summary>Stereo widening, 0 to 10.</summary>
    public double Width { get; init; }
    /// <summary>Room ambience, 0 (off) to 3 (hall).</summary>
    public int Room { get; init; }
    /// <summary>Headphone crossfeed: less of the hard-left/hard-right feel on headphones.</summary>
    public bool Crossfeed { get; init; }
    /// <summary>Evens out loud and quiet videos and passages.</summary>
    public bool LevelVolume { get; init; }
    /// <summary>Makes quiet sounds louder and loud ones softer (night listening).</summary>
    public bool Compressor { get; init; }
    /// <summary>Cuts rumble below the voice range.</summary>
    public bool VoiceFocus { get; init; }

    public double GainAt(int band) => band < EqualizerGains.Count ? EqualizerGains[band] : 0;

    public bool IsFlat =>
        Enumerable.Range(0, BandFrequencies.Count).All(i => GainAt(i) == 0) &&
        Bass == 0 && Clarity == 0 && Width == 0 && Room == 0 && !Crossfeed && !LevelVolume && !Compressor && !VoiceFocus;
}

public sealed record AudioPreset(string Name, string Description, AudioEffectSettings Settings);

public static class AudioPresets
{
    public const string Custom = "Custom";
    public const string Flat = "Flat";

    private static double[] Eq(params double[] gains) => gains;

    public static readonly IReadOnlyList<AudioPreset> All = new[]
    {
        new AudioPreset(Flat, "No effects: the sound as uploaded.", new AudioEffectSettings()),
        new AudioPreset("Music", "Fuller bass, a little sparkle and a wider stereo image.",
            new AudioEffectSettings { EqualizerGains = Eq(2, 2, 1, 0, -1, 0, 1, 2, 2, 1), Bass = 3, Clarity = 2, Width = 3 }),
        new AudioPreset("Bass boost", "Strong low end for music.",
            new AudioEffectSettings { EqualizerGains = Eq(5, 5, 3, 1, 0, 0, 0, 0, 0, 0), Bass = 6 }),
        new AudioPreset("Headphones", "Wider, more natural sound on headphones, with crossfeed.",
            new AudioEffectSettings { EqualizerGains = Eq(1, 1, 0, 0, 0, 0, 0, 1, 1, 0), Width = 2, Crossfeed = true }),
        new AudioPreset("Voice and podcasts", "Clear speech: less rumble, more presence, even volume.",
            new AudioEffectSettings { EqualizerGains = Eq(-6, -4, -2, 0, 0, 1, 3, 3, 1, 0), VoiceFocus = true, Compressor = true, LevelVolume = true }),
        new AudioPreset("Night", "Quiet passages louder and sudden loud ones softer, for low volume.",
            new AudioEffectSettings { Compressor = true, LevelVolume = true }),
        new AudioPreset("Concert hall", "A wide sound with the ambience of a large room.",
            new AudioEffectSettings { EqualizerGains = Eq(1, 1, 0, 0, 0, 0, 0, 1, 1, 0), Width = 4, Room = 3 }),
        new AudioPreset("Loudness", "Evens out the volume between videos.",
            new AudioEffectSettings { LevelVolume = true }),
    };

    public static IReadOnlyList<string> Names { get; } = All.Select(p => p.Name).Append(Custom).ToList();

    public static AudioPreset? Find(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Turns effect settings into mpv's "af" (audio filter) property.</summary>
public static class AudioFilterChain
{
    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The mpv "af" value; empty means no effects.</summary>
    public static string Build(AudioEffectSettings? settings)
    {
        if (settings is null || settings.IsFlat)
            return string.Empty;

        var filters = new List<string>();
        if (settings.VoiceFocus)
            filters.Add("highpass=f=90");

        for (int i = 0; i < AudioEffectSettings.BandFrequencies.Count; i++)
        {
            var gain = Math.Clamp(settings.GainAt(i), -AudioEffectSettings.MaxBandGain, AudioEffectSettings.MaxBandGain);
            if (gain != 0)
                filters.Add($"equalizer=f={AudioEffectSettings.BandFrequencies[i]}:t=o:w=1:g={N(gain)}");
        }

        var bass = Math.Clamp(settings.Bass, 0, 12);
        if (bass > 0)
            filters.Add($"bass=g={N(bass)}:f=110:w=0.6");

        var clarity = Math.Clamp(settings.Clarity, 0, 10);
        if (clarity > 0)
            filters.Add($"aexciter=amount={N(clarity * 0.3)}:drive=8.5:freq=6000");

        var width = Math.Clamp(settings.Width, 0, 10);
        if (width > 0)
            filters.Add($"extrastereo=m={N(1 + width * 0.15)}");

        if (settings.Crossfeed)
            filters.Add("bs2b=profile=default");

        filters.AddRange(Math.Clamp(settings.Room, 0, 3) switch
        {
            1 => new[] { "aecho=0.9:0.85:30|45:0.18|0.12" },
            2 => new[] { "aecho=0.9:0.8:45|70:0.28|0.22" },
            3 => new[] { "aecho=0.85:0.75:60|100|140:0.35|0.3|0.25" },
            _ => Array.Empty<string>()
        });

        if (settings.Compressor)
            filters.Add("acompressor=threshold=0.125:ratio=3:attack=15:release=200:makeup=2");
        if (settings.LevelVolume)
            filters.Add("dynaudnorm=f=300:g=15:m=8");

        // Boosts can push peaks past full scale; a limiter at the end keeps them from crackling.
        bool boosts = bass > 0 || clarity > 0 || settings.Compressor || settings.LevelVolume ||
                      Enumerable.Range(0, AudioEffectSettings.BandFrequencies.Count).Any(i => settings.GainAt(i) > 0);
        if (boosts)
            filters.Add("alimiter=limit=0.97:level=0");

        return filters.Count == 0 ? string.Empty : $"@timeplayer:lavfi=[{string.Join(",", filters)}]";
    }
}
