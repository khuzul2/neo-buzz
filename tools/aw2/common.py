"""Shared helpers of the AW2 data tools (docs/specs/neo-audio.md, AW2 as built). Nothing here is kept or committed: audio and labels live on the runner."""
import io, json, os, time, wave
import numpy as np

RATE = 22050
ALLOWED_LICENCES = ("creativecommons.org/licenses/by/", "creativecommons.org/publicdomain/zero/", "cc-by-4.0", "cc0-1.0", "cc-by", "cc0")
FORBIDDEN_MARKERS = ("-nc", "-nd", "-sa", "sampling")


def licence_ok(text):
    """CC BY (any version), CC0 and public domain only; NC, ND, SA, sampling+ and anything unknown are dropped."""
    t = (text or "").lower().strip()
    if not t:
        return False
    if any(m in t for m in FORBIDDEN_MARKERS):
        return False
    return any(a in t for a in ALLOWED_LICENCES)


def fnv(name):
    h = 2166136261
    for c in name:
        h ^= ord(c)
        h = (h * 16777619) & 0xFFFFFFFF
    return h


def to_mono22k(x, rate):
    """Mono (mean of the channels) at 22.05 kHz, float32."""
    from scipy.signal import resample_poly
    x = np.asarray(x, dtype=np.float64)
    if x.ndim > 1:
        x = x.mean(axis=1)
    if rate != RATE:
        from math import gcd
        g = gcd(RATE, int(rate))
        x = resample_poly(x, RATE // g, int(rate) // g)
    return x.astype(np.float32)


def write_wav(path, x, rate=RATE):
    x = np.clip(np.asarray(x, float), -1, 1)
    with wave.open(path, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate)
        w.writeframes((x * 32767).astype("<i2").tobytes())


def save_item(out, name, x, beats, downbeats, bpb, split, dataset, licence, source, extra=None):
    """One item: NAME.wav (mono 22.05 kHz) and NAME.json. Returns the duration in seconds. The licence is logged to licences.tsv."""
    os.makedirs(out, exist_ok=True)
    write_wav(os.path.join(out, name + ".wav"), x)
    d = {"beats": [round(float(b), 4) for b in beats], "downbeats": [round(float(b), 4) for b in downbeats], "beats_per_bar": int(bpb),
         "split": split, "dataset": dataset, "licence": licence, "source": source}
    if extra:
        d.update(extra)
    json.dump(d, open(os.path.join(out, name + ".json"), "w"))
    with open(os.path.join(out, "licences.tsv"), "a") as f:
        f.write(f"{name}\t{dataset}\t{licence}\t{source}\n")
    return len(x) / RATE


def retry(fn, tries=6, what=""):
    for a in range(tries):
        try:
            return fn()
        except Exception as e:  # noqa: BLE001
            if a == tries - 1:
                raise
            print(f"retry {what}: {type(e).__name__} {e}", flush=True)
            time.sleep(4 * (a + 1))


class Zip:
    """A RemoteZip that survives a broken connection: each read is retried (the zip reopened)."""
    def __init__(self, url):
        self.url, self.z = url, None

    def _zip(self):
        from remotezip import RemoteZip
        if self.z is None:
            self.z = RemoteZip(self.url)
        return self.z

    def read(self, name):
        def go():
            try:
                return self._zip().read(name)
            except Exception:
                self.z = None
                raise
        return retry(go, what=name)

    def names(self):
        return retry(lambda: self._zip().namelist(), what="namelist")

    def size(self, name):
        return self._zip().getinfo(name).file_size


def zenodo_files(record):
    import requests
    meta = retry(lambda: requests.get(f"https://zenodo.org/api/records/{record}", timeout=60).json(), what="zenodo")
    return meta, {f["key"]: f for f in meta["files"]}


def zenodo_licence(meta):
    lic = meta["metadata"].get("license")
    lic = lic.get("id") if isinstance(lic, dict) else lic
    return lic
