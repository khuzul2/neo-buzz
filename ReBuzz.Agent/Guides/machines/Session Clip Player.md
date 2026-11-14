## How it sounds

An audio track for the Session View: it plays the audio clips its track launches, warped to the song's tempo, and records takes from its input or from the master. It is silent until a clip is launched. Two parameters: what you hear of the input, and what a take records.

## Use it for

- Audio loops launched as clips next to pattern clips in the Session View.
- Recording a take from a connected machine or, by resampling, from the master.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Monitor | auto | hear the input only while the track is armed; in always, off never |
| Take source | in | in records what is connected to it; master records the song's output |

## Starting points

- Make the track in the Session View with + Track, then drop an audio file on a slot; `session_get` lists the grid.
- Launch a clip: `session_launch(scene=0, machine="Clips")`.
- Record a take: `session_record(action="start", machine="Clips", bars=4)`.

## Pitfalls

- Silent with no clip launched (the profile's health warn); that is not a fault.
- Take source master records everything the song plays, including this track: mute it first to avoid doubling.
- Outside the app's own Session engine it only passes its input through.

## Pairs well with

- ReBuzz Audio In or any instrument connected to it for takes.

## Successor

None: no Neo machine replaces it, so `machine_replace` does not apply; keep it in songs that use it.

## Checklist

- [ ] `machine_describe(machine="Clips")`: its parameters are `Monitor` and `Take source`.
- [ ] `session_get`: the track's clips are listed.
- [ ] `meters_read(machines=["Clips"])`: a launched clip shows a level.
