## How it sounds

A 30-band graphic equaliser with a band every third of an octave from 20 Hz to 20 kHz, plus or minus 18 dB each, a high-pass and a Bandwidth control for how wide the bands are. A band left at 0 dB is switched off to save CPU.

## Use it for

- Fine tonal correction.
- Sculpting a mix with many small moves.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Enable | On | off bypasses it |
| Input gain | 0 dB | about -118 to +7.6 dB |
| 1460 Hz band | 3 dB | -18 to +18 dB |
| 350 Hz band | -3 dB | -18 to +18 dB |
| Band width | 1 oct | 16 steps, 0.2 to 2.3 as its text shows |
| Highpass | 22 Hz | 5 to 100 Hz as its text shows; the lowest step is off |

## Starting points

- A presence lift: `param_set(machine="Hyperion", changes=[{"param": "2970 Hz band", "value": "3 dB"}, {"param": "Band width", "value": "1 oct"}])`.

## Pitfalls

- Bands overlap and add: at the default width every band at +6 dB makes +43 dB at 3.2 kHz, so lower `Input gain` as you boost.
- Single bands at +18 dB peak above full scale in the profile (+3.9 dBFS and more).
- Band width and Highpass use the machine's own scales: Band width is twice the octaves its text shows; the Highpass corner is about 1.28 times the number shown.
- Many bands cost CPU.

## Pairs well with

- Jeskola EQ-3 for a broad tone move.

## Successor

Neo EQ replaces Joachims Hyperion: `machine_replace(machine="Hyperion")` makes a Neo EQ in Graphic mode, reading the summed curve at its 31 centres; Input gain goes to Output, Highpass to the lowest sliders and Enable to the bypass.

## Checklist

- [ ] `analyze(source="Hyperion", metrics=["loudness"])`: about the input level with the bands at 0 dB.
- [ ] `param_get(machine="Hyperion")`: `Band width` and the bands you moved are as set.
- [ ] `render(probes=["Hyperion"])`: boosts do not clip.
