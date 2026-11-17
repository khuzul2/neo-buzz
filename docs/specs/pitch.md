# The shared pitch engine (PT1)

| | |
|---|---|
| Status | Built (PT1): pitch tracker, onset detector, trigger features and classifier, vowel estimator, the evaluation tool and its workflow. No machine uses it yet. |
| Branch | `dsp/pitch-pt1` |
| Code | `Machines/NeoBuzz.Dsp/PitchTracker.cs`, `OnsetDetector.cs`, `TriggerClassifier.cs`, `VowelEstimator.cs` (all `internal`, namespace `NeoBuzz.Dsp`); tests `NeoBuzz.MachineTests/PitchTests.cs`, `PitchEngineTests.cs`, `PitchSignals.cs`; evaluation `tools/PitchEval`, `.github/workflows/pitch-eval.yml` |
| Builds on | `ComplexFft`, `Biquad`, `PortableMath` of the same library (the portable maths rule applies: the audio files use no `Math.Sin/Cos/Exp/Log/Pow`) |
| For | Neo Tune (NT1), Neo Voice to MIDI (VM1), Neo Audio to MIDI (AM1, AM2), offline Convert to MIDI |

## 1. What it is

Four small engines the pitch machines share. Each is allocation-free after construction, takes mono `float[]` blocks of any size (the tests use 256 at 48 kHz and other sizes; the result does not depend on the block size), has no locks and no static state, and reports its own latency. There is no machine UI in PT1.

| Engine | Input | Output |
|---|---|---|
| `PitchTracker` | audio | one `PitchEstimate` per hop: f0 Hz, MIDI pitch and cents, confidence, voiced flag, RMS |
| `OnsetDetector` | audio | `OnsetEvent`s: sample position, strength, level, when it was reported |
| `TriggerFeatures`, `TriggerClassifier`, `TriggerEngine` | the audio around an onset | a class, a confidence, or "none of these" |
| `VowelEstimator` | audio, and the tracker's voiced flag | smoothed weights Aa, Oo, Ee (0 ... 1) and F1/F2 for display |

## 2. The pitch tracker

`new PitchTracker(sampleRate, PitchRange, PitchQuality)`; `Process(x, offset, count, frames, frameOffset)` returns the number of estimates written (`MaxFrames(count)` sizes the buffer). Properties: `Window`, `Span`, `Hop`, `Latency`, `MinHz`, `MaxHz`, `DetectionSeconds(hz)`.

### 2.1 Algorithm

YIN with the difference function computed by FFT cross-correlation. At each hop the newest `W` samples are compared with the same `W` samples `tau` back, so every estimate uses the freshest audio (the cost is that a lag `tau` needs `W + tau` samples of history: `Span`). One complex FFT does both the newest-window and the whole-span transforms (real part and imaginary part); `r(tau) = IFFT(A conj(B))`, and `d(tau) = E_new + E_lag(tau) - 2 r(tau)` with the energies from prefix sums. Then the cumulative mean normalised difference, parabolic interpolation of the minimum.

- **Candidates.** Every local minimum of the normalised difference within 0.10 of the deepest is a candidate, ranked by the value at its parabola's vertex (the sample value of a minimum between two samples is biased by the fractional period, and with the plain sample value a bright voice near 520 Hz picked the octave below). The shortest period among the candidates wins (the octave guard: a sub-multiple of the period dips as deep).
- **Continuity.** A candidate near the last pitch is preferred over the shortest only when the shortest is worse by more than 0.04. A tie goes to the shortest: otherwise a wrong octave at the start of a note locks in.
- **Ceiling and edges.** No dip under 0.45 means no pitch (the deepest dip is still reported as `RawF0`, unvoiced, for the evaluation's raw pitch accuracy); a dip at the edge of the lag range is no pitch.
- **Voicing.** Confidence is `1 - d'` at the chosen dip. Hysteresis: voiced from confidence 0.8, unvoiced under 0.55, and an RMS gate (0.0008, about -62 dBFS) so silence and dither are not voiced. A 15 Hz DC blocker sits in front.
- **Output.** `F0`, `Midi` (fractional) and `Cents` (from the nearest semitone), `Confidence`, `Voiced`, `Rms`, `EndSample` (the newest sample the estimate used).

### 2.2 Ranges

| Range | Quality | Lowest | Highest | Window | Hop | Span / 2 (latency) |
|---|---|---:|---:|---:|---:|---:|
| Voice | Quality | 70 Hz | 1100 Hz | 21 ms | 2.7 ms | 17.7 ms |
| Voice | Low latency | 100 Hz | 1100 Hz | 5 ms | 1.35 ms | 7.5 ms |
| Guitar | | 80 Hz | 1200 Hz | 19 ms | 2.7 ms | 15.8 ms |
| Bass | | 38 Hz | 400 Hz | 40 ms | 5.3 ms | 33.2 ms |
| Full | | 38 Hz | 2000 Hz | 40 ms | 5.3 ms | 33.2 ms |

Low latency applies to Voice only (the other ranges ignore it). It trades the voice's lowest octave (down to 100 Hz instead of 70) and some stability at the start of a note for a 5 ms window.

### 2.3 Latency

Two figures. `Latency` is `Span / 2`: the delay between the middle of the audio an estimate analysed and the moment it is available, the number to subtract when lining the f0 curve up with the audio. `DetectionSeconds(hz)` is the delay from the start of a note to its first correct, voiced estimate, measured by the tests on a sine of that pitch (a window plus about a period, then the hysteresis):

| Range | Pitch (Hz): detection after the note starts (ms) |
|---|---|
| Voice, Low latency | 131: 12.4, 220: 9.7, 440: 5.6, 880: 4.3 |
| Voice, Quality | 131: 21.9, 220: 13.8, 440: 8.3, 880: 5.6 |
| Guitar | 98: 21.9, 196: 16.5, 392: 8.3, 784: 5.6 |
| Bass | 41: 48.2, 55: 42.9, 98: 32.3, 196: 16.4 |
| Full | 41: 48.2, 110: 27.0, 440: 11.1, 1760: 5.8 |

The latency of a low note is physics: it needs a window of about two periods.

### 2.4 Measured on synthetic sources (tests)

Sines, sawtooth and pulse trains in every range: raw pitch accuracy above 99 %, 95th percentile error under 6 cents (12 cents for sines above 700 Hz), no octave errors. A glottal-pulse voice through three formant resonators with 0.6 % jitter, 4 % shimmer and vibrato of 40 cents: all frames right, median error 3.4 cents, 95th percentile 10.7 cents. Karplus-Strong plucks with slides for the guitar and bass ranges: 100 %. Noise, pink noise and silence are never voiced. Low latency against Quality on voices from 130 to 523 Hz: 99.5 % against 99.9 % (the extra errors are octave errors in the first milliseconds of a note, worst on an "ee" at 523 Hz).

### 2.5 CPU and memory

Per 256-sample block at 48 kHz (block = 5333 us): Voice 53 us (1.0 %), Guitar 53 us, Bass 55 us, Full 73 us (1.4 %), Voice Low latency 71 us (1.3 %), measured in the test suite on the development machine (2 cores); the evaluation runner (4 cores) processed all five configurations at 46 to 103 times real time. Zero allocations per block (a test counts `GC.GetAllocatedBytesForCurrentThread`).

## 3. The onset detector

`new OnsetDetector(sampleRate)`; `Process(x, offset, count, events, eventOffset)`; `MaxEvents(count)`. Tunables: `Threshold` (4), `MinRatio` (2.2), `LowBandFactor` (1.5), `MinIntervalSeconds` (0.04), `Gate` (0.002, -54 dBFS), `RiseDb` (9), `LowRiseDb` (14).

- 10.7 ms Hann frames, hop a quarter of that (128 samples at 48 kHz); a SuperFlux-style flux (max-filtered square-root magnitudes, so vibrato does not fire) of four functions: the whole spectrum, 60-300 Hz, 0.3-3 kHz, 3-16 kHz. Each has an adaptive threshold: mean + `Threshold` x sigma over the recent past, at least `MinRatio` x the mean, plus a small floor.
- Two energy paths: a rise of the broadband level by `RiseDb` within two hops, and a rise of the low-passed (150 Hz) level against the mean of the last six hops by `LowRiseDb` (a kick's fundamental lies below the first bin, so the flux misses it).
- A spectral crossing counts only when the level is not falling, so a voice fading out does not fire.
- A candidate is confirmed one hop later (the peak must be a peak), then the position is refined to the first 0.5 ms block above 30 % of the block peak's level in the neighbourhood. `ReportedAt` is the sample at which the event became known (about 8 ms after the onset). `Strength` is the flux over its threshold, for velocity; `LevelDb` the peak level.
- Minimum inter-onset interval 40 ms (a hi-hat roll, fast guitar). For a voice, whose consonant is followed by a vowel that fires a second onset 50-80 ms later, 100 ms is better (section 7).

Synthetic kit (8 classes, 120-300 ms apart): F-measure 98.7 % (50 ms window), timing median 0.1 ms, 95th percentile 5.1 ms. At 75-100 ms apart 87.9 % (the misses are closed hats under a snare tail). Plucks 5 of 5, sung notes 4 of 4, no false onsets. Limit: steady noise 14 dB above the gate fires about once per 3 s and pink noise at -20 dBFS about four per 3 s; the gate is the user's remedy.

## 4. Trigger features and the classifier

`TriggerFeatures(sampleRate, lengthSeconds = 0.018)`; `Extract(ring, mask, onsetSample, features)` fills `Dimension` (19) numbers from a window starting 3 ms before the onset (a Tukey window; the pre-onset 18 ms are the background). The features:

1. eight octave-band shares of the energy, in dB (the shape of the spectrum), computed from the band-aggregated spectrum with the pre-onset background subtracted at 90 % (floored at 5 %), which is what keeps a kick after a hi-hat tail from looking like a hi-hat;
2. the spectral centroid (log2 re 1 kHz) and flatness over 16 bands;
3. the zero-crossing rate;
4. the steepest attack (dB/ms over 1 ms blocks) and the block of the peak;
5. five cepstral coefficients (a DCT of the log band energies);
6. the decay: the late six 1 ms blocks against the first six.

`TriggerClassifier`: k-NN (k = 3) over standardised features (mean and deviation of the training set), optional Fisher weights (`FisherWeights`), a vote by 1/distance, and a reject class: the answer is "none" when the nearest example of the winning class lies further than `RejectScale` (2.5) x the class's radius (the mean nearest-neighbour distance among its own examples, floored at 0.75 x the mean radius of all classes). `Add(class, features)`, `Train()`, `Classify(features)` returning a `TriggerResult` (class, confidence, distance), `Clear`, `ExamplesOf`. Persistence is plain text: `Serialize()` / `Deserialize(text)`, header `NBTRIG 2 19 <count>` then one line per example (class, then the 19 numbers). Nothing else is stored; the model is the examples.

`TriggerEngine(sampleRate, classifier)` is the onset detector, the features and the classifier in one stream; in `Learning` mode it returns the features for the machine to file as examples. Each `TriggerEvent` has `ReadyAt` (onset - 3 ms + the window; the classification is known about 15 ms after the onset).

### 4.1 Measured

Synthetic kit, 10 examples per class, three seeds: with hits 120-250 ms apart 3 classes 92 %, 4 classes 83 %, 8 classes 81 %; with 450-600 ms apart 4 classes 95 %, 8 classes 93 %. Of four untaught sounds, three (click, clap, rim shot) are rejected 20 of 20 times each; the fourth (a tom) 2 of 19: it sounds like a kick. Real recordings are in section 7; they are lower, and the spec says so.

## 5. The vowel estimator

`new VowelEstimator(sampleRate)`; `Push(x, offset, count)` for every block; `Update(voiced)` every 10 ms or so returns a `VowelResult` (`Valid`, `F1`, `F2`, `Aa`, `Oo`, `Ee`); `SmoothingSeconds` 0.05.

- The audio is low-passed (4th-order Butterworth) and decimated to about 10 kHz, pre-emphasised (0.97), the newest 28 ms Hann-windowed; linear prediction by autocorrelation with a 100 Hz Gaussian lag window and a -50 dB noise floor, Levinson-Durbin at order fs/1000 + 2 (12 at 9.6 kHz). The envelope comes from a 512-point FFT of the predictor.
- The formants F1 (first peak in 200-1100 Hz) and F2 (next peak in 700-3400 Hz at least 250 Hz above F1) are for display and the offline tools.
- The weights do not come from the peaks (two formants closer than the LPC resolution merge, as in "aa" and "oo") but from the envelope's highest point in three regions, Low (180-500 Hz), Mid (500-1400), High (1700-3400), in dB: `lm = L - M`, `hm = H - M`, `lh = L - H`. `Aa = s((-lm - 8)/2) s((14 - lh)/3)`, `Oo = s((lh - 6)/3) s((lm + 8)/3) s((-hm - 4)/3)`, `Ee = s((hm + 1)/3) s((lm + 8)/2.5)` with `s` the logistic function. On VocalSet vowels (eight singers, long tones) the region differences were: /a/ L-M -10 to -16 dB, /u/ L-H +7 to +21 and H-M mostly under -9, /i/ H-M mostly above 0. The constants were set on those 24 files and on the synthetic vowels together (the synthetic test voice has a first-order high-frequency lift so that its spectrum falls in the measured ranges), so the figures in section 7 for the same singers are not independent; the workflow's larger subset is the check. The weights are independent scores in 0 ... 1; a vowel between two shows two middling weights, silence none. Unvoiced input lets them fall (under 0.05 within 200 ms).
- Synthetic vowels (the test voice at several pitches, with the lift above): 24 of 24 right. Real voices in section 7: 87 % of 337 files right on VocalSet (section 7.3), /u/ the weakest.
- Limits: LPC finds a harmonic and not a formant when f0 is above about 300 Hz (a soprano's "ee" is unreliable); a whisper has no voiced frame; the three prototypes are an average adult's, so a very deep voice or a child's shifts all three; a second voice or a loud accompaniment ruins it.

## 6. The evaluation

`.github/workflows/pitch-eval.yml` (workflow_dispatch only) downloads the data on the runner from Zenodo (checksum checked against the one Zenodo publishes), runs `tools/PitchEval`, writes the report to the job summary and uploads the report and a per-stem CSV as the artifact `pitch-eval-report`. The data and any audio are never committed or uploaded (no excerpts: the MDB licence is non-commercial and the others ask for attribution; figures are enough). Inputs: which of the three sets, `mdb_limit` (stems, evenly spread, 0 = all 230) and `vocalset_per_group`. Locally: `dotnet run -c Release --project tools/PitchEval -- --mdb DIR --avp DIR --vocalset ZIP --out report.md --csv stems.csv`.

| Set | Licence | Used for |
|---|---|---|
| MDB-stem-synth (zenodo 1481172) | CC BY-NC 4.0 | pitch: RPA (50 cents), RCA, voicing recall and false alarm, overall accuracy, octave errors, per stem and overall, for each range, Low latency against Quality |
| Amateur Vocal Percussion (zenodo 5036529) | CC BY 4.0 | onset F-measure (50 ms) and latency; trigger accuracy, 10 hits per class per participant to train and the rest to test, 4 classes and 3 (kick, snare, hat) |
| VocalSet 1.1 (zenodo 1442513) | CC BY 4.0 | long tones, scales, arpeggios, vibrato, breathy, fry across voice types, read from the zip in place: voiced share, octave jumps, held-note stability, Low latency against Quality, and the vowel estimator against the labelled vowels (a, u, i) |

The metrics follow mir_eval's melody definitions; each estimate is aligned to the middle of the audio it analysed (not its newest sample), which is the fair reading for a trailing-window tracker. The raw pitch of unvoiced frames counts for RPA/RCA as mir_eval's does.

## 7. Measured on real recordings

Workflow run 44071427967 (head `d77b4dda`; the first full run, 44054292755, gave the same MDB and AVP figures, its VocalSet step found no files because the zip names singers `female1`, not `f1`; fixed). Reports as artifact `pitch-eval-report` of the run; the full per-stem table is in it.

### 7.1 MDB-stem-synth (230 stems, 934 min)

Raw pitch accuracy (50 cents), raw chroma accuracy, voicing recall, voicing false alarm, overall accuracy, octave errors (share of reference-voiced frames). "In range" is the stems whose median pitch lies inside the range's limits.

| Range | stems | RPA | RCA | VR | VFA | OA | Oct |
|---|---:|---:|---:|---:|---:|---:|---:|
| Voice, Quality, all stems | 230 | 81.8 % | 87.7 % | 83.5 % | 0.3 % | 91.3 % | 5.9 % |
| Voice, Quality, in range (70-1100 Hz) | 201 | 89.7 % | 93.2 % | 91.5 % | 0.3 % | 95.1 % | 3.5 % |
| Voice, Low latency, in range (100-1100 Hz) | 160 | 93.3 % | 95.5 % | 96.2 % | 0.0 % | 97.5 % | 2.2 % |
| Guitar, in range (80-1200 Hz) | 181 | 91.8 % | 94.4 % | 93.2 % | 0.3 % | 96.2 % | 2.5 % |
| Bass, in range (38-400 Hz) | 174 | 91.5 % | 95.0 % | 95.7 % | 0.7 % | 94.6 % | 3.5 % |
| Full, all stems | 230 | 94.4 % | 95.3 % | 96.3 % | 0.8 % | 96.5 % | 0.9 % |

Low latency against Quality on the stems in Low latency's range: RPA 93.3 % against 95.2 %, octave errors 2.2 % against 1.9 %. Reading: Full is the best all-round setting on these stems (they are synthesised instruments and voices over two and a half octaves, and the 40 ms window sees the lowest notes); the range-limited trackers are for when the source is known, they cost less latency, and a stem outside the range does badly by construction (a 63 Hz voice in the Voice range, a 700 Hz flute in Bass).

### 7.2 Amateur Vocal Percussion (280 recordings, 28 participants, 9778 hits; MacBook built-in microphone)

Onsets (a hit is found within 50 ms of the annotation):

| Threshold | Min. interval | precision | recall | F-measure | offset median | report delay median |
|---:|---:|---:|---:|---:|---:|---:|
| 4.0 (default) | 40 ms | 48.2 % | 96.5 % | 64.3 % | +2.8 ms | 3.4 ms |
| 4.0 | 100 ms | 67.7 % | 93.5 % | 78.5 % | +2.7 ms | 3.4 ms |
| 4.0 | 120 ms | 71.1 % | 92.0 % | 80.2 % | +2.7 ms | 3.4 ms |

Recall by class at the default: kick 98.9 %, snare 97.9 %, closed hat 95.7 %, open hat 92.6 %. Precision is low at 40 ms because the vowel after a consonant ("boom", "pff") fires a second onset 50-80 ms later; the annotators marked one event. A voice front end should use a 100-120 ms minimum interval; a percussion front end 40 ms.

Triggers, 10 hits per class per participant to train (the first ten detected), the rest to test; "of detected" counts only hits the detector found, "end to end" all annotated hits:

| Test | Classes | Hits | Of detected | End to end | Rejected |
|---|---:|---:|---:|---:|---:|
| rest of the class recordings | 4 | 4300 | 78.1 % | 73.7 % | 1.6 % |
| rest of the class recordings | 3 (kick, snare, hat) | 4300 | 84.3 % | 79.5 % | 1.7 % |
| improvisation (all four in a rhythm) | 4 | 3237 | 64.1 % | 62.1 % | 4.2 % |
| improvisation | 3 | 3237 | 71.7 % | 69.4 % | 4.5 % |
| trained on Personal, tested on Fixed (everyone says the same four) | 4 | 3319 | 37.6 % | 36.4 % | 9.1 % |

Feature window (4 classes, same recordings): 12 ms 74.4 %, 18 ms (default) 78.1 %, 25 ms 79.8 %, 35 ms 81.3 % of detected; classified 9, 15, 22 and 32 ms after the onset. Real beatboxing by novices is much less separable than the synthetic kit (section 4.1): the classifier is a usable "learn my sounds" engine for one person's own set (the machines should show the confidence and the reject, and let the user re-record a class), not a universal one: a model trained on one person's sounds does not carry to another's (Personal to Fixed 37.6 %).

### 7.3 VocalSet 1.1 (378 recordings, 72 min, 20 singers: 9 female, 11 male; up to 2 files per singer and technique, the straight long tones a, i and u)

No f0 truth: the Quality tracker is the reference for Low latency; an octave jump is two consecutive voiced estimates 10-14 semitones apart (stepwise scales and arpeggios contain none, so each is an error).

| Technique | voiced (Quality) | voiced (Low latency) | octave jumps per 1000 (Q / LL) | LL agrees with Q |
|---|---:|---:|---:|---:|
| long tones, straight | 99.8 % | 99.5 % | 0.24 / 0.37 | 99.2 % |
| long tones, messa di voce | 99.8 % | 99.7 % | 0.37 / 0.27 | 99.0 % |
| scales, straight | 99.8 % | 99.7 % | 0.22 / 0.27 | 98.7 % |
| arpeggios, straight | 99.6 % | 99.8 % | 0.33 / 0.29 | 98.9 % |
| arpeggios, vibrato | 95.5 % | 99.7 % | 1.83 / 0.93 | 97.4 % |
| arpeggios, belt | 96.4 % | 99.6 % | 0.76 / 0.48 | 98.8 % |
| arpeggios, breathy | 99.9 % | 99.7 % | 0.04 / 0.17 | 99.2 % |
| arpeggios, vocal fry | 85.7 % | 82.7 % | 6.65 / 20.07 | 79.7 % |
| excerpts, straight | 97.5 % | 97.2 % | 0.35 / 0.46 | 98.7 % |
| all | 97.6 % | 97.8 % | 0.85 / 1.64 | 97.6 % |

Held-note standard deviation inside straight long tones (0.6 s or longer, ends trimmed): 19.4 cents median (female 12.9, male 22.2). Vocal fry is the weak case for both trackers (the voice is a slow train of separate pulses and the period is not well defined) and Low latency's 20 jumps per 1000 there is its worst figure. Normal singing is clean: under 1 octave jump per 1000 estimates in every technique but vibrato (1.8) and fry.

Vowels (each file's weights averaged over its voiced estimates, the largest taken; a, u, i are taken as aa, oo, ee): a 86.8 % of 159, i 90.4 % of 157, u 66.7 % of 21; 87.2 % of 337 overall (female 88.0 %, male 86.6 %). The constants were fitted on eight of these singers, so this is not independent of the fit (section 5). /u/ is the weak vowel: it is called ee seven times of 21 (not investigated further).

### 7.4 What the numbers do not say

The synthetic tests (sections 2.4, 3, 4.1, 5) check the algorithms against known truth; the real sets check them against recordings that are not the algorithms' own. MDB's stems are synthesised from real multitracks; AVP is novices through a laptop microphone; VocalSet is trained singers in a studio, one language. Nothing here covers a noisy room, a headset, a phone or a second voice.

### 7.5 Licences

MDB-stem-synth: CC BY-NC 4.0 (Salamon, Bittner, Bonada, Bosch, Gomez, Bello), used for measurement only; Amateur Vocal Percussion: CC BY 4.0 (Delgado, McDonald, Xu, Sandler); VocalSet: CC BY 4.0 (Wilkins, Seetharaman, Wahl, Pardo). The licence is read from each Zenodo record when the workflow runs and printed in its log. The workflow stores none of the audio.

## 8. Limits and what the machines should do

- Monophonic: a chord or a second voice gives the louder or the shorter period, with octave errors.
- The tracker needs a window and a period: the lowest notes of a bass (under 45 Hz) take 40-50 ms to appear. Show the latency in the machine and compensate when recording (`Latency`, `DetectionSeconds`).
- Low latency Voice cannot see under 100 Hz (a bass voice, fry) and wobbles at the start of a note; Quality is the default for MIDI, Low latency for live use.
- Onsets: steady noise above the gate and a vowel after a consonant fire extra onsets; the machines expose Threshold and the minimum interval (100 ms suits a voice).
- Triggers: trained on 10 hits of the user's own sounds the accuracy depends on how distinct the sounds are and on how steady the user is (section 7); the machines must let the user re-record a class and show the reject. A longer window (25-35 ms) is a little better on real recordings, at 10-15 ms more latency (`TriggerFeatures(sampleRate, lengthSeconds)`).
- Vowels: section 5.
- The evaluation data are small (a subset of VocalSet, one language, studio singers); the numbers are a guide, not a guarantee.
