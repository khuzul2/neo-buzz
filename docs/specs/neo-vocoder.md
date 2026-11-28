# Neo Vocoder

| | |
|---|---|
| Status | Built (VOC1, issue #195): engine, editor, 8 presets, agent guide, gates in `NeoBuzz.MachineTests/NeoVocoderTests.cs`, listening-pack generator `NeoVocoderPack.cs`. The sound is for the user to approve. |
| Branch | `machines/neo-vocoder-voc1` |
| Code | `Machines/NeoVocoder/` (`NeoVocoder.cs`, `NeoVocoderEditor.cs`, `Params.def`, `Presets.txt`, `Engine/VocoderEngine.cs`, `Engine/VocoderSynth.cs`), the filter bank `Machines/NeoBuzz.Dsp/VocoderBank.cs`, the two flavours in `Machines/Portable/NeoVocoder` and `Machines/Studio/NeoVocoder` |
| Builds on | `FxStage` (Mix and Amount), `Biquad`, `VoiceAllocator`, `Adsr`, `Rng`, `PortableMath`, `LevelMeter`, the Neo machine framework (`docs/specs/machines.md`) |
| Clean room | Built from the published channel-vocoder literature (section 9) and our own DSP. No code, table or preset of another vocoder; the machine in the same product that has a vocoder in an effects rack (Dye Essence) was only measured for CPU, not read for design. |

## 1. What it is

A channel vocoder with zero latency. Input 1 is the **modulator** (a voice, drums, anything), summed to mono. Its spectrum is measured in 8 to 40 log-spaced bands, and each band's level scales the same band of a **carrier**:

| Carrier | What it is |
|---|---|
| Synth | the machine's own 8-voice synth: two detuned band-limited oscillators per voice (saw to pulse), a noise blend, glide, an amplitude envelope; played by notes like any instrument (a note wire, live MIDI, the note tracks) |
| Sidechain | whatever is wired into input 2, stereo, sample-exact; with nothing wired the wet sound is silence and the editor says "Wire a carrier into the sidechain" |
| Noise | white noise (a whisper; no notes needed) |

Controls (the order is the state's order; a new one is only ever appended): `Carrier`, `Wave` (0 saw to 100 % pulse), `Noise blend`, `Detune` (0 to 50 cents), `Glide` (0 to 1000 ms), `Synth attack`, `Synth decay`, `Synth sustain`, `Synth release`, `Bands` (8 to 40), `Low` (50 to 500 Hz), `High` (2 to 16 kHz), `Width` (50 to 200 %), `Attack` (0.5 to 100 ms), `Release` (5 to 1000 ms), `Formant` (-12 to +12 st), `Sibilance` (0 to 100 %), `Freeze`, `Spread` (0 to 100 %), `Mix`, `Amount` (0 to 200 %) and `Output` (-24 to +24 dB). State: versioned, version 0 (`Layouts/state-layouts.txt`). Latency 0 at every setting.

## 2. The filter bank

`Bands` centres are log-spaced from `Low` to `High`: centre i is Low x (High / Low)^(i / (bands - 1)). Each band is **two identical RBJ band-pass biquads in series** (a 4th-order band-pass) on the modulator and another pair on the carrier. The Q follows the spacing: the cascade's -3 dB width is 0.6436 of one stage's, so the stage Q is set for a bandwidth of `Width` x spacing in octaves. The carrier's bands sit at the modulator's centres times 2^(`Formant` / 12) (the formant shift: the carrier's filters move, the carrier's pitch does not). `Low`, `High`, `Width` and `Formant` glide over 20 ms (the filters are redesigned every 64 samples while one moves); a change of `Bands` or `Carrier` fades the sound out over 4 ms, makes the change and fades in.

**Levelling.** Every band is levelled so that a white-noise carrier of the nominal level (0.25 RMS) in a flat modulator gives back the modulator's level: each band's exact white-noise power gain is computed (Simpson quadrature of the band's response over the unit circle, 64 panels) and divided out. The bands are summed with **alternating signs**: adjacent 4th-order band-passes overlap with phases that make the plain sum dip by 6 to 9.5 dB between and at centres at the default width; with alternating signs the same overlap sums level-flat (measured +0.5 to +1.4 dB across 8, 16 and 40 bands at `Width` 100 %), and a fixed -0.8 dB makeup and a +1.5 dB trim of the top band (which has no upper neighbour) take the rest. At other widths the composite level moves a little: +1 dB at 50 %, about -3 dB at 150 %.

**Sidechain carrier.** A carrier of unknown level is evened out inside each band: the band's power is followed twice (over a few periods of the band, at least 4 ms, and over 150 ms) and the larger is divided out, so a swell is levelled and a drop is not boosted. A synth or noise carrier has a known level and skips this.

## 3. The followers

Each modulator band is rectified, smoothed by two one-poles at c/2 Hz (so the ripple at twice the centre frequency does not ride on the follower; it also makes a noise band read its RMS, not its peaks, to within 0.06 dB), and followed with an attack and a release coefficient 1 - exp(-ln 9 / (t x rate)): `Attack` and `Release` are the **10 to 90 % times** of a step. Below about 300 Hz the band's own period limits how fast it can follow.

## 4. The synth

Eight voices (`VoiceAllocator`, polyphony 8, the oldest or quietest released one stolen first, a steal fades out over 2 ms before the new note starts). Two oscillators per voice, PolyBLEP saw and pulse (Valimaki and Huovilainen), detuned apart by `Detune` and spread left and right (.75/.25), a noise blend that follows the note's envelope, a fixed pre-emphasis (about +6 dB per octave above 40 Hz) that levels a saw's falling harmonics so every band gets about what white noise would give it, a per-voice sqrt(220 Hz / pitch) so the level does not follow the pitch, and a chord normalisation of 1 / sqrt(voices sounding), smoothed over 50 ms, so a chord is about as loud as a note (measured against the noise carrier: +0.1 dB for one note, +3.3 dB for eight). Velocity is a linear amplitude (127 over 32 is 11.97 dB). Pitch bend +-2 st. `Glide` is portamento at every note.

## 5. Around the bank

`Sibilance`: the modulator through a 4th-order Butterworth high-pass at 5 kHz, added to both channels. `Freeze`: the band followers stop; switched on over a sound the hold is immediate, switched on with nothing heard it waits for the first sound and holds what its first 150 ms showed (so a preset with Freeze on is not silent). `Spread`: odd bands (1st, 3rd ...) to the left, even bands to the right; at 100 % each side gets its own bands only, with a gain of sqrt(2 / (1 + (1 - s)^2)) so the level stays. `Output` is a smoothed gain.

## 6. Mix, Amount, Output

`Mix` is the dry/wet through the shared `FxStage` (no latency to compensate). `Amount` (0 to 200 %, 100 % = the settings as they stand) scales **`Formant`, `Sibilance` and `Spread`** from 0 (200 % doubles them, held to their ranges); 0 % is the modulator bit for bit (the helper's guarantee), as is `Mix` 0. `Output` is the level of the wet sound. Every control is smoothed (20 ms) or glides; 0 bytes are allocated per block; the states and band levels are flushed each block and the output is flushed (no denormals in a 30 s tail).

## 7. The editor

One source for WPF and Avalonia: four sections, Carrier (the choice, the synth's tone and envelope), Bands (a live picture of every band's modulator level, dimmed, and output level, in the accent colour, in dB on the bands' own log-frequency axis, with `Bands`, `Low`, `High`, `Width` under it), Voice (`Attack`, `Release`, `Formant`, `Sibilance`, `Spread`, `Freeze`) and Output (`Amount` and `Mix` side by side, `Output`, input and output meters). With `Carrier` on Sidechain and nothing wired the picture says "Wire a carrier into the sidechain" in the warning colour.

## 8. The gates (all in `NeoVocoderTests.cs`)

| Gate | Test | Result |
|---|---|---|
| Band centres within 1 %, each filter peaks at its centre | `BandCentresAreLogSpaced...` (8, 16, 40 bands) | the centres are exact; the filters peak within 1 % |
| A white carrier follows a shaped modulator within +-1.5 dB per band, Low to High | `AWhiteCarrierFollows...` (16 and 40 bands; flat and shaped; Noise and Sidechain carriers; 24 s, 32768-point Welch, windows +-0.3 spacing around each centre) | worst band 0.53 to 1.23 dB |
| Envelope match, a vowel on a saw carrier, correlation >= 0.9 | `AVowelOnASawCarrier...` | 0.92 |
| +12 st moves the spectral peaks an octave up within 3 % | `PlusTwelveSemitones...` | 2.000 |
| Follower times within 10 % | `AFollowersAttackAndRelease...` (the follower, 1/10/50 ms) and `ABandAboveTwoKilohertz...` (a 3 kHz burst, 20/60 and 40/120 ms) | exact; the band reads 21.5 (of 20) and 43.1 (of 40) ms attack and 60.0, 120.1 ms release |
| Notes: 8-note chords, velocity, 0 clicks at starts, ends and glides | `AnEightNoteChord...`, `NoteStartsNoteEndsAndGlides...`, `ANewNoteAtEveryVoiceSteal...`, and the shared `NoteContractTests` row | 8 voices; 11.97 dB for 127 over 32; 0 clicks with and without glide |
| Sidechain sample-exact; empty gives silence and the message | `TheSidechainCarrierIsSampleExact` (shifts 37, 256, 1000), `AnEmptySidechain...` | difference 0.0; peak 0 |
| Mix and Amount | `AmountZeroAndMixZero...` | bit-exact dry at Amount 0 and Mix 0; Formant 6 st becomes 0, 3, 6, 12 st at 0, 50, 100, 200 % |
| Audio thread | `NothingIsAllocatedPerBlock...` (2000 blocks, notes, parameters, band counts and carriers moving), `NoDenormalsRingOut...` (34 s), `AudioPathsUseNoLibm...` | 0 bytes; 0 denormals; portable maths only |
| CPU, 40 bands, 48 kHz stereo, against Dye Essence's vocoder FX (its most is 32 bands) | `TheCpuOfFortyBands...` | see the report on the issue (the test prints both) |
| Presets audible and different, parameter moving cost, state layout | `PresetTests`, `ModulationCostTests`, `StateVersionTests` | pass |

## 9. Literature

- H. Dudley, "Remaking speech", J. Acoust. Soc. Am. 11 (1939) 169-177 (the channel vocoder: band-pass analysis, envelope followers, a synthesis bank on a carrier). See also H. Dudley, "The vocoder", Bell Laboratories Record 18 (1939).
- J. L. Flanagan, Speech Analysis, Synthesis and Perception, Springer (1965; 2nd ed. 1972), the chapters on channel vocoders.
- L. R. Rabiner and R. W. Schafer, Digital Processing of Speech Signals, Prentice-Hall (1978), the filter-bank (channel) vocoder.
- R. Bristow-Johnson, "Cookbook formulae for audio EQ biquad filter coefficients", the band-pass with constant 0 dB peak gain.
- S. Butterworth, "On the theory of filter amplifiers", Experimental Wireless and the Wireless Engineer 7 (1930) (the sibilance high-pass).
- V. Valimaki and A. Huovilainen, "Antialiasing oscillators in subtractive synthesis", IEEE Signal Processing Magazine 24(2) (2007) (the PolyBLEP oscillators).
- D. Giannoulis, M. Massberg and J. D. Reiss, "Digital dynamic range compressor design - a tutorial and analysis", J. Audio Eng. Soc. 60(6) (2012) (attack and release as 10 to 90 % style time constants of one-pole followers).
- P. D. Welch, "The use of fast Fourier transform for the estimation of power spectra", IEEE Trans. Audio and Electroacoustics 15(2) (1967) (the gate's spectra).

Dudley (1939), Valimaki and Huovilainen (2007) and Giannoulis et al. (2012) were checked against web search results; the others are standard references cited from their well-known details (no page numbers beyond those given).

## 10. Limits

- The bank is not a speech coder: no pitch tracking, no voiced/unvoiced decision. Consonants come through the bands and `Sibilance`, so a very noisy modulator in the top bands sounds like noise.
- Bands under about 300 Hz follow slower than `Attack` asks (their own period); the follower times are exact at 2 kHz and above.
- The composite level depends on `Width`: -3 dB at 150 %, +1 dB at 50 %.
- The sidechain carrier's per-band levelling takes 150 ms to follow a long drop in the carrier's level; a swell is followed in a few periods.
- Freeze holds band levels, not the carrier: a frozen vowel changes as the notes do.
- The machine cannot tell an unconnected second input from a silent one (the host may give a silent buffer for either), so "wired" means that a signal has arrived on it: until then the editor says "Wire a carrier into the sidechain". The output is silent either way, since a silent carrier gives a silent band.
