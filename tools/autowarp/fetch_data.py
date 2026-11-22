#!/usr/bin/env python3
"""Fetches the evaluation data of auto-warp (docs/specs/neo-audio.md, AW1 as built) onto the runner, by HTTP range requests into the published archives
(nothing is committed or uploaded; the audio stays on the runner):

  gmd     Groove MIDI Dataset v1.0.0 (Magenta, CC BY 4.0): recordings of a drummer playing to a click on an electronic kit, 4/4, with their MIDI.
          The ground truth: beats are the click's, from the MIDI's tempo (bar 1 is the MIDI's first beat). Writes NAME.wav and NAME.json.
  slakh   BabySlakh (Zenodo 4603870, CC BY 4.0), the 20 pieces of Slakh2100 published as a small download: mixes rendered from MIDI, with the beats and downbeats
          of that MIDI's tempo and time signature maps (only the pieces that are in 4/4 throughout).
          Writes NAME.wav (the mix, mono 16 kHz, at most --slakh-seconds long) and NAME.json.

    fetch_data.py --gmd DIR [--gmd-per-drummer 3] [--gmd-split test,validation,train] --slakh DIR [--slakh-count 24] [--slakh-seconds 240]
The JSON of each: {"beats": [seconds], "downbeats": [seconds], "beats_per_bar": 4, "bpm": ...}.
"""
import argparse, collections, csv, io, json, os, random, tarfile, time, wave
import numpy as np


class Zip:
    """A RemoteZip that survives a broken connection: each read is retried (up to 6 times, the zip reopened) before it fails."""

    def __init__(self, url):
        self.url, self.z = url, None

    def _zip(self):
        from remotezip import RemoteZip
        if self.z is None:
            self.z = RemoteZip(self.url)
        return self.z

    def read(self, name):
        for attempt in range(6):
            try:
                return self._zip().read(name)
            except Exception as e:
                if attempt == 5:
                    raise
                print(f"read {name}: {type(e).__name__}, retrying ({attempt + 1})", flush=True)
                self.z = None
                time.sleep(5 * (attempt + 1))

    def open(self, name):
        return io.BytesIO(self.read(name))


def write_wav(path, x, rate):
    x = np.clip(np.asarray(x, float), -1, 1)
    with wave.open(path, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate)
        w.writeframes((x * 32767).astype("<i2").tobytes())


def gmd(dest, per, splits):
    random.seed(11)
    z = Zip("https://storage.googleapis.com/magentadata/datasets/groove/groove-v1.0.0.zip")
    rows = list(csv.DictReader(io.TextIOWrapper(z.open("groove/info.csv"), "utf-8")))
    rows = [r for r in rows if r["audio_filename"] and r["time_signature"] == "4-4" and r["beat_type"] == "beat" and 15 < float(r["duration"]) < 60 and r["split"] in splits.split(",")]
    by = collections.defaultdict(list)
    for r in rows:
        by[r["drummer"]].append(r)
    os.makedirs(dest, exist_ok=True)
    n = 0
    for drummer, items in sorted(by.items()):
        random.shuffle(items)
        for r in items[:per]:
            name = r["audio_filename"].replace("/", "_")[:-4]
            raw = z.read("groove/" + r["audio_filename"])
            with wave.open(io.BytesIO(raw)) as w:
                rate, ch, width = w.getframerate(), w.getnchannels(), w.getsampwidth()
                frames = w.readframes(w.getnframes())
            dt = {2: "<i2", 3: None, 4: "<i4"}[width]
            if dt is None:      # 24 bit
                b = np.frombuffer(frames, np.uint8).reshape(-1, 3).astype(np.int32)
                v = (b[:, 0] | (b[:, 1] << 8) | (b[:, 2] << 16)); v = np.where(v >= 1 << 23, v - (1 << 24), v) / 8388608.0
            else:
                v = np.frombuffer(frames, dt) / float(1 << (8 * width - 1))
            v = v.reshape(-1, ch).mean(axis=1)
            write_wav(os.path.join(dest, name + ".wav"), v, rate)
            bpm = float(r["bpm"]); sec = len(v) / rate
            beats = [round(k * 60 / bpm, 4) for k in range(int(sec * bpm / 60) + 1)]
            with open(os.path.join(dest, name + ".json"), "w") as f:
                json.dump({"beats": beats, "downbeats": beats[::4], "beats_per_bar": 4, "bpm": bpm, "style": r["style"], "drummer": drummer}, f)
            n += 1
    print("gmd:", n, "recordings", flush=True)


def slakh(dest, count, max_seconds, wanted_splits=("test",)):
    """BabySlakh (Zenodo 4603870, CC BY 4.0): the 20 pieces of Slakh2100 that its authors published as a small download (880 MB, 16 kHz mixes with their MIDI).
    (The full Slakh2100 is one 100 GB tarball with no index, and a stream of it did not survive on the runner.) The file is fetched with resumed ranges and
    checked against Zenodo's md5; each piece in 4/4 throughout is kept: its mix (at most max_seconds) and the beats and downbeats of its MIDI."""
    import hashlib, requests, soundfile, pretty_midi
    meta = requests.get("https://zenodo.org/api/records/4603870", timeout=60).json()
    f = [x for x in meta["files"] if x["key"].startswith("babyslakh_16k")][0]
    url, size, md5 = f["links"]["self"], f["size"], f["checksum"].split(":")[1]
    print("slakh: BabySlakh", f["key"], size, "bytes; licence", meta["metadata"].get("license"), flush=True)
    os.makedirs(dest, exist_ok=True)
    archive = os.path.join(dest, "babyslakh.tar.gz")
    have = os.path.getsize(archive) if os.path.exists(archive) else 0
    for attempt in range(30):
        if have >= size:
            break
        try:
            with requests.get(url, headers={"Range": f"bytes={have}-"}, stream=True, timeout=120) as r:
                r.raise_for_status()
                with open(archive, "ab") as out:
                    for chunk in r.iter_content(1 << 20):
                        out.write(chunk)
                        have += len(chunk)
        except Exception as e:
            print(f"slakh: download {type(e).__name__} at {have}, resuming ({attempt + 1})", flush=True)
            time.sleep(5 * (attempt + 1))
            have = os.path.getsize(archive) if os.path.exists(archive) else 0
    h = hashlib.md5()
    with open(archive, "rb") as fh:
        for block in iter(lambda: fh.read(1 << 22), b""):
            h.update(block)
    if h.hexdigest() != md5:
        raise SystemExit(f"slakh: checksum {h.hexdigest()} is not {md5}")
    pending = collections.defaultdict(dict)       # track -> {mix: bytes, midi: bytes}
    done = 0
    with tarfile.open(archive, mode="r:gz") as tar:
        for m in tar:
            parts = m.name.split("/")
            if not m.isfile() or len(parts) != 3 or parts[2] not in ("mix.wav", "mix.flac", "all_src.mid"):
                continue
            track = parts[1]
            pending[track]["mix" if parts[2].startswith("mix") else "midi"] = tar.extractfile(m).read()
            if "mix" not in pending[track] or "midi" not in pending[track]:
                continue
            d = pending.pop(track)
            try:
                pm = pretty_midi.PrettyMIDI(io.BytesIO(d["midi"]))
                sigs = {(t.numerator, t.denominator) for t in pm.time_signature_changes} or {(4, 4)}
                if sigs != {(4, 4)}:
                    print("slakh: skipped", track, "not 4/4", flush=True)
                    continue
                x, rate = soundfile.read(io.BytesIO(d["mix"]))
                if x.ndim > 1:
                    x = x.mean(axis=1)
                x = x[: int(max_seconds * rate)]
                sec = len(x) / rate
                beats = [float(b) for b in pm.get_beats() if b < sec]
                downs = [float(b) for b in pm.get_downbeats() if b < sec]
                if len(beats) < 40:
                    continue
                write_wav(os.path.join(dest, track + ".wav"), x, rate)
                with open(os.path.join(dest, track + ".json"), "w") as fh:
                    json.dump({"beats": [round(b, 4) for b in beats], "downbeats": [round(b, 4) for b in downs], "beats_per_bar": 4}, fh)
                done += 1
                print("slakh:", track, f"{sec:.0f} s", len(beats), "beats", flush=True)
            except Exception as e:
                print("slakh: skipped", track, type(e).__name__, e, flush=True)
            if done >= count:
                break
    os.remove(archive)
    print("slakh:", done, "pieces", flush=True)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--gmd"); ap.add_argument("--gmd-per-drummer", type=int, default=3); ap.add_argument("--gmd-split", default="test,validation,train")
    ap.add_argument("--slakh"); ap.add_argument("--slakh-count", type=int, default=24); ap.add_argument("--slakh-seconds", type=float, default=240)
    ap.add_argument("--slakh-split", default="test")
    a = ap.parse_args()
    if a.gmd: gmd(a.gmd, a.gmd_per_drummer, a.gmd_split)
    if a.slakh: slakh(a.slakh, a.slakh_count, a.slakh_seconds, tuple(a.slakh_split.split(",")))
