# Neo Patch and the canvas modulators — Spec

Status: approved by the user 2026-11-13 ("Neo Patch + the canvas modulators"). Planning by the lead. This file is the contract the phases build to.

Status notes (phase NM, branch machines/neo-modulators): built and tested: Neo LFO, Neo Envelope (Note and Follower), Neo Step and Neo Macro with up to 8 targets each; the host side (`modulate`, `modulation_list`, `modulation_remove`, the incoming modulations in `machine_describe`); the studio (dashed wires, the live-range ring, the target picker and scope in each editor). Notes: the modulators carry no factory presets (their setup is the targets, which the song holds); the Pedal LFO to Neo LFO pair maps the wave and the phase, and Pedal Peer Control has no pair (its per-track Value has no single counterpart in Neo Macro: the reason is on the Neo Macro page); a Note envelope sees the notes patterns and midi_play send, not a MIDI keyboard's.

## 1. What and why

Two pieces. Together they give NeoBuzz Live modular synthesis without changing how the engine's wires work.

1. **Canvas modulators (NM):** small control machines on the main canvas, Neo LFO, Neo Envelope, Neo Step and Neo Macro. Each drives **any parameter of any machine**, smoothed. They are useful for every machine, not only synths (an LFO on a delay time, an envelope follower ducking a filter).
2. **Neo Patch (NP):** one machine on the canvas (notes in, stereo audio out). A double-click opens **its own canvas** of primitive nodes (oscillators, LFOs, envelopes, filters, VCAs, math). The nodes are wired port to port. The patch runs as **one polyphonic engine inside the machine**, not as separate Buzz machines.

Why not real machines wired on the main canvas (rejected; the analysis of 2026-11-13): wires carry one stereo signal, so a patch would be mono. Machines can only modulate each other at parameter rate, and a feedback loop would need about a block of delay. Each machine has a fixed cost on every audio block, there is no typed control signal to keep DC off the speakers, and wiring is machine to machine, not port to port. Neo Patch keeps the look of a canvas and avoids all of that.

Neo Patch is a different teaching tool from Neo Learn. Neo Learn teaches **methods** (one per machine, fixed and explained). Neo Patch teaches **architecture** (what is inside a synth). The two meet in §4.7: each Neo Learn method ships as a template patch.

## 2. Decisions

1. Neo Patch's nodes are **not** Buzz machines. The inner canvas is an editor over a graph that the machine compiles and runs.
2. **Polyphony** per voice: per-voice nodes run once for each voice, processed together across voices, from 1 to 16 voices, and global nodes run once. The patch decides which nodes are per voice (§4.3).
3. Signals inside a patch are audio rate everywhere. Ports are typed (Audio, Control, Gate, Pitch) only for colour, checks and safe defaults; any output can feed any input (modular freedom), and the type picks a sensible default scale.
4. **Feedback** only through a Feedback node, which adds one block of delay (or one sample, at High quality, within the CPU floor). Other cycles are refused at connect time.
5. **Automation:** a Buzz machine has a fixed parameter list, so Neo Patch exposes **16 Macro parameters** (Macro 1–16) plus Volume. Patterns and automation drive the macros, and the patch maps each macro to node controls. Everything else is machine state, saved in songs and presets.
6. **Safety:** the output always passes through a DC blocker and the kit's soft safety limiter, and every node has NaN and denormal guards. A node cap (128) and a voice cap keep CPU bounded; the editor shows the patch's CPU.
7. The **editor is built on the Neo editor kit**, so it works in the Windows app (WPF) and the studio alike.
8. **Canvas modulators** write parameters through the host, at most once per tick and per sub-block (64 samples) where the target supports it, with the Neo kit's parameter glides so nothing zips. They never allocate on the audio thread. Each modulation is a modulation, not an edit, so it adds no undo steps and the target's base value stays put; removing the modulator restores the base value.
9. Clean room: our own code and words, and no trademarked product names in machines, nodes, presets or text.

## 3. Canvas modulators (phase NM)

| Machine | What it does | Main controls |
|---|---|---|
| **Neo LFO** | A periodic modulator | Shape (sine, triangle, saw up, saw down, square, smooth random, S&H), Rate (Hz, or synced 1/64–8 bars, dotted and triplet), Phase, Retrigger (free, beat, bar, note on a chosen machine), Smooth |
| **Neo Envelope** | Triggered by notes on a chosen machine or track, or an envelope follower on any machine's output | Mode (Note/Follower), ADSR or Attack/Release (follower), Threshold (follower), Curve |
| **Neo Step** | A step sequencer for values | 1–32 steps, Rate (synced), Glide, Random amount, Swing |
| **Neo Macro** | One knob that drives many targets (also a target for MIDI learn) | Value, plus per-target range and curve |

Each modulator has **up to 8 targets**. A target is a machine and one of its parameters (by id, so it survives renames and reorders), with Depth (−100…+100 %), Polarity (uni/bi) and an optional Range (min/max in the target's real units).

How it works:
- Targets are resolved by machine identity, not name, and saved by name and id with a fallback.
- A deleted target goes inactive and shows in the editor; nothing throws.
- The modulated value is base + depth × modulator, clamped to the target's range **and to its safe range from the manifest** (machine-safety.md); the master volume and safety settings can't be pushed past their safe range.

UI:
- **On the canvas:** a modulation wire, dashed in the modulator's identity colour, from the modulator to each target machine, labelled with the parameter on hover.
- **In the target's device view:** a modulated parameter shows a ring or band for the live range.
- **In the modulator's editor:** a target picker (machine, then a searchable parameter list) and a live scope of the modulator's output.

Agent tools:
- `modulate(source, target_machine, param, depth, …)` and `modulation_list` / `modulation_remove`;
- `machine_describe` lists a machine's incoming modulations.

The phase also delivers Explain text on every control, pages and guides on the §4 template, a catalogue category of Control, tests (allocation-free, smoothness with no step bigger than the glide allows, safe-range clamping, save and reload, rename and delete robustness), and null-test cases.

Pedal LFO and Pedal Peer Control remain for old songs. The Neo modulators are their successors, with a `machine_replace` mapping where the parameters line up.

## 4. Neo Patch (phases NP0–NP3)

### 4.1 Machine shell
- A generator: notes in (patterns, MIDI, MPE), stereo out.
- Parameters: Volume, Macro 1–16 (0–1000 raw, shown in the macro's label and unit), Voices (1–16), Glide.
- State holds the graph, the node settings, the macro mappings and the patch name.
- Presets are whole patches.

### 4.2 Nodes v1 (about 20)

| Group | Nodes |
|---|---|
| Notes | **Keys** (pitch, gate, velocity, aftertouch/pressure, MPE slide; per voice), **Global Keys** (last note, for mono patches) |
| Sources | **Osc** (sine, triangle, saw, square/PWM, analog drift; sync in; FM/PM in; band-limited), **Wavetable Osc** (Dye's tables, position in), **Noise** (white, pink), **Sample** (one-shot or loop, Dye's built-ins or the user's WAV) |
| Modulation | **LFO** (per voice or global), **Envelope** (DAHDSR with curves, Dye's), **Slew**, **Sample & Hold**, **Random** |
| Tone | **Filter** (state-variable and ladder modes from Neo Filter / Dye; cutoff and resonance in), **Shaper** (soft clip, fold, using Neo Learn's Fold with its alias cap), **Delay** (short, up to 2 s; for plucked strings, flanging and combs) |
| Plumbing | **VCA**, **Mixer** (4 in), **Math** (add, multiply, scale and offset, min, max, abs, invert), **Crossfade**, **Feedback** (§2.4), **Macro** (outputs Macro n), **Out** (stereo, with pan) |

Each node exposes its controls with units and Explain How/Sound. Port inputs add to their control's knob value, scaled by the input's amount.

### 4.3 Per-voice and global
Keys, Osc, Envelope, Filter and VCA default to per voice. LFO, Random and Macro can run either way: per voice by default, with a global switch. Once a per-voice signal reaches a node set to global, it is summed over the voices: the Out node and anything after a "Voice sum" are global. The editor shows per-voice nodes with a stacked edge.

### 4.4 Runtime
- The graph is compiled into an ordered list of block jobs (64 samples, the host's block cut down to this size).
- Per-voice nodes process all active voices together, with the Dye kernels' processing across voices where they exist.
- Voice allocation follows Dye's NoteBank (stealing with fades, mono/legato, glide).
- No allocation on the audio thread: the graph is recompiled off thread and swapped in at a block boundary, with a short crossfade when the structure changes while playing.
- PortableMath on the audio path. Deterministic, so null tests are bit-identical.

### 4.5 Inner canvas (editor)
- Boxes with ports, port-to-port wires (drag from an output dot to an input dot), and a right-click node menu with search.
- Copy, paste and duplicate; one undo step per edit, on the song's stack; zoom and pan.
- Node controls appear in a side panel when a node is selected, with Explain.
- A macro strip is always visible.
- A "Learn" overlay names each wire's signal on hover.
- A CPU readout per patch.
- It matches the main canvas's look (tokens, identity colours by node group, type scale) but is built on the Neo kit.

### 4.6 Agent tools
- `patch_describe` returns the nodes, ports, wires, macros and CPU.
- `patch_add_node`, `patch_connect`, `patch_disconnect`, `patch_set` (a node control in real units), `patch_remove`, `patch_macro_map`.
- `patch_load_template` and `patch_save_preset`.
- Each edit is one undo step, and the tools are covered by guides and tests.

### 4.7 Templates and teaching
Template patches ship for the Neo Learn methods that the nodes can build: Subtractive, FM (2 and 4 operators), AM/ring, Wavefolder, Sync lead, a plucked string (Delay plus Filter plus Feedback), Vector-style crossfade, and Sample loop. They come with a guide, "Build a synth", that walks a learner through them one node at a time.

## 5. Phases

| Phase | Contents | Exit |
|---|---|---|
| **NM** | The four canvas modulators (§3): modulation wires, the device-view ring, agent tools, pages, tests | Green; smooth and allocation-free with 8 targets × 4 modulators on the big song |
| **NP0 Gate** | The runtime spike: the compiler, per-voice processing across voices, Feedback, 6 nodes (Keys, Osc, Envelope, Filter, VCA, Out); CPU against Dye on the same patch | **Go** if a 20-node, 8-voice patch costs ≤1.5× the same sound in Dye and stays allocation-free; otherwise adjust the design |
| **NP1 Engine** | The machine shell, all v1 nodes, macros, state, presets as code, null tests, CPU floors (x64 and arm64) | Green |
| **NP2 Editor** | The inner canvas on the Neo kit (both apps), the side panel, Explain, undo, the CPU readout, gallery shots | Green, plus shots reviewed |
| **NP3 Agents and teaching** | §4.6 tools, the §4.7 templates and the guide, the machine page, 20+ presets | Green; GuideTests and template checks |

Order: NM and NP0 can run in parallel. NP1 waits for NP0's gate, and NP2 and NP3 follow NP1.

## 6. Risks

| Risk | Measure |
|---|---|
| CPU of many small nodes | Block jobs, per-voice nodes processed together, a node cap; the NP0 gate |
| Feedback cost at one-sample delay | Block delay by default; the one-sample delay only at High quality and within the CPU floor |
| Users make silence, DC or loud noise | DC blocker, safety limiter, sensible default port scaling, empty-patch template with sound in 3 steps |
| A modulator fighting user edits | Base value plus modulation; edits move the base; the ring shows both |
| Old songs with Pedal LFO | Kept; the successor mapping is optional |
