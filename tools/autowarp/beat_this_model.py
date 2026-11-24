#!/usr/bin/env python3
"""AW4 (docs/specs/neo-audio.md, AW4 as built): the pretrained Beat This! model (Foscarin, Schlueter, Widmer, ISMIR 2024; github.com/CPJKU/beat_this, MIT).

    beat_this_model.py fetch OUT_DIR [--strict]
                                             download the pinned small checkpoint, check its SHA-256, export it to ONNX (a free time axis), check the export
                                             against the PyTorch model, print the ONNX file's SHA-256 (--strict: fail when it is not the pinned one) and write OUT_DIR/beat_this_small.onnx
    beat_this_model.py reference OUT_DIR WAV [WAV ...]
                                             run the upstream package (pip install beat-this) on each file and write, per file, NAME.spect.f32 (the log-mel
                                             spectrogram, frames x 128 float32), NAME.exact.f32 (the same recomputed in float64, to tell the package's float32
                                             rounding from our difference), NAME.logits.f32 (beat then downbeat logits, 2 x frames) and NAME.ref.json
                                             (beats and downbeats in seconds, the minimal post-processor), for the C# parity tests

The weights are never committed (3rdParty/beat-this/.gitignore). Pinned toolchain (the export is deterministic on it): torch 2.11.0 (CPU), torchaudio 2.11.0,
beat-this 1.1.0, onnx, onnxruntime. Upstream: the MIT licence covers the code and the published weights (3rdParty/beat-this/NOTICE).
"""
import hashlib, json, os, sys, urllib.request

CKPT_NAME = "small0"
CKPT_URL = "https://cloud.cp.jku.at/public.php/dav/files/7ik4RrBKTS273gp/small0.ckpt"
CKPT_SHA256 = "6074be2c4d490c5f6101fcc374a1ec72ae93456e23bb6019783b849f5dc7d47b"
ONNX_NAME = "beat_this_small.onnx"
# The SHA-256 of the export on the pinned toolchain (the app checks the file it loads against this; see ReBuzz/Core/Clips/BeatThisModel.cs).
ONNX_SHA256 = "20957568b91eb46caf8c5559726bb49a1aa5afcd2da7bd64d68586c0b597eef1"


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def fetch_ckpt(out_dir):
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, CKPT_NAME + ".ckpt")
    if not os.path.exists(path) or sha256(path) != CKPT_SHA256:
        print("downloading", CKPT_URL)
        urllib.request.urlretrieve(CKPT_URL, path)
    got = sha256(path)
    if got != CKPT_SHA256:
        raise SystemExit(f"checkpoint SHA-256 {got} is not the pinned {CKPT_SHA256}")
    print("checkpoint", path, got)
    return path


def export(ckpt, onnx_path):
    import torch
    from beat_this.inference import load_model
    model = load_model(ckpt, "cpu").eval()
    print("parameters (million):", round(sum(p.numel() for p in model.parameters()) / 1e6, 3))

    class Wrap(torch.nn.Module):
        def __init__(self, m):
            super().__init__(); self.m = m

        def forward(self, x):
            o = self.m(x)
            return o["beat"], o["downbeat"]

    torch.onnx.export(Wrap(model).eval(), (torch.randn(1, 1500, 128),), onnx_path, input_names=["spect"], output_names=["beat", "downbeat"],
                      dynamic_axes={"spect": {1: "T"}, "beat": {1: "T"}, "downbeat": {1: "T"}}, opset_version=17, dynamo=False)
    return model


def check(model, onnx_path):
    import numpy as np, onnxruntime as ort, torch
    s = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
    worst = 0.0
    g = torch.Generator().manual_seed(7)
    for T in (60, 333, 1500):
        x = torch.randn(1, T, 128, generator=g) * 2 + 3
        with torch.inference_mode():
            o = model(x)
        b, d = s.run(None, {"spect": x.numpy()})
        e = max(abs(o["beat"].numpy() - b).max(), abs(o["downbeat"].numpy() - d).max())
        print(f"  T={T}: max |onnx - torch| = {e:.2e}")
        worst = max(worst, float(e))
    if worst > 1e-4:
        raise SystemExit(f"the ONNX export differs from the PyTorch model by {worst}")


def cmd_fetch(out_dir, strict=False):
    ckpt = fetch_ckpt(out_dir)
    onnx_path = os.path.join(out_dir, ONNX_NAME)
    model = export(ckpt, onnx_path)
    check(model, onnx_path)
    got = sha256(onnx_path)
    print("onnx", onnx_path, os.path.getsize(onnx_path), "bytes, sha256", got)
    if ONNX_SHA256 and got != ONNX_SHA256:
        print(f"::warning::the export's SHA-256 {got} is not the pinned {ONNX_SHA256}: the toolchain differs from the pinned one")
        if strict:
            raise SystemExit(3)


def exact_spect(a2b, signal, sr):
    """The reference's spectrogram in float64 (numpy's FFT, torchaudio's Slaney filterbank): the package computes it in float32, whose rounding shows in quiet bins."""
    import numpy as np, torch, torchaudio, soxr
    if signal.ndim == 2:
        signal = signal.mean(1)
    if sr != 22050:
        signal = soxr.resample(signal, in_rate=sr, out_rate=22050)
    x = np.asarray(torch.tensor(signal, dtype=torch.float32).numpy(), dtype=np.float64)
    n, N, H = len(x), 1024, 441
    w = 0.5 - 0.5 * np.cos(2 * np.pi * np.arange(N) / N)
    pad = np.pad(x, (512, 512), mode="reflect")
    S = np.stack([np.abs(np.fft.rfft(pad[i * H:i * H + N] * w)) for i in range(1 + n // H)]) / np.sqrt(N)
    fb = torchaudio.functional.melscale_fbanks(513, 30, 11000, 128, 22050, norm=None, mel_scale="slaney").numpy().astype(np.float64)
    return np.log1p(1000 * (S @ fb))


def cmd_reference(out_dir, wavs):
    import numpy as np, torch
    from beat_this.inference import Audio2Beats, load_audio
    os.makedirs(out_dir, exist_ok=True)
    ckpt = fetch_ckpt(out_dir)
    a2b = Audio2Beats(checkpoint_path=ckpt, device="cpu", dbn=False)
    for w in wavs:
        name = os.path.splitext(os.path.basename(w))[0]
        sig, sr = load_audio(w)
        spect = a2b.signal2spect(sig, sr)
        beat_l, down_l = a2b.spect2frames(spect)
        beats, downs = a2b.frames2beats(beat_l, down_l)
        spect.numpy().astype(np.float32).tofile(os.path.join(out_dir, name + ".spect.f32"))
        exact_spect(a2b, sig, sr).astype(np.float32).tofile(os.path.join(out_dir, name + ".exact.f32"))
        np.concatenate([beat_l.numpy(), down_l.numpy()]).astype(np.float32).tofile(os.path.join(out_dir, name + ".logits.f32"))
        json.dump({"sr": int(sr), "frames": int(spect.shape[0]), "beats": [float(v) for v in beats], "downbeats": [float(v) for v in downs]}, open(os.path.join(out_dir, name + ".ref.json"), "w"))
        print(name, spect.shape[0], "frames,", len(beats), "beats,", len(downs), "downbeats")


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "fetch":
        cmd_fetch(sys.argv[2], "--strict" in sys.argv)
    elif len(sys.argv) >= 4 and sys.argv[1] == "reference":
        cmd_reference(sys.argv[2], sys.argv[3:])
    else:
        print(__doc__); sys.exit(2)
