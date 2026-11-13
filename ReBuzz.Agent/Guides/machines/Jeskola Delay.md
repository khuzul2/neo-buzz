## How it sounds

A classic Buzz mono delay with up to 8 taps, one per track, each with its own length, feedback and level. Lengths count in ticks by default, so the echoes follow the tempo. Simple, clean repeats.

## Use it for

- Tempo echoes, slapback and dub-style long repeats.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Length | 3 | in the length unit; 3 ticks is a dotted eighth |
| Length unit | tick | tick follows the tempo; ms and sample are fixed |
| Feedback | 40% | -100 to +100 %; the safe range is 98.4 % either way |
| Wet out | 48% | echo level, 0 to 100 % |
| Dry thru | On | off for a send with echoes only |

## Starting points

- Presets: "Echo — Dotted Eighth", "Echo — Quarter Note", "Slapback — Short", "Dub — Long Feedback", "Send — Echo Only"; load with `preset_load(machine="Delay", preset="Echo — Quarter Note")`.
- A send: `Dry thru` Off, `Wet out` 100 %.

## Pitfalls

- `Feedback` at the ends never decays (+30 dBFS after 30 s): stay inside the safe range.
- With `Dry thru` On a centred signal comes out about 6 dB louder.
- The longest delay is capped by the machine's Max Delay attribute (1 s by default).
- It is mono: NeoBuzz Live feeds it left + right summed, so a centred signal comes out about 6 dB louder.

## Pairs well with

- Jeskola Reverb after it; Jeskola Filter in the send to darken the repeats.

## Successor

Neo Delay replaces Jeskola Delay: `machine_replace(machine="Delay")` uses Neo Delay's stereo mode with the first track's length as Sync and Feel (or a free Time) and Feedback carried over to plus or minus 99 %. Extra tracks have no counterpart.

## Checklist

- [ ] `analyze(source="Delay", metrics=["loudness"])`: the output is where you expect with `Dry thru` On.
- [ ] `param_get(machine="Delay")`: `Length` and `Length unit` agree.
- [ ] `render(probes=["Delay"])`: the repeats decay to silence.
