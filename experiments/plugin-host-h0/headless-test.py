#!/usr/bin/env python3
# H0 spike (docs/specs/plugin-host.md §6.1): Neo Plug Spike inside ReBuzz's engine, driven over MCP
# like tools/headless-smoke.py. For each plugin given it checks that the machine
#  - loads the plugin (a generator plays a chord; an effect processes the generator before it);
#  - is audible in the render (a probe per machine);
#  - keeps the plugin in the song: save, reopen, the moved parameter comes back, the render matches;
#  - saves the same plugin state again (byte for byte in the song);
#  - and, with the plugin gone, loads as "missing" and saves its state back unchanged.
#
#   python3 headless-test.py <rebuzz-headless> <work folder> --gen "path|id" [--gen ...] [--fx "path|id" ...]
#
# Generators and effects pair up in order: Fx0 goes after Gen0, and so on (an effect without a
# generator gets a Neo Poly in front of it). Results go to <work>/headless-result.json.

import argparse
import base64
import json
import os
import queue
import re
import subprocess
import sys
import threading
import xml.etree.ElementTree as ET

ap = argparse.ArgumentParser()
ap.add_argument("exe")
ap.add_argument("work")
ap.add_argument("--gen", action="append", default=[])
ap.add_argument("--fx", action="append", default=[])
args = ap.parse_args()

work = os.path.abspath(args.work)
os.makedirs(work, exist_ok=True)
stderr_path = os.path.join(work, "stderr.txt")
failures, results = [], {"generators": args.gen, "effects": args.fx, "checks": []}


def check(ok, what):
    print(("ok   " if ok else "FAIL ") + what, flush=True)
    results["checks"].append({"ok": bool(ok), "what": what})
    if not ok:
        failures.append(what)


env = dict(os.environ)
env["NEOPLUG_SPIKE_PLUGINS"] = ";".join(args.gen)
env["NEOPLUG_SPIKE_FX_PLUGINS"] = ";".join(args.fx)
env["NEOPLUG_SPIKE_TWEAK"] = "1"
# Windows: rebuzz-headless has no native machine hosts (ReBuzzEngine32/64.exe) beside it.
env["REBUZZ_NATIVE_MACHINES"] = "off"
# ReBuzz caches its gear scan per user (MachineDLLCache.xml); a private one sees freshly built gear.
env["XDG_CONFIG_HOME"] = os.path.join(work, "config")
env["APPDATA"] = os.path.join(work, "appdata")
proc = subprocess.Popen([args.exe, "--agent", "--settings", os.path.join(work, "settings")], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=open(stderr_path, "w"), text=True, bufsize=1, env=env)
next_id = 0
lines = queue.Queue()
threading.Thread(target=lambda: [lines.put(l) for l in iter(proc.stdout.readline, "")] + [lines.put("")], daemon=True).start()


def request(method, params=None, timeout=180):
    global next_id
    next_id += 1
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params or {}}) + "\n")
    proc.stdin.flush()
    try:
        line = lines.get(timeout=timeout)
    except queue.Empty:
        proc.kill()
        tail = open(stderr_path, encoding="utf-8", errors="replace").read()[-4000:]
        json.dump({"ok": False, "failures": [f"{method} timed out"], "checks": results["checks"], "stderr_tail": tail},
                  open(os.path.join(work, "headless-result.json"), "w"), indent=1)
        print(f"FAIL {method} timed out after {timeout} s; stderr:\n{tail}")
        sys.exit(1)
    if not line:
        tail = open(stderr_path, encoding="utf-8", errors="replace").read()[-4000:]
        json.dump({"ok": False, "failures": ["rebuzz-headless closed stdout"], "checks": results["checks"], "stderr_tail": tail},
                  open(os.path.join(work, "headless-result.json"), "w"), indent=1)
        raise SystemExit("rebuzz-headless closed stdout; stderr:\n" + tail)
    return json.loads(line)


def call(tool, **a):
    r = request("tools/call", {"name": tool, "arguments": a})
    res = r.get("result") or {}
    text = res.get("content", [{}])[0].get("text", "") if res else json.dumps(r.get("error"))
    print(f"     {tool}: {text[:200]}", flush=True)
    return (not res.get("isError", True)) and "error" not in r, text, res.get("structuredContent") or {}


def stderr_lines():
    return open(stderr_path, encoding="utf-8", errors="replace").read().splitlines()


init = request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "h0", "version": "1"}})
check(init.get("result", {}).get("serverInfo", {}).get("name") == "rebuzz", "initialize")
print("     " + json.dumps(init)[:200], flush=True)
proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
proc.stdin.flush()

ok, text, _ = call("gear_list")
check(ok and "Neo Plug Spike" in text, "Neo Plug Spike is in the gear")

call("song_set", tempo_bpm=120, song_end_beats=8)
machines = []   # (name, kind, spec)
gens = []
for i, spec in enumerate(args.gen):
    name = f"Gen{i}"
    ok, _, _ = call("machine_add", gear="Neo Plug Spike", name=name, tracks=3)
    check(ok, f"machine_add {name} ({spec})")
    call("pattern_create", machine=name, name="A", length_beats=8)
    ok, _, _ = call("notes_write", machine=name, pattern="A",
                    notes=[{"pitch": "C4", "start": 0, "length": 4}, {"pitch": "E4", "start": 0, "length": 4}, {"pitch": "G4", "start": 0, "length": 4},
                           {"pitch": "A3", "start": 4, "length": 3}])
    check(ok, f"notes_write {name}")
    call("arrangement_place", machine=name, pattern="A", at=[0])
    machines.append((name, "gen", spec))
    gens.append(name)
for j, spec in enumerate(args.fx):
    name = f"Fx{j}"
    src = gens[j] if j < len(gens) else None
    if src is None:
        src = f"Src{j}"
        call("machine_add", gear="Neo Poly", name=src)
        call("pattern_create", machine=src, name="A", length_beats=8)
        call("notes_write", machine=src, pattern="A", notes=[{"pitch": "C4", "start": 0, "length": 6}])
        call("arrangement_place", machine=src, pattern="A", at=[0])
    ok, _, _ = call("machine_add", gear="Neo Plug Spike FX", name=name, insert_after=src)
    check(ok, f"machine_add {name} after {src} ({spec})")
    machines.append((name, "fx", spec))

names = [m[0] for m in machines]
log = "\n".join(stderr_lines())
loaded = re.findall(r"\[neoplug-spike\] loaded (\w+) \"([^\"]*)\"", log)
check(len(loaded) >= len(machines), f"{len(loaded)} of {len(machines)} plugins loaded: {', '.join(n for _, n in loaded)}")
tweaks = re.findall(r"\[neoplug-spike\] tweaked \"([^\"]*)\" to (.*) \(([-0-9.eE+]+)\)", log)


def probe_levels(data):
    levels = {}
    for p in data.get("probes") or []:
        m = p.get("measure") or {}
        lufs = (m.get("loudness") or {}).get("integrated_lufs")
        levels[p.get("machine") or p.get("name") or p.get("id")] = (lufs, m.get("silent"))
    return levels


ok, text, r1 = call("render", **{"from": 0, "to": 8, "tail_seconds": 1, "probes": names, "name": "before"})
check(ok and not (r1.get("measure") or {}).get("silent", True), "render r1 is audible")
lv1 = probe_levels(r1)
results["render_before"] = {"measure": r1.get("measure"), "probes": lv1}
for n in names:
    lufs, silent = lv1.get(n, (None, None))
    check(silent is False, f"{n} is audible in r1 ({lufs} LUFS)")

song = os.path.join(work, "h0.bmxml")
check(call("song_save", path=song, overwrite=True)[0], "song_save")
check(call("song_open", path=song, discard_unsaved=True)[0], "song_open")
ok, text, r2 = call("render", **{"from": 0, "to": 8, "tail_seconds": 1, "probes": names, "name": "after"})
lv2 = probe_levels(r2)
results["render_after"] = {"measure": r2.get("measure"), "probes": lv2}
ok, text, cmp = call("compare", a=r1.get("id", "r1"), b=r2.get("id", "r2"))
results["compare"] = text
identical = ok and "No measurable difference" in text
loud_same = True
for n in names:
    a, b = lv1.get(n, (None, None))[0], lv2.get(n, (None, None))[0]
    # (below the loudness gate both are -inf: equal)
    same = a is not None and (a == b or (isinstance(a, (int, float)) and isinstance(b, (int, float)) and abs(a - b) < 0.3))
    loud_same &= same
    check(same, f"{n}: {a} LUFS before, {b} after")
# Some plugins aren't deterministic (Surge XT's oscillators start at random phases): then the same
# loudness per machine is the test, and compare's verdict is reported.
results["identical_render"] = identical
check(identical or loud_same, "the reopened song renders the same" + (" (compare: no measurable difference)" if identical else f" per machine (compare: {text[:160]})"))

log = "\n".join(stderr_lines())
restored = re.findall(r"\[neoplug-spike\] restored \"([^\"]*)\" from (\d+) bytes; \"([^\"]*)\" is (.*) \(([-0-9.eE+]+)\)", log)
results["tweaks"] = tweaks
results["restored"] = restored
for (pname, ptext, pval), (_, nbytes, rname, rtext, rval) in zip(tweaks, restored):
    close = abs(float(pval) - float(rval)) <= 1e-3 * max(1.0, abs(float(pval)))
    check(pname == rname and (close or ptext.strip() == rtext.strip()), f"\"{pname}\" tweaked to {pval} ({ptext.strip()}), restored as {rval} ({rtext.strip()}) ({nbytes} bytes)")
check(len(restored) == len(machines), f"{len(restored)} of {len(machines)} machines restored their plugin state")


def machine_states(path):
    """The spike machines' PlugState (decoded) from a .bmxml, by machine name."""
    out = {}
    root = ET.parse(path).getroot()
    for m in root.iter():
        if m.tag.split("}")[-1] != "Machine":
            continue
        name = m.get("Name") or (m.findtext("Name") or "")
        for d in m.iter():
            tag = d.tag.split("}")[-1]
            if tag in ("Data", "MachineState") and d.text and len(d.text) > 40:
                try:
                    raw = base64.b64decode(d.text)
                except Exception:
                    continue
                if b"PlugState" in raw:
                    out[name] = raw
    return out


song2 = os.path.join(work, "h0-resaved.bmxml")
check(call("song_save", path=song2, overwrite=True)[0], "song_save again")
s1, s2 = machine_states(song), machine_states(song2)
results["state_sizes"] = {k: len(v) for k, v in s1.items()}
check(set(s1) >= set(names), f"the song holds every machine's state ({sorted(s1)})")
for n in names:
    check(n in s1 and n in s2 and s1[n] == s2[n], f"{n}: the saved state is identical after reopen and save ({len(s1.get(n, b''))} bytes)")

# A missing plugin: point one machine at a path that doesn't exist, open, save: its state survives.
if names:
    victim = names[0]
    text = open(song, encoding="utf-8").read()
    raw = s1[victim]
    state_xml = raw[5:].decode("utf-8", errors="replace")
    path_now = re.search(r"<Path>(.*?)</Path>", state_xml)
    if path_now:
        # Data is a version byte, a 32-bit length, then the MachineState XML.
        body = raw[5:].decode("utf-8").replace(path_now.group(0), "<Path>/nowhere/missing-plugin.vst3</Path>").encode("utf-8")
        changed = raw[:1] + len(body).to_bytes(4, "little") + body
        missing_song = os.path.join(work, "h0-missing.bmxml")
        open(missing_song, "w", encoding="utf-8").write(text.replace(base64.b64encode(raw).decode(), base64.b64encode(changed).decode()))
        check(call("song_open", path=missing_song, discard_unsaved=True)[0], "song_open with a missing plugin")
        resaved = os.path.join(work, "h0-missing-resaved.bmxml")
        check(call("song_save", path=resaved, overwrite=True)[0], "song_save with a missing plugin")
        s3 = machine_states(resaved)
        check(s3.get(victim) == changed, f"{victim}: a missing plugin's state is saved back unchanged")
        check("plugin missing" in "\n".join(stderr_lines()), "the machine reports the plugin missing")
    else:
        check(False, "no <Path> in the machine state")

proc.stdin.close()
try:
    code = proc.wait(60)
except subprocess.TimeoutExpired:
    proc.kill()
    code = None
check(code == 0, f"exits when stdin closes (code {code})")
results["ok"] = not failures
results["failures"] = failures
json.dump(results, open(os.path.join(work, "headless-result.json"), "w"), indent=1)
print(f"\n{len(failures)} failed" if failures else "\nall passed")
if failures:
    print("stderr tail:\n" + "\n".join(stderr_lines()[-60:]))
sys.exit(1 if failures else 0)
