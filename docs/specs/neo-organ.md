# Neo Organ

| | |
|---|---|
| Status | Built (OR1, gap G4 of `docs/specs/legacy-machines.md`): engine, editor, 12 presets, agent guide, gates in `NeoBuzz.MachineTests/OrganTests.cs`. The sound is for the user's ear: the listening pack is local. |
| Branch | `machines/neo-organ-or1` |
| Code | `Machines/NeoOrgan/` (`NeoOrgan.cs`, `NeoOrganEditor.cs`, `Params.def`, `Presets.txt`, `Engine/Tonewheels.cs`, `Engine/OrganEngine.cs`, `Engine/OrganEffects.cs`), the two flavours in `Machines/Portable/NeoOrgan` and `Machines/Studio/NeoOrgan` |
| Clean room | Published literature on how a tonewheel organ is built and our own DSP only. No existing machine's source or parameter table was read. No brand, model or player is named anywhere. |

## 1. What it is

A polyphonic tonewheel organ with a stereo output, 16 tracks. It is an instrument, so it has no Mix or Amount: its presets are deliberately strong and clearly different from one another.

## 2. The tone generator

- 91 free-running sine wheels, equal tempered from A = 440 Hz: wheel 1 is C1 (32.703 Hz), wheel 91 is F#8 (5919.9 Hz). `Tonewheels.WheelHz(w) = 440 * 2^((24 + w - 1 - 69) / 12)`. Each wheel is a 32-bit phase accumulator reading a 4096-point sine table with linear interpolation, with its own fixed level offset of up to 0.5 dB (a seeded generator, the same in every instance).
- A manual of 61 keys, MIDI notes 36 to 96. A key's 8' pitch is its own note; the nine drawbars take the wheel 16': -12, 5 1/3': +7, 8': 0, 4': +12, 2 2/3': +19, 2': +24, 1 3/5': +28, 1 1/3': +31, 1': +36 semitones from it. A footage above wheel 91 folds back an octave (repeatedly), as on the instrument. Notes outside the manual sound in the nearest octave inside it.
- Each key switches wheels onto the nine busses; the sum over wheels is built once per sample from a per-wheel coefficient array, so the cost depends on the 91 wheels and not on the number of keys (61 keys held cost 1.00 times one key). The key gains are rebuilt every 16 samples while a key or a drawbar changes and ramped linearly between.
- Drawbars are integer steps 0 to 8 at 3 dB a step (8 is 0 dB, 1 is -21 dB, 0 is off), smoothed.
- Key envelope: a smoothstep of 6 ms on and 10 ms off (smooth enough that the tone itself starts with no more power above 6 kHz than it has once it sounds; the click is added on top).
- Leakage: every sounding wheel leaks a trace (up to 0.15 % a contact) into its neighbours' wheels.

## 3. Around the tone

1. **Percussion**: a 2nd (the 4' wheel) or 3rd (the 2 2/3' wheel) harmonic with an exponential decay of 0.2 s (Fast) or 0.7 s (Slow) to -20 dB, Soft (8 dB below Normal) or Normal. Single trigger: only a key pressed when none is held starts it. While it is on, the 1' drawbar is not used, as on the instrument.
2. **Key click**: a contact transient, not a burst of white noise (OR1 review). A key closing starts 2 to 4 contact bounces at random times within about 3 ms and at random, falling levels; each bounce is a smooth Hann pulse of 1.2 to 2.5 ms shaping its own noise, and a band-pass (4th-order 1.2 kHz high-pass, 8th-order 3.5 kHz low-pass) keeps the sum between about 1.2 and 3.5 kHz: -48 dB at 6 kHz, nothing above 8 kHz. Its level is relative to the key's own level (its drawbars summed by power), times the square of `Key click` (100 % is about 12 dB under one key's steady peak, 30 % about 33 dB); 0 adds nothing at all. A key opening makes one or two bounces 16 dB lower (about 28 dB under the press, as measured).
3. **Scanner vibrato and chorus**: a delay line read with a triangle sweep, six settings: V1, V2, V3 (pitch swing of about ±6, ±12 and ±24 cents at 6.8 Hz) and C1, C2, C3 (the same sweep mixed with the dry sound at 0.5).
4. **Drive**: a tube-style overdrive (asymmetric soft saturation, 2x oversampled) with level compensation at -14 dBFS, before the speaker.
5. **Rotary speaker**: a horn (highs) and a drum (lows), each a rotor with its own slow and fast speed and its own acceleration (first-order lag; Stop brakes them to rest). Each makes an amplitude swell, a directivity change and a Doppler shift (a fractional delay); two virtual microphones at an angle (Mic spread) make the stereo; Horn/drum balances the two. `Speed` is Slow / Fast / Stop, so a mapped knob or pad switches it. With `Rotary` off the dry organ is in both channels.
6. **Output**: `Expression` (a swell pedal before the drive and the speaker, gain the square of the setting) and `Volume` (dB, default 0 dB: one note at the defaults is about -21 LUFS, as Neo Poly), a soft limiter at 0.9.

## 4. Documented values and gates

| Item | Documented | Measured (`OrganTests`) |
|---|---|---|
| Drawbar pitch | wheel table | within 0.01 cent on every bar, notes 60, 84, 96 |
| Step law | 3 dB a step | 8 to 1 is 21.00 dB, each step 3.00 dB |
| Percussion 2nd, 3rd | 523.25 Hz, 783.99 Hz (note 60) | 523.27 Hz, 783.97 Hz |
| Percussion decay (-20 dB) | 0.2 s, 0.7 s | 0.21 s, 0.71 s |
| Single trigger | no percussion on a legato note | -600 dB against -19.8 dB for a fresh note |
| Key click level | 0 gives none; relative to the note; the square of the setting | peak re one key's steady peak: none at 0, -32.8 dB at 30 %, -11.9 dB at 100 %; the same (-20.8 dB at 60 %) with the 8' at 8 or at 2 |
| Key click band | about 1 - 6 kHz, nothing above 8 kHz | 95 % of its energy between 1 and 6 kHz, -71 dB above 8 kHz, -45 dB below 500 Hz; over within 8 ms; it fades in |
| Release click | much quieter than the press | -27.6 dB re the press |
| Every preset, a Cmaj7 chord | click peak 20 dB under the chord's steady peak (12 dB for Funk Stabs and Reggae Bubble); power above 6 kHz in the first 10 ms at most 6 dB over the 30 - 80 ms window (floor 60 dB under the chord) | see the table below; all pass |
| Vibrato swing | ±6, ±12, ±24 cents | ±6.4, ±12.8, ±25.7 |
| Horn swells | 0.8 Hz slow, 6.7 Hz fast | 0.805, 6.709 |
| Drum swells | 0.67 Hz slow, 5.7 Hz fast | 0.671, 5.695 |
| Slow to Fast, 90 % of the fast speed | horn 1.09 s, drum 5.44 s | 1.11 s, 5.50 s |
| Stop | rotors at rest | loudness varies 0.001 dB over 2 s |
| Cost | 61 keys within 1.5x of one key | 1.00x; 61 keys with vibrato, drive and rotary 77x real time |
| Hygiene | 0 bytes a buffer, no denormals, no zipper above -60 dB | all hold; moving drawbars, expression and volume leaves -75 dB above 2 kHz |

### The key click per preset (OR1 review)

A Cmaj7 chord (C3 E3 G3 B3) from silence at each preset's settings, rendered with the click and again with Key click at 0: the difference is the click as it comes out. "Steady" is 300 - 500 ms. Power above 6 kHz is measured through a 16th-order 6 kHz high-pass; the 30 - 80 ms window's power has a floor 60 dB under the chord (a tonewheel chord with no drive has next to nothing above 6 kHz, its top wheel being 5.9 kHz). The pack columns are the listening pack's 8-bar takes. Before is the white-noise click of the first OR1 build (the Key click scale was different: a fixed level, not the note's).

| Preset | Key click | Click peak re the chord's steady peak (limit) | Above 6 kHz: first 10 ms over 30 - 80 ms (limit +6 dB) | Above 6 kHz in the first 10 ms, re the chord | Pack: 1-ms windows > 20x median | Pack: max abs 2nd difference |
|---|---|---|---|---|---|---|
| Full — Full Gospel | 45% → 45% | -9.0 → -27.6 dB (-20) | +14.7 → -0.9 dB | -10.9 → -25.2 dB | 0 → 0 | 0.957 → 0.343 |
| Full — Church Full | 10% → 20% | -19.0 → -48.0 dB (-20) | +34.2 → -30.7 dB | -23.0 → -86.9 dB | 7 → 0 | 0.143 → 0.015 |
| Full — Fast Whirl | 15% → 25% | -18.5 → -47.5 dB (-20) | +32.5 → -27.1 dB | -23.1 → -81.5 dB | 70 → 0 | 0.394 → 0.013 |
| Jazz — Jazz Trio | 35% → 40% | -9.0 → -36.9 dB (-20) | +41.8 → -24.0 dB | -14.1 → -76.5 dB | 269 → 0 | 0.716 → 0.007 |
| Jazz — Smoky Blues | 25% → 35% | -1.9 → -30.5 dB (-20) | +45.4 → -21.2 dB | -9.4 → -72.9 dB | 203 → 0 | 0.764 → 0.008 |
| Jazz — Warm Soul | 20% → 30% | -14.9 → -45.4 dB (-20) | +36.6 → -32.5 dB | -15.7 → -80.4 dB | 215 → 0 | 0.363 → 0.004 |
| Soft — Soft Ballad | 5% → 10% | -20.2 → -60.7 dB (-20) | +30.4 → -27.0 dB | -21.5 → -76.3 dB | 42 → 0 | 0.065 → 0.003 |
| Soft — Mellow Flutes | 12% → 15% | -13.9 → -51.8 dB (-20) | +38.5 → -26.3 dB | -16.8 → -80.3 dB | 21 → 0 | 0.209 → 0.011 |
| Rock — Rock Drive | 40% → 15% | -3.8 → -28.0 dB (-20) | +10.3 → -2.7 dB | -6.7 → -18.8 dB | 0 → 0 | 0.892 → 0.452 |
| Percussive — Bright Percussion | 60% → 55% | -5.9 → -33.0 dB (-20) | +44.8 → -19.4 dB | -13.0 → -74.2 dB | 82 → 0 | 1.054 → 0.023 |
| Percussive — Reggae Bubble | 80% → 90% | -3.3 → -21.6 dB (-12) | +47.7 → -13.2 dB | -11.3 → -71.3 dB | 225 → 0 | 0.913 → 0.009 |
| Percussive — Funk Stabs | 100% → 80% | 1.6 → -23.6 dB (-12) | +53.5 → -12.5 dB | -5.0 → -67.5 dB | 421 → 0 | 1.110 → 0.008 |
| Rotary take (Jazz Trio, Slow-Fast-Slow) | 35% → 40% | | | | 289 → 2 (also 2 with Key click 0: its percussion through the fast horn) | 0.774 → 0.013 |

## 5. Parameters and state

`Params.def` lists the 32 parameters in seven groups (Drawbars, Percussion, Key, Vibrato, Drive, Rotary, Output); the order is the version-0 layout (`state-layouts.txt`). The state is versioned (`StateMigrations`).

## 6. Editor

The nine drawbars as vertical bars with their footage labels and brown, white and black caps (press or drag to set a step, the wheel steps it, a double-click pushes it in), then Percussion, Key, Vibrato, Drive, Rotary (Rotary on/off, the Slow / Fast / Stop switch, the horn's and the drum's live speeds, the speeds, inertia, depth, Doppler, spread and balance) and Output with a level meter.

## 7. Presets

Twelve, named by sound: Full (Full Gospel, Church Full, Fast Whirl), Jazz (Jazz Trio, Smoky Blues, Warm Soul), Soft (Soft Ballad, Mellow Flutes), Rock (Rock Drive), Percussive (Bright Percussion, Reggae Bubble, Funk Stabs). Their drawbar registrations, vibrato, drive, speaker speed and depth differ widely so that each is clearly different at first listen.
