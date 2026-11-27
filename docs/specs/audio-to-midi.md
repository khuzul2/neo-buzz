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

- A note starts when an onset has been followed by a clean pitch. Fast takes the tracker's best candidate when two consecutive estimates agree (within half a semitone) at confidence 0.55, or after 25 ms of waiting at 0.4; Stable needs two agreeing estimates at 0.80 or 40 ms at 0.5, and at least 40 ms after the onset (`StableMinMs`). Over a note still sounding (a re-pluck) the estimate must also be made of at least half (Fast) or all (Stable) of a window that holds the new sound. Velocity is from the attack's level (`Soft level` to `Loud level`, `Softest velocity` to `Fixed velocity`), or Fixed.
- Fast corrects: if the first note was another one (an octave out, the string's first cycles), two agreeing estimates within 80 ms of the start change it (a note off and a note on), or, with `Pitch bend` on and the correction inside the bend range, a bend. Stable never plays a wrong first note it could have waited for.
- A running note: `Stickiness` (cents past the middle between two notes) and `Settle` (ms) before it changes; `Pitch bend` Off or plus or minus 1 to 12 semitones (free or anchored): a slide or a hammer-on inside the range is followed by the bend, outside it a new note. `Legato` sends the new note on before the old one's off. `Min note length` (default 50 ms) holds an off back; a note that ends sooner than that is not followed at once by another.
- A plucked string is handled as one. An onset while a note sounds is a re-pluck (`Retrigger` on) only when it is a real rise: no sooner than 50 ms after the note began, at least 1.2 times the level the tracker hears, and within 16 dB of the loudest recent attack (a ringing neighbour string, bleed and hum are far under it). The note ends when the level falls `Release` dB under the note's own peak and stays there for 25 ms, or the pitch is lost for 25 ms (120 ms while the level is within 8 dB of the peak, so a low string that the tracker briefly calls unvoiced is not cut off). After an end the next note needs the level to rise twice over where the last one ended (when no onset starts it), 60 ms to pass, and, for the same pitch, 300 ms or a real onset: a dropout inside a ringing note is not a new note. `Level` (default -40 dB) is the quietest note.
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

The `am1` job of `pitch-eval.yml` (dispatched by hand; the data are downloaded on the runner and never kept): GuitarSet's solo takes of players 03 to 05, the microphone and the pickup mix against the merged notes of the six strings, and the per-string hexaphonic lines (Melody, Guitar range, Fast and Stable), IDMT-SMT-Drums (with and without calibration), MDB-stem-synth (Melody, note F and pitch on voiced frames), and piano-roll plots of three excerpts of each mode. The figures are in the report of the run (`machines-neo-audio-to-midi-am1.md`) and below.

Held-out results (the rules and gates were fitted on a development split; the figures below are on data they never saw): GuitarSet players 00 to 02 (and, for the gates, twelve hexaphonic files across all six players) were the development data; players 03 to 05 (45 solo takes) are the held-out set. IDMT-SMT-Drums: the drum rules were fitted on the even-numbered takes; the odd-numbered takes are held out.

GuitarSet, held-out, solo takes against every string's notes merged into one line (a note is right when its onset is within 50 ms and its pitch within 50 cents):

| audio | response | precision | recall | F | F at 100 ms | latency median | octave errors | extra notes / min |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| pickup mix | Fast | 68.3 % | 81.5 % | 74.3 % | 79.7 % | 4.7 ms | 39 | 59.3 |
| pickup mix | Stable | 69.8 % | 72.8 % | 71.2 % | 82.6 % | 31.3 ms | 29 | 49.5 |
| microphone | Fast | 38.0 % | 68.9 % | 49.0 % | 60.5 % | 15.8 ms | 531 | 158.0 |
| microphone | Stable | 45.1 % | 58.3 % | 50.8 % | 70.6 % | 36.7 ms | 176 | 106.6 |

The hexaphonic single strings (secondary, the cleanest signal): Fast 72.6 %, Stable 74.5 %; by string, Fast / Stable: low E 23.7 % / 40.0 %, A 68.6 % / 73.7 %, D 76.5 % / 79.3 %, G 74.8 % / 76.6 %, B 70.6 % / 72.4 %, high E 74.7 % / 72.4 %.

IDMT-SMT-Drums, held-out odd takes, all kits, no calibration: kick 91.1 % (precision 95.8 %, recall 86.8 %), snare 81.6 % (83.5 % and 79.7 %), hat 83.0 % (94.6 % and 74.0 %); the hit is sent a median 16 to 17 ms after it. With calibration (five hits per drum from the kit's own training recordings): kick 88.3 %, snare 82.7 %, hat 83.1 %. Calibration does not raise these figures on this data.

MDB-stem-synth (resynthesised voices and instruments, a stress test, not the machine's target): note F 34.4 % in Guitar range, Fast; pitch right on 72 to 84 % of the voiced frames.

**Targets and what was missed**, in numbers:

- Melody F-measure 80 % (Stable) and 75 % (Fast) on GuitarSet single strings: **missed**. On the pickup mix: Stable 71.2 % (9 points short), Fast 74.3 % (0.7 short). On the microphone: about 50 % (Fast 49.0 %, Stable 50.8 %), far short. The microphone's room and the other strings' sympathetic ringing give many extra notes (107 to 158 a minute) and octave errors (531 in Fast). Fast is reported ahead of Stable at the 50 ms tolerance because Stable's median is 31 to 37 ms late; at 100 ms Stable is ahead (82.6 % against 79.7 %).
- The low E string is weak (23.7 % Fast, 40.0 % Stable on the hexaphonic string): the longest periods, the loudest sympathetic ringing. The range is not the cause: widening Guitar's lowest frequency from 80 to 70 Hz, and the window from 19 to 24 ms, changed low E by under 3 points on the development data (Fast 39.5 % before, 38.3 % with 70 Hz, 36.4 % with both).
- Drums: kick 91.1 % (target 90 %: met), hat 83.0 % (target 80 %: met), snare 81.6 % (target 90 %: **missed**, 8.4 points). Snare is confused with the tom-like hits of the real and techno kits and missed under loud hats; a spectral flatness and a 1 to 2 kHz body band were tried on the development takes and did not help.
- Guitar Fast median latency 20 ms: met (4.7 ms on the pickup mix, 15.8 ms on the microphone).
- Tried without gain: requiring two agreeing estimates before Fast's first note (the attack's one-semitone blips): no change in F (62.6 % to 62.6 % on the development hexaphonic data), median latency unchanged to a tenth of a millisecond; kept. A sub-harmonic check of each estimate against the spectrum (the energy at twice the pitch compared with the pitch's, ratios 2 and 6) on the development microphone set: ratio 6 raised Stable by 1.4 points and lowered Fast by 0.3, ratio 2 lowered both; not adopted.
- Use a pickup or a DI for the best results: the microphone figures are about 50 % and the pickup's 71 to 74 %.

## 8. Limits

- One note at a time. A chord, or several strings ringing together, gives one wandering note; GuitarSet's recordings are one string a line.
- The drum rules were tuned on the IDMT-SMT-Drums mixes (three kits) and the synthetic loops: other kits will be further from them. Calibrating helps little; five hits of a drum teach the classifier that drum, not the mix it will be heard in.
- A hit with two drums at once plays both notes; a loud cymbal hides the quieter drums under it.
- Pitch bend range must be set on the receiving synth to the same value.
- The microphone is poor: about 50 % F on GuitarSet's microphone takes, against 71 to 74 % on the pickup mix. Use a pickup or a DI.
- Drop-D and lower tunings: Guitar range starts at 80 Hz, so the low D (73 Hz) is out of it; use Bass range.
- Bass range is slower (a long window): Stable plays a note a median 47 ms late.
- AT1 held-out: pickup mix 76 to 77 %, microphone about 50 %, low E 29 / 39 %; the drum classifier still mistakes a snare for a hat or a tom (IDMT held-out snare 81.8 %).
- No plug-in export, no MPE, no on-device model: everything is a few KB of state.

## 9. AT1: a tuning round

What changed, with the held-out figures (GuitarSet players 03 to 05, IDMT odd takes; CI run 44695471371, job `am1`). Before is section 7.

**Melody constants** (`MelodyTuning`, tried on the development split, players 00 to 02, pickup mix F Fast / Stable 59.4 / 58.5 before, 63.4 / 63.1 after):
- Level gate 24 dB under the recent loudest (was 16), and a note over a sounding one must rise 1.5 times (was 1.2; 2.0 gave the same on the development data but cost the synthetic bass line its 50 ms gate).
- Stable: at least 20 ms (was 40), confidence 0.7, wait 30 ms. The median latency falls from 31 ms to 13 to 15 ms.
- Change confidence 0.8, correction confidence 0.9, an 8 ms settle for a note that starts without an onset.
- Each note carries an attack estimate (the onset, or the first voiced estimate minus half the tracker's window).
- The octave check by subharmonic summation (`OctaveCheck`, weight 0.84 a harmonic, Goertzel magnitudes) is built and **off** (`OctMargin` 0): on the development data it took Stable octave errors from 134 to 114 and cost 1.5 points of F in Fast.

**Held-out results** (before, section 7, in brackets):

| audio | response | F | latency median | octave errors | extra / min |
|---|---|---:|---:|---:|---:|
| pickup mix | Fast | 76.8 % (74.3) | 4.3 ms | 30 (39) | 50.0 (59.3) |
| pickup mix | Stable | 76.0 % (71.2) | 13.2 ms (31.3) | 30 (29) | 45.7 (49.5) |
| microphone | Fast | 49.1 % (49.0) | 13.2 ms | 496 (531) | 128.4 (158.0) |
| microphone | Stable | 51.4 % (50.8) | 19.4 ms | 311 (176) | 107.2 (106.6) |

Hexaphonic low E: Fast 28.6 % (23.7), Stable 38.8 % (40.0). All strings: Fast 74.3 %, Stable 75.5 %.

**Drums**: the onset threshold of the high band's detection function (3 to 16 kHz) uses the median and the median absolute deviation of the recent spread (`OnsetDetector.RobustSpread` with `RobustMask` 8, Drums mode only), so a dense run of equal hats no longer lifts the threshold above itself; the whole-spectrum, low and middle functions keep the mean and standard deviation. Using it on every function cost kick 1.8 and snare 2.2 points on IDMT held-out. Synthetic loops, hat F at 174 bpm 73.3 to 90.8 % (the hats on a kick's sixteenth, 82.2 to 87.8 %), at 140 bpm 78.5 to 91.7 %, at 120 bpm 86.4 to 85.6 %. IDMT held-out: kick 91.1 to 90.9 %, snare 81.6 to 81.8 %, hat 83.0 to 86.1 % (local run; the CI figures are in the report). Drums CPU 364x to 278x real time.

**Recorded timing**: `MidiEvent.Lag` is the number of samples between a sound's attack and the event. The router passes it to `IMachineMidiBus.SendMachineMIDI(source, data, earlierSamples)` while `Record at attack` is on (default), and the session recorder places the note that many samples earlier. Live output and the device output are unchanged. Neo Voice to MIDI uses the same parameter.

**Missed, in numbers**: melody 80 / 78 % on the pickup mix (76.0 / 76.8), low E 60 % (38.8), microphone 60 % (about 50), extra notes and octave errors halved (about 15 % fewer extras; octave errors 39 to 30 on the mix, higher on the microphone Stable), snare 90 % (81.8). Not measured: F on the recorded timestamps in `PitchEval` (the unit tests cover the placement: a synthetic guitar line in Stable, live F 95.0 %, recorded 97.9 %, median offset 0.1 ms).

**Tried without gain** (development split unless noted): raising `Level` to -34 / -30 / -26 dB (low E Stable 46.7 / 55.2 % at -36 / -30 dB, but the mix falls to 62.3 / 60.5 / 55.8 % F at -34 / -30 / -26 dB); no idle-start notes (F 56.3 %); a change settle of 5 to 15 ms (no net gain), of 60 ms (collapses); no corrections (Fast 58.0 %); the octave check above; a longer recent-maximum memory, a larger fresh-share and over-confidence (marginal). E-GMD drums (CC BY 4.0, 60 files used) gave kick 80 %, snare 59 %, hat 16 to 29 % with this classifier, so the drum rules were not retuned on it; the snare-versus-tom features, per-band floors and per-kit threshold normalisation were not built.

## 10. Slice to new MIDI track (SL1 as built)

Right-click an audio clip (Session slot, Arrangement clip, or the Audio page) and choose **Slice to new MIDI track...** next to Convert to MIDI. The clip is cut into at most 16 slices, each slice becomes a song wave, and a new MIDI track plays them from a Neo Sampler with one note per slice. One undo step. Agent tool `audio_slice_to_midi(machine, pattern|slot, by, apply)`.

**The dialog.** "Slice by": Transients (the default), Warp markers, 1 bar, 1/4, 1/8, 1/16, and always the number of slices ("12 slices - limit 16"). The region is the clip's loop or region, as Convert uses: from `min(Start, LoopStart)` when looped (else `Start`) to the loop end (else the clip's end). A grid or warp-marker choice that would make more than 16 slices is disabled, its tooltip says how many it would make; Transients with more than 16 onsets keeps the strongest and merges the rest into the slice before (the dialog says so). A cut of fewer than two slices is refused with the reason.

**The cut.** `ReBuzz/Core/Clips/SliceMath.cs` (pure, tested without the studio). The first slice starts at the region start; the others at the attacks found by the warp editor's transient detector joined with the drum converter's onset detector (the second hears a kick under a ringing cymbal that the first misses), each placed on the first rise of the level (the detectors' estimates are a little early): the sample in the 10 ms around the estimate where the energy of the next 0.5 ms most exceeds the previous 0.5 ms (at least 3 times), with a strength of the level reached in the next 15 ms over the level before. Attacks within 15 ms are one, within 12 ms of the region's end are dropped, and ones under 5 % of the strongest (a tail's wobble) are dropped. Audio below -60 dBFS before the first attack is not kept. With more than 16, the 15 strongest after the start stay. Grid choices cut at the beats of the grid; Warp markers at the markers inside the region.

**Where the cut starts.** Each slice starts at the sample of the beat its note will have on the clip notes' grid (1/240 of a tick, about 0.5 ms at 120 BPM and 4 ticks a beat): the note and its slice are never apart by the grid's rounding. The first slice of a loop is exact; the others are within 0.3 ms of the attack.

**The waves.** Cut from the clip's audio as it plays (reverse, gain; unwarped), in the first free song wave slots (1-based, empty ones), named "<clip> 01" and on (the clip's name cut to 24 characters), float, mono or stereo as the source, at the source's rate, root C-4 (Neo Sampler's Kit mode ignores roots). Fade in 1 ms (a raised cosine; none when the slice starts on a zero crossing in every channel, or at the audio's own start with a first sample under 1e-4) and fade out 2 ms (always). The clip's transpose and detune are not cut into the audio: they retune the zones (`Zn tune`, `Zn fine`), as the sampler repitches. One undo step restores the waves (the bytes are in the undo action's size).

**The track.** `NewInstrumentTrack("Neo Sampler", "<clip> slices")` (as Convert does), the sampler in Kit mode (`Mode` 1, `Kit base` 36: zone n plays wave slot n on key 36 + n - 1), `Vel -> amp` 0 (a slice plays at its own level at any velocity), every used zone in choke group 1. The clip is where the source is (same Session scene, same arrangement positions, same length, loop and region) with one note per slice at its beat position as warped (`beat * ticks per beat * 240`), as long as the slice (to the next one, the last to the region's end) and velocity 100.

**Left as it is.** The source clip is not muted: Convert does not mute it either and a clip has no mute of its own (the brief's "muted, never deleted" does not apply); mute the source's track to hear only the slices. Slices are not warped: at another song tempo the notes move with the clip's beats and each slice plays at its own speed (a 1/8 grid at 120 BPM played at 90 BPM has gaps).

**Measured** (`ReBuzz.Studio.Tests/SliceToMidiTests.cs`, `ReBuzz.SessionTests/SliceMathTests.cs`, headless, synthetic loops):
- A click track: boundaries within 1 ms of the known hits, notes on the right ticks. A drum loop (kicks, snares, hats): 16 hits found on two bars, the loop's region cuts one bar to 8.
- Counts by mode on an 8-beat loop: Transients, Warp markers, 1 bar (2), 1/4 (8), 1/8 (16), 1/16 refused (32); the cap on 24 clicks keeps the 15 strongest after the start and merges the rest.
- A warped clip at 90 BPM song tempo: notes at the clip's beats (the slice starts at the beat's sample), the render's onsets within the engine's sub-tick (below).
- Null test at the loop's own tempo (the 16-hit drum loop, mono and stereo, warp Off, the source muted in one render and the slices in the other): every slice replays its part of the source at -141 dB (mono, the 16-bit-free float render's floor) and -310 dB (stereo) outside 3 ms around its edges, 13 of 16 slices at exactly the source's sample. **The other three start 18 samples late (tick 28), 23 and 130 samples early (sub-tick) because the engine starts a note on its sub-tick grid** (a tick is divided into about 260-sample sub-ticks, ReBuzzCore.SubTickSize) **and a tick's start is a whole number of samples** (5512 against 5512.5), so a note can sound up to one sub-tick (about 6 ms) from its beat; the test measures each slice at its own offset and requires at most 300 samples. From FU2b a note can be: the slice notes carry a fine start (`NoteBits`: bits 24-31 of the event value) and the engine ends a block on that sample, so every slice of the new track starts within 1 sample of its slice point (test at 90, 120 and 133 BPM, mono and stereo). The 18 samples late and sub-tick early offsets above are the earlier build's.
- Save and reopen as .nbl and .bmx: the waves, the sampler's zones and the clip come back (the waves to 1e-4 of the float data).

**Limits.** 16 slices (Neo Sampler's zones); no sampler slice mode; a slice is audio only (no per-slice pitch other than the clip's transpose); a hit under a long open hat with the same energy is missed or merged (the null loop's hat on 2.25 s sits 10 dB under the kick's tail and is found at the lower ratio of 3); Pro warp's stretch is not applied (slices are the raw audio). Notes start on their slice's sample (FU2b; see above).
