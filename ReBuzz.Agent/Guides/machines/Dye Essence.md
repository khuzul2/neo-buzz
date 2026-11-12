## How it sounds

A hybrid synth with three engine slots (A, B, C). Each plays a morphing wavetable of 64 frames (the Wavetable engine) or a classic analog wave (the Analog engine), plays a sample (the Sample engine: built-in or your own, looped or one-shot), scatters grains of one (the Granular engine) or adds up to 128 sine partials (the Harmonic engine, additive), with up to 16 unison voices and two warps in series; plus a sub and a noise. Every note has two filters (ladder, state-variable, comb) that run from serial to parallel. Voices run at Standard 2× (the default) or High 4×: harmonics reach 18 kHz at every pitch below 9 kHz and a plain saw's alias floor is −92 dB or lower. Good for wide unison leads, evolving pads, FM and sync basses, plucks and bells.

## Signal flow

- Slots A, B and C: `A engine` (Off, Wavetable, Analog, Sample, Granular or Harmonic). Wavetable: `A table`, `A position` (0–100 % through the table), octave, semitone and fine tune, level and pan; `A unison` (1–16 voices), `A detune` (the outermost voices this many cents either side), `A spread` (stereo); `A phase` and `A random` (how much of a random start phase each voice gets); `A warp 1` and `A warp 2` with their amounts; `A modulator` (what FM, PM, AM and Ring take: another slot, the sub or the noise); `A → filter 2` (0 %: filter 1, 100 %: filter 2). Analog: `A shape` (sine, triangle, saw, square, pulse), `A pulse width` (3–50 %, for Pulse) and `A drift` (each voice a little out of tune, up to ±12 cents, fixed for the note); the slot's tuning, unison, warps and routing as above. B and C the same; B and C start Off.
- Sample and Granular engines (both read `A sample`: Choir, Strings, Bell, Pluck or Texture, made by code, or User: one you loaded for the slot). Sample: plays it at the note's pitch (`A root` is the note it sounds at its own pitch, 60 = C4), from `A position`; `A loop` Off (plays once), Forward or Ping pong between `A loop start` and `A loop end` (% of the sample), `A loop fade` (0 ms: a hard wrap, right for a seamless loop; a few ms smooths a loop cut from a recording), `A reverse`. Granular: `A position` is the centre grains are taken from, `A scan` moves it (1× the sample's own speed, 0 frozen, negative backwards, up to ±2×), `A spray` scatters it, `A grain size` (5–500 ms), `A grain rate` (1–100 grains a second), `A grain pitch` (how far each grain may wander in cents), `A grain shape` (Hann, Triangle, Tukey, Gauss, Percussive), `A grain reverse` (share of grains played backwards) and `A grain width` (stereo scatter). The slot's tuning, unison, level, pan and filter-2 send work as for the other engines; warps and the modulator apply to Wavetable and Analog slots only. Playback is band-limited (a 32-tap windowed sinc, a mipmap of the sample for high notes): alias products stay below −85 dB.
- Harmonic engine (additive): a bank of sine partials at multiples of the note. `A harmonics` is the set of partial levels (Saw, Square, Triangle, Organ, Reed, Brass, Strings, Vowel A, Vowel O, Bright, Comb, Random, Octaves, or User: the first frame of the slot's loaded wavetable, analysed); `A harmonics 2` the set `A position` morphs to (0 %: the first, 100 %: the second; User here is the table's last frame); `A partials` (1–128) how many sound; `A tilt` (±24 dB per octave of partials, on top of the set; loudness stays the same); `A odd even` (+ weakens the even partials toward a hollow odd-only tone, − the odd ones toward the octave above); `A stretch` (+ spreads the partials off whole multiples of the pitch, partial k at k^(1+stretch/2): bells, glass, wire; − bunches them into clusters); `A damp` (the upper partials die away while the note is held: partial k's level falls by e^(−damp·(k−1)/2) a second at 100 %: plucks, struck tones). Tuning, unison, detune, spread, phase, random, level, pan and the filter-2 send work as for the other engines; warps and the modulator do not apply. Partials above 0.46 of the sample rate are not made at all, so there is nothing to alias, and a wide unison shares one partial budget per note (256 partials, never fewer than 8 a voice).
- Sub (sine, triangle, saw or square, 0, −1 or −2 octaves) and noise (`Noise color` 0 % dark … 100 % white), each with level, pan and `→ filter 2`.
- Filter 1 and Filter 2: Ladder 24, Ladder 12, LP/BP/HP 12 and 24, Notch, Peak, Comb+ and Comb− (the cutoff is the comb's pitch; `F1 key track` 100 % plays it in tune), with drive, envelope amount (±6 octaves), key tracking and velocity. `Filter routing` 0 %: filter 1 into filter 2; 100 %: side by side.
- Amp and filter envelopes; voice mode (Poly, Mono, Legato), polyphony up to 32, glide, bend range, `Quality`.
- Modulation: two DAHDSR envelopes (`Env1 delay` … `Env1 release`, a curve per segment, −100 % a fast start, +100 % a slow one; `Env1 sync` Beats reads the times as beats), four LFOs (`LFO1 shape`, rate or `LFO1 sync`, `LFO1 retrig` On: one per note from `LFO1 phase`, Off: one free LFO; `LFO1 fade`), four macros, and sixteen routes: `R1 source`, `R1 dest`, `R1 amount`. Full amount moves pitch ±12 st, a cutoff ±6 octaves, an LFO's rate ±4 octaves, and positions, warp amounts, levels and resonance across their whole range; Amp scales the gain by 1 + amount.

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
| User | that slot's own table (`machine_data_import`) |

| Warp | What it does at full amount | Aliasing at 70 %, 880 Hz |
|---|---|---|
| Bend+, Bend− | reads the cycle slowly at the start and fast at the end (or the reverse): brighter, vocal | edged: −55 to −71 dB at Standard, −63 to −86 dB at High |
| Squeeze | plays the whole cycle in 4 % of the period and holds: pulse-width modulation for any wave | edged: −39 to −62 dB, High −49 to −77 dB |
| Mirror | crossfades in the cycle read forward to its half and back (smooth turns): symmetric, hollow, an octave-ish lift | clean (−92 dB) |
| Asym | wobbles the read speed through the cycle: phase distortion | clean |
| Sync | hard sync, the cycle read up to 8 times per period | edged: −45 to −53 dB, High −52 to −60 dB |
| Window sync | sync with each restart windowed: the sync sound without the edge | clean (−98 dB) |
| FM | linear FM through zero from the modulator (±4 × the pitch at full amount) | clean |
| PM | phase modulation from the modulator (±1 cycle) | clean |
| AM | amplitude modulation from the modulator | clean |
| Ring | ring modulation, crossfaded in | clean |

## How to use it

- Supersaw-style leads: `A position` 67 % (saw), `A unison` 5–9, `A detune` 15–30 ct, `A spread` 80–100 %, filter 1 a Ladder 24 near 4–6 kHz.
- FM and PM: set a slot's `A warp 1` to FM or PM, `A modulator` to another slot (or the sub), and give that slot `B engine` Wavetable and `B level` 0 % to hear only the carrier. A modulator an octave up (`B octave` +1) keeps the result harmonic.
- Basses: `Voice mode` Mono, the sub at −1 octave, a short filter envelope; Window sync or FM for growl.
- Two filters: send a slot to filter 2 with `B → filter 2` 100 % and set `Filter routing` 100 % for two parallel paths; at 0 % filter 2 also hears filter 1.
- Edged warps (Bend, Squeeze, Sync) alias on high notes at Standard: `Quality` High 4× keeps them 7–15 dB cleaner at twice the CPU.
- Analog basses and strings: `A engine` Analog, `A shape` Saw or Pulse, `A drift` 30–60 % for the analog spread; Pulse with a narrow width for reedy tones. The waves are band-limited as the tables are: the same −92 dB alias floor.
- Additive tones: Harmonic with `A harmonics` Saw to Square and `A position` as the morph; `A odd even` + for clarinet-like hollow tones; `A stretch` +40–100 % and `A damp` 40–80 % with a short amp decay for bells; Vowel A to Vowel O with a slow LFO on `A position` for a vocal pad. For a morph of your own, load a wavetable into the slot (`kind="wavetable"`) and set both `A harmonics` and `A harmonics 2` to User: the position then morphs from the table's first frame to its last (only the partial levels are used, not their phases).
- Your own sample, per slot: `machine_data_import(machine="Dye", kind="sample", path="C:/Samples/mine.wav", item=1)` loads a WAV (8 to 32 bit, mono or stereo; the first 20 s) into slot B, sets `B sample` to User and, unless the slot is Granular, `B engine` to Sample. It lives in the song and the preset's machine data, 24 bit; set `B root` to its pitch. There is no multisample or SFZ import yet: one sample per slot, so layer slots for zones.
- Pads from a sample: Sample, `A loop` Forward, `A unison` 3–8; for clouds, Granular with `A scan` 0.1–0.5×, `A spray` 10–30 %, size 100–200 ms and 30–60 grains a second. Frozen textures: `A scan` 0.
- Your own table, per slot: a WAV of 2048-sample frames (or a clm chunk saying the frame size) or a single cycle. `machine_data_import(machine="Dye", kind="wavetable", path="C:/Tables/mine.wav", item=1)` loads it into slot B and sets `B table` to User.

## Gotchas

- Routes are parameters (source, destination, amount), so `param_set` and presets set them like any other; a route with source or dest Off, or amount 0 %, does nothing.
- Free LFOs (retrig Off) don't take the LFO-rate destinations; per-note ones do.

- Freshly added it plays a saw (`A table` Basic at 67 %) through a Ladder 24; B and C are Off.
- A silent slot still renders when it is another's modulator (its level only mutes it).
- `Sub octave` takes the text "0", "-1" or "-2" (quoted), not a number.
- Tables are built in the background on first use; renders wait for them (up to 5 s).
- Unison costs CPU per voice: 16 notes × 3 slots × 8 voices run some 3–6× real time on a desktop core; prefer fewer, wider voices.
- Combs with feedback near 100 % ring loud: watch the peaks.
- Grains cost CPU per grain (each read with the sinc), so a slot of a note keeps at most 10 grains live and the whole bank shares 160 between its granular slots (never fewer than 4 a slot); past the cap the oldest grain fades out over a millisecond or so. 100 grains a second at 300 ms on 16 notes runs about twice real time on a desktop core; keep rate × size under 10 for polyphony, or fewer voices. The first 20 s of a loaded WAV are kept; longer ones are cut.
- Samples are built in the background like tables; renders wait for them. A Sample slot with `A loop` Off ends silent after the sample ends, whatever the amp envelope does.
- Samples are normalised to a peak of 0.9 and decaying ones (Bell, Pluck) are quiet by RMS: turn `Volume` up or layer a second slot.

## Preset map

| Preset | What | Tweak first |
|---|---|---|
| "Init — Basic" | a saw through the ladder | everything |
| "Lead — Wide Unison" | 7 + 5 detuned saws, an octave apart | `A detune`, `F1 cutoff` |
| "Lead — Sub PM" | a triangle phase-modulated by the sub | `A warp 1 amt` |
| "Pad — Glass Choir" | Formants in unison over Harmonics, slow | `A position` |
| "Pad — Two Filters" | Grit to filter 1, Resonant to a band-pass in parallel | `F2 cutoff` |
| "Bass — FM Growl" | a sine FM'd by a silent saw, mono, glide | `A warp 1 amt` |
| "Bass — Window Sync" | a window-synced saw with a sub | `A warp 1 amt` |
| "Bass — Detuned Stack" | three detuned saws and one an octave down | `F1 env` |
| "Bass — Analog Saw" | an analog saw over a 30 % pulse an octave down, mono | `F1 cutoff`, `B pulse width` |
| "Pad — Analog Strings" | six drifting analog saws and a narrow pulse an octave up | `A drift`, `A detune` |
| "Pluck — Mirror Bell" | Harmonics mirrored, a fast filter envelope | `A warp 1 amt` |
| "Keys — Bent Organ" | Organ with Bend+, an octave layer | `A warp 1 amt` |
| "Texture — Comb Noise" | noise through a tuned comb | `F1 reso` |
| "Pad — Position Sweep" | Formants swept by a slow free LFO, a mod envelope opening the filter | `R1 amount`, `LFO1 rate` |
| "Lead — Delayed Vibrato" | vibrato fading in over 0.7 s; the wheel opens the filter | `LFO1 fade`, `R1 amount` |
| "FX — Squeeze Sweep" | Digital squeezed and warped, High 4× | `A warp 1 amt` |
| "Pad — Sample Choir" | a looped sung vowel, three voices | `A loop fade`, `F1 cutoff` |
| "Pad — Eight Strings" | a looped bowed-string sample in eight voices | `A detune`, `F1 cutoff` |
| "Keys — Struck Bell" | the bell sample played once, two voices | `A detune`, `Amp release` |
| "Pluck — Double String" | the plucked sample in two slots a few cents apart | `B fine` |
| "FX — Reverse Pluck" | the pluck backwards | `A reverse`, `Amp release` |
| "Texture — Frozen Cloud" | grains of a noise texture, the centre frozen | `A position`, `A grain size` |
| "Pad — Scanning Strings" | a cloud scanning slowly through the strings | `A scan`, `A spray` |
| "Pad — Choir Cloud" | a cloud of the choir, scanning at half speed | `A grain rate`, `A spray` |
| "FX — Bell Rain" | sparse percussive grains of the bell, pitch scattered up to 600 ct, some reversed | `A grain pitch`, `A grain reverse` |
| "Pad — Additive Strings" | the Strings set morphing to Brass, four unison voices | `A position`, `A tilt` |
| "Keys — Drawbar Organ" | the Organ set with a decaying upper end, a second slot an octave up | `A damp`, `B level` |
| "Lead — Reed Morph" | the Reed set; the mod wheel morphs it to Brass, vibrato from LFO 1 | `A position`, `R1 amount` |
| "Pad — Vowel Choir" | Vowel A to Vowel O, a slow LFO morphing three voices | `R1 amount`, `LFO1 rate` |
| "Bell — Stretched Glass" | stretched, quickly damped partials | `A stretch`, `A damp` |
| "Pluck — Damped Wire" | 96 partials of a saw that die from the top | `A damp`, `Amp decay` |
| "Bass — Hollow Pipe" | Reed, odd partials only, an octave down through a Ladder 24 | `A odd even`, `F1 cutoff` |
| "Texture — Random Partials" | a random set drifting to Comb, five wide voices | `A stretch`, `R1 amount` |
| "Keys — Glass Bell" | the bell sample with a Formants table an octave up | `B level` |
