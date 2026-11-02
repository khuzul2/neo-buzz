# ReBuzz MCP server — Specification

| | |
|---|---|
| Status | A0–A5 and A7 built (2026-11-02); A6 (evaluation) and Neo Analyzer in progress. As built: §3.5 |
| Date | 2026-10-31, updated 2026-11-02 |
| Scope | An MCP server that lets an AI agent (Claude Code or any MCP client) compose, design sounds, mix and master in this ReBuzz fork, with tools clear enough to use without guessing, guides for the craft, and the analysis needed to check its own work |
| Protocol | MCP 2026-07-28, also negotiates 2025-11-25, 2025-06-18 and 2025-03-26 (stdio and Streamable HTTP; structured tool output; progress). Own protocol layer, no SDK (§3.5) |
| Related | docs/specs/machines.md (Neo machines), docs/specs/session-view.md |

---

## 1. Summary

An agent connected to ReBuzz should be able to take "make a 2-minute melancholic synthwave track at 92 BPM, mixed and mastered for streaming" and deliver it. It should work the way a good producer does: build, listen, measure, adjust.

Three things make that possible:

1. **Hands.** Tools that create and change everything in a song: machines and connections, parameters in real units, presets, patterns, notes, automation, the arrangement and the session grid, samples.
2. **Ears.** Faster-than-real-time rendering of any part of the song, plus measurements an agent can reason with: loudness, true peak, spectrum, dynamics, stereo, masking between tracks, pitch and tempo. Plots come back as images the agent can look at.
3. **Craft.** Guides the agent reads before it acts: the studio's concepts, composition, sound design per machine, mixing and mastering, with concrete numbers and checks.

The server runs **inside ReBuzz**. It works with the window open, where the user watches and can undo every agent edit, or headless with no window and no audio device, for autonomous work and CI.

## 2. What exists today

From a code inventory (file references in §13):

| Area | Today | Consequence |
|---|---|---|
| Remote control | None: no pipes, sockets, HTTP or scripting. A command-line file argument; MIDI in; controller scripts loaded from `Controllers/` | The server is new code; nothing to wrap |
| Public API | `IBuzz`, `ISong`, `IMachineGraph` (undoable create/connect/delete), `IMachine`, `IParameter`, `IPattern`/`IPatternColumn`, `ISequence`, `IWavetable`, `ISession` | Most hands-on operations exist |
| Pattern notes | Core patterns have no note columns: notes live in the Modern Pattern Editor. Ways in: `PatternEditorMachineMIDIEvents` and Export/ImportClipNotes (used by the Session View). The engine plays MIDI columns and parameter columns tagged `SessionAutomation` | Notes go through the clip-notes path; automation through tagged parameter columns |
| Rendering | `WorkManager.MainAudioFillBuffer` renders any amount of audio, faster than real time, with no device (the test Driver uses it). `IBuzz.RenderAudio` needs an open driver; the HD recorder is window code | Offline render uses the WorkManager path; internal, so the server lives in the ReBuzz assembly's trust boundary |
| Headless | `ReBuzzCore` runs without a window, with injected dispatcher, registry, file choices and user messages (the test Driver does it). Windows only | A headless mode is a small step, not a rewrite |
| Threading | One audio lock per render buffer; a per-machine lock for parameter sets; property changes on the UI dispatcher | All tool calls go through one engine queue |
| Undo | Graph actions are undoable (`SongCore.ActionStack`, action groups); the Session View has its own stack | Each mutating tool call becomes one undo group |
| Modal dialogs | Open, save and preset failures call `MessageBox` | The server injects non-modal user messages and turns them into tool errors |
| Analysis | Onsets, tempo and slices (`Core/Clips/AudioAnalysis.cs`), an FFT. No loudness or true peak | A real analysis library is needed (§7) |

## 3. Architecture

```
Claude Code ──(stdio)──────────► ReBuzz.exe --agent            headless: no window, no audio device
Claude Code ──(Streamable HTTP)─► ReBuzz.exe (window open)     http://127.0.0.1:<port>/mcp, token

ReBuzz process
  ReBuzz.Agent (new assembly, pure C#: builds and tests on Linux)
    Mcp              JSON-RPC 2.0, schemas and validation, tools, resources, prompts, progress; stdio and HTTP
    EngineQueue      every call runs on the engine/UI thread, one at a time, with a timeout
    Model            stable names, units, time in beats, notes as MIDI numbers
    Render           offline renders (WorkManager), probes on any machine's output
    Analysis         NeoBuzz.Analysis: loudness, spectrum, dynamics, stereo, masking, pitch, key
    Guides           markdown guides and generated machine manuals
```

### 3.1 Two modes, one tool set

- **Studio (window open).** `Settings → Agent → Allow agents` starts Streamable HTTP on `127.0.0.1`.
  - The port and a bearer token are shown with a ready-to-paste `claude mcp add --transport http …` line.
  - The `Origin` header is validated and only localhost is served.
  - The user sees every change as it happens; an "Agent" badge shows when a call is running.
  - Every agent edit is one undo step labelled `Agent: <what>`.
- **Headless.** `ReBuzz.exe --agent [song.bmxml]` serves MCP on stdio.
  - No window and no audio device; playback tools render instead.
  - For autonomous sessions and for CI evaluations (§10).
  - Song files written are ordinary songs the user can open later.

### 3.2 Execution model

- **One engine queue.** Tools run one at a time on the engine thread (the WPF dispatcher in studio mode, a dedicated thread in headless mode). Reads are cheap; writes take the locks ReBuzz already uses. A call never blocks the audio callback for longer than a parameter set does today.
- **Long work** (renders over ~2 s, analyses of long ranges, bounces) reports progress notifications. With the tasks extension it runs as a task the client can poll or cancel.
- **No modal dialogs.** The server injects `IUserMessages`/file-choice implementations that fail fast with a message instead of opening a dialog. Those messages become tool errors that say what to do next.
- **Undo.** Each mutating call opens an action group and closes it at the end, so `undo` reverts a whole call. Session View edits go to its own stack inside the same logical step.

### 3.3 Addressing and units (the agent never guesses)

| Thing | How the agent names it | Notes |
|---|---|---|
| Machine | its name in the song (`"Bass"`) | Names are unique in Buzz; renames return the new name |
| Gear (a machine type) | its gear name (`"Neo Poly"`) | From `gear_list` |
| Parameter | its name (`"F1 cutoff"`), or `#index` when a legacy machine repeats names | Track parameters take `track` |
| Pattern | machine + pattern name | |
| Time | **beats** (float) from the song start; bars accepted as `"17.1"` (bar.beat) | Ticks, TPB and Buzz's 240-per-tick time base stay inside the server |
| Pitch | MIDI number or scientific name: `60` = `"C4"`, `69` = `"A4"` = 440 Hz | The Buzz pattern editor shows the same notes an octave higher (`C-5`, `A-5`); tools always say which they mean |
| Levels | dB everywhere (connection gain, faders, meters); pan −1 … +1 | Raw Buzz amp/pan values are converted |
| Parameter values | in the parameter's own unit: `"800 Hz"`, `"-6 dB"`, `"35%"`, `"Saw"`, `"1/8"`, or relative `"+3 dB"`, `"x2"`; `raw:512` as the escape hatch | Neo machines publish exact units and mappings (§8.3); legacy machines are resolved by searching their value descriptions |

### 3.4 Safety

- Destructive calls carry the MCP `destructiveHint` annotation and use elicitation to confirm in studio mode: deleting machines, a new song over unsaved changes, overwriting files.
- File access is limited to the song's folder, the user's configured sample and project folders, and the server's render folder.
- An agent log (call, arguments, result, duration) is kept per session. The user can open it from the Agent badge.

### 3.5 As built (A0, A1)

**Two projects.**

| Project | What | Tests |
|---|---|---|
| `ReBuzz.Agent` | Pure C#, no ReBuzz reference: the protocol (`Mcp/`), stdio and HTTP transports, the engine queue, the agent log, the model (pitches, beats, values in units) and, linked in, `NeoBuzz.Analysis` | `ReBuzz.AgentTests` on Linux, in Fork CI |
| `ReBuzz/Agent` | The glue inside ReBuzz: `AgentHost` (engine calls on ReBuzz's dispatcher, one undo group per changing call, dialogs captured), the tools, headless `--agent`, studio mode (settings page, token, connection window, Ctrl+Shift+A) | `ReBuzzTests/AgentServerTests` on Windows, in the package workflow |

**Own protocol layer instead of the C# SDK.** Decided while building A0:

- NuGet is unreachable from the build sandbox, so an SDK dependency could only be compiled in CI. The protocol layer is the part that most needs fast local tests.
- What the server uses of MCP is small: JSON-RPC, tools with schemas and structured output, resources, prompts, progress, cancellation, logging. About 1,500 lines with tests.
- Error quality is the product. The layer validates arguments against each tool's schema and answers in the agent's terms: `tempo: unknown argument (known: tempo_bpm, …)`, `notes[1].start: missing (required)`, and unknown tools with "Did you mean".
- The SDK can replace the layer later behind the same `ToolDef`s if the protocol moves faster than we can follow. The tasks extension (long renders) is the first candidate for that.

**HTTP.** A `TcpListener` on 127.0.0.1 (no HttpListener URL ACLs, no admin rights):

- bearer token, `Host`/`Origin` checks, `Mcp-Session-Id`;
- 202 for notifications, 405 for GET; DELETE ends a session;
- server-sent events when the client sends a `progressToken` and accepts `text/event-stream`.

**Values in units** (`Model/Values.cs`). `param_set` changes take one of three forms:

- `value`: `"800 Hz"`, `"-6 dB"`, `"35%"`, `"4:1"`, `"Saw"`, `min`/`max`/`default`, `raw:512`;
- `by`: `"+3 dB"`, `"-2 st"`;
- `times`: `2`, `0.5`.

Neo machines resolve through their exact mappings. Legacy machines resolve by searching their value descriptions, with log distance for Hz and seconds. A wrong unit or choice is an error that lists examples.

**Manifests (A1).** `tools/gen-machine-params.py` writes `<Name>.machine.json` from each Neo machine's Params.def, next to its generated code. The build copies it next to the DLL. It holds:

- a summary line (`summary` in Params.def);
- each parameter's kind, mapping, unit, choices and group.

`NeoBuzz.MachineTests` fails if a manifest drifts from its parameter table. The server ignores a manifest entry whose range doesn't match the machine's, and falls back to the machine's value texts.

**Undo (A1).**
- Each changing call is one undo group. Steps ReBuzz has no action for get their own `IAction`s (`AgentActions`): parameter sets, connection gain and pan, mute, solo and bypass, notes, pattern renames, tempo and ticks per beat. These find machines and patterns by name when they run, as BuzzGUI's actions do.
- Pattern creation, deletion, arrangement places and markers reuse BuzzGUI's actions.
- A call that fails part-way undoes its own group, so it changes nothing.

**Notes (A1)** go into each pattern's *engine MIDI column*, the one the Session View records takes into (session-view.md 0.7):
- The engine plays it to the machine as MIDI. Songs save it, and undo keeps it.
- The Session View's piano roll edits it.
- The Pattern View doesn't show it. The slot menu's "Move notes to the Pattern View" moves the notes onto the Modern Pattern Editor's note lanes.
- Writing into the pattern editor's own store instead is a later option. It would need the editor created per machine, and its undo is separate.

**Render (A1)** runs a second `WorkManager`, as `ReBuzzTests`' Driver does, on the engine thread and under the audio lock:
- It temporarily sets the loop to the range, aligns to the next tick, plays, then stops for the tail.
- Afterwards it puts back the play position, loop, markers, mutes (solo and mute lists are applied as temporary mutes) and the unsaved-changes flag.
- In studio mode the user's audio output pauses for the duration.
- Output is a 32-bit float WAV under the agent log folder's `renders/`, plus the loudness and peak report.

**A2–A3 as built.**
- **Probes** subscribe to the first output connection's tap (it runs on the engine thread, in order), scale by that connection's gain and pad or trim to the master's length. Each gets its own id (`r3:Bass`).
- **Renders wait for machines that are still preparing.** A managed machine can expose `public bool Busy`, and renders wait up to 5 s while any reports it. Neo Wave reports it while it builds a table in the background.
- **Presets** are read from the gear's `.prs.xml`/`.prs` beside its DLL, without ReBuzz's dialogs. Loading one is an undo step that captures the whole machine state, data included.
- **Snapshots** live for the session in the host. A restore is one undo step made of parameter, flag and connection actions.
- **Song markers** are a new `SongMarkerStore` in the engine, saved in a "SongMarkers" subsection that upstream ignores.
- **Automation** goes into the pattern's engine-played parameter columns (`SessionAutomation`), the same columns the Session View records into. Linear ramps are written as events every quarter tick.
- **Session grid edits** go through `SessionLayout`, and the agent's undo step restores layout snapshots. The Session View's own undo also sees them.
- **Machine data imports** follow a convention. The manifest lists `imports` (Params.def `import` lines), and the managed machine has `public string ImportData(string kind, string path, int item, Action<int,int> setParameter)`. The machine's data before and after, plus any parameters it sets, make up one undo step. Neo Wave takes `wavetable`; Neo FM6 takes `dx7_bank`.
- **Machine readings** (A4, for `machine_readings`) follow a convention too. The manifest lists `readings` (Params.def `reading` lines: key, unit, meaning), and the managed machine has `public IReadOnlyDictionary<string, double> Readings { get; }`, read by reflection from any thread: each get returns a new read-only dictionary copied from the machine's newest snapshot (published by the audio thread through a lock-free triple buffer), which nothing changes afterwards. Keys are snake_case with their unit; silence is −∞. Neo Analyzer has 48: `momentary_lufs`, `short_term_lufs`, `integrated_lufs`, `range_lu`, `true_peak_dbtp`, `momentary_max_lufs`, `short_term_max_lufs`, `seconds`, `correlation`, `width`, the named bands `sub_db` … `air_db` and the third octaves `band_20hz_db` … `band_31_5hz_db` … `band_20000hz_db` (windows: 400 ms, 3 s, or since the reset; machines.md §5). The values stay after the transport stops or a render ends; `public void ResetReadings()` (or the Reset parameter) starts the integrated ones again.
- **Theory and transforms** are pure (`ReBuzz.Agent/Model/Theory.cs`, `Transforms.cs`, 98 chord symbols tested). `notes_transform` replaces a pattern's notes in one undo step.

**Guides (A5)** live in `ReBuzz.Agent/Guides/`, embedded:
- Seven guides (§6). Each ends with a checklist whose items name the call that checks them.
- A curated page per Neo machine (`Guides/machines/<Gear>.md`). `MachineManual.Build(manifest, curated, presets)` merges it with tables generated from the manifest (unit, range, default and meaning of every parameter; numbered groups that repeat, such as the FM6 operators, shown once) and the presets. `MachineManual.Resource(manifest, presets)` serves it as `rebuzz://machines/<slug>/manual` (`neo-poly`), and `guide(machine=…)` returns it when the ReBuzz side passes a provider to `GuideLibrary.Register`.
- Prompts from `GuideLibrary.Prompts()`: a step plan with the brief worked out (bars from a length, a loudness target from a platform), then the guides in full.
- Neo EQ, Limiter and Utility are named by their parameters (A4).
- `GuideTests` keep them true: tool names and arguments against a table of the built tools, Neo parameter names and values against the manifests, preset names against the factory presets.

**A7 as built (live, studio mode).**
- **Headless answers with the way to hear instead.** `transport`, `session_launch`, `session_record`, `midi_play` and `meters_read` play live for the user. Headless ReBuzz has no audio output, so they answer there with the way to hear instead: render, `arrangement_place`, `notes_write`.
- **Not undo steps.** These calls change what plays, not the song. A take goes on the Session View's own undo history, and `pattern_delete` removes one.
- **Launching** goes through the Session View's own entry points (`SessionLayout.LaunchScene` and `LaunchClip`, `SessionCore.Stop`, `StopAll`, `BackToArrangement`). So a launch waits for the clip's or the session's quantization exactly as a pad press does, and it starts a stopped transport. The state returned names, per track, the clip playing and the clip waiting.
- **Takes.** `session_record` can:
  - arm a track;
  - start a take in its first empty slot (bars given for that take only; the Session View's setting is put back);
  - stop a take, disarming a track it armed;
  - capture what armed tracks were played lately (`SessionLayout.CaptureMidi`).
- **`midi_play`** runs phrases on a thread of its own (`LivePlayer`).
  - **Timing.** Phrases are timed by the session's clock as heard, the clock a take stamps. A phrase started on the bar lands on the bar in a take and follows tempo changes. On Windows the thread waits on a high-resolution waitable timer, as the MIDI clock output does. With the transport stopped, the wall clock times it from the tempo.
  - **Routing.** An armed machine gets the notes through the session's keyboard input (`SessionCore.OnMidiInput`), so it plays and records them. Any other machine gets them directly.
- **`meters_read`** fills `LiveMeter` windows (pure, in `ReBuzz.Agent/Audio`):
  - from the master's tap, on the UI thread;
  - from machines' first output connection, on the audio thread, through arrays allocated up front, so the audio thread neither allocates nor waits.

  It then measures each window with the analysis library, so live numbers mean what `analyze`'s do: momentary loudness now, short-term from a 3 s window, integrated over the window, true peak, RMS and clipped samples. The window is counted in audio frames, with a wall-clock limit for an output that isn't running.
- **Subscriptions are not built.** §5.8's subscribable meters resource was dropped for now: clients surface tools, not resource updates, to the model, and Streamable HTTP would need a GET stream for server-sent updates. A windowed `meters_read` covers "react to live levels".
- **`machine_readings`** reads a managed machine's `public IReadOnlyDictionary<string, double> Readings`, a snapshot it swaps in from the audio thread. Neo Analyzer is the first machine to have one. In headless mode the values are what the last render played through it.
- **Tests** pump the audio as the device would, and freeze the session's timestamp for exact take stamps: `AgentA7Tests` in the package run, `LiveMeterTests` on Linux.

## 4. Tool design rules

These rules are what makes the server "extremely clear":

1. **Names are `area_verb`** (`machine_add`, `notes_write`, `render`, `analyze_mix`). There are about 45 tools, grouped so a client's tool search finds them.
2. **Every description answers four questions in two or three sentences:** what it does, when to use it, what it returns, and one example call. Units are stated in the parameter schemas.
3. **Structured output.** Every tool declares an `outputSchema` and returns `structuredContent`, plus a one-paragraph text summary for clients that only read text. Big results (audio, long note lists, spectra) come back as resource links, not inline blobs.
4. **Errors say the next step.** For example: `Unknown parameter "Cutoff" on "Bass" (Neo Poly). Did you mean "F1 cutoff"? Call machine_describe("Bass") for the list.` Errors set `isError` so the agent can recover.
5. **Batch by default.** Calls that change values accept lists (`param_set` with many changes, `notes_write` with many notes) and apply them as one undo step.
6. **Annotations.** `readOnlyHint` on every read, `idempotentHint` where repeating is harmless, `destructiveHint` on deletions, `openWorldHint: false` throughout.
7. **No hidden state.** Tools never depend on a "selected" machine or pattern; everything is named in the call. The only state is the song, plus named snapshots and renders, which are listable.
8. **Everything the agent writes, it can read back** in the same shape. For example, `notes_read` returns exactly what `notes_write` takes.

## 5. Tools

Phases (§9) are marked A1 … A7.

### 5.1 Orientation

| Tool | Phase | Purpose |
|---|---|---|
| `studio_status` | A1 | Song name and path, tempo, beats per bar, length, loop, sample rate, mode (studio/headless), transport, counts, unsaved changes, what undo would undo. The first call of every session |
| `guide` | A1 | Returns a guide by topic (`workflow`, `concepts`, `composition`, `sound-design/neo-poly`, `mixing`, `mastering`, …). Same content as the resources and prompts (§6), for clients that only use tools |
| `undo`, `redo` | A1 | One agent call at a time; returns what was undone or redone |

### 5.2 Song and transport

| Tool | Phase | Purpose |
|---|---|---|
| `song_new` | A1 | Empty song with a master; tempo and beats per bar optional. Destructive when there are unsaved changes |
| `song_open`, `song_save` | A1 | `.bmx`/`.bmxml`; save as when given a path |
| `song_set` | A1 | Tempo, ticks per beat, beats per bar, loop range, song end |
| `transport` | A7 | Play, stop, seek, loop: studio mode only (live listening for the user). Headless mode answers with a pointer to `render` |

### 5.3 Machines and routing

| Tool | Phase | Purpose |
|---|---|---|
| `gear_list` | A1 | Available machines: name, generator/effect, category, a one-line description, manifest quality (generated, curated, profiled draft), health, preset count. Filter by kind, category or text |
| `machine_list` | A1 | The song's machines: gear, inputs and outputs with gain dB and pan, mute/solo/bypass, tracks, latency |
| `machine_add` | A1 | Adds a machine from the gear list; optional name, track count, preset, and where to connect it (`"Master"`, or between two machines). Returns its name and its parameter summary |
| `machine_remove` | A1 | Deletes machines; reconnects the chain around effects when asked |
| `machine_set` | A1 | Rename, mute, solo, bypass, track count |
| `connect`, `disconnect` | A1 | Audio connections with gain dB and pan; `input: "sidechain"` for machines with a second input (Neo Comp) |
| `connection_set` | A1 | Gain and pan of existing connections, in a batch |
| `machine_replace` | A4 | Swaps a machine for another (a classic for its Neo successor, §12.5), keeping connections and patterns. Maps parameters through manifest roles and units, then renders before and after and reports the difference |

### 5.4 Sound design

| Tool | Phase | Purpose |
|---|---|---|
| `machine_describe` | A1 | Everything about one machine:<br>• parameters by group, each with name, current value (text, number and unit), range, default and description<br>• track parameters<br>• the manual link (Neo)<br>• presets and commands |
| `param_set` | A1 | Many changes in one call, global or per track. Each change is `value` (in units, §3.3), `by` (relative, in units) or `times` (a factor). Returns the values as the machine now describes them |
| `param_get` | A1 | Current values (a subset, or all) |
| `preset_list`, `preset_load`, `preset_save` | A2 | ReBuzz presets (the factory `.prs.xml` files and the user's own) |
| `snapshot_save`, `snapshot_restore`, `snapshot_diff` | A2 | Named whole-song parameter snapshots (all machines, connections, mutes) for A/B comparison of mixes and sounds |
| `machine_data_import` | A3 | Machine-specific data: a wavetable WAV for Neo Wave, a DX7 bank for Neo FM6, a sample for a sampler. Typed per machine through the Neo manifest; never through dialog commands |

### 5.5 Composition

| Tool | Phase | Purpose |
|---|---|---|
| `pattern_list` | A1 | Patterns per machine: name, length in beats, note count, where it is used (arrangement, session) |
| `pattern_create`, `pattern_delete`, `pattern_rename`, `pattern_duplicate` | A1 | Length in beats |
| `notes_write` | A1 | Notes into a pattern:<br>• each note is pitch, start beat, length in beats, velocity 1–127<br>• `mode: add \| replace \| replace_range`<br>• chords are notes with the same start<br>• returns a short summary and clashes (overlaps on a monophonic track, notes outside the pattern) |
| `notes_read` | A1 | The same shape back, optionally for a range |
| `notes_transform` | A3 | Transpose (semitones or in-scale steps), quantize (grid, strength, swing), humanize, velocity curve, shift, stretch, reverse, invert, legato. Each runs on a selection by range or pitch |
| `automation_write`, `automation_read` | A3 | A parameter's curve in a pattern: points at beats, step or linear, values in units. Written as engine-played automation columns |
| `arrangement_get` | A1 | The timeline: one lane per machine with its pattern placements in bars and beats, mutes, markers, loop, song end |
| `arrangement_place` | A1 | Puts patterns at bars (one, a list, or repeated every N bars); `arrangement_clear` removes a range |
| `arrangement_markers` | A3 | Named sections (intro, verse…), used by renders and guides |
| `session_get`, `session_clip_set`, `session_scene_set`, `session_launch` | A3/A7 | The Session View grid: clips, scenes, follow actions, launching (live in studio mode) |
| `session_record` | A7 | Takes in the Session View: arm a track, record a take of what is played on its keyboard (bars long or until stopped), stop it, capture what armed tracks were played lately (studio mode) |
| `midi_play` | A7 | Plays a phrase into a machine live, on the next beat or bar, as a keyboard player would; an armed track's take records it (studio mode) |
| `theory` | A3 | Music theory without arithmetic slips:<br>• chord symbols → pitches (`"Am9/E"`), voicings and inversions, scale degrees<br>• key and scale notes; roman-numeral progressions → chords in a key<br>• a melody's fit to a key<br>Read-only; nothing in the song changes |

### 5.6 Samples

| Tool | Phase | Purpose |
|---|---|---|
| `wave_list` | A2 | The wavetable: slots, names, lengths, root notes, loops |
| `wave_import` | A2 | A file into a slot (WAV/AIFF/FLAC…) |
| `wave_from_render` | A3 | A render (or part of one) into a slot: resampling, bouncing a synth to audio |

### 5.7 Ears: render and analysis

| Tool | Phase | Purpose |
|---|---|---|
| `render` | A1 | Renders faster than real time:<br>• **range:** beats/bars, a marker, the loop, a pattern played alone, or seconds<br>• **mix overrides:** solo, mute and bypass lists that do not touch the song<br>• **probes:** any machines' outputs, captured alongside the master<br>Returns a render id, WAV resource links, duration, and a loudness and peak summary. A task when long |
| `analyze` | A1/A2 | Measures a render, a probe or a file, by metric:<br>• `loudness`: integrated, short-term max, momentary max LUFS, loudness range<br>• `peaks`: true peak dBTP, sample peak, clipping count<br>• `dynamics`: crest factor, PLR, RMS over time<br>• `spectrum`: per third-octave and per band (sub, low, low-mid, mid, high-mid, presence, air), centroid, tilt<br>• `stereo`: correlation, width per band, mono compatibility<br>• `pitch`: fundamental and cents over time (monophonic)<br>• `key`; `tempo` and onsets<br>Numbers come with plain-language reads ("tilt −4.2 dB/oct: dark") |
| `analyze_mix` | A2 | One render with a probe on every machine feeding the mix:<br>• per track: loudness, peak, spectrum bands, crest<br>• a masking table: which pairs fight in which bands, how much<br>• headroom and the master's measures<br>• a short list of the biggest problems |
| `compare` | A2 | Two sources (two renders, a render and a reference file, before/after snapshots): differences per band, loudness, dynamics and stereo |
| `plot` | A2 | An image the agent looks at: spectrum, spectrogram, waveform, loudness over time, stereo field (goniometer), or per-track bands. Returned as image content |
| `notes_heard` | A5 | Transcribes a monophonic render (pitch and onsets) so the agent can check that what plays is what it wrote |

### 5.8 Live readings (studio mode)

| Tool | Phase | Purpose |
|---|---|---|
| `meters_read` | A7 | Live peak and loudness of the master and chosen machines while the user plays the song, over a window (§3.5: subscriptions not built) |
| `machine_readings` | A7 | Values an analysis machine shows (Neo Analyzer: spectrum, LUFS, correlation), from wherever the user put it |

## 6. Guides: prompts, resources and the `guide` tool

The craft goes to the agent three ways, with one source:

- **Resources** `rebuzz://guides/<topic>` (markdown) and `rebuzz://machines/<gear>/manual`.
- **Prompts** (slash commands in Claude Code), with arguments:
  - `/compose` (genre, mood, tempo, length)
  - `/design-sound` (machine, description)
  - `/mix` (goals)
  - `/master` (target platform)
  - `/critique` (scope)
  Each expands to the relevant guides plus a step plan built from the tools.
- **The `guide` tool**, for clients that only use tools.

| Guide | Contents |
|---|---|
| `workflow` | The loop: plan → build → render → measure → adjust. Which tools for what. Naming. Undo. Always check with `analyze` before claiming a result |
| `concepts` | Buzz in five minutes: generators, effects, the machine graph, tracks, patterns vs the arrangement vs the session grid, ticks and beats, note naming (C4 vs Buzz's C-5) |
| `composition` | Forms and section lengths by genre. Harmony (functional progressions, voice leading, tension). Melody (range, contour, motif development). Rhythm and groove (drum archetypes per genre, swing, humanizing). Arrangement energy (adding and removing layers per section). Templates with tempo, key habits and structure per genre |
| `sound-design` | How to read a sound (envelope, spectrum, movement, space). Recipes per Neo machine with exact parameter values (pads, basses, leads, plucks, keys, bells, textures). Effects for sound design (drive, reverb, shimmer) |
| `mixing` | Gain staging targets (track peaks around −10 to −6 dBFS, master headroom ≥ 6 dB before mastering). Balance by reference. Frequency slotting with per-instrument starting ranges. EQ and compression starting points by source, with numbers. Sidechain. Space (sends, pre-delay vs tempo). Stereo (mono lows below ~120 Hz). The checks to run with `analyze_mix` |
| `mastering` | Targets: streaming −14 LUFS integrated and true peak ≤ −1 dBTP; club and loud genres −9 to −7 LUFS. The chain (corrective EQ → glue compression → saturation → limiter). Measure after every change. What not to do |
| `troubleshooting` | Silence, clipping, phase, latency, CPU, missing machines |
| Machine manuals | Generated from each Neo machine's Params.def (every parameter with unit, range and meaning) plus a curated "how it sounds" section and its presets |

Guide numbers are starting points the agent verifies with measurements, never rules it applies blindly. Every guide ends with a checklist.

## 7. Analysis library

`Machines/NeoBuzz.Analysis` is pure C#, tested on Linux like the DSP library. The server and the analysis machines (§8) share it. All of it is built (loudness and true peak in A1, the rest in A2); the last column is what `ReBuzz.AgentTests` measures, with the tolerance it asserts in brackets.

| Measure | Method | Verified against | Built: measured (asserted) |
|---|---|---|---|
| Loudness | ITU-R BS.1770-5: K-weighting, 400 ms momentary, 3 s short-term, gated integrated, EBU R128 loudness range | EBU Tech 3341/3342 test signals, generated in the tests | A1. Integrated, momentary, short-term (±0.1 LU); loudness range (±1 LU) |
| True peak | 4× oversampled peak (BS.1770 Annex 2) | Tech 3341 true-peak cases | A1. Inter-sample peak of a quarter-rate sine at 45° (±0.2 dB); a 1 kHz sine (±0.05 dB) |
| Spectrum | Welch-averaged FFT (Hann, 50% overlap, 32768 points at 44.1/48 kHz), scaled so the bins sum to the mean square. Levels are plain RMS dB: a full-scale sine reads −3 dB, and bands add up to the total. ISO third octaves 20 Hz – 20 kHz and the named bands (sub 20–60, low 60–250, low-mid 250–500, mid 500–2k, high-mid 2–4k, presence 4–6k, air 6–20k Hz). Centroid and 85% roll-off are amplitude-weighted; flatness is on power. Tilt is the least-squares slope of the third octaves, 50 Hz – 16 kHz, in dB per octave (pink 0, white +3) | Pink noise reads flat in third-octave bands within ±0.5 dB; sine bins | A2. Periodic pink noise reads flat in all 31 third octaves within ±0.013 dB (±0.5); tilt 0.000 (±0.05); white noise +3.010 dB/oct (±0.05). Random pink noise (4 s) reads flat 100 Hz – 10 kHz within ±0.45 dB (±1.0). Sines at 97 Hz, 1 kHz and 12.3 kHz: right peak bin, own band within 0.001 dB (±0.05), bands two away > 40 dB down, centroid within 0.1 Hz |
| Dynamics | Crest factor (louder channel), PLR (true peak − integrated), RMS envelope (50 ms), transient density (onsets per second) | Known signals | A2. Sine crest 3.010 dB (±0.02); square 0 (±0.01); PLR of a 1 kHz stereo sine −0.007 (±0.2); a 20 dB level step spreads the envelope 20 dB (±0.5); 16 of 16 clicks counted |
| Stereo | Correlation (overall and per 200 ms), width as side/mid energy (overall and per named band, from the Welch cross spectrum), mono fold-down loss of (L+R)/2 | Mono, anti-phase and decorrelated noise | A2. Mono, level-offset and anti-phase cases exact (1e-6). Unrelated noise: correlation 0.005 (±0.02), width 0.99 (±0.05), mono loss −2.99 dB (±0.1), per band width 0.95–1.02 (±0.3). An inverted sub under centred mids: sub correlation < −0.99, mid width < 0.001 |
| Masking | Bark bands, 85 ms frames. A band fights in a frame when both tracks are at −50 dB or above, within 24 dB of their own loudest band, and within 6 dB of each other. Severity is the sum over fighting frames of 1 − (Δ/6 dB)², divided by the frames where the less active track plays in that band. Pairs → bands → severity, for N tracks (`Masking.Table`) | Constructed overlaps | A2. A kick and a 55 Hz bass fight at 20–100 Hz, severity 0.43 (> 0.35), and nothing else does. 20 dB apart, or one after the other: none. Half the overlap gives half the severity (±0.15) |
| Pitch | YIN on audio decimated to about 6× the highest pitch, a Hann-weighted difference function, threshold 0.15, the dip refined by Newton steps on its band-limited Fourier form. Confidence 1 − d′, cents from equal temperament, notes split at gaps, at pitch jumps over 0.6 semitones and at onsets | Synth notes from Neo Poly | A2. Band-limited sawtooth notes A1–A6 (55–1760 Hz), detuned −7 to +21 cents: each note's cents exact to 0.01 (±1), worst frame 2.0 cents (< 5). A melody with a repeated note splits into its notes. Neo Poly renders themselves are left to the ReBuzz-side tests |
| Key | Chroma from the Welch spectrum's peaks, 55 Hz – 5 kHz, leaving out overtones (peaks at a whole multiple of a stronger, lower peak); Krumhansl–Kessler profiles in all 24 keys; best key, correlation, runner-up | Rendered scales and progressions | A2. C major scale (r 0.71), A natural minor scale and triad (0.89), F# I–IV–V–I (0.89), E minor i–iv–V–i (0.85), Bb I–vi–ii–V–I (0.84), a lone C triad (0.83): all found |
| Tempo and onsets | The Session View's spectral flux (`Core/Clips/AudioAnalysis.cs`), ported to sample arrays (that file is unchanged): onsets as flux peaks over a running median. Tempo from the flux autocorrelation over 60–200 BPM (beat to bar multiples, a mild prior around 120), then a least-squares beat grid fitted to the onsets on it | Its existing tests | A2. Click tracks at 90, 120 and 128 BPM within 0.001 BPM (±0.05), every onset within 5 ms; 124 BPM with off-beat hats within 0.001 (±0.1) |

Plots (spectrum, spectrogram, waveform, loudness, goniometer, per-track bands) are drawn to PNG by the server with the same code in headless and studio mode (`ReBuzz.Agent/Audio/Plot*.cs`). They use a small rasterizer, a built-in 5×7 font and a PNG encoder on `ZLibStream`, with no System.Drawing. The tests decode every plot: the signature, each chunk's CRC, and IDAT inflating to height × (1 + 3 × width) bytes.

## 8. Machines for agentic work

### 8.1 Analysis: probes first, machines second

An agent's ears should not need machines in the song. Probes capture any machine's output during a render, without changing the graph, and the analysis library measures them. This is non-invasive and exact, and it works headless.

Analysis machines still earn their place:

- for the user watching in studio mode;
- for live sessions, where the agent reads what the machine measures;
- because building them on the same library keeps the numbers identical.

| Machine | What | Priority |
|---|---|---|
| **Neo Analyzer** | Spectrum (third-octave and FFT with peak hold), LUFS momentary/short-term/integrated, loudness range, true peak, correlation and width. Readings exposed to `machine_readings` | High |
| **Neo Scope** | Oscilloscope (tempo-synced) and goniometer/vectorscope. Mostly for people; the agent's equivalent is `plot` | Medium |

### 8.2 Machines the agent needs to mix and master

Today an agent could compress and saturate with Neo Comp and Neo Drive, but it has no precise EQ, no true-peak limiter and no simple utility stage. Those are the moves every mix and master is built from:

| Machine | What | Why the agent needs it | Priority |
|---|---|---|---|
| **Neo EQ** | 8-band parametric: bell, shelves, cuts at 12–48 dB/oct, Q. Optional dynamic bands. Minimum or linear phase. Spectrum overlay | Precise, labelled moves ("−3 dB at 320 Hz, Q 1.4"). The existing EQs have coarse or unlabelled controls | High |
| **Neo Limiter** | True-peak lookahead limiter: ceiling in dBTP, release (auto), gain into it. Optional target-LUFS mode that sets the gain from a measurement | Mastering to a loudness target without clipping | High |
| **Neo Utility** | Gain dB, pan, width, mono below a frequency, phase invert, channel swap, DC removal | Gain staging and stereo fixes as one-parameter moves | High |
| **Neo Delay** | Tempo-synced stereo/ping-pong delay with filters, saturation and ducking | Space and depth in mixes; sound design | Medium |
| **Neo Multiband** | 3–4 band compressor/expander with linear-phase crossovers | Mastering and bus control | Medium |
| **Neo Sampler** | Plays waves: multi-sample zones, velocity layers, ADSR, filter, choke groups; drum-kit mode | Drums and sampled sounds with clear units, driven by `wave_import` | Medium |

### 8.3 Machine manifests

Every Neo machine already declares its parameters once (Params.def) with units, mappings and descriptions. The build writes them out as a **manifest**, a sidecar file next to the DLL: `Neo Poly.machine.json`. It holds:

- every parameter: id, name, group, unit, kind (linear, exponential, dB, time, frequency, enum…), range and mapping, choices, description;
- the machine's data imports (a wavetable WAV, a DX7 bank);
- its readings (Neo Analyzer).

With it, `param_set("Bass", [{"F1 cutoff": "800 Hz"}])` lands on the exact raw value. `machine_describe` speaks in units, and the manual is generated.

The same sidecar format describes **every other machine** too, profiled and curated rather than compiled (§12). The server never needs a machine's code to understand it.

## 9. Phases

| Phase | Delivers | Done when |
|---|---|---|
| **A0 Foundation** | `ReBuzz.Agent` assembly; studio mode (Streamable HTTP, token, settings page) and headless mode (`--agent`, stdio); the engine queue; undo groups; non-modal errors; `studio_status`; the agent log | Claude Code connects in both modes; a call that fails in the engine returns an error instead of a dialog |
| **A1 Make and hear** | Song, machines, routing, parameters with units (manifest for Neo, description search for others), patterns, `notes_write`/`read`, arrangement, `render`, loudness and peak `analyze`, guides `workflow` and `concepts` | Scripted: build a 4-bar beat with bass and chords from nothing, render it, read back notes and loudness; every step undoable in studio mode |
| **A2 Real ears** | The analysis library (§7); `analyze` with every metric; `analyze_mix`, `compare`, `plot`, probes; snapshots; presets; waves | Analysis passes its reference tests; `analyze_mix` finds a planted kick/bass masking problem and a planted phase problem |
| **A3 Full composition** | Automation, note transforms, markers, session grid, `theory`, machine data imports, `wave_from_render`; the composition and sound-design guides | Scripted: a verse/chorus song with automation and a session view; `theory` checked against a chord dictionary |
| **A4 Mix and master machines** | Neo EQ, Neo Limiter, Neo Utility, Neo Analyzer (each to the machines spec's standard: measured tests, editor, presets); the mixing and mastering guides | A master hits −14 LUFS ±0.5 and ≤ −1 dBTP from a scripted mix; the analyzer's readings match the library's |
| **A5 Guides complete** | Every guide and manual written and checked, prompts, `notes_heard`, critique flow | Every guide's checklist maps to tools that exist |
| **A6 Evaluation** | The task suite (§10) in CI with a scripted MCP client against headless ReBuzz, plus periodic runs with a real agent | Scores tracked per change; regressions block |
| **A7 Live** | Transport, live meters (subscriptions), session launching, MIDI, recording takes | An agent can jam: launch scenes in time and react to live levels |

## 10. Evaluation

The server is only as good as what agents achieve with it. There are two layers.

**Scripted (CI, every change).** A client written in C# against headless ReBuzz runs fixed tool sequences and asserts results:

- notes round-trip exactly;
- `param_set "800 Hz"` reads back `800 Hz`;
- renders are bit-identical across runs;
- `analyze` meets reference values;
- undo restores the song exactly.

**Agent tasks (periodic, scored).** Real agents get briefs and we score the outcome with the analysis library, plus a human listen for a sample:

- *Beat:* "4 bars of 124 BPM house: kick, clap, hats, bass" → it renders; there are onsets on the beats; the bass is in key; no clipping.
- *Sound match:* "make a sound like this" (a rendered reference) → spectral and envelope distance under a threshold.
- *Fix the mix:* a song with planted problems (masking, a harsh resonance, a too-loud hi-hat, phase) → each one measurably fixed, nothing else made worse.
- *Master:* "master for streaming" → −14 ±1 LUFS, ≤ −1 dBTP, crest factor within the genre's range.
- *Song:* "2-minute synthwave at 92 BPM" → structure with sections, key consistency, loudness target, no silence gaps; then a human rating.

Scores per task and tool-call counts (fewer wrong calls means clearer tools) are tracked over time. Tools whose errors agents hit often get rewritten.

## 11. Example

A shortened session as the agent would run it:

```
studio_status                          → empty song, 120 BPM, 4/4, headless
guide("workflow")
song_set(tempo=92)
machine_add("Neo Poly", name="Pad", preset="Pad — Warm Strings", connect_to="Master")
machine_add("Neo Verb", name="Space", between=["Pad","Master"], preset="Hall — Concert")
theory(progression="i VI III VII", key="A minor", voicing="close", octave=3)
                                       → Am: [57,60,64] F: [53,57,60] C: [55,60,64] G: [55,59,62]
pattern_create("Pad", "Verse", length_beats=16)
notes_write("Pad", "Verse", notes=[{pitch:57,start:0,length:4,velocity:90}, …], mode="replace")
arrangement_place("Pad", "Verse", bars=[1,5,9,13])
render(range={bars:[1,17]})            → render r1, −19.8 LUFS, −7.1 dBTP
analyze(r1, metrics=["spectrum","stereo"])
                                       → tilt −5.1 dB/oct (dark); width 0.92; build-up 250–400 Hz
param_set("Pad", [{"F1 cutoff":"+40%"}, {"F2 type":"HP"}, {"F2 cutoff":"180 Hz"}])
render(range={bars:[1,17]}) … compare(r1, r2)
```

## 12. Upgrading the existing machines

The package ships 101 machines: 82 from upstream ReBuzz (prebuilt DLLs in `RunTime/Gear`, plus ReBuzz's own) and 19 Pedal machines built from source. By code, 46 are managed (C#) and 55 native (C++). An agent can drive all of them through `IParameter` today, but most are hard to use well:

- Values are raw integers whose meaning lives in a description string.
- Note pitch conventions differ between machines (some play `A-4` at 440 Hz, others `A-5`).
- Parameter names are terse.
- Nothing says what a machine is for.

Their editors are the generic parameter list.

There is a path for every one of them, in four tiers. The tiers cost more as they go, and each helps on its own.

### 12.1 What we can touch

| Group | Examples | Source | What's possible |
|---|---|---|---|
| Neo machines | Neo Drive, Comp, Verb, Poly, Wave, FM6 | ours | Everything; manifests from Params.def |
| ReBuzz's own machines | Session Clip Player, Audio In/Out, Groove, Modern Pianoroll, QuickNote, Envelope/Audio Block, MIDI Output, Connection Mixer | this repo (C#) | Tiers 1–3 |
| Pedal machines (19) | Juno106, SH-101, FM, M1, Drum Matrix, Comp, MComp, Plate, EQ, Filter, Gate, Shaper, Chorus… | `ExternalMachines/source/thepedal` (C#) | Tiers 1–3. Most have no licence file: changes are fine for private builds; redistribution needs the authors' consent (GPL-3.0 for Juno106, SH-101 and Drum Matrix) |
| Classic natives with sources in Buzztrax | FSM Infector, KickXP, PhatMan, PanzerDelay, Chorus2, WahMan; Jeskola Delay, Cross Delay, Distortion, NiNjA dElaY, Trilok, Noise; Oomek Aggressor, Exciter, Masterizer | `ExternalMachines/source/buzztrax-buzzmachines` (C++, per-author licences) | Tiers 1–2 now. Rebuilding them needs the known MSVC header fix first |
| Closed binaries | Jeskola Bass 2/3, Organ, Tracker, Reverb, Raverb, Stereo Reverb, Filter/2, EQ-3, Flanger, Chorus, Wave Shaper; Joachims Jupiter, Saturn, Neptune, Mars, Hyperion, Chopper; Polac VST hosts; Matilde Tracker2; Rout 909… | none | Tiers 1 and 4 only |

### 12.2 Tier 1: describe every machine (no code changes)

A **profiler** loads each machine headlessly, then measures it. It produces a draft manifest, which a person curates and commits to the repo. The package ships it beside the DLL.

The profiler, per machine:

- **Parameters.** Lists every parameter and reads `DescribeValue` across its range: every value for byte parameters, a dense grid for word parameters. From that it infers:
  - the unit (Hz, dB, ms, %, semitones, note, choice) from the text;
  - the mapping (linear, exponential, dB, table) by fitting the parsed numbers;
  - whether the parameter is a switch or a choice list.
- **Audio, effects.** Renders impulse, sine sweep, noise and level-step tests. Reads off:
  - what kind of effect it is (filter, EQ, delay, reverb, distortion, dynamics, modulation, utility);
  - its latency, tail length, gain at default, and whether it is linear;
  - for each parameter, what moving it changes (cutoff moves the response's corner; time moves the echo), which suggests roles.
- **Audio, generators.** Plays notes on several tracks and reads off:
  - the pitch convention (which note plays 440 Hz: the manifest records a pitch offset);
  - whether it is pitched, polyphonic or drum-like;
  - velocity response, envelope length, and level at default.
- **Health.** Crashes, NaN or infinite output, denormal slowdowns, CPU per voice or instance, silence handling (does `Work` report silence), behaviour at parameter extremes.

**Curated manifests** add a summary ("what it's for"), plain-English parameter names and descriptions, roles (`filter.cutoff`, `env.release`, `mix.wet`, `drive`), safe ranges, and quirks ("track 0 only", "needs a wave", "stereo in, mono out").

The `machine-health.md` report sits next to the inventory. Machines that crash or emit NaN go on a list the server won't offer to agents unless asked.

Result: every machine becomes usable in real units through `param_set`. `notes_write` plays the written pitch on every generator, because the server applies each machine's pitch offset. `gear_list` can say what everything is for. Curation starts with the 25 most useful machines; the rest ship with their profiled draft.

### 12.3 Tier 2: a better face for every machine (host-side)

- **Manifest editor.** A Neo-style editor generated from any machine's manifest (§8.3): groups, knobs with units, choice buttons, ADSR pictures for envelope roles, a response plot for filters and EQs. It is built with the editor kit. Machines without their own GUI can open it instead of the parameter list (a per-machine setting, on by default for curated manifests). Their design improves without touching their code.
- **Presets for everything.** Curated factory presets for the top machines in `.prs.xml` beside them, with names that say what they do. The agent can start from a sound, not from zero.
- **Song-safe.** None of this changes a machine's DLL, parameters or saved data, so every existing song plays exactly as before.

### 12.4 Tier 3: fix and modernise machines we have the source of

For the C# machines (ReBuzz's own and the Pedal set), each gets:

- **Manifests from code.** A Params.def (or an equivalent attribute table) generates the manifest, so it can never drift from the code.
- **Bug and quality fixes.** Remove the multi-track note workarounds now that the core delivers chords whole (machines spec §3.1). Add denormal guards, smoothing where parameters zipper, and silence reporting.
- **Editors** on the shared kit, where the manifest editor isn't enough (Drum Matrix, MComp, Juno106).
- **Tests.** The same measurement suite as the Neo machines: aliasing, levels, NaN and CPU.

Parameter order, ranges and saved state stay as they are: songs and presets keep working. Changes that would break them become a new machine instead (tier 4).

For the native machines with Buzztrax sources, the first step is the build fix (current MSVC headers). Then the same treatment minus the editors: fixes, x64 builds of the ones upstream lacks, and generated manifests.

### 12.5 Tier 4: successors, with migration

Where a machine's design limits its quality (2000s-era reverbs and delays, unlabelled EQs, aliasing distortions, fixed-voice synths), the answer is a Neo successor. Originals stay installed so old songs play. A replacement tool moves a song over: `machine_replace(name, gear)` uses ReBuzz's `ReplaceMachine` with a mapping built from manifest roles:

1. it maps cutoff to cutoff and wet to mix;
2. it converts values through units, not raw numbers;
3. it renders before and after with the analysis library, and reports the difference.

The tool works for agents and, from the machine menu, for people.

| Classic machines | Successor | Status |
|---|---|---|
| Jeskola Reverb, Stereo Reverb, Raverb; Pedal Plate | Neo Verb | built (machines spec M1) |
| Jeskola Distortion, Wave Shaper; FSM Philthy | Neo Drive | built |
| Pedal Comp; Jeskola Limiter (as a compressor) | Neo Comp | built |
| Jeskola EQ-3, Joachims Enceladus, Pedal EQ | Neo EQ | next batch (§8.2) |
| Jeskola Limiter, Oomek Masterizer | Neo Limiter | next batch |
| Jeskola Gain, Pedal Gain | Neo Utility | next batch |
| Jeskola Delay, Cross Delay, NiNjA dElaY; FSM PanzerDelay | Neo Delay | next batch |
| Jeskola Chorus, Flanger; FSM Chorus2; Pedal Chorus | Neo Ensemble (chorus/flanger/phaser) | proposed |
| Jeskola Filter, Filter 2; Joachims Mars; FSM WahMan; Pedal Filter | Neo Filter (the Neo Poly filters as an effect, with envelope follower and LFO) | proposed |
| Jeskola Bass 2/3, Oomek Aggressor 303, Pedal SH-101 | Neo Poly (mono/legato presets) | built (M2) |
| FSM Infector, Pedal Juno106 | Neo Poly, Neo Wave | built (M2) |
| Jeskola Organ | Neo Wave (organ table), or a dedicated tonewheel organ | proposed |
| Pedal FM | Neo FM6 | in progress (M2) |
| Rout 909, FSM KickXP, Pedal Drum Matrix | Neo Drums (synthesised kit with per-voice outputs) | proposed |
| Jeskola Tracker, Matilde Tracker2, Pedal M1 | Neo Sampler | next batch (§8.2) |

### 12.6 How the tiers fit the phases

| Phase (§9) | Machine upgrade work |
|---|---|
| A1 | Pitch offsets and hand-written manifests for the 10 machines agents reach for first (Neo machines generated; Juno106, SH-101, Drum Matrix, Infector, Jeskola Delay, Pedal EQ…) |
| A2 | The profiler and health report over all 101; draft manifests for everything |
| A3 | Curated manifests for the top 25; the manifest editor; factory presets for the top machines |
| A4 | Neo EQ, Limiter, Utility (successors); `machine_replace` with role mapping and render comparison |
| A5+ | Tier 3 for the Pedal and ReBuzz machines; the Buzztrax build fix; Neo Delay, Ensemble, Filter, Drums, Sampler as the next machine batches |

**Done when** every machine in the package has a manifest; `notes_write` plays the written pitch on every pitched generator; the health report is clean or every exception is listed; and an old song converted with `machine_replace` measures within a stated tolerance of the original, or the report says why not.

## 13. Inventory notes (for implementers)

- **Undoable graph operations:** `IMachineGraph` (`I/IMachineGraph.cs`). Connection gain is unity at 0x4000; pan runs 0…0x8000 with 0x4000 the centre (`Core/Session/SessionMixer.cs`). `SetConnectionParameter` is not undoable today.
- **Parameters:** `IParameter.SetValue(track | 1<<16, v)` sets a value without recording it into patterns (`Core/ParameterCore.cs:303`).
- **Notes:** Modern Pattern Editor data:
  - `IPattern.PatternEditorMachineMIDIEvents` rewrites the note lanes;
  - `MachineManager.Export/ImportClipNotes` exchanges (time, message, duration) triples;
  - the Session View's `ClipNotes` covers drawing, quantizing and moving.
- **Automation the engine plays:** parameter columns tagged `SessionAutomation` (`Audio/WorkManager.cs:691`).
- **Undoable pattern and sequence actions:** `BuzzGUI.Common/Actions` (`SetOrClearEventsAction`, `CreatePatternAction`, `SetEventAction`, `SetMarkerAction`…), applied through `song.Do`.
- **Offline render:** `WorkManager.MainAudioFillBuffer` under `ReBuzzCore.AudioLock` gives interleaved stereo at ±32768. The test Driver shows the headless setup (`ReBuzzTests/Automation/Driver.cs`). `MasterTap` is raised on the UI thread via the dispatcher, and the multi-channel tap needs an audio provider, so renders capture inside the render loop instead.
- **Headless start:** `ReBuzzCore` is constructed with injected dispatcher, registry, file choices and user messages (`AppViews/ReBuzzCoreInitialization.cs`, steps 1–6); internals are visible to `ReBuzzTests`, and would be to `ReBuzz.Agent`.
- **Presets:** `MachineExtensions.GetPresetNames`/`GetPreset`/`SetPreset` and `Preset.Apply` (they show a `MessageBox` on errors today).
