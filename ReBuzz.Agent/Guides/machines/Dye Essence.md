## How it sounds

A hybrid synth with three engine slots (A, B, C). Each plays a morphing wavetable of 64 frames (the Wavetable engine) or a classic analog wave (the Analog engine), plays a sample (the Sample engine: built-in or your own, looped or one-shot), scatters grains of one (the Granular engine) adds up to 128 sine partials (the Harmonic engine, additive) or rings a body of up to 64 modes that a mallet, a pluck or a bow excites (the Modal engine), with up to 16 unison voices and two warps in series; plus a sub and a noise. Every note has two filters (ladder, state-variable, comb) that run from serial to parallel. Voices run at Standard 2× (the default) or High 4×: harmonics reach 18 kHz at every pitch below 9 kHz and a plain saw's alias floor is −92 dB or lower. Good for wide unison leads, evolving pads, FM and sync basses, plucks and bells.

## Signal flow

- Slots A, B and C: `A engine` (Off, Wavetable, Analog, Sample, Granular, Harmonic or Modal). Wavetable: `A table`, `A position` (0–100 % through the table), octave, semitone and fine tune, level and pan; `A unison` (1–16 voices), `A detune` (the outermost voices this many cents either side), `A spread` (stereo); `A phase` and `A random` (how much of a random start phase each voice gets); `A warp 1` and `A warp 2` with their amounts; `A modulator` (what FM, PM, AM and Ring take: another slot, the sub or the noise); `A → filter 2` (0 %: filter 1, 100 %: filter 2). Analog: `A shape` (sine, triangle, saw, square, pulse), `A pulse width` (3–50 %, for Pulse) and `A drift` (each voice a little out of tune, up to ±12 cents, fixed for the note); the slot's tuning, unison, warps and routing as above. B and C the same; B and C start Off.
- Sample and Granular engines (both read `A sample`: Choir, Strings, Bell, Pluck or Texture, made by code, or User: one you loaded for the slot). Sample: plays it at the note's pitch (`A root` is the note it sounds at its own pitch, 60 = C4), from `A position`; `A loop` Off (plays once), Forward or Ping pong between `A loop start` and `A loop end` (% of the sample), `A loop fade` (0 ms: a hard wrap, right for a seamless loop; a few ms smooths a loop cut from a recording), `A reverse`. Granular: `A position` is the centre grains are taken from, `A scan` moves it (1× the sample's own speed, 0 frozen, negative backwards, up to ±2×), `A spray` scatters it, `A grain size` (5–500 ms), `A grain rate` (1–100 grains a second), `A grain pitch` (how far each grain may wander in cents), `A grain shape` (Hann, Triangle, Tukey, Gauss, Percussive), `A grain reverse` (share of grains played backwards) and `A grain width` (stereo scatter). The slot's tuning, unison, level, pan and filter-2 send work as for the other engines; warps and the modulator apply to Wavetable and Analog slots only. Playback is band-limited (a 32-tap windowed sinc, a mipmap of the sample for high notes): alias products stay below −85 dB.
- Harmonic engine (additive): a bank of sine partials at multiples of the note. `A harmonics` is the set of partial levels (Saw, Square, Triangle, Organ, Reed, Brass, Strings, Vowel A, Vowel O, Bright, Comb, Random, Octaves, or User: the first frame of the slot's loaded wavetable, analysed); `A harmonics 2` the set `A position` morphs to (0 %: the first, 100 %: the second; User here is the table's last frame); `A partials` (1–128) how many sound; `A tilt` (±24 dB/oct of partials, on top of the set; loudness stays the same); `A odd even` (+ weakens the even partials toward a hollow odd-only tone, − the odd ones toward the octave above); `A stretch` (+ spreads the partials off whole multiples of the pitch, partial k at k^(1+stretch/2): bells, glass, wire; − bunches them into clusters); `A damp` (the upper partials die away while the note is held: partial k's level falls by e^(−damp·(k−1)/2) a second at 100 %: plucks, struck tones). Tuning, unison, detune, spread, phase, random, level, pan and the filter-2 send work as for the other engines; warps and the modulator do not apply. Partials above 0.46 of the sample rate are not made at all, so there is nothing to alias, and a wide unison shares one partial budget per note (256 partials, never fewer than 8 a voice).
- Modal engine (physical modelling): a resonating body, a bank of up to 64 damped modes at the frequencies a real object has, struck, plucked or bowed. `A exciter`: Struck (a mallet, one half-sine pulse at the start of the note), Plucked (the body released from a displaced shape) or Continuous (noise that sounds while the key is held and fades 30 ms after it: a bow or breath). `A geometry` sets the mode spacing: String (whole multiples), Tube (closed at one end: odd multiples), Bar (free bar: 1, 2.76, 5.40, 8.93…), Membrane (a drum skin: 31 modes, 1, 1.59, 2.14, 2.30…), Plate (a rectangle: dense, uneven), Bell (12 partials, hum an octave below the note: 0.5, 1, 1.18, 1.51, 2…). `A modes` (1–64) how many sound, lowest first (at most the body has). `A force` (0–100 %): how hard it is excited: harder is brighter (a harder mallet, a sharper pluck, a pressed bow) and the loudness stays; the key's velocity adds to it. `A position` (0–100 %): where on the body it is excited: 0 at an end (every mode, bright), 100 % at the middle (a string loses its even modes). `A material` (0–100 %): 0 is gut, felt or skin (exact spacing, the upper modes die fast), 100 % is glass or steel (stiffness stretches the spacing, mode k × √(1 + 0.003·material²·k²), and the upper modes ring on). `A ring time` (0.05–20 s): how long the lowest mode takes to fall 60 dB (the damping). `A hf damp` (0–100 %): extra loss growing with the square of the frequency (the material softens it), so the upper modes die first. `A body size` (0–100 %, 50 % neutral): a larger body rings longer (×0.35 … ×1.7 across the range) and favours the low modes, a smaller one the high ones. Tuning, unison, detune, spread, phase, random, level, pan and the filter-2 send work as for the other engines; warps and the modulator do not apply. Modes above 0.46 of the sample rate are not made, so there is nothing to alias. A struck or plucked note rings by itself: give the amp envelope a sustain of 100 % and a release of 0.1–0.5 s (the key damps it); a bowed one needs only its attack. A wide unison shares one mode budget per note (256 modes, never fewer than 8 a voice).
- Sub (sine, triangle, saw or square, 0, −1 or −2 octaves) and noise (`Noise color` 0 % dark … 100 % white), each with level, pan and `→ filter 2`.
- Filter 1 and Filter 2: Ladder 24, Ladder 12, LP/BP/HP 12 and 24, Notch, Peak, Comb+ and Comb− (the cutoff is the comb's pitch; `F1 key track` 100 % plays it in tune), with drive, envelope amount (±6 octaves), key tracking and velocity. `Filter routing` 0 %: filter 1 into filter 2; 100 %: side by side.
- Amp and filter envelopes; voice mode (Poly, Mono, Legato), polyphony up to 32, glide, bend range, `Quality`.
- Modulation: two DAHDSR envelopes (`Env1 delay` … `Env1 release`, a curve per segment, −100 % a fast start, +100 % a slow one; `Env1 sync` Beats reads the times as beats), four LFOs (`LFO1 shape`, rate or `LFO1 sync`, `LFO1 retrig` On: one per note from `LFO1 phase`, Off: one free LFO; `LFO1 fade`), four macros, and sixteen routes: `R1 source`, `R1 dest`, `R1 amount`. Full amount moves pitch ±12 st, a cutoff ±6 octaves, an LFO's rate ±4 octaves, and positions, warp amounts, levels and resonance across their whole range; Amp scales the gain by 1 + amount.
- Arpeggiator (`Arp mode`: Up, Down, Up Down, Played, Random or Chord; Off plays the keys as they come): the held notes one by one on the song's grid. `Arp rate` (1/32 … 1 bar) and `Arp feel` (Straight, Dotted, Triplet) give the step, `Arp octaves` (1–4) repeats the notes upwards, `Arp gate` is how much of a step each note sounds, `Arp swing` delays every second step (100 % by half a step), `Arp latch` keeps the notes after the keys are up (the next key replaces them). Random (and the sequencer's probabilities) draw from `Arp seed`: the same seed plays the same notes every time. While the song plays the steps follow its position (a bar's first step is on the beat); stopped, they start on the first key.
- Step sequencer (`Seq mode`: Notes plays the steps on the key held (the last one pressed; the arpeggiator stands aside), Mod only runs the steps as modulation sources, Off): `Seq length` 1–32 steps of `Seq rate`/`Seq feel`, `Seq swing`, `Seq slew` (how smoothly the sources glide from step to step). Each step has a note offset (±48 semitones from the key), a velocity (0 is a rest), a gate (1–100 % of the step), a tie (holds into the next step, which then plays legato; the same note just goes on) and a probability. The steps are in the song (the machine state), not parameters: set them with `machine_data_import(machine="Dye", kind="sequence", path="seq.txt")`, a text file of steps separated by spaces, each `offset,velocity,gate,tie,probability` (every field after the first optional: `0,100,80,0,100` is a plain step; `7,110,60,1` is a tied +7). Route sources `Seq step` (the step's offset / 24, −1…1) and `Seq velocity` work in both modes.
- Live MPE (`MPE` On): MIDI notes keep their channel (1 is the master; 2–16 member channels), and each member channel's pitch bend (`MPE bend range`, 48 semitones by default), pressure and slide (CC74) move that note alone: bend always, pressure and slide as the route sources `MPE pressure` and `MPE slide` (and `MPE bend`, ±1). The master channel's bend is the plain `Bend range` one for every note; Off ignores channels (pressure is then the plain `Aftertouch`, CC74 the slide for every note). Pattern notes are unchanged.

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

- Effects rack (after the amp, all Off by default): ten slots `FX1 type` ... `FX10 type` (Off, EQ, Filter, Comp, Clipper, Delay, Verb, Dimension, Bitcrusher, Freq shift, Pitch delay: the engines of Neo EQ (four bands: a low shelf, two bells, a high shelf), Neo Filter, Neo Comp, Neo Clipper, Neo Delay and Neo Verb, then four effects of the rack's own), each with `FX1 mix` (dry to wet, 100 % by default: set a delay or reverb below that in a serial chain) and ten generic parameters `FX1 p1` ... `FX1 p10`, 0-100 %, whose names and units the slot's type gives (the table). 50 % is the engine's own default for the parameter (the EQ's bands at 100 Hz, 500 Hz, 2.5 kHz and 8 kHz, 0 dB). A choice (a mode, a sync value) spans 0-100 % in equal steps with its default at 50 %: read the value text back to see what a percentage picked. Three chains: `FX layout` Trio is bus A and bus B (parallel: `FX send A` and `FX send B` take the synth in, `FX return A` and `FX return B` bring it back, added to the dry) and a main chain (serial, on the sum); Duo is insert 1 and insert 2 in series, then an aux (`FX send A` and `FX return A`). Slots 1-4 start in bus A (insert 1), 5-7 in bus B (insert 2), 8-10 in main (aux), in order; move them with `machine_data_import(machine="Dye", kind="fx_routing", path=...)` of a text file such as "1 2 3 4 | 5 6 7 | 8 9 10" (bus A or insert 1 | bus B or insert 2 | main or aux; each slot once, in order) or on the Effects page. All slots Off run nothing: the output is the synth's, bit for bit.

| Type | p1 ... p10 |
|---|---|
| EQ | Low freq, Low gain, Mid 1 freq, Mid 1 gain, Mid 1 Q, Mid 2 freq, Mid 2 gain, Mid 2 Q, High freq, High gain |
| Filter | Type, Slope, Cutoff, Resonance, Drive, Env amount, LFO rate, LFO sync, LFO shape, LFO depth |
| Comp | Mode, Threshold, Ratio, Knee, Attack, Release, SC low cut, Makeup, Character, Link |
| Clipper | Drive, Ceiling, Shape, Knee, Oversampling, Bands, Crossover 1, Crossover 2, Stereo, Output |
| Delay | Mode, Sync, Feel, Time, Offset, Feedback, Low cut, High cut, Mod depth, Width |
| Verb | Mode, Pre-delay, Size, Decay, Low decay, Damping, Shimmer, Width, Low cut, High cut |
| Dimension | Voices (2-4), Rate, Depth, Delay (1-25 ms), Width, Shape (Sine, Triangle), Tone, Low cut, Drift, Feedback (50 % none; the ends are -90 % and +90 %: a flanger) |
| Bitcrusher | Bits (1-16, 8 at 50 %), Rate (100 Hz-48 kHz, 12 kHz at 50 %), Anti-alias (Off, On), Drive, Quantizer (Mid-tread, Mid-rise, Companded), Bias, Dither, Tone, Wobble (50 % none; below it a slow wow up to 0.6 % of pitch, above it a faster flutter up to 0.3 %), Output |
| Freq shift | Shift (50 % is 0 Hz; the value is cubed, so the middle is fine: 75 % is an eighth of the Range, 100 % the Range), Range (100 Hz, 1 kHz, 5 kHz: what the ends of Shift are), Mode (Shift, Ring), Feedback (50 % none; negative inverts), Feedback tone, Stereo (the two channels shift apart), LFO rate, LFO depth, Glide, Output |
| Pitch delay | Time (10 ms-2 s, 300 ms at 50 %), Pitch (-24 to +24 semitones, +12 at 50 %: 17 % is -12, 33 % is 0), Fine (cents), Feedback, Grain (20-200 ms), Mode (Per repeat: every echo is shifted again; Output only), Tone, Low cut, Spread (right channel's time), Cross-feed (ping-pong) |

The four new types return the wet signal only (the slot's mix blends it): the dimension, a 3-voice chorus with the two channels modulated half a cycle apart, widens a mono source (left-right correlation about 0.3 for noise); the bitcrusher's anti-alias is an eighth-order filter before the hold and a fourth-order after it (a 1 kHz sine at 12 kHz: strongest spurious line -20 dB without, -50 dB with; a 9.3 kHz sine above the new rate's half: alias at -1 dB without, -54 dB with); the frequency shifter is a single-sideband (Bode) shifter whose opposite sideband is 61 to 64 dB down from 500 Hz to 15 kHz; the pitch delay shifts with a two-tap crossfaded delay line, its first echo on the time set. Each is a kernel in NeoBuzz.Dsp that any machine can use.

## How to use it

- Supersaw-style leads: `A position` 67 % (saw), `A unison` 5–9, `A detune` 15–30 ct, `A spread` 80–100 %, filter 1 a Ladder 24 near 4–6 kHz.
- FM and PM: set a slot's `A warp 1` to FM or PM, `A modulator` to another slot (or the sub), and give that slot `B engine` Wavetable and `B level` 0 % to hear only the carrier. A modulator an octave up (`B octave` +1) keeps the result harmonic.
- Basses: `Voice mode` Mono, the sub at −1 octave, a short filter envelope; Window sync or FM for growl.
- Two filters: send a slot to filter 2 with `B → filter 2` 100 % and set `Filter routing` 100 % for two parallel paths; at 0 % filter 2 also hears filter 1.
- Edged warps (Bend, Squeeze, Sync) alias on high notes at Standard: `Quality` High 4× keeps them 7–15 dB cleaner at twice the CPU.
- Analog basses and strings: `A engine` Analog, `A shape` Saw or Pulse, `A drift` 30–60 % for the analog spread; Pulse with a narrow width for reedy tones. The waves are band-limited as the tables are: the same −92 dB alias floor.
- Additive tones: Harmonic with `A harmonics` Saw to Square and `A position` as the morph; `A odd even` + for clarinet-like hollow tones; `A stretch` +40–100 % and `A damp` 40–80 % with a short amp decay for bells; Vowel A to Vowel O with a slow LFO on `A position` for a vocal pad. For a morph of your own, load a wavetable into the slot (`kind="wavetable"`) and set both `A harmonics` and `A harmonics 2` to User: the position then morphs from the table's first frame to its last (only the partial levels are used, not their phases).
- Struck and bowed bodies: Modal with `A geometry` Bar, `A exciter` Struck, `A force` 50–70 %, `A ring time` 0.5–1 s for mallet keys; Bell with `A material` 80 % and `A ring time` 5–8 s for a bell; String with Plucked, `A material` 10–20 % and `A hf damp` 50 % for a plucked string; Continuous with String and `A material` 80 % for a bowed glass, with Tube and `A force` 30 % for a breath tone; Membrane with `A octave` −1 and `A body size` 85 % for a skin drum. Put an LFO or the mod wheel on `A position` for a moving timbre, and a second Modal slot an octave up, bowed, for a pad.
- Your own sample, per slot: `machine_data_import(machine="Dye", kind="sample", path="C:/Samples/mine.wav", item=1)` loads a WAV (8 to 32 bit, mono or stereo; the first 20 s) into slot B, sets `B sample` to User and, unless the slot is Granular, `B engine` to Sample. It lives in the song and the preset's machine data, 24 bit; set `B root` to its pitch. There is no multisample or SFZ import yet: one sample per slot, so layer slots for zones.
- Pads from a sample: Sample, `A loop` Forward, `A unison` 3–8; for clouds, Granular with `A scan` 0.1–0.5×, `A spray` 10–30 %, size 100–200 ms and 30–60 grains a second. Frozen textures: `A scan` 0.
- Your own table, per slot: a WAV of 2048-sample frames (or a clm chunk saying the frame size) or a single cycle. `machine_data_import(machine="Dye", kind="wavetable", path="C:/Tables/mine.wav", item=1)` loads it into slot B and sets `B table` to User.
- Arpeggios: `Arp mode` Up Down, `Arp rate` 1/16, `Arp octaves` 2, `Arp gate` 40–60 % over a plucked or short sound; Chord with `Arp feel` Triplet for stabs; `Arp latch` On with Played at 1/4 for a pad that plays itself. Hold the chord from a pattern or the keyboard.
- Sequenced lines: `Seq mode` Notes, `Voice mode` Legato with a little glide for a tied acid line, steps with velocities 70–120 and `R1 source` Seq velocity to `F1 cutoff` for accents; probabilities of 50–70 % on some steps and a fixed `Arp seed` for a pattern that varies but repeats. `Seq mode` Mod with `Seq slew` 70 % and `R1 source` Seq step → `F1 cutoff` makes a stepped filter sweep under chords you play.
- Expressive MIDI controllers: `MPE` On, `R1` MPE pressure → `F1 cutoff`, `R2` MPE slide → `A position`; set the controller's bend range to match `MPE bend range`.
- Effects: a reverb beside the dry: `FX1 type` Verb (slot 1 is in bus A), `FX1 p4` (Decay) 50-70 %, `FX return A` -5 to -8 dB. A saturator or an EQ on everything: slots 8-10 (main). A delay in bus A: `FX1 p2` is Sync (50 % is 1/8) and `FX1 p3` Feel (50 % is Dotted), `FX1 p6` Feedback. Parallel clipping: a Clipper in bus A at `FX1 mix` 100 % and `FX return A` -9 dB. Width without a wobble: a Dimension in bus A (`FX1 p1` 3 voices, `FX1 p3` 50-60 % depth), `FX return A` -3 dB. Lo-fi: a Bitcrusher in the main chain, `FX8 p1` 30 % (about 6 bits), `FX8 p2` 38 % (about 5 kHz), Anti-alias on; Wobble `FX8 p9` below 50 % for tape wow. Phasing: a Freq shift with `FX5 p1` 52-56 % (under 2 Hz in the 1 kHz Range) and feedback `FX5 p4` 60 % in bus B. Octave echoes: a Pitch delay (`FX1 p2` 50 % is +12 st, 17 % is -12 st) in bus A at `FX1 p4` 55-60 % feedback.

## Gotchas

- Routes are parameters (source, destination, amount), so `param_set` and presets set them like any other; a route with source or dest Off, or amount 0 %, does nothing.
- Free LFOs (retrig Off) don't take the LFO-rate destinations; per-note ones do.
- The arpeggiator and the sequencer play only while there are held notes (the sequencer's Mod mode runs always); the sequencer's Notes mode takes over from the arpeggiator. A latched arpeggio ends with an All notes off or a transport stop. Changing `Arp mode` or `Seq mode` releases what they were sounding.
- Steps are in the song, so `param_set` can't change them: import a `sequence` file (see above), or edit them on the Arp & Seq page.
- Held notes and the grid: a key played while the song is stopped starts the steps at that moment; while the song plays the first step after the key is the next grid step, and note-ons land on exact samples whatever the block size.

- Freshly added it plays a saw (`A table` Basic at 67 %) through a Ladder 24; B and C are Off.
- A silent slot still renders when it is another's modulator (its level only mutes it).
- `Sub octave` takes the text "0", "-1" or "-2" (quoted), not a number.
- Tables are built in the background on first use; renders wait for them (up to 5 s).
- Unison costs CPU per voice: 16 notes × 3 slots × 8 voices run some 3–6× real time on a desktop core; prefer fewer, wider voices.
- Combs with feedback near 100 % ring loud: watch the peaks.
- Grains cost CPU per grain (each read with the sinc), so a slot of a note keeps at most 10 grains live and the whole bank shares 160 between its granular slots (never fewer than 4 a slot); past the cap the oldest grain fades out over a millisecond or so. 100 grains a second at 300 ms on 16 notes runs about twice real time on a desktop core; keep rate × size under 10 for polyphony, or fewer voices. The first 20 s of a loaded WAV are kept; longer ones are cut.
- Samples are built in the background like tables; renders wait for them. A Sample slot with `A loop` Off ends silent after the sample ends, whatever the amp envelope does.
- Samples are normalised to a peak of 0.9 and decaying ones (Bell, Pluck) are quiet by RMS: turn `Volume` up or layer a second slot.
- The effects rack builds an engine in the background the first time a slot takes a type (renders wait for it); until then the slot passes its input. A slot's `FX1 mix` ramps over 5 ms and a changed type fades in. The Clipper has a latency (its oversampling and ceiling guard, a few hundred samples) and so has the Bitcrusher (a fixed 2 ms, 96 samples at 48 kHz, the centre of its wobble delay): the rack delays the dry signals and parallel paths to match, so everything stays in time, and the rack reports the latency to the host (`GetLatency`), whose machine delay compensation delays the other paths into the mix by it. The rack stops running when every slot is Off (its tails end then); it keeps the machine awake while a tail sounds, up to 5 s after it falls below -100 dB.
- The chains are in the song's machine data, not parameters: `param_set` cannot move a slot between chains; import an `fx_routing` file or use the Effects page.

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
| "Keys — Glass Marimba" | a struck bar, a short ring | `A force`, `A ring time` |
| "Bell — Struck Bronze" | a bell body, a hard strike, seven seconds | `A material`, `A body size` |
| "Pluck — Nylon String" | a plucked string, two voices, upper modes dying fast | `A hf damp`, `A force` |
| "Pad — Bowed Glass" | a bowed string of a hard material, three voices, a slow LFO on the place | `A material`, `R1 amount` |
| "Lead — Breath Pipe" | a breath through a tube, vibrato from LFO 1, the mod wheel moving the place | `A position`, `R1 amount` |
| "Bass — Skin Drum" | a struck membrane an octave down, a large body | `A body size`, `A ring time` |
| "Keys — Steel Plate" | a hard struck plate, 48 modes, a long ring | `A material`, `A modes` |
| "Pad — Bowed Bell and Pipe" | a bowed bell and an octave-up bowed tube, slowly moving | `B level`, `A position` |
| "Keys — Glass Bell" | the bell sample with a Formants table an octave up | `B level` |
| "Arp — Glass Pluck Up" | a plucked string up two octaves in sixteenths | `Arp octaves`, `Arp gate` |
| "Arp — Saw Up Down" | detuned saws up and down in swung eighths, a resonant filter envelope | `Arp swing`, `F1 reso` |
| "Arp — Triplet Chord Stabs" | the whole chord in triplet eighths, short | `Arp gate`, `Flt decay` |
| "Arp — Random Bells" | struck bells in a seeded random order over three octaves | `Arp seed`, `Arp gate` |
| "Arp — Dotted Organ Down" | the organ set down in dotted eighths | `Arp feel`, `A tilt` |
| "Arp — Latched Played Pad" | a pad arpeggiated in played order, latched | `Arp latch`, `Arp rate` |
| "Seq — Acid Line" | a 16-step legato line with ties, rests and accents on the key held | `Seq mode`, `R1 amount` |
| "Seq — Pulse Bass" | an 8-step swung pulse bass | `Seq swing`, `A pulse width` |
| "Seq — Filter Steps" | the sequencer as modulation: stepped, slewed filter and position under held chords | `Seq slew`, `R1 amount` |
| "Seq — Probability Pluck" | a plucked 16-step pattern with probabilities of 50–100 % | `Arp seed`, `A force` |
| "MPE — Expressive Lead" | pressure opens the filter and the level, slide moves the position, per-note bend | `MPE bend range`, `R1 amount` |
| "Rack — Bell In A Hall" | the mirror bell into a reverb on bus A | `FX1 p4`, `FX return A` |
| "Rack — Dotted Echo Keys" | the organ into a dotted echo on bus A | `FX1 p6`, `FX return A` |
| "Rack — Saturated Bass" | the detuned bass through a Clipper in the main chain at 80 % | `FX8 p1`, `FX8 mix` |
| "Rack — Sweeping Choir" | the glass choir through a Filter with a slow LFO in the main chain | `FX8 p10`, `FX8 p7` |
| "Rack — Tilted And Squeezed Pluck" | an EQ then a compressor in the main chain (moved there by the routing) | `FX1 p2`, `FX2 p2` |
| "Rack — Duo Plate Keys" | the Duo layout: an EQ insert and a reverb on the aux | `FX8 p4`, `FX return A` |
| "Rack — Parallel Crush Bass" | a Clipper in bus A (parallel) and a small reverb in bus B | `FX return A`, `FX1 p1` |
| "Rack — Delayed Choir" | an echo in bus B and a light reverb in the main chain | `FX5 p6`, `FX return B` |
| "Rack — Dimension Pad" | the glass choir widened by a 3-voice Dimension on bus A | `FX1 p3`, `FX return A` |
| "Rack — Lo-Fi Crunch Keys" | the dotted organ through a Bitcrusher in the main chain: 6 bits, 5 kHz, a little flutter | `FX8 p1`, `FX8 p2` |
| "Rack — Worn Tape Bits" | the bell on a gently crushed, wowing tape, on bus A | `FX1 p9`, `FX1 p1` |
| "Rack — Barber Shift Pad" | the choir under a slow, fed-back frequency shift on bus B | `FX5 p1`, `FX5 p4` |
| "Rack — Shimmer Echoes" | the bell into octave-up echoes that climb each repeat | `FX1 p4`, `FX return A` |
| "Rack — Falling Octave Echoes" | the dotted organ into octave-down echoes that sink | `FX1 p2`, `FX1 p4` |
