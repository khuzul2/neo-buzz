## How it sounds

A three-band equaliser with tube-style drive: each band has a boost or cut of plus or minus 16 dB, its own overdrive, a centre frequency, a width and one of six circuit models (transistors, tubes I or II, plain or warm).

## Use it for

- Warm, coloured tone shaping.
- Tube-style saturation on one band.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Band 1 boost | 4 dB | -16 to +16 dB |
| Band 1 drive | 25% | 0 to 100 percent |
| Band 1 frequency | 100 Hz | about 20 Hz to 20 kHz |
| Band 2 boost | -3 dB | -16 to +16 dB |
| Band 3 boost | 3 dB | -16 to +16 dB |
| Band 3 mode | Tubes I, warm | six circuit models |

## Starting points

- A warm top: `param_set(machine="Tube EQ", changes=[{"param": "Band 3 boost", "value": "4 dB"}, {"param": "Band 3 mode", "value": "Tubes I, warm"}])`.

## Pitfalls

- Band 3's boost at its top is hot: +4.7 dBFS peak in the profile.
- Bands 2 and 3 carry band 1's descriptions in the machine's own texts (a copy mistake); they are separate bands.
- It is 0 dB at the defaults.

## Pairs well with

- Jeskola EQ-3 for a cleaner cut.

## Successor

Neo EQ covers the same ground, but nothing maps the tube bands onto it: `machine_replace` has no rule for it, so set it up by hand.

## Checklist

- [ ] `analyze(source="Tube EQ", metrics=["loudness"])`: the output stays within about 3 dB of the input.
- [ ] `param_get(machine="Tube EQ")`: `Band 1 boost` is what you set.
- [ ] `render(probes=["Tube EQ"])`: no band clips.
