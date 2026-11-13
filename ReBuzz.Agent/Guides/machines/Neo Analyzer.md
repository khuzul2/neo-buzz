## How it sounds

It doesn't. Neo Analyzer passes its input through bit for bit, with zero latency, and measures it with the analysis library's own code, the numbers `analyze` reports, live wherever it sits:

- **Loudness**: momentary (400 ms), short-term (3 s), integrated (gated, since the reset) and loudness range, in LUFS and LU.
- **True peak**: 4x oversampled, in dBTP: the maximum since the reset, the recent peak and a held marker.
- **Spectrum**: third-octave bars and a finer curve with held peaks, in plain RMS dB (a full-scale sine reads -3 dB).
- **Stereo**: correlation (+1 mono, 0 unrelated, -1 opposed), width (side over mid energy) and a goniometer.

Over a whole signal its integrated loudness, range and true peak agree with `analyze` within 0.001 LU and 0.001 dB. `Target` only colours the meters (green within 1 LU, blue under, amber up to 3 LU over, red beyond). The display settings never change the readings. The editor draws all of it; the strip under it explains each control.

## Use it for

- Watching a mix or master while it plays: loudness against a target, true peak, tonal balance, width and mono safety.
- Reading numbers back as an agent: 48 readings through `machine_readings`, still valid after the transport stops (they describe the last 3 s, tail of a render included).
- Finding resonances ("Tones") and judging the top end of a master ("Mastering").

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Target | -14 dB | the loudness (LUFS) the meters are coloured against: -14 streaming, -23 broadcast |
| Smoothing | 1/3 oct | the curve passes through the third-octave bars; 1/24 oct resolves single tones |
| Speed | Slow | a steady picture of a mix (Fast averages 0.1 s, Very slow 3 s) |
| Tilt | 3 dB/oct | a finished mix draws about flat; 0 shows pink noise flat |
| Peak hold | 5 s | how long peaks and the true-peak marker stay up |
| Reset | On | restarts the integrated loudness, range, maxima and held peaks; switch it Off and On again for the next reset |

Readings: `momentary_lufs`, `short_term_lufs`, `integrated_lufs`, `range_lu`, `true_peak_dbtp`, `momentary_max_lufs`, `short_term_max_lufs`, `seconds`, `correlation`, `width`, the named bands `sub_db` to `air_db` and the third octaves `band_20hz_db` to `band_20000hz_db`. Silence reads -inf. `machine_describe` gives each control's id, unit, range, how and sound.

## Starting points

- "Mix": -18 LUFS target (headroom for mastering), medium speed, 3 dB/oct tilt.
- "Mastering": -14 LUFS, very slow, 4.5 dB/oct tilt to judge the top end.
- "K-14": -14 LUFS, slow, flat third octaves. "Broadcast R128": -23 LUFS.
- "Tones": 1/24 octave and fast, for finding resonances.
- Streaming master: the Key controls above, then play the whole track after a `Reset`.

## Pitfalls

- Integrated loudness and range count from the reset: reset, play the passage you judge from its start, then read.
- Momentary, short-term, band and stereo readings cover the last 400 ms or 3 s, including a render's tail: keep tails short.
- Bands below 100 Hz span few FFT bins, so 3 s of noise varies there by about 0.5 dB.
- Without a machine in the song, `render` and `analyze` measure the same things; the analyzer is for watching a song play.

## Pairs well with

- Neo Limiter and Neo Dither at the end of a master chain (`guide(topic="mastering-chain")`): put the Analyzer last to read what the file will contain.
- Neo Scope for the waveform and stereo picture, Neo Multiband and Neo EQ when the bands show a problem.

## Checklist

- [ ] `machine_readings(machine="Analyzer")` after a `Reset` and a full play: `integrated_lufs` within 1 LU of the `Target`.
- [ ] `machine_readings(machine="Analyzer")` again: `true_peak_dbtp` is at or under the limiter's ceiling.
- [ ] `correlation` stays above 0 (check with `machine_readings`), so the mix survives mono.
- [ ] `analyze(source="master", metrics=["loudness", "peaks"])` agrees with the readings.
