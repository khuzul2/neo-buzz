# Pitch engine evaluation on real recordings

Run 2026-11-19 18:37 UTC on 2 cores, .NET 10.0.13. The engine is `Machines/NeoBuzz.Dsp` (PitchTracker, OnsetDetector, TriggerFeatures and TriggerClassifier, VowelEstimator) as the machines will use it, fed 256 samples at a time. The data are downloaded on the runner and are not kept; the licences are in the last section.

## Neo Audio to MIDI, Melody mode: GuitarSet solo takes (a single-note line)

30 solo takes (players 00,01,02), mono audio (the microphone, the pickup mix) against every string's notes merged into one line (an earlier note is trimmed to the next onset). The engine is the machine's (Melody, Guitar range, default settings). A note is right when its onset is within 50 ms of a truth note's and its pitch within 50 cents; F exact is the same with the semitone exact. Extra notes are notes that match nothing.

| audio | response | notes ref | notes out | precision | recall | F-measure | F exact semitone | F at 100 ms | latency median | p95 | octave errors | extra notes / min |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| mic | Fast | 3153 | 4860 | 35.9% | 55.3% | **43.5%** | 43.5% | 54.3% | 16.1 ms | 46.5 ms | 325 | 179.5 |
| mic | Stable | 3153 | 3264 | 45.1% | 46.7% | **45.8%** | 45.8% | 62.1% | 41.9 ms | 47.9 ms | 117 | 107.7 |
| pickup mix | Fast | 3153 | 4162 | 52.2% | 68.9% | **59.4%** | 59.4% | 65.8% | 6.6 ms | 40.9 ms | 174 | 116.7 |
| pickup mix | Stable | 3153 | 3251 | 57.7% | 59.5% | **58.6%** | 58.6% | 69.0% | 36.4 ms | 43.5 ms | 135 | 79.6 |

## Neo Audio to MIDI, Melody mode: GuitarSet single strings (hexaphonic, secondary)

1 recordings (6 string lines, 2 minutes) of GuitarSet's hexaphonic pickup (debleeded, 6 channels, one string each), against the JAMS note annotation of each string. The engine is the machine's (Melody, Guitar range, default settings, 256-sample blocks); a note is right when its onset is within 50 ms of an annotated note's and its pitch within 50 cents of it. Latency is the note on's time less the annotated onset (the annotation's own accuracy is a few tens of milliseconds). Octave errors are notes at the right time an octave out; extra notes are notes that match nothing.

| configuration | notes ref | notes out | precision | recall | F-measure | F exact semitone | F at 100 ms | latency median | p95 | octave errors | extra notes / min |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Guitar range, Fast | 89 | 221 | 36.2% | 89.9% | **51.6%** | 51.6% | 56.1% | 5.1 ms | 39.6 ms | 0 | 72.0 |
| Guitar range, Stable | 89 | 159 | 45.9% | 82.0% | **58.9%** | 58.9% | 70.2% | 35.4 ms | 44.5 ms | 0 | 43.9 |

F-measure by string (Fast / Stable): low E 60.6% / 46.2%; A 69.9% / 81.2%; D 23.4% / 30.5%; G 0.0% / 0.0%; B 0.0% / 0.0%; high E 0.0% / 0.0%

## Data and licences

Licence of each record as its Zenodo page states it, checked when the workflow was written (docs/specs/pitch.md §7.5):

- **MDB-stem-synth** (https://zenodo.org/records/1481172), J. Salamon, R. Bittner, J. Bonada, J. J. Bosch, E. Gomez, J. P. Bello: Creative Commons Attribution-NonCommercial 4.0 (CC BY-NC 4.0). Used here for measurement only; nothing derived from it is shipped.
- **Amateur Vocal Percussion Dataset** (https://zenodo.org/record/5036529), A. Delgado, S. McDonald, N. Xu, M. Sandler: CC BY 4.0.
- **VocalSet** (https://zenodo.org/records/1442513), J. Wilkins, P. Seetharaman, A. Wahl, B. Pardo: CC BY 4.0. Version 1.1 (VocalSet11.zip, 2 GB) is the one read; version 1.2 (6 GB) holds the same recordings in three folder layouts.
