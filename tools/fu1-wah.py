#!/usr/bin/env python3
# FU1-DIAG (temporary, item 9): ReBuzzTests' FSM WahMan -> Neo Filter pair ("cutoff 200, resonance 0, LFO still") failed
# once. Plays the test's song again and again, in one agent process and in fresh ones, and hashes what each part renders:
# the synth (its input), FSM WahMan, then Neo Filter after machine_replace, with the comparison's numbers.
#   python tools/fu1-wah.py <NeoBuzzLive.exe> <work> [--reps N] [--procs N]
import hashlib
import importlib.util
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, file))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


nc = load("native_compare", "native-compare.py")
nt = nc.nt

CASES = {
    "c60": [{"param": "Cutoff", "value": "raw:60", "track": 0}, {"param": "Resonance", "value": "raw:200", "track": 0}, {"param": "Modulation", "value": "raw:0", "track": 0}],
    "c200": [{"param": "Cutoff", "value": "raw:200", "track": 0}, {"param": "Resonance", "value": "raw:0", "track": 0}, {"param": "Modulation", "value": "raw:0", "track": 0}],
}
NOISE = [{"param": "Noise", "value": "100%"}, {"param": "O1 level", "value": "0%"}, {"param": "F1 cutoff", "value": "20 kHz"},
         {"param": "F1 env", "value": "0%"}, {"param": "Amp sustain", "value": "100%"}, {"param": "Volume", "value": "0 dB"}]


def sha(path):
    with open(path, "rb") as f:
        return hashlib.sha1(f.read()[44:]).hexdigest()[:8]


def song(c, case):
    c.call("song_new", discard_unsaved=True)
    c.call("machine_add", gear="Neo Poly", name="Synth")
    c.call("param_set", machine="Synth", changes=NOISE)
    c.call("machine_add", gear="FSM WahMan", name="FX", insert_after="Synth")
    c.call("param_set", machine="FX", changes=CASES[case])
    c.call("pattern_create", machine="Synth", name="00", length_beats=8)
    notes = [{"pitch": p, "start": s, "length": 1.0, "velocity": 100}
             for s, ps in ((0.0, (48, 60, 64, 67)), (2.0, (53, 60, 65, 69)), (4.0, (55, 62, 67, 71)), (6.0, (43, 59, 62, 67))) for p in ps]
    c.call("notes_write", machine="Synth", pattern="00", notes=notes)
    c.call("arrangement_place", machine="Synth", pattern="00", at=[0, 8])


def render(c, label):
    r = c.call("render", **{"from": 0, "to": 16, "tail_seconds": 1, "probes": ["Synth", "FX"], "name": label})
    files = {p["machine"]: p["file"] for p in r.get("probes") or []}
    out = {m: sha(f) for m, f in files.items()}
    for f in list(files.values()) + [r.get("file")]:
        try:
            os.remove(f)
        except (OSError, TypeError):
            pass
    return out


def one(c, case, i):
    song(c, case)
    a = render(c, f"{case}-{i}-a")
    b = render(c, f"{case}-{i}-b")
    r = c.call("machine_replace", machine="FX")
    cmp_ = r.get("comparison") or {}
    m = cmp_.get("machine") or {}
    after = render(c, f"{case}-{i}-n")
    return (f"{case} #{i}: synth {a.get('Synth')}/{b.get('Synth')}/{after.get('Synth')} wah {a.get('FX')}/{b.get('FX')} "
            f"filter {after.get('FX')} within {cmp_.get('within_tolerance')} loudness {(m.get('loudness_db') or {}).get('difference')} "
            f"worst {(m.get('worst_band') or {}).get('difference_db')} at {(m.get('worst_band') or {}).get('hz')}")


def main(argv):
    exe, work = os.path.abspath(argv[0]), os.path.abspath(argv[1])
    reps = int(argv[argv.index("--reps") + 1]) if "--reps" in argv else 12
    procs = int(argv[argv.index("--procs") + 1]) if "--procs" in argv else 4
    if os.path.exists(work):
        shutil.rmtree(work)
    os.makedirs(work)
    for k in range(procs):
        c = nt.Client(exe, os.path.join(work, f"p{k}"), f"wah{k}")
        t = time.time()
        try:
            c.initialize()
            print(f"=== process {k}", flush=True)
            for i in range(reps if k == 0 else 3):
                for case in ("c60", "c200"):
                    print("  " + one(c, case, i), flush=True)
        except Exception as e:
            print(f"  failed: {str(e)[:400]}", flush=True)
        finally:
            c.close()
        print(f"  ({time.time() - t:.0f} s)", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
