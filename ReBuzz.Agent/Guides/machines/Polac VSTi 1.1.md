## How it sounds

Hosts an instrument plugin in the graph. It has a level, a pan, a program number, a global command with a value, and for each of its tracks a plugin parameter number, a value and a MIDI channel. An older 32-bit-era host; with no plugin loaded it is silent.

## Use it for

- Running a plugin you already have, from an old song.
- Automating plugin parameters from the tracker.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Wet level | 0 dB | the plugin's level, silence to +12 dB |
| Pan | 0 | -64 to +64; unit unverified |
| Program | 0 | the plugin's patch number, 0 to 65,534 |
| Global command | Transpose | one of 18 host commands |
| Global command value | 65534 | its value; its own default, 65,535, means not written (NeoBuzz Live takes 65534) |

## Starting points

- Read what it does in a song that has it: `machine_describe(machine="VSTi")`.

## Pitfalls

- Unverified: which plugins it can load in this build, the numbering behind Plugin parameter, and the unit of Pan.
- With nothing loaded the profile measured the empty host only; what a loaded plugin does was not tested.
- When it has the keyboard focus, MIDI input reaches it with a conversion for its format (the host's own handling).
- Up to 128 tracks; each track's three columns steer one plugin parameter on one MIDI channel.

## Pairs well with

- The plugin you load into it.

## Checklist

- [ ] `machine_describe(machine="VSTi")`: its parameters read in the units above.
- [ ] `param_get(machine="VSTi")`: `Wet level` is 0 dB at the default.
- [ ] `render(probes=["VSTi"])`: it does what the loaded plugin does.
