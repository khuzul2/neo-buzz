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
- Key envelope: a linear ramp of 3 ms on and 6 ms off.
- Leakage: every sounding wheel leaks a trace (up to 0.15 % a contact) into its neighbours' wheels.

## 3. Around the tone

1. **Percussion**: a 2nd (the 4' wheel) or 3rd (the 2 2/3' wheel) harmonic with an exponential decay of 0.2 s (Fast) or 0.7 s (Slow) to -20 dB, Soft (8 dB below Normal) or Normal. Single trigger: only a key pressed when none is held starts it. While it is on, the 1' drawbar is not used, as on the instrument.
2. **Key click**: a short high-passed noise burst at note on (and a half-level one at note off), level 0 to 100 % (0 is silent: the machine adds nothing).
3. **Scanner vibrato and chorus**: a delay line read with a triangle sweep, six settings: V1, V2, V3 (pitch swing of about ±6, ±12 and ±24 cents at 6.8 Hz) and C1, C2, C3 (the same sweep mixed with the dry sound at 0.5).
4. **Drive**: a tube-style overdrive (asymmetric soft saturation, 2x oversampled) with level compensation at -14 dBFS, before the speaker.
5. **Rotary speaker**: a horn (highs) and a drum (lows), each a rotor with its own slow and fast speed and its own acceleration (first-order lag; Stop brakes them to rest). Each makes an amplitude swell, a directivity change and a Doppler shift (a fractional delay); two virtual microphones at an angle (Mic spread) make the stereo; Horn/drum balances the two. `Speed` is Slow / Fast / Stop, so a mapped knob or pad switches it. With `Rotary` off the dry organ is in both channels.
6. **Output**: `Expression` (a swell pedal before the drive and the speaker, gain the square of the setting) and `Volume` (dB), a soft limiter at 0.9.

## 4. Documented values and gates

| Item | Documented | Measured (`OrganTests`) |
|---|---|---|
| Drawbar pitch | wheel table | within 0.01 cent on every bar, notes 60, 84, 96 |
| Step law | 3 dB a step | 8 to 1 is 21.00 dB, each step 3.00 dB |
| Percussion 2nd, 3rd | 523.25 Hz, 783.99 Hz (note 60) | 523.27 Hz, 783.97 Hz |
| Percussion decay (-20 dB) | 0.2 s, 0.7 s | 0.21 s, 0.71 s |
| Single trigger | no percussion on a legato note | -600 dB against -19.8 dB for a fresh note |
| Key click | 0 gives none | energy above 6 kHz: -78.6 dB at 0, -25.0 dB at 30 %, -14.7 dB at 100 % |
| Vibrato swing | ±6, ±12, ±24 cents | ±6.4, ±12.8, ±25.7 |
| Horn swells | 0.8 Hz slow, 6.7 Hz fast | 0.805, 6.709 |
| Drum swells | 0.67 Hz slow, 5.7 Hz fast | 0.671, 5.695 |
| Slow to Fast, 90 % of the fast speed | horn 1.09 s, drum 5.44 s | 1.11 s, 5.50 s |
| Stop | rotors at rest | loudness varies 0.001 dB over 2 s |
| Cost | 61 keys within 1.5x of one key | 1.00x; 61 keys with vibrato, drive and rotary 77x real time |
| Hygiene | 0 bytes a buffer, no denormals, no zipper above -60 dB | all hold; moving drawbars, expression and volume leaves -75 dB above 2 kHz |

## 5. Parameters and state

`Params.def` lists the 32 parameters in seven groups (Drawbars, Percussion, Key, Vibrato, Drive, Rotary, Output); the order is the version-0 layout (`state-layouts.txt`). The state is versioned (`StateMigrations`).

## 6. Editor

The nine drawbars as vertical bars with their footage labels and brown, white and black caps (press or drag to set a step, the wheel steps it, a double-click pushes it in), then Percussion, Key, Vibrato, Drive, Rotary (Rotary on/off, the Slow / Fast / Stop switch, the horn's and the drum's live speeds, the speeds, inertia, depth, Doppler, spread and balance) and Output with a level meter.

## 7. Presets

Twelve, named by sound: Full (Full Gospel, Church Full, Fast Whirl), Jazz (Jazz Trio, Smoky Blues, Warm Soul), Soft (Soft Ballad, Mellow Flutes), Rock (Rock Drive), Percussive (Bright Percussion, Reggae Bubble, Funk Stabs). Their drawbar registrations, vibrato, drive, speaker speed and depth differ widely so that each is clearly different at first listen.
