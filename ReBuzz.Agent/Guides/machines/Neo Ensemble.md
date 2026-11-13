## How it sounds

Three modulation effects on one LFO: a multi-voice chorus, a flanger and a phaser. All of them are zero latency, and the feedback is bounded to +-99 %, so a sweep rings and levels off but never runs away.

| Mode | What moves | Sound | Use it for |
|---|---|---|---|
| Chorus | 1-4 delayed voices around `Delay` | thicker, wider, a gentle pitch wobble | pads, keys, guitars, strings, backing vocals |
| Flanger | one short delay with feedback | a comb of notches sweeping up and down; with feedback, the jet | drums, hats, leads, risers |
| Phaser | 2-12 allpass stages around `Centre` | `Stages` / 2 soft notches moving through the spectrum | keys, pads, guitars, stabs |

The LFO runs at `Rate` in Hz or follows the tempo with `Sync`; `Spread` offsets the right channel's LFO (0 degrees moves both alike, 180 opposite). Every control has a How and a Sound sentence in `machine_describe`; `param_set` takes the name or the id.

## Use it for

- Thickening and widening a pad or guitar (chorus), jet sweeps on drums (flanger), swirl on keys (phaser), and vibrato at `Mix` 100 %.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Mode | Chorus | Flanger and Phaser use the same controls |
| Rate | 0.6 Hz | slow for a chorus; 5-6 Hz with `Mix` 100 % for vibrato |
| Depth | 35% | the sweep: pitch wobble grows with rate and depth |
| Delay | 9 ms | Chorus and Flanger; 1-2 ms for a jet flanger |
| Spread | 120° | 90-180 degrees for width |
| Feedback | 50% | Flanger and Phaser; negative is hollower |
| Mix | 50% | the deepest comb or notches; 100 % is only the wet |

## Starting points

- "Chorus — Classic", "Chorus — Wide Stereo", "Chorus — Subtle Doubling", "Ensemble — Strings", "Ensemble — Thick (3 Voices)".
- "Flanger — Slow Jet", "Flanger — Metallic", "Phaser — 4-Stage Classic", "Phaser — 8-Stage Deep", "Phaser — Slow Sweep (4 Bars)".
- "Vibrato — Pitch Wobble" (wet only, 5.5 Hz). Lush pad chorus by hand: 3 `Voices`, `Rate` 0.7 Hz, `Depth` 35 %, `Delay` 9 ms, `Tone` 9 kHz, `Mix` 55 %.

## Pitfalls

- `Delay` and `Voices` apply to Chorus and Flanger; `Centre` and `Stages` to Phaser; `Rate` is ignored while `Sync` is not Free.
- High positive `Feedback` with a long `Delay` rings like a short echo: lower `Tone` (4-8 kHz) to darken it.
- `Mix` is a straight crossfade: 50 % on bright material sits about 3 dB under the input. Four chorus voices lift the wet's lows by about 6 dB: trim `Output`.
- A wide chorus on a bass loses its low end in mono: check with `analyze`.

## Pairs well with

- Neo Poly, Neo Wave or Neo Sampler pads before it; Neo Verb after it; Neo Delay for echoes of the swept sound; Neo EQ to keep the lows out of the wet.

## Successor

Neo Ensemble replaces Jeskola Chorus, Jeskola Flanger, FSM Chorus2 and Pedal Chorus: `machine_replace(machine="Chorus")` carries the settings over in their units.

## Checklist

- [ ] `machine_add(gear="Neo Ensemble", name="Chorus", insert_after="Pad")`, then `preset_load(machine="Chorus", preset="Chorus — Classic")`.
- [ ] `analyze(source="Chorus", metrics=["stereo"])`: the correlation falls as `Spread` rises, and stays positive in the lows.
- [ ] `machine_readings(machine="Chorus")` gives `rms_db` and `peak_db`: level about matches the dry sound; trim `Output` if not.
- [ ] `compare` a bypassed render with the processed one at the same loudness.
