## How it sounds

A clean feed-forward compressor with a soft knee, peak or RMS detection and an optional lookahead. It adds little colour: at the defaults the level changes by under 0.5 dB. `Wet/Dry` below 100 % mixes the original back in, for parallel compression. Its own value texts for Attack, Release, Lookahead and Makeup Gain are wrong; the manual and `param_set` use the real values (the raw number is the milliseconds or decibels).

## Use it for

- Gluing a bus, levelling vocals and leads, taming peaks, and parallel compression on drums.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Threshold | -18 dB | where compression starts; lower compresses more |
| Ratio | 4:1 | 2:1 gentle; 8:1 and above flattens peaks |
| Knee | 6 dB | 0 is hard; a wider knee is less noticeable |
| Attack | 10 ms | slow lets transients through; 1 ms softens them |
| Release | 100 ms | short pumps with the beat; 300 ms or more glides |
| Makeup Gain | 3 dB | or switch on `Auto Makeup` |
| Detection | RMS | Peak for transients, RMS for full mixes |

## Starting points

- Presets: "Glue — Gentle Bus", "Vocal — Even Level", "Drums — Punch", "Bass — Tight", "Parallel — New York", "Limit — Peak Catcher".
- A mix-bus glue: Threshold -18 dB, Ratio 2:1, Attack 30 ms, Release 200 ms, Knee 6 dB.
- Load with `preset_load(machine="Comp", preset="Drums — Punch")`.

## Pitfalls

- `Lookahead` delays the output by its time (up to 20 ms): a parallel path around the machine will be out of time.
- `Auto Makeup` replaces `Makeup Gain` (up to +24 dB); read the level before trusting it.
- It has no sidechain input: ducking needs Neo Comp.

## Pairs well with

- Neo EQ before it to clean the lows; Neo Limiter after it on the master; Pedal Gate before it on drums.

## Successor

Neo Comp replaces Pedal Comp: `machine_replace(machine="Comp")` carries threshold, ratio, knee, attack, release, makeup, mix and lookahead over, in Neo Comp's Clean character. The match is within 1 dB of loudness and 2 dB per third octave; Neo Comp's Auto release is off after the swap, which is about 1.3 dB quieter than Pedal Comp's program-dependent release.

## Checklist

- [ ] `analyze(source="Bus", metrics=["dynamics"])`: crest factor down, the loudest parts down by the amount you planned.
- [ ] `param_get(machine="Comp")`: `Attack`, `Release` and `Makeup Gain` read in ms and dB, not the machine's own texts.
- [ ] `render` with the machine bypassed (`machine_set(machine="Comp", bypass=true)`) and `compare` for the level match.
