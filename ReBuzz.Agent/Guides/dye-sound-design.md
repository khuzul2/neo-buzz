# Dye Essence sound design

Dye Essence has three engine slots, A, B and C, and each slot plays one of seven engines. The engine is the first decision in a sound: it sets what the other controls mean. This guide is the map from the sound you want to the engine, the controls to move first, a preset to start from and what to measure. Signal flow, modulation, arpeggiator, sequencer and the effects rack are in `guide(topic="dye-essence-in-depth")` and `guide(topic="dye-essence-effects")`; the Spectral engine and the imports are in `guide(topic="dye-essence-analysis")`. Every parameter below is read and set as `A …`; B and C have the same ones with their own letter.

## Choose the engine

| You want | Engine | Start from |
|---|---|---|
| Supersaw leads, evolving pads, FM and sync basses | Wavetable | "Lead — Wide Unison", "Bass — Resonant Table Bass" |
| Analog basses, strings, pulse leads at the lowest CPU | Analog | "Bass — Analog Saw", "Pad — Analog Strings" |
| Choirs, strings, bells and plucks from a recording, your own sample | Sample | "Pad — Sample Choir", "Keys — Struck Bell" |
| Clouds, frozen textures, bell rain | Granular | "Texture — Frozen Cloud", "FX — Bell Rain" |
| Organs, clarinets, vowel pads, glass and bell tones from partials | Harmonic | "Pad — Additive Strings", "Lead — Reed Morph", "Bell — Stretched Glass" |
| Mallets, bells, strings, tubes, drum skins, bowed glass | Modal | "Keys — Glass Marimba", "Bell — Struck Bronze", "Pluck — Nylon String" |
| A sound slowed, frozen or stretched without changing its pitch | Spectral | "Pad — Frozen Choir Pad", "Vocal — Formant Choir" |

Switch a slot with `param_set(machine="Dye", changes=[{"param": "A engine", "value": "Modal"}])`; `preset_list(machine="Dye Essence", search="Modal")` finds presets by words. Wavetable and Analog are the lightest; Harmonic costs per partial, Modal per mode, Granular per live grain, and Spectral by `A spectral partials`. Unison multiplies every one of them, and `Quality` High 4× doubles all.

## Wavetable

A table of waveform frames read at `A position`; `A table` picks the table (Basic, PWM, Harmonics, Formants, Sync sweep, FM sweep, Organ, Resonant, Digital, Grit, User). Two warps in series bend what is read (`A warp 1`, `A warp 2`, each with an amount). Move first: `A position`, then `A warp 1` and its amount, then `A unison` and `A detune`.

| Parameter | Value | Why |
|---|---|---|
| A table | Basic | the default: sine at 0 %, triangle 33 %, saw 67 %, square 100 % |
| A position | 67% | saw: the supersaw start; sweep or modulate it for movement |
| A unison | 5 | gentle width; each voice costs CPU in direct proportion |
| A detune | 20 ct | shimmer; 40 to 100 ct is a clearly detuned stack |
| A spread | 90% | the voices across the stereo field |

Warps: Mirror, Asym, Window sync, FM, PM, AM and Ring are clean; Bend, Squeeze and Sync alias on high notes at Standard quality (`Quality` High 4× keeps them 7 to 15 dB cleaner at twice the CPU). Measure: `plot(kind="spectrogram", sources=["r1"])` of a note with `A position` moving shows the harmonics opening; `analyze(source=…, metrics=["spectrum"])` of a high note shows the alias floor.

## Analog

Band-limited classic waves: `A shape` (Sine, Triangle, Saw, Square, Pulse), `A pulse width` for Pulse, and `A drift`, each voice a little out of tune (up to ±12 cents, fixed for the note). The same unison, warps and routing as Wavetable, and the lightest slot there is.

| Parameter | Value | Why |
|---|---|---|
| A engine | Analog | |
| A shape | Saw | strings and basses; Pulse with a narrow width for reedy tones |
| A drift | 40% | the analog spread: 30 to 60 % is natural, 0 is clinical |
| A unison | 3 | a few voices are enough for a bass or a string |

Measure: pitch should hold within the drift: `analyze(source=…, metrics=["pitch"])`; bass in mono below 120 Hz: `metrics=["stereo"]`.

## Sample

Plays a recording at the note's pitch: `A sample` is Choir, Strings, Bell, Pluck or Texture (made by code), or User (yours, `machine_data_import(machine="Dye", kind="sample", path=…, item=1)` loads one into slot B). `A root` is the note it sounds at its own pitch (60 is C4); `A position` where it starts; `A loop` Off plays once, Forward or Ping pong loops between `A loop start` and `A loop end`; `A loop fade` smooths a loop cut from a recording (0 ms is a hard wrap, right for a seamless loop).

- The first 20 s of a loaded WAV are kept; samples are normalised to a peak of 0.9, and decaying ones (Bell, Pluck) are quiet: raise `Volume` or layer a second slot.
- With `A loop` Off a Sample slot ends silent when the sample ends.
- For pads: `A loop` Forward, `A unison` 3 to 8. One sample per slot: layer slots for zones.

Measure: a user sample is in tune only if `A root` is right: `analyze(source=…, metrics=["pitch"])` on a rendered C4 should read 261.6 Hz.

## Granular

The sample is cut into short windowed grains read around a centre. `A position` is the centre, `A scan` moves it (0 frozen, 1× the sample's own speed, negative backwards), `A spray` scatters it, `A grain size`, `A grain rate`, `A grain pitch` (how far each grain may wander), `A grain shape` (Hann, Triangle, Tukey, Gauss, Percussive) and `A grain reverse`.

| Parameter | Value | Why |
|---|---|---|
| A engine | Granular | |
| A sample | Texture | a built-in source with plenty of motion |
| A scan | 0× | frozen: the cloud stays at `A position` |
| A grain size | 120 ms | 100 to 200 ms is a smooth cloud; 20 to 40 ms is gritty |
| A grain rate | 40 | grains a second; 30 to 60 is dense |
| A spray | 40% | how far the centre scatters |

A note keeps at most 10 live grains per slot and the whole machine 160, so keep `A grain rate` times `A grain size` (in seconds) under 10 for polyphony: 40 grains a second at 0.12 s is 4.8. Measure: `render(cpu=true)` on the busiest chord; granular cost rises with live grains.

## Harmonic

A bank of sine partials at multiples of the note (up to 128): `A harmonics` is the set of levels (Saw, Square, Triangle, Organ, Reed, Brass, Strings, Vowel A, Vowel O, Bright, Comb, Random, Octaves, User), `A harmonics 2` the set `A position` morphs to. `A partials` how many sound, `A tilt`, `A odd even` (more odd for hollow, clarinet-like tones), `A stretch` (spreads partials off whole multiples: bells, glass, wire) and `A damp` (the upper partials die first while the note is held).

| Parameter | Value | Why |
|---|---|---|
| A engine | Harmonic | |
| A harmonics | Brass | a set to start from; `A harmonics 2` another to morph to |
| A partials | 32 | the cost control: each partial costs |
| A stretch | +60% | partials spread off whole multiples; 0 % keeps them on the harmonics |
| A damp | 40% | bell-like decay with a short amp decay |

Vowel A to Vowel O with a slow LFO on `A position` is a vocal pad. Measure: `plot(kind="spectrum", sources=["r1"])`: the partial lines are visible; with `A stretch` raised they are no longer evenly spaced.

## Modal

A resonating body of up to 64 damped modes, struck, plucked or bowed. `A exciter` (Struck, Plucked, Continuous), `A geometry` (String, Tube, Bar, Membrane, Plate, Bell: the mode spacing), `A modes`, `A force` (harder is brighter), `A material` (the damping from soft to hard), `A ring time`, `A hf damp`, `A body size`.

| Parameter | Value | Why |
|---|---|---|
| A engine | Modal | |
| A geometry | Bar | mallet keys: marimba and vibraphone |
| A exciter | Struck | a mallet |
| A force | 60% | 50 to 70 % for mallets |
| A modes | 24 | the cost control: each mode costs |

Bell: `A geometry` Bell, `A material` 80 %, a long ring time. Plucked string: Plucked, `A geometry` String, `A hf damp` 50 %. Bowed glass: Continuous, String, `A material` 80 %. Skin drum: Membrane, `A octave` -1 (the slot's octave), `A body size` 85 %. Measure: a struck sound is very dynamic (`analyze(source=…, metrics=["dynamics"])`); the ring against the note length decides whether notes pile up.

## Spectral

A sample is analysed once into partials and noise and rebuilt at any key: `A spectral speed` and the pitch are independent. `A spectral speed` (0 holds one moment of the sample at `A position`), `A key tracking`, `A formant keep` (keeps the spectral envelope when the pitch changes), `A harmonic snap`, `A tonal noise` (0 only the noise, 100 only the partials), `A blur`, `A spectral partials` (the cost control), `A spectral tilt` and `A spectral stretch`.

| Parameter | Value | Why |
|---|---|---|
| A engine | Spectral | |
| A sample | Choir | a source that holds its tone |
| A spectral speed | 0% | frozen: a pad from one moment of the sound |
| A position | 35% | which moment |
| A blur | 40% | smears changes: a smoother, more ghostly pad |

No phases are kept, so attacks soften into a short swell: use it for sustained and evolving sounds, and the Sample engine for drums and plucks. Measure: `plot(kind="spectrogram", …)` of a frozen note should show steady lines, not a decay.

## Combining slots

- **Layer:** A Analog saw, B a Sample of Strings an octave up (`B octave`), a few dB below: depth without a thick unison.
- **FM and PM:** set `A warp 1` to FM or PM, `A modulator` to slot B, and B's engine Wavetable with `B level` 0 % to hear only the carrier.
- **Body plus air:** a Modal slot for the strike, a Granular or noise slot beneath it for breath.
- **Sub:** `Sub octave` takes the text "-1" or "-2" (quoted), not a number.
- Every slot you add multiplies voices; check `render(cpu=true)` before adding a third.

## Checklist

- [ ] The engine is the one you meant: `param_get(machine="Dye", params=["A engine"])`.
- [ ] The first sound came from a preset of that engine: `preset_load(machine="Dye", preset="Keys — Glass Marimba")`, then moved by the controls named above.
- [ ] It is in tune: `analyze(source=…, metrics=["pitch"])` within ±10 cents (more with unison or drift, on purpose).
- [ ] It does not clip alone or in chords: `analyze(source=…, metrics=["peaks"])` shows a true peak below -1 dBTP; lower `Volume` otherwise.
- [ ] Its cost is known: `render(from=…, to=…, cpu=true)` on the busiest bars, before layering more slots.
