# Neo Learn — Specification

Status: Approved by the user 2026-11-12; L0 in progress. Source: the user's "NeoSimpleSynth" brief, sharpened for NeoBuzz.
Controls and Explain text for all 13 machines: [Appendix A](neo-learn-controls.md).

## 1. Summary

**Neo Learn** is a family of 13 small synthesizers. Each one teaches one way of making sound.

- **Same controls on every machine.**
  - 6 core knobs for the synthesis method.
  - 6 global knobs: Attack, Decay, Sustain, Release, Unison and Output.
  - An interactive picture: dragging it moves core knobs 1 and 2.
  - An **Explain strip** that says how the control you touch changes the sound, and what you will hear.
- **Built on Dye Essence's engine.** Each machine is a fixed Dye Essence patch that its six core knobs drive (macros). Nothing is written twice.
  - Methods Dye lacks are written once in the shared engine (§4), so Dye Essence can adopt them later: string, vowel filter, wavefolder, low-pass gate, chorus, grit and room.
- **The agent sees what you see.** Every control's id, unit, range and Explain text are in `machine_describe`. Agent changes show in the Explain strip with an **Agent** badge.

## 2. Decisions

| # | Decision | Why |
|---|---|---|
| 1 | The name is **Neo Learn ‹Type›**, with short box names "Learn ‹Short›" (§3). | The user's choice. |
| 2 | Machines are Dye Essence patches driven by macros, not separate engines (option A). | There is one sound engine to test and keep bit-identical. Dye already covers 9 of the 13 methods. |
| 3 | Explain is a feature of the Neo kit (§5.3). Neo Learn is its first user. | Other Neo machines can adopt it later. Filling in their text is not part of this spec. |
| 4 | No limiter on each machine. Output is a gain, and a clip light shows anything above 0 dBFS. | NeoBuzz mixes in floating point. A "transparent" limiter either colours the sound or adds delay. |
| 5 | Explain sources are **Manual**, **Agent** and **Automation** (patterns, peer machines, MIDI controllers). | NeoBuzz has more writers than "MCP". The agent tools are built in, so there is no external server. |
| 6 | The Sampler is slim. It plays one sample (Dye's five built-ins, or your WAV) with Start, Tone, Loop, Grit and Room. There are no multisampled instruments and no formant lock. | Those need content we don't have, plus licences, hundreds of MB and research-grade DSP. |
| 7 | Spectral waits for Dye Essence P5. Its sources are analysed offline, so a note starts within one hop and is not delayed by a 2048-point window. | A live 2048-point FFT adds about 46 ms at 44.1 kHz and smears attacks. |
| 8 | No trademarked names anywhere: no CZ/DCW, Buchla, Serge, Vactrol, Prophet, and so on. Appendix A is already clean. | House rule. |
| 9 | The String machine's decay is a time (T60 in seconds) from 0.05 to 20 s, not a loop coefficient. | A coefficient makes decay depend on pitch. |
| 10 | Every machine is grouped under one of four categories: **Classic, Sample, Shaping, Acoustic**. | It groups methods by how they work, which helps you learn them. |

## 3. The 13 machines

"Built on" names Dye Essence parts: slots A, B and C (engine types Wavetable, Analog, Sample, Granular, Harmonic, Modal), Sub, Noise, filters F1 and F2, warps, Env2 and the LFOs. "Adds" lists what the shared engine gains. Every addition is append-only and leaves Dye Essence's pinned hashes unchanged.

| Machine (box name) | Category | Built on | Adds | Phase |
|---|---|---|---|---|
| Subtractive (Learn Sub) | Classic | A: Analog; Sub; F1 low-pass 24 with drive; slot drift; filter envelope | none | **L0 pilot** |
| FM (Learn FM) | Classic | A: sine carrier. B and C: modulators through the PM warps. Env2 drives the index; velocity drives the index | exact-ratio pitch mode for a modulator slot; PM self-feedback | L1 |
| Wavetable (Learn WT) | Classic | A: Wavetable (Dye's 10 tables plus User); warps Bend (left) and Sync (right), run at High quality; LFO1 drives position; Sub sine | none | L1 |
| Additive (Learn Add) | Classic | A: Harmonic (partials, tilt, odd/even, stretch) | comb mask; per-partial shimmer | L1 |
| Granular (Learn Grain) | Sample | A: Granular (scan, size, rate, spray, jitter) | continuous window morph from trapezoid to Gaussian; octave grains | L1 |
| Physical (Learn Phys) | Acoustic | A: Modal (Dye P3b, in progress) | exciter morph mallet → pick → bow, if P3b ships separate exciters | L1, after P3b |
| String (Learn String) | Acoustic | new **String** slot type (Karplus-Strong) | String engine: fractional delay, damping, dispersion all-pass, pick-position comb, sympathetic strings | L2 |
| Formant (Learn Voice) | Acoustic | A: Analog pulse (glottal); Noise (breath); Env2 drives the vowel | **Vowel** filter type: 4 band-passes over vowel tables, with tract scale and Q | L2 |
| Wavefolder (Learn Fold) | Shaping | A: Analog sine/triangle; FM warp from B | **Fold** shaper (1 to 6 stages, symmetry, run at High quality); **LPG** filter type with strike | L2 |
| Phase Distortion (Learn PD) | Shaping | A: cosine with Bend/Squeeze, then Window-sync warps (High quality); Ring warp from B; Env2 drives the bend | **Chorus** effect | L2 |
| Vector (Learn Vector) | Shaping | Corners W = A, E = B, N = C, S = Sub (+ Noise); 4 corner palettes | orbit generator (in the machine) | L2 |
| Sampler (Learn Sampler) | Sample | A: Sample (start, loop); F1 tilt | **Grit** effect (bits, rate, wow/flutter); **Room** effect (early reflections, from Neo Verb's code) | L2 |
| Spectral (Learn Spectral) | Sample | Dye Essence P5's spectral engine | per P5 | L3 |

## 4. Architecture

- **Host.** `LearnHost` runs one `DyeEngine` with a base patch fixed per machine. If the engine is still tied to the Dye Essence machine class, L0's first step separates it, with no change to Dye's output.
- **Macro maps.** Each machine has a `MacroMap`: a pure function from its 12 control values to Dye control values. One macro may drive several Dye controls, along curves. It is evaluated on Dye's control grid and ramps across the tick like every Dye control, so there is no zipper noise from the editor, the agent or automation. Each map's formulas are documented in the machine's "as built" section.
- **Songs** store the 12 values; Sampler and Granular also store the user sample, as Dye does. The base patch is code, so a song reloads to the same bits.
- **Notes and voices.** Notes come through Dye's track parameters and MIDI, with Dye's polyphony of 16 notes. All sample rates from 44.1 to 192 kHz work.
- **New code** (String, Vowel, Fold, LPG, Chorus, Grit, Room) lives in the shared engine (`Machines/DyeEssence/Engine`). Whether Dye Essence shows them in its own pages is a decision for a Dye phase (its P4 adds effects), not for this spec.

## 5. Machine contract

### 5.1 Controls

Exactly 12 controls per machine, in Appendix A's order: core 1–6, then global 1–6. Each has a stable snake_case `id`, a Name, a range and unit, a default, a curve (linear, logarithmic or bipolar), and Explain **How** and **Sound** sentences.

### 5.2 Buzz parameters

- Controls use Dye's `ParamDef`:
  - Continuous controls get at least 1000 raw steps, mapped by `MapKind`.
  - Enumerations and counts get exact integers.
- The displayed value is in real units: Hz, ms, s, dB, dB/oct, %, × (ratio), grains/s, a count, or a name.
- FM's ratio has detents at whole and half ratios.
- Double-clicking a knob resets it to its default.
- `ParamDef` gains `How` and `Sound`. The Buzz description (tooltip) is the How sentence.

### 5.3 Explain (Neo kit feature)

- **Strip.** The bottom strip of the editor has three lines:
  - a title line: Name · value with unit · source badge;
  - How;
  - Sound.
- **What updates it.** Hover, drag, keyboard focus, a drag on the picture (it shows both controls' names), and agent writes.
- **Automation.** It shows only when no manual focus has happened for 2 s, and it updates at most 10 times a second.
- **Inspector.** The studio inspector's Selection tab shows the same card for the focused control of any machine with Explain text.
- **Agent.** `machine_describe` returns `id`, `unit`, `min`, `max`, `default`, `curve`, `how` and `sound`. `param_set` accepts the `id`. Agent writes carry the Agent badge. `machine_readings` adds `rms_db`, `peak_db` and `active_voices`.

### 5.4 Editor

One fixed layout for all 13 machines, fitting a 1280×720 screen together with the piano keyboard below it (shown by default, can be hidden). It has four zones:
1. **Header:** the name, a category tag, presets (Init, factory, user), the active-voice count and the clip light.
2. **Picture:** dragging moves core knobs 1 (X) and 2 (Y), as one undo step per drag.
3. **Deck:** core knobs on the left (3×2), global knobs on the right (3×2).
4. **Explain strip.**

Pictures are composed from shared Neo kit parts: scope, response curve, bars, waveform with markers, transfer curve, X/Y pad, and spectrogram (L3).
- They draw at the display rate from lock-free snapshots of the audio thread.
- Each takes at most 1.5 ms per frame on the Linux CI box.
- They use StudioTokens colours, with text contrast of at least 4.5:1.
- Each machine's picture is described in Appendix A.

### 5.5 Sound and quality

- **Presets:** Init plus about 8 factory presets per machine, levelled like Dye Essence's (RMS −22 ± 1 dB).
- **Memory:** zero allocation on the audio thread, tested over 2000 blocks.
- **Stability:** NaN and denormal guards in every feedback path: filters, FM feedback, Modal, String, LPG and Fold.
- **CPU, 16 notes, Linux CI box** (arm64 is reported as Dye does):

| Setting | Minimum speed |
|---|---|
| Default patch | 4× real time |
| Heaviest settings (maximum unison, partials or density) | 2× real time |

- **Aliasing:**
  - smooth paths at or below −80 dB at Standard (2×);
  - edged warps and Fold run at High (4×) and report their floors.
- **Null tests:** at least 4 pinned cases per machine. Dye Essence's pinned hashes stay unchanged.
- **Macro maps:** tested for range and continuity. No jump between adjacent values, except enumerations and documented detents.

### 5.6 Deliverables per machine

- The machine, its editor and presets.
- Tests for Explain behaviour: hover, drag, picture, agent and automation set the strip text and badge.
- An agent guide entry, a profile entry, null tests, a FORK.md row, and an "as built" section in this spec.

## 6. Phases

| Phase | Contents | Exit |
|---|---|---|
| **L0** | Explain in the Neo kit (strip, inspector card, agent fields and readings); `LearnHost` and `MacroMap`; the editor template; the scope, curve and X/Y picture parts; **Neo Learn Subtractive** as the pilot | Green CI, and the lead reviews the gallery screenshots |
| **L1** | FM, Wavetable, Additive, Granular and Physical (after Dye P3b), with their small engine additions. Two workers in parallel. | Green |
| **L2** | The String engine, Vowel, Fold, LPG, Chorus, Grit and Room, then String, Formant, Wavefolder, Phase Distortion, Vector and Sampler | Green |
| **L3** | Spectral, after Dye Essence P5 | Green |

Each phase is merged in the next integration batch, like the other threads. L0 starts once batch 5 (the inspector) is on main.

## 7. Risks

- **Separating `DyeEngine`.** It may need a refactor in L0. Dye's hashes guard it.
- **Additions to the shared engine** (exact ratios, self-feedback, window morph) touch Dye's hot loops. They are append-only, measured, and Dye's CPU must not regress by more than 3%.
- **Physical** depends on the exciter design in P3b.
- **Capacity.** This is a fourth parallel thread, and disk is about 12 GB free. Workers clean their build output.

## 8. Out of scope

- Explain text for other Neo machines (a later follow-up).
- Showing the new engine parts in Dye Essence's own UI.
- Multisampled instruments and formant-preserving pitch shift.
- An external MCP server.

## 9. As built

(Filled in phase by phase.)

### L0 (Neo Learn Subtractive, the pilot)

**Explain (§5.3).** `ParamDef` has `How` and `Sound`; `Params.def` takes `id`, `how` and `sound` lines under a parameter, and the generator writes them into `Params.g.cs` and the manifest (the description defaults to How; `--all` changes nothing for existing machines). `ExplainState` (Core/Explain.cs) decides what the strip shows: hover, a drag, keyboard focus, a picture drag (both names) and an agent write show at once; automation waits 2 s after a touch and shows at most 10 times a second. The badge is who wrote the value last. ReBuzz queues parameter events to the UI, so the agent cannot tell itself from automation by thread: `ParamSource` (BuzzGUI.Interfaces/ExplainBoard.cs) holds a 3 s mark `param_set` makes before it writes, and the editor takes it when the value arrives. A hand edit within 800 ms is Manual. `ExplainBoard` carries the strip's card to the studio inspector's Selection tab (a timer follows it 4 times a second). Agent: `machine_describe` adds `id`, `unit`, `min`, `max`, `default_number`, `curve`, `how` and `sound` (in the unit: Hz, s, dB, %); in value_table mode it writes `min_raw` and `max_raw` for such parameters; `param_set` and `SongModel.Param` accept the snake_case id; `machine_readings` adds `rms_db`, `peak_db` and `active_voices`.

**LearnHost and MacroMap (§4).** `LearnHost` owns one `DyeEngine` over its own `ParamValues` built from Dye's table, writes a fixed base patch and the map before the engine is made, and re-maps when a control moved. No change under `Machines/DyeEssence`. Output is a gain after the engine (Dye's Volume stops at +6 dB), ramped over the block; non-finite samples are zeroed; it keeps the output meter, a 0.5 s RMS, a clip light (1 s above 0 dBFS), a 4096-sample scope ring, and `Readings()`.

**Subtractive.** Base patch: 16 notes, Standard quality, three Analog slots with random phase, Ladder 24 filter (F1, no key tracking, no velocity), filter 2 off, a square sub one octave down. Wave Shape crossfades sine, triangle, saw, pulse by three hat-function slots (slot A plays the sine below 50 % and the pulse above, silent at the swap). Sub Osc is the sub's level; Env to Filter is up to 6 octaves; Drive is up to 18 dB before the filter plus 0.6 of the pitch drift; one ADSR drives both amplitude and filter; Unison / Spread is 1 voice at 0 % and 5 above it (detune up to 50 ct, width with the value; random phases, so the level stays put). Init and nine presets sit at -22 dB RMS (the loudest 50 ms of a chord, within 1 dB). The default Output is raw 635 (about -5.5 dB) so Init is at -22.

**Editor (§5.4).** `LearnEditor` (NeoLearn/LearnEditor.cs) has four zones: header (category tag, voices, clip light; the kit's header above has name and presets), picture, deck (6 core, 6 global knobs), Explain strip; about 900 x 440 pt, so with the machine keyboard it fits 1280 x 720. The picture (Core/LearnPicture.cs, an `IGraphInput` over `INeoCanvas`, so both toolkits share it) has the scope, the 24 dB ladder's response curve and a pad: the handle is Cutoff (across) and Resonance (up); a drag moves both as one undo step, Shift is fine, a double-click resets both. A knob's double-click resets it.

**Decisions to confirm.** "Low-pass 24" is the Ladder 24 filter. Unison enabling is a step (1 voice to 5) and the Wave Shape swap and the unison step are the documented detents of the continuity tests. `active_voices` counts notes. Id matching in the agent is general: any manifest with an `Id` works.

**Measured.** Alias floor -89 to -101 dB at notes 33, 57 and 94; CPU floors 4x (16 notes, default patch) and 2x (heavy) hold, the last local runs under load showed about 25x and 20x; no allocation over 2000 blocks; extreme settings stay finite and free of denormals. Four pinned renders (init, pluck-bass, wide-pad, morph-lead) in `LearnTests`, four null-test cases `learn-sub-*` (the Windows package job renders their references).

### L1a (Neo Learn FM, Wavetable, Additive)

**Engine additions (append-only; Dye Essence's parameters, manifest and pinned hashes do not change).** `OscSettings` has six new fields that only `LearnHost` sets, through `IDyeSink.Ext(slot, SlotExt, value)` (pushed after every map): `ExactRatio` (a slot's pitch as an exact multiple of the note instead of a transpose), `Feedback` (PM self-feedback: the slot's last output moves its own phase; the slot takes the warped kernel and the speed multiplier `1 + 2 pi Feedback` keeps the mip level safe), `Comb` and `CombPeriod` (a notch mask across the Harmonic engine's partials), `Shimmer` (each upper partial drifts in level up to 80 % and in pitch up to 0.4 % on its own 0.3 to 2 Hz sines; a voice works them out every fourth tick, staggered, and holds them between) and `Inharm` (stiff-string spacing). `ParamDef` has a `Detent` (`detent=N` after an `exp` range in `Params.def`): a drag or a typed value near a whole or half ratio snaps to it (window 1.5 % of the range, at most 12 % of the ratio, so high ratios do not jump).

**FM.** Slots C (carrier, Analog sine) -> B -> A through the Pm warp, which is one modulator per slot, so the third operator is a chain, not a parallel pair. Mod Ratio is `ExactRatio` on the modulator (0.25x to 16x, detents at whole and half ratios: whole ratios give harmonic sidebands, half ratios fold sidebands between the harmonics). FM Depth is the index; Mod Decay is Env2's decay on it (a 30 % body stays); Metallic Edge is the third operator at 7x on Env1 (80 ms) with a small carrier-index share so the click is heard at depth 0; Velocity to FM scales the index by velocity. Feedback reaches 0.6 cycles at 100 %.

**Wavetable.** No engine change. Slot A plays Dye's Wavetable engine on the chosen table (the ten built-in ones, and User, item 10, which a WAV import fills as in Dye: frames of 2048 samples, a `clm` chunk or one cycle; it is kept with the song). Frame Pos is the frame; Phase Warp is Bend left of centre and Sync right of it (at High quality); Motion Rate and Depth are LFO 1 on the frame position; Sub Anchor is a sine one octave down.

**Additive.** Slot A plays the Harmonic engine: Brightness Tilt (-24 to +6 dB/oct), Odd / Even (left odd only, right even only: Dye's sign flipped), Partials (1 to 64), Inharmonicity, Spectral Comb (period 12 partials softly to 4 at full), Shimmer.

**Decisions to confirm.** FM's third operator is a chain (above). The unison step is 1 voice to 5, as in Subtractive. The detent window is the one above. Shimmer is slow by design, so its sines are held for four ticks.

**Measured (local, load about 1.5).** CPU, 16 notes: default patch 10.3x (FM), 10.2x (Wavetable), 19.3x (Additive); heaviest settings 7.9x, 8.2x and 3.5x (the first Additive build, with shimmer worked out every tick, ran 1.9x, under the 2x floor, which is why shimmer is held). Alias floor, Standard quality: FM at a whole ratio and 35 % depth -93.6 to -100.8 dB at notes 33 to 57 (-92.8 at note 81); at 70 % and 100 % depth, -75.3 and -85 dB (reported: deep FM folds); Wavetable unwarped at High -92 to -103.5 dB, with Bend 80 % -54.8 to -84.4 dB and Sync 60 % -27.8 to -59.6 dB (the warps alias, as Dye's do; the table and Phase Warp 50 % are clean); Additive -92.6 dB (partials are limited to the band, shimmer moves them so they are not harmonics). No allocation over 2000 blocks; extreme settings and feedback at the top stay finite, with no denormals in the tails. Presets are -22 dB RMS (+-1). Twelve pinned renders (`LearnL1Tests`, four per machine) and six null-test cases (`learn-fm-*`, `learn-wt-*`, `learn-add-*`; the Windows package job renders their references). The profiler reads Explain machines' raw ranges from `min_raw` and `max_raw` now (a choice control has no `min`).
