## How it sounds

A convolution reverb with no latency: the sound is run through an impulse response, so the space is the recorded or made shape of a room, not an algorithm. Nine rooms are made by the machine's own code (`Small room`, `Ambience`, `Drum room`, `Chamber`, `Plate`, `Hall`, `Large hall`, `Cathedral`, `Tunnel`), or `Wave` uses a wave of the song as the room (mono or stereo, up to 10 s). A new room is prepared in the background and crossfaded in over 50 ms, so changing it while the song plays does not click. All rooms are set to the same loudness on music.

| Room | Character | Use it for |
|---|---|---|
| Small room, Ambience | close, short (0.3-0.5 s) | vocals, guitars, a place for a dry sound |
| Drum room, Chamber | 0.9-1.5 s, even | drums, keys, depth in a mix |
| Plate | no room, dense and bright, 2 s | vocals, snares |
| Hall, Large hall, Cathedral | 2-6 s, slow | pads, strings, last chords |
| Tunnel | long, narrow, a flutter | effects, drones |
| Wave | any wave of the song | a clap, a burst of noise, a field recording, a drone |

Path: the response is shaped (`Length` trim, `Reverse`, `Low cut`, `High cut`, `Width`, `Pre-delay`), then convolved. Every control has a How and a Sound sentence in `machine_describe`; `param_set` takes the name or the id.

## Use it for

- A send reverb (`Mix` 100 %), a room on one part (`Mix` 15-40 %), a reverse swell into a downbeat (`Reverse` On), putting a sound inside any wave.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Room | Chamber | rooms are in order of size; Wave uses the slot in `Wave` |
| Wave | 1 | the slot (1-200) when `Room` is Wave; an empty slot plays the hall and says so |
| Pre-delay | 20 ms | keeps the attack dry; 60000 / BPM x the beat fraction |
| Length | 100 % | shorter cuts the tail with a fade (a tighter room) |
| Low cut | 150 Hz | keeps the return from muddying the bass; the lowest setting is off |
| High cut | 9 kHz | darker and farther; the highest setting is off |
| Width | 100 % | 0 is mono, 200 % is twice the side |
| Mix | 100 % | on a send; 15-40 % as an insert |

`Amount` (default 100 %) scales the reverb level: 0 % is the bypassed dry sound bit for bit, 200 % doubles the wet level. `Output` sets the reverb's level in dB.

## Starting points

- "Room — Small Room", "Drums — Drum Room", "Plate — Bright Plate", "Chamber — Smooth Chamber", "Hall — Large Hall", "Room — Ambience", "Effect — Reverse Swell", "Tail — Dark Long Tail".

## Pitfalls

- With `Room` on Wave the song saves the wave, not the machine: the slot must hold a wave. Loading a wave or moving `Pre-delay`, `Length`, the cuts, `Width` or `Reverse` builds a new response (up to about a second for a long one): the old one plays until the new one is crossfaded in.
- `Reverse` plays the whole response backwards from the trimmed end, so it needs a long `Length`; the tail comes before the sound it follows.
- A response longer than 10 s is cut; very long waves cost more CPU (about one percent of a core for 4 s of stereo).
- A response is mono-compatible only at `Width` 0; check long tails in mono.
- Several long reverbs on sends add up: filter each return with `Low cut` and `High cut`.

## Pairs well with

- Neo Delay before it (echoes into a room); Neo EQ on the return; Neo Comp with the reverb on input 2 to duck it under the source; Neo Verb for an algorithmic tail beside it.

## Checklist

- [ ] `param_set(machine="Space", changes=[{"param": "Room", "value": "Hall"}])`, then `render(probes=["Space"], tail_seconds=6)`: the render waits for the room, and the tail is not cut off.
- [ ] `machine_readings(machine="Space")` gives `rms_db` and `peak_db`: the reverb sits behind the dry sound.
- [ ] `analyze(source="Space", metrics=["stereo"])`: the correlation stays positive at the chosen `Width`.
- [ ] With `Room` Wave: `machine_describe(machine="Space")` shows the Wave slot; the slot must hold a wave, or the hall plays instead.
