# Neo Voice to MIDI (VM1)

| | |
|---|---|
| Status | Built (VM1): the machine, its editor, the trigger classifier, the machine MIDI source, tests, the evaluation. |
| Code | `Machines/NeoVoiceToMidi/` (`Engine/VoiceEngine.cs`, `Scales.cs`, `Chords.cs`, `NeoVoiceToMidi.cs`, `NeoVoiceToMidiEditor.cs`), `Machines/NeoBuzz.Dsp/VoiceFeatures.cs`, `VoiceClassifier.cs`, `ReBuzzGUI/BuzzGUI.Interfaces/IMachineMidiBus.cs`, `ReBuzz/Core/Session/TrackInputs.cs`; tests `NeoBuzz.MachineTests/VoiceToMidiTests.cs`, `ReBuzz.SessionTests/TrackInputTests.cs`, `ReBuzz.Studio.Tests/TrackInputEngineTests.cs`; evaluation `tools/PitchEval/VoiceTriggers.cs`, `Lab.cs`, `VocalSet.cs` |
| Builds on | [`pitch.md`](pitch.md) (the tracker, onset detector, vowel estimator), [`neo-audio.md`](neo-audio.md) (arm and MIDI From), [`machines.md`](machines.md) |

Clean room: pitch, onset, MFCC and discriminant-analysis methods from the literature; no other product's code, names or presets.

## 1. What it is

An effect with one input and one output. The audio passes through at `Monitor` (0 to 100 %). The voice is also read for pitch, percussive hits, vowels and level, and played as MIDI to (1) a target machine (`IMachine.SendMIDINote` / `SendMIDIControlChange`, so the notes land in the target's next block), (2) an external MIDI output device, and (3) the host's machine MIDI bus: a track whose MIDI From is `Machine: <name>` hears it (section 7).

## 2. Pitch

- `Range`: Low latency or Quality (the PT1 voice trackers, about 7.5 and 17.7 ms). `Response` (Fast, the default, or Stable) decides how soon the first note plays: Fast plays it at the first clean pitched estimate (section 2.1), a median of 14 ms (Low latency) and 21 ms (Quality) after the voice on synthetic voices; Stable waits `Settle` 30 ms and plays at a median of 43 and 47 ms.
- `Lock`: Off (every semitone), Set key (`Root`, `Scale`: 13 scales and Custom), Song key (the song's `Key` association, else the set key), Suggest (a Krumhansl-Schmuckler key from a decaying histogram of the notes sung so far, re-evaluated every second). `Muted notes` (one bit per pitch class) are skipped: the nearest note that is on is played.
- A note starts at the first clean pitched estimate (Fast) or when a candidate has been the nearest allowed note for `Settle` ms (Stable). A running note changes only when the voice is nearer the new note by `Stickiness` (a hysteresis in cents) for `Settle` ms. The note ends 25 ms after the voice stops, and not before `Min note length`. `Legato` sends the new note on before the old note off. `Octave` shifts the notes.
- `Pitch bend` (off, plus or minus 1 to 12 semitones): the voice's distance from the note's centre as a 14-bit bend. `Anchored` sends nothing until the distance passes `Anchor` cents, then follows it, and returns to centre when the distance falls under half the anchor.
- Velocity: Dynamic (from the note's peak level between `Soft level` and `Loud level`) or Fixed.
- The note machine also runs when only `Chords` is on (the pitch notes are then not sent).

### 2.1 Where the note-on delay goes

Measured on the synthetic voices of `VoiceToMidiTests` (`NoteOnDelayBrokenDownByStage`), median from the start of the voice:

| Stage | Low latency | Quality | Notes |
|---|---:|---:|---|
| Tracker: first voiced estimate | 9.4 ms | 12.7 ms | the analysis window (7.5 and 17.7 ms) is mostly filled by then |
| Wait for a clean estimate (Fast) | +4.7 ms | +8.1 ms | the first estimate of an onset is often an octave out, so Fast waits for clarity 0.93 or 12 ms at most |
| Voicing hysteresis | 0 | 0 | none after the first voiced estimate (release only: 25 ms after the voice stops) |
| Stickiness | 0 | 0 | filters note changes only, never the first note |
| Min note length | 0 | 0 | delays note offs only |
| Settle | 0 (Fast) / 30 ms (Stable) | 0 / 30 ms | note changes always wait; the first note only in Stable |
| Note-on | 14.1 ms | 20.8 ms | median; worst 17.8 and 25.8 ms |
| Block dispatch (host) | up to 5.3 ms | up to 5.3 ms | a 256-sample block; the event carries its sample offset, the target hears it in its next block |

Before this change Stable was the only behaviour: 42.6 and 46.6 ms. With Triggers on (the default) Stable also holds a note back while an onset is being named (about 37 ms in all); Fast does not hold, and cuts the pitch note when the hit is named.

## 3. Triggers

Up to eight sounds. Onsets come from the PT1 `OnsetDetector` with a minimum gap of 110 ms (a voice's second onset, the vowel after a consonant, is not a hit; PT1 measured 100 to 120 ms). Each onset's window (`Window`: Fast 18 ms or Accurate 30 ms, from 3 ms before the onset) is turned into 44 features (`VoiceFeatures`): PT1's 19 (log-energy slopes, centroid, flatness and so on), 13 MFCCs over 26 mel bands of the window with the previous window's spectrum partly subtracted (the tail of the last hit), 8 coarse-band energy changes from the first to the second half, centroid and zero-crossing changes, roll-off and tilt. `VoiceClassifier` is a shrinkage linear discriminant (lambda 0.5) with a distance reject (`RejectScale` 3): small enough to train in milliseconds on 10 to 64 examples per sound, with a published immutable model for the audio thread.

Train mode (`StartTraining(sound)`, the editor's pad and Train button): the engine files every onset of a pass as an example of that sound until 20 are heard or `Done`; the machine's timer moves the examples off the audio thread and trains. Per sound: `note`, `velocity` (0 from the hit's level), `sensitivity` (how sure the classifier must be). A sound taken for a trigger plays its note on `Trigger channel` for `Trigger length`; with `Overlap` Triggers win it also keeps the pitch part from playing a note for that hit.

## 4. Chords

`ChordBuilder`: the diatonic chord of the key built on the note's scale degree (`Root`) or the chord of the key that fits the sung note (`Melody`), triads or sevenths, inversion Root, First, Second or Auto (the one nearest the last chord), voicing Close, Open or Wide, an octave register, its own channel and a velocity share. A note out of the key takes its nearest scale degree. `ChordSuggester` keeps a half-life histogram (6 s) of the pitch classes sung and ranks the key's chords by how much of the histogram each contains and how much is outside it; the editor shows the best three.

## 5. Expression

`Level` (the smoothed voice level, -60 to 0 dB) and the vowels aa, oo, ee (the PT1 `VowelEstimator`) each map, between a min and a max threshold, to a controller value between a low and a high value, with its own controller number and channel; sent on change, at most every 10 ms.

## 6. State, presets, editor

`MachineState` (`NeoVoiceToMidiState`) holds the target machine, the device, the sounds' names and the training (`VoiceClassifier.Serialize`: a header "NBVOICE 1 44 n" and the examples as text). It is saved with the song and captured by a preset (a preset's state lines; a preset with an empty target or device leaves the routing as it is). The nine factory presets are parameter sets; a preset you save with trained sounds carries the training, so a profile for a voice (a person's kick, snare and hats) can be shared. The editor: the live view (the pitch line over 3 s on a piano roll with the key's notes lit, the chord and the three best fits), the key and scale keyboard (click mutes a note, Shift-click flips it in Custom), the eight pads (lit by the trigger that plays, click selects) with Train, Done and Forget, per-sound note and sensitivity knobs, the chords, the expression lanes with meters, and the "MIDI to" buttons that cycle the target machine and the device.

## 7. The machine MIDI source

`IMachineMidiSource` marks a machine that makes MIDI; the host's `ReBuzzCore` implements `IMachineMidiBus.SendMachineMIDI(machine, data)`, which hands the message to the session as `MidiDevice.Machine(name)`. A track's MIDI From can be `Machine: <name>` (saved as `midi="machine" device="<name>"`, listed in the three views' chooser, and in the agent as `midi_from="machine:<name>"`). `MidiRouting.Accepts`: a machine's message reaches only the tracks that name that machine (and the channel filter); All inputs and tracks with no entry never take a machine; a machine track takes no device and not the computer keyboard.

**Record at attack** (AT1, `Record at attack`, on by default, appended at the end of the parameter table). The engine knows each note's attack: the first voiced estimate minus half the tracker's window (not before the last note off), and a trigger's onset sample. The router sends it with the note: `MidiEvent.Lag` is the number of samples between the attack and the event, and `IMachineMidiBus.SendMachineMIDI(machine, data, earlierSamples)` hands it to the session, whose recorder stamps the note that many samples earlier (`TakeTime.Earlier`, never before the take's start). Live output (the target machine and the device) is unchanged; the note off carries the same lag, so a recorded note keeps its length. On the test voice: median offset from the sound's start, live 14.1 ms and recorded 6.9 ms in Fast, live 46.6 ms and recorded 2.6 ms in Stable Quality.

## 8. Latency and cost

Reported latency is 0 (the audio passes through): the MIDI is late by the figures in section 2, and `note_latency_ms` is in the readings. CPU (pitch, chords, four trained sounds and the expression lanes, 256-sample blocks at 48 kHz): about 80 times real time on the core measured here (about 1.3 % of a core). No allocation on the audio thread (tested with parameter changes mid-run).

## 9. Measured

Synthetic tests (`VoiceToMidiTests`, deterministic): 8 of 8 sung notes right at 120 ms in both ranges and both responses; Fast: median note-on delay 14.1 ms (Low latency) and 20.8 ms (Quality), worst 17.8 and 25.8 ms, one first note of eight wrong in each range (an octave error in the first 10 ms, corrected 8 ms later); the same with Triggers on and three trained sounds; Stable: 42.6 and 46.6 ms, worst 51.7 and 70.6 ms, no wrong first notes; key lock puts the out-of-key notes on the nearest key note; the suggested key for an A minor melody is A minor; the chords of a C major scale are C, Dm, Em, F, G, Am and Bdim; the level controller stays inside its range and channel; kit triggers: 3 sounds 100 % and 4 sounds 86.7 % at 15 examples, 8 sounds 80 % at 20 examples.

Real recordings (the `pitch-eval.yml` workflow; AVP is the Amateur Vocal Percussion set, VocalSet is read from its zip in place; nothing is committed):

AVP, onset detector at 110 ms, per participant and modality (56 sets), trained on 15 examples per class and tested on the rest of the same recordings (accuracy of the detected hits; "end to end" counts missed and extra hits as wrong):

| Classifier | 4 classes, mean | 4 classes, median | 4 classes, sets at 85 % or more | 3 classes, mean | 4 classes, end to end |
|---|---:|---:|---:|---:|---:|
| PT1: k-NN, 19 features, 18 ms (the 78 % starting point) | 79.1 % | 78.1 % | 21 of 56 | 84.8 % | 68.0 % |
| Voice to MIDI Fast: 44 features, 18 ms | 85.1 % | 85.5 % | 29 of 56 | 89.1 % | 73.1 % |
| Voice to MIDI Accurate: 44 features, 30 ms (default) | 88.0 % | 90.5 % | 38 of 56 | 91.5 % | 75.7 % |

The target (85 % or more for four classes with 15 examples) is met on average and in the median; 18 of the 56 participant sets stay under it, the worst at 63 %. Trained on one recording session and tested on another of the same participant (improvised, harder) the Accurate classifier gets 70.3 % for four classes; trained on other participants and tested on a new one it gets 40.0 % (chance 25 %): a person trains their own sounds. The reject threshold removes 1.5 % of known sounds and 7.2 % of a sound it was not trained on.

Vowels on singers the estimator was not fitted on: the workflow's "Vowels on held-out singers" section (a leave-one-singer-out refit, and the PT1 weights per half of the singers and per singer); the figures are in the VM1 report.

First note on real voices (`pitch-eval.yml`, VocalSet straight long tones, 59 files with a steady pitch; Lock off; `FirstNote.cs` runs the engine itself). VocalSet has no note truth, so a note-on is right when it is within 0.75 semitone of the median pitch the Quality tracker gives in the next 40 ms (the note at 120 ms: of its pitch at 100 to 140 ms), so a singer's scoop into the note is not held against the engine. Voice start is the first 2.7 ms window above -40 dBFS.

| Setting | Delay median | First note wrong | of which an octave | Wrong at 120 ms |
|---|---:|---:|---:|---:|
| Low latency, Fast | 8.8 ms | 21.4 % | 0 | 28.6 % |
| Quality, Fast | 10.6 ms | 13.6 % | 0 | 25.0 % |
| Low latency, Stable | 40.1 ms | 2.2 % | 0 | 26.7 % |
| Quality, Stable | 43.0 ms | 6.5 % | 0 | 23.9 % |

Fast plays the first note a median of 31 ms (Low latency) and 32 ms (Quality) sooner than Stable. Its wrong first notes are the voice still on its way into the note (none an octave out), and the 120 ms note is within 2 points of Stable's (28.6 against 26.7 and 25.0 against 23.9). Delay is from the first window above -40 dBFS, so it is shorter here than on the synthetic voices, whose attack is slower; the 90th percentile (127 to 154 ms) is breathy starts where the voice is not pitched yet.

## 10. Limits

- Fast response can play a wrong first note (an octave error in the first 10 to 20 ms of a voice, mostly on voices with a strong second harmonic) and corrects it with a retrigger within 80 ms (with Pitch bend on the bend follows the voice instead and the note is left); a hit named a trigger cuts the pitch note it started, a blip of up to about 60 ms, so a beatboxer who wants no stray notes picks Stable. The first note's velocity comes from the first 10 to 20 ms of the voice, so it reads a little low on a slow attack. Stable plays notes 40 to 50 ms late.
- LPC-based vowels fail above about 300 Hz of f0; the vowel controllers are for speaking-range and low singing voices.
- The trigger classifier is per person: sounds trained by one voice do not carry to another. It does not detect a hit hidden by the voice's own sustained note.
- Pitch bend range must be set on the receiving synth to the same value.
- No plug-in export, no MPE, no on-device model: everything is a few KB of state.
