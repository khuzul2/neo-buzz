# Neo EQ in depth

The parts of Neo EQ that come after the everyday bands: the 31-band graphic mode, linear phase with the curve layer, and the map of its factory presets. The everyday bands, channels, shapes and dynamic bands are on its page (`guide(machine="Neo EQ")`).

## Neo EQ: graphic mode

`Mode` = Graphic: 31 sliders at the ISO third-octave centres, `G 20 Hz`, `G 25 Hz`, `G 31.5 Hz` … `G 1 kHz`, `G 1.25 kHz` … `G 16 kHz`, `G 20 kHz`, each −18 to +18 dB in 0.1 dB steps. `Output` still applies.

- **The response passes through the sliders.** The bands overlap, so their real gains are solved for: at every centre the response is within 0.2 dB of its slider, even with neighbours 36 dB apart. All sliders at +X is exactly +X.
- **A slider is about a third of an octave wide**: one slider at +6 dB leaves its neighbours' centres at 0 dB. For a wider move, set the neighbours too, in steps.
- **Below 20 Hz and above 20 kHz** the response settles at the sliders' median (the median is applied as a plain gain), not at 0 dB.
- Constant-Q, minimum phase, zero latency; the 16 and 20 kHz bands keep their shape at 44.1 and 48 kHz.
- It succeeds Joachims Hyperion: `machine_replace` on a Hyperion reads Hyperion's curve (its 30 bands overlap and add) at these 31 centres, with the rest in `Output`.
- Use it for a curve given band by band (matching a target spectrum, a loudness contour, a telephone or radio band) or to follow `analyze`'s third-octave bands one for one. For a single precise move (a resonance, a notch, a shelf with a set Q), the parametric bands are better.

| Parameter | Value | Why |
|---|---|---|
| Mode | Graphic | |
| G 63 Hz | +2 dB | warmth |
| G 80 Hz | +2 dB | |
| G 315 Hz | -2 dB | boxiness |
| G 400 Hz | -2.5 dB | |
| G 500 Hz | -1.5 dB | |
| G 4 kHz | +1.5 dB | presence |
| G 12.5 kHz | +2 dB | air |
| G 16 kHz | +2.5 dB | |

`param_set` takes them as any other: `{"param": "G 2.5 kHz", "value": "-3 dB"}`.

## Neo EQ: preset map

| Preset | What |
|---|---|
| "Mixing — Vocal Clarity" | 90 Hz high-pass, a small mud cut, +2 dB at 3 kHz and on the top shelf |
| "Mixing — Kick Punch" | +3 dB at 60 Hz, −4 dB box cut at 350 Hz, +3 dB click |
| "Mixing — Bass Tighten" | 35 Hz high-pass, a low-mid cut, +2 dB growl at 800 Hz |
| "Mixing — Snare Crack" | +2 dB body at 200 Hz, −3 dB box at 500 Hz, +3 dB crack at 5 kHz |
| "Mixing — Acoustic Guitar" | 80 Hz high-pass, −2 dB boom at 200 Hz, a top-shelf lift |
| "Mixing — De-mud" | −3 dB at 300 Hz |
| "Mixing — Telephone" | 400 Hz to 3 kHz at 48 dB/oct, mids lifted: an effect |
| "Mastering — Gentle Smile" | +1 dB at 80 Hz, −0.7 dB at 300 Hz, +1.5 dB shelf at 12 kHz |
| "Mastering — Air Shelf" | +2 dB shelf at 12 kHz |
| "Mastering — Tame Harshness" | −1.5 dB at 3.5 kHz, −1 dB at 7 kHz |
| "Mastering — Low Cut 25 Hz" | the master's high-pass, 48 dB/oct |
| "Fix — Rumble Filter" | a 40 Hz high-pass |
| "Fix — Hum Notch 50 Hz" | notches at 50, 100 and 150 Hz |
| "Fix — Hum Notch 60 Hz" | notches at 60, 120 and 180 Hz |
| "Graphic — Flat" | graphic mode, every slider at 0 dB |
| "Graphic — Loudness (Smile)" | +6 dB lows and +4 dB highs, tapering to 0 by 200 Hz and 4 kHz |
| "Graphic — Telephone" | 315 Hz to 2.5 kHz, a little lift at 2 kHz, −18 dB outside: an effect |
| "Graphic — Air" | rising from +0.5 dB at 5 kHz to +5 dB at 20 kHz |
| "Graphic — Low Cut" | −18 dB at 20 and 25 Hz, back to 0 by 100 Hz |
| "Graphic — Mid Scoop" | down to −5 dB at 630 Hz |
| "Graphic — Vocal Presence" | a little low-mid cut, +3 dB at 3.15 kHz |
| "Dynamic — De-ess" | a dynamic bell down at 6.5 kHz (the de-esser table in Neo EQ's page) |
| "Dynamic — Tame Low Mids" | a dynamic bell down at 300 Hz, 6 dB at most: boom only when it builds up |
| "Dynamic — Resonance Control (Mid)" | a narrow dynamic bell down at 2.5 kHz on the mid |
| "Dynamic — Bass Ducks the Kick (Sidechain)" | a low shelf at 120 Hz down 9 dB at most while input 2 (the kick) hits |
| "Dynamic — Click on Hits (Up)" | a bell at 4 kHz up 4 dB at most on hits |
| "Mastering — Mono Bass (Side Cut 100 Hz)" | a 24 dB/oct low cut at 100 Hz on the side only |
| "Mastering — Side Air" | +2 dB shelf at 10 kHz on the side only |
| "Character — Vintage Low Boost" | +4 dB at 60 Hz with the dip above it |
| "Character — Baxandall Tilt" | +2 dB Baxandall shelves at 250 Hz and 4 kHz |
| "Character — Proportional Presence" | +3 dB at 1.5 kHz, proportional Q |
| "Fix — Phase Turn (All-pass 200 Hz)" | an all-pass at 200 Hz, Q 0.7 |

## Neo EQ: linear phase and the curve layer

`Phase` Linear runs the same bands as one linear-phase filter: the same dB at every frequency (within 0.05 dB of the minimum-phase design from 30 Hz), no phase turning, so a cut or boost does not smear the transients or shift the low end against the highs. The price is a constant delay of 4607 samples at 44.1/48 kHz (4095 for the filter's centre and 512 for one block of buffering), which the machine reports to the host, and pre-ringing: a narrow, strong band rings before the sound as well as after it (`pre_ringing_db` in machine_readings is the share of the filter's energy before its centre; a bell of +12 dB at 1 kHz with Q 4 is about −17 dB). Use it on a bus or a master, where the delay is compensated; on one track in a mix with others, minimum phase is simpler.

- Linear phase works in `Parametric` mode only; `Graphic` mode stays minimum phase with no delay.
- Dynamic bands are static in linear phase (their setting).
- A very narrow band at a low frequency would ring for longer than the filter is, so its Q is limited to π × frequency × 17 ms (1.6 at 30 Hz, 5 at 100 Hz, none above 1.2 kHz); the editor says which bands, and `q_capped_bands` reports how many.
- The response follows the bands within about a tenth of a second of a change (a new filter is designed in small steps, then crossfaded in; nothing is heard as a click).
- `Curve 1` … `Curve 32`: a layer of 32 points, log-spaced from 20 Hz to 20 kHz, ±24 dB, joined by a smooth curve and added to the bands before the filter is designed. It is for matching a curve (set the points from a measured difference, the way `eq_fit` reads one) or for broad tilts that would take many bands. It acts in linear phase only; all 0 dB is flat.
- With every band flat and the curve flat the output equals the input delayed, bit for bit.

## Checklist

- [ ] The mode is the one you mean: `param_get(machine=…, params=["Mode"])` reads Parametric or Graphic, and a preset load sets it too.
- [ ] A graphic curve reads back at its sliders: `param_get(machine=…)` on the `G …` values, and `analyze(source=…, metrics=["spectrum"])` shows the move.
- [ ] Linear phase is on only where its delay is acceptable: `machine_describe(machine=…)` shows `Phase`, and the latency in `studio_status` or `machine_list` is the one you expect.
- [ ] A preset from the map was loaded by its exact name: `preset_list(machine=…)` lists it, then `preset_load(machine=…, preset=…)`.
