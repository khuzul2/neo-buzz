#!/usr/bin/env python3
"""Fetches GuitarSet (Zenodo 3371780, CC BY 4.0) for Neo Audio to MIDI's evaluation (docs/specs/audio-to-midi.md section 8) onto the runner:
the JAMS annotations (39 MB, whole, md5 checked) and a spread of the hexaphonic pickup's debleeded recordings (one mono line per string), read
with HTTP range requests into the 3.6 GB zip (nothing is downloaded whole, nothing is committed or uploaded).

    fetch_guitarset.py DIR [--every 3] [--offset 1]

Writes DIR/annotation/*.jams and DIR/audio/*_hex_cln.wav: the recordings whose index in the sorted list of the zip's files is OFFSET modulo EVERY
(so --every 30 --offset 8 is the 12 recordings the tuning used, and the default is a different set: the tuning files are not the test).
"""
import argparse, hashlib, json, os, sys, time, urllib.request, zipfile

REC = "https://zenodo.org/api/records/3371780"


def get(url, tries=8):
    for t in range(tries):
        try:
            with urllib.request.urlopen(url, timeout=120) as r:
                return r.read()
        except Exception as e:  # noqa: BLE001
            print("retry", url, e, file=sys.stderr)
            time.sleep(5 * (t + 1))
    raise SystemExit("cannot read " + url)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dir")
    ap.add_argument("--every", type=int, default=3)
    ap.add_argument("--offset", type=int, default=1)
    a = ap.parse_args()
    from remotezip import RemoteZip
    meta = json.loads(get(REC))
    files = {f["key"]: f for f in meta["files"]}
    print("licence:", meta["metadata"]["license"])
    ann = os.path.join(a.dir, "annotation"); aud = os.path.join(a.dir, "audio")
    os.makedirs(ann, exist_ok=True); os.makedirs(aud, exist_ok=True)
    f = files["annotation.zip"]
    data = get(f["links"]["self"])
    assert hashlib.md5(data).hexdigest() == f["checksum"].split(":")[1], "annotation.zip md5"
    zp = os.path.join(a.dir, "annotation.zip")
    open(zp, "wb").write(data)
    with zipfile.ZipFile(zp) as z:
        for n in z.namelist():
            if n.endswith(".jams"):
                open(os.path.join(ann, os.path.basename(n)), "wb").write(z.read(n))
    os.remove(zp)
    url = files["audio_hex-pickup_debleeded.zip"]["links"]["self"]
    z = RemoteZip(url)
    names = sorted(n for n in z.namelist() if n.endswith("_hex_cln.wav"))
    pick = [n for i, n in enumerate(names) if i % a.every == a.offset]
    print(len(names), "recordings in the zip,", len(pick), "taken")
    for n in pick:
        for t in range(5):
            try:
                open(os.path.join(aud, os.path.basename(n)), "wb").write(z.read(n))
                break
            except Exception as e:  # noqa: BLE001
                print("retry", n, e, file=sys.stderr)
                time.sleep(5 * (t + 1))
        else:
            raise SystemExit("cannot read " + n)
    print("done:", len(os.listdir(aud)), "files")


if __name__ == "__main__":
    main()
