## How it sounds

A wavetable synth: two oscillators read through tables of 64 frames that morph smoothly, from glassy and vocal to hard and digital. Warps bend how each cycle is read, so one table gives many timbres. It runs at 2x with a clean decimator, so harmonics stay clean up to 18 kHz.

| Table | Position 0 % to 100 % |
|---|---|
| Basic | sine, triangle (33 %), saw (67 %), square |
| PWM | pulse width 50 % down to 3 % |
| Formants | vowels A, E, I, O, U |
| Sync sweep, FM sweep | hard-synced saw, ratio 1 to 8; FM 1:1, index 0 to 6 |
| Harmonics, Organ, Resonant, Digital, Grit | more saw harmonics; drawbars; a resonant sweep; stepped saw; seeded spectra |
| User | your table, from a WAV |

Warps: Bend+ and Bend- (brighter, vocal), Squeeze (pulse-width movement for any wave), Mirror (hollow), Asym (phase distortion), Window sync (hard-sync sounds), PM from B (oscillator A only). Every control has a How and a Sound sentence in `machine_describe`; `param_set` takes the name or the id.

## Use it for

- Evolving pads, wavetable sweeps, digital leads and modern basses; movement comes from modulating the position.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| A table | Formants | the family of tones; B has its own table |
| A position | 40% | morphs the tone; modulate it for movement |
| A warp amt | 0% | adds harmonics with a chosen `A warp` |
| Cutoff | 8 kHz | 20 Hz to 20 kHz |
| Reso | 15% | ring at the cutoff |
| Unison | 3 | 2-4 for pads |
| Sub level | 0% | a sine or square one or two octaves down |
| Volume | -6 dB | check `peak_db` with drive |

## Starting points

- "Init — Basic" (a saw), "Pad — Vowel Choir", "Pad — Organ Drift", "Pad — Glass Harmonics", "Lead — Sync Scream", "Lead — Digital Steps".
- "Bass — Wave Sub", "Bass — FM Growl", "Bass — Reso Pluck", "Pluck — PWM Keys", "Keys — Squeeze EP", "Texture — Grit Motion", "Texture — PM Bloom".
- A sweep: `Mod attack` 2 s, `Mod sustain` 100 %, `M3 source` Mod env, `M3 dest` A position, `M3 amount` +60 % (matrix amounts are fractions of the whole table).
- Your own table: `machine_data_import(machine="Wave", kind="wavetable", path="C:/Tables/mine.wav")`, then set `A table` to User.

## Pitfalls

- Freshly added it plays a sine (Basic at position 0 %): set `A position` 67 % for a saw.
- `Sub octave` takes the text "-1" or "-2", not a number.
- Tables are built in the background on first use; renders wait up to 5 s.
- Matrix slots 1-4 come with sources set and the destination Off.
- A ladder with heavy drive on a full table adds a lot of level: watch the peaks.

## Pairs well with

- Neo Filter and Neo Ensemble for movement, Neo Delay and Neo Verb for space, Neo Comp on basses; Neo Poly for classic analogue shapes, and Neo Learn Subtractive to learn filters and envelopes first.

## Checklist

- [ ] `param_set(machine="Wave", changes=[{"param": "A position", "value": "67%"}])`, then `render(probes=["Wave"])`.
- [ ] `machine_readings(machine="Wave")` gives `rms_db` and `peak_db`: `peak_db` under 0 dBFS with `Drive` up.
- [ ] `analyze(source="Wave", metrics=["spectrum"])`: the table's character shows, with no aliasing above the notes.
- [ ] `machine_describe(machine="Wave", group="Matrix")` shows each slot's source, destination and amount.
