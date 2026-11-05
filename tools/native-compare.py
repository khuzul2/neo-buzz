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
#   "<gear> <label>"  each --variant LABEL=DIR: the DLL tools/build-natives.ps1 built (DIR\<Generators|Effects>);
#                     each --unfixed LABEL=DIR likewise: build-natives.ps1 -Unfixed, a fixed machine without its fix
# Each gets a copy of the machine's manifest, so notes and triggers reach it as they reach the original.
#
# A build matches when every case's samples are bit-identical to the reference's, or differ by at most
# TOLERANCE_DBFS (−100 dBFS, the null test's line across platforms, docs/specs/cross-platform.md §4.3: below a
# 16-bit step, −96 dBFS, so no 16-bit export can tell; rounding differences between compilers, x87 against SSE2
# and C runtime maths show up at −150…−110 dBFS). With --gate, the package's own build of every machine must
# match, else the script fails: what songs hear doesn't change.
#
# A machine whose entry names a source fix (machines.json "fix": state upstream's code never initialises, so its start
# is whatever the heap held and two instances of upstream's binary can play the same song differently) can't be held
# to upstream's binary from the first sample: four instances of it ("<gear> shipped", "… shipped 2" to "… shipped 4")
# split two or three ways, or all agree on a start that isn't the fix's, run to run. Its gate is three checks that
# don't depend on what the heap held, each of which a broken rebuild fails:
#   one sound     the fixed builds (the package's, every --variant: x64 and x86, each its own instance and process)
#                 render the same bits in every case: the fix leaves nothing unset.
#   the fix is    the fixed build renders the same bits as the --unfixed one in every case: upstream's code without
#   the start     the fix, every allocation zeroed (tools/native-zeroed-heap.cpp), so it starts as a fresh allocation
#                 did; the fix is that start and changes nothing else.
#   settled       each case again after PREROLL beats of silence ("settled …"): by the time the input comes, the state
#                 upstream left unset has converged (the glide has reached its cutoff, the first block's coefficients
#                 are gone, the filters held zeros throughout), so upstream's instances render what the fixed build
#                 does; it must match at least one of the four (bit for bit or within the tolerance; one instance
#                 whose start was NaN or far out can't fail it). This is the check against upstream's binary.
# Before settling, which of upstream's instances the fixed build matches is reported, not judged.
#
# --pitch (§12.6's "Done when"): every pitched generator among them (its manifest's notes.pitched) plays the written
# pitch through notes_write, its manifest's pitch offset applied as an agent's call applies it: A3, A4 and A5, each its own
# render, heard by analyze's pitch (YIN) or, where that hears none (a 303's squelch), the profiler's harmonic sieve
# (tools/machine_roles.py) on the note's first 0.37 s. A note heard a semitone or more away fails the script.
#
#   python tools/native-compare.py <ReBuzz.exe> <work> [--variant LABEL=DIR ...] [--unfixed LABEL=DIR] [--only gear,...]
#                                  [--gate] [--shipped RunTime/Gear] [--json out.json] [--pitch] [--no-compare]

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
# Silence before the input in a fixed machine's settled cases (2 s at TEMPO). The slowest glide among the cases (FSM
# WahPro2 at three quarters of its inertia range, 1.85 cutoff units per 64 samples; WahMan's slowest, 2.5) moves 2,500
# units in it; a start anywhere in the cutoff's own range (−120 … 360 with the LFO) is at most 480 away.
PREROLL = 4
UPSTREAM_INSTANCES = 4
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


def case_list(kind, fixed=False):
    """(name, fraction of the raw range or None for the defaults, input gain dB, beats of silence first)."""
    cases = [("default", None, 0.0, 0)]
    cases += [("quarter", 0.25, 0.0, 0), ("three-quarters", 0.75, 0.0, 0)]
    if kind == "effect":
        cases.append(("quiet-input", None, -40.0, 0))
    if fixed:
        cases += [("settled " + name, frac, db, PREROLL) for name, frac, db, _ in cases]
    return cases


def play_generator(c, machine, spec, trigger, mode, at=0):
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
    c.call("arrangement_place", machine=machine, pattern="P", at=[at])


def manifest_of(gear_dir, gear):
    p = os.path.join(gear_dir, gear + ".machine.json")
    if not os.path.exists(p):
        return {}
    with open(p, encoding="utf-8-sig") as f:
        return json.load(f)


def render_case(run, spec, builds, case, man):
    """One render of every build of the machine side by side; {build: wav path} or raises."""
    c = run.client()
    name, frac, input_db, preroll = case
    kind = "generator" if spec["gearFolder"] == "Generators" else "effect"
    c.call("song_new", discard_unsaved=True, tempo_bpm=TEMPO)
    c.call("song_set", song_end_beats=BEATS + preroll)
    names = {}
    if kind == "effect":
        gear, preset, params, notes = nt.SOURCE
        c.call("machine_add", gear=gear, name="Src", connect_to="none")
        c.call("preset_load", machine="Src", preset=preset)
        nt.set_params(c, "Src", params)
        c.call("pattern_create", machine="Src", name="A", length_beats=BEATS)
        c.call("notes_write", machine="Src", pattern="A", notes=notes)
        c.call("arrangement_place", machine="Src", pattern="A", at=[preroll])
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
            play_generator(c, m, spec, trigger, mode, at=preroll)
    r = c.call("render", **{"from": 0, "to": BEATS + preroll, "tail_seconds": TAIL, "probes": list(names.values()),
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



# The roles of a machine's builds: instances of upstream's binary, the package's own build, the --variant builds
# (built from the sources with the fix, where the machine has one) and the --unfixed ones.
OURS = ("package (rebuilt)", "rebuilt")


def where(d):
    """' (X dBFS, from Y s)' for a difference [identical, max difference dBFS, first differing s]."""
    if not d:
        return ""
    _, worst, first = d
    return f" ({fmt(worst)} dBFS" + (f", from {first:.2f} s" if first is not None else "") + ")"


def judge_fixed(cases, roles):
    """The gate on a machine with a source fix (the top of this file): for every build that isn't an instance of
    upstream's binary, its verdict and, for the builds with the fix, what fails it.

    cases: {case name: {label: entry}}; an entry has "status" ("ok" or "diverged") or "error", "sha1" and, for a
    build that isn't upstream's, "as_upstream" (the upstream labels it matches: bit for bit or within the
    tolerance), "closest_upstream" ([identical, max difference dBFS, first differing s] against the closest of
    them) and "vs" ({label: [identical, …]} against each other build that isn't upstream's; identical means the
    same bits). roles: {label: "upstream", "package (upstream)", "package (rebuilt)", "rebuilt" or "unfixed"}.
    Returns {label: {"verdict": str, "failures": [str]}}; the gate is the package build's failures."""
    upstream = [lb for lb, r in roles.items() if r == "upstream"]
    ours = [lb for lb, r in roles.items() if r in OURS]
    unfixed = [lb for lb, r in roles.items() if r == "unfixed"]
    settled = [c for c in cases if c.startswith("settled ")]
    before = [c for c in cases if not c.startswith("settled ")]
    out = {}
    for label, role in roles.items():
        if role == "upstream":
            continue
        es = {c: cases[c].get(label) or {"error": "not rendered"} for c in cases}
        failures = [f"{c}: {e.get('error') or e.get('status')}" for c, e in es.items()
                    if "error" in e or e.get("status") not in (None, "ok")]
        # Once settled: as at least one of upstream's instances (the check against upstream's binary).
        for c in settled:
            if es[c].get("as_upstream") or "error" in es[c]:
                continue
            groups = {}
            for u in upstream:
                groups.setdefault((cases[c].get(u) or {}).get("sha1"), []).append(u)
            failures.append(f"{c}: unlike every instance of upstream's" + where(es[c].get("closest_upstream"))
                            + ("; upstream's own instances render " + " | ".join(", ".join(g) for g in groups.values())
                               if len(groups) > 1 else "") + ": the rebuild doesn't play upstream's DSP"
                            + (", or the fix changes more than the start" if role in OURS else ""))
        # One sound (the fixed builds alike in every case) and the fix as the start a zeroed allocation gives.
        alike = []
        if role in OURS:
            for t in [x for x in ours if x != label] + unfixed:
                bad = [c for c in cases if "error" not in es[c] and not ((es[c].get("vs") or {}).get(t) or [False])[0]]
                if not bad:
                    alike.append(t)
                    continue
                d = (es[bad[0]].get("vs") or {}).get(t)
                if t in unfixed:
                    failures.append(f"not the same bits as {t} (upstream's code from a zeroed start) in {', '.join(bad)}" + where(d)
                                    + ": the fix isn't the start a fresh allocation gives, or changes more than the start")
                else:
                    failures.append(f"not the same bits as {t} in {', '.join(bad)}" + where(d)
                                    + ": the fix leaves state unset, or the two builds compute differently")
        n = len(upstream)
        verdict = []
        if settled:
            counts = [len(es[c].get("as_upstream") or []) for c in settled]
            verdict.append(("once settled as upstream's instances" if all(counts) else "once settled UNLIKE upstream's instances in a case")
                           + f" ({', '.join(f'{k}/{n}' for k in counts)})")
        if alike:
            verdict.append(f"the same bits as {'/'.join(alike)} in every case")
        if before:
            verdict.append("from the start as " + ", ".join(f"{len(es[c].get('as_upstream') or [])}/{n}" for c in before)
                           + " of upstream's instances (not judged: their start is heap garbage)")
        out[label] = {"verdict": "; ".join(verdict), "failures": failures}
    return out


def main(argv):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except AttributeError:
        pass
    if len(argv) < 2:
        print(__doc__ or "usage: native-compare.py <ReBuzz.exe> <work> [--variant LABEL=DIR] [--unfixed LABEL=DIR] [--only a,b] [--gate]")
        return 2
    exe, work = os.path.abspath(argv[0]), os.path.abspath(argv[1])
    variants, only, gate, shipped_dir, json_out = [], None, False, os.path.join(ROOT, "RunTime", "Gear"), None
    do_pitch, do_compare = False, True
    i = 2
    while i < len(argv):
        a = argv[i]
        if a in ("--variant", "--unfixed"):
            label, _, d = argv[i + 1].partition("=")
            variants.append((label, os.path.abspath(d), "unfixed" if a == "--unfixed" else "rebuilt"))
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
    plan = {}    # gear -> [(label, gear name in Gear)]
    roles = {}   # gear -> {label: role}
    try:
        for s in specs:
            folder = os.path.join(gear_root, s["gearFolder"])
            x64 = s.get("upstreamBinary") == "x64"
            up_name = s["gear"] + (".x64.dll" if x64 else ".dll")
            builds, rl = [], {}
            ship = os.path.join(shipped_dir, s["gearFolder"], up_name)
            pkg = None
            for cand in (s["gear"] + ".x64.dll", s["gear"] + ".dll"):
                if os.path.exists(os.path.join(folder, cand)):
                    pkg = os.path.join(folder, cand)
                    break
            if os.path.exists(ship):
                for k in range(1, UPSTREAM_INSTANCES + 1 if s.get("fix") else 2):
                    label = "shipped" + ("" if k == 1 else f" {k}")
                    dst = os.path.join(folder, s["gear"] + " " + label + (".x64.dll" if x64 else ".dll"))
                    shutil.copyfile(ship, dst)
                    added.append(dst)
                    builds.append((label, s["gear"] + " " + label))
                    rl[label] = "upstream"
            if pkg:
                same = os.path.exists(ship) and open(pkg, "rb").read() == open(ship, "rb").read()
                label = "package" + (" (upstream)" if same else " (rebuilt)")
                builds.append((label, s["gear"]))
                rl[label] = label
            for label, d, role in variants:
                for ext in (".x64.dll", ".dll"):
                    src = os.path.join(d, s["gearFolder"], s["gear"] + ext)
                    if os.path.exists(src):
                        dst = os.path.join(folder, s["gear"] + " " + label + ext)
                        shutil.copyfile(src, dst)
                        added.append(dst)
                        builds.append((label, s["gear"] + " " + label))
                        rl[label] = role
            man_src = os.path.join(folder, s["gear"] + ".machine.json")
            if os.path.exists(man_src):
                for label, g in builds:
                    if g != s["gear"]:
                        dst = os.path.join(folder, g + ".machine.json")
                        shutil.copyfile(man_src, dst)
                        added.append(dst)
            plan[s["gear"]] = builds
            roles[s["gear"]] = rl
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
            builds, rl = plan[s["gear"]], roles[s["gear"]]
            if len(builds) < 2:
                print(f"skip {s['gear']}: {len(builds)} build(s) in Gear", flush=True)
                continue
            man = manifest_of(os.path.join(gear_root, s["gearFolder"]), s["gear"])
            kind = "generator" if s["gearFolder"] == "Generators" else "effect"
            fixed = bool(s.get("fix"))
            ref_label = builds[0][0]
            res = {label: {"cases": {}} for label, _ in builds}
            upstream = [lb for lb, _ in builds if rl[lb] == "upstream"]
            for case in case_list(kind, fixed):
                t = time.time()
                try:
                    files = render_case(run, s, builds, case, man)
                except Exception as e:
                    print(f"FAIL {s['gear']} {case[0]}: {str(e)[:400]}", flush=True)
                    run.restart()
                    for label, _ in builds:
                        res[label]["cases"][case[0]] = {"error": str(e)[:200]}
                    continue
                sha = {}
                for label, (st, f) in files.items():
                    if f:
                        with open(f, "rb") as h:
                            sha[label] = hashlib.sha1(h.read()[44:]).hexdigest()[:12]
                diffs = {}

                def compare(a, b):
                    """[the same bits, max difference dBFS, first differing s] between two builds' renders."""
                    if sha.get(a) and sha.get(a) == sha.get(b):
                        return [True, float("-inf"), None]
                    key = tuple(sorted((sha.get(a) or a, sha.get(b) or b)))   # renders with the same bits: one diff
                    if key not in diffs:
                        same, worst, _, first = diff(files[a][1], files[b][1])
                        diffs[key] = [same, worst, first]
                    return diffs[key]

                status, ref = files.get(ref_label, (None, None))
                ref_peak = peak_db(ref) if ref else None
                for label, (st, f) in files.items():
                    entry = {"status": st}
                    if f and ref and label != ref_label:
                        same, worst, rel, first = diff(ref, f)
                        entry.update(identical=same, max_diff_dbfs=worst, rel_diff_db=rel, first_diff_s=first)
                    if f:
                        entry["peak_dbfs"] = peak_db(f)
                        entry["sha1"] = sha[label]
                    if fixed and f and rl[label] != "upstream":
                        # A fixed machine: which of upstream's instances it matches, and each other build of ours.
                        ds = {u: compare(u, label) for u in upstream if files.get(u, (0, None))[1]}
                        entry["as_upstream"] = [u for u, d in ds.items() if d[0] or d[1] <= TOLERANCE_DBFS]
                        entry["closest_upstream"] = min(ds.values(), key=lambda d: d[1]) if ds else None
                        entry["vs"] = {o: compare(o, label) for o, _ in builds if o != label and rl[o] != "upstream" and files.get(o, (0, None))[1]}
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
                print(f"{s['gear']:<24} {case[0]:<24} peak {fmt(ref_peak)} dBFS  {line}  ({time.time() - t:.1f} s)", flush=True)
            for label, _ in builds:
                cs = res[label]["cases"].values()
                worst = max((e.get("max_diff_dbfs", float("-inf")) for e in cs if "max_diff_dbfs" in e), default=None)
                errors = [e for e in cs if "error" in e or e.get("status") not in (None, "ok")]
                res[label]["worst_dbfs"] = worst
                res[label]["identical"] = all(e.get("identical") for e in cs) if label != ref_label and cs else None
                res[label]["match"] = (label == ref_label) or (worst is not None and worst <= TOLERANCE_DBFS and not errors)
                res[label]["errors"] = len(errors)
            if fixed:
                cases = {c[0]: {lb: res[lb]["cases"].get(c[0]) for lb, _ in builds} for c in case_list(kind, fixed)}
                for label, j in judge_fixed(cases, rl).items():
                    res[label]["judged"] = j
            results[s["gear"]] = {"reference": ref_label, "fixed": fixed, "roles": rl, "builds": res}
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
    rows, failed, reasons = [], [], []
    for gear, pr in pitches.items():
        bad = [k for k, v in pr.items() if not isinstance(v, dict) or v.get("heard") != v.get("expected")]
        if bad or "error" in pr:
            failed.append(f"{gear} (pitch: {', '.join(bad) or pr.get('error')})")
    for gear, r in results.items():
        for label, b in r["builds"].items():
            if label == r["reference"]:
                continue
            verdict = "identical" if b["identical"] else ("within" if b["match"] else "DIFFERS")
            if "judged" in b:
                verdict = b["judged"]["verdict"]
            elif r["fixed"]:
                n_same = sum(1 for e in b["cases"].values() if e.get("identical"))
                verdict = f"an instance of upstream's binary: the same bits as {r['reference']} in {n_same} of {len(b['cases'])} cases"
            rows.append((gear, label, verdict, fmt(b["worst_dbfs"]), b["errors"]))
            # The package's own binary where it is upstream's file is the same machine: a difference there is the machine
            # not rendering the same twice (reported, not a failure).
            if gate and label == "package (rebuilt)":
                if r["fixed"]:
                    why = b.get("judged", {"failures": ["not judged"]})["failures"]
                    if not any(v == "unfixed" for v in r["roles"].values()):
                        why = why + ["no --unfixed build to hold the fix to"]
                    if why:
                        failed.append(f"{gear} ({label})")
                        reasons += [f"{gear} ({label}): {w}" for w in why]
                elif not b["match"]:
                    failed.append(f"{gear} ({label})")
    lines = [f"{g:<24} {lb:<20} {v} (worst {w} dBFS against upstream's first)" + (f", {e} case(s) failed or diverged" if e else "") for g, lb, v, w, e in rows]
    lines += [f"{g:<24} notes_write plays " + ", ".join(f"{k} as {v['heard']} ({v['cents']:+.1f} ct)" if v.get("heard") is not None else f"{k}: no pitch heard"
                                                    for k, v in pr.items() if isinstance(v, dict)) for g, pr in pitches.items()]
    lines += [f"FAIL {x}" for x in reasons]
    print("\n".join(lines), flush=True)
    summary = (f"{len(results)} machines, {len(rows)} builds against upstream's binaries, tolerance {TOLERANCE_DBFS:.0f} dBFS"
               + (f", {len(pitches)} pitched generators" if pitches else "") + "; "
               + (f"failed: {', '.join(failed)}" if failed else "every package build matches" + (", every note at its pitch" if pitches else ""))
               + f" ({time.time() - started:.0f} s)")
    print(summary, flush=True)
    if json_out:
        with open(json_out, "w", encoding="utf-8") as f:
            json.dump({"tolerance_dbfs": TOLERANCE_DBFS, "preroll_beats": PREROLL, "results": results, "pitch": pitches, "failures": reasons},
                      f, indent=1, default=str)
    if os.environ.get("GITHUB_ACTIONS"):
        kind = "error" if failed else "notice"
        chunk, n_ = [], 0
        for line in [summary] + [f"FAIL {x}" for x in reasons] + lines:
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
                f.write(f"### Natives: shipped vs rebuilt\n\n{summary}\n\n")
                for x in reasons:
                    f.write(f"- FAIL {x}\n")
                f.write("\n| Machine | Build | Verdict | Worst difference (dBFS) |\n|---|---|---|---|\n")
                for g, lb, v, w, e in rows:
                    f.write(f"| {g} | {lb} | {v} | {w} |\n")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
