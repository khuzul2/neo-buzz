## How it sounds

A granular generator: it cuts small grains from two wavetable slots and scatters their position, length, rate, pan and envelope into a cloud of sound. It is silent until a wave is in its slot, and the profile could not hear it, so what it sounds like was not measured.

## Use it for

- Textures and drones from a recorded sound.
- Smearing a sample into ambience.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Skip direction | Random | Forwards or Backwards scan through the wave instead |
| Grain length | 0.023 s | short grains fizz, long grains stay recognisable |
| Grain rate | 1 | playback rate of the grains, a table from 0.25 to 4 times |
| Envelope amount | 50% | fade grains in and out to avoid clicks |
| Maximum grains | 20 | 1 to 100; more are thicker and cost CPU |
| Random seed | 0 | the same seed repeats the same cloud |

## Starting points

- Load a wave first: `wave_import(path="/path/pad.wav", slot=1)`, then set Wave slot 1 to 1.
- A slow cloud: Grain length 0.1 s, Envelope amount 80%, Maximum grains 30.

## Pitfalls

- Silent at its defaults (empty slots, health warn): load a wave first.
- Its parameter list has gaps in the machine's own numbering: some controls are not exposed.
- Unverified: how it is triggered, and what it sounds like with a wave loaded.

## Pairs well with

- Neo Verb for a wash; Neo Delay for movement.

## Successor

None: Neo Sampler and the Neo Learn Granular teaching machine are different machines, so `machine_replace` does not apply.

## Checklist

- [ ] `machine_describe(machine="ACloud")`: 16 parameters, `Grain length` in seconds.
- [ ] `wave_list`: a wave is in slot 1 before you expect sound.
- [ ] `machine_readings(machine="ACloud")`: reads silence until a wave is loaded.
