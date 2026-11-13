## How it sounds

An algorithmic reverb with four spaces, dense and smooth, with exact decay times: the mid-band decay measures within -5 % and +2 % of `Decay`. Modulation keeps long tails from ringing metallic, `Freeze` holds a tail forever and `Shimmer` feeds it back an octave up.

| Mode | Character | Use it for |
|---|---|---|
| Hall | big, smooth, slow build | pads, strings, leads, long sends |
| Room | short, early-reflection heavy | drums, percussion, a sense of place |
| Chamber | dense and even, between room and hall | vocals, keys |
| Plate | bright, instant density | snares, vocals, drums |

Path: `Pre-delay`, input `Diffusion`, `Early` reflections, a 16-line feedback network (`Size`, `Decay`, `Low decay`, `High damping`, `Mod depth`, `Mod rate`), `Late`, `Width`, `Low cut`, `High cut`, `Mix`. Every control has a How and a Sound sentence in `machine_describe`; `param_set` takes the name or the id.

## Use it for

- A send reverb for a whole mix (`Mix` 100 %), a room on one drum or vocal (`Mix` 15-35 %), ambient pads with `Freeze` and `Shimmer`.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Mode | Plate | Hall for pads, Room for drums, Chamber for vocals |
| Decay | 1.6 s | gone by the next downbeat; 1-2 beats for drums, 1-2 bars for pads |
| Pre-delay | 20 ms | the attack stays dry; 60000 / BPM x the beat fraction |
| High damping | 12 kHz | lower (3-6 kHz) is darker and farther |
| Low cut | 200 Hz | keeps the return clean; 200-400 Hz |
| High cut | 8 kHz | 6-10 kHz keeps it from hissing |
| Mix | 100% | on a send; 15-35 % as an insert |

## Starting points

- "Send — Hall (100% wet)", "Send — Plate (100% wet)", "Hall — Concert", "Room — Live Drums", "Chamber — Vocal", "Plate — Bright Vocal", "Plate — Snare".
- Long and special: "Hall — Cathedral" (8 s), "Hall — Dark Strings", "Ambient — Shimmer", "Ambient — Infinite Pad", "Ambient — Long Dark Hall".
- Warmer: `Low decay` 1.2-1.5 x. Darker and farther: `High damping` 3-6 kHz.

## Pitfalls

- `Freeze` On takes no new input: automate it with steps (On, then Off) so the reverb hears the next section.
- `Shimmer` above 50 % with a long decay builds up: watch the level with `machine_readings`.
- `Width` above 100 % lowers the tail's correlation: check the mix in mono.
- Long decays on sends add up across parts: filter the return with `Low cut` and `High cut`.

## Pairs well with

- Neo Delay before it (echoes into a plate) or beside it on another send; Neo EQ on the return; Neo Comp with the reverb on input 2 to duck it under the source; Neo Gate on the return for a gated tail.

## Successor

Neo Verb replaces Jeskola Reverb and Pedal Plate: `machine_replace(machine="Reverb")` carries the settings over in their units.

## Checklist

- [ ] `param_set(machine="Verb", changes=[{"param": "Decay", "value": "1.6 s"}])`, then `render(probes=["Verb"])` and `analyze` its loudness: the wet level sits behind the dry sound.
- [ ] `machine_readings(machine="Verb")` gives `rms_db` and `peak_db`: no clipping with `Shimmer` or `Freeze`.
- [ ] `analyze(source="Verb", metrics=["stereo"])`: the correlation stays positive at the chosen `Width`.
- [ ] `render` with `tail_seconds` longer than `Decay`, so the tail is not cut off.
