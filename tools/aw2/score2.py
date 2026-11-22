#!/usr/bin/env python3
"""Scores AutoWarpEval results for AW2 (docs/specs/neo-audio.md, AW2 as built): AW1's DSP tracker (`auto`) against the beat model (`aw2`) per set on the held-out items,
with the metrics of AW1's scorer (beat F at +-70 ms, CMLt, AMLt, downbeat F, grid error) and the AW2 gates.

    score2.py results.json [more.json ...] --out report.md [--csv per-file.csv]
"""
import argparse, collections, json, os, sys
import numpy as np
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "autowarp"))
from score import evaluate, mean, pct, fmt  # noqa: E402

GATES = {  # set -> (beat F, downbeat F or None)
    "gmd": (0.90, 0.70), "guitarset": (0.88, 0.70), "candombe": (0.85, None), "aam": (0.90, 0.80), "slakh": (0.90, 0.80), "fsl10k": (None, None),
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("results", nargs="+"); ap.add_argument("--out", default="aw2-report.md"); ap.add_argument("--csv")
    a = ap.parse_args()
    items = []
    for p in a.results:
        items += json.load(open(p))
    rows = collections.defaultdict(list)
    per = []
    for it in items:
        # Labels from the sets can repeat or be out of order (an annotator's duplicate, a tie in a hash): mir_eval wants them strictly increasing.
        for key in ("gt_beats", "gt_downbeats"):
            it[key] = sorted(set(it[key]))
        for method in ("auto", "aw2"):
            if method in it:
                for key in ("beats", "downbeats"):
                    it[method][key] = sorted(set(it[method][key]))
        for method in ("auto", "aw2"):
            if method not in it:
                continue
            e = evaluate(it, method)
            if e is None:
                continue
            rows[(it["set"], it["half"], method)].append(e)
            per.append((it["set"], it["half"], method, e))
    out = ["# AW2: AW1 DSP against the beat model (with the DSP-or-model choice), held-out items\n",
           "`aw1` is AW1's DSP tracker, `aw2` the beat model through the same decoder path (AutoWarp), with AW2b's choice of the DSP track when it is steady and the model agrees. Beat F at +-70 ms, CMLt, AMLt, downbeat F (the first 5 s do not count), grid error (ms at 120 BPM). `test` = held out (never trained on), `val` = the validation split (thresholds were set on it), `dev` = training items, for reference (not scored in gates).\n",
           "| set | half | method | files | beat F | CMLt | AMLt | downbeat F | grid median ms | grid p95 ms |", "|---|---|---|---|---|---|---|---|---|---|"]
    name = {"auto": "aw1", "aw2": "aw2"}
    for s in sorted({k[0] for k in rows}):
        for half in ("test", "val", "dev"):
            for m in ("auto", "aw2"):
                es = rows.get((s, half, m), [])
                if not es:
                    continue
                g = [e["grid"] for e in es]
                out.append(f"| {s} | {half} | {name[m]} | {len(es)} | {fmt(mean([e['F'] for e in es]))} | {fmt(mean([e['CMLt'] for e in es]))} | {fmt(mean([e['AMLt'] for e in es]))} | {fmt(mean([e['down'] for e in es]))} | {fmt(pct(g, 50), 1)} | {fmt(pct(g, 95), 1)} |")
    out += ["\n## Gates (test halves, AW2 model)\n", "| gate | AW1 | AW2 | needed | met |", "|---|---|---|---|---|"]
    for s, (bf, df) in GATES.items():
        e2, e1 = rows.get((s, "test", "aw2"), []), rows.get((s, "test", "auto"), [])
        if not e2:
            continue
        if bf is not None:
            v1, v2 = mean([e["F"] for e in e1]), mean([e["F"] for e in e2])
            out.append(f"| {s}: beat F | {fmt(v1)} | {fmt(v2)} | >= {bf} | {'yes' if v2 >= bf else 'NO'} |")
        if df is not None:
            v1, v2 = mean([e["down"] for e in e1]), mean([e["down"] for e in e2])
            out.append(f"| {s}: downbeat F | {fmt(v1)} | {fmt(v2)} | >= {df} | {'yes' if v2 >= df else 'NO'} |")
    la = {e["name"]: e for e in rows.get(("loops", "all", "auto"), [])}
    lb = {e["name"]: e for e in rows.get(("loops", "all", "aw2"), [])}
    if lb:
        worst = max(abs(lb[k]["bpm"] - la[k]["bpm"]) for k in lb if k in la)
        out.append(f"\nSteady loops (whole bars, 4 tempos): largest tempo difference between AW2 and AW1: {worst:.3f} BPM (gate: within 0.1 of the true tempo).")
    gen = [e for e in rows.get(("generated", "test", "aw2"), []) if e["name"].startswith(("drift", "step"))]
    if gen:
        out.append(f"Generated drift and step lines (held out), AW2 grid error p95: {pct([e['grid'] for e in gen], 95):.1f} ms (gate 20); drums and mix lines only: {pct([e['grid'] for e in gen if '-pad-' not in e['name']], 95):.1f} ms.")
    
    open(a.out, "w").write("\n".join(out) + "\n")
    print("\n".join(out))
    if a.csv:
        with open(a.csv, "w") as f:
            f.write("set,half,method,name,beatF,CMLt,AMLt,downF,grid_p95_ms,bpm\n")
            for s, h, m, e in per:
                g = e["grid"]
                f.write(f"{s},{h},{name[m]},{e['name']},{e['F']:.4f},{e['CMLt']:.4f},{e['AMLt']:.4f},{'' if e['down'] is None else format(e['down'], '.4f')},{np.percentile(g, 95) if len(g) else ''},{e['bpm']}\n")


if __name__ == "__main__":
    main()
