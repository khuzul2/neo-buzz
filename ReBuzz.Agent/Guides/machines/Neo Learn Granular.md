## How it sounds

A teaching granular synth: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. A source recording (one of five built-in samples, or your own WAV) is cut into short windowed grains read around a frozen scan position; grain size, density, spray, a pitch shimmer and the shape of each grain's fade are the whole sound. Ambient clouds, frozen textures, glitchy stutters, robotic buzz, crystalline pads.

## Signal flow

- `Scan Position` is where in the source the grains are taken from (0 % start, 100 % end); it does not move by itself.
- `Grain Size` is each grain's length, 5 to 500 ms: short is buzzy, long is smooth.
- `Density` is new grains per second, 1 to 64. At most 10 grains sound per note; past that the oldest fade out, so a long size with a high density does not get denser.
- `Time Spray` scatters each grain's start around the scan position (100 % is anywhere in the source).
- `Pitch Shimmer` wanders each grain's pitch by up to 25 cents and turns up to half of the grains an octave up (no extra grains are added).
- `Grain Contour` morphs each grain's window from a trapezoid with sharp edges (0 %, clicky) to a Gaussian (100 %, seamless).
- `Attack`, `Decay`, `Sustain`, `Release` shape the level. `Unison / Spread`: 0 % is one voice per note; above it each note plays 3 detuned, spread voices (a step, not a ramp).
- `Output` is the level after synthesis (presets sit near -22 dB RMS). There is no limiter; the clip light shows over 0 dBFS. 16 notes.

## How to use it

- Frozen pad: `Grain Size` 250 ms, `Density` 24, `Grain Contour` 90 %, slow `Attack`.
- Glitch stutter: `Grain Size` under 25 ms, `Density` under 6, `Grain Contour` 0 %, `Sustain` 0 %.
- Robot buzz: `Grain Size` 8 ms, `Density` 48, `Time Spray` 0 %.
- Octave glow: `Pitch Shimmer` 60-100 % over a long, soft pad.
- Your own sound: import a WAV (`machine_data_import`, kind `sample`), or pick a built-in (kind `source`, a name such as Strings).

## Checklist

- Read each control's id, range and how/sound with `machine_describe` (ids are snake_case and also taken by `param_set`).
- Optionally choose the source with `machine_data_import`; the controls do not change it.
- Change one control at a time with `param_set`, play a pattern (`notes_write`), then `render` with probes and check `machine_readings` (rms_db, peak_db, active_voices).
- `plot` a spectrum or the waveform of the render, and `compare` two settings.
- Writes via `param_set` show on the Explain strip with the Agent badge.

## Gotchas

- The source is machine data, not a control: presets do not store it, and the factory presets are levelled on the Choir.
- The built-in samples build in the background; a render right after load waits for them (`Busy`).
- A very short `Grain Size` with a low `Density` leaves silence between grains, by design.
- A machine of 12 controls: the other Dye Essence controls are fixed and cannot be set here.
