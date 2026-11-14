## How it sounds

A control machine that sends the notes of a guitar chord to target machines on a MIDI channel, strummed with a delay per string and a little randomness. It makes no sound itself; the chord is picked in its window and the targets play it.

## Use it for

- Guitar-like strums from a synth or a sampler.
- Humanised chord stabs with random timing and volume.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Send notes | Yes | turns the sending on |
| MIDI channel | 0 | 0 is channel 1 |
| Send velocity | 60 | 0 to 127 |
| Delay randomness | 40 | 0 to 2000 ms of random delay |
| Low E delay | 0 s | the first string |
| High E delay | 0.1 s | up to 2 s; a later string makes a strum |
| Transpose | 0 | -12 to +12 semitones |

## Starting points

- A downstrum: Low E delay 0 s up to High E delay 0.1 s in equal steps.
- A loose feel: Delay randomness 40 and Volume randomness 20.

## Pitfalls

- The chord and the targets are chosen in its window, not through parameters: nothing plays until a person picks them.
- Unverified: which machines receive the notes by default, and the chords on offer.
- String delays reach 2 s, longer than most strums: keep them short.

## Pairs well with

- A guitar-like instrument to receive the notes.

## Successor

None: no Neo machine replaces it, so `machine_replace` does not apply; keep it in songs that use it.

## Checklist

- [ ] `machine_describe(machine="GuitarChordBox")`: twelve parameters, `Send notes` first.
- [ ] `param_get(machine="GuitarChordBox")`: `Send notes` reads No until set.
- [ ] `midi_bindings_list`: lists what drives the app, as a way to start a strum.
