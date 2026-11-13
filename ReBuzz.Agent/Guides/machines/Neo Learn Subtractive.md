## How it sounds

A teaching subtractive synth: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. A wave that morphs sine, triangle, saw, pulse, an optional sub square one octave down, a 24 dB/octave ladder low-pass with resonance and drive, and one ADSR that shapes both amplitude and filter. Warm basses, pulse and saw leads, slow pads, soft keys, plucks and resonant sweeps. Band-limited oscillators keep the top clean.

## Signal flow

- Oscillator: `Wave Shape` crossfades 0 % sine, 33 % triangle, 66 % saw, 100 % pulse. `Sub Osc` adds a phase-locked square an octave down.
- `Drive` saturates before the filter and adds slight per-voice pitch drift.
- Filter: 24 dB/octave low-pass, `Cutoff` (20 Hz to 20 kHz) and `Resonance`. `Env to Filter` sweeps the cutoff by up to 6 octaves with the envelope (negative closes it).
- One ADSR (`Attack`, `Decay`, `Sustain`, `Release`) drives both the amplitude and the filter.
- `Unison / Spread`: 0 % is one voice per note; above it each note plays 5 detuned, spread voices (a step, not a ramp).
- `Output` is the level after synthesis (default about -5.5 dB, so presets sit near -22 dB RMS). There is no limiter; the clip light shows over 0 dBFS.
- 16 notes.

## How to use it

- Bass: `Wave Shape` saw or pulse, `Sub Osc` 50-80 %, cutoff 300-900 Hz, `Env to Filter` positive, short decay, `Unison / Spread` 0.
- Lead: `Wave Shape` 66-100 %, resonance 20-40 %, a little unison.
- Pad: slow attack and release, `Unison / Spread` 30-60 %, triangle or saw.
- Pluck: `Sustain` 0, short decay, positive `Env to Filter`.
- Read the result with `machine_readings` (rms_db, peak_db, active_voices); `machine_describe` gives each control's id (snake_case, also taken by `param_set`), unit, range, curve, how and sound. Writes with `param_set` show on the machine's Explain strip with the Agent badge.

## Gotchas

- Unison 0 to anything above it jumps from 1 voice to 5 (level stays even; the sound gets wider).
- `Wave Shape` has no gap, but the oscillator that plays sine below 50 % changes to pulse above it at a silent point.
- Filter envelope amount at 50 % (raw 500) is zero.
- With resonance high and the cutoff swept, the output can pass 0 dBFS: watch peak_db and lower `Output`.
- A machine of 12 controls: the other Dye Essence controls are fixed and cannot be set here.
