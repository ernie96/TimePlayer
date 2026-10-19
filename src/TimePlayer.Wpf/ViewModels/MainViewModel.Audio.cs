using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Audio;

namespace TimePlayer.Wpf.ViewModels;

/// <summary>One equalizer band in Settings.</summary>
public sealed partial class EqualizerBand : ObservableObject
{
    private readonly Action _changed;

    public EqualizerBand(int frequency, double gain, Action changed)
    {
        Frequency = frequency;
        _gain = gain;
        _changed = changed;
    }

    public int Frequency { get; }
    public string Label => Frequency >= 1000 ? $"{Frequency / 1000} kilohertz" : $"{Frequency} hertz";
    public string SliderName => $"{Label}, decibels";

    public override string ToString() => Label;

    [ObservableProperty]
    private double _gain;

    partial void OnGainChanged(double value) => _changed();
}

/// <summary>
/// Sound: effect presets, a custom equalizer and music mode. Everything runs inside TimePlayer's
/// own player, so other apps and Windows sound the same as before.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan SoundApplyDelay = TimeSpan.FromMilliseconds(250);

    public IReadOnlyList<string> AudioPresetNames { get; } = AudioPresets.Names;

    public IReadOnlyList<ChoiceOption<int>> RoomChoices { get; } = new[]
    {
        new ChoiceOption<int>(0, "Off"),
        new ChoiceOption<int>(1, "Small room"),
        new ChoiceOption<int>(2, "Large room"),
        new ChoiceOption<int>(3, "Hall"),
    };

    public IReadOnlyList<EqualizerBand> EqualizerBands { get; private set; } = Array.Empty<EqualizerBand>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VideoPresetDescription))]
    private string _videoSoundPreset = AudioPresets.Flat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MusicPresetDescription))]
    private string _musicSoundPreset = "Music";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MusicModeButtonText))]
    private bool _musicMode;

    [ObservableProperty] private double _customBass;
    [ObservableProperty] private double _customClarity;
    [ObservableProperty] private double _customWidth;
    [ObservableProperty] private int _customRoom;
    [ObservableProperty] private bool _customCrossfeed;
    [ObservableProperty] private bool _customLevelVolume;
    [ObservableProperty] private bool _customCompressor;
    [ObservableProperty] private bool _customVoiceFocus;

    private bool _soundSettingsLoaded;
    private bool _loadingCustom;
    private CancellationTokenSource? _soundApplyCts;

    public string MusicModeButtonText => MusicMode ? "Music mode: on" : "Music mode: off";
    public string VideoPresetDescription => DescribePreset(VideoSoundPreset);
    public string MusicPresetDescription => DescribePreset(MusicSoundPreset);

    /// <summary>The preset playing now: the music one in music mode.</summary>
    private string ActiveSoundPreset => MusicMode ? MusicSoundPreset : VideoSoundPreset;

    public string ActiveSoundPresetName => ActiveSoundPreset;

    /// <summary>Chooses the sound for the current mode (Actions menu).</summary>
    public void SelectSoundPreset(string name)
    {
        if (!AudioPresetNames.Contains(name))
            return;
        if (MusicMode)
            MusicSoundPreset = name;
        else
            VideoSoundPreset = name;
        _accessibility.Announce($"{name} sound.", AnnouncementPriority.Medium);
    }

    private static string DescribePreset(string name) =>
        name == AudioPresets.Custom
            ? "Your own settings, below."
            : AudioPresets.Find(name)?.Description ?? string.Empty;

    private void InitializeSoundSettings()
    {
        var audio = _configService.CurrentConfig.Audio;
        _videoSoundPreset = Known(audio.Preset, AudioPresets.Flat);
        _musicSoundPreset = Known(audio.MusicPreset, "Music");
        _musicMode = audio.MusicMode;
        LoadCustom(audio.Custom);
        _soundSettingsLoaded = true;

        // The engine keeps these until it starts, then applies them.
        _ = _playerEngine.SetAudioFiltersAsync(AudioFilterChain.Build(EffectiveSound()));
        _ = _playerEngine.SetVideoEnabledAsync(!_musicMode);
    }

    private static string Known(string? name, string fallback) =>
        name is not null && AudioPresets.Names.Contains(name) ? name : fallback;

    private void LoadCustom(AudioEffectSettings custom)
    {
        _loadingCustom = true;
        EqualizerBands = AudioEffectSettings.BandFrequencies
            .Select((f, i) => new EqualizerBand(f, custom.GainAt(i), OnCustomChanged))
            .ToList();
        OnPropertyChanged(nameof(EqualizerBands));
        CustomBass = custom.Bass;
        CustomClarity = custom.Clarity;
        CustomWidth = custom.Width;
        CustomRoom = custom.Room;
        CustomCrossfeed = custom.Crossfeed;
        CustomLevelVolume = custom.LevelVolume;
        CustomCompressor = custom.Compressor;
        CustomVoiceFocus = custom.VoiceFocus;
        _loadingCustom = false;
    }

    private AudioEffectSettings CustomSound => new()
    {
        EqualizerGains = EqualizerBands.Select(b => b.Gain).ToList(),
        Bass = CustomBass,
        Clarity = CustomClarity,
        Width = CustomWidth,
        Room = CustomRoom,
        Crossfeed = CustomCrossfeed,
        LevelVolume = CustomLevelVolume,
        Compressor = CustomCompressor,
        VoiceFocus = CustomVoiceFocus
    };

    private AudioEffectSettings EffectiveSound() =>
        ActiveSoundPreset == AudioPresets.Custom
            ? CustomSound
            : AudioPresets.Find(ActiveSoundPreset)?.Settings ?? new AudioEffectSettings();

    partial void OnVideoSoundPresetChanged(string value) => SoundChanged();
    partial void OnMusicSoundPresetChanged(string value) => SoundChanged();
    partial void OnCustomBassChanged(double value) => OnCustomChanged();
    partial void OnCustomClarityChanged(double value) => OnCustomChanged();
    partial void OnCustomWidthChanged(double value) => OnCustomChanged();
    partial void OnCustomRoomChanged(int value) => OnCustomChanged();
    partial void OnCustomCrossfeedChanged(bool value) => OnCustomChanged();
    partial void OnCustomLevelVolumeChanged(bool value) => OnCustomChanged();
    partial void OnCustomCompressorChanged(bool value) => OnCustomChanged();
    partial void OnCustomVoiceFocusChanged(bool value) => OnCustomChanged();

    partial void OnMusicModeChanged(bool value)
    {
        if (!_soundSettingsLoaded)
            return;
        _ = _playerEngine.SetVideoEnabledAsync(!value);
        SoundChanged();
    }

    /// <summary>Editing the custom sound makes it the one you hear, so the change is audible right away.</summary>
    private void OnCustomChanged()
    {
        if (!_soundSettingsLoaded || _loadingCustom)
            return;
        if (ActiveSoundPreset != AudioPresets.Custom)
        {
            if (MusicMode)
                MusicSoundPreset = AudioPresets.Custom;
            else
                VideoSoundPreset = AudioPresets.Custom;
            return; // the preset change applies and saves
        }
        SoundChanged();
    }

    /// <summary>Applies and saves after a short pause, so dragging a slider doesn't rebuild the effects on every step.</summary>
    private void SoundChanged()
    {
        if (!_soundSettingsLoaded)
            return;
        _soundApplyCts?.Cancel();
        _soundApplyCts = new CancellationTokenSource();
        _ = ApplySoundAsync(_soundApplyCts.Token);
    }

    private async Task ApplySoundAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SoundApplyDelay, token);
            await ApplySoundNowAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ApplySoundNowAsync()
    {
        try
        {
            await _playerEngine.SetAudioFiltersAsync(AudioFilterChain.Build(EffectiveSound()));
            var current = _configService.CurrentConfig;
            await _configService.UpdateConfigAsync(current with
            {
                Audio = current.Audio with
                {
                    Preset = VideoSoundPreset,
                    MusicPreset = MusicSoundPreset,
                    MusicMode = MusicMode,
                    Custom = CustomSound
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not apply the sound settings.");
        }
    }

    /// <summary>Control+M: music mode on or off.</summary>
    [RelayCommand]
    public void ToggleMusicMode()
    {
        MusicMode = !MusicMode;
        _accessibility.Announce(MusicMode
            ? $"Music mode on: no video, {ActiveSoundPreset} sound."
            : $"Music mode off: video back, {ActiveSoundPreset} sound.", AnnouncementPriority.High);
    }

    /// <summary>E and Shift+E in the player: the next or previous sound preset for the current mode.</summary>
    [RelayCommand]
    public void CycleSoundPreset(int direction)
    {
        var names = AudioPresetNames;
        var index = Math.Max(0, names.ToList().IndexOf(ActiveSoundPreset));
        var next = names[(index + (direction < 0 ? names.Count - 1 : 1)) % names.Count];
        if (MusicMode)
            MusicSoundPreset = next;
        else
            VideoSoundPreset = next;
        _accessibility.Announce($"{next} sound.", AnnouncementPriority.Medium);
    }

    /// <summary>Starts the custom sound from the preset playing now.</summary>
    [RelayCommand]
    public void CopyPresetToCustom()
    {
        var source = AudioPresets.Find(ActiveSoundPreset)?.Settings ?? CustomSound;
        var name = ActiveSoundPreset;
        LoadCustom(source);
        if (MusicMode)
            MusicSoundPreset = AudioPresets.Custom;
        else
            VideoSoundPreset = AudioPresets.Custom;
        SoundChanged();
        _accessibility.Announce($"Custom now starts from {name}.", AnnouncementPriority.Medium);
    }

    [RelayCommand]
    public void ResetCustomSound()
    {
        LoadCustom(new AudioEffectSettings());
        SoundChanged();
        _accessibility.Announce("Custom sound reset to flat.", AnnouncementPriority.Medium);
    }
}
