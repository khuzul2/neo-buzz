## How it sounds

A shaper for the band below a crossover (20–300 Hz, a Linkwitz–Riley 24 low-pass; 120 Hz by default) and nothing else: everything above the crossover is left as it was. The low band goes through, in this order, `Punch` and `Sustain` (the onset and the body of kicks and bass), `Contrast`, `Balance`, mono below a frequency and `Peak control`, then is added back.

- `Punch` / `Sustain` (±12 dB): the gain on a kick's onset and on its tail. On a kick sample, `Punch` +6 dB raises the first peak by 6 dB (±0.5 dB) when the kick sits below the crossover.
- `Contrast` (±100 %): gain from how far the low end is above or below its own slow level (300 ms). Positive keeps the strongest parts and pulls the rest down (a tighter, more focused low end), negative does the reverse (evens it out). The band's energy is kept, so it changes how the bass moves, not how loud it is.
- `Balance` and `Target`: the low band's level against the mid band (everything above the crossover up to 2 kHz), measured over 1 s. `Target` is the difference you want in dB; `Balance` is how much of the distance to it is corrected, at most ±9 dB on the low band, moving slowly. At 0 % nothing moves. Read the difference now in `machine_readings` (`balance_db`).
- `Mono` below `Mono freq`: the low band's side signal is removed below that frequency (a 4th-order high-pass on it, −6 dB at the frequency).
- `Peak control` (`Peak level`): a soft clipper (a 6 dB knee, anti-aliased) on the low band only; the kick's peaks stay under the level without the rest of the mix feeling it.
- `Phase`: Minimum, the Linkwitz–Riley split with no ringing before the sound; or Linear, linear-phase FIRs that keep the band's phase untouched (latency 96 ms at 48 kHz, reported and compensated, so use it on masters and not live).

## Signal flow

Input → split into the low band and the rest. The gain (`Punch`, `Sustain`, `Contrast`, `Balance`) is worked out from the low band as it arrives and applied to the audio `Lookahead` later (minimum phase: the audio is delayed by it, the latency; linear phase: free, the FIR's latency is longer) → mono → peak control → added to the rest → `Delta` → `Output`. At rest (every control neutral) the output is the input, delayed, bit for bit.

- Only the low band changes. The skirt of a 24 dB/octave low-pass lets a little of what is just above the crossover through: a tone one octave up sees about 6 % of the low band's gain, at 8× the crossover about 0.0002 %. The linear option leaves everything above the crossover exactly as the delayed input.
- The minimum-phase bands sum to the input's allpass: levels are untouched, the phase of what is above the crossover moves a little.
- `Delta` plays only what Low End changes. The editor's "Match" sets `Output` from the gain-match reading (one undo step).

## How to use it

- **Tighten a kick that is muddy:** `Crossover` 100–120 Hz, `Punch` +2 … +4 dB, `Sustain` −2 … −3 dB, `Speed` Punchy. Watch `low_crest_in_db` and `low_crest_out_db` (the low band's peak over its RMS): a few dB more crest is what Punch does.
- **A bass that jumps around:** `Contrast` −30 … −50 %.
- **Focus the low end of a master:** `Contrast` +20 … +40 %, then `Balance` toward a `Target` that `target_build` or a reference's tonal curve (`analyze`) says. The mid band is measured on the input, so Balance has no feedback.
- **Mono bass:** `Mono` On, `Mono freq` 90–120 Hz with the crossover a bit above it (150 Hz).
- **Catch peaks only in the bass:** `Peak control` On, `Peak level` a few dB under the master's ceiling; `peak_clip_db` in `machine_readings` says how far the low band went over it.
- **Check:** `analyze` the mix before and after (`stereo.ms_bands`, `tonal`); `Delta` On to hear what is added or taken.

## Gotchas

- `Phase` and `Lookahead` change the latency: set them before playing. Switching `Phase` resets the filters and fades in over 20 ms.
- A crossover at 20–40 Hz in linear phase is only an approximation of the Linkwitz–Riley response (the 8192-tap FIR is 170 ms long: 0.5 dB off at 20 Hz; from 60 Hz up it is within 0.001 dB).
- `Peak control` holds the low band under its level; a tone just under the crossover leaks into the rest and can pass it by a little (about 0.1 dB at 60 Hz with a 300 Hz crossover).
- `Contrast` and `Balance` need sound in the low band; on silence they do nothing.

## Preset map

| Preset | What |
|---|---|
| "Mastering — Tighten the Low End" | crossover 100 Hz, `Punch` +2, `Sustain` −2 dB |
| "Mastering — Focus (Keep the Strongest)" | crossover 120 Hz, `Contrast` +40 % |
| "Mastering — Mono Below 90 Hz" | crossover 150 Hz, mono below 90 Hz |
| "Mastering — Balance to the Mids (+6 dB)" | crossover 150 Hz, `Target` +6 dB, `Balance` 70 % |
| "Mastering — Peak Control (−3 dBFS)" | crossover 150 Hz, peak control at −3 dBFS |
| "Mastering — Linear Phase Split" | linear phase, `Punch` +1 dB, `Contrast` +20 % |
| "Drums — Kick Punch" | crossover 120 Hz, `Punch` +4, `Sustain` −3 dB, Punchy |
| "Drums — Kick Weight" | crossover 120 Hz, `Sustain` +4 dB, Smooth |
| "Bass — Even Out" | crossover 200 Hz, `Contrast` −50 %, `Sustain` +1 dB |
| "Mixing — Clean Up the Sub" | crossover 100 Hz, `Sustain` −3 dB, `Contrast` +30 %, mono below 100 Hz |
