## How it sounds

A hybrid synth with three engine slots (A, B, C). Each plays a morphing wavetable of 64 frames (the Wavetable engine) or a classic analog wave (the Analog engine), plays a sample (the Sample engine: built-in or your own, looped or one-shot), scatters grains of one (the Granular engine) adds up to 128 sine partials (the Harmonic engine, additive) or rings a body of up to 64 modes that a mallet, a pluck or a bow excites (the Modal engine), with up to 16 unison voices and two warps in series; plus a sub and a noise. Every note has two filters (ladder, state-variable, comb) that run from serial to parallel. Voices run at Standard 2× (the default) or High 4×: harmonics reach 18 kHz at every pitch below 9 kHz and a plain saw's alias floor is −92 dB or lower. Good for wide unison leads, evolving pads, FM and sync basses, plucks and bells.

The signal flow (every engine, the filters, modulation, arpeggiator and sequencer, the effects and imports) and how to use it are in `guide(topic="dye-essence-in-depth")`.

## Gotchas

- The effects rack (ten slots `FX1`…`FX10`; Trio and Duo chains; EQ, Filter, Comp, Clipper, Delay, Verb, Dimension, Bitcrusher, Freq shift, Pitch delay, Convolution, Vocoder) is in `guide(topic="dye-essence-effects")`. Its chains are machine data, so `param_set` cannot move a slot between chains.
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

## Starting points per category

There are about 340 factory presets, named `Category — Name`; `preset_list(machine, category="Bass")` pages one category (`search` filters by words, `limit` and `offset` page), and the generated manual lists every name under its category. The `Rack — …` presets (the effects rack's types) are listed in `guide(topic="dye-essence-effects")`. Each category below starts well from the preset named; the second is a different engine.

| Category | Start with | Or | Tweak first |
|---|---|---|---|
| Init | "Init — Basic" | | everything |
| Bass | "Bass — Analog Saw" | "Bass — FM Growl" | `F1 cutoff`, `A warp 1 amt` |
| Lead | "Lead — Wide Unison" | "Lead — Reed Morph" | `A detune`, `A position` |
| Pad | "Pad — Analog Strings" | "Pad — Glass Choir" | `A position`, `F1 cutoff` |
| Keys | "Keys — Drawbar Organ" | "Keys — Glass Marimba" | `A damp`, `A force` |
| Pluck | "Pluck — Double String" | "Pluck — Nylon String" | `B fine`, `A hf damp` |
| Bell | "Bell — Struck Bronze" | "Bell — Stretched Glass" | `A material`, `A stretch` |
| Arp | "Arp — Glass Pluck Up" | "Arp — Random Bells" | `Arp octaves`, `Arp gate` |
| Seq | "Seq — Acid Line" | "Seq — Filter Steps" | `Seq mode`, `R1 amount` |
| Texture | "Texture — Frozen Cloud" | "Texture — Comb Noise" | `A position`, `F1 reso` |
| FX | "FX — Bell Rain" | "FX — Reverse Pluck" | `A grain pitch`, `A reverse` |
| Drum | "Drum — Sine Kick" | "Drum — Noise Snare" | `Amp decay`, `A level` |
| Vocal | "Vocal — Sung Choir" | "Vocal — Formant Voice" | `A position`, `R1 amount` |
| Cinematic | "Cinematic — Rising Swell" | "Cinematic — Stone Impact" | `Amp attack`, `F1 cutoff` |
| Lo-Fi | "Lo-Fi — Dusty Keys" | "Lo-Fi — Tape Pad" | the Bitcrusher and Delay slots |
| MPE | "MPE — Expressive Lead" | | the MPE route amounts |

Generated presets carry a one-line comment saying what is in them (`preset_list` returns it). Most name the effect in the rack that shapes them ("in a Hall", "Crushed", "Chorused"): the slot's `FXn type` and its ten parameters are the thing to turn.
