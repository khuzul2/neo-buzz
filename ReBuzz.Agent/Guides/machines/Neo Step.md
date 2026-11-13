## How it sounds

A step sequencer for values with no audio of its own: 1 to 32 steps, each a level from 0 to 100 %, stepping at a rate locked to the song's tempo. It moves up to 8 parameters of other machines, so a cutoff, a pan or a send follows a drawn pattern: rhythmic filtering, a hand-made gate, a stepped melody on a pitch parameter.

- `Steps` sets the length (1 to 32); `Rate` is 1/2 down to 1/32, straight or dotted; `Swing` pushes every second step late.
- `Glide` slides between the steps (0 % is stepped, 100 % a continuous line); `Random` adds a random amount to each step on every pass.
- `Step 1` … `Step 32` are the step levels, drawn as bars in the editor (also settable by `param_set`).
- Per target: depth −100 … +100 %, polarity (uni by default), an optional range and curve; `Write glide` limits how fast a write moves.

## Use it for

- Rhythmic filter patterns, a stepped gate on a pad, a pan sequence, a bass cutoff that follows a groove.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Steps | 8 | the pattern length; odd lengths drift against the bars |
| Rate | 1/16 | how long each step lasts |
| Glide | 0 % | 0 is hard steps, more is a slur |
| Random | 0 % | varies the steps every pass |
| Swing | 0 % | late second steps |
| Step 1 | 0 % | level of step 1 (Step 1 … Step 32) |
| Write glide | 30 ms | the least time a target takes to cross its range |

## Starting points

- A 16th-note cutoff pattern: Steps 16, Rate 1/16, `Step 1` 100 %, `Step 5` 70 %, `Step 9` 90 %, `Step 13` 50 % and the rest low; `modulate(source="Step", target_machine="Bass", param=<the cutoff>, depth=40)`.
- A stepped gate: Glide 0 %, alternate step levels 100 % and 0 % with depth −100 % on a volume parameter.

## Pitfalls

- A step pattern is a modulation, not a note: it cannot change which note plays, only a parameter such as cutoff or pitch.
- Hard steps on a pitch parameter click; use a little `Glide` or a longer `Write glide`.
- Steps beyond `Steps` are kept but not played.

## Pairs well with

- Neo Filter, Neo Poly, Neo Delay and Neo Utility (pan) as targets; Neo LFO for a smooth layer on top.

## Successor

Nothing replaces it.

## Checklist

- [ ] `modulation_list(machine="Step")` shows each target `active`.
- [ ] `param_get(machine="Step", params=["Steps", "Rate"])` shows the length and rate you meant.
- [ ] `render(probes=["Bass"])` and a spectrogram show the pattern in time with the beat.
