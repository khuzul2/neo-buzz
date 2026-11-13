## How it sounds

A four-voice stereo chorus. Each voice is a short delay swept by its own LFO, and the four LFOs run a quarter-cycle apart, so most of the pitch wobble cancels and what remains is width and thickness. The input can be mono or stereo; the output is always stereo. `Tone` darkens only the wet copies, never the dry sound. At `Spread` 0 % the four voices sit in the centre and the output is mono.

## Use it for

- Thickening drums, pads, plucks and bass without an obvious wobble, widening a mono source, and a doubled sound at short `Delay` values.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Rate | 0.6 Hz | 0.1-5 Hz; slow is a shimmer, fast starts to warble |
| Depth | 2.8 ms | 0-8 ms of delay swing; the default |
| Delay | 12 ms | 5-25 ms; short sounds tight and doubled, long sounds distant |
| Spread | 80% | 0% is mono, 100% is widest |
| Tone | 18 kHz | wet low-pass, 22 kHz bright to 2 kHz dark |
| Mix | 45% | on a send use 100% |

## Starting points

- Presets: "Chorus — Subtle Width", "Chorus — Lush Ensemble", "Chorus — Slow Shimmer", "Chorus — Dark Mono", "Doubler — Short Thick", "Vibrato — Wet Wobble".
- A doubler: Delay 20 ms, Depth 10 %, Mix 50 %, Spread 100 %.
- Load one with `preset_load(machine="Chorus", preset="Chorus — Lush Ensemble")`.

## Pitfalls

- The wet signal low-passes at `Tone` before it is mixed: the highs of the chorus are quieter than the dry, which is intended.
- A mono-compatibility check matters at high `Spread`: the four voices panned wide can thin in mono.
- Very high Depth with long Delay detunes audibly: it stops being a chorus and becomes vibrato (`Mix` 100 %).

## Pairs well with

- Neo Verb after it for space; Neo EQ before it to remove low end that does not need width; Pedal Plate on a send.

## Successor

Neo Ensemble replaces Pedal Chorus: `machine_replace(machine="Chorus")` carries Rate, Depth, Delay, Spread, Tone and Mix over in their units. The match is within 1 dB of loudness and 2.5 dB per third octave; with wet-heavy mixes above about 60 % and Spread up, one low third octave can differ by up to 7 dB.

## Checklist

- [ ] `analyze(source="Chorus", metrics=["stereo"])`: the width grew against the input, and mono sums do not hollow out.
- [ ] `param_get(machine="Chorus")`: `Spread` and `Mix` are what you set, in %.
- [ ] `render` with the machine bypassed (`machine_set(machine="Chorus", bypass=true)`) and `compare` the two.
