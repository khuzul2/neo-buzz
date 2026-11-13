## How it sounds

A two-oscillator sample synth. Each oscillator plays a wave from the song's wavetable and can switch to a different wave in up to four key zones (set by the split notes), so a multisampled piano or string keeps its natural tone across the keyboard. The sound goes into a two-pole low-pass (gentle, never self-oscillating), two ADSR envelopes (filter and amplifier) and two LFOs that can move pitch, filter and level. It is as good as the waves you give it: pianos, strings, brass and other sampled instruments.

## Use it for

- Playing multisampled instruments, layering two waves, and tracks that need a filter and envelopes on a sample.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| OSC1 Wave | 1 | the wavetable slot, 1-200; it is silent until the slot holds a wave |
| OSC Mode | Both | OSC1 only, OSC2 only, or Both |
| OSC2 Detune | 8 ct | against oscillator 1 for width; ±50 cents |
| Cutoff | 8 kHz | 20 Hz to 18 kHz; wide open is the default |
| Amp Attack | 10 ms | 1 ms to 8 s |
| Amp Release | 400 ms | the tail |
| Amp Vel | 80% | how much velocity sets the level |
| Volume | 100% | the machine runs about 6 dB below other synths: raise it |

## Starting points

- Load a sample first: `wave_import(path="C:/Samples/piano-C3.wav", slot=1, name="Piano", root_note="C3")`.
- A multisample: OSC1 Wave 1, OSC1 Wave 2 2 with OSC1 Split 2 60 (C4): the second wave takes over from C4 up.
- A layered pad: OSC1 Wave 1, OSC2 Wave 2, OSC2 Detune 10 ct, Amp Attack 600 ms, LFO1 to VDF 1 oct.

## Pitfalls

- Needs a wave: it is silent until the slot holds one. A wave edited after it first played is cached: reload the machine to hear the change.
- Its Note track plays a voice per track at the track `Volume` (that is a separate track parameter from the global `Volume`).
- It runs about 6 dB below other synths because of its internal headroom: raise `Volume`.
- Pitched down an octave, the cubic interpolation dulls the highs; pitched up it adds aliases.

## Pairs well with

- Pedal Plate for space; Pedal Comp; Pedal Chorus on strings.

## Successor

Neo Sampler replaces Pedal M1: `machine_replace(machine="M1")` puts both oscillators and their key zones on Neo Sampler's zones 1-4 and 5-8, with the same waves from the song's wavetable at the same pitch, and carries the low-pass, the envelopes and the levels. The match is within 1 dB of loudness and 2 dB per third octave. Not carried: the LFOs and the portamento; resonance differs (Neo Sampler's filter removes bass with resonance), and a wave pitched down an octave is brighter.

## Checklist

- [ ] `wave_import` reports the slot; `machine_describe(machine="M1")` shows `OSC1 Wave` at that slot.
- [ ] `analyze(source="M1", metrics=["pitch"])`: a note plays at its written pitch.
- [ ] `analyze(source="M1", metrics=["loudness"])`: close to your other synths after raising `Volume`.
