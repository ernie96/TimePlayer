using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace TimePlayer.Wpf.ViewModels;

/// <summary>Settings that only the Settings dialog changes: seek and volume steps, and the region list.</summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<ChoiceOption<double>> SmallSeekChoices { get; } = SecondsChoices(2, 5, 10, 15, 30);
    public IReadOnlyList<ChoiceOption<double>> LargeSeekChoices { get; } = SecondsChoices(15, 30, 60, 120, 300);
    public IReadOnlyList<ChoiceOption<double>> VolumeStepChoices { get; } =
        new double[] { 1, 2, 5, 10 }.Select(v => new ChoiceOption<double>(v, $"{v} percent")).ToList();

    public IReadOnlyList<ChoiceOption<Core.Models.QualityPreference>> QualityChoices { get; } = new[]
    {
        new ChoiceOption<Core.Models.QualityPreference>(Core.Models.QualityPreference.Auto, "Automatic"),
        new ChoiceOption<Core.Models.QualityPreference>(Core.Models.QualityPreference.P1080, "1080p, full HD"),
        new ChoiceOption<Core.Models.QualityPreference>(Core.Models.QualityPreference.P720, "720p, HD"),
        new ChoiceOption<Core.Models.QualityPreference>(Core.Models.QualityPreference.P480, "480p"),
        new ChoiceOption<Core.Models.QualityPreference>(Core.Models.QualityPreference.P360, "360p, saves data"),
        new ChoiceOption<Core.Models.QualityPreference>(Core.Models.QualityPreference.AudioOnly, "Audio only"),
    };

    /// <summary>Every country Windows knows, by English name, for Featured and the charts.</summary>
    public IReadOnlyList<ChoiceOption<string>> RegionChoices { get; } = BuildRegionChoices();

    [ObservableProperty] private double _smallSeekSeconds = 5;
    [ObservableProperty] private double _largeSeekSeconds = 30;
    [ObservableProperty] private double _volumeStepPercent = 5;

    private bool _stepSettingsLoaded;

    private void InitializeStepSettings()
    {
        var playback = _configService.CurrentConfig.Playback;
        _smallSeekSeconds = Nearest(SmallSeekChoices, playback.SmallSeekSeconds);
        _largeSeekSeconds = Nearest(LargeSeekChoices, playback.LargeSeekSeconds);
        _volumeStepPercent = Nearest(VolumeStepChoices, playback.VolumeStepPercent);
        if (RegionChoices.All(r => r.Value != _featuredRegion))
            _featuredRegion = "US";
        _stepSettingsLoaded = true;
    }

    partial void OnSmallSeekSecondsChanged(double value) => _ = PersistStepSettingsAsync();
    partial void OnLargeSeekSecondsChanged(double value) => _ = PersistStepSettingsAsync();
    partial void OnVolumeStepPercentChanged(double value) => _ = PersistStepSettingsAsync();

    private async Task PersistStepSettingsAsync()
    {
        if (!_stepSettingsLoaded)
            return;
        var current = _configService.CurrentConfig;
        try
        {
            await _configService.UpdateConfigAsync(current with
            {
                Playback = current.Playback with
                {
                    SmallSeekSeconds = SmallSeekSeconds,
                    LargeSeekSeconds = LargeSeekSeconds,
                    VolumeStepPercent = VolumeStepPercent
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the seek and volume steps.");
        }
    }

    private static List<ChoiceOption<double>> SecondsChoices(params double[] values) =>
        values.Select(v => new ChoiceOption<double>(v, v < 60
            ? $"{v} seconds"
            : v % 60 == 0 ? $"{v / 60} minute{(v == 60 ? "" : "s")}" : $"{v} seconds")).ToList();

    private static double Nearest(IReadOnlyList<ChoiceOption<double>> choices, double value) =>
        choices.OrderBy(c => Math.Abs(c.Value - value)).First().Value;

    private static IReadOnlyList<ChoiceOption<string>> BuildRegionChoices()
    {
        var regions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                if (region.TwoLetterISORegionName.Length == 2 && char.IsLetter(region.TwoLetterISORegionName[0]))
                    regions.TryAdd(region.TwoLetterISORegionName.ToUpperInvariant(), region.EnglishName);
            }
            catch (ArgumentException)
            {
            }
        }
        regions.TryAdd("US", "United States");
        return regions
            .OrderBy(r => r.Value, StringComparer.CurrentCulture)
            .Select(r => new ChoiceOption<string>(r.Key, $"{r.Value} ({r.Key})"))
            .ToList();
    }
}
