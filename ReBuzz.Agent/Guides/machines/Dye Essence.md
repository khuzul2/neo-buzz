## How it sounds

A hybrid synth with three engine slots (A, B, C). Each slot plays one of: a morphing wavetable (64 frames), a classic analog wave, a sample (built-in or your own), grains of a sample, up to 128 additive sine partials, a resonating body of up to 64 modes struck, plucked or bowed, or the resynthesis of a sample from its partials and noise (the Spectral engine: speed, pitch and timbre move apart). Each slot has up to 16 unison voices and two warps in series (bend, sync, FM, PM, AM, ring and more); a sub and a noise sit beside them. Every note passes two filters (ladder, state-variable, comb) from serial to parallel, then a ten-slot effects rack. Voices run at Standard 2× (default) or High 4×: at 2× a plain saw's alias floor is -92 dB or lower. Wavetable and Analog slots are the lightest; Harmonic, Modal, Spectral and Granular cost more.

Full signal flow, every engine, modulation, arpeggiator, sequencer and imports: `guide(topic="dye-essence-in-depth")`. The Spectral engine and the sample imports (partials into Harmonic, an impulse response into Modal): `guide(topic="dye-essence-analysis")`. The effects rack (Trio and Duo chains, twelve types): `guide(topic="dye-essence-effects")`.

## Use it for

- Wide unison leads, evolving pads, FM and sync basses, plucks, bells and glass keys, frozen granular textures, sung or formant voices, arpeggios and acid-style sequences, expressive MPE playing.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| A engine | Wavetable | the default saw-like start; Analog is lighter on CPU |
| A position | 67 % | where the table is read; sweep or modulate it for movement |
| A unison | 5 | 1 is narrow; each voice costs CPU in direct proportion |
| A detune | 20 ct | gentle shimmer; 40-100 ct is a thick, clearly detuned stack |
| F1 cutoff | 3000 Hz | the main tone control; F1 env moves it per note |
| F1 reso | 15 % | a mild peak; combs near 100 % ring loud |
| Amp release | 0.4 s | 0.02 s for stabs and plucks, 2 s or more for pads |
| Quality | Standard 2× | High 4× only for hard FM, PM and sync, at twice the CPU |

Modulation is a route table: `R1 source`, `R1 dest` and `R1 amount` (16 routes). A route with source or dest Off, or amount 0 %, does nothing.

## Starting points

There are about 340 factory presets, named `Category — Name`. `preset_list(machine="Dye Essence", category="Bass")` pages a category (`search` filters by words). Each row starts well; the second preset is a different engine.

| Category | Start with | Or | Tweak first |
|---|---|---|---|
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

Also "Init — Basic" (a plain saw) and the `Rack — …` presets, one per effect type, listed in the effects guide. Most generated presets name the effect that shapes them in their comment; the slot's `FXn type` and ten parameters are the thing to turn.

## Pitfalls

- Freshly added, it plays a saw through a Ladder 24; B and C are Off. A silent slot still renders when another uses it as its modulator.
- CPU per engine: unison voices multiply it (16 notes × 3 slots × 8 voices runs about 8-11× real time on a desktop core); Harmonic costs per partial (128 partials in 8 unison voices, about 2×); Modal costs per mode; Granular per live grain; High 4× doubles it all. Prefer fewer, wider voices.
- Granular: a note keeps at most 10 live grains per slot and the whole bank 160; past that the oldest fades out. Keep `A grain rate` × `A grain size` under 10 for polyphony.
- Samples and tables build in the background on first use; renders wait for them (up to 5 s). The first 20 s of a loaded WAV are kept. With `A loop` Off a Sample slot ends silent when the sample ends.
- Samples are normalised to a peak of 0.9, and decaying ones (Bell, Pluck) are quiet: raise `Volume` or layer a second slot.
- `Sub octave` takes the text "0", "-1" or "-2" (quoted), not a number.
- Free LFOs (retrig Off) take no LFO-rate destinations; per-note ones do.
- The arpeggiator and sequencer play only while notes are held (the sequencer's Mod mode always runs); Notes mode takes over from the arpeggiator. Steps live in the song: import a `sequence` file or edit them on the Arp & Seq page, since `param_set` cannot change them.
- Effect chains are machine data: `param_set` cannot move a slot between chains.

## Pairs well with

- Neo Verb or Neo Delay after it for space; Neo Comp or Neo Limiter on a bus of several Dye parts; Neo EQ to carve the low end under a sub.
- Learning the ideas one at a time: Neo Learn Subtractive, Neo Learn FM, Neo Learn Wavetable, Neo Learn Additive, Neo Learn Granular and Neo Learn Physical are each a small Dye patch.

## Checklist

- [ ] `preset_load(machine="Dye Essence", preset="Pad — Analog Strings")`, then `analyze(source="Dye Essence", metrics=["peaks"])` shows a peak below 0 dB.
- [ ] `param_get(machine="Dye Essence")` reads back `Quality`, `A unison` and the routes you set; compare `app_status` (its `cpu` figure) before and after raising `A unison` or switching to High 4×.
- [ ] After setting a route, `param_get` shows its source and dest are not Off and its amount is not 0 %.
