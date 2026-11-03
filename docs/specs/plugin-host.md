# Plugin host (VST3, CLAP, VST2, AU) — Analysis and options

| | |
|---|---|
| Status | Decided 2026-11-03: V1 B, V2 A, V3 B, V4 A, V6 A; timing: the H0 spike and the plugin census now, H1–H3 after P3. Refines cross-platform.md D5 and P6. **H0 spike done (§8): its exit waits for your two commercial plugins (§8.7); the census tool is ready to run (§8.5)** |
| Date | 2026-11-03 |
| Scope | Whether to build our own VST/VSTi host machine, and how, instead of or beside Polac VST/VSTi 1.1: formats, isolation, performance, mapping to Buzz, GUIs on WPF and Avalonia, songs, agents, licences, effort |
| Code baseline | fork `f2ab63b` (main, 2026-11-03) on upstream `wasteddesign/ReBuzz` @ `0a48d88` (2026-10-30) |
| Related | cross-platform.md (D5, §4.5, P6), mac-engine.md (P2), studio-mvp.md (P3, `IMachineUiHost`), machines.md (Neo manifests, latency §3.1), mcp-server.md (§3.3 units, §12 tiers), LinkHelper/README.md (GPL helper precedent) |

---

## 1. Summary

**Verdict: build a new host, but beside Polac, not instead of it.**

- **The gain is macOS, CLAP and agents, not Windows VST.** On Windows, Polac already hosts VST2 and VST3, bridges 32-bit plugins and runs crash-isolated in ReBuzz's native host process. Its author still ships updates through upstream (b46 in September 2025, b47 in March 2026). On macOS it can never run: it is a closed Windows DLL. CLAP it doesn't do at all. And agents see only its generic "Track Parameter / Value" columns.
- **"Loads any VST" is realistic for current plugins, not for all of them.** CLAP + VST3 (both MIT now) cover practically every plugin sold or updated in recent years, on both OSes. VST2 can't be licensed any more. The only route is a GPL community header (not Steinberg's) in a separate GPL helper process, a legal grey area. 32-bit exists only on Windows, and Polac already bridges it.
- **Recommended shape:** a C# host machine ("Neo Plug") over a small C++ hosting library (VST3 SDK + CLAP). The same library runs either inside ReBuzz or in a sandbox process of its own, chosen per plugin, as Bitwig does.
  - Ship it in-process first: fastest, and plugin GUIs embed on both OSes.
  - Then add the sandbox: crash isolation like Polac's, and the base for 32-bit and VST2 helpers if they're ever wanted.
- **Effort:** 10–14 pw for CLAP + VST3 with the sandbox and GUIs on WPF and Avalonia. P6's 6–10 pw covers only the in-process part. AU, a 32-bit server build and a VST2 helper are add-ons (§5.2).
- **Before building:** a 1 pw spike, plus a census of the plugins your songs and plugin folders actually use. The census settles the VST2, 32-bit and Polac-song questions with data instead of guesses.
- **H0 (§8):** VST3 and CLAP, including Surge XT, load, play, and keep their state in a song, inside ReBuzz's engine on Linux, macOS and Windows. A sandbox costs 8–20 µs per block (hybrid wait) on Linux and Windows CI VMs. Plugin views embed in Avalonia's `NativeControlHost` on macOS and Windows, and floating windows work. The effort stays 10–14 pw.

### Decisions (taken 2026-11-03)

V1–V4 and V6 were decided as recommended. V5 was decided as a fourth option: the H0 spike and the plugin census now, then H1–H3 after P3 (the Mac studio MVP). So plugins come after the first Mac studio, not in it, and not as late as P4.

| # | Decision | Options | Recommended |
|---|---|---|---|
| V1 | Strategy | **A** keep Polac only · **B** new host beside Polac (Polac stays for old songs, VST2 and 32-bit on Windows) · **C** new host replacing Polac (adds VST2, 32-bit and a Polac song converter) | **B** |
| V2 | Formats in the first release | **A** CLAP + VST3 · **B** A + AU on macOS · **C** A + VST2 through a GPL helper | **A** |
| V3 | Isolation | **A** in-process only · **B** one library, two placements (in-process or sandbox, per plugin) · **C** sandbox only | **B** |
| V4 | Parameters in Buzz | **A** a bank of automatable slots, named at runtime, plus Polac-style param/value track columns · **B** a generated machine type per plugin, one Buzz parameter per plugin parameter · **C** slots without names | **A** |
| V5 | When | **A** after P4, as D5 says · **B** spike now, engine side after P2, GUIs with P3 S1, in the first Mac release · **C** Windows-only now (WPF), Mac after P4 | **B** |
| V6 | Old Polac songs on macOS | **A** placeholders that save back unchanged (they play on Windows) · **B** a converter (plugin, state, notes, mapped parameters) after a format study | **A**; B only if the census shows heavy use |

V5 B changes D5, which was decided as A on 2026-11-01: plugins would then arrive in the first Mac release.

## 2. Today: Polac VST/VSTi 1.1

### 2.1 What it is

- **Machines:** `Polac VSTi 1.1` (generator) and `Polac VST 1.1` (effect), plus `Polac Out II` (extra outputs), `MIDI In`, `MIDI Bus II`, Audio In/Out. Version 1.1.10b47 (28 February 2026), by Frank Potulski. Freeware: redistribution allowed, sale forbidden.
- **Files:** `RunTime/Gear/{Generators,Effects}/Polac VST(i) 1.1.x64.dll`; docs in `RunTime/Gear/Generators/pvstdocs/`; helpers in `RunTime/Gear/vst/` (`vst3shell`, `vsti3shell`, DMO, LADSPA and MIDI adapters) and `RunTime/bin64/` (`vst.x64.exe` bridge, `scan.x64.exe` scanner; `bin64/bin32/vst.exe` for 32-bit plugins).
- **How it runs:** a native machine, so ReBuzz loads it in `ReBuzzEngine64.exe` (or `32`), by default one process per machine. A plugin crash takes down that process only; ReBuzz marks the machine crashed and the song plays on.
- **Plugin list:** through the native "loader instrument" messages (`UIGetInstrumentList`, `UISetInstrument`). The instrument name is only used when a machine is created: songs don't store it (the `.bmx` and `.bmxml` readers create machines with no instrument). Polac keeps the chosen plugin in its own machine data, a closed format (§8.5).
- **ReBuzz knows it by name:** MIDI pitch bend and aftertouch are converted for it (`polacConversion` in `ReBuzzCore.cs`, `MachineManager.SendMidiInput`, `SessionCore.Recording.cs`), and loading can skip its init (`MachineManager.CallInit`).
- **Upstream ships it as its VST strategy.** The README promises "32 and 64 bit VST2/3 support"; that is Polac.
- **One more VST host is in the tree:** the signal-analysis window (`BuzzGUI.MachineView/SignalAnalysis`) loads VST2 plugins through VST.NET (`3rdParty/Jacobi`, LGPL-2.1, C++/CLI). It is Windows-only and VST2-only, so it is no base for a new host.

### 2.2 What it does well, and what it can't do

| Area | Polac 1.1.10b47 | Notes |
|---|---|---|
| Formats | VST2; VST3 through its `vst3shell` adapters; DMO, LADSPA, Buzz and Psycle machines as plugins | No CLAP, no AU |
| Platforms | Windows only | A Windows DLL: no route to macOS (cross-platform.md D2) |
| 32-bit plugins | Yes, through `bin32/vst.exe` | Option strings show bridge modes: dedicated process, async processing, embedded GUI |
| Crash isolation | Per machine, from ReBuzz's native host | Each plugin call is an IPC round trip already (§3.4) |
| Parameters | Learned or assigned per track: each track has a "Track Parameter" selector and a value; up to 128 at once | Patterns show "Track Parameter / Value", not the plugin's names |
| Tracker features | Track commands (retrigger, arps, chords, slides, LFOs, inertia, MIDI messages, program/bank, RPN/NRPN, VST3 note expression track), FT2-style commands, preset factory (morph, breed, randomize), oversampling | Unique to Polac. A new host shouldn't try to clone them |
| Multi-out | Through a separate `Polac Out II` machine | ReBuzz now has native multi-IO for machines |
| Multi-in, sidechain | A dialog picks inputs | |
| Latency and PDC | Not documented | Polac offers a 256-sample fixed-block mode for plugins that need it |
| Agents (MCP) | Opaque: generic columns, raw values, no units | mcp-server.md §12.1 puts Polac in "tiers 1 and 4 only" |
| Source | Closed | We can only replace it or harden around it |

**Hardening Polac itself isn't possible.** What we can harden is the host around it: for example, restarting a crashed native-machine process and reloading the machine's last state. That helps every native machine on Windows (about 1–2 pw), and it is independent of this decision.

## 3. How a host plugs into neo-buzz

A host machine has to map everything a plugin has onto what a Buzz machine has. The managed machine API (`IBuzzMachine`) already covers most of it.

### 3.1 The seams

| Plugin side | Buzz side today | Fit |
|---|---|---|
| One host, many plugins in the Gear menu | Native machines list "instruments" (`UIGetInstrumentList`, `UISetInstrument`); songs save `InstrumentName`. Managed machines have no such thing: one DLL is one machine type | **Core change:** a managed equivalent, so one host DLL lists every scanned plugin, as Polac does |
| Instrument vs effect | Machine type comes from the `Work` signature (`ManagedMachineDLL.GetWorkFunctionType`) | Two DLLs from one source: `Neo Plug` (generator) and `Neo Plug FX` (effect) |
| Audio buses | `Work(IList<Sample[]> out, IList<Sample[]> in, n, mode)` with `MULTI_IO`; `IBuzzMachineHost.InputChannelCount`/`OutputChannelCount`; `GetChannelName` | Good. A Buzz channel is a stereo pair (`Sample` = L, R at ±32768), so mono buses copy to both sides and surround buses fold down. Convert to ±1.0 planar floats per block, as the Neo glue does |
| Sidechain | Neo Comp takes its sidechain on input 2 | Plugin aux input (VST3 `kAux` bus, CLAP non-main port) → input 2 |
| Block size | ≤ 256 frames, split at tick boundaries (`WorkManager.MainAudioFillBuffer`), so sizes vary | Set the plugin's max block to 256. Offer a fixed-block adapter (reported as latency) for plugins that misbehave with short blocks, as Polac does |
| Parameters | Declared statically by `[ParameterDecl]` properties, read once per DLL by reflection | The core problem: §3.2 |
| Parameter timing | Values change at tick and sub-tick starts, which are block starts | Each change goes to the plugin at offset 0 of the block: exact. Ramps (Polac's "inertia") become several points in one block (VST3 `IParamValueQueue`, CLAP timed events) |
| MIDI in | `MidiNote(channel, note, velocity)` and `MidiControlChange(ctrl, channel, value)`, with no timestamp; pitch bend and channel aftertouch arrive as special controller numbers. Live input comes on the MIDI thread; engine MIDI columns (Session takes) are sent per block from the audio thread (`WorkManager`) | Queued, then delivered at the next block start. Live input jitters by up to one block (5.3 ms at 256 frames, 48 kHz); column events land at the start of the block they fall in. No poly aftertouch, no SysEx: timestamped events with both are an optional core change, and make column notes sample-accurate |
| MIDI out | `IMachine.SendMIDINote`, `SendMIDIControlChange` | A plugin's note output (arpeggiators) goes to a chosen machine |
| Latency | `GetLatency()`; the fork applies changes while running (`ManagedMachineHost.Tick` → `MachineCore.UpdateLatencyFromMachine`) | Good: VST3 `restartComponent(kLatencyChanged)` and CLAP `latency.changed` map straight onto it |
| Silence | `Work` returns false when the output is silent | From the plugin's tail (VST3 `getTailSamples`, CLAP `tail`) |
| State | `MachineState` property, saved in songs | Plugin identity, the plugin's state blobs, the slot map, host settings (§3.3) |
| GUI on Windows today | `IMachineGUIFactory` → a WPF window; native GUIs already embed across processes with `HwndHost` (`NativeEditorHost`) | Plugin view as an HWND child, in or out of process |
| GUI in the studio | `StudioMachineUiHost.GuiContent` returns an Avalonia `Control` per machine | A `NativeControlHost` around the plugin view: HWND on Windows, NSView on macOS (to confirm in the spike) |
| Agents | Manifests are per DLL (`ReBuzz.Agent/Model/Manifest.cs`, `SongModel.Manifest(IMachineDLL)`) | **Core change:** a per-machine manifest, built at runtime from the plugin (§3.6) |
| Headless and CI | Managed machines load in `rebuzz-headless` on Linux and macOS today; native machines don't | A managed host is testable on Linux CI with the SDKs' example plugins. A native Buzz machine isn't |

**Core changes needed** (small, fork-only, about 1 pw, inside H1): managed loader instruments; runtime parameter names and visibility for managed machines (`ParameterCore.Name` is already settable inside the engine, and `IParameterMetadata.IsVisible` exists); per-machine manifests for the agent; optionally timestamped MIDI with poly aftertouch and SysEx. H0 added two (§8.3): song load must not override plugin state through re-sent parameter values (skip stateless parameters on load, or slots mirror the plugin), and a main-thread tick for managed machines.

### 3.2 Parameters: the fixed-count problem

A Buzz machine type has a fixed parameter list. Plugins have anything from 0 to tens of thousands of parameters, and the set depends on which plugin is loaded.

| Model | How | For | Against |
|---|---|---|---|
| **A Slot bank** (recommended) | 64 global slots (word, 0–65534 over the plugin's normalized 0–1; stepped parameters use their own steps). Each slot binds to a plugin parameter ID: by learn (touch a control), by hand, or automatically (CLAP remote-control pages, else the first automatable parameters). Slots take the bound parameter's name at runtime; unbound ones are hidden. Track columns: Note, Velocity, Delay, plus a Polac-style "Param / Value" pair for anything beyond the bank | One machine type; robust to plugin updates (the map stores stable IDs: VST3 `ParamID`, CLAP `clap_id`); patterns stay small; Polac users know the idea | Needs the names/visibility core change; 64 is a cap (the pair columns lift it) |
| B Generated type per plugin | At scan time, emit a stub machine assembly per plugin, with one `[ParameterDecl]` per plugin parameter | Every parameter visible and automatable by name | Thousands of columns; songs store by position, so a plugin update that adds parameters breaks old songs; one stub DLL per plugin in Gear |
| C Slots without names | A without the core change | No engine change | Patterns say "Slot 17"; unusable by hand |

Value text comes from the plugin (VST3 `getParamStringByValue`, CLAP `value_to_text`), through `DescribeValue`.

### 3.3 State, presets and songs

- **Machine state** (versioned): format, plugin ID (CLAP id, VST3 class UID), name, vendor, version, a path hint; the plugin's state (VST3 component and controller blobs, CLAP `state`); the slot map; bus layout, MIDI channel, hosting placement.
- **Missing plugin** (another machine, or a Windows-only plugin on a Mac): the machine loads as "plugin missing", keeps its state and saves it back unchanged, as native placeholders already do (mac-engine.md M2).
- **Presets:** the plugin's own (VST3 `.vstpreset` and program lists, CLAP `preset-discovery`) in the host's browser. ReBuzz `.prs` presets hold only slot values.
- **Cross-platform songs:** VST3 and CLAP state is usually portable between Windows and macOS, but that is up to each vendor.
- **Polac songs:** Polac's saved data is closed. Reading it means a black-box format study, which is legal (cross-platform.md §4.4). A converter could carry over the plugin, its chunk, notes and assigned parameters, but not Polac's commands, arps or LFOs. A VST3 build of the same plugin can take its VST2 state if the vendor implemented Steinberg's VST2-replacement path (`moduleinfo.json` Compatibility, `tryVst2StateLoad`).

### 3.4 Isolation and performance

| | In-process | Sandbox: per plugin | Sandbox: per instance | Sandbox: shared |
|---|---|---|---|---|
| A crash takes down | ReBuzz (autosave since 0.4.1 limits the loss) | that plugin's instances | that instance | every sandboxed plugin |
| Cost per block | a call | 1 IPC round trip per instance | same | same |
| Memory | lowest | one process per plugin | one per instance | one |
| GUI on Windows | embedded | embedded (cross-process HWND, as ReBuzz's natives already do) | embedded | embedded |
| GUI on macOS | embedded (NSView) | floating window: macOS can't embed a view from another process | floating | floating |
| Who does it | REAPER's default; Polac without bridging (inside its own machine process) | Bitwig "By plug-in", REAPER "dedicated process" | Bitwig "Individually" | Bitwig "Together", REAPER "separate process" |

**IPC cost.** Every block crosses the boundary once per sandboxed instance: about 188 blocks a second per instance at 48 kHz, more with tick splits.

- ReBuzz's native host already pays this, and more: a named-event round trip per message, with `AudioTick` and `AudioWork` as separate messages.
- A new protocol batches parameters, MIDI and audio into one shared-memory message per block. The server waits by spinning briefly, then on an event.
- Engineering estimate, to be measured in the spike: a few µs per round trip spinning, around 10–20 µs on kernel events. Five sandboxed plugins in series on a 128-frame buffer at 48 kHz (2.67 ms) would then use about 2–4 % of the deadline.
- **Measured in H0 (§8.4), on CI VMs:**
  - spinning: under 1 µs;
  - kernel events alone: 14–32 µs p50;
  - the hybrid (the server sleeps, the engine's thread spins briefly for the reply): 8–20 µs p50 and 10–40 µs p99 on Linux and Windows;
  - macOS: 14–18 µs p50 and 41–68 µs p99 with Mach semaphores.

  Five plugins in series then use 2–8 % of the deadline (Linux and Windows) or 8–13 % (macOS VM). A server must never spin between blocks: the OS penalizes real-time threads that do.
- yabridge works the same way (sockets for control, shared memory for audio, priorities kept in sync) and reports performance comparable to native in most cases.

**Multi-threading.** ReBuzz's worker threads (`WorkThreadEngine`) run independent machines in parallel.

- An in-process plugin runs on the worker that calls it.
- A sandboxed plugin runs on a server thread while the worker waits, so parallelism is kept. The server needs one audio thread per instance it may be asked to run at once.
- **Async mode** (Polac has one): render one block ahead on the server and report one block of latency to PDC. It buys parallelism at the cost of latency. Offer it per plugin, off by default.

**Real-time.**

- **Our side:** no allocation or locks on the audio path, as the Neo machines and the 0.4.1 engine hardening already require; lock-free rings between the C# machine and the library.
- **The server:** C++, so no garbage collector. A C# NativeAOT server would bring back GC pauses.
- **Windows:** server audio threads get the same priority as the engine's workers.
- **macOS:** server audio threads join the device's audio workgroup. The engine passes the workgroup's Mach port (`os_workgroup_copy_port`); the server joins it (`os_workgroup_create_with_port`). This is the same need M7 solved in-process.
- **Plugins:** they may break real-time rules themselves. Only isolation helps there.

### 3.5 MIDI, MPE and note expressions

- **Notes from patterns:** Buzz tracks are voices. Note and Velocity per track become note-on/off with note IDs (CLAP note IDs, VST3 `noteId`). Per-track expression columns (tuning, pressure, brightness, pan, volume) map to CLAP note expressions and VST3 Note Expression events. That is polyphonic expression sequenced in the tracker, which Polac reaches only through its VST3 note-expression command.
- **MPE from a controller** (per-channel pitch bend, pressure, CC74 on channels 2–16): Buzz passes the channel, so it can be forwarded raw to CLAP plugins that accept MIDI, and converted to note expressions for VST3 (VST3 has no raw MIDI CC events; it maps CCs to parameters through `IMidiMapping`, now `IMidiMapping2` in 3.8).
- **MIDI 2.0:** VST 3.8 adds MIDI 2.0 controller interfaces; ReBuzz has a MIDI 2.0 device path on Windows. Not needed for a first release.

### 3.6 GUIs, HiDPI and agents

**GUIs.**

- VST3 `IPlugView` attaches to an HWND (Windows) or NSView (macOS). CLAP's `gui` extension does the same, and also allows floating windows.
- WPF: an `HwndHost` around the view, as `NativeEditorHost` does for native editors.
- Avalonia: a `NativeControlHost`. Native views always draw on top of Avalonia content, so nothing may overlap them.

**HiDPI.**

- Windows works in physical pixels. The host tells the plugin the scale: VST3 `IPlugViewContentScaleSupport`, CLAP `set_scale`.
- macOS works in logical points, and the host sends no scale (the CLAP header says so).
- DPI-unaware old plugins: give their window a lower DPI awareness on Windows. That is easier in a sandbox process.

**Agents (MCP).** A host can give agents what Polac can't. The manifest is built per instance from the plugin:

- parameter names and groups (VST3 units, CLAP module paths);
- ranges and defaults;
- steps and enum choices (the text of each step);
- units (VST3 `ParameterInfo.units`);
- exact text-to-value conversion where the plugin implements it (VST3 `getParamValueByString`, CLAP `text_to_value`), so "cutoff 1 kHz" means 1 kHz;
- curated controls from CLAP remote-control pages;
- the plugin's presets.

`render` runs faster than real time, so the host puts plugins in offline mode (VST3 `kOffline`, CLAP `render`): sample streamers then load synchronously instead of dropping notes.

### 3.7 Scanning and blocklists

- **Scan out of process** (`neoplug-scan`): one plugin per run, with a timeout. A crash or hang puts the plugin on the blocklist, with a "retry" for the user.
- **Cache** by path, size and modification time. Rescans are incremental and run in the background; the Gear menu updates when they finish.
- **VST3 bundles with `moduleinfo.json`** (SDK 3.7.5 and later) describe their classes without the scanner loading the binary.
- **Shell plugins:** a VST3 module's factory lists many classes (Waves' VST3 shells). Waves V15 removed VST2, so its VST2 `WaveShell` no longer matters.
- **Copy protection (iLok/PACE):** the checks run inside the plugin, so any host that loads it normally works. The scanner must allow the authorization dialogs some plugins show on first load. On macOS the app's `disable-library-validation` entitlement, already planned, lets third-party plugins load. Test with your own protected plugins in the spike.

## 4. "Loads any VST": what is realistic

| Format | Licence route | Coverage | Effort |
|---|---|---|---|
| **VST3** | SDK under MIT since 3.8 (October 2025); the C API under a BSD-style Steinberg licence | Practically every plugin sold today, on both OSes. KVR lists about 13,700 VST3 products | core of H1 |
| **CLAP** | MIT (version 1.2.10) | 513 titles from 133 developers (clapdb.tech), including u-he, FabFilter (Pro-Q 4), Surge, Plogue, Audio Damage. Hosts include Bitwig, REAPER, FL Studio, Studio One. Nearly all CLAP plugins also ship VST3. CLAP's value is the API: it is the simplest to host and the richest for us (note expressions, remote controls, text↔value, thread pool) | 1–2 pw; the first backend to build |
| **AU** (macOS) | AudioToolbox, no licence | About 13,600 AU products on KVR, but most Mac plugins also ship VST3. AU adds Apple's own AUs and AU-only freeware. macOS can host AUs out of process itself (AUHostingService), including Intel AUs under Rosetta while it lasts (general-purpose through macOS 27) | +2–3 pw (Objective-C shim, AUv2 and AUv3 views) |
| **VST2** | Steinberg stopped VST2 licences in October 2018 and won't license new hosts. Community headers that don't come from Steinberg's SDK exist: VeSTige (GPL-2.0-or-later; in LMMS and Ardour) and FST (GPL-3.0-or-later). Compiled into the host, either would put ReBuzz's distribution under the GPL; both can go into a **separate GPL helper program**, like `ReBuzzLink` (§5.3) | VST2-only plugins are mostly pre-2018 freeware and discontinued products. Vendors keep dropping VST2 (Steinberg's own hosts, announced in 2022; Sonnox 2022; Waves V15 2025). Polac covers them on Windows | +1–2 pw on top of the sandbox; legal grey area |
| **32-bit** | Our own code | Windows only; macOS has none since 10.15. Old freeware. Polac already bridges it | +1 pw: an x86 build of the sandbox server |
| **Misbehaving plugins** | – | Short or variable blocks, calls from the wrong thread, GUIs that don't resize, crashes on unload | Per-plugin quirk flags (fixed blocks, no resize, sandbox forced), the blocklist, the sandbox |

**What "any" can mean** (judgement, not measured):

- **CLAP + VST3:** well over 90 % of a current commercial collection on both OSes. Missing: VST2-only and 32-bit plugins.
- **+ VST2 helper:** nearly every 64-bit plugin.
- **+ 32-bit server:** the rest, on Windows only.

**For a long-time Buzz user the gap is larger**, because the freeware back catalogue is VST2 and often 32-bit. **Measure it before deciding V1/V2/V6:**

- extend the song census (cross-platform.md §8 item 8) to count Polac machines by plugin (guessed from Polac's machine data: songs don't store an instrument name);
- add a folder census of installed plugins by format and bitness.

That is about 0.5 pw.

**Excluded:** AAX (Pro Tools' format; hosting it needs an agreement with Avid), LV2 and LADSPA (Linux formats; Polac already wraps LADSPA on Windows).

## 5. Options

### 5.1 Compared

Effort is in person-weeks (pw) of focused work with coding agents doing most of the typing, as in cross-platform.md.

| | O0 Keep Polac | O1 Native Buzz machine (C++) | O2 Managed machine, in-process | **O3 Managed machine + library, in-process or sandbox** | O4 Carla as a GPL helper | O5 JUCE as a helper |
|---|---|---|---|---|---|---|
| Formats | VST2, VST3, 32-bit | CLAP, VST3 | CLAP, VST3 | **CLAP, VST3**; add-ons: AU, VST2, 32-bit | VST2, VST3, LV2, AU, SF2/SFZ; CLAP only in development builds | VST3, AU; no CLAP hosting (JUCE 9 adds CLAP plugins, not hosting); VST2 only with the withdrawn SDK |
| Effort, pw | 0 | 6–9 on Windows; macOS needs R1's portable native host (+5–8, shared with R1) | 7–9 | **10–14** (7–9 without the sandbox) | 8–12, plus upkeep | 8–12 |
| Crash isolation | per machine process | per machine process (inherited) | none | per plugin, chosen by the user | per bridge process | per helper process |
| macOS | never | after R1, floating GUIs only | yes; engine side runs in `rebuzz-headless` today | yes; embedded GUIs in-process, floating when sandboxed | secondary platform for Carla | yes |
| Agents, CI | opaque | native machines have no manifests and don't load on Linux CI | per-instance manifests; Linux CI | per-instance manifests; Linux CI | as O3, through a protocol we don't own | as O3 |
| Licence | freeware, closed | MIT; but Buzz's `MachineInterface.h` is freeware-only (§4.4 of cross-platform.md), which matters if the host is ever sold | MIT | MIT | Carla GPL-2.0-or-later in a helper; its bridge protocol is internal to Carla | AGPLv3 helper with its source shipped, or the free Starter EULA (≤ $20,000 revenue from JUCE use; no open-source licensing of JUCE parts) |
| Main risk | no Mac, no CLAP, no units | two native hosts to keep; Mac depends on R1 | a plugin crash ends ReBuzz | effort; Mac floating windows when sandboxed | last release 2.5.10 (July 2025); bridges "experimental"; Linux-first | licence churn (GPLv3 → AGPLv3 in JUCE 8, tiers changed); C++ framework for little gain over the VST3 SDK |

**Why not O1.** It looks cheapest on Windows, because ReBuzz's native host gives it isolation for free. But it is a Windows answer to a macOS problem. On macOS it waits for R1, a portable native host that is optional and not started. It can't be tested on Linux CI, and agents get nothing from it.

**Why not O4 (Carla).** Carla brings VST2 and bridging, but the machine, parameters, state, GUIs and agent work all stay with us. Talking to Carla's bridges means following an internal protocol that changes with Carla. Its Windows and macOS builds come second to Linux.

**Why not O5 (JUCE).** For hosting, JUCE adds little over the VST3 SDK (now MIT) and AudioToolbox. It has no CLAP hosting and no VST2 without the withdrawn SDK. Its licence has changed twice in two years.

**O2 vs O3.** O2 is O3's first half. Building O2 with O3's protocol boundary from day one costs almost nothing extra and keeps the sandbox open (§6).

### 5.2 Add-ons to O3

| Add-on | What | pw | When |
|---|---|---|---|
| AU (macOS) | AUv2 and AUv3 through AudioToolbox; AU views as NSViews | 2–3 | if the census shows AU-only plugins you use |
| 32-bit server | the sandbox server built for x86 Windows | 1 | if you want to retire Polac on Windows |
| VST2 helper | `neoplug-vst2`: the sandbox server with a VST2 backend on FST, licensed GPL-3.0, shipped with its source like `ReBuzzLink` | 1–2 | V2 C, or V1 C |
| Polac converter | black-box study of Polac's song data; plugin, chunk, notes, mapped parameters; before/after render report on Windows | 1–2 | V6 B |
| Native crash restart | restart a crashed `ReBuzzEngine` process and reload the machine's last state (helps Polac and every native machine) | 1–2 | independent |

### 5.3 The GPL helper route, honestly

- **The precedent:** `LinkHelper` is GPL-2.0-or-later. ReBuzz stays MIT because it only starts the program and exchanges text lines with it.
- **The FSF's line:** pipes, sockets and command-line arguments normally connect separate programs. Modules linked into one address space are one program. Communication "intimate enough, exchanging complex internal data structures" could also make them one.
- **Why a VST2 helper should land on the safe side:** it exchanges more than text (shared-memory audio and parameter events). But it speaks the same documented protocol our MIT server speaks for VST3 and CLAP. That shows it is a general interface, not one program's internals.
- **Conditions to keep:**
  - a separate executable;
  - ReBuzz works without it;
  - its source and licence ship beside it;
  - the protocol is documented in our repo.
- **AGPL adds nothing here.** Its extra clause covers users interacting with a modified program over a network; a local helper has none.
- **Copyright is one question, Steinberg another.** Headers written independently of the SDK avoid its copyright, as far as they really are independent. Steinberg still won't license VST2, and the vst-rs maintainers called this kind of binding "a grey area". Open-source hosts such as Ardour and LMMS ship VST2 hosting on VeSTige.
- **Trademarks:** name the format plainly ("VST 2 plug-ins") and use no Steinberg logo.
- **Not legal advice.**

## 6. Recommended design (O3)

```
ReBuzz / ReBuzz.Studio / rebuzz-headless
  Neo Plug, Neo Plug FX         C# managed machines (MIT): slots, notes, MIDI, buses, state, manifest
        │  plugin protocol: control messages + shared-memory blocks (audio, parameter and note events)
        ├── in-process: neoplug library loaded by the machine (C ABI)
        └── sandbox:    neoplug-server process (same library), per plugin / per instance / shared
neoplug (C++, MIT, CMake)       VST3 SDK hosting + CLAP; win-x64, osx-arm64, linux-x64 (CI); win-x86 later
neoplug-scan                    scanner and blocklist
neoplug-vst2 (optional, GPL-3)  the server with a VST2 backend on FST
```

- **One protocol for both placements.** The machine never calls a plugin directly. In-process, the library runs a message on the calling thread; sandboxed, the same message crosses shared memory. Switching a plugin between placements reloads it from its state.
- **Main thread.** In-process, plugin GUI and controller calls go to ReBuzz's UI thread through `IUiDispatcher`. The server has its own main thread and event loop, which is simpler.
- **Default placement.** In-process at first (H1). Once H2 ships: "sandbox per plugin" by default on Windows, in-process on macOS (embedded GUIs), sandbox per plugin or instance on demand, and forced for blocklisted or flagged plugins.
- **Tests.** Linux CI loads the VST3 SDK's example plugins (MIT) and the CLAP example plugins (MIT), and checks load, play, automation, state round trip, latency, offline render and a crash in the sandbox.

### 6.1 Phases

| Phase | When (V5 B) | Delivers | Exit | pw |
|---|---|---|---|---|
| **H0 Spike** | done 2026-11-03 (§8), but for your plugins (§8.7) | CLAP and VST3 in a throwaway managed machine on Windows; a toy shared-memory server; an NSView plugin view in an Avalonia `NativeControlHost` on macOS | Surge XT (CLAP and VST3), the SDK's AGain and Note Expression Synth examples, and two of your commercial plugins (one with iLok) load, play, save and reload in a song; the round trip is measured | 1 |
| **H1 Host, in-process** | after P3 (decided) | The core changes (§3.1); `Neo Plug` and `Neo Plug FX`; the library with CLAP and VST3; slots, notes, MIDI in/out, buses, sidechain, latency, tail, state; the scanner and blocklist; the Gear menu; the plugin GUI in a WPF window; per-instance manifests; offline render; Linux CI | Every CI plugin passes; a song with three plugins round-trips on Windows and in `rebuzz-headless` on macOS; `machine_describe` shows a plugin's parameters with units | 5–7 |
| **H2 Sandbox** | after H1 | `neoplug-server`; placements per plugin, per instance and shared; crash detection and reload from the last state; embedded GUIs on Windows, floating on macOS; the workgroup on macOS | Killing a server mid-song leaves the song playing and the plugin reloadable; five sandboxed plugins at 128 frames for an hour, no dropouts | 3–5 |
| **H3 Studio GUIs** | after H2 (the studio's GUI hook from P3 S1) | `GuiContent` for plugins: a `NativeControlHost` on Windows and macOS; HiDPI | A plugin GUI opens, resizes and scales in the studio on both OSes | 1 |
| **H4 Add-ons** | by the census | §5.2 | each its own | 0–8 |

**Total H0–H3: 10–14 pw.** cross-platform.md P6 (6–10 pw) matches H0 + H1 + H3; the sandbox is the difference. The plugin census (§4, about 0.5 pw) can run any time and isn't in the total.

## 7. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| VST3 hosting edge cases: bus arrangements, separate component and controller, `IConnectionPoint`, `restartComponent` flags, CC mapping | high | some plugins misbehave | Build on the SDK's hosting helpers; CI plugins; quirk flags; CLAP first to prove the design |
| A plugin crash ends ReBuzz before H2 | medium | lost unsaved work | Autosave (0.4.1); keep Polac as the Windows default until H2; ship H2 before calling the host the default |
| Sandboxed plugin GUIs float on macOS | certain | less integrated | In-process is the default on macOS; sandbox on demand |
| Real-time in a sandbox on macOS | medium | dropouts | The workgroup Mach port; the H2 hour test. H0 (§8.4): on the macOS VM the round trip's p99 is 3–4× Linux's, so H2's hour test runs on real Macs, server threads sleep on Mach semaphores and never spin |
| Song load overrides a plugin's restored state | certain without a fix (H0 §8.3) | plugins sound wrong after reopening | Slots mirror the plugin's values, or the engine skips stateless parameters on load (H1) |
| Studio has no app manifest on Windows | certain (H0 §8.6) | Avalonia's `NativeControlHost` throws | Add `app.manifest` with the supported-OS list in H3 |
| Plugins that aren't deterministic (Surge XT) | certain | sample-exact tests fail | Compare loudness per machine, as `headless-test.py` does |
| iLok or other protection fails in the scanner or sandbox | medium | a plugin won't load | Test your own plugins in H0 (§8.7: the probe is a separate process, like a sandbox); let scanner dialogs show; in-process as a fallback |
| VST2 helper (if chosen): Steinberg's stance | low to medium | takedown request | A separate GPL program; no logos; easy to drop from the package |
| Plugin updates change parameters | medium | slots point at missing IDs | Stable IDs in the slot map; missing IDs flagged in the machine and to agents |
| Users expect Polac's tracker features | medium | "it does less than Polac" | Keep Polac; later build arps and chords as generic Buzz MIDI machines, not inside the host |
| Ongoing triage of new plugins | certain | upkeep | Quirk flags and the blocklist; about 0.5–1 pw a year |

## 8. H0 results (2026-11-03)

The spike is in `experiments/plugin-host-h0/` (branch `experiments/plugin-host-h0`), its CI in `.github/workflows/plugin-host-spike.yml`, the census in `tools/plugin-census.py`. The product's code changed in one line: an opt-out so that `rebuzz-headless` starts on Windows (§8.3). CI: five runs, 41599180097 to 41605409479. The results below come from 41604298108 (Linux, macOS 15 arm64, Windows) and 41605409479 (Linux and Windows, all green); the IPC table also uses 41602913328.

**Verdict: H0's technical questions are answered; its exit waits only for your two commercial plugins (§8.7).**

- **Load, play, state:** every fixture works on all three OSes and in both formats, in the probe and inside ReBuzz's engine. That is AGain, Note Expression Synth (both classes), our CLAP synth and gain, and Surge XT (synth and effects) as VST3 and as CLAP.
- **The sandbox:** the doc's estimate holds for spinning and for the recommended hybrid wait. Kernel events alone cost more than guessed (§8.4).
- **macOS GUIs:** a plugin's NSView embeds in Avalonia 12's `NativeControlHost`, VST3 and CLAP alike. A floating window from a separate process also works (§8.6).

### 8.1 What was built

| Piece | What it is |
|---|---|
| `neoplug` (C++, about 2,000 lines, 1,600 of them hosting) | A C ABI (`neoplug.h`) over VST3 hosting and CLAP 1.2.10 (its headers only). VST3 uses the SDK 3.8.1's hosting helpers: `Module`, `PlugProvider`, `HostProcessData`, `EventList`, `ParameterChanges`. Covers: list classes, load, activate, parameters (info, value, text↔value, set), process with notes and parameter events, state in a small envelope, latency, tail, GUI attach, and platform helpers for the GUI checks. One CMake project for Linux, macOS arm64 and Windows x64 |
| `neoplug-probe` | The C ABI's test. It loads a plugin and plays a chord (instruments) or a sine (effects) on an audio thread of its own. It moves the first automatable parameter, saves the state, restores it into two fresh instances and renders both. Last, it opens the GUI in a floating window and checks the pixels |
| `neoplug-test.clap` | Our CLAP module (MIT): a synth, and a gain with 64 samples of latency. Both have state and a native GUI panel |
| Neo Plug Spike, Spike FX | A throwaway managed machine (`machine/NeoPlugSpike.cs`, generator and effect builds) with the plugin in-process through P/Invoke. It has 8 tracks of Note and Velocity, MIDI and one slot, and keeps the plugin's identity and state in `MachineState`. A missing plugin loads as "missing" and its state saves back unchanged |
| `headless-test.py` | The machine in `rebuzz-headless`, driven over MCP. It renders with a probe per machine, saves, reopens and compares. It also checks the restored parameter, the saved state and a missing plugin |
| `neoplug-ipc-bench` | The sandbox's toy server: a forked (POSIX) or spawned (Windows) process with shared memory and every wake mechanism (§8.4) |
| `NeoPlugGuiSpike` | A headed Avalonia 12.1.4 app. It puts each plugin's view in a `NativeControlHost`, then in a floating window, and records view trees and pixels |
| `run-h0.py` | Runs all of the above on one machine and writes `summary.md` and `summary.json`. It is CI's driver and your manual test (§8.7) |

Fixtures: AGain and Note Expression Synth (both classes) are built from the SDK in CI. `neoplug-test.clap` is ours. Surge XT 1.3.4 comes as VST3 and CLAP, synth and effects; it is GPL-3.0, so it is downloaded and installed on the runner only, never built in or shipped.

### 8.2 Loading, playing, state

Results by OS (run 41604298108, and run 41605409479 for Windows): "ok" means the plugin loads, plays, restores its moved parameter in fresh instances, and re-saves the same state.

| Plugin | Format | Linux x64 | macOS arm64 | Windows x64 | Open, ms | 256-frame block, µs (mean) | State |
|---|---|---|---|---|---:|---:|---:|
| AGain | VST3 | ok | ok | ok | 3–22 | 0.3–1.1 | 285 B |
| Note Expression Synth (and With UI) | VST3 | ok | ok | ok | 3–20 | 10–20 | 131–133 B |
| NeoPlug Test Synth | CLAP | ok | ok | ok | 0–3 | 5–10 | 48 B |
| NeoPlug Test Gain | CLAP | ok | ok | ok | 0–3 | 2–5 | 32 B |
| Surge XT Effects | VST3, CLAP | ok | ok | ok | 23–77 | 6–13 | 1.1 KB |
| Surge XT | VST3, CLAP | ok | ok | ok | 100–290 | 24–48 | 50 KB |

- **The restored parameter matched every time.** The state re-saves byte for byte.
- **Two fresh instances from one state render bit-identically on Linux.** Surge XT's synth is the exception everywhere: its oscillators start at random phases, so the loudness is the same and the samples differ. Note Expression Synth differs by −47 to −49 dBFS on macOS and Windows. Tests must compare loudness, not samples.
- **Processing is cheap, opening isn't always.** Surge XT takes 100–290 ms to open, Windows being slowest, and costs 24–48 µs per 256-frame block. That is 0.5–1 % of the block's 5.3 ms, plus spikes on first notes (up to 1.2 ms).
- **The plugin's state is what makes a song portable, and it can be large.** Surge XT's is 50 KB. In a `.bmxml` it is base64 inside `MachineState`'s XML, which is itself inside the base64 `Data` element: about 1.8× the raw size. Fine for the spike; H1 should store it more directly.
- **Surge XT's VST3 build has an audio input bus, and it's still an instrument.** The first version of the spike classed any plugin with an input as an effect and fed it no notes. Category first, buses second.

### 8.3 Inside ReBuzz's engine

In `rebuzz-headless` the song has five Neo Plug Spike generators, each followed by a Spike FX effect: NES, NES With UI, our CLAP synth, and Surge XT VST3 and CLAP, with AGain, our gain and Surge XT Effects (VST3 and CLAP) as effects. **All 65 checks pass on Linux and macOS:**

- every machine is audible in the render;
- the song saves, reopens and renders the same per machine (compare's verdict is kept, since Surge isn't deterministic);
- every moved parameter comes back after reopening;
- every machine's saved state is byte-identical after reopen and save;
- a machine whose plugin is gone loads as missing and saves its state back unchanged.

**On Windows all 65 pass too** (run 41605409479), after the fixes in items 4–6 below.

What the engine side taught. Each item is H1 work, not a blocker:

1. **ReBuzz re-sends every global parameter's saved value on song load, stateless ones too.** The spike's slot got its minimum on load and silenced every plugin. H1's slots must mirror the plugin's current value, so that re-sending is harmless. The alternative is a small core change: skip stateless parameters on load (add it to §3.1's list).
2. **A managed machine has no main-thread tick.** CLAP's `request_callback` and the VST3 controller's parameter echo need one; the spike services them only when the song saves. H1: a timer through `IUiDispatcher`.
3. **`MasterInfo` is valid only in `Work` and parameter setters.** But CLAP's `activate` and VST3's `setupProcessing` belong to the main thread, so a sample-rate change must hop to it and back. The spike activates once, at creation.
4. **ReBuzz disposes `IDisposable` managed machines when they are deleted or their song closes,** but not when the process exits. Plugins still loaded at exit crashed `rebuzz-headless`'s exit on Windows (0xC000000D), so the spike closes them on `ProcessExit`. H1: close every plugin on shutdown, on the main thread.
5. **Plugin editors need COM and OLE on the main thread on Windows,** as every DAW has. Without them VSTGUI's `createView` crashed. Avalonia's UI thread has them; the probe and `rebuzz-headless` didn't.
6. **`rebuzz-headless` didn't start on Windows:** it tried to launch the native machine hosts (`bin32/ReBuzzEngine32.exe`), which aren't beside it. `REBUZZ_NATIVE_MACHINES=off` (`NativeMachineHost.Supported`, default unchanged) treats native machines as placeholders, as on macOS and Linux.
7. **CLAP's `stop_processing` belongs to the audio thread.** The spike calls it from the main thread before deactivating. H1's protocol needs a "stop" that the audio side answers.
8. **Songs don't store Polac's instrument name** (§2.1 corrected): the plugin is inside Polac's own data.

### 8.4 The sandbox's cost

Per block, the client copies stereo audio and 256 bytes of events into shared memory and wakes the server. The server applies a gain and wakes the client back. The time measured runs from the copy in to the copy out.

- **Paced runs** send one block per period, as an audio callback does, so the server is idle between blocks. This is the realistic case for kernel wake-ups; back-to-back runs flatter them.
- **Mechanisms:** spin (both sides busy-wait), kernel (both sides sleep on the OS primitive) and hybrid (the server sleeps; the engine's thread spins up to 200 µs for the reply, then sleeps).
- **Priority:** threads get the audio priority each OS offers: SCHED_FIFO on Linux (as root), MMCSS "Pro Audio" on Windows, the time-constraint policy on macOS.
- **The runners are VMs** (Azure x64 with 4 vCPUs; Apple M1 VM with 3 vCPUs), and their hardware changes between runs. Wake-ups on real machines are faster, so read these as upper bounds.

Paced round trip at 128 frames, 48 kHz, in µs, as p50 / p99, for two runs (64 and 256 frames are within the same ranges):

| OS (priority) | Spin | Kernel | Hybrid |
|---|---|---|---|
| Linux, SCHED_FIFO (futex) | 0.1 / 0.2 · 0.5 / 0.8 | 16.3 / 20.8 · 22.5 / 33.0 | 8.5 / 10.2 · 13.2 / 20.5 |
| Linux, normal priority (futex) | 0.3 / 0.7 · 0.5 / 1.1 | 16.2 / 161 · 23.1 / 50.3 | 9.5 / 11.9 · 12.9 / 16.0 |
| Windows, MMCSS (events) | 0.4 / 9.1 · 1.6 / 11.1 | 13.9 / 20.9 · 31.9 / 54.1 | 8.0 / 16.2 · 19.6 / 40.4 |
| macOS, time-constraint (Mach semaphores) | 1.1 / 12,541 · 1.0 / 7,292 | – · 18.1 / 67.8 | – |
| macOS (POSIX semaphores) | | 22.0 / 83.7 · 20.4 / 107 | |
| macOS (`os_sync_wait_on_address`) | | 28.7 / 116 · 23.2 / 136 | 29.2 / 417 · 31.8 / 219 |

In-process, the same copies cost 0.1–0.3 µs.

- **The estimate in §3.4 holds for spinning, which is far under a µs, and for the hybrid: 8–20 µs p50 on Linux and Windows.** Kernel events alone cost 14–32 µs p50 there, above the 10–20 µs guessed.
- **A server must never spin between blocks.** It burns a core per sandbox, and the OS punishes real-time threads that never sleep. On Linux the RT throttle (950 ms per second) stalled the spinner for up to 12 ms. On macOS a spinning time-constraint thread is demoted: p99 of 3–37 ms, worst stalls of 0.6–2.6 s.
- **On macOS the hybrid is worse than sleeping.** The engine's spinning thread competes for the VM's three cores. Mach semaphores are the best primitive there: 14–18 µs p50, 41–68 µs p99. Mach port rights don't survive `fork`, and `mach_ports_register` no longer carries them on current macOS, so the server gets them in a Mach message through its inherited bootstrap port. H2 will need the same handover (or `posix_spawn` with a special port).
- **The design, then:**
  - the server sleeps on an address-based or counting kernel primitive: futex on Linux, a Windows event, a Mach semaphore on macOS;
  - on Linux and Windows the engine's thread spins briefly for the reply, then sleeps;
  - on macOS it sleeps at once, and the server's threads join the device's audio workgroup (§3.4).
- **Budget:** five sandboxed plugins in series at 128 frames (2,667 µs) cost 5 × p99:
  - Linux and Windows, hybrid: 50–200 µs, 2–8 % of the deadline (§3.4 guessed 2–4 %);
  - macOS VM, Mach semaphores: 200–340 µs, 8–13 %.

  Parallel chains overlap. H2's hour test must run on real Macs before sandboxing is offered there; it isn't the macOS default anyway, because in-process keeps GUIs embedded.

### 8.5 The plugin census

`tools/plugin-census.py` is plain Python 3.8+. It runs on Windows and macOS (and reads Linux folders too) and never loads a plugin.

- **Installed plugins**, from the standard folders:
  - VST2 on Windows: the VstPlugins and VST2 folders under both Program Files; the registry's `VSTPluginsPath` in HKLM and HKCU, in both registry views; **Polac's own `Plugin_Path` settings** under `SOFTWARE\Polac\VST Loader`; and `Gear\Vst` in Buzz installs;
  - VST2 on macOS: `/Library/Audio/Plug-Ins/VST` and the same folder under `~/Library`;
  - VST3, CLAP (with `CLAP_PATH`), and AU on macOS.

  `--folder vst2=DIR` adds other folders.
- **Per plugin:**
  - format;
  - bitness and architectures from the PE or Mach-O header (fat binaries list theirs; Intel-only Mac plugins are called out);
  - name and vendor from `moduleinfo.json` (JSON5), `Info.plist`, the AU's `AudioComponents`, or the PE version resource;
  - VST3 shell modules list their classes;
  - DLLs in VST2 folders without a `VSTPluginMain` or `main` export are not counted.
- **Songs** (`--songs DIR`, recursive): `.bmxml`, and `.bmx` (the MACH section read with PARA's parameter layouts; songs without PARA are searched for Polac's library strings).
  - For each Polac VST/VSTi machine, the plugin is guessed from Polac's data. In order: a DLL path in it (ASCII or UTF-16), an installed plugin's name in it, its first meaningful string, or the machine's name.
  - The JSON keeps the strings found, as hints.
- **The report:**
  - counts by format and bitness;
  - the VST2 plugins that also come as VST3 or CLAP, and the VST2-only ones (with how many of those are 32-bit);
  - AU-only plugins;
  - Polac use: songs, machines, plugins by song count, and whether each plugin is installed as VST3 or CLAP, only as VST2, or not at all.
- **Tests:**
  - `--self-test` builds synthetic Windows and macOS folders and songs and checks the results: PE files with exports and version resources, fat Mach-O bundles, AU components, `.bmxml`, and `.bmx` with and without PARA. It runs in CI on all three OSes.
  - On the runners, the census found Surge XT as VST3, CLAP and AU (macOS) and as VST3 and CLAP (Windows and Linux), and it read the 37 Buzz demo songs in `ExternalMachines/source`.

Run it on your computer (on Windows `py` instead of `python3`). Add `--songs` once per folder, and `--folder vst2=<dir>` for VST folders elsewhere:

```
python3 tools/plugin-census.py --songs "<your songs folder>" --out census.md --json census.json
```

Send `census.md`. It decides V6 (Polac songs on macOS: placeholders or a converter) and §5.2's add-ons (VST2 helper, 32-bit server, AU) with numbers.

### 8.6 macOS GUIs: embedded in Avalonia, and floating

**An Avalonia 12 `NativeControlHost` embeds a plugin's NSView: VST3 `IPlugView` and CLAP `gui` alike.**

- **How:** `CreateNativeControlCore` returns `new PlatformHandle(view, "NSView")` for a container NSView, and the plugin attaches its view to that container (`IPlugView::attached(container, "NSView")`, CLAP `set_parent`).
- **Checked on the macOS 15 runner** for AGain (VSTGUI), Note Expression Synth With UI (VSTGUI), our CLAP panel, and Surge XT VST3 and CLAP (JUCE):
  - the plugin's view sits in the Avalonia window's tree (`AvnWindow > AutoFitContentView > AvnView > NSView > container > VSTGUI_NSView or JUCEView`);
  - the window server's capture of that window shows the plugin drawn: 1,630 colours for AGain, 5,190 for Surge XT, against about 100 for an empty window.
- **Two things the studio must do:**
  - the container must have no autoresizing mask, and take the plugin's size before Avalonia lays it out. A mask on top of Avalonia's own frame management collapsed small views to 0×0 and shifted large ones (the first runs);
  - on Windows, the app needs an `app.manifest` with the supported-OS list, or Avalonia's Win32 `NativeControlHost` throws on its first native control. `ReBuzz.Studio` has none: H3 must add one.
- **Windows too:** the same app embeds the same plugins as HWNDs (Surge XT: 2,309 colours inside the Avalonia window).
- **Floating works everywhere:** a window owned by the library (an `NSWindow` or a top-level HWND) with the plugin's view inside. The probe runs in its own process, as a sandbox server would, and opened every editor that way on both OSes. So H2's "floating on macOS when sandboxed" is confirmed, and embedding across processes on macOS stays impossible with public API, as §3.4 says.

### 8.7 Your manual test: two commercial plugins, one with iLok

This is what only you can do, and it closes H0's exit. The CI run's `neoplug-h0-<OS>` artifact has everything. .NET 10 must be installed; it is, if you build neo-buzz.

1. Download the artifact for your OS from the run's page (Actions → "Plugin host spike (H0)"): `neoplug-h0-Windows-X64` from run 41605409479, `neoplug-h0-macOS-ARM64` from run 41604298108 (or both from any later run). Unzip it, then unzip the `neoplug-h0.zip` inside.
2. Clear the download flag:
   - macOS: `xattr -dr com.apple.quarantine neoplug-h0`;
   - Windows, in PowerShell: `Get-ChildItem -Recurse neoplug-h0 | Unblock-File`.
3. Run it with the VST3 or CLAP build of each plugin (the spike hosts no VST2):

   ```
   python3 neoplug-h0/run-h0.py --plugin "/Library/Audio/Plug-Ins/VST3/<Plugin>.vst3" --plugin "/Library/Audio/Plug-Ins/CLAP/<Other>.clap" --out h0-mine
   py neoplug-h0\run-h0.py --plugin "C:\Program Files\Common Files\VST3\<Plugin>.vst3" --plugin "C:\Program Files\Common Files\CLAP\<Other>.clap" --out h0-mine
   ```

   - Add `|<class name>` after a path to pick one class of a shell module.
   - Each step waits up to 5 minutes (`--timeout` raises it). If the iLok plugin asks for authorization the first time, answer it.
4. Per plugin, it runs:
   - the probe: load, play, state round trip, and the GUI in a floating window. The probe is a separate process from any host, which is the sandbox's situation for iLok;
   - Neo Plug Spike in `rebuzz-headless`: a song that plays, saves, reopens and compares;
   - the GUI embedded in an Avalonia window, and floating.

   Plugin windows open and close by themselves.
5. Send `h0-mine/summary.md` and `summary.json`. H0 closes when the probe says "yes" to loads, plays and state restored for both plugins, and the engine reports all checks passed.

### 8.8 What changes for H1–H3

- **H1 (5–7 pw, unchanged).** The library's shape holds: one C ABI, two backends of about 500–650 lines each, with the SDK's hosting helpers doing the VST3 heavy lifting. The managed glue (notes, effects, state, missing plugins) worked without core changes. Add to H1:
  - the items in §8.3: slots that mirror plugin values (or skip stateless parameters on load), a main-thread pump, rate changes on the main thread, closing plugins at shutdown, and COM on Windows;
  - a state format more compact than base64 inside XML inside base64;
  - tests that compare loudness for non-deterministic plugins.
- **H2 (3–5 pw, unchanged).** The protocol:
  - the hybrid wait on Linux and Windows, kernel-only on macOS, and never an indefinite spin;
  - the Mach-semaphore handover through a special port;
  - the hour test on a real Mac, since the macOS VM's p99 is 3–4× Linux's.
- **H3 (1 pw, unchanged).**
  - The embedding works on both OSes.
  - Add `ReBuzz.Studio`'s `app.manifest` (Windows), size the container from the plugin before layout, and handle resize requests (VST3 `IPlugFrame::resizeView`, CLAP `request_resize`).
- **Total stays 10–14 pw.** The spike retired the GUI-embedding and IPC unknowns. What remains unknown is commercial plugins under copy protection (your test) and real-time sandboxing on real Macs (H2).

## 9. Sources

Repository evidence, measured on the worktree (fork `f2ab63b`) on 2026-11-03:

- Polac files in `RunTime/`, its manual (`RunTime/Gear/Generators/pvstdocs/main.html`), option strings in its x64 DLLs, and upstream commits `5b70a5e` (b46, 2025-09-23) and `195d505` (b47, 2026-03-01).
- `ReBuzz/ManagedMachine/`, `ReBuzz/NativeMachine/`, `ReBuzz/MachineManagement/`, `ReBuzz/Audio/WorkManager.cs`, `ReBuzzGUI/BuzzGUI.Interfaces/MachineInterface/`, `ReBuzz.Agent/Model/Manifest.cs`, `ReBuzz.Studio/Platform/StudioMachineUiHost.cs`, `LinkHelper/README.md`.

Web sources:

- VST 3.8 press release (MIT): https://ocl-steinberg-live.steinberg.net/_storage/asset/819253/storage/master/Press%20Release%20-%202025-10-29%20-%20VST%203.8%20-%20EN.pdf
- VST 3.8.0 SDK release notes (MIDI 2.0 interfaces, `IMidiMapping2`): https://forums.steinberg.net/t/vst-3-8-0-sdk-released/1011988
- VST 3 SDK repository and MIT licence: https://github.com/steinbergmedia/vst3sdk and https://raw.githubusercontent.com/steinbergmedia/vst3sdk/master/LICENSE.txt
- VST 3 C API and its BSD-style licence: https://github.com/steinbergmedia/vst3_c_api and https://raw.githubusercontent.com/steinbergmedia/vst3_c_api/master/LICENSE.txt
- VST 3 C API announcement (2022): https://forums.steinberg.net/t/new-vst-3-c-api-released/816413
- VST 3 `moduleinfo.json` (3.7.5): https://steinbergmedia.github.io/vst3_dev_portal/pages/Technical+Documentation/Change+History/3.7.5/ModuleInfo.html
- Replacing a VST 2 plug-in with VST 3 (UIDs, `tryVst2StateLoad`, `IRemapParamID`): https://steinbergmedia.github.io/vst3_dev_portal/pages/Tutorials/Guideline+for+VST3+replacing+VST2.html
- Steinberg: no VST2 licences for new hosts (2020): https://forums.steinberg.net/t/can-i-create-vst-2-support-for-host-application-vst-3-license/202012
- Steinberg hosts drop VST2 (2022): https://www.gearnews.com/steinberg-discontinues-support-for-vst2-plug-ins
- Sonnox drops VST2 (2022): https://support.sonnox.com/en/support/solutions/articles/22000290221-vst2-discontinuation-migration-to-vst3
- Waves V15 removes VST2 (user reports, 2025): https://forum.waves.com/t/v15-plugins-backwards-compatibility-fix/11059
- VeSTige header (GPL-2.0-or-later), LMMS: https://raw.githubusercontent.com/LMMS/lmms/master/include/aeffectx.h
- VeSTige in Ardour: https://ardour.org/files/doxygen/vestige_8h_source.html
- FST (GPL-3.0-or-later): https://metadata.ftp-master.debian.org/changelogs/main/f/fst/stable_copyright
- vst-rs deprecation ("a grey area"): https://www.github.com/RustAudio/vst-rs/issues/193
- CLAP repository (MIT, extensions): https://github.com/free-audio/clap
- CLAP version 1.2.10: https://raw.githubusercontent.com/free-audio/clap/main/include/clap/version.h
- CLAP GUI extension (embedded and floating, scaling per OS): https://raw.githubusercontent.com/free-audio/clap/main/include/clap/ext/gui.h
- CLAP hosts and vendors: https://cleveraudio.org/hosts-and-plug-ins/
- CLAP database statistics (513 titles, 133 developers): https://clapdb.tech/stats
- KVR VST3 and AU listings (page counts, 2026-11-03): https://www.kvraudio.com/plugins/vst3/newest and https://www.kvraudio.com/plugins/au/newest
- Bitwig plug-in hosting modes: https://bitwig.com/userguide/latest/vst_plug-in_handling_and_options
- REAPER bridging modes: https://reaper.blog/2012/02/run-plugin-as-dedicated-process/
- yabridge performance design: https://deepwiki.com/robbert-vdh/yabridge/6-performance-considerations
- Carla (GPL-2.0-or-later, formats, experimental bridges): https://github.com/falkTX/Carla
- Carla releases: https://github.com/falkTX/Carla/releases ; 2.5.10 reported 2025-07-31: https://bugzilla.redhat.com/show_bug.cgi?id=2385833
- JUCE 8 licence (Starter free up to $20,000; §2.3): https://juce.com/legal/juce-8-licence/
- JUCE dual licence (AGPLv3 or JUCE licence): https://forum.juce.com/t/do-i-need-the-starter-or-indie-licence/69021
- JUCE roadmap (CLAP authoring in JUCE 9): https://juce.com/?p=1627
- JUCE VST2 hosting needs the old SDK: https://forum.juce.com/t/linux-vst-and-audiopluginhost/30940/2
- GPL FAQ on separate programs, as quoted: https://lwn.net/Articles/999841/
- AGPL section 13 explained: https://writing.kemitchell.com/2021/01/24/Reading-AGPL
- Out-of-process Audio Units on Apple silicon: https://developer.apple.com/documentation/audiotoolbox/debugging-out-of-process-audio-units-on-apple-silicon.md
- Sharing an audio workgroup with another process: https://developer.apple.com/documentation/os/os_workgroup_copy_port.md
- Avalonia native control hosting (z-order and clipping limits): https://docs.avaloniaui.net/docs/app-development/native-interop
- VST.NET (VST2 only, Windows, LGPL-2.1; used by ReBuzz's signal-analysis window): https://github.com/obiwanjacobi/vst.net

Not reachable during this analysis: the FSF's GPL FAQ (gnu.org) and Steinberg's ModuleInfo-JSON page. Their content is cited through the pages above.
