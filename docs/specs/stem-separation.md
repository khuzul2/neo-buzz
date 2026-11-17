# Stem separation (as built)

"Separate stems..." splits a Neo Audio clip (Session or Arrangement) or an audio file (a browser item, or a file from the File menu) into vocals, drums, bass and other. The stems become new Neo Audio tracks named after them ("Bass - <source>"), with clips aligned to the source, in one undo step. Everything runs locally; the models are separate files.

## 1. Models

| Quality | Id | Model | Source | Passes |
|---|---|---|---|---|
| Fast | `htdemucs` | Demucs v4 hybrid transformer | facebookresearch/demucs | 1 |
| Balanced | `htdemucs_ft` | Demucs v4 fine-tuned bag | facebookresearch/demucs | 4 (one per stem) |
| Best | `bs-roformer-sw` | BS-RoFormer SW, six stems | jarredou/BS-ROFO-SW-Fixed (weights); ZFTurbo/Music-Source-Separation-Training (code, MIT); lucidrains/BS-RoFormer (architecture) | 1 |

**Why SW for Best.** The public four-stem BS-RoFormer checkpoint (ZFTurbo, `ep_17_sdr_9.6568`) scores a bass SDR of 8.48 dB on MUSDB (11.08 on Multisong), under htdemucs_ft's 9.4, and below the 11.3 the research note had assumed. A dedicated bass model plus Demucs for the rest would need two model families and a mixing rule. BS-RoFormer SW is the strongest single public checkpoint found (MVSEP: bass 14.62, drums 14.11, vocals 11.30, other 8.71). It has six outputs; guitar and piano are folded into "other" (`model.json` lists which outputs are summed per stem). Doubts: its weights carry no stated licence or provenance (section 6), and its ONNX export was written without access to the checkpoint when the code went in (the workflow is where it is proved).

A model is a folder with `model.json` and its `.onnx` files. `model.json` holds the id, quality, family (`htdemucs` or `spec-mask`), revision, sample rate, segment length, overlap, FFT size and hop, the stems, and the passes (`{file, outputs: {stem: [output indexes summed]}}`). The graphs hold the network only: no STFT inside, so no complex numbers, and CPU and CoreML providers see plain real tensors. Weights may be stored as 16-bit floats with a Cast back to 32-bit (half the size; htdemucs: 91 MB, 52.7 dB SDR against PyTorch).

Where the app looks: `Gear/Models/Stems`, then the folder set in Preferences > Stems, then the per-user data folder. "Install models..." takes the "NeoBuzz Live Stem Models" zip (a zip-slip guard; the folders land in the first writable place). A missing quality is greyed out in the dialog with a hint that names Install models....

## 2. The pipeline (`ReBuzz/Core/AudioLibrary/Stems/`, pure C#, no UI)

- `Stft`: a windowed FFT (the engine's `Fft`), torch's reflect padding, analysis into `[bin*frames + frame]` and synthesis with overlap-add divided by the squared window. Golden data from torch is in `SessionTests/Stems/Data/stft-golden.json`.
- `DemucsTransform`: htdemucs' own spectrogram (nfft 4096, hop 1024, normalised Hann, reflect pad, the Nyquist bin dropped, the edge frames) into the `cac` input, and its inverse added onto the time-domain output.
- `OverlapAdd`: chunks of the model's segment with the model's overlap (Demucs 25 %, RoFormer 50 %), triangle weights, the sum of weights divides the result, the last chunk zero-padded. No allocation per chunk: the input, output and tensor buffers are made once per pass.
- `OnnxStemPass`: `HtDemucsPass` (inputs `mix`, `cac`; outputs `freq`, `time`) and `SpecMaskPass` (`spec_real`, `spec_imag` in, the masked spectra out), on preallocated `OrtValue`s. CPU everywhere. CoreML on macOS is opt-in with `NEOBUZZ_STEMS_PROVIDER=coreml` and is untested (no Mac was available).
- `StemSeparator`: resamples to the model's rate (44.1 kHz), runs the passes, resamples back, and caches. Cancelling calls `RunOptions.Terminate` and stops between chunks. Longer than 20 minutes is refused.
- `StemCache`: `DataFolder/cache/<asset hash>-<model id>-r<revision>/<stem>.f32`, 2 GB, the oldest trimmed first. "Clear cache" is in Preferences > Stems.

## 3. In the app

- The job runs in the background with a progress bar and Cancel; the UI and the engine keep running.
- `StemFlow` (`ReBuzz.Studio/Shell/`): the dialog (quality radios, stem checkboxes, Install models...), the menus and the Preferences page. Entries: the Session slot menu of an audio clip, the arrangement context menu of an audio event, the browser's Audio items, and File > Separate Stems from Audio File....
- `SessionLayout.ApplyStems`: a new track per stem, a clip in the source's scene and at each of the source's arrangement events (for a file: the chosen slot and/or position), with the source's warp, tempo, gain and colour. The audio goes into the song's audio store, so the song stays self-contained. One undo group.
- The Classic WPF UI is not covered (only Studio).

## 4. Agent tool

`audio_separate {machine, pattern | path, quality, stems, slot, at_beats}` returns the model, whether it came from the cache, the seconds, and the new tracks (stem, machine, pattern, asset, length, slot, positions). It reports progress, honours cancellation, and is one undo step. Guides: `Neo Audio.md` and `composition.md`; `GuideTests` and `tools/agent-coverage.py` know it.

## 5. Models in CI (`.github/workflows/stem-models.yml`)

workflow_dispatch. It installs CPU torch, exports the models from their official sources (`tools/stems/demucs_onnx.py`, `roformer_onnx.py`), checks each against PyTorch on a synthetic clip (max absolute difference and SDR, `validate.py`), runs the C# pipeline against the PyTorch stems (`StemRealModelTests`, SDR at least 35 dB), runs a quality check with the engine's own audio (`quality_check.py`: Neo Drums, a Dye Essence bass, a Neo Poly pad and a Dye Essence lead mixed, separated by each model, SDR per stem and time per minute on the runner; nothing asserted), and uploads "NeoBuzz Live Stem Models" with a README (`make_readme.py`) that names each model, its source and the licence caveat.

**The references are checked first (ST1).** The first quality runs gave SDRs that did not tell the models apart (htdemucs, htdemucs_ft and BS-RoFormer within 0.5 dB of each other on every stem, drums at -1.5 dB), and the engine's mix was only 14.5 dB SDR from the sum of its soloed parts. The cause was not latency, gain, pan law, master processing or the resampler (a shift search on the mix and the sum peaked at 0 samples, and the levels agreed to 0.1 dB): Dye Essence was not reset for an offline render, so its random oscillator phases, whose seed moves on with every note, differed from one render to the next. The same lead rendered twice was -3 dB SDR against itself (uncorrelated), the bass differed between the first render and the others, and the whole-song mix against the sum of its parts left exactly the lead's own difference (-31 dB against a -17 dB mix). The references (the soloed drums, bass, and pad plus lead) were therefore not what was in the mix, so the separated stems could not score above the noise whatever the model. Dye Essence now builds a new engine in `Restart()` (as Neo Poly does) and puts its song state back on it (`ResetForRender`, `docs/specs/ux-overhaul.md` P4a), and `UxP4aTests` renders it three times for the same bytes. `quality_check.py` renders the mix twice and against the sum of its soloed parts, prints both and stops with an error (so no SDR is reported against wrong references) when the mix is not within 60 dB of the sum (it is about 247 dB now: float rounding), and reports each stem's lag against its reference (a cross-correlation within 2048 samples).

**The second cause: the length of the mix.** With the references right, the stems still scored 3.8 dB at best, every model alike, and a lag of 54 to 66 samples. The stems' levels matched the parts' to 0.1 dB, so they were fine; they were early. The render is a block or two longer than the song (32.003 s for 64 beats at 120 BPM). Imported as a file, Neo Audio takes it for a loop of whole bars and fits the 128 extra samples into 64 beats, a tempo of 119.989 BPM, so the clip plays 1e-4 fast: on time at the start and 128 samples early at the end. (Playing the mix back through a Neo Audio track with no separation at all gave -2.8 dB and the same lag; with the mix cut to its 1411200 samples it gives 120 BPM, lag 0 and 32.7 dB, the ceiling of Beats mode's slices and the clip's 64-sample fade-in.) `quality_check.py` now separates the mix cut to exactly its beats. This is the behaviour of any file whose length is not a whole number of bars; for the app it is the warp's tempo detection, not the separation, and is left as it is.

Measured locally (a loaded 2-core machine, so the times are poor): htdemucs ONNX against PyTorch 68 to 84 dB SDR, largest absolute difference 1.6e-4; the C# pipeline against the PyTorch stems 68 to 84 dB; htdemucs_ft 65 to 88 dB; about 4.4 s per 7.8 s segment, about 70 s of compute per minute of audio for htdemucs. The runner's figures are in the workflow's README and report.

## 6. Licences and limits

- Demucs: code MIT, weights published by the project under the same licence; trained on MUSDB18-HQ (non-commercial research data) plus 800 songs.
- BS-RoFormer SW weights: no stated licence and no documented training data. Code MIT. The README says so. The user decides whether to ship them or to look for a licensed alternative.
- The models are not bundled with the app or its licence; the user installs them.
- Limits: 20 minutes of audio at most; stereo at 44.1 kHz inside (other rates and mono are converted); memory is the model plus one segment (htdemucs_ft loads one pass at a time); the result is as good as the model (bleed, no vocals in a source without vocals is reported as a near-silent stem, not removed).
- Untested: the RoFormer export until the workflow has run, CoreML, the Classic WPF UI. The artifact is large (the three models, fp16: several hundred MB); a private repository's artifact quota may matter.
