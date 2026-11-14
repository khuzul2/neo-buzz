## How it sounds

A subtractive polysynth: two oscillators with pulse-width modulation and a sub-oscillator, a multimode six-pole filter with its own envelope, an amp envelope and two LFOs. The supersaw wave gives the big detuned sound; it plays an octave low.

## Use it for

- Supersaw leads, pads and basses.
- Plucks and soft keys.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Oscillator 1 wave | SuperSaw | stacked detuned saws |
| Oscillator 2 detune | 8 ct | 0 to 100 cents above oscillator 1 |
| Filter type | 2L Phlatt | 18 filters, from six-pole low-passes to a notch |
| Filter cutoff | 55% | 0 to 100 percent |
| Filter envelope amount | 40% | -200 to +200 percent |
| Release | 0.2 s | 80 ms to 12 s |

## Starting points

- Presets: "Pad — Supersaw Strings", "Lead — Saw Bright", "Bass — Square Sub", "Pluck — Glassy", "Keys — Soft PWM"; load with `preset_load(machine="Infector", preset="Lead — Saw Bright")`.

## Pitfalls

- It plays an octave low: `notes_write` adds 12 semitones so the written pitch sounds.
- Several parameters share a name (the filter's and the amp's Decay): use the plain names, or the index.
- One voice per track, 12 at most; `notes_write` spreads chords over tracks.
- Quiet at its defaults: one note measures about -26 LUFS.

## Pairs well with

- Jeskola Delay for echoes; Jeskola Reverb for space.

## Successor

Neo Poly covers the same ground (a subtractive polysynth with detuned oscillators and a multimode filter), but nothing maps Infector's settings onto it: `machine_replace` has no rule for it, so rebuild the patch by hand.

## Checklist

- [ ] `analyze(source="Infector", metrics=["loudness"])`: a note is near -26 LUFS at the defaults; raise the level at the output.
- [ ] `param_get(machine="Infector")`: `Filter cutoff` is what you set.
- [ ] `render(probes=["Infector"])`: the note decays to silence after release.
