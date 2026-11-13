## How it sounds

It doesn't. Neo Scope passes its input through bit for bit, with zero latency, and draws it for the person at the screen:

- **Oscilloscope**: the waveform of the left or right channel, mid (L + R)/2, side (L - R)/2, or left and right overlaid, drawn left to right as the lowest and highest value per pixel.
- **Time base**: synced to the song (`Sync` 1/16 to 4 bars: each sweep starts on a multiple of that note value from the song's start, so a loop draws the same picture every pass), or free (`Sync` Free: `Time/div`, ten divisions across, with an edge trigger).
- **Vectorscope**: a goniometer (Lissajous: mid up, side across) or polar display, with a correlation meter.

`Auto scale`, `Gain`, `Trail`, `Freeze` and `Vector` change only what the editor draws, never the readings. For an agent, the picture is available through `plot(kind="waveform", sources=["r1"])` or `plot(kind="goniometer", sources=["r1"])` after a `render`; probes (`render(probes=[...])`) give any machine's output without a scope in the song.

## Use it for

- Watching a signal while it plays: a synth's cycle, a kick's shape, a sidechain's duck, the stereo image, mono compatibility.
- Cheap level and correlation readings on any track or the master.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Sync | Free | the trigger works in free time only; 1/4 or 1 bar for a loop that stands still |
| Time/div | 2 ms | ten divisions: 20 ms, a few cycles from 100 Hz up |
| Trigger | Rising | each sweep starts on a rising crossing of Level |
| Level | 0% | the zero crossing: every waveform crosses it |
| Display | Mid | one trace, the sum of both channels |
| Auto scale | On | the display fits the signal; Off uses Gain |

Readings (`machine_readings`): `peak_dbfs` and `rms_db` (last 3 s, a full-scale sine reads -3 dB), `correlation` (+1 mono, -1 opposed) and `trigger_locked` (1 while sweeps start on the trigger; 0 with a trigger set means the Level is outside the signal). Silence reads -inf; values stay after the transport stops.

## Starting points

- "Synced — One Beat" (kicks, transients, a sidechain's duck), "Synced — One Bar (Mid)" (the groove's shape), "Synced — Four Bars (Overview)".
- "Free — Waveform (Triggered)" (2 ms a division from a rising zero crossing), "Free — Low End (Triggered, Holdoff)" (10 ms, 5 ms holdoff).
- "Stereo — Side and Polar", "Levels — Fixed Gain (0 dB)".

## Pitfalls

- Sweeps follow the song's position: while the song stands still, or plays a pattern alone, a synced scope runs free at the tempo, unaligned.
- A bar is 4 beats here; in another meter choose beats or a length the loop is a multiple of.
- The trigger watches the first trace (left in L & R) with a small hysteresis (0.2 % of full scale).
- The reading window is 3 s, tail of a render included: keep tails short.
- Looking costs about 0.1 % of a core; the editor, while open, a little more.

## Pairs well with

- Neo Analyzer for loudness and spectrum, Neo Imager and Neo Utility for width problems the vector display shows.

## Checklist

- [ ] `machine_add(gear="Neo Scope", name="Scope", insert_after="Bass")`, then `preset_load(machine="Scope", preset="Free — Waveform (Triggered)")`.
- [ ] `machine_readings(machine="Scope")`: `trigger_locked` is 1 with a trigger set; `peak_dbfs` below 0.
- [ ] `plot(kind="waveform", sources=["r1"])` after a `render`, for the agent's own look.
