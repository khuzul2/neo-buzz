# MIDI mapping (MM1 and MM2 as built)

Any MIDI input is a controller; nothing in the engine is model-specific. A controller that has a **profile** (a data file) gets a default layout and lights; one that has none works through maps alone, exactly as in MM1. This is what MM1 and MM2 built.

## Model (`ReBuzz/Midi/Mapping`, `ReBuzz/Core/MidiMapping`)

- **Controller**: every MIDI input, named by its port name plus an index for duplicates ("Name #2"), remembered per computer. Any number work at once.
- **Source**: (device, kind CC / note / pitch bend / program change, channel, number). A map may name "any device"; old Buzz bindings become such maps.
- **Target**: a parameter (machine, parameter, track; `I:Name` for an Input-group parameter), a **mixer control** (volume, pan, send to a return, on, solo of a strip; the Master's fader) or a command.
  - Commands: play, stop, record, loop, previous / next track, select track n, arm, mute, solo; MM2: bank left / right by 1 or 8, scenes up / down, select / arm / stop clip of bank track n, stop all clips, launch scene row n, launch slot, knob mode pan / next send.
  - A mixer target names a strip (`track:Bass`, `group:Drums`, `return:Hall`) or **bank track n** (1 to 8): "track n of the current bank".
- **Drive**: range, invert, takeover (pickup / jump / scale); MM2: the target's raw min and max (map version 2). Absolute and relative encoders are told apart while learning (`EncoderDetector`). 14-bit and NRPN are out of scope.

## Scopes

- **Track page**: belongs to an MT1 track id and is saved in the song. A page has N slots, N being the device's knob-group size (default 8); a track may have several pages. A knob group is learned once per device; its knobs drive slots 1..N of the selected track's current page. An empty slot falls back to the generator's Inspector pin of the same number (shown with a grey ring).
- **Global**: per computer, with an optional per-song copy.
- Mixer targets are saved in the song like any other (the song's page or global map), and a rename of a machine follows them.

## Map version 2

The song's map file is version 2. A map stores the **raw range** (`rawmin`, `rawmax`) the target had when the map was saved; the router drives the parameter inside that range, so a parameter whose enum later grows values at its end is still reached at the same raw values. A version 1 map has no raw range and loads as before (the whole range of the parameter). A file saved by a newer version is not read, and says so.

## Banks

The bank is a window of 8 tracks of the track model (`BankOffset`), and a window of 5 scenes (`SceneOffset`; a song has at least 8 scenes). `bank left / right` move it by 1 or 8 (it stays on the song's tracks), `scenes up / down` by 1. A move rebuilds the router's snapshot, so every fader and knob of the bank **takes over again** (pickup waits until the control meets the value of the new track), and all lights are sent again. The window is outlined in the Mixer (the 8 strips) and in the Session View (the 8 x 5 box).

## Router order (MIDI thread, from a snapshot the UI rebuilds)

1. Map-mode capture (listening for a control to map).
2. The selected track's page: the device's knobs (slot i is knob i), then the page's direct entries.
3. Global maps.
4. **MM2: the device's profile** (after the person's own maps, so a control they mapped keeps what they gave it).
5. Notes, by the device's keys role: keys play the selected track's generator (or the track the device is pinned to by the header's MIDI From).
6. Nothing: the status says "K7 is free on Drums".

Knob moves are recorded as automation through `RecordControlChange`. A message allocates 0 bytes, including the mixer, bank and profile paths (events reach the UI through `UiEventQueue`; `ParameterCore` keeps its values in an array; a connection's Amp and Pan reach the Input-group parameter without the facade copy). A mapped knob reaches the parameter within the MIDI message. A mixer fader moves the wire's gain on the MIDI thread; mute, solo and a send's first wire are made by the UI thread (one undo step, as the mixer's own).

## Controllers (MM2)

Preferences ▸ Controllers has one card per MIDI input:

| Setting | Meaning |
|---|---|
| Use | the input is opened (the MIDI page's own flag) |
| Role | Keys (its notes play the selected track), Controls (its controls are mapped, its notes do not play) or Both; unset: the profile's |
| Profile | Auto (the profile whose port-name pattern matches), None, or a profile |
| Output for lights | the MIDI output that gets its lights; unset: the output of the same name |
| Knobs | how many knobs its group has (0: all) |
| Default takeover | what a new map of this device gets, and what its profile's controls use |
| Last | the last message it sent, live |

The settings belong to the computer (`ComputerMap.Devices`); each change is one undo step of the song's history. The page is keyboard navigable (Tab between controls, Space or Alt+Down opens a list) and changes its controls in place, so the focus stays where it is. The agent has the same: `controller_list` shows each controller's profile, role, output and last message; `controller_profile_set` sets them with the same checks and one undo step.

## Profiles

A profile is a JSON file: `ReBuzz/Midi/Profiles/*.json` (built in, embedded) and any `*.json` in a `Profiles` folder beside the app. A file with a problem (a clash between controls, a bad system exclusive message) records it (`Problems`), is never matched by Auto and is not offered on the Controllers page.

```
{ "id", "name", "match": ["port-name patterns"], "role", "knobs", "takeover", "sources": [...],
  "enter": ["F0 ... F7"], "exit": ["F0 ... F7"],
  "controls": [ { "id", "action", "kind", "channel", "number", "count", "channelStep", "numberStep", "index", "indexStep", "anyChannel",
                  "led": { "on", "off", "typeNumber", "typeValue", "typePanValue" } } ],
  "grid": { "cols", "rows", "kind", "channel", "first", "rowStride", "bottomRowFirst", "states": { "empty": { "velocity", "channel", "base" }, ... } } }
```

Actions: `volume n`, `master`, `knob n` (a slot of the selected track's page; a taught group wins), `trackknob n` (pan, or the send to return A, B or C, as its buttons chose), `select n`, `mute n`, `solo n`, `arm n`, `clipstop n`, `stopall`, `scene n`, `play`, `stop`, `record`, `sceneup`, `scenedown`, `bankleft`, `bankright`, `bankleft8`, `bankright8`, `knobmode.pan`, `knobmode.send`; and the grid's pads (a press launches the clip of the bank's column and the window's row; on an empty slot of an armed track it records a take, as the Session View's own controllers do). "n" is the bank track, 1 to 8.

**The clip-grid controller (8 x 5)** (`clip-grid-8x5.json`; Auto matches "APC40 mkII"): 8 track faders to the bank's volumes, the master fader to the Master; 8 device knobs with rings to the selected track's page slots; 8 track knobs with rings to pan or send A, B, C (the Pan and Sends buttons; the Sends button steps A, B, C); the 5 x 8 grid to the clip slots of the bank window; scene launch, clip stop and stop all; track select, mute, solo and arm; play, stop and record; bank arrows (the four arrows move the bank by 1 and the scenes by 1, the bank buttons by 8). On start it sends the maker's mode-switch message (generic mode 0x41, "host drives LEDs") and on end the one that gives the device its own mode back (0x40).

**The 25-key keyboard (knobs and pads)** (`keys-knobs-pads-25.json`; Auto matches "MPK225"): the keys and the pads play notes (role Both, so its notes follow the selected track); stop, play and record are CC 117, 118 and 119 on any channel; the 8 knobs are a knob group, taught once.

### Where the numbers come from

Only the makers' published documents (no code or tables of another product):

- Clip-grid controller: *APC40 mkII Communications Protocol, version 1.2* (`cdn.inmusicbrands.com/akai/attachments/apc40II/APC40Mk2_Communications_Protocol_v1.2.pdf`): the introduction message and its modes (generic 0x40, Ableton Live 0x41 as the mode in which the host sets the LEDs), the note numbers of the pads (0x00 to 0x27, bottom row first) and buttons (clip stop 0x34, track select 0x33, mute 0x32, solo 0x31, record arm 0x30, scene launch 0x52 to 0x56, stop all 0x51, pan 0x57, sends 0x58, play 0x5B, stop 0x5C, record 0x5D, arrows 0x5E to 0x61, bank 0x3C / 0x3D), the controller numbers of faders (7, master 14), device knobs (0x10 to 0x17, ring types 0x18 to 0x1F) and track knobs (0x30 to 0x37, ring types 0x38 to 0x3F), the ring types (0 off, 1 single, 2 volume, 3 pan) and the pad colour behaviour (velocity is the colour, the channel is steady 0, one-shot 1 to 5, pulsing 6 to 10, blinking 11 to 15).
- Keyboard: *MPD and MPK series transport controls MIDI details* (`cdn.inmusicbrands.com/akai/mpk88/mpd_and_mpk_series___transport_controls_midi_details.pdf`): the transport buttons' controller numbers in the CC mode (rewind 115, forward 116, stop 117, play 118, record 119).

The profile names and every text a person reads say what the controller is (a clip-grid controller, a keyboard), never a brand or a model; the port name the OS reports is shown as the device's name and is the pattern Auto matches.

## Feedback

Lights are sent from the UI thread's timer (every `FeedbackPeriodMs` = 10 ms), never from the audio or the MIDI thread, through `IFeedbackOutput` (the engine's MIDI outputs; a fake in the tests). Every light has what it shows now and what was sent last:

- **Rings** follow the value of what their knob drives (the mapped parameter, the pan, the send), whatever changed it: the knob, automation, the agent, undo, the editors' own knobs (the value is read every tick, so every source is covered).
- **Buttons** show the state: select (the selected track), mute, solo, arm, clip stop (the track plays a clip), play, record, the knob mode.
- **Pads** show the clip: empty, armed-empty, stopped (the clip's colour, the nearest of the controller's palette), queued (blink), playing (pulse), stopping, recording and waiting to record, with the colour the profile gives each; a pad that pulses or blinks first gets its base colour.
- Only changes are sent, **coalesced** (a sweep is one message per light per tick, the newest value), at most **100 messages per second per device** (a window of the last 100 sends), and the lights not yet sent go first next tick. A bank move, a controller coming back, or a new output sends every light again. A tick with nothing changed allocates 0 bytes.
- A controller that the Session View's own script runs is left alone (no profile routes, no lights).

## Selection

Selecting a track arms it exclusively (hand-armed tracks stay armed), except a pick in the Session View: there an armed track's empty slots turn into record buttons (they lose their stop button), so the keys and knobs follow the pick and nothing is armed for it. The track selector "◀ track name ▶" with a muted chip ("2 devices"; which ones is its tooltip) sits at the right end of the view strip's row under the top bar; the "K7 is free" notice takes the chip's place for a few seconds; Alt+Up / Alt+Down (and two mappable commands) select the previous / next track.

## Studio

- **Map mode** (Edit, Map MIDI controls). The key is Ctrl+Alt+K (Alt+Cmd+K on a Mac), not the briefed Cmd+M: Cmd+M minimises on a Mac and mutes in the Arrangement. Every mappable control is outlined, MM2: the mixer's faders, pans, sends, On and Solo, the Master's fader and the Input group's parameters too. A click arms it, the next control moved maps to it. Esc ends, Delete unmaps the armed control. Badges on mapped controls, in a short form that lies inside the control ("K3", "CC74", "N36"); the full source with the device is the badge's tooltip and the popover's text.
- **MIDI popover**: scope, range, invert, takeover, the listening state.
- **Inspector MIDI page**: slots laid out as the knobs, drag a parameter onto a slot, right-click "Free this knob"; the knob groups ("Teach knobs") and keys roles.
- **Bank outline**: the Mixer outlines the bank's 8 strips; the Session View outlines the bank window while a bank is in use.

## Agent

`controller_list`, `controller_profile_set`, `midi_map_list`, `midi_map_set` (mixer, strip, bank, send, the bank commands), `midi_map_clear`, `track_page_get`, `track_page_set`: the same validation and undo as the UI (`docs/agent-coverage.md`).

## Gates (unit tests)

`MidiMapping2Tests` (phase 1), `MidiMapping3Tests` (phase 2) and `ProfileTests` / `MapModelTests` (ReBuzz.SessionTests):

| Gate | Test |
|---|---|
| Fader banks: a bank move by 8 maps the faders onto tracks 9 to 16; pickup holds until the knob crosses | `MovingTheBankByEight...` |
| Map v2: a grown enum keeps its raw targets; a v1 map loads as before | `MapModelTests`, `MidiMapping2Tests` |
| Mixer targets saved in the song, reopened and undone | `MixerInputAndSendMapsSave...` |
| Auto-match; None and a chosen profile override it | `EachProfileMatchesItsPortName...`, `ProfileTests` |
| Feedback latency under 50 ms | `AnOutsideChangeOfAMappedValueReachesTheRingWithin50Ms` |
| Rate limit: a 1 kHz sweep sends at most 100 messages per second; unchanged values send nothing | `ARingSweepSendsAtMost100Messages...` |
| Grid: every clip state sends its LED message; a bank move sends the window again | `EveryClipStateOfTheGrid...`, `TheButtonsShowMute...` |
| 0 bytes per message in the router (profile and mixer paths) and in the steady-state tick | `TheFeedbackTickAllocatesNothing...`, `TheMixerPathsOfTheRouterAllocateNothing...` |
| Agent: profile in `controller_list`; `controller_profile_set` validates and undoes | `TheAgentSeesAControllersProfile...` |

## How to test on the real devices

1. Plug the controller in; Preferences ▸ MIDI: check its input (and, for lights, its output). Preferences ▸ Controllers: its card says "open", its Profile is Auto, and its Last box moves with every control you touch.
2. **Clip-grid controller.** Open a song with 8 or more tracks and some clips in the Session View. The pads light with the clips' colours as soon as the profile starts (the device switches to the mode in which the host drives the lights). Press a pad: the clip is queued (blinking) and then plays (pulsing). Move the faders: the Mixer's bank strips (outlined) follow, and the pads and faders of tracks 9 to 16 after the bank buttons (bank right by 8). Pick up: after a bank move a fader does nothing until it meets the track's value. Turn a track knob: pan; press Sends: the knob is the send to the first return and the ring shows it; press Pan to go back. Mute, Solo, Arm and Select light with the state. Change a value in the app (an automation, the mouse, the agent): the ring follows within a tenth of a second.
3. **Keyboard.** Select a track: the keys and pads play it. Set its transport buttons to send controller messages (CC mode) and press play, stop and record. Teach the knobs once (Inspector ▸ MIDI ▸ Teach knobs) and they drive the page of the selected track.
4. If a device is wrong: Preferences ▸ Controllers ▸ Profile ▸ None gives MM1's behaviour (maps only); a profile of your own goes in a `Profiles` folder beside the app.

## Limits

- A profile's rings need the controller's output (its paired output must exist); with none, the profile's controls work and nothing lights.
- The keyboard's knobs have no default numbers: the maker's factory presets could not be read in a published document, so they are taught (a knob group) rather than guessed. Its transport buttons are the CC mode's numbers; a keyboard left in its MMC mode sends system exclusive messages, which the MIDI backends do not deliver.
- Old Buzz bindings stay in the song's binding list and are shown as global any-device maps.
