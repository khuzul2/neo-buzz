## How it sounds

A transient shaper: two followers of the level find the onset of every hit and its body, and `Attack` and `Sustain` (+-12 dB each) set the gain on each. Attack up makes drums snap without louder tails; Attack down pushes them back; Sustain up brings the room and the tails up; Sustain down tightens. It works on how a sound moves, not on its level, so it keeps working when the loudness changes.

- A steady sound has no onset: Attack never changes a held tone. Attack and Sustain at 0 dB pass the audio bit for bit (delayed by the lookahead).
- `Lookahead` (3 ms by default) delays the audio so the gain is up at a hit's first sample. At 0 ms there is no latency and the first millisecond of a hit gets less of the gain: use 0 live, 3 ms for mixing and mastering.
- `Speed`: Punchy finds onsets from 0.1 ms and lets go fast (clicks, hats), Balanced is the default, Smooth is slower (bass, long hits).
- Impact mode (`Mode` Impact) replaces the two gains with four bands (`Crossover 1` to `Crossover 3`): each band's microdynamics are moved by one knob, +100 % doubling the distance (in dB) between the level now and the level around it, -100 % taking it out, 0 leaving it. `Auto gain` keeps each band's energy, so Impact changes dynamics, not loudness. All four knobs at 0 pass the audio unchanged.
- `Stereo` M/S shapes mid and side with their own detectors. `Mix` blends the dry signal (delayed to match).

## Use it for

- Snap or weight on a snare or drum bus, less room, restoring punch after limiting, taming harsh hits, gentle punch on a master.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Attack | 4 dB | +4 to +6 for more crack; raise until the hits stand out, then back off 1 dB |
| Sustain | -2 dB | negative tightens; +3 to +5 brings up the tails |
| Speed | Punchy | Smooth for bass and long hits |
| Lookahead | 3 ms | set before playing; 0 for live |
| Mode | Attack_Sustain | Impact for four bands |
| Mix | 100% | lower for a subtler effect |

## Starting points

- "Drums — More Snap" (Attack +6, Sustain -2 dB, Punchy), "Drums — Tighter (Less Room)", "Drums — Bigger Tails", "Drums — Hit Harder, Keep the Tail".
- "Mastering — Gentle Punch" (Attack +1.5 dB, Smooth), "Mastering — Soften the Hits", "Mastering — Impact (Lows Up, Highs Calm)", "Mastering — Impact (Tighten the Mids)", "Mastering — Impact (Flatten the Dynamics a Little)".
- "Bus — Glue the Sustain", "Mid/Side — Punch the Mid", "Live — No Latency".

## Pitfalls

- Clicks or a buzz in `Delta` (output minus input) mean too much Attack for the Speed: try Smooth.
- Changing Lookahead while playing moves the audio (a splice): set it first.
- In Impact mode Attack and Sustain are not used; the lookahead still delays the audio so the latency stays the same when you switch modes. Mode switches crossfade over 20 ms.
- Impact's band 1 follows slower than the others; Punchy speeds all bands up.
- Read `machine_readings`: `gain_max_db` and `gain_min_db` (largest and smallest gain in the last 3 s: a large `gain_max_db` with a small `transient_percent` means few hits do all the work), `gain_match_db`, `latency_samples`.
- To compare fairly after 3 s of sound, set Output to the negative of `gain_match_db` (`param_set(machine="M Transient", changes=[{"param": "Output", "value": "-1.2 dB"}])`), or use the editor's Match.

## Pairs well with

- Neo Comp (a slow attack keeps what Transient adds), Neo Gate for tails, Neo Limiter after it on a master, Neo Low End for the bass band.

## Checklist

- [ ] `machine_readings(machine="Transient")`: `gain_max_db` close to the Attack you set, `transient_percent` a sensible share.
- [ ] `analyze(source="Drums", metrics=["microdynamics", "loudness"])`: microdynamics moved the way you meant, loudness within about 1 LU.
- [ ] `param_set(machine="Transient", changes=[{"param": "Delta", "value": "On"}])`, listen once for clicks; then switch it Off.
