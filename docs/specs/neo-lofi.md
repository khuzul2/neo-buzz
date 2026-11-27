# Neo Lo-Fi

| | |
|---|---|
| Status | Built (LF1, gap G2 of `docs/specs/legacy-machines.md`): engine, editor, 10 presets, agent guide, gates in `NeoBuzz.MachineTests/LoFiTests.cs`. LF2 (2026-11-24, after the user's verdict on the first pack): an **Amount** knob, seven stronger presets, audible defaults. The sound is for the user's ear: the listening packs are not blind and carry no verdict. |
| Branch | `machines/neo-lofi-lf1` |
| Code | `Machines/NeoLoFi/` (`NeoLoFi.cs`, `NeoLoFiEditor.cs`, `Params.def`, `Presets.txt`, `Engine/LoFiEngine.cs`), the two flavours in `Machines/Portable/NeoLoFi` and `Machines/Studio/NeoLoFi` |
| Builds on | `SmoothRamp`, `Biquad`, `FracDelay`, `Rng`, `PortableMath`, the Neo machine framework (`docs/specs/machines.md`) |
| Clean room | Literature and our own DSP only: textbook quantisation, sample-and-hold, a Butterworth low-pass, white noise, Poisson pops, a modulated delay. No code, parameter table or measurement from any legacy machine was read or used. |

## 1. What it is

A stereo effect with zero latency that makes a sound lo-fi: it reduces the word length and the sample rate, adds noise, vinyl crackle and light pitch motion, and tilts the tone. It replaces the lo-fi role of Joachims Saturn and Pedal Drum Matrix (the user chose a clean break on 2026-11-23: the old machines are removed later, CB1) and is a sound-design staple the Neo family lacked.

## 2. Signal flow

1. **Motion** (`Wow`, `Wow rate`, `Flutter`, `Drift`): one modulated read of a short delay line (linear interpolation), the same delay for both channels. The read delay is `d = sum A (1 - cos phi) >= 0`, so it never looks into the future: there is no baseline delay. Each wave's amplitude is `A = c ln2 / 1200 * fs / omega` samples for a peak deviation of `c` cents. Wow is one sine (0.3 - 6 Hz, up to 50 cents); flutter two (7.3 and 11.9 Hz, weights 0.65 and 0.35, up to 50 cents); drift three slow ones (0.12, 0.19 and 0.31 Hz, weights 0.5, 0.3 and 0.2, up to 25 cents), incommensurate, so it never repeats within a song. The depths glide for 0.4 s: a change of a delay bends the pitch, so a quick change would be a whoop.
2. **Rate** (`Rate`, `Anti-alias`, `Jitter`): a sample-and-hold with a fractional period `fs / Rate` (200 Hz up). A hold clock counts samples; when it runs out the input is taken and held. Both channels take the same instants. `Jitter` varies each period by up to 25 % of a hold period at 100 % (scaled down towards the identity as the period nears 1). `Anti-alias` puts a 12th-order Butterworth low-pass (six biquads in double precision) at 0.40 of the hold rate before the hold, crossfaded in and out over 30 ms. `Rate` at the top of its range, or at or above the host's rate, is off and passes the input.
3. **Bits** (`Bits`, `Dither`): a mid-tread quantiser, `k = floor(x / step + 0.5)` clamped to the codes `-ceil(N) ... ceil(N) - 1` with `N = 2^(bits-1)` and `step = 1 / N`: at a whole number of bits the output takes exactly `2^n` levels (the two's-complement grid; the positive full scale is one step lower than the negative), silence stays exactly zero. A fractional depth sets the step size (`2^(1-bits)`). `Dither` adds TPDF noise of one step peak to peak each side before the rounding. 24 is off.
4. **Noise** (`Hiss`, `Hiss follow`, `Crackle`, `Crackle level`), added after the crush so it is never crushed: hiss is white noise through a one-pole tilt (0.6), normalised so its RMS is the setting in dBFS; with `Hiss follow` it is scaled by the input's envelope (a peak follower, 10 ms fall) against a reference of -20 dBFS and gated, with hysteresis, below -70 dBFS (a smoothed gate), so silence stays silent. Crackle is a Poisson process, `Crackle` pops per second (each channel its own), each a decaying pulse (time constant 0.4 ms) of random sign and an amplitude between 0.4 and 1 times `Crackle level`.
5. **Tone**: left of centre a 12 dB per octave low-pass from 20 kHz down to 600 Hz, right of centre a high shelf at 3 kHz up to +9 dB; crossfaded in over the first tenth of the range, so the middle is exactly off.
6. **Mix and Output**: `y = dry + (wet - dry) * mix` (exactly the wet at 100 %, exactly the dry at 0), then `Output` in dB.

### 2a. Amount (LF2)

`Amount` is the effect strength: 0 to 200 %, 100 % by default, the last parameter (index 16, in the Output group; the editor shows it first, with `Mix` under it). It scales every active stage on a perceptual scale before the stage runs; `Mix` stays the dry/wet blend after it. `a` is the Amount as a fraction (0 to 2). At exactly 100 % the settings are used as they are, with no arithmetic in between.

| Stage | At Amount `a` | Capped at |
|---|---|---|
| Bits | the bits removed from 24 scale: `24 - a (24 - bits)`; above 100 % each extra 100 % removes `min(24 - bits, 6)` more | 2 bits (or the setting, when lower); 24 stays off |
| Rate | octaves below the host's rate scale: `log2(rate) = log2(fs) - a (log2(fs) - log2(setting))` | 200 Hz |
| Jitter | `a` times the setting | 100 % |
| Hiss | dB above the control's floor (-90) scale: `-90 + a (setting + 90)` dBFS; off at `a` = 0 | -12 dBFS |
| Crackle | density `a` times the setting; level `-60 + a (setting + 60)` dBFS | 200 /s; -6 dBFS |
| Wow, flutter, drift | cents `a` times the setting | 100, 100, 50 ct |
| Tone | the position `a` times the setting: the cut in octaves (the low-pass corner `20000 (0.03)^t`) and the lift in dB scale together | 250 Hz; +12 dB |

`Dither`, `Anti-alias`, `Hiss follow`, `Wow rate`, `Mix` and `Output` are not scaled. Every scaled value goes through the stage's own smoothing (30 ms; 0.4 s for the motion depths), so a moving Amount makes no zipper. At 0 % every stage is off: the machine passes the input bit for bit (as at `Mix` 0) once the glide has ended. At 200 % the motion can reach 150 ms of delay, so the delay line is 150 ms long.

Every control is smoothed (`SmoothRamp`, 30 ms; 0.4 s for the motion depths and rate). Coefficients are refreshed every 16 samples while a control moves. After `SetSampleRate` the audio path allocates nothing, and nothing ringing out of a filter, the gate or a pop falls into the denormal range.

## 3. What is exact

- With every stage off (`Bits` 24, `Rate` at the top, `Hiss`, `Crackle`, `Wow`, `Flutter` and `Drift` at 0, `Tone` in the middle), or with `Mix` or `Amount` at 0, at `Output` 0 dB, the machine does not touch the buffer: the input passes bit for bit.
- The latency is 0 at every setting (`GetLatency` returns 0). While the motion is on the wet signal runs behind the dry by between 0 and `2 * sum A` (about 15 ms for 50 cents of wow at 0.3 Hz, a few ms for light settings): that is the effect, not a delay to compensate, and with `Mix` below 100 % it is a gentle chorus. The anti-alias filter is an IIR low-pass with its own phase delay (about 100 samples at a 24 kHz hold); that too is part of the sound and is not reported.

## 4. Gates (`NeoBuzz.MachineTests/LoFiTests.cs`)

| Gate | Test |
|---|---|
| Bypass, latency | all stages off at three block sizes, Mix 0 with every stage on, a Mix glide to 0, an impulse stays on its sample in five configurations |
| Bits | exactly `2^n` levels for n = 1, 2, 3, 4, 8, 12 with and without dither, on the grid; a fractional depth has the step `2^(1-b)`; dither keeps a quarter-step tone alive |
| Rate | a hold of 8 samples; the hold rate within 3 % at 200 Hz, 1 kHz and 12.3 kHz; jitter spreads the run lengths around the same mean; a rate at or above the host's is the identity |
| Alias | a 6 kHz tone held at 8 kHz: the 2 kHz alias with the anti-alias on is at least 60 dB below the one with it off; noise above 6.5 kHz held at 8 kHz leaves at least 60 dB less energy in 100 Hz - 3.6 kHz with it on |
| Noise | hiss within 1 dB of the setting at -80, -60, -40 and -20 dB, also after the crusher; following hiss is silent over silence and at its setting over a loud input; crackle count per second within 20 % at 5, 20 and 60 per second |
| Wow | the peak deviation measured with the shared pitch tracker within 15 % for 20 ct at 1 Hz, 50 ct at 0.5 Hz, 30 ct at 2 Hz and 12 ct at 3 Hz; flutter and drift stay within their settings |
| Hygiene | 0 bytes allocated per block with every control moving (Amount too); no denormals after a loud burst; parameter sweeps under automation leave less than -60 dB above 2 kHz (Amount too); the bit depth and hold rate move in small steps (Amount too) |
| Amount (LF2) | 0 % passes the input bit for bit with every stage on, also after a glide to 0; Mix 0 stays the dry at any Amount; the bits removed and the octaves of rate scale as in 2a (50 %, 100 %, 150 %, 200 % checked); from 0 to 200 % the measured effective bits, hold rate, hiss, crackle count and level, wow, flutter and drift depth and the tone's cut and lift each move monotonically, and at 100 % equal the setting within the stage gates above; every factory preset at 100 % measures what it is set to; a fresh instance is 8 bits at 12 kHz and far from the input; 200 % with every stage at its maximum stays finite |

The portable maths is basic float and double arithmetic plus `PortableMath`, so the Mac and Linux builds agree.

## 5. Parameters, presets, agent

`Params.def` lists the 17 parameters in four groups (Crush, Noise, Motion, Output); the order is the version-0 layout (Neo Lo-Fi has not shipped yet, so `Amount`, appended last, is part of version 0 and its line in `state-layouts.txt` has 17 parameters). Ten presets are named by sound, not by product: 8-bit, 12-bit sampler, Crushed drums, Broken toy, Cassette, Worn tape, Dusty vinyl, AM radio, Telephone and Gentle warmth. The agent guide is `ReBuzz.Agent/Guides/machines/Neo Lo-Fi.md`; the catalogue tags are bitcrush, noise, vintage, gritty and tape. The units `bit` and `/s` are known to the agent's value parser.

## 5a. LF2: what the user heard and what changed

The user's verdict on the first pack (2026-11-24): 04 Broken toy, 06 Worn tape and 08 AM radio were right (kept, unchanged, Amount 100 %); the other seven were too light ("the samples do not sound crushed", "the effect is non audible", "almost inaudible"); every effect should have a strength and a dry/wet control.

**No bug.** Every pack render ran with its preset's values (the new `renders.log` of the pack lists each file's parameter state and the machine's bit depth and hold rate at the end of the render), and each stage measured what its knob said on test signals: hiss within 0.1 dB of the setting, 13.7 pops a second for 14, effective bits 8.0 for 8, the hold within 0.5 % of its rate, wow 12.0 ct for 12, the tone's -3 dB point where the formula puts it. The presets were simply weak: 8 bits at -3 dBFS music is quantisation noise 32 dB under the signal, a hold at 22 kHz folds only the top octave, a hiss at -52 dB is 34 dB under the music, 12 ct of wow is under the threshold on drums, a pop at -34 dB of peak is buried.

Stage figures, measured on test signals with the other stages off (`LoFiStageReport`), before (LF1) and after (LF2). The effective bits come from the signal-to-error ratio of a -3 dBFS sine, the wow, flutter and drift from the shared pitch tracker on a 330 Hz sine.

| Preset | Effective bits | Hold rate (Hz) | Hiss (dBFS) | Crackle | Wow / flutter / drift (ct) | Tone -3 dB point |
|---|---|---|---|---|---|---|
| 01 Crush — 8-bit | 8.0 -> 8.0 | 22074 -> 5988 | - | - | - | flat |
| 02 Crush — 12-bit sampler | 12.0 -> 12.0 | 26029 -> 13010 | -78 -> -62 | - | - | 11803 -> 9906 Hz; jitter 0 -> 12 % |
| 03 Crush — Crushed drums | 5.9 -> 5.0 | 14030 -> 9486 | - | - | - | flat; Mix 60 -> 100 %, jitter 8 -> 10 % |
| 04 Crush — Broken toy | 3.9 | 2197 | - | - | 6.0 / - / 5.2 | 8311 Hz (unchanged) |
| 05 Tape — Cassette | - | - | -52 -> -43 | - | 12.0 / 5.6 / 2.6 -> 18.0 / 8.4 / 4.4 | 5853 -> 4579 Hz |
| 06 Tape — Worn tape | - | - | -46 | - | 22.0 / 11.3 / 8.7 | 3459 Hz (unchanged) |
| 07 Vinyl — Dusty vinyl | 13.2 | - | -62 -> -54 | 13.7 /s at -34 dB -> 22.9 /s at -19.5 dB | 3.0 -> 7.0 / - / - | 9906 -> 8311 Hz |
| 08 Radio — AM radio | 10.0 | 11033 | -48 | - | - / 2.8 / - | 1715 Hz (unchanged) |
| 09 Radio — Telephone | 7.2 -> 5.0 (no dither) | 7989 | -58 -> -46 | 2.7 /s at -40 dB -> 7.0 /s at -30 dB | - | 2904 -> 1974 Hz |
| 10 Warm — Gentle warmth | - | - | -76 -> -58 | - | 4.0 / - / 4.4 -> 8.0 / - / 7.0 | 13119 -> 6978 Hz |

The pack's residual (what is left of a render after the best gain and delay of the dry is taken away, in dB under the source; `renders.log`) for the crushed presets on the chords went from -26 dB (8-bit), -40 dB (12-bit sampler) and -25 dB (Crushed drums) to -16, -21 and -16 dB: the crush is about ten decibels louder against the music, on drums from -11 to -6 dB.

**Defaults.** A fresh instance is 8 bits held at 12 kHz (`Bits` 8, `Rate` 12 kHz, no dither, no anti-alias) with everything else off and `Amount` 100 %: lo-fi at once. A preset that does not name `Bits` or `Rate` takes these defaults, so the tape and vinyl presets name `Bits = 24` and `Rate = raw:1000` (off) explicitly (the kept Worn tape gained the `Rate` line only: its values are the same).

## 6. Limits

- The motion uses linear interpolation (a lo-fi machine; the loss at fractional positions is part of the sound) and a delay that is never negative, so it cannot be pitch-only: see section 3.
- Amount's "exaggerated" end is a design choice, not a measurement: the caps in 2a keep 200 % usable (a 150 ms delay line, hiss no louder than -12 dBFS), not natural.
- Rate shows "Off" at the top of its range (the engine takes the top as off whatever the host's rate), as Bits shows 24 bit; the sections' knobs are laid out so none has empty space (FU2).
- The classic machines' pair files (what Saturn and Pedal Drum Matrix become) belong to LM3, not to this build.
