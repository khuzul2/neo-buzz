## How it sounds

A noise gate and downward expander, the successor of Pedal Gate. The gate opens when the key's level reaches `Threshold` and closes when it falls `Hysteresis` under it; after the signal falls it stays open for `Hold`, then closes over `Release`. `Attack` and `Release` are linear gain ramps (the time for the whole opening or closing), as in Pedal Gate. A closed gate sits at `Range` (−90 dB is nearly shut, 0 dB does nothing).

- `Ratio` below infinity makes it an expander: under the close threshold every dB of level is (ratio − 1) dB quieter, down to `Range`; at infinity (the top of the knob) the closed gate goes straight to `Range`.
- `Detection` **Peak** follows transients (1 ms attack, 10 ms release); **RMS** averages 10 ms: smoother on sustained sources.
- The key: `Sidechain` Internal (the input) or External (connect a machine to input 2); `Key low cut` and `Key high cut` filter it, so rumble or cymbal spill doesn't open the gate. `Listen` plays the filtered key.
- `Lookahead` (0–25 ms) delays the audio so the gate is open before the transient arrives (latency, reported). `Delta` plays only what the gate takes out. `machine_readings`: `gain_db`, `open`, `gain_match_db`, `latency_samples`.

## How to use it

- **A snare:** `Threshold` −28 dB, `Hysteresis` 8 dB, `Attack` 0.2 ms, `Hold` 60 ms, `Release` 120 ms, `Lookahead` 2 ms, `Key low cut` 150 Hz (the kick's spill). Preset "Drums — Snare, Tight".
- **Toms:** a gentle expander (`Ratio` 4, `Range` −30 dB) is less obvious than a hard gate. Preset "Drums — Toms, Gentle Expander".
- **Between vocal phrases:** `Ratio` 3, `Range` −18 dB, a long `Hold` and `Release`. Preset "Vocals — Soft Gate Between Phrases".
- **Duck with a key:** `Sidechain` External with a kick into input 2 opens a gate on a pad on every kick.
- Set `Threshold` by watching `open` and `gain_db` in `machine_readings`; use `Hysteresis` (6–10 dB) against chatter before lengthening `Hold`.

## Gotchas

- A hard gate on a decaying note chops the tail: use `Range` −18 dB or an expander ratio, or a longer `Hold`.
- With `Lookahead` above 0 the audio is late by it, and the engine compensates; with 0 there is no latency.
- Replacing Pedal Gate with `machine_replace` carries every setting over in its units (Bypass becomes the machine's bypass).

## Preset map

| Preset | What |
|---|---|
| "Drums — Snare, Tight" | −28 dB, hold 60 ms, lookahead 2 ms, key low cut 150 Hz |
| "Drums — Kick, Clean the Spill" | −30 dB, key 40 Hz – 5 kHz, lookahead 1 ms |
| "Drums — Toms, Gentle Expander" | ratio 4, range −30 dB |
| "Vocals — Soft Gate Between Phrases" | ratio 3, range −18 dB, hold 200 ms |
| "Bass — Tighten the Tail" | RMS, −36 dB, hold 40 ms |
| "Noise — Hiss Between Takes" | −55 dB, range −60 dB, key 200 Hz – 8 kHz |
| "Pedal Gate — Same Settings" | Pedal Gate's defaults |
