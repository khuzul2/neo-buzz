#!/usr/bin/env python3
"""Warp quality against an objective reference (neo-audio.md section 11, WP1).

Reads the float WAVs that `WarpQualityTests.WriteWp1Renders` writes (`<group>--<mode>.wav`: original, ideal, repitch, beats,
complex, pro) and prints, per group and mode:

  lsd     log-spectral distance to the reference in dB (lower is better): 2048-point Hann STFT, hop 512, 40 Hz to 12 kHz, each frame's
          power floored 60 dB under the reference frame's loudest bin, frames silent in the reference left out, one global lag (up to 60 ms) removed.
  smear   how much slower the mode's attacks are than the reference's: the mean over the reference's onsets of the 10-90 % rise
          time of a 3 ms energy envelope, mode over reference (1.0 is as sharp). pre = the energy 10-40 ms before each onset, in
          dB under the note's peak, minus the reference's (0 is no pre-echo beyond the reference's).
  clicks  short broadband spikes the reference does not have: 1 ms peaks of the signal above 1.5 kHz that stand 4 dB over the
          reference's highest such peak within 12 ms either side, and over -60 dBFS; spikes within 5 ms are one.

The reference is `ideal` (the generator at the target tempo and pitch) where there is one. Real excerpts have none: their reference is
the original played at the new tempo in time only, i.e. its spectrogram resampled in time (lsd), its onsets moved to their new times
(smear: the attack of a real note does not stretch, so the original's own rise time is the reference), its high-band peaks moved the
same way (clicks).
"""
import argparse
import glob
import json
import os
import sys

import numpy as np
import scipy.io.wavfile as wavfile
import scipy.signal as sg

NFFT, HOP = 2048, 512


def read(path):
    sr, x = wavfile.read(path)
    x = x.astype(np.float64)
    if x.dtype.kind == "i" or np.abs(x).max() > 4:
        x = x / 32768.0
    return sr, x if x.ndim == 1 else x[:, 0]


def stft_pow(x, sr):
    w = np.hanning(NFFT + 1)[:-1]
    n = 1 + max(0, (len(x) - NFFT) // HOP)
    idx = np.arange(NFFT)[None, :] + HOP * np.arange(n)[:, None]
    return np.abs(np.fft.rfft(x[idx] * w, axis=1)) ** 2


def envelope(x, sr, ms=3.0):
    n = max(1, int(sr * ms / 1000))
    w = np.hanning(2 * n + 1)
    w /= w.sum()
    return np.sqrt(np.maximum(np.convolve(x * x, w, mode="same"), 0))


def best_lag(ref, x, sr, max_ms=60):
    """The shift (samples) to apply to x so that it lines up with ref, by the cross-correlation of 5 ms energy envelopes."""
    a, b = envelope(ref, sr, 5), envelope(x, sr, 5)
    a = a - a.mean()
    b = b - b.mean()
    m = int(sr * max_ms / 1000)
    n = min(len(a), len(b))
    def corr(s):
        return np.dot(a[: n - s], b[s:n]) if s >= 0 else np.dot(a[-s:n], b[: n + s])
    step = max(1, int(sr * 0.0005))
    best, lag = -1e30, 0
    for s in range(-(m // step) * step, m + 1, step):
        c = corr(s)
        if c > best:
            best, lag = c, s
    for s in range(lag - step, lag + step + 1):
        c = corr(s)
        if c > best:
            best, lag = c, s
    return lag


def align(ref, x, sr):
    lag = best_lag(ref, x, sr)
    if lag > 0:
        x = x[lag:]
    elif lag < 0:
        ref = ref[-lag:]
    n = min(len(ref), len(x))
    return ref[:n], x[:n], lag


def lsd(ref_pow, x_pow, sr, lo=40, hi=12000, range_db=60):
    """Mean over the reference's live frames of the rms difference in dB over the bins lo..hi, each frame's power floored
    range_db under that frame's loudest bin of the reference (so a bin that is only skirts counts as little as it sounds)."""
    k = np.fft.rfftfreq(NFFT, 1 / sr)
    sel = (k >= lo) & (k <= hi)
    frame = ref_pow.sum(1)
    live = frame > frame.max() * 1e-7
    rp, xp = ref_pow[:, sel], x_pow[:, sel]
    floor = rp.max(axis=1, keepdims=True) * 10 ** (-range_db / 10)
    a = 10 * np.log10(np.maximum(rp, floor))
    b = 10 * np.log10(np.maximum(xp, floor))
    d = np.sqrt(np.mean((a - b) ** 2, axis=1))
    return float(d[live].mean())


def lsd_short(ref, x, sr, onsets, max_lag_ms=15, nfft=256, hop=128):
    """The waveform-shape distance: the same distance on 5.8 ms frames (shorter than a bass period), after each note's
    waveform is lined up with the reference's by its own best lag (so only the shape of the cycle counts, not where in the
    cycle it starts). Notes are the stretches between the reference's onsets."""
    edges = [0] + [int(o) for o in onsets] + [len(ref)]
    ml = int(max_lag_ms / 1000 * sr)
    ra, xa = [], []
    for a, b in zip(edges[:-1], edges[1:]):
        if b - a < 2 * ml + nfft * 2:
            continue
        r = ref[a:b]
        lo, hi = max(0, a - ml), min(len(x), b + ml)
        xs = x[lo:hi]
        # best lag by the cross-correlation of the two (FFT)
        n = 1 << int(np.ceil(np.log2(len(r) + len(xs))))
        c = np.fft.irfft(np.fft.rfft(xs, n) * np.conj(np.fft.rfft(r, n)), n)
        c = np.concatenate([c[-len(r) + 1:], c[:len(xs)]])      # lags -(len(r)-1) .. len(xs)-1
        lags = np.arange(-(len(r) - 1), len(xs))
        # lag l means xs[l:] lines up with r[0:]; the window around l = a - lo is zero shift
        z = a - lo
        sel = (lags >= z - ml) & (lags <= z + ml)
        best = lags[sel][int(np.argmax(c[sel]))]
        seg = x[lo + best: lo + best + len(r)]
        if len(seg) < len(r):
            seg = np.pad(seg, (0, len(r) - len(seg)))
        ra.append(r)
        xa.append(seg)
    if not ra:
        return float("nan")
    w = np.hanning(nfft + 1)[:-1]
    tot, cnt = 0.0, 0
    for r, z in zip(ra, xa):
        n = 1 + (len(r) - nfft) // hop
        idx = np.arange(nfft)[None, :] + hop * np.arange(n)[:, None]
        rp = np.abs(np.fft.rfft(r[idx] * w, axis=1)) ** 2
        zp = np.abs(np.fft.rfft(z[idx] * w, axis=1)) ** 2
        k = np.fft.rfftfreq(nfft, 1 / sr)
        sel = (k >= 40) & (k <= 12000)
        live = rp.sum(1) > np.abs(ref).max() ** 2 * nfft * 1e-9
        rp, zp = rp[:, sel], zp[:, sel]
        floor = rp.max(axis=1, keepdims=True) * 1e-6
        d = np.sqrt(np.mean((10 * np.log10(np.maximum(rp, floor)) - 10 * np.log10(np.maximum(zp, floor))) ** 2, axis=1))
        tot += d[live].sum()
        cnt += live.sum()
    return float(tot / max(1, cnt))


def onsets_of(x, sr):
    """Where sounds start: spectral-flux peaks of the 2048 STFT, in samples (for a reference without known times)."""
    p = np.log1p(1e3 * np.sqrt(stft_pow(x, sr)) / np.sqrt(NFFT))
    flux = np.maximum(np.diff(p, axis=0), 0).sum(1)
    flux = np.concatenate([[0], flux])
    thr = max(flux.mean() + 1.5 * flux.std(), flux.max() * 0.15)
    peaks, _ = sg.find_peaks(flux, height=thr, distance=int(0.08 * sr / HOP))
    out = []
    for pk in peaks:
        # refine on the energy envelope: the earliest point within a frame before the flux peak that is above 10 % of its local peak
        a = max(0, pk * HOP - NFFT // 2)
        b = min(len(x), pk * HOP + NFFT)
        seg = envelope(x[a:b], sr, 3)
        if seg.max() <= 0:
            continue
        j = int(np.argmax(seg > 0.1 * seg.max()))
        out.append(a + j)
    return out


def rise(env, sr, t0, win_ms=80):
    """The attack at t0: (10-90 % rise time in ms, pre-echo dB, peak). The rise runs from the quiet just before t0 (the lowest point
    from 15 ms before to 2 ms after) to the highest point within 80 ms after; the pre-echo is the loudest point 40-10 ms before,
    in dB under that peak (floored at -80)."""
    t0 = int(t0)
    if t0 < 0 or t0 + 10 >= len(env):
        return None
    b = min(len(env), t0 + int(win_ms / 1000 * sr))
    lo_i, hi_i = max(0, t0 - int(0.015 * sr)), min(len(env), t0 + int(0.002 * sr))
    base = env[lo_i:hi_i].min()
    start = max(0, t0 - int(0.005 * sr))
    pk_i = start + int(np.argmax(env[start:b]))
    pk = env[pk_i]
    if pk - base <= 1e-3:
        return None
    lo10, lo90 = base + 0.1 * (pk - base), base + 0.9 * (pk - base)
    seg = env[lo_i:pk_i + 1]
    below = np.where(seg < lo10)[0]
    s10 = (below[-1] if len(below) else 0)
    above = np.where(seg[s10:] >= lo90)[0]
    s90 = s10 + (above[0] if len(above) else len(seg) - 1 - s10)
    r = (s90 - s10) / sr * 1000
    pa, pb = max(0, t0 - int(0.04 * sr)), max(0, t0 - int(0.01 * sr))
    pre = env[pa:pb].max() if pb > pa else 0
    return r, max(20 * np.log10(max(pre, 1e-9) / pk), -80.0), pk


def attacks(er, ex, sr, onsets_ref, onsets_x):
    """Mean rise-time ratio (mode over reference) and mean pre-echo difference (dB) over the onsets."""
    ratios, pres = [], []
    for tr, tx in zip(onsets_ref, onsets_x):
        a, b = rise(er, sr, tr), rise(ex, sr, tx)
        if a is None or b is None:
            continue
        ratios.append(max(b[0], 0.05) / max(a[0], 0.05))
        pres.append(b[1] - a[1])
    if not ratios:
        return float("nan"), float("nan")
    return float(np.mean(ratios)), float(np.mean(pres))


def hf_peaks(x, sr, ms=1.0, cutoff=1500):
    sos = sg.butter(4, cutoff, "hp", fs=sr, output="sos")
    h = sg.sosfiltfilt(sos, x)
    n = max(1, int(sr * ms / 1000))
    m = len(h) // n
    return np.abs(h[: m * n]).reshape(m, n).max(1)


def clicks(ref, x, sr, ref_map=None, reach_ms=12, over_db=4.0, floor_db=-60.0, merge_ms=5.0):
    """The clicks of x that ref does not have, as a list of times (s). ref_map moves the reference's frame index to x's (real excerpts)."""
    pr, px = hf_peaks(ref, sr), hf_peaks(x, sr)
    if ref_map is not None:
        idx = np.clip(np.round(np.arange(len(px)) * ref_map).astype(int), 0, len(pr) - 1)
        pr = pr[idx]
        reach = int(round(reach_ms * max(1.0, ref_map)))
    else:
        n = min(len(pr), len(px))
        pr, px = pr[:n], px[:n]
        reach = reach_ms
    from scipy.ndimage import maximum_filter1d
    cover = maximum_filter1d(pr, size=2 * reach + 1)
    floor = 10 ** (floor_db / 20)
    hit = np.where((px > over_db_factor(over_db) * cover) & (px > floor))[0]
    out = []
    for i in hit:
        if not out or i - out[-1] > merge_ms:
            out.append(i)
    return [float(i) / 1000 for i in out]


def over_db_factor(db):
    return 10 ** (db / 20)


def time_warped_pow(orig_pow, ratio, n_out):
    """Frames of the original's spectrogram at the times the new tempo plays them (linear interpolation of the power)."""
    src = np.arange(n_out) * ratio
    i0 = np.clip(np.floor(src).astype(int), 0, len(orig_pow) - 1)
    i1 = np.clip(i0 + 1, 0, len(orig_pow) - 1)
    f = (src - np.floor(src))[:, None]
    return orig_pow[i0] * (1 - f) + orig_pow[i1] * f


def measure_group(files, skip_smear=False):
    """files: mode -> path. Returns mode -> dict of lsd, smear, pre, clicks, click_times, lag_ms."""
    sr, orig = read(files["original"])
    res = {}
    ideal = read(files["ideal"])[1] if "ideal" in files else None
    onsets = None
    for mode in ("ideal", "repitch", "beats", "complex-old", "complex", "pro-old", "pro"):
        if mode not in files:
            continue
        x = read(files[mode])[1]
        if ideal is not None:
            ref, y, lag = align(ideal, x, sr)
            d = lsd(stft_pow(ref, sr), stft_pow(y, sr), sr)
            if onsets is None:
                onsets = onsets_of(ideal, sr)
            sm = pre = float("nan")
            if not skip_smear:
                sm, pre = attacks(envelope(ref, sr, 4), envelope(y, sr, 4), sr, onsets, onsets)
            ck = clicks(ref, y, sr)
        else:
            # no ideal: the original, in time only
            ratio = len(orig) / max(1, len(x))
            xp = stft_pow(x, sr)
            d = lsd(time_warped_pow(stft_pow(orig, sr), ratio, len(xp)), xp, sr)
            if onsets is None:
                onsets = onsets_of(orig, sr)
            sm = pre = float("nan")
            if not skip_smear:
                # the reference's rise times are the original's own (attacks do not stretch)
                sm, pre = attacks(envelope(orig, sr, 4), envelope(x, sr, 4), sr, onsets, [int(o / ratio) for o in onsets])
            ck = clicks(orig, x, sr, ref_map=ratio)
            lag = 0
        res[mode] = {"lsd": round(d, 2), "smear": round(sm, 2), "pre": round(pre, 1), "clicks": len(ck),
                     "click_times": [round(c, 3) for c in ck[:12]], "lag_ms": round(1000 * lag / sr, 1)}
    return res


def collect(directory):
    groups = {}
    for p in sorted(glob.glob(os.path.join(directory, "*--*.wav"))):
        g, m = os.path.basename(p)[:-4].split("--")
        groups.setdefault(g, {})[m] = p
    return groups


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dir", help="folder of <group>--<mode>.wav")
    ap.add_argument("--json", help="write the numbers here")
    ap.add_argument("--groups", help="comma list of group prefixes to measure")
    a = ap.parse_args()
    groups = collect(a.dir)
    if a.groups:
        want = a.groups.split(",")
        groups = {g: f for g, f in groups.items() if any(g.startswith(w) for w in want)}
    out = {}
    print(f"{'group':26s} {'mode':8s} {'lsd':>6s} {'smear':>6s} {'pre dB':>7s} {'clicks':>6s}  lag ms")
    for g, files in groups.items():
        r = measure_group(files, skip_smear='pad' in g)
        out[g] = r
        for mode, v in r.items():
            print(f"{g:26s} {mode:8s} {v['lsd']:6.2f} {v['smear']:6.2f} {v['pre']:7.1f} {v['clicks']:6d}  {v['lag_ms']}")
    if a.json:
        json.dump(out, open(a.json, "w"), indent=1)


if __name__ == "__main__":
    sys.exit(main())
