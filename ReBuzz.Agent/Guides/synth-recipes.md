# Synth recipes: Neo Poly and Neo Wave

Recipes with exact values for the two synths that cover most sounds: pads, basses, leads, plucks, chords, drums and risers on Neo Poly, wavetable sweeps and growls on Neo Wave. Values are what `param_set` takes; each recipe starts from a freshly added machine (its defaults) or from the preset it names. How to read a sound and the effects that shape it are in `guide(topic="sound-design")`.

## Neo Poly

Three oscillators (shape morphs sine → triangle → saw → pulse; `O2 sync`, `FM 1 → 2`, ring), noise, filter 1 (ladder 24/12 or SVF), filter 2 (SVF, serial or parallel), amp, filter and mod envelopes, two LFOs, an 8-slot matrix. Up to 16 notes × 7 unison voices from a 32-voice pool.

Defaults when added: oscillator 1 saw only (O2 and O3 at 0 %), Ladder 24 at 2.52 kHz, resonance 15 %, filter envelope +30 %, amp 1.4 ms / 100 ms / 80 % / 59 ms, Volume −6 dB. O3 is a sine an octave down when you raise its level.

- `O1 shape` (and O2, O3): 0 sine, 1 triangle, 2 saw, 3 pulse (its PW; 50 % is a square); values between morph.
- `F1 env`, `F2 env`: ±100 % is ±6 octaves at the envelope's peak (+50 % = +3 octaves above the cutoff).
- `F1 key track` 100 % follows the keyboard one octave per octave; `F1 velocity` closes the filter on soft notes (up to 3 octaves).
- Matrix slots come with sources set (M1 LFO 1, M2 Mod wheel, M3 Mod env, M4 LFO 2) and dest Off: set the dest and the amount to use one.

| Destination | ±100 % amount means |
|---|---|
| Pitch, Osc n pitch | ±24 semitones (1 % = 24 cents: vibrato is 0.5–1 %) |
| F1 cutoff, F2 cutoff | ±6 octaves (10 % = 0.6 octave) |
| FM amount | ±4 (the `FM 1 → 2` index) |
| Osc n PW | ±48 points of pulse width |
| Osc n shape | ±3 (the whole sine → pulse range) |
| Levels, reso, Amp, Pan | ±100 % |
| LFO n rate | ±4 octaves of rate |

Sources: LFOs swing −1 … +1; envelopes, velocity, mod wheel and aftertouch run 0 … 1; Key is (note − 60) ÷ 60.

### Pad: warm analog

Close to the preset "Pad — Warm Strings"; the table says why.

| Parameter | Value | Why |
|---|---|---|
| O2 level | 80% | a second saw (both oscillators default to saw) |
| O2 fine | +7 ct | beating against oscillator 1 |
| Unison | 3 | three voices per note |
| Uni detune | 30% | ±15 cents between them |
| Uni spread | 70% | stereo width |
| F1 cutoff | 1.8 kHz | warm, not dull |
| F1 env | +20% | a slight bloom, +1.2 octaves at the peak |
| Flt attack | 800 ms | the bloom arrives with the swell |
| Flt decay | 2 s | |
| Flt sustain | 50% | |
| Amp attack | 600 ms | soft swell |
| Amp decay | 1.5 s | |
| Amp sustain | 85% | |
| Amp release | 1.8 s | tails overlap the next chord |
| LFO1 shape | Triangle | |
| LFO1 rate | 0.25 Hz | one sweep every 4 s |
| M1 dest | F1 cutoff | LFO 1 is already M1's source |
| M1 amount | +10% | ±0.6 octave of slow movement |
| Volume | -15 dB | unison voices and chords add up |

Darker: `F1 cutoff` 1 kHz. Brighter and airier: 3 kHz and `O2 octave` +1 oct. Slower: Amp attack 1.5 s.

### Supersaw chords and leads

| Parameter | Value | Why |
|---|---|---|
| Unison | 7 | the supersaw stack |
| Uni detune | 35% | ±17.5 cents |
| Uni spread | 80% | wide |
| O2 level | 60% | a second stack |
| O2 octave | +1 oct | brightness without more detune |
| F1 type | Ladder 12 | gentler slope, brighter |
| F1 cutoff | 6 kHz | |
| F1 reso | 10% | |
| F1 env | +15% | |
| Amp attack | 5 ms | |
| Amp sustain | 90% | |
| Amp release | 350 ms | |
| Drift | 20% | |
| Volume | -14 dB | seven voices per note sum loud |

As a mono lead: `Voice mode` Legato, `Glide mode` Legato, `Glide` 60 ms. Voices: notes × unison must fit 32; with Unison 7, at most 4 notes ring.

### Chord stab (house, garage)

| Parameter | Value | Why |
|---|---|---|
| O2 shape | 3 | a square under the saw |
| O2 level | 60% | |
| O2 fine | +5 ct | |
| F1 cutoff | 600 Hz | closed between hits |
| F1 reso | 20% | |
| F1 env | +45% | +2.7 octaves at the hit |
| Flt attack | 0.5 ms | |
| Flt decay | 250 ms | the "pew" of the stab |
| Flt sustain | 0% | |
| F1 velocity | 50% | harder hits open more |
| Amp attack | 1 ms | |
| Amp decay | 400 ms | |
| Amp sustain | 0% | |
| Amp release | 150 ms | |
| Volume | -10 dB | |

Play minor 7th or 9th chords in C3–C5 on an offbeat rhythm; send to a plate.

### Sub bass

| Parameter | Value | Why |
|---|---|---|
| Voice mode | Mono | one note at a time, no overlaps |
| O1 shape | 0 | pure sine |
| F1 type | SVF LP | a clean filter |
| F1 cutoff | 2 kHz | out of the way of a sine |
| F1 env | 0% | |
| F1 key track | 0% | |
| Amp attack | 3 ms | no click |
| Amp sustain | 100% | |
| Amp release | 40 ms | no click, no overlap |
| Drift | 0% | stays in tune |
| Vel → amp | 20% | even level |
| Volume | -3 dB | |

Write it E1–E2. Check `analyze(metrics=["pitch"])` reads within ±5 cents and `stereo` reads mono.

### Plucky bass (house, synthwave octaves)

| Parameter | Value | Why |
|---|---|---|
| Voice mode | Mono | |
| O3 level | 60% | the sine an octave below for weight |
| F1 cutoff | 250 Hz | |
| F1 reso | 25% | |
| F1 env | +45% | the pluck |
| Flt attack | 0.5 ms | |
| Flt decay | 180 ms | |
| Flt sustain | 10% | |
| Amp decay | 400 ms | |
| Amp sustain | 60% | |
| Amp release | 60 ms | |
| Volume | -6 dB | |

### Acid bass

Start from the preset "Bass — Acid" (Legato, Ladder 24 with resonance 80 %, drive 9 dB). Then:

| Parameter | Value | Why |
|---|---|---|
| F1 velocity | 60% | accents (velocity 120 against 80) open the filter more |
| Glide | 80 ms | slides |

Play 16ths in A1–A3. Accents: velocity 120 on 3–5 notes a bar, 80 elsewhere. Slides: let a note overlap the next (length 0.3 on a 0.25 step); Legato mode glides instead of retriggering. Sweep `F1 cutoff` from 200 Hz to 1.2 kHz over 16 bars with `automation_write`.

### Square pluck and arp (synthwave)

| Parameter | Value | Why |
|---|---|---|
| O1 shape | 3 | pulse |
| O1 PW | 50% | square |
| F1 cutoff | 1.2 kHz | |
| F1 env | +40% | |
| Flt attack | 0.5 ms | |
| Flt decay | 200 ms | |
| Flt sustain | 0% | |
| Amp attack | 0.5 ms | |
| Amp decay | 300 ms | about an 8th note at 100 BPM |
| Amp sustain | 0% | |
| Amp release | 200 ms | |
| Volume | -10 dB | |

Match the decay to the note spacing: an 8th lasts 30 ÷ BPM seconds (300 ms at 100 BPM).

### Brass

Start from "Keys — Poly Brass": two saws, filter envelope attack 60 ms (the swell), Drift 25 %. Swell harder with `Flt attack` 120 ms and `F1 env` +55%; use it for stabs with Amp release 250 ms.

### Drum kit

One machine per voice (Kick, Snare, Clap, Hat, Open hat), each from a fresh Neo Poly.

Kick (play A1, 55 Hz; tune to the key between F#1 and C2):

| Parameter | Value | Why |
|---|---|---|
| Voice mode | Mono | |
| O1 shape | 0 | sine body |
| F1 type | SVF LP | |
| F1 cutoff | 20 kHz | filter out of the way |
| F1 env | 0% | |
| F1 key track | 0% | |
| M3 source | Mod env | |
| M3 dest | Pitch | the pitch sweep |
| M3 amount | +100% | starts 24 semitones up (220 Hz for A1) |
| Mod attack | 0.5 ms | |
| Mod decay | 120 ms | shorter: clickier; longer: a zap |
| Mod sustain | 0% | lands on the note |
| Amp attack | 0.5 ms | |
| Amp decay | 350 ms | the boom's length |
| Amp sustain | 0% | |
| Amp release | 100 ms | |
| Drift | 0% | |
| Vel → amp | 30% | |
| Volume | 0 dB | |

Snare (play G3, 196 Hz):

| Parameter | Value | Why |
|---|---|---|
| O1 shape | 1 | triangle body |
| Noise | 70% | the snares |
| Noise colour | 0% | white |
| F1 type | SVF LP | |
| F1 cutoff | 9 kHz | |
| F1 env | 0% | |
| F1 key track | 0% | |
| M3 source | Mod env | |
| M3 dest | Pitch | |
| M3 amount | +20% | a 4.8-semitone snap down |
| Mod attack | 0.5 ms | |
| Mod decay | 40 ms | |
| Mod sustain | 0% | |
| Amp attack | 0.5 ms | |
| Amp decay | 200 ms | |
| Amp sustain | 0% | |
| Amp release | 80 ms | |
| Drift | 0% | |

Clap: write each clap as three notes 0.02 beats apart (about 10 ms), velocities 90, 100, 115.

| Parameter | Value | Why |
|---|---|---|
| Polyphony | 4 | the three bursts overlap |
| O1 level | 0% | noise only |
| Noise | 100% | |
| Noise colour | 30% | |
| F1 type | SVF BP | the clap's band |
| F1 cutoff | 1.2 kHz | |
| F1 reso | 25% | |
| F1 env | 0% | |
| F1 key track | 0% | |
| Amp attack | 0.5 ms | |
| Amp decay | 160 ms | |
| Amp sustain | 0% | |
| Amp release | 60 ms | |

Closed hat (any pitch; velocity carries the groove):

| Parameter | Value | Why |
|---|---|---|
| O1 level | 0% | |
| Noise | 100% | |
| Noise colour | 0% | |
| F1 type | SVF HP | |
| F1 cutoff | 7 kHz | |
| F1 reso | 20% | a little metal |
| F1 env | 0% | |
| F1 key track | 0% | |
| Amp attack | 0.5 ms | |
| Amp decay | 60 ms | |
| Amp sustain | 0% | |
| Amp release | 40 ms | |
| Vel → amp | 90% | |

Open hat: the same on its own machine with `Amp decay` 400 ms, `Amp release` 250 ms and `F1 cutoff` 6 kHz.

Check each hit: `analyze(metrics=["pitch", "spectrum", "dynamics"])` on a solo render. The kick reads near 55 Hz with most energy in `sub` and `low`; hats have their centroid above 6 kHz.

### Riser

Start from "FX — Noise Sweep" (band-passed noise). For a build, replace its LFO with automation: `automation_write(machine="Riser", pattern="Build", param="F1 cutoff", points=[{"at": 0, "value": "300 Hz"}, {"at": 32, "value": "10 kHz"}])` and set `M1 dest` Off.

## Neo Wave

Two table oscillators (A, B), a sub, noise, one filter, three envelopes, two LFOs and an 8-slot matrix (sources set as in Neo Poly; dests A/B pitch, position, warp, level, Sub and Noise level, Cutoff, Reso, Amp, Pan, LFO rates).

Defaults when added: `A table` Basic at position 0 % (a sine), B silent, Ladder 24 at 3.56 kHz, filter envelope +20 %, Volume −6 dB.

| Table | Position 0 % → 100 % |
|---|---|
| Basic | sine → triangle (33 %) → saw (67 %) → square |
| PWM | pulse width 50 % → 3 % |
| Harmonics | more and more saw harmonics |
| Formants | vowels A → E → I → O → U |
| Sync sweep | hard-synced saw, ratio 1 → 8 |
| FM sweep | FM 1:1, index 0 → 6 |
| Organ | drawbar registrations |
| Resonant | a saw through a resonant low-pass sweeping up |
| Digital | stepped saw, 4 → 256 steps |
| Grit | seeded spectra, morphing |
| User | a table loaded with `machine_data_import` |

- Warps: Bend+ and Bend− (shift the cycle's centre), Squeeze, Mirror, Asym, Window sync, PM from B (oscillator A only).
- Matrix position amounts move across the whole table: +50 % is half the table.
- `Sub octave` takes the text "-1" or "-2".
- Your own table: `machine_data_import(machine="Wave", kind="wavetable", path="C:/Tables/vowels.wav")` (frames of 2048 samples, or one cycle), then set `A table` to User.

### Pad: slow wavetable sweep

| Parameter | Value | Why |
|---|---|---|
| A table | Harmonics | |
| A position | 10% | starts mellow |
| B table | Formants | |
| B position | 40% | |
| B level | 50% | |
| B fine | +6 ct | |
| Unison | 3 | |
| Uni detune | 25% | |
| Uni spread | 80% | |
| Filter type | SVF LP | |
| Cutoff | 6 kHz | |
| Filter env | 0% | the tables do the movement |
| Amp attack | 1.2 s | |
| Amp sustain | 90% | |
| Amp release | 2.5 s | |
| Mod attack | 4 s | |
| Mod sustain | 100% | |
| M3 source | Mod env | |
| M3 dest | A position | sweeps A from 10 % to 70 % over 4 s |
| M3 amount | +60% | |
| M4 source | LFO 2 | |
| M4 dest | B position | slow drift of the vowel |
| M4 amount | +30% | |
| LFO2 rate | 0.08 Hz | one cycle every 12.5 s |
| Volume | -12 dB | |

### Tempo-synced sweep (basses and arps)

| Parameter | Value | Why |
|---|---|---|
| Voice mode | Mono | |
| A table | Resonant | |
| A position | 20% | |
| Sub level | 50% | weight under the sweep |
| Filter type | Ladder 24 | |
| Cutoff | 2.5 kHz | |
| Filter env | +20% | |
| LFO1 shape | Saw down | a sweep that restarts every step |
| LFO1 sync | 1/8 | |
| LFO1 retrig | On | each note starts the sweep |
| M1 source | LFO 1 | |
| M1 dest | A position | |
| M1 amount | +45% | |
| Amp sustain | 100% | |
| Amp release | 80 ms | |
| Volume | -6 dB | |

### Wobble and growl bass

| Parameter | Value | Why |
|---|---|---|
| Voice mode | Mono | |
| A table | FM sweep | |
| A position | 20% | |
| Sub level | 60% | |
| Sub octave | -1 | |
| Filter type | Ladder 12 | |
| Cutoff | 1.2 kHz | |
| Reso | 30% | |
| Drive | 9 dB | |
| Filter env | 0% | |
| LFO1 shape | Sine | |
| LFO1 sync | 1/8 | the wobble rate |
| M1 source | LFO 1 | |
| M1 dest | A position | |
| M1 amount | +45% | |
| M2 source | LFO 1 | |
| M2 dest | Cutoff | the "wow" |
| M2 amount | +25% | ±1.5 octaves |
| Amp sustain | 100% | |
| Amp release | 60 ms | |
| Volume | -4 dB | |

Change the wobble rate per bar with step automation of `LFO1 sync` (1/8, 1/16, 1/4T). Keep the sub clean: Neo Drive after it in parallel (see Neo Drive below), lows mono.

### Talking lead (vowels)

| Parameter | Value | Why |
|---|---|---|
| Voice mode | Legato | |
| Glide mode | Legato | |
| Glide | 60 ms | |
| A table | Formants | |
| A position | 50% | the LFO swings it across all five vowels |
| Filter type | SVF LP | |
| Cutoff | 5 kHz | |
| Filter env | 0% | |
| LFO1 shape | Triangle | |
| LFO1 sync | 1/4 | |
| M1 source | LFO 1 | |
| M1 dest | A position | |
| M1 amount | +50% | |
| M2 source | Mod wheel | |
| M2 dest | A position | |
| M2 amount | +40% | |
| Amp attack | 10 ms | |
| Amp release | 200 ms | |
| Volume | -8 dB | |

### More Neo Wave starting points

- Digital bell pluck: "Pluck — Mirror Bell" (Harmonics with the Mirror warp, FM sweep an octave up).
- Organ pad with drift: "Pad — Organ Drift" (two Organ positions, slow LFO on position).
- Moving texture: "Texture — Grit Motion" (Grit table, band-pass, two LFOs).
- Electric piano from a table: "Keys — Squeeze EP" (velocity drives the Squeeze warp).

## Checklist

- [ ] The recipe loaded as written: `preset_list(machine=…)` names the preset and `param_get(machine=…)` reads the values back.
- [ ] It is in tune: `analyze(source=…, metrics=["pitch"])` reads within ±10 cents (±5 for basses), unless detune is the point.
- [ ] It doesn't clip alone or in chords: `render(solo=[…])` reports no clipped samples and a true peak below -1 dBTP; lower `Volume` with `param_set` if not.
- [ ] A bass or kick is mono below about 120 Hz: `analyze(source=…, metrics=["stereo"])` shows `sub` and `low` correlation above 0.9.
