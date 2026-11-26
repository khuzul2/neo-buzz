# Neo Mallet

Status: MAL1 (issue #187). A modal synth for mallets, bars, bells and plucks: a normal instrument (a generator), clean room. A resonator mode is later (section 10).
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
Each model has a level trim (`Trim` in `MalletModels.cs`) so that the models sit at about the same level whatever the model; the strike gain and the limiter below set the instrument's level.

## 4. The shape: a generator

Neo Mallet is a **generator**, a normal instrument like the other Neo synths: no audio input (`InputCount = 0`), notes in through the host's note input (`note_in`), 16 tracks of Note
and Velocity, MIDI and note wires. It can start a new track on an empty-space drop and replace a generator. It has no Mix and no Amount (those belong to effects; the shared helper is
not used). The first build was an effect that also played by notes (so that audio could ring the bank); the project owner decided an effect cannot be a track's instrument, so the
resonator source, the input, Mix and Amount were removed before anything shipped (the state is still version 0, its layout line rewritten).

Level: a strike's pulse gain is 0.65 and every preset's Volume is set so that the pack's melody (a tune and a bass note, velocities 0.6 to 0.9) peaks between -6 and -3 dBFS
(measured -5.1 to -4.5, under the knee: the limiter does not touch it). The output limiter is transparent below 0.7 (-3.1 dBFS: the output is exactly linear in Volume there) and rounds above it with slope 1 at the knee and a smooth approach to a ceiling of 0.89 (-1 dBFS), so four notes struck together
at full velocity stay under -1 dBFS and click-free (`TheLevelIsThatOfAnInstrumentAndAFourNoteChordDoesNotClip`).

## 5. Parameters and state

`Params.def` (16 parameters, version 0): Model, Hardness (ms), Position (%), Velocity to hardness (%), Mallet noise (%), Decay (s), Damping tilt (%), Brightness (%),
Release damping (ms), Modes (4 to 32), Stretch (cents), Inharmonicity (%), Tremolo rate (Hz), Tremolo depth (%), Stereo spread (%), Volume (dB).
New parameters are appended. Every continuous parameter is smoothed: Volume, tremolo and spread have their own ramps, the rest go through the per-tick slew and the per-sample gain glide.

## 6. Editor

House style (NeoEditor): the Model drop-down, the **mode drawing** (a line per mode on a log axis of frequency against the note, its height the mode's gain at the strike
position and Brightness, the carrier lit), Modes, Stretch and Inharmonicity; Exciter (Hardness, Position, Velocity to hardness, Mallet noise); Decay and damping (Decay, Damping tilt,
Brightness, Release damping); Tremolo; Output (Volume, Stereo spread, meter). Mac gallery shot `660-neo-mallet-editor`, dark and light, with the Church Bell preset.

## 7. Presets

Eight, generic names: Bars - Soft Wooden Bars, Bright Metal Bars, Tremolo Bars; Chimes - Hollow Tube Chimes; Bells - Church Bell; Plucked - Thumb Tines; Glass - Glass Harmonics;
Plates - Struck Steel Plate (the Plate model hit hard: a clangorous, inharmonic burst that rumbles for five seconds). Each is clearly different from the others (TheFactoryPresetsAreAudibleAndClearlyDifferent, over one-third-octave bands), and the pack's melody peaks between -6 and -3 dBFS.

## 8. Gates, as tested (`MalletTests`)

| Gate | Test | Measured |
|---|---|---|
| Fundamental within 1 cent, C2 to C7 | `TheFundamentalIsWithinOneCentOfTheNoteFromC2ToC7`, 11 models x 61 notes, FFT peak | worst 0.108 cent (Glass), 0.006 for the others |
| Every mode within 1 cent of its table ratio after stretch | `EveryModeIsWithinOneCentOf...` (engine snapshot, 32 modes, three stretch / inharmonicity settings) and `TheModesInTheAudioSitAtTheirTableRatios` (42 modes in the audio) | 0.0001 cent; 0.002 cent in the audio |
| T60 of the fundamental within 5 % of Decay | `TheFundamentalsT60IsWithinFivePercentOfDecay`, 11 models x 3 notes x 3 decays, complex demodulation | worst 0.58 % |
| No clicks at note start, release and steal, blocks 16, 64, 256 | `NoClicksAtNoteStartReleaseAndStealAtBlockSizes16And64And256` (11 models, hardness 2 and 0.3 ms, a chord, 20 notes at once, a hard All Notes Off), | 0 clicks |
| Ear-proxy: max abs second difference of the first and the last 5 ms no higher than the sharpest 5 ms of the body | `TheFirstAndLast5MsHaveNoSharperEdgesThanTheMiddle` | met, with the strike as below |
| Block size does not change the sound | `ABlockOf16And64And256GiveTheSameSamples` | bit-exact |
| Steal fade at least 3 ms | `AStolenVoiceFadesOutOverAtLeast3Ms` | 95 % to 5 % in 4.25 ms (fade 5 ms) |
| Generator: no audio input, no Mix, no Amount, 16 parameters | `TheMachineIsAGeneratorWithNoAudioInputMixOrAmount` (manifest kind, both projects, `InputCount = 0`) | met |
| Level: the pack melody -6 to -3 dBFS, a four-note chord under -1 dBFS, 0 clicks | `TheLevelIsThatOfAnInstrumentAndAFourNoteChordDoesNotClip`, `TheLimiterIsTransparentBelowItsKneeAndHoldsTheCeiling` | -5.1 to -4.5 dBFS |
| 0 bytes per block, 16 voices x 32 modes, 2000 blocks | `ARenderMakesNoGarbageWith16VoicesAnd32Modes` (parameters moving) | 0 bytes |
| CPU per voice-mode | `CpuPerVoiceModeIsReported` | 3.4 ns per voice-mode-sample; 16 voices of 32 modes use 8.3 to 8.5 % of one core at 48 kHz (this 2-core box) |
| No denormals, silent after the tail | `TheTailsLeaveNoDenormalsAndTheMachineGoesQuiet` | 0 |
| No zipper above -60 dB under automation | `AutomationMakesNoZipperNoise` (Volume, Brightness, Position, Decay, spread, tremolo moved every block on a long soft strike) | -71.6 dB under the carrier above 2 kHz |
| State and presets | `EveryParameterSurvivesAMigration...`, `TheFactoryPresetsAreAudibleAndClearlyDifferent`, `EveryFactoryPresetPlaysWithoutClicks`, the shared `PresetTests`, `StateVersionTests`, `ModulationCostTests` | pass |

### The ear-proxy, and what it means for a hard strike

A struck sound is sharpest at the strike: every mode starts together, so the first milliseconds are the most pointed part of the note, and the stronger the mallet (short contact, many
modes) the more so. The gate therefore compares the first and last 5 ms of a note with the sharpest 5 ms of its body (from 5 ms to its release), and is measured with a soft mallet
(6 ms; 12 ms for Tine and Pluck, whose contact is 0.3 times as long) where the strike is a part of the note, not its edge. For a hard mallet the first 5 ms are the attack and
exceed the body by design (up to a factor of 3 for a Tine at 2 ms contact); what the gate rules out is an artefact at the edge: the click detector (0 clicks at hardness 2 and 0.3 ms
on every model) and the smooth, corner-free fade of a steal or kill, whose last 5 ms are no sharper than the note's body.

## 9. Limits

Modes above 0.45 of the sample rate are dropped, so high notes on the bright models ring fewer than the Modes setting (at C7 every mode above the tenth ratio of a bar is gone). No per-mode detune or per-mode decay editing (the tilt and Brightness shape them all together); the Custom model is the open
series. The presets are levelled on the pack melody; a coherent strike of several notes at once peaks 3 to 5 dB above it, and the limiter rounds what passes -3.1 dBFS and holds -1 dBFS.

## 10. Resonator mode: later, needs audio into an instrument

The first design had a Resonator source: audio into the machine rings one body for each held note (the drive normalised so each mode adds the same power to white noise; a gate rising
over 5 ms and falling with Release damping; a held chord tuned white noise within 1 cent, worst 0.31 cent), so a drum loop turned into pitched ringing that followed the keys. It needs
audio going into an instrument, which the host cannot do today (an instrument has no audio input, a generator cannot take a sidechain), and an effect cannot be a track's instrument.
It comes back when the host can feed audio to an instrument, either as a sidechain input on a generator or as a separate Neo Resonator effect that takes notes and shares this engine
(the modes, the exciter and the per-tick slew are the same code); then it gets Mix and Amount like any effect. The first version's engine and tests are in the history of
`machines/neo-mallet-mal1` (head 1a7d595).
