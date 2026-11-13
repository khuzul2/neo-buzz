## How it sounds

A harmonic exciter. The input is split into four bands; each band goes through an oversampled, anti-aliased shaper (`Mode`: Tape, Tube, Transistor, Diode or Warm) driven by `Drive`; only what the shaper adds, its harmonics, is added back to the untouched audio by `Amount 1` to `Amount 4`. The audio itself never goes through a filter: with every Amount at 0 % the output is the input, delayed (bit for bit).

- Tape adds odd harmonics (3rd, 5th), smooth. Tube is asymmetric: 2nd and 3rd. Transistor is harder (more upper harmonics). Diode is soft and asymmetric. Warm adds even harmonics only (2nd, 4th): body without edge.
- `Drive` (0-36 dB) sets how hard the shaper works: more drive, more and higher harmonics. `Amount` sets how much of them is added.
- `Shelf freq` and `Shelf gain` (+-12 dB) shape the added harmonics only: brighter or duller excitation without touching the audio.
- `Stereo` M/S excites mid and side separately (harmonics of the side widen, of the mid centre). `Auto gain` keeps the loudness within 6 dB of the input. Latency is constant and reported.

## Use it for

- Air on a master, presence on vocals, crack on drums, bass that carries on small speakers.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Mode | Tape | smooth; Tube for presence, Warm for body |
| Drive | 12 dB | more drive, more and higher harmonics |
| Amount 4 | 20% | the band above the third crossover: air |
| Amount 3 | 0% | upper midrange: presence at 15-35 % |
| Crossover 3 | 8 kHz | where the top band begins (the default) |
| Shelf gain | 0 dB | brightens (+) or softens (-) only the added harmonics |
| Auto gain | Off | On keeps a fair comparison |

## Starting points

- "Mastering — Air" (Tape, Drive 12 dB, Amount 4 20 %), "Mastering — Presence and Air", "Mastering — Subtle Warmth" (Warm).
- "Vocal — Bright and Present" (Tube, Drive 14 dB, Amount 3 35 %, Amount 4 25 %), "Drums — Crack and Sizzle" (Transistor).
- "Bass — Add Harmonics to Be Heard on Small Speakers": Warm, Crossover 1 100 Hz, Crossover 2 400 Hz, Crossover 3 2 kHz, Amount 2 about 40 %.
- "Mid/Side — Air on the Sides" (Tape, M/S, Amount 4 35 %); "Old Exciter (Oomek-style)" for songs moved from the old machine.

## Pitfalls

- An exciter adds energy: the master gets brighter and a little louder. Auto gain or Match keeps comparisons fair.
- Too bright: lower `Shelf gain` before lowering Amount.
- Read `added_db` in `machine_readings`: -25 to -18 dB is audible but gentle; a buzz or hiss in `Delta` means too much Drive for the material.
- Only band 4 is on by default.

## Pairs well with

- Neo EQ before it; Neo Limiter after it; Neo Imager to widen what it adds (`guide(topic="mastering-chain")`).

## Successor

Neo Exciter replaces Oomek Exciter: `machine_replace(machine="Exciter")` carries its Drive, cuts and Amount into the matching settings.

## Checklist

- [ ] `machine_readings(machine="Exciter")`: `added_db` between -25 and -18 dB.
- [ ] `analyze(source="master", metrics=["spectrum", "loudness"])`: the added region is brighter, loudness has not jumped more than 1 LU.
- [ ] `param_set(machine="Exciter", changes=[{"param": "Delta", "value": "On"}])`, listen once: harmonics only, no buzz; then switch it Off.
