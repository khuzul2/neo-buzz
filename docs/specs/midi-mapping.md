# MIDI mapping (MM1 as built)

Any MIDI input is a controller; nothing is model-specific. This is what MM1 built and what MM2 adds.

## Model (`ReBuzz/Midi/Mapping`, `ReBuzz/Core/MidiMapping`)

- **Controller**: every MIDI input, named by its port name plus an index for duplicates, remembered per computer. Any number work at once.
- **Source**: (device, kind CC / note / pitch bend / program change, channel, number). A map may name "any device"; old Buzz bindings become such maps.
- **Target**: a parameter (machine, parameter, track) or a command: play, stop, record, loop, previous track, next track, select track n, arm, mute, solo.
- **Drive**: range, invert, takeover (pickup / jump / scale). Absolute and relative encoders are told apart while learning (`EncoderDetector`). 14-bit and NRPN are out of scope.

## Scopes

- **Track page**: belongs to an MT1 track id and is saved in the song. A page has N slots, N being the device's knob-group size (default 8); a track may have several pages. A knob group is learned once per device; its knobs drive slots 1..N of the selected track's current page. An empty slot falls back to the generator's Inspector pin of the same number (shown with a grey ring).
- **Global**: per computer, with an optional per-song copy.

## Router order (MIDI thread, from a snapshot the UI rebuilds)

1. Map-mode capture (listening for a control to map).
2. Notes, by the device's keys role: keys play the selected track's generator (or the track the device is pinned to by the header's MIDI From).
3. The selected track's page.
4. Global maps.
5. Nothing: the status says "K7 is free on Drums".

Knob moves are recorded as automation through `RecordControlChange`. A message allocates 0 bytes (events reach the UI through `UiEventQueue`; `ParameterCore` keeps its values in an array). A mapped knob reaches the parameter within the MIDI message.

## Selection

Selecting a track arms it exclusively (hand-armed tracks stay armed). The track selector "◀ track name ▶" with a muted chip ("2 devices"; which ones is its tooltip) sits at the right end of the view strip's row under the top bar: in the transport bar it wrapped Lock, Keys and the device picker onto a second row at 1440 px, and shifted every view under it at 1280 px, so the bar keeps the height it had; the "K7 is free" notice takes the chip's place for a few seconds; Alt+Up / Alt+Down (and two mappable commands) select the previous / next track.

## Studio

- **Map mode** (Edit, Map MIDI controls). The key is Ctrl+Alt+K (Alt+Cmd+K on a Mac), not the briefed Cmd+M: Cmd+M minimises on a Mac and mutes in the Arrangement: every mappable control outlined; a click arms it, the next control moved maps to it. Esc ends, Delete unmaps the armed control. Badges on mapped controls, in a short form that lies inside the control ("K3", "CC74", "N36"); the full source with the device ("Note 36 ch10 · Pads") is the badge's tooltip and the popover's text; right-click for Edit / Unmap. The "Listening…" chip is drawn inside the armed card, never over the group header above it. The banner is one line ("Listening for Lead · PEG rate 1: move any control · Delete: unmap · Esc: done").
- **MIDI popover**: scope, range (one row: from and to, in percent, with "20% to 60% of the parameter's range" below), invert, takeover, the listening state. Opened by map mode, a control's MIDI button (shown on hover where it is clear of the control's middle; the small transport buttons are mapped in map mode), a badge's Edit and the old "Bind to MIDI Controller" item.
- **Inspector MIDI page**: slots laid out as the knobs, drag a parameter onto a slot, right-click "Free this knob".

## Agent

`controller_list`, `midi_map_list`, `midi_map_set`, `midi_map_clear`, `track_page_get`, `track_page_set`: the same validation and undo as the UI (`docs/agent-coverage.md`).

## Limits of MM1

Mixer faders, sends and the Input group are not mappable yet; they come after the shared mixer (MX1) merges. Old Buzz bindings stay in the song's binding list and are shown as global any-device maps. Notes through the rest of the engine were not allocation-measured, only the mapped-knob path.

## MM2

A Controllers page, controller profiles, LED feedback, fader banks.
