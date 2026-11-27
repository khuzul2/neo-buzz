# Neo Keys

Status: KEY1 (issue #193). Electric keys by physical modelling: a normal instrument (a generator), clean room. Three models: a struck tine beside a tone bar read by a magnetic pickup, a struck steel reed read by an electrostatic pickup, and a struck string with a damper read by two magnetic pickups.
Code: `Machines/NeoKeys` (engine in `Engine/KeysEngine.cs`, mode tables and pickup curves in `Engine/KeysModels.cs`, parameters in `Params.def`),
gates in `NeoBuzz.MachineTests/KeysTests.cs`, listening pack in `KeysPack.cs`, guide `ReBuzz.Agent/Guides/machines/Neo Keys.md`.

## 1. What it is

Each note is a hammer pulse that rings a small bank of decaying modes (a complex one-pole resonator each, as in Neo Mallet), read through a pickup curve, then a master chain: drive, tone tilt, tremolo with auto-pan, a soft limiter.
16 voices; a stolen voice, and every voice on a hard All Notes Off, fades out in 5 ms. The sustain pedal keeps released notes ringing until it lifts. Modes above 0.45 of the sample rate are dropped.
Coefficients are kept in double, slewed per 16-sample tick; Volume, Pickup, Bar, Drive, Tone and the tremolo have their own ramps, so there is no zipper noise under automation.

## 2. The models

| Model | Modes | How the weights come out |
|---|---|---|
| Tine | a clamped-free bar, ratios 1 : 6.267 : 17.55 : 34.39, and a slow partial 2 cents off the note (the tone bar) with 2.6 times the T60 | each bar mode weighs shape at the hammer x shape at the pickup / ratio (displacement of an impulse-driven mode falls as 1 / f) |
| Reed | the same bar, hammer and pickup placed differently, a slow partial on the note (the body) at 1.7 times the T60, faster decay of the high modes | as above; the pickup curve is odd |
| String | a stiff string, partial n at `n f sqrt(1 + B n^2)` (B = 0.0004), 12 partials and a second polarisation 1 cent off with 2.8 times the T60 | partial n is driven with `sin(n pi beta)` / n at the strike and read with `sin(n pi x)` at each pickup (neck 0.26, bridge 0.075 of the length) |

Every mode carries its own T60 as a multiple of Decay (the note's own mode is exactly Decay). The String's Mute shortens the decay to an eighth and damps the high partials much faster.

## 3. The pickup: where the bark is

A magnetic pickup's voltage is the rate of change of the flux through its coil, and the flux falls with the gap to the vibrating part. The model is `flux(y) = y / (1 - a y)` of the (soft-clipped) displacement `y`,
with `a` the closeness (Pickup, 0 to 100 %), and the output is the difference of the flux from one sample to the next (the first-order antiderivative form, so the curve's harmonics do not alias).
At a far pickup the curve is straight and the output is the velocity of the tine; close, a large swing is bent and makes harmonics, and as the note dies the swing falls and the bark with it. The electrostatic reed pickup is
push-pull, `flux(y) = y / (1 - (a y)^2)`, an odd curve: odd harmonics only. The Drive stage is a tanh with first-order antiderivative anti-aliasing and bypassed (state reset) at 0 dB.

## 4. The hammer, noise and master chain

- **Hardness** is the contact time of a Gaussian-times-Hann pulse normalised at the note's own frequency (6 ms down to 0.15 ms); **Touch** scales the time by velocity (up to 6 times shorter at velocity 127 and 6 times longer at a soft press); the **Velocity curve** raises velocity to `2^curve`. The hammer's amplitude is its speed to the power 1.25, which makes 30 to 127 about 15 dB at the note's own mode.
- **Key noise** is two short shaped-noise thumps, at the strike and at the damper, scaled by velocity; a note that has already died away has no damper thump.
- **Release** is the damper time (8 ms to 1.5 s) after the key goes up; the pedal holds it off.
- **Tone** is a tilt around 1 kHz (plus or minus 6 dB), **Tremolo** is a level swing of rate 0.5 to 12 Hz and depth 0 to 100 %, **Auto-pan** moves the swing to opposite channels.

## 5. Parameters and state

`Params.def` (17 parameters, version 0): Model, Hardness (ms), Touch (%), Velocity curve (%), Pickup (%), Bar and body (%), Pickup select, Decay (s), Release (ms), Mute, Key noise (%), Drive (dB), Tone (%),
Tremolo rate (Hz), Tremolo depth (%), Auto-pan (%), Volume (dB). New parameters are appended. No Mix and no Amount: it is a generator with no audio input.

## 6. Editor

House style (NeoEditor): Model and pickup (Model, Pickup select, Mute, the pickup drawing with the flux curve and the harmonics of a hard note, Pickup, Bar and body), Hammer (Hardness, Touch, Velocity curve, Key noise), Decay and damper, Preamp (Drive, Tone), Tremolo (rate, depth, Auto-pan) and Output (Volume, meter). Mac gallery shot `662-neo-keys-editor`, dark and light, with the Bark Drive preset.

## 7. Presets

Eight, generic names, each levelled so that the chord C3 E3 G3 B3 at velocity 0.8 has its loudest 50 ms at -22 dB RMS (within 1 dB): Tine - Classic Ballad, Bark Drive, Stereo Tremolo, Glass Bells; Reed - Soul Keys, Gritty; String - Funky Pluck, Muted Staccato.
The default patch sits at -22.0 dB as well.

## 8. Gates, as tested (`KeysTests`)

| Gate | Test | Measured |
|---|---|---|
| Tuning within 3 cents, 3 octaves per model (C2 to C6 here, with Bar and body at 0 and 100 %) | `EveryNoteIsWithinThreeCentsOfEqualTemperamentOverThreeOctaves` | worst 2.40 cents (Tine, C5, full Bar), 1.21 (String), 0.00 (Reed) |
| T60 of the fundamental within 5 % of Decay | `TheFundamentalsT60IsWithinFivePercentOfDecay`, 3 models x 3 decays x 3 notes | worst 0.14 % |
| Velocity: monotone 1 to 127, 12 dB or more between 30 and 127, centroid rises | `LevelRisesWithVelocityAndSoDoesTheCentroid` | 30 to 127: Tine 17.0 dB, Reed 14.3 dB, String 22.7 dB; centroid at 10, 30, 60, 90, 127: Tine 262, 262, 263, 353, 723 Hz; String 293, 390, 579, 850, 1413 Hz; Reed 262 to 266 Hz (rises, but only a little: the Reed's brightness comes from its odd harmonics, not the pitch band) |
| Sustain pedal holds and releases | `TheSustainPedalHolds...`, `AKeyPressedAgainUnderThePedalStrikesAgain` | no pedal -570 dB (freed), pedal down -11.7 dB, lifted -570 dB |
| Note contract line | `NoteContractTests` (Neo Keys row) | met |
| No clicks at note start, release and steal, blocks 16, 64, 256 | `NoClicksAtNoteStartReleaseAndStealAtBlockSizes16And64And256` (3 models, hardness 1.5 and 0.2 ms) | 0 |
| Parameter jumps | `ParameterJumpsAreSmooth` (every parameter jumped between its ends every 4096 samples against the steady signal; and the click detector) | worst ratio 1.05 (Drive), 0 clicks |
| Steal fade | `AStolenVoiceFadesOutOverAtLeast3Ms` | 95 % to 5 % in 3.67 ms |
| 0 bytes per block, 16 voices, parameters moving, 2000 blocks | `ARenderMakesNoGarbageWith16VoicesOfEveryModel` | 0 bytes |
| No denormals, silent after a 30 s tail | `TheTailsLeaveNoDenormalsAndTheMachineGoesQuiet` (3 models) | 0 |
| No zipper above -60 dB under automation | `AutomationMakesNoZipperNoise` | -102 dB under the carrier |
| CPU: 16 voices of each model at most 2x Neo Mallet's 16 voices | `SixteenVoicesOfEachModelCostAtMostTwiceNeoMalletsSixteen` | Tine 4.26 %, Reed 4.18 %, String 9.90 % of one core at 48 kHz; Neo Mallet 11.49 % (16 modes), 17.41 % (32 modes), same box and run: 0.37, 0.36, 0.86 times |
| Level -22 dB (+-1) for the default patch and every preset | `TheDefaultPatchAndEveryPresetSitAtTheNeoLevelOfMinus22Db` | -22.0 dB for all nine |
| Presets audible and clearly different, no clicks, under the ceiling | `TheFactoryPresetsAreAudibleAndClearlyDifferent`, `EveryFactoryPresetPlaysWithoutClicksAndAFourNoteChordStaysUnderTheCeiling` | closest pair 4.6 dB apart (band RMS) |
| Sound | `ThePickupBarks...`, `TheReedHasMoreOdd...`, `TheStringsMuteShortens...`, `TheTremoloSwings...`, `KeyNoiseIsThere...` | bark: harmonics 2 to 5 at -22.3 dB far, -6.7 dB close and hard, -26.9 close and soft, -24.0 two seconds later; string centroid neck 673, bridge 1084 Hz; tremolo swing 38 dB |
| Generator, no Mix, no Amount, 17 parameters, state version 0 | `TheMachineIsAGenerator...`, `EveryParameterSurvivesAMigration...`, `StateVersionTests` | met |

## 9. Limits

The tine, reed and string are modal approximations: no hammer-string nonlinearity beyond the contact time, no sympathetic resonance, no detune between voices. The pickup is a memoryless curve of displacement, so there is no pickup inductance or cable. The Reed's brightness changes with velocity mostly through its odd harmonics.
The levels were set on the chord, so a single loud high note can be 10 dB above it.

## 10. Literature (for the owner to verify; written from memory, no other product's code, tables or presets used)

- Adrien, J.-M., 1991, in *Representations of Musical Signals*, MIT Press: modal synthesis as a bank of decaying resonators.
- Fletcher, H., 1964, *J. Acoust. Soc. Am.* 36, 203: normal vibration frequencies of a stiff string (the inharmonicity of the String model).
- Fletcher, N. H. and Rossing, T. D., 1998, *The Physics of Musical Instruments*, 2nd ed., Springer: the clamped-free bar's modes and mode shapes (ch. 2), the string and its double decay (ch. 12).
- Horton, M. and Moore, R., 2009, *Am. J. Phys.* 77, 144: the voltage of a magnetic pickup from the flux through its coil as a vibrating string moves (the flux curve of the Tine and String pickups).
- Parker, J. D., Esqueda, F., Bergner, A., 2016, *Proc. DAFx*: antiderivative anti-aliasing of memoryless nonlinearities (first-order form for the pickup curve and the Drive).
- Cook, P. R., 2002, *Real Sound Synthesis for Interactive Applications*, A K Peters: struck-object synthesis with decaying resonators.
