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
