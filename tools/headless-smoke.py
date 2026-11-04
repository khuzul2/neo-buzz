#!/usr/bin/env python3
# Fork (docs/specs/mac-engine.md M1): rebuzz-headless end to end, as an MCP client drives it.
# One request at a time over stdio. It checks five things:
#  - the Neo gear is there;
#  - a chord played by Neo Poly through Neo Verb renders at a sane loudness and in tune;
#  - the song saves, reopens and renders the same;
#  - undo works;
#  - the server exits when stdin closes.
# Linux and macOS CI run it.
#
#   python3 tools/headless-smoke.py <path to rebuzz-headless> [work folder]

import json
import os
import subprocess
import sys
import tempfile
import time

exe = sys.argv[1]
work = sys.argv[2] if len(sys.argv) > 2 else tempfile.mkdtemp(prefix="rebuzz-smoke-")
os.makedirs(work, exist_ok=True)
settings = os.path.join(work, "settings")
song = os.path.join(work, "smoke.bmxml")
failures = []


def check(ok, what):
    print(("ok   " if ok else "FAIL ") + what, flush=True)
    if not ok:
        failures.append(what)


proc = subprocess.Popen([exe, "--agent", "--settings", settings], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=open(os.path.join(work, "stderr.txt"), "w"), text=True, bufsize=1)
next_id = 0


def request(method, params=None):
    global next_id
    next_id += 1
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params or {}}) + "\n")
    proc.stdin.flush()
    line = proc.stdout.readline()
    if not line:
        raise SystemExit("rebuzz-headless closed stdout; stderr:\n" + open(os.path.join(work, "stderr.txt")).read()[-4000:])
    return json.loads(line)


def call(tool, **args):
    r = request("tools/call", {"name": tool, "arguments": args})
    res = r.get("result") or {}
    text = res.get("content", [{}])[0].get("text", "") if res else json.dumps(r.get("error"))
    print(f"     {tool}: {text[:160]}", flush=True)
    data = res.get("structuredContent") or {}
    return (not res.get("isError", True)) and "error" not in r, text, data


started = time.time()
init = request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "1"}})
check(init.get("result", {}).get("serverInfo", {}).get("name") == "rebuzz", f"initialize ({time.time() - started:.1f} s from start)")
proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
proc.stdin.flush()

tools = request("tools/list")["result"]["tools"]
check(len(tools) >= 50, f"{len(tools)} tools")

ok, text, data = call("gear_list")
names = {g.get("name") for g in data.get("gear", [])}   # the text names the first dozen only
check(ok and "Neo Poly" in names and "Neo Verb" in names and "Neo Sampler" in names, "the portable Neo machines are installed")

ok, _, _ = call("song_set", tempo_bpm=120, song_end_beats=8)
check(ok, "song_set")
check(call("machine_add", gear="Neo Poly", name="Keys")[0], "machine_add Neo Poly")
check(call("pattern_create", machine="Keys", name="A", length_beats=8)[0], "pattern_create (with the Modern Pattern Editor, as on Windows)")
check(call("notes_write", machine="Keys", pattern="A",
           notes=[{"pitch": "C4", "start": 0, "length": 2}, {"pitch": "E4", "start": 2, "length": 2}, {"pitch": "G4", "start": 4, "length": 4}])[0], "notes_write")
check(call("arrangement_place", machine="Keys", pattern="A", at=[0])[0], "arrangement_place")
check(call("machine_add", gear="Neo Verb", name="Room", insert_after="Keys")[0], "machine_add Neo Verb after it")

ok, _, r1 = call("render", **{"from": 0, "to": 8, "tail_seconds": 1})
loud = (r1.get("measure") or {}).get("loudness", {}).get("integrated_lufs")
check(ok and isinstance(loud, (int, float)) and -45 < loud < -6, f"render r1 is audible ({loud} LUFS)")
ok, text, a = call("analyze", source="r1", metrics=["pitch"])
check(ok and "in tune" in text, "analyze hears it in tune")

check(call("song_save", path=song, overwrite=True)[0], "song_save")
check(call("song_open", path=song, discard_unsaved=True)[0], "song_open")
ok, _, r2 = call("render", **{"from": 0, "to": 8, "tail_seconds": 1})
loud2 = (r2.get("measure") or {}).get("loudness", {}).get("integrated_lufs")
check(ok and loud2 == loud, f"the reopened song renders the same ({loud2} LUFS)")
ok, text, _ = call("compare", a="r1", b="r2")
check(ok and "No measurable difference" in text, "compare: no measurable difference")

check(call("param_set", machine="Keys", changes=[{"param": "F1 cutoff", "value": "800 Hz"}])[0], "param_set in units")
ok, text, _ = call("param_get", machine="Keys", params=["F1 cutoff"])
check(ok and "800 Hz" in text, "param_get reads 800 Hz back")
check(call("undo")[0], "undo")
ok, text, _ = call("param_get", machine="Keys", params=["F1 cutoff"])
check(ok and "800 Hz" not in text, "undo restored the cutoff")

proc.stdin.close()
try:
    code = proc.wait(30)
except subprocess.TimeoutExpired:
    proc.kill()
    code = None
check(code == 0, f"exits when stdin closes (code {code})")

print(f"\n{len(failures)} failed" if failures else "\nall passed")
if failures:
    print("stderr tail:\n" + open(os.path.join(work, "stderr.txt")).read()[-3000:])
sys.exit(1 if failures else 0)
