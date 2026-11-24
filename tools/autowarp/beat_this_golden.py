#!/usr/bin/env python3
"""AW4: writes the golden data of ReBuzz.SessionTests/AutoWarp/BeatThisTests.cs from the upstream package (pip install beat-this, the pinned toolchain of beat_this_requirements.txt):

    beat_this_golden.py CHECKPOINT OUT_DIR       OUT_DIR/beat-this-golden.bin and beat-this-golden.json

The signal is made by integer arithmetic (a linear congruential noise, clicks, a tone), so the test can make the same one. The binary file holds: int32 samples, int32 frames, the samples (float32),
the log-mel spectrogram of the package (float32, frames x 128), the same recomputed in float64 (frames x 128), and the logits the package makes of the package's spectrogram (its windows included) (beat then downbeat,
2 x frames). The JSON holds the windows the package makes for several lengths and the beats and downbeats its minimal post-processor picks from fixed random logits.
"""
import json, os, struct, sys
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from beat_this_model import exact_spect


def signal(n=26460):
    x = np.zeros(n, np.float64)
    s = 12345
    for i in range(n):
        s = (s * 1103515245 + 12345) & 0x7FFFFFFF
        noise = (s / 0x7FFFFFFF - 0.5) * 0.2
        click = 0.0
        ph = i % 6615                                     # a click every 0.3 s
        if ph < 400:
            click = 0.6 * (1 - ph / 400.0) * ((-1) ** (ph // 3))
        x[i] = noise + click + 0.15 * np.sin(2 * np.pi * 220 * i / 22050.0)
    return x.astype(np.float32)


def main(ckpt, out):
    import torch
    from beat_this.inference import Audio2Beats, split_piece, load_audio
    from beat_this.model.postprocessor import Postprocessor
    os.makedirs(out, exist_ok=True)
    x = signal()
    a2b = Audio2Beats(checkpoint_path=ckpt, device="cpu", dbn=False)
    spect = a2b.signal2spect(x.astype(np.float64), 22050)
    exact = exact_spect(a2b, x.astype(np.float64), 22050)
    beat_l, down_l = a2b.spect2frames(spect)          # the package's own windowing (a piece shorter than a window is padded by the border), then the network
    logits = np.concatenate([beat_l.numpy(), down_l.numpy()]).astype(np.float32)
    with open(os.path.join(out, "beat-this-golden.bin"), "wb") as f:
        f.write(struct.pack("<ii", len(x), spect.shape[0]))
        for a in (x, spect.numpy().astype(np.float32), exact.astype(np.float32), logits):
            f.write(a.tobytes())
    windows = {}
    for n in (1, 100, 1488, 1489, 1490, 1500, 2976, 3000, 4000, 30001):
        chunks, starts = split_piece(torch.zeros(n, 128), 1500, border_size=6, avoid_short_end=True)
        windows[str(n)] = {"starts": [int(s) for s in starts], "lengths": [int(c.shape[0]) for c in chunks]}
    rng = np.random.default_rng(5)
    peaks = []
    pp = Postprocessor("minimal", 50)
    for k, n in enumerate((300, 2000, 5000)):
        beat = (np.round(rng.normal(-1.0, 3.0, n) * 2) / 2).astype(np.float32)          # halves: exact in text, and ties between neighbours
        down = (np.round(rng.normal(-3.0, 3.0, n) * 2) / 2).astype(np.float32)
        for u in range(5, n - 5, 25 + 3 * k):                      # clean, regular peaks, some neighbours: the averaging of adjacent peaks
            beat[u] = 6.0; beat[u + 1] = 5.5 if u % 2 else -4
        for u in range(5, n - 5, 100):
            down[u] = 5.0
        b, d = pp(torch.tensor(beat), torch.tensor(down))
        peaks.append({"beat": [float(v) for v in beat], "down": [float(v) for v in down], "beats": [float(v) for v in b], "downbeats": [float(v) for v in d]})
    json.dump({"windows": windows, "peaks": peaks}, open(os.path.join(out, "beat-this-golden.json"), "w"), separators=(",", ":"))
    print("frames", spect.shape[0], "max |exact - package| =", float(abs(exact.astype(np.float32) - spect.numpy()).max()))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
