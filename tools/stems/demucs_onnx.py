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
#   python3 demucs_onnx.py export <htdemucs|htdemucs_ft> <out folder>
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


def export(name, out):
    os.makedirs(out, exist_ok=True)
    models, weights = single_models(name)
    entries = []
    for i, m in enumerate(models):
        m.eval()
        core = DemucsCore(m).eval()
        mix = torch.randn(1, 2, SEGMENT)
        x = cac_spec(m, mix)
        with torch.no_grad():
            f, t = core(mix, x)
            # the cut must give the model's own output
            ref = m(mix)
            mine = post(m, f, t, SEGMENT)
            err = (ref - mine).abs().max().item()
        assert err < 1e-3, f"the cut model differs from HTDemucs.forward by {err}"
        path = os.path.join(out, f"{name}_{i}.onnx")
        torch.onnx.export(core, (mix, x), path, input_names=["mix", "cac"], output_names=["freq", "time"],
                          opset_version=17, dynamo=False, do_constant_folding=True)
        w = weights[i] if weights else [1, 1, 1, 1]
        entries.append({"file": os.path.basename(path), "sources": list(m.sources), "weights": w,
                        "cut_error": err, "bytes": os.path.getsize(path)})
        print(f"{path}: {os.path.getsize(path) / 1e6:.0f} MB, cut error {err:.2e}", flush=True)
    meta = {"family": "htdemucs", "name": name, "sampleRate": 44100, "segment": SEGMENT, "nfft": 4096, "hop": 1024,
            "bins": 2048, "frames": math.ceil(SEGMENT / 1024), "channels": 2,
            "sources": list(models[0].sources), "models": entries}
    with open(os.path.join(out, name + ".json"), "w") as fh:
        json.dump(meta, fh, indent=1)
    return meta


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "export":
        export(sys.argv[2], sys.argv[3])
    else:
        sys.exit(__doc__)
