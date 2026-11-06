#!/usr/bin/env python3
# FU1-DIAG (temporary, fix/followups-1; removed before the branch's final runs): items 5 and 6 on the Windows runner.
#  1. a process's first render of FSM_Infector_Demo.bmx (no warm-up): does every native sound, does any host hang;
#  2. the demo in fresh processes, the package's Gear as shipped and with the x64 candidates in (as native-songs' phase B):
#     does each machine play the same bits every time, which machines diverge and when.
#   python tools/fu1-diag.py <NeoBuzzLive.exe> <work> [--candidate DIR] [--procs N]
import hashlib
import importlib.util
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, file))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


ns = load("native_songs", "native-songs.py")
nc = ns.nc
nt = nc.nt
DEMO = os.path.join(ROOT, "ExternalMachines", "source", "buzztrax-buzzmachines", "FSM", "Infector", "FSM_Infector_Demo.bmx")
NAMES = ["Infector", "X-Delay", "Infector2", "Infector3", "PhatMan", "Infector4", "Infector5", "X-Delay2", "PhatMan2", "PanzDelay"]


def sha(path):
    with open(path, "rb") as f:
        return hashlib.sha1(f.read()[44:]).hexdigest()[:10]


def stderr_lines(c, keep):
    try:
        c.stderr.flush()
    except ValueError:
        pass
    try:
        with open(c.stderr_path, "rb") as f:
            return [l for l in f.read().decode("utf-8", "replace").splitlines() if any(k in l for k in keep)]
    except OSError:
        return []


def render(c, beats, label, tail=2):
    r = c.call("render", **{"from": 0, "to": beats, "tail_seconds": tail, "probes": NAMES, "name": label})
    files = {p["machine"]: p["file"] for p in r.get("probes") or []}
    try:
        os.remove(r["file"])
    except (OSError, KeyError, TypeError):
        pass
    return files, r.get("warnings") or [], r.get("diverged") or []


def first_render(exe, work, k):
    print(f"=== 1. a process's first render of the demo (process {k})", flush=True)
    os.environ["NEOBUZZ_NATIVE_TRACE"] = "40"
    c = nt.Client(exe, os.path.join(work, "server"), f"first{k}")
    try:
        c.initialize()
        c.call("song_open", path=DEMO, discard_unsaved=True)
        t = time.time()
        files, warnings, diverged = render(c, 176, f"first{k}")
        print(f"  ({time.time() - t:.1f} s) " + "; ".join(f"{n} {nc.fmt(nc.peak_db(files[n]))}" for n in NAMES if n in files), flush=True)
        if warnings or diverged:
            print(f"  warnings {warnings}; diverged {diverged}", flush=True)
        health = [(m["name"], m.get("health")) for m in c.call("machine_list").get("machines") or [] if m.get("health")]
        print(f"  health {health}", flush=True)
        for f in files.values():
            os.remove(f)
    except Exception as e:
        print(f"  failed: {e}", flush=True)
    finally:
        c.close()
        os.environ.pop("NEOBUZZ_NATIVE_TRACE", None)
    for l in stderr_lines(c, ("[trace] render", "hang", "stopped working", "crashed"))[:12]:
        print("  " + l[:600], flush=True)


def demo_runs(exe, work, label, procs):
    print(f"=== 2. {label}: the demo in {procs} fresh processes", flush=True)
    ref = None
    for k in range(procs):
        c = nt.Client(exe, os.path.join(work, "server"), f"{label}{k}")
        try:
            c.initialize()
            c.call("song_open", path=DEMO, discard_unsaved=True)
            t = time.time()
            files, warnings, diverged = render(c, 176, f"{label}{k}")
            d = os.path.join(work, f"{label}{k}")
            os.makedirs(d, exist_ok=True)
            kept = {}
            for n, f in files.items():
                kept[n] = os.path.join(d, n + ".wav")
                shutil.move(f, kept[n])
            row = []
            for n in NAMES:
                if n not in kept:
                    continue
                if ref is None or n not in ref:
                    row.append(f"{n} {nc.fmt(nc.peak_db(kept[n]))} {sha(kept[n])}")
                else:
                    same, worst, _, first = nc.diff(ref[n], kept[n])
                    row.append(f"{n} " + ("same" if same else f"DIFFERS {nc.fmt(worst)} dBFS from {first:.2f} s"))
            print(f"  process {k} ({time.time() - t:.1f} s): " + "; ".join(row), flush=True)
            if warnings or diverged:
                print(f"    warnings {[w[:200] for w in warnings]}; diverged {diverged}", flush=True)
            if ref is None:
                ref = kept
        except Exception as e:
            print(f"  process {k} failed: {str(e)[:400]}", flush=True)
        finally:
            c.close()
        for l in stderr_lines(c, ("hang", "stopped working"))[:4]:
            print("    " + l[:300], flush=True)


def main(argv):
    exe, work = os.path.abspath(argv[0]), os.path.abspath(argv[1])
    candidate, procs = None, 4
    i = 2
    while i < len(argv):
        if argv[i] == "--candidate":
            candidate = os.path.abspath(argv[i + 1])
        elif argv[i] == "--procs":
            procs = int(argv[i + 1])
        i += 2
    if os.path.exists(work):
        shutil.rmtree(work)
    os.makedirs(work)
    for k in range(2):
        first_render(exe, work, k)
    demo_runs(exe, work, "shipped", procs)
    if candidate:
        gear_root = os.path.join(os.path.dirname(exe), "Gear")
        swap = ns.Swap(gear_root, os.path.join(work, "held"))
        try:
            for g in ("Jeskola Cross Delay", "Jeskola Delay", "Jeskola NiNjA dElaY"):
                src = os.path.join(candidate, "Effects", g + ".x64.dll")
                if os.path.exists(src):
                    swap.put("Effects", src, g + ".x64.dll")
            ns.forget_cache()
            demo_runs(exe, work, "candidates", procs)
        finally:
            swap.restore()
            ns.forget_cache()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
