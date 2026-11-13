## How it sounds

Neo Poly's filters as an effect: the transistor ladder and the state-variable filter (SVF), four times oversampled, with drive in front, an envelope follower and an LFO moving the cutoff. From a gentle top cut in a mix to auto-wah, dub high-pass throws, resonant acid and tempo-locked sweeps.

| Type | Response | Use it for |
|---|---|---|
| Ladder 24 | 24 dB/oct low-pass; the passband drops as the resonance rises | fat sweeps, acid, dub sirens |
| Ladder 12 | the ladder's second stage, 12 dB/oct | bass auto-wah, softer sweeps |
| SVF LP | low-pass, 12 or 24 dB/oct (`Slope`); the passband stays at 0 dB | clean top cuts, sweeps |
| SVF BP | band-pass: the peak is Q times the input | auto-wah, telephone |
| SVF HP | high-pass, 12 or 24 dB/oct | build-ups, clearing a send, dub throws |
| SVF Notch | a cut at the cutoff | swept notches, phaser-like movement |
| SVF Peak | a resonant peak over the dry signal (flat at Q 0.5) | resonant sweeps that keep the body |

`Resonance` is the Q of the resonant poles (0.71 flat, 2-5 a bump, 10 and up a whistle); at its top the filter self-oscillates at the cutoff, bounded. The cutoff moves in octaves: `Cutoff` x 2^(envelope x `Env amount` + LFO x `LFO depth`), the envelope from the input's peak times `Env sensitivity`; the LFO runs free at `LFO rate` or at `LFO sync`'s note value of the song's tempo, the right channel `LFO spread` degrees ahead. `LFO retrig`: Free runs on, Song follows the song position (a 1-bar sweep starts each bar at `LFO phase`), Env restarts on each rising note. Changes glide over 20 ms. Zero latency; at `Mix` 0 % the input passes bit for bit.

## Use it for

- Auto-wah on funk guitar, clav or bass; tempo-locked low-pass sweeps; dub high-pass throws; resonant acid; a gentle top cut or telephone band in a mix.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Type | SVF LP | Ladder 24 for fat sweeps, SVF BP for wah |
| Cutoff | 1 kHz | 20 Hz to 20 kHz: the corner, or the centre of a band |
| Resonance | 0.71 | flat; 2-5 a bump; 6-12 on the ladder sings |
| Drive | 0 dB | 3-12 dB with resonance thickens; 0 is clean |
| Env amount | 0 oct | +2 to +4 octaves for auto-wah; negative closes on loud notes |
| LFO depth | 0 oct | 1-3 octaves around Cutoff for sweeps |
| LFO sync | Free | a note value (1/4 to 8 bars) locks the sweep to the tempo |
| Mix | 100% | below it the dry signal is crossfaded in |

## Starting points

- "Auto-Wah — Funky (Band-Pass)": SVF BP, Cutoff 420 Hz, Resonance 4, Env amount 3 oct, Env sensitivity 12 dB, Env attack 3 ms, Env release 120 ms. "Auto-Wah — Bass (Ladder 12)", "Auto-Wah — Closing (Low-Pass, Envelope Down)".
- "Sweep — Ladder Low-Pass (1 Bar)": Ladder 24, Cutoff 900 Hz, Resonance 3, LFO sync 1 bar, Sine, LFO depth 2.2 oct, LFO retrig Song, LFO phase 270 degrees (the bar starts closed). "Sweep — Slow Rise (4 Bars, Saw)", "Sweep — High-Pass Build (8 Bars)".
- "Dub — High-Pass Throw" (automate Cutoff), "Dub — Resonant Low-Pass Siren (1/4)", "Resonant — Acid (Ladder, Drive, Envelope)".
- "LFO — Stepped Filter (1/8, Square)", "LFO — Stereo Sweep (1/2, 90°)", "LFO — Sample and Hold (1/16)".
- "Mix — Gentle Top Cut (12 kHz)" (SVF LP 12 dB, flat), "Mix — Telephone Band", "Notch — Swept Notch (2 Bars)", "Peak — Resonant Peak Sweep (1 Bar)".

## Pitfalls

- `Slope` applies to SVF LP, BP, HP and Notch; the ladders set their own and SVF Peak has one section. `LFO rate` is ignored while LFO sync is not Free.
- `Env amount` at 0 oct turns the envelope off; below it the envelope closes the filter.
- Ladder 24 loses passband level as Resonance rises (about -4 dB at Q 4 and above): make it up with `Output`.
- At the top of Resonance the filter sings on its own, input or not, until it and its input have been quiet for 0.25 s.
- Song retrig follows the song only while it plays; stopped, the LFO runs free.
- Mix is a straight crossfade: at 50 % the dry and filtered signals are each 6 dB down.

## Pairs well with

- Neo Drive before it for fuzz into a filter; Neo Ensemble or Neo Delay after it for movement; Neo Poly and Neo Wave as the source.

## Successor

Neo Filter replaces Pedal Filter, Jeskola Filter, Jeskola Filter 2 and FSM WahMan: `machine_replace(machine="Filter")` carries their settings over.

## Checklist

- [ ] `machine_add(gear="Neo Filter", name="Filter", insert_after="Bass")`, then `preset_load(machine="Filter", preset="Auto-Wah — Funky (Band-Pass)")`.
- [ ] `render(probes=["Filter"])` then `analyze(source="Filter", metrics=["spectrum"])`: the spectrum moves with the envelope or LFO.
- [ ] `machine_readings(machine="Filter")`: `peak_db` under 0 dBFS at the highest Resonance you use.
