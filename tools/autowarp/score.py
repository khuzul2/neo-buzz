#!/usr/bin/env python3
"""Scores AutoWarpEval's results (docs/specs/neo-audio.md, AW1 as built): beat F-measure at +-70 ms, CMLt and AMLt (mir_eval, MIT), the downbeat
F-measure at +-70 ms, and the outcome: the median and the 95th percentile of the grid error, for auto-warp against today's steady-tempo detection,
per set and per half (development: where the tuning was done; test: held out, the numbers to report), with the gates of the brief.

    score.py results.json [more.json ...] [--out report.md] [--csv per-file.csv]
"""
import argparse, json, collections, statistics
import numpy as np
import mir_eval

GATES = {
    "generated": dict(beat=0.95, down=0.80),
    "slakh": dict(beat=0.95, down=0.80),
    "gmd": dict(beat=0.90, down=None),
}


def f_down(ref, est):
    ref = np.asarray(ref, float); est = np.asarray(est, float)
    ref = ref[ref >= 5.0]; est = est[est >= 5.0]          # as the beat metrics: the first 5 s do not count
    if len(ref) == 0:
        return None
    if len(est) == 0:
        return 0.0
    return float(mir_eval.beat.f_measure(ref, est, f_measure_threshold=0.07))


def beat_scores(ref, est):
    ref = mir_eval.beat.trim_beats(np.asarray(ref, float)); est = mir_eval.beat.trim_beats(np.asarray(est, float))
    if len(ref) < 2:
        return None
    if len(est) < 2:
        return dict(F=0.0, CMLt=0.0, AMLt=0.0)
    f = mir_eval.beat.f_measure(ref, est, f_measure_threshold=0.07)
    cmlc, cmlt, amlc, amlt = mir_eval.beat.continuity(ref, est)
    return dict(F=float(f), CMLt=float(cmlt), AMLt=float(amlt))


def evaluate(item, method):
    m = item[method]
    b = beat_scores(item["gt_beats"], m["beats"])
    if b is None:
        return None
    d = f_down(item["gt_downbeats"], m["downbeats"])
    g = np.asarray(m["grid_ms"], float)
    return dict(b, down=d, grid=g, bpm=m.get("bpm", 0), name=item["name"], half=item["half"], set=item["set"], ms=item.get("analysis_ms", 0), steady=m.get("steady"), markers=m.get("markers"))


def mean(xs):
    xs = [x for x in xs if x is not None]
    return float(np.mean(xs)) if xs else float("nan")


def pct(a, q):
    return float(np.percentile(np.concatenate(a), q)) if a and sum(len(x) for x in a) else float("nan")


def fmt(v, p=3):
    return "-" if v != v else f"{v:.{p}f}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("results", nargs="+")
    ap.add_argument("--out", default="autowarp-report.md")
    ap.add_argument("--csv")
    a = ap.parse_args()
    items = []
    for p in a.results:
        items += json.load(open(p))
    rows = collections.defaultdict(list)           # (set, half, method) -> evaluations
    perfile = []
    for it in items:
        for method in ("today", "auto"):
            e = evaluate(it, method)
            if e is None:
                continue
            rows[(it["set"], it["half"], method)].append(e)
            rows[(it["set"], "all", method)].append(e)
            perfile.append((it["set"], it["half"], method, e))
    out = ["# Auto-warp (AW1): beats, downbeats and the grid error\n",
           "Beat F-measure at +-70 ms, CMLt and AMLt (mir_eval), downbeat F-measure at +-70 ms (the first 5 s do not count, as the beat metrics have it), and the grid error: how far each ground-truth beat lands from the nearest beat of the song's grid after warping, in ms at 120 BPM (median and 95th percentile over all beats of the set). `today` is the steady-tempo detection of the import (a straight grid from the start of the file); `auto` is auto-warp. `dev` is the half the tuning used, `test` the held-out half: report test.\n",
           "| set | half | method | files | beat F | CMLt | AMLt | downbeat F | grid median ms | grid p95 ms |", "|---|---|---|---|---|---|---|---|---|---|"]
    sets = sorted({k[0] for k in rows})
    for s in sets:
        for half in ("dev", "test", "all"):
            for method in ("today", "auto"):
                es = rows.get((s, half, method), [])
                if not es:
                    continue
                g = [e["grid"] for e in es]
                out.append(f"| {s} | {half} | {method} | {len(es)} | {fmt(mean([e['F'] for e in es]))} | {fmt(mean([e['CMLt'] for e in es]))} | {fmt(mean([e['AMLt'] for e in es]))} | {fmt(mean([e['down'] for e in es]))} | {fmt(pct(g, 50), 1)} | {fmt(pct(g, 95), 1)} |")
    gen = [(h, m, e) for (s, h, m, e) in perfile if s == "generated"]
    if gen:
        out += ["\n## Generated lines\n", "| line | half | today F | auto F | today down | auto down | today grid p95 | auto grid p95 | auto steady | auto markers | auto BPM | analysis ms |", "|---|---|---|---|---|---|---|---|---|---|---|---|"]
        byname = collections.defaultdict(dict)
        for h, m, e in gen:
            byname[(e["name"], h)][m] = e
        for (name, h), d in sorted(byname.items()):
            t, u = d.get("today"), d.get("auto")
            if not t or not u:
                continue
            p95 = lambda e: float(np.percentile(e["grid"], 95)) if len(e["grid"]) else float("nan")
            out.append(f"| {name} | {h} | {fmt(t['F'], 2)} | {fmt(u['F'], 2)} | {fmt(t['down'], 2)} | {fmt(u['down'], 2)} | {fmt(p95(t), 1)} | {fmt(p95(u), 1)} | {u['steady']} | {u['markers']} | {u['bpm']:.2f} | {u['ms']:.0f} |")
    out += ["\n## Gates (held-out half)\n", "| gate | measured | needed | met |", "|---|---|---|---|"]

    def gate(label, v, need, ge=True):
        ok = v == v and (v >= need if ge else v <= need)
        out.append(f"| {label} | {fmt(v, 3 if ge else 1)} | {'>=' if ge else '<='} {need} | {'yes' if ok else 'NO'} |")
    for s, g in GATES.items():
        es = rows.get((s, "test", "auto"), [])
        if not es:
            continue
        gate(f"{s}: beat F", mean([e["F"] for e in es]), g["beat"])
        if g["down"] is not None:
            gate(f"{s}: downbeat F", mean([e["down"] for e in es]), g["down"])
        else:
            out.append(f"| {s}: downbeat F (reported, no gate) | {fmt(mean([e['down'] for e in es]))} | - | - |")
    es = [e for e in rows.get(("generated", "test", "auto"), []) if e["name"].startswith(("drift", "step"))]
    if es:
        gate("generated drift and step lines, all styles: grid error p95 (ms)", pct([e["grid"] for e in es], 95), 20, ge=False)
        gate("  of which drums and mix lines (ritardando included): grid error p95 (ms)", pct([e["grid"] for e in es if "-pad-" not in e["name"]], 95), 20, ge=False)
        out.append(f"| (reported) pad lines (weak onsets): grid error p95 (ms) | {fmt(pct([e['grid'] for e in es if '-pad-' in e['name']], 95), 1)} | - | - |")
    es = [e for e in rows.get(("loops", "all", "auto"), [])]
    ts = {e["name"]: e for e in rows.get(("loops", "all", "today"), [])}
    if es:
        worst = max(abs(e["bpm"] - ts[e["name"]]["bpm"]) for e in es if e["name"] in ts)
        gate("steady loops (whole bars, 4 tempos): largest tempo difference from today's (BPM)", worst, 0.1, ge=False)
    ms = [e["ms"] for (s, h, m, e) in perfile if m == "auto"]
    if ms:
        out.append(f"\nAnalysis time per file (auto-warp, whole file, on this machine): median {statistics.median(ms):.0f} ms, worst {max(ms):.0f} ms.")
    open(a.out, "w").write("\n".join(out) + "\n")
    print("\n".join(out))
    if a.csv:
        with open(a.csv, "w") as f:
            f.write("set,half,method,name,beatF,CMLt,AMLt,downF,grid_median_ms,grid_p95_ms,bpm\n")
            for s, h, m, e in perfile:
                g = e["grid"]
                f.write(f"{s},{h},{m},{e['name']},{e['F']:.4f},{e['CMLt']:.4f},{e['AMLt']:.4f},{'' if e['down'] is None else format(e['down'], '.4f')},{np.median(g) if len(g) else ''},{np.percentile(g, 95) if len(g) else ''},{e['bpm']}\n")


if __name__ == "__main__":
    main()
