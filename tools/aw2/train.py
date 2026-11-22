#!/usr/bin/env python3
"""Trains the AW2 beat and downbeat network (model.py) on the shards of prep.py. CPU only, fixed seed, time-boxed (checkpoints so a chained job resumes).

    train.py SHARD_DIR [SHARD_DIR ...] --out ckpt.pt --minutes 55 [--resume] [--d 96]

Excerpts of 30 s are cropped at random; the datasets are sampled in equal shares (then an item by its length), so the large synthetic set does not drown the human ones.
Targets: a 1 at the frame of each beat (and downbeat) at 50 fps; a Gaussian over the tempo bins for the excerpt's median beat interval.
Loss (clean room, after the idea of the shift-tolerant loss of Foscarin et al. 2024): the beat and downbeat logits, weighted binary cross-entropy where a positive is scored
on the highest logit within one frame of it (a prediction one frame off is not wrong) and the frames next to a positive are not scored as negatives; plus a tempo cross-entropy.
"""
import argparse, collections, glob, math, os, random, time
import numpy as np
import torch
import torch.nn.functional as F
from model import Net, FPS, TEMPO_BINS, BPM_MIN, BPM_MAX, MELS

EXC = 1500


def load(dirs):
    items = []
    for d in dirs:
        for p in sorted(glob.glob(os.path.join(d, "*.npz"))):
            z = np.load(p)
            if len(z["beats"]) < 4 or z["X"].shape[0] < 300:
                continue
            items.append(dict(X=z["X"], beats=z["beats"], downs=z["downbeats"], ds=str(z["dataset"]), bpb=int(z["bpb"])))
    return items


def tempo_target(beats):
    b = np.sort(beats)
    if len(b) < 3:
        return None
    bpm = 60.0 / np.median(np.diff(b))
    if not (BPM_MIN * 0.95 <= bpm <= BPM_MAX * 1.05):
        return None
    pos = math.log(bpm / BPM_MIN) / math.log(BPM_MAX / BPM_MIN) * (TEMPO_BINS - 1)
    g = np.exp(-0.5 * ((np.arange(TEMPO_BINS) - pos) / 1.2) ** 2)
    return (g / g.sum()).astype(np.float32)


def batch(items, by_ds, rng, B):
    X = np.zeros((B, EXC, MELS), np.float32)
    Y = np.zeros((B, 2, EXC), np.float32)
    mask = np.zeros((B, EXC), np.float32)
    TT = np.zeros((B, TEMPO_BINS), np.float32)
    TM = np.zeros(B, np.float32)
    dss = list(by_ds)
    for i in range(B):
        pool = by_ds[dss[rng.integers(len(dss))]]
        lens = np.array([items[j]["X"].shape[0] for j in pool], float)
        it = items[pool[rng.choice(len(pool), p=lens / lens.sum())]]
        T = it["X"].shape[0]
        a = int(rng.integers(0, max(1, T - EXC + 1)))
        n = min(EXC, T - a)
        X[i, :n] = it["X"][a:a + n] / 36.0
        mask[i, :n] = 1
        t0, t1 = a / FPS, (a + n) / FPS
        for c, arr in enumerate((it["beats"], it["downs"])):
            f = np.round((arr[(arr >= t0) & (arr < t1)] - t0) * FPS).astype(int)
            f = f[(f >= 0) & (f < n)]
            Y[i, c, f] = 1
        tb = it["beats"][(it["beats"] >= t0) & (it["beats"] < t1)]
        tt = tempo_target(tb)
        if tt is not None and len(tb) >= 6:
            TT[i] = tt; TM[i] = 1
        # masks over the spectrogram (small): one band of up to 12 mel bins, one stretch of up to 15 frames
        if rng.random() < 0.5:
            w = int(rng.integers(1, 13)); f0 = int(rng.integers(0, MELS - w)); X[i, :, f0:f0 + w] = 0
        if rng.random() < 0.3:
            w = int(rng.integers(1, 16)); t = int(rng.integers(0, max(1, n - w))); X[i, t:t + w, :] = 0
    return torch.from_numpy(X), torch.from_numpy(Y), torch.from_numpy(mask), torch.from_numpy(TT), torch.from_numpy(TM)


def beat_loss(logits, Y, mask):
    """logits (B, 2, T), Y (B, 2, T) of 0/1."""
    pooled = F.max_pool1d(logits, 3, 1, 1)
    near = F.max_pool1d(Y, 3, 1, 1)                       # 1 within a frame of a positive
    pos_w = 4.0
    pos = Y * pos_w * F.softplus(-pooled)                  # -log sigmoid(pooled) at the positives
    neg = (1 - near) * F.softplus(logits)                  # -log(1 - sigmoid) away from the positives
    m = mask[:, None, :]
    return ((pos + neg) * m).sum() / (m.sum() * 2)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("shards", nargs="+"); ap.add_argument("--out", required=True); ap.add_argument("--minutes", type=float, default=55)
    ap.add_argument("--resume", action="store_true"); ap.add_argument("--d", type=int, default=64); ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--lr", type=float, default=2e-3); ap.add_argument("--seed", type=int, default=1234); ap.add_argument("--total-minutes", type=float, default=0)
    a = ap.parse_args()
    torch.manual_seed(a.seed); random.seed(a.seed)
    torch.set_num_threads(os.cpu_count() or 2)
    items = load(a.shards)
    by_ds = collections.defaultdict(list)
    for i, it in enumerate(items):
        by_ds[it["ds"]].append(i)
    hours = sum(it["X"].shape[0] for it in items) / FPS / 3600
    print(f"{len(items)} shards, {hours:.1f} h of features, datasets {({k: len(v) for k, v in by_ds.items()})}, threads {torch.get_num_threads()}", flush=True)
    net = Net(d=a.d)
    opt = torch.optim.AdamW(net.parameters(), a.lr, weight_decay=1e-2)
    elapsed, step = 0.0, 0
    if a.resume and os.path.exists(a.out):
        ck = torch.load(a.out, map_location="cpu")
        net.load_state_dict(ck["state"]); opt.load_state_dict(ck["opt"]); elapsed, step = ck["elapsed"], ck["step"]
        print(f"resumed at step {step}, {elapsed / 60:.1f} min", flush=True)
    total = (a.total_minutes or a.minutes) * 60
    rng = np.random.default_rng(a.seed + step)
    t_start = time.time() - 0.0
    base = elapsed
    last_save = time.time()
    run = collections.deque(maxlen=50)
    while True:
        now = base + (time.time() - t_start)
        if now - base >= a.minutes * 60 or now >= total:
            break
        prog = min(1.0, now / total)
        lr = a.lr * (min(1.0, step / 100)) * (0.5 * (1 + math.cos(math.pi * prog)) * 0.97 + 0.03)
        for g in opt.param_groups:
            g["lr"] = lr
        X, Y, mask, TT, TM = batch(items, by_ds, rng, a.batch)
        logits, tempo = net(X)
        lb = beat_loss(logits, Y, mask)
        lt = -(TT * F.log_softmax(tempo, dim=1)).sum(1)
        lt = (lt * TM).sum() / TM.sum().clamp(min=1)
        loss = lb + 0.05 * lt
        opt.zero_grad(); loss.backward()
        torch.nn.utils.clip_grad_norm_(net.parameters(), 1.0)
        opt.step(); step += 1
        run.append((lb.item(), lt.item()))
        if step % 50 == 0:
            m = np.mean(run, axis=0)
            print(f"step {step} {now / 60:.1f} min lr {lr:.5f} beat loss {m[0]:.4f} tempo loss {m[1]:.3f}", flush=True)
        if time.time() - last_save > 300:
            save(a.out, net, opt, base + time.time() - t_start, step); last_save = time.time()
    save(a.out, net, opt, base + time.time() - t_start, step)
    print("done", step, "steps", flush=True)


def save(path, net, opt, elapsed, step):
    torch.save({"cfg": net.cfg, "state": net.state_dict(), "opt": opt.state_dict(), "elapsed": elapsed, "step": step}, path + ".tmp")
    os.replace(path + ".tmp", path)


if __name__ == "__main__":
    main()
