## How it sounds

A four-band compressor. Steep crossovers (24 dB per octave) split the sound into low, low-mid, high-mid and high bands, each with its own threshold, ratio, attack, release, makeup and bypass; the knee and the detection (peak or RMS) are shared. The bands add back up flat. `Dry-Wet` blends the original back for parallel compression. Two options add delay: `Lookahead` (off to 10 ms) lets the compressors act before a peak arrives, and `Phase Linear` keeps the bands in phase at the crossovers for a few milliseconds of delay.

## Use it for

- Mastering and mix-bus control, taming a boomy low end or a harsh top without touching the rest, drum buses, and taming a vocal band by band.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Xover L-LM | 120 Hz | top of the low band; 40-600 Hz |
| Xover LM-HM | 1 kHz | between body and presence; 200 Hz-3 kHz |
| Xover HM-H | 5 kHz | bottom of the top band; 800 Hz-12 kHz |
| L Threshold | -18 dB | each band has Threshold, Ratio, Attack, Release, Makeup, Bypass |
| L Ratio | 3:1 | 1:1 to 40:1; the top step is a limiter |
| L Release | 150 ms | bass wants longer releases than treble |
| Knee | 6 dB | shared by all bands |
| Dry-Wet | 100% | lower for parallel compression |

## Starting points

- Its own presets (`preset_list(machine="MComp")`, 20): "Mastering - Transparent", "Mastering - Glue", "Mastering - Punch", "Mix Bus - Tighten", "Drum Bus - Punch", "Vocal - De-Ess", "Bass - Tighten", "FX - Squash".
- A gentle master: ratio 2:1 on all four bands, thresholds -18 dB, attack 30 ms, release 150 ms, Makeup 1 dB.
- To hear a band alone, set `Listen` to L, LM, HM or H; set it back to All.

## Pitfalls

- Each band's `Makeup` and `Output Gain` reach +24 dB: a band's Makeup at its maximum peaks above 0 dBFS (up to +15 dBFS in the profile). Watch the output.
- `Lookahead` and `Phase Linear` delay the output (up to 10 ms and a few ms): a parallel path around the machine is out of time.
- 35 parameters: set the shared ones (`Knee`, `Detection`, the crossovers) first, then each band.
- `Spectrum View` only draws the analyser in the machine's window.

## Pairs well with

- Pedal EQ before it; Neo Limiter after it on the master.

## Successor

No `machine_replace` pair. Neo Multiband (3 to 4 bands, with an upward expander) is the modern alternative: set it up by ear.

## Checklist

- [ ] `analyze(source="Master", metrics=["dynamics"])`: the loudest parts are down by the amount you planned in the band you meant.
- [ ] `analyze(source="Master", metrics=["spectrum"])`: the bands stay in balance; no hole at a crossover.
- [ ] `param_get(machine="MComp")`: ratios in :1, times in ms, crossovers in Hz.
