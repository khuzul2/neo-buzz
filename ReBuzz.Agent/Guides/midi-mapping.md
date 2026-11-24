# MIDI mapping

Any MIDI controller can drive the studio: keys play the selected track, knobs move the parameters of that track's page, and a map ties any other control to a parameter or a command. Everything here is the same as what a person does in the app (map mode, the MIDI button, the Inspector's MIDI page), with the same validation and undo.

| Tools | What they do |
|---|---|
| `controller_list`, `midi_map_list`, `midi_map_set`, `midi_map_clear`, `track_page_get`, `track_page_set` | all modes; reading a map needs no MIDI input |

`controller_list()` names every MIDI controller (a port; a second of the same name is "Name #2"), its knob count and whether its keys follow the selected track. `midi_map_list(scope="active")` is what is in force now: the selected track's current page, then the global map. `midi_map_set(scope="global", device="Pads", kind="note", number=36, command="play")` maps a control; `scope="track"` fills the knob slot of the selected track's page (a control that is a knob of the device's group) or adds a direct entry. `midi_map_set(op="knob_group", device="Faders", first_number=21, count=8)` teaches a group. `midi_map_clear(machine="Bass", parameter="Cutoff")` unmaps. `track_page_get(track_id="track:Bass")` lays the page out as the knobs; `track_page_set(slots=[{"knob":1,"machine":"Bass","parameter":"Cutoff"}])` fills it. A free knob falls back to the generator's pinned parameter of the same number. Each call is one undo step.
## Checklist

- [ ] The controller you map is listed: `controller_list` names it (and its knob count).
- [ ] A map is in force: `midi_map_list(scope="active")` shows it for the selected track, then the global map.
- [ ] A track page holds what you meant: `track_page_get(track_id=…)` lays it out as the knobs.
- [ ] A mapping edit is one undo step: `history_list` shows it, and `midi_map_clear` takes it away.
