## How it sounds

Lo-fi in one effect, with no latency. The chain runs in this order: the pitch motion (`Wow`, `Flutter`, `Drift`), then the crush (the hold at `Rate`, then `Bits`), then the noise (`Hiss` and `Crackle`, added after the crush so they are never crushed), then `Tone`, `Mix` and `Output`. `Amount` (0-200%, 100% is the settings as they stand) scales every active stage at once.

- `Bits` 1-24 in fractions of a bit; 24 is off. At a whole number of bits the output takes exactly 2^n levels. `Dither` adds TPDF noise before the rounding: off, quiet parts turn into crunchy distortion; on, into smooth hiss.
- `Rate` is a sample-and-hold from 200 Hz up; the top of the range (at or above the song's rate) is off. `Anti-alias` off leaves the fold-over aliasing in (the raw, harsh sound); on puts a steep low-pass at 0.4 of the hold rate first (at least 60 dB down above 1.5 times the new Nyquist), so it is duller but clean. `Jitter` shakes the hold clock.
- `Hiss` is the level in dBFS (RMS); `Hiss follow` makes it follow the input and shut when the input is silent. `Crackle` is pops per second, `Crackle level` their peak.
- `Wow` (rate in `Wow rate`), `Flutter` and `Drift` move the pitch by up to 50, 50 and 25 cents. They read a short delay line, so the wet signal runs a few ms behind the dry while they are on (a gentle chorus when `Mix` is below 100%); nothing is reported as latency.
- `Tone` left of centre is a low-pass (20 kHz down to 600 Hz), right of centre a high-frequency lift of up to 9 dB.
- `Amount` scales on a perceptual scale: the bits removed from 24 and the octaves of rate reduction, the hiss and crackle level in dB, the cents of wow, flutter and drift, the tone cut. 0% is clean, 200% exaggerated; `Mix` still blends the dry in.
- With every stage off, or `Mix` or `Amount` at 0 and `Output` at 0 dB, the input passes bit for bit.

## Use it for

- 8-bit and early-sampler grit on drums, bass and leads; cassette, radio and telephone colour; dusty vinyl under a sample; taking the digital edge off a part (`Bits` 24 with a little `Wow`, `Drift` and `Hiss`).

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Bits | 8 bit | 12 is a gentle grain, 8 is gritty, 4 and below is a buzz; 24 is off |
| Rate | 11 kHz | the hold rate; lower is more metallic, 200 Hz is a toy |
| Anti-alias | On | off for the raw alias, on for the clean, dull sound of a sampler's output filter |
| Hiss | -55 dB | -70 is faint, -45 is a worn tape |
| Crackle | 12 /s | a few a second is dust, 60 is a worn record |
| Wow | 10 ct | 5-15 is a tired cassette, 25 and more is seasick |
| Tone | -30% | darker; the crush is harsh without it |
| Amount | 100% | one knob for the whole strength: 50% is half the bits removed, 150% pushes every stage further |
| Mix | 60% | blend the crush under the original to keep the punch |

## Starting points

- "Crush — 8-bit", "Crush — 12-bit sampler", "Crush — Crushed drums", "Crush — Broken toy".
- "Tape — Cassette", "Tape — Worn tape", "Vinyl — Dusty vinyl".
- "Radio — AM radio", "Radio — Telephone", "Warm — Gentle warmth".

## Pitfalls

- A fresh instance is lo-fi on purpose: `Bits` 8 and `Rate` 12 kHz. Set `Bits` to 24 and `Rate` to the top to switch the crush off, as the tape and vinyl presets do.
- `Rate` below about 8 kHz with `Anti-alias` on takes the whole top end; with it off it makes loud, inharmonic aliasing: lower `Output` first.
- `Hiss` and `Crackle` make sound with no input; the machine never rests while they are on.
- The `Wow` and `Drift` depth glide for 0.4 s on purpose: any quick change of a delay bends the pitch.
- `Anti-alias` is a steep filter with a phase delay of about 2 ms, not reported as latency.
- Dither at 8 bits or fewer is loud; it is a texture, not a polish.

## Pairs well with

- Neo Drive or Neo Tape before it for warmth; Neo EQ after it for the top; Neo Delay or Neo Verb after it for space; Neo Comp on a crushed drum bus.

## Checklist

- [ ] `machine_describe(machine="LoFi")`: `Bits` 24 and `Rate` at the top are off; start from a preset, then use `Amount` for strength and change one control at a time.
- [ ] `analyze(source="LoFi", metrics=["loudness"])`: the crush and noise change the level; set `Output` to match before judging.
- [ ] `compare` a bypassed render (`machine_set(machine="LoFi", bypass=true)`) with the processed one at matched loudness.
- [ ] `render` a short loop and listen with `Anti-alias` on and off, and with `Dither` on and off, before settling.
