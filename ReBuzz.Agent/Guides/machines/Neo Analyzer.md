## How it sounds

It doesn't: Neo Analyzer passes its input through bit for bit, with no latency, and measures it. Its numbers are the analysis library's, the ones `analyze` reports, measured live wherever it sits:

- **Loudness**: momentary (400 ms), short-term (3 s), integrated (BS.1770, gated) and loudness range (EBU R128) since the reset, in LUFS and LU.
- **True peak**: 4× oversampled, in dBTP: the maximum since the reset, the recent peak and a held marker.
- **Spectrum**: third-octave bars and a finer curve with held peaks, in plain RMS dB (a full-scale sine reads −3 dB, so a sine reads its RMS level in its band).
- **Stereo**: correlation (+1 mono, 0 unrelated, −1 opposed), width (side over mid energy) and a goniometer.

## Reading it

The machine's readings (machine_readings in studio mode) are 48 numbers: `momentary_lufs`, `short_term_lufs`, `integrated_lufs`, `range_lu`, `true_peak_dbtp`, `momentary_max_lufs`, `short_term_max_lufs`, `seconds` (measured since the reset), `correlation` and `width` (last 3 s), the named bands `sub_db`, `low_db`, `low_mid_db`, `mid_db`, `high_mid_db`, `presence_db`, `air_db` and the third octaves `band_20hz_db` … `band_20000hz_db` (last 3 s). Silence reads −∞. The values stay when the transport stops, so after a render they describe it.

- Integrated loudness counts from the last reset: switch Reset Off and On (or use the editor's Reset button) before playing the passage to judge.
- The display settings (Smoothing, Speed, Tilt, Peak hold) change only what the editor draws, never the readings.
- In headless work, `render` and `analyze` measure the same things without a machine in the song; the analyzer is for watching a song while it plays.

## Settings

| Parameter | Value | Why |
|---|---|---|
| Target | -14 dB | streaming loudness: the meters turn green within 1 LU of it |
| Smoothing | 1/3 oct | the curve passes through the third-octave bars |
| Speed | Slow | a steady picture of a mix |
| Tilt | 3 dB/oct | a finished mix (about −3 dB per octave) draws about flat |
| Peak hold | 5 s | |

## Presets

| Preset | For |
|---|---|
| Mix | −18 LUFS target (headroom for mastering), medium speed, 3 dB/oct tilt |
| Mastering | −14 LUFS, very slow, 4.5 dB/oct tilt to judge the top end |
| K-14 | −14 LUFS, slow, flat third octaves |
| Broadcast R128 | −23 LUFS |
| Tones | 1/24 octave and fast, for finding resonances |
