## How it sounds

An envelope with no audio of its own that moves up to 8 parameters of other machines. In Note mode it opens with each note of a machine you choose: a filter that snaps open and falls, a volume swell, a pitch drop. In Follower mode it follows the loudness of a machine's output, so one machine ducks or pumps another.

- `Mode` Note: an ADSR (`Attack`, `Decay`, `Sustain`, `Release`) started by the notes of the machine in `listen_machine` (and `listen_track`), or by the `Gate` switch; `Curve` bends its segments.
- `Mode` Follower: the level of the listened machine's output, above `Threshold`, rising at `Attack` and falling at `Release`.
- Per target: depth −100 … +100 % (negative depth ducks), polarity (uni by default), an optional range and curve; `Write glide` limits how fast a write moves.

## Use it for

- A pluck filter on every note, a pumping pad that ducks under the kick (Follower, negative depth), a swell, a gated tremolo.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Mode | Note | Follower follows a machine's level |
| Attack | 10 ms | rise time; in Follower how fast it follows a louder signal |
| Decay | 500 ms | fall to Sustain (Note) |
| Sustain | 70 % | the level held while the note is held (Note) |
| Release | 500 ms | fall after the note ends; in Follower how slowly it lets go |
| Threshold | 0 dB | Follower only: the level it counts from; lower it for quiet sources |
| Curve | 0 % | bends the segments |
| Gate | Off | writes a note by hand when nothing is listened to |

## Starting points

- A pluck: `modulate(source="Env", target_machine="Lead", param=<the cutoff>, depth=60, listen_machine="Lead")`, Attack 1 ms, Decay 300 ms, Sustain 0 %.
- Ducking: Mode Follower, `listen_machine="Kick"`, depth −60 % on the pad's volume parameter, Attack 5 ms, Release 200 ms.

## Pitfalls

- In Note mode with nothing listened to, only `Gate` starts it; `modulate` says so.
- Notes played from a MIDI keyboard are not seen, only those a pattern or `midi_play` send; note events arrive a few milliseconds late.
- The follower measures the output of the listened machine's connections, one block late, and only while a follower runs; a machine with no output has no level.

## Pairs well with

- Neo Poly, Neo Filter and Neo Comp as targets; Neo LFO for cyclic movement alongside.

## Successor

Nothing replaces it: Pedal LFO's free-running wave is Neo LFO. Pedal Peer Control's per-track values are Neo Macro.

## Checklist

- [ ] `modulation_list(machine="Env")` shows each target `active`.
- [ ] `machine_readings` or `modulation_list` shows the output moving after a note (Note) or with the source's level (Follower).
- [ ] `render(probes=["Lead"])`: the filter opens on every note.
