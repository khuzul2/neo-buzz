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
| Vowel | a formant filter: three resonances at a spoken vowel's frequencies | talking wah, choir pads, robot voices, making any sound say A, E, I, O or U |

`Resonance` is the Q of the resonant poles (0.71 flat, 2-5 a bump, 10 and up a whistle); at its top the filter self-oscillates at the cutoff, bounded. The cutoff moves in octaves: `Cutoff` x 2^(envelope x `Env amount` + LFO x `LFO depth`), the envelope from the input's peak times `Env sensitivity`; the LFO runs free at `LFO rate` or at `LFO sync`'s note value of the song's tempo, the right channel `LFO spread` degrees ahead. `LFO retrig`: Free runs on, Song follows the song position (a 1-bar sweep starts each bar at `LFO phase`), Env restarts on each rising note. Changes glide over 20 ms. Zero latency; at `Mix` 0 % the input passes bit for bit.

**Vowel type.** `Vowel` (0-100 %) morphs A, E, I, O, U a quarter apart (formants move in log frequency between neighbours); `Voice` is Man, Woman or Child (Peterson and Barney's 1952 averages for F1-F3). `Cutoff` shifts all the formants together (2 kHz, the default, is the table; within 2 octaves either way), `Resonance` scales their bandwidths (0.71 as spoken, higher is sharper), `Slope` is ignored. `Vowel mod` (default 100 %) decides what the envelope and LFO move: at 100 % one octave of their motion is one vowel step (25 %) and the formants stay, at 0 % they slide all the formants together like a wah, in between a mix. `Drive`, `Mix`, `Output` and `Amount` work as in the other types (`Amount` scales the cutoff shift and the Q from their neutrals). The level is matched to the input at the default `Resonance`; sharper formants take level away (a bounded boost keeps it up). The Vowel section of the editor is dimmed in the other types.

## Use it for

- Talking synths and choir pads (Vowel type); auto-wah on funk guitar, clav or bass; tempo-locked low-pass sweeps; dub high-pass throws; resonant acid; a gentle top cut or telephone band in a mix.

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
| Vowel | 0% | Type Vowel only: 0 % A, 25 % E, 50 % I, 75 % O, 100 % U (`Voice` picks Man, Woman or Child) |

`Amount` (default 100 %) scales cutoff distance from open, resonance, drive, envelope and LFO depth: 0 % is the bypassed dry, 200 % exaggerates. `Mix` stays its own dry/wet: below 100 % the dry signal is crossfaded in.

## Starting points

- "Auto-Wah — Funky (Band-Pass)": SVF BP, Cutoff 420 Hz, Resonance 4, Env amount 3 oct, Env sensitivity 12 dB, Env attack 3 ms, Env release 120 ms. "Auto-Wah — Bass (Ladder 12)", "Auto-Wah — Closing (Low-Pass, Envelope Down)".
- "Sweep — Ladder Low-Pass (1 Bar)": Ladder 24, Cutoff 900 Hz, Resonance 3, LFO sync 1 bar, Sine, LFO depth 2.2 oct, LFO retrig Song, LFO phase 270 degrees (the bar starts closed). "Sweep — Slow Rise (4 Bars, Saw)", "Sweep — High-Pass Build (8 Bars)".
- "Dub — High-Pass Throw" (automate Cutoff), "Dub — Resonant Low-Pass Siren (1/4)", "Resonant — Acid (Ladder, Drive, Envelope)".
- "LFO — Stepped Filter (1/8, Square)", "LFO — Stereo Sweep (1/2, 90°)", "LFO — Sample and Hold (1/16)".
- Vowel type: "Vowel — Talking Wah" (synced LFO walks the vowel), "Vowel — Choir Ah" (static A, Woman), "Vowel — Envelope Yeah" (the envelope moves I to A), "Vowel — Robot Vowels" (S&H steps between vowels, Man).
- "Mix — Gentle Top Cut (12 kHz)" (SVF LP 12 dB, flat), "Mix — Telephone Band", "Notch — Swept Notch (2 Bars)", "Peak — Resonant Peak Sweep (1 Bar)".

## Pitfalls

- `Slope` applies to SVF LP, BP, HP and Notch; the ladders set their own and SVF Peak has one section. `LFO rate` is ignored while LFO sync is not Free.
- Vowel type: leave `Cutoff` at its default 2 kHz for the vowels as spoken (each octave up or down shifts every formant by an octave, within 2 octaves); the vowel only changes through `Vowel`, `Voice` and, with `Vowel mod`, the envelope and LFO. A MIDI controller or automation lane on `Type` now steps through eight types.
- `Env amount` at 0 oct turns the envelope off; below it the envelope closes the filter.
- Ladder 24 loses passband level as Resonance rises (about -4 dB at Q 4 and above): make it up with `Output`.
- At the top of Resonance the filter sings on its own, input or not, until it and its input have been quiet for 0.25 s.
- Song retrig follows the song only while it plays; stopped, the LFO runs free.
- Mix is a straight crossfade: at 50 % the dry and filtered signals are each 6 dB down.

## Pairs well with

- Neo Drive before it for fuzz into a filter; Neo Ensemble or Neo Delay after it for movement; Neo Poly and Neo Wave as the source.

## Checklist

- [ ] `machine_add(gear="Neo Filter", name="Filter", insert_after="Bass")`, then `preset_load(machine="Filter", preset="Auto-Wah — Funky (Band-Pass)")`.
- [ ] `render(probes=["Filter"])` then `analyze(source="Filter", metrics=["spectrum"])`: the spectrum moves with the envelope or LFO.
- [ ] `machine_readings(machine="Filter")`: `peak_db` under 0 dBFS at the highest Resonance you use.
