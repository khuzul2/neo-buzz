# Note wires — Specification

| | |
|---|---|
| Status | NW1 (issue #171): design, then built in stages; see the status line at the end of each section |
| Date | 2026-11-24 |
| Scope | The base for note machines (Neo Chord, Arp, Notes, Pulse, Turing, Groove, Harmony: later tasks). Builds no new user-facing note machine. |

## 1. What it is

A **note wire** carries MIDI-style note events from one machine to another, in the same block, at the sample offsets they were made at. It is separate from the audio wire: audio still goes left in, right out; notes go top and bottom.

Two decisions by the project owner (2026-11-24):

1. **Look: own ports, dotted.** A machine that takes notes shows a small diamond *note-in* port on its top edge; one that sends notes shows a diamond *note-out* port on its bottom edge. A note wire is **dotted** (dashed already means a send to a return) with a note arrowhead, in the source track's colour. View ▸ "Note wires" hides them. Ports show only on machines that have them.
2. **On a track: both.** (1) *Note inserts*: a track has an ordered chain of note machines before its instrument. The track's clip notes, live MIDI (selected or armed track) and mapped keys enter the first insert, each insert feeds the next, the last feeds the instrument (like MIDI effects in a DAW). (2) *Free note wires* from any note source to any note target, across tracks. **One mechanism**: an insert chain is note wires the track owns.

## 2. Model

* **Capabilities.** A machine declares *note in* and/or *note out*. At run time through two interfaces in `BuzzGUI.Interfaces` (`INoteCapable` with `AcceptsNotes` / `SendsNotes`, and `INoteReceiver.ReceiveNotes` for the delivery); in the catalogue through two optional manifest fields `note_in` / `note_out` (written by `tools/machine_catalog.py` from `Machines/Manifests/machine-catalog.json`; `python3 tools/machine_catalog.py` reports 0 problems). A test checks the two agree for every machine the catalogue marks.
  * In: Neo Poly, Neo FM6, Neo Wave, Neo Patch, Neo Sampler, Neo Drums, Dye Essence, the Neo Learn synths, Neo Plug (instrument).
  * Out: Neo Voice to MIDI, Neo Audio to MIDI, Neo Plug when the plug-in makes MIDI (`SendsNotes` follows the loaded plug-in).
  * A machine with both is a note effect (an insert). A control machine (no audio) can't have note ports.
* **`NoteMessage`** (`BuzzGUI.Interfaces`): `Offset` (samples into the block), `Kind` (on, off, controller, pitch bend, pressure), `Channel`, `Data1`, `Data2`. A struct, so queues are arrays of it.
* **`NoteWire`**: `(Source, Target)`, machine **names** (as pins, MIDI maps and track colours do), unique per pair, source ≠ target, source must send, target must accept.
* **Insert chain**: per target machine ("the instrument"), an ordered list of insert machine names. Derived wires: `insert[i] → insert[i+1]` and `last → instrument`. The *track input* (clip notes, live MIDI, mapped keys, all of which the engine today hands to the instrument through `MachineManager.SendMidiInput`) goes to `insert[0]` instead while the chain is not empty. A chain belongs to the instrument machine, so it follows the instrument through renames and is the same thing as "the track's chain" in the Machines view (the track is the instrument's track).
  * A wire into an instrument that has a chain enters the instrument directly (it is a free wire); to enter the chain, wire to its first insert.
  * An insert is any note-in and note-out machine, and belongs to one chain at most (the same machine can't sit in two; a free wire is the way to share).
  * Removing an insert reconnects the chain (its neighbours become adjacent).
* **`NoteWireStore`** (`ReBuzz/Core/NoteWireStore.cs`, on `ReBuzzCore.NoteWires`): the free wires and the chains; `Connect`, `Disconnect`, `InsertInto(instrument, insert, index)`, `RemoveInsert`, `MoveInsert`. Each is **one undo step** (`ActionStack.Do`, a named `HistoryName`). Events: `Changed`.
  * **Rename** (`ReBuzzCore.RenameMachine`) carries wires and chains to the new name, as pins do; no step of its own (the rename's undo renames back).
  * **Delete** of either end: the wire stops being active and is not drawn; the store keeps it by name so that undo of the delete brings it back, and the song's save writes only wires whose two machines exist. (Known edge: a new machine given a deleted machine's name before a save picks the wire up.)
  * **Cycle**: refused (§4).
* **Saved** in a versioned `NoteWires` subsection (`NoteWireStore.SubSectionName`, `FormatVersion = 1`) in `NblFile.KnownStores`, in .nbl; written only when there is something to write; a newer version is left unread with a warning. XML:
  `<NoteWires version="1"><Wire from="A" to="B"/><Track instrument="I"><Insert name="X"/><Insert name="Y"/></Track></NoteWires>`.

## 3. Delivery

The engine builds a **note plan** from the store when the graph changes (a `TopologyGeneration` bump, a machine added or removed, a rename, a store change), under the audio lock, never on the audio thread: for each machine, its note sources and its note targets, with a preallocated queue per wire.

* **Emit.** A source calls `INoteEmitter.EmitNote(in NoteMessage)` on its own `MachineCore` (during its `Work`). The core copies the event into each outgoing wire's queue. No locks, no allocation: each wire queue is a fixed array (capacity 1024 events per block; further events in a block are dropped and counted) owned by the source's thread for the block.
* **Receive.** Before a target's `Work` (in `MachineWorkInstance.TickAndWorkCore`) the engine merges the queues of its incoming wires (and the track-input queue, if it is the first insert) into one list sorted by offset (stable: ties by the wire's creation order, so the result does not depend on which thread ran which source) and calls `ReceiveNotes` once. A target without `INoteReceiver` gets the events through its existing `MidiNote` / `MidiControlChange` (offsets lost).
* **Same block, same offset.** An event emitted with offset *k* in block *n* is received with offset *k* in block *n*, for every block size. (Neo synths apply received events at the start of the block, as they do for every MIDI event; per-sample placement inside a synth's block is a later step. The offset is carried to the synth's queue.)
* **Order.** A note source runs before its targets. All work-order algorithms use the same rule: a machine can work when its audio inputs *and* its note sources are done. In the cached order (`CachedWorkOrder`) note edges are in the cone, the in-degrees, the successor lists and the self-check; in the legacy walks they are part of the "can work" test and the recursion. A note source or target that is not connected to the Master by audio is still run when a machine of the cone is reached through a note wire.
* **Track input.** `SendMidiInput` (clip notes, live MIDI, keys) and the two note-off sites of the clip player route through `NoteWires.RouteTrackInput`: with a chain the event is queued for `insert[0]` (offset 0), else it goes straight to the machine as before. `MachineCore.SendMIDINote` (a machine sending to another) is unchanged: it is the old direct path.
* **Audio-thread rules.** Queues and merge scratch are allocated with the plan; the per-buffer path allocates 0 bytes (tested with 16 wires active); no locks the UI can hold: the plan is swapped under the audio lock, the track-input queue is a small lock-protected ring written by the MIDI and tick threads and drained by the audio thread (as `NoteInput` is).

## 4. Cycles

The combined order graph is the audio connections plus the note wires plus the derived chain wires. `Connect` and `InsertInto` check, before changing anything, whether the new edge would make a cycle (a pure, tested function over names: `NoteWirePlan.WouldCycle`) and refuse: `Connect` returns a reason, the Machines view shows it in a tooltip on the target, the agent tool returns it as an error. The audio connect path is checked the same way against note wires: an audio connection that would close a loop with the note wires is refused with the same reason.

## 5. Migration of the single-target routing

* **MidiRouter** (Neo Voice to MIDI, Neo Audio to MIDI): the machine's saved *Send MIDI to machine* target (a name) becomes a wire `machine → target` when the song opens (the machine loads its state, then `NoteWires.Adopt(machine, name)` creates the wire without an undo step and clears the machine's own target). The Commands menu "Send MIDI to machine" now lists the machines that accept notes and creates or removes wires through the store (one undo step each; several wires allowed). The device output and the MIDI From "Machine: X" bus are untouched: `MidiRouter.Send` emits through `EmitNote` for the wires and keeps the device and bus paths.
* **Neo Plug** "MIDI out to": the same: the saved `MidiOut` name becomes a wire at open; the menu creates and removes wires; events keep the frame they have in the plug-in's output.
* "Sound the same": the old path delivered at the target's next block; the wire delivers in the same block, so a migrated song's notes arrive up to one block earlier. This is the point of the change (§3). A test renders the old and the new route of one song and compares them within one block of latency.

## 6. Machines view

* **Ports.** A diamond on the top edge (note in) and bottom edge (note out) of a machine box that has them. Hovering a port or a wire highlights it and names it in the status line. While a note wire is dragged, only machines with note in show their port lit.
* **Wires.** Dotted, a note arrowhead, in the source track's colour (the machine's own colour in free mode); drawn from the bottom of the source to the top of the target. View ▸ "Note wires" hides them (ports stay). Free canvas and Tracks mode both draw them; in Tracks mode the inserts of a track sit left of its instrument in the track's row, joined by short dotted wires.
* **Making and removing.** Drag from a note-out port to a machine with note in; or right-click a machine ▸ *Send notes to* ▸ (the machines that accept notes); select a wire and press Delete, or right-click it ▸ *Remove*. A refused wire (cycle, wrong kind) shows the reason in the tooltip.
* **Drag and drop.** A machine with note in and note out dropped on an instrument's box or on its track in Tracks mode goes into the chain before the instrument (the existing drop rules for effects on wires and tracks are unchanged for audio machines).
* **Inspector.** A machine's Inspector card lists its note chain (when it accepts notes): the inserts in order, each with move up, move down and remove, and the free wires into and out of it.

## 7. Agent

Tools (in `ReBuzz/Agent/NoteWireTools.cs`; guide page `ReBuzz.Agent/Guides/…`): `note_wires` (list: free wires, chains, which machines have note ports), `note_wire_connect(source, target)`, `note_wire_disconnect(source, target)`, `note_chain(instrument, inserts[])` (set the chain: add, remove, reorder in one undo step). Errors carry the reason (cycle, not a note machine, name unknown). `python3 tools/agent-coverage.py` regenerates `docs/agent-coverage.md`.

## 8. Test machine

`Test Note Echo` in `ReBuzz.Studio.Tests/TestMachines` (named so that it sorts last in the gear menus: a menu one item longer pushed the tests' clicks off the window) (nothing ships): note in and out; echoes each note up 12 semitones at the same offset and a delayed copy a fixed number of samples later (to cross a block edge). Used for the chain and ordering gates.

## 9. Gates (from the brief) and where each is tested

| Gate | Test |
|---|---|
| Offset *k* in = offset *k* out, same block, blocks 16/64/256/512, chains of 3 | `ReBuzz.SessionTests/NoteWireTests` (pure plan, merge) and `Studio.Tests/NoteWireEngineTests` (real engine with `TestNoteEcho`) |
| Source runs before target against the audio order; cycle refused | both |
| Clip → A → B → instrument in order; remove reconnects | `NoteWireEngineTests` |
| Save and reopen; rename and delete of either end; one undo step per edit | `NoteWireEngineTests`, `NblFormatTests` |
| Old songs with a MidiRouter or Neo Plug target open with the wire | `NoteWireMigrationTests` |
| 0 allocations per buffer with 16 wires | `AudioHygieneTests` (extended) |
| Existing tests and clip goldens unchanged | CI |
| Gallery shots (ports and wires, free and Tracks, dark and light, hidden) | `GalleryTests`, read from `ci-artifacts-mac` |

## 10. Using note wires (user guide)

* **Make a wire.** In the Machines view a machine that takes notes (Neo Poly, FM6, Wave, Patch, Sampler, Drums, Dye Essence, Neo Plug) has a small diamond on its top edge; one that sends notes (Neo Voice to MIDI, Neo Audio to MIDI, a Neo Plug whose plug-in makes MIDI) has one on its bottom edge. Drag from the bottom diamond to a machine, or right-click the sender and choose **Send notes to**. A refused wire (a loop, a machine that takes no notes) says why in a tooltip and changes nothing.
* **Read it.** A note wire is dotted with an arrowhead, in the sender's colour (its track's colour when Tracks is on). Point at it to name it. The **Note wires** button in the bar hides the wires; the diamonds stay.
* **Remove it.** Click the wire and press Delete, or right-click it and choose **Remove**. Undo puts it back (one step).
* **Inserts.** A machine that both takes and sends notes can go into a track's chain before its instrument: right-click it, **Note insert before**, then the instrument. The Inspector's Notes section lists the chain for the selected instrument or insert, with move up, move down and remove. Clip notes, live MIDI and mapped keys enter the first insert.
* **Old songs.** A song whose Voice to MIDI, Audio to MIDI or Neo Plug named a target opens with the same target as a wire.

## 11. Decisions and limits

* Wires are keyed by machine **name** (as pins and track colours are); a delete keeps the wire in the store, a save writes only wires whose two machines exist.
* A free wire into an instrument that has a chain enters the instrument itself, past its inserts; wire to the first insert to go through the chain. An insert sits in one chain at most.
* Notes typed in the Tracker's columns are not diverted into a chain (only clip playback notes, live MIDI and mapped keys are).
* The Neo synths receive the offset with each event but apply the events at the start of the block, as they do for every MIDI event; Neo Plug honours the frames.
* Tracks mode draws the inserts where the machines are (no machine is moved), with the chain's order numbers on the short wires; it does not lay the inserts out left of the instrument.
* A machine dropped from the browser is not turned into an insert automatically: no machine ships that both takes and sends notes yet, and the catalogue is not read at drop time. Use the menu, the Inspector or `note_chain`. The drop rules for audio machines are unchanged.
* A synth's note-in port exists whether or not anything is wired; a Neo Plug shows note-out only while its loaded plug-in makes MIDI.
