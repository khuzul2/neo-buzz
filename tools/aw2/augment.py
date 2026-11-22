"""AW2 augmentation (docs/specs/neo-audio.md, AW2 as built): the audio is warped along a random tempo curve with Signalsmith Stretch (the library of the app's Pro warp,
through aw2warp.cpp), transposed, and its labels are mapped through the same curve from the map the tool writes. The curve kinds: slow drift (up to +-8 %), tempo
steps, ritardando, accelerando, and none; the overall tempo is moved to a random tempo of 55 to 215 BPM (at most one octave from the original).

    augment.py --selftest WARP_EXE        warps a click train and checks the mapped labels against where the clicks land (the exactness of the label mapping)
"""
import math, os, subprocess, sys, tempfile
import numpy as np
from common import RATE


def base_bpm(beats):
    b = np.asarray(beats, float)
    if len(b) < 3:
        return 0.0
    return 60.0 / float(np.median(np.diff(b)))


def make_curve(rng, seconds, bpm0, kind=None, lo=55.0, hi=215.0):
    """Returns (points [(t_in, r)], kind, target bpm). r is output seconds per input second."""
    kind = kind or rng.choice(["none", "drift", "steps", "ritard", "accel", "drift", "steps"])
    t_lo, t_hi = max(lo, bpm0 * 0.5), min(hi, bpm0 * 2.0)
    target = math.exp(rng.uniform(math.log(t_lo), math.log(t_hi))) if t_hi > t_lo else bpm0
    ts = np.arange(0, seconds + 1.0, 0.5)
    tau = np.ones_like(ts)                                  # tempo multiplier over input time
    if kind == "drift":
        a, per, ph = rng.uniform(0.02, 0.08), rng.uniform(15, 60), rng.uniform(0, 2 * math.pi)
        tau = 1 + a * np.sin(2 * math.pi * ts / per + ph)
    elif kind == "steps":
        for _ in range(rng.integers(2, 5)):
            at, m = rng.uniform(0.15, 0.9) * seconds, math.exp(rng.uniform(math.log(0.85), math.log(1.2)))
            tau *= np.where(ts >= at, 1.0, 1.0) * (1 + (m - 1) * np.clip((ts - at) / 0.3, 0, 1))
    elif kind in ("ritard", "accel"):
        start = rng.uniform(0.2, 0.6) * seconds
        end = start + rng.uniform(0.2, 0.4) * seconds
        final = rng.uniform(0.7, 0.85) if kind == "ritard" else rng.uniform(1.15, 1.3)
        tau = 1 + (final - 1) * np.clip((ts - start) / max(1e-6, end - start), 0, 1)
    r = bpm0 / (target * tau)
    r = np.clip(r, 0.4, 2.5)
    return list(zip(ts.tolist(), r.tolist())), kind, target


def warp(exe, x, curve, semitones, workdir):
    """x: float32 mono at 22.05 kHz. Returns (y, map_in, map_out)."""
    fi, fo, fc, fm = (os.path.join(workdir, n) for n in ("in.f32", "out.f32", "curve.txt", "map.txt"))
    np.asarray(x, "<f4").tofile(fi)
    with open(fc, "w") as f:
        for t, r in curve:
            f.write(f"{t:.4f} {r:.6f}\n")
    subprocess.run([exe, fi, fo, fc, fm, str(RATE), str(semitones)], check=True)
    y = np.fromfile(fo, "<f4")
    m = np.loadtxt(fm)
    for p in (fi, fo, fc, fm):
        os.remove(p)
    return y, m[:, 0], m[:, 1]


def map_labels(times, map_in, map_out, out_seconds):
    t = np.interp(np.asarray(times, float), map_in, map_out)
    return t[(t >= 0.0) & (t < out_seconds - 0.02)]


def selftest(exe):
    rng = np.random.default_rng(1)
    seconds = 40
    clicks = np.arange(1.0, seconds - 1, 0.5)
    x = np.zeros(int(seconds * RATE), np.float32)
    for c in clicks:
        i = int(c * RATE)
        x[i:i + 200] = np.hanning(400)[:200][::-1] * 0 + np.sin(2 * math.pi * 1500 * np.arange(200) / RATE) * np.exp(-np.arange(200) / 40.0)
    worst = 0
    for kind in ("none", "drift", "steps", "ritard", "accel"):
        curve, k, target = make_curve(rng, seconds, 120.0, kind)
        with tempfile.TemporaryDirectory() as d:
            y, mi, mo = warp(exe, x, curve, 0, d)
        pred = map_labels(clicks, mi, mo, len(y) / RATE)
        env = np.abs(y)
        found = []
        for p in pred:
            a, b = int((p - 0.05) * RATE), int((p + 0.05) * RATE)
            if a < 0 or b > len(y):
                continue
            seg = env[a:b]
            thr = 0.3 * seg.max()
            if seg.max() < 1e-3:
                continue
            found.append((np.argmax(seg > thr) + a) / RATE - p)
        err = np.array(found)
        print(f"{kind:7s} target {target:6.1f} bpm: {len(err)} clicks, median offset {np.median(err) * 1000:6.1f} ms, p95 |offset| {np.percentile(np.abs(err), 95) * 1000:6.1f} ms, out {len(y) / RATE:5.1f} s")
        worst = max(worst, float(np.percentile(np.abs(err - np.median(err)), 95)))
    print("worst spread p95 (ms):", worst * 1000)


if __name__ == "__main__":
    if len(sys.argv) > 2 and sys.argv[1] == "--selftest":
        selftest(sys.argv[2])
