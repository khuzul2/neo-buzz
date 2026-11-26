# Clips: one store for everything a clip holds — Proposal

| | |
|---|---|
| Status | **Decided** (the user, 2026-11-23): D1 **B** (code identifiers too, §9.4–9.7), D2 **B, drop** (convert on open, keep nothing that does not convert), D3 **one engine, direct**, D4 **A** (one store, one engine; real notes; old machines view-only). Design only: no product code changed. Phases re-cut in §10 |
| Decision (binding) | "Unify and only use one automation store for both tracker and clips. Tracker patterns should use the same exact store (also for notes, velocity, length as well as all parameters automation) as the clips and arrangement views. So effectively we unify patterns and clips which are used anywhere. We'll call them all 'Clips' everywhere." (the user, 2026-11-23) |
| Decided later that day | "I don't care about old songs compatibility that much. So: go with the unified engine (Direct) and we live with the consequences." (D3). "One store, one engine so let's go with A and add the 'one store one engine' as a design principle of NeoBuzz." (D4). "Make sure all new machines and our VST host take real notes." "The tracker also can play chords (given we unify everything) by allowing to add more 'notes + velocity' columns." "For old machines, make them read-only." (fully view-only, notes and automation, until the machine is converted to a Neo one). "...which ones is worth to rebuild as Neo native machines and which ones can be converted on load and mapped to existing Neo machines." (phase LM). Principles are in `FORK.md` ("Design principles", 2026-11-23) |
| Also decided that day | EV1 (parallel branch): an envelope lane in the Detail panel, read-only curves on clips, writes the engine automation columns. T2 (waits for this plan): a vertical envelope lane beside the tracker's columns, with a show/hide toggle on each parameter column. MT1 (parallel): tracks in the Machines view |
| Scope | Every store of clip content, every reader and writer of each, the one store, the tracker as a view of it, old songs, the rename, phases and gates |
| Code baseline | fork `4f0beba5` (origin/main, 2026-11-23) |
| Related | daw-modes.md (§3 the model, §8 AR0–AR6 as built), session-view.md (§4.2, 0.7), native-format.md (NF1, NF2 §11), agent-knowledge.md (§11), mcp-server.md |

---

## 1. Summary

**Verdict: the store already exists. It is the pattern's engine columns (`PatternColumnCore`). The work is to move everything into it, not to invent one.**

- **Today a clip's content is in up to five places** (§2): the engine MIDI column, the engine parameter columns, the Modern Pattern Editor's (MPE) own per-machine blob, the built-in Buzz rows an old song brings, and a foreign editor's opaque blob. Each has its own readers, its own timing and its own undo.
- **Every one of them is a list of `PatternEvent(time 1/240 tick, value, duration)`**, keyed by (machine, parameter, track). MPE's columns are the same type as the engine's. So the one store is the engine column list, and the work is mostly a change of owner, plus a few things the engine columns do not yet say (who plays a column, what a voice is, the row grid).
- **Recommendation**
  1. **The store is the clip's column list.** Notes of MIDI machines: the MIDI column (time, length, velocity, channel; the column's `Track` is the voice). Everything else, notes of classic Buzz machines included: parameter columns, steps or ramps. No new file section: TAP2 already writes events, durations and metadata (§5).
  2. **One engine plays every lane** (D3, direct). There is no editor player in the Studio: the tracker is a view and an editor of the store, nothing more. New and migrated data use the engine's exact timing; no `Lag` emulation. Songs may therefore sound slightly different after migration: CU1 builds a **change report** (which songs, by how much, why) instead of a pass/fail gate for migrated tracker data (§10.1).
  3. **The tracker stays MPE, adapted** (§6): its data layer reads and writes the store through a small seam; only view state (column set, order, rows per beat, mutes, which note column shows which voice) stays in its blob. A machine that takes notes can have **any number of note+velocity columns** (+ and −), all views of the one Notes lane, so a chord typed across columns is several notes at one time (§5.3). A new tracker costs 8–12 pw more and gains nothing the seam does not.
  4. **Old songs convert on open, in memory** (D2 B, drop), by one pure converter, with the original file untouched (§7). Classic built-in rows, MPE and PXP blobs convert (the notes and parameters exactly). What does not convert (PatternXP's sub-pattern triggers, modulators, any other editor's data) is dropped, gone at the next save and named in the loss report.
  4a. **Real notes; old machines view-only** (D4 A). The Notes lane is the store for every machine that takes real notes (note-on and note-off with velocity, any polyphony): every Neo machine, Neo Plug and exported plugins, which CU6 audits and fixes. The clips of an **old machine** (§5.7) are fully view-only, notes and automation alike, until the machine is converted to a Neo one (phase LM); they keep playing. No write-side voice allocation exists.
  5. **The rename (D1 B):** UI, browser, MCP (aliases for two releases), guides, docs, tests **and the code identifiers**, behind a compatibility layer: what a binary, a saved file or a reflection lookup stores keeps its name, the new names sit beside the old (§9.4–9.7). File formats keep "pattern".
- **Effort:** 14–20.5 pw in seven waves plus the LM track (§10; 11–16 before D1 B and the direct engine). CU1 builds the corpus and the change report first. T2 can start after CU3.

### 1.1 Words

| Word | Means | Was |
|---|---|---|
| **Clip** | The unit of content: a length and its columns. Used by session slots, the arrangement and the tracker | pattern, and the session's clip |
| **Placement** | One run of a clip on an arrangement lane (a `clip id` in the code). The user sees a clip on the lane; "linked ×3" says the content is shared | the arrangement's "clip" |
| **Lane** | One column of a clip: notes, one parameter on one track | column |
| **Note lane** | The MIDI column of a clip | MIDI column |

The word "pattern" stays where it is a Buzz fact: `.bmx` sections, upstream, Classic, the loss report's "Patterns" row, the `IPattern` interface.

---

## 2. What exists: every store of clip content

### 2.1 The stores

| # | Store | What it holds | Who plays it | Saved in |
|---|---|---|---|---|
| S1 | **Engine MIDI column** (`PatternColumnType.MIDI`, `PatternCore.Columns`) | Events: time, packed message `status \| d1<<8 \| d2<<16`, `Duration` = note length (negative: no note-off). Notes (velocity = d2, channel in status), control changes, pitch bend (`ControllerPoint`). One column, `Track` 0 | `WorkManager.PlayPatternColumnEvents`, every active pattern, any machine | TAP2 (`.bmx`, `.nbl`), `.bmxml` |
| S2 | **Engine parameter columns** (metadata `SessionAutomation`) | Steps, or ramps (`Duration` > 0 to the next event, S3). Written by session takes, the arrangement recorder, the agent (`automation_write`), the lanes | Same, `EnginePlays`, `Parameter.SetValue(track \| 1<<16)` | TAP2 |
| S3 | **Engine Note and velocity track columns** | Note-ons and `Off` as steps, one column per track, for machines that take notes in tracks. Written by `MachineNotes.Replace` (`TrackNotes.Allocate`: lowest free track, refused beyond `MaxTracks`) | Same (they are S2 columns) | TAP2 |
| S4 | **MPE data**, `MPEPatternDatabase`, one blob per machine in the hidden editor machine (`\x01pe1`…), keyed by **pattern name** | Per pattern: rows per beat, then columns (machine name, parameter index, track, graphical flag, events at exact time and value, no duration, rows per beat of each beat). Columns may belong to **other machines** than the pattern's. Internal parameters for MIDI machines: Note, Velocity, Delay, Cut, Pitch Wheel, CC (index −128…−133). Column set and order, mutes. Written as MPE format (`0xFF`, v1) or PXP v3 (`PXPDataFormat`, quantized to rows) | `PlayRecordManager.Play`, from the editor machine's `Work`, after `CallTick`, for sequences of its machine | `PatternEditorData` blob (machine data) |
| S5 | **Foreign editors**: PatternXP (native, `ReBuzzPatternXP`), upstream's Jeskola Pianoroll prototype (`Machines/ModernPianoroll`, writes Note columns in its own store, `ExportMidiEvents` returns null), any other | Opaque. Read only through `PatternEditorMachineEvents` (columns as events) and `PatternEditorMachineMIDIEvents` | The editor | `PatternEditorData` blob |
| S6 | **Built-in Buzz rows** (`PATT`) | Old songs: rows × parameters per track, plus **input columns** (connection amp and pan per row). On load: converted to a PXP v3 blob for the editor (`BuiltInPattern.ToPXP`, rows per beat 4), where input columns are **dropped**; with no editor kept on the pattern (`BuiltInData`). `PATT` is written **empty** by every fork save (a PATT row is `NoValue`) | The editor | `PATT` (old songs), `XTAP` links patterns to the editor |
| S7 | **Session clip settings** (`SessionDocument.ClipSettings`, by (machine name, pattern name)) | Launch mode, quantization, legato, loop, start and loop markers, velocity sensitivity, follow actions; `Audio`: asset, tempo, warp mode, **warp markers**, gain, transpose, detune, fades, tail, reverse | `SessionCore` (launch), `SessionClipEngine` (audio) | `SessionView` subsection |
| S8 | **Arrangement placements**: `SequenceEvent` (`Offset`, `End`, `Gain`, `Clip` id), `ArrangementClips` loops, lane roles (main, take, automation), takes | Where a clip plays and how much of it | `WorkManager.UpdatePatternPositions` | `SEQU`, `ArrangementEvents`, `Arrangement` subsections |
| S9 | **Controller lane** (`MidiControllers`) | Not a store: CC and pitch bend points inside S1 | S1's player | S1 |
| S10 | **Copies of S4/S5 blobs** in clone, replace, delete-undo, templates, history snapshots (`LiveSong`) | The whole machine blob, or `EngineColumns` for S1–S3 | – | – |

Audio clips are clips of the Session Clip Player: S7 holds their audio settings and warp markers, S1–S3 their automation of the player's parameters. Nothing else of theirs is a lane.

### 2.2 Readers and writers

| Who | S1 MIDI col. | S2/S3 engine columns | S4 MPE | S5 foreign | S6 built-in | S7/S8 |
|---|---|---|---|---|---|---|
| **Playback** | `WorkManager` | `WorkManager` | `PlayRecordManager` (editor's `Work`) | the editor | – (converted) | `SessionCore`, `UpdatePatternPositions` |
| **Arrangement automation lanes** | – | read and write (`ArrangementAutomation`) | read only, `PatternEditorMachineEvents` | – | – | – |
| **Piano roll, `NoteSource`s** | `MidiColumnNotes` | `TrackerColumnNotes` | `EditorTrackerNotes`, `EditorLaneNotes` (S4 notes as rows) | `EditorPreviewNotes` (read only) | – | `SessionClipNotes` (session roll) |
| **Tracker** | AR3 computed note lanes (display; edits become notes) | not shown | MPE (read and write) | – | – | – |
| **Recorders** | session and arrangement takes write S1, S2 | same | MPE records its own (`RecordControlChange`) | – | – | takes: `Arrangement` roles |
| **MCP** | `notes_read`, `notes_write`, `arrangement_get` | `notes_*` (tracks mode), `automation_write`, `automation_read` (**only S2**), `arrangement_automation` | none: **the agent cannot read or write tracker data** | – | – | `session_clip_set`, `audio_clip_*`, `arrangement_clip`, `arrangement_place`, `takes` |
| **Save and load** | TAP2 / `.bmxml` | TAP2 | blob in machine data (per machine, 7z in templates) | blob | `PATT` empty on save | subsections |
| **Buzz export (NF2)** | kept in TAP2, upstream plays it | kept, never played upstream | kept; names ASCII (a Latin-1 rename can lose it) | kept | – | trims, gain, ids not baked |
| **Classic (WPF, frozen)** | plays (engine) | plays S2 | MPE (WPF build of the same sources) | PatternXP etc. | – | no new UI |
| **Upstream ReBuzz** | **plays** | keeps, never plays | plays through MPE (its own editor) | plays if it has the editor | – | plays sequences; loses trims, gain, ids, roles |
| **Clone, replace, undo of delete, snapshots** | `EngineColumns` | `EngineColumns` | whole blob | whole blob | – | – |

### 2.3 Found in the code

1. **MPE's columns are engine `PatternEvent`s.** The two stores differ in owner, player and key, not in shape. MPE's undo is already the song's (`EditContext.ManagedActionStack`).
2. **Two players, two timings.** The engine plays S1–S3 in `UpdatePatternColumnEvents`, *before* `CallTick`, in the block that holds the event. MPE plays in its `Work`, *after* `CallTick`, the window [previous play position, play position), that is the previous block's events (`daw-modes.md` §8.5: "a tracker column lands after the engine's in a block"; read from the code, not measured). For a classic machine that reads its parameters at `Tick`, a row-aligned value may arrive one tick apart. **Unverified. The first task of CU1 measures it** (§5.6).
3. **MPE keeps a full default column set per pattern**, empty or not. The two `.nbl` templates carry 3,648 and 4,052 columns with 0 events, 9 and 10 blobs of 2.8–40 KB. The store creates a column on its first event.
4. **The blob is keyed by pattern name and by machine.** A rename, a Latin-1 export or a replace can lose it; `PatternRenamed` patches only the live case.
5. **The agent is blind to tracker data**: `automation_read` lists only S2 columns, `notes_read` reads S1 or S3.
6. **Input columns are lost on open today** (Windows with MPE): 2 of 18 songs have them (136 cells). A host with no editor keeps them in `BuiltInData`.
7. **Chords are an input aid, not data.** They are not in the Studio build (`Chords/**` is excluded from the portable build). There is no effect-command store: "Command", "Effect" and "Cmd/Arg" are ordinary track parameters.
8. **MPE hard-codes 4 ticks a beat.** A row is a tick at 4 rows per beat; the song's TPB does not enter.
9. **The MIDI message's high byte is free** (`MIDI.Decode*` mask each byte), but the column's own `Track` is the cleaner voice (TAP2 and `EngineColumns` already keep it).
10. **A live restore swaps column lists on the audio thread** (`SwapEvents`, HS3). Tracker data, as a blob, is outside that today.

---

## 3. How often (the repo's songs)

Scanned with small local scripts (not committed) over `.bmx`, `.bmw`, `.nbl` in the repo.

| | Count |
|---|---|
| Song files | 20 old (`.bmx` 19, `.bmw` 1: Buzz-era demos in `ExternalMachines` and `Other`), 2 `.nbl` (the Bass House and Tech House templates) |
| Parsed fully (PARA present) | 18 of 20 (FSM_Kick_Demo and Makk_M4 lack PARA) |
| Songs with an editor blob, XTAP or TAP2 | **0 of 18 old** (all classic: patterns in `PATT`). Both templates: MPE blobs (10 and 9, rows per beat 4, **0 events**) and TAP2 with MIDI columns only (10 each, 86 and 147 events, no parameter column) |
| Patterns / machines with patterns | 154 / 50 |
| Note cells / Off cells | 782 / 159 (four notes in five have no Off in their pattern: they end at the next note or ring on) |
| Parameter cells | 722 on tracks, 752 global |
| Multi-track machines | 10 of 50 (the most: 8 tracks, Dynamite6; 6, Infector) |
| TPB | 4 in 16 songs, **1** (Dynamite6), **8** (11Stereo) |
| Pattern lengths (rows) | 1, 16, 32, 64, 128, 256 (two songs have 1-row patterns) |
| Effect-command columns | 7 machines (Infector ×5 "Command 1/2", Hih.TR and Clap.TR "Effect / Effect Data") |
| Input columns (amp/pan per row) | 2 songs, 136 cells (Masterizer_test 134, Elak_SVF 2) |
| MPE-only features (delay, cut, MIDI lanes, per-beat subdivisions, rows per beat ≠ 4, chords) | **0 songs.** They exist in the tests' generated songs only |

**Reading.** The corpus proves the classic path (PATT rows → Note, velocity and parameter columns, TPB ≠ 4, 1-row patterns, 8 tracks, command columns) and nothing of MPE's own features. CU1 therefore builds a **generated corpus**, one song a feature (§10), and records goldens for both. Of the old songs, only the Windows package job has their real machines: on Linux and macOS the machines are placeholders, so goldens are per platform.

---

## 4. Why not keep two stores

- Two note stores, two parameter stores and two players mean that the piano roll, the arrangement lanes, the agent, the session takes and the tracker each see part of a clip (§2.2). The Detail panel's envelope lane (EV1) and T2's lane would show different curves for one parameter.
- AR3's computed note lanes and the 0.7.1 "Move notes" commands are bridges between stores; every new feature needs a bridge pair.
- The blob's identity (a pattern name) is the weakest key in the song.

---

## 5. The one store

### 5.1 Shape

A clip is `IPattern` (length in ticks) with its `Columns`. A **lane** is a `PatternColumnCore`: machine, parameter, track, events, `BeatSubdivision`, `Metadata`. Nothing new in the file: TAP2 writes all of it.

| Lane | Events | Plays as |
|---|---|---|
| **Notes** (MIDI column; `Track` = voice) | `time`, packed message (note-on with velocity and channel; note-off; CC; pitch bend), `Duration` = length in 1/240 tick; negative = open (no note-off); 0 = a trigger or an explicit off | `SendMidiInput`; a note-on with a duration schedules its off (`playingEvents`) |
| **Parameter** (any parameter of any machine, global or one track) | `time`, value; `Duration` > 0 on an event = a ramp to the next (S3) | `Parameter.SetValue(track \| 1<<16)` |
| **Note parameter** (a track machine's Note, its velocity) | Note-ons and `Off` (`BuzzNote.Off`) as steps | the same, by the machine's tick |
| **Trigger** | Parameters without `State`: events heard once | the same |
| **Wire** (not built: connection amp and pan, S6 input columns) | – | listed in §11 |

### 5.2 Metadata (all optional; absent means what it means today)

| Key | Values | Meaning |
|---|---|---|
| `SessionAutomation` | `1` | **Kept as is.** Older builds and upstream read it the same way. After CU3 the Studio plays every column whether it is marked or not (the mark only tells an older build whether it may play it) |
| `Play`, `Owner` | **Not built** (D3 direct) | An earlier draft named the editor as a second player. There is one player, the engine, so no column says who plays it. A column with no `SessionAutomation` mark is played by the engine from CU3 on (today the editor plays unmarked columns) |
| `Grid` | `rpb=4;sub=3:6,7:3` | Rows per beat of the lane and the beats whose rows differ (MPE's `BeatRowsList`). View only: never changes a time |
| `Mute` | `1` | The tracker's column mute (MPE `ParamterMuted`) |

View state that is not music (column order, which columns show, collapsed sets, T2's lane toggles) goes in a new `ClipView` subsection keyed by (machine, clip name), as `ArrangementStore` does: losing it loses a layout, never a note. Rows per beat per clip is `Grid` on its lanes and `ClipView` for empty clips.

### 5.3 Notes: one model, two shapes by route

One **note model** for every view and tool: `ClipNote` (time, length, pitch, velocity, voice). It is read and written by one `NoteSource` per route, chosen by `KeyboardRoute` (existing):

| Route | Stored as | The roll's rules |
|---|---|---|
| **MIDI** (Neo machines, managed `MidiNote`, manifest note input) | Notes lane: exact time, length, velocity | no limit; the tracker draws virtual voices (AR3) |
| **Tracks** (old machines: a Note track parameter, no real notes) | Note and velocity parameter columns, **voice = track**, as old songs have them. **Display and playback only**: no write (D4 A, old machines are view-only, §5.7). There is no voice allocation on write and no `MaxTracks` refusal, because nothing is written | read-only |

This is D4 A (§12). D4 B (one list for every machine, the engine derives Note/Off/velocity at play) was not chosen.

**Note columns in the tracker (chords).** For a machine that takes real notes, the tracker shows **n note+velocity column pairs**, with + and − to add and remove a pair. A pair has **no data of its own**: it is a view of the one Notes lane. Notes that sound at the same time are spread over the pairs (a chord typed across three pairs is three notes at one time). Which pair shows which note is **view state** (`ClipView`; the default is the lowest free pair at that time, so a clip with no stored view looks the same every time). − refuses while it would hide notes ("4 notes would not be shown; keep at least 4 columns"). The Notes lane stores no voice. The derived blob for Classic and upstream (§7.2) gets no MIDI lanes for such a machine.

### 5.4 What a tracker needs that a note list does not

| Tracker construct | In the store | Exact? |
|---|---|---|
| **Off** | MIDI machines: note length, or an explicit note-off event (duration 0) for an off with no note before it in this clip (a note rings across clips). Track machines: `Off` in the Note column | yes |
| **Cut** | MIDI lanes: the note's length. Track machines: no cut | yes (MIDI) |
| **Delay** | MIDI lanes: the note's time (row start + delay), exact in 1/240 tick | time yes, scheduling see §5.6 |
| **Legato, retrigger** | Open length (negative duration); the next note on the voice is the end. Track machines: Note events with no Off between | yes |
| **Velocity** | MIDI: in the message. Track machines: the machine's velocity column (by name, as `MachineNotes.For`) | yes |
| **Effect commands** (Cmd/Arg, Effect/Effect Data) | Ordinary track parameter columns; stay aligned to the voice because the voice is the track | yes |
| **Chords** | Several notes at one time; no data of their own. Typed across note+velocity column pairs (§5.3); the pair a note shows in is view state | n/a |
| **Pitch bend, CC** | S1 events | yes |
| **Rows per beat, subdivisions** | `Grid` | view only |
| **Values off the grid** (the agent's quarter-tick steps, ramps) | A row shows the last event in `[row start, next row start)` marked as "more"; an edit rewrites only the events of that cell | the AR3 rule, generalized |
| **Input columns (amp, pan)** | Not stored (they are not today) | §11 |

### 5.5 Parameters

- A parameter lane of any machine is a column of the clip; the tracker's "other machine" columns are the same thing.
- A state parameter (`ParameterFlags.State`) is a curve (steps or ramps); a trigger is a marker. EV1 and T2 draw the same lane the tracker column edits.
- One writer rule stays: two lanes writing one parameter in one place are overlaps, found and marked, never hidden (AR4).

### 5.6 What the engine plays

**MIDI machines.** The clip's Notes lane, as today: `SendMidiInput` at the start of the block that holds the event, the note-off scheduled from `Duration`. Parameter lanes: `SetValue` before `CallTick`, ramps on tick and subtick (S3).

**Old machines.** Their Note and velocity parameter columns and their parameter columns play as parameter steps, as now. Nothing is written to them from the Studio (§5.7), so there is no voice allocation anywhere, in or out of the audio path.

**One player.** Every lane of a clip, migrated or new, is played by the engine (D3, direct): parameter lanes before `CallTick` (steps, ramps), the Notes lane through `SendMidiInput` with exact time. MPE no longer plays anything in the Studio: its `Work`-side window and `PlayRecordManager`'s playing code are deleted from the Studio build in CU3. **Consequence the user accepted:** the timing of a migrated tracker song is the engine's, not MPE's, so a value that used to arrive in the next block, or a delayed note, may move by up to a block or a subtick. No `Lag` emulation.

**The timing change report (CU1 builds the tool; CU3 runs it).** A song whose parameter step and note sit on rows, played by (a) MPE now and (b) the same events as engine columns. For each corpus song and each generated feature song: does the render differ, from which sample, by how much (peak and RMS of the difference, in dB against the signal), and the **cause** (a tag the converter writes while migrating: a delay or cut on a MIDI lane, a block shift, PatternXP's dropped SP columns, a subtick rounding, an input column). It is a report for the user to read, not a pass/fail gate for tracker data (§10.1). An unexplained difference is investigated before the wave merges.

**Measured (CU1, `ClipTimingProbeTests`): what changes, per case, and by how much.** A row-aligned value played (a) by the editor's own data (MPE) and (b) as an engine column, in one song with the Clip Probe test machine, 44.1 kHz, sub-tick timing on (the default), at 120 and 125 BPM, 4 ticks per beat, with host calls of 64, 256 and 1024 samples and with the tick start inside the call and at its edge. "Lands" is the first sample where the output is non-zero. 36 cases; the table is the test's output (`CLIP_PROBE_OUT` writes it to a file).

| Case | Engine column | Editor (MPE) now | What changes in CU3 (editor to engine) |
|---|---|---|---|
| Classic parameter value (state or note parameter) | the tick's first sample | one sub-tick later: **262 samples at 120/4, 264 at 125/4** (0.05 tick) | the value arrives **262 or 264 samples earlier**, whatever the call size or alignment. About 6 ms at 44.1 kHz |
| MIDI-lane note (Notes lane, `SendMidiInput`) | the tick's first sample | **exactly one tick later**: 5513 or 5512 samples at 120/4 (125 ms), 5292 or 5276 at 125/4 | the note starts **one tick (a sixteenth at 4 ticks per beat) earlier**, at the row. This is the one audible shift |
| Short window (125/4, call 64, tick 4, tick starts at offset 48 in the call) | the value lands **264 samples after** the tick start (a parameter), or 16 (a MIDI note): the engine's block window is empty for blocks under about 22 samples and the event waits for the next block | the editor equals the engine for parameters here (both late); the MIDI note is 5276 later | the only case where the engine is late. **Not a CU3 change to hide: a finding.** A real host call is 256 or more; CU3 decides if the window gets a floor |

The same run says the editor's MIDI path is coarser than the engine's: a lane note fires on the **tick** (so a delay inside a row does nothing at 4 rows per beat, and any cut inside a row ends the note at once), and a Pitch Wheel or CC lane on a track with a note lane throws in the render (found by the corpus; CU2 drops those lanes with a log line). So the change report predicts: (1) classic parameter songs differ by **a shift of 262 or 264 samples** at each value; (2) songs with MIDI lanes differ by **one tick** at each note and also in the delay and cut length; (3) songs with only a single value per row and no fast notes are the least affected. There is no Lag design to choose: the engine's timing is the new timing.

**Zero allocation.** The store's `GetEventsInto` is the audio path's read (already used). MPE's read after the seam is the same call. The seam adds no allocation per buffer; `AudioHygieneTests` and `EngineHardeningTests` get a migrated tracker song (§10 gate).

### 5.7 Old machines: view-only (D4 A)

The user's words: "For old machines, make them read-only", and then **fully view-only, notes and automation alike, until the machine is converted to a Neo one. They keep playing.**

**Definition (an explicit list, not a guess).** A machine is **writable** if any of these holds, and **old** if none does:

1. It is a **Neo machine** (`Machines/Neo*`: the 60-odd generators, effects, utilities and `Neo Plug`/`Neo Plug FX`), or a **plugin loaded through Neo Plug**, or an **exported plugin** (`PluginExport`).
2. It is an **effect or utility of ours** (built and owned in this repo for the fork, not vendored: for instance the fork's `ReBuzzGroove`, `MidiOutput`, `QuickNote`, `ConnectionMixer`, the mixer's sends, and the Master). 
3. It takes **real notes** (note-on and note-off with velocity, any polyphony: a manifest note input or a managed `MidiNote`) and is listed.

The list is a field in the machine's manifest (`clipEdit: true`; default: absent = old). It is data, so LM changes a machine's status by editing a manifest, not code. A machine with no manifest is old.

**What view-only means.** The tracker, the roll, the lanes (EV1, T2) and the agent **show** the clip of an old machine (notes, steps, ramps, the track layout) and the engine **plays** it. They refuse every write to it: typing, recording (a take onto an old machine's track), paste, `notes_write`, `automation_write`, `clip_*` tools (the reason is in the message: "X is an old machine: convert it to a Neo one first"). Cut, copy and delete of the whole clip stay allowed (they are not edits of the machine's content). A machine whose manifest later turns writable unlocks its existing clips.

**Borderline families** (status left to LM; until then they are **old** unless a rule above names them):

| Family | Where | Why it is borderline |
|---|---|---|
| **Pedal** (19 machines: Chord, Chorus, Comp, DrumMatrix, EQ, FM, Filter, Gain, Gate, Juno106, LFO, M1, MComp, PeerControl, Plate, SH101, Shaper, Tracker) | `ExternalMachines/portable`, source `thepedal`, builds portable | Vendored third-party source, built by the fork; effects (ours?) and note generators (Juno106, SH101, FM, DrumMatrix, Chord, Tracker take notes in tracks) |
| **R1 rebuilds** (the Buzztrax classics recompiled for arm64/Linux: FSM Infector, KickXP, PhatMan, PanzerDelay, Chorus2, WahMan, WahManPro, WahPro2 and others, cross-platform.md R1) | `ExternalMachines/source/buzztrax-buzzmachines` | Public-source classics, track-note machines; a rebuild, not a Neo machine |
| **Jeskola rebuilds and classics** (Bass, Bass 2/3, Organ, Noise Generator, Trilok, Tracker, Arpeggiator, Relativion, Note Matrix, Loop Recorder, Live…) | `RunTime/Gear`, `jeskola-dev-examples` | Track-note generators and managed utilities; many have a Neo equivalent |
| **Managed third-party** (AudioBlock, EnvelopeBlock, DAWControl, GuitarChordBox, Info, ModernPianoroll…) and **native third-party** (FSM, Oomek, Polac, KrimZon, Rout, Bigyo, BTDSys…) | `Machines/`, `RunTime/Gear` | Some ship in this repo (AudioBlock, EnvelopeBlock), some are users' own; no manifest |

**Consequences to know** (they follow from the decision, none is hidden): (1) a song of old machines opens, plays and can be arranged, but its tracker cannot be edited in the Studio (the tracker stays a viewer for it); (2) recording a session take onto an old machine, EV1 envelopes and T2 lanes on an old machine's parameters are read-only too (EV1 is a parallel branch: its lane write must check the same flag); (3) Classic's WPF app is unchanged and still edits everything; (4) `MaxTracks` and the voice-allocation code in `TrackNotes.Allocate` are no longer written to by the Studio and can go with CU7.

### 5.8 Everything else a clip holds

- **Launch settings, audio settings, warp markers** stay in `ClipSettings` (S7), keyed by (machine, clip name). They are clip properties already. No change in CU.
- **Placements** (S8) stay: `SequenceEvent` with `Offset`, `End`, `Gain`, the clip id of a run, lane roles, takes.
- **Identity** stays the (machine, name) pair everywhere. A stable clip id is debt: noted in §11, not done here.
- **Undo and history.** One `ColumnEventsAction` a tracker edit (MPE's actions already run on the song's stack). Snapshots and live restore (HS3) swap whole columns, so tracker data restores glitch-free for the first time. Clone, replace and delete-undo copy columns (`EngineColumns`), and the blob copies shrink to view state.

---

## 6. The tracker as a view of the store

### 6.1 The options

| | (a) Adapt MPE: a store-backed data layer | (b) A new Studio tracker |
|---|---|---|
| What it is | `MPEPatternColumn`'s event list and lock become a seam (`IColumnStore`): its own list for Classic's WPF build, `PatternColumnCore` for the portable and Avalonia build. `MPEPattern` keeps the view state. The AR3 note-lane host becomes the general note view | A new grid, editor and playback-free model over the store |
| Size today | The Studio build compiles about **9.0k** shared MPE lines, 1.8k portable and 2.2k Avalonia (the WPF-only 3.7k and chords 0.6k are out) | Rewrites most of those 13k lines |
| Keyboard parity | Free: `PEKeys.xml`, `KeyboardMapping`, `Digit`, `Selection` untouched | Rebuild and re-test every key (`EditorKeys` 115, `KeyboardMapping` 153, `Digit` 323, `Selection` 203) |
| Rows per beat, subdivisions, column sets, mutes | Free (view state in the blob) | Rebuild |
| Hex and decimal entry | Free (`Digit` by parameter type) | Rebuild |
| Multi-track machines | Free (columns per track) | Rebuild |
| Room for T2 | A lane strip beside the column: `PatternGrid` (908 lines) gains a strip; the column already knows its lane | Designed in from the start |
| Classic | Not touched: its WPF build keeps the list store | Not affected |
| Cost | **3–4 pw** (CU3) | **8–12 pw more** than (a), then CU3's migration on top |
| Risk | The shared sources: the seam must not change Classic. Guarded by `#if WINDOWS` and Classic's tests | Parity regressions in a tool people type in all day |

### 6.2 Recommendation: (a)

Revisit (b) only if T2 or T3 shows that the grid code blocks the lane, not before. Under (a):

- **What leaves MPE:** its event lists and `PlayRecordManager`'s *read* of them (it reads the store); `ExportMidiEvents`/`ImportMidiEvents`/`ExportClipNotes`/`ImportClipNotes` bridges and the 0.7.1 "Move notes" commands (CU4); the editor-side copy of notes in `NoteLaneColumns` (it keeps one display role).
- **What stays:** recording (`RecordControlChange`, step input), chords in WPF, the digit and key code. **What goes in the Studio build (CU3):** the playing code; the tracker only edits and shows.
- **The seam in detail:** `GetEvents`, `GetEventsInto`, `SetEvents`, `Clear`, `HasEvents`, `EventsChanged`. The store's `EventsChanged` reaches the tracker, so a lane drawn by EV1, a take, or the agent shows at once; today only the AR3 note lanes poll.
- **Columns that do not exist yet in the store** are created on first write (`SessionLayout.ColumnFor(create: true)`), so the 4,000 empty default columns of a template disappear.

---

## 7. Old songs

### 7.1 On open

One pure converter, `ClipMigration`, runs after all machines are loaded (as MPE's own `ProcessEditorData` does), on every open path (Studio, headless, agent, `song_open`). It never writes the original file: a song opened from `.bmx`, `.bmw` or `.bmxml` has `LegacyOrigin` and Save asks for a name (native-format.md §7).

| Source | Converts to | Exact? |
|---|---|---|
| `PATT` rows (S6) and `BuiltInData` | Note and parameter lanes of the machine, event time = row × 240, `Grid rpb=4`. Played by the engine | **Yes** in the data (same events the PXP v3 path makes now); sound per the change report |
| MPE blob (S4, MPE format v1), parameter and Note columns | The same lanes, exact times | **Yes** in the data |
| MPE blob, PXP v3 | The same, at the rows PXP holds | **Yes** |
| MPE blob, **MIDI lanes** (Note, Velocity, Delay, Cut, Pitch Wheel, CC) | **At migration they become the Notes lane** (`TrackerNotes.FromRows`, as `ExportClipNotes`): time = row start + delay, length from cut, off or next note, velocity. CC and pitch wheel become controller points. The tracker shows them as note+velocity column pairs (§5.3) | Events yes; sound per the change report |
| MPE columns of **other machines** | Parameter lanes on that machine, same `Play` | Yes |
| MPE column set, order, mutes, rows per beat of an empty clip | `ClipView` and `Grid` | View only |
| **PatternXP-format editors and foreign editors (S5)** (D2 B, drop) | Converted through the same reader MPE has (rows → lanes). **Everything that does not convert is dropped** and named in the loss report: PatternXP's SP columns (sub-pattern triggers, play mode, offset, diatonic transpose, modulators), any editor's data in a format we cannot read (cleared today already). The clip has no `Owner` and no blob afterwards. A song saved after this has lost them for good | Notes and parameters yes; the rest dropped (§7.5) |
| **Input columns** (S6) | Not converted (they are dropped today; converting changes the sound) | §11 |
| Engine columns S1–S3 | Nothing to do | – |
| `ClipSettings`, placements | Nothing to do | – |

**Order on open:** load machines → load engine columns (TAP2) → run `ClipMigration` for every machine whose editor is MPE, PatternXP-format or none → write the loss report lines → mark the song as migrated (in memory) → build the editor view from the store and `ClipView`. A clip whose lanes are already in the store (a song saved after CU3) skips its blob. **Old machines'** clips migrate the same way and open view-only (§5.7).

### 7.2 On save

| Target | What is written |
|---|---|
| `.nbl` | TAP2 with every lane and its metadata, `ClipView`. `formatVersion` 3. A song that holds migrated lanes writes `minReaderVersion` 3 (an older build refuses and names the version rather than drop tracker data, as NF3 does for audio); any other song keeps writing 2 or 1. The MPE blob is written as **view state only** (no events) |
| `.bmx`, `.bmxml` (the full, non-lossy old kind) | TAP2 as above **and a derived MPE blob** (`ClipExport`) with a stamp (hash of the store content it came from), so Classic and upstream still play the tracker |
| Export for Buzz/ReBuzz (NF2) | As `.bmx`, plus the loss report below |

**Derived blob, one rule against double play.** Notes of **MIDI machines stay in the MIDI column only** (upstream plays that column); the blob gets no MIDI lanes for them. Notes and parameters of track machines go in the blob (upstream's tracker plays them); their TAP2 columns carry no `SessionAutomation`, so upstream never plays them either.

**Classic and stale blobs.** If a song is edited in Classic and saved, its blob changes and the columns do not. On the next open in the Studio the stamp does not match: **the blob wins**, the clip is migrated again from it, and a message says so.

### 7.3 What upstream loses (additions to NF2's report)

| Category | Item | What happens upstream |
|---|---|---|
| Tracker ramps | machine / clip / parameter: K ramps | Written as steps at rows (sampled), shape lost |
| Off-grid events | K events between rows | Rounded to the row before |
| Notes of MIDI machines in the roll | clip: K notes | Played from the MIDI column (as now); not shown in upstream's tracker |
| Voices | clip: K notes | Track kept as the column's `Track`; upstream ignores it |
| Pattern editor data not converted | clip: K sub-pattern triggers, modulators, offsets, transposes (PatternXP); clip: editor data of an unknown editor | Dropped on open (D2 B); gone from the song at the next save; named in the Studio's open report |
| Voices | note+velocity column of a chord | Not exported (MIDI machines stay in the MIDI column only) |
| Unchanged | trims, gain, clip ids, roles, ramps (steps), subsections | as NF2 §11.2 |

### 7.4 Songs saved by builds before this

A `.nbl` saved before CU3 (`formatVersion` 2) carries MPE blobs: `NblMigrations` step 2 to 3 does nothing to the manifest and the open-time converter runs as for any legacy blob. The first save writes 3.

### 7.5 D2 loss analysis: what a conversion keeps and loses, editor by editor

**Decided 2026-11-23: D2 B, drop.** The user chose, after this analysis, to convert on open and keep nothing that does not convert (the earlier D2-b). The analysis stays as the record of exactly what is dropped. D2-a and D2-c below are **not chosen**; *Convert to Clip* is not built.

Asked for by the user (2026-11-23): "Analyze which data is lost exactly on conversion". Evidence: the code (`BMXFile.cs`, `Gear.cs`, `ReBuzzCore.cs` `CreateEditor`, `ReBuzzPatternXP`'s `SetPatternEditorData` and its `patternxp` sources, `PatternEditorUtils.cs`, `Machines/ModernPianoroll`, `RunTime/Gear/gear.xml`) and one test on real data (below). **No audio was rendered**: every statement about sound is read from code and marked *unverified* until CU1's goldens.

**What "convert" means.** The target is the clip's lanes (§5): a Note or parameter column per (machine, parameter, track), events at time = row × 240, no player field. The converter is the same one old songs already go through when no editor DLL is present (§7.1).

**Two facts that change the question.**

1. **Conversion through MPE is already today's behaviour wherever the original editor is missing.** `BMXFile` (around line 474) replaces a missing pattern editor by the Modern Pattern Editor, and keeps the blob only if `Gear.HasSameDataFormat` says the formats match (all four editors named in `gear.xml` are marked `DataFormat="PatternXP"`, so they match); any other editor name has its blob **cleared**. The Studio, headless, macOS and Linux never have `ReBuzzPatternXP`'s DLL, so for them PatternXP songs are already read by MPE's `SetPatternEditorData` (v1–3 rows, MPE 255 v1). On Windows the same happens for *Jeskola Pattern XP* and *Jeskola Pattern XP mod* unless `ReBuzzPatternXP.NET` is built.
2. **No repo song uses any pattern editor at all** (§3: 0 of 18 old songs have an editor blob, XTAP or TAP2; the two `.nbl` templates have MPE blobs with 0 events). So D2 decides how *the user's own songs* and old songs outside the repo behave, and nothing in the repo can show a difference. That is why the analysis below says "unverified" for audio.

**The editors** (everything the package ships in `RunTime/Gear/gear.xml` plus what a song can name):

| # | Editor (name in a song) | Data format | Ships? | Repo songs |
|---|---|---|---|---|
| E1 | none: Buzz's own pattern rows (`PATT`) | rows × parameters per track, plus input columns | in every song | 18 of 18 old songs (154 patterns) |
| E2 | **Modern Pattern Editor** (MPE, WPF and portable) | MPE format `0xFF` v1 (exact times, per-beat rows, no durations) or PXP v3 | yes (`ModernPatternEditor`) | 0 (2 templates, empty) |
| E3 | **Modern Pianoroll** (`Machines/ModernPianoroll`, upstream's prototype) | MPE-format copy; its `Editor.ExportMidiEvents` returns null; marked `PatternXP` in gear.xml | yes | 0 |
| E4 | **Jeskola Pattern XP** (native, `ReBuzzPatternXP`) | PXP v1–3 (rows; v2 adds rows per beat; v3 the graphical flag), plus its SP columns | native build only | 0 |
| E5 | **Jeskola Pattern XP mod** | the same format and DLL family | native build only | 0 |
| E6 | Any other editor a song names (Buzz's own, third-party) | opaque | no | 0 |

**Features, per editor (E4/E5 are the ones that lose something).**

| Feature | E1 Built-in rows | E2 MPE | E3 Pianoroll | E4/E5 PatternXP | E6 other |
|---|---|---|---|---|---|
| Note, velocity and track-parameter cells | **Exact.** Same events the PXP v3 path makes now. Checked on real data: all 18 songs' patterns, **2,415 cells** compared PATT → PXP column mapping, **0 mismatches** (local script `pxp_check.py`; the rows→events step is the code the editor path already runs) | **Exact** (time, value) | Exact, if its data reads as MPE (same structures; **unverified** on a real blob: none exists) | **Exact** (rows; row × 240) | **Lost**: the blob is cleared today on open if the data format differs; the converter has nothing to read |
| Global parameters, columns of other machines | Exact | Exact | Exact | Exact **if the machine's parameter layout is the one the blob was written for**: the index is positional; a changed machine misaligns, as it does today | Lost |
| Event durations / ramps | n/a (none) | n/a (none in v1) | Pianoroll notes have lengths; **converted as note-on and off** (**unverified**) | none | Lost |
| Rows per beat of a clip, column order, mutes | View state, kept as `ClipView` / `Grid` | Kept as view | Kept | v2/v3 rows per beat kept; column order kept | Lost |
| Per-beat subdivisions | none | **Kept** as `BeatSubdivision` (MPE-only) | n/a | **PatternXP ignores them**: no data | Lost |
| Delay and cut (MPE internal parameters −130, −131) | none | Becomes the note's start + delay and length from cut (the Notes lane, CU3) | Pianoroll: "Todo: delay & cut" in its export | PatternXP's own delay and cut (scale **96**) are *effect* data inside the SP columns, see next row | Lost |
| **PatternXP SP columns** (internal parameters −10…−112: sub-pattern **triggers** and **play mode**, **offset**, **diatonic transpose**, **modulators**, loop and reverse) | none | none | none | **Lost, in every conversion, and already lost today** whenever MPE reads such a blob (`GetParameter` returns null for the SP indices; the columns are dropped). A pattern that is only a trigger for sub-patterns converts to **an empty clip** | Lost |
| **Input columns** (connection amp and pan) | Lost today on open; **not converted** by design (§11) | – | – | – | – |
| Chords | not data | not data | not data | not data | – |
| Machine-side notes (MIDI machines: Note, Velocity, Delay, Cut, Pitch Wheel, CC) | n/a | Become the Notes lane at migration (§7.1); played by the engine | n/a | n/a | – |

**Sound after conversion, now that the engine replaces the editor's own playing (D3 direct).** Applies to E4/E5, and to E1 and E2 as the change report measures it (§5.6):

| Playback detail of PatternXP (`patternxp/PatternXP.cpp`, `MachinePattern.h`) | What the engine would do instead | Status |
|---|---|---|
| **Subtick timing**: events fall on sub-tick boundaries by its own rounding | The engine puts an event at its exact 1/240-tick time inside the block | Differs by under a sub-tick; **unverified** by ear; CU1's probe measures the block |
| **Delay and cut scale 96** vs the engine's note model (MPE: 95 in its own fields) | The store has the real time, so a delayed note starts where the row plus fraction says | Differs by 1/96 vs 1/95 of a tick on a delayed note; **unverified** |
| **Monophonic note ordering**: a new note on a track ends the previous one at its subtick, in PatternXP's order within the tick | Note and Off are separate events; an Off and a Note on the same tick keep the order the store has | Edge case only (two events in one tick on one track); **unverified** |
| Loop, reverse, play-mode of sub-patterns | Not in the store (they are the SP columns) | **Lost**, see above |

**What each choice would do to the user's songs**

| Choice | PatternXP songs (E4/E5) | Other editors (E6) | MPE (E2) and rows (E1) |
|---|---|---|---|
| Convert on open (D2 B) | Note and parameter content exact; SP columns lost; the sound shifts by the playback details above (**unverified**); the original blob is kept only inside the `.bmx` as a derived export (so a saved-back song loses the SP data for good) | Cleared as today (no change) | Converted (the rest of this design) |
| Leave alone (D2 A) | No change of sound; the clip is visible only as a read-only preview outside the editor; **cannot be edited in the Studio** (no editor DLL there) | Cleared as today | – |
| Convert on request (*Convert to Clip*) | Not built (D2 B) | – | – |

**Sub-choices that were offered** (chosen: **D2-b**, "drop"):

- **D2-a (recommended): convert on open, keep the original.** The converter runs in memory as for old songs. PatternXP's SP columns, and the blob, stay in the clip as an opaque, unplayed `Owner=<editor>` record so they are not destroyed on save; the loss report names each clip that had SP columns ("clip X: 3 sub-pattern triggers not converted; they stay in the song file, silent"). Nothing the user wrote is deleted; sound can differ by the details in the table above.
- D2-b: convert on open, keep nothing. The simplest code; SP columns are gone on the next save. It is today's Studio behaviour for these songs, now made explicit.
- D2-c: convert only when the clip has no SP columns (a pattern with only notes and parameters, which is exact in the data); a clip with SP columns stays as it is, read-only preview, no conversion.

**Verified and unverified, in one list.** Verified: the PATT→PXP column mapping (2,415 cells, 0 mismatches); what `gear.xml`, `BMXFile.cs` and `Gear.cs` do with a missing editor and a data-format mismatch; that `PatternEditorUtils.GetParameter` drops the SP indices; that no repo song carries an editor blob. **Unverified** (no PatternXP song, no PatternXP DLL on this machine, no renders): every sound difference, the Pianoroll's blob, and a parameter-index misalignment on a real changed machine.

---

## 8. Readers and writers after

| Who | After |
|---|---|
| Playback | The engine plays every lane. There is no editor player in the Studio (D3). Classic's WPF build keeps MPE's own playing, unchanged |
| Arrangement automation lanes, EV1, T2 | One reader: the lane's events, whoever wrote them (the "tracker columns" read-only source goes away) |
| Piano roll | `SongNotes.For` for every machine; `Editor*Notes` and the roll's Tracker / MIDI lanes / Song lane switch go away (one store, one source) |
| Tracker | MPE over the store (§6), as a view and editor; note+velocity column pairs for machines that take notes; view-only for old machines |
| Recorders | Unchanged (they write S1, S2) |
| MCP | `notes_*`, `automation_*`, `arrangement_automation` see all lanes; `automation_read` lists tracker automation; one new read, `clip_get` (§9) |
| Save, load, export, Classic, upstream | §7 |

---

## 9. The rename

### 9.1 Where "pattern" shows

| Place | Count (grep, 2026-11-23) | Action |
|---|---|---|
| Studio UI strings with "pattern" | 106 in 32 files (`PatternView.cs`, the Sequence tab's list, menus, tooltips, `AutomationProperties.Name`) | "Clip". The Pattern tab becomes the **Clip** tab; "Tracker | Piano roll" stays |
| Browser | `browser_search` kind "patterns", the Patterns section | "Clips" |
| MCP tools named pattern | `pattern_create`, `pattern_delete`, `pattern_duplicate`, `pattern_list`, `pattern_rename` | New `clip_create`, `clip_delete`, `clip_duplicate`, `clip_list`, `clip_rename`; the old names stay as aliases (same handler, "deprecated: use clip_…" in the description) for **two releases**, listed in `agent-coverage.md` as aliases |
| MCP tools taking a `pattern` argument | 25 of 151 tools (`notes_*`, `automation_*`, `arrangement_*`, `session_*`, `audio_*`, `takes`, `select`, `view_show`, `window_layout`…), 101 `"pattern"` argument keys in 11 files | Accept `clip` and `pattern` (either); outputs carry `clip` and, for two releases, `pattern` too |
| `arrangement_clip` and the arrangement's "clip id" | A **placement** today | Keep the tool name; argument `clip` stays the placement id and `at`; the description says "placement". `arrangement_get` adds `placements` beside `clips` for two releases |
| Agent guides | 183 lines in `ReBuzz.Agent/Guides`, 81 markdown files mention "pattern" (guides, machine pages, docs) | Reword; the guide `concepts.md` gains the glossary line |
| Tests keyed on labels | 482 string literals with "pattern" in `ReBuzz.Studio.Tests`, `ReBuzz.SessionTests`, `ReBuzz.AgentTests` | Change with the labels, one wave |
| Specs | 34 docs | `clips.md` is the vocabulary; older specs get a one-line pointer, not an edit of history |
| Code identifiers | `IPattern`, `IPatternColumn`, `PatternCore`, `PatternColumnCore`, `IMachine.Patterns`, `CreatePattern`, `PatternEvent`…: 650 `.cs` files, 33 `.cpp`, 42 `.h`, 27 `.xaml` mention "pattern" (D1 B) | Renamed, behind a compatibility layer, §9.4–9.7 |
| **Stays (by interface or by file, not by choice)** | `.bmx` section names (`PATT`, `XTAP`, `TAP2`) and `.bmxml` element names; machine and DLL names a song stores (`Modern Pattern Editor`, `Jeskola Pattern XP`, `ReBuzzPatternXP`); the Buzz SDK header `MachineInterface.h` and the `CPattern` vtable; the reflection names the managed host looks up on a machine (§9.4); Classic's UI text (frozen); upstream-facing text (loss report categories) | §9.4 |

### 9.2 One new read

`clip_get(machine, clip)`: the clip's length, grid, its notes (the note model of §5.3), each parameter lane with its points and ramps, who plays each lane, and its launch and audio settings. It replaces the three reads an agent makes today (`notes_read`, `automation_read`, `session_get` for the clip) and is the first tool that sees tracker data.

### 9.3 Collisions to settle in the same wave

`session_clip_set`, `audio_clip_*`, `ClipSettings`, `ClipNotes` already mean the content. `arrangement_clip` and `SequenceEvent.Clip` mean the placement. After the rename the UI says "clip" for both and the shared badge says "linked ×N" (the clips that share the content). The code and the MCP descriptions say **placement** for the run on a lane.

### 9.4 D1 B: which renamed names an outside binary or source binds to

D1 is decided **B** (2026-11-23): the code identifiers are renamed too. Four kinds of code that we do not build bind to names. For each: what breaks if the name changes, and the way to keep it working.

| Interface | Bound by | Names that would change | What breaks if renamed | Keep working by |
|---|---|---|---|---|
| **`BuzzGUI.Interfaces.dll`** (managed API, `ReBuzzGUI/BuzzGUI.Interfaces`) | Managed machines compiled against it: the **7 shipped** (`Machines/AudioBlock`, `EnvelopeBlock`, `ModernPianoroll`, and others), every third-party managed machine, and any managed machine **cherry-picked from upstream** | `IPattern` (17 mentions in its file), `IPatternColumn` (11), `IMachine.Patterns`/`CreatePattern`/`GetPattern`, `ISequence`'s pattern members (13), `IBuzz`, `ISession` | A .NET binary references types and members by assembly, namespace and **name**. A renamed `IPattern` makes the machine fail with `TypeLoadException` or `MissingMethodException` the first time it touches it. **Type forwarders do not help**: they redirect a type that *moved to another assembly*, not one that was *renamed* | **Do not rename the public names in that assembly. Add the new names beside them** and make the old ones aliases: `interface IClip`, `IClipColumn`; `interface IPattern : IClip` (kept, marked `[Obsolete(error: false)]`); the old members stay on `IMachine` and forward to the new ones (default interface members, supported by the .NET runtime the engine uses). `PatternCore` becomes `ClipCore : IClip, IPattern`. The assembly keeps its name `BuzzGUI.Interfaces`, so the binaries load unchanged |
| **Reflection names the host looks up** (`ManagedMachineHost.cs` lines 254–268) | A managed machine that *implements* them as plain methods: `PatternEditorControl`, `SetEditorPattern`, `CreatePatternCopy`, `SetPatternEditorData`, `GetPatternEditorData`, `GetPatternEditorMachineMIDIEvents`, `SetPatternEditorMachineMIDIEvents`, plus the fork's `ExportClipNotes`, `ImportClipNotes`, `EditTrackerEvents` | Those method names | The host finds them with `GetMethod("<name>")`; a renamed host lookup silently finds nothing and the editor stops working, with no error | The host probes the **new name first, the old name second**, for the whole list. The machine author's side does not change. Documented as the managed machine ABI in `docs/specs/` and covered by one test per name |
| **Native machines** (`MachineInterface.h`, `NativeMachineFramework/`, `ReBuzzPatternXP/`) | Buzz's machine SDK: `CPattern`, `CMachineInterface`, `CMICallbacks::GetPatternName`, `CreatePattern`, `GetPatternLength`… | The **callbacks and `CPattern`**, if we renamed them | A native machine is bound by **vtable order and struct layout**, not names, so the binary would still run; but anyone recompiling a machine against our header breaks, and the header is Buzz's SDK, kept to stay compatible with every Buzz machine source | **Keep `MachineInterface.h` and the callback names as they are** (they are the SDK). Rename only our own wrapper classes (`PatternManager` in `NativeMachineFramework` and `ReBuzzPatternXP`: 189 + 52 mentions, 33 `.cpp`, 42 `.h` in all), internal to the DLL |
| **Plugin export** (`PluginExport/`, CMake; 1 file mentions it) | The plugins we export (VST/CLAP descriptors, `plugins.json`), and any DAW state they saved | Descriptor and state keys, if any contain "pattern" | A host that saved plugin state under a key would lose it | Not renamed: state keys and descriptor ids are persisted names, like section names. Check in CU5 (**unverified**: the one mention was not read) |
| **MCP and agent surface** | Agents, eval scenarios (`ReBuzz.AgentEval`), saved agent notes | `pattern_*` tools, 25 tools' `pattern` argument, 101 argument keys | Already settled in §9.1: **aliases for two releases**, both argument names accepted | §9.1 |
| **File formats** | Every song ever saved; upstream ReBuzz reading our `.bmx` | Section and element names (`PATT`, `XTAP`, `TAP2`, `<Pattern>` in `.bmxml`), the TAP2 key names, `.nbl` manifest keys | Old files stop opening; upstream stops reading our exports | **Not renamed.** Readers accept old keys forever; a new field gets a new name and `.nbl` formatVersion rules (native-format.md) apply |

**Rule.** *A name that a binary, a saved file or a reflection lookup stores stays; the new name is added next to it.* Everything else is renamed.

### 9.5 The rename, re-estimated

Counts (grep, 2026-11-23, `.cs`, `.cpp`, `.h`, `.xaml` that mention "pattern" in any case): **752 files** (650 `.cs`, 33 `.cpp`, 42 `.h`, 27 `.xaml`), plus 105 `.md`, 35 `.py`, 49 `.json`. By area: `ReBuzz` 131, `ReBuzzGUI` 100, `ModernPatternEditor` 80, `Machines` 77, `ReBuzz.Studio.Tests` 63, `ReBuzz.Studio` 61, `ReBuzzTests` 47, `ExternalMachines` 45, `ReBuzz.Agent` 17, the rest under 10 each. The earlier §9.1 estimate (UI, MCP, guides, tests: about 1–1.5 pw) was for a text change. The code rename is a **mechanical symbol rename** (a Roslyn rename, not a text replace, so strings and file formats are untouched) followed by review.

| Wave | What | Files (about) | Size | Gate |
|---|---|---|---|---|
| **CU5a** compatibility layer first | New names beside old in `BuzzGUI.Interfaces`; `ClipCore : IClip, IPattern`; the host's two-name probe; a test machine compiled against the **old** assembly (a checked-in binary) that must load and play | 15 | 0.5–1 pw | Old binary machine loads; the 7 shipped machines pass; native framework unchanged |
| **CU5b** engine and GUI interfaces | Core types and members inside the engine: `PatternCore`→`ClipCore`, `PatternColumnCore`, `WorkManager`, `BMXFile`, `ReBuzzCore`, `ReBuzzGUI` | about 230 | 1 pw | CI full; renders bit for bit (the CU1 goldens); `.bmx`, `.nbl`, `.bmxml` byte-equal round trips |
| **CU5c** editors and machines | `ModernPatternEditor` (WPF, portable, Avalonia), the 7 shipped managed machines (they may keep using the old names: they are a test of the layer), `ExternalMachines`, native wrapper classes | about 200 | 0.5–1 pw | Same; Classic's tests unchanged |
| **CU5d** Studio, agent, tests, text | UI strings (§9.1), MCP, guides, 482 test literals, specs | about 320 plus text | 1–1.5 pw | Studio tests, agent eval |
| **Total** | | 752 files | **3–4.5 pw** (was 1–1.5) | |

### 9.6 What it does to cherry-picking from upstream (the 2026-11-15 rule)

The policy is: upstream is **cherry-picked, not merged** (`FORK.md`, last merge base `af162b9a`). Measured on the base: 2,384 files; of the 752 files that mention "pattern" today, **279 also exist in upstream's base**. After a rename, a cherry-pick of an upstream commit conflicts wherever its hunk touches a renamed line in those 279 files (new files are not affected).

- **Machine fixes and new machines** (what the policy says to take) compile against `BuzzGUI.Interfaces`, which keeps the old names, so they apply and build **unchanged**. This is the strongest reason for the compatibility layer in §9.4.
- **Loader and core fixes** (`BMXFile.cs`, `ReBuzzCore.cs`, `WorkManager.cs`) hit renamed names, and will conflict wherever they mention a pattern. Cost: the conflict is only names, so resolving takes minutes, but it is every such pick.
- **Mitigation**: keep the identifier map as a file (`tools/clip-rename-map.txt`, old name → new name) and a small script that applies it to an upstream patch before `git am -3`. A pure identifier map is mechanical, so most picks apply with no hand work. Strings and format names are not in the map.
- **The rule needs one sentence added** in `FORK.md`: *"Cherry-picks run through `tools/clip-rename-map` first; machines that compile against `BuzzGUI.Interfaces` need no change."*
- **Decision cost.** The rename is paid once (3–4.5 pw). The cherry-pick cost is small per pick and stays for as long as the fork takes upstream's fixes. A fork that never takes upstream core fixes pays nothing more.

### 9.7 Order and risks of the rename

- **Order:** CU5a (layer) can start any time after CU1; CU5b–c should be a **quiet window**: no other branch open on `ReBuzz/`, `ReBuzzGUI/` and `ModernPatternEditor/`, because a rename touches 750 files and every open branch conflicts. Do it **after** EV1 and MT1 have merged (they add labels) and **before T2**, but **after CU4** if the readers and writers are being rewritten at the same time, so the rewrite is not made twice (§10).
- **Risks:** a renamed reflection lookup that silently finds nothing (§9.4: the two-name probe and a test per name); a renamed string that a saved file or the agent eval depends on (the Roslyn rename leaves strings alone; the map lists the few string keys that do change); Classic is **frozen** but shares the sources: its tests run in every sub-wave.

---

## 10. Phases

Waves of about one branch. Gates in the second table apply to every wave from CU3.

| # | Wave | What | Size | After |
|---|---|---|---|---|
| **CU1** | Corpus, change report, timing measurement | Tests and tools only. (a) The **corpus**: every repo song, plus a **generated** song for each feature: classic multi-track Note + velocity + command columns; TPB 1, 4, 8; 1-row and 256-row patterns; rows per beat 4, 6, subdivided beats; MIDI lanes with delay and cut; a Note column with explicit Off and an Off with no note before it; a parameter column of another machine; 8 tracks; a track-count change; a looped session clip with automation; a PatternXP-format blob with SP columns (hand-built); a chord of 4 notes. (b) **Before-renders** of each, SHA-256 and audio, per platform, in `eval/clip-goldens.json` (hashes only). (c) `tools/clip-change-report.py`: after-render against before-render per song: identical or not, first differing sample, peak and RMS of the difference in dB, **cause tag** from the converter's migration log (§5.6). (d) The timing measurement of §5.6, so the first report is predicted | 1–1.5 pw | now; parallel to EV1 and MT1 |
| **CU2** | The store learns what trackers need | Engine and `BuzzGUI.Common` only, no view, no behaviour change: `Grid` and `Mute` metadata; explicit note-off events; `ClipMigration` (PATT / `BuiltInPattern`, PXP v1–3, MPE v1 → lanes, MIDI lanes → the Notes lane, SP columns and foreign data dropped with a log line each); `ClipExport` (lanes → MPE blob and PXP, the double-play rule, the stamp). Round trips on the whole corpus: blob → store → blob has the same events | 2–3 pw | CU1 |
| **CU3** | **One engine, direct** (was CU3 + CU6) | The seam (§6); migration on open in Studio and headless; the engine plays **every** lane; MPE's playing code and the editor-player path **deleted from the Studio build** (Classic's WPF build keeps its own); `ClipView` (column set, order, rows per beat, mutes, which note column shows which note); `.nbl` v3; derived blob in `.bmx`/`.bmxml`; the **old-machine view-only mode** (§5.7: the `clipEdit` manifest flag, the refusals in tracker, roll, agent); the open-time loss report; EV1's and the arrangement reader's one source; the change report run and read by the user | 4–5.5 pw | CU2 |
| **CU4** | One reader and writer layer | `SongNotes` for every machine; `Editor*Notes`, `ExportClipNotes` bridges and "Move notes" removed; `automation_read` and `notes_*` see all lanes; `clip_get`; clone, replace, snapshots on columns only | 1.5–2 pw | CU3 |
| **CU5** | The rename (D1 B: UI, MCP **and code identifiers**) | §9, four sub-waves: **a** compatibility layer (new names beside the old in `BuzzGUI.Interfaces`, host probes both names, an old-binary test machine), **b** engine, **c** editors and machines, **d** Studio, agent, tests, text. Mechanical (symbol rename) but 752 files: needs a quiet window. **a** can start after CU1. **b–d** after EV1 and MT1 have merged and after CU4 (the readers and writers are rewritten there, so the rename is not done twice), **before T2 starts its views** | **3–4.5 pw** (was 1–1.5) | a: CU1; b–d: EV1, MT1, CU4 |
| **CU6** | **Real notes** | **(a) Audit and fix, start any time:** every Neo machine, Neo Plug and Neo Plug FX, and every exported plugin takes note-on and note-off **with velocity, any polyphony** (by a test per machine: chord of N notes, overlapping notes, velocity 1 and 127, note-off stops the right voice, legato; the profiler's `machine-profiler.py` extended), and the manifest says so. Fixes as found. **(b) Note columns in the tracker (after CU3):** + and − for note+velocity column pairs on a machine that takes notes; chords typed across pairs; `ClipView` keeps which pair shows which note; − refuses to hide notes | (a) 1–2 pw (the audit decides; unknown until run), (b) 1–1.5 pw | (a) CU1; (b) CU3 |
| **CU7** | Retire | The Studio's legacy list store, `PXPDataFormat` write, the blob's event section, `TrackNotes.Allocate` and `MaxTracks` refusal, `EditorPreviewNotes` | 0.5–1 pw | CU4 |
| **LM** | **Legacy machines** (its own track; the user's words: "analyze all of them and decide a. which ones is worth to rebuild as Neo native machines and which ones can be converted on load and mapped to existing Neo machines") | **LM0 analysis, a separate worker** (not this design): every machine in `RunTime/Gear`, `ExternalMachines`, the Pedal, R1 and Jeskola families (§5.7) and managed third-party: what it does, whether a Neo machine already covers it, the parameter mapping for a load-time conversion (Jeskola Verb → Neo Verb as the user's example), what the sound difference is, and the verdict per machine: **rebuild** as a Neo native machine / **convert on load** to an existing Neo machine / **stay old** (view-only). Output: a table the user decides from. **LM1** the load-time machine conversion (a `MachineMigration` step beside `ClipMigration`: maps a machine's name, parameters and its clips' columns to the Neo machine, in memory, with a loss report); **LM2..n** rebuilds, one wave a family, sized by LM0. Each converted machine gets `clipEdit: true`, which unlocks its clips | LM0 1 pw (estimate); LM1 2–3 pw; rebuilds sized by LM0 | LM0 now (parallel); LM1 after CU3 |

**T2 starts after CU3** (store-backed parameter columns) and runs in parallel with CU4–CU7. With D1 B, T2's new views use the **new names** from the start (`ClipCore`, `IClip`), so CU5a should have merged before T2 writes its first class; CU5b–d then rename what T2 added along with the rest. T2 and EV1 must not write to an old machine's parameters (§5.7). **Sizes:** CU1–CU7 total **14–20.5 pw** (CU6a unknown until audited), LM on top.

### 10.0 CU1 as built

Tests and tools only; no behaviour change in the app or the engine.

- **Corpus.** `tools/clip-null.py list`: every repo song (the 20 old songs and 2 templates, found under `ExternalMachines` and `Other`) plus **18 generated songs**, written by code (`generate`; no song file or audio is committed): `gen/classic-tracks`, `gen/tpb1`, `gen/tpb4`, `gen/tpb8`, `gen/rows1`, `gen/rows256`, `gen/rpb6`, `gen/subdivided`, `gen/explicit-off` (both Offs), `gen/midi-lanes` (delay and cut, 2 rows per beat), `gen/midi-lanes-rpb4`, `gen/other-machine`, `gen/tracks8`, `gen/track-change`, `gen/session-loop` (engine columns only, looped, with automation), `gen/chord4`, `gen/chord4-midi`, `gen/pxp-blob` (a hand-built PatternXP v3 blob with SP columns). The tracker stand-in is Pedal Tracker (up to 16 tracks), the MIDI synth is Neo Poly.
- **Goldens.** `render` renders each song (at most 32 beats plus a 1 s tail) through `rebuzz-headless` and hashes the WAV; `check` compares with `eval/clip-goldens.json` for the platform (`windows-x64`, `linux-x64`, `macos-arm64`) and fails naming the songs; `merge` folds a platform's `observed.json` in. `--twice` renders again and marks a song that differs. Windows renders with the real machines (the package job), Linux and macOS with placeholders. Wired into `fork-ci.yml` (Linux), `fork-mac.yml` (macOS) and `fork-package.yml` (Windows).
- **Gate split (the coordinator's D3 decision).** Each golden carries `tracker: true|false`. `tracker: false` (only `gen/session-loop`, and any song with no tracker data) is the hard gate of §10.1; the others are what the change report compares against.
- **Open time.** `opentime` opens the largest songs five times in fresh processes and records the best time (the cost gate of §10.1; artifacts `open-times.json`).
- **Goldens as run (first run, 2026-11-23).** 40 songs on each of `windows-x64` (real machines), `linux-x64` and `macos-arm64` (placeholders), committed in `eval/clip-goldens.json` (hashes only). Every song rendered twice in separate processes: **all deterministic** on all three platforms, nothing excluded. `repo/FSM_Kick_Demo.bmx` and `repo/Makk_M4.bmw` do not open on any platform (their machines are missing; status `open failed`, kept as the golden). Silent renders: 8 on Windows, 18 on Linux and macOS (old songs whose machines are placeholders). `check` fails on: a non-deterministic song, a render failure, a missing platform or song, and a changed render of a song with `tracker: false`. A changed render of a `tracker: true` song is printed as `CHANGED` and a CI warning, with the song named, and does not fail: it is the change report's input.
- **Open times (Windows, best of 5, ms; the cost baseline stored under `open_ms` in the goldens).** Bass House Template 5111, Tech House Template 4566, FSM_Infector_Demo 2676, MadBrain_Dynamite6_Demo 485, FrequencyUnknown_O-Delay_delaytest 463, gen/rows256 428, gen/tracks8 426, Jeskola_NiNjA_dElaY 216. Linux (placeholders, ms): Bass House 3575, Tech House 3155, rows256 254, tracks8 259, the old demos 115 to 156 (their machines are placeholders). macOS arm64 the same within 10%. A rerun of the Windows runner showed the same song 20 to 34% slower (3234 vs 2676 for FSM_Infector), so a cost gate must use a ratio of at least 1.5 on a shared runner.
- **Probe.** §5.6.
- **Not built here.** `tools/clip-change-report.py` needs the converter and its migration log (CU2, CU3); CU1 gives it the before-renders and the predicted differences (§5.6).
- **Findings.** Pitch Wheel and CC lanes with a note lane on one track throw in MPE (render); a MIDI-lane delay or cut is quantised to the tick; two repo songs (`FSM_Kick_Demo.bmx`, `Makk_M4.bmw`) cannot open without the native machines (status `open failed`, deterministic, kept as a value).

### 10.0a CU2 as built

Engine and `BuzzGUI.Common` only; no view, no behaviour change (the CU1 goldens are unchanged on all three platforms). Nothing calls the converter on open: that is CU3.

- **Metadata.** `Grid` (`rpb=4;sub=3:6,7:3`: rows per beat, and the beats that have another count) and `Mute` on a lane's column, as TAP2 already reads and writes metadata (no file-format change). `ClipMeta`, `ClipGrid` in `BuzzGUI.Common/ClipLanes.cs`.
- **Explicit note-off.** On the Notes lane an event with duration 0 and a `0x80` message (`ClipEvents.Off`) is an off with no note before it in the clip; it names the note that rang from the clip before when the converter knows it (else note 0, tag `off-note-unknown`). Round trips through `.bmx`, `.bmxml` and `.nbl` (ClipConverterTests).
- **ClipMigration** (`BuzzGUI.Common/ClipMigration.cs`, pure; `ReBuzz/Core/ClipStore.cs` is the engine's side). Sources: `PATT` (`BuiltInPattern`), PXP v1 to 3 (and a placeholder editor's opaque data), MPE v1 become parameter lanes; MIDI lanes (Note, Velocity, Delay, Cut) become the Notes lane through `TrackerNotes.FromRows`. Dropped, one log line each: PatternXP's SP columns (`pxp-sp-dropped`), data of another editor or a truncated blob (`foreign-dropped`), pitch wheel and CC lanes on a track with a note lane (`cc-lane-dropped`; the editor throws on them while it renders), the amp and pan cells of input columns (`input-column`: the loaders drop them when the editor takes the patterns, so `PatternCore.LostInputCells` counts them), a column of a machine, parameter or track the song lacks (`machine-missing`, `param-missing`, `track-dropped`), an event past the pattern's end (`event-past-end`). Pitch wheel and CC lanes of a track with no note lane become controller events (`cc-lane-converted`; the editor does not play these lanes, so the engine plays something new). Every step writes a `ClipLog` entry with a cause tag and a kind (`loss`, `change`, `info`); the change report treats `loss` and `change` as explaining. In memory only; the file is never written.
- **Cause tags.** Changes: `block-shift` (a parameter event moves from the editor's block to the engine's), `subtick`, `midi-tick`, `midi-delay`, `midi-cut`, `note-fixed`, `cc-lane-converted`, `off-note-unknown`. Info: `off-explicit`, `midi-not-in-blob`, `auto-not-in-blob`. Losses: the tags above and, on export, `lane-not-in-blob`, `mute-not-in-blob`, `export-quantized` (PXP rows), `ramp-sampled`.
- **ClipExport.** Lanes to the MPE blob (v1) or PXP v3, the derived blob. The double-play rule: the Notes lane stays out of the blob (the editor would play the same notes again), so do lanes marked `SessionAutomation`. The stamp is the SHA-256 of the store's content, appended after the patterns; a blob whose stamp is not the store's is stale.
- **Round trips on the whole corpus** (`ClipRoundTripTests`; Fork CI Linux, Mac, and the Windows `studio-windows` job). The repo songs (opened from disk) and the 18 generated songs (built in the open session by `ClipCorpus`, the same features as `clip-null.py`; with `CLIP_CORPUS_DIR` the python-generated files too). Checked per machine: every event is kept, merged into a note or dropped with a log line (in = kept + merged + dropped); lanes written into the columns read back equal; blob to store to blob has the same events (stamp equal); store to blob to store has the same lanes; every dropped column has a log line. On Linux: generated 18/18 songs, 1531 events in = 1242 kept + 179 merged into notes + 110 dropped; repo songs 20/22 opened (`FSM_Kick_Demo.bmx` and `Makk_M4.bmw` do not open, as in the goldens), 2551 in = 2415 kept + 136 dropped (the amp and pan cells of input columns). The table and the migration log are the `clip-roundtrip` artifact of Fork CI.
- **The change report.** `tools/clip-change-report.py` (tests: `tools/test_clip_change_report.py`, synthetic WAVs and log) and `clip-null.py render --audio <dir>`, which keeps each render's WAV for it. The goldens hold hashes only, so CU3 must render the before audio on a build before its change.
- **Open for CU3.** The loaders drop input columns before any converter could read them: CU3 migrates before or with the loader. The CC value encoding is a guess, and the editor does not play pitch wheel and CC lanes. Notes in the MIDI column play beside the editor's own until CU3 stops the editor playing them: a song written with a Notes lane is not to be played before then.

### 10.0b CU3 as built

**Scope.** The user chose a clean break (option C, 2026-11-23): old Buzz songs (`.bmx`, `.bmw`, `.bmxml`), old machines (native Buzz DLLs, Pedal, the prebuilt managed ones, Polac) and Classic are removed in a later wave (CB1). CU3 therefore builds the engine and the store for our own songs: no change report and no listening pack, no loss panel, no view-only mode for old machines. Classic's WPF build still compiles (the player in the Modern Pattern Editor stays under `#if WINDOWS`).

One store, one engine (D3). In the Studio and headless the engine plays every lane and every pattern editor views the store. `EngineHost.PrepareProcess` turns it on (`ClipStore.UseDirectEngine` sets `PatternColumnCore.PlaysEveryLane`); Classic and the engine's own tests keep the old semantics.

- **Migration on open** (`ClipOpen.Run`, called by `ReBuzzCore.OpenSongFileCore` after the load: machines, then the engine columns, §7.1 order). For our own `.nbl` v1 and v2 songs: each machine's pattern editor data (PatternXP or Modern Pattern Editor) and built-in rows become lanes once (`ClipMigration`, `ClipStore.Write`, the Notes lane merged with the MIDI column), the machine's editor is told to view the store (`AttachClipStore`, bound by name in `ManagedMachineHost`), the song is marked migrated in memory (`SongCore.ClipsMigrated`), and what cannot convert is dropped with one log line each (`SongCore.ClipOpen`, the cause tags of §5.6; no panel). A `.nbl` of format 3 holds its lanes already and is not migrated again. The file is never written on open. Placeholder machines (missing gear) are skipped and keep their data.
- **The engine plays every lane** (`WorkManager.PlayPatternColumnEvents`): a parameter lane (marked or not) sets its value before `CallTick`; the Notes lane goes through `SendMidiInput` with exact time. `PlayRecordManager.Play`, `MPEPatternColumn.PlayColumnEvents` and the `PatternEditor.Work` call are `#if WINDOWS` only. **No double play:** `ANoteInTheMidiColumnSoundsOnceWhetherWrittenOrMigrated` counts the note-ons a probe machine takes (1, written or migrated, at calls of 64 and 512 frames).
- **The window floor: none.** A host call that leaves fewer than about 22 samples at the start of a tick gives the engine an empty window there, so the value lands in the next block. `AValueOnATickThatStartsWithAShortFirstBlockWaitsAtMostOneSubTick` sweeps 120, 125 and 133 BPM at calls of 64, 128 and 256 for the three kinds of lane: a parameter lands **261 to 264 samples** after the tick (one sub-tick, 6 ms at 44.1 kHz), a MIDI note lands **3 to 18 samples** after (inside the short block). It happens when a tick starts fewer than 22 samples before the end of a host call (about 9% of ticks at 256 frames, 17% at 128). A floor would change when non-tracker data lands too, which breaks their bit-for-bit gate. Decision: no floor in CU3; if a song ever needs one it is one line in the block window, and the sweep is its test.
- **The tracker views the store** (`MPEPatternColumn`: `Bound`, `StoreLane`). A column reads and writes the pattern's unmarked lane for its parameter and track (made on the first write); the lane's `EventsChanged` reaches the view; the lane's `Grid` and `Mute` metadata are the column's rows per beat and mute (the ClipView of §5.2, kept with the lane). The Notes lane shows through the note host (AR3). A machine made with editor data outside a song load keeps its own data and does not attach (a known limit). `AutomationReader.TrackerColumns` reads the unmarked lanes: one source for the arrangement reader and for EV1's envelopes (`ClipEnvelopes`; the engine's own points are the marked lanes only: `IsMarkedAutomation`).
- **`.nbl` format 3** (`NblMigrations.Current`; step 2 to 3 is empty: v1 and v2 files open and migrate on open). The editor data in a v3 file holds no events; `minReaderVersion` is 3 when the song holds tracker lanes.
- **Left in, harmless, for CB1 to delete.** The derived blob in `.bmx`/`.bmxml` (`ClipDerived.Provider`, `ClipStore.Export`, `ClipBlob`), the `NBST` stamp and its handling in `tools/song-roundtrip.py`, `ClipEditPolicy` (it allows everything; `Rule`, `AllowAll`, `IsWritable`), the `clipEdit` manifest flag in 57 manifests and in `tools/gen-machine-params.py`, `tools/source_manifests.py` and `PlugManifest`, `ClipDerived.EditRefusal`/`Refused` (the tracker's hook, never refuses), `ClipWords`, the BMX input-column count (`LostInputCells`), the `--audio` upload steps of the clip goldens in the three workflows, and `tools/clip-change-report.py`.
- **Tests.** `ClipOpenTests` (migration, `.nbl` round trip twice, format 3), `ClipTimingProbeTests` (the probe counts note-ons; the editor half of the CU1 probe is gone with the editor's player).

#### What changes for old songs (user help)

Our own songs open as before. Their pattern data moves into the new clip store on open and is played by the engine's one player, so a few things sound slightly different: a value in a machine's pattern arrives at the start of its tick, about 6 ms earlier than before; a note on a machine that takes MIDI starts on its row, a tick earlier; a delay or cut inside a row now counts. What cannot move is dropped with a line in the log. Nothing is written to your file until you save. Saving writes `.nbl` format 3, which older builds refuse (they would play it without its pattern data).

### 10.0c CU4 as built

**One layer.** `SongNotes.For(buzz, pattern)` is the one reader and writer of a clip's notes in the store. The piano roll, Session, Arrangement, the agent (`notes_read`, `notes_write`, `clip_get`), imports and exports all go through it. Which lane it uses follows `KeyboardRoute`: the MIDI route reads the Notes lane, the tracks route reads the Note and velocity parameter lanes (one voice per track). `For(buzz, pattern, "midi"|"tracks")` overrides the route (a Neo Poly takes both). `StoreLanes` reads every lane of a parameter (the tracker's unmarked lane and the marked take or agent lanes), merged by time, and writes into the lane that holds the event, or into the home lane for a new one. A write is one `ColumnEventsAction`, one undo step. `AllowMoreTracks` raises the track count (an undo step of its own) when the agent writes more voices than the machine has tracks.

**Gone.** `EditorTrackerNotes`, `EditorLaneNotes`, `EditorPreviewNotes`, `TrackerNoteSources`, the editor's `ExportClipNotes`/`ImportClipNotes`/`EditClipNotes`/`EditTrackerEvents` bridges, the Pattern View "Move notes" menu and `ClipNotesMoveAction`, the roll's lane box, and the agent's `ReplaceNotes`. Their tests were rewritten against `SongNotes`.

**Agent.** `automation_read` and `notes_*` see every lane (`source` is `pattern`, `automation` or `both`). `clip_get` returns a clip's lanes, notes and view. Clone, replace and snapshots copy columns only.

**CU3 follow-ups.** `MachineCore.ClonePattern` copies the original's columns when the copy has none, so a pasted or cloned machine plays its own pattern data (`ReplaceMachineAction` restores lanes the same way). The editor's MIDI Note and Velocity columns are hidden beside a Notes lane until they hold data or the user reveals them.

**Limits.** A Neo Poly's typed Note-parameter column (tracks route) is a separate store from the roll's MIDI column. `automation_write` writes marked lanes only. Session takes and recorders write the MIDI column directly.

### 10.1 Gates (every wave from CU3; CU1 sets them)

Direct engine (D3) changes the first gate: for data the engine now plays differently **by design**, the goldens become a **change report**; for everything else they stay a hard gate.

| Gate | How |
|---|---|
| **Unchanged by design: bit for bit** | Songs and clips with **no tracker data** (engine columns only: Session, Arrangement, takes, automation lanes, audio clips, a `.nbl` made before any pattern editor) and every generated song for those features: render, SHA-256 equal to the CU1 before-render. Windows package job for the real machines; Linux and macOS against their own goldens. A difference fails the wave |
| **Migrated tracker data: change report, not a gate** | Every repo song with `PATT` or blob data (the 18 old songs, the templates) and every generated tracker song: `tools/clip-change-report.py` (CU1) lists identical or not, first differing sample, peak and RMS of the difference in dB, and the cause tag. The user reads it at CU3. Expected: most identical or a block-shift on one machine; PatternXP-style loss songs differ by what was dropped. **An unexplained difference (no cause tag) is investigated before the wave merges** |
| **Open → save → open → save is stable** | `.nbl` A == B byte for byte; `.bmx` and `.bmxml` core sections byte-equal (`tools/song-roundtrip.py --compare`); the derived blob's events equal the migrated store's events on the corpus. (Unchanged) |
| **0 bytes per buffer** | `AudioHygieneTests` and `EngineHardeningTests` extended with a migrated tracker song (notes, automation, ramps, a 4-machine mix, a chord on 6 note columns) playing, and a take recording over it. (Unchanged) |
| **Undo and redo** | Each tracker, roll, lane and agent edit is one history step; undo restores events and `ClipView` exactly; a migration is not an undo step; undo across a clone and a delete keeps lanes. Adding or removing a note column is a `ClipView` step. (Unchanged) |
| **Old machines stay view-only** | Every write path (typing, paste, record, `notes_write`, `automation_write`, `clip_*`, EV1, T2) refuses on an old machine and says why; a machine with `clipEdit: true` is writable. A test per path |
| **Real notes** (CU6a) | Per Neo machine, Neo Plug, exported plugin: the note test of CU6a passes |
| **Old behaviour kept** | Classic's tests pass unchanged; the loss report's old rows unchanged; CI full |
| **Cost** | Open time of the largest song (Dye stress, FSM_Infector) within the NF1 measures ×1.5 (a ratio of at least 1.5, since the same song on a shared runner has been 20 to 34% slower: §10.0); the templates open faster (no empty columns) |

---

### T2 as built: tracker parameter lanes

- **What:** each parameter column's header has a toggle (not note columns) that opens a vertical lane strip (56 px at 100 %) right of the column. It draws the column's own events at the grid's row scale, scrolls with the grid and shows the play position. Click adds a point, a drag moves it (snaps to the column's rows; Alt = no snap), a drag on empty space selects, R switches ramp and step, Delete removes. Each gesture is one step of the song's history. Ctrl+Z with the lane focused undoes on the song's history; a typed value in a cell is undone on the editor's own stack.
- **Which lane:** the column's own (unmarked) lane, written through `ColumnEventsAction(own: true)`. EV1's Detail lane keeps writing the marked automation lane and shows the tracker lane read only as dashed steps and ramps (now with ramps). A Detail edit is therefore not mirrored into the tracker's cells: the reverse direction of "one curve" is covered by a typed value showing in the strip and the Detail model. Decide with EV1 whether the Detail lane should write the unmarked lane once the "tracker columns" read-only source goes away (§8).
- **Shared code:** `LaneController` (Studio, `Views/Detail`) holds the points, hit-test, selection and edit gestures for `EnvelopeLane` (horizontal) and `TrackerLane` (vertical). The editor is a separately loaded machine, so the seam is `BuzzGUI.Common.ClipLaneHost` (`IClipRowAxis`, `IClipLaneProvider`), additive.
- **Saved view:** `Lane` = `1` in the store lane's metadata (beside `Grid`, `Mute`), saved in `.nbl` with the lane; a song from before has no `Lane` and opens with every lane closed. An open lane keeps its column shown even when empty.
- **Allocation:** the strip's geometry is cached and rebuilt only when its data, width, extent or row scale change; scrolling and the play line draw from the cache. Steady-state `Prepare` allocates 0 bytes (test); a rebuild after an edit allocates the new geometry.

## 11. Risks and loose ends

| Risk | Handling |
|---|---|
| Timing (§2.3 item 2): the engine's timing differs from MPE's for migrated songs | **Accepted by the user** (D3 direct). The change report (CU1, run at CU3) shows which songs and by how much; no `Lag` emulation |
| The shared MPE sources change under Classic | `#if WINDOWS` keeps the list store; Classic's tests in every wave |
| Pattern-name identity | Unchanged here. A stable clip id (`ClipView`, `ClipSettings`, TAP2 all key by name) is debt; do it with CU7 if renames still lose data |
| A blob edited in Classic after a Studio save | The stamp (§7.2): the blob wins, with a message |
| Notes doubled upstream | One-place rule for MIDI machines (§7.2); tested by the NF2 simulated upstream read |
| Store size: a 12-byte event, a dense agent ramp is many events | Unchanged from today (S2); EV1 and the agent write ramps as single events |
| Input columns (wire amp and pan): lost today on open, 2 of 18 songs | **Not part of CU.** Restoring them changes how those songs sound. A `Wire` lane kind, listed in §5.1, is the place for them; the user decides separately |
| Chords | Solved in the store (D4): note+velocity column pairs in the tracker are views of the Notes lane (§5.3, CU6b). The WPF-only chord *tool* (`Chords/**`) stays out of the Studio |
| TAP2 has no per-column version | `Play` and `Owner` are metadata, which TAP2 already stores, so no change |
| Old machines become uneditable in the Studio (D4 A) | Accepted. Defined by one manifest flag (§5.7); LM decides which machines are converted or rebuilt first; Classic still edits everything |
| A song of old machines loses PatternXP data and sounds different | Accepted (D2 B, D3). The open-time loss report and the change report name it; the original file is never overwritten (`LegacyOrigin`) |
| EV1 (a parallel branch) writes automation to any machine | EV1's lane write must respect the old-machine flag; tell its owner (§5.7) |

---

## 12. Decisions for the user

**D1. How far does "Clips everywhere" go?** **Decided B** (2026-11-23): "Rename the code identifiers too, not only the UI." Plan §9.4–9.7. The old names stay **permanently** as obsolete aliases in `BuzzGUI.Interfaces` (confirmed to the user), so third-party and upstream machines keep loading.

**D2. Songs whose clips use a foreign pattern editor (PatternXP and others)?** **Decided B, drop** (2026-11-23): convert on open and keep nothing that does not convert (sub-pattern triggers, modulators and so on): gone at the next save, named in the loss report. §7.1, §7.5.

**D3. One player or two?** **Decided: one engine, direct** (2026-11-23): "I don't care about old songs compatibility that much. So: go with the unified engine (Direct) and we live with the consequences." The engine plays every lane from CU3; no editor player in the Studio; no `Lag`; goldens become a change report for migrated tracker data and stay a hard gate for the rest (§10.1). The consequence: migrated tracker songs may sound slightly different.

**D4. Notes for classic Buzz machines (tracks)?** **Decided A** (2026-11-23): "One store, one engine so let's go with A and add the 'one store one engine' as a design principle of NeoBuzz." Added to `FORK.md`. Extended by the user: all new machines and the plugin host take real notes (CU6a); the tracker plays chords through note+velocity column pairs (CU6b, §5.3); **old machines are fully view-only** until converted (§5.7); the write-side voice allocation is dropped. New track: **LM** (legacy machines, §10).

**Principles** (written into `FORK.md`, "Design principles", 2026-11-23): (1) one store, one engine; (2) real notes everywhere; (3) a spiritual successor, not a ReBuzz-compatible fork.

---


## 13. Sources

Read for this document: `ReBuzz/Core/PatternColumnCore.cs`, `PatternCore.cs`, `EngineColumns.cs`, `ArrangementAutomation.cs`, `Notes/*`, `Session/SessionDocument.cs`, `ReBuzz/Audio/WorkManager.cs` (block order, `UpdatePatternColumnEvents`, `UpdatePatternPositions`), `ReBuzz/FileOps/BMXFile.cs` (`LoadPatterns`, `CreatePATTSection`, `CreateTAP2Section`), `BuiltInPattern.cs`, `BuzzExport.cs`; `ModernPatternEditor/ModernPatternEditor.NET` (`MPEStructures`, `PlayRecordManager`, `PatternEditorUtils`, `NoteLaneColumns`, actions), `Portable/`, `Avalonia/`; `ReBuzz/Agent/*Tools.cs`, `MachineNotes.cs`; `docs/specs/daw-modes.md`, `session-view.md`, `native-format.md`, `agent-knowledge.md`, `agent-coverage.md`; the repo's songs (§3).
