## How it sounds

A teaching granular synth: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. A source recording (one of five built-in samples, or your own WAV) is cut into short windowed grains read around a frozen scan position.

- `Scan Position` is where in the source the grains come from; it does not move by itself. `Grain Size` is each grain's length, 5 to 500 ms: short is buzzy, long is smooth.
- `Density` is new grains per second, 1 to 64 (at most 10 sound per note). `Time Spray` scatters each grain's start around the scan position.
- `Pitch Shimmer` wanders each grain's pitch and turns some an octave up. `Grain Contour` morphs the window from sharp and clicky (0 %) to a smooth Gaussian (100 %).
- Each control has a How and a Sound sentence: `machine_describe` returns them with the id, and `param_set` takes the name or the id. 16 notes; no limiter.

## Use it for

- Learning granular synthesis: lower `Grain Size` and `Density` until single grains are audible. Then ambient clouds, frozen textures, glitchy stutters, robotic buzz and crystalline pads.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Scan Position | 30% | where in the source the grains are taken |
| Grain Size | 250 ms | under 25 ms buzzes and stutters |
| Density | 24 grains/s | 1-64; under 6 leaves gaps |
| Time Spray | 20% | scatter; 0 % is a steady read |
| Pitch Shimmer | 0% | 60-100 % for an octave glow |
| Grain Contour | 90% | 0 % is clicky, 100 % seamless |
| Output | -6 dB | lower it if `peak_db` passes 0 dBFS |

## Starting points

- "Pad — Frozen Choir", "Pad — Octave Glow", "Cloud — Wide Mist", "Drone — Slow Bloom", "Glitch — Stutter Drops", "Buzz — Robot Grains", "Pluck — Scattered Droplets", "Scrub — Tape Slice", "Shimmer — Crystal Rain".
- Your own sound: `machine_data_import` with kind `sample` and a WAV path; a built-in is kind `source` with a name such as Strings.

## Pitfalls

- The source is machine data, not a control: presets do not store it, and the factory presets are levelled on the Choir.
- The built-in samples build in the background; a render right after load waits for them.
- A very short `Grain Size` with a low `Density` leaves silence between grains, by design.
- Unison 0 to anything above it jumps from 1 voice to 3. Only these 12 controls exist.

## Pairs well with

- Neo Verb and Neo Delay for clouds, Neo EQ to shape the texture, Neo Ensemble for width; Neo Sampler when you want the source played as an instrument.

## Checklist

- [ ] `machine_describe(machine="Learn")` shows the id, range, How and Sound of each control.
- [ ] `param_set(machine="Learn", changes=[{"param": "Grain Size", "value": "250 ms"}])`, then `notes_write` a chord and `render(probes=["Learn"])`.
- [ ] `machine_readings(machine="Learn")` gives `rms_db` near -22 dB and `peak_db` under 0 dBFS.
- [ ] `plot` the waveform and `compare` two grain sizes: short grains show gaps and buzz.
