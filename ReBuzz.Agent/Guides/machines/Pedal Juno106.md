## How it sounds

A six-voice analog-style polysynth: a saw, a pulse, a sub-oscillator and noise into a resonant four-pole low-pass, one ADSR envelope for both the filter and the amplifier, a triangle LFO and a chorus. The voices are detuned a little and drift slowly, within a cent, like real oscillators. Warm and wide with the chorus on: pads, brass, keys, plucks and basses. The chorus is the only thing that makes the output stereo.

## Use it for

- Pads and strings, brass stabs, soft keys, plucked chords and simple basses; the starting point for any classic polysynth sound.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| VCF Freq | 2 kHz | the cutoff, 30 Hz to 16 kHz; the default is raw 100, about 4.2 kHz |
| VCF Reso | 10% | 0 to 100%; the top self-oscillates; see Pitfalls before raising it |
| VCF Env Amt | 3 oct | how far the envelope opens the filter, up to 6 oct |
| Env A | 20 ms | attack, 1 ms to 4 s; long for pads |
| Env R | 400 ms | release, 5 ms to 8 s |
| DCO Sub | 30% | a square an octave down, for weight |
| DCO Range | 8' | 16' an octave down, 4' an octave up |
| Chorus | II | Off, I, II or III; makes the output stereo |

## Starting points

- Its own presets (`preset_list(machine="Juno")`, 48): "Pad — Lush", "Pad — Vintage Strings", "Brass — Synth", "Brass — Punchy", "Lead — Saw", "Bass — Sub", "Bass — Acid", "Keys — Bright Pluck", "FX — Sci-Fi Sweep", "Classic — Trance Pad".
- A pad: saw on, Env A 400 ms, Env R 800 ms, VCF Freq 1.5 kHz, VCF Reso 10 %, Chorus I.
- Load with `preset_load(machine="Juno", preset="Pad — Lush")`.

## Pitfalls

- MIDI notes (`notes_write`'s default) take any of the six voices, a free one first; a seventh note steals the one released longest ago.
- No glide between notes.
- `VCF Reso` is 0 at the defaults. Where the envelope or `VCF Freq` takes the filter to its ceiling (the defaults do, from about C2 up), `VCF Reso` of about 16 % and more rings as a steady tone near 18 kHz that carries most of the output's energy: lower `VCF Env Amt` or `VCF Freq` before raising `VCF Reso`.
- Mono voices go into the chorus: with Chorus off the sound is mono.
- The profile found no single parameter that overloads the output.

## Pairs well with

- Pedal Chord or Pedal LFO as controllers; Pedal Plate for space; Pedal Comp on a pad bus.

## Successor

No `machine_replace` pair. Neo Poly (analog-style voices) and Neo Wave cover the same ground; build a preset by ear from this page's settings.

## Checklist

- [ ] `analyze(source="Juno", metrics=["pitch"])`: A4 plays at 440 Hz.
- [ ] `analyze(source="Juno", metrics=["spectrum"])`: the cutoff sits where you set it.
- [ ] `analyze(source="Juno", metrics=["peaks"])`: under 0 dBFS with the saw, pulse and sub all up.
