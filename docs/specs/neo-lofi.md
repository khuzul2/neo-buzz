# Neo Lo-Fi

| | |
|---|---|
| Status | Built (LF1, gap G2 of `docs/specs/legacy-machines.md`): engine, editor, 10 presets, agent guide, gates in `NeoBuzz.MachineTests/LoFiTests.cs`. The sound is for the user's ear: the listening pack is not blind and carries no verdict. |
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

Every control is smoothed (`SmoothRamp`, 30 ms; 0.4 s for the motion depths and rate). Coefficients are refreshed every 16 samples while a control moves. After `SetSampleRate` the audio path allocates nothing, and nothing ringing out of a filter, the gate or a pop falls into the denormal range.

## 3. What is exact

- With every stage off (`Bits` 24, `Rate` at the top, `Hiss`, `Crackle`, `Wow`, `Flutter` and `Drift` at 0, `Tone` in the middle), or with `Mix` at 0, at `Output` 0 dB, the machine does not touch the buffer: the input passes bit for bit.
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
| Hygiene | 0 bytes allocated per block with every control moving; no denormals after a loud burst; parameter sweeps under automation leave less than -60 dB above 2 kHz; the bit depth and hold rate move in small steps |

The portable maths is basic float and double arithmetic plus `PortableMath`, so the Mac and Linux builds agree.

## 5. Parameters, presets, agent

`Params.def` lists the 16 parameters in four groups (Crush, Noise, Motion, Output); the order is the version-0 layout. Ten presets are named by sound, not by product: 8-bit, 12-bit sampler, Crushed drums, Broken toy, Cassette, Worn tape, Dusty vinyl, AM radio, Telephone and Gentle warmth. The agent guide is `ReBuzz.Agent/Guides/machines/Neo Lo-Fi.md`; the catalogue tags are bitcrush, noise, vintage, gritty and tape. The units `bit` and `/s` are known to the agent's value parser.

## 6. Limits

- The motion uses linear interpolation (a lo-fi machine; the loss at fractional positions is part of the sound) and a delay that is never negative, so it cannot be pitch-only: see section 3.
- The classic machines' pair files (what Saturn and Pedal Drum Matrix become) belong to LM3, not to this build.
