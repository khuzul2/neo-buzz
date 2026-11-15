# Neo machines as plug-ins (CLAP, VST3, AU)

Status: phase PX1 (branch `plugins/export-px1`). The measurement spike that led here is `docs/specs/plugin-export-spike-report.md` (PX0: editor path A, two NativeAOT runtimes, zero GCs in the DSP runtime).

## 1. What it is

Every exportable Neo machine becomes a plug-in for other DAWs (Ableton Live first): CLAP, VST3 (Windows, macOS, Linux) and AU (macOS). The plug-in runs the machine's own engine (`Machines/<Name>/Engine`, the code the studio runs) and shows the machine's own Neo editor (the INeoCanvas / Avalonia kit, the same editor classes as the machine windows in the studio). One descriptor of about 15 lines per machine; everything else comes from the engine and its `Params` table.

Exported now: Neo Filter, Neo EQ, Neo Comp, Neo Delay, Neo Verb, Neo Poly. Plug-in names are "Neo ..." and carry no vendor trademarks. Out of scope by design: machines that need the host's song (canvas modulators, Neo Plug itself).

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

`python3 tools/gen-plugin-export.py` (CI: `--check`, which ignores CRLF conversions of a Windows checkout). A descriptor names the plug-in (`id` `org.neobuzz.neo-...`, `name`, `au` four-character subtype), its machine folder, engine / machine / editor classes, `kind` (effect or instrument), CLAP `features`, `process` (`stereo`, `sidechain`, `synth`), and flags for what the engine offers: `tempo`, `transport` (`ticks`), `idle`, `latency`, `tailSeconds`, extra source files, and the `test` the null test applies.

Derived at run time from the machine's `ParamDef` table, never written by hand: parameter id (= the Neo index), range, default, flags, display name, module (group), text and units (`value_to_text` / `text_to_value`: an exact text match first, then the nearest number, kHz understood). A new machine is a descriptor plus `dotnet`-side nothing else, as long as its engine follows the shape of the existing ones (`Create(ParamValues)`, `SetSampleRate`, `Process`, `Latency`, `Reset`, and for instruments `NoteOn/NoteOff/...`). An engine that differs gets a new `process` kind in the generator, not a hand-written plug-in.

## 4. Behaviour

- **Parameters.** Value = the Neo raw value (a frequency's raw value is its position on the log scale), so automation curves equal the editor's and state round trips are exact. Order and ranges are the machine's; they never change (append only), so a plug-in state saved earlier always loads.
- **State.** `"NBPS"`, format 1, machine state version, count, then int32 raw values. A shorter state (older machine) loads with defaults for the new parameters; a bad header is refused. The machine's own `StateVersion` / `MigrateState` run on load. Note: PX0 had no state version at all; the machines' `ParamDef` tables are append-only, which is what makes older states safe.
- **Latency and tail.** Latency from the engine (Neo EQ's depends on its parameters: a change sets a flag, `request_callback`, then `request_restart` on the main thread; `latency.get` reports the value current at activation). Neo Verb reports 0. Tail from the descriptor (`tailSeconds`).
- **Ports.** Effects: stereo in and out, plus a second stereo input as sidechain for Neo Comp and Neo EQ. Instruments: no audio input, one note port (CLAP note + MIDI dialects). Notes: key = 1000 + MIDI note, velocity quantised to 1..127; CC1 mod wheel, CC64 sustain, CC120 and CC123 all notes off, pitch bend, channel aftertouch. Blocks are split at event times and at the engine's maximum block (256), so events are sample accurate.
- **Silence.** Like the machine in a song, an engine with an `idle` state that has silent input and nothing ringing is not run and writes zeros.
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
| `ci/null-test.py` (Linux) | each plug-in as CLAP and as VST3 in Neo Plug (rebuzz-headless) renders null against the native machine; parameter count and units against `machine_describe`; defaults, changed and reopened-state phases |
| auval (macOS) | each AU passes `auval -v` |

## 7. Packaging and licences

CI (`.github/workflows/plugin-export.yml`) builds per OS: Windows `.clap` + `.vst3`; macOS `.clap` + `.vst3` + `.component`, ad-hoc signed (not notarised); Linux for testing. One artifact, "NeoBuzz Live Plug-ins", with the bundles, `NOTICE.txt` (CLAP MIT, clap-wrapper MIT, VST3 SDK MIT, AudioUnitSDK Apache-2.0, Avalonia, SkiaSharp, HarfBuzz, Inter OFL, .NET) and `INSTALL.txt` (including the AU registrar note: `killall -9 AudioComponentRegistrar` or a logout makes macOS see a new `.component`).

## 8. Limits and not done

- Not exported yet: Dye Essence, Neo FM6, the Neo Learn synths, Neo Drums, Neo Patch. They are descriptors if their engines fit the generator's shapes; each needs a null test and an editor check.
- Windows and macOS editor views are compile-checked and run only by CI's headless smoke test; real embedding (HiDPI, resize, focus) needs a hand test in Ableton.
- No host keyboard input in the editor, no live meters, macOS renders at 1x (blurry on Retina), macOS arm64 only.
- Each bundle carries its own copy of the libraries (about 40 MB); a shared install location would remove that.
- Ad-hoc signing only: Gatekeeper needs quarantine removal until a Developer ID is available.

## 9. Hand-test list (Ableton Live)

Windows: load the VST3 for Neo Filter / Neo Poly; editor opens, sizes correctly at 100 / 150 / 200 % scale, resizes; knobs move and record automation (gesture begin/end: "touch" mode); playback automation moves the knobs; save and reopen the set (state); Neo EQ latency compensation; sidechain routing for Neo Comp; Neo Poly plays from MIDI.
macOS: the same for VST3 and AU (rescan: AU registrar), the editor in a Live window, Gatekeeper behaviour of the unsigned bundles.
