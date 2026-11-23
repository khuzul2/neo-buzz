#!/usr/bin/env python3
"""Fetches the AW2 pilot's training and evaluation data onto the runner (docs/specs/neo-audio.md, AW2 as built). Every source is licence-checked at fetch time
(the record's licence, and for FSL10K each loop's own); anything that is not CC BY, CC0 or public domain is dropped and the drop is logged. Nothing is committed.

    fetch.py gmd|egmd|guitarset|candombe|aam|fsl10k OUT [--limit N]

Writes OUT/NAME.wav (mono, 22.05 kHz) and OUT/NAME.json ({beats, downbeats, beats_per_bar, split: train|test, dataset, licence, source}) and OUT/licences.tsv.
The split is by who played (GMD: two held-out drummers; GuitarSet: two held-out players), by performance (Candombe) or by a hash of the name (AAM, FSL10K).
"""
import argparse, collections, csv, io, json, os, re, sys, zipfile
import numpy as np
import soundfile as sf
from common import *


def gmd(out, limit):
    """Groove MIDI Dataset v1.0.0 (Magenta, CC BY 4.0). The official splits are by session and every drummer is in all of them, so the held-out drummers are our own:
    drummer5 and drummer9 are the test, the other eight the training; 4/4 recordings of the 'beat' type only, as AW1 used. Beats come from the MIDI's tempo."""
    z = Zip("https://storage.googleapis.com/magentadata/datasets/groove/groove-v1.0.0.zip")
    rows = list(csv.DictReader(io.TextIOWrapper(io.BytesIO(z.read("groove/info.csv")), "utf-8")))
    rows = [r for r in rows if r["audio_filename"] and r["time_signature"] == "4-4" and r["beat_type"] == "beat" and 8 < float(r["duration"]) < 120]
    rows.sort(key=lambda r: fnv(r["id"]))
    n = collections.Counter()
    for r in rows:
        test = r["drummer"] in ("drummer5", "drummer9")
        split = "test" if test else "train"
        if n[split] >= (limit if not test else max(8, limit // 3)):
            continue
        x, rate = sf.read(io.BytesIO(z.read("groove/" + r["audio_filename"])))
        x = to_mono22k(x, rate)
        bpm = float(r["bpm"])
        beats = [k * 60 / bpm for k in range(int(len(x) / RATE * bpm / 60) + 1)]
        name = "gmd_" + r["id"].replace("/", "_")
        save_item(out, name, x, beats, beats[::4], 4, split, "gmd", "CC-BY-4.0", "Groove MIDI Dataset v1.0.0 (Magenta), " + r["drummer"], {"bpm": bpm})
        n[split] += 1
    print("gmd:", dict(n), flush=True)


def egmd(out, limit):
    """E-GMD, the Expanded Groove MIDI Dataset (Callender, Hawthorne, Hawthorne; ICML 2020; Magenta; CC BY 4.0): the Groove MIDI Dataset's performances played on 43 drum-kit
    sounds of a Roland TD-17, so the same grooves (same MIDI, same beats) on many kits. AW2c uses a subset spread over the kits for training only: the drummers held out of GMD
    (drummer5 and drummer9) are left out altogether, so no held-out performance is trained on in another sound. 4/4 'beat' recordings, 8 to 120 s."""
    import requests
    page = retry(lambda: requests.get("https://magenta.tensorflow.org/datasets/e-gmd", timeout=60).text, what="e-gmd page")
    lic = "CC-BY-4.0" if re.search(r"Creative Commons Attribution 4\.0 International \(CC BY 4\.0\)", page) else None
    print("egmd licence on the dataset page:", lic, flush=True)
    assert lic and licence_ok(lic), "E-GMD licence not confirmed on its page"
    z = Zip("https://storage.googleapis.com/magentadata/datasets/e-gmd/v1.0.0/e-gmd-v1.0.0.zip")
    names = z.names()
    csvn = next(n for n in names if n.endswith(".csv"))
    prefix = csvn.rsplit("/", 1)[0] + "/" if "/" in csvn else ""
    rows = list(csv.DictReader(io.TextIOWrapper(io.BytesIO(z.read(csvn)), "utf-8")))
    rows = [r for r in rows if r["audio_filename"] and r["time_signature"] == "4-4" and r["beat_type"] == "beat" and 8 < float(r["duration"]) < 120 and r["drummer"] not in ("drummer5", "drummer9")]
    # AW2d: a performance that is in GMD's validation split (the hash of its GMD item name) is left out, so no validation groove is trained on in another sound
    rows = [r for r in rows if fnv("gmd_" + r["id"].replace("/", "_")) % 8 != 1]
    rows.sort(key=lambda r: fnv(r["id"] + "e"))
    by_kit = collections.defaultdict(list)
    for r in rows:
        by_kit[r["kit_name"]].append(r)
    kits = sorted(by_kit)
    print(f"egmd: {len(rows)} usable recordings, {len(kits)} kits", flush=True)
    pick = []
    for i in range(limit):
        pool = by_kit[kits[i % len(kits)]]
        if pool:
            pick.append(pool.pop(0))
    n = 0
    for r in pick:
        x, rate = sf.read(io.BytesIO(z.read(prefix + r["audio_filename"])))
        x = to_mono22k(x, rate)
        bpm = float(r["bpm"])
        beats = [k * 60 / bpm for k in range(int(len(x) / RATE * bpm / 60) + 1)]
        name = "egmd_" + re.sub(r"[^A-Za-z0-9_.-]", "_", r["audio_filename"].rsplit(".", 1)[0])      # the id is the performance (shared by the kits): the audio file is per kit
        save_item(out, name, x, beats, beats[::4], 4, "train", "egmd", lic, "E-GMD (Callender et al., Magenta), " + r["drummer"] + ", " + r["kit_name"], {"bpm": bpm})
        n += 1
    print("egmd:", n, "items over", len(kits), "kits", flush=True)


def guitarset(out, limit):
    """GuitarSet (Zenodo 3371780, CC BY 4.0): the mono microphone recordings, beats and downbeats from the JAMS beat_position annotations. Players 04 and 05 are the test."""
    meta, files = zenodo_files(3371780)
    lic = zenodo_licence(meta)
    assert licence_ok(lic), "GuitarSet licence " + str(lic)
    ann = zipfile.ZipFile(io.BytesIO(retry(lambda: __import__("requests").get(files["annotation.zip"]["links"]["self"], timeout=300).content, what="annotation.zip")))
    jams = {os.path.basename(n)[:-5]: ann.read(n) for n in ann.namelist() if n.endswith(".jams")}
    z = Zip(files["audio_mono-mic.zip"]["links"]["self"])
    n = collections.Counter()
    for name in sorted(z.names(), key=fnv):
        if not name.endswith("_mic.wav") or os.path.basename(name).startswith("._"):
            continue
        base = os.path.basename(name)[:-8]
        if base not in jams:
            continue
        player = base[:2]
        split = "test" if player in ("04", "05") else "train"
        if n[split] >= (limit if split == "train" else max(8, limit // 3)):
            continue
        j = json.loads(jams[base])
        bp = [a for a in j["annotations"] if a["namespace"] == "beat_position"]
        if not bp:
            continue
        data = bp[0]["data"]
        beats = [d["time"] for d in data]
        downs = [d["time"] for d in data if int(d["value"]["position"]) == 1]
        bpb = int(data[0]["value"]["num_beats"])
        x, rate = sf.read(io.BytesIO(z.read(name)))
        save_item(out, "gs_" + base, to_mono22k(x, rate), beats, downs, bpb, split, "guitarset", "CC-BY-4.0", "GuitarSet (Xi et al.), player " + player)
        n[split] += 1
    print("guitarset:", dict(n), flush=True)


def candombe(out, limit):
    """Uruguayan candombe drumming (Zenodo 6533068, CC BY 4.0): expert beats and downbeats (bar.beat labels). A quarter of the performances (by name hash) are the test."""
    meta, files = zenodo_files(6533068)
    lic = zenodo_licence(meta)
    assert licence_ok(lic), "candombe licence " + str(lic)
    import requests
    ann = zipfile.ZipFile(io.BytesIO(retry(lambda: requests.get(files["candombe_annotations.zip"]["links"]["self"], timeout=120).content, what="annotations")))
    csvs = {os.path.basename(n)[:-4]: ann.read(n).decode() for n in ann.namelist() if n.endswith(".csv") and "with_bar_number" in n}
    z = Zip(files["candombe_audio.zip"]["links"]["self"])
    n = collections.Counter()
    for name in sorted(z.names()):
        if not name.endswith(".flac"):
            continue
        base = os.path.basename(name)[:-5]
        if base not in csvs:
            continue
        beats, downs = [], []
        for line in csvs[base].splitlines():
            p = line.strip().split(",")
            if len(p) < 2 or not p[0]:
                continue
            t = float(p[0]); beats.append(t)
            if p[1].strip().endswith(".1"):
                downs.append(t)
        if len(beats) < 8:
            continue
        split = "test" if fnv(base) % 4 == 0 else "train"
        x, rate = sf.read(io.BytesIO(z.read(name)))
        save_item(out, "cd_" + base, to_mono22k(x, rate), beats, downs, 4, split, "candombe", "CC-BY-4.0", "Candombe beat and downbeat tracking dataset (Nunes et al.)")
        n[split] += 1
    print("candombe:", dict(n), flush=True)


def aam_stems(out, tid, beats, downs, bpb, mt):
    """Stem dropout (AW2): the mix again with some of the track's stems left out (no drums; bass and keys only; the sustained instruments only), same labels."""
    names = [x for x in mt.names() if x.startswith(tid + "_") and x.endswith(".flac")]
    stems = {}
    for nme in names:
        x, rate = sf.read(io.BytesIO(mt.read(nme)))
        stems[nme[5:-5]] = to_mono22k(x, rate)
    groups = {"nodrums": [k for k in stems if k != "Drums"],
              "bassandkeys": [k for k in stems if re.search(r"Bass|Piano|Organ|Keys", k)],
              "sustained": [k for k in stems if re.search(r"Violin|Viola|Cello|Erhu|Jinghu|MorinKhuur|Organ|Flute|Clarinet|Shakuhachi|Fujara|PanFlute|Trombone|Flugelhorn|Trumpet", k)]}
    for g, ks in groups.items():
        if not ks or (g != "nodrums" and len(ks) == len(stems)):
            continue
        n = max(len(stems[k]) for k in ks)
        mix = np.zeros(n, np.float32)
        for k in ks:
            mix[:len(stems[k])] += stems[k]
        save_item(out, f"aam_{tid}__{g}", mix, beats, downs, bpb, "train", "aam", "CC-BY-4.0", f"AAM track {tid}, stems {'+'.join(sorted(ks))}", {"stem_dropout": g})


def aam(out, limit, stems=0, part=0, offset=1, eval_only=False):
    """AAM, Artificial Audio Multitracks (Zenodo 5794629, CC BY 4.0): mixes of tracks 0001-1000 (every third one, up to LIMIT), beats and downbeats from the beatinfo files
    (bar count and quarter count). One in ten (by hash) is the test."""
    meta, files = zenodo_files(5794629)
    lic = zenodo_licence(meta)
    assert licence_ok(lic), "AAM licence " + str(lic)
    import requests
    rng = f"{part * 1000 + 1:04d}-{part * 1000 + 1000:04d}"          # AW2b: the three zips of 1000 tracks are parts 0, 1 and 2
    ann = zipfile.ZipFile(io.BytesIO(retry(lambda: requests.get(files[rng + "-annotations-v1.1.0.zip"]["links"]["self"], timeout=300).content, what="annotations")))
    z = Zip(files[rng + "-audio-mixes.zip"]["links"]["self"])
    multitracks = Zip(files[rng + "-audio-multitracks.zip"]["links"]["self"]) if stems else None
    n = collections.Counter()
    names = sorted(x for x in z.names() if x.endswith("_mix.flac"))
    names = [x for i, x in enumerate(names) if i % (3 if part == 0 and limit <= 100 and offset == 1 else 4) == offset]      # the pilot's pick for 100 tracks; AW2b's 250 per zip
    for name in names:
        tid = os.path.basename(name)[:4]
        split = "test" if fnv(tid) % 10 == 0 else "train"
        if n[split] >= (limit if split == "train" else max(8, limit // 8)):
            continue
        if eval_only and split == "train" and fnv("aam_" + tid) % 8 != 1:
            continue                                         # AW2c: only the test items and the validation ones (prep.py makes them from the hash)
        try:
            rows = [l.split(",") for l in ann.read(tid + "_beatinfo.arff").decode().splitlines() if l.strip() and l[0] not in "@%"]
        except KeyError:
            continue
        beats = [float(r[0]) for r in rows]
        downs = [float(r[0]) for r in rows if int(r[2]) == 1]
        bpb = max(int(r[2]) for r in rows)
        x, rate = sf.read(io.BytesIO(z.read(name)))
        save_item(out, "aam_" + tid, to_mono22k(x, rate), beats, downs, bpb, split, "aam", "CC-BY-4.0", "AAM: Artificial Audio Multitracks Dataset (Ostermann et al.), track " + tid)
        n[split] += 1
        if split == "train" and n["stems"] < stems:
            n["stems"] += 1
            aam_stems(out, tid, beats, downs, bpb, multitracks)
    print("aam:", dict(n), flush=True)


def fsl10k(out, limit):
    """Freesound Loop Dataset (Zenodo 3967852). Each loop has its own licence (metadata.json): only CC0 and CC BY are kept (NC and 'sampling+' are dropped, logged).
    The tempo is the experts' (annotations: defined tempo, well cut, one tempo given by every annotator who gave one); the loop must be a whole number of bars at it.
    The audio is the loop repeated to at least 30 s, so the downbeat is the loop start and the bars are the loop length's. One in ten (by hash) is the test.
    Writes attribution.tsv: id, author, name, licence, URL."""
    import requests
    meta, files = zenodo_files(3967852)
    z = Zip(files["FSL10K.zip"]["links"]["self"])
    md = json.loads(z.read("metadata.json"))
    ann = zipfile.ZipFile(io.BytesIO(retry(lambda: requests.get(files["annotations.zip"]["links"]["self"], timeout=300).content, what="annotations.zip")))
    by = collections.defaultdict(list)
    for nme in ann.namelist():
        m = re.search(r"sound-(\d+)\.json$", nme)
        if m:
            by[m.group(1)].append(json.loads(ann.read(nme)))
    audio = {}
    for nme in z.names():
        m = re.match(r"audio/wav/(\d+)_\d+\.wav\.wav$", nme)
        if m:
            audio[m.group(1)] = nme
    dropped = collections.Counter()
    os.makedirs(out, exist_ok=True)
    attr = open(os.path.join(out, "attribution.tsv"), "w")
    n = collections.Counter()
    for sid in sorted(by, key=fnv):
        if sid not in audio or sid not in md:
            dropped["no audio or metadata"] += 1
            continue
        lic = md[sid].get("license", "")
        if not licence_ok(lic):
            dropped["licence " + lic] += 1
            continue
        anns = [a for a in by[sid] if not a.get("discard") and a.get("defined_tempo") and a.get("well_cut") and str(a.get("bpm", "")).replace(".", "").isdigit()]
        sigs = {a.get("signature") for a in anns}
        bpms = [float(a["bpm"]) for a in anns]
        if not anns or sigs - {"4/4", "3/4"} or len(sigs) != 1 or max(bpms) - min(bpms) > 0.5:
            dropped["annotation"] += 1
            continue
        bpm = float(np.mean(bpms)); bpb = int(sigs.pop()[0])
        if z.size(audio[sid]) > 6_000_000:
            dropped["too big"] += 1
            continue
        split = "test" if fnv(sid) % 10 == 0 else "train"
        if n[split] >= (limit if split == "train" else max(8, limit // 8)):
            continue
        x, rate = sf.read(io.BytesIO(z.read(audio[sid])))
        x = to_mono22k(x, rate)
        sec = len(x) / RATE
        bars = round(sec * bpm / 60 / bpb)
        if bars < 1 or sec < 1.0:
            dropped["no whole bars"] += 1
            continue
        bpm2 = bars * bpb * 60 / sec
        if abs(bpm2 - bpm) / bpm > 0.03:
            dropped["not whole bars at the tempo"] += 1
            continue
        reps = max(1, int(np.ceil(30.0 / sec)))
        y = np.tile(x, reps)
        total = bars * bpb * reps
        beats = [k * 60 / bpm2 for k in range(total)]
        save_item(out, "fsl_" + sid, y, beats, beats[::bpb], bpb, split, "fsl10k", lic, f"Freesound loop {sid} by {md[sid].get('username')}", {"bpm": bpm2})
        attr.write(f"{sid}\t{md[sid].get('username')}\t{md[sid].get('name')}\t{lic}\thttps://freesound.org/s/{sid}/\n")
        n[split] += 1
    attr.close()
    print("fsl10k:", dict(n), "dropped:", dict(dropped), flush=True)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("dataset"); ap.add_argument("out"); ap.add_argument("--limit", type=int, default=100); ap.add_argument("--stems", type=int, default=0); ap.add_argument("--part", type=int, default=0); ap.add_argument("--offset", type=int, default=1); ap.add_argument("--eval-only", action="store_true")
    a = ap.parse_args()
    if a.dataset == "aam":
        aam(a.out, a.limit, a.stems, a.part, a.offset, a.eval_only)
    else:
        {"gmd": gmd, "egmd": egmd, "guitarset": guitarset, "candombe": candombe, "fsl10k": fsl10k}[a.dataset](a.out, a.limit)
