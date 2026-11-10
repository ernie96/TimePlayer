//! Sound effects for TimePlayer only, never the rest of the system: mpv's FFmpeg audio filters (1.x's
//! `AudioEffects`).

use serde::{Deserialize, Serialize};

/// Equalizer band centres in hertz, one octave apart.
pub const BAND_FREQUENCIES: [u32; 10] = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
pub const MAX_BAND_GAIN: f64 = 12.0;
pub const CUSTOM: &str = "Custom";
pub const FLAT: &str = "Flat";

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct AudioEffectSettings {
    /// Gain per band in decibels, -12 to 12; missing bands count as 0.
    pub equalizer_gains: Vec<f64>,
    /// Extra bass, 0 to 12 dB.
    pub bass: f64,
    /// Brightness and detail, 0 to 10.
    pub clarity: f64,
    /// Stereo widening, 0 to 10.
    pub width: f64,
    /// Room ambience, 0 (off) to 3 (hall).
    pub room: i32,
    pub crossfeed: bool,
    pub level_volume: bool,
    pub compressor: bool,
    pub voice_focus: bool,
}

impl Default for AudioEffectSettings {
    fn default() -> Self {
        Self {
            equalizer_gains: vec![0.0; 10],
            bass: 0.0,
            clarity: 0.0,
            width: 0.0,
            room: 0,
            crossfeed: false,
            level_volume: false,
            compressor: false,
            voice_focus: false,
        }
    }
}

impl AudioEffectSettings {
    pub fn gain_at(&self, band: usize) -> f64 {
        self.equalizer_gains.get(band).copied().unwrap_or(0.0)
    }

    pub fn is_flat(&self) -> bool {
        (0..BAND_FREQUENCIES.len()).all(|i| self.gain_at(i) == 0.0)
            && self.bass == 0.0
            && self.clarity == 0.0
            && self.width == 0.0
            && self.room == 0
            && !self.crossfeed
            && !self.level_volume
            && !self.compressor
            && !self.voice_focus
    }
}

pub struct AudioPreset {
    pub name: &'static str,
    pub description: &'static str,
    pub settings: AudioEffectSettings,
}

fn eq(gains: [f64; 10]) -> Vec<f64> {
    gains.to_vec()
}

/// The presets, in 1.x's order (E and Shift+E go through them).
pub fn presets() -> Vec<AudioPreset> {
    let d = AudioEffectSettings::default;
    vec![
        AudioPreset { name: FLAT, description: "No effects: the sound as uploaded.", settings: d() },
        AudioPreset {
            name: "Music",
            description: "Fuller bass, a little sparkle and a wider stereo image.",
            settings: AudioEffectSettings { equalizer_gains: eq([2., 2., 1., 0., -1., 0., 1., 2., 2., 1.]), bass: 3., clarity: 2., width: 3., ..d() },
        },
        AudioPreset {
            name: "Bass boost",
            description: "Strong low end for music.",
            settings: AudioEffectSettings { equalizer_gains: eq([5., 5., 3., 1., 0., 0., 0., 0., 0., 0.]), bass: 6., ..d() },
        },
        AudioPreset {
            name: "Headphones",
            description: "Wider, more natural sound on headphones, with crossfeed.",
            settings: AudioEffectSettings { equalizer_gains: eq([1., 1., 0., 0., 0., 0., 0., 1., 1., 0.]), width: 2., crossfeed: true, ..d() },
        },
        AudioPreset {
            name: "Voice and podcasts",
            description: "Clear speech: less rumble, more presence, even volume.",
            settings: AudioEffectSettings {
                equalizer_gains: eq([-6., -4., -2., 0., 0., 1., 3., 3., 1., 0.]),
                voice_focus: true,
                compressor: true,
                level_volume: true,
                ..d()
            },
        },
        AudioPreset {
            name: "Night",
            description: "Quiet passages louder and sudden loud ones softer, for low volume.",
            settings: AudioEffectSettings { compressor: true, level_volume: true, ..d() },
        },
        AudioPreset {
            name: "Concert hall",
            description: "A wide sound with the ambience of a large room.",
            settings: AudioEffectSettings { equalizer_gains: eq([1., 1., 0., 0., 0., 0., 0., 1., 1., 0.]), width: 4., room: 3, ..d() },
        },
        AudioPreset { name: "Loudness", description: "Evens out the volume between videos.", settings: AudioEffectSettings { level_volume: true, ..d() } },
    ]
}

/// Preset names followed by "Custom".
pub fn preset_names() -> Vec<&'static str> {
    presets().iter().map(|p| p.name).chain(std::iter::once(CUSTOM)).collect()
}

pub fn find_preset(name: &str) -> Option<AudioPreset> {
    presets().into_iter().find(|p| p.name.eq_ignore_ascii_case(name))
}

/// The settings a preset name stands for ("Custom" is the user's own).
pub fn settings_for(name: &str, custom: &AudioEffectSettings) -> AudioEffectSettings {
    if name.eq_ignore_ascii_case(CUSTOM) {
        custom.clone()
    } else {
        find_preset(name).map(|p| p.settings).unwrap_or_default()
    }
}

fn n(value: f64) -> String {
    let s = format!("{value:.3}");
    let s = s.trim_end_matches('0').trim_end_matches('.');
    if s.is_empty() || s == "-0" { "0".into() } else { s.to_owned() }
}

/// mpv's "af" value; empty means no effects.
pub fn filter_chain(settings: &AudioEffectSettings) -> String {
    if settings.is_flat() {
        return String::new();
    }
    let mut filters: Vec<String> = Vec::new();
    if settings.voice_focus {
        filters.push("highpass=f=90".into());
    }
    for (i, freq) in BAND_FREQUENCIES.iter().enumerate() {
        let gain = settings.gain_at(i).clamp(-MAX_BAND_GAIN, MAX_BAND_GAIN);
        if gain != 0.0 {
            filters.push(format!("equalizer=f={freq}:t=o:w=1:g={}", n(gain)));
        }
    }
    let bass = settings.bass.clamp(0.0, 12.0);
    if bass > 0.0 {
        filters.push(format!("bass=g={}:f=110:w=0.6", n(bass)));
    }
    let clarity = settings.clarity.clamp(0.0, 10.0);
    if clarity > 0.0 {
        filters.push(format!("aexciter=amount={}:drive=8.5:freq=6000", n(clarity * 0.3)));
    }
    let width = settings.width.clamp(0.0, 10.0);
    if width > 0.0 {
        filters.push(format!("extrastereo=m={}", n(1.0 + width * 0.15)));
    }
    if settings.crossfeed {
        filters.push("bs2b=profile=default".into());
    }
    match settings.room.clamp(0, 3) {
        1 => filters.push("aecho=0.9:0.85:30|45:0.18|0.12".into()),
        2 => filters.push("aecho=0.9:0.8:45|70:0.28|0.22".into()),
        3 => filters.push("aecho=0.85:0.75:60|100|140:0.35|0.3|0.25".into()),
        _ => {}
    }
    if settings.compressor {
        filters.push("acompressor=threshold=0.125:ratio=3:attack=15:release=200:makeup=2".into());
    }
    if settings.level_volume {
        filters.push("dynaudnorm=f=300:g=15:m=8".into());
    }
    // Boosts can push peaks past full scale; a limiter at the end keeps them from crackling.
    let boosts = bass > 0.0
        || clarity > 0.0
        || settings.compressor
        || settings.level_volume
        || (0..BAND_FREQUENCIES.len()).any(|i| settings.gain_at(i) > 0.0);
    if boosts {
        filters.push("alimiter=limit=0.97:level=0".into());
    }
    if filters.is_empty() { String::new() } else { format!("@timeplayer:lavfi=[{}]", filters.join(",")) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn flat_has_no_filters() {
        assert_eq!(filter_chain(&AudioEffectSettings::default()), "");
    }

    #[test]
    fn night_compresses_and_limits() {
        let chain = filter_chain(&find_preset("night").unwrap().settings);
        assert!(chain.starts_with("@timeplayer:lavfi=[acompressor"));
        assert!(chain.ends_with("alimiter=limit=0.97:level=0]"));
    }

    #[test]
    fn music_matches_1x() {
        let chain = filter_chain(&find_preset("Music").unwrap().settings);
        assert!(chain.contains("equalizer=f=31:t=o:w=1:g=2"));
        assert!(chain.contains("equalizer=f=500:t=o:w=1:g=-1"));
        assert!(chain.contains("aexciter=amount=0.6:drive=8.5:freq=6000"));
        assert!(chain.contains("extrastereo=m=1.45"));
    }
}
