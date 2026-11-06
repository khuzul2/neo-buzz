#!/usr/bin/env python3
# FU1-DIAG (temporary, fix/followups-1; removed before the branch's final runs): items 5 and 6 on the Windows runner.
#  1. a process's first renders of a song with natives, with NEOBUZZ_NATIVE_TRACE: silent or not, and why;
#  2. FSM_Infector_Demo.bmx rendered in fresh processes: does each Infector play the same bits every time;
#  3. the same with upstream's FSM Infector on a zeroed heap (--zeroed DIR: native-zeroheap.py's copy and ZEROHEAP.dll).
#   python tools/fu1-diag.py <NeoBuzzLive.exe> <work> [--zeroed DIR] [--procs N]
import hashlib
import importlib.util
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
spec = importlib.util.spec_from_file_location("native_compare", os.path.join(HERE, "native-compare.py"))
nc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(nc)
nt = nc.nt
DEMO = os.path.join(ROOT, "ExternalMachines", "source", "buzztrax-buzzmachines", "FSM", "Infector", "FSM_Infector_Demo.bmx")


def sha(path):
    with open(path, "rb") as f:
        return hashlib.sha1(f.read()[44:]).hexdigest()[:12]


def natives_in(c):
    out = []
    for m in c.call("machine_list").get("machines") or []:
        g = (m.get("gear") or "")
        if any(g.startswith(p) for p in ("FSM ", "Jeskola ", "Oomek ")):
            out.append(m["name"])
    return out


def render(c, beats, names, label, tail=0.5):
    r = c.call("render", **{"from": 0, "to": beats, "tail_seconds": tail, "probes": names, "name": label})
    files = {p["machine"]: p["file"] for p in r.get("probes") or []}
    try:
        os.remove(r["file"])
    except (OSError, KeyError, TypeError):
        pass
    return files, r.get("warnings") or []


def trace_of(c, lines=60):
    c.stderr.flush()
    try:
        with open(c.stderr_path, "rb") as f:
            text = f.read().decode("utf-8", "replace").splitlines()
    except OSError:
        return []
    keep = [l for l in text if "[trace" in l or "hang" in l.lower() or "stopped" in l]
    return keep[:lines]


def first_renders(exe, work, k):
    print(f"=== 1. first renders (process {k})", flush=True)
    os.environ["NEOBUZZ_NATIVE_TRACE"] = "120"
    c = nt.Client(exe, os.path.join(work, "server"), f"first{k}")
    try:
        c.initialize()
        t = time.time()
        if k % 2 == 0:
            c.call("song_open", path=DEMO, discard_unsaved=True)
            names = natives_in(c)
            beats = 32
        else:
            c.call("song_new", discard_unsaved=True, tempo_bpm=120)
            c.call("song_set", song_end_beats=4)
            c.call("machine_add", gear="FSM Infector", name="Inf")
            c.call("machine_add", gear="Jeskola Delay", name="JD", connect_to="Master")
            c.call("connect", **{"from": "Inf", "to": "JD"})
            nc.play_generator(c, "Inf", {"gear": "FSM Infector"}, None, "tracks")
            names = ["Inf", "JD"]
            beats = 4
        print(f"  opened/built in {time.time() - t:.1f} s: {names}", flush=True)
        prev = None
        for r in (1, 2, 3):
            t = time.time()
            files, warnings = render(c, beats, names, f"first{k}-r{r}")
            row = []
            for n in names:
                f = files.get(n)
                row.append(f"{n} {nc.fmt(nc.peak_db(f)) if f else '-'} {sha(f) if f else '-'}")
            same = prev is not None and all(sha(files[n]) == sha(prev[n]) for n in names if n in files and n in prev)
            print(f"  render {r} ({time.time() - t:.1f} s){' same as before' if same else ''}: " + "; ".join(row) + (f"; warnings {warnings}" if warnings else ""), flush=True)
            if prev:
                for f in prev.values():
                    os.remove(f)
            prev = files
        for f in (prev or {}).values():
            os.remove(f)
    except Exception as e:
        print(f"  failed: {e}", flush=True)
    finally:
        c.close()
        os.environ.pop("NEOBUZZ_NATIVE_TRACE", None)
    for l in trace_of(c):
        print("  " + l, flush=True)


def demo_runs(exe, work, label, procs):
    print(f"=== {label}: FSM_Infector_Demo.bmx in {procs} fresh processes", flush=True)
    ref = None
    for k in range(procs):
        c = nt.Client(exe, os.path.join(work, "server"), f"{label}{k}")
        try:
            c.initialize()
            c.call("song_open", path=DEMO, discard_unsaved=True)
            names = natives_in(c)
            render(c, 1, names, f"{label}{k}-warm", tail=0)
            c.call("song_open", path=DEMO, discard_unsaved=True)
            t = time.time()
            files, warnings = render(c, 176, names, f"{label}{k}", tail=2)
            d = os.path.join(work, f"{label}{k}")
            os.makedirs(d, exist_ok=True)
            kept = {}
            for n, f in files.items():
                kept[n] = os.path.join(d, n + ".wav")
                shutil.move(f, kept[n])
            row = []
            for n in names:
                if n not in kept:
                    continue
                if ref is None or n not in ref:
                    row.append(f"{n} peak {nc.fmt(nc.peak_db(kept[n]))}")
                else:
                    same, worst, _, first = nc.diff(ref[n], kept[n])
                    row.append(f"{n} " + ("same" if same else f"DIFFERS {nc.fmt(worst)} dBFS from {first:.2f} s"))
            print(f"  process {k} ({time.time() - t:.1f} s): " + "; ".join(row) + (f"; warnings {warnings}" if warnings else ""), flush=True)
            if ref is None:
                ref = kept
        except Exception as e:
            print(f"  process {k} failed: {e}", flush=True)
        finally:
            c.close()


def main(argv):
    exe, work = os.path.abspath(argv[0]), os.path.abspath(argv[1])
    zeroed, procs = None, 4
    i = 2
    while i < len(argv):
        if argv[i] == "--zeroed":
            zeroed = os.path.abspath(argv[i + 1])
        elif argv[i] == "--procs":
            procs = int(argv[i + 1])
        i += 2
    if os.path.exists(work):
        shutil.rmtree(work)
    os.makedirs(work)
    for k in range(4):
        first_renders(exe, work, k)
    demo_runs(exe, work, "upstream", procs)
    if zeroed and not (os.path.exists(os.path.join(zeroed, "ZEROHEAP.dll")) and os.path.exists(os.path.join(zeroed, "Gear", "Generators", "FSM Infector.x64.dll"))):
        print(f"=== zeroed: {zeroed} has no ZEROHEAP.dll or zeroed FSM Infector: {os.listdir(zeroed)}", flush=True)
        zeroed = None
    if zeroed:
        gear = os.path.join(os.path.dirname(exe), "Gear", "Generators")
        orig = os.path.join(gear, "FSM Infector.x64.dll")
        held = os.path.join(work, "held.dll")
        host = os.path.join(os.path.dirname(exe), "bin64", "ZEROHEAP.dll")
        shutil.copyfile(orig, held)
        shutil.copyfile(os.path.join(zeroed, "Gear", "Generators", "FSM Infector.x64.dll"), orig)
        shutil.copyfile(os.path.join(zeroed, "ZEROHEAP.dll"), host)
        cache = os.path.join(os.environ.get("APPDATA", ""), "ReBuzz", "MachineDLLCache.xml")
        if os.path.exists(cache):
            os.remove(cache)
        try:
            demo_runs(exe, work, "zeroed", procs)
        finally:
            shutil.copyfile(held, orig)
            os.remove(host)
            if os.path.exists(cache):
                os.remove(cache)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
