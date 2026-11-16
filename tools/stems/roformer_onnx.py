#!/usr/bin/env python3
# NeoBuzz Live stem models (docs/specs/stem-separation.md): the "Best" model, BS-RoFormer SW, to ONNX.
#
# The checkpoint is jarredou's rehost of "BS-Roformer SW" (a six-stem BS-RoFormer: bass, drums, other, vocals, guitar, piano; MVSEP reports
# bass 14.62, drums 14.11, vocals 11.30, other 8.71 SDR on its multisong set) in the architecture of lucidrains/BS-RoFormer as ZFTurbo's
# Music-Source-Separation-Training (MIT) runs it. The weights have no stated licence. The ONNX graph is cut the way the Demucs one is:
# the STFT and the inverse STFT stay outside (our C# does them), so the graph takes
#   spec_real, spec_imag  [1, 2, 1025, T]       the spectrogram of one 4 s segment (n_fft 2048, hop 512, Hann, centred)
# and returns
#   out_spec_real, out_spec_imag  [1, S, 2, 1025, T]       the spectrogram of each stem (masks applied, DC zeroed).
# "guitar" and "piano" are folded into "other" by the model.json's output mapping.
#
#   python3 roformer_onnx.py export <out folder> [--chunk 176400] [--fp16-weights]
import argparse
import glob
import json
import math
import os
import subprocess
import sys

import numpy as np
import torch
import torch.nn as nn

HF_REPO = os.environ.get("STEM_HF_REPO", "enerjazzer/BS-ROFO-SW-Fixed")  # a mirror of jarredou/BS-ROFO-SW-Fixed (that repo answers 401 from CI)
MSST_URL = "https://github.com/ZFTurbo/Music-Source-Separation-Training"
WORK = os.environ.get("STEM_WORK", os.path.join(os.path.expanduser("~"), ".cache", "neobuzz-stems"))
FOUR = ["vocals", "drums", "bass", "other"]
LICENSE = ("BS-RoFormer code: MIT (lucidrains/BS-RoFormer, ZFTurbo/Music-Source-Separation-Training). The pretrained weights (jarredou's rehost of "
           "BS-Roformer SW) have no stated licence and no known provenance: personal use only, do not redistribute.")


def fetch():
    """Downloads the checkpoint and its config from Hugging Face and clones the training/inference code; returns (ckpt, yaml, code dir, sha)."""
    from huggingface_hub import hf_hub_download, list_repo_files
    os.makedirs(WORK, exist_ok=True)
    files = list_repo_files(HF_REPO)
    print("repo files:", files, flush=True)
    ckpts = [f for f in files if f.endswith((".ckpt", ".pt", ".pth", ".safetensors"))]
    yamls = [f for f in files if f.endswith((".yaml", ".yml"))]
    if not ckpts or not yamls:
        raise SystemExit(f"{HF_REPO} has no checkpoint or no config: {files}")
    ckpt = hf_hub_download(HF_REPO, ckpts[0], cache_dir=WORK)
    cfg = hf_hub_download(HF_REPO, yamls[0], cache_dir=WORK)
    code = os.path.join(WORK, "msst")
    if not os.path.isdir(code):
        subprocess.check_call(["git", "clone", "--depth", "1", MSST_URL, code])
    sha = subprocess.check_output(["git", "-C", code, "rev-parse", "HEAD"], text=True).strip()
    return ckpt, cfg, code, sha


def load_model(ckpt, cfg, code):
    """The BSRoformer of the config with the checkpoint's weights (eval, no flash attention, no checkpointing)."""
    sys.path.insert(0, code)
    try:
        from utils.settings import get_model_from_config
    except ImportError:
        from utils import get_model_from_config
    model, config = get_model_from_config("bs_roformer", cfg)
    if ckpt.endswith(".safetensors"):
        from safetensors.torch import load_file
        state = load_file(ckpt)
    else:
        state = torch.load(ckpt, map_location="cpu", weights_only=False)
    if isinstance(state, dict) and "state_dict" in state:
        state = state["state_dict"]
    result = model.load_state_dict(state, strict=False)
    print("load_state_dict: missing", len(result.missing_keys), "unexpected", len(result.unexpected_keys), flush=True)
    if result.missing_keys:
        raise SystemExit(f"checkpoint does not fit the model: missing {result.missing_keys[:5]}")
    for mod in model.modules():
        if hasattr(mod, "flash"):
            mod.flash = False              # plain attention: exports to every opset
    if hasattr(model, "use_torch_checkpoint"):
        model.use_torch_checkpoint = False
    if hasattr(model, "use_sage_attention"):
        model.use_sage_attention = False
    return model.eval(), config


def instruments_of(config):
    ins = list(getattr(config.training, "instruments", None) or [])
    if not ins:
        raise SystemExit("the config names no instruments")
    return ins


class RoformerCore(nn.Module):
    """BSRoformer.forward from the spectrogram to the masked spectrograms, in real arithmetic (no complex numbers, no STFT)."""

    def __init__(self, m):
        super().__init__()
        self.m = m
        self.s = getattr(m, "audio_channels", 2)

    def forward(self, spec_real, spec_imag):
        m = self.m
        b, s, f, t = spec_real.shape
        stft = torch.stack([spec_real, spec_imag], dim=-1)                    # b s f t 2
        stft = stft.permute(0, 2, 1, 3, 4).reshape(b, f * s, t, 2)             # b (f s) t c
        x = stft.permute(0, 2, 1, 3).reshape(b, t, f * s * 2)                  # b t (f c)
        x = m.band_split(x)                                                    # b t bands d
        store = [None] * len(m.layers)
        for i, block in enumerate(m.layers):
            if len(block) == 3:
                linear, time_tr, freq_tr = block
                bb, tt, ff, dd = x.shape
                x = linear(x.reshape(bb, tt * ff, dd)).reshape(bb, tt, ff, dd)
            else:
                time_tr, freq_tr = block
            if getattr(m, "skip_connection", False):
                for j in range(i):
                    x = x + store[j]
            bb, tt, ff, dd = x.shape
            x = x.permute(0, 2, 1, 3).reshape(bb * ff, tt, dd)                 # (b f) t d
            x = time_tr(x)
            x = x.reshape(bb, ff, tt, dd).permute(0, 2, 1, 3).reshape(bb * tt, ff, dd)  # (b t) f d
            x = freq_tr(x)
            x = x.reshape(bb, tt, ff, dd)
            if getattr(m, "skip_connection", False):
                store[i] = x
        if getattr(m, "final_norm", None) is not None:
            x = m.final_norm(x)
        mask = torch.stack([fn(x) for fn in m.mask_estimators], dim=1)         # b n t (f c)
        n = mask.shape[1]
        mask = mask.reshape(b, n, t, f * s, 2).permute(0, 1, 3, 2, 4)          # b n (f s) t c
        a_re, a_im = stft[..., 0].unsqueeze(1), stft[..., 1].unsqueeze(1)      # b 1 (f s) t
        m_re, m_im = mask[..., 0], mask[..., 1]
        out_re = a_re * m_re - a_im * m_im
        out_im = a_re * m_im + a_im * m_re
        if getattr(m, "zero_dc", True):
            keep = torch.ones(1, 1, f * s, 1, dtype=out_re.dtype)
            keep[:, :, :s] = 0                                                 # the DC bin of every channel
            out_re, out_im = out_re * keep, out_im * keep
        out_re = out_re.reshape(b, n, f, s, t).permute(0, 1, 3, 2, 4)          # b n s f t
        out_im = out_im.reshape(b, n, f, s, t).permute(0, 1, 3, 2, 4)
        return out_re, out_im


def stft_args(m):
    kw = dict(m.stft_kwargs)
    return int(kw["n_fft"]), int(kw["hop_length"]), int(kw.get("win_length", kw["n_fft"]))


def spec_of(chunk, nfft, hop, win):
    """The STFT the C# does: [2, L] -> real, imag [1, 2, bins, T]."""
    z = torch.stft(torch.from_numpy(chunk), nfft, hop, win_length=win, window=torch.hann_window(win), center=True, normalized=False,
                   return_complex=True, pad_mode="reflect")
    return z.real[None].contiguous(), z.imag[None].contiguous()


def wave_of(re, im, nfft, hop, win, length):
    """The inverse STFT the C# does: [1, S, 2, bins, T] x 2 -> [S, 2, length]."""
    z = torch.complex(torch.from_numpy(re), torch.from_numpy(im))[0]
    n, s, bins, t = z.shape
    y = torch.istft(z.reshape(n * s, bins, t), nfft, hop, win_length=win, window=torch.hann_window(win), center=True, normalized=False,
                    length=length)
    return y.reshape(n, s, length).numpy()


def to_fp16_weights(path):
    """Stores the big float weights as float16 with a Cast back to float32 after them: half the file, the same graph (ORT folds the casts)."""
    import onnx
    from onnx import numpy_helper, TensorProto, helper
    model = onnx.load(path)
    g = model.graph
    casts, kept, saved = [], [], 0
    for init in g.initializer:
        if init.data_type == TensorProto.FLOAT:
            arr = numpy_helper.to_array(init)
            if arr.size >= 4096 and np.isfinite(arr).all() and np.abs(arr).max() < 6e4:
                half = numpy_helper.from_array(arr.astype(np.float16), init.name + "__fp16")
                kept.append(half)
                casts.append(helper.make_node("Cast", [half.name], [init.name], to=TensorProto.FLOAT, name=init.name + "__cast"))
                saved += arr.size * 2
                continue
        kept.append(init)
    del g.initializer[:]
    g.initializer.extend(kept)
    nodes = list(g.node)
    del g.node[:]
    g.node.extend(casts + nodes)
    onnx.save(model, path)
    print(f"fp16 weights: {saved / 1e6:.0f} MB saved", flush=True)


def export(out, chunk, fp16):
    ckpt, cfg, code, sha = fetch()
    model, config = load_model(ckpt, cfg, code)
    nfft, hop, win = stft_args(model)
    instruments = instruments_of(config)
    folder = os.path.join(out, "bs-roformer-sw")
    os.makedirs(folder, exist_ok=True)
    core = RoformerCore(model).eval()
    rng = np.random.default_rng(1)
    audio = (rng.standard_normal((2, chunk)) * 0.1).astype(np.float32)
    re, im = spec_of(audio, nfft, hop, win)
    with torch.no_grad():
        o_re, o_im = core(re, im)
        mine = wave_of(o_re.numpy(), o_im.numpy(), nfft, hop, win, chunk)
        ref = model(torch.from_numpy(audio)[None])[0].numpy()               # the model's own forward: [S, 2, L]
    err = float(np.abs(ref - mine).max())
    print(f"the cut model differs from BSRoformer.forward by {err:.2e} (peak {np.abs(ref).max():.2f})", flush=True)
    if err > 1e-3:
        raise SystemExit("the cut model does not reproduce BSRoformer.forward")
    fname = "bs_roformer_sw.onnx"
    path = os.path.join(folder, fname)
    torch.onnx.export(core, (re, im), path, input_names=["spec_real", "spec_imag"], output_names=["out_spec_real", "out_spec_imag"],
                      opset_version=17, dynamo=False, do_constant_folding=True)
    if fp16:
        to_fp16_weights(path)
    print(f"{path}: {os.path.getsize(path) / 1e6:.0f} MB", flush=True)
    outputs = {}
    for k, name in enumerate(instruments):
        outputs.setdefault(name if name in FOUR else "other", []).append(k)
    meta = {"id": "bs-roformer-sw", "quality": "Best", "title": "BS-RoFormer SW (six stems folded into four)", "family": "spec-mask",
            "source": f"https://huggingface.co/{HF_REPO} (checkpoint), {MSST_URL} @ {sha} (model code)", "license": LICENSE, "revision": "1",
            "sampleRate": int(config.audio.sample_rate) if hasattr(config.audio, "sample_rate") else 44100, "segment": chunk, "overlap": 0.5,
            "nfft": nfft, "hop": hop, "stems": FOUR, "outputStems": len(instruments), "instruments": instruments,
            "passes": [{"file": fname, "outputs": outputs}],
            "export": [{"file": fname, "cutError": err, "bytes": os.path.getsize(path), "fp16Weights": fp16}]}
    with open(os.path.join(folder, "model.json"), "w") as fh:
        json.dump(meta, fh, indent=1)
    return meta


# ---------------------------------------------------------------------------------------------------------- validation hooks
def torch_pass(manifest):
    ckpt, cfg, code, _ = fetch()
    model, _ = load_model(ckpt, cfg, code)

    def run(chunk):
        with torch.no_grad():
            return model(torch.from_numpy(chunk)[None])[0].numpy()
    return run


def onnx_pass(manifest, path):
    import onnxruntime as ort
    session = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    nfft, hop = manifest["nfft"], manifest["hop"]

    def run(chunk):
        re, im = spec_of(chunk, nfft, hop, nfft)
        o_re, o_im = session.run(None, {"spec_real": re.numpy(), "spec_imag": im.numpy()})
        return wave_of(o_re, o_im, nfft, hop, nfft, chunk.shape[-1])
    return run


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("command", choices=["export"])
    ap.add_argument("out")
    ap.add_argument("--chunk", type=int, default=176400)
    ap.add_argument("--fp16-weights", action="store_true")
    a = ap.parse_args()
    export(a.out, a.chunk, a.fp16_weights)
