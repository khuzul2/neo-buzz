#!/usr/bin/env python3
"""AW4 (docs/specs/neo-audio.md, AW4 as built): the pretrained Beat This! model (Foscarin, Schlueter, Widmer, ISMIR 2024; github.com/CPJKU/beat_this, MIT).

    beat_this_model.py fetch OUT_DIR [--strict]
                                             download the pinned small checkpoint, check its SHA-256, export it to ONNX (a free time axis, the long attentions fused), check the export
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
ONNX_SHA256 = "b700cc6814038bfad9a3585357cd3d2deb606e7c60453af1a8bd8e8efba5a309"


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


HEAD_DIM = 32
# Heads of each attention of the small model (the head size is 32 everywhere): the time attentions of the three frontend blocks, and the six transformer layers.
def _heads(name):
    for key, heads in (("blocks.0/partial/attnT", 1), ("blocks.1/partial/attnT", 2), ("blocks.2/partial/attnT", 4), ("transformer_blocks", 4)):
        if key in name:
            return heads
    return None


def fuse_attention(path):
    """Replaces each long attention (MatMul, Softmax, MatMul over 1500 x 1500 scores) of the exported graph by ONNX Runtime's MultiHeadAttention operator (com.microsoft; the
    queries and keys already carry the scale, so scale=1): the same function, 1.3 times faster on a CPU (it works on one head at a time instead of on the whole score tensor).
    The short attentions over the 8 to 32 frequency bands are left as they are. Returns the number of attentions fused."""
    import numpy as np, onnx
    from onnx import helper as h, numpy_helper as nh
    m = onnx.load(path)
    g = m.graph
    nodes = list(g.node)
    prod = {o: n for n in nodes for o in n.output}
    first_input = {}
    for n in nodes:
        if n.op_type == "MatMul":
            first_input.setdefault(n.input[0], []).append(n)
    drop, replace, inits = set(), {}, []
    for sm in [n for n in nodes if n.op_type == "Softmax"]:
        heads = _heads(sm.name)
        if heads is None:
            continue
        mm1, mm2 = prod[sm.input[0]], first_input[sm.output[0]][0]
        q, kt, v, out = mm1.input[0], mm1.input[1], mm2.input[1], mm2.output[0]
        base = sm.name.rsplit("/", 1)[0] + "/mha"
        inits.append(nh.from_array(np.array([0, 0, -1], np.int64), base + "/shape_bsd"))
        inits.append(nh.from_array(np.array([0, 0, heads, HEAD_DIM], np.int64), base + "/shape_split"))
        replace[id(mm2)] = [
            h.make_node("Transpose", [q], [base + "/q_t"], perm=[0, 2, 1, 3], name=base + "/q_T"),
            h.make_node("Reshape", [base + "/q_t", base + "/shape_bsd"], [base + "/q_r"], name=base + "/q_R"),
            h.make_node("Transpose", [kt], [base + "/k_t"], perm=[0, 3, 1, 2], name=base + "/k_T"),
            h.make_node("Reshape", [base + "/k_t", base + "/shape_bsd"], [base + "/k_r"], name=base + "/k_R"),
            h.make_node("Transpose", [v], [base + "/v_t"], perm=[0, 2, 1, 3], name=base + "/v_T"),
            h.make_node("Reshape", [base + "/v_t", base + "/shape_bsd"], [base + "/v_r"], name=base + "/v_R"),
            h.make_node("MultiHeadAttention", [base + "/q_r", base + "/k_r", base + "/v_r"], [base + "/o"], domain="com.microsoft", num_heads=heads, scale=1.0, name=base + "/MHA"),
            h.make_node("Reshape", [base + "/o", base + "/shape_split"], [base + "/o_r"], name=base + "/o_R"),
            h.make_node("Transpose", [base + "/o_r"], [out], perm=[0, 2, 1, 3], name=base + "/o_T"),
        ]
        drop |= {id(mm1), id(sm)}
    new_nodes = []
    for n in nodes:
        if id(n) in drop:
            continue
        new_nodes += replace.get(id(n), [n])
    del g.node[:]
    g.node.extend(new_nodes)
    g.initializer.extend(inits)
    m.opset_import.append(h.make_opsetid("com.microsoft", 1))
    onnx.save(m, path)
    return len(replace)


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
    print("attentions fused:", fuse_attention(onnx_path))
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
