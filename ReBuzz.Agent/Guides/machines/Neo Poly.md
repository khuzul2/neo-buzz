## How it sounds

A polyphonic analogue-style synth: warm saws and pulses through a resonant ladder filter (24 or 12 dB per octave, self-oscillating at the top) or a clean state-variable filter. Detuned unison and per-voice `Drift` make it wide and alive. The oscillators are band-limited, so bright leads and sync sweeps stay clean.

- Oscillators 1-3: each `shape` morphs sine, triangle, saw, pulse (`O1 PW`; 50 % is a square). Oscillator 2 can hard-sync to 1 (`O2 sync`) and take FM from it (`FM 1 → 2`); oscillator 3 defaults to a sine an octave down. Ring modulation and noise sit in the mixer.
- Filter 1: ladder 24, ladder 12, state-variable (low-pass, band-pass, high-pass, notch) or Diode 18, with drive, envelope amount (+-6 octaves), key tracking and velocity. Filter 2: state-variable, serial or parallel.
- Acid: Diode 18 is a three-pole (18 dB per octave) diode ladder that squelches, keeps more bass than the 24 dB ladder as the resonance rises and self-oscillates at the top. `Accent` (with `Accent threshold`, `Accent decay`, `Accent sweep` and `Accent level`) makes hard notes open the filter further, snap shut faster and play louder, and a run of accents swells. `Slide` makes a note that overlaps the one before glide in `Slide time` without retriggering (Mono and Legato voice modes).
- Amp, filter and mod envelopes, two LFOs, an 8-slot matrix (sources: LFOs, envelopes, velocity, key, wheel, aftertouch; destinations: pitch, shapes, levels, filters, amp, pan).
- Every control has a How and a Sound sentence in `machine_describe`; `param_set` takes the name or the id. New to subtractive synthesis? Neo Learn Subtractive has 12 controls and the same Explain strip.

## Use it for

- Pads, basses, leads, plucks, brass, keys and synthesised percussion.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| O1 shape | 2 | 0 sine, 1 triangle, 2 saw, 3 pulse |
| F1 cutoff | 2 kHz | 1-3 kHz for pads; open it for leads |
| F1 reso | 20% | the ladders self-oscillate near 100 % |
| F1 env | 30% | positive opens the filter with each note (+-6 octaves) |
| Amp attack | 20 ms | 0.3-1.5 s for pads |
| Amp release | 400 ms | 1-3 s for pads |
| Unison | 2 | 2-4 for pads and wide leads; 1 keeps basses mono |
| Volume | -6 dB | unison and chords add up |

## Starting points

- Pads: "Pad — Warm Strings", "Pad — Glass", "Pad — Dark Choir", "Pad — Evolving PWM".
- Bass: "Bass — Classic Sub", "Bass — Acid", "Bass — Reese", "Bass — FM Growl". Acid (Diode 18, accents and slides): "Acid — Squelch", "Acid — Rubber", "Acid — Screamer", "Acid — Deep", "Acid — Plucky", "Acid — Sub". Leads: "Lead — Sync Scream", "Lead — Smooth Saw", "Lead — Square Chip".
- Keys and plucks: "Pluck — Bright", "Pluck — Marimba", "Keys — Poly Brass", "Keys — Electric Piano", "Keys — Organ". FX: "FX — Laser Drop".
- Vibrato that fades in: `LFO1 shape` Sine, `LFO1 rate` 5.5 Hz, `LFO1 fade` 500 ms, `M1 source` LFO 1, `M1 dest` Pitch, `M1 amount` +1 %.

## Pitfalls

- Matrix slots 1-4 come with sources set and the destination Off: set a destination and an amount to use them.
- Notes x unison voices must fit 32: with `Unison` 7 only 4 notes ring at once.
- `Volume` starts at -6 dB, and 7 unison voices add about 8.5 dB: check peaks on chords.
- A ladder at resonance near 100 % with `F1 key track` 100 % plays a sine at the note.
- `Accent threshold` is 110 and a note written without a velocity plays at 100, so plain notes stay plain: write 110 or more (127 is usual) on the notes to accent. Lower the threshold to accent more of a line.
- `Accent` and its companions do nothing at their defaults (0 %, `Accent decay` at its top, 0 dB); `Slide` is off. A song made before they existed sounds as it did. Overlap slides need Mono or Legato voice mode; in Poly mode only a note written with `slide: true` (notes_write; the piano roll's Slide button) glides in, at `Slide time` whatever the glide mode, while `Slide` is on. Accents act on filter 1 only.
- An acid patch (it uses Slide, Accent, Accent sweep, Accent level or Diode 18) keeps its notes clean, and four controls set the bounds: `Filter range` (the soft ceiling filter 1's cutoff approaches, default 10 kHz), `Sweep limit` (the most a run of accents can swell the filter, 50 % of 3 octaves), `Soft start` (a smooth onset and the shortest release, 3.5 ms; it also sets how fast the cutoff may rise, 120 octaves a second at the default, and how smoothly it turns, so an accent opens over about 10 to 25 ms) and `Filter quality` (Eco 1x, High 2x, Ultra 4x oversampling of the filter, which adds 0, 15 or 23 samples of delay to it). The defaults are the clean ones, and 18 kHz is clean too: lower `Soft start` to 0.5 ms, set `Sweep limit` to 100 % or choose Eco for a harder line and clicks can come back, by your choice. Other patches play exactly as before.
- The editor's Full | Acid switch: the Acid view shows Tuning (O1 fine), Cutoff, Resonance, Env mod (F1 env), Decay (filter decay) and Accent as big knobs, the Waveform (saw or square on O1 shape), Slide and Slide time, and the four limits; they are the same parameters as in Full. A patch that uses the acid controls opens in the Acid view.
- `Drift` (15 %) moves pitch and cutoff per voice: set 0 % for drums, sub basses and exact A/B renders.

## Pairs well with

- Neo Filter for extra sweeps, Neo Ensemble for chorus, Neo Delay and Neo Verb for space, Neo Comp on basses, Neo Drive for grit, Neo Learn Subtractive to learn the ideas first.

## Checklist

- [ ] `preset_load(machine="Poly", preset="Pad — Warm Strings")`, then `notes_write` a chord and `render(probes=["Poly"])`.
- [ ] `machine_readings(machine="Poly")` gives `rms_db` and `peak_db`: `peak_db` stays under 0 dBFS on the loudest chord.
- [ ] `analyze(source="Poly", metrics=["spectrum"])`: the tone is as bright or dark as you set `F1 cutoff`.
- [ ] `midi_learn(machine="Poly", parameter="F1 cutoff")` binds a controller, then `midi_bindings_list(machine="Poly")` shows it.
