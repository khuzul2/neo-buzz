#!/usr/bin/env python3
# Fork (docs/specs/mcp-server.md §12.14, U7): the natives with sources, rebuilt, against the binaries songs play
# today. Every build of a machine plays the same cases side by side in one render through ReBuzz.exe --agent:
# effects each fed the same Neo FM6 phrase (a stab, a bass note, a high note, then silence for the tails),
# generators each playing the same notes or triggers; each instance is probed (render's probes), so every one
# hears exactly the same input in the same render. Cases: the defaults, then every global and track parameter
# a quarter and three quarters of the way up its range (raw values, so no manifest decides anything), and the
# defaults again with a quiet input (−40 dB) for what happens near silence.
#
# The builds of a machine, as gear names in the app's Gear (the script copies them in and takes them out again):
#   "<gear> shipped"  the binary upstream ReBuzz ships (RunTime/Gear in the repo): the reference
#   "<gear>"          what this package has (the rebuilt DLL where it ships in place, else the upstream one)
#   "<gear> <label>"  each --variant LABEL=DIR: the DLL tools/build-natives.ps1 built (DIR\<Generators|Effects>)
# Each gets a copy of the machine's manifest, so notes and triggers reach it as they reach the original.
#
# A build matches when every case's samples are bit-identical to the reference's, or differ by at most
# TOLERANCE_DBFS (−100 dBFS, the null test's line across platforms, docs/specs/cross-platform.md §4.3: below a
# 16-bit step, −96 dBFS, so no 16-bit export can tell; rounding differences between compilers, x87 against SSE2
# and C runtime maths show up at −150…−110 dBFS). With --gate, the package's own build of every machine must
# match, else the script fails: what songs hear doesn't change.
#
# A machine whose entry names a source fix (machines.json "fix": state that was never initialised, so two instances of
# upstream's binary could start differently) plays with four instances of upstream's binary ("<gear> shipped",
# "… shipped 2" to "… shipped 4"): each case lists which builds rendered the same bits. Its package build must render
# the same as its other rebuilt instance (the fix: one sound for every instance) and match at least one of upstream's
# instances in every case (the sound an instance of it made).
#
# --pitch (§12.6's "Done when"): every pitched generator among them (its manifest's notes.pitched) plays the written
# pitch through notes_write, its manifest's pitch offset applied as an agent's call applies it: A3, A4 and A5, each its own
# render, heard by analyze's pitch (YIN) or, where that hears none (a 303's squelch), the profiler's harmonic sieve
# (tools/machine_roles.py) on the note's first 0.37 s. A note heard a semitone or more away fails the script.
#
#   python tools/native-compare.py <ReBuzz.exe> <work> [--variant LABEL=DIR ...] [--only gear,...] [--gate]
#                                  [--shipped RunTime/Gear] [--json out.json] [--pitch] [--no-compare]

import hashlib
import importlib.util
import json
import math
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
import machine_roles as mr  # noqa: E402
_spec = importlib.util.spec_from_file_location("nulltest", os.path.join(HERE, "null-test.py"))
nt = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(nt)

TOLERANCE_DBFS = -100.0
TEMPO = 120
BEATS = 4
TAIL = 2.0
SOURCE_NOTES = nt.SOURCE[3]


def natives():
    with open(os.path.join(ROOT, "ExternalMachines", "machines.json"), encoding="utf-8") as f:
        d = json.load(f)
    bt = next(m for m in d["machines"] if m["id"] == "buzztrax-buzzmachines")
    return bt["natives"]


def n(pitch, start, length, velocity=100):
    return {"pitch": pitch, "start": start, "length": length, "velocity": velocity}


GEN_NOTES = [n("A3", 0, 0.75), n("C4", 1, 0.75, 70), n("E4", 2, 0.5, 120), n("A4", 2.5, 0.5, 40), n("A2", 3, 1)]
CHORD = [n("A3", 0, 1.5), n("C4", 0, 1.5), n("E4", 0, 1.5), n("G4", 2, 1.5, 80), n("D4", 2, 1.5, 80)]
KICKS = [n("C3", b, 0.25, 127) for b in (0, 1, 2, 2.5, 3)]


class Run:
    def __init__(self, exe, work):
        self.exe, self.work = exe, work
        self.c = None
        self.started = 0

    def client(self):
        if self.c is None:
            self.started += 1
            self.c = nt.Client(self.exe, self.work, f"server{self.started}")
            self.c.initialize()
        return self.c

    def restart(self):
        if self.c is not None:
            try:
                self.c.close()
            except Exception:
                pass
        self.c = None

    def close(self):
        self.restart()


def describe(c, machine):
    """The machine's global and track parameters with their raw ranges."""
    d = c.call("machine_describe", machine=machine, value_table=True, table_points=2)
    ps = [p for p in d.get("parameters") or [] if p.get("min") is not None]
    return d, [p for p in ps if not p.get("per_track")], [p for p in ps if p.get("per_track")]


def raw_changes(glob, track, frac):
    """Every parameter frac of the way up its raw range; globals by index (FSM Infector repeats names)."""
    out = []
    for p in glob:
        lo, hi = p["min"], p["max"]
        out.append({"param": f"#{p['index']}", "value": f"raw:{lo + int((hi - lo) * frac)}"})
    for p in track:
        lo, hi = p["min"], p["max"]
        out.append({"param": p["name"], "value": f"raw:{lo + int((hi - lo) * frac)}", "track": 0})
    return out


def case_list(kind):
    cases = [("default", None, 0.0)]
    cases += [("quarter", 0.25, 0.0), ("three-quarters", 0.75, 0.0)]
    if kind == "effect":
        cases.append(("quiet-input", None, -40.0))
    return cases


def play_generator(c, machine, spec, trigger, mode):
    c.call("pattern_create", machine=machine, name="P", length_beats=BEATS)
    if trigger:
        args = {"machine": machine, "pattern": "P", "param": trigger["param"], "shape": "step",
                "points": [{"at": b, "value": f"raw:{trigger['value']}"} for b in (0, 1, 2, 2.5, 3)]}
        if trigger.get("track") is not None:
            args["track"] = trigger["track"]
        c.call("automation_write", **args)
    else:
        notes = KICKS if "KickXP" in spec["gear"] else CHORD if "Infector" in spec["gear"] else GEN_NOTES
        c.call("notes_write", machine=machine, pattern="P", notes=notes, input=mode, apply_pitch_offset=False)
    c.call("arrangement_place", machine=machine, pattern="P", at=[0])


def manifest_of(gear_dir, gear):
    p = os.path.join(gear_dir, gear + ".machine.json")
    if not os.path.exists(p):
        return {}
    with open(p, encoding="utf-8-sig") as f:
        return json.load(f)


def render_case(run, spec, builds, case, man):
    """One render of every build of the machine side by side; {build: wav path} or raises."""
    c = run.client()
    name, frac, input_db = case
    kind = "generator" if spec["gearFolder"] == "Generators" else "effect"
    c.call("song_new", discard_unsaved=True, tempo_bpm=TEMPO)
    c.call("song_set", song_end_beats=BEATS)
    names = {}
    if kind == "effect":
        gear, preset, params, notes = nt.SOURCE
        c.call("machine_add", gear=gear, name="Src", connect_to="none")
        c.call("preset_load", machine="Src", preset=preset)
        nt.set_params(c, "Src", params)
        c.call("pattern_create", machine="Src", name="A", length_beats=BEATS)
        c.call("notes_write", machine="Src", pattern="A", notes=notes)
        c.call("arrangement_place", machine="Src", pattern="A", at=[0])
    notes = man.get("notes") or {}
    trigger = (man.get("profile") or {}).get("trigger")
    mode = notes.get("input") if notes.get("input") not in (None, "none") else "tracks"
    for i, (label, gear) in enumerate(builds):
        m = f"B{i}"
        c.call("machine_add", gear=gear, name=m)
        names[label] = m
        if kind == "effect":
            c.call("connect", **{"from": "Src", "to": m, "gain_db": input_db})
    glob = track = None
    for label, m in names.items():
        if frac is not None:
            if glob is None:
                _, glob, track = describe(c, m)
            changes = raw_changes(glob, track, frac)
            if changes:
                c.call("param_set", machine=m, changes=changes, allow_unsafe=True)
        if kind == "generator":
            play_generator(c, m, spec, trigger, mode)
    r = c.call("render", **{"from": 0, "to": BEATS, "tail_seconds": TAIL, "probes": list(names.values()),
                            "name": (spec["gear"] + "-" + name).replace(" ", "_")})
    files = {p["machine"]: p["file"] for p in r.get("probes") or []}
    diverged = {d.get("machine") for d in r.get("diverged") or []}
    out = {}
    for label, m in names.items():
        if m in diverged:
            out[label] = ("diverged", files.get(m))
            c.call("machine_set", machine=m, reset=True)
        else:
            out[label] = ("ok", files.get(m))
    return out


def heard_pitch(c, path, render_id, expected):
    """(MIDI note heard, cents, how): YIN through analyze, else the harmonic sieve on the note's start."""
    try:
        a = c.call("analyze", source=render_id, metrics=["pitch"], from_seconds=0.03, to_seconds=0.45)
        pt = a.get("pitch") or {}
        if pt.get("median_hz") and (pt.get("voiced_percent") or 0) >= 20:
            m = 69 + 12 * math.log2(pt["median_hz"] / 440.0)
            return round(m), round((m - round(m)) * 100, 1), "yin"
    except Exception:
        pass
    rate, ch, x = nt.read_wav(path)
    mono = [x[i] for i in range(0, len(x), ch)]
    binhz, mags = mr.magnitude_spectrum(mono, rate)
    r = mr.pitch_sieve(binhz, mags, expected)
    if r and r.get("pitched"):
        return r["midi"], r["cents"], "sieve"
    return None, None, "none"


NOTE_NAMES = {"A3": 57, "A4": 69, "A5": 81}


def pitch_check(run, spec, man):
    """notes_write as an agent calls it (the manifest's offset applied) on the package's build: the pitch heard per note."""
    c = run.client()
    out = {}
    for name, midi in NOTE_NAMES.items():
        c.call("song_new", discard_unsaved=True, tempo_bpm=TEMPO)
        c.call("song_set", song_end_beats=2)
        c.call("machine_add", gear=spec["gear"], name="G")
        c.call("pattern_create", machine="G", name="P", length_beats=2)
        c.call("notes_write", machine="G", pattern="P", notes=[n(name, 0, 1.5, 100)])
        c.call("arrangement_place", machine="G", pattern="P", at=[0])
        r = c.call("render", **{"from": 0, "to": 2, "tail_seconds": 0.2, "name": (spec["gear"] + "-pitch-" + name).replace(" ", "_")})
        got, cents, how = heard_pitch(c, r["file"], r["id"], midi)
        out[name] = {"expected": midi, "heard": got, "cents": cents, "method": how}
        try:
            os.remove(r["file"])
        except OSError:
            pass
    return out


def diff(ref, got):
    """(identical, max |difference| in dBFS, difference RMS over the reference's in dB, first differing second)."""
    r_rate, r_ch, a = nt.read_wav(ref)
    g_rate, g_ch, b = nt.read_wav(got)
    if (r_rate, r_ch, len(a)) != (g_rate, g_ch, len(b)):
        return False, float("inf"), float("inf"), 0.0
    if a.tobytes() == b.tobytes():
        return True, float("-inf"), float("-inf"), None
    worst, first, e2, s2 = 0.0, None, 0.0, 0.0
    for i in range(len(a)):
        x, y = a[i], b[i]
        d = abs(x - y)
        if math.isnan(d):
            d = float("inf")
        if d > 0 and first is None:
            first = (i // r_ch) / r_rate
        if d > worst:
            worst = d
        e2 += d * d if d != float("inf") else 0
        s2 += x * x
    rel = 10 * math.log10(e2 / s2) if e2 > 0 and s2 > 0 else (float("-inf") if e2 == 0 else float("inf"))
    return False, nt.db(worst) if worst != float("inf") else float("inf"), rel, first


def peak_db(path):
    _, _, a = nt.read_wav(path)
    return nt.db(max((abs(x) for x in a), default=0.0))


def fmt(x):
    if x is None:
        return "-"
    if x == float("-inf"):
        return "identical"
    if x == float("inf"):
        return "inf"
    return f"{x:.1f}"


def main(argv):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except AttributeError:
        pass
    if len(argv) < 2:
        print(__doc__ or "usage: native-compare.py <ReBuzz.exe> <work> [--variant LABEL=DIR] [--only a,b] [--gate]")
        return 2
    exe, work = os.path.abspath(argv[0]), os.path.abspath(argv[1])
    variants, only, gate, shipped_dir, json_out = [], None, False, os.path.join(ROOT, "RunTime", "Gear"), None
    do_pitch, do_compare = False, True
    i = 2
    while i < len(argv):
        a = argv[i]
        if a == "--variant":
            label, _, d = argv[i + 1].partition("=")
            variants.append((label, os.path.abspath(d)))
            i += 2
        elif a == "--only":
            only = set(argv[i + 1].split(","))
            i += 2
        elif a == "--gate":
            gate = True
            i += 1
        elif a == "--shipped":
            shipped_dir = os.path.abspath(argv[i + 1])
            i += 2
        elif a == "--pitch":
            do_pitch = True
            i += 1
        elif a == "--no-compare":
            do_compare = False
            i += 1
        elif a == "--json":
            json_out = os.path.abspath(argv[i + 1])
            i += 2
        else:
            raise SystemExit(f"unknown argument {a}")
    os.makedirs(work, exist_ok=True)
    gear_root = os.path.join(os.path.dirname(exe), "Gear")
    cache = os.path.join(os.environ.get("APPDATA", ""), "ReBuzz", "MachineDLLCache.xml")

    specs = [s for s in natives() if not only or s["gear"] in only]
    added = []
    plan = {}   # gear -> [(label, gear name in Gear)]
    try:
        for s in specs:
            folder = os.path.join(gear_root, s["gearFolder"])
            x64 = s.get("upstreamBinary") == "x64"
            up_name = s["gear"] + (".x64.dll" if x64 else ".dll")
            builds = []
            ship = os.path.join(shipped_dir, s["gearFolder"], up_name)
            pkg = None
            for cand in (s["gear"] + ".x64.dll", s["gear"] + ".dll"):
                if os.path.exists(os.path.join(folder, cand)):
                    pkg = os.path.join(folder, cand)
                    break
            if os.path.exists(ship):
                for k in range(1, 5 if s.get("fix") else 2):
                    label = "shipped" + ("" if k == 1 else f" {k}")
                    dst = os.path.join(folder, s["gear"] + " " + label + (".x64.dll" if x64 else ".dll"))
                    shutil.copyfile(ship, dst)
                    added.append(dst)
                    builds.append((label, s["gear"] + " " + label))
            if pkg:
                same = os.path.exists(ship) and open(pkg, "rb").read() == open(ship, "rb").read()
                builds.append(("package" + (" (upstream)" if same else " (rebuilt)"), s["gear"]))
            for label, d in variants:
                for ext in (".x64.dll", ".dll"):
                    src = os.path.join(d, s["gearFolder"], s["gear"] + ext)
                    if os.path.exists(src):
                        dst = os.path.join(folder, s["gear"] + " " + label + ext)
                        shutil.copyfile(src, dst)
                        added.append(dst)
                        builds.append((label, s["gear"] + " " + label))
            man_src = os.path.join(folder, s["gear"] + ".machine.json")
            if os.path.exists(man_src):
                for label, g in builds:
                    if g != s["gear"]:
                        dst = os.path.join(folder, g + ".machine.json")
                        shutil.copyfile(man_src, dst)
                        added.append(dst)
            plan[s["gear"]] = builds
        if os.path.exists(cache):
            os.remove(cache)

        run = Run(exe, os.path.join(work, "server"))
        results, pitches = {}, {}
        started = time.time()
        for s in specs:
            if not do_pitch:
                break
            man = manifest_of(os.path.join(gear_root, s["gearFolder"]), s["gear"])
            if not (man.get("notes") or {}).get("pitched") or not any(g == s["gear"] for _, g in plan[s["gear"]]):
                continue
            try:
                pitches[s["gear"]] = pitch_check(run, s, man)
            except Exception as e:
                pitches[s["gear"]] = {"error": str(e)[:300]}
                run.restart()
            print(f"pitch {s['gear']:<22} " + "; ".join(f"{k}: " + (f"heard {v['heard']} ({v['cents']:+.1f} ct, {v['method']})" if v.get("heard") is not None else f"none ({v.get('method')})")
                                                   for k, v in pitches[s["gear"]].items() if isinstance(v, dict)), flush=True)
        for s in specs:
            if not do_compare:
                break
            builds = plan[s["gear"]]
            if len(builds) < 2:
                print(f"skip {s['gear']}: {len(builds)} build(s) in Gear", flush=True)
                continue
            man = manifest_of(os.path.join(gear_root, s["gearFolder"]), s["gear"])
            kind = "generator" if s["gearFolder"] == "Generators" else "effect"
            ref_label = builds[0][0]
            res = {label: {"cases": {}} for label, _ in builds}
            upstream = [lb for lb, _ in builds if lb.startswith("shipped")]
            for case in case_list(kind):
                t = time.time()
                try:
                    files = render_case(run, s, builds, case, man)
                except Exception as e:
                    print(f"FAIL {s['gear']} {case[0]}: {str(e)[:400]}", flush=True)
                    run.restart()
                    for label, _ in builds:
                        res[label]["cases"][case[0]] = {"error": str(e)[:200]}
                    continue
                status, ref = files.get(ref_label, (None, None))
                ref_peak = peak_db(ref) if ref else None
                for label, (st, f) in files.items():
                    entry = {"status": st}
                    if f and ref and label != ref_label:
                        same, worst, rel, first = diff(ref, f)
                        entry.update(identical=same, max_diff_dbfs=worst, rel_diff_db=rel, first_diff_s=first)
                        if len(upstream) > 1 and not label.startswith("shipped"):
                            # the closest of upstream's instances (a machine with a fix: they needn't agree)
                            best = min((diff(files[u][1], f)[1] for u in upstream if files.get(u, (0, None))[1]), default=None)
                            entry["best_upstream_dbfs"] = best
                    if f:
                        entry["peak_dbfs"] = peak_db(f)
                        with open(f, "rb") as h:
                            entry["sha1"] = hashlib.sha1(h.read()[44:]).hexdigest()[:12]
                    res[label]["cases"][case[0]] = entry
                groups = {}
                for label, _ in builds:
                    groups.setdefault(res[label]["cases"][case[0]].get("sha1"), []).append(label)
                if len(groups) > 1:
                    print(f"  {s['gear']} {case[0]}: the same bits: " + " | ".join(", ".join(g) for g in groups.values()), flush=True)
                for _, f in files.values():      # the reference too, once every build is compared with it
                    try:
                        os.remove(f)
                    except (OSError, TypeError):
                        pass
                line = "; ".join(f"{lb}: " + ("ref" if lb == ref_label else
                                               ("identical" if e.get("identical") else f"{fmt(e.get('max_diff_dbfs'))} dBFS ({fmt(e.get('rel_diff_db'))} dB rel)"))
                                 + ("" if e.get("status") in (None, "ok") else f" [{e.get('status')}]")
                                 for lb, e in ((lb, res[lb]["cases"].get(case[0], {})) for lb, _ in builds))
                print(f"{s['gear']:<24} {case[0]:<15} peak {fmt(ref_peak)} dBFS  {line}  ({time.time() - t:.1f} s)", flush=True)
            for label, _ in builds:
                cs = res[label]["cases"].values()
                worst = max((e.get("max_diff_dbfs", float("-inf")) for e in cs if "max_diff_dbfs" in e), default=None)
                errors = [e for e in cs if "error" in e or e.get("status") not in (None, "ok")]
                res[label]["worst_dbfs"] = worst
                res[label]["identical"] = all(e.get("identical") for e in cs) if label != ref_label and cs else None
                res[label]["match"] = (label == ref_label) or (worst is not None and worst <= TOLERANCE_DBFS and not errors)
                if len(upstream) > 1 and not label.startswith("shipped"):
                    # A fixed machine: the closest upstream instance within tolerance in every case.
                    bests = [e.get("best_upstream_dbfs") for e in cs]
                    res[label]["worst_best_upstream_dbfs"] = max((b for b in bests if b is not None), default=None)
                    res[label]["match_any_upstream"] = all(b is not None and b <= TOLERANCE_DBFS for b in bests) and not errors
                    twins = [lb for lb, g in builds if lb != label and not lb.startswith("shipped") and lb in ("R64", "R86")]
                    res[label]["same_as"] = [t for t in twins if all(res[label]["cases"][c].get("sha1") == res[t]["cases"][c].get("sha1")
                                                                     for c in res[label]["cases"])]
                res[label]["errors"] = len(errors)
            results[s["gear"]] = {"reference": ref_label, "builds": res}
        run.close()
    finally:
        for p in added:
            try:
                os.remove(p)
            except OSError:
                pass
        if os.path.exists(cache):
            try:
                os.remove(cache)
            except OSError:
                pass

    # ---- report
    rows, failed = [], []
    for gear, pr in pitches.items():
        bad = [k for k, v in pr.items() if not isinstance(v, dict) or v.get("heard") != v.get("expected")]
        if bad or "error" in pr:
            failed.append(f"{gear} (pitch: {', '.join(bad) or pr.get('error')})")
    for gear, r in results.items():
        for label, b in r["builds"].items():
            if label == r["reference"]:
                continue
            verdict = "identical" if b["identical"] else ("within" if b["match"] else "DIFFERS")
            if "match_any_upstream" in b:
                # A machine with a fix: upstream's own instances disagree, so the verdict is against the closest of them.
                verdict = ("as one of upstream's instances in every case" if b["match_any_upstream"] else "UNLIKE every upstream instance in a case") + \
                          (f", the same bits as {'/'.join(b['same_as'])}" if b.get("same_as") else "")
            rows.append((gear, label, verdict, fmt(b["worst_dbfs"]), b["errors"]))
            # The package's own binary where it is upstream's file is the same machine: a difference there is the machine
            # not rendering the same twice (reported, not a failure).
            if gate and label == "package (rebuilt)":
                fixed = "match_any_upstream" in b
                ok = (b["match_any_upstream"] and (b.get("same_as") or not any(x in r["builds"] for x in ("R64", "R86")))) if fixed else b["match"]
                if not ok:
                    failed.append(f"{gear} ({label})")
    lines = [f"{g:<24} {lb:<20} {v} (worst {w} dBFS against upstream's first)" + (f", {e} case(s) failed or diverged" if e else "") for g, lb, v, w, e in rows]
    lines += [f"{g:<24} notes_write plays " + ", ".join(f"{k} as {v['heard']} ({v['cents']:+.1f} ct)" if v.get("heard") is not None else f"{k}: no pitch heard"
                                                    for k, v in pr.items() if isinstance(v, dict)) for g, pr in pitches.items()]
    print("\n".join(lines), flush=True)
    summary = (f"{len(results)} machines, {len(rows)} builds against upstream's binaries, tolerance {TOLERANCE_DBFS:.0f} dBFS"
               + (f", {len(pitches)} pitched generators" if pitches else "") + "; "
               + (f"failed: {', '.join(failed)}" if failed else "every package build matches" + (", every note at its pitch" if pitches else ""))
               + f" ({time.time() - started:.0f} s)")
    print(summary, flush=True)
    if json_out:
        with open(json_out, "w", encoding="utf-8") as f:
            json.dump({"tolerance_dbfs": TOLERANCE_DBFS, "results": results, "pitch": pitches}, f, indent=1, default=str)
    if os.environ.get("GITHUB_ACTIONS"):
        kind = "error" if failed else "notice"
        chunk, n_ = [], 0
        for line in [summary] + lines:
            chunk.append(line)
            if sum(len(x) + 1 for x in chunk) > 3300:
                n_ += 1
                print(f"::{kind} title=Natives shipped vs rebuilt ({n_})::" + "%0A".join(x.replace("%", "%25") for x in chunk), flush=True)
                chunk = []
        if chunk:
            n_ += 1
            print(f"::{kind} title=Natives shipped vs rebuilt ({n_})::" + "%0A".join(x.replace("%", "%25") for x in chunk), flush=True)
        step = os.environ.get("GITHUB_STEP_SUMMARY")
        if step:
            with open(step, "a", encoding="utf-8") as f:
                f.write(f"### Natives: shipped vs rebuilt\n\n{summary}\n\n| Machine | Build | Verdict | Worst difference (dBFS) |\n|---|---|---|---|\n")
                for g, lb, v, w, e in rows:
                    f.write(f"| {g} | {lb} | {v} | {w} |\n")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
