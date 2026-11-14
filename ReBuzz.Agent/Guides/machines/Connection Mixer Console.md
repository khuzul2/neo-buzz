## How it sounds

A control machine that is a mixing console for up to 16 machine connections: each strip's Volume and Pan set one connection's amp and pan, as the machine view's connection sliders do. It makes no sound itself. A strip does nothing until a connection is assigned to it in the machine's window.

## Use it for

- Hands-on mixing of many connections from one place, with MIDI controllers bound in its window.
- Automating a connection's level or pan from patterns.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Strip 1 volume | 100% | unity gain; 0 to 400% |
| Strip 1 pan | 0% | centre; -100% left, +100% right |
| Strip 2 volume | 100% | the second connection's level |
| Strip 2 pan | 0% | the second connection's pan |
| Strip 3 volume | 100% | the third connection's level |

## Starting points

- Assign a connection to each strip in the window, then move Volume and Pan; unassigned strips do nothing.
- From the tools, `connection_set` sets a connection's amp and pan directly and needs no assignment.

## Pitfalls

- Volume above 100% boosts the connection and can overload the machine it feeds.
- The assignment is saved with the song but is not a parameter, so param_set cannot make it.
- Unverified: how the console behaves in the Studio, which does not show its window.

## Pairs well with

- The machines whose connections you assigned; the master for the overall level.

## Successor

None: no Neo machine replaces it, so `machine_replace` does not apply; keep it in songs that use it.

## Checklist

- [ ] `machine_describe(machine="ConnMixer")`: it lists sixteen Volume and sixteen Pan parameters.
- [ ] `param_get(machine="ConnMixer")`: every Volume reads 100% and every Pan 0%.
- [ ] `analyze_mix`: the mix changes when a strip's Volume moves.
