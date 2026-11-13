## How it sounds

A teaching wavetable synth: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. A table of waveform frames is scanned: you pick the table and the frame, bend the phase of each cycle, and let a slow motion sweep the frame for you. Band-limited tables keep the top clean.

- `Wavetable` picks one of ten tables (Basic, PWM, Harmonics, Formants, Sync_sweep, FM_sweep, Organ, Resonant, Digital, Grit) or User, an imported WAV of 2048-sample frames or a single cycle.
- `Frame Pos` is where in the table the sound reads, blended between neighbouring frames. `Phase Warp` bends the cycle (0 % is untouched), adding harmonics and sync-like bite.
- `Motion Rate` and `Motion Depth` run a slow LFO on the frame position. `Sub Anchor` adds a sine an octave down so the low end stays solid.
- Each control has a How and a Sound sentence: `machine_describe` returns them with the id, and `param_set` takes the name or the id. 16 notes; no limiter.

## Use it for

- Learning wavetables: pick a table, move `Frame Pos` and hear the timbre morph. Then evolving pads, sync sweeps, pinched basses, organ keys and digital glass. Neo Wave is the full two-oscillator version.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Wavetable | Formants | the family of tones |
| Frame Pos | 50% | where in the table; modulate it for movement |
| Phase Warp | 0% | neutral; positive and negative bend the cycle |
| Motion Rate | 0.3 Hz | 0.2-0.5 Hz for pads; 1-3 Hz for sweeps |
| Motion Depth | 40% | the width of the sweep |
| Sub Anchor | 0% | 40-80 % for basses |
| Output | -6 dB | lower it if `peak_db` passes 0 dBFS |

## Starting points

- "Pad — Formant Drift", "Lead — Sync Sweep", "Bass — Pinch", "Keys — Organ Table", "Pad — Digital Glass", "Lead — Grit Wobble", "Bass — PWM Wobble", "Pluck — Harmonic Stab".
- Pad: slow `Attack` and `Release`, `Motion Depth` 40-70 %, `Unison / Spread` 30 %. Sync lead: Sync_sweep, `Motion Rate` 1-3 Hz.

## Pitfalls

- Unison 0 to anything above it jumps from 1 voice to 5.
- With high `Motion Depth` the frame reaches the table's ends, and some tables are loud there: watch `peak_db`.
- User plays only after a wave has been imported into the machine; until then pick a built-in table.
- Only these 12 controls exist: the other Dye Essence controls are fixed and cannot be set here.

## Pairs well with

- Neo Filter and Neo Ensemble for movement, Neo Delay and Neo Verb for space; Neo Wave when you want two oscillators, warps and a filter.

## Checklist

- [ ] `machine_describe(machine="Learn")` shows the id, range, How and Sound of each control.
- [ ] `param_set(machine="Learn", changes=[{"param": "Frame Pos", "value": "70%"}])`, then `notes_write` a chord and `render(probes=["Learn"])`.
- [ ] `machine_readings(machine="Learn")` gives `rms_db` near -22 dB and `peak_db` under 0 dBFS.
- [ ] `plot` a spectrum at two frame positions and `compare` them: the harmonics move.
