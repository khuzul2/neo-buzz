#!/usr/bin/env python3
"""Scores AutoWarpEval --aw3 results (docs/specs/neo-audio.md, AW3 as built): per set, half and setting, beat F at +-25, +-40 and +-70 ms, downbeat F at +-70 ms, and the signed
timing offset of the grid's beats against the annotations (median, mean, IQR, histogram) and, where onsets are in the results, against strong onsets.

    aw3score.py results.json [more.json ...] --out report.md [--csv per-file.csv] [--half val] [--hist-sets gmd,aam]

Settings are the keys of each item's `res` (aw1 is AW1's DSP track; r0_pinf_minf is the shipped AW2 path; rN_pP_mM: attack reach N ms, residual bounds p95 P and max M ms).
Offsets: for each annotated beat (the first 5 s do not count) the nearest beat of the grid within 70 ms; offset = grid - annotation, ms (positive: the grid is late).
"""
import argparse, collections, json
import numpy as np
import mir_eval

THR = (0.025, 0.040, 0.070)


def trim(x, t=5.0):
    x = np.asarray(x, float)
    return x[x >= t]


def fscore(ref, est, thr):
    ref, est = trim(ref), trim(est)
    if len(ref) < 2:
        return None
    if len(est) < 2:
        return 0.0
    return float(mir_eval.beat.f_measure(ref, est, f_measure_threshold=thr))


def offsets(ref, est, win=0.070):
    ref, est = trim(ref), np.asarray(est, float)
    if len(est) == 0 or len(ref) == 0:
        return np.zeros(0)
    idx = np.clip(np.searchsorted(est, ref), 1, len(est) - 1)
    cand = np.stack([est[idx - 1], est[idx]], 1)
    d = cand - ref[:, None]
    k = np.abs(d).argmin(1)
    o = d[np.arange(len(ref)), k]
    return o[np.abs(o) <= win] * 1000.0


def onset_offsets(beats, onsets, smin, win=0.060):
    on = np.asarray([t for t, s in onsets if s >= smin], float)
    b = trim(beats)
    if len(on) == 0 or len(b) == 0:
        return np.zeros(0)
    idx = np.clip(np.searchsorted(on, b), 1, len(on) - 1) if len(on) > 1 else np.zeros(len(b), int)
    cand = np.stack([on[np.maximum(idx - 1, 0)], on[idx]], 1)
    d = b[:, None] - cand                       # grid minus onset: positive = the grid is late
    k = np.abs(d).argmin(1)
    o = d[np.arange(len(b)), k]
    return o[np.abs(o) <= win] * 1000.0


def stats(o):
    if len(o) == 0:
        return "-", "-", "-", "-"
    q = np.percentile(o, [25, 50, 75])
    return f"{q[1]:+.1f}", f"{o.mean():+.1f}", f"{q[0]:+.1f} .. {q[2]:+.1f}", f"{len(o)}"


def hist(o):
    bins = np.arange(-70, 71, 10)
    h, _ = np.histogram(o, bins)
    return " ".join(f"{b:+d}:{c}" for b, c in zip(bins[:-1], h))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("results", nargs="+"); ap.add_argument("--out", default="aw3-report.md"); ap.add_argument("--csv")
    ap.add_argument("--half", default=None); ap.add_argument("--hist-sets", default="")
    ap.add_argument("--onset-min", type=float, default=3.0)
    a = ap.parse_args()
    items = []
    for p in a.results:
        items += json.load(open(p))
    if a.half:
        items = [i for i in items if i["half"] in a.half.split(",")]
    agg = collections.defaultdict(lambda: collections.defaultdict(list))
    per = []
    for it in items:
        gt = sorted(set(it["gt_beats"])); gd = sorted(set(it["gt_downbeats"]))
        for name, r in [("aw1", it["res"]["aw1"])] + [(k, v) for k, v in it["res"].items() if not k.startswith("model_") and k != "aw1"]:
            key = (it["set"], it["half"], "aw2" if name == "r0_pinf_minf" else name)
            if not r["found"]:
                for t in THR:
                    agg[key][f"F{int(t * 1000)}"].append(0.0)
                agg[key]["down"].append(0.0)
                continue
            est = sorted(set(r["beats"]))
            for t in THR:
                f = fscore(gt, est, t)
                if f is not None:
                    agg[key][f"F{int(t * 1000)}"].append(f)
            d = fscore(gd, sorted(set(r["downbeats"])), 0.07)
            if d is not None:
                agg[key]["down"].append(d)
            o = offsets(gt, est)
            agg[key]["off"].append(o)
            agg[key]["markers"].append(r["markers"]); agg[key]["steady"].append(1.0 if r["steady"] else 0.0)
            if "onsets" in it:
                agg[key]["onoff"].append(onset_offsets(est, it["onsets"], a.onset_min))
            per.append((it["set"], it["half"], key[2], it["name"], fscore(gt, est, 0.025), fscore(gt, est, 0.07), d, float(np.median(o)) if len(o) else float("nan"), r["bpm"], r["markers"]))
        for m in ("peak_off_ms",):
            if m in it:
                agg[(it["set"], it["half"], "~activation peak")]["off"].append(np.asarray(it[m], float))
        if "onsets" in it:
            on = it["onsets"]
            agg[(it["set"], it["half"], "~annotation vs onsets")]["onoff"].append(onset_offsets(gt, on, a.onset_min))                # the annotation as the "grid": annotation minus onset
    mean = lambda xs: float(np.mean(xs)) if xs else float("nan")
    f = lambda v: "-" if v != v else f"{v:.3f}"
    out = ["# AW3 measurements\n", "Beat F at +-25, +-40 and +-70 ms (the first 5 s do not count), downbeat F at +-70 ms, files, mean markers and the share of steady results; offset = grid beat minus annotated beat in ms (positive: late), nearest beat within 70 ms, pooled over the set's annotated beats: median, mean, interquartile range, count.\n",
           "| set | half | setting | files | F25 | F40 | F70 | down F70 | markers | steady | offset median | mean | IQR | n |", "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
    for key in sorted(agg):
        s, h, name = key
        g = agg[key]
        if name.startswith("~"):
            continue
        o = np.concatenate(g["off"]) if g["off"] else np.zeros(0)
        med, mn, iqr, n = stats(o)
        out.append(f"| {s} | {h} | {name} | {len(g['F70'])} | {f(mean(g['F25']))} | {f(mean(g['F40']))} | {f(mean(g['F70']))} | {f(mean(g['down']))} | {mean(g['markers']):.1f} | {mean(g['steady']):.2f} | {med} | {mn} | {iqr} | {n} |")
    out += ["\n## Where the model's own beat activation peaks against the annotations (before any decoding), ms\n", "| set | half | median | mean | IQR | n |", "|---|---|---|---|---|---|"]
    for key in sorted(agg):
        if key[2] == "~activation peak":
            o = np.concatenate(agg[key]["off"]); med, mn, iqr, n = stats(o)
            out.append(f"| {key[0]} | {key[1]} | {med} | {mn} | {iqr} | {n} |")
    out += [f"\n## Grid beats against strong onsets (strength >= {a.onset_min}, within 60 ms), grid minus onset, ms\n", "| set | half | setting | median | mean | IQR | n |", "|---|---|---|---|---|---|---|"]
    for key in sorted(agg):
        if agg[key].get("onoff"):
            o = np.concatenate(agg[key]["onoff"]); med, mn, iqr, n = stats(o)
            out.append(f"| {key[0]} | {key[1]} | {key[2]} | {med} | {mn} | {iqr} | {n} |")
    if a.hist_sets:
        out.append("\n## Histograms of the signed offset against the annotations (10 ms bins, bin start: count)\n")
        for key in sorted(agg):
            if key[0] in a.hist_sets.split(",") and agg[key].get("off") and not key[2].startswith("~"):
                out.append(f"- {key[0]} {key[1]} {key[2]}: " + hist(np.concatenate(agg[key]["off"])))
    open(a.out, "w").write("\n".join(out) + "\n")
    print("\n".join(out))
    if a.csv:
        with open(a.csv, "w") as fh:
            fh.write("set,half,setting,name,F25,F70,downF,offset_median_ms,bpm,markers\n")
            for r in per:
                fh.write(",".join("" if (isinstance(v, float) and v != v) or v is None else (f"{v:.4f}" if isinstance(v, float) else str(v)) for v in r) + "\n")


if __name__ == "__main__":
    main()
