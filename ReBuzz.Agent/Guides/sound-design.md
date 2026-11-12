# Sound design

How to describe a sound in numbers, then recipes with exact values for the Neo synths and effects. Values are what `param_set` takes. Each recipe starts from a freshly added machine (its defaults) or from the preset it names. They are starting points: render, measure, adjust. The recipes are in `guide(topic="synth-recipes")` (Neo Poly, Neo Wave) and `guide(topic="fm-recipes")` (Neo FM6).

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
