## How it sounds

A mixer, not a colour: up to 24 stereo inputs (6 on a new machine), one per track, each with a fader, a pan, a mono switch, mute and solo, then a master fader and master mute. Faders move in 0.5 dB steps from -80 dB to +10 dB (the lowest step is silence). Pan is a balance control on stereo inputs and a constant-power pan on mono ones. Every mute and solo fades over `Inertia`, so nothing clicks. Each track also has its own direct output, after mute, fader and pan but before solo.

## Use it for

- Mixing several machines into one stage, a submix bus, and soloing parts without touching the main mixer.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Volume | -6 dB | each track's fader (track parameter, `track` argument); 0 dB is the default |
| Pan | 0% | track pan: -100% left, 0% centre, +100% right |
| Mute | Off | per track; fades over Inertia |
| Solo | Off | per track; affects the master mix only |
| Mono | Off | sums the track to mono before the fader |
| Gain | 0 dB | the master fader |
| Master Mute | Off | fades the whole mix |
| Inertia | 25 ms | 0-500 ms fade for every mute and solo |

## Starting points

- Connect each source with `connect(from="Pad", to="Mix")`: each connection takes the next track, in order.
- A balanced bus: Volume 0 dB on every track, the master Gain at -3 dB, Pan spread from 32 to 96.
- A solo check: `param_set(machine="Mix", changes=[{"param": "Solo", "track": 0, "value": "On"}])`.

## Pitfalls

- Solo does not reach the direct outs, and the master Gain and Master Mute never touch a direct out; Mute does.
- At its maximum, `Gain` or a `Volume` peaks above 0 dBFS (+0.8 and +1.0 dBFS in the profile).
- The 2.0 parameter layout broke 1.x songs: their settings do not carry over.
- Its meters and the loudness window are in the machine's own window; the tools read levels with `meters_read` and `analyze`.

## Pairs well with

- Pedal Comp or Neo Limiter after it on the master; Neo Utility for per-source width.

## Checklist

- [ ] `machine_describe(machine="Mix")`: the tracks show as many inputs as you connected.
- [ ] `meters_read(machines=["Mix"])`: no clipping on the master.
- [ ] `param_set(machine="Mix", changes=[{"param": "Mute", "track": 1, "value": "On"}])` mutes the second input; the others still play.
