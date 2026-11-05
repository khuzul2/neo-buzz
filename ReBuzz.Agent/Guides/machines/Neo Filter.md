## How it sounds

Neo Poly's filters as an effect: the transistor ladder and the state-variable filter, four times oversampled, with drive in front, an envelope follower and an LFO moving the cutoff. From a gentle top cut in a mix to auto-wah, dub high-pass throws, resonant acid and tempo-locked sweeps.

| Type | Response | Use it for |
|---|---|---|
| Ladder 24 | Neo Poly's ladder, 24 dB/oct low-pass; the passband drops as the resonance rises (the classic ladder) | fat sweeps, acid, dub sirens |
| Ladder 12 | the ladder's second stage, 12 dB/oct | bass auto-wah, softer sweeps |
| SVF LP | state-variable low-pass, 12 or 24 dB/oct (`Slope`); the passband stays at 0 dB | clean top cuts, sweeps |
| SVF BP | band-pass with fixed skirts: the peak is Q times the input (+12 dB at Q 4, 0 dB at Q 1); 24 dB/oct is narrower | auto-wah, telephone, sample-and-hold |
| SVF HP | high-pass, 12 or 24 dB/oct | build-ups, clearing the lows on a send, dub throws |
| SVF Notch | a cut at the cutoff, the rest at 0 dB | swept notches, phaser-like movement |
| SVF Peak | a resonant peak over the dry signal (2 × Q at the cutoff, flat at Q 0.5) | resonant sweeps that keep the body |

Measured: the response is the design the editor draws (within 0.02 dB at 44.1 and 48 kHz); the −3 dB point of SVF LP 12 dB at Q 0.71 is the cutoff; `Resonance` is the Q of the resonant poles (0.71 flat, 2–5 a bump, 10 and up a whistle) and at its top the filter self-oscillates at the cutoff, bounded (about −8 dBFS for the SVF, −13 dBFS for Ladder 24). The drive's aliases stay under −137 dB at −12 dBFS with 24 dB of drive.

## Signal flow

Input → `Drive` (an oversampled soft saturator: quiet signals pass as they are) → the filter (`Type`, `Slope`, `Cutoff`, `Resonance`) → `Mix` against the dry input → `Output`. The cutoff moves in octaves: `Cutoff` × 2^(envelope × `Env amount` + LFO × `LFO depth`), the envelope from the input's peak (both channels) times `Env sensitivity`, with `Env attack` and `Env release`; the LFO free at `LFO rate` or at `LFO sync`'s note value of the song's tempo, the right channel `LFO spread` degrees ahead. Changes glide (20 ms), so automation doesn't zip. Zero latency; at `Mix` 0 % the input passes bit for bit.

`LFO retrig`: Free runs on from where it is; Song follows the song's position while it plays (a 1-bar sweep starts each bar at `LFO phase`); Env restarts at `LFO phase` when the envelope rises past half, on each new note.

## How to use it

- Insert after the sound: `machine_add(gear="Neo Filter", name="Filter", insert_after="Bass")`, then a preset (`preset_load(machine="Filter", preset="Auto-Wah — Funky (Band-Pass)")`) or the settings below.
- Auto-wah: SVF BP or Ladder 12, a low `Cutoff` (200–500 Hz), `Env amount` +2 to +4 octaves, `Env sensitivity` so the envelope fills on the loudest notes (12 dB for a part peaking at −12 dBFS), `Env attack` 1–5 ms, `Env release` 100–250 ms. Watch the editor's envelope meter: full on the accents, low between them.
- Tempo sweeps: `LFO sync` a note value, `LFO retrig` Song, `LFO depth` 1–3 octaves around `Cutoff` (the middle of the sweep); `LFO phase` 270° starts a sine at its bottom.
- Dub high-pass throw: SVF HP 24 dB/oct, automate `Cutoff` up over a bar (the glide keeps it smooth).
- Resonant sounds: `Resonance` 6–12 on the ladder with `Drive` 3–12 dB; at its top the filter sings on its own, a pitched tone at the cutoff.
- Check: `render(probes=["Filter"])` and `analyze` its spectrum; the editor shows the response at the setting and where the envelope and LFO have the cutoff now.

Auto-wah on a funk guitar or clav:

| Parameter | Value | Why |
|---|---|---|
| Type | SVF BP | |
| Cutoff | 420 Hz | the wah's heel |
| Resonance | 4.00 | vocal, not whistling |
| Env amount | 3.00 oct | up to 3.4 kHz on the accents |
| Env sensitivity | 12.0 dB | |
| Env attack | 3.0 ms | |
| Env release | 120 ms | |

One-bar low-pass sweep (house, techno):

| Parameter | Value | Why |
|---|---|---|
| Type | Ladder 24 | |
| Cutoff | 900 Hz | the middle of the sweep |
| Resonance | 3.00 | |
| LFO sync | 1 bar | |
| LFO shape | Sine | |
| LFO depth | 2.20 oct | about 200 Hz to 4 kHz |
| LFO retrig | Song | each bar alike |
| LFO phase | 270 | the bar starts closed |

Gentle top cut in a mix:

| Parameter | Value | Why |
|---|---|---|
| Type | SVF LP | |
| Slope | 12 dB/oct | |
| Cutoff | 12.0 kHz | |
| Resonance | 0.71 | flat, no bump |

## Gotchas

- `Slope` applies to SVF LP, BP, HP and Notch; the ladder types set their own slope and SVF Peak has one section.
- `LFO rate` is ignored while `LFO sync` is not Free.
- `Env amount` at 0 oct (its middle) turns the envelope off; below it the envelope closes the filter.
- Ladder 24 loses level in its passband as `Resonance` rises (about −4 dB at Q 4 and above): make it up with `Output`. The SVF low-pass doesn't; the band-pass and peak rise with Q instead.
- Song retrig only follows the song while it plays; stopped, the LFO runs free.
- At the top of `Resonance` the filter sings on its own for as long as it stays there, input or not; it sleeps only once it and its input have been quiet for 0.25 s.
- `Mix` is a straight crossfade: at 50 % the dry and the filtered signal are each at half level (−6 dB).

## Preset map

| Preset | What |
|---|---|
| "Sweep — Ladder Low-Pass (1 Bar)" | Ladder 24, a sine over one bar, 2.2 octaves |
| "Sweep — Slow Rise (4 Bars, Saw)" | SVF LP 24 dB, a saw rising over four bars |
| "Sweep — High-Pass Build (8 Bars)" | SVF HP 24 dB, rising over eight bars: a build-up |
| "Auto-Wah — Funky (Band-Pass)" | SVF BP, 3 octaves of envelope |
| "Auto-Wah — Bass (Ladder 12)" | Ladder 12 at 220 Hz, 3.5 octaves of envelope |
| "Auto-Wah — Closing (Low-Pass, Envelope Down)" | SVF LP at 4.5 kHz, the envelope closes it |
| "Dub — High-Pass Throw" | SVF HP 24 dB at 420 Hz: automate the cutoff |
| "Dub — Resonant Low-Pass Siren (1/4)" | Ladder 24, Q 8, driven, a triangle on quarter notes |
| "Resonant — Ladder Low-Pass" | Ladder 24 at 1 kHz, Q 6, a little drive |
| "Resonant — Acid (Ladder, Drive, Envelope)" | Ladder 24 at 500 Hz, Q 12, 12 dB drive, the envelope on |
| "LFO — Stepped Filter (1/8, Square)" | SVF LP stepping on eighths |
| "LFO — Stereo Sweep (1/2, 90°)" | Ladder 24, half-note sine, the right side 90° on |
| "LFO — Sample and Hold (1/16)" | SVF BP, a random step each sixteenth |
| "Mix — Gentle Top Cut (12 kHz)" | SVF LP 12 dB, flat |
| "Mix — Telephone Band" | SVF BP 24 dB at 1.3 kHz, 6 dB made up |
| "Notch — Swept Notch (2 Bars)" | a notch swept over two bars |
| "Peak — Resonant Peak Sweep (1 Bar)" | SVF Peak, Q 4, swept over a bar, 6 dB down |
