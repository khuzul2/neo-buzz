## How it sounds

A control machine that sends MIDI to an output device: each track sends notes and, from its controller columns, controller changes and the pitch wheel. It makes no sound itself; the device it drives does.

## Use it for

- Playing external synthesisers or a drum machine from a pattern.
- Sending controller changes (modulation, volume) and pitch bend to a device on a given channel.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| MIDI output device | 0 | the device's number in the app's output list |
| MIDI channel | 0 | 0 is channel 1, 15 is channel 16 |
| Controller number | 1 | 0 to 127 a controller, 224 the pitch wheel |
| Controller value | 64 | 0 to 127; the wheel uses 0 to 16,383 with 8,192 as centre |

## Starting points

- Write notes into a pattern of the machine with `notes_write(machine="Midi Output", pattern="A", notes=[{"pitch": "C3", "start": 0, "length": 1}])`.
- Modulation sweep: Controller number 1 with Controller value written along the pattern.

## Pitfalls

- The profile heard silence by design (a health warn); whether a device receives the notes was not tested here: unverified.
- Controller number values other than 0 to 127 and 224 send nothing.
- Channel is 0-based: raw 0 is MIDI channel 1.

## Pairs well with

- An external instrument on the chosen channel; `midi_bindings_list` for the opposite direction, controllers into the song.

## Successor

None: no Neo machine replaces it, so `machine_replace` does not apply; keep it in songs that use it.

## Checklist

- [ ] `machine_describe(machine="Midi Output")`: it lists four track parameters, `MIDI output device` first.
- [ ] `param_get(machine="Midi Output", track=0)`: the device and channel read 0.
- [ ] `notes_read(machine="Midi Output", pattern="A")`: the notes written are back.
