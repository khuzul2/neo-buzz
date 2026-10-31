# ReBuzz MCP server — Specification

| | |
|---|---|
| Status | Draft v1 — design, no code yet |
| Date | 2026-10-31 |
| Scope | An MCP server that lets an AI agent (Claude Code or any MCP client) compose, design sounds, mix and master in this ReBuzz fork, with tools clear enough to use without guessing, guides for the craft, and the analysis needed to check its own work |
| Protocol | MCP 2026-07-28 (stdio and Streamable HTTP; structured tool output; tasks extension for long renders), C# SDK `ModelContextProtocol` 2.x |
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

From a code inventory (file references in §12):

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
  ReBuzz.Agent (new assembly, InternalsVisibleTo from ReBuzz)
    McpHost          C# MCP SDK: tools, resources, prompts, tasks, progress
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
| `gear_list` | A1 | Available machines: name, generator/effect, author, a one-line description, whether it has a manual and units (Neo), preset count. Filter by kind or text |
| `machine_list` | A1 | The song's machines: gear, inputs and outputs with gain dB and pan, mute/solo/bypass, tracks, latency |
| `machine_add` | A1 | Adds a machine from the gear list; optional name, track count, preset, and where to connect it (`"Master"`, or between two machines). Returns its name and its parameter summary |
| `machine_remove` | A1 | Deletes machines; reconnects the chain around effects when asked |
| `machine_set` | A1 | Rename, mute, solo, bypass, track count |
| `connect`, `disconnect` | A1 | Audio connections with gain dB and pan; `input: "sidechain"` for machines with a second input (Neo Comp) |
| `connection_set` | A1 | Gain and pan of existing connections, in a batch |

### 5.4 Sound design

| Tool | Phase | Purpose |
|---|---|---|
| `machine_describe` | A1 | Everything about one machine:<br>• parameters by group, each with name, current value (text, number and unit), range, default and description<br>• track parameters<br>• the manual link (Neo)<br>• presets and commands |
| `param_set` | A1 | Many changes in one call, values in units (§3.3), global or per track. Returns the values as the machine now describes them |
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
| `meters_read` | A7 | Live peak and loudness of the master and chosen machines while the user plays the song. Subscribable as a resource that updates |
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

`Machines/NeoBuzz.Analysis` is pure C#, tested on Linux like the DSP library. The server and the analysis machines (§8) share it.

| Measure | Method | Verified against |
|---|---|---|
| Loudness | ITU-R BS.1770-5: K-weighting, 400 ms momentary, 3 s short-term, gated integrated, EBU R128 loudness range | EBU Tech 3341/3342 test signals, generated in the tests |
| True peak | 4× oversampled peak (BS.1770 Annex 2) | Tech 3341 true-peak cases |
| Spectrum | Welch-averaged FFT, third-octave and named bands, centroid, roll-off, flatness, tilt (dB/octave fit) | Pink noise reads flat in third-octave bands within ±0.5 dB; sine bins |
| Dynamics | Crest factor, PLR, RMS envelope, transient density | Known signals |
| Stereo | Correlation, per-band width (mid/side ratio), mono fold-down loss | Mono, anti-phase and decorrelated noise |
| Masking | Per critical band, both tracks above a level and within a few dB of each other, weighted by time overlap | Constructed overlaps |
| Pitch | YIN with confidence, cents from equal temperament | Synth notes from Neo Poly |
| Key | Chroma plus key profiles (Krumhansl–Schmuckler) | Rendered scales and progressions |
| Tempo and onsets | The existing `AudioAnalysis` (spectral flux), moved into the library | Its existing tests |

Plots (spectrum, spectrogram, waveform, loudness, goniometer) are drawn to PNG by the server with the same code in headless and studio mode.

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

### 8.3 The Neo manifest

Every Neo machine already declares its parameters once (Params.def) with units, mappings and descriptions. Each machine class gets a public static method the server finds by reflection:

```csharp
public static string GetManifest() // JSON
```

The JSON covers:

- every parameter: id, name, group, unit, kind (linear, exponential, dB, time, frequency, enum…), range and mapping, choices, description;
- the machine's data imports (a wavetable WAV, a DX7 bank);
- its readings (Neo Analyzer).

With it, `param_set("Bass", [{"F1 cutoff": "800 Hz"}])` lands on the exact raw value. `machine_describe` speaks in units, and the manual is generated. Legacy machines get the best the server can do: raw ranges plus their own value texts, searched to resolve units.

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

## 12. Inventory notes (for implementers)

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
