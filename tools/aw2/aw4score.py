#!/usr/bin/env python3
"""Scores AutoWarpEval --aw4 results (docs/specs/neo-audio.md, AW4 as built): AW1 (DSP), AW2 as shipped, AW3's candidate and the pretrained network (aw4: its own beats;
aw4_r25: with the beats moved to the attacks within 25 ms), per set: beat F at +-25 and +-70 ms, downbeat F at +-70 ms, bar 1 correct, the grid's median offset against strong onsets,
the analysis time, and the gates of the brief.

    aw4score.py results.json [more.json ...] --out report.md [--csv per-file.csv] [--half test] [--clips a,b,c] [--seen gmd,guitarset,candombe]

Bar 1 correct: the first annotated downbeat at or after 5 s has a downbeat of the grid within 70 ms (the grid's bar numbering is right where the first bar that counts starts).
The first 5 s do not count for any figure (AW1's rule). `--seen`: sets the pretrained network was trained on (the upstream annotation repository lists them): marked in the tables.
"""
import argparse, collections, json
import numpy as np
import aw3score as s3

METHODS = ["aw1", "aw2", "aw3", "aw4", "aw4_old", "aw4_plain", "aw4_sub", "aw4_s6", "aw4_s10", "aw4_s14", "aw4_s10_r15", "aw4_s10_r25", "aw4_r25", "bt_raw", "bt_raw_old"]


def bar1(gd, down):
    g = s3.trim(gd)
    if len(g) == 0:
        return None
    if len(down) == 0:
        return 0.0
    return 1.0 if np.min(np.abs(np.asarray(down, float) - g[0])) <= 0.070 else 0.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("results", nargs="+"); ap.add_argument("--out", default="aw4-report.md"); ap.add_argument("--csv")
    ap.add_argument("--half", default="test"); ap.add_argument("--clips", default="aam_1006,gs_04_Funk1-114-Ab_comp,gmd_drummer5_session2_2")
    ap.add_argument("--seen", default="gmd,guitarset,candombe,ballroom")
    a = ap.parse_args()
    seen = set(a.seen.split(","))
    items = []
    for p in a.results:
        items += json.load(open(p))
    items = [i for i in items if i["half"] in a.half.split(",")]
    agg = collections.defaultdict(lambda: collections.defaultdict(list))
    per = []
    for it in items:
        gt = sorted(set(it["gt_beats"])); gd = sorted(set(it["gt_downbeats"]))
        for m in METHODS:
            r = it["res"].get(m)
            if r is None:
                continue
            g = agg[(it["set"], m)]
            g["ms"].append(it["ms"].get(m, float("nan"))); g["sec"].append(it["seconds"])
            if not r["found"]:
                for t in s3.THR:
                    g[f"F{int(t * 1000)}"].append(0.0)
                g["down"].append(0.0); g["bar1"].append(0.0)
                per.append((it["set"], it["name"], m, 0, 0, 0, 0, 0, 0))
                continue
            est = sorted(set(r["beats"])); dn = sorted(set(r["downbeats"]))
            f25, f70 = s3.fscore(gt, est, 0.025), s3.fscore(gt, est, 0.07)
            d = s3.fscore(gd, dn, 0.07)
            b1 = bar1(gd, dn)
            for k, v in (("F25", f25), ("F70", f70), ("down", d), ("bar1", b1)):
                if v is not None:
                    g[k].append(v)
            g["markers"].append(r["markers"]); g["steady"].append(1.0 if r["steady"] else 0.0)
            g["off"].append(s3.offsets(gt, est))
            if "onsets" in it:
                g["onoff"].append(s3.onset_offsets(est, it["onsets"], 3.0))
            per.append((it["set"], it["name"], m, r["bpm"], f25, f70, d, b1, r["markers"]))
    mean = lambda xs: float(np.mean(xs)) if len(xs) else float("nan")
    f = lambda v: "-" if v != v else f"{v:.3f}"
    sets = sorted({k[0] for k in agg})
    out = ["# AW4 measurements\n", f"Halves: {a.half}. Beat F at +-25 and +-70 ms, downbeat F at +-70 ms, bar 1 correct (share of files), the median offset of the grid against strong onsets (ms, grid minus onset), "
           "markers per file, mean analysis time per file (ms) and per second of audio (ms; the network's first file includes making the session). Sets marked * are in the pretrained network's own training data "
           "(its annotation repository lists Groove MIDI, GuitarSet, Candombe and Ballroom): its figures there are not a fair test; AAM and GTZAN are not in it.\n",
           "| set | method | files | F25 | F70 | down F | bar 1 | on-onset median | markers | ms/file | ms per s |", "|---|---|---|---|---|---|---|---|---|---|---|"]
    for s in sets:
        for m in METHODS:
            g = agg.get((s, m))
            if not g:
                continue
            on = np.concatenate(g["onoff"]) if g.get("onoff") else np.zeros(0)
            med = f"{np.median(on):+.1f}" if len(on) else "-"
            ms = mean(g["ms"]); per_s = mean([x / y for x, y in zip(g["ms"], g["sec"]) if y])
            out.append(f"| {s}{'*' if s in seen else ''} | {m} | {len(g['F70'])} | {f(mean(g['F25']))} | {f(mean(g['F70']))} | {f(mean(g['down']))} | {f(mean(g['bar1']))} | {med} | {mean(g.get('markers', [])):.1f} | {ms:.0f} | {per_s:.1f} |")
    out += ["\n## Gates (aw4 against AW2 as shipped)\n", "| set | down F aw4 | down F aw2 | down F higher | beat F70 aw4 | beat F70 aw2 | beat F70 at least |", "|---|---|---|---|---|---|---|"]
    allok = True
    for s in sets:
        g4, g2 = agg.get((s, "aw4")), agg.get((s, "aw2"))
        if not g4 or not g2:
            continue
        d4, d2, b4, b2 = mean(g4["down"]), mean(g2["down"]), mean(g4["F70"]), mean(g2["F70"])
        ok1, ok2 = d4 > d2, b4 >= b2 - 1e-9
        allok &= ok1 and ok2
        out.append(f"| {s}{'*' if s in seen else ''} | {f(d4)} | {f(d2)} | {'yes' if ok1 else 'NO'} | {f(b4)} | {f(b2)} | {'yes' if ok2 else 'NO'} |")
    out.append(f"\nAccuracy gate over these sets: {'met' if allok else 'NOT met'}.\n")
    clips = [c for c in a.clips.split(",") if c]
    if clips:
        out += ["## The clips both methods failed in round 2\n", "| clip | method | tempo | beat F70 | down F | bar 1 correct | markers |", "|---|---|---|---|---|---|---|"]
        for c in clips:
            for r in per:
                if r[1] == c:
                    out.append(f"| {c} | {r[2]} | {r[3]:.1f} | {f(r[5])} | {f(r[6])} | {'yes' if r[7] == 1 else 'no' if r[7] == 0 else '-'} | {r[8]} |")
    open(a.out, "w").write("\n".join(out) + "\n")
    print("\n".join(out))
    if a.csv:
        with open(a.csv, "w") as fh:
            fh.write("set,name,method,bpm,F25,F70,downF,bar1,markers\n")
            for r in per:
                fh.write(",".join("" if v is None or (isinstance(v, float) and v != v) else (f"{v:.4f}" if isinstance(v, float) else str(v)) for v in r) + "\n")


if __name__ == "__main__":
    main()
