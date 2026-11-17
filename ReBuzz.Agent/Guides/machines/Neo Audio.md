## How it sounds

The audio track of the Session View and the arrangement: it plays audio clips (a WAV, AIFF, FLAC, OGG or MP3 file kept inside the song), warped to the song's tempo, and records takes from its input or from the master. It is silent until a clip plays. Two parameters: what you hear of the input, and what a take records.

- A clip warps in one of four modes: Off (its own speed), Repitch (a turntable: tempo and pitch move together), Beats (slices on the transients, for drums and loops) and Complex (a phase vocoder, for anything). Transpose is -48 to +48 semitones, detune is in cents, gain in dB.
- The tempo of an imported file is found when it is imported (a loop is taken as whole bars, other audio by its transients). A file with no tempo to find plays at its own speed (warp Off) until one is set: `audio_clip_set(..., tempo_bpm=96)` or `detect_tempo=true` turns warp to Beats. A clip follows the song's tempo unless its warp is Off.

## Use it for

- Audio loops and one-shots launched as clips next to pattern clips, or placed in the arrangement.
- Recording a take from a connected machine or, by resampling, from the master.

## Key controls

- `Monitor` (auto): hear the input only while the track is armed; `in` always, `off` never.
- `Input` (in): `in` records what is connected to the track; `master` records the song's output (resampling).
- Each clip's warp (Beats for drums and loops): Off, Repitch, Beats or Complex; set it with `audio_clip_set`.
- Each clip's tempo (found at import): the tempo of the audio, which sets the clip's length in beats; `detect_tempo` finds it again.
- Each clip's transpose (-48 to +48 semitones) and detune (cents): pitch without changing speed in Beats and Complex.
- Each clip's gain (0 dB): -70 to +24 dB.
- Each clip's loop and region: where it starts and what part repeats.
- In the arrangement (NA2): every audio clip has a pattern of its own, with its own settings (copies share the audio only). Fade in and out, gain, reverse and a crossfade are set per clip: `audio_clip_set(machine="Drums audio", pattern="loop", fade_in_beats=1, fade_out_beats=2, reverse=true)`, or on the clip: `arrangement_clip(op="fade", ...)`, `op="gain"`, `op="crossfade"`, `op="reverse"`.

## Starting points

- Import a file: `audio_import(path="/music/loop.wav", track="Drums audio", slot=1)`; leave `track` out to get a new audio track named after the file.
- Change it: `audio_clip_set(machine="Drums audio", pattern="loop", warp="Complex", transpose=-2, gain_db=-3)`; `audio_clip_get` reads it back.
- Split a clip (or a file) into stems on new tracks: `audio_separate(machine="Drums audio", pattern="loop", quality="Fast", stems=["drums", "bass"])` (or `path="/music/song.flac"`). Each stem gets a Neo Audio track named after it, with a clip in the same scene and at the same arrangement positions, one undo step. It needs a stem model installed (Gear/Models/Stems or Preferences > Stems; Install models takes the "NeoBuzz Live Stem Models" zip) and takes minutes for a song, longer for Balanced (four times Fast) and Best; a repeat on the same audio and quality is instant.
- Launch it: `session_launch(scene=0, machine="Drums audio")`, or place it with `audio_import(..., at_beats=0)`.
- Edit it in the arrangement: `arrangement_clip(op="trim", machine="Drums audio", at=1, end=7)` (the audio stays in time), `op="stretch", end=9` (warps the audio to fill the clip up to beat 9), `op="split", split_at=4`, `op="consolidate"` (renders the clip, with its warp, fades and gain, into a new audio clip), `op="copy"` or `op="move"` with `to_machine="Bass audio"` for another audio track. A clip moved or trimmed over another audio clip crossfades with it.
- Stretch to a length: `audio_clip_set(machine="Drums audio", pattern="loop", stretch_to_beats=8)` (the clip must be in the arrangement, warp not Off, loop off).
- Record a take: `session_record(action="start", machine="Drums audio", bars=4)`.

## Pitfalls

- Silent with no clip playing (the profile's health warn); that is not a fault.
- The audio belongs to the song, not to the wavetable: an old song's wave-based clips are moved into the song on load, and the wave stays as it was. Saving as .bmx writes the audio into free waves; what does not fit is listed in the export report.
- Input master records everything the song plays, including this track: mute it first to avoid doubling.
- Beats mode on sustained material smears; use Complex for pads and voices, Repitch for a deliberate speed change.
- Outside the app's own Session engine it only passes its input through.

## Pairs well with

- ReBuzz Audio In or any instrument connected to it for takes; the Session View's mixer for levels and sends.

## Successor

None needed: it is the Session Clip Player renamed. A song that names Session Clip Player loads it as Neo Audio, and Classic keeps playing the old name.

## Checklist

- [ ] `audio_separate(machine="Drums audio", pattern="loop", quality="Fast")`: four new audio tracks (or the error that says which model to install).
- [ ] `machine_describe(machine="Drums audio")`: its parameters are `Monitor` and `Input`.
- [ ] `audio_clip_get(machine="Drums audio", pattern="loop")`: the warp, tempo and audio are listed.
- [ ] `meters_read(machines=["Drums audio"])`: a launched clip shows a level.
