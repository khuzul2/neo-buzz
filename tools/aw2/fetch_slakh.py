#!/usr/bin/env python3
"""Streams Slakh2100 (Zenodo 4599666, slakh2100_flac_redux.tar.gz, 104 GB, CC BY 4.0) on the runner without ever holding the archive (docs/specs/neo-audio.md, AW2 as built).

The tar's members are in a shuffled order (a track's mix, stems and MIDI are far apart), so nothing can be done per track while streaming. What is kept, per member as it passes:
  mix.flac      -> OUT/mix/SPLIT_TRACK.wav (mono, 22.05 kHz)        (the mix is all the features need)
  all_src.mid, metadata.yaml -> OUT/meta/SPLIT_TRACK.{mid,yaml}      (tiny; the labels come from the MIDI)
  stems/S*.flac -> OUT/stems/SPLIT_TRACK_S*.flac, only for about --stem-share of the train tracks (stem dropout)
The omitted/ tracks are skipped. After the stream, `--build-items` writes NAME.wav/NAME.json (fetch.py's format) for every track that has its mix and MIDI, 4/4 throughout.

The download is one HTTP stream that resumes with Range requests: the file object below reconnects at the exact byte it stopped at and goes on feeding the same
gzip and tar decoder, so a dropped connection loses nothing. `--force-reconnect-at BYTES` drops the connection on purpose once (the test of that).

    fetch_slakh.py OUT [--max-bytes N] [--force-reconnect-at N] [--stem-share 0.05] [--build-items DIR]
"""
import argparse, collections, io, json, os, sys, tarfile, time
import numpy as np
import requests
import soundfile as sf
from common import *

RECORD = 4599666
NAME = "slakh2100_flac_redux.tar.gz"


class ResumableStream(io.RawIOBase):
    def __init__(self, url, size, force_at=None, max_bytes=None, max_seconds=0):
        self.url, self.size, self.pos = url, size, 0
        self.deadline = time.time() + max_seconds if max_seconds else 0
        self.resp, self.it, self.buf = None, None, b""
        self.reconnects, self.force_at, self.max_bytes = 0, force_at, max_bytes
        self.forced = False

    def readable(self):
        return True

    def cut(self):
        """A deliberate end of the stream: the byte limit or the time limit (the rest of the archive is not wanted)."""
        return bool((self.max_bytes and self.pos >= self.max_bytes) or (self.deadline and time.time() > self.deadline))

    def _open(self):
        if self.resp is not None:
            self.resp.close()
        self.resp = requests.get(self.url, headers={"Range": f"bytes={self.pos}-"}, stream=True, timeout=120)
        self.resp.raise_for_status()
        if self.pos > 0 and self.resp.status_code != 206:
            raise IOError("the server did not honour the Range request")
        self.it = self.resp.iter_content(1 << 20)

    def read(self, n=-1):
        if n is None or n < 0:
            n = 1 << 20
        while len(self.buf) < n:
            if self.pos >= self.size or self.cut():
                break
            if self.force_at and not self.forced and self.pos >= self.force_at:
                self.forced = True
                print(f"forcing a reconnect at byte {self.pos}", flush=True)
                self.resp.close(); self.it = None
            try:
                if self.it is None:
                    self._open()
                    if self.reconnects or self.forced:
                        pass
                chunk = next(self.it, b"")
                if not chunk:
                    if self.pos < self.size:
                        raise IOError("the stream ended early")
                    break
                self.buf += chunk
                self.pos += len(chunk)
            except Exception as e:  # noqa: BLE001
                self.reconnects += 1
                print(f"stream: {type(e).__name__} {e} at byte {self.pos}; resuming ({self.reconnects})", flush=True)
                if self.reconnects > 200:
                    raise
                self.it = None
                time.sleep(min(30, 2 * self.reconnects))
        out, self.buf = self.buf[:n], self.buf[n:]
        return out


def stream(out, max_bytes, force_at, stem_share, max_seconds=0):
    meta, files = zenodo_files(RECORD)
    lic = zenodo_licence(meta)
    assert licence_ok(lic), "Slakh2100 licence " + str(lic)
    f = files[NAME]
    print("Slakh2100 licence:", lic, f["size"], "bytes", flush=True)
    for d in ("mix", "meta", "stems"):
        os.makedirs(os.path.join(out, d), exist_ok=True)
    rs = ResumableStream(f["links"]["self"], f["size"], force_at, max_bytes, max_seconds)
    count = collections.Counter()
    try:
        with tarfile.open(fileobj=rs, mode="r|gz") as tar:
            for m in tar:
                parts = m.name.split("/")
                if not m.isfile() or len(parts) < 4 or parts[1] == "omitted":
                    continue
                split, track, leaf = parts[1], parts[2], parts[-1]
                tag = f"{split}_{track}"
                if leaf == "mix.flac":
                    x, rate = sf.read(io.BytesIO(tar.extractfile(m).read()))
                    write_wav(os.path.join(out, "mix", tag + ".wav"), to_mono22k(x, rate)); count["mix"] += 1
                elif leaf == "all_src.mid":
                    open(os.path.join(out, "meta", tag + ".mid"), "wb").write(tar.extractfile(m).read()); count["mid"] += 1
                elif leaf == "metadata.yaml":
                    open(os.path.join(out, "meta", tag + ".yaml"), "wb").write(tar.extractfile(m).read()); count["yaml"] += 1
                elif parts[-2] == "stems" and leaf.endswith(".flac") and split == "train" and (fnv(track) % 1000) < stem_share * 1000:
                    open(os.path.join(out, "stems", f"{tag}_{leaf}"), "wb").write(tar.extractfile(m).read()); count["stem"] += 1
    except (tarfile.ReadError, EOFError) as e:
        if not rs.cut():         # a deliberate cut-off (bytes or seconds) ends the stream mid-member
            raise
        print("cut off at the byte limit:", type(e).__name__, flush=True)
    print(json.dumps({"bytes": rs.pos, "reconnects": rs.reconnects, "forced": rs.forced, "members": dict(count)}), flush=True)
    return rs, count


def build_items(out, dest):
    """NAME.wav and NAME.json for every track with its mix and MIDI, in 4/4 throughout; test split = Slakh's test, everything else trains."""
    import pretty_midi
    os.makedirs(dest, exist_ok=True)
    n = collections.Counter()
    for mix in sorted(os.listdir(os.path.join(out, "mix"))):
        tag = mix[:-4]
        mid = os.path.join(out, "meta", tag + ".mid")
        if not os.path.exists(mid):
            continue
        try:
            pm = pretty_midi.PrettyMIDI(mid)
            sigs = {(t.numerator, t.denominator) for t in pm.time_signature_changes} or {(4, 4)}
            if sigs != {(4, 4)}:
                n["not 4/4"] += 1
                continue
            x, _ = sf.read(os.path.join(out, "mix", mix), dtype="float32")
            sec = len(x) / RATE
            beats = [float(b) for b in pm.get_beats() if b < sec]
            downs = [float(b) for b in pm.get_downbeats() if b < sec]
            if len(beats) < 40:
                n["short"] += 1
                continue
        except Exception as e:  # noqa: BLE001
            n["error " + type(e).__name__] += 1
            continue
        split = "test" if tag.startswith("test_") else "val" if tag.startswith("validation_") else "train"
        save_item(dest, "slakh_" + tag, x, beats, downs, 4, split, "slakh", "CC-BY-4.0", "Slakh2100 (Manilow et al.), " + tag)
        n[split] += 1
    print("slakh items:", dict(n), flush=True)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("out"); ap.add_argument("--max-bytes", type=int, default=0); ap.add_argument("--force-reconnect-at", type=int, default=0)
    ap.add_argument("--max-seconds", type=int, default=0); ap.add_argument("--stem-share", type=float, default=0.05); ap.add_argument("--build-items")
    a = ap.parse_args()
    if not a.build_items or a.max_bytes or a.force_reconnect_at or a.max_seconds:
        rs, count = stream(a.out, a.max_bytes, a.force_reconnect_at, a.stem_share, a.max_seconds)
        if a.max_bytes:
            assert rs.forced or not a.force_reconnect_at, "the forced reconnect did not happen"
            assert not a.force_reconnect_at or rs.reconnects >= 1 or rs.forced
    if a.build_items:
        build_items(a.out, a.build_items)
