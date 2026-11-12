# Dye Essence effects

Dye Essence's effects rack: ten slots after the amp, in two layouts, hosting the engines of six Neo machines. Read the machine's page first: `guide(machine="Dye Essence")`.

## The rack

- Effects rack (after the amp, all Off by default): ten slots `FX1 type` ... `FX10 type` (Off, EQ, Filter, Comp, Clipper, Delay, Verb: the engines of Neo EQ (four bands: a low shelf, two bells, a high shelf), Neo Filter, Neo Comp, Neo Clipper, Neo Delay and Neo Verb), each with `FX1 mix` (dry to wet, 100 % by default: set a delay or reverb below that in a serial chain) and ten generic parameters `FX1 p1` ... `FX1 p10`, 0-100 %, whose names and units the slot's type gives (the table). 50 % is the engine's own default for the parameter (the EQ's bands at 100 Hz, 500 Hz, 2.5 kHz and 8 kHz, 0 dB). A choice (a mode, a sync value) spans 0-100 % in equal steps with its default at 50 %: read the value text back to see what a percentage picked. Three chains: `FX layout` Trio is bus A and bus B (parallel: `FX send A` and `FX send B` take the synth in, `FX return A` and `FX return B` bring it back, added to the dry) and a main chain (serial, on the sum); Duo is insert 1 and insert 2 in series, then an aux (`FX send A` and `FX return A`). Slots 1-4 start in bus A (insert 1), 5-7 in bus B (insert 2), 8-10 in main (aux), in order; move them with `machine_data_import(machine="Dye", kind="fx_routing", path=...)` of a text file such as "1 2 3 4 | 5 6 7 | 8 9 10" (bus A or insert 1 | bus B or insert 2 | main or aux; each slot once, in order) or on the Effects page. All slots Off run nothing: the output is the synth's, bit for bit.

| Type | p1 ... p10 |
|---|---|
| EQ | Low freq, Low gain, Mid 1 freq, Mid 1 gain, Mid 1 Q, Mid 2 freq, Mid 2 gain, Mid 2 Q, High freq, High gain |
| Filter | Type, Slope, Cutoff, Resonance, Drive, Env amount, LFO rate, LFO sync, LFO shape, LFO depth |
| Comp | Mode, Threshold, Ratio, Knee, Attack, Release, SC low cut, Makeup, Character, Link |
| Clipper | Drive, Ceiling, Shape, Knee, Oversampling, Bands, Crossover 1, Crossover 2, Stereo, Output |
| Delay | Mode, Sync, Feel, Time, Offset, Feedback, Low cut, High cut, Mod depth, Width |
| Verb | Mode, Pre-delay, Size, Decay, Low decay, Damping, Shimmer, Width, Low cut, High cut |

- Effects: a reverb beside the dry: `FX1 type` Verb (slot 1 is in bus A), `FX1 p4` (Decay) 50-70 %, `FX return A` -5 to -8 dB. A saturator or an EQ on everything: slots 8-10 (main). A delay in bus A: `FX1 p2` is Sync (50 % is 1/8) and `FX1 p3` Feel (50 % is Dotted), `FX1 p6` Feedback. Parallel clipping: a Clipper in bus A at `FX1 mix` 100 % and `FX return A` -9 dB.
- The effects rack builds an engine in the background the first time a slot takes a type (renders wait for it); until then the slot passes its input. A slot's `FX1 mix` ramps over 5 ms and a changed type fades in. The Clipper has a latency (its oversampling and ceiling guard, a few hundred samples): the rack delays the dry signals and parallel paths to match, so everything stays in time, and the rack reports the latency to the host (`GetLatency`), whose machine delay compensation delays the other paths into the mix by it. The rack stops running when every slot is Off (its tails end then); it keeps the machine awake while a tail sounds, up to 5 s after it falls below -100 dB.
- The chains are in the song's machine data, not parameters: `param_set` cannot move a slot between chains; import an `fx_routing` file or use the Effects page.

## Checklist

- [ ] The slot has the type you meant: `param_get(machine=…, params=["FX1 type"])` reads it back (and its value text names it).
- [ ] The slot is in the chain you meant: `param_get(machine=…, params=["FX layout", "FX send A", "FX return A", "FX1 mix"])`; a bus slot needs its send and return up, a main-chain slot its mix.
- [ ] Each percentage picked what you meant: `param_get(machine=…, params=["FX1 p1", "FX1 p2"])` and read the value text of every `FX1 p…` you set.
- [ ] It sounds as intended: `render`, then `analyze(source=…)`; a parallel path adds level, so check the peaks.
