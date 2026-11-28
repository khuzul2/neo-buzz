# Neo Split

| | |
|---|---|
| Status | Built (SPL1, issue #201): engine, editor, 8 presets, agent guide, gates in `NeoBuzz.MachineTests/NeoSplitTests.cs` and `ReBuzz.Studio.Tests/NeoSplitRoutingTests.cs`, listening-pack generator `NeoSplitPack.cs`. The sound is for the user to approve. |
| Branch | `machines/neo-split-spl1` (on MO1, `engine/multi-output-mo1`) |
| Code | `Machines/NeoSplit/` (`NeoSplit.cs`, `NeoSplitEditor.cs`, `Params.def`, `Presets.txt`, `Engine/SplitEngine.cs`, `Engine/SplitDesign.cs`, `Engine/SplitHandles.cs`), the two flavours in `Machines/Portable/NeoSplit/` and `Machines/Studio/NeoSplit/`, the new shared `Machines/NeoBuzz.Dsp/LrBands48.cs` |
| Builds on | `LrBands` (24 dB/oct crossovers), `TransientSplit`, `LevelMeter`, `LinearRamp`, `PortableMath`, the several outputs of `docs/specs/multi-output.md` (MO1), the Neo machine framework (`docs/specs/machines.md`) |
| Clean room | From published filter theory (below) and our own DSP. Nothing is copied from another product. |

## 1. What it is

One stereo input and four outputs, each wired to a chain of its own. The outputs add up to the input. It routes; it does not change the sound.

**No Mix, no Amount.** Every effect has both (AGENTS.md section 3) except meters and analysers, which pass audio unchanged. Neo Split is exempt in the same spirit: its outputs summed are the input (Bands: flat in level, the phase turned by the crossovers' allpass; Transient / Body: the input delayed by the reported latency; Mid / Side: the input), so a Mix or an Amount would only break that. The processing is in the chains the outputs go to, and those have both.

| Mode | Out 1 | Out 2 | Out 3 | Out 4 |
|---|---|---|---|---|
| Bands, 2 | Low | High | silent | silent |
| Bands, 3 | Low | Mid | High | silent |
| Bands, 4 | Low | Low Mid | High Mid | High |
| Transient / Body | Transient | Body | silent | silent |
| Mid / Side | Mid | Side | silent | silent |

Controls (the order is the state's order; a new one is only ever appended): `Mode`, `Bands` (2 to 4), `Slope` (24 or 48 dB/oct), `Crossover 1` to `3` (20 Hz to 20 kHz), `Sensitivity` (%), `Transient length` (2 to 100 ms), `Body smoothing` (0 to 100 ms). State version 0 (`Layouts/state-layouts.txt`).

## 2. Output names (decision)

The studio reads a machine's output names once, when `OutputChannelCount` is set (`MachineCore.outputChannelNames`), and does not ask again when it draws the Inspector picker, the mixer strip or the agent's port list. A name that changed with the mode would be stale. So the outputs are declared as **Out 1 to Out 4** and what each carries is shown where it can change: on the editor's meters (labelled with the mode's name for the output) and in the guide's table. The agent connects by `Out n` or by the number.

## 3. Bands

Linkwitz-Riley crossovers, in a cascade: band 1 = L1, band 2 = H1·L2, band 3 = H1·H2·L3, band 4 = H1·H2·H3, where a lower band also passes the allpasses of the higher crossovers so that the bands sum to a chain of allpasses (flat in level).

- **24 dB/oct** is the existing `LrBands` (LR4: two 2nd-order Butterworth sections run twice).
- **48 dB/oct** is the new `LrBands48` (LR8: a 4th-order Butterworth low-pass or high-pass run twice; the two sections have 1/Q = 2 sin(pi/8) and 2 sin(3 pi/8)). With B the 4th-order Butterworth polynomial, LP^2 + HP^2 = B(-s)/B(s): the pair sums to the 4th-order Butterworth allpass, so the sum stays flat. Seven state-variable sections per crossover and channel, in double.
- **12 dB/oct is not offered.** A 12 dB/oct Linkwitz-Riley pair (LR2) sums flat only with one band's polarity flipped; the outputs must sum to the input, so it is left out. The brief allowed this: "only if the sum stays flat".
- **Crossovers** are kept in order and a third of an octave apart (a ratio of 2^(1/3)) in the engine and on the editor's handles. A moved crossover glides (a one-pole in log frequency, 20 ms, the coefficients every 16 samples).
- **Bilinear warp.** The sections are topology-preserving state-variable filters, so a high crossover is slightly steeper than the analogue design near Nyquist. The sum is flat to the last digit anyway (the same sections feed both sides).
- **Fewer bands.** The outputs are a matrix of twelve sources (four bands of the 24 dB/oct cascade, four of the 48, the transient, the body, the mid, the side) with a 20 ms linear ramp on every gain, so a change of mode, band count or slope crossfades. Two bands are band 1 and the sum of 2 to 4; three are 1, 2 and the sum of 3 and 4 (the unused crossovers add only a common allpass).
- Latency 0.

## 4. Transient / Body

`TransientSplit` (docs/specs/mastering-tools.md section 4.2) on the input now gives a weight w; the audio is delayed by a 2 ms look-ahead (96 samples at 48 kHz), so the weight arrives before the onset. With t = min(1, 2 w): **Transient = d · t, Body = d - Transient**, d the delayed input, so the sum is exact (the difference is computed from the same float, 1e-7).

- `Transient length` L sets the followers: fast attack 0.3 ms, fast release 0.5 L, slow attack L, slow release 2 L.
- `Sensitivity` sets the deadband, 2 + 10 (1 - s) dB: at 50 % a rise has to be 7 dB over the sustained part to count.
- `Body smoothing` is a release-only one-pole on t: the weight falls over that time, so the body comes back slowly (0: at once).
- Latency 96 samples at 48 kHz (2 ms), reported to the host (`GetLatency`, read again every tick), the same on all outputs. In the other modes it is 0; the host compensates the change.

## 5. Mid / Side

Out 1 = (M, M), Out 2 = (S, -S), M = (L + R) / 2, S = (L - R) / 2; Out 1 + Out 2 = (L, R). No parameters. A mono input gives S = 0 exactly (L - R is exactly 0).

## 6. Unwired outputs, audio thread

The engine always computes all four outputs and the meters, wired or not; only the write to the host's buffer is skipped when an output has none (the host hands `null` for an unwired output). So the filters and the detector run on whatever is wired, and a wire added during playback starts from the same state as one made at the start. (A split with no wired output at all is not run by the host; the test therefore adds the new wire to a split that has another.) 0 bytes per block, state flushed under 1e-30 and outputs under 1e-18, `PortableMath` only, fixed-order sums.

## 7. The editor

Mode tabs; for Bands a frequency strip (20 Hz to 20 kHz, log) drawn with the four bands and one draggable handle per crossover (Shift: fine; the wheel moves a twelfth of an octave; the handles stop a third of an octave from a neighbour), the Bands and Slope choices and three knobs; for Transient / Body three knobs; for Mid / Side a note. Four meters, one per output, each labelled with what it carries in the mode (unused outputs are dimmed).

## 8. The gates

| Gate | Test | Result |
|---|---|---|
| Bands sum flat within 0.1 dB, 20 Hz to 20 kHz, 2/3/4 bands, both slopes | `Bands_Sum_IsFlatWithin0Point1DbFrom20HzTo20kHz` | within 0.0001 dB in all 16 cases |
| Each band -6 dB at its crossover | `Bands_EachBandIsMinus6DbAtItsCrossover` | -6.01 to -6.03 dB |
| One octave away within 1 dB of the nominal slope | `Bands_OneOctaveFromACrossoverIsWithin1DbOfTheSlope` | 24 dB/oct: -24.6 to -24.8 dB; 48 dB/oct: -48.2 to -48.5 dB |
| Matches the response the editor draws | `Bands_MatchTheDesignTheEditorDraws` | within 0.15 dB (0.4 dB at 48) above -20 dB |
| Transient / Body and Mid / Side sum to the input within 1e-6 | `TransientBody_And_MidSide_SumToTheInputWithin120Db` | -144.5 dB (Mid / Side), -150.5 dB (Transient / Body, 96 samples late) |
| Mono Mid / Side: Side exactly 0 | `MidSide_AMonoInputGivesASideOfExactlyZero` | exactly 0 |
| Drum loop: transient peak within 2 ms of each hit | `TransientBody_TheTransientOutputPeaksWithEachHitOfTheDrumLoop` | 17 hits, 0.00 ms, at least 0.57 of the hit's level |
| Sustained pad: transient 40 dB under the input | `TransientBody_ASustainedPadLeavesTheTransientOutput40DbUnderTheInput` | under -200 dB in every 50 ms window |
| No zipper noise above -60 dB with a crossover moving | `Bands_AMovingCrossoverMakesNoZipperNoise` | spurs -71.7 / -67.2 dB (24), -65.4 / -61.9 dB (48) under the tone |
| 0 bytes over 2000 blocks, no denormals | `AudioThread_*`, `NeoSplitRoutingTests` | 0 bytes; no subnormal in the state or the outputs |
| Mode, band count or slope changed during playback: no click | `ChangingTheModeTheBandsOrTheSlopeDuringPlaybackDoesNotClick` | 0 clicks |
| Each output to its own chain; the mix is their sum | `EachOutputGoesToItsOwnChainAndTheMixIsTheirSumToTheLastBit` | the pitches 110, 220, 440 Hz on Out 1, 2, 3; mix = sum of the four, 0 samples differ |
| Wiring Out 4 during playback | `WiringOutputFourDuringPlaybackStartsWithoutAClickAndAllocatesNothing` | the wire hears bit for bit what a wire from the start hears; 0 bytes |
| Save and load keep each wire's output | `SaveAndLoadKeepEachWiresOutput` | each wire on its output; renders bit for bit |
| The agent connects `Out n` or n | `TheAgentConnectsEachOutputByOutNOrByNumber` | yes |

## 9. Literature

- S. H. Linkwitz, "Active crossover networks for noncoincident drivers", J. Audio Eng. Soc. 24 (1), 1976 (the crossover Linkwitz and Riley described: the squared Butterworth pair, -6 dB at the crossover, in phase). Checked against the AES E-Library entry (author, title, year).
- S. Butterworth, "On the theory of filter amplifiers", Wireless Engineer 7 (1930).
- S. P. Lipshitz and J. Vanderkooy, "A family of linear-phase crossover networks of high slope derived by time delay", J. Audio Eng. Soc. 31 (1/2), 1983 (the sums of high-slope crossover pairs and their allpass sums).
- V. Zavalishin, The Art of VA Filter Design (Native Instruments, 2012), for the topology-preserving state-variable section.
- A. D. Blumlein, British Patent 394,325 (1931, granted 1933), "Improvements in and relating to sound-transmission, sound-recording and sound-reproducing systems" (the sum-and-difference, mid and side, pair).

## 10. Limits

- Bands is flat in level, not in phase: the allpass of the crossovers turns the phase, so a chain that is mixed back should leave the bands at unity and linear. Transient / Body and Mid / Side sum sample for sample.
- No 12 dB/oct slope (see section 3).
- The names are Out 1 to Out 4 (section 2).
- Transient / Body finds hits by the rise of the level; a quiet hit right after a loud one may stay on Out 2.
- A split with no output wired is not run by the host.
