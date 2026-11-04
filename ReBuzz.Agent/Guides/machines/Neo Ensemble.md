## How it sounds

Three modulation effects on one LFO:

| Mode | What moves | Sound | Use it for |
|---|---|---|---|
| Chorus | 1–4 delayed voices, swept an octave either side of `Delay` at 100 % `Depth` | thicker, wider, a gentle pitch wobble (the default: about 10 cents) | pads, keys, guitars, strings, backing vocals |
| Flanger | one short delay with feedback, swept two octaves either side | a comb of notches sweeping up and down; with feedback, ringing peaks: the "jet" | drums, hats, leads, risers |
| Phaser | 2–12 allpass stages, their frequency swept three octaves either side of `Centre` | `Stages` ÷ 2 soft notches moving through the spectrum; with feedback, vocal peaks | keys, pads, guitars, synth stabs |

Measured: the phaser's notches sit where theory puts them (within 0.0001 %), the flanger's within 0.5 % of (k + ½)/`Delay` (the lowest a few hertz higher), and the chorus's pitch wobble within 3 % of its formula.

`Feedback` reaches ±99 % and can't run away: what goes round saturates softly above full scale (it never passes +6 dBFS) and the loop's gain is below 0.99 at every frequency, so at full feedback the sound rings and levels off, and dies away when the input stops.

## Signal flow

Input → (Chorus, Flanger) a delay line read by `Voices` voices at delays swept around `Delay`, or (Phaser) `Stages` allpass stages swept around `Centre` → `Tone` (a one-pole high cut; open at its top) → back through the saturator and `Feedback` → `Mix` against the dry signal → `Output`. The LFO: `Rate` in Hz, or `Sync` to the song's tempo (`1/4` is one cycle per beat); `Shape` sine or triangle; `Spread` offsets the right channel's LFO (0° mono movement, 180° opposite). Zero latency; at `Mix` 0 % the input passes bit for bit.

## How to use it

- As an insert right after the sound: `machine_add(gear="Neo Ensemble", name="Chorus", insert_after="Pad")`, then a preset (`preset_load(machine="Chorus", preset="Chorus — Classic")`) or a recipe below.
- `Mix` 50 % is the classic sound (dry and wet equal: the deepest comb or notches). 100 % is vibrato (only the moving copy).
- Depth and rate trade off: the pitch wobble grows with both. For a chorus that stays in tune, keep `Rate` × `Depth` small (0.5 Hz at 30 %); for vibrato, `Mix` 100 % and 5–6 Hz.
- Tempo-synced sweeps: `Sync` 1 bar, 2 bars or 4 bars for flangers and phasers that move with the song.
- Wider: `Spread` 90–180°. Check the mix in mono afterwards (`analyze(metrics=["stereo"])`): a wide chorus on a bass loses its low end in mono.

Lush chorus on a pad:

| Parameter | Value | Why |
|---|---|---|
| Mode | Chorus | |
| Voices | 3 | |
| Rate | 0.7 Hz | |
| Depth | 35% | |
| Delay | 9.0 ms | |
| Spread | 120° | |
| Tone | 9.0 kHz | darker, as an analog chorus |
| Mix | 55% | |

Jet flanger on drums:

| Parameter | Value | Why |
|---|---|---|
| Mode | Flanger | |
| Sync | 2 bars | one sweep every two bars |
| Depth | 80% | |
| Delay | 1.6 ms | the comb's first notch sweeps about 100 Hz – 950 Hz |
| Feedback | 70.0% | the ringing peaks; negative gives a hollower sound |
| Mix | 50% | |

Phaser on keys:

| Parameter | Value | Why |
|---|---|---|
| Mode | Phaser | |
| Stages | 8 | four notches |
| Rate | 0.3 Hz | |
| Depth | 60% | |
| Centre | 800 Hz | |
| Feedback | 50.0% | |
| Mix | 50% | |

## Gotchas

- `Delay` and `Voices` apply to Chorus and Flanger; `Centre` and `Stages` to Phaser.
- High positive `Feedback` with a long `Delay` rings like a short echo: lower `Tone` (4–8 kHz) to darken it.
- With `Feedback` near ±99 % a loud input reaches the saturator: the peaks round off. Lower `Output` or the input if it's too hot.
- `Mix` is a straight crossfade: at 50 % on bright material the dry and the moving wet add in power, about 3 dB under the input; the lows (where they agree) stay level. Raise `Output` if it matters.
- More voices add up in the lows (four voices lift the wet's lows about 6 dB): trim `Output`.
- `Rate` is ignored while `Sync` is not Free.

## Preset map

| Preset | What |
|---|---|
| "Chorus — Classic" | 2 voices, 0.6 Hz, 7 ms |
| "Chorus — Wide Stereo" | 2 voices, right channel opposite |
| "Chorus — Subtle Doubling" | 1 voice at 22 ms, barely moving |
| "Chorus — Poly Synth I" | triangle LFO, short delay, deep: an 80s polysynth chorus |
| "Ensemble — Strings" | 4 voices, dark, for string machines and pads |
| "Ensemble — Thick (3 Voices)" | 3 voices, faster |
| "Flanger — Slow Jet" | 0.08 Hz, feedback 70 % |
| "Flanger — Tape Style (Negative)" | the delay sweeping close to zero, negative feedback |
| "Flanger — Metallic" | feedback 92 %, ringing |
| "Phaser — 4-Stage Classic" | 2 notches, gentle |
| "Phaser — 8-Stage Deep" | 4 notches, feedback 55 % |
| "Phaser — Slow Sweep (4 Bars)" | synced to the tempo |
| "Rotary — Leslie-like Fast" | 6.7 Hz, opposite channels |
| "Vibrato — Pitch Wobble" | wet only, 5.5 Hz |
