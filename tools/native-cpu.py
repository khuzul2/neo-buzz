#!/usr/bin/env python3
# Fork (docs/specs/mcp-server.md §12.14, fix/ci-flakes-4): the work time of each build of a native effect, side by side
# in one song through NeoBuzzLive.exe --agent: upstream's binary ("shipped"), the package's own, and each --variant LABEL=DIR
# (a tools/build-natives.ps1 output, e.g. one built with -Flags '/O2' -Suffix ' O2'). All builds hear the same input (the
# null test's Neo FM6 phrase, 2 s), then 20 s of silence: render's cpu counters give each machine's own milliseconds of
# work per second of audio in 2 s windows, so a tail that slows a build down (denormal numbers) shows as the "after" figure
# climbing above the "playing" one. Each case is repeated --reps times in fresh songs; the table gives the median.
#
#   python tools/native-cpu.py <NeoBuzzLive.exe> <work> --only "Jeskola Delay,Jeskola NiNjA dElaY" [--variant LABEL=DIR ...] [--reps 5]

import importlib.util
import json
import os
import shutil
import statistics
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
_spec = importlib.util.spec_from_file_location("nulltest", os.path.join(HERE, "null-test.py"))
nt = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(nt)

TEMPO = 120
BEATS = 4        # 2 s of input
TAIL = 20.0


def natives():
    with open(os.path.join(ROOT, "ExternalMachines", "machines.json"), encoding="utf-8-sig") as f:
        d = json.load(f)
    return next(m for m in d["machines"] if m["id"] == "buzztrax-buzzmachines")["natives"]


# Cases: the machine's defaults, and a short delay with feedback: its tail passes through the denormal range in about two
# seconds (0.6^170 = 1e-38), where a build that slows down on denormals shows it in the windows after the input.
CASES = [("defaults", []),
         ("10 ms, +60 % feedback", [("Length", 10), ("Length unit", 1), ("Feedback", 102)])]


def measure(c, builds, settings):
    """{label: (playing ms/s, median after, worst after)} for one render of the builds side by side."""
    c.call("song_new", discard_unsaved=True, tempo_bpm=TEMPO)
    c.call("song_set", song_end_beats=BEATS)
    gear, preset, params, notes = nt.SOURCE
    c.call("machine_add", gear=gear, name="Src", connect_to="none")
    c.call("preset_load", machine="Src", preset=preset)
    nt.set_params(c, "Src", params)
    c.call("pattern_create", machine="Src", name="A", length_beats=BEATS)
    c.call("notes_write", machine="Src", pattern="A", notes=notes)
    c.call("arrangement_place", machine="Src", pattern="A", at=[0])
    names = {}
    for i, (label, g) in enumerate(builds):
        m = f"B{i}"
        c.call("machine_add", gear=g, name=m)
        c.call("connect", **{"from": "Src", "to": m})
        if settings:
            c.call("param_set", machine=m, changes=[{"param": p, "value": f"raw:{v}", "track": 0} for p, v in settings])
        names[m] = label
    r = c.call("render", **{"from": 0, "to": BEATS, "tail_seconds": TAIL, "cpu": True, "cpu_window_seconds": 2.0})
    out = {}
    for row in r.get("cpu") or []:
        label = names.get(row.get("machine"))
        w = (row.get("windows_ms_per_second") or [])[:11]
        if label and len(w) >= 2:
            out[label] = (w[0], statistics.median(w[1:]), max(w[1:]))
    try:
        os.remove(r.get("file"))
    except (OSError, TypeError):
        pass
    return out


def wall(c, gear, settings):
    """Render time per second of audio, as tools/machine-profiler.py reads it (the faster of two 4 s renders), of the
    input alone (gear None) or through one build: the machine's cost is the difference, in ms per second of audio."""
    c.call("song_new", discard_unsaved=True, tempo_bpm=TEMPO)
    c.call("song_set", song_end_beats=8)
    g, preset, params, notes = nt.SOURCE
    c.call("machine_add", gear=g, name="Src", connect_to="none" if gear else "Master")
    c.call("preset_load", machine="Src", preset=preset)
    nt.set_params(c, "Src", params)
    c.call("pattern_create", machine="Src", name="A", length_beats=8)
    c.call("notes_write", machine="Src", pattern="A", notes=notes)
    c.call("arrangement_place", machine="Src", pattern="A", at=[0])
    if gear:
        c.call("machine_add", gear=gear, name="M", connect_to="Master")
        c.call("connect", **{"from": "Src", "to": "M"})
        if settings:
            c.call("param_set", machine="M", changes=[{"param": p, "value": f"raw:{v}", "track": 0} for p, v in settings])
    best = None
    for _ in range(3):
        t = time.time()
        r = c.call("render", **{"from": 0, "to": 8, "tail_seconds": 0.0})
        t = (time.time() - t) / 4.0
        best = t if best is None else min(best, t)
        try:
            os.remove(r.get("file"))
        except (OSError, TypeError):
            pass
    return best


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    exe, work = os.path.abspath(argv[0]), os.path.abspath(argv[1])
    only, variants, reps, shipped_dir = None, [], 5, os.path.join(ROOT, "RunTime", "Gear")
    i = 2
    while i < len(argv):
        a = argv[i]
        if a == "--only":
            only = set(argv[i + 1].split(","))
        elif a == "--variant":
            label, _, d = argv[i + 1].partition("=")
            variants.append((label, os.path.abspath(d)))
        elif a == "--reps":
            reps = int(argv[i + 1])
        else:
            raise SystemExit(f"unknown argument {a}")
        i += 2
    gear_root = os.path.join(os.path.dirname(exe), "Gear")
    added = []
    plan = {}
    try:
        for s in natives():
            if only and s["gear"] not in only:
                continue
            folder = os.path.join(gear_root, s["gearFolder"])
            builds = []
            x64 = s.get("upstreamBinary") == "x64"
            ship = os.path.join(shipped_dir, s["gearFolder"], s["gear"] + (".x64.dll" if x64 else ".dll"))
            if os.path.exists(ship):
                dst = os.path.join(folder, s["gear"] + " shipped" + (".x64.dll" if x64 else ".dll"))
                shutil.copyfile(ship, dst)
                added.append(dst)
                builds.append((f"upstream {'x64' if x64 else 'x86'}", s["gear"] + " shipped"))
            if any(os.path.exists(os.path.join(folder, s["gear"] + e)) for e in (".x64.dll", ".dll")):
                builds.append(("package", s["gear"]))
            for label, d in variants:
                for ext in (".x64.dll", ".dll"):
                    src = os.path.join(d, s["gearFolder"], s["gear"] + ext)
                    if os.path.exists(src):
                        dst = os.path.join(folder, s["gear"] + " " + label + ext)
                        shutil.copyfile(src, dst)
                        added.append(dst)
                        builds.append((label, s["gear"] + " " + label))
            plan[s["gear"]] = builds
        cache = os.path.join(os.environ.get("APPDATA", ""), "ReBuzz", "MachineDLLCache.xml")
        if os.path.exists(cache):
            os.remove(cache)
        os.makedirs(work, exist_ok=True)
        c = nt.Client(exe, os.path.join(work, "server"), "cpu")
        c.initialize()
        try:
            for gear, builds in plan.items():
                for case, settings in CASES:
                    runs = {lb: [] for lb, _ in builds}
                    for _ in range(reps):
                        try:
                            for lb, v in measure(c, builds, settings).items():
                                runs[lb].append(v)
                        except RuntimeError as e:
                            print(f"  {gear} {case}: {str(e)[:300]}", flush=True)
                            break
                    base = [wall(c, None, None) for _ in range(reps)]
                    for lb, g in builds:
                        w = [wall(c, g, settings) for _ in range(reps)]
                        print(f"  {lb:<14} render wall minus input alone (the profiler's reading): {1000 * (statistics.median(w) - statistics.median(base)):6.1f} ms/s", flush=True)
                    print(f"{gear}, {case}: work time, ms per second of audio (median of {reps} renders; after = the 20 s of silence)", flush=True)
                    for lb, vs in runs.items():
                        if vs:
                            print(f"  {lb:<14} playing {statistics.median(v[0] for v in vs):6.2f}   after (median window) {statistics.median(v[1] for v in vs):6.2f}"
                                  f"   after (worst window) {statistics.median(v[2] for v in vs):6.2f}", flush=True)
        finally:
            c.close()
    finally:
        for p in added:
            try:
                os.remove(p)
            except OSError:
                pass
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
