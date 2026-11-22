#!/usr/bin/env python3
"""Chooses the DSP-or-model thresholds on the validation split (never the test halves): for each setting a directory of AutoWarpEval results run with that setting
(DIR/NAME/*.json, NAME = "STEADY_AGREE"), the mean beat F (and downbeat F) of the `aw2` method over the validation items of each set, equal weight per set.
The best setting (highest mean beat F, then downbeat F; ties go to the smaller steady tolerance) is written to --out as JSON {"steady":..,"agree":..,"table":[..]}.

    tune.py RESULTS_DIR --out tune.json
"""
import argparse, glob, json, os, sys
import numpy as np
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "autowarp"))
from score import evaluate  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dir"); ap.add_argument("--out", default="tune.json")
    a = ap.parse_args()
    table = []
    for d in sorted(glob.glob(os.path.join(a.dir, "*_*"))):
        s, g = os.path.basename(d).split("_")
        per = {}
        for p in glob.glob(os.path.join(d, "*.json")):
            for it in json.load(open(p)):
                if it["half"] != "val" or "aw2" not in it:
                    continue
                for key in ("gt_beats", "gt_downbeats"):
                    it[key] = sorted(set(it[key]))
                for key in ("beats", "downbeats"):
                    it["aw2"][key] = sorted(set(it["aw2"][key]))
                e = evaluate(it, "aw2")
                if e:
                    per.setdefault(it["set"], []).append((e["F"], e["down"] if e["down"] is not None else 0.0))
        if not per:
            continue
        f = float(np.mean([np.mean([v[0] for v in vs]) for vs in per.values()]))
        dn = float(np.mean([np.mean([v[1] for v in vs]) for vs in per.values()]))
        table.append({"steady": float(s), "agree": float(g), "beatF": round(f, 4), "downF": round(dn, 4), "sets": {k: round(float(np.mean([v[0] for v in vs])), 4) for k, vs in per.items()}})
        print(table[-1], flush=True)
    best = sorted(table, key=lambda t: (-t["beatF"], -t["downF"], t["steady"]))[0]
    json.dump({"steady": best["steady"], "agree": best["agree"], "table": table}, open(a.out, "w"), indent=1)
    print("chosen:", best["steady"], best["agree"])


if __name__ == "__main__":
    main()
