## How it sounds

A four-operator FM synth with eight algorithms (ways to connect the operators), up to 16 voices, an envelope per operator and a global LFO. Operator 1 can feed back into itself, from a pure sine to a rough saw-like tone. Bright, glassy and percussive: electric pianos, bells, brass stabs, organs and FM basses. Which operators you hear (the carriers) depends on `Algorithm`; in algorithm 1 (a single chain) only operator 4 is heard and the rest modulate.

## Use it for

- Electric pianos, bells and mallets, brass and chord stabs, plucked and punchy basses, and glassy pads.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Algorithm | 1: Linear | how the operators connect; change it first |
| Op4 Level | 100% | the carrier's loudness in a linear chain |
| Op3 Level | 60% | a modulator's level is the FM depth: more is brighter |
| Op3 Ratio | 2x | multiple of the note's pitch, 0.125 to 16; whole numbers stay harmonic |
| Op3 Decay | 300 ms | a short modulator decay gives a pluck that mellows |
| Op1 Feedback | 20 | 0 to 127; 0 is a pure sine, high is rough |
| Voices | 8 | 1 to 16; extra notes steal the oldest |
| Output | -6 dB | the master level; a square-law curve |

## Starting points

- Factory presets: "Keys — Electric Piano", "Bell — Crystal", "Brass — Dual Chain", "Bass — FM Pluck", "Organ — Additive Drawbars", "Pad — Glass".
- Its own presets (`preset_list(machine="FM")`, 32): "EP Tine", "Glass Marimba", "Vibraphone", "Brass Stab", "String Pad", "FM Bass", "Plucked", "Whistle Lead".
- An electric piano: Algorithm 1, Op3 Ratio 1x, Op3 Level 40 %, Op3 Decay 400 ms, Op4 Ratio 1x, Op4 Decay 800 ms.
- Load with `preset_load(machine="FM", preset="Bell — Crystal")`.

## Pitfalls

- Every parameter at its maximum at once peaks above +4 dBFS; no single one does. Lower `Output` for stacked chords.
- The pitch tracker hears a bright FM spectrum an octave or more off: trust the notes, which play as written.
- `Compound Mod` scales modulators that feed other modulators: raise it slowly, long chains get harsh.
- Glide only slides between overlapping notes on the same voice.
- MIDI notes take any voice up to `Voices`; a note's velocity works through each `Op1 VelSens`... `Op4 VelSens`, which are 0 by default (velocity does nothing until you raise one).

## Pairs well with

- Pedal Chorus or Pedal Plate for gloss; Pedal Chord as a controller; Pedal EQ to tame the highs.

## Successor

No `machine_replace` pair. Neo FM6 (six operators) is the successor for new work; build the patch there by ear from this page's operator settings.

## Checklist

- [ ] `analyze(source="FM", metrics=["spectrum"])`: the harmonics you expected, none above 15 kHz unless you wanted them.
- [ ] `analyze(source="FM", metrics=["peaks"])`: under 0 dBFS with a chord.
- [ ] `param_get(machine="FM")`: `Op3 Ratio` as a multiple, `Op3 Decay` in ms.
