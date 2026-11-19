#!/usr/bin/env python3
"""Basic Pitch's own notes for audio files: the reference the C# port (ReBuzz/Core/AudioLibrary/AudioToMidi) is checked against.

    basic_pitch_reference.py --model nmp.onnx --out notes.json file1.wav file2.wav ...

Runs the upstream `basic_pitch` package (Spotify, Apache-2.0: pip install basic-pitch, or its checkout on PYTHONPATH) on the same ONNX
model file the app embeds, with the upstream's default thresholds, and writes {file: [[start, end, pitch, amplitude], ...]} (note events
before the MIDI export, as predict() returns them) and, with --activations, the model outputs too (npz next to the json) so the C#
side can be compared frame by frame. Nothing here is shipped: it is for the evaluation workflow and for the tests' data.
"""
import argparse
import json
import os
import sys


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--activations", help="directory: one .npz per file with note, onset and contour")
    ap.add_argument("--upstream", help="a checkout of spotify/basic-pitch to import instead of an installed package")
    ap.add_argument("files", nargs="+")
    a = ap.parse_args()
    if a.upstream:
        sys.path.insert(0, a.upstream)
    import numpy as np
    from basic_pitch.inference import Model, predict

    model = Model(a.model)
    result = {}
    for f in a.files:
        output, _midi, events = predict(f, model)
        result[os.path.basename(f)] = [[float(s), float(e), int(p), float(amp)] for s, e, p, amp, _bends in events]
        if a.activations:
            os.makedirs(a.activations, exist_ok=True)
            np.savez_compressed(os.path.join(a.activations, os.path.basename(f) + ".npz"), **{k: v.astype("float32") for k, v in output.items()})
    with open(a.out, "w") as fh:
        json.dump(result, fh)


if __name__ == "__main__":
    main()
