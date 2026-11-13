## How it sounds

A polyphonic analogue-style synth: warm saws and pulses through a resonant ladder filter (24 or 12 dB per octave, self-oscillating at the top) or a clean state-variable filter. Detuned unison and per-voice `Drift` make it wide and alive. The oscillators are band-limited, so bright leads and sync sweeps stay clean.

- Oscillators 1-3: each `shape` morphs sine, triangle, saw, pulse (`O1 PW`; 50 % is a square). Oscillator 2 can hard-sync to 1 (`O2 sync`) and take FM from it (`FM 1 → 2`); oscillator 3 defaults to a sine an octave down. Ring modulation and noise sit in the mixer.
- Filter 1: ladder 24, ladder 12 or state-variable (low-pass, band-pass, high-pass, notch), with drive, envelope amount (+-6 octaves), key tracking and velocity. Filter 2: state-variable, serial or parallel.
- Amp, filter and mod envelopes, two LFOs, an 8-slot matrix (sources: LFOs, envelopes, velocity, key, wheel, aftertouch; destinations: pitch, shapes, levels, filters, amp, pan).
- Every control has a How and a Sound sentence in `machine_describe`; `param_set` takes the name or the id. New to subtractive synthesis? Neo Learn Subtractive has 12 controls and the same Explain strip.

## Use it for

- Pads, basses, leads, plucks, brass, keys and synthesised percussion.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| O1 shape | 2 | 0 sine, 1 triangle, 2 saw, 3 pulse |
| F1 cutoff | 2 kHz | 1-3 kHz for pads; open it for leads |
| F1 reso | 20% | the ladder self-oscillates near 100 % |
| F1 env | 30% | positive opens the filter with each note (+-6 octaves) |
| Amp attack | 20 ms | 0.3-1.5 s for pads |
| Amp release | 400 ms | 1-3 s for pads |
| Unison | 2 | 2-4 for pads and wide leads; 1 keeps basses mono |
| Volume | -6 dB | unison and chords add up |

## Starting points

- Pads: "Pad — Warm Strings", "Pad — Glass", "Pad — Dark Choir", "Pad — Evolving PWM".
- Bass: "Bass — Classic Sub", "Bass — Acid", "Bass — Reese", "Bass — FM Growl". Leads: "Lead — Sync Scream", "Lead — Smooth Saw", "Lead — Square Chip".
- Keys and plucks: "Pluck — Bright", "Pluck — Marimba", "Keys — Poly Brass", "Keys — Electric Piano", "Keys — Organ". FX: "FX — Laser Drop".
- Vibrato that fades in: `LFO1 shape` Sine, `LFO1 rate` 5.5 Hz, `LFO1 fade` 500 ms, `M1 source` LFO 1, `M1 dest` Pitch, `M1 amount` +1 %.

## Pitfalls

- Matrix slots 1-4 come with sources set and the destination Off: set a destination and an amount to use them.
- Notes x unison voices must fit 32: with `Unison` 7 only 4 notes ring at once.
- `Volume` starts at -6 dB, and 7 unison voices add about 8.5 dB: check peaks on chords.
- A ladder at resonance near 100 % with `F1 key track` 100 % plays a sine at the note.
- `Drift` (15 %) moves pitch and cutoff per voice: set 0 % for drums, sub basses and exact A/B renders.

## Pairs well with

- Neo Filter for extra sweeps, Neo Ensemble for chorus, Neo Delay and Neo Verb for space, Neo Comp on basses, Neo Drive for grit, Neo Learn Subtractive to learn the ideas first.

## Successor

Neo Poly replaces Jeskola Bass, Bass 2 and Bass 3: `machine_replace(machine="Bass")` carries the settings over in their units.

## Checklist

- [ ] `preset_load(machine="Poly", preset="Pad — Warm Strings")`, then `notes_write` a chord and `render(probes=["Poly"])`.
- [ ] `machine_readings(machine="Poly")` gives `rms_db` and `peak_db`: `peak_db` stays under 0 dBFS on the loudest chord.
- [ ] `analyze(source="Poly", metrics=["spectrum"])`: the tone is as bright or dark as you set `F1 cutoff`.
- [ ] `midi_learn(machine="Poly", parameter="F1 cutoff")` binds a controller, then `midi_bindings_list(machine="Poly")` shows it.
