# Neo Audio to MIDI (AM1)

| | |
|---|---|
| Status | Built (AM1): the machine, its editor, the melody and drum engines, tests, the evaluation. |
| Code | `Machines/NeoAudioToMidi/` (`Engine/AudioMidiEngine.cs`, `MelodyNotes.cs`, `DrumHits.cs`, `NeoAudioToMidi.cs`, `NeoAudioToMidiEditor.cs`), shared with Neo Voice to MIDI in `Machines/NeoBuzz.Dsp/Midi/` (`MidiTypes.cs`, `Scales.cs`, `KeyLock.cs`, `MidiParts.cs`), `Machines/NeoBuzz.Machine/MidiRouter.cs`, `Machines/NeoBuzz.Editor/Core/ClickInput.cs`; tests `NeoBuzz.MachineTests/AudioToMidiTests.cs`, `AudioToMidiSignals.cs`; evaluation `tools/PitchEval/Am1.cs`, `tools/am1/`, the `am1` job of `.github/workflows/pitch-eval.yml` |
| Builds on | [`pitch.md`](pitch.md) (the tracker and the onset detector), [`voice-to-midi.md`](voice-to-midi.md) (the outputs, key lock, bend, velocity, the trainer), [`machines.md`](machines.md) |

Clean room: pitch (YIN-style difference function), onset (spectral flux with a max filter), band-energy and discriminant-analysis methods from the literature; no other product's code, names or presets.

## 1. What it is

An effect with one input and one output, the instrument twin of Neo Voice to MIDI. The audio passes through at `Monitor` (0 to 100 %, default 100). The input is read for MIDI, in one of two modes (`Mode`):

- Melody: one note at a time (a guitar, a bass, a lead, a keyboard line).
- Drums: kick, snare and hat.

The MIDI goes to the same three places as Voice to MIDI's, through the same code (`MidiRouter`): a target machine (`SendMIDINote`, landing in its next block), an external MIDI output device, and the host's machine MIDI bus (the MIDI From source `Machine: <name>` of armed tracks, [`voice-to-midi.md`](voice-to-midi.md) section 7). The target, the device and the drum calibration are the machine's state (`NeoAudioToMidiState`) and are saved with the song.

## 2. What is shared with Voice to MIDI

The parts of Voice to MIDI's engine that are not about a voice moved to `NeoBuzz.Dsp/Midi` in the namespace `NeoBuzz.Dsp`, and both engines use them: `MidiBlock` (the events of a block, sorted by sample offset), `Scales` and `KeyFinder` (13 scales, Custom, Krumhansl-Schmuckler key finding), `KeyLock` (Off, Set key, Song key, Suggest), `VelocityMap`, `PitchBendMap` (free and anchored), `NoteOffQueue`, `OnsetTrainer` (the five or twenty example pass) and `HitQueue` (a hit waits for the window its class needs); `MidiRouter` (target, device, bus) and `ClickInput` (the editor's keyboard) moved the same way. Voice to MIDI's behaviour and tests are unchanged (28 of 28 `VoiceToMidiTests` pass before and after). What is only AM1's: the note machine (`MelodyNotes`, section 3), the drum bands (`DrumHits`, section 4) and the engine that joins them.

## 3. Melody

`Range` Guitar (the PT1 guitar tracker: 80 Hz to 1.2 kHz, a 912-sample window at 48 kHz), Bass (38 Hz and up, a longer window) or Full. The note machine is armed by the onset detector (`OnsetDetector`, 4.5 ms hops, `Sensitivity` sets its threshold; at least 45 ms between onsets):

- A note starts when an onset has been followed by a clean pitch. Fast takes the tracker's best candidate as soon as it has confidence 0.80, or two agreeing candidates at 0.55, or 25 ms of waiting at 0.4; Stable needs two agreeing estimates at 0.80, or 40 ms at 0.5. Over a note still sounding (a re-pluck) the estimate must also be made of at least half (Fast) or all (Stable) of a window that holds the new sound. Velocity is from the attack's level (`Soft level` to `Loud level`, `Softest velocity` to `Fixed velocity`), or Fixed.
- Fast corrects: if the first note was another one (an octave out, the string's first cycles), two agreeing estimates within 80 ms of the start change it (a note off and a note on), or, with `Pitch bend` on and the correction inside the bend range, a bend. Stable never plays a wrong first note it could have waited for.
- A running note: `Stickiness` (cents past the middle between two notes) and `Settle` (ms) before it changes; `Pitch bend` Off or plus or minus 1 to 12 semitones (free or anchored): a slide or a hammer-on inside the range is followed by the bend, outside it a new note. `Legato` sends the new note on before the old one's off. `Min note length` holds an off back.
- A plucked string is handled as one. An onset while a note sounds is a re-pluck (`Retrigger` on) only when it is a real rise: no sooner than 50 ms after the note began, at least 1.2 times the level the tracker hears, and within 16 dB of the loudest recent attack (a ringing neighbour string, bleed and hum are far under it). The note ends when the level falls `Release` dB under its peak and stays there for 25 ms, or the pitch is lost for 25 ms (120 ms while the level is within 8 dB of the peak, so a low string that the tracker briefly calls unvoiced is not cut off). After an end the next note needs the level to rise twice over where the last one ended (when no onset starts it), and 60 ms to pass: the decay of a note is not a new note. `Level` (default -40 dB) is the quietest note.
- Key lock (off by default): `Lock`, `Root`, `Scale` and `Custom notes`, as in Voice to MIDI. `Octave` shifts the notes.
- Latency, reported: `note_latency_ms` in the readings is the tracker's detection time plus Fast's longest wait (or Stable's `Settle`): 49 ms for Guitar, 62 ms for Bass at the figures above. The medians measured are in section 6.

## 4. Drums

Kick, snare and hat, out of the box. Hits come from the same onset detector (at least the shortest of the three intervals between onsets). Four band filters (second-order, under 130 Hz, 130 to 450 Hz, 450 Hz to 3 kHz, over 5 kHz) run on the signal and their energies are kept per millisecond. When a hit's 14 ms are in (that and the detector's lead are the hit's note delay, about 15 ms), `DrumHits.Analyse` compares each band's energy then with the 14 ms before it: the bands the hit added, as shares of the total, and each band's rise in dB. The rules (the offline converter's idea, [`pitch.md`](pitch.md) section 9, retuned to a window short enough to play live):

- Kick: the low band's share at least 0.25 and its rise at least 8 dB.
- Snare: the 130 Hz to 3 kHz bands' share at least 0.60 and the larger of their rises at least 10 dB.
- Hat: the high band's share at least 0.02 and its rise at least 3 dB.

(Each is scaled by the drum's `Sensitivity`: at 50 % as stated, higher is looser.) A hit can be several drums (a kick with a hat); each drum has its own note (36, 38, 42 by default), `velocity` (0 from the hit's level), `sensitivity` and `interval` (the shortest time between two of that drum: 50, 30 and 15 ms), so that hats run fast and a kick's tail is not a second kick. The note is held `Hit length` ms on `Drum channel` 10.

Calibration (the editor's Calibrate panel, in Drums mode; `StartCalibration(drum)`): the next five hits are the drum's examples, filed by the trainer Voice to MIDI uses (44 features, `VoiceClassifier`); when all three drums have examples the kit is named by the classifier where it is sure (confidence 0.95 less half the drum's sensitivity), and by the rules where it is not. The classifier is trained from five hits per drum, so it recognises the player's sounds; it does not replace the rules for the hits it is unsure of.

## 5. State, presets, editor

`MachineState` holds the target, the device and the calibration (`VoiceClassifier.Serialize`). The five factory presets are parameter sets: Guitar lead, Bass, Keys, Drum kit, Percussion (a set of congas, maracas and similar notes). The editor (`NeoAudioToMidiEditor`, one source for WPF and Avalonia): the live roll (Melody: the input's pitch over the last three seconds as a line, the notes sent as bars; Drums: a lane per drum with its hits), the key and scale keyboard, the melody and drum knobs, the three pads (they light on a hit; click one to choose it) with the Calibrate panel, and the MIDI-to buttons.

## 6. Measured (synthetic)

`AudioToMidiTests` (deterministic; three seeds each; a note is right when its onset is within 50 ms of the truth's and its note exact):

| Test | F at 50 ms | F at 100 ms | Latency median | worst | Octave errors | Extra notes / min |
|---|---:|---:|---:|---:|---:|---:|
| Guitar line (Karplus-Strong, slides, hammer-ons, re-plucks), Fast | 96.5 % | 97.9 % | 13.1 ms | 46.0 ms | 0 | 1.7 |
| the same, Stable | 95.0 % | 97.9 % | 23.7 ms | 46.0 ms | 0 | 3.4 |
| Bass line E1 to G3, Bass range, Fast | 94.3 % | 98.4 % | 30.1 ms | 46.8 ms | 0 | 7.0 |
| the same, Stable | 44.2 % | 100 % | 47.4 ms | 49.9 ms | 0 | 52 (the notes are 48 ms late: they are not wrong, but past 50 ms) |
| Saw lead with vibrato (plus or minus 25 cents at 5.5 Hz), Fast | 100 % | 100 % | 9.2 ms | 24.4 ms | 0 | 0 |
| Sine lead with vibrato, Fast | 100 % | 100 % | 8.6 ms | 21.0 ms | 0 | 0 |

Drum loops (8 bars at 90, 120, 140 and 174 bpm with ghost notes at a fifth of the level; kick, snare and hat; the snare's ghost notes are counted apart): the table of `DrumLoopsWithGhostNotes` is in the report; every right hit is within 50 ms and the median is 15 ms after the hit. With the default rules at 120 bpm: kick F 88.8 %, snare F 90.0 % (ghost notes 50 of 63), hat F 86.4 %; at 174 bpm: kick 72.3 %, snare 95.0 %, hat 73.3 % (a hat on the same sixteenth as a kick is often missed). Calibration from five hits of each drum: the figures are the same to a tenth.

No allocation on the audio thread (`ProcessingAllocatesNothing`, with parameter and mode changes mid-run). CPU (`TheEngineCostsUnderFourPercentOfRealTime`): the figures are in the test output.

## 7. Real recordings

The `am1` job of `pitch-eval.yml` (dispatched by hand; the data are downloaded on the runner and never kept): GuitarSet's per-string hexaphonic lines against the JAMS annotation (Melody, Guitar range, Fast and Stable), IDMT-SMT-Drums (with and without calibration), MDB-stem-synth (Melody, note F and pitch on voiced frames), and piano-roll plots of three excerpts of each mode. The figures are in the report of the run (`machines-neo-audio-to-midi-am1.md`) and below.

@@REAL@@

## 8. Limits

- One note at a time. A chord, or several strings ringing together, gives one wandering note; GuitarSet's recordings are one string a line.
- The drum rules were tuned on the IDMT-SMT-Drums mixes (three kits) and the synthetic loops: other kits will be further from them. Calibrating helps little; five hits of a drum teach the classifier that drum, not the mix it will be heard in.
- A hit with two drums at once plays both notes; a loud cymbal hides the quieter drums under it.
- Pitch bend range must be set on the receiving synth to the same value.
- Bass range is slower (a long window): Stable plays a note a median 47 ms late.
- No plug-in export, no MPE, no on-device model: everything is a few KB of state.
