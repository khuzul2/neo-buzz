# ReBuzz MCP server — Specification

| | |
|---|---|
| Status | A0–A7 built, with Neo Analyzer (2026-11-02); U1 of the machine upgrades (2026-11-03). As built: §3.5, §10.1 (A6), §12.8 (U1) |
| Date | 2026-10-31, updated 2026-11-03 |
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
Claude Code ──(stdio)──────────► NeoBuzzLive.exe --agent       headless: no window, no audio device
Claude Code ──(Streamable HTTP)─► NeoBuzzLive.exe (window open) http://127.0.0.1:<port>/mcp, token

NeoBuzz Live process (ReBuzz.dll; rebuzz-headless and the studio host the same engine)
  ReBuzz.Agent (new assembly, pure C#: builds and tests on Linux)
    Mcp              JSON-RPC 2.0, schemas and validation, tools, resources, prompts, progress; stdio and HTTP
    EngineQueue      every call runs on the engine/UI thread, one at a time, with a timeout
    Model            stable names, units, time in beats, notes as MIDI numbers
    Render           offline renders (WorkManager), probes on any machine's output
    Analysis         NeoBuzz.Analysis: loudness, spectrum, dynamics, stereo, masking, pitch, key
    Guides           markdown guides and generated machine manuals
```

### 3.1 Two modes, one tool set

The server announces itself as `neobuzz-live`, titled "NeoBuzz Live" (`GuideLibrary.AppServer`, from `ReBuzz.Product`), so Claude Code lists its tools as `mcp__neobuzz-live__<tool>`; the tools' own names don't carry the app's name.

- **Studio (window open).** `Settings → Agent → Allow agents` starts Streamable HTTP on `127.0.0.1`.
  - The port and a bearer token are shown with a ready-to-paste `claude mcp add --transport http neobuzz-live …` line (`ClaudeCode.HttpCommand`).
  - The `Origin` header is validated and only localhost is served.
  - The user sees every change as it happens; an "Agent" badge shows when a call is running.
  - Every agent edit is one undo step labelled `Agent: <what>`.
  - The Avalonia studio (macOS; studio-mvp.md S8) serves the same: Preferences ▸ Agent (Command+Shift+A) with the port, the token and the `claude mcp add` line; "studio" mode, so the live tools play through its audio device.
- **Headless.** `NeoBuzzLive.exe --agent [song.bmxml]` serves MCP on stdio (`claude mcp add neobuzz-live -- "<path>\NeoBuzzLive.exe" --agent`; on macOS and Linux `rebuzz-headless --agent`).
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
| Pitch | MIDI number or scientific name: `60` = `"C4"`, `69` = `"A4"` = 440 Hz | The apps name notes as Preferences ▸ General ▸ Note names says (ux-overhaul.md §3.5): C3 = 60 by default (the pattern editor's `C-3`), scientific, or Buzz's (`C-5`, `A-5`); tools always say which they mean |
| Levels | dB everywhere (connection gain, faders, meters); pan −1 … +1 | Raw Buzz amp/pan values are converted |
| Parameter values | in the parameter's own unit: `"800 Hz"`, `"-6 dB"`, `"35%"`, `"Saw"`, `"1/8"`, or relative `"+3 dB"`, `"x2"`; `raw:512` as the escape hatch | Neo machines publish exact units and mappings (§8.3); legacy machines are resolved by searching their value descriptions. A whole-number parameter whose unit is a note (Neo Sampler's keys, roots and kit base) takes and prints a pitch name (`"C2"`, `"F#3"`) as well as its number, and one whose unit is a wave takes its own text (`"Built-in Kick"`, a slot's name, `"Off"`); its own value texts come first (`"Wave"` for a root that follows the wavetable) |

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
- **Renders wait for machines that are still preparing.** A managed machine can expose `public bool Busy`, and renders wait up to 30 s while any reports it (with a warning if one still does). The wait runs no audio, so how long it takes changes nothing in the render: `Busy` must be current without a block being processed. Neo Wave reports it until all its built-in tables are built in the background (not just the ones chosen: the engine applies parameter changes, a preset's table, at its next tick, after the wait).
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
  - **Timing.** Phrases are timed by the session's clock as heard, the clock a take stamps. A phrase started on the bar lands on the bar in a take and follows tempo changes. The thread waits on each OS's own precise timer (`PreciseWaiter`: a high-resolution waitable timer on Windows, `mach_wait_until` on macOS, `clock_nanosleep` on Linux), as the MIDI clock output does. With the transport stopped, the wall clock times it from the tempo.
  - **Routing.** An armed machine gets the notes through the session's keyboard input (`SessionCore.OnMidiInput`), so it plays and records them. Any other machine gets them directly.
- **`meters_read`** fills `LiveMeter` windows (pure, in `ReBuzz.Agent/Audio`):
  - from the master's tap, on the UI thread;
  - from machines' first output connection, on the audio thread, through arrays allocated up front, so the audio thread neither allocates nor waits.

  It then measures each window with the analysis library, so live numbers mean what `analyze`'s do: momentary loudness now, short-term from a 3 s window, integrated over the window, true peak, RMS and clipped samples. The window is counted in audio frames, with a wall-clock limit for an output that isn't running.
- **Subscriptions are not built.** §5.8's subscribable meters resource was dropped for now: clients surface tools, not resource updates, to the model, and Streamable HTTP would need a GET stream for server-sent updates. A windowed `meters_read` covers "react to live levels".
- **`machine_readings`** reads a managed machine's `public IReadOnlyDictionary<string, double> Readings`, a snapshot it swaps in from the audio thread. Neo Analyzer is the first machine to have one; Neo Multiband the second (each band's gain change now and over the last 3 s, its level, the latency: machines.md, Neo Multiband); Neo Scope the third (peak, RMS and correlation over 3 s, and whether its trigger locks: machines.md, Neo Scope). In headless mode the values are what the last render played through it.
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
| `studio_status` | A1 | Song name and path, tempo, beats per bar, length, loop, sample rate, mode (studio/headless), transport, counts, unsaved changes, what undo would undo. The first call of every session. `safety` ([machine-safety.md](machine-safety.md) §3.6): whether the machine guard is on, the diverged machines (reason, time, song position), the safety limiter (on, ceiling, whether it acted since the clip indicator was cleared) and the clip indicator (threshold, latched over) |
| `guide` | A1 | Returns a guide by topic (`workflow`, `concepts`, `composition`, `sound-design/neo-poly`, `mixing`, `mastering`, …). Same content as the resources and prompts (§6), for clients that only use tools |
| `undo`, `redo` | A1 | One agent call at a time; returns what was undone or redone |

### 5.2 Song and transport

| Tool | Phase | Purpose |
|---|---|---|
| `song_new` | A1 | Empty song with a master; tempo and beats per bar optional. Destructive when there are unsaved changes |
| `song_open`, `song_save` | A1 | `.bmx`/`.bmxml`; save as when given a path |
| `song_set` | A1 | Tempo, ticks per beat, beats per bar, loop range, song end |
| `song_notes_read` | H3 | The song's notes, the studio's Song Notes tab ([machine-safety.md](machine-safety.md) §3.7): Markdown (**bold**, *italic*, `<u>`, headings, lists, links; a line break is a line break), or `format: "text"` for plain text. `imported` lists where text in them came from (Info machines, the song's info text), `imported_on_open` what opening this song brought in |
| `song_notes_write` | H3 | Writes them in Markdown: `mode: replace` (default) or `append` (below the notes, after an empty line). One undo step; saved with the song (the `SongNotes` subsection). At most 200 000 characters. The workflow guide says to append under a heading of the agent's own and to leave the person's notes alone |
| `transport` | A7 | Play, stop, seek, loop: studio mode only (live listening for the user). Headless mode answers with a pointer to `render` |

### 5.3 Machines and routing

| Tool | Phase | Purpose |
|---|---|---|
| `gear_list` | A1, U1, U2 | Available machines: name, generator/effect, type and a one-line description (from the manifest), manifest quality (generated, curated, profiled draft), health, preset count. Filter by kind or text. U2 (machine-safety.md §3.1): health says diverging (`diverging: true`), hot (a warning naming the setting) or `needs_window` ("works in ReBuzz on Windows with its window"); each parameter's `safe_ranges`. Order: exact units, drafts, the rest, machines that need a window, unhealthy last |
| `machine_list` | A1 | The song's machines: gear, inputs and outputs with gain dB and pan, mute/solo/bypass, tracks, latency; `diverged`, `diverged_at` and `diverged_reason` when the guard tripped (machine-safety.md §3.2) |
| `machine_add` | A1, U2 | Adds a machine from the gear list; optional name, track count, preset, and where to connect it (`"Master"`, or between two machines). Returns its name and its parameter summary. U2: a warning only for diverging gear, naming its safe ranges (not for hot machines, nor for other problems in a profile); gear that needs a window says so when it can't be built |
| `machine_remove` | A1 | Deletes machines; reconnects the chain around effects when asked |
| `machine_set` | A1 | Rename, mute, solo, bypass, track count. `reset: true` is Reset and unmute for a diverged machine (machine-safety.md §3.2); `mute: false` does the same |
| `connect`, `disconnect` | A1 | Audio connections with gain dB and pan; `input: "sidechain"` for machines with a second input (Neo Comp) |
| `connection_set` | A1 | Gain and pan of existing connections, in a batch |
| `machine_replace` | U5 | Moves a classic machine onto its Neo successor (§12.5; built: §12.10): the successor in its place (connections with their gain and pan, position, name, mute and bypass), every parameter converted through real units by the pair's successor mapping, its patterns' columns and notes moved (engine automation and the pattern editor's tracker columns, ramps kept; tracker notes as notes, slides as glides), the arrangement and the Session View following; the old machine removed or (`keep_old`) kept muted. Renders the song (the first 60 s, or `from`/`to`/`section`) before and after and compares the machine's output, read against its input, with the pair's stated tolerance (loudness, third octaves), saying why not when it misses. `dry_run` reports the mapping and the expected differences. One undo step. `gear_list` (`successor`) and `machine_describe` (`successors`) name a machine's successors |

### 5.4 Sound design

| Tool | Phase | Purpose |
|---|---|---|
| `machine_describe` | A1, U1 | Everything about one machine:<br>• parameters by group, each with name, current value (text, number and unit), range, default and description<br>• track parameters<br>• the manual link (Neo)<br>• presets and commands<br>• `gear_info` (U1): its DLL under Gear, track range, Note track parameter, whether it takes MIDI (managed machines), the manifest's note input and pitch offset<br>• `value_table=true` (U1): each parameter's value texts across its range, every value up to 256 values, else `table_points` (default 1025) evenly spaced, with its raw range and default. What the profiler infers units from; agents can read an unlabelled machine with it<br>• `safe_range` (U2, machine-safety.md §3.4): where a parameter was found to diverge, the range `param_set` keeps to (the machine's texts, raw values and why); the health with `diverging`, `hot` and `needs_window`<br>• `machine.diverged`, `diverged_at`, `diverged_reason` when its guard tripped (machine-safety.md §3.2) |
| `param_set` | A1, U2 | Many changes in one call, global or per track. Each change is `value` (in units, §3.3), `by` (relative, in units) or `times` (a factor). Returns the values as the machine now describes them; with a draft manifest (U1) it says so, and marks values whose unit came from a description as approximate. U2 (machine-safety.md §3.4): a value outside the parameter's `safe_range` refuses the whole call with the reason, unless `allow_unsafe: true`, which sets it and marks the change `unsafe` with why. People keep the full range in every UI |
| `param_get` | A1 | Current values (a subset, or all) |
| `preset_list`, `preset_load`, `preset_save` | A2 | ReBuzz presets (the factory `.prs.xml` files and the user's own) |
| `snapshot_save`, `snapshot_restore`, `snapshot_diff` | A2 | Named whole-song parameter snapshots (all machines, connections, mutes) for A/B comparison of mixes and sounds |
| `machine_data_import` | A3 | Machine-specific data: a wavetable WAV for Neo Wave, a DX7 bank for Neo FM6, a sample for a sampler. Typed per machine through the Neo manifest; never through dialog commands |

### 5.5 Composition

| Tool | Phase | Purpose |
|---|---|---|
| `pattern_list` | A1 | Patterns per machine: name, length in beats, note count, where it is used (arrangement, session) |
| `pattern_create`, `pattern_delete`, `pattern_rename`, `pattern_duplicate` | A1 | Length in beats |
| `notes_write` | A1, U1 | Notes into a pattern:<br>• each note is pitch, start beat, length in beats, velocity 1–127<br>• `mode: add \| replace \| replace_range`<br>• chords are notes with the same start<br>• returns a short summary and clashes (overlaps on a monophonic track, notes outside the pattern)<br>• U1: the written pitch is the pitch heard. A machine's manifest can say it takes notes in its tracks (`input: tracks`, a voice per track) and give a pitch offset; `input` and `apply_pitch_offset=false` override both |
| `notes_read` | A1, U1 | The same shape back, optionally for a range; the same `input` and `apply_pitch_offset` |
| `notes_transform` | A3 | Transpose (semitones or in-scale steps), quantize (grid, strength, swing), humanize, velocity curve, shift, stretch, reverse, invert, legato. Each runs on a selection by range or pitch |
| `automation_write`, `automation_read` | A3, U2 | A parameter's curve in a pattern: points at beats, step or linear, values in units. Written as engine-played automation columns. U2: points outside the parameter's `safe_range` are refused with the reason unless `allow_unsafe: true`, as `param_set` does |
| `arrangement_get` | A1, AR0 | The timeline: one lane per machine with its pattern placements in bars and beats, mutes, markers, loop, song end. AR0 (daw-modes.md S1, S2):<br>• a placement that plays part of its pattern gives the part (`part_beats`) and its volume (`gain`), as the Session View records them<br>• placements joined into a clip carry its id (`clip`); each lane lists its `clips`: id, pattern, start and end, where it starts in the pattern, how many placements, and its loop (start, end, on)<br>• `meter`: the song's bars (`from_beats`, `bar_beats`), as the sequence editor sets them |
| `arrangement_place` | A1 | Puts patterns at bars (one, a list, or repeated every N bars); `arrangement_clear` removes a range |
| `arrangement_markers` | A3 | Named sections (intro, verse…), used by renders and guides |
| `session_get`, `session_clip_set`, `session_scene_set`, `session_launch` | A3/A7 | The Session View grid: clips, scenes, follow actions, launching (live in studio mode) |
| `session_record` | A7 | Takes in the Session View: arm a track, record a take of what is played on its keyboard (bars long or until stopped), stop it, capture what armed tracks were played lately (studio mode) |
| `midi_play` | A7 | Plays a phrase into a machine live, on the next beat or bar, as a keyboard player would; an armed track's take records it (studio mode) |
| `theory` | A3 | Music theory without arithmetic slips:<br>• chord symbols → pitches (`"Am9/E"`), voicings and inversions, scale degrees<br>• key and scale notes; roman-numeral progressions → chords in a key<br>• a melody's fit to a key<br>Read-only; nothing in the song changes |

### 5.6 Samples

| Tool | Phase | Purpose |
|---|---|---|
| `wave_list` | A2 | The wavetable: slots, names, lengths, root notes, loops; a slot with several layers, each one's root (`layer_roots`) |
| `wave_import` | A2 | A file into a slot (WAV/AIFF/FLAC…), at a `root_note`; with `layer`, as another layer of a filled slot, with its own root (Neo Sampler splits a zone's keys by its layers' roots and alternates layers of one root); one undo step either way |
| `wave_from_render` | A3 | A render (or part of one) into a slot: resampling, bouncing a synth to audio |

### 5.7 Ears: render and analysis

| Tool | Phase | Purpose |
|---|---|---|
| `render` | A1 | Renders faster than real time (the true signal: the safety limiter is on the audio device only; a machine whose guard trips stays muted, listed under `diverged` with a warning):<br>• **range:** beats/bars, a marker, the loop, a pattern played alone, or seconds<br>• **mix overrides:** solo, mute and bypass lists that do not touch the song<br>• **probes:** any machines' outputs, captured alongside the master<br>Returns a render id, WAV resource links, duration, and a loudness and peak summary. A task when long |
| `analyze` | A1/A2 | Measures a render, a probe or a file, by metric:<br>• `loudness`: integrated, short-term max, momentary max LUFS, loudness range<br>• `peaks`: true peak dBTP, sample peak, clipping count<br>• `dynamics`: crest factor, PLR, RMS over time<br>• `spectrum`: per third-octave and per band (sub, low, low-mid, mid, high-mid, presence, air), centroid, tilt<br>• `stereo`: correlation, width per band, mono compatibility<br>• `pitch`: fundamental and cents over time (monophonic)<br>• `key`; `tempo` and onsets<br>• `microdynamics`, `tonal`, `platform` and `stereo`'s `ms_bands` (M1, mastering-tools.md §6): LDR and the side/mid ratio in four bands, the 1/6-octave long-term curve, what each streaming service does to the level<br>• `target` (M1): a style target (`target_build`) or a reference render or WAV; each measure also reads against it<br>Numbers come with plain-language reads ("tilt −4.2 dB/oct: dark") |
| `analyze_mix` | A2 | One render with a probe on every machine feeding the mix:<br>• per track: loudness, peak, spectrum bands, crest<br>• a masking table: which pairs fight in which bands, how much<br>• headroom and the master's measures<br>• a short list of the biggest problems |
| `compare` | A2 | Two sources (two renders, a render and a reference file, before/after snapshots): differences per band, loudness, dynamics and stereo |
| `plot` | A2 | An image the agent looks at: spectrum, spectrogram, waveform, loudness over time, stereo field (goniometer), or per-track bands. Returned as image content |
| `notes_heard` | A5 | Transcribes a monophonic render (pitch and onsets) so the agent can check that what plays is what it wrote |
| `target_build` | M1 | Averages the user's own reference recordings (1–20 WAVs or renders) into a style target: the tonal curve, loudness, loudness range, PLR, side/mid and LDR in four bands, each with the references' spread. Saved as `targets/<name>.target.json` beside the agent's log (or a file given), so every song can use it |
| `eq_fit` | M1 | A tonal difference (a source against a target, level-matched, smoothed to 1/3 octave, clipped to ±3 dB) or a curve given, fitted with up to 6 Neo EQ bands on its own response; returns the bands, the residual and their `param_set` changes, and with `machine` writes them into a Neo EQ's B2–B7 as one undo step (mastering-tools.md §8, M1) |

### 5.8 Live readings (studio mode)

| Tool | Phase | Purpose |
|---|---|---|
| `meters_read` | A7 | Live peak and loudness of the master and chosen machines while the user plays the song, over a window (§3.5: subscriptions not built). Measured before the safety limiter; `safety` gives the master's latched over and whether the limiter acted during the window, and `clear_clip` clears the indicator first (machine-safety.md §3.3) |
| `machine_readings` | A7 | Values an analysis machine shows (Neo Analyzer: spectrum, LUFS, correlation; Neo Multiband: its bands' gain changes; Neo Scope: peak, RMS, correlation, its trigger's lock), from wherever the user put it |

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
| Machine manuals | Generated from each machine's manifest: the Neo machines' Params.def (every parameter with unit, range and meaning) plus a curated "how it sounds" section and its presets; since U3 every other machine's curated or draft manifest too (plain names, roles, quirks, how it takes notes, health and safe ranges, §12.9) |

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

M1 (mastering-tools.md §6 and §8) adds, in the same library:

| Measure | Method | Built: measured (asserted) |
|---|---|---|
| Microdynamics | Per band (Linkwitz–Riley 24 dB/oct split at 120 Hz, 2 kHz, 10 kHz) and whole: 20 ms frames' level over the mean of the 1 s around them, dB; LDR is its 95th percentile (and the median), where the band is within 30 dB of its loudest second | Noise bursts 40 ms in 250 ms, 20 dB over a bed: 7.37 dB (7.7 ±0.5); steady noise 0.31 (0–1), per band 3.2, 1.2, 0.5, 0.4 (±0.3: the measure's floor); a level change moves nothing (1e-9); a fast leveller lowers it 3.8 dB (> 3) |
| Side/mid in four bands | `stereo`'s `ms_bands`: the Welch cross spectrum's side over mid in the same four bands | Mono lows −154 dB (< −30), uncorrelated highs −0.1 dB (0 ±0.5), correlation 0.00 (±0.05); a mono signal −∞ |
| Tonal curve | 1/6 octave, 20 Hz – 20 kHz, each point the power in a sixth of an octave; against a target, target − source after matching the mean level from 100 Hz to 10 kHz, its RMS from 50 Hz to 16 kHz and the same smoothed to 1/3 octave | Pink noise flat within 0.3 dB 40 Hz – 10 kHz; itself 0 (1e-9); a 12 dB level change only an offset; an f^0.2 tilt recovered at 2 dB a decade (±0.1) |
| Platform preview | By integrated loudness and true peak: Spotify normal −14, Loud −11 (raised into its limiter from −1 dBFS), Quiet −19 (raised only to −1 dBTP), from Spotify's published rules; Apple Music −16, YouTube, TIDAL, Amazon −14, Deezer −15, observed, turning down only | Spotify's example (−20 LUFS, −5 dBTP → −16 LUFS) exact |

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
| **Neo Scope** | Oscilloscope (tempo-synced) and goniometer/vectorscope. Mostly for people; the agent's equivalent is `plot` | Medium. **Built** 2026-11-04 (machines.md, Neo Scope): left, right, mid, side or both overlaid; synced from 1/16 to 4 bars, each sweep starting on the song's beats and bars so a loop draws the same frame every pass, or free in time per division with an edge trigger and holdoff; a goniometer or polar vectorscope with the correlation meter; auto scale or gain, a trail, freeze; audio untouched, snapshots at the display's rate. `machine_readings` gives its peak, RMS, correlation and the trigger's lock; its guide sends agents to `render`, `plot` and `analyze` |

### 8.2 Machines the agent needs to mix and master

Today an agent could compress and saturate with Neo Comp and Neo Drive, but it has no precise EQ, no true-peak limiter and no simple utility stage. Those are the moves every mix and master is built from:

| Machine | What | Why the agent needs it | Priority |
|---|---|---|---|
| **Neo EQ** | 8-band parametric: bell, shelves, cuts at 12–48 dB/oct, Q. Optional dynamic bands. Minimum or linear phase. Spectrum overlay | Precise, labelled moves ("−3 dB at 320 Hz, Q 1.4"). The existing EQs have coarse or unlabelled controls | High |
| **Neo Limiter** | True-peak lookahead limiter: ceiling in dBTP, release (auto), gain into it. Optional target-LUFS mode that sets the gain from a measurement | Mastering to a loudness target without clipping | High |
| **Neo Utility** | Gain dB, pan, width, mono below a frequency, phase invert, channel swap, DC removal | Gain staging and stereo fixes as one-parameter moves | High |
| **Neo Delay** | Tempo-synced stereo/ping-pong delay with filters, saturation and ducking | Space and depth in mixes; sound design | Medium. **Built** 2026-11-04 (machines.md, Neo Delay): stereo, ping-pong and cross; a note value straight, dotted or triplet, or ms; low cut, high cut and tape saturation in the loop, wow, ducking, width; feedback to ±99 %, bounded at every setting |
| **Neo Multiband** | 3–4 band compressor/expander with linear-phase crossovers | Mastering and bus control | Medium. **Built** 2026-11-04 (machines.md, Neo Multiband): 3 or 4 bands on linear-phase crossovers that sum back to the input exactly (4607 samples of latency at 44.1 and 48 kHz, reported to ReBuzz) or zero-latency minimum-phase Linkwitz–Riley ones with the same band shapes; per band Neo Comp's detector and gain computer (above 1:1 compression, below it upward expansion), range, makeup, solo, bypass; stereo or mid/side; each band's gain change in `machine_readings` |
| **Neo Sampler** | Plays waves: multi-sample zones, velocity layers, ADSR, filter, choke groups; drum-kit mode | Drums and sampled sounds with clear units, driven by `wave_import` | Medium. **Built** 2026-11-04 (machines.md, Neo Sampler): the song's waves (saved with the song) and 26 built-in samples in 16 zones by key and velocity; a slot's layers split by root, round robin, velocity crossfades; loops (the wave's own, seamless at any pitch, or a zone's, crossfaded), one-shot or gate, reverse, choke groups; amp and filter envelopes; Kit mode on the General MIDI drum keys from C2; a 32-tap windowed sinc with octave-down copies (aliases under −90 dB from −12 to +48 st). Note names and wave names as values (§3.3), `wave_import(layer)` (§5.6); the successor of Pedal M1 (§12.10) |
| **Neo Drums** | Synthesised drum kit: kick, snare, clap, rim, hats, toms, cowbell, cymbals in real units | Drum parts with clear units and no files; the successor of the classic drum machines (§12.5) | Medium. **Built** 2026-11-05 (machines.md, Neo Drums): twelve voices in the TR-909 and TR-808 manner, voiced against the Rout 909's samples, each with its tune (Hz or semitones), decay (to −60 dB), tone or snap, level and pan; the closed hat chokes the open one; band-limited metallic oscillators (aliases under −115 dB); the General MIDI drum keys from C2 and per-track hit columns (BD … RC, a velocity each) with an Accent column, which `automation_write` writes as played parameters; one stereo output (per-voice outputs: machines.md, Neo Drums); the successor of Rout 909 and FSM KickXP (§12.10) |
| **Neo Filter** | Filter effect: Neo Poly's ladder and state-variable filter, drive, an envelope follower and an LFO on the cutoff | Sweeps, auto-wah and dub filter moves in units (Hz, Q, octaves, note values); the successor of the classic filters (§12.5) | Medium. **Built** 2026-11-05 (machines.md, Neo Filter): Ladder 24 and 12, SVF low-pass, band-pass, high-pass, notch and peak at 12 or 24 dB/oct, four times oversampled (its response is the design the editor draws, within 0.012 dB); resonance as Q up to a bounded self-oscillation; drive with anti-aliasing (aliases under −137 dB at a mix's level); the envelope (±6 octaves) and the LFO (free, or 1/32 to 16 bars locked to the song) in octaves; zero latency, Mix 0 % bit for bit; the successor of Jeskola Filter and Filter 2, FSM WahMan and Pedal Filter (§12.10) |

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

### 10.1 A6 as built

**What runs where.**

| Layer | Where | When | Takes |
|---|---|---|---|
| Self-test | Fork CI, Linux (`ReBuzz.AgentEval selftest`) | every PR and push to main | seconds |
| Scripted suite | `fork-package.yml`, step "Agent evaluation", after the tests, on the built `ReBuzz.exe` (quick and full builds) | every package run | about 2 minutes, its build included |
| Real agent | `agent-eval.yml` | weekly (Monday 03:17 UTC) and by hand | per task, up to the turn and budget caps |

**`ReBuzz.AgentEval`** is a net10.0 console app with no packages and no WPF. It references `ReBuzz.Agent` for its WAV reader and `NeoBuzz.Analysis`, so every score comes from our own meters on the files `render` wrote, never from a tool's report of itself.

- **The client** (`McpClient.cs`) is the one from `AgentHeadlessTests`, made reusable. It starts `NeoBuzzLive.exe --agent [song]` (or `rebuzz-headless --agent`) and does `initialize`, `tools/list` and `tools/call`, with structured results, `isError` and timeouts. It counts calls and error calls. On failure it reports ReBuzz's stderr and, on Windows, the process's visible windows.
- **The self-test** runs without ReBuzz:
  - the client against our protocol layer, in process;
  - the meters on signals with known answers: spectral and envelope distance, masking, resonance prominence, correlation of a widened noise, silent gaps;
  - the scoring rules, and the gate's rules.

**Scripted checks.** Each runs through the real protocol and asserts exact results:

| Check | Asserts |
|---|---|
| `protocol` | The handshake and instructions. Every tool is named `area_verb` and has an example, an object input schema, an output schema and `openWorldHint: false` (§4). An unknown tool says "Did you mean". An unknown argument names the right one. stdout carries only JSON-RPC |
| `notes_roundtrip` | Names, sharps and flats, MIDI numbers, chords, 16ths, triplets and velocities 1–127 read back exactly. What `notes_read` returns, `notes_write` takes back unchanged. Ranges, `pattern_duplicate` and save/open keep the notes |
| `param_units` | `"800 Hz"` reads back `800 Hz`. Units, `by`, `times`, choices, `min`/`max`/`default` and `raw:` each land within one step of the parameter's resolution. A misspelt parameter or a wrong unit is an error that says what next, and a call with one bad change changes nothing |
| `render_bit_identical` | A song (LFOs, drift, reverb, saturation, compression) renders to the same bits, probes included, each time it is opened afresh, and in a second ReBuzz process |
| `analyze_reference` | EBU Tech 3341's 1 kHz sine at −23 dBFS reads −23 LUFS and −23 dBTP, crest 3.01 dB. Anti-phase noise reads correlation −1. 120 BPM clicks read 120 BPM. A sawtooth on A3 reads A3 at 0 cents. A C major scale reads C major. On a render, the tool's loudness, true peak, tilt and correlation equal our meters' |
| `undo_exact` | 24 changing calls of every kind (song, machines, routing, parameters, presets, patterns, notes, transforms, automation, markers, the session grid, waves, snapshots), undone one by one, give back the full tool-read state exactly; redone, the state after them. A call that fails changes nothing and leaves undo where it was |
| `machine_replace` | U5 (§12.10): a machine this ReBuzz has with a successor mapping (Pedal EQ or Gain here and in a full Windows build, else Jeskola Reverb or Gain) moves onto its successor: a dry run changes nothing, the replacement takes its place and compares before and after, one undo gives back the full tool-read state exactly. Whether the comparison is within the pair's tolerance is noted; the tests assert it |

**Agent tasks.** Each task (`Tasks/*.cs`) has four parts:

- a brief, the exact prompt with where to save the song;
- a setup that builds the starting song;
- a scorer: named criteria, 0–1 each;
- a scripted reference solution.

A task passes with a total (the mean) of at least 0.85 and no criterion under 0.5. In the suite, each reference solution must pass, and its starting song, scored the same way, must not.

| Task | Setup | Criteria (1 at, falling to 0 at) |
|---|---|---|
| Beat | an empty song | renders (16 beats, audible); tempo 124; four parts audible (Kick, Clap, Hats, Bass); an onset within 35 ms of ≥ 90 % of the beats on the kick (0 at 40 %); claps on ≥ 85 % of beats 2 and 4; the bass's note time in one scale ≥ 95 % (0 at 70 %) and heard on its written pitches; true peak < 0 dBTP, no clipped sample |
| Sound match | renders a Neo Poly pluck to `reference.wav`, then leaves a default Neo Poly on the same notes | level-matched third-octave distance ≤ 1.5 dB (0 at 6); 10 ms envelope distance ≤ 2 dB (0 at 8); loudness within 1 LU (0 at 6); notes kept |
| Fix the mix | kick and bass both on 55 Hz; a lead at 88 % ladder resonance fixed at 3.15 kHz; hats at +4 dB; a pad only through a 100 % wet reverb at 150 % width | kick/bass masking (summed severity, each part probed where it reaches Master) at most half the start's; the resonance ≤ 3 dB above its neighbours; hats 9–30 LU under the master; the pad's correlation ≥ +0.1; nothing worse: every part audible within 12 LU of its start, the master not clipping or 8 LU quieter |
| Master | an 8-bar mix at about −31 LUFS | −14 ±1 LUFS (0 at ±3); true peak ≤ −1 dBTP (0 at +0.5); crest factor 6–14 dB (0 at 3 dB outside); no clipping; every part's loudness against the others' mean within 3 LU of the start's |
| Master to a reference (M1) | the Master mix, and `reference.wav`: the same mix through a known EQ (−3 dB at 250 Hz, +1.5 dB at 2.5 kHz, a +3 dB shelf at 7 kHz) limited to −12 LUFS | the third-octave tonal difference after level matching ≤ 1.5 dB RMS, 50 Hz – 16 kHz (0 at 3); side/mid width per band within 0.1 (0 at 0.3); PLR within 1.5 dB (0 at 4); loudness within 1 LU of the reference's (0 at 3); true peak ≤ −1 dBTP; no clipping; balance kept |
| Master for Spotify Loud (M1) | the Master mix | −11 ±1 LUFS (0 at ±3); true peak ≤ −2 dBTP (0 at −1); Spotify Loud neither raises (±1 dB, 0 at 3) nor limits it (0.1 dB, 0 at 1); crest ≥ 5 dB (0 at 3); no clipping; balance kept |
| Fix harshness (M1) | the Master mix with +12 dB at 3.5 kHz (Q 0.9) on the lead, in its own Neo EQ | the 2–6 kHz excess over 1–2 and 6–12 kHz at least 3 dB less (0 at 1), and not under −2 dB; 8–14 kHz against 100 Hz – 1 kHz within 1 dB (0 at 2.5); loudness within 2 LU (0 at 4); no clipping; balance kept (the lead may fall by up to 6 dB) |
| Song | an empty song | 105–140 s long (0 at 40 s outside); tempo 92; at least 4 markers; sections ≥ 2 LU apart in loudness (0 at 0.3); every section's key the song's or its relative; −14 ±1.5 LUFS (0 at ±4); true peak ≤ −1 dBTP; no stretch under −60 dBFS longer than 0.5 s (0 at 2 s); 4 or more audible generators. The human rating is recorded by hand |

The reference solutions use the tools as an agent would:

- presets and parameters in units;
- `theory` for the song's chords and to check the hook;
- `analyze_mix`, `plot` and `compare` before and after;
- Neo EQ for the mix moves: a high cut on the kick, a low cut on the bass, a bell on the resonance;
- a mastering chain on a bus (Neo EQ 30 Hz low cut, Neo Comp glue under the measured peaks, Neo Limiter at −1.2 dBTP), with the loudness reached by raising the limiter's `Input gain` and measuring again;
- (M1) `target_build` from the reference, `analyze` against it, `eq_fit` writing the chain's Neo EQ (it finds the reference's −2.7 dB at 257 Hz Q 1.03, +1.5 dB at 2.4 kHz Q 1.34 and a +3.2 dB bell at 12 kHz for the shelf, 1.42 dB RMS wanted, 0.11 left; the master ends 0.23 dB RMS from the reference); `analyze`'s `platform` for Spotify Loud; `eq_fit` from a curve for the harshness (a −9 dB cut at 3.5 kHz in a Neo EQ after the lead's).

**Results.** Per check: pass, failures, notes, tool calls, error calls and seconds. Per task, the same for the reference solution, plus:

- each criterion with what was measured, and the total;
- the starting song's total;
- everything measured, for both songs (`facts`, `start_facts`).

These go to `eval-out/eval-results.json` and `eval-summary.md`, the step summary (`$GITHUB_STEP_SUMMARY`), annotations (readable through the API) and the `eval-results` artifact, with the songs and briefs.

**Regressions block.** `eval/baseline.json` holds each check's pass and each task's total and criteria. The run exits 1 when:

- a check that passed fails;
- a task that passed fails, or its total drops more than the tolerance (0.05);
- a check or task without a baseline entry fails;
- a starting song passes.

Known failures (failing in the baseline too) are reported but don't block. `run --update-baseline`, or `rebaseline --results eval-out/eval-results.json`, rewrites the baseline.

**Found by the suite and fixed.**
- **Renders weren't reproducible.** Each started wherever the previous render left the engine's tick phase, so renders of the same song differed by up to 0.12 in sample value. `render` now starts on a tick boundary.
- **Undo lost a connection's pan.** Undoing a disconnect, or a machine deletion, re-created the connection without its pan (`DisconnectMachinesAction`), so `connection_set(pan=…)` then failed.
- `undo` and `redo` had no output schema.

**Found and recorded (notes in the results, not failures):**
- A bare number out of a parameter's range is clamped without a warning. A bare `20` for Neo Verb's Pre-delay is read in seconds and lands at 250 ms.
- A second render in one session differs from the first, because voices, random phases and envelopes carry over. A freshly opened song renders identically.
- Undo puts a restored machine or connection at the end of its list. The content is exact.
- The Session View's grid fills itself from the song on its first edit, and stays when that edit is undone (as in A3). The check makes it exist before it starts.

**Running it locally** (Windows, after `build.ps1`):

```
dotnet build ReBuzz.AgentEval -c Release
dotnet ReBuzz.AgentEval/bin/Release/net10.0/ReBuzz.AgentEval.dll run --exe bin/Release/x64/net10.0-windows10.0.26100.0/ReBuzz.exe [--only beat,undo_exact] [--verbose]
dotnet run --project ReBuzz.AgentEval -- selftest          # any OS, no ReBuzz
```

**Real-agent runs** (`agent-eval.yml`).
- **The secret.** It needs the repository secret `ANTHROPIC_API_KEY`: Settings → Secrets and variables → Actions → New repository secret. Without it, the run posts a notice and skips the Windows job; the workflow stays green.
- **Each task:**
  1. `prepare` builds the starting song, `brief.md` and an `mcp.json` that runs `NeoBuzzLive.exe --agent start.bmxml` as the server `neobuzz-live`.
  2. Claude Code runs headless from an empty folder (no repository context): `claude -p "<brief>" --bare --mcp-config mcp.json --strict-mcp-config --tools "" --allowedTools "mcp__neobuzz-live__*" --permission-mode dontAsk --max-turns 80 --max-budget-usd 5 --output-format stream-json --verbose --no-session-persistence`. Flags were checked against the CLI's own help, 2.1.316: only the rebuzz tools are available and allowed, everything else is denied without a prompt. `MCP_TIMEOUT` is raised to 240 s, since the CLI waits 30 s by default for a server to answer and ReBuzz's start-up can take longer.
  3. `score` opens the starting song and the saved one in a fresh headless NeoBuzz Live, scores them, and reads the transcript: the app's tool calls, error calls with their texts, turns, cost, how it stopped.
- **Inputs:** tasks, model, turn cap, budget, CLI version.
- **Uploads:** transcripts, songs and scores. The step summary has the table, and the tools whose errors the agent hit, worst first.

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
| Classic natives with sources in Buzztrax | FSM Infector, KickXP, PhatMan, PanzerDelay, Chorus2, WahMan; Jeskola Delay, Cross Delay, Distortion, NiNjA dElaY, Trilok, Noise; Oomek Aggressor, Exciter, Masterizer | `ExternalMachines/source/buzztrax-buzzmachines` (C++, per-author licences) | Tiers 1–3: built in the package since U7 (§12.14); FSM's and Oomek's ship rebuilt, Jeskola's have no licence |
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

For the native machines with Buzztrax sources, the first step is the build fix (current MSVC headers). Then the same treatment minus the editors: fixes, x64 builds of the ones upstream lacks, and generated manifests. Built in U7 (§12.14).

### 12.5 Tier 4: successors, with migration

Where a machine's design limits its quality (2000s-era reverbs and delays, unlabelled EQs, aliasing distortions, fixed-voice synths), the answer is a Neo successor. Originals stay installed so old songs play. A replacement tool moves a song over: `machine_replace(name, gear)` uses ReBuzz's `ReplaceMachine` with a mapping built from manifest roles:

1. it maps cutoff to cutoff and wet to mix;
2. it converts values through units, not raw numbers;
3. it renders before and after with the analysis library, and reports the difference.

The tool works for agents and, from the machine menu, for people. Built in U5 (§12.10), with the mappings as data (one file per pair) rather than from roles alone: a role says what a parameter is for, not how two machines' levels, curves and topologies line up.

| Classic machines | Successor | Status |
|---|---|---|
| Jeskola Reverb, Stereo Reverb, Raverb; Pedal Plate | Neo Verb | built (machines spec M1) |
| Jeskola Distortion, Wave Shaper; FSM Philthy | Neo Drive | built |
| Pedal Comp; Jeskola Limiter (as a compressor) | Neo Comp | built |
| Jeskola EQ-3, Joachims Enceladus, Pedal EQ | Neo EQ | built (A4, machines spec M3) |
| Jeskola Limiter, Oomek Masterizer | Neo Limiter | built (A4, M3) |
| Jeskola Gain, Pedal Gain | Neo Utility | built (A4, M3) |
| Jeskola Delay, Cross Delay, NiNjA dElaY; FSM PanzerDelay | Neo Delay | built (§8.2; machines.md, Neo Delay), with the four `machine_replace` pairs (§12.10) |
| — (no multiband in the package) | Neo Multiband (3–4 band compressor and upward expander) | built (§8.2; machines.md, Neo Multiband): a new machine, so no `machine_replace` pair |
| Jeskola Chorus, Flanger; FSM Chorus2; Pedal Chorus | Neo Ensemble (chorus/flanger/phaser) | built (machine-safety.md §4; roles for the mapping there) |
| Joachims Hyperion | Neo EQ, graphic mode (31 constant-Q bands at the ISO centres, the overlap compensated) | built, with its `machine_replace` pair (machine-safety.md §4, §12.10) |
| Jeskola Filter, Filter 2; Joachims Mars; FSM WahMan; Pedal Filter | Neo Filter (the Neo Poly filters as an effect, with envelope follower and LFO) | built (§8.2; machines.md, Neo Filter), with the four `machine_replace` pairs (§12.10); none for Joachims Mars filter 2: measured, it is a distortion (its response follows the input's level), whose successor would be Neo Drive |
| Jeskola Bass 2/3, Oomek Aggressor 303, Pedal SH-101 | Neo Poly (mono/legato presets) | built (M2) |
| FSM Infector, Pedal Juno106 | Neo Poly, Neo Wave | built (M2) |
| Jeskola Organ | Neo Wave (organ table), or a dedicated tonewheel organ | proposed |
| Pedal FM | Neo FM6 | built (M2) |
| Rout 909, FSM KickXP | Neo Drums (synthesised kit) | built (§8.2; machines.md, Neo Drums), with the two `machine_replace` pairs (§12.10); one stereo output: per-voice outputs wait for the studio, the tools and the mixer to route a machine's outputs (machines.md, Neo Drums). Pedal Drum Matrix is a multi-effect rack for drums, not a drum machine: no pair |
| Jeskola Tracker, Matilde Tracker2, Pedal M1 | Neo Sampler | built (§8.2; machines.md, Neo Sampler), with the Pedal M1 `machine_replace` pair (§12.10); none for the trackers: they choose a wave per note and play effects per row, which zones can't hold |

### 12.6 How the tiers fit the phases

| Phase (§9) | Machine upgrade work |
|---|---|
| A1 | Pitch offsets and hand-written manifests for the 10 machines agents reach for first (Neo machines generated; Juno106, SH-101, Drum Matrix, Infector, Jeskola Delay, Pedal EQ…) |
| A2 | The profiler and health report over all 101; draft manifests for everything |
| A3 | Curated manifests for the top 25; the manifest editor; factory presets for the top machines |
| A4 | Neo EQ, Limiter, Utility (successors); `machine_replace` with role mapping and render comparison |
| A5+ | Tier 3 for the Pedal and ReBuzz machines; the Buzztrax build fix; Neo Delay, Ensemble, Filter, Drums, Sampler as the next machine batches |

**Done when** every machine in the package has a manifest; `notes_write` plays the written pitch on every pitched generator; the health report is clean or every exception is listed; and an old song converted with `machine_replace` measures within a stated tolerance of the original, or the report says why not.

As of A7 the phases above ran ahead of this work: A0–A7 are built, with the Neo successors of §12.5 marked built and their generated manifests, but nothing of tiers 1–4 for the other machines. §12.7 is the plan from here.

### 12.7 Build plan

PR-sized phases. Each ends with its exit check passing in CI (Linux for the portable machines, the Windows package run for the natives) and the agent eval unchanged.

Unhealthy machines (decided 2026-11-03) are in [machine-safety.md](machine-safety.md): the profiler's finer health categories and `safe_range` (part of U2), a guard on every machine and a safe master in the engine, safe ranges for agents, Song Notes for Info, and Neo Ensemble, Neo EQ's graphic mode and a Jeskola Bass mapping as successors.

| Phase | Delivers | Exit check | Effort |
|---|---|---|---|
| **U1 Profile and describe** (tier 1, first half) | `tools/machine-profiler.py` over MCP: value tables, unit and mapping inference, pitch convention, note input, polyphony, effect kind, latency, tail, gain, health. Draft manifests in `Machines/Manifests/`, copied beside their DLLs; `docs/machine-health.md`. The server reads drafts: units in `param_set`/`param_get`, pitch offsets and track notes in `notes_write`/`notes_read`/`midi_play`, kind and health in `gear_list` | A Pedal synth plays A4 at 440 Hz through `notes_write` after profiling; `param_set` in units on a Pedal machine; the Linux job profiles the portable machines and the Windows package job all of them | L |
| **U2 Profile in depth** (tier 1, second half) | A test-signal source for effects (sweep, impulse, level steps through a general tool), per-parameter sweeps (what moving each one changes) as suggested roles, velocity response and envelope length, denormal and extreme-value checks per parameter. `gear_list` leaves unhealthy machines out unless asked. **Built first** ([machine-safety.md](machine-safety.md) §3.1, §3.4): the level test with each parameter alone at each end, diverging, hot and needs-a-window instead of one "unhealthy", `safe_range` in the drafts and `param_set`'s refusal (§12.8, last part). **Built in depth** (§12.12): the test signal through the profiler (a click, a stepped sine sweep and level steps, played by Neo Poly and read on both sides), suggested roles from each parameter alone, velocity and envelopes, a harmonic sieve for pitch, triggers through `automation_write`, slowdowns after silence from `render`'s per-machine work time, safe ranges for ends that ring, and `gear_list` leaving unhealthy machines out unless asked | Suggested roles for the filter, time and mix parameters of the 25 most used machines; every unhealthy machine has a reason in the report | M |
| **U3 Curate** (tier 1 done) | Curated manifests for the top 25 (summary, plain names, roles, safe ranges, quirks); manuals (`rebuzz://machines/…`) for every machine with a manifest; `GuideTests` check curated names and values. **Built** (§12.9) | 25 curated, the rest drafts: every machine in the package has a manifest | M |
| **U4 A better face** (tier 2) | The manifest editor on the editor kit (groups, knobs in units, choices, ADSR and response plots), a per-machine setting; factory presets for the top machines. **Built** (§12.11) | The editor opens for every curated machine in the studio and ReBuzz.exe; its presets load through `preset_load` | L |
| **U5 `machine_replace`** (tier 4) | Role mapping, unit conversion, render before and after with `compare`; one undo step; the machine menu's Replace. **Built** (§12.10) | Jeskola Reverb → Neo Verb, Pedal EQ → Neo EQ, Jeskola Distortion → Neo Drive and Pedal Gain → Neo Utility convert within a stated tolerance, or say why not | M |
| **U6 Source fixes** (tier 3, C#) | Pedal and ReBuzz machines: manifests from code, MIDI input for the Pedal synths, denormal guards, smoothing, silence reporting; the Neo measurement suite on them. **Built** for the Pedal effects and ReBuzz's own machines (§12.13), with Neo Analyzer's slowdown, and for the Pedal synths (§12.15): MIDI input, their glides and silence, Pedal Tracker's, Chord's, LFO's and Peer Control's manifests from code, Juno106's and SH101's banks levelled | Their health report is clean; their manifests no longer drafts; old songs render as before | L |
| **U7 Natives with sources** (tier 3) | The Buzztrax MSVC header fix, x64 builds, fixes and generated manifests. **Built** (§12.14): all 17 build as x86 and x64; the 11 FSM and Oomek x64 rebuilds are bit-identical to upstream's, 10 ship in its place (WahMan and WahPro2 with a fix); Jeskola's six aren't redistributable (no licence); 8 manifests from code | The Buzztrax machines build in the package; §12.6's "Done when" holds | L |

The successors of §12.5 were machine batches of their own (machines spec), each landing with its `machine_replace` mappings: Neo Ensemble, Neo Delay, Neo Sampler, Neo Drums and Neo Filter are built; Jeskola Organ's (Neo Wave's organ table, or a tonewheel organ) is still proposed.

### 12.8 U1 as built

**The profiler** (`tools/machine-profiler.py`, standard library only) is an MCP client like any other: `rebuzz-headless --agent` on Linux and macOS, `ReBuzz.exe --agent` on Windows. A machine that crashes or hangs the server (no answer in 90 s) is recorded and the server restarted; one that takes its native host down is caught through `gear_list`'s `crashed`. Per machine, in a fresh song:

- **Parameters.** `machine_describe(value_table=true)`. `tools/machine_infer.py` reads each table:
  - the unit from the texts (`kHz`, `ms`, `dBFS`, `−inf`, `L 50%`, `4:1`, tracker-style `0040 (−54.2 dB)`);
  - the mapping, the simplest that reproduces every text to its precision (or 1 %): linear, dB (with −∞ at the bottom), exponential (`freq`, `time`), ratio, power, else a table of up to 129 points;
  - choices and switches.
  Where the texts are bare numbers, a unit can come from the name or description (`in ms`, `Attack ms`, `(1 ms – 4 s exponential)`, `0 = 0 dBFS, 60 = −60 dBFS`, `0=Sine, 1=Triangle…`), marked `confidence: description`. Descriptions whose anchors contradict the texts (`0 = slow (0.1 Hz)` over texts 0…100) give no unit. On the Neo machines, whose manifests are exact, the inference reproduces 460 of 461 parameters (the other is a two-value choice read as numbers).
- **Generators.** An A4 sent as MIDI, then into the Note track parameter (or the other way round when the machine has no `MidiNote`); if both are silent, again with a sine rooted on A4 in wave slot 1 (samplers, trackers). The `pitch` metric on A3, A4 and A5 gives the pitch convention: an offset when all three agree within 0.6 semitones, else unpitched. Chords of 1, 2, 4 and 8 notes (a tritone, fourth or seventh apart) give the polyphony: each voice must add 1.5 dB. Then level, true peak and CPU.
- **Effects.** A Neo Poly feeds it, with probes on both sides:
  - white noise for 0.5 s: gain, latency (first sample), output correlation, spectrum change (third octaves, level-matched);
  - a 20 ms noise click with 3 s to ring out: the tail (until 60 dB down) and echoes (from 60 ms on, a 10 dB rise from a dip);
  - a 440 Hz sine at −6 and −26 dB: THD over the input's, level dependence, movement.

  These give traits, the first of which is its type: delay (two echoes, or one after a near-silent gap), reverb (a long smooth tail), distortion, modulation, dynamics (level dependence, unless the output moves on its own), filter or EQ, stereo, utility. An effect that does nothing at its defaults is typed by its parameters' units (frequencies and gains: EQ; threshold or ratio: dynamics).
- **Health.** Load failures, crashes and hangs, NaN or a runaway level (over +24 dBFS) at the defaults and with every parameter at its minimum and at its maximum, sound from silence, silence from a note (U2 replaced the +24 dBFS line with a level test and three categories: below). CPU is milliseconds of render time per second of audio over a render without the machine (rough: a few ms/s of noise).

**Outputs.**

| Output | What |
|---|---|
| `Machines/Manifests/<Generators\|Effects>/<Gear>.machine.json` | A draft per machine, in the manifest format plus: `status: draft`, `type`, `notes` (`input`, `pitch_offset`, `pitched`, `polyphony`), `health` (`status`, `problems`; U2: `diverging`, `hot`, `needs_window`), `profile` (the measurements), a parameter's `safe_range` (U2), `track_parameters` described in full, each parameter's `confidence`, and `kind: table` with `points` (`"-inf"` for −∞). Neo machines keep their generated manifests |
| `Machines/Manifests/machine-profile.json` | The summary, keyed by gear: a run merges its machines into it and keeps the other platforms' |
| `docs/machine-health.md` | Generated from the summary: unhealthy, warnings, generators, effects |

`--repo .` writes all three. A Windows run's artifact merges in with `--merge <artifact folder> --repo .`, and `--report .` rewrites the report. The package job also prints its results into its log (`--pack`, gzipped base64 lines), and `--unpack <job log> --out <folder>` takes them back out where artifacts can't be fetched. Drafts keep the measurements, not the timings (CPU, load time vary run to run); the summary and the report have those. Render files are deleted once read.

**Into Gear.** `ReBuzz.Host/AppGear.targets` (rebuzz-headless, the studio) and `build.ps1` step 5b (ReBuzz.exe's bin and the package, through `Copy-IntoGear`) copy the drafts beside the DLLs. There `MachineManifest.PathFor` finds them as it finds the Neo ones. Drafts of machines a host doesn't have are copied too, and ignored.

**The server.**
- **Units.** Drafts map global and track parameters (`SongModel.Params`). A draft matches the machine's own texts first (`ParamInfo.TextsFirst`: "Off", "1/8"), then its mapping. `param_set` and `param_get` say `manifest: draft`. Values whose unit came from a description are `approximate`. `exact_units` stays for generated and curated manifests.
- **Notes.** `notes_write`, `notes_read` and `notes_transform` go through `MachineNotes`. For `input: tracks` (from the manifest, or given), notes get a voice each (`TrackNotes.Allocate`), Note events go into the pattern's engine-played Note track columns, and offs at each end, a note to the pattern's end let go on its last tick. Velocity goes into a track parameter named like "velocity" if there is one. The track count rises when a chord needs it, in the same undo step. `pitch_offset` is added on the way in and taken off on the way out, so `notes_read` gives what was written. `apply_pitch_offset=false` and an explicit `input` are how the profiler measures. `midi_play` applies the offset too, and warns that live MIDI may not reach a tracks-only machine. `automation_read` leaves note columns out.
- **`gear_list`** gives `type`, `summary`, `manifest` and `health` with `problems`: generated and curated first, then drafts, then the rest, unhealthy last. `machine_add` warns when the gear is unhealthy (U2: only when it diverges). `machine_describe` adds `gear_info` and `value_table`.
- Songs: nothing a song saves changes meaning. Notes written to a tracks machine are ordinary Note column events (engine-played, as automation is), and an old song plays as it did. The Session View's piano roll edits MIDI columns, so it doesn't show them, and `notes_read` on such a machine doesn't show notes left in its MIDI column, which it never played.

**Tests.**
- `tools/test_machine_infer.py`: 24 inference cases from known tables.
- `ReBuzz.AgentTests/DraftManifestTests.cs`: parsing, units through drafts, table mappings with −∞, texts first, voices, the track-note round trip, offsets and their limits.
- `tools/headless-manifests.py`, in Fork CI on rebuzz-headless:
  - Pedal SH101 plays A4 at 440 Hz through `notes_write` and is silent through MIDI alone;
  - `notes_read` round-trips;
  - a copy of the app with a +12 offset sends A5 and reads back A4;
  - `param_set` in Hz, dB, ms, ratios and choices on Pedal EQ, MComp and Juno106;
  - the unhealthy flags and warning; every Pedal machine has a manifest.
- Fork CI also runs the profiler on every portable machine (artifact `machine-profile-linux`), and the package job on everything in ReBuzz.exe's Gear (artifact `machine-profile-windows`, never failing the job).

**Found.** 108 machines: 29 on Linux (10 Neo, 19 Pedal) and 89 in the Windows package's Gear (quick build: the 73 upstream machines, ReBuzz's own and the Neo ones). `docs/machine-health.md` has them all.
- **Notes.** Apart from the three Neo synths, no generator in the package plays a MIDI note: every Pedal synth and every native one takes notes only in its Note track parameter. Before U1, `notes_write` left all of them silent. (The Pedal synths take MIDI notes too since §12.15.)
- **Pitch conventions.** Most pitched generators play A4 at 440 Hz as written (offset 0). Three don't:
  - **FSM Infector** and **kibibu Green Milk** play an octave low (offset +12);
  - **KrimZon AY-3-8910** plays 13 semitones high (offset −13).
  `notes_write` corrects all three through their drafts. Juno106 is 11 cents flat (its analog drift).
- **Unhealthy** (listed last by `gear_list`, warned about by `machine_add`; U2 measured them again parameter by parameter, below):
  - **Pedal Plate** diverged at its default Decay (+26 dBFS on noise). Its modulated all-pass was fixed in P3 (studio-mvp.md S2), and it profiles clean since (a 2.5 s tail; typed "delay", a U2 check for reverb vs delay);
  - **Jeskola Flanger** runs away with every parameter at its minimum or at its maximum, **Jeskola Bass** and **Joachims Hyperion** at their maximum;
  - **Info** doesn't load headless.
- **The server bug it found.** Jeskola Bass 2 and 3 and Joachims Neptune made `render` fail ("infinity cannot be written as JSON"). Fixed: levels and loudness write `"inf"`, and every tool result replaces non-finite numbers.
- **Chords** through tracks are heard on Juno106, FM, Infector and the AY-3-8910. **Pedal M1** and **Pedal Tracker** play one voice of a chord: tier 3's multi-track note workaround.
- **Silent at the defaults**:
  - Pedal M1, Tracker and Matilde Tracker2 until wave slot 1 has a wave; Jeskola Tracker and Polac VSTi even then (a wave or plugin to choose);
  - Rout 909, Jeskola Noise Generator, Trilok and Intoxicat ACloud15, which have no Note parameter (triggers: U2);
  - the audio inputs, MIDI output and controllers, as expected.
- **Effects** type as expected: delays (Jeskola Delay, Cross Delay, NiNjA dElaY, FSM PanzerDelay, Joachims Neptune), reverbs (Jeskola Reverb, Stereo Reverb, Raverb, Neo Verb), choruses and flangers as modulation, Neo Drive and Jeskola Distortion as distortion. Latencies: Neo Limiter 99 samples (lookahead), Neo Drive 62 (oversampling), Pedal Gate 37.
- **Parameters.** 349 of the Pedal machines' 361 parameters got a mapping, 25 of them from descriptions; 580 of the Windows machines' 629. The rest have texts that say nothing (Drum Matrix's twelve "-" parameters).

**Left for U2** (§12.7), and where each went (§12.12, U2 in depth):
- pitch where the analysis hears none at the defaults (Jeskola Bass 1–3, Oomek Aggressor 303, Pedal FM): a harmonic sieve on A2, A3 and A4, **built**;
- triggers for generators without a Note parameter (Rout 909, Jeskola Noise Generator, Trilok): **built**, through `automation_write` on played track parameters; choosing a wave or plugin (Jeskola Tracker, Polac VSTi): **left**, a person's choice;
- per-parameter sweeps and suggested roles: **built**;
- swept-sine and impulse responses (a test-signal source): **built**, the profiler's own (a stepped sweep, a click, level steps from Neo Poly, read on both sides);
- velocity response and envelope length: **built**;
- denormal slowdowns: **built** (`render`'s `cpu`);
- whether `Work` reports silence: **left**, the engine doesn't keep what `Work` returns, and keeping it is an engine change;
- hiding unhealthy machines from `gear_list` by default: **built** (`include_unhealthy`); the Pedal check now expects the two it makes unhealthy left out and named;
- `midi_play` into a tracks-only machine (it would set the Note parameter live): **left**, a live-input feature rather than a profile.

**U2, health first** ([machine-safety.md](machine-safety.md) §3.1, §3.4, built 2026-11-03). The "unhealthy" above became three categories, from a level test (4 s renders of the machine alone, noise into effects, A4 held on generators) at the defaults, with every parameter at each end at once, and each parameter alone at each end for machines that misbehave and effects with a feedback, resonance, gain or Q parameter:

- **Diverging** (unhealthy): NaN or infinity, above +60 dBFS, or growing (the last second 6 dB over the second, and still growing over 30 s). Where one parameter's end causes it, a bisection finds the edge, and the draft's parameter gets `safe_range` (`min`, `max`, `min_text`, `max_text`, `reason`). With the engine guard on, `render` lists such a machine as diverged and the profiler counts that.
- **Hot** (a warning naming the setting): above 0 dBFS, steady. An end where the loop never decays (feedback at ±100 %: noise piling up 2 dB or more per doubling of time over 30 s) gets a `safe_range` too, and stays hot.
- **Needs a window** (`status: needs_window`, its own): a managed machine whose construction fails for UI reasons. `gear_list` says it works in ReBuzz on Windows with its window.

The server keeps agents inside safe ranges: `param_set` and `automation_write` refuse values outside one with the reason unless `allow_unsafe: true`; `machine_describe`, `gear_list` and the machine manual show them; `machine_add` warns only for diverging gear. Found (with the guard on): Jeskola Flanger diverges at +100 % feedback (at −100 % it is hot and settles), Joachims Hyperion is hot per band, Jeskola Bass hot (Resonance); Info failed on a theme file missing under the Default theme, not for want of a window, and is healthy since the file was added; Joachims Neptune (Threshold at −100 dB) diverges, and so did Pedal Plate (Feedback above 78) and Neo Verb (High damping below 1.17 kHz), both since fixed in their source (machine-safety.md §3.1); Jeskola Delay never decays at −100 % feedback; FSM WahPro2 put out infinity once and varies by instance. Tests: `tools/test_machine_infer.py` (levels, never decays, window, sweeps, edges), `DraftManifestTests` (safe ranges, refusal, statuses) and `tools/headless-manifests.py` (the tools end to end, `automation_write` included).

### 12.9 U3 as built

**The 25.** Chosen for what an agent reaches for in each role, for what the repo's 37 Buzz songs use, and for the evidence there is to curate from; none diverges, and where U2 measured a problem a safe range or a quirk covers it.

| Role | Curated | Evidence |
|---|---|---|
| Synths | Pedal Juno106 (poly), Pedal SH101 (mono), Pedal FM (4-op FM), Pedal M1 (samples), FSM Infector (supersaw poly) | Pedal sources and READMEs; Infector's source and its HTML reference guide |
| Bass and drums | Jeskola Bass 3, Oomek Aggressor 303, FSM KickXP, Rout 909 | Aggressor's and KickXP's sources; Bass 3 and Rout 909 are closed: their own texts, and the repo songs' saved parameter tables |
| Space | Jeskola Reverb, Pedal Plate, Jeskola Delay, Jeskola Cross Delay, FSM PanzerDelay | Plate's, the delays' sources; Reverb is closed |
| Tone and dynamics | Jeskola EQ-3, Pedal EQ, Jeskola Filter, Pedal Filter, Pedal Comp, Oomek Masterizer, Oomek Exciter | Pedal, Oomek sources; EQ-3 and Filter are closed |
| Colour and texture | Jeskola Distortion, Joachims Mars filter 2, Pedal Chorus, Pedal Drum Matrix | Distortion's, Pedal sources; Mars's HTML documentation |

Song use, of machines the package has (`tools/song-roundtrip.py`'s BMX reader over the 36 `.bmx` and one `.bmw`; 16 are bmxplay demos with its own built-in machines, and most of the rest use machines the package doesn't ship): Rout 909 in 6 songs, Jeskola EQ-3 and Oomek Exciter in 3, Jeskola Bass 3, Oomek Aggressor 303 and Jeskola Noise Generator in 2, and once each the Jeskola Reverb, Delay, Cross Delay, Distortion, Filter, Filter 2, Flanger, NiNjA dElaY, Stereo Reverb, Wave Shaper and Tracker, FSM Infector, PanzerDelay and PhatMan, and Oomek Masterizer. Left out: Jeskola Flanger and Joachims Neptune (they diverge), Joachims Hyperion (Neo EQ's graphic mode is its successor), Jeskola Noise Generator and Trilok (they play only from trigger columns the tools can't write, as Rout 909 does, which got in on its use), the trackers (Pedal M1 covers samples), and what the Neo machines already do better (Pedal MComp, Gate, Gain, Shaper).

**What curation adds** (the draft's format and place, `Machines/Manifests/<Generators|Effects>/<Gear>.machine.json`, with `"status": "curated"`, so the package and `AppGear.targets` copy it as they did the draft):

- `summary` (what it is and what it's for), `quirks`, `evidence` (repository files with what each shows, the profile, the machine's own texts) and, for a generator, `notes` where the source settles what the profiler couldn't hear (Pedal FM and Oomek Aggressor read as unpitched on their default patches; their sources play 440 · 2^((n − 69)/12), as the measured Pedal SH101 does);
- per parameter: `plain_name`, `role` (from `ManifestRoles.Known`, 147 used: `filter.cutoff`, `env.release`, `mix.wet`, `drive`, `delay.feedback`, `eq.lowmid.q`, `drum.level`…), a `group`, a plain-English `description`, and a mapping that is exact: from the source's formula where there is one, else the profiler's fit to the machine's texts; none read from a description alone. `own_texts: false` marks a parameter whose own texts are bare numbers or wrong: the manifest formats its values instead (140 of 409);
- U2's safe range kept (Jeskola Delay's Feedback from −98.4 %); none tightened, as no new measurement called for it;
- parameters the draft left out, from the source: Pedal Drum Matrix's twelve Char and Mode parameters (texts that change with the slot's effect) and FSM PanzerDelay's five pans and spread ("50% L" texts the inference didn't read).

For example, Pedal Juno106's `VCF Freq`: the draft had raw 0–127 with no unit (bare texts); curated it is "Filter cutoff", `filter.cutoff`, 30 Hz – 16 kHz exponential (`DspMath.ParamToHz` in its source), so `param_set(changes=[{"param": "Filter cutoff", "value": "800 Hz"}])` lands exactly and `machine_describe` shows its default as "4.21 kHz", and its quirks say one envelope drives filter and amp, voices drift 11 cents (profiled), and the chorus is what makes it stereo.

**The server.** `MachineManifest` reads the curated fields (`Quirks`, `Evidence`, `IsCurated`; per parameter `PlainName`, `Role`, `OwnTexts`); `Quality` says "curated", and `exact_units` holds as for generated manifests.
- A machine's parameter finds its entry by name and, where names repeat (FSM Infector's " - Decay" for filter and amp), by index (`FindAt`): before, both took the first entry's mapping.
- Curated entries take the machine's own texts first, as drafts do ("off", "1/8"), except where `own_texts` is false: then `machine_describe`, `param_get`, `param_set` and `automation_write` read and write through the mapping (Pedal Comp's Attack at raw 10 shows "10 ms", not the "0.1 ms" its own descriptor prints: raw ÷ 200).
- `param_set` and `automation_write` take a plain name for a parameter ("Filter cutoff"), when no parameter has that name.
- `machine_describe` adds each parameter's `plain_name` and `role` and the gear's `quirks`; `gear_list` adds `quirks` and lists curated gear with the exact ones. Units: `ticks` is a unit `param_set` reads.

**Manuals for every machine with a manifest.** `rebuzz://machines/<slug>/manual` was already registered for every installed gear with a manifest; the manual now says what a draft or curated manifest knows: quirks; how it takes notes (Note track column or MIDI, the pitch offset `notes_write` applies, voices); every global and track parameter (curated: the plain name in bold, the role, the description); the profile's health; safe ranges; how to write values (track parameters with their track); and the sources. A draft's says it is one. `guide(machine=…)` serves it for any installed gear and lists them all in `manuals` (with "did you mean"). No curated "how it sounds" pages were written for the 25: a page needs listening, and the manifest's evidence doesn't reach that far.

**The pipeline.** `tools/machine-profiler.py --merge`/`--repo` keeps a curated manifest when it brings a new profile (`machine_infer.recurate`): profile and health from the new run, safe ranges added or tightened by it (never widened), the curation as it is, and a warning for each parameter the new profile names or ranges differently, a range it no longer finds, or a different pitch convention. The curation was written from the drafts and the evidence by hand; the manifests are the source of truth.

**Tests.**
- `ReBuzz.AgentTests/CuratedManifestTests.cs`: 25 curated and the rest drafts, and every machine the package profiled (`machine-profile.json`: the Windows package's Gear and rebuzz-headless's) has a manifest, generated or in `Machines/Manifests`; every curated manifest exact: a summary, quirks, evidence whose repository files exist, a plain name, role from the vocabulary and description per parameter, plain names unique and not another parameter's name, no draft confidence, choices matching the range, tables covering it in order, units `param_set` reads, safe ranges inside the range holding the default, and each end and default printed as the manual does coming back through `param_set` unchanged; a manual for every manifest (every parameter in a table, folded families aside; quirks, notes, health and sources where they apply).
- `GuideTests`: every parameter a guide, page or manual quotes, now with the curated manuals, exists on its gear by name or plain name with its value in range (over 400 curated values checked); `guide` lists and serves drafts' and curated manuals.
- `DraftManifestTests`: the curated fields, `FindAt` with repeated names, committed manifests being drafts or curated.
- `tools/test_machine_infer.py`: `recurate` keeps the curation, takes the measurements, never widens a safe range, keeps a curated pitch convention and reports what changed.
- `tools/curated-check.py` (Fork CI on rebuzz-headless, the package job on `ReBuzz.exe`): each curated manifest against the machine itself, its parameters' indices and ranges, and wherever it keeps the machine's texts, the texts agreeing with the mapping to a step of their last digit (some machines truncate) or 1 %: the 10 Pedal manifests on Linux (127 parameters), the 15 natives on Windows (142).
- `tools/headless-manifests.py`: curated Pedal machines in `gear_list` and `machine_describe` (plain names, roles, quirks), a plain name in `param_set`, Pedal Comp's real values, the manuals of a curated and a draft machine, a manual resource for each; `ReBuzzTests/AgentHeadlessTests.cs` (Windows): every machine in `ReBuzz.exe`'s Gear has a manifest and a manual resource, the curated natives are exact with quirks, and Jeskola Delay takes a plain name.

**Found.**
- ReBuzz feeds a mono native effect left + right summed (`NativeMessage.cs`), which is why every mono native effect measured about +6 dB at unity (Jeskola Delay +6.2, EQ-3 +6.0, Filter +6.0): a quirk on each.
- Pedal Comp's texts for Attack, Release, Lookahead and Makeup Gain are wrong (ReBuzz's ms and dB descriptors scale the raw value, which the machine uses as ms and dB): its draft read Attack as 3 µs – 1 ms and Makeup as −inf … 0 dB.
- Jeskola Distortion's default clamp is 200 % of full scale, which is why it is hot at its defaults (+6 dBFS).
- FSM Infector's repeated names (" - Decay" for the filter and the amp, " - PWM Rate" for both oscillators) matched their manifest entries by name alone, so each took the first; the drafts' mappings happened to agree, the curated names and roles don't, so entries now match by index too (`FindAt`). Infector, PanzerDelay, the Exciter and the Masterizer assume 44.1 kHz in places (quirks).
- Pedal EQ's frequencies and Q move in steps (its source's tables; its README's Q table is out of date): exact tables.
- The check against the natives' own texts on Windows found what the drafts had smoothed over: Jeskola Reverb's levels are 20 × log₁₀(raw ÷ 4096) dB (the draft's table jumped from −inf at raw 0 to −41 dB at 36: now dense at the bottom); FSM Infector and Rout 909 truncate their texts ("%d%%" of raw × 100 ÷ 240); Oomek Exciter's and Masterizer's frequency texts read 1 Hz until the engine has run (they divide by its sample rate).

**Left.**
- Thin evidence: the closed machines (Jeskola Reverb, EQ-3, Filter, Bass 3, Rout 909) have only their texts, so Filter's cutoff, resonance and bandwidth, Rout 909's tunes and decays and EQ-3's Dry Thru stay bare scales, and Bass 3's pitch convention is unmeasured; internal scales without units in the sources stay raw too (Aggressor's cutoff, resonance and decay; KickXP's decay rates and slopes; Infector's PWM rates and inertia).
- Columns the tools can't write: Rout 909's triggers, KickXP's Trigger, the Aggressor's slide and accent, Bass 3's volume, length and slide (non-state track parameters).
- Jeskola Filter's Resonance is hot near its top (+53.8 dBFS): a safe range needs the bisection U2 does only for diverging and never-decaying ends.
- Curated "how it sounds" pages for these machines, and factory presets (U4).

### 12.10 U5 as built

**Mappings are data.** One file per pair, `Machines/Manifests/Successors/<old> to <new>.successor.json` (format `rebuzz-successor/1`), embedded in `ReBuzz.Agent` and read by `SuccessorLibrary`; a new successor adds its file (Neo Ensemble's four did, Neo EQ's graphic mode Joachims Hyperion's, Neo Delay's four, Neo Sampler's Pedal M1, Neo Drums' Rout 909 and FSM KickXP, and Neo Filter's four). Roles alone weren't enough: the Neo manifests have none, and a role says what a parameter is for, not how two machines' levels, curves and topologies line up (Jeskola Reverb sets dry and wet apart where Neo Verb crossfades; Jeskola Distortion's threshold is a drive). A mapping has:

| Field | What |
|---|---|
| `set` | The successor's settings first, as `param_set` takes them (`"Mode": "Plate"`) |
| `rules` | Each old parameter's way to the new one, in real units. `unit` (the same quantity, times `scale` and another parameter's value with `times`, times 2 to the power of a weighted sum of others with `octaves_from` (Pedal M1's cutoff, which its key tracking centres an octave lower and its velocity moves round the middle), plus `offset`), `db` (a fraction or linear gain to dB, `zero_when` a switch is on), `inverse_db` (a threshold to the gain that brings it to full scale), `choice` (by name, or by raw value), `range` (a bare scale laid on a unit range, linear or log), `sum` (EQ-3's bands relative to its mid), `levels_to_mix` (separate dry and wet levels to a crossfade, the output connections making up the level it takes away; pattern events keep that make-up), `clip` (threshold and clamp to drive into a hard clip and an output level), `bypass` (a bypass parameter to the machine's bypass; `invert`: an enable switch), `sweep` (a modulated delay's shortest and longest delay, weighted sums of old times, to the delay at the sweep's geometric centre and its depth in octaves either side; a sweep wider than the new machine reaches keeps its top), `dry_wet` (dry and wet amplitudes, or one wet/dry mix, or with `levels_db` dry and wet levels in dB, to a crossfade and an output gain, the wet weighted by how the two machines sum their voices; an inverted dry or wet keeps its level, not its polarity, with a note), `graphic` (a graphic EQ whose bands overlap and add onto sliders at other centres that the new response passes through: the old bands' curve, as constant-Q bells or, with `rate`, the Audio EQ Cookbook's peaking filters at that rate (`alpha_max` caps their alpha), `bandwidth` octaves between half-gain points, read at each slider's centre; where it leaves the sliders' range it moves as a whole and `output` takes the move with the old `gain` (beyond the output's reach, the output connections); a curve wider than the range keeps its middle, its ends clamped with a note; an optional `highpass` corner, a second-order Butterworth, goes in after the curve is placed), `delay_time` (a delay's length in its unit, ticks, 1/256 ticks, samples or ms, the unit a choice whose names `units` maps or a fixed `unit_kind`: a length in ticks that is a note value becomes the new machine's `sync` and `feel` (`divisions` in beats, `feels` as factors), so it follows the tempo as the old one did; any other a free `time` at the song's tempo and rate (ticks as Buzz's whole samples per tick), which `time` takes either way; a `length_right` becomes `offset_param`, the right line against the left; the converter knows the song's tempo, ticks per beat and rate: `SuccessorTiming`), `from_db` (a level in dB to an amplitude or fraction, a feedback in dB to one in %), `filter` (a filter type and cutoff to a low cut and a high cut: per old type `types` says which the cutoff becomes, `high_cut`, `low_cut`, `band` (both, an octave apart round it) or `none`, the other set to `low_off` or `high_open`; a type left out has no counterpart, with a note), `key_zones` (a keyboard split, Pedal M1's oscillators: zone 1 below the splits, each higher zone whose wave is set from its split up, a higher one overriding a lower, onto the new machine's zones' wave, lowest and highest key, `split_offset` between the two machines' note numbers; `enable` and `enable_when`: a choice that switches the whole split on, OSC Mode), `interval` (the semitones from one old frequency down to another, `over`, plus `octaves_from`, times `scale`, plus `offset`: FSM KickXP's Start down to its note, a fitted sweep), `grid` (a table over two old values, `x` and `y` at the points `xs` and `ys`, one row of `values` per y, read bilinearly, in the logarithm with `curve` "log", held at its edges; without `y` a table over one: a classic filter's cutoff and resonance onto the new one's cutoff, Q and level, fitted point by point where no formula separates them). Any rule takes `when`: one condition or a list, each an old parameter that is one of `is` (choice names or raw values) or lies from `from` to `to`; while one doesn't hold the rule sets nothing, so rules on one output can share it out (a table per filter type; Pedal Filter's LFO rate or its division, by Tempo Sync) |
| `describe` | How old parameters read where their manifest doesn't say: non-state columns (Jeskola Bass 3's Volume), or a measured table (Pedal Plate's Decay in seconds; Joachims Hyperion's Bandwidth in octaves between half-gain points and its Highpass corner, neither what its display says) |
| `input_gain_db`, `output_gain_db` | Added to the successor's connections: +6.02 dB in for a mono native effect, which ReBuzz feeds left + right summed (out instead for Neo Delay's, so its line keeps the input's level under its saturator's knee) |
| `notes` | For generators: the note, slide, slide length, note length and volume columns (a track parameter where the machine also has a global one of that name: Pedal M1's Volume); tracker notes become notes, slides glides (the new machine's glide time set where each starts), volume velocity (`volume_start`: a volume column that holds no value, FSM KickXP's Trigger, starts at that). Drum machines: `triggers`, hit columns that become notes on a pitch each (Rout 909's BD Trigger … RC Trigger onto Neo Drums' General MIDI keys), at a velocity or by their value; `fixed_pitch` puts every note on one key (KickXP's, the kick); `rules_read_note`: the rules read the song's most common note as the note column's value for the settings, and each note in the patterns moves what they set as it plays (KickXP's note is the floor its pitch falls to, so it tunes the kick, hit by hit); a JSON null `note`: no note column (Rout 909) |
| `unmapped` | Old parameters with no counterpart, each with why |
| `tolerance` | Loudness (dB), third octaves (dB, level-matched, over a frequency range, ignoring bands 40 dB under the loudest), what was measured, and why not where it misses |
| `remarks`, `evidence` | What changes by design ("Neo Verb is stereo"), and what the mapping rests on |

`SuccessorConverter` takes each old raw value through its manifest (or `describe`) to a value, the rule to the new value, and the new manifest to a raw value; a value that lands more than 0.3 dB, 3 % in frequency or time, or 1 % of a level away gets a note ("High damping: 900 Hz is out of its reach; set to 1.00 kHz").

**What moves** (`ReBuzz/Agent/MachineReplace.cs`, one undo step):

- the successor in the old machine's place: its position, its name (or a given one), mute and bypass, the same connections in and out with their gain, pan and channels (plus the mapping's gains, at most +12 dB);
- its settings, converted;
- its patterns: each becomes the successor's pattern of the same name and length; the old pattern's parameter columns (the engine's automation columns, and the pattern editor's tracker columns, where the k-th column of a track parameter is track k) become engine-played automation columns of the successor's parameters, event by event with the other inputs as they stand then, ramps kept; notes (tracker notes through `notes`, MIDI notes as they are) go in through `MachineNotes`, in the successor's pitch convention;
- the arrangement: each old sequence gets one of the successor's beside it with the same events (patterns by name, parts, gains, clip ids); the Session View's tracks take the old ones' slots and colours, and the successor's clips the old clip settings, return, cue and crossfader side (`SessionLayout.Adopt`);
- the old machine is deleted, or kept, muted, with `keep_old`. A failed call takes back what it did.

ReBuzz's own Replace (the machine menu's, `ReplaceMachineAction`) isn't used: it makes the sequences again empty (its event copy is commented out) and moves no patterns.

**The comparison.** The song from its start (60 s at most; 30 s from the menu), or `from`/`to`/`section`, renders before and after. The machine's output (its first connection) is probed and its inputs tapped, and `AudioReport.Tolerance` reads each side against its own input: loudness and each third octave of output over input, after against before. Without that, a source that plays differently each render (Neo Poly's noise, free-running oscillators) moved the low third octaves by ±1.5 dB between two renders of an exact conversion (Pedal Gain at 50 %). The master is compared too (`song`), with `compare`'s reads as `differences`. The verdict is `within_tolerance`, and `why_not` (the mapping's, or the measured reads) when it misses. A render plays the patterns, which move the old machine's parameters: its state is put back after the first render, so undo gives back the song as it was.

**Pairs.** Measured on Neo Poly playing chords (its saw) or noise into the effect, each side against its input:

| Pair | Tolerance (loudness, bands) | Measured | Where |
|---|---|---|---|
| Pedal EQ → Neo EQ | ±0.2 dB, ±0.5 dB, 20 Hz – 20 kHz | 0.0 dB; every band 0.0 dB (both are the RBJ cookbook's biquads: Pedal EQ's shelves at slope 1 are Q 0.7071) | Linux |
| Pedal Gain → Neo Utility | ±0.1 dB, ±0.2 dB | ±0.02 dB; bands 0.0 dB | Linux |
| Pedal Comp → Neo Comp | ±1 dB, ±2 dB, 40 Hz – 16 kHz | −0.7 … +0.4 dB; bands within 0.7 dB (with Neo Comp's Auto release on: Pedal Comp's release is program-dependent; off, Neo Comp came out 1.3 dB quieter) | Linux |
| Pedal Plate → Neo Verb (Plate) | ±1.5 dB, ±6 dB, 50 Hz – 12 kHz | −0.8 … +1.5 dB; bands within 2.6 dB with the dry sound in, 6 dB wet only (the lowest band; a saw's harmonics, where the two plates' resonances fall differently) | Linux |
| Jeskola Reverb → Neo Verb (Room) | ±1.5 dB, ±6 dB, 63 Hz – 10 kHz | at its defaults (dry 0 dB, reverb −14.5 dB, 1 s) −1.4 dB, bands within 2.2 dB; as a send (wet only, 2.5 s) on noise −1.3 dB, within 4.2 dB. Not within: wet only on chords (−2.5 dB; 20 dB at 160 Hz, where the two tanks' resonances meet a chord's harmonics differently) and the early reflections alone (−0.9 dB; −8 dB at 160 Hz) | Windows |
| Jeskola Distortion → Neo Drive | ±2 dB, ±6 dB, 80 Hz – 12 kHz | at its defaults +0.9 dB, bands within 3.7 dB; as the Oomek Aggressor demo sets it +1.7 dB, within 2 dB (below 80 Hz the two make different difference tones from a chord); in that song +0.9 dB, bands within 0.5 dB | Windows |
| Jeskola EQ-3 → Neo EQ | ±1.5 dB, ±5 dB, 50 Hz – 12 kHz | low +6 dB, high −6 dB: −0.9 dB, bands within 3 dB. Not within: Masterizer_test.bmx's setting (mid +17.6 dB against high −24 dB), −4.4 dB and 13.4 dB at 250 Hz: a band-split EQ's corners move with its gains, and shelves can't reach bands that far apart | Windows |
| Jeskola Gain → Neo Utility | ±0.2 dB, ±0.3 dB | +0.03 dB; bands 0.0 dB | Windows |
| Jeskola Bass 1, 2, 3 → Neo Poly | ±3 dB, bands not checked | a bass line's notes in tune, its level: Bass 3 −0.3 dB (sine), +1.6 dB (its default saw); Bass 2 −0.3, +1.2 dB; Bass 1 +0.5 dB. Jeskola Bass's cutoff is measured (the brightness of a held A2 at five cutoffs, matched on Neo Poly's ladder): Elak_SVF.bmx's Jeskola Bass, cutoff low, −1.3 dB (−11.2 dB with the cutoff assumed). Not within: CyanPhase_DTMF-1_Demo.bmx's Bass 3 −6.7 dB (Bass 2's and 3's cutoff, envelope and decay are assumed) | Windows |
| Pedal Chorus → Neo Ensemble (Chorus, 4 voices) | ±1 dB, ±2.5 dB, 63 Hz – 12 kHz | at its defaults within 0.1 dB, bands within 0.8 dB; deep and wide (6 ms on 20 ms, Spread 100 %, Mix 60 %) within 0.1 dB, 2.2 dB; Tone 6 kHz, or wet only at Spread 0 %, within 1 dB. Not within: wet-heavy mixes (80–100 %) with Spread up, up to 7 dB in one low third octave (125 Hz): Pedal Chorus's channels hear its panned voices at different levels, Neo Ensemble's hear theirs alike | Linux |
| FSM Chorus2 → Neo Ensemble (Chorus) | ±1.5 dB, ±4 dB, 63 Hz – 12 kHz | its sweep, read from its input and output, is its source's (8.50–10.37 ms against 8.51–10.39); at its defaults −0.9 dB, bands within 3.1 dB (noise; chords −3.5 dB at 250 Hz: its taps sit 60° apart, Neo Ensemble's voices evenly); Fatness 1 −1.1 dB (noise), 0.0 dB (chords), within 1.2 dB; Fatness 6 −0.7 dB, 3.0 dB. Not within: a deep sweep (4.3–9.3 ms), −6.4 dB at 80–100 Hz. On noise Neo Ensemble is about 1 dB quieter above 2 kHz: the binary is brighter than its source predicts | Windows |
| Jeskola Chorus → Neo Ensemble (Chorus, 1 voice) | ±1.5 dB, ±3 dB, 63 Hz – 12 kHz | measured first (`DelaySweep`): a triangle from min. delay up by depth × 9.92 ms, its LFO at half what freq says, mono, dry and wet straight in amplitude. On noise: at its defaults −0.0 dB, bands within 0.5 dB; wet 50 %, 10 ms, depth 127 within 0.5 dB; 5 ms, depth 64, 2 Hz within 2.2 dB. Not within: on chords at its defaults, 7.3 dB at 100 Hz (a few harmonics meet the comb's notches for a different share of a straight-line sweep than of one in octaves) | Windows |
| Jeskola Flanger → Neo Ensemble (Flanger) | ±1.5 dB, ±3 dB, 63 Hz – 12 kHz | the same sweep as Jeskola Chorus's, min. delay in tenths of a ms. On noise, wet out positive: 1–6 ms −0.0 dB, bands within 1.6 dB; 2.5–12.4 ms with feedback +50 % within 1.4 dB; from no delay, wet 50 %, within 1.5 dB. Not within: its defaults (wet out −100 %: no counterpart for the polarity, reported; 3.7 dB at 63 Hz) and chords (up to 10.5 dB in one third octave, as for the chorus) | Windows |
| Jeskola Delay → Neo Delay (Stereo) | ±0.5 dB, ±1 dB, 63 Hz – 12 kHz | its echoes read from bursts (`DelayCalibration`): its source's, every 3 ticks at −2.5, −8.5, −14.5 dB for a centred sound. At its defaults −0.0 dB, bands within 0.3 dB (chords 0.5 dB); 120 ms, −60 %, wet only −0.0 dB, within 0.4 dB. Not within: a long build-up (a free 5 ticks, +80 %, wet 75 %: 1.3 dB at 100 Hz on noise, 2.2 dB at 400 Hz on chords; a free time lands on Time's 0.085 % step, and the build-up reaches the saturator), wet only on chords (1.1 dB at 630 Hz) | Windows |
| Jeskola Cross Delay → Neo Delay (Ping-pong) | ±0.5 dB, ±1 dB, 63 Hz – 12 kHz | measured a ping-pong (its input into the left line only; the repository's rewritten source feeds both). At its defaults +0.0 dB, bands within 0.1 dB (chords 0.2 dB); 2 and 3 ticks, 60 % +0.0 dB, within 0.2 dB (chords the same) | Windows |
| Jeskola NiNjA dElaY → Neo Delay (Stereo) | ±1 dB, ±2 dB, 63 Hz – 12 kHz | its filters measured on one echo: Butterworth high- and low-pass at f. cutoff onto the low cut and the high cut (two real poles: a softer knee). At its defaults (high-pass 550 Hz, 75 %) −0.1 dB, bands within 0.5 dB (chords 1.3 dB); low-pass 2 kHz, 60 % −0.3 dB, within 0.6 dB (chords 1.0 dB); high-pass 200 Hz, 4 ticks +0.0 dB, within 0.8 dB (chords 1.3 dB). No counterpart: its band-pass (about 100 Hz wide) and band-reject | Windows |
| FSM PanzerDelay → Neo Delay (Stereo, Cross with FB PingPong) | ±1 dB, ±2 dB, 63 Hz – 12 kHz | its filter measured on one echo (about twice its Cutoff: it runs over left and right through one state); its default wet pans mirror the echoes, which Neo Delay doesn't (the comparison can't see sides). At its defaults −0.2 dB, bands within 0.7 dB (chords 0.7 dB); FB PingPong, 2 and 3 ticks, FB −3 dB −0.2 dB, within 0.7 dB (chords 1.2 dB); LP at 1876 Hz +0.0 dB, within 0.5 dB (chords 0.7 dB) | Windows |
| Pedal M1 → Neo Sampler | ±1 dB, ±2 dB, 100 Hz – 12 kHz | measured first by hand, then in `tools/headless-replace.py`: a line of four notes (A3 at the wave's root, A4, E3, A2) from a band-limited saw (A3) and a square (A4) in the song's wavetable. At its defaults −0.2 dB, bands within 0.2 dB; a key zone (the square from F#3) with both oscillators (OSC2 an octave up at half level) −0.1 dB, within 0.1 dB; its envelopes and filter (2.5 kHz with its envelope) −0.5 dB, within 0.7 dB; key tracking 100 % at 1 kHz −0.2 dB, within 0.2 dB; the track Volume at 64 −0.1 dB (both 3.3 dB down). Its time constants become Neo Sampler's times (attack ×3.05 to full, decay and release ×6.91 to −60 dB), its splits key ranges 12 notes lower (its MIDI 60 is Buzz C-4). Not within: resonance (25 %: −1.9 dB, 3.2 dB at the cutoff; 50 %: −2.4 dB, 4.5 dB): Pedal M1's 2-pole SVF against the ladder's 2-pole output, which loses bass and passes more above the cutoff as it resonates. Its LFOs and portamento have no counterpart; Neo Sampler plays every track's note where Pedal M1 played one voice of a chord. On Windows (`SamplerCalibration`, a full package run) the same within 0.05 dB: −0.12 dB and 0.2 dB, −0.14 dB and 0.1 dB, −0.54 dB and 0.7 dB; resonance 25 % −1.82 dB, 3.3 dB at 4 kHz | Linux, Windows |
| Joachims Hyperion → Neo EQ (Graphic) | ±1 dB, ±2 dB, 31.5 Hz – 16 kHz | its bands measured first (`HyperionCalibration`): the Audio EQ Cookbook's peaking filters at 44.1 kHz, Bandwidth twice what it displays, alpha capped at 1.5 (the 20 kHz band), adding in dB; that model within 0.3 dB of every measurement from 40 Hz up. Its curve is read at the 31 ISO centres (machine-safety.md §4: 30 bands onto 31). On noise: a smile at the default Bandwidth −0.03 dB, bands within 0.2 dB; with Highpass 22 Hz within 0.1 dB; every band +3 dB (+21.5 dB summed, Output taking the move) with Input gain −12 dB within 0.1 dB; a zigzag ±6 dB at Bandwidth 0.3 within 0.3 dB; one band +12 dB at the narrowest Bandwidth within 1.6 dB (reported). Not within, modelled: narrow boosts past about +13 dB (+18 dB at Bandwidth 0.2: 3.2 dB), curves spanning more than the sliders' 36 dB, a Highpass above about 70 Hz | Windows |
| Rout 909 → Neo Drums | ±3 dB, bands not checked, 40 Hz – 16 kHz | its eleven drums measured first (`DrumsCalibration`, Windows: each alone at its defaults and at 0, 16, 32, 48 and 63 of each scale), Neo Drums' defaults voiced against them; its trigger columns become hits on the General MIDI keys at velocity 127, levels decibels, its scales the voices' units through the measured points, `Volume` +7.5 dB. A two-bar beat of all eleven written by `automation_write`: at its defaults +0.8 dB; at the ends of six scales with BD Level 64 −2.5 dB. The repo's songs: 909.bmx and 909_excite.bmx +2.8 dB, Oomek_Aggressor_demo.bmx +2.3 dB, Csi.bmx +1.7 dB; not within: Exciter/test.bmx +3.1 dB (the songs lean on the kick, whose energy Neo Drums carries further past the attack than the sample: its 20 dB fall at 125 ms against 65 ms); Masterizer_test.bmx not compared (as for its Jeskola Reverb above). Third octaves differ most at 40–50 Hz (11–16 dB: the samples' sub-fundamental thump) | Windows |
| FSM KickXP → Neo Drums | ±3 dB, bands not checked, 30 Hz – 12 kHz | its source read (`KickXP.cpp`: the pitch falls from Start towards End on a power curve, raised by Punch at first, and stops at the note), and its kick at A0–A3 and each parameter at its ends measured (`DrumsCalibration`). Notes and Trigger hits become kicks on C2, a Trigger value the velocity (Vel → level 50 %); the most common note tunes the kick and each note retunes it as it plays; the sweep fitted over Start, the note and Punch (1.6 st RMS), its time over T DecRate at A1; the level matched by loudness, `Volume` +12 dB. Eight kicks on A1 and a Trigger hit at half volume: at its defaults +0.0 dB; on A2 with Start 180, T DecRate 60, Buzz 0, A DecTime 60 −1.2 dB. Third octaves differ most at 1.6–2 kHz (9–13 dB: its buzz layer and thump against Neo Drums' click) | Windows |
| Jeskola Filter → Neo Filter (SVF, by type) | ±1 dB, ±2.5 dB, 50 Hz – 12 kHz | measured first (`FilterCalibration`, Windows): a Chamberlin state-variable filter, f = sin(π · Cutoff / 256) and q = 1 − sin(π · Resonance / 256), within 0.1 dB of that model in every type up to Resonance 116; per type a table over Cutoff and Resonance (`grid`) gives Neo Filter's cutoff, Q and level, fitted point by point by this comparison's measure (within 2.4 dB at and between the points), the input +6.02 dB. On noise: at its defaults (low-pass, open) +0.07 dB, bands within 0.6 dB; low-pass 64 at resonance 64 +0.03 dB, 0.8 dB; high-pass 48 +0.03 dB, 0.1 dB; band-pass 32 at resonance 96 +0.04 dB, 0.6 dB. Not followed: Bandwidth (measured: it widens the band-pass alone, up to 4.7 dB in its skirts at cutoff 64, 22 dB at 16), Resonance above 116 (a Q past Neo Filter's) | Windows |
| Jeskola Filter 2 → Neo Filter (SVF, by type) | ±1 dB, ±2.5 dB, 50 Hz – 12 kHz | the same Chamberlin filter on its own cutoff scale (0.0123 to 1, measured every 4 steps and between, its Type a bare 0–2). On noise: at its defaults +0.07 dB, bands within 0.6 dB; low-pass 64 at resonance 64 +0.07 dB, 0.9 dB; high-pass 48 −0.03 dB, 0.1 dB; band-pass 96 at resonance 110 (Q 41) −0.34 dB, 0.9 dB | Windows |
| FSM WahMan → Neo Filter (SVF LP) | ±1 dB, ±2.5 dB, 50 Hz – 12 kHz | its source's biquad (measured within 0.14 dB) fitted on a table over Cutoff and Resonance (within 2.2 dB); its LFO, a sine on the raw cutoff, Neo Filter's LFO free at the same rate and ±Modulation/96 octaves. With its LFO still: Cutoff 60 at Resonance 200 −0.17 dB, bands within 0.6 dB; Cutoff 200 at Resonance 0 −0.11 dB, 0.8 dB. Not within: its defaults (+0.41 dB, 2.6 dB at 10 kHz): the two free LFOs at different points of their cycle | Windows |
| Pedal Filter → Neo Filter | ±1 dB, ±3 dB, 50 Hz – 12 kHz | its source's TPT SVF (measured within 0.13 dB): the same modes but Comb, slopes and units; Q 1/(2R) and its resonance compensation onto Output by tables over Resonance; its LFO free or at the same division (`when` on Tempo Sync). At its defaults (low pass 2 kHz) +0.06 dB, bands within 2.8 dB (Windows +0.16 dB, 2.7 dB: the 10 kHz band, where its SVF at the base rate closes toward Nyquist and Neo Filter's, oversampled, keeps the analog slope); high pass 24 dB at 500 Hz, resonance 60 % −0.02 dB, 0.2 dB; band pass 800 Hz at 85 % +0.02 dB, 1.5 dB; with its LFO on 1/4 (Windows, reported) +0.13 dB, 1.7 dB. Not within: above about 2 kHz at 24 dB/oct, up to 5 dB in the top third octaves (modelled) | Linux, Windows |

The tests: `ReBuzz.AgentTests/SuccessorTests.cs` (every mapping parses and fits both manifests, every old parameter mapped or unmapped with a reason, the defaults convert with every value reachable (Jeskola Flanger's with its two notes); percent to dB, tables to Hz, levels to a crossfade with the dry path exact, threshold and clamp, a swept delay to its centre and octaves, dry and wet to a crossfade with an inverted one noted; pattern events and ramps, the make-up kept for events; tracker notes, slides, volume as velocity; a graphic EQ's summed bands read at other centres, the move to the output and the high-pass after it; the tolerance check), `tools/headless-replace.py` in Fork CI (Pedal EQ, Gain, Comp and Chorus within tolerance; successors named; a dry run changes nothing; the settings, connections, automation, arrangement and Session View track carry over; one undo restores the whole tool-read state, redo replaces again; `keep_old`; refusals), `ReBuzzTests/AgentReplaceTests.cs` in the package job (the native pairs on representative material, asserting the exit-check pairs, Neo Ensemble's and Joachims Hyperion's within tolerance, Jeskola Flanger's inverted wet reported, undo exact; the Basses with their notes, pitch and level; the repo's songs that use these machines, their arrangement kept), `ReBuzz.Studio.Tests/ReplaceMenuTests.cs` (the menu, one undo step) and the evaluation's `machine_replace` check. Three explicit tests hold the measurements behind the mappings and run by name (`test_filter`): `ChorusAndFlangerCalibration` (each closed chorus's and flanger's sweep through `ReBuzzTests/DelaySweep.cs`, which reads the delay from input and output over short windows by cross-correlation (checked on Neo Ensemble: 1.001–4.010 ms for 1–4 ms), and the pairs' comparisons on noise and chords), `NativeSuccessorCalibration` (the Basses' brightness against Neo Poly's cutoff) and `HyperionCalibration` (Joachims Hyperion's response on noise at 6 points an octave through `ReBuzzTests/FineTransfer.cs`, a Welch H1 estimate: one band at every Bandwidth step and several gains, neighbours, every band at once, zigzags, the end bands, Highpass and Input gain), and `DelayCalibration` (each classic delay's echoes, when, on which side and how loud, read from a 20 ms burst of noise by cross-correlation, centred and panned; NiNjA dElaY's and PanzerDelay's filters on one short echo through `FineTransfer`; and the four pairs' comparisons on noise and chords). The Neo Delay pairs are asserted in `NativeMachinesMoveOntoTheirSuccessorsWithinTolerance` (two settings each, on noise), and `SuccessorTests` checks lengths in ticks becoming note values (and the rest a free time at the song's tempo), the right length the offset, levels in dB a crossfade, and the filters' types and factors. Pedal M1 → Neo Sampler is asserted in `tools/headless-replace.py` (at its defaults, with a key zone and both oscillators, with its envelopes and filter); `SuccessorTests` checks its zones (a covered one off, OSC Mode switching an oscillator off), its times, the cutoff's octaves and its track Volume as velocity; `SamplerCalibration` (explicit, Windows) measures Pedal M1, Matilde Tracker2 and Jeskola Tracker playing a line from the song's wave against Neo Sampler, and the pair's comparisons. Neo Drums' pairs: `SuccessorTests` checks Rout 909's trigger columns becoming hits on the General MIDI keys and its scales through their measured points, and KickXP's notes and Trigger hits becoming kicks on C2 at their volume, its most common note tuning the kick and its fitted sweep; `DrumMachinesMoveOntoNeoDrums` (ReBuzzTests, a full package run) converts a two-bar beat on Rout 909's trigger columns (written by `automation_write`) and KickXP's kicks, asserts every hit moves and the defaults within tolerance, and moves the repo's six songs with Rout 909; `DrumsCalibration` (explicit) holds the measurements. Neo Filter's pairs: `SuccessorTests` checks the grid and its conditions (between points in the logarithm, held at the edges, a table over one value, a list of conditions with a range), Jeskola Filter's and Filter 2's types each reading their table, the top of Resonance held under self-oscillation until 128, Pedal Filter's Q, its compensation on Output and its divisions, FSM WahMan's table and its LFO in octaves; `tools/headless-replace.py` asserts Pedal Filter's three settings; `NativeMachinesMoveOntoTheirSuccessorsWithinTolerance` the four pairs on noise (the free-running LFOs reported); `FilterCalibration` (explicit) holds the measurements, printed compactly (the CI summary keeps 60 lines and 40 kB of a test's output, so each run measured what was left).

**The UI.** The machine menu offers "Replace with <successor>..." on a machine with a mapping, in the studio (`MachineMenus`) and in ReBuzz.exe (`BuzzGUI.Common.MachineSuccessors`, set by ReBuzz at start, read by `BuzzGUI.MachineView`). It runs `MachineReplace.FromMenu`: the same code, one undo step, the song's first 30 s compared, the summary in a message (and ReBuzz.exe's status line).

**Left.**

- The successor's patterns hold the converted data in engine-played columns, which play, render and read back through the tools (`automation_read`, `notes_read`) and show in the Session View's lanes; the pattern editor's tracker grid of the successor starts empty (the editor's own data format has no host-side writer).
- Jeskola Bass 2 and 3: their cutoff, envelope and decay scales are assumed (stated per rule; Bass 1's cutoff is measured); only loudness is checked, and a song that leans on Bass 3's filter misses (CyanPhase_DTMF-1_Demo.bmx, −6.7 dB). Measured on a held A2 with its default envelope, Bass 3's brightness runs from a centroid of 224 to 841 Hz over its cutoff, much darker than the assumed curve; with the envelope in play one static curve can't be fitted from that alone.
- Neo Ensemble's pairs match on noise and miss in single third octaves on chords, where a straight-line sweep (the closed machines') and one in octaves (Neo Ensemble's) put the comb's notches on a few harmonics for different shares of time. Jeskola Flanger's inverted wet (its default) has no counterpart. FSM Chorus2's binary is brighter on noise than its published source predicts (unexplained). At depth 0 Jeskola Flanger's and Chorus's delay doesn't hold still (it wandered over 10–23 ms with min. delay 5 ms; unexplained); Neo Ensemble's holds.
- Joachims Hyperion's pair reads its biquads at 44.1 kHz (at other rates its high bands move, graphic mode's don't), and graphic mode can't make a boost narrower than its sliders' spacing (Bandwidth 0.2 and 0.3, more than about +13 dB); its Bandwidth and Highpass are measured, not displayed, values (the pair's `describe`).
- The delays (Neo Delay's four pairs): one tap each (their first track: the others' columns write the same parameters, with a note); a classic delay's Max Delay attribute (1 s by default) caps its lengths and the mapping doesn't read attributes; a classic mono delay's echoes on both sides, Neo Delay's keep the input's image (a centred sound the same); PanzerDelay's mirrored echo pans and FB Spread are left out, and its filter's factors were measured at its default Resonance; NiNjA dElaY's narrow band-pass (about 100 Hz) has no counterpart. The comparison reads loudness and third octaves, so it doesn't see which side an echo is on: the sides were read from bursts (`DelayCalibration`).
- No mapping for Jeskola Tracker or Matilde Tracker2 onto Neo Sampler: each note chooses its wave in a column, and rows play effects (volume and pitch slides, retrigger, sample offset), where Neo Sampler's zones are a fixed map from keys to waves; a conversion would keep the notes but not their waves. Measured (`SamplerCalibration`, Windows): written a line of notes with a 16-bit saw in slot 1, neither tracker plays through the tools (why wasn't pursued: Jeskola Tracker's draft manifest has no track parameters, and Matilde Tracker2's profile, with its own test wave, heard A4), where Pedal M1 and Neo Sampler play it in tune.
- Neo Drums' pairs: one kit for Rout 909's eleven drums, so a song with two Rout 909s gets two Neo Drums; Rout 909's Shuffle has no counterpart (the hits land as written), nor its toms' Decay (measured: it changes nothing). KickXP's tracks are each a kick with its own settings and Neo Drums has one (track 1's); its sweep time is fitted at A1 (quicker above, slower below), its T DecShape, buzz and click fades and NoteDelay have no counterpart; a Trigger value above 100 % plays at velocity 127. Pedal Drum Matrix is a multi-effect rack for drums, not a drum machine: no pair.
- Neo Filter's pairs: Jeskola Filter's and Filter 2's tables are fitted at 44.1 kHz, where their cutoffs, a share of the song's rate, are 8.8 % lower than at 48 kHz (as FSM WahMan's, whose coefficients assume 44.1 kHz at any rate); Resonance above 116 is a Q past Neo Filter's (held at 99.4), and 128 a ringing without bound against Neo Filter's bounded self-oscillation; Jeskola Filter's Bandwidth, measured (it widens the band-pass alone, by up to 4.7 dB in its skirts at cutoff 64 and 22 dB at 16), is left out. FSM WahMan's Set phase (a jump of its LFO) and Inertia (a slew limit on its cutoff) have no counterpart, nor Pedal Filter's Comb; Pedal Filter's drive divides by a running peak before its tanh, Neo Filter's is on the level. Free-running LFOs (WahMan's at its defaults, Pedal Filter's) start at another phase than the successor's, so those comparisons are reported, not asserted. Joachims Mars filter 2 is a distortion (measured): no pair.
- No mapping yet where the successor isn't built or the evidence is thin: Jeskola Stereo Reverb and Raverb (drafts whose level tables are inexact), Oomek Aggressor 303 and Pedal SH101 (Neo Poly, unmeasured).

### 12.11 U4 as built

**The manifest editor** (`Machines/NeoBuzz.ManifestEditor`) is a Neo-style editor made from any machine's manifest, curated or draft, on the Neo editor kit. Like the Neo editors it is one source for both front ends: `ManifestEditor.cs` builds with the kit's WPF backend into ReBuzz.exe and with its Avalonia backend into the studio (`AVALONIA`, the kit's `WpfNames.cs`), and everything it draws goes through `INeoCanvas`.

- **What it shows** is planned toolkit-free (`Core/ManifestFace.cs`, also compiled into NeoBuzz.MachineTests): a section per manifest group in the order the groups first appear, track parameters last (the first track's; "Track 1" when a machine has more, the parameter list has every track); a knob per continuous parameter, choice buttons for up to five short choices, a drop-down for more, Off/On buttons for a switch; labels lose their section's name ("Low shelf gain" in Low shelf is "Gain"; the tooltip has the full name, the machine's name, range, default, safe range).
- **Units are the agent server's own.** The hosts (`Host/MachineFaces.cs`, compiled into ReBuzz.exe and the studio) read the machine as the server does (`SongModel.Params`: the manifest beside its DLL) and hand the editor the server's conversions: a knob shows what `param_get` shows (`ParamInfo.Describe`), and a value typed into the value box below (a right click on a knob puts it there, focused) goes through `ValueResolver.Resolve`, `param_set`'s parser: "800 Hz", "-6 dB", "35%", a choice, "raw:12". What `param_set` refuses the box refuses, with its message. There is no second copy of any conversion.
- **Envelopes.** Each section's `env.*` and `filter.env.*` roles with two or more stages get an ADSR picture (the kit's `EnvelopePainter`): to scale when every time stage is in seconds, by position when all are bare scales (said under it), none when the two mix (FSM KickXP's decay in seconds and release a bare slope).
- **Response plots, only where honest.** A curve is drawn only for a design known from its source, computed exactly as the machine filters (`Core/ResponseModels.cs`): Pedal EQ (the Audio EQ Cookbook's biquads, shelves at slope 1, in series or, soloed, in parallel, then the output gain) and Pedal Filter (its TPT state-variable filter, 2 or 4 poles, its resonance compensation and DC blocker, mixed with the dry signal, or its FIR or IIR comb; small-signal, at the LFO's centre, without Drive). NeoBuzz.MachineTests runs both machines' own filter code from their sources sample by sample on sines and checks the curves against it: within 0.05 dB (Pedal EQ, three settings, solos), 0.1 dB (Pedal Filter, 4 modes × 2 slopes × 3 settings) and 0.15 dB (its comb). Every other effect with frequencies in filter or EQ roles gets **markers**: a line at each frequency, labelled, the plot titled "Frequencies" and captioned "Markers only: … no curve is drawn" (Jeskola EQ-3, FSM PanzerDelay, Oomek Exciter). Generators get **nothing**: a synth's filter moves with its envelope, key tracking and velocity, so a line at the knob's cutoff says no more than the knob. A curve from roles alone would be made up.
- **Safe ranges** (machine-safety.md §3.4) are marked on their knobs: the unsafe ends in red outside the track, a tick where each begins, the value text red while outside. The knob still turns through them (people keep the whole range; agents keep inside it); a typed value outside one is set and the box says why it is unsafe. Jeskola Delay's Feedback (from −98.4 %) is the curated one; the drafts' (Jeskola Flanger, Joachims Neptune) show the same way.
- **Undo.** In the studio a knob drag, a typed value or a preset from the bar is one step on the song's undo history, track parameters included: the kit's parameter layer (`EditorContext`, `ParamBinding`, `ParamEdits`) now binds any group and track (`ParamSlot`); the Neo editors bind global parameters by index as before. WPF's editors have no undo steps, as before.

**Where it opens.** On a double click on a machine with no GUI of its own, the manifest editor opens instead of the parameter list where the per-gear setting says so: on by default for curated manifests, off for drafts (`ManifestEditorSettings`, kept per gear in the settings, `RegistryEx`: the Windows registry or the studio's JSON settings; never in songs). ReBuzz.exe: `MachineCore.DoubleClick`'s fallbacks to the parameter window ask the UI host's new `OpenEditor` first (a default interface method; `WpfMachineUiHost` makes the window); a native machine that handles its double click itself, or a managed one with a windowed GUI, keeps it. The Neo machines' manifests are generated (neither curated nor drafts), so the setting is off for them, and they keep their own editors whatever it says. The studio: `StudioMachineUiHost.OpenGui` does the same where a machine has no Avalonia GUI (the Pedal machines' WPF GUIs don't load there, so Pedal EQ, Comp and Drum Matrix get the editor in the studio and keep their own embedded GUIs in ReBuzz.exe). The machine menu, in both, has **Editor...** (any machine with a manifest) and **Double-click Opens Editor** (the setting); **Parameters...** is the plain list as before, and the editor's **Parameter list** button opens it too. The editor's window isn't saved in songs (the parameter window and GUI are, as before).

**Factory presets** for the curated machines, in their manifests' units: `Machines/Manifests/<Generators|Effects>/<Gear>.presets.txt` (the Neo machines' Presets.txt format, parameters by name or plain name, values as `param_set` takes them) compile through `param_set`'s parser into `<Gear>.prs.xml` beside it (`FactoryPresets`, `REBUZZ_WRITE_PRESETS=1 dotnet test ReBuzz.AgentTests --filter FactoryPreset`), refusing a value outside a safe range. The file lists every manifest parameter in the machine's order, global then the first track's, so a machine whose names repeat (FSM Infector), whose presets ReBuzz applies by position, lines up. `AppGear.targets` puts them beside the Pedal machines (the studio, rebuzz-headless) and `build.ps1` beside the natives, adding ours to a file the machine ships: FSM Infector's 609, Oomek Aggressor 303's 60, FSM PanzerDelay's 27 and Joachims Mars filter 2's one "Crunch example" stay beside ours (ReBuzz.exe lists 614, 64, 32 and 5). Names say what they do, "Group — What", as the Neo machines' do, and each has a comment; the manuals list them (`rebuzz://machines/<slug>/manual` reads the file beside the DLL).

96 presets for 20 machines, as measured by the check below (each preset after the one before it, as `preset_list` orders them): a generator's level is its loudest 400 ms, an effect's its loudness over its input's.

| Machine | Presets | Level | Highest sample peak | Measured on |
|---|---|---|---|---|
| FSM Infector | 5 | −21.5 … −11.4 LUFS | −2.1 dBFS | Windows |
| FSM KickXP | 4 | −12.3 … −11.2 LUFS | −1.7 dBFS | Windows |
| Jeskola Bass 3 | 3 | −12.3 … −11.3 LUFS | −1.6 dBFS | Windows |
| Oomek Aggressor 303 | 4 | −22.6 … −21.5 LUFS | −2.8 dBFS | Windows |
| Pedal FM | 6 | −14.8 … −10.3 LUFS | −4.1 dBFS | Linux |
| FSM PanzerDelay | 5 | −4.1 … +0.4 dB | −10.2 dBFS | Windows |
| Jeskola Cross Delay | 4 | −0.1 … +6.6 dB (mono: +6.0 is unity) | −4.3 dBFS | Windows |
| Jeskola Delay | 5 | +3.0 … +6.1 dB (mono) | −3.7 dBFS | Windows |
| Jeskola Distortion | 4 | +4.1 … +6.0 dB (mono) | −6.7 dBFS | Windows |
| Jeskola EQ-3 | 5 | +5.7 … +6.9 dB (mono) | −1.8 dBFS | Windows |
| Jeskola Filter | 4 | −4.9 … +6.2 dB (mono) | −4.0 dBFS | Windows |
| Jeskola Reverb | 5 | +6.2 … +8.2 dB (mono) | −1.7 dBFS | Windows |
| Joachims Mars filter 2 | 4 | −6.7 … +2.7 dB | −3.2 dBFS | Windows |
| Oomek Exciter | 4 | −0.1 … +2.6 dB | −9.1 dBFS | Windows |
| Oomek Masterizer | 4 | −8.2 … −1.0 dB | −11.9 dBFS | Windows |
| Pedal Chorus | 6 | −4.2 … −2.6 dB | −12.6 dBFS | Linux |
| Pedal Comp | 6 | −1.0 … +5.7 dB | −4.8 dBFS | Linux |
| Pedal EQ | 6 | −1.8 … +0.4 dB | −8.0 dBFS | Linux |
| Pedal Filter | 6 | −4.6 … +0.3 dB | −10.1 dBFS | Linux |
| Pedal Plate | 6 | −7.2 … −2.0 dB | −13.1 dBFS | Linux |

"Mono": ReBuzz feeds a mono native effect left + right summed, so these read 6 dB above what the preset does (§12.9's quirk).

Fewer or none, and why:
- **Rout 909**: none. It plays only from trigger columns the tools can't write (§12.9), so no preset of it can be checked to sound.
- **Pedal M1**: none. It plays waves from the song's wavetable; a preset can't carry a wave, so no preset sounds on its own.
- **Pedal Juno106, Pedal SH101, Pedal Drum Matrix** keep their own banks (48, 22 and 30 presets, named for what they do) and get none of ours; `preset-check.py --theirs` measures them and reports what it finds, below.
- **Pedal FM** ships a bank of its own (32 presets) in `PedalFM_Presets.prs.xml`, a name ReBuzz doesn't look for, so until U4 it had none; it gets six of ours.
- **Jeskola Bass 3**: three, its sawtooth ones; its other waves and any resonance peak above −1 dBFS whatever the filter (below).
- Jeskola Cross Delay, Distortion, Filter, Mars filter 2, Oomek Exciter and Masterizer, FSM KickXP and Oomek Aggressor 303 have four rather than five or six: what their closed or bare scales let us say with confidence.

**The check** (`tools/preset-check.py`, in the spirit of the Neo preset test, machines.md §6): for each preset file whose gear the server has, each preset loads through `preset_load` (with its comment) and sets the machine to exactly the file's raw values; it sounds (a generator plays a chord progression, a monophonic line or kick hits; an effect gets Neo Poly's chords and bass at −20 LUFS, peaking near −10 dBFS); its level is consistent (a generator's loudest 400 ms between −32 and −8 LUFS; an effect's loudness −12 … +6 dB over its input's, 6 dB more for a mono native effect, which ReBuzz feeds left + right summed; a machine's presets within 14 LU of each other); its sample peak stays below −1 dBFS; nothing diverges. Fork CI runs it on rebuzz-headless (the Pedal machines, with `--theirs`), the package job on ReBuzz.exe (the natives).

**What measuring changed.** The first Windows run failed 15 of the 61 native presets then (none of the Pedal ones), each fixed by measuring the machine, not by widening the check; three package runs measured candidate settings side by side (the "Sweep" presets, removed after):
- FSM Infector has no output level: each voice is its oscillators' sum through the filter, scaled by the square of envelope × velocity, so a four-note chord at velocity 100 adds up (Square Sub peaked at +7.3 dBFS, Supersaw +1.9, Soft PWM +2.3). Its sub-oscillator and cutoff barely helped through its default filter (6L Separated: a lower cutoff was louder); its Phlatt filters with less envelope did: Supersaw and Soft PWM on 4L Phlatt, Saw Bright and Square Sub on 2L Phlatt, the sub at 0–30 %. Measured in another order (after other presets), the same settings peaked up to 2 dB apart (the voices' oscillator phases carry over from preset to preset), so each keeps at least 1 dB below the limit.
- Jeskola Bass 3 has no level either and plays a velocity-100 note near full scale. Its sine, triangle and 303 saw, and any resonance from 10 %, peaked at −0.6 … +4.5 dBFS, and its pulse played louder than −8 LUFS, at every cutoff we tried: the filter's lowest setting stays above a bass line's fundamentals. Its presets are the three sawtooth ones that measure clean (Round Saw, Saw Pluck, Open Saw); its acid and pulse presets were dropped.
- Oomek Aggressor 303's presets set its Volume (linear: 55–75 %; at 100 % two peaked at +1.5 and +1.6 dBFS, and Screamer at 70 % at −1.0).
- FSM KickXP's Hard Clicky clicks at 50 % instead of 80 % (−0.1 → −1.7 dBFS: the click adds to the sine's peak).
- Jeskola EQ-3's Telephone no longer lifts the mids (+2.3 → −1.8 dBFS): the mono sum's 6 dB is already there.
- Oomek Exciter's Bass Harmonics at −24 dB instead of −10 (+9.1 → +2.6 dB over its input): its shaper turns a bass into a full-scale square whatever its level, so Amount alone sets how loud the harmonics are.
- Joachims Mars filter 2's God of Iron at −9 dB input (+6.1 → +2.7 dB) and Cannon at −20 dB (+13.8 → −2.4 dB), which also brings its four within 9.4 LU of each other (20.5 before).
- On the first Linux run the check's source moved from −6 to −12 dB (a part's level in a mix, peaking near −10 dBFS), where Pedal Comp's makeup gain had peaked at −0.2 … −0.5 dBFS; Pedal FM's Output levels were set to −14 … −20 dB.

**The Pedal machines' own banks** (`--theirs`, Linux; reported, not judged, not changed here: they are the machines' own, and levelling another author's bank belongs with their source, U6; Juno106's and SH101's are levelled since, §12.15):
- Pedal Juno106: 30 of its 48 peak above −1 dBFS or play louder than −8 LUFS with the check's chords at velocity 100 (worst: Bass — Pulse +5.1 dBFS at −5.7 LUFS, Lead — Sync +4.3 dBFS, Bass — Sub +2.9 dBFS).
- Pedal SH101: 6 of its 22 (Deep Sub +2.3 dBFS; Sub Bass +0.2 dBFS at −4.3 LUFS; Kick +0.3; Wobble Bass +0.1; PWM Sweep −0.9; Sci-Fi Drone −0.6).
- Pedal Drum Matrix: none peaks above −1 dBFS, but 17 of its 30 are more than 6 dB louder than their input (Resonant Drone +15.5 dB, Dub Chamber +11.8 dB, Evolving Texture +10.9 dB, Pentatonic Arp +10.8 dB, Telephone +10.4 dB at −1.2 dBFS).

**Tests.**
- NeoBuzz.MachineTests `ManifestFaceTests`: sections, controls, envelopes and their scales, safe ranges as knob positions and their marks (the Neo editors' knobs unchanged), levels for pictures, pictures inside their frames; the response curves against the machines' own filter code; markers for an effect without a known design, nothing for a synth.
- ReBuzz.Studio.Tests `ManifestEditorTests` (Linux and macOS): every curated portable machine (the ten Pedal ones) opens its editor on a double click, each parameter the server shows with its control and the server's text (screenshots `manifest-editor-*.png`); a double click in the machine view, Parameters... and the Parameter list button; a draft opens its list until the menu turns the setting on; a Neo machine (Neo Delay) keeps its own editor on a double click, even with the setting on; a value typed after a right click lands where `param_set` puts it, one undo step, refused text refused with the reason; a drag is one undo step; Juno106's ADSR picture, Pedal EQ's and Pedal Filter's computed curves (a +12 dB band reads +12 dB), none for a synth or a chorus. P3's exit test now meets SH101's manifest editor on its double click and reaches the parameter window through Parameters....
- ReBuzzTests `ManifestEditorTests` (the Windows package run): `ReBuzz.exe --agent --editor-shots` opens every curated machine in ReBuzz.exe's Gear as a double click does, through `WpfMachineUiHost`, draws it and reads it back; the test asserts that all 15 curated natives opened (and the curated Pedal machines the package has), every control showing the server's value, a typed value at `param_set`'s raw value, Jeskola Delay's safe range marked, markers for Jeskola EQ-3, PanzerDelay and the Exciter, none for the synths, Infector's two envelopes, and the presets beside the machines; the screenshots go to the ci-artifacts branch with the others.
- ReBuzz.AgentTests `FactoryPresetTests`: every source compiles and its `.prs.xml` is exactly what it compiles to; names "Group — What" with a comment; values in range and inside safe ranges; no two presets the same sound; every parameter in order; the compiler refusing what `param_set` refuses. `GuideTests` checks the curated manuals list their presets.

**Song-safe.** No machine's DLL, parameters or saved data change: the editor sets parameters through `IParameter` as the parameter window does, presets are ReBuzz's own format beside the machines, and the setting lives in the app's settings. The Neo machines' editors share the kit and gained only optional features (track slots, safe-range marks, a right-click event); their audio is untouched, and `tools/null-test.py` stays bit-identical.

**Left.**
- Computed curves for more designs: the synth filters whose source we have (Pedal SH101's Moog ladder, Juno106's, M1's) could be drawn at the knob's cutoff, but they move with every note; and the closed effects (Jeskola EQ-3, Filter) would need measuring (U2's test-signal source) before a curve could be honest.
- Track parameters show the first track's; the plain list has every track.
- The editor's window isn't restored with a song.
- Rout 909 and Pedal M1 presets (above); Pedal Drum Matrix's own presets that gain more than 6 dB are theirs, reported, not changed (Juno106's and SH101's are levelled, §12.15).
- The natives' own banks beside ours (Infector's 609, Aggressor 303's 60, PanzerDelay's 27, Mars filter 2's one) aren't measured: the check measures our files, and `--theirs` the curated machines without one.
- The check plays each machine's presets one after another, as someone trying them would; FSM Infector's peaks move by up to 2 dB with what played before (its oscillators keep their phases), which the 1 dB margins cover for the orders measured, not for every order. Jeskola Bass 3 and Infector have no level of their own: velocity is it.

### 12.12 U2 in depth as built

The second half of U2 (§12.7), in the profiler (`tools/machine-profiler.py`, with the measurements and the role rules in `tools/machine_roles.py`, standard library only) and five small changes to the tools. Nothing in any machine or in the engine changed: the null test stays bit-identical, and no parameter, range or saved datum moved.

**The test signal.** The profiler's own path, not a general tool: three Neo Polys feed the machine, each probed, and the machine is probed, so every measurement divides by what really went in (Neo Poly is in every host; a source needn't be exact when its input is read too). One render (`TestBench`):

| Part | What | Read off |
|---|---|---|
| Click | a 1 ms noise burst, first, so nothing before it rings into it, then 1.5 s (3 s at the defaults) to ring out | latency (onset), the direct sound, early (to 80 ms) and late energy over the burst's, the T60 from Schroeder's backward integral over what follows the direct sound (−5 to −25 dB, extrapolated), how long it rings to −60 dB, echoes on a 5 ms envelope (a 10 dB rise from a dip; a low resonance's swells are not echoes) on each channel and how fast they fade (dB per second), a delayed copy 2.5–60 ms behind (a chorus's), the tail's brightness (RMS frequency) and its share under 250 Hz |
| Stepped sine sweep | one sine per half octave, 23 Hz – 16.7 kHz (whole tones, 20 Hz – 19.9 kHz, at the defaults and for EQs), each six periods and at least 40 ms long | the response, out over in per tone by Goertzel, over 35–95 % of each tone shifted by the latency; its level, spread, centroid in octaves, −3 dB band, a peak's prominence over the response an octave either side, tilt; harmonics (THD) at 1 kHz; whether the gain moves within a tone (a modulated effect) |
| Level steps | for dynamics (by type or name) and at the defaults: a three-sine chord (123 Hz, 988 Hz, 7.9 kHz: a band each of a multiband) at −36 … 0 dB in 6 dB steps, then a 30 dB jump up and back | the static curve (gain per step), its compression and knee, and the time the gain takes to move 63 % of the way after the jump up (attack) and down (release) |

At the defaults its results go into the draft's `profile.effect` (`response`: [Hz, dB] per whole tone; `response_summary`; `impulse`; `steps`; `thd_percent`), and a delay or reverb typed by U1's 20 ms burst is typed again by the click: regular echoes a delay, a smooth tail of 0.3 s or more a reverb (Pedal Plate, a reverb since). A setting with no dry sound starts where the defaults' did (a dynamics' lookahead excepted). A machine with a loop (a delay, reverb or chorus, a tail at its defaults, or a feedback by name) is measured in a fresh instance per setting, after a warm-up past the patterns, so nothing the last render left circulating reads as an echo; a render that still has sound before the click is made again.

**Per-parameter sweeps and suggested roles.** Each parameter alone at its minimum and maximum (inside its safe range), against the defaults, through the test signal (an effect) or a held A1 (a generator: 55 Hz fills every third octave from 500 Hz with harmonics, so a resonance stands out). `suggest_effect_role` and `suggest_generator_role` name a role from `ManifestRoles.Known` with the evidence, or none ("no measurable change …"); a choice (a list, a switch, or five steps or fewer without a unit) gets a role where its texts say what (LP/HP: `filter.type`; ticks/ms: `delay.unit`; off/on: `bypass`; peak/RMS: `dyn.detection`; 16'/8': `pitch.octave`) or where its settings change the response's shape (`filter.type`). The rules, in order:

- **Effects.** One end passes the signal unchanged and the other doesn't: a wet level, or a corner, loop or decay moving out of the way. Half way (the default, or a third render) a mix keeps the wet end's corners and first echo at a lower level, and a compressor's blend flattens its reduction at the loudest steps or matches an even blend of the dry and compressed curves (decided by the name, which the curves can't, between a blend and a ratio): `mix.wet`. A feedback, resonance or Q, or a parameter in Hz or seconds, is never a mix. The direct sound coming and going with the tail staying is `mix.dry`. Then: reflections coming in ahead of the reverb's own (`reverb.early`); the first echo moving (`delay.time`, `_left`/`_right` per channel; `reverb.predelay`, or `reverb.size` when the tail grows with it; a chorus's copy, `mod.delay`); the echoes' brightness (`delay.tone`); how fast the echoes fade or the tail lasts (`delay.feedback`, `reverb.decay`, `mod.feedback`); the late sound (`mix.wet`); the tail's brightness (`reverb.damping`) and low end (`reverb.lowcut`); the gain moving within each tone at one loud end only (`mod.depth`, `lfo.depth`); the response (a peak growing: `filter.resonance`; a corner: `filter.cutoff`, `filter.highpass`; a band's width: `filter.bandwidth`; an EQ's band: `eq.*.gain`, `.freq`, `.q`); the steps (latency: `dyn.lookahead`; the quiet steps all shifting: `dyn.makeup`; the time constants: `dyn.attack`, `dyn.release`; the knee: `dyn.threshold`; the depth: `dyn.ratio`); harmonics (`drive`, `filter.drive`); balance (`pan`); level (`level`). Filters and EQs read the response first; reverbs, delays and choruses never read corners from it (their response to short tones holds their own tails and combs).
- **Generators.** One end silent: `level`, or `filter.cutoff` when the default is darker than the loud end. Then the held note's brightness or level swinging about its trend at one end only (`lfo.filter`, `lfo.amp`); the brightness 150–250 ms in changing with the start alike (`filter.env.decay`); the start's brightness changing twice as much as the held part's (`filter.env_amount`); a peak growing 12 dB or more where it stands (`filter.resonance`); the pitch (`pitch.coarse`, `pitch.fine`, `pitch.octave` for a stepped one; `drum.tune`); the attack, release, a drum's length; against the default, how fast the level falls (`env.decay`) or where it ends (`env.sustain`); the centroid (`filter.cutoff`, `drum.tone`); the level. What reads as a cutoff is tried on a note three octaves up too: key tracking brightens that one more (darkening the low note under its pivot), a cutoff as much or less (`filter.keytrack`).
- **Second looks**, for parameters that change nothing at the defaults: an EQ's frequencies and widths with their band raised (its gain at its maximum), a band-split EQ's crossovers with every other band raised; times and frequencies with what the sweep found to switch the effect on (a wet level, a feedback, a decay) 60 % of the way to its active end; a modulation effect's parameters with its depth at the end where the response holds still; a filter's other parameters with its cutoff half way; a generator's times with a choice that shapes its envelope (a VCA's mode) switched, and its filter section with the cutoff a third of the way up; a drum machine's parameters with the drum their name starts with ("BD Decay": "BD Trigger").

Drafts get `suggested_role` and `role_evidence` per parameter and `profile.roles` (the ends measured, the type the click read); `machine_describe` shows `suggested_role` and `role_evidence` where there is no curated role, and the draft's manual names it "suggested by the profile". Curated manifests keep their own roles: `--merge` takes the new profile (with `profile.roles`) and never touches a curated role; `docs/machine-health.md` lists the curated filter, time and mix roles beside the suggestions, a disagreement as a note. Neo machines keep their generated manifests and aren't swept (`--roles-generated` sweeps them). The features at each end go to `<out>/roles-detail/` (packed into the Windows job's log too) for a person checking a suggestion.

**Generators.** Velocity: A4 at 16, 32, 64 and 127, each note's loudest 10 ms (`profile.velocity`: range, dB per doubling, a word). Envelope: A4 held 2 s, rendered past its note-off (a tracker machine's note is let go by an event, not by stopping) and 1.75 s on (`profile.envelope`: attack, how far and how fast it falls, length, release, brightness at the start, 150–250 ms in and held). Pitch: where YIN hears none, a harmonic sieve on A2, A3 and A4 (a 16384-point FFT; the equal-tempered note within ±24 semitones whose harmonics, weighted 0.86ᵏ, score best, the highest of those within 10 %; pitched when its series holds 45 % of the energy from 50 Hz to 10 kHz on each note and 60 % on average, and all three agree; `notes.pitch_sieve`, `notes.pitch_method`). Samplers get a 2 s sawtooth in slot 1 now (U1's sine left a filter nothing to do). Triggers: a generator with no Note parameter that MIDI leaves silent is tried with its played track parameters and its globals named like triggers, each written as an event (`profile.trigger`: the first that sounds, and the others); the level test, the envelope and the sweeps then hit it with that trigger.

**Slow after silence.** `render(cpu=true, cpu_window_seconds=2)` over 2 s of input and 20 s after it: the machine's own work time per second of audio, window by window (the engine's per-machine counter, what ReBuzz's CPU monitor reads). A machine more than twice as slow (and 1 ms/s more) for two windows in a row after its input stops, against while it plays, gets a warning naming it; one window's spike (a collection) doesn't, and a slowdown must show again in a second render (each window the faster of the two: a busy computer only adds time). The summary keeps the windows; a draft only whether it slows down.

**Hot ends that ring** (machine-safety.md §3.1, §3.4). The bisection U2 ran for diverging and never-decaying ends now runs for a hot end too where a range is honest: a parameter that isn't a level control by name whose end peaks more than 24 dB over the machine's level with it at its default. The edge is the last value within 24 dB of that level; the draft records each value tried (`profile.sweeps.<param>.<end>.edge_probes`: raw, text, peak, verdict). The machine stays hot (a warning).

**The server.**
- `render` takes `cpu` (and `cpu_window_seconds`, 0.05–60): `cpu` lists each machine with `ms_per_second` (and `windows_ms_per_second`).
- `gear_list` leaves unhealthy gear out unless `include_unhealthy=true`, and names it in `hidden_unhealthy`. Unhealthy is what it was: diverging somewhere (even where a safe range covers it), crashing or hanging the server, failing to load, missing or crashed in this session. Hot gear is listed (a warning; its ringing ends have ranges), and gear that needs a window is listed after the drafts (its own status: it works in ReBuzz on Windows). `machine_add` still builds unhealthy gear by name.
- `machine_describe` lists `played_parameters`: track parameters that hold no value (a drum's trigger, a note's velocity), which `param_set` doesn't take. `automation_write` writes one (where no parameter that holds a value has the name) as one event per point, never ramps, and says `played`. So Rout 909's drums can be programmed by agents now.
- `machine_describe` gives a draft's `suggested_role` and `role_evidence`.
- The CI scripts (`curated-check.py`, `preset-check.py`, `headless-manifests.py`) ask `gear_list` for everything; the package workflow takes `profile_only` (a gear pattern) for a measuring run.

**Found** (33 machines on Linux, rebuzz-headless; 93 in the Windows package's Gear, quick build; `docs/machine-health.md` has them all).
- **Suggested roles against the curated ones** (the exit check). Of the 25 curated machines' 137 filter, time and mix parameters (Jeskola Distortion and Joachims Mars filter 2 have none), the suggestion agrees on 41 of 67 on the Pedal machines and 39 of 70 on the natives, 80 in all (within a family: `delay.time` and `delay.time_left`, `env.decay` and `drum.decay`, `filter.cutoff` and `filter.highpass`…); the table is in `docs/machine-health.md`, every disagreement with what was measured. None changed a curated role. Where they part, the measurement and the curation say different true things:
  - a synth's filter envelope amount, or an envelope that also drives the filter, reads as a cutoff on one held note where the sustain holds the filter open (Juno106's and SH101's Env Amt, Juno106's Env D, Aggressor 303's Env.Mod and Decay); key tracking is told from a cutoff on a second note three octaves up (Juno106's VCF Key Trk agrees);
  - Pedal FM's modulators' envelopes shape the brightness, not the level (Op1–3 Attack read as cutoff or envelope amount), and its decays and releases change nothing with the sustain full; FSM Infector's filter and amp envelopes cross (its decay reads as attack), FSM KickXP's decay rates move its pitch sweep;
  - parameters that do nothing in the state the defaults leave: Pedal M1's filter section on the test sawtooth, Oomek Masterizer's decays (nothing over its threshold), Jeskola Reverb's Pre-Delay (no change on the click at all), Rout 909's attack and tom decays, Pedal Drum Matrix's envelope follower, FSM PanzerDelay's filter and dry level;
  - choices whose texts don't say (Juno106's HPF and Env Pol, SH101's Kbd Follow, Infector's and PanzerDelay's filter types);
  - Jeskola Bass 3's filter envelope shortens the note itself on its default patch (read as the level's decay); a delay length past the click's 1.5 s window reads as feedback (Jeskola Cross Delay's R. Length at its maximum); Jeskola Filter's Bandwidth acts only in band-pass. FSM PanzerDelay's L Delay Len is heard on the right at its default pans (and R on the left): the agreement counts the line, the evidence says the channel.
- **Velocity** (`notes_write`'s velocity, through a velocity track parameter where there is one): it changes the level of 7 of the 20 generators that take notes: steeper than linear for FSM Infector (10.5 dB per doubling), Bigyo Wood One and Neo Sampler (8.4), linear for Pedal FM (6.0), gentle for Neo Poly, Neo Wave and kibibu Green Milk (2.6–2.8). The other 13 (Juno106, SH101, M1, Tracker, Jeskola Bass 1–3, Aggressor 303, KickXP, Organ, the AY-3-8910, Matilde Tracker2, Neo FM6's default patch) play every velocity alike.
- **Envelopes** at the defaults: the basses die fast (Jeskola Bass 1–3: 90–110 ms to 40 dB down; Aggressor 303: 75 ms), the drums as their decays say (KickXP 310 ms, Trilok's BD 160 ms, Rout 909's BD 445 ms), FSM Infector 540 ms, Bigyo Wood One 1.4 s; the pads and leads hold (Pedal FM's release runs past the 1.75 s window).
- **Pitch.** The sieve hears Jeskola Bass 1–3, Oomek Aggressor 303 and Pedal FM play as written (offset 0, within 3 cents on every note), where YIN heard none; `notes_write` needs no offset for them.
- **Triggers.** Rout 909 sounds from BD Trigger (and each of its eleven drums from its own), Jeskola Trilok from BD Trigger, Jeskola Noise Generator from Trigger: `automation_write` plays them. Intoxicat ACloud15 and AudioBlock have no played track parameter or trigger to try; the audio inputs, MIDI output and Pedal Peer Control are silent as expected; Jeskola Tracker and Polac VSTi wait for a wave or plugin (left).
- **Slow after silence.** On Linux, every run: Pedal Shaper (2.1 → 32 ms of work per second of audio, for all 20 s), Neo Analyzer (7.2 → 28 ms/s, all 20 s), Pedal Plate (1.6 → 21 ms/s for 8 s, then 0.3) and Pedal Gate (0.7 → 2.4 ms/s); Pedal Comp sits on the line (1.2 → 2.5 ms/s: just over twice in one run, just under in the others). On Windows, one package run of four found six machines slow after silence, each confirmed by a second render (Jeskola EQ-3 13 → 79 ms/s, Jeskola Reverb 14 → 111, Jeskola Stereo Reverb 15 → 104, Neo Analyzer 7.4 → 28, FSM WahPro2 14 → 35, Joachims Saturn 1.4 28 → 56); the other three found none, and the summary keeps the last. Denormal numbers, most likely, and whether they cost time depends on the thread's floating-point mode (flush to zero), which nothing in the engine sets: the fixes belong with the machines' sources (U6 for the Pedal machines; Neo Analyzer with the Neo machines), or with the engine setting that mode for every machine (which can change a render's bits wherever a machine makes denormals, so the null test would need new references).
- **Ends that ring:** Jeskola Filter and Filter 2 (Resonance from 119 of 128: +53.8 and +52.0 dBFS over 30 s, 57 and 55 dB over their level at the default), Jeskola Delay (Feedback at +100 %, +31.3 dBFS, 31 dB over; its −100 % never decays, as before) and NiNjA dElaY (Feedback at ±100 %: +38.7 and +39.2 dBFS, 35 and 36 dB over) get safe ranges (machine-safety.md §3.1). The other hot ends are level controls by name (their job), or ring less than 24 dB over the default (Jeskola Cross Delay, Chorus and FSM PhatMan at ±100 %, Jeskola Bass's Resonance at +16 dBFS). One early run had Jeskola Bass hang the server with Slide lenght at 255; three package runs since haven't, so it has no range.
- **Unhealthy** (`gear_list` leaves them out unless asked): Jeskola Flanger (diverges with feedback at +100 %) and Joachims Neptune 1.2 (Threshold at −100 dB), each with its reason and safe range in the report. Nothing crashed or hung, and nothing failed to load.

**Tests.**
- `tools/test_machine_infer.py` (64 cases, from U1's 24 and U2 health's): the sweep's tones and Goertzel, a T60, echoes and their fade, the harmonic sieve (a resonant bass, inharmonic partials), third-octave prominence and wobble, the velocity curve; roles from made-up features (a cutoff, mixes with a tail and with a shape, delay time and feedback, a reverb's decay, predelay and early reflections, a resonance never a mix, a closed filter's ring not a tail, a filter's type, the dynamics, a blend named like one, key tracking, a generator's roles, categories and agreement); hot ends that ring and level controls that don't.
- ReBuzz.AgentTests: `DraftManifestTests` (a draft's `suggested_role` and `role_evidence` parse, curated and generated manifests have none, every committed draft's are known roles with evidence), `CuratedManifestTests` (the tightened and new curated safe ranges), `GuideTests` (the tools' new inputs).
- `ReBuzzTests` `AgentHeadlessTests` (the package run): `gear_list` leaves the unhealthy out by default and names them.
- `tools/headless-manifests.py` (Fork CI): the Pedal check with the two machines its copy makes unhealthy left out and named, every Pedal machine with `include_unhealthy`; Pedal Shaper's suggested roles through `machine_describe`; Neo Poly's played Velocity; `automation_write` on it (one event per point); `render`'s `cpu` windows and its refusal of `cpu_window_seconds` alone.

**CI time.** The profile step is what grows: in the package job from 7.8 minutes to 15–25 (93 machines; the same step's time varies 1.7× with the runner, as the job's other steps do), on Linux from 4.5 to 11 minutes for 33 machines (two shared cores, measured locally; Fork CI's runner is faster). The per-parameter sweeps take most of it (a fresh instance per setting for machines with a loop), the 22 s render for slowdowns the rest. `--no-roles`, `--no-denormal` and `--health-only` turn the deeper passes off where time matters, and the package workflow's `profile_only` narrows a measuring run to some gear. Fork CI, which runs on every change, profiles with `--no-roles` (decided 2026-11-05): the roles come from the full package job's profile, and the Linux step keeps the health, test signal, velocity, envelope, trigger and slowdown passes.

**Left.**
- Whether `Work` reports silence: the engine doesn't keep what `Work` returns, and keeping it is an engine change. (U6 found that it keeps it for the machine's LED only, and that a managed effect's false passes its input on: §12.13.)
- Jeskola Tracker and Polac VSTi stay silent until a person chooses a wave or a plugin: what to load is a choice, not a measurement.
- `midi_play` into a tracks-only machine (it would set the Note parameter live): a live-input feature rather than a profile.
- The denormal slowdowns found (above) are measured, not fixed: fixing them is a change to those machines' code (U6, and the Neo machines' own batch). U6 fixed the Pedal machines' and Neo Analyzer's (§12.13); the natives' remain.
- An LFO's rate isn't read (the sweeps hear depth, not speed: a 2 s held note is too short for the slow ones). Roles that need two parameters at once (an envelope's amount with its times, a multiband's crossovers) or a state the defaults don't reach are left to curation, which the report's notes point at.
- The test signal is the profiler's own, not a general tool: Neo Poly is in every host and the profiler reads what really went in; a general `test_signal` tool (Neo Sampler playing a known file sample-exact is the obvious one now) can come when an agent needs one.

### 12.13 U6 as built

Tier 3 (§12.4) for the C# machines, built for the Pedal effects, ReBuzz's own machines and Neo Analyzer. The Pedal synths were left as they were (decided with U6's scope): no MIDI input and no source change for Juno106, SH101, FM, M1 and Tracker, nor for Chord, LFO, Peer Control and Drum Matrix; §12.15 did the synths' share since. No parameter, range or saved datum moved, and nothing in the engine changed: the null test stays bit-identical (its cases use no Pedal machine, and Neo Analyzer passes its audio unchanged).

**Slowdowns after silence** (§12.12 found them): each was a one-pole decay or a filter's state left decaying towards zero in floats, where rounding stops it a few steps above zero, among the denormal numbers the CPU handles slowly. Each such state is now set to zero once it is under 1e-15 of full scale (−300 dBFS), sample by sample, in the machine's own code, not by the engine's floating-point mode, so anything louder is the same bit for bit and the output doesn't depend on the block size:

| Machine | The states |
|---|---|
| Pedal Shaper | its tone low-pass, DC blocker, hysteresis and dust envelope |
| Neo Analyzer | its K-weighting filters (`NeoBuzz.Analysis/Loudness.cs`, doubles) and, after minutes, its spectrum's average |
| Pedal Plate | its input high-pass, the diffusers' and tank's all-passes and the tank's damping |
| Pedal Gate | its peak follower |
| Pedal Comp | its envelope |
| Pedal Chorus | its tone filter (8 denormal samples came out) |

Measured as the profiler measures it (§12.12: the machine's own work time per second of audio while noise plays, then over the 20 s after it stops), in ms/s, playing → after silence (worst 2 s window):

| Machine | Linux before | Linux after | Windows before | Windows after |
|---|---|---|---|---|
| Pedal Shaper | 2.48 → 37.2 (45.7), 17.5× for 20 s | 2.85 → 1.44 (1.53) | 2.24 → 38.4 (47.6), 19.2× for 20 s | 1.95 → 1.67 (1.82) |
| Neo Analyzer | 8.16 → 36.6 (42.1), 4.5× for 20 s | 8.49 → 6.06 (8.16) | 7.29 → 33.7 (35.7), 4.7× for 20 s | 5.90 → 5.34 (6.22) |
| Pedal Plate | 2.02 → 24.4 for 8 s, 12×, then 0.38 | 2.37 → 0.50 (2.35 while its tail rings) | 2.86 → 25.2 for 8 s, 8.4×, then 0.39 | 2.23 → 0.40 (2.84 while its tail rings) |
| Pedal Gate | 0.95 → 2.96 (3.07), 3.1× for 20 s | 0.96 → 0.77 (0.90) | 1.12 → 3.09 (3.19), 2.8× for 18 s | 0.86 → 0.89 (0.94) |
| Pedal Comp | 1.86 → 3.23 (3.36), 1.7× | 1.77 → 0.83 (0.89) | 1.57 → 3.12 (3.28), 2.0× for 6 s | 1.40 → 0.88 (1.01) |

Linux: rebuzz-headless, the build before U6 and after it alternately, each with a warm-up render, the median of four, .NET's tiered compilation off (with it on, a window's time depended on which code was still unoptimised). Windows: the package job's profiler, run 41941025882 before (613a5ef), run 41945508945 after (6bebfaf, the same machine code as U6's head for these figures). Pedal MComp, Chorus, EQ and Filter weren't slow and aren't now (MComp 9.8 → 4.8 ms/s before and after). The flushes cost a little while playing: Pedal Shaper 2.48 → 2.85 ms/s and Pedal Plate 2.02 → 2.37 on Linux, within the runner's spread on Windows. `docs/machine-health.md` lists no machine slow after silence.

**Smoothing.** Automation and pattern events change a parameter once a tick or subtick, and the machines applied a new value once a block: a quarter-second ramp of 24 dB went up 3 dB at a time, and a mix 13 % at a time. A gain, a mix, a bias, a tone or a crossover now glides to its new value in a straight line over 20 ms, sample by sample, and lands on it exactly (the `Glide` struct in each machine's source; Pedal Filter's Mix in doubles, Pedal EQ's Output gain extending its own ramp to 20 ms). A setting that doesn't move is the same number as before, so the output is bit-identical; the first block, and a new sample rate, start at the setting. Pedal MComp's crossovers glide in octaves, their filters recomputed every 16 samples while they move.

Measured with `automation_write` ramping the parameter over 0.25 s while a held sine (Neo Poly) plays through the machine: the largest step between two samples, as a gain (the output over the machine's with the parameter held) or a share of the mix; for parameters that aren't a gain, the largest second difference during the ramp over the steady sine's before it (a click shows up as a spike):

| Parameter | Ramp | Before | After |
|---|---|---|---|
| Pedal Shaper Output, Drive | 24 dB | 3.0 dB, 3.2 dB steps | 0.004, 0.005 dB (a smooth ramp's step: 0.002) |
| Pedal Shaper Mix | 0 → 100 % | 12.6 % (6.0 dB of the wet) | 0.017 % (0.002 dB) |
| Pedal Shaper Tone, Bias | full range, a quarter | spikes 118×, 9515× | 1.0×, 12.6× (a bias moving changes the waveform itself) |
| Pedal Comp Makeup Gain | 6 dB | 1.0 dB | 0.001 dB |
| Pedal Comp Wet/Dry, Pedal MComp Dry-Wet, Pedal Plate, Chorus and Filter Mix | 0 → 100 % | 13 % (1.7–6.4 dB of the wet) | 0.015 % (0.001–0.006 dB) |
| Pedal MComp Output Gain, LM Makeup | 24 dB | 3.0, 2.9 dB | 0.004, 0.010 dB |
| Pedal MComp Xover L-LM | full | spikes 28× | 1.4× |
| Pedal Gate Range dB | 90 dB | 12 dB | 0.03 dB |
| Pedal EQ Output | 24 dB | 0.27 dB (its ramp over one block) | 0.004 dB |

Not changed, measured smooth already: Pedal Gain's and Gain Multi's gains (their own ramps, 0.019 and 0.012 dB), Pedal EQ's bands (it blends its coefficients), Pedal Filter's Cutoff, Pedal Plate's LowCut and Damping, Pedal Chorus's Tone (spikes 0.3–1.7×).

**Silence reporting.** What the engine does with what `Work` returns: `MachineWorkInstance.WorkMachine` keeps it in `MachineCore.IsActive`, which lights the machine's LED in the machine view (`MachineControl`, `GroupControl`), and the recursive work algorithm overwrites it each block with `GetActivity` (the output's level) anyway. No machine is skipped, and none is told its input is silent: effects are always called with `WM_READWRITE`, never `WM_NOIO`. It matters for the sound in one place: a managed machine's false leaves its output buffer uncopied (`ManagedMachineHost.Work`), so an effect's output is its input (the native host keeps the shared buffer as the machine left it). An effect that says it is silent while its tail still rings cuts the tail off, and the four Pedal effects that say so did:

| Machine | Cut | Now |
|---|---|---|
| Pedal EQ | after 32 blocks of input under −90 dBFS, whatever its output: a band at 100 Hz, Q 10, +24 dB at −30 dBFS (and when, by the block size) | also waits for its last block's output to be under −90 dBFS |
| Pedal Plate | 8 s after its input stopped: at Decay 100 at −47 dBFS (−40 with Feedback 95) | also waits for its last block's output to be under −120 dBFS: at Decay 100 33 s on; at Decay 100 and Feedback 95 not within 60 s |
| Pedal Filter | when its input and filter states were silent, its DC blocker's decay not counted: up to −24 dBFS on the test's pink noise, a click, at a moment that depended on the block size | counts it (the decay of its 3.5 Hz DC blocker plays out: 0.3 s at the defaults) |
| Pedal Chorus | 100 ms after its input stops (its delays are 33 ms at most) | unchanged: correct |

Where the old report came when the output was silent already (the defaults, and every moderate setting measured: Pedal Plate's tail is under −130 dBFS 8 s on at its defaults), the new one comes on the same block, bit for bit. Pedal Shaper, Gate, Comp and MComp return true while they have an input, as before. `NeoBuzz.PedalTests` checks every start of a report against the block before it (under −80 dBFS: −89.6 dBFS at most, Pedal Filter's).

What the engine would need for silence to save work (not built): (1) per connection, whether its source's last `Work` returned false; (2) an effect whose inputs are all silent called with `WM_WRITE` only, and skipped once it reports silence too, as Buzz's contract has it; (3) a reported silence treated as a silent output, zeros rather than the input, for that to be safe; (4) a check of each machine's report against its output (the engine's own comment says some report wrong: a profiler pass that flags a false with output over −90 dBFS), and a per-gear opt-out; (5) the null test: its references stay only where every machine in its cases reports truly, since a skipped machine's tail under the threshold becomes zero. The gain is small for the Pedal effects (0.4–1.7 ms/s idle after U6, Pedal MComp 4.7) and larger for the natives (Jeskola Reverb's 111 ms/s after silence on Windows, §12.12).

**Manifests from code.** `tools/source_manifests.py` (standard library only) writes the manifests of 15 machines from their C# source: Pedal Shaper, Gate, Gain, Gain Multi and MComp (the Pedal effects that were drafts) and ReBuzz's AudioBlock, Connection Mixer Console, EnvelopeBlock, QuickNote, ReBuzz Audio In, Audio Out, Groove, Midi Output, Session Clip Player and ManagedMultiIOEffect, 118 parameters. It reads each machine's `[ParameterDecl]`s as `MachineParameter.Create` builds them (properties are global parameters, methods track parameters; a bool a switch; value descriptions that reset a range; played track parameters left out), and its constants; a table in the script gives each parameter's kind and the code's own expression for its value, translated to Python, and the mapping must reproduce that expression for every raw value, or the script stops with the parameter. Whether the machine's texts say the value decides `own_texts`. The result is `status: "generated"` (`Manifest.IsGeneratedFromSource`) with `source` naming the file; the profile, health, type, notes and safe ranges carry over, and the profiler's `--merge` keeps a generated manifest's mapping as it keeps a curated one's. The server shows them in exact units, like the Neo machines' (`machine_describe`'s `exact_units`; the manual says "generated from its source code"). `python3 tools/source_manifests.py` checks the committed ones against the code (`--write` writes them); `test_machine_infer.py` runs that check in Fork CI.

What the code proved the drafts wrong on: Pedal Shaper's Drive and Output (−24 … +24 dB, where the draft had raw numbers), Tone and Bias (−100 … +98.4 %), Mix and Dust (percent) and Hysteresis (0–10 ms); Pedal Gate's Range (−90 … 0 dB); Pedal Gain Multi's switches, Pan (bipolar) and 24 tracks; Pedal MComp's crossovers (exactly 40–600, 200–3000 and 800–12000 Hz in octaves), its Ratio curve (40^(v/126), so 41.19:1 at the top where the draft fitted 41.33), attack and release times, and Lookahead (a table, Off at 0); Connection Mixer Console's Vol (linear, 0–400 %) and Pan (bipolar); ReBuzz Groove's step count (an integer) and steps (×1–3); Midi Output's integers. Found and noted in its manifest, then fixed (below, *ReBuzz Midi Output*): its `Work` read the controller number from the track's fourth parameter (Delay) and the channel from its sixth (MidiCC), and encoded every message without a channel, so MidiCC and Channel did nothing. The curated Pedal effects (Chorus, Comp, Filter, Plate) were checked against their code and are right; they stay curated, as do Pedal EQ and Drum Matrix.

**The Neo measurement suite** (machines.md §6) on the eight Pedal effects U6 changed: `NeoBuzz.PedalTests`, 62 cases on the machines' own `Work` through a host stub at 48 kHz (`PedalHarness.cs`), in Fork CI with its figures in notices.

| Check | Result |
|---|---|
| Silence after sound settles to exact zero, no denormal output | all eight: Pedal Shaper in 0.22 s, MComp 0.15 s, Filter 0.30 s, Chorus 0.01 s, Plate 8.0 s, the others at once |
| Blocks of 256 and 117 give the same output | bit for bit, but Pedal Filter: 115 samples differ, by −90.7 dBFS at most (it decides once a block that its tail is silent) |
| Allocation per block once running | 0 bytes for all eight (Pedal Chorus allocated three arrays a block, Pedal EQ two per sample in solo mode: now made once) |
| Level at the defaults, pink noise at −20 dBFS RMS | Shaper −1.1 dB, Plate −5.7, Gate 0.0, Comp −1.0, MComp −0.2, Chorus −3.7, Filter −1.9, EQ 0.0; every parameter alone at each end finite |
| Aliasing (Pedal Shaper: Drive +12 dB, 4.7 kHz at −6 dBFS; no oversampling) | Soft −41.0, Fold −38.8, Hard −32.9, Crush −32.8, Tube −30.5 dB under the fundamental: recorded, not judged (Neo Drive is its successor) |
| A stepped gain or mix glides and lands | 11 parameters: at most 0.009 dB or 0.1 % of the mix per sample, then the same output as set from the start |
| A silence report cuts no tail | above |

**Render changes.** At settings that don't move, bit for bit the same as before U6, but for: Pedal Shaper's tails under −300 dBFS (now zero), 54 of Pedal Chorus's samples at −314 dBFS, Pedal Filter's DC blocker's decay (no longer cut: from 3 ms after the input stops, up to −57 dBFS at the defaults), and the ringing tails that Pedal EQ and Plate cut (Pedal Plate at Decay 100 from 8 s after its input, by up to −46 dBFS; Pedal EQ's band at 100 Hz, Q 10, +24 dB from 96 ms on, by up to −35 dBFS). Pedal Plate at its defaults and at Decay 80 over 14 s, Pedal EQ at its defaults and with +6 and +12 dB bands over 6 s: identical. A parameter that moves differs during its 20 ms glide (by −10 to −44 dBFS at a step of a gain or a mix) and is the same once landed; Pedal MComp's crossover for a quarter of a second, under −99 dBFS from 25 ms on.

**The profiler.** A tail whose RMS frequency is under 20 Hz is a DC blocker's or a high-pass's decay, not a reverb's: it doesn't type an effect a reverb (U1's burst and U2's click) or count as the tail that stays for `mix.dry` (`machine_roles.subsonic`). Pedal Filter's decay, no longer cut, had made it a reverb with Cutoff as `mix.dry`; with the rule its type and roles are as before. A generated manifest is recurated like a curated one, and the health report leaves generated manifests out of its drafts' suggested roles.

**The server.** A choice named like a keyword (`min`, `max`, `default`: EnvelopeBlock's Default) is that choice in `param_set`, before the keyword.

**Tests.** `NeoBuzz.PedalTests` (above); `NeoBuzz.MachineTests` `AnalyzerTests.SilenceAfterSoundSettlesToExactZero`; `tools/test_machine_infer.py` (70: the committed manifests against their source, declarations read as ReBuzz reads them, kinds that fit exactly or fail, code that changed caught; the subsonic tail); ReBuzz.AgentTests (`CuratedManifestTests`: the 15 from source exact in every value and the rest drafts, their manuals; `DraftManifestTests`; `GuideTests`' manuals for the Neo machines); `tools/headless-manifests.py` (Pedal Shaper's generated manifest through the tools: "+6 dB" is raw 60 and reads "+6.0 dB", Mix 50 % is raw 63 or 64, `exact_units`; the draft checks on Pedal Tracker); `tools/curated-check.py` checks generated manifests as curated ones; `ManifestEditorTests`' draft editor on Pedal Tracker.

**ReBuzz Midi Output** (fixed after U6, `Machines/MidiOutput/MidiOutput.cs`). ReBuzz orders a managed machine's track parameters as they are declared (Device, Note, Volume, Delay, Cut, MidiCC, MidiCCValue, Channel), and `Work` read three of them by indices from before Delay and Cut were declared: the controller number from 3 (Delay), the channel from 5 (MidiCC), and the velocity of a note no Volume had set from 0's default (Device's, 0); and it encoded every message with a bare status byte. Each is read from its own place now and every message carries the track's channel. What each message was and is, bytes as sent (status, data 1, data 2):

| A track's | Before | After |
|---|---|---|
| Controller change (MidiCCValue set) | `B0`, the track's last Delay (0 if none since the song opened: Bank Select), the value | `B0`+Channel, MidiCC, the value |
| Note on, note off | `90` / `80` (channel 1) | `90`+Channel / `80`+Channel; a note-off on the channel its note went out on |
| Note on with no Volume set on the track since the song opened | velocity 0 (a note-off to every receiver: nothing sounded) | velocity 120, Volume's default |
| MidiCCValue above 127 on a controller | its low byte as data 2: a byte with its top bit set on Windows (a status byte to the receiver), wrapped to its low 7 bits on macOS | 127 |
| MidiCC 224 | unreachable (the controller came from Delay, 0–127) | the pitch wheel, `E0`+Channel, the value's low 7 bits, its next 7 (0–16383; above, 16383) |
| MidiCC 128–254 but 224 | unreachable | nothing (no message has such a number) |

So an existing song plays as before where its tracks kept Channel at 0 and wrote no controller; its controller changes go to their MidiCC now (they all went to controller 0, or to whatever Delay a note on the track last had), its notes and controllers move to the channels its tracks set, and its first notes on tracks without a Volume sound. No parameter, range, default or saved datum moved (the manifest, generated from the code, changed only its descriptions). `ReBuzzTests.MidiOutputTests` plays the built machine into a MIDI output that records every message (the engine's `IMidiBackend`), byte for byte: controllers on their channel and number, the pitch wheel, values held to their message, notes on their channel and released there, the default velocity, and the track parameters where songs have them; before the fix, four of its five tests failed on these bytes.

**Left.**
- The Pedal synths: MIDI input, and denormal guards, smoothing and silence reporting in Juno106, SH101, FM, M1 and Tracker; Pedal Tracker's, Chord's, LFO's and Peer Control's manifests from code; Juno106's and SH101's own banks levelled. **Built** since (§12.15); what that leaves is listed there.
- Silence that saves work: an engine change (above).
- Pedal Shaper aliases (measured above): oversampling would change its sound; Neo Drive is the successor.
- Pedal Plate at Decay 100 with Feedback 95 rings for more than a minute, running all the while: correct, and its cost is its playing cost.

### 12.14 U7 as built

Tier 3 (§12.4) for the 17 classic natives with sources in the Buzztrax collection (`ExternalMachines/source/buzztrax-buzzmachines`): FSM Infector, KickXP, PhatMan, PanzerDelay, Chorus2, WahMan, WahManPro and WahPro2; Oomek Aggressor 303, Exciter and Masterizer; Jeskola Delay, Cross Delay, Distortion, NiNjA dElaY, Trilok and Noise Generator. They build in every full package run, as x86 and x64; fourteen of them now ship rebuilt from source in place of upstream's binaries, each rendering as upstream's does (the three FSM wahs as upstream's binary does from a zeroed heap: their fix), three of them Jeskola's, as x64 builds in place of 32-bit originals, by the user's decision of 2026-11-06 and the one exception to the licence gate (below); the eight that had drafts have manifests generated from their code; and one real bug, state never initialised in the FSM wahs, is fixed. No parameter, range or saved datum moved, and nothing in the engine changed: the null test stays bit-identical (its cases use no native machine), and songs saved with upstream's binaries open with the rebuilt ones with the same parameters and play the same bits (`tools/native-songs.py`, below), but where upstream's wah started from heap garbage.

**How ReBuzz hosts them.** Every native machine runs in a process of its own: ReBuzz.exe (x64) starts `bin64/ReBuzzEngine64.exe` for a DLL named `*.x64.dll` and `bin32/ReBuzzEngine32.exe` for any other, and talks to it over named shared memory (`NativeMachineHost`, `NativeMessage`; single-process mode shares one host per bitness). Gear holds one DLL per machine (build.ps1 counts `Foo.dll` and `Foo.x64.dll` as one; ReBuzz's scanner keys both by "Foo", and the last scanned wins). What upstream ships, read from the binaries' headers:

| Machines | Upstream's binary | Built with |
|---|---|---|
| FSM (8), Oomek (3) | x64 only (`*.x64.dll`) | Visual Studio 2015 (linker 14.0), static C runtime, importing `dsplib.dll`: Buzztrax's sources and makefiles, as it turned out (below) |
| Jeskola (6) | 32-bit only | Visual C++ 5 and 6 (linker 5.2, 6.0), `MSVCRT.dll`: Jeskola's own builds |

**The build.** `tools/build-natives.ps1` builds the machines the Buzztrax entry of `ExternalMachines/machines.json` lists under `natives` (sources, defines, libraries, extra files such as presets), as x86 and x64, with the Buzztrax makefiles' flags (`common.nmake`, Release: `/MT`, no optimisation switch, so cl's `/Od`; `WIN32`, `_WINDLL`, `__MSVC__`, `_X86_` or `WIN64 _AMD64_`), against Buzztrax's MDK (`common/mdk`, static) and ReBuzz's own dsplib import libraries (`ReBuzzEngine/dsplib`, `dsplib64`), so a rebuilt machine loads the `dsplib.dll` in `bin32` or `bin64` as upstream's does (Buzztrax's `dsplib.h` declares the same functions: `__fastcall` on x86, as ReBuzz's x86 dsplib exports them). `build.ps1` step 4b runs it in every full build (`-SkipNativeMachines` to leave it out; quick package runs skip it, as they skip the other external machines): 34 DLLs in 21 s on the runner (Visual Studio 18, MSVC 14.51), into `build/ExternalMachines/natives/<x86|x64>/Gear`. A machine whose entry says `"ship": "rebuilt"` (and whose licence allows it) replaces upstream's binary of the same platform in Gear; `GEAR-INVENTORY.txt` says `external:buzztrax`. The old `-NativeMachines` switch (the nmake subset: FSM, Fuzzpilz, Matilde, Oomek, x64 only) is gone; the rest of the collection stays source only.

**The header fix** was one: `FSM/dspchips/DSPChips.h` defined `copysign(x,y)` as a macro for `_copysign` (with a stray semicolon), and when a later header pulled in `<cmath>` (Infector's `<complex>`, in `Vegetable.cpp`) the macro rewrote `<cmath>`'s own `copysign` declarations: `error C2059: syntax error: 'noexcept'` at `cmath` lines 78, 333 and 894. The macro is gone on MSVC and its one use calls `_copysign`, which the macro made it before (on gcc, `_copysign` still maps to `copysign`). Everything else compiled as it was, x86 and x64, with no warning at cl's default level: the "old `windef`, `mdk` and `dsplib`" don't clash with current headers on Windows (`common/windef` is Buzztrax's Linux stand-in, never on MSVC's include path; the MDK is plain C++). FSM Infector was the only machine that didn't build.

**Licences** (`license` and `redistribute` per entry; `ExternalMachines/README.md`):

| Machines | Licence in the source tree | In the package |
|---|---|---|
| FSM (8) | LGPL-2.1 (`FSM/COPYING`; the sources' headers: LGPL-2 or later) | may be built and redistributed, with the source |
| Oomek (3) | GPL-3.0-or-later (each source's header) | may be built and redistributed, with the source |
| Jeskola (6) | none: no file in `Jeskola/`, no header in any source; Cross Delay is Buzztrax's own rewrite ("the original source is not available"), also unlicensed | Distortion, Trilok and Noise Generator ship rebuilt (x64) by the user's decision of 2026-11-06, for private use, under the one recorded exception (below); Delay, NiNjA dElaY and Cross Delay are built for the comparison only, and the package keeps upstream's binaries of them |

Every machine also builds on Buzz's `MachineInterface.h` and MDK, which allow freeware machines only (§4.4 of cross-platform.md). The package's `Licenses\NOTICE.txt` names each rebuilt DLL with its licence, its source (`ExternalMachines\source\buzztrax-buzzmachines\<author>\<machine>` and its files), Buzztrax upstream at the vendored commit, and what was changed (the entry's `patches`, the machine's `fix`); the licence texts ship beside it (`Licenses\LGPL-2.1.txt` from `FSM/COPYING`, `Licenses\GPL-3.0.txt`: the Buzztrax tree has no GPL text, so the FSF's from `source/thepedal/pedal-drum-matrix/LICENSE`), from the `licenseTexts` table of `machines.json` by the id each entry's `license` starts with, so a native added later gets its licence's text too, and one whose licence has no text there ships upstream's binary. `README-FIRST.txt` lists the machines built from source by licence; the package job's log, step summary and an annotation list the licence files in the zip. The exception for the Jeskola three is below.

**Rebuilt against shipped** (`tools/native-compare.py`, the package job's step "Natives, rebuilt against shipped"). Every build of a machine plays the same cases side by side in one render through `ReBuzz.exe --agent`, each instance probed (`render`'s `probes`), so all of them hear exactly the same input: effects each fed the null test's Neo FM6 phrase (a stab, a bass note, a high note, then 2 s of silence for the tails), generators each playing the same notes (two chords on Infector, a note per track: below; kicks on KickXP) or triggers (Trilok, Noise Generator). Cases: the defaults; every global and track parameter a quarter and three quarters of the way up its raw range (FSM Infector's at 0.3 and 0.4, Noise Generator's but its Attack: below); and, for effects, the defaults with the input 40 dB down. A case where the reference or the package's build peaks under −60 dBFS compares (near) silence, proves little by matching and fails the gate (the quietest that plays, Chorus2 with the quiet input, peaks at −52.3 dBFS). The builds: upstream's binary (copied from `RunTime/Gear` as "<gear> shipped", the reference), the package's own, and the rebuilt x64 and x86 ones. A build matches when every case is bit-identical, or differs by at most −100 dBFS: the null test's line across platforms (cross-platform.md §4.3), under a 16-bit step (−96 dBFS), where float rounding differences between compilers, x87 and SSE2 and C runtimes show up (−150 … −105 dBFS here). The step fails when a machine the package ships rebuilt doesn't match, or a pitched generator doesn't play the written pitch.

Measured in runs 41958446240 (x64 and x86 builds as shipped; x87 builds, `/arch:IA32`, and `/O2` builds for comparison in that run only) and 41960698592 (the package with the rebuilt machines in Gear, four instances of upstream's wahs):

| Machine | x64 rebuild against upstream's binary | x86 rebuild (SSE2) | x87 build (its largest difference) | Ships |
|---|---|---|---|---|
| FSM Infector | identical, every case | identical | −79.9 dBFS | rebuilt (x64) |
| FSM KickXP | identical | identical | −105.6 dBFS | rebuilt |
| FSM PhatMan | identical | identical | −68.4 dBFS | rebuilt |
| FSM PanzerDelay | identical | identical | −139.6 dBFS | rebuilt |
| FSM Chorus2 | identical | identical | −55.4 dBFS | rebuilt |
| FSM WahMan | as one of four upstream instances in every case (which disagree among themselves: below) | the same bits as the x64 | — | rebuilt, with the fix (below) |
| FSM WahManPro | with the fix: +8.1 dBFS from upstream's (its five instances agree) at the defaults and with the quiet input, identical at the other settings | the same bits as the x64 | — | rebuilt, with the fix (since 2026-11-06, below) |
| FSM WahPro2 | identical to upstream's in every case (one of its four instances diverged: below) | the same bits as the x64 | — | rebuilt, with the fix |
| Oomek Aggressor 303 | identical | identical | −79.0 dBFS | rebuilt |
| Oomek Exciter | identical | identical | −126.4 dBFS | rebuilt |
| Oomek Masterizer | identical | identical | −130.5 dBFS | rebuilt |
| Jeskola Delay | x64 and x86 against upstream's 32-bit: within −150.5 dBFS | | −150.5 dBFS | upstream's (not bit-identical, below) |
| Jeskola Cross Delay | differs entirely: +3.0 dBFS at the defaults, the echoes another shape (Buzztrax's rewrite) | | | upstream's |
| Jeskola Distortion | identical | identical | identical | rebuilt, x64 (licence exception, below) |
| Jeskola NiNjA dElaY | within −119.4 dBFS | | −150.5 dBFS | upstream's (not bit-identical, below) |
| Jeskola Trilok | identical | identical | identical | rebuilt, x64 (licence exception) |
| Jeskola Noise Generator | identical | identical | identical | rebuilt, x64 (licence exception) |

So upstream's x64 FSM and Oomek binaries are Buzztrax's sources built with its makefiles: today's compiler reproduces them bit for bit, `/Od` and `/O2` alike, and the x86 builds (SSE2, cl's default since VS 2012) compute the same bits. The x87 builds don't: their wider intermediates move the last bits, and feedback and modulation spread them (Chorus2, PhatMan). Jeskola's originals are reproduced by Buzztrax's sources, x86 and x64, but Cross Delay's. Three machines gain an x64 build in the package, where upstream has only Jeskola's 32-bit ones: Distortion, Trilok and Noise Generator, whose x64 rebuilds are bit-identical to the originals in every case, ship as `*.x64.dll` in their place (`"shipPlatform": "x64"`; below); Delay's and NiNjA dElaY's match within −150.5 and −119.4 dBFS, inside the gate's line but not bit for bit, and Cross Delay's not at all: those three keep upstream's 32-bit binaries.

**Fixes.** One real bug, in the three FSM wahs: the cutoff their filter glides from (`CurCutoff`) is never set before the first block, nor is WahManPro's LFO phase when its default (255, no value) leaves it unset, nor WahMan's filter coefficients, which its `DoWork` copies before it first computes them (so its first block filters with whatever they held). Each starts as whatever the heap held, so two instances of upstream's binary can play the same song differently, and a bad start runs the filter unstable. Measured with four instances of upstream's x64 binary side by side (run 41960698592): WahMan's instances split two or three ways in three of its four cases (up to +5.6 dBFS apart at the three-quarters setting, 12.7 dB over the signal); WahPro2's fourth instance diverged at its defaults (the engine's guard silenced it; U2's profile had seen its test signal reach +82.9 dBFS there, and once infinity); WahManPro's five agreed in every case. The fix, in `Init`: they start at 0, where a zeroed allocation started them (the source's own `InitTrack` resets the phase to 0 for tracks added later). With the cutoff alone set, one of WahMan's three rebuilt instances still came out apart with the quiet input (run 41963250135): the coefficients, set since. After it every instance of the rebuilt WahMan and WahPro2 renders the same bits, x64 and x86 alike, the same as the majority of upstream's instances in every case; the package job checks it (below), and WahPro2's profile now reads a response and a 22 ms decay at its defaults, where its test signal had blown up. WahManPro's upstream instances mostly start its LFO somewhere other than 0 (its defaults: +8.1 dBFS apart, the sweep shifted), though not always (below: at its defaults, one or two of four started it at 0 in 3 of 13 runs), so its fix moves the sound of a song that leaves Set phase at no value wherever upstream's instance started elsewhere: built and measured in U7, it ships since the user's decision of 2026-11-06, gated as WahMan's and WahPro2's are.

**The gate on the fixed machines.** It first held each fixed build to at least one of four upstream instances in every case, and that flaked: in run 41983193582 all four of WahMan's agreed at its defaults, on a start that wasn't 0, and the fixed build came out +0.2 dBFS (1.7 dB) from them. Each native instance runs in a host process of its own and inherits whatever that process's heap left in the block `new mi` gets, so upstream's sound depends on it, and no case can be made where it doesn't: run 42000620560 played each case again after 2 s of silence, for the glide to reach its cutoff, and WahMan's upstream instances still split, in one case three of four against the zeroed start's sound, since a start such as 1e10 never moves (8.3 cutoff units a step are under its float step: the filter stays at 20 kHz). Over that run's 13 comparisons (the step and 12 repeats), an upstream instance of WahMan rendered the zeroed start's sound in 52 % of the cases from the start and 58 % after the silence, and none of four did in 3 of the 52 cases from the start: the old gate's failure, one run in four. So the gate holds the fixed build to upstream's own binary on a zeroed heap, which is deterministic:
- **Upstream's binary on a zeroed heap.** `build-natives.ps1 -ZeroHeap` copies upstream's x64 binary of each fixed machine with its `KERNEL32.dll` import renamed `ZEROHEAP.dll` (`tools/native-zeroheap.py`: the name patched in place, six bytes; the code untouched), and builds `ZEROHEAP.dll` (`tools/native-zeroheap.cpp` and the `.def` the script writes), which forwards every function those binaries import to kernel32 but `HeapAlloc`, which it calls with `HEAP_ZERO_MEMORY`. Their C runtime (static, VS 2015) allocates everything through `HeapAlloc`, the machine object among it, so each instance starts its unset state at 0, as a fresh allocation did. The comparison puts `ZEROHEAP.dll` beside `bin64\ReBuzzEngine64.exe` for its run and plays two instances, which must render the same bits.
- **The fix is that start, and the rebuild upstream's code.** The package's build renders the same bits as those instances in every case. A rebuild that doesn't play upstream's DSP fails it, and so does a fix that starts anywhere else or changes more than the start.
- **One sound.** The package's build and the rebuilt x64 and x86 ones, each its own instance and process, render the same bits in every case: the fix leaves nothing unset.

Measured in run 42005475959, the step and 12 repeats of it: in every one, for all three wahs, the package's build, the rebuilt x64 and x86 ones and both instances of upstream's binary on a zeroed heap rendered the same bits in every case, while upstream's instances as they come rendered that sound in 50 % of WahMan's cases (none of four in 4 of 52: the old gate would have failed 4 of the 13), 94 % of WahPro2's and 56 % of WahManPro's.

Which of upstream's instances as they come the fixed build matches is reported ("as 2/4, 3/4, 4/4, 1/4 of upstream's instances as they come"), not judged. A failure names the case, how far apart and from which second, and which check (the zeroing not taking; the rebuild or the fix; state left unset or x86 against x64). `tools/test_native_compare.py` checks the verdicts on the groupings the job has seen, run 41983193582's among them, and the patch on the binaries in `RunTime/Gear`. WahManPro, shipped rebuilt since 2026-11-06, is held to the same gate: its builds render the same bits as its upstream binary on a zeroed heap, which starts the LFO at 0 as the fix does, in every case (run 42005475959's 13 comparisons, and every full run since); its instances as they come mostly start it elsewhere (above), though not always (at its defaults, none of four in 10 of those 13 runs, one or two in the other 3; none of four in run 42181254466).

Looked for and not found, measured: NaN or crashes (the profile's level tests and sweeps on all 17, U2); slowdowns after silence (the profile's 20 s after the input stops: none over twice its playing time, KickXP the most at 1.7×); tails cut by a silence report: these machines report silence only when the host calls them without input (`WM_WRITE`), which ReBuzz never does (it calls effects with `WM_READWRITE`, §12.13), so their idle paths don't run in ReBuzz and nothing cuts a tail. Jeskola Delay's idle time divides by log|feedback|, which is zero at ±100 %: an infinite time cast to an int; the same path, not reached in ReBuzz, and the machine not shipped rebuilt.

**Shipped by the user's decision (2026-11-06).** Against the earlier plan (Jeskola's six kept as upstream's binaries for their licence, WahManPro's fix built but not shipped), the user chose, for private use, to ship (a) the x64 rebuild of each Jeskola native the gate proves bit-identical to upstream's shipped 32-bit binary in every case, and (b) WahManPro with its fix.
- **Which Jeskola machines.** Distortion, Trilok and Noise Generator: bit-identical in every case in runs 41958446240, 42012534937 and 42181254466. Delay (−150.5 dBFS at its defaults, −192.7 with the quiet input, identical at the other two) and NiNjA dElaY (−119.4 … −160.5 dBFS) are within the gate's line, not bit for bit (last-bit rounding differences, which their feedback carries on), so they keep upstream's binaries, as their `machines.json` notes say; each would ship with `"ship": "rebuilt"`, `"shipPlatform": "x64"` and the exception. Cross Delay, measured again, doesn't play as upstream's (+3.0 dBFS at the defaults, as loud as the signal at the other settings; 56.4 dBFS apart over `FSM_Infector_Demo.bmx`): it would need Jeskola's original source.
- **The one exception to the licence gate.** Their sources carry no licence, so `"redistribute": false` stays. Each entry has `"licenseException": "no licence in source; shipped by user decision 2026-11-06 for private use"`; build.ps1 step 4b records that text (`$NativeLicenceException`) and ships a rebuilt native whose licence has no text only when its `licenseException` is exactly it (case and all); any other `licenseException` is a build failure, and upstream's binary stays; without one, a native ships rebuilt only with `"redistribute": true` and its licence's text, as before. `tools/test_native_compare.py` checks that the exceptions in `machines.json` are that text, on the three unlicensed x64 rebuilds, and that the profiler's copy of it is the same. `Licenses\NOTICE.txt` opens with "NOT FOR PUBLIC RELEASE AS IS: Jeskola Distortion, Jeskola Trilok, Jeskola Noise Generator are built from sources that carry no licence …, re-cleared with their author, or reverted to upstream's binaries (machines.json: "ship": "upstream")", and names the exception under each of the three DLLs; `README-FIRST.txt` says the same in two lines, and the build step annotates it in every full run.
- **The x64 builds in the 64-bit host.** The app runs a `*.x64.dll` in `bin64\ReBuzzEngine64.exe`; `tools/native-songs.py` checks each shipped file is an AMD64 DLL (its PE header) and that the app loaded it (`gear_info`). The package's own checks ran on them: the machine profile (profiled from `Gear\…\*.x64.dll`, the same measurements as the 32-bit originals' but timings; merged), the curated check (Distortion's curated manifest, Trilok's and Noise Generator's generated ones), the factory presets (Distortion's four), `ReBuzzTests` (`AgentReplaceTests`: Distortion → Neo Drive within its tolerance, as before) and the gate; the null test has no native case.
- **Songs saved with upstream's binaries** (`tools/native-songs.py`, the package job's step after the comparison). Phase A puts upstream's binary of every native the package ships rebuilt into Gear in place of the package's (Jeskola's 32-bit DLLs, the FSM and Oomek x64 ones) and builds three songs with them through `NeoBuzzLive.exe --agent`: every machine fed by the null test's Neo FM6 phrase or playing its notes or triggers, at its defaults, or with its global and track parameters a quarter (three quarters) of the way up their raw ranges and a second track the other way where it has tracks; its first parameter automated in its pattern; each saved as `.bmx` and `.bmxml`. Each saved song is opened again, rendered with every machine probed, its parameters read on every track, and saved once more; so are the nine classic Buzz songs in `ExternalMachines/source` that use these machines (`Oomek_Aggressor_demo.bmx` with Distortion, `Masterizer_test.bmx` and `CyanPhase_DTMF-1_Demo.bmx` with Noise Generator, the Infector, Aggressor and Exciter demos), start to end (192 beats at most). Phase B opens the same files with the package's Gear. For each machine and song: the file the app loaded, every parameter's raw value, the probe's bits, and the song saved again (the same file; BMXML but its root's stamps). It fails on any of these for a machine shipped rebuilt, and on one that sounds in none of the songs it builds: a silent probe proves nothing by matching (run 42181254466 matched its first song on silence: a native machine set up before the engine's first tick in a process was told 0 Hz and 0 BPM, so Jeskola's delays looped for ever in their first Work and FSM Infectors played nothing, which the songs' first render once worked round; their Init gets the engine's own master info now, `NativeFirstTickTests`; FSM Infector is silent at both fractions of its ranges, so the songs have one at the defaults; its sound and what it feeds in a classic song are reported, not judged: its fast releases depend on the process, below); a fixed machine's sound (upstream's from heap garbage) and the x64 rebuilds of the three natives kept as upstream's binaries (`--candidate`) are reported. Measured in run 42187730991 (in 216 s): in the six files built here every one of the 14 machines shipped rebuilt loads the package's file with the same raw value of every parameter on every track, and every song saved again is the same file; the eleven without a fix render the same bits as upstream's binaries wherever they sound (FSM Infector at its defaults only), the Jeskola three among them, at peaks of +6.0 (Distortion), −0.4 (Trilok) and −6.0 dBFS (Noise Generator); WahMan and WahPro2 the same bits as upstream's instance in that run, WahManPro 7.4 dBFS apart at its defaults (upstream's instance started its LFO elsewhere: the fix's change, reported). In the classic songs: Distortion in `Oomek_Aggressor_demo.bmx` (132 beats, behind an Aggressor 303), Noise Generator in `CyanPhase_DTMF-1_Demo.bmx` (96 beats), the five Infectors, PanzerDelay and a PhatMan in the Infector demo, the Aggressor in both of its songs, the Exciter in its three and the Masterizer: the same bits and parameters; Noise Generator in `Masterizer_test.bmx` is silent in its first 192 beats. The candidates: Delay within −144.5 dBFS at its defaults and the same bits in `Masterizer_test.bmx`; NiNjA dElaY within −115.7 dBFS; Cross Delay 8.2 dBFS apart at its defaults and 56.4 dBFS in the Infector demo, where the PhatMan it feeds then diverges.
- **WahManPro** ships rebuilt, its fix in `Init`: the gate above holds it to upstream's binary on a zeroed heap; its manifest says the LFO phase and the glide start at 0, and its profile is the fixed build's (merged: hot at its defaults +4.3 dBFS, where upstream's instance measured +6.6). A song that leaves Set phase at no value sweeps from phase 0 now; one that sets it plays as before.

**Manifests from code.** `tools/native_manifests.py` (standard library only; a sibling of `tools/source_manifests.py`, whose fitting it uses) writes the manifests of the eight that had drafts: FSM WahMan, WahManPro, WahPro2, Chorus2 and PhatMan, Jeskola NiNjA dElaY, Noise Generator and Trilok, 52 parameters. It reads the C++ as ReBuzz builds the parameters: each `CMachineParameter` (type, name, description, range, default, flags), the parameter list's order, `MacInfo`'s counts and track limit (`#define`s evaluated); a switch runs 0…1, a note is left out, a track parameter without `MPF_STATE` is played and left out, and a default outside its range is clamped as ReBuzz takes it (WahManPro's Set phase: declared 255, range 0–127). Units and value texts come only from `DescribeValue`: the script finds the cases its `switch(param)` has (nested switches aside) and the file names each with its kind, the case's expression and the source text it rests on; the generator evaluates the expression at every raw value and fits it exactly or fails, and fails when DescribeValue has a case the file doesn't map or the file maps one it doesn't have. A parameter without a case is a bare number (`int`, its own texts), a switch Off/On. The result is `status: "generated"` with `source` naming the file, the profile, health, type, notes and safe ranges carried over, and the server shows them in exact units as it does the other generated manifests. `python3 tools/native_manifests.py` checks the committed ones against the code (`--write` writes them, `--table <gear>` prints what any of the 17 declares); `test_machine_infer.py` runs that check in Fork CI, and `curated-check.py` checks them against the machines' own texts in the package run.

What they say that the drafts didn't: the LFO rates exact (0.05 × 800^(v/255) Hz; PhatMan's and WahPro2's 240–254 are tempo-locked tick counts, as their own texts say), PhatMan's dry and wet levels in dB with −inf at 0, its feedback bipolar and its delays exact in ms, NiNjA dElaY's feedback and wet level exact and its **f. bandwidth**, which the draft had left out (the binary has it: "1cps" … "10000cps"), choices for the filter types and LFO shapes, Chorus2's rate and stereo phase; Chorus2's delays are its text's samples in seconds at 44.1 kHz, the rate ReBuzz runs at (its Work delays 2 + 1120.5·v/240 samples where its text says 1102.5: 1.6 % more, which the Neo Ensemble pair already corrects). The bare numbers stay bare where DescribeValue says nothing (the wahs' cutoff and resonance, Noise Generator's times, Trilok's tone and decay).

The curated manifests stay curated; checked against the code (ranges, defaults, track counts and every DescribeValue formula), they were right but for four endpoints fitted to rounded texts: Oomek Exciter's Pre Cut and Post Cut (30.228 … 6990.500 Hz, the code 30.245 … 6990.483 at 44.1 kHz) and Oomek Masterizer's Lo | Mid and Mid | Hi Split (15.621 … 819.856 and 1023.580 … 8042.287 Hz, the code 15.622 … 819.852 and 1023.564 … 8042.292), now the code's. Their dB tables, rounded to 0.01 dB, stay. The Jeskola NiNjA dElaY → Neo Delay pair lists f. bandwidth as unmapped (Neo Delay has no band-pass), and its evidence no longer says the binary lacks it.

**notes_write plays the written pitch** on the three pitched generators among them, as an agent's call does (the manifest's pitch offset applied), heard by `analyze` (YIN) or the profiler's harmonic sieve where YIN hears none: FSM Infector A3, A4 and A5 as written (−2.0, +0.2, −0.4 cents; its offset +12), FSM KickXP (+0.9, +0.4, +0.2) and Oomek Aggressor 303 (sieve: +1.2, −0.1, −0.3). Trilok and Noise Generator play from triggers and have no pitch.

**§12.6's "Done when"** for the natives: they build in the package (all 17, x86 and x64); every machine in the package has a manifest (the rebuilt ones keep theirs; `AgentHeadlessTests` and `CuratedManifestTests` check it); notes_write plays the written pitch on every pitched generator among them (above); the health report lists every exception: the hot ones (loud but steady at a named setting: FSM PanzerDelay, PhatMan and the wahs, Jeskola Delay, Cross Delay, Distortion and NiNjA dElaY, Trilok, Aggressor 303), each with its setting, and Jeskola Delay's and NiNjA dElaY's safe ranges; none of the 17 diverges, crashes or slows down after silence.

**Tests.** `tools/test_native_compare.py` (the gate on the fixed machines, above; the licence exception: one text in build.ps1, the profiler and `machines.json`, on the three unlicensed x64 rebuilds only; native-songs' verdicts on a machine in a song, its Gear swap, its plan, PE platforms, the classic songs it finds and what feeds a machine); `tools/test_machine_infer.py` (`NativeManifestsFromSource`: the committed manifests against their source; declarations as ReBuzz reads them: switches, played parameters, a clamped default, DescribeValue's numbering; cases found past nested switches; a case not mapped, changed code or a case that isn't there caught); `ReBuzz.AgentTests` (`CuratedManifestTests`: the 8 among the manifests from source; `SuccessorTests` with f. bandwidth; `GuideTests`' draft manual now Jeskola Organ's); the package job's comparison and pitch check (above), `tools/native-songs.py` (songs saved with upstream's binaries, above), `curated-check.py` on the generated manifests, and the profiler, curated check, factory presets, eval and `ReBuzzTests` on the rebuilt DLLs in Gear.

**Left.**
- The Jeskola licence: Distortion, Trilok and Noise Generator ship under the private-use exception, and must be cleared with Jeskola (or Buzztrax's word on one), or set back to `"ship": "upstream"`, before any public release; a licence would let Delay and NiNjA dElaY ship the same way if their −150.5 and −119.4 dBFS are accepted (the gate's line takes them; songs: within it as well, reported by native-songs). Cross Delay would need Jeskola's original source.
- **Silent cases** (fixed 2026-11-06). FSM Infector was silent at a quarter and at three quarters of its raw ranges, Noise Generator at −83.8 and −85.2 dBFS, so those four cases compared silence and passed. Measured in run 42272808783 (`tools/fu1-sweep.py` on the branch fix/followups-1-diag): every parameter at 0.1, 0.2, 0.25 and 0.5 … 0.9 silences Infector, 0.3 plays at −0.3 dBFS and 0.4 at −21.9; each parameter alone at 0.25 or 0.75 plays and every one but one doesn't, so it takes two or more together (not traced further). Noise Generator's Attack (ms, to 65535) at a quarter is a 16 s fade-in, at three quarters 49 s; every parameter but Attack plays at −12.2 and −2.6 dBFS. So Infector's two cases are at 0.3 and 0.4 of its ranges (`CASE_FRACTIONS`), Noise Generator's leave Attack at its default (`HOLD`), and a case under −60 dBFS fails the gate. No other case was under −52.3 dBFS.
- **FSM Infector's fast releases depend on the process** (upstream's binary and the rebuild alike). A note on a track whose last one still sounds copies that voice to a spare channel for a 1024-sample release, with no track (`pTrack` NULL); `DoWorkChannel` renders such a channel with `&pmi->Tracks[MAX_TRACKS+1]`, one past the end of `Tracks[MAX_TRACKS+1]`, so its vibrato and LFO fields read the machine's next members: `Vib1Depth` is the low half of `usertables[0].m_levels[4].m_pData`, a pointer from the heap (the disassembly of upstream's x64 binary has the same layout as a gcc build of the source: `CurCutoff` at 0x2f98, `userwaves` at 0x9080). Where those 32 bits are a NaN or an infinity (exponent all ones: about one process in 128 under ASLR) the release's frequency is NaN and so are its samples, which `Work` zeroes; any other value is multiplied by `sin(Vib1Phase / SamplesPerTick)`, where `Vib1Phase` reads a level's size (2048) as a float, 2.9e-42, which the division takes to 0 below about 161 BPM at 4 ticks per beat (above it, to 1.4e-45, and a value over about 6e37 moves the pitch too). `CurLFO2` reads a scan rate (0.0039), the same in every process. Shown on Linux (2026-11-06, a scratch host that plays the source's build on random notes and parameters, not kept): with the heap moved so that pointer reads as NaN (0x7fae2da0, 0x7fde2da0, 0xffae2da0) the render differs, with it reading as anything else (2.5e38 among them) it is the same bits; the source has a second such read, `NotePortFrequency`, never set before a slide (command 03) without a note, which valgrind finds. In the package job, FSM_Infector_Demo.bmx with the package's Gear rendered differently in 1 of 12 fresh processes (run 42272808783: Infector3 −24.9 dBFS from 26.67 s, and what it feeds), and native-songs failed 2 of 4 runs on Infector2 in that song (−41.4 dBFS from 41.91 s). So native-compare's chords give each note a track of its own (none is played over a sounding one, so that path never runs and its cases are deterministic), and native-songs reports, not judges, the sound of Infector and of what it feeds in a classic song (`PROCESS_DEPENDENT`); its parameters, the file it loads and the song saved again are still judged there, and its sound in the songs built here (the same chords) is. A fix in the source (a track of its own for those channels) would change how releases sound wherever an LFO 2 amount isn't centred (`CurLFO2` reads a level's scan rate), so it is not made.
- The rest of the Buzztrax collection (Fuzzpilz, Matilde, Arguru, Geonik and the others) stays source only; upstream ships few of them.
- Oomek Masterizer returns false (silence) when its Input gain is 0 (−inf dB) whatever the mode; ReBuzz keeps a native's buffer as the machine left it (§12.13), so there it passes its input through instead of muting. Read from the code, not measured; upstream's binary does the same, and the fix belongs with the engine's silence handling (§12.13).
- R1 (cross-platform.md §4.2): what this says about building them for macOS and Linux is there.

### 12.15 U6 for the Pedal synths, as built

Tier 3 (§12.4) for the five Pedal synths, the part §12.13 left: MIDI input for Pedal Juno106, SH101, FM, M1 and Tracker, with the denormal guards, parameter glides and silence reporting U6 gave the Pedal effects, and the Neo measurement suite on them; manifests generated from code for Pedal Tracker, Chord, LFO and Peer Control, the last drafts among the Pedal machines; and Juno106's and SH101's own banks levelled. No parameter, range, default or saved datum moved, and nothing in the engine changed: the null test stays bit-identical (its cases use only the Neo machines).

**MIDI input.** Each synth has `MidiNote` and `MidiControlChange`, which `ManagedMachineHost` finds by reflection, so a MIDI keyboard and a pattern's MIDI column (`notes_write`, `midi_play`) play it beside its Note track. ReBuzz calls them on the MIDI thread or the audio thread; they go into a queue (`MidiIn.cs` in each synth's folder: a 1024-entry ring, a lock on the writing side, nothing allocated and no wait on the audio thread) that the synth applies at the start of its next block, after the block's track notes. A track's note on a voice takes it from MIDI; a track's note plays as it did, bit for bit.

| Synth | MIDI notes | Velocity | Mod wheel |
|---|---|---|---|
| Pedal Juno106 | any of its 6 voices: a free one, the highest first (tracks take the first), else the one released longest ago, else the oldest | the voice's level, velocity / 127 | vibrato as DCO LFO, up to its maximum |
| Pedal SH101 | monophonic, the last key down, back to a key still held when it is let go (its pitch, no retrigger; Glide slides as between overlapping notes) | the level, velocity / 127 | vibrato up to ±1 semitone, or VCO Mod's if more |
| Pedal FM | any voice up to Voices (8 by default, 16 at most), picked as Juno106's | what the Velocity column does, for that note: velocity / 127, through each operator's VelSens | vibrato as LFO Pitch, up to its maximum |
| Pedal M1 | any of its 8 voices, picked as Juno106's | what the Volume column does (127 full), through Amp Vel and Filter Vel | vibrato as LFO1 to Pitch, up to its maximum |
| Pedal Tracker | any of its 16 voices, the last first, playing the wave track 1 last chose (wave 1 until it chooses one) | through the Volume column's curve, (velocity / 127)² | none (no LFO: its vibrato is command 04) |

All five: pitch bend ±2 semitones (every voice; the Tracker's MIDI voices), the sustain pedal (CC 64) holds released notes until it comes up, All Notes Off (CC 123) releases them and All Sound Off (CC 120) also fades what sounds, and the transport stopping releases MIDI notes (a track's note on SH101 and FM rings on after Stop, as before). Juno106 bends by an exact ratio (its fast exponential is a few cents out between octaves). Measured in `NeoBuzz.PedalTests` (48 kHz, the defaults):

| | Juno106 | SH101 | FM | M1 | Tracker |
|---|---|---|---|---|---|
| A4 through MIDI at 127, against a track's note | 440.14 Hz on voice 6, its level within 0.07 dB (track 1's voice: 437.34 Hz, −10.4 cents, as before) | the same samples | the same samples | the same samples | the same samples |
| Velocity 100, 64, 20 against 127 | −2.08, −5.95, −16.06 dB | the same | the same | −1.25, −3.25, −6.57 dB (Amp Vel 80) | −4.15, −11.90, −32.11 dB |
| Bend at the top, the bottom | +200.0, −200.1 cents | +200.0, −200.2 | +200.0, −200.2 | +199.9, −205.3 (G4 played is there too: its own exponential) | +200.0, −200.2 |
| Mod wheel at the top: the pitch spreads over | 179 cents | 171 | 197 | 188 | — |
| C4 E4 G4 as MIDI: each fundamental against the note alone | within 0.06 dB | (one voice: the last key) | 0.00 dB | within 0.04 dB | within 0.03 dB |
| A note let go with the pedal down, 1 s on | −0.6 dB (held) | −0.3 dB | −0.2 dB | 0.0 dB | 0.0 dB |

SH101's key stack: A3, then E4 over it (329.6 Hz), E4 let go: A3 again (220.0 Hz) without a retrigger. Keys 0 and 127, velocities 1 and 127 and both controllers at their ends stay finite on all five.

**Silence, denormals and work after silence.** The decaying states that rounding left among the denormal numbers are set to zero under 1e-15 (−300 dBFS), sample by sample, as §12.13 did: Juno106's HPF and its chorus's three low-passes, M1's state-variable filter. A synth's `Work` returning false makes ReBuzz play silence there (the samples aren't copied), so each says it only for a block of exact silence and once its tail has ended: FM and M1 skip a voice once it has finished (its envelopes done, no trigger waiting), where a released voice had stayed active for good; the Tracker returns false only when no voice is active or its master mute has faded all the way. Measured on the old and new sources alike (`ItSaysItIsSilentOnceItIsAndOnlyThen`, `SilenceAfterSoundSettlesToExactZero`, `SilenceCostsNoMoreAfterNotesThanBefore`, `ProcessingAllocatesNothing`; work in ms of the machine's own `Work` per second of audio, interleaved runs, best of each group):

| Machine | Before | After |
|---|---|---|
| Pedal Juno106, Chorus II and HPF 3 | never exact zero: 1,420,863 denormal output samples in 30 s; never said silent; 23.87 ms/s after silence against 1.67 before its first note (14.3×) | exact zero 0.38 s after the last note off, no denormal sample; says so from then; 1.64 against 1.64 |
| Pedal Juno106, Chorus III and HPF 1 | never exact zero (1,416,516 denormal samples); never said silent | exact zero and says so from 0.41 s |
| Pedal FM | never said silent (its output exact zero from 11.34 s); 3.71 ms/s after silence against 0.36 (10.4×) | says so from 11.34 s; 0.37 against 0.37 |
| Pedal M1 | never said silent; 7.55 ms/s after silence against 1.42 (5.3×); with a 0.1 s wave that ends while the note holds, 20 denormal samples | says so from 0.16 s; 2.32 against 1.56 (1.5×: its finished voices' LFOs still step, so a later note's random values are as before); no denormal |
| Pedal Tracker | muted while it played, said silent while its fade was at −28.6 dBFS (heard anyway: a multi-output machine's outputs are used whatever it says); 21,632 bytes allocated per 2 s (a string formatted per trigger) | says so only for exact silence; 0 bytes (the trigger's trace formats on demand) |
| Pedal SH101, and the others at their defaults | already right: SH101 silent and saying so from 0.76 s, Juno106 0.37 s, Tracker 0.01 s; 0.31 ms/s after silence against 0.31 | the same, bit for bit |

With MIDI notes as well as tracks the figures are the same. Nothing allocates per block on any of the five, notes, MIDI and moving parameters included (`AllocationCheck`, as the Neo machines' tests). Every `Work` and the voice render loops (FM's `Voice.Render`, the Tracker's `VoiceRender.Render`, M1's LFO skip) are `AggressiveOptimization`: in a song played through rebuzz-headless with `DOTNET_JitDisasmSummary=1`, they compile once fully optimised and no audio-path method needs an on-stack replacement (the Tracker's sinc tables do, in its constructor).

**Glides.** A level, a mix, a pulse width, a cutoff or a resonance now glides to its new value in a straight line over 20 ms, sample by sample, and lands on it exactly (the `ParamGlide` struct in each synth; SH101's and FM's own Glide parameters are portamento): Juno106 VCA Level, DCO Sub, Noise and PW, VCF Freq (in octaves) and Reso; SH101 Volume, the four mixer levels, PWM, Cutoff and Resonance (and a new note's velocity level, over 2 ms); FM Output and the four operator levels (per sample through the voices); M1 Volume, OSC1 and OSC2 Level, Cutoff and Resonance; the Tracker's Master. A setting that doesn't move is the same number as before. The first block, a new sample rate, and a block that starts with no voice sounding start at the setting: a preset loaded or a parameter set between phrases plays its next note at once, as before, with no sweep of its first 20 ms (`ASettingChangedInSilenceStartsTheNextNoteAtIt`: without that rule, the next note's first 50 ms were −1.6 to −17.9 dB from the setting's; with it, the same samples).

Stepped mid-note (`ASteppedParameterGlides`), a gain as the largest change of the output's gain between two samples (least squares over 17 samples about each), the others as what the step changes in the output over its first 5 ms against 20–40 ms on:

| Parameter | Before | After |
|---|---|---|
| Juno106 VCA Level 100 → 50 | 0.50 dB | 0.022 dB |
| SH101 Volume 96 → 48; Pulse, Saw, Sub Lvl | 0.68; 0.28, 0.36, 0.32 dB | 0.018; 0.022, 0.022, 0.024 dB |
| FM Output 100 → 60; Op4 Level 127 → 60 | 1.57; 0.67 dB | 0.037; 0.026 dB |
| M1 Volume 100 → 50; OSC1 Level 100 → 40 | 0.47; 0.59 dB | 0.009; 0.013 dB |
| Tracker Master 0x8000 → 0x4000 | 0.52 dB | 0.009 dB |
| Juno106 VCF Freq, Reso, DCO PW, DCO Sub into the filter's soft clip | +0.3, +0.2, 0.0, −1.7 dB | −13.2, −13.5, −8.1, −17.4 dB |
| SH101 Cutoff, Resonance, PWM | 0.0, +0.3, −0.4 dB | −13.7, −9.4, −7.7 dB |
| FM Op3 Level (a modulator) | +0.1 dB | −15.0 dB |
| M1 Cutoff, Resonance | −0.9, −0.8 dB | −23.1, −12.5 dB |

(A moving edge, a pulse width's, comes in as the square root of the glide, so its figure is the smallest.) After the glide each plays what a machine set there from the start plays: the same samples, or within −61 to −109 dBFS where the step went into a filter whose state takes its time.

**What stays bit for bit.** The old and new sources rendered side by side through their Note tracks (`$S/synthcmp`, not in the repo: the harness on each source tree): the defaults at blocks of 256, 117 and 64, every parameter alone at each end, 12 settings whose state carries across notes (M1's sample-and-hold LFOs, FM's glide into a released voice), and every preset of the banks (Juno106's 48, SH101's 22, FM's 32 and our 6), 477 renders: 455 the same bit for bit; the other 22, Juno106 with its chorus or HPF on, differ only under 1e-15 of full scale (5.6e-16 at most: the flushed tails). Blocks of 256 and 117 give the same output on SH101, FM, M1 and the Tracker; Juno106's drift steps once a block (its pitch 0.001 cents and its level 0.006 dB apart), as before.

**Found and left as they are** (a fix would change what songs play):
- Juno106's default patch rings: VCF Env Amt 80 opens the filter to its ceiling (0.45 × the sample rate), where VCF Reso 30 sustains a tone at 0.41 × the rate (18.2 kHz at 44.1 kHz, 19.8 kHz at 48), about 80 % of the output's energy and so of its peaks and loudness meters. Two voices' tones can cancel: the profile heard one voice for a MIDI chord (+1.2 dB for two notes; their fundamentals add as they should). None of its factory presets does it. Its manifest says so.
- Juno106's voices 1, 3 and 5 play about 10 cents flat (its fast exponential falls short just under an octave, on their negative detunes); its drift steps by blocks.
- M1's fast exponential is up to 5 cents out between octaves (G4 at −205.3 cents from A4, bent or played); FM's LFO moves once a block.
- FM with Glide on: a track's note after a released note on the same voice takes the legato path and stays silent.
- SH101's and FM's track notes ring on after Stop (MIDI notes don't).

**Manifests from code.** `tools/source_manifests.py` now reads a declaration's constants and enum members (`PERIOD_MIN`, `(int)Interp.Cubic`) and a wave number's fixed range (1–200, default 1, whatever it declares), and generates Pedal Tracker's (20 parameters and 6 per track: Master in % of unity, Mute Inertia in seconds, Volume as its (v / 128)² curve in dB, the commands and arguments as integers), Pedal Chord's (12 per track: 51 voicings, 6 modes, ticks, percent), Pedal LFO's (8: Phase in degrees, 127 being 357.2°) and Pedal Peer Control's (Inertia in ticks, Value in percent): 19 manifests from source, every value checked against the code. The code proved one of the machine's own texts wrong: Pedal Tracker's Mute Inertia declares the engine's Milliseconds descriptor, which shows the raw value over its maximum (0.0 … 1.0 ms), while the code takes the raw value as milliseconds (0 … 500 ms); its manifest overrides its texts (`own_texts` false), so `tools/curated-check.py` doesn't hold it to them. No Pedal machine has a draft now, so `tools/headless-manifests.py` makes Pedal Tracker one in its copy of the app for the drafts' checks, and `ManifestEditorTests` puts a draft of it in the server's manifest cache for the draft editor's.

The synths' manifests (curated, and the Tracker's generated one) take MIDI: `notes.input` "midi", so `notes_write` writes the MIDI column and the note's velocity plays (Juno106 6 voices, SH101 1, FM 8, M1 8, Tracker 16); `input: "tracks"` still writes the Note track, as old songs have it. Their quirks say how each takes MIDI, and the ones that said "no velocity, pitch bend" or "a chord plays as one voice" are gone. The Tracker is typed a sampler (it plays the wavetable, as M1 does). Profiled again on Linux (`tools/machine-profiler.py --only`, the five and Chord, LFO and Peer Control; merged): every pitch heard within 1 cent through MIDI (Juno106's A4 −0.4 cents on voice 6, where track 1 was −11.4), M1's chord heard as 8 voices (1 through its tracks), the Tracker's as 8 (as many as the profiler plays), velocity linear in amplitude on Juno106, SH101 and FM (6.0 dB a doubling), gentle on M1 (2.3), squared on the Tracker (12.0); `docs/machine-health.md` lists no synth slow after silence.

**Their preset banks** (§12.11 measured them, not judged). Levelled, changing only each preset's output value (Juno106's VCA Level, SH101's Volume, both linear in amplitude after the voices), to −1.3 dBFS and −8.3 LUFS at most, 0.3 dB inside the check's limits; `gen_presets.py` beside each bank writes the same file. The check now judges these two banks preset by preset, as ours (not their spread, 14 and 21 LU: a kick and a pad share a bank), so Fork CI fails if one goes over; Drum Matrix's stays reported. `tools/preset-check.py --theirs`, the loudest 400 ms of the check's phrase (chords on Juno106, a line on SH101) in LUFS / the sample peak in dBFS; before, through the Note track (the manifests' input then); after, both ways, through the Note track at full level and through MIDI at velocity 100 (how the check plays them now):

| | Flagged | Peaks up to | Loudest 400 ms |
|---|---|---|---|
| Juno106 (48) before | 30 | +5.1 dBFS | −21.9 … −5.3 LUFS |
| Juno106 after, a track's note | 0 | −1.1 dBFS | −21.9 … −8.3 LUFS |
| Juno106 after, MIDI at velocity 100 | 0 | −1.3 dBFS | −23.9 … −9.7 LUFS |
| SH101 (22) before | 6 | +2.3 dBFS | −31.4 … −4.3 LUFS |
| SH101 after, a track's note | 0 | −1.3 dBFS | −29.5 … −8.3 LUFS |
| SH101 after, MIDI at velocity 100 | 0 | −3.4 dBFS | −31.6 … −10.4 LUFS |

The 31 and 7 presets changed:

**Pedal Juno106** (VCA Level; loudest 400 ms in LUFS / sample peak in dBFS):

| Preset | VCA Level | Before (a track's note) | After, a track's note | After, MIDI at velocity 100 |
|---|---|---|---|---|
| Pad — Lush | 100 → 87 | -7.9 / -0.1 | -9.1 / -1.4 | -10.6 / -3.1 |
| Pad — Hollow | 100 → 78 | -6.7 / +0.8 | -8.8 / -1.4 | -10.8 / -2.8 |
| Pad — Vintage Strings | 100 → 76 | -6.6 / +1.0 | -9.0 / -1.4 | -11.0 / -2.8 |
| Pad — Air | 100 → 78 | -7.5 / +0.8 | -9.7 / -1.3 | -10.5 / -2.3 |
| Pad — Sweep | 100 → 89 | -8.3 / -1.1 | -9.3 / -2.1 | -10.4 / -1.3 |
| Pad — Wide Stack | 100 → 79 | -7.2 / +0.7 | -9.2 / -1.3 | -11.0 / -2.8 |
| Brass — Synth | 100 → 66 | -7.0 / +2.3 | -10.6 / -1.3 | -12.5 / -3.8 |
| Brass — Soft | 100 → 92 | -8.7 / -0.6 | -9.4 / -1.3 | -11.4 / -2.9 |
| Brass — Stack | 100 → 94 | -9.6 / -0.8 | -10.1 / -1.4 | -10.9 / -3.3 |
| Brass — Punchy | 100 → 71 | -9.8 / +1.5 | -12.7 / -1.4 | -14.6 / -4.0 |
| Brass — Mellow | 100 → 92 | -7.7 / -0.6 | -8.4 / -1.3 | -10.1 / -2.1 |
| Lead — Saw | 100 → 96 | -9.4 / -1.0 | -9.7 / -1.3 | -11.8 / -4.1 |
| Lead — Square | 100 → 70 | -6.6 / +1.7 | -9.7 / -1.4 | -11.8 / -3.8 |
| Lead — Sync | 100 → 51 | -7.7 / +4.3 | -13.6 / -1.5 | -15.5 / -3.5 |
| Lead — Acid | 100 → 93 | -11.2 / -0.8 | -11.8 / -1.4 | -13.7 / -4.0 |
| Lead — Bright | 100 → 84 | -7.5 / +0.2 | -9.1 / -1.3 | -10.9 / -3.8 |
| Lead — PWM | 100 → 70 | -6.3 / +1.7 | -9.4 / -1.4 | -11.4 / -1.3 |
| Lead — Mono | 100 → 73 | -7.8 / +1.4 | -10.6 / -1.3 | -12.0 / -3.6 |
| Bass — Sub | 100 → 61 | -5.3 / +2.9 | -9.6 / -1.4 | -11.8 / -3.4 |
| Bass — Saw | 100 → 77 | -8.7 / +0.9 | -10.9 / -1.3 | -13.0 / -3.3 |
| Bass — Pulse | 100 → 47 | -5.7 / +5.1 | -12.3 / -1.4 | -14.6 / -3.6 |
| Bass — Hard | 100 → 87 | -11.2 / -0.1 | -12.4 / -1.4 | -14.4 / -3.4 |
| Bass — Funky | 100 → 88 | -13.5 / -0.2 | -14.6 / -1.3 | -16.9 / -3.4 |
| Bass — Stack | 100 → 84 | -10.5 / +0.2 | -12.1 / -1.3 | -15.0 / -3.7 |
| Bass — Wobble | 100 → 85 | -10.3 / +0.1 | -11.7 / -1.3 | -13.6 / -3.7 |
| Keys — Soft Pluck | 100 → 81 | -13.2 / +0.5 | -15.1 / -1.4 | -17.2 / -3.5 |
| Keys — Bell | 100 → 88 | -12.8 / -0.2 | -13.9 / -1.3 | -15.8 / -3.5 |
| Keys — EP Stack | 100 → 95 | -11.2 / -0.9 | -11.6 / -1.3 | -12.6 / -3.7 |
| Keys — Funky Clav | 100 → 73 | -19.1 / +1.4 | -21.8 / -1.3 | -23.9 / -3.3 |
| FX — Filter Pulse | 100 → 77 | -8.3 / +0.9 | -10.6 / -1.3 | -12.5 / -3.8 |
| Classic — Polysynth | 100 → 94 | -7.8 / -0.9 | -8.4 / -1.4 | -10.2 / -2.6 |

**Pedal SH101** (Volume; loudest 400 ms in LUFS / sample peak in dBFS):

| Preset | Volume | Before (a track's note) | After, a track's note | After, MIDI at velocity 100 |
|---|---|---|---|---|
| Sub Bass | 96 → 60 | -4.3 / +0.2 | -8.4 / -3.9 | -10.4 / -5.9 |
| Deep Sub | 100 → 66 | -12.0 / +2.3 | -15.6 / -1.3 | -17.7 / -3.4 |
| Wobble Bass | 100 → 85 | -9.0 / +0.1 | -10.4 / -1.3 | -11.9 / -4.2 |
| PWM Sweep | 100 → 95 | -8.2 / -0.9 | -8.7 / -1.4 | -10.7 / -3.4 |
| Sci-Fi Drone | 90 → 83 | -9.7 / -0.6 | -10.4 / -1.3 | -16.5 / -5.2 |
| Kick | 110 → 91 | -18.4 / +0.3 | -20.0 / -1.4 | -22.1 / -3.5 |
| Snare Burst | 100 → 124 | -31.4 / -15.1 | -29.5 / -13.3 | -31.6 / -15.8 |

Juno106's Pad — Sweep passed through its track (−1.1 dBFS) but peaked at −0.3 dBFS through MIDI, whose notes take other voices (other detunes beating in its chorus); SH101's Snare Burst, at −31.4 LUFS through its track, played under the check's −32 LUFS through MIDI at velocity 100, so it went up. Juno106's chords and SH101's line through MIDI at velocity 100 are otherwise 2.1 dB under the same through the Note track, within ±0.6 dB (other voices, an LFO's phase), but for SH101's Sci-Fi Drone: its self-oscillating filter plays on from the preset the check played before it (−10.4 LUFS through its track, −16.5 through MIDI; played alone, both −15.2).

**Tests.** `NeoBuzz.PedalTests` (219 cases; the synths' on their own `Work` through a host stub, `SynthHarness.cs`): the MIDI tests above, and each hygiene test with track notes alone and with MIDI as well: silence settles to exact zero with no denormal sample and is said once it's there (for M1 also with a wave that ends while the note holds, for the Tracker also muted while it plays), work after silence at most twice that before the first note, no allocation per block, blocks of 256 and 117 alike (Juno106: its pitch and level), every parameter alone at each end and MIDI at its ends finite, 20 parameters' glides and the 20 set in silence. On the old sources 33 of the 79 that can run there fail (the 34 with MIDI can't: the old synths have no `MidiNote`): the 20 glides, Juno106's chorus and HPF tails, FM's and M1's silence reports and work after silence, M1's short wave, the Tracker's allocation and its report while muted; the 20 settings changed in silence pass there, as the old synths had no glide (that test is the glide's own rule, and fails without it). With the fixes, all pass. The Pedal effects' allocation tests go through `AllocationCheck` too (U6's counted the whole run, and caught the runner's own allocations now and then). `CuratedManifestTests` (19 from source), `AgentHeadlessTests`, `tools/headless-manifests.py` (SH101's notes as MIDI, and through its Note track; the drafts' checks on a Pedal Tracker made a draft), `tools/test_machine_infer.py`, `tools/preset-check.py --theirs` (above: judging the two banks).

**Left.**
- Juno106's default patch tone, voice detune and block-size drift, M1's bend, FM's block-rate LFO and Glide on a released voice (above): fixes that change the sound, for a decision.
- Pedal Drum Matrix: no source change and no MIDI (it is an effect); its own bank isn't levelled (17 of its presets gain 6–12 dB over their input in the check, reported).
- Pedal Chord, LFO and Peer Control: manifests from code, no source change (they make no sound).
- Silence that saves work: the engine change §12.13 describes.

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
