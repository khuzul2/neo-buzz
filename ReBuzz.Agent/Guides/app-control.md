# App control

You can do what a person does in the app: change preferences, bind MIDI controllers, pick the audio device, move around the studio window, export files. Each has guardrails that make a mistake recoverable. Use them; the person may be listening or watching while you work.

## Where each tool works

| Area | Tools | Modes |
|---|---|---|
| Settings | `settings_get`, `settings_set`, `settings_reset`, `settings_history` | all |
| MIDI bindings | `midi_bindings_list`, `midi_bind`, `midi_unbind`, `midi_learn` | all (learn needs a MIDI input) |
| Audio output | `audio_status`, `audio_devices`, `audio_set` | studio and live (headless renders only: they say so) |
| The studio window | `view_show`, `machine_open_editor`, `select`, `panel_set`, `window_layout` | studio only: elsewhere they answer "Studio only" |
| Files | `export_audio`, `song_recent` | all |
| The app | `app_status` | all |

`studio_status` has the song and the audio output at a glance; `app_status` adds the version, CPU and memory.

## Settings

`settings_get(section="audio")` lists keys as `section.Name` with value, default, type, choices or range, what it does and `restart_required`. Sections: general, audio, midi, sync (Link and MIDI clock: `sync.Link`, `sync.LinkStartStop`, `sync.MidiClockIn`, `sync.MidiClockOut` as output names separated by "|"), appearance, agent, safety, advanced. Narrow with `search`, `key` or `changed_only=true`.

- `settings_set(key="general.PatternLength", value=32)` checks the value against the type and range, writes and saves it, and logs it. It says when a restart is needed (`restart_required`): tell the person, don't assume it took effect.
- Two classes need care, and the tools are annotated destructive so a client may ask first:
  - `class: "engine"` (section advanced): how audio is made. Threading, latency compensation and sub-tick timing change what a song sounds like or how much CPU it takes. Change one at a time, render before and after, and say what you changed.
  - `class: "safety"`: the safety limiter, its ceiling, the clip threshold and the machine guard. They guard the speakers and the machines. Don't loosen them to make a meter look better; a clipped render is a mix problem, not a limiter problem. The machine guard is off for the song's session only.
- `agent.AllowAgents` and `agent.Port` end this connection when they change. Leave them alone.
- The audio device, buffer and rate are read in `settings_get` but written with `audio_set`.
- Every change is logged: `settings_history(limit=10)` shows when, which key and the value it replaced. Put a value back with `settings_set(key=…, value=<the "from">)`.
- `settings_reset(key=…)`, `settings_reset(section="advanced")` or `settings_reset(all=true)` restores defaults. A section or all leaves the agent switch and port alone.

## Audio output

`audio_status` gives the device, buffer in frames, sample rate, latency (rendered sample to speaker), dropouts since start and the device's own clock. `audio_devices` lists output devices with the buffer sizes and rates `audio_set` takes.

Latency against buffer, at 48 kHz: 32 frames 0.7 ms, 64 1.3 ms, 128 2.7 ms, 256 5.3 ms, 512 10.7 ms, 1024 21 ms, 2048 43 ms (the device adds its own on top). Playing live wants 128 to 256; rendering and mixing don't care; a dropout costs more than a few ms.

`audio_set(buffer_frames=128)`, `audio_set(device="…")` and `sample_rate` (Hz, 0 is the device's own) open the output at once, so what the person hears changes: say so first. Auto-revert is built in: if no audio flows within 10 seconds, or the dropouts spike in the moments after, the previous settings come back and the result says why (`applied: false`, `reverted: true`, `reason`). Read it. After a revert the old settings are in use; don't retry the same values, try a larger buffer or another device. Buffer and rate are set by `audio_set` only for drivers that take them (`audio_devices` says); others have their own control panel.

Dropouts: take `audio_status` before and after a change, and compare `dropouts`. Zero new dropouts over a minute of playing is the bar.

## MIDI bindings

A binding makes a controller (channel 1 to 16, controller 0 to 127) drive a machine parameter; the song saves it. `midi_bindings_list(machine="Bass")` reads them.

- `midi_bind(machine="Bass", parameter="Cutoff", channel=1, controller=74)` binds, replacing what that parameter had on that track (it says what it replaced). `track` is for track parameters.
- `midi_unbind(machine="Bass", parameter="Cutoff")` removes one; `midi_unbind(machine="Bass", all=true)` removes the machine's.
- `midi_learn(machine="Bass", parameter="Cutoff", timeout_seconds=20)` waits for the next controller move and binds it. Tell the person to move the knob now, then call it. With no machine it only reports which controller moved. It times out with an error and changes nothing.
- Each bind and unbind is one undo step: `undo` takes it back.

Out of scope here: the Session View's MIDI mapping (notes and pads that launch clips) is made in the Session View itself.

## The studio window (studio mode)

The person sees every call, so treat it as a conversation: show them what you are about to talk about.

- `view_show(view="Session")` brings Arrangement, Session or Machines to the front. `view_show(machine="Bass", pattern="Verse", editor="piano_roll")` shows that pattern in the detail panel's piano roll (`editor="tracker"` for the Tracker).
- `select(machine="Bass", pattern="Verse")` selects as a click does: the inspector and the other views follow. `select(clear=true)` selects nothing.
- `panel_set(panel="inspector", open=true, tab="Song")`, `panel_set(panel="browser", open=false)`, `panel_set(panel="detail", maximised=true)`; `size` is the width in pixels (the detail panel's height).
- `machine_open_editor(machine="Bass", window="parameters")` opens a machine's window (`gui`, `parameters` or `editor`).
- `window_layout()` reads where everything is; with `width`, `height` or `maximised` it resizes the window.
- While the transport records, nothing opens a window or takes the keyboard: the pattern editor and machine windows are refused (`transport(action="stop")` first). Switching a view, selecting and panels still work.

## Export

`export_audio(path="C:/Music/demo.wav")` renders as `render` does (faster than real time, the song unchanged) and writes the files itself.

- Bit depth: `bits` 24 (default), 16 or 32 (float). 16 and 24 are dithered: `dither="tpdf"` (default), `off`, `light` or `psychoacoustic`. For a final 16 bit file, export from a 24 or 32 bit master, once, with dither.
- Range: the whole song, `range="loop"`, `from`/`to` in beats or "bar.beat", or `section="Chorus"` (a marker).
- Stems: `stems="generators"` writes each generator's own output as `<name>-<machine>.wav` beside the master (`stems="all"` every machine but Master; `machines=[…]` exactly these). Stems are taken after each machine's connection gain and before the master, so they don't sum to the master when effects or the master chain act.
- It never overwrites: if any target exists it fails before rendering and writes nothing. Pass `overwrite=true` only when asked or when you made the file earlier in this session.
- The result lists each file with its size and the master's loudness and peaks; check them with `analyze(source=…)` on a `render` id if you need more.

`song_recent(limit=5)` lists the songs opened lately (open one with `song_open`). Out of scope for now: MIDI file export, and the plugin scan (there is no plugin host yet).

## Checklist
- [ ] You know the app's state and mode before acting: `app_status`, and `audio_status` when sound matters.
- [ ] You read a setting before changing it, and told the person about a restart: `settings_get(key=…)`, then `settings_set`.
- [ ] Engine and safety settings were changed one at a time and rendered before and after: `render` then `analyze`.
- [ ] An audio change kept: `audio_set` returned `applied` true, and `audio_status` shows no new dropouts.
- [ ] A MIDI binding is listed and undoable: `midi_bindings_list(machine=…)`.
- [ ] An export did not overwrite anything you did not make: `export_audio` without `overwrite`, files checked in the result.
- [ ] Window changes were announced and recording was not interrupted: `window_layout`, `transport`.
