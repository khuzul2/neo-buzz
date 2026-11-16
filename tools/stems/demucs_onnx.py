#!/usr/bin/env python3
# NeoBuzz Live stem models (docs/specs/stem-separation.md): Demucs to ONNX.
#
# The network is cut where sevagh's demucs.cpp / demucs.onnx cut it: the STFT and the inverse STFT stay outside (our own C# does
# them), so the ONNX graph has no complex numbers and runs on every ONNX Runtime provider. The graph takes
#   mix  [1, 2, L]            the waveform of one segment (L = 343980 = 7.8 s at 44.1 kHz)
#   cac  [1, 4, 2048, 336]    its spectrogram as HTDemucs._magnitude makes it (left re, left im, right re, right im)
# and returns the two branches' outputs before the inverse STFT:
#   freq [1, S, 4, 2048, 336] (already un-normalised) and  time [1, S, 2, L]
# The caller adds the inverse STFT of `freq` to `time`.
#
#   python3 demucs_onnx.py export <htdemucs|htdemucs_ft> <out folder> [--fp16-weights]
import json
import math
import os
import sys

import torch
import torch.nn as nn
import torch.nn.functional as F
from demucs.pretrained import get_model
from demucs.spec import spectro, ispectro
from demucs.utils import center_trim  # noqa: F401

SEGMENT = 343980  # int(7.8 * 44100)


class DemucsCore(nn.Module):
    """HTDemucs.forward from the normalisation of the inputs to just before _mask/_ispec, with no complex numbers."""

    def __init__(self, m):
        super().__init__()
        self.m = m

    def forward(self, mix, x):
        m = self.m
        B, C, Fq, T = x.shape
        mean = x.mean(dim=(1, 2, 3), keepdim=True)
        std = x.std(dim=(1, 2, 3), keepdim=True)
        x = (x - mean) / (1e-5 + std)
        xt = mix
        meant = xt.mean(dim=(1, 2), keepdim=True)
        stdt = xt.std(dim=(1, 2), keepdim=True)
        xt = (xt - meant) / (1e-5 + stdt)
        saved, saved_t, lengths, lengths_t = [], [], [], []
        for idx, encode in enumerate(m.encoder):
            lengths.append(x.shape[-1])
            inject = None
            if idx < len(m.tencoder):
                lengths_t.append(xt.shape[-1])
                tenc = m.tencoder[idx]
                xt = tenc(xt)
                if not tenc.empty:
                    saved_t.append(xt)
                else:
                    inject = xt
            x = encode(x, inject)
            if idx == 0 and m.freq_emb is not None:
                frs = torch.arange(x.shape[-2], device=x.device)
                emb = m.freq_emb(frs).t()[None, :, :, None].expand_as(x)
                x = x + m.freq_emb_scale * emb
            saved.append(x)
        if m.crosstransformer:
            if m.bottom_channels:
                b, c, f, t = x.shape
                x = x.reshape(b, c, f * t)
                x = m.channel_upsampler(x)
                x = x.reshape(b, -1, f, t)
                xt = m.channel_upsampler_t(xt)
            x, xt = m.crosstransformer(x, xt)
            if m.bottom_channels:
                x = x.reshape(b, -1, f * t)
                x = m.channel_downsampler(x)
                x = x.reshape(b, -1, f, t)
                xt = m.channel_downsampler_t(xt)
        for idx, decode in enumerate(m.decoder):
            skip = saved.pop(-1)
            x, pre = decode(x, skip, lengths.pop(-1))
            offset = m.depth - len(m.tdecoder)
            if idx >= offset:
                tdec = m.tdecoder[idx - offset]
                length_t = lengths_t.pop(-1)
                if tdec.empty:
                    pre = pre[:, :, 0]
                    xt, _ = tdec(pre, None, length_t)
                else:
                    skip = saved_t.pop(-1)
                    xt, _ = tdec(xt, skip, length_t)
        S = len(m.sources)
        x = x.view(B, S, -1, Fq, T)
        x = x * std[:, None] + mean[:, None]
        xt = xt.view(B, S, -1, mix.shape[-1])
        xt = xt * stdt[:, None] + meant[:, None]
        return x, xt


def cac_spec(model, mix):
    """HTDemucs._spec and _magnitude (what our C# does by hand): [B, 2, L] to [B, 4, 2048, T]."""
    z = model._spec(mix)
    return model._magnitude(z)


def post(model, freq, time, length):
    """HTDemucs._mask and _ispec of the freq branch, plus the time branch (what our C# does by hand)."""
    zout = model._mask(None, freq)
    x = model._ispec(zout, length)
    return x + time


def single_models(name):
    bag = get_model(name)
    return bag.models, [list(w) for w in bag.weights] if hasattr(bag, "weights") else None


QUALITY = {"htdemucs": ("Fast", "Demucs (htdemucs)"), "htdemucs_ft": ("Balanced", "Demucs fine-tuned (htdemucs_ft)")}
SOURCE = {"htdemucs": "https://dl.fbaipublicfiles.com/demucs/hybrid_transformer/955717e8-8726e21a.th (facebookresearch/demucs 4.1)",
          "htdemucs_ft": "https://dl.fbaipublicfiles.com/demucs/hybrid_transformer/ f7e0c4bc-ba3fe64a.th, d12395a8-e57c48e6.th, "
                         "92cfc3b6-ef3bcb9c.th, 04573f0d-f3cf25b2.th (facebookresearch/demucs 4.1)"}
LICENSE = ("The Demucs code is MIT (facebookresearch/demucs). The pretrained weights come with no licence of their own and were trained on "
           "MUSDB18-HQ and 800 further songs: personal use only, do not redistribute.")


def to_fp16_weights(path):
    from roformer_onnx import to_fp16_weights as convert
    convert(path)


def export(name, out, fp16=False):
    """Writes <out>/<name>/model.json and the ONNX files; returns the manifest dict."""
    folder = os.path.join(out, name)
    os.makedirs(folder, exist_ok=True)
    models, weights = single_models(name)
    passes = []
    details = []
    for i, m in enumerate(models):
        m.eval()
        core = DemucsCore(m).eval()
        mix = torch.randn(1, 2, SEGMENT)
        x = cac_spec(m, mix)
        with torch.no_grad():
            f, t = core(mix, x)
            ref = m(mix)            # the cut must give the model's own output
            err = (ref - post(m, f, t, SEGMENT)).abs().max().item()
        assert err < 1e-3, f"the cut model differs from HTDemucs.forward by {err}"
        fname = f"{name}_{i}.onnx"
        path = os.path.join(folder, fname)
        torch.onnx.export(core, (mix, x), path, input_names=["mix", "cac"], output_names=["freq", "time"],
                          opset_version=17, dynamo=False, do_constant_folding=True)
        sources = list(m.sources)
        w = weights[i] if weights else [1.0] * len(sources)
        outputs = {src: [k] for k, src in enumerate(sources) if w[k] > 0}   # a bag of one-stem specialists keeps one stem each
        passes.append({"file": fname, "outputs": outputs})
        details.append({"file": fname, "cutError": err, "bytes": os.path.getsize(path)})
        if fp16:
            to_fp16_weights(path)
        details[-1]["bytes"] = os.path.getsize(path)
        details[-1]["fp16Weights"] = fp16
        print(f"{path}: {os.path.getsize(path) / 1e6:.0f} MB, cut error {err:.2e}", flush=True)
        del core
    quality, title = QUALITY[name]
    meta = {"id": name, "quality": quality, "title": title, "family": "htdemucs", "source": SOURCE[name], "license": LICENSE,
            "revision": "1", "sampleRate": 44100, "segment": SEGMENT, "overlap": 0.25, "nfft": 4096, "hop": 1024,
            "stems": ["vocals", "drums", "bass", "other"], "outputStems": len(models[0].sources), "passes": passes, "export": details}
    with open(os.path.join(folder, "model.json"), "w") as fh:
        json.dump(meta, fh, indent=1)
    return meta




# ---------------------------------------------------------------------------------------------------------- validation hooks
from types import SimpleNamespace  # noqa: E402
from demucs.htdemucs import HTDemucs  # noqa: E402

_NS = SimpleNamespace(hop_length=1024, nfft=4096, cac=True, wiener_iters=0)


def torch_pass(name, index):
    """The PyTorch model of one pass as a function: chunk [2, SEGMENT] float32 -> [S, 2, SEGMENT]."""
    m = get_model(name).models[index].eval()

    def run(chunk):
        with torch.no_grad():
            return m(torch.from_numpy(chunk)[None])[0].numpy()
    return run


def onnx_pass(path):
    """The exported model as a function with the same signature, with the transform done as the C# does it (here by Demucs's own code)."""
    import onnxruntime as ort
    session = ort.InferenceSession(path, providers=["CPUExecutionProvider"])

    def run(chunk):
        mix = torch.from_numpy(chunk)[None]
        cac = HTDemucs._magnitude(_NS, HTDemucs._spec(_NS, mix))
        freq, time = session.run(None, {"mix": mix.numpy(), "cac": cac.numpy()})
        zout = HTDemucs._mask(_NS, None, torch.from_numpy(freq))
        out = HTDemucs._ispec(_NS, zout, chunk.shape[-1]) + torch.from_numpy(time)
        return out[0].numpy()
    return run


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if a != "--fp16-weights"]
    if len(args) == 3 and args[0] == "export":
        export(args[1], args[2], "--fp16-weights" in sys.argv)
    else:
        sys.exit(__doc__)
