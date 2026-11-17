#!/usr/bin/env python3
# NeoBuzz Live stem models (docs/specs/stem-separation.md): checks an exported model against the PyTorch one on a test clip.
# Both are run over the clip with the same chunking as the app (stemlib.chunked); the stems are compared: the largest absolute
# difference and the SDR of the ONNX stem against the PyTorch stem. With --reference the clip and the PyTorch stems are saved as raw
# float32 for the C# test (ReBuzz.SessionTests StemRealModelTests), which checks our own transform and chunker against them.
#
#   python3 validate.py <models folder> <model id> [--seconds 12] [--reference <folder>] [--report <json>]
import argparse
import gc
import json
import os
import sys
import time

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import stemlib  # noqa: E402

STEMS = ["vocals", "drums", "bass", "other"]


def providers(manifest, folder):
    """Yields (torch pass function, onnx pass function) for each pass of the manifest, one at a time (a bag's models are big)."""
    family = manifest["family"]
    for i, p in enumerate(manifest["passes"]):
        if family == "htdemucs":
            import demucs_onnx as m
            yield m.torch_pass(manifest["id"], i), m.onnx_pass(os.path.join(folder, p["file"]))
        else:
            import roformer_onnx as m
            yield m.torch_pass(manifest), m.onnx_pass(manifest, os.path.join(folder, p["file"]))


def assemble(manifest, per_pass):
    """per_pass: a [S, 2, N] array for each pass -> {stem: [2, N]} by the manifest's output mapping."""
    stems = {}
    for p, y in zip(manifest["passes"], per_pass):
        for stem, idx in p["outputs"].items():
            stems[stem] = sum(y[k] for k in idx)
    return stems


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("models")
    ap.add_argument("model_id")
    ap.add_argument("--seconds", type=float, default=12.0)
    ap.add_argument("--reference")
    ap.add_argument("--report")
    a = ap.parse_args()
    folder = os.path.join(a.models, a.model_id)
    manifest = json.load(open(os.path.join(folder, "model.json")))
    mix, rate = stemlib.test_clip(a.seconds)
    assert rate == manifest["sampleRate"]
    seg, overlap, s_model = manifest["segment"], manifest["overlap"], manifest["outputStems"]
    torch_passes, onnx_passes = [], []
    seconds_t = seconds_o = 0.0
    for (tfn, ofn) in providers(manifest, folder):
        t0 = time.time()
        torch_passes.append(stemlib.chunked(tfn, mix, seg, overlap, s_model))
        seconds_t += time.time() - t0
        t0 = time.time()
        onnx_passes.append(stemlib.chunked(ofn, mix, seg, overlap, s_model))
        seconds_o += time.time() - t0
        del tfn, ofn
        gc.collect()
    ref = assemble(manifest, torch_passes)
    got = assemble(manifest, onnx_passes)
    per = {}
    for stem in STEMS:
        per[stem] = {"maxAbsDiff": float(np.max(np.abs(ref[stem] - got[stem]))), "sdrDb": stemlib.sdr(ref[stem], got[stem])}
    report = {"id": manifest["id"], "quality": manifest["quality"], "clipSeconds": a.seconds,
              "maxAbsDiff": max(v["maxAbsDiff"] for v in per.values()), "sdrDb": min(v["sdrDb"] for v in per.values()),
              "perStem": per, "secondsTorch": seconds_t, "secondsOnnx": seconds_o,
              "onnxSecondsPerMinute": seconds_o * 60.0 / a.seconds}
    print(json.dumps(report, indent=1))
    if a.report:
        stemlib.write_json(a.report, report)
    if a.reference:
        d = os.path.join(a.reference, a.model_id)
        stemlib.save_f32(os.path.join(d, "clip.f32"), mix)
        for stem in STEMS:
            stemlib.save_f32(os.path.join(d, stem + ".f32"), ref[stem])
        stemlib.write_json(os.path.join(d, "reference.json"), {"samples": int(mix.shape[1]), "rate": rate, "stems": STEMS})


if __name__ == "__main__":
    main()
