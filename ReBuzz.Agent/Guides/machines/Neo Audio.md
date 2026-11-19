## How it sounds

The audio track of the Session View and the arrangement: it plays audio clips (a WAV, AIFF, FLAC, OGG or MP3 file kept inside the song), warped to the song's tempo, and records takes from its input or from the master. It is silent until a clip plays. Two parameters: what you hear of the input, and what a take records.

- A clip warps in one of five modes: Off (its own speed), Repitch (a turntable: tempo and pitch move together), Beats (slices on the transients, for drums and loops), Complex (a phase vocoder, for anything) and Pro (the best quality stretch and pitch shift, for anything, at more CPU). Transpose is -48 to +48 semitones, detune is in cents, gain in dB.
- The tempo of an imported file is found when it is imported (a loop is taken as whole bars, other audio by its transients). A file with no tempo to find plays at its own speed (warp Off) until one is set: `audio_clip_set(..., tempo_bpm=96)` or `detect_tempo=true` turns warp to Beats. A clip follows the song's tempo unless its warp is Off.

## Use it for

- Audio loops and one-shots launched as clips next to pattern clips, or placed in the arrangement.
- Recording a take from a connected machine or, by resampling, from the master.

## Key controls

- `Monitor` (auto): hear the input only while the track is armed; `in` always, `off` never.
- `Input` (in): `in` records what is connected to the track; `master` records the song's output (resampling).
- Each clip's warp (Beats for drums and loops): Off, Repitch, Beats, Complex or Pro; set it with `audio_clip_set`.
- Each clip's tempo (found at import): the tempo of the audio, which sets the clip's length in beats; `detect_tempo` finds it again.
- Each clip's transpose (-48 to +48 semitones) and detune (cents): pitch without changing speed in Beats, Complex and Pro.
- Each clip's gain (0 dB): -70 to +24 dB.
- Each clip's loop and region: where it starts and what part repeats.
- In the arrangement (NA2): every audio clip has a pattern of its own, with its own settings (copies share the audio only). Fade in and out, gain, reverse and a crossfade are set per clip: `audio_clip_set(machine="Drums audio", pattern="loop", fade_in_beats=1, fade_out_beats=2, reverse=true)`, or on the clip: `arrangement_clip(op="fade", ...)`, `op="gain"`, `op="crossfade"`, `op="reverse"`.

## Starting points

- Import a file: `audio_import(path="/music/loop.wav", track="Drums audio", slot=1)`; leave `track` out to get a new audio track named after the file.
- Change it: `audio_clip_set(machine="Drums audio", pattern="loop", warp="Complex", transpose=-2, gain_db=-3)`; `audio_clip_get` reads it back.
- Split a clip (or a file) into stems on new tracks: `audio_separate(machine="Drums audio", pattern="loop", quality="Fast", stems=["drums", "bass"])` (or `path="/music/song.flac"`). Each stem gets a Neo Audio track named after it, with a clip in the same scene and at the same arrangement positions, one undo step. It needs a stem model installed (Gear/Models/Stems or Preferences > Stems; Install models takes the "NeoBuzz Live Stem Models" zip) and takes minutes for a song, longer for Balanced (four times Fast) and Best; a repeat on the same audio and quality is instant.
- Turn a clip into MIDI on a new track: `audio_convert_to_midi(machine="Bass audio", pattern="line", mode="melody", quantize="1/16", strength=0.8)`. `mode` is `melody` (one note at a time: a voice, a lead, a bass line; `range` any, voice, guitar or bass; `pitch_bends=true` keeps a slide or vibrato as one note with bend events), `harmony` (chords and piano: several notes at once; `onset_threshold` and `frame_threshold`, lower finds more) or `drums` (kick, snare, closed and open hat, toms, cymbals as General MIDI notes; `drum_threshold`, lower finds more). The new track has Neo Poly (melody, harmony) or Neo Drums, and a clip of the same length in the same scene and at the same arrangement positions; one undo step; it takes seconds. `apply=false` lists the notes without changing anything: check a conversion before keeping it. The clip's transpose is added to the pitches; its warp markers place the notes. Melody and drums are strongest on a clean single line and on kick, snare and closed hat; open hats, toms and cymbals are weaker.
- Best quality for vocals, pads and full mixes: `audio_clip_set(machine="Vocals audio", pattern="verse", warp="pro", tempo_bpm=96)`; `audio_clip_get` reports `pro`, and `warp_playing` too when the clip is playing as Complex because the native library is missing.
- Launch it: `session_launch(scene=0, machine="Drums audio")`, or place it with `audio_import(..., at_beats=0)`.
- Edit it in the arrangement: `arrangement_clip(op="trim", machine="Drums audio", at=1, end=7)` (the audio stays in time), `op="stretch", end=9` (warps the audio to fill the clip up to beat 9), `op="split", split_at=4`, `op="consolidate"` (renders the clip, with its warp, fades and gain, into a new audio clip), `op="copy"` or `op="move"` with `to_machine="Bass audio"` for another audio track. A clip moved or trimmed over another audio clip crossfades with it.
- Stretch to a length: `audio_clip_set(machine="Drums audio", pattern="loop", stretch_to_beats=8)` (the clip must be in the arrangement, warp not Off, loop off).
- Record a take: `session_record(action="start", machine="Drums audio", bars=4)`.
- Choose what a track listens to (NA4a): `track_input(action="get")` lists the audio input device's channels and the tracks; `track_input(action="set", machine="Vocals audio", audio_from="in:1", monitor="in")`. audio_from is `none`, `in:3` (one input, in both sides), `in:1-2` (a pair), `track:Name` (another track's output after its mixer, to resample it) or `wired` (what is connected to the machine, as old songs have it). A new track starts on `in:1-2` when an input device is open, else `none`. monitor is `in`, `auto` (while armed) or `off`.

## Pitfalls

- Silent with no clip playing (the profile's health warn); that is not a fault.
- The audio belongs to the song, not to the wavetable: an old song's wave-based clips are moved into the song on load, and the wave stays as it was. Saving as .bmx writes the audio into free waves; what does not fit is listed in the export report.
- Input master records everything the song plays, including this track: mute it first to avoid doubling.
- Beats mode on sustained material smears; use Complex or Pro for pads and voices, Repitch for a deliberate speed change.
- Pro costs more CPU than Complex (see the CPU figures in docs/specs/neo-audio.md section 11) and softens the attack of drums a little, which Beats keeps. It is lined up with the other modes (a Pro clip's hits land where a Repitch clip's do). Where its native library is missing a Pro clip plays as Complex.
- Outside the app's own Session engine it only passes its input through.

## Pairs well with

- ReBuzz Audio In or any instrument connected to it for takes; the Session View's mixer for levels and sends.

## Successor

None needed: it is the Session Clip Player renamed. A song that names Session Clip Player loads it as Neo Audio, and Classic keeps playing the old name.

## Checklist

- [ ] `audio_convert_to_midi(machine="Drums audio", pattern="loop", mode="drums", apply=false)`: a list of notes (36 kick, 38 snare, 42 hat), nothing changed; with `apply` left out, a new Neo Drums track and a MIDI clip.
- [ ] `audio_separate(machine="Drums audio", pattern="loop", quality="Fast")`: four new audio tracks (or the error that says which model to install).
- [ ] `machine_describe(machine="Drums audio")`: its parameters are `Monitor` and `Input`.
- [ ] `audio_clip_get(machine="Drums audio", pattern="loop")`: the warp, tempo and audio are listed.
- [ ] `meters_read(machines=["Drums audio"])`: a launched clip shows a level.
- Take and comp (NA4b): an armed Neo Audio track records into the Arrangement with `arrangement_takes(op="arm", machine=…)` then `op="start"` (`loop=true`: every pass of the song loop is a take on a take lane, the newest also plays); then `takes(op="list", machine=…)`, `takes(op="comp", machine=…, ranges=[{take, from, to}])`, `takes(op="choose", machine=…, take=2)`, `takes(op="flatten", machine=…)` (renders the comp into one clip) and `takes(op="delete_unused", machine=…)`. A Session take is `session_record`. Recordings are unwarped with the tempo they were made at (`audio_clip_set` `warp` turns it on).
