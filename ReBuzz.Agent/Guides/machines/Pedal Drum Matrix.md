## How it sounds

A rack of six effects in series, each slot any of 21 effects (bitcrush, drive, low-pass, high-pass, ring modulation, comb, stutter, delay, reverb, gate, tuned resonator, transient shaper, wavefolder, phaser, sub-octave, formant, resampler, chorus, freeze, auto-wah, exciter). Every slot has `Amount`, a `Char` control and a `Mode` switch whose meaning follows the effect loaded (a filter's cutoff and Q, a delay's time in ticks, a resonator's scale degree), and an envelope-follower and LFO depth. A feedback loop with its own time and tone wraps the whole rack, an output limiter guards it, and `Morph` blends toward a stored scene.

All slots are None at the defaults: it passes sound unchanged until a slot has a `Type` and an `Amount`.

## Use it for

- Mangling and texturing drums and loops: crushed, resonant, gated, stuttered, tuned or fed-back variations of a break.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Slot1Type | Drive | choose the effect first; None is off |
| Slot1Amount | 50% | 0% is clean; this slot's intensity or wet |
| Slot1Char | 64 | raw 0-127: its meaning follows the effect (read the value text) |
| Slot2Type | Reverb | slots run 1 to 6 in series |
| Feedback | 20% | the rack's output back into its input; the limiter holds it |
| FbTime | 80 ms | the feedback path's delay, 1-500 ms |
| Limiter | On | on by default; leave it on with feedback |
| Morph | 0% | blends toward the stored scene |

## Starting points

- Its own presets (`preset_list(machine="DrumFx")`, 30): "Sub Crusher", "Warm Drive", "Fuzz Clip", "Reso Lowpass", "Telephone", "Ring Bell", "Metal Comb", "Beat Repeat", "Dub Echo", "Big Hall", "Trance Gate", "Tuned Bell C", "Industrial Kit".
- A tape-ish crunch: Slot1Type Drive at 40 %, Slot2Type Lowpass at 60 % with `Slot2Char` 90.
- Load with `preset_load(machine="DrumFx", preset="Dub Echo")`.

## Pitfalls

- `Slot1Char` and `Slot1Mode` change meaning with the effect: read the value texts in `machine_describe`, do not assume a unit.
- Delay, stutter and LFO times are in ticks, so they follow the tempo of the song.
- `Feedback` with several slots can howl: keep `Limiter` on and move `Feedback` slowly.
- It is a lot of control: 49 parameters. Start with one slot.

## Pairs well with

- Neo Drums or Neo Sampler into it; Pedal Gate before it; Pedal Comp after it to glue the result.

## Successor

None: Neo has no multi-effect rack, and Neo Drums is a drum machine, so there is no `machine_replace` pair. For one job, use Neo Drive (distortion), Neo Filter (filter) or Neo Delay.

## Checklist

- [ ] `analyze(source="Drums", metrics=["spectrum"])`: the slot you changed moved the spectrum, and nothing else did.
- [ ] `analyze(source="Drums", metrics=["peaks"])`: the output stays under 0 dBFS with `Limiter` on.
- [ ] `param_get(machine="DrumFx")`: the slots you set show their effect by name.
