# Neo machines as plug-ins (CLAP, VST3, AU)

Status: phases PX1 (branch `plugins/export-px1`) and PX2 (branch `plugins/export-px2`: more machines, and the plug-ins dogfooded in NeoBuzz). The measurement spike that led here is `docs/specs/plugin-export-spike-report.md` (PX0: editor path A, two NativeAOT runtimes, zero GCs in the DSP runtime).

## 1. What it is

Every exportable Neo machine becomes a plug-in for other DAWs (Ableton Live first): CLAP, VST3 (Windows, macOS, Linux) and AU (macOS). The plug-in runs the machine's own engine (`Machines/<Name>/Engine`, the code the studio runs) and shows the machine's own Neo editor (the INeoCanvas / Avalonia kit, the same editor classes as the machine windows in the studio). One descriptor of about 15 lines per machine; everything else comes from the engine and its `Params` table.

Exported now (22): Neo Filter, Neo EQ, Neo Comp, Neo Delay, Neo Verb, Neo Poly, Neo FM6, Neo Drums, Dye Essence and the 13 Neo Learn synths (Additive, FM, Formant, Granular, Phase Distortion, Physical, Sampler, Spectral, String, Subtractive, Vector, Wavefolder, Wavetable). Plug-in names are the machines' own ("Neo ...", and "Dye Essence", which is the machine's name in ReBuzz too: PX1 called it "Neo Dye Essence"; the plug-in id `org.neobuzz.dye-essence` did not change) and carry no vendor trademarks. Out of scope by design: machines that need the host's song (canvas modulators, Neo Plug itself) and Neo Patch.

## 2. Layout

```
PluginExport/
  descriptors/*.json        one per plug-in (the only per-machine input)
  dsp/                      neo_dsp: NativeAOT library, CLAP ABI in C# ([UnmanagedCallersOnly]), the engines of all plug-ins, one factory
  editor/                   neo_editor: NativeAOT library, own runtime and GC: the Neo editors on Avalonia headless (Skia), native child windows
  shared/Shared.cs          the only thing the two libraries share: lock-free struct (parameter values, edit ring, meters, stats)
  native/stub_impl.c        the exported clap_entry data symbol (C# cannot export data); one stub per plug-in (-DNEO_PLUGIN_ID)
  CMakeLists.txt            clap-wrapper make_clapfirst_plugins per plug-in: .clap, .vst3, .component from the same stub
  host/neohost.cpp          a CLAP host for the CI checks (list, check, gc, gui under Xvfb)
  selftest/, editor-smoke/  managed tests (CoreCLR)
  ci/                       stage.py, run-host.py, null-test.py, au-list.py
  plugins.json              manifest generated from the descriptors (parameter counts, test changes)
tools/gen-plugin-export.py  descriptors -> Registry.g.cs, EditorRegistry.g.cs, MSBuild source lists, cmake/plugins.cmake, plugins.json
```

A bundle for one plug-in is the module (`.clap` / `.vst3` / `.component`, a thin stub) with `neo_dsp.*`, `neo_editor.*` and Skia/HarfBuzz beside it. The stub loads `neo_dsp` and filters its factory to its own plug-in id, so each file exposes exactly one plug-in. The editor library is loaded only when an editor is opened.

## 3. The generator and the descriptor

`python3 tools/gen-plugin-export.py` (CI: `--check`, which ignores CRLF conversions of a Windows checkout). A descriptor names the plug-in (`id` `org.neobuzz.neo-...`, `name`, `au` four-character subtype), its machine folder, engine / machine / editor classes, `kind` (effect or instrument), CLAP `features`, `process` (`stereo`, `sidechain`, `synth`, `learn`: a Neo Learn synth, whose adapter builds the shared `LearnHost` over Dye Essence's engine from the descriptor's `map` class), `tables` (the engine builds tables and samples on background threads: see Activation), and flags for what the engine offers: `tempo`, `transport` (`ticks`), `idle`, `latency`, `tailSeconds`, extra source files, and the `test` the null test applies.

Derived at run time from the machine's `ParamDef` table, never written by hand: parameter id (= the Neo index), range, default, flags, display name, module (group), text and units (`value_to_text` / `text_to_value`: an exact text match first, then the nearest number, kHz understood). A new machine is a descriptor plus `dotnet`-side nothing else, as long as its engine follows the shape of the existing ones (`Create(ParamValues)`, `SetSampleRate`, `Process`, `Latency`, `Reset`, and for instruments `NoteOn/NoteOff/...`). An engine that differs gets a new `process` kind in the generator, not a hand-written plug-in.

## 4. Behaviour

- **Parameters.** Value = the Neo raw value (a frequency's raw value is its position on the log scale), so automation curves equal the editor's and state round trips are exact. A parameter is `STEPPED` when its kind is enum, switch or int, or its range is one step (`ENUM` for choices). Order and ranges are the machine's; they never change (append only), so a plug-in state saved earlier always loads.
- **State.** `"NBPS"`, format 1, machine state version, count, then int32 raw values. A shorter state (older machine) loads with defaults for the new parameters; a bad header is refused. The machine's own `StateVersion` / `MigrateState` run on load. Note: PX0 had no state version at all; the machines' `ParamDef` tables are append-only, which is what makes older states safe.
- **Latency and tail.** Latency from the engine (Neo EQ's depends on its parameters: a change sets a flag, `request_callback`, then `request_restart` on the main thread; `latency.get` reports the value current at activation). Neo Verb reports 0. Tail from the descriptor (`tailSeconds`).
- **Ports.** Effects: stereo in and out, plus a second stereo input as sidechain for Neo Comp and Neo EQ. Instruments: no audio input, one note port (CLAP note + MIDI dialects). Notes: key = 1000 + MIDI note, velocity quantised to 1..127; CC1 mod wheel, CC64 sustain, CC120 and CC123 all notes off, pitch bend, channel aftertouch. Blocks are split at event times and at the engine's maximum block (256), so events are sample accurate.
- **Silence.** Like the machine in a song, an engine with an `idle` state that has silent input and nothing ringing is not run and writes zeros. Neo Verb has no such state in its engine but a rule in its machine (16 blocks with no input and no sound): the descriptor says `"idle": "quiet"` and the runtime applies the same rule, because Verb's modulators free-run while it is processed and a render would otherwise differ from the machine's.
- **Activation.** The second and later activations of an instance start from a new engine on the same parameters (the machine's `Restart` for an offline render: random streams, drift, voice age and modulator phases back to the start). It is on the main thread, so it may allocate. A host that reactivates for a render (Neo Plug does) gets the same samples as a fresh instance.
- **Audio thread.** No allocation in `process` (checked by the self-test and by `neohost gc`); sub-block splitting uses preallocated buffers.

## 5. The editor

`neo_editor` creates the machine's own editor class over a `FakeMachine` (BuzzGUI `IMachine` / `IParameter` over `Shared`), so the kit's `EditorContext` binds exactly as in the studio. The editor draws in an Avalonia headless window (Skia, no OS window). A 60 Hz timer (host `timer-support`, or the host's own tick) polls host changes, runs the UI jobs, renders and blits the frame into a native child window of the host's: Windows HWND (`WS_CHILD`), macOS NSView (layer-backed `NSView` subclass), X11 (child window). A `Viewbox` gives resize and scale; `get_resize_hints` keeps the aspect ratio. Without a parent (floating) the editor is rendered but not shown.

- **Editor to host.** A knob edit is: gesture begin (first edit of a parameter), value events, gesture end (pointer up, or 300 ms after the last edit for a wheel turn). They go through the edit ring; the DSP side applies them and emits `PARAM_GESTURE_BEGIN` / `PARAM_VALUE` (live) / `PARAM_GESTURE_END` output events via `request_flush`.
- **Host to editor.** Host automation and state loads update `Shared.Param`; the editor polls the sequence number and refreshes.
- **Preset bar.** Hidden in the plug-in (`NeoEditor.NoPresets`): the host has presets.
- **GC.** The editor library has its own runtime. The DSP runtime does zero GCs while an editor allocates (PX0 measured it; `neohost gc` checks it on every CI run with the load generator).

## 6. Tests

| Where | What |
|---|---|
| `selftest` (CoreCLR, all OSes) | struct layouts, descriptor, ports, parameters and texts, latency/tail, bit-identical render against the engine used directly (param and note events), zero allocation in process, state round trip, old state, bad header, ring edit to gesture/value events, host automation to Shared |
| `editor-smoke` (CoreCLR, all OSes) | every editor renders without an "Editor error" panel (PNG), a knob drag makes begin / values / end in the ring, a host change reaches the machine |
| `neohost` (AOT, per OS) | loads each module through the CLAP ABI: entry, factory, ports, params, state, process; `gc` run; Linux: `gui` under Xvfb (window, screenshot variance, drag, gesture events) |
| `ci/null-test.py` (Linux) | each plug-in as CLAP and as VST3 in Neo Plug (rebuzz-headless) renders null against the native machine (1e-5, i.e. -100 dBFS; instruments 3e-3, see below); the VST3 equals the CLAP to 1e-6; the first 64 parameters (Neo Plug has 64 slots) against `machine_describe`: names, texts, units; phases defaults, changed (changed parameters, first 0.5 s skipped: the engines glide to them), reopened (song saved and opened) |
| auval (macOS) | each AU passes `auval -v` |

## 7. Packaging and licences

CI (`.github/workflows/plugin-export.yml`) builds per OS: Windows `.clap` + `.vst3`; macOS `.clap` + `.vst3` + `.component`, ad-hoc signed (not notarised); Linux for testing. One artifact, "NeoBuzz Live Plug-ins", with the bundles, `NOTICE.txt` (CLAP MIT, clap-wrapper MIT, VST3 SDK MIT, AudioUnitSDK Apache-2.0, Avalonia, SkiaSharp, HarfBuzz, Inter OFL, .NET) and `INSTALL.txt` (including the AU registrar note: `killall -9 AudioComponentRegistrar` or a logout makes macOS see a new `.component`).

Null results (Linux CI, run on the final head): Neo Filter, Neo EQ, Neo Comp, Neo Delay null within 1e-5 in all phases (Filter, Comp bit-identical in defaults); Neo Verb defaults bit-identical, reopened within the reload tolerance; Neo Poly and Neo Dye Essence null within 3e-3 (instruments: the native machine's notes start at tick boundaries, the plug-in's at the block start Neo Plug sends them). Two phases are reported and not asserted: Neo Verb and Neo Dye Essence "changed" (the native machine carries state from the render before; the same parameters in the "reopened" phase, fresh instances, null). Neo Dye Essence builds its tables on a background thread and is silent for about a second after its first instance in a process; the test host waits for it.

## 8. PX2 as built, limits and not done

**PX2 (as built).** 22 plug-ins: Dye (display name "Dye Essence"; IDs unchanged), Neo FM6, Neo Drums and the 13 Neo Learn synths join the first seven. Neo Patch stays out. Each has a descriptor, a self-test, an editor smoke test, a null test and an artifact entry. The Learn synths run through a `LearnHost` over their `MacroMap`; Dye and the Learn synths build their tables in `Prepare()` at activation, off the audio thread (the host's main thread waits at most 150 ms, then the first blocks are silent until the tables are ready, never a stall); the Learn Vector map no longer allocates on the audio thread.

**Dogfood** (`PluginExport/ci/dogfood.sh`, the "Dogfood in NeoBuzz" step, on all three OSes): every plug-in as CLAP and VST3 beside its native machine in Neo Plug, driven over MCP like an agent (`ci/null-test.py`): parameters and units (a slot takes the native machine's text), patterns, automation (8 steps and 1024 steps, every 1/128 beat), renders nulled, `.nbl` and `.bmx` round trips (every slot value equal). On Windows and macOS the studio loads each editor (`--plugin-export-check`): screenshot (colour count), a knob dragged through the window system (Win32 `PostMessage`, Cocoa `NSEvent`), resize, close, reopen, song saved and reloaded as `.nbl` and `.bmx` (slot values and the plug-in's state blob equal). Screenshots are in the `dogfood-<OS>` artifact and a few in the log (base64). macOS views render at the backing scale (`contentsScale`, 2x on Retina).

**Host fixes found by dogfooding.** (1) Neo Plug ignored a CLAP plug-in's `params.request_flush`, so a knob moved in a plug-in's own window changed no slot unless a block ran. The pump now calls the plug-in's flush (`np_flush`, gate held, process stopped); in a sandbox the call crosses the protocol (`OpFlush`: np_remote sends it, np_serve calls `np_flush` of its instance and publishes the values again). (2) A VST3 module made by clap-wrapper does not set the value it reports through `IComponentHandler::performEdit` on its own controller (its parameters are updated only through output parameter changes of a running block), and Neo Plug's slots read the controller. The host's handler now sets it, like a host's should (`fromEditor` ring, drained by `idle()` on the main thread), so a knob dragged in a VST3 plug-in's window moves the slot with no audio running, in process and in a sandbox.

**Automation is sample-accurate in Neo Plug** (`np_event.frame`: CLAP `param_value` events with the time inside the block, VST3 `IParameterChanges` with `sampleOffset`, in process and across the sandbox's protocol, which keeps the frames when it splits a long block). `neoplug-sandbox-test smoke` asserts it on the gain effect (an event at frame 70 changes the output at exactly sample 70 plus the latency, in process and in every placement). The engine puts a tick's or subtick's value at frame 0 of the block that starts there, which is where the native machine takes it: the trace of both machines' blocks (Neo Filter, 1/128-beat steps) shows the same block sizes, the same value in the same block. The earlier dogfood differences of 0.1 to 0.9 were not timing but history: the native machine had played earlier renders (filter state, random streams, tails) and a native machine loaded from a song glides from its defaults while a plug-in restored from state starts settled. The null test now compares like with like: the automation is written, every machine is set to the automation's first values, the song is saved and opened (fresh instances), and one render of each is nulled at the tolerances of the plug-in's other renders (effects 1e-5 after the settle window, instruments 3e-3). Measured on Linux (CLAP): see PX2 report.

**Two host bugs the sandbox showed** (Windows sandboxes by default, so its null tests found them; reproduced on Linux with `NEOPLUG_PLACEMENT=plugin`): (1) the pump's copy of a slot value (`np_param_set`, meant for a VST3 controller) reached the plug-in blocks after the block event, and in a sandbox after newer events, putting an old value back for a while; a block that delivered a slot's event now clears the copy (the VST3 library already tells the controller itself). (2) The crash-recovery snapshot holds the block gate while it saves the state, and a block that finds the gate taken is silent: one dropped block at the first change and every 15 s after, so two runs of a song were not the same samples. While blocks are coming in the snapshot now waits (at most a minute).

**Save and reload** is compared like with like too: a render of a fresh instance (the song written as bmxml and opened) before saving, and of a fresh instance after saving as `.nbl` or `.bmx` and opening, nulled at the same tolerances, for the native machine and each plug-in format, besides every slot value and the plug-in's state blob.

**Reported, not asserted** (the figures are in the CI log as `NOTE` lines): how far the automation moves each machine's output against the render before it; Neo Verb "changed" stays informational (0.325 after 0.5 s, also with fresh instances: its modulation is not restartable bit for bit). Units compare tolerates 1.5 % (a slot holds 65535 steps). Knob drag is asserted for every plug-in, CLAP and VST3, on Windows and macOS (a knob already at its top is dragged down instead).

**Bundle size.** Linker flags (`IlcOptimizationPreference=Size`, identical-body folding, no stack traces, event source or debugger support) took `neo_editor` from 24.2 to 23.5 MB (Windows), 23.5 to 22.5 (Linux) and 22.2 to 21.3 (macOS): about 4 %. The rest is Skia (11 to 15 MB), HarfBuzz (2 to 3 MB) and Avalonia. `neo_dsp` is 3.0 to 3.4 MB. Each bundle still carries its own copies (self-contained); a shared install location would cut a 22-plug-in install by about 90 %.

- Not exported: Neo Patch (by decision).
- Windows and macOS editor views run in CI (studio check, headless smoke); real embedding (HiDPI at 125 to 200 %, focus, host keyboard) needs a hand test in Ableton.
- No host keyboard input in the editor, no live meters, macOS arm64 only.
- Ad-hoc signing only: Gatekeeper needs quarantine removal until a Developer ID is available.

## 9. Hand-test list (Ableton Live)

Windows: load the VST3 (and CLAP) for Neo Filter, Neo Poly, Dye Essence, Neo FM6, Neo Drums and one Neo Learn synth; the editor opens and sizes correctly at 100 / 150 / 200 % scale and resizes; knobs move and record automation ("touch" mode); playback automation moves the knobs; Dye Essence and the Learn synths are silent for about a second on the first instance only if the tables are not ready (check no hang); save and reopen the set (state); several different Neo plug-ins open at once (each has its own view class); the plug-in's name and parameters in the browser.
macOS: the same for VST3 and AU (rescan: AU registrar), the editor in a Live window on a Retina display (sharp, 2x), Gatekeeper behaviour of the unsigned bundles.
