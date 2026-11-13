## How it sounds

A hybrid synth with three engine slots (A, B, C). Each plays a morphing wavetable of 64 frames (the Wavetable engine) or a classic analog wave (the Analog engine), plays a sample (the Sample engine: built-in or your own, looped or one-shot), scatters grains of one (the Granular engine) adds up to 128 sine partials (the Harmonic engine, additive) or rings a body of up to 64 modes that a mallet, a pluck or a bow excites (the Modal engine), with up to 16 unison voices and two warps in series; plus a sub and a noise. Every note has two filters (ladder, state-variable, comb) that run from serial to parallel. Voices run at Standard 2× (the default) or High 4×: harmonics reach 18 kHz at every pitch below 9 kHz and a plain saw's alias floor is −92 dB or lower. Good for wide unison leads, evolving pads, FM and sync basses, plucks and bells.

The signal flow (every engine, the filters, modulation, arpeggiator and sequencer, the effects and imports) and how to use it are in `guide(topic="dye-essence-in-depth")`.

## Gotchas

- The effects rack (ten slots `FX1`…`FX10`; Trio and Duo chains; EQ, Filter, Comp, Clipper, Delay, Verb) is in `guide(topic="dye-essence-effects")`, with its "Rack" presets. Its chains are machine data, so `param_set` cannot move a slot between chains.
- Routes are parameters (source, destination, amount), so `param_set` and presets set them like any other; a route with source or dest Off, or amount 0 %, does nothing.
- Free LFOs (retrig Off) don't take the LFO-rate destinations; per-note ones do.
- The arpeggiator and the sequencer play only while there are held notes (the sequencer's Mod mode runs always); the sequencer's Notes mode takes over from the arpeggiator. A latched arpeggio ends with an All notes off or a transport stop. Changing `Arp mode` or `Seq mode` releases what they were sounding.
- Steps are in the song, so `param_set` can't change them: import a `sequence` file (see `guide(topic="dye-essence-in-depth")`), or edit them on the Arp & Seq page.
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
