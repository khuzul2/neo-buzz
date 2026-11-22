"""AW2 beat and downbeat network (docs/specs/neo-audio.md, AW2 as built). Clean room, from the literature:
a small convolutional front end over the log-mel spectrogram (cf. Boeck and Davies 2019/2020; Foscarin et al. 2024 "Beat This!") and a stack of dilated
temporal convolution blocks (a temporal convolutional network, Boeck and Davies), with three outputs: beat and downbeat activations per frame (50 fps) and
a tempo distribution per excerpt (multi-task, as Boeck and Davies).  The C# forward pass (ReBuzz/Core/Clips/BeatModelNet.cs) is an exact port of Net.forward.

Input  x: (B, T, 128) float, the dequantised 8-bit log-mel (BeatFront.cs).
Graph  (every convolution is 'same'-padded with zeros along time):
  x/4 -> conv2d 1->C1 3x3, GELU, maxpool freq /4 -> conv2d C1->C2 3x3, GELU, maxpool freq /4 -> conv2d C2->C3 3x3, GELU
      -> flatten (C3 * 8) -> linear -> D -> LayerNorm
  repeat blocks with dilation d: y = x + pw2(GELU(pw1(LN(depthwise_k(x, d)))))
  -> LayerNorm -> beat/downbeat logits (linear D->2)    and    tempo logits (mean over time of the features -> linear D->NT)
GELU is the tanh approximation. LayerNorm eps 1e-5, over channels.
"""
import math
import torch
import torch.nn as nn
import torch.nn.functional as F

MELS = 128
FPS = 50.0
BPM_MIN, BPM_MAX, TEMPO_BINS = 55.0, 215.0, 68      # tempo bins, log spaced (about 2 % apart)
DEFAULT = dict(c1=8, c2=16, c3=32, d=64, k=5, dil=[1, 2, 4, 8, 16, 32, 64, 128, 256], ratio=2)


def bpm_of_bin(i):
    return BPM_MIN * math.exp(math.log(BPM_MAX / BPM_MIN) * i / (TEMPO_BINS - 1))


class LN(nn.Module):
    """LayerNorm over the channel dim of (B, C, T)."""
    def __init__(self, c):
        super().__init__()
        self.ln = nn.LayerNorm(c, eps=1e-5)

    def forward(self, x):
        return self.ln(x.transpose(1, 2)).transpose(1, 2)


class Block(nn.Module):
    def __init__(self, d, k, dil, ratio):
        super().__init__()
        self.dw = nn.Conv1d(d, d, k, padding=dil * (k // 2), dilation=dil, groups=d)
        self.ln = LN(d)
        self.p1 = nn.Conv1d(d, d * ratio, 1)
        self.p2 = nn.Conv1d(d * ratio, d, 1)

    def forward(self, x):
        y = self.ln(self.dw(x))
        y = self.p2(F.gelu(self.p1(y), approximate="tanh"))
        return x + y


class Net(nn.Module):
    def __init__(self, c1=8, c2=16, c3=32, d=64, k=5, dil=None, ratio=2):
        super().__init__()
        self.cfg = dict(c1=c1, c2=c2, c3=c3, d=d, k=k, dil=list(dil or DEFAULT["dil"]), ratio=ratio)
        self.conv1 = nn.Conv2d(1, c1, 3, padding=1)
        self.conv2 = nn.Conv2d(c1, c2, 3, padding=1)
        self.conv3 = nn.Conv2d(c2, c3, 3, padding=1)
        self.proj = nn.Linear(c3 * (MELS // 16), d)
        self.ln0 = LN(d)
        self.blocks = nn.ModuleList([Block(d, k, di, ratio) for di in self.cfg["dil"]])
        self.lnf = LN(d)
        self.head = nn.Conv1d(d, 2, 1)
        self.tempo = nn.Linear(d, TEMPO_BINS)

    def features(self, x):                      # x: (B, T, 128)
        B, T, _ = x.shape
        h = (x / 4.0).unsqueeze(1).transpose(2, 3)          # (B, 1, 128, T): channels, freq, time
        h = F.max_pool2d(F.gelu(self.conv1(h), approximate="tanh"), (4, 1))
        h = F.max_pool2d(F.gelu(self.conv2(h), approximate="tanh"), (4, 1))
        h = F.gelu(self.conv3(h), approximate="tanh")       # (B, C3, 8, T)
        h = h.permute(0, 3, 1, 2).reshape(B, T, -1)          # flatten in (channel, freq) order
        h = self.proj(h).transpose(1, 2)                     # (B, D, T)
        h = self.ln0(h)
        for b in self.blocks:
            h = b(h)
        return self.lnf(h)

    def forward(self, x):
        h = self.features(x)
        beat = self.head(h)                                  # (B, 2, T)
        tempo = self.tempo(h.mean(dim=2))                    # (B, NT)
        return beat, tempo


def count(net):
    return sum(p.numel() for p in net.parameters())


def macs_per_frame(cfg):
    c1, c2, c3, d, k, ratio = cfg["c1"], cfg["c2"], cfg["c3"], cfg["d"], cfg["k"], cfg["ratio"]
    m = MELS * 9 * c1 + (MELS // 4) * 9 * c1 * c2 + (MELS // 16) * 9 * c2 * c3 + c3 * (MELS // 16) * d
    m += len(cfg["dil"]) * (d * k + 2 * d * d * ratio) + 2 * d
    return m


if __name__ == "__main__":
    import time
    torch.manual_seed(0)
    for cfg in (dict(), dict(d=96), dict(d=96, c1=12, c2=24, c3=48)):
        net = Net(**cfg)
        c = net.cfg
        x = torch.randn(16, 1500, MELS)
        opt = torch.optim.AdamW(net.parameters(), 1e-3)
        t = time.time()
        for _ in range(3):
            b, tp = net(x)
            loss = b.square().mean() + tp.square().mean()
            opt.zero_grad(); loss.backward(); opt.step()
        dt = (time.time() - t) / 3
        print(cfg, "params", count(net), "MAC/frame", macs_per_frame(c), "step(16x30s) %.2fs" % dt, "threads", torch.get_num_threads())
