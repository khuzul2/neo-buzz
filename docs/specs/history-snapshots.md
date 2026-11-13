# History and snapshots

Status: HS1 and HS2 built (HS1a the engine, HS1b the panel, the agent tools and the live test; HS2 full snapshots). HS3 built (glitch-free restore while playing; see 5.1).

**Goal.** One history for the whole song that every view shares, and full-song snapshots kept beside the song. Neither may disturb a performance: nothing here runs on the audio thread, takes `AudioLock` longer than today, or allocates while audio plays. A snapshot can be restored while the song plays, without a click.

**Decision (2026-11-12).** Option A, all of it: global undo/history, full snapshots, and glitch-free restore while playing. Snapshots live in a history folder next to the song, with an "embed in song" option on save.

## 1. Where things stand

- One song stack: `BuzzGUI.Common.ManagedActionStack`, which is `SongCore.ActionStack`. The studio's edits (`ParameterEdits`, `MachineEdits`, `SequenceEdits`), the Modern Pattern Editor, the WPF editors and the agent layer use it. An agent call is one action group, and a failed call is rolled back.
- Separate stacks: the Session View's `SessionLayout.UndoStack` (reset per song) and AudioBlock's `WaveUndo` (one level).
- Not undoable: MIDI-controller parameter moves, tempo and loop, machine state blobs (Dye Essence samples and wavetables, Neo FM6 banks, Neo Wave tables), pattern renames from the Modern Pattern Editor. WPF Classic sliders are not undoable and stay so: Classic gets bug fixes only.
- Opening a song clears the history. Studio ⌘Z goes to a focused text box, then the view's `IStudioEditTarget`, then the song stack.
- Snapshots exist for agents only (`ReBuzz/Agent/StateTools.cs`: parameters, mute/bypass, amp/pan), in memory, with no UI.
- Edits run on the UI/engine thread. `lock(AudioLock)` covers transport changes only. Session launches are quantised on `SessionCore.LaunchQuantization` and the latency-compensated `HeardClock`.

## 2. Principles

1. **Live safety first.** History code runs on the UI/engine thread. The audio thread never reads or writes it. Capturing a step costs a reference copy (an immutable state object, a before/after pair), never a walk over audio data.
2. **A step is a user gesture.** One drag, one load, one agent call. Steps carry a name, a time and a source (Manual, Agent, MIDI).
3. **Performance is not editing.** MIDI controller moves are recorded while the transport is stopped; while the song plays they are performance and are not recorded. Scene tempo changes and launches are performance.
4. **Blobs are shared, immutable and counted once.** Samples, wavetables and wave data are held by reference. A blob that three steps hold is counted once against the cap.
5. **The oldest steps drop first**, whole groups at a time, never the newest step.

## 3. HS1: one history (this round)

### 3.1 Decision: the Session View joins the song stack

The Session View's edits go on `SongCore.ActionStack`. `SessionLayout.UndoStack` becomes a view of it.

Why not a coordinator over two stacks:
- Order. With two stacks, undo has to decide which stack holds the newest step. That needs a shared clock and a merge rule, and every undo-like feature (the History panel, jump, snapshots) needs the same logic again. One list is the order by construction.
- Mixed steps. A clip edit and a machine delete are one timeline for the user. A jump to "before the delete" has to pass both.
- Cost. The session's actions are already `IAction`s. Session identity (`SessionLayout.Identity.cs`) already follows renames and structure changes from every view across every snapshot, so interleaving does not make its snapshots stale. The one thing that moves is the refresh after an undo: `SessionLayout.Undo` called `ContentChanged` and `ReshapeLaunched`. The song stack now raises an event after every undo, redo and jump, and the layout does the same refresh when the group held one of its actions, whichever view asked.

What changes for the user: ↶ ↷ in the Session View, and Ctrl+Z in its window, undo the newest step of the song, not only the newest grid edit. Launch undo (Ctrl+Alt+Z) is separate and stays: launches are performance.

### 3.2 The stack

`ManagedActionStack` keeps its API (`Do`, `Undo`, `Redo`, groups, `PostDoOrUndoGroup`) and grows:
- Steps (`HistoryStep`: id, name, time, source, bytes), `Position`, `JumpTo(position)` (undo or redo to there), a `Changed` event and an `Applied` event (undo, redo, do) for views.
- `Record(action)`: an edit already made by someone else (a controller move, a state load) goes on the stack without running `Do` again.
- `RecordCoalesced(action, key, gap)`: merges into the newest step when it has the same key, nothing was done since, and the idle gap (500 ms) has not passed. Actions that implement `ICoalescingAction` merge into one action, so a 10-second knob sweep is one small step.
- `Source` and `Label(name)` for the group being built. The agent host sets Agent and "Agent: tool".
- Memory: `MaxBytes`, `UsedBytes`, shared-blob counting (`IHistoryAction.Blobs`), group-aware trimming.
- `Applying`: true while an action runs, so engine setters that record a step themselves (pattern rename) do not record during an undo.

### 3.3 Gaps filled

| Gap | How |
|---|---|
| Tempo and loop | `TransportEdits` for the studio transport bar's tempo and loop button, and the Session View's tempo box. One step per gesture; tempo changes coalesce (500 ms). |
| MIDI controller moves | `MidiControllerAssignments` records a move through `RecordCoalesced`, keyed by parameter and track, only while the transport is stopped. A binding's soft-takeover stays as is. |
| Machine state blobs | `StateEditAction` holds a machine's `MachineState` object before and after, and sets it back through the machine's own property. Dye Essence's user tables and samples, Neo FM6's bank and Neo Wave's user table use it. Their state classes are copy-on-write, so a step holds a reference. Dye Essence's routes and FX rack are parameters and were already steps. |
| AudioBlock waves | `WaveUndo` pushes a step on the shared stack instead of keeping one level. The wave data is a float array captured once (the "after" is captured on the first undo, as before) and counted as a blob. |
| Renames | `MachineCore.RenamePattern` records a step unless the stack is applying. Machine and group renames already were steps. |

Not covered, by choice: mute/solo toggles in the Session View mixer (performance), Session settings (view, quantisation, MIDI map), WPF Classic sliders.

### 3.4 Memory cap

`HistoryMemoryMB` (engine setting, default 256) per song. The estimate is the sum of each action's `HistoryBytes` (default 256 B) plus each distinct blob once. When over, the oldest groups drop first. The newest step is never dropped, so a single huge edit still undoes. A dropped step cannot be jumped to.

### 3.5 History panel and agent tools (HS1b)

- A History tab in the studio inspector: the steps, newest at the top, with name, time and source; the current position marked; click to jump; steps after the position are dimmed (redo side). `StudioTokens` only.
- Agent: `history_list` (steps with id, name, source, time, current) and `history_jump` (to an id). `undo` and `redo` stay as they are. The agent guides and the tool reference say that one agent call is one step and that history is shared with the person's edits.

### 3.6 Tests

`ReBuzz.Studio.Tests/HistoryTests.cs`: each new edit kind; undo and redo across views (Session, Machine, Sequence) in the order done; coalescing; the cap; blob counting; jump; agent tools. HS1b adds the live test: while a big song plays headless, 1,000 edits plus undo and redo, with no audio-thread allocation (`AllocationWindow`) and the worst-block time within the throughput guard's tolerance. Songs load and render bit-identically; the null tests are unchanged.

### 3.7 As built (HS1)

- **HS1a (engine).** `ManagedActionStack` (`History.cs`): steps with name, time and source, `JumpTo`, `Record`, `RecordCoalesced`, `BeginSource`, the `MaxBytes` cap with shared blobs counted once, `Applying`, and `Changed`/`Applied` events that `SongCore` forwards as `HistoryChanged`/`HistoryApplied`. The Session View uses the song's stack, so Session undo reverts the newest step of any view. Steps for tempo and loop, MIDI controller moves (stopped only, 500 ms coalesce), state blobs, AudioBlock wave edits and pattern renames; agent calls are steps with source Agent.
- **History tab (HS1b).** `ReBuzz.Studio/Views/Inspector/HistoryTab.cs`, a class of its own that `InspectorPanel` adds as a third tab (the Selection tab's code is untouched). Newest step at the top: name, source (Manual, Agent, MIDI) and time. The current step is marked with a triangle and the row fill; steps undone (the redo side) are italic in the secondary text colour, not faded by opacity. Click, or Enter or Space on a focused row, jumps there (`JumpTo`: undo or redo as many steps as it takes) and marks the song modified. The panel redraws once per burst of changes, on the UI thread, whichever thread made them, and draws the newest 300 steps (the summary line says so and shows the dropped count). The line under the summary names Undo and Redo with `StudioKeymap` labels, so they are right per platform. Text colours are `StudioTokens` and every pair is at least 4.5:1 (`HistoryPanelTests`; the secondary colour is only 2.5:1 on the selection fill, so the current row's source and time use the primary colour).
- **Agent tools (HS1b).** `history_list` (`limit`, default 20, at most 500; read-only) returns the steps newest first with `id`, `name`, `source`, `time` (UTC), `done` and `current`, plus `current`, `total`, `dropped`, `can_undo`, `can_redo`. `history_jump` (`step`; destructive, idempotent) undoes or redoes to the state right after that step (0: before the oldest kept) and returns the same list's head; an unknown or dropped id is an error that names `history_list`. A failed call's taken-back step (the one `redo` already refuses) is left out of the list and cannot be redone by a jump. `undo` and `redo` are unchanged. The workflow guide's Undo section and checklist name them.
- **Live-safety test (HS1b).** `EngineHardeningTests.ThousandEditsWithUndoRedoAndJumpsWhileTheBigSongPlaysAreLiveSafe` (Windows package job): the 32-machine song of `AHeavySongRendersFasterThanRealTime` playing, 1,000 edits (tempo, pattern renames, undo, redo, jumps over many steps) each made between two 256-frame buffers, outside the timed read and the `AllocationWindow`. It asserts zero bytes allocated reading the buffers, the median buffer within 1.5x of the same song read without edits (plus 50 us), at least the guard's 25x real time, and the worst buffer inside its real-time budget. The edits run on the reading thread between buffers, not concurrently: what it shows is that undo and redo leave nothing behind for the audio thread to do, not lock contention with a UI thread.

## 4. HS2: full snapshots

**Capture.** A consistent copy on the UI thread: parameters, patterns, sequences, connections, machine state objects, wave references, the Session layout, tempo. This is a copy of references and small values. Then a background task serialises it into a content-addressed chunk store in `<song>.history/` (chunks named by hash, so a sample or wavetable used by 50 snapshots is stored once). A snapshot is a small manifest of chunk hashes plus a name, time, trigger and thumbnail data.

**When.** On save; every N minutes of editing (setting, default 10) when the song changed; before big agent or song-wide operations (replace machine, import, a song-wide edit tool, restore).

**Embed on save.** An option puts the snapshots in the song file as a new section that other Buzz versions ignore (as MID2 is). A song opened without its folder still has its history; one with both uses the folder.

**UI.** List, name, delete, diff (machines, parameters, patterns, sequence), A/B toggle between two snapshots, restore. Restore is itself one history step, so it can be undone.

**Agent.** `snapshot_*` tools use the full snapshots; `history_list` and `history_jump` cover the step history.

### 4.1 As built (HS2)

- **Capture.** `ReBuzzCore.CaptureSongSections` runs `BMXFile.BuildSections` on the UI thread: the song-file sections a save builds before it touches the disk (so every machine's state comes through its own save, Dye Essence samples and wavetables and native data blobs included, and the Session View layout through the `SaveSong` subsections), plus `SongSummary` (machines, parameter values, wires, patterns and sequences by content hash, waves). No file, no `AudioLock`. The background queue (`SongSnapshots`) cuts each section into content-defined chunks (`ChunkStore.Split`, a gear hash, 16-256 KB, one chunk under 64 KB), hashes (SHA-256), deflates and writes them, then the manifest. A snapshot is pending (listed, "saving…") until stored; `Flush` waits.
- **Store.** `<song>.history/manifests/<id>.json` and `chunks/xx/<hash>`. Untitled songs use `%LOCALAPPDATA%/NeoBuzz/History/untitled-<guid>` until the first save moves it (Save As to another name copies it). Deleting a snapshot removes the chunks no other one holds.
- **Autosnapshots** (trigger auto, named "Auto: ..."; the newest 40 kept, manual ones never dropped): on save (before the file is written, so an embed includes it), every `SnapshotIntervalMinutes` (default 5, 0 off) when the song changed and the transport is stopped, and before `machine_replace`, an agent `history_jump` over more than 20 steps, importing a song and every restore. The timer rides the autosave's one-minute tick.
- **Embed.** `EmbedHistoryInSong`: a save adds a `HIST` section (manifests plus the chunks they use, as stored); without it, or with no snapshots, the file is byte-for-byte what a save wrote before. Loading keeps the history folder's own snapshots if it has any, else imports the embedded ones into the folder. `.bmxml` songs keep the folder only.
- **Restore** replaces the song as opening a file does (`ReBuzzCore.RestoreSongBytes`: the name, the history folder and "modified" are kept or set), so the undo history starts again; the spec's "restore is one history step" is met by the auto snapshot taken first (restore it to go back), not by an undo step. Only while stopped: "Stop to restore (glitch-free restore while playing comes next)".
- **UI.** History tab, Snapshots section: list (name, time, manual or auto), save under a name, rename, delete, diff (A against B, or the selected one against the song now), A/B toggle (Set A, Set B, A/B), restore, the embed checkbox.
- **Agent.** `snapshot_save`, `snapshot_list`, `snapshot_restore` (destructive), `snapshot_diff` take full snapshots by name or id; `kind="parameters"` is the old session-only snapshot; `snapshot_delete` (destructive) is new.
- **Not done / limits.** Pattern hashes cover pattern columns and built-in data, not a pattern editor machine's private state beyond what its own save writes. Waves are compared by name and size in diffs. The History panel's list is not capped (40 auto snapshots at most).

## 5. HS3: glitch-free restore while playing

- **Parameters and patterns** are applied together at the next beat or bar on the Session launch clock (`SessionCore.LaunchQuantization`, the latency-compensated `HeardClock`), with parameter glides so nothing steps.
- **Structure.** New machines are created and prepared off the audio path, then swapped in at the boundary with a 10-20 ms crossfade; the old ones fade and are disposed later.
- **When a machine cannot be prepared safely while playing** (native machines with unknown state, a machine that allocates on create), the restore waits until the transport stops and says so.
- **Guards.** No audio-thread allocation. Worst-block time unchanged on the big test song while a restore happens. Restore then render is bit-identical to the original.

### 5.1 As built (HS3)

- **Live.** `snapshot_restore` (and the panel's Apply at: Bar, Beat, Now) takes the quantization; the default is bar while playing. The restore is planned off the audio path (`LiveRestorePlanner`), takes an "Auto: before restoring X" snapshot, and runs from `SessionCore.OnTick` when the launch clock reaches the boundary. Parameters glide over one beat (tick/subtick points; Word/Byte parameters with range above 32 glide, others step at the boundary). Pattern column events, sequence events, flags and wire amp/pan change at the boundary. Changed or new machines are built hidden as "X (restore N)", fed and sequenced, silent until the boundary, then swapped with the engine's amp ramp (about 4 blocks, 23 ms at 256 frames; wider than the 10-20 ms asked). Old machines are deleted at finish. The result says live, deferred, or applied, with the wait in ms.
- **Deferred to the stop** (UI line, agent result and history entry say so): tempo/TPB differences, waves, a missing master, a gear not installed, a new machine that has a pattern editor, a native machine whose state differs, a swapped machine with a pattern editor whose data differs (Modern Pattern Editor data cannot be swapped live), sequence track counts or wire channel counts that differ, and a restore asked before the launch clock has a position.
- **Not restored live:** the Session View clip grid; tempo is set by the UI thread at finish.
- **Undo.** A restore is one step on the song stack (`RestoreAction`); undo is a restore of the "before" snapshot (live again if playing). The auto snapshots a step depends on are pinned against pruning. Where a step cannot be a single action (a deferred restore that is cancelled, or a stop-time restore that replaces the song object), the "Auto: before restoring X" snapshot remains the way back.
- **Guards.** The audio-thread boundary and glide allocate 0 bytes (Linux test); the Windows live test on the 32-machine song checks allocation, the worst block, and the largest sample step against a baseline. The restore-then-render check uses the HS2 tolerance (-50 dB), not bit identity.

## 6. Decisions and risks

| Decision | Why |
|---|---|
| Song stack, no coordinator | One order; fewer states (§3.1). |
| State objects by reference | Copy-on-write states make a step a pointer; serialising would copy megabytes per step. |
| Chunk store, not a file per snapshot | A sample is stored once; snapshots stay small. |
| Source recorded per step | The panel and agents can tell a person's step from an agent's. |

| Risk | Measure |
|---|---|
| Session snapshots grow with the layout | Counted by size against the cap; the layout is small. |
| An undo that touches a deleted object | Each action finds its object when it runs (by name if the object is gone), as the existing actions do. |
| Controller moves flood the stack | Coalesced; recorded only while stopped. |
| Restore while playing clicks | HS3 guards; defer to stop when not provably safe. |
| Snapshots of machines with state that cannot be captured (native) | Parameters and the machine's saved data (`Data`) are captured; a machine that cannot restore live is deferred. |
| A new undo history changes what ↶ does in the Session View | Said in §3.1 and in the Session View's tooltips. |
| Memory cap drops steps the user wanted | A setting; the panel shows how much is kept. |

## 7. Measures

- History code on the audio thread: none (reviewed, and the HS1b live test).
- Cost of a step: a controller sweep of 1,000 moves is one step of a few hundred bytes; a sample load holds one reference.
- Steps kept within the cap and the dropped count, shown in the panel.
