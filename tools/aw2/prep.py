#!/usr/bin/env python3
"""Turns fetched items (fetch.py) into training shards: the log-mel features of the beat model's own front end (the C# BeatFront, through AutoWarpEval --feat), for the
audio as it is and for K warped, transposed variants of each training item, with the labels mapped through the warp (augment.py).

    prep.py ITEMS_DIR SHARD_DIR --eval-dir EVAL_DIR --variants 2 --seed 1 --eval-exe AutoWarpEval.dll --warp aw2warp

A shard is NAME__vK.npz: X uint8 (frames, 128), beats and downbeats (seconds, frame 0 = 0 s), beats_per_bar, dataset, bpm. Test items are not shards: their audio and labels are
copied to EVAL_DIR for the evaluation (and never trained on). Features are version-keyed: change FEATURE_VERSION with BeatFront.
"""
import argparse, glob, json, os, shutil, subprocess, sys, tempfile
import numpy as np
from common import RATE, write_wav, fnv
import augment

FEATURE_VERSION = 1


def read_wav(path):
    import soundfile as sf
    x, r = sf.read(path, dtype="float32")
    assert r == RATE
    return x


def features(exe, wavs, outs):
    lst = tempfile.mktemp(suffix=".txt")
    with open(lst, "w") as f:
        for w, o in zip(wavs, outs):
            f.write(f"{w}\t{o}\n")
    subprocess.run(["dotnet", exe, "--feat", lst], check=True)
    os.remove(lst)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("items"); ap.add_argument("shards"); ap.add_argument("--eval-dir"); ap.add_argument("--variants", type=int, default=2)
    ap.add_argument("--seed", type=int, default=1); ap.add_argument("--eval-exe", required=True); ap.add_argument("--warp", required=True)
    ap.add_argument("--max-test-seconds", type=float, default=150)
    a = ap.parse_args()
    os.makedirs(a.shards, exist_ok=True)
    if a.eval_dir:
        os.makedirs(a.eval_dir, exist_ok=True)
    work = tempfile.mkdtemp()
    jobs = []                                   # (shard path, wav path, labels)
    for jp in sorted(glob.glob(os.path.join(a.items, "*.json"))):
        meta = json.load(open(jp))
        name = os.path.basename(jp)[:-5]
        wav = jp[:-5] + ".wav"
        base = name.split("__")[0]
        # AW2b: a validation split apart from the test halves (thresholds and choices are set on it): Slakh's own, else one track in eight by hash (a track with its stem remixes).
        if meta["split"] == "train" and meta["dataset"] != "slakh" and meta["dataset"] != "generated" and fnv(base) % 8 == 1:
            meta["split"] = "val"
        if meta["split"] in ("test", "val"):
            if a.eval_dir:
                import soundfile as sf
                x = read_wav(wav)[: int(a.max_test_seconds * RATE)]
                sec = len(x) / RATE
                meta["beats"] = [b for b in meta["beats"] if b < sec]; meta["downbeats"] = [b for b in meta["downbeats"] if b < sec]
                write_wav(os.path.join(a.eval_dir, name + ".wav"), x)
                json.dump(meta, open(os.path.join(a.eval_dir, name + ".json"), "w"))
            continue
        rng = np.random.default_rng(a.seed * 1000003 + sum(map(ord, name)))
        x = read_wav(wav)
        sec = len(x) / RATE
        bpm0 = augment.base_bpm(meta["beats"])
        jobs.append((os.path.join(a.shards, name + "__v0.npz"), wav, meta, 0.0, bpm0))
        for v in range(1, a.variants + 1):
            if bpm0 <= 0:
                break
            curve, kind, target = augment.make_curve(rng, sec, bpm0)
            semi = int(rng.integers(-5, 7))
            y, mi, mo = augment.warp(a.warp, x, curve, semi, work)
            outsec = len(y) / RATE
            wp = os.path.join(work, f"{name}__v{v}.wav")
            write_wav(wp, y)
            m2 = dict(meta)
            m2["beats"] = augment.map_labels(meta["beats"], mi, mo, outsec).tolist()
            m2["downbeats"] = augment.map_labels(meta["downbeats"], mi, mo, outsec).tolist()
            m2["aug"] = {"curve": kind, "target_bpm": round(target, 1), "semitones": semi}
            jobs.append((os.path.join(a.shards, f"{name}__v{v}.npz"), wp, m2, semi, target))
        if len(jobs) >= 40:
            flush(a, jobs, work)
            jobs = []
    flush(a, jobs, work)
    shutil.rmtree(work, ignore_errors=True)


def flush(a, jobs, work):
    if not jobs:
        return
    outs = [os.path.join(work, f"f{i}.u8") for i in range(len(jobs))]
    features(a.eval_exe, [j[1] for j in jobs], outs)
    for (shard, wav, meta, _, _), o in zip(jobs, outs):
        X = np.fromfile(o, np.uint8).reshape(-1, 128)
        os.remove(o)
        np.savez_compressed(shard, X=X, beats=np.asarray(meta["beats"], np.float32), downbeats=np.asarray(meta["downbeats"], np.float32),
                            bpb=meta["beats_per_bar"], dataset=meta["dataset"], version=FEATURE_VERSION)
        if "__v" in os.path.basename(wav) and os.path.dirname(wav) == work:
            os.remove(wav)
    print("shards:", len(jobs), flush=True)


if __name__ == "__main__":
    main()
