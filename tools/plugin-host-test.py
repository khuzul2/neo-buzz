#!/usr/bin/env python3
# Plug-in host (docs/specs/plugin-host.md, phase H1): Neo Plug and Neo Plug FX inside ReBuzz's engine, driven over MCP like
# tools/headless-smoke.py. For each plug-in named it checks that the machine
#  - is listed by gear_list and added with machine_add(plugin=...);
#  - shows its parameters with their units in machine_describe, and takes a change in the plug-in's unit;
#  - is audible in the render (a generator plays a chord; an effect processes the generator before it);
#  - keeps the plug-in in the song: save, reopen, the changed parameter comes back, the render matches;
#  - saves the same state again (byte for byte in the song);
#  - and, with the plug-in gone, loads as missing and saves its state back unchanged.
# With --fixtures it also runs the checks the test plug-ins make possible: a sidechain ducker, a note effect's MIDI out, latency.
#
#   python3 plugin-host-test.py <rebuzz-headless> <work folder> --dir <plug-in folder> [--lib neoplug.so] [--scan neoplug-scan]
#                               --gen "text" [--gen ...] [--fx "text" ...] [--fixtures]
#
# A plug-in is named by a piece of its name ("Test Synth", "Surge XT (CLAP)"). Results go to <work>/plugin-host-result.json.

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
ap.add_argument("--dir", action="append", default=[], help="a folder with plug-ins (NEOBUZZ_PLUGIN_PATH)")
ap.add_argument("--lib", help="the neoplug library (NEOPLUG_LIB)")
ap.add_argument("--scan", help="the neoplug-scan executable (NEOPLUG_SCAN)")
ap.add_argument("--gen", action="append", default=[])
ap.add_argument("--fx", action="append", default=[])
ap.add_argument("--fixtures", action="store_true", help="also run the checks that need the neoplug test plug-ins")
args = ap.parse_args()

work = os.path.abspath(args.work)
os.makedirs(work, exist_ok=True)
stderr_path = os.path.join(work, "stderr.txt")
result_path = os.path.join(work, "plugin-host-result.json")
failures, results = [], {"generators": args.gen, "effects": args.fx, "checks": []}


def check(ok, what):
    print(("ok   " if ok else "FAIL ") + what, flush=True)
    results["checks"].append({"ok": bool(ok), "what": what})
    if not ok:
        failures.append(what)


env = dict(os.environ)
env["NEOBUZZ_PLUGIN_DIR"] = os.path.join(work, "plugin-data")
env["NEOBUZZ_PLUGIN_PATH"] = os.pathsep.join(os.path.abspath(d) for d in args.dir)
env["NEOBUZZ_PLUGIN_STANDARD"] = "0"
if args.lib:
    env["NEOPLUG_LIB"] = os.path.abspath(args.lib)
if args.scan:
    env["NEOPLUG_SCAN"] = os.path.abspath(args.scan)
env["REBUZZ_NATIVE_MACHINES"] = "off"
env.setdefault("NEOPLUG_DEBUG", "")
env["XDG_CONFIG_HOME"] = os.path.join(work, "config")
env["APPDATA"] = os.path.join(work, "appdata")
proc = subprocess.Popen([args.exe, "--agent", "--settings", os.path.join(work, "settings")], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=open(stderr_path, "w"), text=True, bufsize=1, env=env)
next_id = 0
lines = queue.Queue()
threading.Thread(target=lambda: [lines.put(l) for l in iter(proc.stdout.readline, "")] + [lines.put("")], daemon=True).start()


def finish(extra):
    tail = open(stderr_path, encoding="utf-8", errors="replace").read()[-4000:]
    json.dump({"ok": False, "failures": failures + [extra], "checks": results["checks"], "stderr_tail": tail}, open(result_path, "w"), indent=1)
    return tail


def request(method, params=None, timeout=300):
    global next_id
    next_id += 1
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params or {}}) + "\n")
    proc.stdin.flush()
    try:
        line = lines.get(timeout=timeout)
    except queue.Empty:
        proc.kill()
        print(f"FAIL {method} timed out after {timeout} s; stderr:\n{finish(method + ' timed out')}")
        sys.exit(1)
    if not line:
        raise SystemExit("rebuzz-headless closed stdout; stderr:\n" + finish("rebuzz-headless closed stdout"))
    return json.loads(line)


def call(tool, **a):
    r = request("tools/call", {"name": tool, "arguments": a})
    res = r.get("result") or {}
    text = res.get("content", [{}])[0].get("text", "") if res else json.dumps(r.get("error"))
    print(f"     {tool}: {text[:200]}", flush=True)
    return (not res.get("isError", True)) and "error" not in r, text, res.get("structuredContent") or {}


def stderr_lines():
    return open(stderr_path, encoding="utf-8", errors="replace").read().splitlines()


init = request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "h1", "version": "1"}})
check(init.get("result", {}).get("serverInfo", {}).get("name") == "neobuzz-live", "initialize")
proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
proc.stdin.flush()

# --- The Gear menu --------------------------------------------------------------------------------------------------
ok, text, data = call("gear_list", search="Neo Plug", limit=100)
rows = {g["name"]: g for g in data.get("gear", [])}
check("Neo Plug" in rows and "Neo Plug FX" in rows, "Neo Plug and Neo Plug FX are in the gear")
gen_names = (rows.get("Neo Plug") or {}).get("plugins") or []
fx_names = (rows.get("Neo Plug FX") or {}).get("plugins") or []
ok, _, data = call("gear_list", search="", limit=1000)
for g in data.get("gear", []):
    if g["name"] == "Neo Plug":
        gen_names = g.get("plugins") or gen_names
    if g["name"] == "Neo Plug FX":
        fx_names = g.get("plugins") or fx_names
results["plugins"] = {"instruments": gen_names, "effects": fx_names}
check(len(gen_names) + len(fx_names) > 0, f"the scan found {len(gen_names)} instruments and {len(fx_names)} effects")


def resolve(text, names, what):
    hits = [n for n in names if text.lower() in n.lower()]
    check(len(hits) >= 1, f"{what} \"{text}\" is in the library" + (f" ({hits[0]})" if hits else f" (have: {names[:6]})"))
    return hits[0] if hits else None


call("song_set", tempo_bpm=120, song_end_beats=8)
machines = []   # (name, kind, plugin)
gens = []
for i, text in enumerate(args.gen):
    plugin = resolve(text, gen_names, "instrument")
    if not plugin:
        continue
    name = f"Gen{i}"
    ok, _, _ = call("machine_add", gear="Neo Plug", plugin=plugin, name=name, tracks=3)
    check(ok, f"machine_add {name} ({plugin})")
    call("pattern_create", machine=name, name="A", length_beats=8)
    ok, _, _ = call("notes_write", machine=name, pattern="A",
                    notes=[{"pitch": "C4", "start": 0, "length": 4}, {"pitch": "E4", "start": 0, "length": 4}, {"pitch": "G4", "start": 0, "length": 4},
                           {"pitch": "A3", "start": 4, "length": 3}])
    check(ok, f"notes_write {name}")
    call("arrangement_place", machine=name, pattern="A", at=[0])
    machines.append((name, "gen", plugin))
    gens.append(name)
for j, text in enumerate(args.fx):
    plugin = resolve(text, fx_names, "effect")
    if not plugin:
        continue
    name = f"Fx{j}"
    src = gens[j] if j < len(gens) else None
    if src is None:
        src = f"Src{j}"
        call("machine_add", gear="Neo Poly", name=src)
        call("pattern_create", machine=src, name="A", length_beats=8)
        call("notes_write", machine=src, pattern="A", notes=[{"pitch": "C4", "start": 0, "length": 6}])
        call("arrangement_place", machine=src, pattern="A", at=[0])
    ok, _, _ = call("machine_add", gear="Neo Plug FX", plugin=plugin, name=name, insert_after=src)
    check(ok, f"machine_add {name} after {src} ({plugin})")
    machines.append((name, "fx", plugin))

names = [m[0] for m in machines]
log = "\n".join(stderr_lines())
loaded = re.findall(r"\[neoplug\] loaded (\w+) \"([^\"]*)\"", log)
check(len(loaded) >= len(machines), f"{len(loaded)} of {len(machines)} plug-ins loaded: {', '.join(n for _, n in loaded)}")

# --- machine_describe: parameters with units ------------------------------------------------------------------------
tweaks = {}
for name, kind, plugin in machines:
    ok, text, d = call("machine_describe", machine=name, all=True)
    ps = [p for p in d.get("parameters", []) if p.get("name") and not p["name"].startswith("Slot ")]
    check(ok and len(ps) > 0, f"{name}: machine_describe lists {len(ps)} plug-in parameters")
    withunit = [p for p in ps if p.get("unit")]
    check(len(withunit) > 0 or len(ps) == 0, f"{name}: {len(withunit)} of its parameters have units ({', '.join(p['name'] + ' [' + p['unit'] + ']' for p in withunit[:3])})")
    check(d.get("exact_units") is True, f"{name}: exact_units")
    # change the first parameter that is not a choice, in raw terms, and keep what machine_describe says about it
    def wide(p):   # a continuous parameter: not a switch or a channel pick (a stepped one clamps a raw value of this size)
        try:
            return not p.get("choices") and not any(w in p["name"].lower() for w in ("channel", "input", "output", "mode", "bypass")) and float(p["max"]) - float(p["min"]) > 12
        except (KeyError, TypeError, ValueError):
            return False
    def plain(p):
        return not p.get("choices") and not any(w in p["name"].lower() for w in ("channel", "input", "output", "mode", "bypass"))
    pick = next((p for p in ps if wide(p)), next((p for p in ps if plain(p)), next((p for p in ps if not p.get("choices")), ps[0] if ps else None)))
    if pick:
        want = 20000 if kind != "fx" else 42000   # an effect's first parameter is often a gain: stay audible
        ok, text, s = call("param_set", machine=name, changes=[{"param": pick["name"], "value": f"raw:{want}"}])
        check(ok, f"{name}: param_set {pick['name']} = raw:{want}")
        ok, text, d2 = call("machine_describe", machine=name, all=True)
        now = next((p for p in d2.get("parameters", []) if p.get("name") == pick["name"]), {})
        tweaks[name] = (pick["name"], now.get("value"), now.get("raw"))
        check(now.get("raw") == want, f"{name}: {pick['name']} reads {now.get('value')} (raw {now.get('raw')})")


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

song = os.path.join(work, "h1.bmxml")
check(call("song_save", path=song, overwrite=True)[0], "song_save")
check(call("song_open", path=song, discard_unsaved=True)[0], "song_open")
ok, text, r2 = call("render", **{"from": 0, "to": 8, "tail_seconds": 1, "probes": names, "name": "after"})
lv2 = probe_levels(r2)
results["render_after"] = {"measure": r2.get("measure"), "probes": lv2}
ok, text, cmp = call("compare", a=r1.get("id", "r1"), b=r2.get("id", "r2"))
identical = ok and "No measurable difference" in text
loud_same = True
for n in names:
    a, b = lv1.get(n, (None, None))[0], lv2.get(n, (None, None))[0]
    same = a is not None and (a == b or (isinstance(a, (int, float)) and isinstance(b, (int, float)) and abs(a - b) < 0.3))
    loud_same &= same
    check(same, f"{n}: {a} LUFS before, {b} after")
results["identical_render"] = identical
# Some plug-ins aren't deterministic (Surge XT's oscillators start at random phases): then the same loudness per machine is the test.
check(identical or loud_same, "the reopened song renders the same" + (" (compare: no measurable difference)" if identical else f" per machine (compare: {text[:160]})"))

for name, (pname, value, raw) in tweaks.items():
    ok, text, d3 = call("machine_describe", machine=name, all=True)
    now = next((p for p in d3.get("parameters", []) if p.get("name") == pname), {})
    check(now.get("raw") == raw, f"{name}: {pname} comes back as {value} (raw {raw}) after reopen; now {now.get('value')} (raw {now.get('raw')})")


def machine_states(path):
    """The plug-in machines' PlugState (decoded) from a .bmxml, by machine name."""
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


song2 = os.path.join(work, "h1-resaved.bmxml")
check(call("song_save", path=song2, overwrite=True)[0], "song_save again")
s1, s2 = machine_states(song), machine_states(song2)
results["state_sizes"] = {k: len(v) for k, v in s1.items()}
check(set(s1) >= set(names), f"the song holds every machine's state ({sorted(s1)})")
for n in names:
    check(n in s1 and n in s2 and s1[n] == s2[n], f"{n}: the saved state is identical after reopen and save ({len(s1.get(n, b''))} bytes)")

def edit_state(src, dst, victim, fn):
    """Writes dst: src with the victim machine's PlugState XML run through fn. Returns (old state bytes, new state bytes)."""
    text = open(src, encoding="utf-8").read()
    states = machine_states(src)
    raw = states[victim]
    # Data is a version byte, a 32-bit length, then the MachineState XML.
    body = fn(raw[5:].decode("utf-8")).encode("utf-8")
    changed = raw[:1] + len(body).to_bytes(4, "little") + body
    open(dst, "w", encoding="utf-8").write(text.replace(base64.b64encode(raw).decode(), base64.b64encode(changed).decode()))
    return raw, changed


# A missing plug-in: make one machine's plug-in unknown (path, id and name), open, save: its state survives.
if names and names[0] in s1:
    victim = names[0]

    def unknown(xml):
        for tag, value in (("Path", "/nowhere/missing-plugin.vst3"), ("Id", "no-such-plugin"), ("Name", "No Such Plugin")):
            xml = re.sub(rf"<{tag}>.*?</{tag}>", f"<{tag}>{value}</{tag}>", xml)
        return xml

    _, changed = edit_state(song, os.path.join(work, "h1-missing.bmxml"), victim, unknown)
    check(call("song_open", path=os.path.join(work, "h1-missing.bmxml"), discard_unsaved=True)[0], "song_open with a missing plug-in")
    resaved = os.path.join(work, "h1-missing-resaved.bmxml")
    check(call("song_save", path=resaved, overwrite=True)[0], "song_save with a missing plug-in")
    check(machine_states(resaved).get(victim) == changed, f"{victim}: a missing plug-in's state is saved back unchanged")
    check("plug-in not found" in "\n".join(stderr_lines()), "the machine reports the plug-in missing")

if args.fixtures:
    # --- A sidechain: Test Duck ducks the synth while the second input has sound -------------------------------------------
    call("song_open", path=song, discard_unsaved=True)
    ok, _, _ = call("machine_add", gear="Neo Plug", plugin=resolve("Test Synth", gen_names, "instrument"), name="Pad", tracks=2)
    call("pattern_create", machine="Pad", name="A", length_beats=8)
    call("notes_write", machine="Pad", pattern="A", notes=[{"pitch": "C3", "start": 0, "length": 8}, {"pitch": "G3", "start": 0, "length": 8}])
    call("arrangement_place", machine="Pad", pattern="A", at=[0])
    call("machine_add", gear="Neo Poly", name="Kick")
    call("pattern_create", machine="Kick", name="A", length_beats=8)
    call("notes_write", machine="Kick", pattern="A", notes=[{"pitch": "C2", "start": b, "length": 0.5} for b in range(8)])
    call("arrangement_place", machine="Kick", pattern="A", at=[0])
    ok, _, _ = call("machine_add", gear="Neo Plug FX", plugin=resolve("Test Duck", fx_names, "effect"), name="Duck", insert_after="Pad")
    check(ok, "machine_add Duck after Pad")
    check(call("connect", **{"from": "Kick", "to": "Duck", "input": "Sidechain", "gain_db": 0})[0], "the Kick feeds Duck's sidechain")
    call("connection_set", **{"from": "Kick", "to": "Master", "gain_db": -60}) if False else None
    ok, _, quiet = call("render", **{"from": 0, "to": 8, "tail_seconds": 1, "probes": ["Duck"], "mute": ["Kick"], "name": "no-key"})
    ok, _, keyed = call("render", **{"from": 0, "to": 8, "tail_seconds": 1, "probes": ["Duck"], "name": "keyed"})
    la, lb = probe_levels(quiet).get("Duck", (None, None))[0], probe_levels(keyed).get("Duck", (None, None))[0]
    check(la is not None and lb is not None and la - lb > 2.0, f"the sidechain ducks the pad: {la} LUFS without the kick, {lb} with it")
    check("latency 64" in "\n".join(stderr_lines()), "a plug-in's latency (64 samples) is read")

    # --- MIDI out: Test Octaver plays a note an octave up on the machine it is sent to --------------------------------
    call("song_new", discard_unsaved=True)
    call("song_set", tempo_bpm=120, song_end_beats=8)
    call("machine_add", gear="Neo Plug", plugin=resolve("Test Synth", gen_names, "instrument"), name="Voice", tracks=1)
    call("machine_add", gear="Neo Plug", plugin=resolve("Test Octaver", gen_names, "instrument"), name="Octaver", tracks=1)
    call("pattern_create", machine="Octaver", name="A", length_beats=8)
    call("notes_write", machine="Octaver", pattern="A", notes=[{"pitch": "C4", "start": 0, "length": 4}])
    call("arrangement_place", machine="Octaver", pattern="A", at=[0])
    ok, _, r0 = call("render", **{"from": 0, "to": 8, "tail_seconds": 1, "probes": ["Voice"], "name": "midi-off"})
    check(probe_levels(r0).get("Voice", (None, True))[1] is True, "Voice is silent while the Octaver's MIDI goes nowhere")
    midi_song = os.path.join(work, "midi.bmxml")
    call("song_save", path=midi_song, overwrite=True)
    edit_state(midi_song, midi_song, "Octaver", lambda xml: xml.replace("</PlugState>", "<MidiOut>Voice</MidiOut></PlugState>") if "<MidiOut" not in xml else re.sub(r"<MidiOut\s*/>|<MidiOut>.*?</MidiOut>", "<MidiOut>Voice</MidiOut>", xml))
    call("song_open", path=midi_song, discard_unsaved=True)
    ok, _, r1m = call("render", **{"from": 0, "to": 8, "tail_seconds": 1, "probes": ["Voice"], "name": "midi-on"})
    lvl = probe_levels(r1m).get("Voice", (None, True))
    check(lvl[1] is False, f"Voice plays what the Octaver sends it ({lvl[0]} LUFS)")

proc.stdin.close()
try:
    code = proc.wait(60)
except subprocess.TimeoutExpired:
    proc.kill()
    code = None
check(code == 0, f"exits when stdin closes (code {code})")
results["ok"] = not failures
results["failures"] = failures
json.dump(results, open(result_path, "w"), indent=1)
if failures:
    for line in stderr_lines():   # with NEOPLUG_DEBUG=1: what was saved and loaded
        if "[neoplug] save:" in line or "[neoplug] load:" in line: print(line[:300])
print(f"\n{len(failures)} failed" if failures else "\nall passed")
if failures:
    print("stderr tail:\n" + "\n".join(stderr_lines()[-60:]))
sys.exit(1 if failures else 0)
