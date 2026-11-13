## How it sounds

One knob that moves up to 8 parameters of other machines at once, with no audio of its own. Turn `Value` from 0 to 100 % and every target moves by its own depth, range and curve, so one move changes the cutoff, the reverb and the drive together: a build, a filter-and-wet sweep, a performance control. `Value` is an ordinary parameter, so a pattern can automate it, the agent can set it and a MIDI controller can be bound to it.

- Per target: depth −100 … +100 % (a negative depth moves it the other way), polarity (uni by default: 0 % leaves the base, 100 % adds the depth), an optional range and a curve (bends the response).
- `Write glide` limits how fast any target moves, so a jump of the knob becomes a smooth move.

## Use it for

- Build-ups and drops, one fader for a whole sound's brightness, a live control for a set.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Value | 0 % | the knob |
| Write glide | 100 ms | how fast the targets follow a jump |

## Starting points

- A build: `modulate(source="Macro", target_machine="Lead", param=<the cutoff>, depth=60)`, then the reverb send `depth=40` and a drive `depth=30`; automate `Value` over 8 bars with `automation_write`.

## Pitfalls

- The targets move from the values you set: with Value at 0 % nothing is added; move the base with `param_set` and the whole sweep moves with it.
- More than 8 targets need a second Neo Macro.

## Pairs well with

- Neo LFO or Neo Envelope on the same target for movement on top (the second one takes the first's output as its base).

## Successor

Pedal Peer Control has no mapping to it: its Value is a per-track parameter (up to 64 tracks, each with its own target and mapping curve) and Neo Macro has one Value with up to 8 targets, so the tracks have no single counterpart. Make one Neo Macro per control and point it with `modulate`.

## Checklist

- [ ] `modulation_list(machine="Macro")` shows each target `active`.
- [ ] `param_set(machine="Macro", changes=[{"param": "Value", "value": "100%"}])` moves every target to the top of its swing: `modulation_list` shows each `now`.
- [ ] `modulation_remove(source="Macro", all=true)` leaves the parameters at the values you set.
