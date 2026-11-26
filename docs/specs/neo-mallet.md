# Neo Mallet

Status: MAL1 (issue #187). A modal synth for mallets, bars, bells and plucks, with a resonator mode, one machine, clean room.
Code: `Machines/NeoMallet` (engine in `Engine/MalletEngine.cs`, mode tables in `Engine/MalletModels.cs`, parameters in `Params.def`),
gates in `NeoBuzz.MachineTests/MalletTests.cs`, guide `ReBuzz.Agent/Guides/machines/Neo Mallet.md`.

## 1. What it is

Each note rings a bank of up to 32 decaying modes. A mode is a complex one-pole oscillator, `z[n+1] = z[n] * r * e^(j w) + g * x[n]`, with `r = 10^(-3 / (T60 * fs))`; its output is the
mode's velocity (the real part of `z`, less half the input the one-step update puts into it, so a slow force leaves nothing but the ringing behind). 16 voices, up to 32 modes each,
modes above 0.45 of the sample rate dropped. A stolen voice, and every voice on a hard All Notes Off, fades out in 5 ms (a raised cosine, so the fade has no corner); the stolen
voice's new note starts when the fade has landed. Coefficients are kept in double, slewed per 16-sample tick and cast to float; the drive gain of a mode glides per sample inside the
tick, so no parameter change steps what a mode is fed (no zipper above -60 dB under automation). The output is block-size independent for blocks that are multiples of 16.

Sources: modal synthesis as a bank of decaying resonators (Adrien 1991; Cook, *Real Sound Synthesis*); the mode ratios from beam, string, plate and bell theory and from the
measurements in Fletcher and Rossing, *The Physics of Musical Instruments*, and Rossing, *Science of Percussion Instruments*. The tables are written here from those and
commented with their source in `MalletModels.cs`. No other product's code, tables or presets were used.

## 2. The exciter

- **Mallet:** a raised-cosine force pulse of area 1 whose contact time is Hardness (12 down to 0.3 ms, exponential), scaled by `8^(-(2v-1) * VelHard)` for velocity `v` (so a hard hit is also a harder
  hit); a plucked model (Tine, Pluck) strikes 0.3 times as long; the contact is clamped to 0.08 to 30 ms. A little noise rides on the pulse (Mallet noise, multiplicative, so it is as smooth as the pulse).
- **Position** weighs mode `n` by `sin(n pi x)` (cosine for the second half of a Glass doublet), keeping its sign (neighbouring modes move in opposite senses, so the strike does not
  start every mode in phase and add up to one sharp pulse), with a floor of 3 % so a mode on a node is 30 dB down, not gone. Bells, glass and plates use their own shape numbers.
- **Resonator source:** no exciter; the audio in drives each held note's bank through a gate that rises over 5 ms (and falls with Release damping). The drive is normalised so that each mode adds the same power to white noise.

## 3. The models

| Model | Ratios (to the note) | Source |
|---|---|---|
| Soft bar | 1 : 4 : 10, then the free bar's spacing | undercut (tuned) bar, Fletcher and Rossing |
| Hard bar | 1 : 3 : 6, then the free bar's spacing | the 1 : 3 : 6 bar |
| Metal bar | 1 : 4 : 10 with a long tail, for Tremolo | as above |
| Bright bar | 1 : 2.756 : 5.404 : 8.933 : 13.344 ... | free-free bar (Euler-Bernoulli beam) |
| Tube | near-harmonic, slightly flat upper modes | open tube with end correction |
| Bell | 0.5, 1, 1.183, 1.506, 2, ... (hum, prime, tierce, quint, nominal) | Rossing, church bell partials; the prime carries the note |
| Tine | 1 : 6.267 : 17.55 : 34.39 | clamped-free bar |
| Pluck | `n sqrt(1 + B n^2)`, `B = 0.02 * Inharmonicity^2` | stiff string (Fletcher) |
| Glass | ring modes `n (n^2 - 1) / sqrt(n^2 + 1)` as doublets 0.35 % apart | thin ring (Rayleigh, Leissa) |
| Plate | square-plate (m, n) modes, sorted | Leissa |
| Custom | `n * 2^((n-1) * Stretch / 1200) * sqrt(1 + B n^2)` | Stretch in cents per mode step |

Whatever the table, the mode that carries the note (the first, or the prime for the bell, or the first of a stiff string) is tuned to the note: the engine divides every ratio by the carrier's.
Each model has a level trim (`Trim` in `MalletModels.cs`) so that a single note's peak lands at about -9.5 dBFS at Volume 0 dB whatever the model.

## 4. The single-machine shape, and why

The brief asks for audio input that rings the bank, and a host that cannot always feed audio into a generator. Neo Mallet is therefore **one effect that also plays by notes**:
it has one audio input and one output, takes notes through the host's note input (`note_in`), and has the shared Mix and Amount (the `FxStage` helper). On a track it plays as an
instrument (Mix 100 %: only the modes; the input is silent); with `Source` on Resonator the input is what rings the held notes' banks (Mix below 100 % keeps the input beside the ringing).
The alternatives were a generator plus a separate effect (two machines, two presets, and the resonator one could not hold notes without a second input) or a generator with a hidden
sidechain (not supported by the host's wiring). One machine means one preset set, one editor and one state, and it is the same code path in both sources. The cost: at Amount 0 % the
machine passes its input bit for bit, so the notes play no sound; Amount, as for every effect, is the effect strength, and 0 % is dry. This is stated in the guide.
Mix and Amount are latency compensated; the machine has no latency (the dry is not delayed).

## 5. Parameters and state

`Params.def` (20 parameters, version 0): Model, Source, Hardness (ms), Position (%), Velocity to hardness (%), Mallet noise (%), Decay (s), Damping tilt (%), Brightness (%),
Release damping (ms), Modes (4 to 32), Stretch (cents), Inharmonicity (%), Tremolo rate (Hz), Tremolo depth (%), Input drive (dB), Stereo spread (%), Volume (dB), Mix (%), Amount (%).
New parameters are appended. Amount scales the decay time of the modes and the drive of the input at once (the `AmountRules`), so 100 % is the settings as they stand, 200 % rings longer and
harder, and 0 % is the dry input bit for bit. Every continuous parameter is smoothed: Volume, tremolo and spread have their own ramps, the rest go through the per-tick slew and the per-sample gain glide.

## 6. Editor

House style (NeoEditor): the Model drop-down and Source switch, the **mode drawing** (a line per mode on a log axis of frequency against the note, its height the mode's gain at the strike
position and Brightness, the carrier lit), Modes, Stretch and Inharmonicity; Exciter (Hardness, Position, Velocity to hardness, Mallet noise); Decay and damping (Decay, Damping tilt,
Brightness, Release damping); Tremolo; Resonator (Input drive, Mix, Amount); Output (Volume, Stereo spread, meter). Mac gallery shot `660-neo-mallet-editor`, dark and light, with the Church Bell preset.

## 7. Presets

Eight, generic names: Bars - Soft Wooden Bars, Bright Metal Bars, Tremolo Bars; Chimes - Hollow Tube Chimes; Bells - Church Bell; Plucked - Thumb Tines; Glass - Glass Harmonics;
Resonator - Resonated Drums (Source Resonator over a drum loop). Each is different from the others by at least 11 dB RMS over one-third-octave bands, and peaks between -3 and -6 dBFS on the preset test's held four-note chord.

## 8. Gates, as tested (`MalletTests`)

| Gate | Test | Measured |
|---|---|---|
| Fundamental within 1 cent, C2 to C7 | `TheFundamentalIsWithinOneCentOfTheNoteFromC2ToC7`, 11 models x 61 notes, FFT peak | worst 0.108 cent (Glass), 0.006 for the others |
| Every mode within 1 cent of its table ratio after stretch | `EveryModeIsWithinOneCentOf...` (engine snapshot, 32 modes, three stretch / inharmonicity settings) and `TheModesInTheAudioSitAtTheirTableRatios` (42 modes in the audio) | 0.0001 cent; 0.002 cent in the audio |
| T60 of the fundamental within 5 % of Decay | `TheFundamentalsT60IsWithinFivePercentOfDecay`, 11 models x 3 notes x 3 decays, complex demodulation | worst 0.58 % |
| No clicks at note start, release and steal, blocks 16, 64, 256 | `NoClicksAtNoteStartReleaseAndStealAtBlockSizes16And64And256` (11 models, hardness 2 and 0.3 ms, a chord, 20 notes at once, a hard All Notes Off), and the resonator's | 0 clicks |
| Ear-proxy: max abs second difference of the first and the last 5 ms no higher than the sharpest 5 ms of the body | `TheFirstAndLast5MsHaveNoSharperEdgesThanTheMiddle`, `...Resonator...` | met, with the strike as below |
| Block size does not change the sound | `ABlockOf16And64And256GiveTheSameSamples` | bit-exact |
| Steal fade at least 3 ms | `AStolenVoiceFadesOutOverAtLeast3Ms` | 95 % to 5 % in 4.25 ms (fade 5 ms) |
| Resonator: Amount 0 bit-exact; Mix, Amount latency 0 | `AmountZeroIsBitExactDry`, `AmountFadesTheRingingIn...`, `MixIsADryWetBlendWithNoLatency` | bit-exact, latency 0 |
| A held chord tunes white noise within 1 cent | `AHeldChordTunesWhiteNoiseIntoItsPitchesWithinOneCent` (C E G, 8 to 40 s of noise, Welch average) | worst 0.31 cent |
| 0 bytes per block, 16 voices x 32 modes, 2000 blocks | `ARenderMakesNoGarbageWith16VoicesAnd32Modes` (both sources, parameters moving) | 0 bytes |
| CPU per voice-mode | `CpuPerVoiceModeIsReported` | 3.4 ns per voice-mode-sample; 16 voices of 32 modes use 8.3 to 8.5 % of one core at 48 kHz (this 2-core box) |
| No denormals, silent after the tail | `TheTailsLeaveNoDenormalsAndTheMachineGoesQuiet` | 0 |
| No zipper above -60 dB under automation | `AutomationMakesNoZipperNoise` (Volume, Brightness, Position, Decay, drive, spread, tremolo, Mix moved every block) | -71.6 dB under the carrier above 2 kHz |
| State and presets | `EveryParameterSurvivesAMigration...`, `TheFactoryPresetsAreAudibleAndClearlyDifferent`, `EveryFactoryPresetPlaysWithoutClicks`, the shared `PresetTests`, `StateVersionTests`, `ModulationCostTests` | pass |

### The ear-proxy, and what it means for a hard strike

A struck sound is sharpest at the strike: every mode starts together, so the first milliseconds are the most pointed part of the note, and the stronger the mallet (short contact, many
modes) the more so. The gate therefore compares the first and last 5 ms of a note with the sharpest 5 ms of its body (from 5 ms to its release), and is measured with a soft mallet
(6 ms; 12 ms for Tine and Pluck, whose contact is 0.3 times as long) where the strike is a part of the note, not its edge. For a hard mallet the first 5 ms are the attack and
exceed the body by design (up to a factor of 3 for a Tine at 2 ms contact); what the gate rules out is an artefact at the edge: the click detector (0 clicks at hardness 2 and 0.3 ms
on every model) and the smooth, corner-free fade of a steal or kill, whose last 5 ms are no sharper than the note's body.

## 9. Limits

Modes above 0.45 of the sample rate are dropped, so high notes on the bright models ring fewer than the Modes setting (at C7 every mode above the tenth ratio of a bar is gone). Resonator mode needs held notes and audio on the input;
there is no sustain from a single audio burst without a key. No per-mode detune or per-mode decay editing (the tilt and Brightness shape them all together); the Custom model is the open
series. The preset levels are set on the 4-note chord of the preset test and, with a coherent strike on a chord, the peak is 6 to 15 dB above the ring's RMS.
