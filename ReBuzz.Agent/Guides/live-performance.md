# Live performance

Playing the song while the person listens: the Session View's scenes and clips launched in time, a phrase played from the keyboard, knobs on a controller, and the levels watched. The live tools (`transport`, `session_launch`, `session_record`, `midi_play`, `meters_read`) work in studio and live mode; headless, they say so and you render instead. Check `studio_status` for the mode first.

A performance is a set of choices made before the audience hears it. Build the grid, rehearse it with the person, then play it. Nothing here is an undo step except changes to the grid and to machines.

## The grid

The Session View is one track per machine, one scene (row) per section, and a pattern in each slot. Scenes count from 1.

- `session_get` reads it: tracks, slots, scene names and tempos, and the clips' launch settings when they are not the defaults.
- `session_scene_set(scene=3, name="Drop", tempo_bpm=128)` names a scene and can give it a tempo (0: none) and beats per bar (0: none). `insert=true` puts a new empty scene before it.
- `session_clip_set(machine="Bass", scene=2, pattern="Chorus", quantization="Bar")` puts a pattern in a slot; `pattern=null` empties it. A machine with no track gets one. One undo step.
- Launch settings on a clip: `mode` Trigger (plays on launch), Gate (plays while held), Toggle (launch starts it, launch again stops it) or Repeat; `quantization` Global (the session's), None, Beat, Bar, TwoBars, FourBars, EightBars, HalfNote, Eighth or Sixteenth; `loop` (default on); `legato` (start where the previous clip of the track was, so a variation continues the phrase instead of restarting it).

Build every scene as a whole: the same patterns, lengths in whole bars, and a level you checked with `render` (the arrangement and the grid use the same patterns, so `arrangement_place` plus `render(section=…)` is the offline check of what a scene will sound like).

## Playing it

- `session_launch(scene=2)` launches a row on the next bar (or each clip's own quantization). `session_launch(machine="Bass", scene=2)` one slot; `session_launch(machine="Bass", pattern="Fill")` any pattern of that machine, in the grid or not. It starts the transport if it is stopped.
- `session_launch(stop=true)` stops everything at the quantization; `immediate=true` stops now. With `machine`, only that track. `back_to_arrangement=true` hands every track back to the arrangement.
- `transport(action="status")` says what plays now and what waits for its boundary; `transport(action="play", at="9.1")` and `stop` move the whole song.
- Tempo follows the scene that carries one. A scene with no tempo keeps the current one.
- Launches wait for their boundary. Read the answer: it says what plays and what waits, so say "the drop lands on the next bar" and watch it land before the next move.

## Playing notes and recording

- `midi_play(machine="Keys", notes=[{"pitch": "C4", "start": 0, "length": 1}])` plays a phrase on the next bar (`start` can be "now" or "beat"); `stop=true` lets go of everything.
- `session_record(action="start", machine="Keys", scene=1, bars=4)` records what is played (the person's keyboard, or `midi_play`) into the first empty slot from that scene. `stop` ends the take on a whole bar; `capture` keeps what armed tracks just played; `arm`, `disarm` and `status` do what they say. Takes are on the Session View's own history, not `undo`'s.
- A Note envelope on a modulator hears notes written by patterns and by `midi_play`, not notes played from a MIDI keyboard.

## Controllers and knobs

- `midi_learn(machine="Bass", parameter="F1 cutoff", timeout_seconds=20)`: tell the person to move the knob, then call it. `midi_bind(machine=…, parameter=…, channel=1, controller=74)` when you know the numbers. `midi_bindings_list` shows them, `midi_unbind` removes one. The binding is saved with the song and each change is one undo step. Pad and note mapping that launches clips is made in the Session View itself.
- One knob for a whole move: Neo Macro (`machine_add(gear="Neo Macro", name="Build", connect_to="none")`), point it at up to 8 parameters with `modulate(source="Build", target_machine="Lead", param="F1 cutoff", depth=60)`, and bind a controller to its `Value`. Its `Write glide` keeps a jump of the knob from clicking.
- Movement that plays itself: Neo LFO (cyclic), Neo Step (a 1 to 32 step pattern) and Neo Envelope (opens on each note). The parameter keeps the value you set; the modulator adds on top. `modulation_list` says where everything reaches.
- Neo Patch has sixteen macros of its own for the sounds built there (`patch_macro_map`).

## Changing the sound while it plays

- `param_set` moves a parameter at once: move it in steps, not from one end to the other, and keep the ranges the tools give you (the safe ranges stay in force).
- `snapshot_save(name="set-A")` before the show and after each section you may want back. `snapshot_restore(name="set-A")` while the transport plays is glitch-free: parameters glide, and patterns, tracks, mutes and wire levels change together at the next boundary (`quantize` now, beat or bar; default bar). The result's `mode` says `live` (with `waiting` and `applies_in_ms`) or `deferred` (applied when the transport stops: read the `reason`). The Session View's clip grid is not part of it.
- `machine_set(machine="Pad", mute=true)` and `bypass` are the quick cuts; `connect` gains with `connection_set` are the quick fades.
- Do not use `render`, `analyze_mix` or `export_audio` while the person is listening: a render pauses their output for its length. Do not call `audio_set`, `settings_set` on engine keys, `song_open` or `song_new` mid-show: each one interrupts or replaces what plays.

## Watching the levels

`meters_read(machines=["Bass", "Drums"], window_seconds=2)` listens as the audience does and reports loudness now, short-term and integrated, the loudest moment, true peak and clipped samples for the master and the machines. Its `safety` part tells whether the master's clip indicator latched an over and whether the safety limiter acted; `clear_clip=true` clears the indicator first. A Neo Analyzer in the graph is read with `machine_readings`. The safety limiter is not a mix tool: if it acts, lower a level.

`audio_status` before and after a change shows new `dropouts`; zero over a minute is the bar (`guide(topic="performance")` when it is not).

## Rehearsal, then the set

1. `studio_status` (mode, tempo), `session_get` (the grid), `snapshot_save(name="start of set")`.
2. Launch each scene once with the person listening; fix what is too loud or too long.
3. Agree the order and the moves with them: which scenes, which controller does what.
4. Play it: one move, read the answer, next move. Say out loud what you are about to do.
5. Afterwards `session_launch(back_to_arrangement=true)` and `song_save` if the grid should stay.

## Checklist

- [ ] The grid is what you think it is: `session_get` shows each scene's patterns and names.
- [ ] Every scene was rehearsed and its level measured: `meters_read(machines=[…], window_seconds=2)` shows true peak below -1 dBTP and no clipped samples.
- [ ] A way back exists: `snapshot_save(name=…)` before the set and `snapshot_list` shows it.
- [ ] No new dropouts while playing: `audio_status` before and after, `dropouts` unchanged.
- [ ] Controllers are bound and read back: `midi_bindings_list(machine=…)`.
