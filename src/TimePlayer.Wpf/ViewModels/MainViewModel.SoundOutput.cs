using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Playback;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Wpf.ViewModels;

/// <summary>
/// Where TimePlayer's sound goes (so the screen reader can stay on another device) and volume boost
/// (Up past 100 percent, for videos recorded too quietly).
/// </summary>
public sealed partial class MainViewModel
{
    public const string DefaultAudioDevice = "auto";

    public ObservableCollection<ChoiceOption<string>> AudioDeviceChoices { get; } = new();
    [ObservableProperty] private string _audioDevice = DefaultAudioDevice;
    [ObservableProperty] private bool _volumeBoost;

    private bool _soundOutputLoaded;

    public double MaxVolume => VolumeBoost ? 200 : 100;

    private void InitializeSoundOutput()
    {
        var playback = _configService.CurrentConfig.Playback;
        _audioDevice = string.IsNullOrWhiteSpace(playback.AudioDevice) ? DefaultAudioDevice : playback.AudioDevice;
        _volumeBoost = playback.VolumeBoost;
        ResetAudioDeviceChoices(Array.Empty<AudioDevice>());
        // The player reads these when it starts.
        _ = _playerEngine.SetAudioDeviceAsync(_audioDevice);
        _ = _playerEngine.SetMaxVolumeAsync(MaxVolume);
        _soundOutputLoaded = true;
    }

    private void ResetAudioDeviceChoices(IReadOnlyList<AudioDevice> devices)
    {
        var chosen = AudioDevice;
        AudioDeviceChoices.Clear();
        AudioDeviceChoices.Add(new ChoiceOption<string>(DefaultAudioDevice, T("Windows default")));
        foreach (var device in devices.Where(d => d.Name != DefaultAudioDevice))
            AudioDeviceChoices.Add(new ChoiceOption<string>(device.Name, device.Description));
        if (AudioDeviceChoices.All(c => c.Value != chosen))
            AudioDeviceChoices.Add(new ChoiceOption<string>(chosen, T("The device chosen before (not connected now)")));
        OnPropertyChanged(nameof(AudioDevice)); // the combo box finds its item again
    }

    /// <summary>Asks the player which devices there are (Settings and the Actions menu call this).</summary>
    public async Task RefreshAudioDevicesAsync()
    {
        try
        {
            var devices = await _playerEngine.GetAudioDevicesAsync();
            if (devices.Count > 0)
                ResetAudioDeviceChoices(devices);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not list sound devices.");
        }
    }

    partial void OnAudioDeviceChanged(string value)
    {
        if (!_soundOutputLoaded || string.IsNullOrWhiteSpace(value))
            return;
        _ = _playerEngine.SetAudioDeviceAsync(value);
        var current = _configService.CurrentConfig;
        if (current.Playback.AudioDevice != value)
            _ = _configService.UpdateConfigAsync(current with { Playback = current.Playback with { AudioDevice = value } });
    }

    /// <summary>The Actions menu's Sound, Output: switch and say where the sound goes now.</summary>
    public void ChooseAudioDevice(string device)
    {
        AudioDevice = device;
        var name = AudioDeviceChoices.FirstOrDefault(c => c.Value == device)?.Label ?? device;
        _accessibility.Announce(T("Sound plays through {0}.", name), AnnouncementPriority.Medium);
    }

    partial void OnVolumeBoostChanged(bool value)
    {
        OnPropertyChanged(nameof(MaxVolume));
        if (!_soundOutputLoaded)
            return;
        _ = _playerEngine.SetMaxVolumeAsync(MaxVolume);
        if (!value && VolumePercent > 100)
            _ = SetVolumeQuietlyAsync(100);
        var current = _configService.CurrentConfig;
        if (current.Playback.VolumeBoost != value)
            _ = _configService.UpdateConfigAsync(current with { Playback = current.Playback with { VolumeBoost = value } });
    }

    private async Task SetVolumeQuietlyAsync(double percent)
    {
        try
        {
            await _playerEngine.SetVolumeAsync(percent);
            VolumePercent = percent;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not set the volume.");
        }
    }

    /// <summary>"Volume 120 percent, boosted" above 100.</summary>
    private static string VolumeSpoken(double percent) => percent > 100
        ? T("Volume {0} percent, boosted", (int)Math.Round(percent))
        : AccessibleTextFormatter.FormatVolumeSpoken(percent);
}
