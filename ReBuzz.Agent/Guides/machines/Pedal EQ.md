## How it sounds

A clean four-band equaliser: a low shelf, two bells (low-mid and high-mid) and a high shelf, each with up to 24 dB of boost or cut, and an output trim. Frequencies and Q move in fixed, labelled steps (`param_set` lands on the nearest step), so a setting always means the same thing. With every gain at 0 dB and `Output` at 0 dB the signal passes untouched. The solo switches play one band's filter alone, to hear what it touches.

## Use it for

- Corrective cuts (mud, rumble, boxiness), tonal shaping of a track or bus, and a quick high-pass-like cut with the low shelf.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| LS Freq | 80 Hz | the shelf's corner; steps from 20 to 500 Hz |
| LS Gain | -3 dB | cut for rumble, boost for weight; ±24 dB in 0.5 dB steps |
| LM Freq | 400 Hz | where mud and boxiness live; 100 Hz to 5 kHz |
| LM Gain | -3 dB | a small cut clears the body |
| LM Q | 1 | 0.3 broad to 10 narrow; narrow for single problems |
| HM Freq | 3150 Hz | presence; 500 Hz to 16 kHz |
| HS Gain | 2 dB | air; the corner is `HS Freq` (1 to 20 kHz) |
| Output | 0 dB | trim to match the untouched level |

## Starting points

- Presets: "Tone — Warm Lows", "Tone — Bright Air", "Cut — Mud Removal", "Cut — Low Rumble", "Vocal — Presence", "Telephone — Narrow Band".
- Find a problem with `LM Gain` +9 dB, sweep `LM Freq`, then cut where it sounds worst, with `LM Q` 2 or more.
- Load with `preset_load(machine="EQ", preset="Cut — Mud Removal")`.

## Pitfalls

- The gains reach +24 dB per band: `HM Gain`, `HS Gain` and `Output` at their maxima peak above 0 dBFS (up to +15 dBFS in the profile). Lower `Output` as you boost.
- Q and frequencies are steps, not a continuous range: two neighbouring values can differ more than you expect.
- A band at 0 dB does nothing; it is not a bypass for the machine (`Bypass` is).

## Pairs well with

- Pedal Comp after it; Neo Limiter on the master; Neo Filter for sweeps.

## Successor

Neo EQ replaces Pedal EQ: `machine_replace(machine="EQ")` puts the four bands on Neo EQ's bands 2, 3, 4 and 7 with frequency, gain and Q as they are. The match is within 0.2 dB of loudness and 0.5 dB per third octave.

## Checklist

- [ ] `analyze(source="Mix", metrics=["spectrum"])` before and after: the band you moved changed, the others did not.
- [ ] `param_get(machine="EQ")`: gains in dB, frequencies in Hz at the steps you meant.
- [ ] `analyze(source="Mix", metrics=["peaks"])`: no clipping after boosts; lower `Output` if so.
