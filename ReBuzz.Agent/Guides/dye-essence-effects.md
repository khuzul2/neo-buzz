# Dye Essence effects

Dye Essence's effects rack: ten slots after the amp, in two layouts, hosting the engines of six Neo machines and six effects of its own. Read the machine's page first: `guide(machine="Dye Essence")`.

## The rack

- Effects rack (after the amp, all Off by default): ten slots `FX1 type` ... `FX10 type` (Off, EQ, Filter, Comp, Clipper, Delay, Verb, Dimension, Bitcrusher, Freq shift, Pitch delay, Convolution, Vocoder: the engines of Neo EQ (four bands: a low shelf, two bells, a high shelf), Neo Filter, Neo Comp, Neo Clipper, Neo Delay and Neo Verb, then six effects of the rack's own), each with `FX1 mix` (dry to wet, 100 % by default: set a delay or reverb below that in a serial chain) and ten generic parameters `FX1 p1` ... `FX1 p10`, 0-100 %, whose names and units the slot's type gives (the table). 50 % is the engine's own default for the parameter (the EQ's bands at 100 Hz, 500 Hz, 2.5 kHz and 8 kHz, 0 dB). A choice (a mode, a sync value) spans 0-100 % in equal steps with its default at 50 %: read the value text back to see what a percentage picked. Three chains: `FX layout` Trio is bus A and bus B (parallel: `FX send A` and `FX send B` take the synth in, `FX return A` and `FX return B` bring it back, added to the dry) and a main chain (serial, on the sum); Duo is insert 1 and insert 2 in series, then an aux (`FX send A` and `FX return A`). Slots 1-4 start in bus A (insert 1), 5-7 in bus B (insert 2), 8-10 in main (aux), in order; move them with `machine_data_import(machine="Dye", kind="fx_routing", path=...)` of a text file such as "1 2 3 4 | 5 6 7 | 8 9 10" (bus A or insert 1 | bus B or insert 2 | main or aux; each slot once, in order) or on the Effects page. All slots Off run nothing: the output is the synth's, bit for bit.

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
| Convolution | IR (Small room 0 %, Room 12 %, Chamber 25 %, Plate 38 %, Hall 50 %, Cathedral 75 %, User 100 %), Size (x0.25-1.5 time stretch, x1 at 50 %), Pre-delay (1-250 ms), Decay (50 % as recorded; below it the tail slopes down to -40 dB, above it up by 9 dB), Damping (50 % none; below it the tail darkens with time, above it thins), Low cut, High cut, Width (0 mono, x1 at 50 %, x2), Reverse, Output |
| Vocoder | Modulator (Internal voice, Sample), Bands (8-32, 16 at 50 %), Formant (-12 to +12 st, 0 at 50 %), Low (80-400 Hz), High (3-16 kHz), Attack, Release, Sibilance, Speed (x0.25-4: the internal voice's syllables or the sample's playback), Output |

Dimension, Bitcrusher, Freq shift and Pitch delay return the wet signal only (the slot's mix blends it): the dimension, a 3-voice chorus with the two channels modulated half a cycle apart, widens a mono source (left-right correlation about 0.3 for noise); the bitcrusher's anti-alias is an eighth-order filter before the hold and a fourth-order after it (a 1 kHz sine at 12 kHz: strongest spurious line -20 dB without, -50 dB with; a 9.3 kHz sine above the new rate's half: alias at -1 dB without, -54 dB with); the frequency shifter is a single-sideband (Bode) shifter whose opposite sideband is 61 to 64 dB down from 500 Hz to 15 kHz; the pitch delay shifts with a two-tap crossfaded delay line, its first echo on the time set. Each is a kernel in NeoBuzz.Dsp that any machine can use.

Convolution (zero latency: a 64-tap time-domain head, then FFT partitions of 64, 256, 1024 and 4096 samples, within -130 dB of the direct sum) and Vocoder are wet only too, and are kernels in NeoBuzz.Dsp (`StereoConvolver`, `ChannelVocoder`) that any machine can use. The six built-in responses are made by code (no recordings, no licence): 0.35, 0.8, 1.4, 2.8 and 5.6 s for the rooms and halls, the plate 2 s. Your own: `machine_data_import(machine="Dye", kind="ir", path="room.wav", item=2)` loads a WAV (mono or stereo, any rate, at most 480000 frames: 10 s at 48 kHz, the rest cut; kept in the song) as the one imported response, sets slot `item` (0-9 for FX1-FX10) to Convolution and its IR to User (every Convolution slot on User shares it). The response is resampled to the host's rate, shaped by Size, Pre-delay, Decay, Damping, the cuts, Width and Reverse, and normalised to unit energy. A change builds a new convolver in the background (the old one plays on and crossfades over 2048 samples; a 1 s response costs about 1.3 % of a core, 4 s 1.5 %). The vocoder's carrier is the synth (the signal into the slot); its modulator is the internal voice (a deterministic robot of five vowels, 3 syllables a second) or a sample, `machine_data_import(machine="Dye", kind="modulator", path="speech.wav", item=7)` (mono mix, at most 20 s at 48 kHz, looped, peak-normalised), which sets slot `item` to Vocoder and Modulator to Sample. Dye Essence is a generator, so it has no second input for an outside sidechain. Bands are log-spaced from Low to High (16 bands, 120 Hz to 10 kHz: 120, 161, 216, 291, 390, 524, 704, 945, 1269, 1705, 2289, 3075, 4129, 5545, 7446, 10000 Hz); Formant moves the carrier's bands against the modulator's; Sibilance lets the modulator's own highs through when they dominate it. 16 bands cost about 4 % of a core, 32 bands 9 %.

- Effects: a reverb beside the dry: `FX1 type` Verb (slot 1 is in bus A), `FX1 p4` (Decay) 50-70 %, `FX return A` -5 to -8 dB. A saturator or an EQ on everything: slots 8-10 (main). A delay in bus A: `FX1 p2` is Sync (50 % is 1/8) and `FX1 p3` Feel (50 % is Dotted), `FX1 p6` Feedback. Parallel clipping: a Clipper in bus A at `FX1 mix` 100 % and `FX return A` -9 dB. Width without a wobble: a Dimension in bus A (`FX1 p1` 3 voices, `FX1 p3` 50-60 % depth), `FX return A` -3 dB. Lo-fi: a Bitcrusher in the main chain, `FX8 p1` 30 % (about 6 bits), `FX8 p2` 38 % (about 5 kHz), Anti-alias on; Wobble `FX8 p9` below 50 % for tape wow. Phasing: a Freq shift with `FX5 p1` 52-56 % (under 2 Hz in the 1 kHz Range) and feedback `FX5 p4` 60 % in bus B. Octave echoes: a Pitch delay (`FX1 p2` 50 % is +12 st, 17 % is -12 st) in bus A at `FX1 p4` 55-60 % feedback. A real room: `param_set(machine="Dye", changes=[{"param": "FX1 type", "value": "Convolution"}, {"param": "FX1 p1", "value": "37.5 %"}, {"param": "FX return A", "value": "-6 dB"}])` (the plate in bus A), or `machine_data_import(machine="Dye", kind="ir", path="my-room.wav", item=0)` for yours. A robot voice: `param_set(machine="Dye", changes=[{"param": "FX8 type", "value": "Vocoder"}, {"param": "FX8 p2", "value": "70 %"}])` over a bright, thick pad, then `preset_load(machine="Dye", preset="Rack — Robot Choir")` to hear one set up.
- The effects rack builds an engine in the background the first time a slot takes a type (renders wait for it); until then the slot passes its input; a Convolution slot also waits for its first response, and a Vocoder on Sample with nothing imported plays the internal voice. A slot's `FX1 mix` ramps over 5 ms and a changed type fades in. The Clipper has a latency (its oversampling and ceiling guard, a few hundred samples) and so has the Bitcrusher (a fixed 2 ms, 96 samples at 48 kHz, the centre of its wobble delay; Convolution and the Vocoder have none): the rack delays the dry signals and parallel paths to match, so everything stays in time, and the rack reports the latency to the host (`GetLatency`), whose machine delay compensation delays the other paths into the mix by it. The rack stops running when every slot is Off (its tails end then); it keeps the machine awake while a tail sounds, up to 5 s after it falls below -100 dB.
- The chains are in the song's machine data, not parameters: `param_set` cannot move a slot between chains; import an `fx_routing` file or use the Effects page.

## Rack presets

| Preset | What | Tweak first |
|---|---|---|
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
| "Rack — Small Room Keys" | the dotted organ in a small built-in room, bus A | `FX1 p1`, `FX return A` |
| "Rack — Plate Bell" | the bell on a bright plate, bus A | `FX1 p1`, `FX1 p3` |
| "Rack — Hall Pad" | the glass choir in a hall with a long pre-delay, bus A | `FX1 p3`, `FX return A` |
| "Rack — Cathedral Choir" | the choir in a cathedral, 42 % in the main chain, darkened | `FX8 p5`, `FX8 mix` |
| "Rack — Reverse Room Swell" | the bell into a stretched, reversed room that swells | `FX1 p9`, `FX1 p2` |
| "Rack — Robot Choir" | the choir through the internal-voice vocoder (22 bands), main chain | `FX8 p2`, `FX8 p9` |

## Checklist

- [ ] The slot has the type you meant: `param_get(machine=…, params=["FX1 type"])` reads it back (and its value text names it).
- [ ] The slot is in the chain you meant: `param_get(machine=…, params=["FX layout", "FX send A", "FX return A", "FX1 mix"])`; a bus slot needs its send and return up, a main-chain slot its mix.
- [ ] Each percentage picked what you meant: `param_get(machine=…, params=["FX1 p1", "FX1 p2"])` and read the value text of every `FX1 p…` you set.
- [ ] It sounds as intended: `render`, then `analyze(source=…)`; a parallel path adds level, so check the peaks.
- [ ] A user response or modulator took: `machine_data_import(machine="Dye", kind="ir", path=…, item=0)` answers with the frames and rate it kept; `param_get(machine="Dye", params=["FX1 type", "FX1 p1"])` reads Convolution and User back.
- [ ] A convolution tail is as long as intended: `render`, then `analyze(source=…)` over the tail; shorten it with `FX1 p2` or `FX1 p4` below 50 % rather than the mix.
