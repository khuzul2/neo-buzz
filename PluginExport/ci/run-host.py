#!/usr/bin/env python3
"""Runs neohost on the CLAP module of every (or one) exported plug-in: python3 run-host.py <assets folder> <neohost> <mode> [--only "Neo Filter"] [extra neohost args]
Finds the modules the way each OS lays them out (Linux/Windows: Neo X.clap; macOS: Neo X.clap/Contents/MacOS/Neo X)."""
import glob, json, os, subprocess, sys

assets, host, mode = (os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2]), sys.argv[3])
rest = sys.argv[4:]
only = None
if "--only" in rest:
    i = rest.index("--only"); only = rest[i + 1]; del rest[i:i + 2]
plugins = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "plugins.json")))


def module(name):
    for pat in (f"{assets}/**/{name}.clap", f"{assets}/{name}.clap"):
        for p in glob.glob(pat, recursive=True):
            if os.path.isfile(p): return p
            inner = os.path.join(p, "Contents", "MacOS", name)
            if os.path.isfile(inner): return inner
    return None


bad = 0
for p in plugins:
    if only and p["name"] != only: continue
    m = module(p["name"])
    if not m:
        print(f"FAIL no module for {p['name']}"); bad += 1; continue
    r = subprocess.run([host, "--module", m, "--mode", mode, "--id", p["id"]] + rest, capture_output=True, text=True)
    out = r.stdout + r.stderr
    keep = [l for l in out.splitlines() if not l.startswith("ok ")]
    print("\n".join(keep[-25:]))
    if r.returncode != 0: print(f"FAIL {p['name']} ({mode}) exit {r.returncode}"); bad += 1
sys.exit(1 if bad else 0)
