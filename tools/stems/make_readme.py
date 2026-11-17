#!/usr/bin/env python3
# NeoBuzz Live stem models (docs/specs/stem-separation.md): writes the README of the "NeoBuzz Live Stem Models" artifact.
#
#   python3 make_readme.py <models folder> <reports folder> <out README.md>
# The reports folder holds <id>.json from validate.py (and optionally quality.json from quality_check.py, csharp-<id>.json).
import json
import os
import sys

models, reports, out = sys.argv[1:4]

SOURCES = {
    "htdemucs": ("Fast", "Demucs v4, hybrid transformer (facebookresearch/demucs, pretrained \"htdemucs\")",
                 "Code: MIT. The weights are published by the Demucs project under the repository's MIT licence. They were trained on MUSDB18-HQ "
                 "(non-commercial research data) plus 800 extra songs, so ask yourself whether the licence of the training data matters for your use."),
    "htdemucs_ft": ("Balanced", "Demucs v4, fine-tuned bag of four models, one per stem (pretrained \"htdemucs_ft\")",
                    "As htdemucs. Four times the work of htdemucs for a little more quality."),
    "bs-roformer-sw": ("Best", "BS-RoFormer SW, six stems (bass, drums, other, vocals, guitar, piano), weights from jarredou/BS-ROFO-SW-Fixed on Hugging Face; "
                       "architecture lucidrains/BS-RoFormer, code and config from ZFTurbo/Music-Source-Separation-Training",
                       "Code: MIT. THE WEIGHTS CARRY NO STATED LICENCE and their training data is not documented: treat them as unlicensed for anything "
                       "but your own use until their author says otherwise. Guitar and piano are folded into the \"other\" stem."),
}


def load(path):
    try:
        return json.load(open(path))
    except Exception:
        return None


lines = ["# NeoBuzz Live Stem Models", "",
         "Three stem separation models as ONNX files, for NeoBuzz Live's \"Separate stems\" (Preferences > Stems > Install models..., or put the",
         "folders into `Gear/Models/Stems`). Each folder holds a `model.json` (how to run it) and its `.onnx` file(s).",
         "The STFT, the chunking and the resampling are done by the app; the ONNX graphs hold the network only.", "",
         "| Quality | Folder | Source | Size |", "|---|---|---|---|"]
for mid in sorted(os.listdir(models)):
    folder = os.path.join(models, mid)
    if not os.path.isdir(folder):
        continue
    size = sum(os.path.getsize(os.path.join(folder, f)) for f in os.listdir(folder)) / 1e6
    q, src, _ = SOURCES.get(mid, ("?", mid, ""))
    lines.append(f"| {q} | `{mid}` | {src} | {size:.0f} MB |")
lines += ["", "## Licences", ""]
for mid in sorted(os.listdir(models)):
    if mid in SOURCES and os.path.isdir(os.path.join(models, mid)):
        lines += [f"- **{mid}**: {SOURCES[mid][2]}"]
lines += ["", "These models are not part of NeoBuzz Live's licence. You are responsible for your use of them.", "",
          "## Export check against PyTorch", "",
          "Each model was exported from its official source and run beside the original on a synthetic test clip with the same chunking.", "",
          "| Model | Max abs difference | Worst stem SDR vs PyTorch (dB) | ONNX seconds per minute of audio |", "|---|---|---|---|"]
for mid in sorted(os.listdir(models)):
    r = load(os.path.join(reports, mid + ".json"))
    if r:
        lines.append(f"| {mid} | {r['maxAbsDiff']:.2e} | {r['sdrDb']:.1f} | {r['onnxSecondsPerMinute']:.0f} |")
    elif os.path.isdir(os.path.join(models, mid)):
        lines.append(f"| {mid} | not validated | | |")
cs = [load(os.path.join(reports, f)) for f in sorted(os.listdir(reports)) if f.startswith("csharp")] if os.path.isdir(reports) else []
if cs and cs[0]:
    lines += ["", "The same clip through NeoBuzz Live's own pipeline (C# STFT, chunker, ONNX Runtime) against the PyTorch stems is in `csharp-*.json` of the CI run.", ""]
q = load(os.path.join(reports, "quality.json"))
if q:
    lines += ["", "## Quality check on the engine's own audio", "",
              f"A {q.get('audio_seconds')} s mix of Neo Drums, a Dye Essence bass, a Neo Poly pad and a Dye Essence lead, separated by each model; "
              "SDR against the parts that were mixed (drums, bass, other = pad + lead). The references are checked first: the engine's mix against the sum of "
              f"its soloed parts is {q.get('mixing_check_db')} dB SDR and the mix rendered twice {q.get('render_repeat_db')} dB (at least 60 wanted, or the check stops). "
              "Lag is the shift in samples of the separated stem against its reference (0: aligned). Nothing else is asserted; this is a measurement on a GitHub runner.", "",
              "| Model | Stem | SDR (dB) | Lag (samples) | Seconds per minute of audio |", "|---|---|---|---|---|"]
    for name, row in q.get("models", {}).items():
        if not row.get("ok"):
            lines.append(f"| {name} | failed | | | |")
            continue
        for stem, e in row["stems"].items():
            lines.append(f"| {name} ({row.get('model')}) | {stem} | {e.get('sdr_db', 'n/a')} | {e.get('lag_samples', 'n/a')} | {row['seconds_per_minute']} |")
open(out, "w").write("\n".join(lines) + "\n")
