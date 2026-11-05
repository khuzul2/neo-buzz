# Sound design

How to describe a sound in numbers, then recipes with exact values for the Neo synths and effects. Values are what `param_set` takes. Each recipe starts from a freshly added machine (its defaults) or from the preset it names. They are starting points: render, measure, adjust.

## The loop

1. Gear by role:

   | Sound | Gear | Start from |
   |---|---|---|
   | Analog pads, basses, leads, plucks, brass, synth drums | Neo Poly | a recipe below or a "Pad —", "Bass —", "Lead —" preset |
   | Evolving pads, wavetable sweeps, digital leads, growl basses | Neo Wave | a recipe below or a Neo Wave preset |
   | Electric pianos, bells, mallets, FM basses, organs, metallic tones | Neo FM6 | "Keys — Tine Piano", "Bell — Glass Bell", "Init — Sine" |
   | Saturation, compression and space as part of the sound | Neo Drive, Neo Comp, Neo Verb | the effect recipes below |
   | Movement and width: chorus, ensemble, flanger, phaser, vibrato | Neo Ensemble | the recipes below or a "Chorus —", "Flanger —", "Phaser —" preset |
   | Echoes: tempo delays, ping-pong, dub and tape echo, slapback, combs | Neo Delay | the recipes below or a "Mix —", "Dub —", "Texture —" preset |
   | Filter movement: sweeps, auto-wah, dub filters, resonant and synced filters | Neo Filter | the recipes below or a "Sweep —", "Auto-Wah —", "LFO —" preset |

2. `machine_add(gear="Neo Poly", name="Pad")`, then `preset_list(machine="Pad", search="pad")` and `preset_load(machine="Pad", preset="Pad — Warm Strings")`, or apply a recipe with `param_set`.
3. An audition pattern in the sound's register: `pattern_create(machine="Pad", name="Audition", length_beats=8)`, `notes_write(machine="Pad", pattern="Audition", notes=[{"pitch": "A3", "start": 0, "length": 4}, {"pitch": "C4", "start": 0, "length": 4}, {"pitch": "E4", "start": 0, "length": 4}, {"pitch": "G4", "start": 4, "length": 0.5}])`. Place it with `arrangement_place`: at 0 in an empty song, otherwise after the song's end. Placing it there moves the song end, so note the length from `studio_status` first; when done, `arrangement_clear(machine="Pad", from=…, to=…)` and `song_set(song_end_beats=…)` put the song back.
4. `render(from=…, to=…, solo=["Pad"], tail_seconds=3)`; solo keeps the effects the machine feeds.
5. `analyze(source="r1", metrics=["spectrum", "dynamics", "pitch", "stereo"])`. For one moment of the sound, slice it: `analyze(source="r1", metrics=["spectrum"], from_seconds=0.5, to_seconds=1.5)`. Look at it with `plot(kind="spectrogram", sources=["r1"])`; match a reference sound with `compare(a="r1", b="C:/Refs/sound.wav")`.
6. One idea per `param_set`, then render and measure again. `snapshot_save(name="pad A")` versions worth keeping, `snapshot_diff(a="pad A")` to see what changed, `undo` what measured worse.
7. Keep it: `preset_save(machine="Pad", name="Agent warm pad", comment="Two saws, slow filter bloom")`.

Test notes per role: sub bass E1–A1, bass A1–A2, pad chord A3 C4 E4, lead A4, pluck and bell C5, kick A1.

## Reading a sound

| Axis | Listen for | Measure | Main controls |
|---|---|---|---|
| Envelope | attack, decay, sustain, release; the transient | `dynamics`: `rms_over_time`, crest, transients per second | amp envelope, velocity |
| Spectrum | brightness, body, harshness, pitch | `spectrum`: centroid, tilt, `bands_db`; `pitch`: fundamental and cents | oscillator shapes, filter cutoff and envelope, FM, drive |
| Movement | what changes while a note holds | `rms_over_time` spread; spectrum of slices (from_seconds, to_seconds) | LFOs, mod envelope, mod matrix, drift |
| Space | width, depth, tail | `stereo`: correlation, width per band; tail length in `rms_over_time` | unison spread, pan modulation, Neo Verb |

Words to numbers (a solo render of the sound, full range):

| Word | Reads as | First move |
|---|---|---|
| Dark, muffled | centroid low for the role; tilt below −4.5 dB/oct | open the filter (`times`: 1.5–2), more filter envelope |
| Bright, harsh | `high_mid` or `presence` 3 dB or more over the slope | cutoff × 0.7, less resonance, Neo Drive Tone −20 % |
| Thin | `low` dips 4 dB or more under the slope | a sub oscillator an octave down |
| Boomy, muddy | `low` or `low_mid` 3 dB or more over the slope | high-pass (Neo Poly F2 HP 100–200 Hz), less sub |
| Punchy | crest 12 dB or more on hits; fast transient | attack under 2 ms, filter envelope decay 50–200 ms |
| Plucky | level falls 20 dB within 0.1–0.4 s | amp sustain 0 %, decay 100–400 ms |
| Soft, pad-like | slow rise | attack 0.2–2 s, release 0.5–3 s |
| Wide | correlation 0.2–0.7 | unison 3–7 with spread 60–90 % |
| Centred | correlation 0.95 or more | unison 1, or spread 0 % |
| Moving, evolving | level or centroid changes over 2–8 s | LFO 0.05–0.5 Hz on cutoff or table position |
| Metallic, bell-like | pitch confidence low, partials not harmonic | FM at non-integer ratios, ring modulation |
| Warm | tilt −3.5 to −5 dB/oct, gentle harmonics | Tape or Tube drive 3–8 dB, low-pass 2–4 kHz |

Targets by role (solo render of a typical phrase):

| Role | Register | Centroid | Envelope | Stereo correlation |
|---|---|---|---|---|
| Sub bass | E1–E2 (41–82 Hz) | under 150 Hz | attack 2–10 ms, held | 0.95 or more |
| Bass | E1–E3 | 150–700 Hz | attack 5 ms or less | 0.9 or more below 120 Hz |
| Pad | C3–G5 | 600–2500 Hz | attack 0.2–2 s, release 0.5–4 s | 0.2–0.7 |
| Lead | C4–C6 | 1–4 kHz | attack 1–20 ms | 0.6–1 |
| Pluck, arp | C4–C7 | 1–5 kHz | decay 100–500 ms, sustain 0 | 0.4–0.9 |
| Keys, electric piano | C3–C6 | 500–2500 Hz | decay 1–4 s | 0.5–0.9 |
| Bell | C5–C7 | 2–6 kHz | decay 1–6 s | 0.4–0.9 |
| Kick | 45–65 Hz fundamental | under 200 Hz plus a 2–5 kHz click | 200–500 ms | mono |
| Snare, clap | body 150–250 Hz, crack 2–5 kHz | 1–4 kHz | 100–250 ms | 0.8–1 |
| Hats | 6–14 kHz | over 6 kHz | 30–80 ms closed, 200–600 ms open | 0.7–1 |

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

## Neo FM6

A DX7 in DX7 units: six operators, the 32 algorithms, rate/level envelopes, DX7 SysEx banks.

- **Carriers** are heard; **modulators** set brightness. `OpN level` 0–99, about 0.75 dB per step near the top (8 steps ≈ 6 dB).
- **Ratio**: `OpN coarse` 0 = 0.5, 1–31 = ×1 … ×31; `OpN fine` adds 0–99 % of it (coarse 3, fine 50 = 3.5). Carrier 1 with a modulator at 1 sounds saw-like (all harmonics), at 2 square-like (odd harmonics); non-integer ratios are inharmonic (bells, metal).
- **Fixed**: `OpN mode` Fixed: coarse picks 1, 10, 100 or 1000 Hz, fine multiplies by ×1 … ×9.77; the operator ignores the note.
- **Envelope**: on a note the level moves to level 1 at rate 1, level 2 at rate 2, level 3 at rate 3 and holds; on release to level 4 at rate 4. Rate 99 is instant; about 6 rate steps halve or double a segment's time.
- **Velocity sensitivity** 0–7 per operator: on modulators, harder notes are brighter (the electric piano's bark). **Rate scaling** 0–7: higher notes decay faster (3–4 for pianos and plucks). Level scaling: break point 39 is C4.
- **Detune** −7 … +7, about 1 cent a step at A4. **Feedback** 0–7 on the algorithm's feedback operator; 6–7 approach a saw.
- `Algorithm` takes 1–32 as the DX7 numbers them. `Engine` Clean 2× (oversampled) or Raw 1× (the original's aliasing). `Tone`: a gentle low-pass, 20 kHz is open.
- A freshly added Neo FM6 (or "Init — Sine") is algorithm 1 with only operator 1 sounding: a sine. In algorithm 1, operator 2 modulates operator 1, so a two-operator patch only needs operator 2's level, ratio and envelope.

| Algorithm | Carriers | Modulation | Good for |
|---|---|---|---|
| 1 | 1, 3 | 2→1; 6→5→4→3, feedback on 6 | brass, strings, rich leads |
| 2 | 1, 3 | 2→1 with feedback on 2; 6→5→4→3 | leads, basses with bite |
| 5 | 1, 3, 5 | 2→1, 4→3, 6→5, feedback on 6 | electric pianos, mallets, bells: three two-operator voices |
| 16 | 1 | 2→1, 4→3→1, 6→5→1, feedback on 6 | one complex tone: leads, basses |
| 22 | 1, 3, 4, 5 | 2→1; 6→3, 4 and 5, feedback on 6 | brass sections, organs |
| 31 | 1–5 | 6→5, feedback on 6 | organ with one bright partial |
| 32 | 1–6 | none, feedback on 6 | additive organs, drawbars |

Read one operator at a time: `machine_describe(machine="Keys", group="Operator 2")`.

DX7 banks: `machine_data_import(machine="Keys", kind="dx7_bank", path="C:/DX7/rom1a.syx", item=11)` loads a voice into every parameter. In the original ROM1A bank: 1 BRASS 1, 4 STRINGS 1, 11 E.PIANO 1, 15 BASS 1, 22 MARIMBA, 26 TUB BELLS.

### Electric piano: the tine

Start from "Keys — Tine Piano": algorithm 5, feedback 6. Three two-operator pairs: 2→1 (body, operator 2 at velocity sensitivity 7 for the bark), 4→3 (operator 4 at ratio 14: the tine's ping, quieter on high notes through its level scaling), 6→5 (operator 6 with feedback for edge). Carriers 3 and 5 are detuned +3 and −3 for the chorus. Variations:

| Parameter | Value | Why |
|---|---|---|
| Op4 level | 82 | louder tine (the preset has 76) |
| Op2 level | 66 | more bark on hard notes (60) |
| Feedback | 4 | smoother edge (6) |
| Unison | 2 | width |
| Uni detune | 20% | ±5 cents |
| Tone | 8 kHz | darker, vintage |

Play velocities 60–110: the bark appears above about 100. Pair with Neo Drive "Tube — Glow" and a slow chorus-like unison for the classic stage sound.

### Bell (two operators, from "Init — Sine")

| Parameter | Value | Why |
|---|---|---|
| Op2 coarse | 3 | |
| Op2 fine | 50 | ratio 3.5: inharmonic, bell-like |
| Op2 level | 72 | brightness of the strike |
| Op2 rate 2 | 35 | the modulator fades: the bell darkens as it rings |
| Op2 level 2 | 0 | |
| Op2 rate 3 | 35 | |
| Op2 level 3 | 0 | |
| Op2 rate 4 | 30 | |
| Op2 vel sens | 5 | harder strikes are brighter |
| Op1 rate 2 | 28 | the carrier rings longer than the modulator |
| Op1 level 2 | 0 | |
| Op1 rate 3 | 28 | |
| Op1 level 3 | 0 | |
| Op1 rate 4 | 30 | rings on after release |
| Op1 vel sens | 2 | |

Other bell ratios: 1.41 (coarse 1, fine 41) for glassy, 7 for a high ping. For a richer bell use algorithm 5 and stack three pairs at different ratios: "Bell — Glass Bell".

### FM bass (two operators, from "Init — Sine")

| Parameter | Value | Why |
|---|---|---|
| Voice mode | Mono | |
| Op2 coarse | 1 | 1:1, saw-like; 2 for a hollow square |
| Op2 level | 80 | |
| Op2 rate 2 | 55 | the brightness drops after the attack |
| Op2 level 2 | 60 | |
| Op2 rate 3 | 40 | |
| Op2 level 3 | 45 | held brightness |
| Op2 rate 4 | 70 | |
| Op2 vel sens | 5 | |
| Op1 rate 4 | 70 | short release |
| Volume | -6 dB | |

Play E1–E3. Thicker: "Bass — Solid" (algorithm 5, a pair at ratio 0.5 under a pair at 1).

### Metallic percussion (fixed modulator, from "Init — Sine")

| Parameter | Value | Why |
|---|---|---|
| Op2 mode | Fixed | the modulator ignores the note |
| Op2 coarse | 2 | 100 Hz |
| Op2 fine | 50 | × 3.16: 316 Hz |
| Op2 level | 85 | |
| Op2 rate 2 | 60 | |
| Op2 level 2 | 0 | |
| Op2 rate 3 | 60 | |
| Op2 level 3 | 0 | |
| Op1 rate 2 | 45 | |
| Op1 level 2 | 0 | |
| Op1 rate 3 | 45 | |
| Op1 level 3 | 0 | |
| Op1 rate 4 | 45 | |

Each note gives a different metal, because the carrier moves and the modulator doesn't.

### Brass swell (from "Init — Sine")

| Parameter | Value | Why |
|---|---|---|
| Op2 level | 85 | |
| Op2 rate 1 | 55 | the brightness rises slower than the level: the brass "blat" |
| Op2 rate 2 | 50 | |
| Op2 level 2 | 88 | |
| Op2 rate 3 | 40 | |
| Op2 level 3 | 85 | held brightness |
| Op2 rate 4 | 60 | |
| Op1 rate 1 | 75 | |
| Op1 rate 4 | 60 | |
| LFO speed | 35 | about 6 Hz |
| LFO delay | 40 | vibrato comes in late |
| LFO pitch | 6 | |
| Unison | 2 | |
| Uni detune | 30% | |

A section: "Brass — Bright Section" (algorithm 22).

### Organ

Algorithm 32 makes all six operators carriers: six drawbars. Ratios for the drawbar footages:

| Drawbar | 16′ | 8′ | 5⅓′ | 4′ | 2⅔′ | 2′ | 1⅗′ | 1⅓′ | 1′ |
|---|---|---|---|---|---|---|---|---|---|
| Ratio | 0.5 | 1 | 1.5 | 2 | 3 | 4 | 5 | 6 | 8 |
| Coarse, fine | 0, 0 | 1, 0 | 1, 50 | 2, 0 | 3, 0 | 4, 0 | 5, 0 | 6, 0 | 8, 0 |

"Organ — Drawbars" uses 16′ to 2′; "Organ — Percussive" adds a fast-decaying partial at ratio 3. Each operator's output level is its drawbar: 99 full, about 8 steps per 6 dB.

## Effects as sound tools

Put them right after the sound: `machine_add(gear="Neo Drive", name="Bass drive", insert_after="Bass")`.

### Neo Drive

Modes: Tape (soft, emphasis, head bump), Tube (asymmetric: even harmonics, sags with level), Transistor (harder knee), Diode (asymmetric, edgy), Fold (wavefolder: new harmonics, best on simple waves). `Character` means: Tape emphasis, Tube sag, Transistor hardness, Diode asymmetry, Fold amount. Auto gain (on by default) keeps the level steady as the drive changes. Latency is 62 samples at every quality.

Bass warmth (insert):

| Parameter | Value | Why |
|---|---|---|
| Mode | Tape | |
| Drive | 9 dB | |
| Character | 50% | |
| Tone | -10% | keeps the added harmonics soft |

Bass grit in parallel (the lows stay clean in the dry path):

| Parameter | Value | Why |
|---|---|---|
| Mode | Transistor | |
| Drive | 18 dB | |
| Low cut | 150 Hz | nothing below 150 Hz is distorted |
| High cut | 8 kHz | no fizz |
| Mix | 35% | |

Lead fuzz:

| Parameter | Value | Why |
|---|---|---|
| Mode | Transistor | |
| Drive | 30 dB | |
| Character | 80% | |
| Low cut | 120 Hz | |
| High cut | 6 kHz | |
| Quality | 8× | high drive on a bright source aliases less |

West-coast timbre on a sine or triangle: "Fold — West Coast"; automate `Drive` from 6 dB to 24 dB for a sweep. Even-harmonic warmth on keys and pads: "Tube — Glow".

### Neo Comp

Shaping, not just level (the mixing guide covers control). Set the threshold so the hits get the reduction named; the rest is fixed.

Snappier transients (plucks, drums):

| Parameter | Value | Why |
|---|---|---|
| Mode | Clean | |
| Threshold | -24 dB | 4–6 dB of reduction on hits |
| Ratio | 4:1 | |
| Attack | 25 ms | lets the transient through |
| Release | 80 ms | |
| Knee | 3 dB | |
| Auto makeup | On | |

More body and sustain (drums, room):

| Parameter | Value | Why |
|---|---|---|
| Mode | FET | |
| Threshold | -30 dB | 8–12 dB of reduction |
| Ratio | 8:1 | |
| Attack | 0.1 ms | flattens the transient |
| Release | 60 ms | the tail comes up between hits |
| Character | 50% | FET colour |
| Makeup | 6 dB | |
| Mix | 50% | parallel: keeps the punch |

### Neo Verb

Pads and atmospheres take the reverb as part of the sound; mixes use it on a send (mixing guide).

Dark wash (100 % wet on a send):

| Parameter | Value | Why |
|---|---|---|
| Mode | Hall | |
| Decay | 12 s | |
| Size | 100% | |
| High damping | 2.5 kHz | the tail darkens fast |
| Low decay | 1.5× | warm low tail |
| Mod depth | 50% | no metallic ringing |
| Low cut | 200 Hz | no mud |
| Mix | 100% | |

- Shimmer: "Ambient — Shimmer" (Shimmer 45 % feeds the tail an octave up; 15–25 % is subtler).
- Endless drone: freeze the tail with step automation of `Freeze` (On after a chord, Off later); "Ambient — Infinite Pad" is the starting point.
- Snare and 80s drums: "Plate — Snare" (Plate, 1.2 s, no pre-delay).

### Neo Ensemble

Chorus (1–4 voices), flanger and phaser (2–12 stages) on one LFO, in Hz or synced to the tempo. `Mix` 50 % is the classic sound (the deepest comb or notches), 100 % vibrato. `Feedback` reaches ±99 % and can't run away. The pitch wobble grows with `Rate` × `Depth`: keep both low on anything that must stay in tune.

String machine and pad ensemble (insert):

| Parameter | Value | Why |
|---|---|---|
| Mode | Chorus | |
| Voices | 4 | |
| Rate | 0.7 Hz | |
| Depth | 40% | |
| Delay | 9.0 ms | |
| Spread | 120° | the right channel's voices fall between the left's |
| Tone | 8.0 kHz | darker, as a bucket-brigade chorus |
| Mix | 60% | |

Jet sweep on drums or a riser, once per two bars:

| Parameter | Value | Why |
|---|---|---|
| Mode | Flanger | |
| Sync | 2 bars | |
| Depth | 85% | |
| Delay | 1.6 ms | |
| Feedback | 75.0% | the ringing peaks of the jet |
| Mix | 50% | |

- Phaser on keys and pads: "Phaser — 4-Stage Classic" (two notches, gentle) or "Phaser — 8-Stage Deep" (four, with feedback).
- Organ and electric piano movement: "Rotary — Leslie-like Fast" (6.7 Hz, the channels opposite); slow it to 0.8 Hz for the slow rotor.
- Doubling a lead or vocal without obvious movement: "Chorus — Subtle Doubling".

### Neo Delay

Stereo, ping-pong or cross echoes, timed in ms or as a note value of the song's tempo. The low cut, the high cut and the tape saturator sit in the loop, so each repeat passes them again: that is what makes a dub echo darken and thin as it fades. `Feedback` reaches ±99 % and the line can't run away.

Dub echo that builds and darkens (insert):

| Parameter | Value | Why |
|---|---|---|
| Mode | Ping-pong | |
| Sync | 1/8 | |
| Feel | Dotted | |
| Feedback | 80.0% | the tail builds up |
| Low cut | 400 Hz | |
| High cut | 2.00 kHz | each repeat a little darker |
| Drive | 15.0 dB | the repeats saturate and stay level |
| Mod depth | 1.00 ms | tape wow |
| Mix | 40% | |

Metallic comb on a drum loop or noise (a pitched resonance at 1 ÷ Time):

| Parameter | Value | Why |
|---|---|---|
| Sync | Free | |
| Time | 4.55 ms | about 220 Hz (A3) |
| Feedback | 90.0% | a sharp, ringing comb; negative: an octave lower and hollower |
| Low cut | 20.0 Hz | off |
| Mix | 50% | |

- Tape stop and pitch bends: automate `Time` slowly (`automation_write` with a ramp): small steps glide and bend the pitch, as tape does; a jump to another `Sync` crossfades.
- Rhythmic stutter: "Rhythm — Sixteenth Stutter"; for triplet bounce "Cross — Triplet Bounce".
- Haas widening without a repeat: "Mix — Doubler (Wide)" (18 ms left, 29 ms right, no feedback).

### Neo Filter

Neo Poly's ladder and state-variable filter as an effect, with drive in front and an envelope follower and an LFO moving the cutoff in octaves. The SVF band-pass's peak is Q times the input; the ladder's passband drops a few dB as its resonance rises.

Acid on a plain saw bass (insert; the envelope opens it on each note):

| Parameter | Value | Why |
|---|---|---|
| Type | Ladder 24 | |
| Cutoff | 400 Hz | |
| Resonance | 12.0 | squelch, short of singing |
| Drive | 12.0 dB | grit before the filter |
| Env amount | 2.50 oct | up to about 2.3 kHz on each note |
| Env sensitivity | 12.0 dB | full on a −12 dBFS part |
| Env attack | 1.0 ms | |
| Env release | 150 ms | |
| Output | 3.0 dB | the ladder's level back |

Sixteenth-note gated sweep on a pad (synced, the same on every pass):

| Parameter | Value | Why |
|---|---|---|
| Type | SVF LP | |
| Slope | 24 dB/oct | |
| Cutoff | 1.20 kHz | |
| Resonance | 2.00 | |
| LFO sync | 1/16 | |
| LFO shape | Saw down | each step opens then closes |
| LFO depth | 2.00 oct | |
| LFO retrig | Song | |

- A pitched drone from nothing: Resonance at its top makes the filter sing at its cutoff (bounded, about −8 dBFS on the SVF); set `Cutoff` to the note's frequency (A3: 220 Hz).
- Measure a sweep: `render(probes=["Filter"])` and a spectrogram (`plot(kind="spectrogram", …)`); `LFO retrig` Song makes each render the same.

## Checklist

- [ ] The sound matches the description on all four axes, each with a number: `analyze(source=…, metrics=["spectrum", "dynamics", "stereo"])` on a solo render.
- [ ] It is in tune: `analyze(source=…, metrics=["pitch"])` reads within ±10 cents (±5 for basses), unless detune is the point.
- [ ] It doesn't clip alone or in chords: `render(solo=[…])` reports no clipped samples and a true peak below -1 dBTP; lower `Volume` with `param_set` if not.
- [ ] Bass and kick are mono below about 120 Hz: `analyze(source=…, metrics=["stereo"])` shows `sub` and `low` correlation above 0.9.
- [ ] Its envelope fits the tempo: `analyze(source=…, metrics=["dynamics"])`; plucks decay within one note spacing, pads release before the next chord change.
- [ ] The chosen values are readable back: `param_get(machine=…)`.
- [ ] Worth keeping: `preset_save(machine=…, name=…, comment=…)`.
- [ ] The song is as it was apart from the sound: the audition placement removed (`arrangement_clear`) and the song end restored (`song_set(song_end_beats=…)`), checked with `arrangement_get`.
