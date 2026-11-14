## How it sounds

A control machine for swing: up to eight steps, each stretching or squeezing one tick, repeated over the song. It makes no sound itself; it changes when notes fall. Only the ratios between steps matter, because the pattern is scaled to keep the average length at 1.

## Use it for

- Swing and shuffle for the whole song: a long first tick and a short second tick make a swung eighth.
- A loose, played-by-hand feel on a quantised pattern.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Steps in groove | 2 | how many steps repeat; 0 is off |
| Step 1 length | 1.5x | the first tick lasts one and a half times |
| Step 2 length | 1x | the second tick is the reference |
| Step 3 length | 1x | unused at 2 steps |
| Step 4 length | 1x | unused at 2 steps |
| Step 5 length | 1x | unused at 2 steps |

## Starting points

- Swing: Steps in groove 2, Step 1 length 1.5x, Step 2 length 1x.
- Straight again: Steps in groove 0.
- Set it with `param_set(machine="Groove", changes=[{"param": "Steps in groove", "value": 2}])`.

## Pitfalls

- Every step at its default (1x) means no swing at all; set a step first.
- Steps above Steps in groove are ignored.
- It shifts every machine's timing; render before and after to hear it: `render(probes=["Master"])`.

## Pairs well with

- Any drum or bass pattern written on a straight grid.

## Successor

None: no Neo machine replaces it, so `machine_replace` does not apply; keep it in songs that use it.

## Checklist

- [ ] `machine_describe(machine="Groove")`: it lists `Steps in groove` and eight step lengths.
- [ ] `param_get(machine="Groove")`: `Steps in groove` reads 0 until you set it.
- [ ] `render(probes=["Master"])`: with steps set, the notes' timing differs from a render with 0.
