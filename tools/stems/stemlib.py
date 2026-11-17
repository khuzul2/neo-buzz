#!/usr/bin/env python3
# NeoBuzz Live stem models (docs/specs/stem-separation.md): what the validation and the quality check share.
#   - the same chunking as the C# OverlapAdd (and Demucs's apply_model): a segment every (1 - overlap), a triangle weight
#   - SDR as the MUSDB evaluation defines it (energy of the reference over energy of the error, dB)
#   - reading and writing raw float32 (the C# test reads the same files)
import json
import os
import numpy as np


def sdr(reference, estimate):
    reference = np.asarray(reference, dtype=np.float64)
    estimate = np.asarray(estimate, dtype=np.float64)
    num = np.sum(reference ** 2) + 1e-12
    den = np.sum((reference - estimate) ** 2) + 1e-12
    return float(10 * np.log10(num / den))


def weights(segment):
    rise = segment // 2
    w = np.concatenate([np.arange(1, rise + 1), np.arange(segment - rise, 0, -1)]).astype(np.float64)
    return (w / w.max()).astype(np.float32)


def chunked(process, mix, segment, overlap, stems, progress=None):
    """mix [2, N] float32 -> [stems, 2, N]. process(chunk [2, segment]) -> [stems, 2, segment]."""
    n = mix.shape[1]
    stride = max(1, int((1 - overlap) * segment))
    w = weights(segment)
    out = np.zeros((stems, 2, n), dtype=np.float32)
    total = np.zeros(n, dtype=np.float32)
    offsets = list(range(0, n, stride))
    for k, off in enumerate(offsets):
        have = min(segment, n - off)
        chunk = np.zeros((2, segment), dtype=np.float32)
        chunk[:, :have] = mix[:, off:off + have]
        y = process(chunk)
        out[:, :, off:off + have] += y[:, :, :have] * w[:have]
        total[off:off + have] += w[:have]
        if progress:
            progress(k + 1, len(offsets))
    return out / np.maximum(total, 1e-12)


def save_f32(path, a):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    np.ascontiguousarray(a, dtype="<f4").tofile(path)


def load_f32(path, shape):
    return np.fromfile(path, dtype="<f4").reshape(shape)


def test_clip(seconds=12.0, rate=44100, seed=3):
    """A deterministic stereo clip with something in every stem's range: a bass line, kick and hat bursts, a pad and a lead."""
    rng = np.random.default_rng(seed)
    n = int(seconds * rate)
    t = np.arange(n) / rate
    bass = 0.35 * np.sin(2 * np.pi * 55 * t) * (0.6 + 0.4 * np.sin(2 * np.pi * 0.5 * t))
    kick = np.zeros(n)
    for beat in np.arange(0, seconds, 0.5):
        i = int(beat * rate)
        m = min(n - i, int(0.15 * rate))
        tt = np.arange(m) / rate
        kick[i:i + m] += 0.6 * np.sin(2 * np.pi * (140 * np.exp(-tt * 25) + 45) * tt) * np.exp(-tt * 18)
    hat = np.zeros(n)
    for beat in np.arange(0.25, seconds, 0.5):
        i = int(beat * rate)
        m = min(n - i, int(0.05 * rate))
        hat[i:i + m] += 0.15 * rng.standard_normal(m) * np.exp(-np.arange(m) / rate * 120)
    pad = sum(0.08 * np.sin(2 * np.pi * f * t) for f in (220, 277.2, 329.6, 440))
    lead = 0.2 * np.sin(2 * np.pi * (660 + 20 * np.sin(2 * np.pi * 5 * t)) * t) * (np.sin(2 * np.pi * 0.25 * t) > 0)
    left = bass + kick + hat + pad + lead
    right = bass + kick + 0.8 * hat + 0.9 * pad + lead
    return np.stack([left, right]).astype(np.float32), rate


def write_json(path, data):
    with open(path, "w") as fh:
        json.dump(data, fh, indent=1)
