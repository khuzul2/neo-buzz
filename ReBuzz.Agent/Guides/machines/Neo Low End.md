## How it sounds

A shaper for the band below a crossover (20-300 Hz, a Linkwitz-Riley 24 low-pass; 120 Hz by default) and nothing else: everything above it is left as it was. The low band goes through `Punch` and `Sustain` (the onset and the body of kicks and bass), `Contrast`, `Balance`, mono below a frequency and `Peak control`, then is added back.

- `Punch` and `Sustain` (+-12 dB): the gain on a kick's onset and on its tail. `Contrast` (+-100 %): gain from how far the low end is above or below its own slow level (300 ms); positive keeps the strongest parts and pulls the rest down, negative evens it out; the band's energy is kept.
- `Balance` and `Target`: the low band's level against the mid band (above the crossover up to 2 kHz) over 1 s. `Target` is the difference you want; `Balance` is how much of the distance is corrected (at most 9 dB, slowly).
- `Mono` removes the low band's side signal below `Mono freq`. `Peak control` is a soft clipper on the low band only, holding its peaks under `Peak level`.
- `Phase`: Minimum (no ringing before the sound) or Linear (keeps the band's phase untouched; 96 ms of latency at 48 kHz, reported: for masters, not live). `Lookahead` lets Punch already be up at a kick's first peak.
- At rest the output is the input, delayed, bit for bit. `Delta` plays only what Low End changes.

## Use it for

- Tightening a muddy kick, evening out a bass that jumps around, focusing the low end of a master, mono bass, catching bass peaks without touching the rest.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Crossover | 120 Hz | where the low band ends |
| Punch | 2 dB | gain on the onsets; +2 to +4 tightens a kick |
| Sustain | -2 dB | gain on the tail; -2 to -3 dB cleans mud |
| Contrast | 0% | +20 to +40 focuses a master; -30 to -50 evens a jumpy bass |
| Mono freq | 100 Hz | below it the low band is mono (with Mono On) |
| Peak level | -3 dB | where the low band's peaks are held (with Peak control On) |

## Starting points

- "Mastering — Tighten the Low End", "Mastering — Focus (Keep the Strongest)", "Mastering — Mono Below 90 Hz", "Mastering — Balance to the Mids (+6 dB)", "Mastering — Peak Control (−3 dBFS)", "Mastering — Linear Phase Split".
- "Drums — Kick Punch" (Punchy), "Drums — Kick Weight" (Smooth), "Bass — Even Out" (Contrast -50 %), "Mixing — Clean Up the Sub".
- Mono bass: Mono On, Mono freq 90-120 Hz, Crossover a bit above (150 Hz).

## Pitfalls

- `Phase` and `Lookahead` change the latency: set them before playing. Switching Phase fades over 20 ms.
- A crossover at 20-40 Hz in linear phase only approximates the Linkwitz-Riley response (0.5 dB off at 20 Hz; within 0.001 dB from 60 Hz).
- A tone just under the crossover can leak past Peak level by about 0.1 dB.
- Contrast and Balance need sound in the low band; on silence they do nothing.
- Read `low_crest_in_db` and `low_crest_out_db`, `balance_db` and `peak_clip_db` in `machine_readings`: a few dB more crest is what Punch does.

## Pairs well with

- Neo Utility (bass mono), Neo EQ before it, Neo Multiband for low-band dynamics, Neo Limiter at the end (`guide(topic="mastering-chain")`).

## Checklist

- [ ] `machine_readings(machine="Low End")`: `low_crest_out_db` is a few dB over `low_crest_in_db` when Punch is up.
- [ ] `analyze(source="master", metrics=["spectrum", "stereo"])` before and after: the low bands balanced, mono below the chosen frequency.
- [ ] `param_set(machine="Low End", changes=[{"param": "Delta", "value": "On"}])`, listen once: only what you meant to change; then switch it Off.
