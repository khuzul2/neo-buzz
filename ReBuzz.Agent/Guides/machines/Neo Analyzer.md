## How it sounds

It doesn't. Neo Analyzer passes its input through bit for bit, with zero latency, and measures it. Its numbers are the analysis library's, the ones `analyze` reports, measured live wherever it sits:

- **Loudness**: momentary (400 ms), short-term (3 s), integrated (BS.1770, gated) and loudness range (EBU R128) since the reset, in LUFS and LU.
- **True peak**: 4× oversampled, in dBTP: the maximum since the reset, the recent peak and a held marker.
- **Spectrum**: third-octave bars and a finer curve with held peaks, in plain RMS dB. A full-scale sine reads −3 dB, so a sine reads its RMS level in its band.
- **Stereo**: correlation (+1 mono, 0 unrelated, −1 opposed), width (side over mid energy) and a goniometer.

Over a whole signal its integrated loudness, range and true peak agree with `analyze` within 0.001 LU and 0.001 dB.

## Signal flow

Input → output, unchanged. On the side, the analysis:

- K-weighting into 100 ms steps → momentary and short-term windows → the gated integrated loudness and the loudness range since the reset.
- A 4× interpolator → the true peak.
- An 8192-point FFT every 2048 samples → the average set by `Speed` → third-octave bars and a curve whose bands are `Smoothing` wide → held peaks (`Peak hold`) → the display `Tilt`.
- Left and right energies → correlation and width over 400 ms (the meter) and 3 s (the readings).

`Target` only colours the meters (green within 1 LU of it, blue under, amber up to 3 LU over, red beyond). `Reset` switched On starts the integrated loudness, range, maxima and held peaks again.

## How to use it

- Put it on the master (or any track) and play the passage to judge after a reset. Switch `Reset` Off, then On, or use the editor's Reset button.
- Its readings (machine_readings in studio mode) are 48 numbers:
  - `momentary_lufs`, `short_term_lufs`, `integrated_lufs`, `range_lu`, `true_peak_dbtp`;
  - `momentary_max_lufs`, `short_term_max_lufs`, and `seconds` (time measured since the reset);
  - `correlation` and `width`, over the last 3 s;
  - the named bands `sub_db`, `low_db`, `low_mid_db`, `mid_db`, `high_mid_db`, `presence_db`, `air_db`, and the third octaves `band_20hz_db` … `band_20000hz_db`, over the last 3 s.

  Silence reads −∞. The values stay when the transport stops, so after a render they describe it.
- For a master aimed at streaming:

| Parameter | Value | Why |
|---|---|---|
| Target | -14 dB | streaming loudness, in LUFS |
| Smoothing | 1/3 oct | the curve passes through the third-octave bars |
| Speed | Slow | a steady picture of a mix |
| Tilt | 3 dB/oct | a finished mix (about −3 dB per octave) draws about flat |
| Peak hold | 5 s | |

## Gotchas

- `Smoothing`, `Speed`, `Tilt` and `Peak hold` change only what the editor draws, never the readings.
- The momentary, short-term, band and stereo readings cover the last 400 ms or 3 s, including a render's tail: keep tails short when reading them after a render.
- Bands below 100 Hz span few FFT bins (5.9 Hz each), so 3 s of noise varies there by about ±0.5 dB.
- Without a machine in the song, `render` and `analyze` measure the same things: the analyzer is for watching a song while it plays.

## Preset map

| Preset | What |
|---|---|
| Mix | −18 LUFS target (headroom for mastering), medium speed, 3 dB/oct tilt |
| Mastering | −14 LUFS, very slow, 4.5 dB/oct tilt to judge the top end |
| K-14 | −14 LUFS, slow, flat third octaves |
| Broadcast R128 | −23 LUFS |
| Tones | 1/24 octave and fast, for finding resonances |
