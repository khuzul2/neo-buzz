## How it sounds

A transient shaper: two followers of the level find the onset of every hit and its body, and `Attack` and `Sustain` (±12 dB each) set the gain on each. `Attack` up makes drums snap and hit harder without louder tails; `Attack` down pushes them back; `Sustain` up brings the room, the tail and the sustain of bass up; `Sustain` down tightens. It works on how a sound moves, not on its level, so it keeps working when the loudness changes.

- A steady sound has no onset: `Attack` never changes a held tone (within 0.01 dB), `Sustain` changes it as the gain says.
- `Attack` and `Sustain` at 0 dB pass the audio bit for bit (delayed by the lookahead).
- `Lookahead` (3 ms by default) delays the audio so the gain is already up at a hit's first sample: with it, a hit's first 5 ms peak moves by exactly the `Attack` gain (±0.5 dB). At 0 ms there is no latency and the first millisecond of a hit gets less of the gain. Use 0 live, 3 ms for mixing and mastering.
- `Speed`: Punchy finds onsets from 0.1 ms and lets go fast (clicks, hats), Balanced is the default, Smooth is slower (bass, long hits).

## Impact mode

`Mode` Impact replaces the two gains with four bands (`Crossover 1`–`3`; zero-latency Linkwitz–Riley crossovers that sum flat). Each band's microdynamics, its short-term level against its long-term level, is moved by one knob: +100 % doubles the distance (in dB) of the level now from the level around it, −100 % takes it out (the band follows its slow level), 0 leaves the band alone. `Auto gain` keeps each band's energy what it was, so Impact changes dynamics, not loudness. All four knobs at 0 pass the audio unchanged.

## Signal flow

Input → (`Stereo` M/S: mid and side, each with its own detector and gain) → followers → gain on the audio `Lookahead` later (or the four bands) → `Mix` with the dry signal (delayed to match) → `Delta` → `Output`.

- The latency is the lookahead, reported to NeoBuzz Live and compensated.
- `Delta` plays only what the shaper changes (output minus input). `Output` comes after it. The editor's "Match" sets `Output` from the gain-match reading (after 3 s of sound, one undo step); an agent does the same with `param_set(machine="M Transient", changes=[{"param": "Output", "value": "-1.2 dB"}])`, the value the negative of `gain_match_db`.

## How to use it

- **Snare or drums bus, more crack:** `Attack` +4 … +6 dB, `Sustain` 0 to −2 dB, `Speed` Punchy. Raise until the hits stand out, then back off 1 dB.
- **Too much room in a drum bus:** `Sustain` −4 … −6 dB, `Attack` +1 … +2 dB.
- **On a master:** small moves. "Mastering — Gentle Punch" (`Attack` +1.5 dB, Smooth) or Impact with one or two bands at ±20–30 %. Check with `analyze` that the microdynamics move the way you meant (`microdynamics` per band) and that the loudness didn't (`gain_match_db`).
- **Hear what it does:** `Delta` On. Clicks or a buzz in the delta mean too much `Attack` for the `Speed`; try Smooth.
- `machine_readings`: `gain_max_db` and `gain_min_db` (the largest and smallest gain it applied in the last 3 s: a large `gain_max_db` with a small `transient_percent` means a few hits doing all the work), `transient_percent` (the share of the time the audio was in an onset), `gain_match_db`, `latency_samples`.

## Gotchas

- Changing `Lookahead` while playing moves the audio (a splice): set it before playing.
- `Stereo` M/S shapes mid and side on their own: a hit that is only in the side gets the gain only there.
- Impact mode doesn't use `Attack`, `Sustain` or `Lookahead`'s gain lead; the lookahead still delays the audio so the latency stays the same when you switch modes.
- Impact's band 1 follows slower than the others (a power follower of a 50 Hz band needs its time): `Speed` Punchy speeds all bands up.
- Mode and Impact switches crossfade over 20 ms.

## Preset map

| Preset | What |
|---|---|
| "Drums — More Snap" | `Attack` +6, `Sustain` −2 dB, Punchy |
| "Drums — Tighter (Less Room)" | `Attack` +2, `Sustain` −6 dB |
| "Drums — Bigger Tails" | `Sustain` +5 dB, Smooth |
| "Drums — Hit Harder, Keep the Tail" | `Attack` +8 dB, Punchy |
| "Mastering — Gentle Punch" | `Attack` +1.5 dB, Smooth |
| "Mastering — Soften the Hits" | `Attack` −2, `Sustain` +1 dB, Smooth |
| "Mastering — Impact (Lows Up, Highs Calm)" | Impact mode: +30 %, +15 %, 0, −20 % |
| "Mastering — Impact (Tighten the Mids)" | Impact mode: 0, +25 %, +25 %, 0 |
| "Mastering — Impact (Flatten the Dynamics a Little)" | Impact mode: −25 % on every band |
| "Bus — Glue the Sustain" | `Attack` −1, `Sustain` +3 dB, `Mix` 70 % |
| "Mid/Side — Punch the Mid" | M/S, `Attack` +4 dB |
| "Live — No Latency" | `Lookahead` 0, `Attack` +3, `Sustain` −1 dB |
