# Neo Amp

| | |
|---|---|
| Status | Built (AMP1, issue #197): engine, editor, 8 presets, agent guide, gates in `NeoBuzz.MachineTests/NeoAmpTests.cs`, listening-pack generator `NeoAmpPack.cs`. The sound is for the user to approve. |
| Branch | `machines/neo-amp-amp1` |
| Code | `Machines/NeoAmp/` (`NeoAmp.cs`, `NeoAmpEditor.cs`, `AmpWaves.cs`, `Params.def`, `Presets.txt`, `Engine/AmpEngine.cs`, `Engine/AmpCore.cs`, `Engine/AmpVoicings.cs`, `Engine/AmpTone.cs`, `Engine/AmpCabs.cs`, `Engine/AmpTuner.cs`), the two flavours in `Machines/Portable/NeoAmp/` and `Machines/Studio/NeoAmp/`. |
| Builds on | `FastOversampler`, `StereoConvolver` (the shared zero-latency partitioned convolver), `PitchTracker`, `FxStage` (Mix and Amount), `Biquad`, `DcBlocker`, `SmoothRamp`, `Fft`, `PortableMath`, `LevelMeter`, the Neo machine framework (`docs/specs/machines.md`) |
| Clean room | Everything is our own DSP from the published literature in §14. No impulse response, capture, parameter table or preset of another amp, cabinet or product is used, stored or downloaded; the cabinets are made by code, a wave is the user's own. The tone stack's network and values were found by our own numeric search and checked against our own solver of its equations. No code or source of any other amp model was opened. |

## 1. What it is

A guitar or bass amp for a DI signal, in one machine: a noise gate, a preamp of one to four gain stages, a passive tone stack, a power stage, a cabinet, and a tuner. The input is mono; the amp and cabinet are mono; both outputs carry the same signal. After the amp come Mix, Amount and Output.

Signal path: **Input** (Left, Right or both summed) → **Trim** → *tuner reads here* → **Gate** → **preamp stages** → **tone stack** → **power stage** (Sag, Presence, Resonance, Master) → **down-sample** → **cabinet** (or direct, or a wave) → **mic position, low cut, high cut** → **Output** → **Mix / Amount** → *tuner mute*.

Controls (the order is the state's order; a new one is only ever appended): `Input`, `Trim` (-24 to +24 dB), `Gate` (-80 to -20 dB; the lowest is off), `Gate release` (10 to 500 ms), `Voicing`, `Gain` (0 to 40 dB), `Bass`, `Mid`, `Treble`, `Sag`, `Presence`, `Resonance`, `Master`, `Quality` (2x, 4x, 8x), `Cabinet`, `Wave` (slot 1 to 200), `Mic position`, `Low cut` (20 to 400 Hz; the lowest is off), `High cut` (2 to 20 kHz; the highest is off), `Tuner`, `Reference` (A4 = 415 to 466 Hz), `Output` (-24 to +24 dB), `Mix`, `Amount` (0 to 200 %).

State: versioned, version 0 (`Layouts/state-layouts.txt`). `Wave` is a slot number: **the song saves the wave, the machine saves only its slot**.

## 2. The voicings

A voicing is a set of numbers for one signal chain (`AmpVoicings.cs`): the number of stages, the fixed gain in front of each, each stage's asymmetry and bias, the coupling high-pass in front of it and the low-pass behind it (three one-pole sections: the stage's capacitance), the stage the Gain knob feeds, a bright filter, the tone stack's capacitor scales, the power stage's drive range, Sag depth, Presence and Resonance shelves and a level.

| Voicing | Stages (fixed gain, dB) | Coupling high-pass, Hz | Low-pass, Hz | Bright | Power drive, dB (Master 0 to 100 %) | Presence / Resonance (at 100 %) |
|---|---|---|---|---|---|---|
| Clean | 2 (1.6, -12) | 25, 30 | 14000, 11000 | none | -10 to +6 | 3.5 kHz +8 dB / 110 Hz +7 dB |
| Bright clean | 2 (3, -11) | 30, 40 | 16000, 13000 | +9 dB above 2.2 kHz, fades as Gain rises | -10 to +6 | 4 kHz +8 / 100 Hz +5 |
| Crunch | 3 (6, -3.5, 4) | 30, 50, 60 | 12000, 9000, 8000 | +4 dB | -8 to +10 | 3.2 kHz +8 / 105 Hz +7 |
| Classic rock | 3 (10, 0, 10) | 35, 70, 90 | 10000, 8000, 7000 | none | -6 to +14 | 3 kHz +9 / 100 Hz +8 |
| Lead | 4 (13.6, -2, 12, 9.6) | 40, 80, 110, 130 | 9000, 7500, 6500, 6000 | none | -6 to +12 | 3 kHz +8 / 100 Hz +7 |
| High gain | 4 (18, 1, 15.6, 14) | 60, 140, 200, 240 | 8000, 6500, 5500, 5000 | none | -8 to +10 | 3.5 kHz +9 / 95 Hz +8 |
| Bass clean | 2 (1.6, -12) | 12, 15 | 12000, 9000 | none | -10 to +6 | 2.5 kHz +6 / 60 Hz +6 |
| Bass drive | 3 (6, -3.5, 4) | 15, 30, 40 | 9000, 7000, 6000 | none | -8 to +10 | 2.5 kHz +6 / 65 Hz +7 |

The **Gain** knob adds its 0 to 40 dB in front of the second stage (the first stage is a fixed input stage). The two bass voicings use tone-stack capacitors 2.5 to 3 times larger, which moves the stack's corners down. Each voicing has a level (dB) so that the eight sit within a few dB of each other at the same DI level (checked on the preset renders in §13).

## 3. The stage and its anti-aliasing

Each stage is a static, asymmetric, algebraic sigmoid, then a polarity flip (an inverting triode stage), with a bias that is added in front and removed behind (so a quiet signal has even harmonics but no DC):

f(u) = u / sqrt(1 + u²) for u ≥ 0, f(u) = u / sqrt(1 + (u/k)²) for u < 0,

with k ≥ 1 the asymmetry (the negative half saturates at -k). Its antiderivative is known in closed form: F(u) = sqrt(1 + u²) - 1 and F(u) = k² (sqrt(1 + (u/k)²) - 1), so the **first-order antiderivative anti-aliasing** of Parker, Zavalishin and Le Bivic (2016) and Bilbao, Esqueda, Parker and Välimäki (2017) applies: y[n] = (F(u[n]) - F(u[n-1])) / (u[n] - u[n-1]), with the curve itself at the mid-point where the two inputs are closer than 1e-7. It runs in double precision. The same curve (with its own asymmetry) is the power stage's.

The whole chain runs at 2x, 4x or 8x (`Quality`, default 4x) through `FastOversampler`, so the ADAA's half-sample delay and the oversampler's filters are the only things that add to the latency, and each stage's low-pass (three one-pole sections) takes the highs off before the next stage can fold them. A two-pole low-pass in front of the power stage (9 kHz) does the same for the last curve.

## 4. The tone stack

A passive stack of three knobs, **derived** here from its network, not copied: the network is a list of resistors and capacitors between six nodes (`ToneNetwork.Parts`): the treble capacitor (1.2 nF) from the input to the top of the treble pot (7.5 kOhm), the slope resistor (33 kOhm) and the mid pot's series part ((1 - mid) x 68 kOhm), the bass pot (62 kOhm, log taper) from the stack's middle node to ground, the mid capacitor (150 nF) to ground, and the bass-side resistor (43 kOhm) with its capacitor (470 pF). The treble pot is an unloaded divider between two nodes, so it only mixes them: Vout = t x Va + (1 - t) x Vb.

The nodal equations Y(s) V = I of the nodes the signal does not drive have entries that are polynomials of degree 1 in s; solving for the two nodes by **Cramer's rule with polynomial determinants** (24 permutations of the 4 x 4 determinant, in code) gives H(s) = N(s) / D(s) of third order (three capacitors). The polynomials are scaled to s' = s / 2π1000 so the coefficients stay near 1, and the bilinear transform (s = k (1 - z⁻¹)/(1 + z⁻¹), with k pre-warped to match the analog response at 1.5 kHz) gives a third-order digital filter in transposed direct form II, in double precision. The knobs are smoothed (30 ms) and the coefficients are recomputed at most once per 32 samples, only when a knob moves.

A second, independent solver, `ToneNetwork.Response` (Gaussian elimination of the same nodal equations with complex arithmetic at each frequency), is the reference of the test and of nothing else. The stack's level loss is made up by the mean power of its response over 120 Hz to 3.8 kHz (limited to +40 dB), so the knobs move the tone and not the loudness.

The values (found by a numeric search of fixed topologies for a response with a Bass, a Mid and a Treble that each do what they say over their whole travel, a mid dip near 700 Hz at its middle setting, and a pot rotation that is never dead at the ends) are ours; **the gate**: at five knob settings the digital response is within **0.06 dB** of the analytic network at 4x (0.24 dB at 2x, 0.015 dB at 8x) from 20 Hz to 10 kHz.

## 5. The power stage

After the tone stack and its makeup: the stage's own asymmetric curve (ADAA), with the supply's **Sag**: an envelope follower of the stage's output (40 ms attack, 250 ms release) lowers the supply, y = f(x · drive / supply) · supply, so a loud chord pulls the supply down and the curve softens (a slow compression that swells back); the voicing's Sag depth (0.25 to 0.55 of the supply) times the Sag knob sets how far. **Master** moves the drive over the voicing's range; its level is made up by the square root of the drive's distance from its value at Master 60 %, so Master moves the character far more than the loudness. **Presence** (a high shelf) and **Resonance** (a low shelf) are the effect of less negative feedback: biquad shelves after the curve (corners in §2). A DC blocker (8 Hz) ends the chain.

## 6. Latency

`AmpEngine.Latency` is `FastOversampler.MaxLatency` (62 samples at 48 kHz) at **every** quality: the oversampler's own latency differs by factor, and each core is padded with a delay line to the same total; the cabinet (the zero-latency convolver) adds none. The dry signal of Mix is delayed by it. In its linear range the amp's delay at 1 kHz is the reported latency within a few samples for the clean voicings (the other voicings' tone stack and bright filter have a group delay of their own there, which is part of the sound).

## 7. The cabinets

Six cabinets are made by code (`AmpCabs.cs`), then **Off** (the amp direct) and **Wave** (any wave of the song). Each is a magnitude response designed from analog sections: a second-order high-pass (the speaker's resonance), a second-order and a first-order low-pass (the cone's roll-off), and a handful of peaking sections (the box, the cone's breakup, the standing waves), all in `Specs`: 

| Cabinet | Resonance high-pass | Roll-off | Character |
|---|---|---|---|
| Open 1x12 | 115 Hz, Q 1.1 | 5.6 kHz (2nd order), 9.5 kHz | thin lows, a wide upper-mid bump, an airy top |
| Closed 2x12 | 98 Hz, Q 1.0 | 5.2 kHz, 8.5 kHz | more low end, a low-mid ripple, firm upper mids |
| Closed 4x12 | 84 Hz, Q 1.15 | 4.7 kHz, 6.5 kHz | deep and tight, scooped low mids, a hard 2.5 kHz peak |
| Bass 1x15 | 46 Hz, Q 0.9 | 3.3 kHz, 5 kHz | a deep round bottom, a dark top |
| Bass 4x10 | 55 Hz, Q 1.0 | 6.2 kHz, 9 kHz | firm lows, a clear upper mid for definition |
| Bass 8x10 | 42 Hz, Q 1.1 | 5.6 kHz, 8 kHz | very deep, scooped lower mid, a wide bright top |

The impulse response is the **minimum-phase** one for that magnitude (the real cepstrum folded to the causal side, Oppenheim and Schafer), made on a 8192 to 32768-point grid by `PortableMath`, cut to **at most 50 ms** with a raised-cosine fade over its last fifth, and **levelled** so that pink noise between 100 Hz and 5 kHz passes at unit gain (the cabinets sit at the same loudness). It is built **off the audio thread** (the `AmpBuilder` thread, as Neo Space's) and handed over without a lock; the convolver is `StereoConvolver` (zero latency). It is never committed or stored: it is made from the numbers above at each load and at each sample-rate change.

**Wave** uses the song's wave in the slot named by `Wave` as the impulse response: read at its own rate and resampled, mono (a stereo wave is summed), the DC offset and a lead of silence trimmed (to 0.5 ms before the sound), cut to 250 ms with a fade, levelled the same way. A slot with no wave (or one deleted) plays the **closed 4x12** and the editor says "Wave N is not in the song: the closed 4x12 plays" in its warning colour; while a wave is still being read, what plays keeps playing.

**Mic position** (0 = the centre of the cone, 100 % = its edge) is a high shelf at 2.8 kHz (+2.5 dB to -8.5 dB) and a low-pass (16 kHz at the centre to 8.8 kHz at the edge); **Low cut** and **High cut** are second-order Butterworth sections after it, off at their ends; all three are smoothed and apply to a wave as well. With the cabinet Off, the mic shelf and low-pass are bypassed (the cuts still work).

## 8. Switching

Changing `Voicing` or `Quality` configures the second core from rest on the new settings while the first plays on; the two outputs crossfade over 30 ms (equal power) on the same input, then the old core is dropped. Changing `Cabinet` (or the wave) builds the new convolver off the audio thread, then crossfades old and new over 30 ms (a cabinet to Off, and back, fades to and from the direct amp). A second change while a fade is running waits for it. 0 bytes are allocated on the audio thread (two cores and the convolvers are made in advance or on the builder's thread), and the click detector finds no new click.

## 9. The gate

A peak envelope follower (instant attack, 20 ms fall) on the DI after Trim; the gate opens above the `Gate` threshold and closes **6 dB lower** (hysteresis), so a note decaying through the threshold, or a level wobbling by a few dB, does not chatter. It opens in 2 ms and closes exponentially, 60 dB in `Gate release`; the gain is exactly 0 below 1e-6, so the gate is silent under its threshold. At its lowest the gate is off.

## 10. The tuner

The DI (after Trim, before the gate) goes to the shared `PitchTracker` (YIN, de Cheveigné and Kawahara, 2002) in its Full range. That range stops at 38 Hz and a bass's low B is 30.9 Hz, so the tracker is **told the sample rate is 1.4 times the real one** (the pitch it sees is 1.4 times higher: low B reads 43 Hz, E6 reads 1847 Hz, both in its range, and what it reports is divided by 1.4; the period in samples is untouched). Its estimate starts a **refinement** over the DI itself: the normalised cross-correlation of the newest window with itself one period back, at fractional periods (4-point Lagrange interpolation), a coarse search then a fine one, each ended by a parabola. That is within a few hundredths of a cent for a clean string.

The reading (the note, its MIDI number, the cents from the nearest note at `Reference`, the frequency, the level) is published **without a lock**: a sequence number (odd while being written) around the fields, and the reader retries. It is **held while the note decays** (the tracker unsure, the level still above -70 dBFS, for up to 2 s) and **cleared** after 150 ms under -70 dBFS: silence shows nothing. The tuner runs only while `Tuner` is on, and **mutes the output** (a 10 ms ramp) so you tune silently. The editor shows the note, the cents, the frequency and a needle on a -50 to +50 cent scale (green within 5 cents).

## 11. Mix, Amount, Output

Through the shared `FxStage`: Amount scales the **Gain in dB** and the **Master** drive (`AmountRule.Lin`, from 0): 0 % is the bypass (bit for bit the input, delayed by the latency), 100 % the settings as shown, **200 % twice the Gain in dB (up to 40) and twice the Master (up to 100 %)**. Mix blends the dry input (delayed by the latency) with the amp; Output scales the amp and cabinet (before Mix). Every gain is smoothed (no zipper noise: a sweep of Mix and Amount, through 0 %, gives no new clicks).

## 12. The editor

The house style (`docs/specs/ux-overhaul.md`): five sections in the Distortion identity. **Input** (the channel, Trim, Gate and its release, the input level meter), **Amp** (the voicing, Quality, Gain, Bass, Mid, Treble, Sag, Presence, Resonance, Master), **Cabinet** (the cabinet, a picture of its response from 40 Hz to 16 kHz as it plays, with the mic position and the cuts drawn on it, Wave, Mic position, Low cut and High cut; the amber line when a wave is missing), **Tuner** (the switch, Reference, the note, the cents, a needle) and **Output** (Amount and Mix side by side as 40 px knobs, Output, the output meter). One source for WPF and Avalonia; the pictures draw on the kit's canvas.

## 13. The gates (all in `NeoAmpTests.cs`)

| Gate | Measured |
|---|---|
| Tone stack against the analytic network, 5 knob settings, 20 Hz to 10 kHz | 0.06 dB at 4x (gate 0.5 dB); 0.24 dB at 2x; 0.015 dB at 8x |
| Aliasing, 2.5 kHz sine, High gain, Gain 40 dB, every non-harmonic component under the fundamental, DI at -12 dBFS / full scale | 4x: **77.3 dB / 74.3 dB** (gate 70 dB); 8x: 94.1 / 89.0 dB; **2x: 65.0 / 62.2 dB (under the gate: 2x is the draft setting, reported, not claimed)** |
| Tuner, a plucked string (Karplus-Strong with a pickup filter, §15), E1 to E6 | within 1 cent after 101 ms; worst error after 200 ms 0.42 cents (gate 1 cent, 200 ms); holds the note until the string is under -65 dBFS |
| Tuner, low B (30.87 Hz) | within 2 cents after 101 ms; worst error after 200 ms 0.13 cents (gate 2 cents) |
| Tuner, silence | nothing on silence, on hiss at -86 dBFS and 2 s after a note |
| Gate | on hiss at -70 dBFS RMS with the threshold at -50 dB: silent (peak under 1e-6); a note opens it at its own level (within 0.7 dB); a note decaying 40 dB through the threshold with a 7 Hz wobble: 1 change between sound and silence; 0 clicks |
| Switching, 12 changes of voicing, quality and cabinet under a chord strum | 0 clicks; 22 switches over 2000 blocks: 0 bytes on the audio thread; after the fade the output is that of an engine that was on the new setting all along (under -60 dB) |
| Latency | `Latency` = 62 samples at every quality; Mix 0: the input delayed by it, bit for bit, at blocks of 256, 64 and 37; the wet path at 1 kHz is the same at 2x, 4x and 8x within 2 samples |
| Mix and Amount | Amount 0 and Mix 0: the input delayed by the latency, bit for bit; Mix 30 % is 0.7 dry + 0.3 wet; Amount 200 %, 50 % and 150 % are Gain and Master at 2, 0.5 and 1.5 times (under -90 dB); the crest factor falls from 15.9 dB at 10 % to 9.6 dB at 200 % |
| Audio thread | 0 bytes per block over 2000 blocks with 12 controls moving; 0 denormals and 0 non-finite samples in a 30 s tail (Clean, High gain, Bass drive); silence after the tail; PortableMath only (the shared scan and a scan of the engine files) |
| CPU | High gain, 4x, closed 4x12, blocks of 256: 25 times real time at the reference's speed, 4.0 % of a core: **1.92 times Neo Drive's CPU** (2.1 %) |
| State | the Wave slot round-trips bit for bit and follows the song; a missing wave plays the closed 4x12, bit for bit, and the status says so until it appears |
| Cabinets | six, each at most 50 ms, deterministic, minimum phase (over 90 % of the energy in the first millisecond), the designed magnitude within 1.5 dB, levelled on pink noise within 2 %, at least 1.5 dB rms apart |
| Presets | 8, each 6 to 30 dB under full scale on a full-scale riff (RMS between -30 and -6 dB), no clipping, the two closest 2.8 dB apart in the third-octave spectrum |

## 14. Literature

Each entry was checked against the publisher's or the conference's own listing.

- D. T. Yeh and J. O. Smith, a paper on discretising a classic passive guitar-amplifier tone stack (its title names the amplifier), Proc. Int. Conf. Digital Audio Effects (DAFx-06), Montreal, 2006 (the nodal analysis of the RC network and the bilinear transform).
- J. Pakarinen and D. T. Yeh, "A review of digital techniques for modeling vacuum-tube guitar amplifiers", Computer Music Journal 33(2), 2009.
- J. D. Parker, V. Zavalishin and E. Le Bivic, "Reducing the aliasing of nonlinear waveshaping using continuous-time convolution", Proc. DAFx-16, Brno, 2016.
- S. Bilbao, F. Esqueda, J. D. Parker and V. Välimäki, "Antiderivative antialiasing for memoryless nonlinearities", IEEE Signal Processing Letters 24(7), 2017.
- K. Karplus and A. Strong, "Digital synthesis of plucked-string and drum timbres", Computer Music Journal 7(2), 1983.
- D. A. Jaffe and J. O. Smith, "Extensions of the Karplus-Strong plucked-string algorithm", Computer Music Journal 7(2), 1983.
- A. de Cheveigné and H. Kawahara, "YIN, a fundamental frequency estimator for speech and music", J. Acoust. Soc. Am. 111(4), 2002.
- A. V. Oppenheim and R. W. Schafer, Discrete-Time Signal Processing (the real cepstrum and the minimum-phase impulse response).

## 15. The test sources and the listening pack

The tests need a string and no third-party audio: `AmpSources.Pluck` is a Karplus-Strong loop (a noise burst in a delay line with an averaging low-pass) with a first-order all-pass in the loop tuned so that the loop's phase delay at the fundamental is exactly one period (Jaffe and Smith), then a **pickup**: a comb for the pickup's place on the string and a one-pole low-pass at 5 kHz. Chords, a riff and a bass line are made from it. `NeoAmpPack.cs` (explicit, not part of the run) renders every preset on its source, 48 kHz 16-bit, about 15 s, peaks near -12 dBFS, into the session's scratch folder (never committed), with a README, and runs the click detector on each.

## 16. Limits

- The cabinets are made, not recorded: six designs from a high-pass, low-passes and peaks, with no cone breakup noise, no room and no mic proximity effect. A recorded cabinet is a `Wave`.
- **2x aliases more than the gate asks** (65 dB at -12 dBFS, 62 dB at full scale, on the worst case, a 2.5 kHz sine at the highest gain). 4x (the default) and 8x meet it. A `Quality` of 2x is for cheap tracking.
- The stage is a static curve (no memory in the curve itself beyond the filters around it), so the amp has no reactive speaker load and no grid current; the Sag is a slow envelope, not a model of a rectifier.
- The tuner follows one note: a chord shows what the tracker settles on, or nothing. Below 30 Hz and above 2 kHz it does not read.
- The wet delay at 1 kHz is the reported latency only for the clean voicings; a heavy voicing's tone stack has group delay of its own (negative and positive around its mid dip).
- A wave is used as it is (summed to mono, up to 250 ms): a long room recording is cut.
