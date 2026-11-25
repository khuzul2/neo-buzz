# Note wires

A note wire carries notes from one machine to another in the same block, at the sample offsets they were made at. Audio still goes left in, right out; notes go on their own wires (dotted in the Machines view, from a diamond on the bottom edge of the sender to a diamond on the top edge of the receiver). A machine that sends notes runs before the machines it sends to.

| Tools | What they do |
|---|---|
| `note_wires`, `note_wire_connect`, `note_wire_disconnect`, `note_chain` | all modes; each edit is one undo step |

Who can take or send notes: the Neo synths, Neo Sampler, Neo Drums and Neo Plug (as an instrument) take notes; Neo Voice to MIDI, Neo Audio to MIDI and a Neo Plug that makes MIDI send them. `note_wires()` lists those machines, the free wires and the insert chains.

Two ways to use them on a track:

- **Free wires**: `note_wire_connect(source="Voice", target="Lead")` sends the notes of one machine to another, across tracks. `note_wire_disconnect(source="Voice", target="Lead")` removes it.
- **Note inserts**: `note_chain(instrument="Lead", inserts=["Arp","Chord"])` puts note machines before the instrument. The track's clip notes, live MIDI (the selected or armed track) and mapped keys enter the first insert, each insert feeds the next, and the last feeds the instrument. `note_chain(instrument="Lead", add="Arp", index=0)` adds one; `note_chain(instrument="Lead", remove="Arp")` takes one out and joins its neighbours.

A wire that would make a loop (with the audio wires and the other note wires) is refused, and the error says which machine already runs before which. A machine that takes no notes, or an unknown name, is refused with the reason. Renaming a machine carries its wires; deleting one hides them, and undoing the delete brings them back. A song saved with a Neo Voice to MIDI or Neo Plug target opens with the same target as a wire.
## Checklist

- [ ] The sender and the receiver are listed by `note_wires` with `note_out` and `note_in`.
- [ ] The wire or chain is there after the edit: `note_wires` shows it.
- [ ] Notes arrive: `midi_play` on the track sounds through the receiving machine.
- [ ] One undo step per edit: `history_list` shows it.
