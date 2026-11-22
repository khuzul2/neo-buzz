#!/usr/bin/env python3
"""Measure-only sets (AW2c): GTZAN and Ballroom, runner side. The audio is downloaded from the sets' own hosts (GTZAN: the Hugging Face copy of the Marsyas archive, Ballroom: the
MTG ISMIR 2004 tempo contest data), the beat and downbeat annotations from the published annotation repositories (Beat This!'s `beat_this_annotations`, `BallroomAnnotations`).
Nothing of it is trained on, committed or uploaded: items are written to a runner directory for AutoWarpEval, only the metrics leave the runner. RWC is not freely downloadable
(a licensed purchase), so it is skipped here.

    measure.py DATA_DIR [gtzan] [ballroom]       writes DATA_DIR/<set>/NAME.wav and NAME.json (split "test")
"""
import json, os, re, subprocess, sys, tarfile, zipfile
import numpy as np

AUDIO = {"gtzan": "https://huggingface.co/datasets/marsyas/gtzan/resolve/main/data/genres.tar.gz",
         "ballroom": "http://mtg.upf.edu/ismir2004/contest/tempoContest/data1.tar.gz"}
ANN = {"gtzan": "https://github.com/CPJKU/beat_this_annotations", "ballroom": "https://github.com/CPJKU/BallroomAnnotations"}
EXT = (".wav", ".au", ".mp3", ".flac", ".ogg")


def sh(*a):
    print("+", " ".join(a), flush=True)
    return subprocess.run(a, check=True)


def key(s):
    return re.sub(r"[^a-z0-9]", "", os.path.splitext(os.path.basename(s))[0].lower())


def read_beats(path):
    beats, downs = [], []
    for line in open(path, errors="ignore"):
        p = line.replace(",", " ").split()
        if not p:
            continue
        try:
            t = float(p[0])
        except ValueError:
            continue
        beats.append(t)
        if len(p) > 1:
            try:
                if int(float(p[1])) == 1:
                    downs.append(t)
            except ValueError:
                pass
    return beats, downs


def run(name, root):
    work = os.path.join(root, "_" + name)
    out = os.path.join(root, name)
    os.makedirs(work, exist_ok=True); os.makedirs(out, exist_ok=True)
    try:
        sh("git", "clone", "--depth", "1", ANN[name], os.path.join(work, "ann"))
    except Exception as e:  # noqa: BLE001
        print(f"{name}: the annotations could not be fetched ({e}); skipped", flush=True); return 0
    annfiles = []
    for d, _, fs in os.walk(os.path.join(work, "ann")):
        if ".git" in d:
            continue
        for f in fs:
            if f.endswith((".beats", ".beat", ".txt")) and (name != "gtzan" or "gtzan" in os.path.join(d, f).lower()) and (name != "ballroom" or True):
                annfiles.append(os.path.join(d, f))
    print(f"{name}: {len(annfiles)} annotation files, e.g. {annfiles[:3]}", flush=True)
    tarp = os.path.join(work, "audio.tgz")
    try:
        sh("curl", "-sS", "-L", "--retry", "6", "--retry-all-errors", "--speed-limit", "20000", "--speed-time", "120", "-o", tarp, AUDIO[name])
        with tarfile.open(tarp) as t:
            t.extractall(os.path.join(work, "audio"))
    except Exception as e:  # noqa: BLE001
        print(f"{name}: the audio could not be fetched ({e}); skipped", flush=True); return 0
    os.remove(tarp)
    ak = {}
    for f in annfiles:
        ak.setdefault(key(f), f)
    n = 0
    for d, _, fs in os.walk(os.path.join(work, "audio")):
        for f in sorted(fs):
            if not f.lower().endswith(EXT) or f.startswith("."):
                continue
            k = key(f)
            a = ak.get(k) or ak.get("gtzan" + k) or next((v for kk, v in ak.items() if kk.endswith(k) or (len(k) > 8 and k in kk)), None)
            if not a:
                continue
            beats, downs = read_beats(a)
            if len(beats) < 8:
                continue
            nm = f"{name}_{k}"
            wav = os.path.join(out, nm + ".wav")
            subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", os.path.join(d, f), "-ac", "1", "-ar", "22050", wav], check=True)
            json.dump({"beats": beats, "downbeats": downs, "beats_per_bar": 4, "split": "test", "dataset": name, "licence": "measure-only", "source": name}, open(os.path.join(out, nm + ".json"), "w"))
            n += 1
    print(f"{name}: {n} items matched to annotations", flush=True)
    return n


if __name__ == "__main__":
    root = sys.argv[1]
    for s in (sys.argv[2:] or ["gtzan", "ballroom"]):
        run(s, root)
