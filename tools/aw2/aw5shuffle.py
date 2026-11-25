#!/usr/bin/env python3
"""The blind A/B pack of AW5 round 5: the renders NN-name--grid.wav and NN-name--beat.wav of AutoWarpEval --pack5 become NN-a.wav and NN-b.wav.

    aw5shuffle.py RENDER_DIR OUT_DIR [--seed 5] [--list pack-list.txt]

A seeded shuffle puts "to grid" in slot A in exactly half the items (4 of 8) and sets the order of the items; KEY.txt (apart from the pack: do not open it before listening) says per item
the version in slot A, the clip and the render tempo. README.txt tells the listener what to do and not what is where.
"""
import argparse, glob, os, random, re, shutil

ap = argparse.ArgumentParser()
ap.add_argument("render"); ap.add_argument("out"); ap.add_argument("--seed", type=int, default=5); ap.add_argument("--list", default=None)
a = ap.parse_args()
tempo = {}
if a.list:
    for line in open(a.list):
        p = line.rstrip("\n").split("\t")
        if len(p) >= 2:
            tempo[os.path.splitext(os.path.basename(p[0]))[0]] = p[1]
clips = sorted({re.match(r"\d+-(.*)--(grid|beat)\.wav$", os.path.basename(f)).group(1) for f in glob.glob(os.path.join(a.render, "*--grid.wav"))})
assert len(clips) % 2 == 0, "an even number of clips, so that half can have 'to grid' in A"
rng = random.Random(a.seed)
order = list(clips); rng.shuffle(order)
grid_in_a = set(rng.sample(range(len(order)), len(order) // 2))
os.makedirs(a.out, exist_ok=True)
rows = []
for i, clip in enumerate(order, 1):
    src = {k: glob.glob(os.path.join(a.render, f"*-{clip}--{k}.wav"))[0] for k in ("grid", "beat")}
    first = "grid" if (i - 1) in grid_in_a else "beat"
    second = "beat" if first == "grid" else "grid"
    shutil.copy(src[first], os.path.join(a.out, f"{i:02d}-a.wav")); shutil.copy(src[second], os.path.join(a.out, f"{i:02d}-b.wav"))
    rows.append(f"{i:02d}\tA={'auto-warp to grid' if first == 'grid' else 'plain auto-warp'}\t{clip}\t{tempo.get(clip, '?')} BPM")
assert sum("to grid" in r.split("\t")[1] for r in rows) == len(rows) // 2
open(os.path.join(a.out, "KEY.txt"), "w").write("NN\tslot A\tclip\trender tempo\n" + "\n".join(rows) + "\n")
open(os.path.join(a.out, "README.txt"), "w").write(
    "Auto-warp listening pack, round 5.\n\nFor each item NN, NN-a.wav and NN-b.wav are the same 15 s of music played at the same song tempo against a click on the grid (a higher, louder\n"
    "tick on each bar line). One of them is plain auto-warp (the markers follow the beat) and the other is auto-warp to grid (the same, then every transient pulled to the 1/16 grid); which is\n"
    "which is in KEY.txt, which you should not open until you have chosen. For each item say: A is better, B is better, same, or neither (both off). Better = the notes sit on the click's\n"
    "grid, without the sound being stretched oddly.\nSeed %d; to grid is in slot A in %d of %d items.\n" % (a.seed, len(rows) // 2, len(rows)))
print(open(os.path.join(a.out, "KEY.txt")).read())
