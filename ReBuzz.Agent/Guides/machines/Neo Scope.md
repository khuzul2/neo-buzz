## How it sounds

It doesn't. Neo Scope passes its input through bit for bit, with zero latency, and draws it for the person at the screen:

- **Oscilloscope**: the waveform of the left or right channel, mid (L + R)/2, side (L − R)/2, or left and right overlaid. Each sweep is drawn left to right as its lowest and highest value per pixel.
- **Time base**: synced to the song (`Sync` 1/16 … 4 bars: each sweep starts on a multiple of that note value counted from the song's start, so a loop draws the same picture on every pass), or free (`Sync` Free: `Time/div`, ten divisions across, with an edge trigger).
- **Vectorscope**: a goniometer (Lissajous: mid up, side across) or a polar display (each sample at its angle and level, folded into the upper half), with a correlation meter.

It is for people watching a signal while it plays. You look at audio with `render`, then `plot(kind="waveform", sources=["r1"])` or `plot(kind="goniometer", sources=["r1"])`, and measure it with `analyze`; probes (`render(probes=[…])`) give you any machine's output without a scope in the song.

## Signal flow

Input → output, unchanged. On the side:

- The trace the display chooses → sweeps: synced to the song's ticks (a sweep's columns are positions in the bar, not samples), or free with the trigger (Rising or Falling through `Level`, re-armed `Holdoff` after each sweep; with no crossing for two sweeps' time, at least 100 ms, it runs free and says Auto) → up to 1024 columns of lowest and highest values → the editor, at most 60 times a second.
- The last 1024 samples of both channels → the vectorscope.
- Both channels in 100 ms steps → the peak, RMS and correlation over 3 s (the readings), and the correlation over 400 ms (the meter's bar). The correlation is the analysis library's (`analyze`'s stereo measures).

`Auto scale`, `Gain`, `Trail`, `Freeze` and `Vector` change only what the editor draws, never the readings.

## How to use it

- Put it after what the user wants to watch (`machine_add(gear="Neo Scope", name="Scope", insert_after="Bass")`), or on the master, and leave it: looking costs about 0.1 % of a core (its editor, while open, a little more).
- Its readings (machine_readings in studio mode) are four numbers:
  - `peak_dbfs`: the highest sample of either channel over the last 3 s;
  - `rms_db`: the RMS of both channels over the last 3 s (a full-scale sine reads −3 dB, as `analyze` reads it);
  - `correlation`: +1 mono, 0 unrelated, −1 opposed, over the last 3 s;
  - `trigger_locked`: 1 while the sweeps start on the trigger (Sync Free with a trigger, and the last sweep found its crossing), else 0. If the user says the picture won't stand still, read it: 0 with a trigger set means the level is outside the signal (move `Level` inside it) or the sweeps are much shorter than the signal's period.

  Silence reads −∞. The values stay when the transport stops, so after a render they describe its last 3 s.
- To show the user a synth's waveform standing still:

| Parameter | Value | Why |
|---|---|---|
| Sync | Free | the trigger works in free time only |
| Time/div | 2 ms | ten divisions: 20 ms, a few cycles from 100 Hz up |
| Trigger | Rising | each sweep starts on a rising crossing |
| Level | 0% | the zero crossing: every waveform crosses it |
| Display | Mid | one trace, both channels' sum |

- To show a beat or a bar standing still while the loop plays: `Sync` 1/4 or 1 bar. A bar is 4 beats here (machines don't see the song's bar list); in another meter, choose beats (1/4, 1/2) or a length the loop is a multiple of.

## Gotchas

- The sweeps follow the song's position. While the song stands still (or plays a pattern alone, whose position the song doesn't move) a synced scope runs free at the tempo, unaligned.
- The trigger looks at the first trace (left in L & R). It has a small hysteresis (0.2 % of full scale): noise below that around the level doesn't trigger.
- The readings window is 3 s, including a render's tail: keep tails short when reading them after a render.

## Preset map

| Preset | What |
|---|---|
| Synced — One Beat | both channels, a beat wide: kicks, transients, a sidechain's duck |
| Synced — One Bar (Mid) | a bar of the mid signal: the groove's shape |
| Synced — Four Bars (Overview) | the envelope of a phrase |
| Free — Waveform (Triggered) | 2 ms a division from a rising zero crossing: a synth's cycle |
| Free — Low End (Triggered, Holdoff) | 10 ms a division, 5 ms holdoff: bass and kick cycles |
| Stereo — Side and Polar | the side signal over a bar and the polar vectorscope |
| Levels — Fixed Gain (0 dB) | no auto scale: full scale fills the screen |
