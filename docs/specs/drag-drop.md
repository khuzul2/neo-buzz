# Drag and drop (DD1)

Dragging a browser item (Browser panel results) or a file from the desktop onto the Arrangement, the Session or the Machine View. One router decides, all three views and the agent use it: `ReBuzz/Core/Drop/DropRouter.cs` (`Plan` says what would happen and why not, `Drop` does it). The drag data is BR1's (`neobuzz-browser-item`, see browser.md); files from the desktop are read as audio (several allowed) or one MIDI file.

## The rules

| Dropped | On empty space | On a track |
|---|---|---|
| Generator, or an instrument plug-in (Neo Plug) | A new machine and track, wired to Master. In the Arrangement an empty clip at the drop time. | Replaces the track's generator (BR1's hot-swap: connections, name and position kept, patterns made again by name and length). The same gear again is refused. An audio track is refused. |
| Effect | Refused: "Drop on a track." (Machine View: added loose, as in BR1.) | Inserted between the track's last machine and Master. A track that branches asks which branch (a menu; `target.branch` for the agent). |
| Audio file(s) | A new Neo Audio track with the clips. | Audio track: clips at the drop time (Arrangement, one after another) or from the drop slot (Session, down the scenes). Neo Sampler: the samples load. Any other track: a new audio track. |
| Pattern | Refused. | On its own machine's track: a clip at the drop time or in the slot. Another machine's: refused. |
| MIDI file | A new track on the default instrument (Neo Poly, then the last instrument a drop made) with the notes. | An instrument's track: the notes in a new pattern at the drop time or in the slot. An audio track or an effect: refused. |
| Preset | A new track with that machine and the preset. | On a track of that machine, the preset loads; on another's, refused. |

Every drop is one undo step. The MIDI parser is our own (`MidiFile`, formats 0, 1 and 2; tracks and channels merged; SMPTE time refused; at most 200000 notes). The file's tempo and meter are only reported in the message, never applied.

## Feedback while dragging

- Arrangement: the track under the pointer is lit (the error colour when refused); a line at the bottom for a new track; a guide where the drop lands, snapped to the beat (hold Shift for the tick); the label says what would happen or why not.
- Session: the slots the drop fills are lit (several files go down the scenes), the track lit, a line right of the last track for a new one, the label by the pointer.
- Machine View: the machine under the pointer lit, the label by the pointer.
- After a drop the words of what it did (or why it was refused) show for a few seconds.

## Agent

`drop_item(view, kind, id | path | paths, preset, target{track, at_beats, slot, x, y, branch, instrument}, plan)` does the same through the router, one undo step; `plan=true` changes nothing. The other tools do the parts (`machine_add`, `machine_swap`, `audio_import`, `notes_write`, `arrangement_place`, `preset_load`); `drop_item` is the one call with the drop's rules, and the only one that reads a MIDI file or inserts an effect after a track's last machine.

## Tests

`ReBuzz.Studio.Tests/DragDropTests.cs`: every row of the table in both the Arrangement and the Session (headless, real drops through the views' handlers' entry points), each with undo and redo as one step and a save and reload; the feedback; the Machine View; `drop_item`. Gallery shots: `drag-drop-*`.

## Limits

- No drag of clips or tracks between views (that is not a drop from the browser).
- A track that branches asks once; a drop onto a machine of a branch picks the way by its own track.
- A MIDI file's tempo map, meter and program changes are ignored; channels are merged.
- Neo Drums is synthesised and takes no samples; audio dropped on it makes a new audio track.
