## How it sounds

A monophonic analog-style synth: a pulse wave, a saw, a sub-oscillator (three choices) and noise into a resonant four-pole ladder low-pass, an ADSR, an LFO and glide. Direct and fat in the bass; with high resonance and the envelope it makes the classic acid zap. Mono, last note priority: going back to a key still held when you release the top one.

## Use it for

- Acid and sub basses, simple leads, plucks and sweeps.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Cutoff | 800 Hz | 20 Hz to 20 kHz; low for basses |
| Resonance | 50% | 0 to 100%; the top self-oscillates |
| Env Amt | 3 oct | -5 to +5 octaves; negative closes the filter |
| Decay | 250 ms | short decays pluck; 2 ms to 15 s |
| Sub Lvl | 40% | adds an octave-down weight; at 100% it clips |
| Glide | 60 ms | slides between overlapping notes only |
| Range | 16' | 16' an octave down, 8', 4' |

## Starting points

- Its own presets (`preset_list(machine="SH")`, 22): "Acid Bass", "Sub Bass", "Deep Sub", "Wobble Bass", "Square Lead", "Glide Lead", "Pluck Seq", "Resonant Sweep", "Kick", "Snare Burst".
- An acid bass: Saw Lvl 100 %, Pulse Lvl 0, Cutoff 400 Hz, Resonance 70 %, Env Amt 4 oct, Decay 180 ms, VCA Mode Gate.
- Load with `preset_load(machine="SH", preset="Acid Bass")`.

## Pitfalls

- The pulse wave is on at the defaults (Pulse Lvl 100 %) and the saw is off.
- `Sub Lvl` at its maximum is hot (+0.7 dBFS at velocity 100, about +2.8 at full level): lower `Volume` when the sub is up.
- Glide only slides between overlapping notes: a note after silence starts at its own pitch.
- `Env Amt` is bipolar: its default (raw 64) is 0 octaves, so the envelope does nothing until you raise it.

## Pairs well with

- Pedal Filter or Pedal Shaper for more bite; Pedal Plate for space; Pedal LFO to sweep the cutoff.

## Successor

No `machine_replace` pair. Neo Poly's mono and legato presets are the successor for new work; build one by ear from this page's settings.

## Checklist

- [ ] `analyze(source="SH", metrics=["pitch"])`: A4 plays at 440 Hz.
- [ ] `analyze(source="SH", metrics=["peaks"])`: under 0 dBFS with the sub up.
- [ ] `param_get(machine="SH")`: `Cutoff` in Hz, `Env Amt` in octaves.
