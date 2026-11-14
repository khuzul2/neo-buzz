## How it sounds

A resonance suppressor that works on a short-time spectrum: every frame (2048 samples at 44.1 and 48 kHz) each frequency is compared with a reference, and where it stands out the machine turns that narrow region down for as long as it stands out. A ringing note, a boxy low-mid bump or a harsh edge on a voice is smoothed without a fixed EQ cut that would also dull the moments when it isn't there.

- `Mode` Suppress compares each frequency with the spectrum around it (`Selectivity` octaves either side): it catches narrow peaks. Balance moves the whole spectrum toward a pink slope (-3 dB per octave), with `Tilt` added above 300 Hz: it evens out a tonal tilt, broadly. Stabilize compares the short-term spectrum with its own long-term average (4 s): it pulls down moments that jump out, for instance a sudden loud passage or a note that rings.
- `Depth` is how much of what stands out is taken out (100 % brings it to the reference); `Knee` is how far over the reference a frequency must stand before it is touched at all.
- `Smoothing` spreads each cut over neighbouring bins (0 is the most surgical); `Lift` lets it also raise dips by up to that many dB (0: cut only).
- `Attack` and `Release` are the speed of each cut (both faster in the highs); `Low limit` and `High limit` keep it to a frequency range.
- `Mix` blends in the dry (delayed to match); `Delta` plays only what is taken out. With nothing to take out the output is the input, delayed, within -138 dB; at `Mix` 0 % it is the input bit for bit.

## Use it for

- A vocal with a harsh or nasal region, a guitar or a snare that rings on one note, a mix bus or a master with a resonance that comes and goes.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Mode | Suppress | narrow resonances; Balance and Stabilize act broadly |
| Depth | 40 % | gentle; 100 % flattens a resonance completely and can sound hollow |
| Knee | 4 dB | a low knee also touches ordinary variation |
| Selectivity | 0.4 oct | the width of the neighbourhood: lower for needle-thin peaks |
| Smoothing | 2 | 0 is the narrowest notch, 6 the broadest |
| Release | 150 ms | longer is smoother, shorter follows the signal |
| Low limit | 150 Hz | keep the body of the sound out of it |
| High limit | 12000 Hz | keep the air out of it |

## Starting points

- "Vocal — Gentle Resonance Control" (Suppress, 40 %, 150 Hz - 12 kHz), "Vocal — Tame Harshness (2 - 8 kHz)" (70 %, only 2 - 8 kHz), "Vocal — Even Tone (Fills Dips)" (Lift 3 dB).
- "Guitar — Tame Boxiness" (150 - 900 Hz), "Drums — Ringing Toms and Snare" (fast Attack and Release).
- "Mix Bus — Subtle Smoothing" (30 %), "Master — Gentle Balance", "Master — Brighter Balance", "Master — Warmer Balance" (Balance with Tilt), "Mix Bus — Stabilize the Tone".
- "Hear What It Removes (Delta)": Suppress at 70 % with `Delta` on, to hear that it takes resonance and not the body of the sound.

## Pitfalls

- High `Depth` with a low `Knee` also cuts ordinary variation and hollows the sound: raise the knee first.
- Latency is one frame (2047 samples at 44.1 and 48 kHz, 4095 at 88.2 kHz and above), reported to the host: it is for mixing and mastering, not for playing live through.
- A click or a sharp transient gets a faint spread of the cut in the frame before it (about -38 dB against the click at Depth 100 %, below the music's own level): at very high `Depth` on a drum bus use `Attack` short and `Smoothing` low.
- Balance changes the tone of the whole signal: keep `Depth` low (20 - 35 %) and compare with `Mix`.
- Read `reduction_db` and `mean_reduction_db` in `machine_readings`: 3 - 6 dB of deepest cut is about right, a mean over 1 dB means it is doing too much.

## Pairs well with

- Neo EQ after it for tone; Neo De-esser for sibilance only; Neo Analyzer to see the region it works on (`presence_db`, `air_db`).

## Checklist

- [ ] `machine_readings(machine="Spectral")`: `reduction_db` is 3 - 6 dB on the worst passages and `mean_reduction_db` under 1 dB.
- [ ] `Delta` on (`param_set(machine="Spectral", changes=[{"param": "Delta", "value": "On"}])`) contains resonance, not the body of the sound.
- [ ] `analyze(source="Vocal", metrics=["spectrum"])`: the region that was spiky is smoother.
