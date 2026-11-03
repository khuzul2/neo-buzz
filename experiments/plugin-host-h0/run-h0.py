#!/usr/bin/env python3
# H0 spike driver (docs/specs/plugin-host.md §6.1 and "H0 results"): runs every check on one machine
# and writes <out>/summary.md and <out>/summary.json. CI runs it with the test fixtures; you run it on
# your own computer with your own plugins (the commercial ones, iLok included):
#
#   python3 run-h0.py --plugin "<path to .vst3 or .clap>[|class name or id]" [--plugin ...] [--out results]
#
# from the unzipped CI artifact (neoplug-h0-windows-x64 or neoplug-h0-macos-arm64), which has this
# layout (found next to this script):
#   bin/       neoplug library, neoplug-probe, neoplug-ipc-bench, neoplug-test.clap
#   VST3/      AGain and Note Expression Synth (the SDK's examples)
#   headless/  rebuzz-headless with the Neo Plug Spike machines in its Gear
#   guispike/  NeoPlugGuiSpike (Avalonia 12)
#
# Per plugin:  1. neoplug-probe: load, play notes or audio, save the state, restore it in a fresh
#                 instance, render again, open the GUI in a floating window (a separate process from
#                 ReBuzz: the sandbox's situation);
#              2. inside ReBuzz's engine (rebuzz-headless, the Neo Plug Spike machine): play in a song,
#                 save, reopen, compare;
#              3. the GUI embedded in an Avalonia window (NativeControlHost), and floating.
# Copy-protected plugins may show an authorization dialog on first load: answer it, the steps wait.
#
# CI also passes --fixtures (the SDK examples and neoplug-test.clap), --surge and --bench.

import argparse
import json
import os
import platform
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
EXE = ".exe" if os.name == "nt" else ""

ap = argparse.ArgumentParser()
ap.add_argument("--plugin", action="append", default=[], help="path[|class]: a plugin to test (repeatable)")
ap.add_argument("--fixtures", action="store_true", help="also the CI fixtures (AGain, Note Expression Synth, neoplug-test.clap)")
ap.add_argument("--surge", action="append", default=[], help="Surge XT bundles to test, as --plugin (CI)")
ap.add_argument("--surge-list", help="a file of Surge XT specs, one per line (get-surge.py)")
ap.add_argument("--bin", default=os.path.join(HERE, "bin"))
ap.add_argument("--vst3", default=os.path.join(HERE, "VST3"))
ap.add_argument("--headless", default=os.path.join(HERE, "headless", "rebuzz-headless" + EXE))
ap.add_argument("--guispike", default=os.path.join(HERE, "guispike", "NeoPlugGuiSpike" + EXE))
ap.add_argument("--bench", action="store_true", help="run the IPC benchmark")
ap.add_argument("--bench-args", default="", help="extra arguments for neoplug-ipc-bench")
ap.add_argument("--no-gui", action="store_true")
ap.add_argument("--timeout", type=int, default=300, help="seconds per step (raise it if a plugin's authorization takes long)")
ap.add_argument("--out", default="h0-results")
args = ap.parse_args()

out = os.path.abspath(args.out)
os.makedirs(out, exist_ok=True)
probe = os.path.join(args.bin, "neoplug-probe" + EXE)
osname = {"win32": "windows", "darwin": "macos"}.get(sys.platform, "linux")
summary = {"os": osname, "machine": platform.machine(), "python": platform.python_version(), "plugins": [], "headless": None, "gui": None, "bench": None}
required_failures = []


def run(cmd, timeout=None, env=None, cwd=None):
    t0 = time.time()
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout or args.timeout, env=env, cwd=cwd, encoding="utf-8", errors="replace")
        return p.returncode, p.stdout, p.stderr, time.time() - t0
    except subprocess.TimeoutExpired as e:
        return None, (e.stdout or "") if isinstance(e.stdout, str) else "", "timeout", time.time() - t0
    except OSError as e:
        return None, "", str(e), time.time() - t0


def last_json(text):
    for line in reversed((text or "").strip().splitlines()):
        line = line.strip()
        if line.startswith("{"):
            try:
                return json.loads(line)
            except ValueError:
                pass
    return None


# --- The plugins ------------------------------------------------------------------------------------

specs = []   # (path, id, required)
if args.fixtures:
    test_clap = os.path.join(args.bin, "neoplug-test.clap")
    specs += [(os.path.join(args.vst3, "again.vst3"), "AGain VST3", True),
              (os.path.join(args.vst3, "note-expression-synth.vst3"), "Note Expression Synth", True),
              (os.path.join(args.vst3, "note-expression-synth.vst3"), "Note Expression Synth With UI", True),
              (test_clap, "neoplug.test.synth", True),
              (test_clap, "neoplug.test.gain", True)]
if args.surge_list and os.path.exists(args.surge_list):
    args.surge += [l.strip() for l in open(args.surge_list, encoding="utf-8") if l.strip()]
    if not args.surge:
        required_failures.append("Surge XT: no plugins installed")
for s in args.surge:
    p, _, i = s.partition("|")
    specs.append((p, i, True))
for s in args.plugin:
    p, _, i = s.partition("|")
    specs.append((p, i, True))

results = []
for path, cid, required in specs:
    name = os.path.basename(path.rstrip("/\\")) + (f" [{cid}]" if cid else "")
    entry = {"plugin": name, "path": path, "id": cid}
    if not os.path.exists(path):
        entry["error"] = "not found"
        results.append(entry)
        if required:
            required_failures.append(f"{name}: not found")
        continue
    code, so, se, secs = run([probe, "list", path])
    listing = last_json(so) or {}
    classes = listing.get("classes") or []
    if not cid and classes:
        cid = classes[0]["id"]
    cls = next((c for c in classes if c["id"] == cid or c["name"] == cid), classes[0] if classes else {})
    entry["class"] = cls
    folder = os.path.join(out, "probe", "".join(ch if ch.isalnum() else "_" for ch in name)[:60])
    os.makedirs(folder, exist_ok=True)
    cmd = [probe, "test", path, "--out", folder] + (["--id", cid] if cid else []) + ([] if args.no_gui or osname == "linux" else ["--gui"])
    code, so, se, secs = run(cmd)
    r = last_json(so)
    open(os.path.join(folder, "stderr.txt"), "w", encoding="utf-8").write(se or "")
    entry["probe_seconds"] = round(secs, 1)
    if r is None:
        entry["error"] = f"probe exited {code} (0x{(code or 0) & 0xFFFFFFFF:08X}): {(se or '')[-400:]}"
        print(f"  probe exited {code}; stdout tail: {(so or '')[-800:]}", flush=True)
        if required:
            required_failures.append(f"{name}: probe crashed or hung ({code})")
    else:
        entry["probe"] = r
        if not r.get("ok") and required:
            required_failures.append(f"{name}: " + ", ".join(r.get("failures") or ["probe failed"]))
    results.append(entry)
    print(f"{name}: {'ok' if r and r.get('ok') else 'FAIL'} ({secs:.1f} s)", flush=True)
    if not (r and r.get("ok")):
        brief = {k: v for k, v in (r or {}).items() if k != "params"}
        print("  probe: " + json.dumps(brief)[:3000], flush=True)
        print("  stderr: " + (se or "")[-1500:].replace("\n", "\n          "), flush=True)
    elif r.get("gui"):
        print("  gui: " + json.dumps({k: v for k, v in r["gui"].items()})[:2500], flush=True)
summary["plugins"] = results

# --- In ReBuzz's engine -------------------------------------------------------------------------------

loadable = [e for e in results if e.get("probe", {}).get("ok")]
if os.path.exists(args.headless) and loadable:
    gens = [f"{e['path']}|{e['probe']['id']}" for e in loadable if e["probe"].get("instrument")]
    fxs = [f"{e['path']}|{e['probe']['id']}" for e in loadable if not e["probe"].get("instrument")]
    cmd = [sys.executable, os.path.join(HERE, "headless-test.py"), args.headless, os.path.join(out, "headless")]
    for g in gens:
        cmd += ["--gen", g]
    for f in fxs:
        cmd += ["--fx", f]
    env = dict(os.environ)
    env["NEOPLUG_LIB"] = os.path.join(args.bin, ("neoplug.dll" if os.name == "nt" else "libneoplug.dylib" if osname == "macos" else "libneoplug.so"))
    code, so, se, secs = run(cmd, timeout=args.timeout * 3, env=env)
    open(os.path.join(out, "headless-log.txt"), "w", encoding="utf-8").write((so or "") + "\n" + (se or ""))
    try:
        hr = json.load(open(os.path.join(out, "headless", "headless-result.json"), encoding="utf-8"))
    except Exception:
        hr = {"ok": False, "failures": [f"headless test exited {code}"], "log_tail": (so or "")[-2000:] + (se or "")[-1000:]}
    summary["headless"] = hr
    if not hr.get("ok"):
        required_failures.append("in the engine: " + "; ".join(hr.get("failures", [])[:6]))
        print("engine log tail:\n" + ((so or "") + (se or ""))[-4000:], flush=True)
        try:
            print("rebuzz-headless stderr tail:\n" + open(os.path.join(out, "headless", "stderr.txt"), encoding="utf-8", errors="replace").read()[-4000:], flush=True)
        except OSError:
            pass
    for c in hr.get("checks", []):
        print(("  ok   " if c["ok"] else "  FAIL ") + c["what"][:240], flush=True)
    print(f"engine: {'ok' if hr.get('ok') else 'FAIL'} ({secs:.1f} s)", flush=True)
elif not os.path.exists(args.headless):
    summary["headless"] = {"skipped": f"no rebuzz-headless at {args.headless}"}

# --- GUIs in Avalonia ---------------------------------------------------------------------------------

if not args.no_gui and osname in ("macos", "windows") and os.path.exists(args.guispike):
    gui_specs = [f"{e['path']}|{e['probe']['id']}" for e in loadable]
    if gui_specs:
        cmd = [args.guispike, "--out", os.path.join(out, "gui")]
        for g in gui_specs:
            cmd += ["--plugin", g]
        env = dict(os.environ)
        code, so, se, secs = run(cmd, timeout=args.timeout * 2, env=env)
        open(os.path.join(out, "gui-log.txt"), "w", encoding="utf-8").write((so or "") + "\n" + (se or ""))
        try:
            summary["gui"] = json.load(open(os.path.join(out, "gui", "gui-results.json"), encoding="utf-8"))
        except Exception:
            summary["gui"] = {"error": f"gui spike exited {code}", "log_tail": (se or "")[-2000:]}
        print(f"gui: exit {code} ({secs:.1f} s)", flush=True)
        print("gui results: " + json.dumps(summary["gui"])[:12000], flush=True)
        print("gui stderr: " + (se or "")[-3000:], flush=True)

# --- The sandbox's round trip -------------------------------------------------------------------------

if args.bench:
    bench = os.path.join(args.bin, "neoplug-ipc-bench" + EXE)
    cmd = [bench, "--json", os.path.join(out, f"ipc-{osname}.json")] + args.bench_args.split()
    code, so, se, secs = run(cmd, timeout=1800)
    open(os.path.join(out, "ipc-log.txt"), "w", encoding="utf-8").write((se or ""))
    print("ipc bench log:\n" + (se or ""), flush=True)
    try:
        summary["bench"] = json.load(open(os.path.join(out, f"ipc-{osname}.json"), encoding="utf-8"))
    except Exception:
        summary["bench"] = {"error": f"bench exited {code}: {(se or '')[-500:]}"}

# --- Summary ------------------------------------------------------------------------------------------

L = []
w = L.append
w(f"# H0 spike on {osname} ({platform.machine()})")
w("")
w("| Plugin | Format | Loads | Plays | State restored | Same render | GUI (floating) | open ms | block µs (mean/max) |")
w("|---|---|---|---|---|---|---|---:|---:|")
for e in results:
    r = e.get("probe")
    if not r:
        w(f"| {e['plugin']} | | **no**: {e.get('error', '')[:80]} | | | | | | |")
        continue
    rend = r.get("render") or {}
    rest = r.get("restored") or {}
    gui = r.get("gui") or {}
    snap = ((gui.get("snapshot") or {}).get("view") or {}) if isinstance(gui.get("snapshot"), dict) else {}
    gtxt = "–" if not gui else ("yes, %d colours" % snap.get("colors", 0) if gui.get("attached") else "no: " + str(gui.get("error", ""))[:40])
    w(f"| {r.get('name')} | {r.get('format')} | yes | {rend.get('rms_dbfs')} dBFS | {'yes' if rest.get('matches') else 'no'} ({r.get('state_bytes')} B) | "
      f"{'yes' if (r.get('restored_null_dbfs') or 0) < -120 else str(r.get('restored_null_dbfs')) + ' dB'} | {gtxt} | {r.get('open_ms', 0):.0f} | "
      f"{rend.get('process_mean_us', 0):.1f} / {rend.get('process_max_us', 0):.0f} |")
w("")
h = summary.get("headless")
if h:
    if h.get("skipped"):
        w(f"Engine: skipped ({h['skipped']}).")
    else:
        checks = h.get("checks") or []
        w(f"Engine (rebuzz-headless, Neo Plug Spike): **{'all passed' if h.get('ok') else 'FAILED'}**, {sum(1 for c in checks if c['ok'])}/{len(checks)} checks.")
        for c in checks:
            if not c["ok"]:
                w(f"- FAIL {c['what']}")
    w("")
g = summary.get("gui")
if isinstance(g, list):
    w("| GUI in Avalonia | Embedded | Inside the window | Pixels (colours) | Floating | Pixels (colours) |")
    w("|---|---|---|---|---|---|")
    for e in g:
        em, fl = e.get("embedded") or {}, e.get("floating") or {}
        es = (((em.get("inspect") or {}).get("snapshot") or {}).get("view") or {})
        ew = (((em.get("inspect") or {}).get("snapshot") or {}).get("window") or {})
        fs = (((fl.get("inspect") or {}).get("snapshot") or {}).get("view") or {})
        w(f"| {e.get('plugin', '')[-60:]} | {em.get('attached')} {em.get('error', '')[:40]} | {em.get('inside_avalonia_window')} | view {es.get('colors')}, window {ew.get('colors') if ew else '–'} | {fl.get('attached')} {fl.get('error', '')[:40]} | {fs.get('colors')} |")
    w("")
elif g:
    w(f"GUI spike: {json.dumps(g)[:300]}")
    w("")
b = summary.get("bench")
if b and b.get("results"):
    w(f"IPC round trip per block ({b.get('cpu')}, {b.get('cores')} cores), µs:")
    w("")
    w("| Mechanism | Policy | Pacing | Frames | p50 | p99 | p99.9 | max | RT |")
    w("|---|---|---|---:|---:|---:|---:|---:|---|")
    for r in b["results"]:
        w(f"| {r['mechanism']} | {r['policy']} | {r['pacing']} | {r['frames']} | {r['p50_us']:.1f} | {r['p99_us']:.1f} | {r['p999_us']:.1f} | {r['max_us']:.0f} | {'y' if r['rt_server'] or r['policy'] == '-' else 'n'} |")
    w("")
if required_failures:
    w("**Failures:**")
    for f in required_failures:
        w(f"- {f}")
summary["failures"] = required_failures
md = "\n".join(L) + "\n"
open(os.path.join(out, "summary.md"), "w", encoding="utf-8").write(md)
json.dump(summary, open(os.path.join(out, "summary.json"), "w", encoding="utf-8"), indent=1)
print(md)
if os.environ.get("GITHUB_STEP_SUMMARY"):
    open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8").write(md)
print(f"Results in {out} (send summary.md and summary.json).")
sys.exit(1 if required_failures else 0)
