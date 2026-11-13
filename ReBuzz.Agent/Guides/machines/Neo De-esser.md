## How it sounds

A de-esser: a band-pass detector on the sibilance band (`Frequency`, `Width` octaves wide) reads how hard the "s" and "t" sounds are, and the machine takes that band down when it passes `Threshold`, by (1 - 1/`Ratio`) of the excess, at most `Max reduction`, with `Attack` and `Release`.

- `Detector` Relative reads the band against the whole signal, so it follows a singer who sings louder or softer (Threshold -15 dB: the band within 15 dB of the whole counts as sibilance). Absolute reads the band in dBFS: for cymbals, hats and bright masters.
- `Action` Split takes only the band down (minimum phase, no latency of its own; the rest of the spectrum is left bit for bit). Split linear does it with a linear-phase band (255 samples, constant). Shelf and Bell act on the whole signal as a dynamic high shelf or peak filter at `Frequency`: gentler on a dense mix.
- `Lookahead` (0-5 ms) delays the audio so the reduction is in place when the sibilant arrives (reported). With nothing to reduce the output is the input, delayed, bit for bit.
- `Listen` plays the band the detector hears; `Delta` plays only what is taken out.

## Use it for

- Vocals (the main job), hats and cymbals on a drum bus, taming a bright master.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Detector | Relative | follows the singer; Absolute for hats and masters |
| Frequency | 6.5 kHz | female 7-8 kHz, male 5-6 kHz: find it with Listen |
| Width | 1.5 oct | wide enough for the whole hiss, narrow enough to spare the voice |
| Threshold | -15 dB | Relative: how far under the whole signal counts as sibilance |
| Ratio | 4:1 | 3-4 is natural; higher lisps |
| Max reduction | 6 dB | the most it takes out; lower it first if it lisps |
| Action | Split | only the band is reduced; Split linear for no phase shift |

## Starting points

- "Vocal — Gentle De-ess" (Relative, 6.5 kHz, ratio 3, up to 6 dB), "Vocal — Female, Strong" (7.5 kHz, ratio 6, up to 12 dB), "Vocal — Male, Warm Shelf" (5.5 kHz, dynamic high shelf), "Vocal — Transparent (Linear Phase Split)".
- A bright master: Absolute, 9 kHz, 2 octaves, Bell, 4 dB: "Mastering — Tame a Bright Master".
- Hats and cymbals: Absolute, 8.5 kHz, fast Attack: "Drums — Harsh Hats and Cymbals".
- Finding the frequency: "Find the Sibilance (Listen)", sweep Frequency until the harsh sound is loudest, then switch Listen off.

## Pitfalls

- It works on the level of the band, not the word: a high Ratio with a low Threshold lisps. Lower Max reduction first.
- Shelf and Bell change the whole top end while they reduce; Split changes only the band. If "s" sounds go dull, use Split.
- Latency is the lookahead (plus 255 samples in Split linear): Lookahead 0 with Split is zero latency for live use.
- Read `reduction_db` in `machine_readings` on the loud passages: 3-6 dB is about right, more than 10 means the threshold is too low.

## Pairs well with

- Neo Comp before it to level the vocal; Neo EQ after it for tone; Neo Analyzer to check the 4-10 kHz bands (`presence_db`, `air_db`).

## Checklist

- [ ] `machine_readings(machine="DeEss")`: `reduction_db` is 3-6 dB on the loud passages, not more than 10.
- [ ] `Delta` on (`param_set(machine="DeEss", changes=[{"param": "Delta", "value": "On"}])`) contains hiss, not the body of the voice.
- [ ] `analyze(source="Vocal", metrics=["spectrum"])`: the 4-10 kHz region is no longer spiky.
