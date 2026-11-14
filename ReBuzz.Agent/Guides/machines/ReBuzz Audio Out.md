## How it sounds

An effect that sends what is connected to it to one of the audio device's extra outputs, or, at channel 0, passes it through unchanged (the profile: 0 dB, no latency). The channel is chosen in the machine's menu; its one parameter does nothing.

## Use it for

- Sending one part of the song to a separate hardware output (a headphone cue, a second pair of speakers).
- Leaving it at channel 0 is a transparent pass-through.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Unused switch | Off | does nothing; the channel is set in the menu, not here |

## Starting points

- Connect a machine to it and choose its channel from its menu in the machine view; param_set cannot choose the channel.
- To check it passes audio: `analyze(source="Audio Out", metrics=["loudness"])`.

## Pitfalls

- At any channel above 0 the machine's own output is silent: the audio goes to the device output instead, so do not connect it onward and expect sound.
- Unverified: whether the device really has that many outputs; the profile only measured channel 0.

## Pairs well with

- The master, with ReBuzz Audio Out hanging off a bus you want to monitor separately.

## Successor

None: no Neo machine replaces it, so `machine_replace` does not apply; keep it in songs that use it.

## Checklist

- [ ] `machine_describe(machine="Audio Out")`: its one parameter is `Unused switch`.
- [ ] `analyze(source="Audio Out", metrics=["loudness"])`: at channel 0 the level equals its input.
- [ ] `param_get(machine="Audio Out")`: the switch reads Off.
