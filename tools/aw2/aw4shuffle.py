#!/usr/bin/env python3
"""The blind A/B pack of AW4 round 3: the renders NN-name--aw4.wav and NN-name--aw2.wav of AutoWarpEval --pack4 become NN-a.wav and NN-b.wav.

    aw4shuffle.py RENDER_DIR OUT_DIR [--seed 4] [--list pack-list.txt]

A seeded shuffle puts the new source (aw4) in slot A in exactly half the items (8 of 16, never 7 or 9) and sets the order of the items; KEY.txt (apart from the pack: do not
open it before listening) says per item the source in slot A, the clip and the render tempo. README.txt tells the listener what to do and not what is where.
"""
import argparse, glob, os, random, re, shutil

ap = argparse.ArgumentParser()
ap.add_argument("render"); ap.add_argument("out"); ap.add_argument("--seed", type=int, default=4); ap.add_argument("--list", default=None)
a = ap.parse_args()
tempo = {}
if a.list:
    for line in open(a.list):
        p = line.rstrip("\n").split("\t")
        if len(p) >= 2:
            tempo[os.path.splitext(os.path.basename(p[0]))[0]] = p[1]
clips = sorted({re.match(r"\d+-(.*)--aw[24]\.wav$", os.path.basename(f)).group(1) for f in glob.glob(os.path.join(a.render, "*--aw4.wav"))})
assert len(clips) % 2 == 0, "an even number of clips, so that half can have the new source in A"
rng = random.Random(a.seed)
order = list(clips); rng.shuffle(order)
new_in_a = set(rng.sample(range(len(order)), len(order) // 2))
os.makedirs(a.out, exist_ok=True)
rows = []
for i, clip in enumerate(order, 1):
    src = {k: glob.glob(os.path.join(a.render, f"*-{clip}--{k}.wav"))[0] for k in ("aw4", "aw2")}
    first = "aw4" if (i - 1) in new_in_a else "aw2"
    second = "aw2" if first == "aw4" else "aw4"
    shutil.copy(src[first], os.path.join(a.out, f"{i:02d}-a.wav")); shutil.copy(src[second], os.path.join(a.out, f"{i:02d}-b.wav"))
    rows.append(f"{i:02d}\tA={'new source' if first == 'aw4' else 'AW2 as shipped'}\t{clip}\t{tempo.get(clip, '?')} BPM")
assert sum("new source" in r.split("\t")[1] for r in rows) == len(rows) // 2
open(os.path.join(a.out, "KEY.txt"), "w").write("NN\tslot A\tclip\trender tempo\n" + "\n".join(rows) + "\n")
open(os.path.join(a.out, "README.txt"), "w").write(
    "Auto-warp listening pack, round 3.\n\nFor each item NN, NN-a.wav and NN-b.wav are the same 40 s of music played at the same song tempo against a click on the grid the\n"
    "auto-warp found (a higher, louder tick on each bar line). One of them is the new beat source and the other is the one that ships now; which is which is in KEY.txt, which you\n"
    "should not open until you have chosen. For each item say: A is better, B is better, same, or neither (both off). Better = the click sits on the beat of the music, and the\n"
    "bar tick on the first beat of the bar.\nSeed %d; the new source is in slot A in %d of %d items.\n" % (a.seed, len(rows) // 2, len(rows)))
print(open(os.path.join(a.out, "KEY.txt")).read())
