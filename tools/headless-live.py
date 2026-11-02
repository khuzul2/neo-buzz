#!/usr/bin/env python3
# Fork (docs/specs/mac-engine.md M5): rebuzz-headless playing through an audio device, as an MCP
# client drives it in live mode. It checks that:
#  - the device opens and the server says "live";
#  - a phrase played with midi_play while the transport is stopped is heard (meters_read);
#  - the transport plays the arrangement in real time, and is heard, and stops;
#  - a render still works while the device plays;
#  - the server exits when stdin closes (closing the device).
# Linux CI runs it on a PulseAudio null sink, macOS CI on the runner's virtual sound device.
#
#   python3 tools/headless-live.py <path to rebuzz-headless> [work folder] [--device <name>]

import json
import os
import subprocess
import sys
import tempfile
import time

args = [a for a in sys.argv[1:]]
device = None
if "--device" in args:
    i = args.index("--device")
    device = args[i + 1]
    del args[i:i + 2]
exe = args[0]
work = args[1] if len(args) > 1 else tempfile.mkdtemp(prefix="rebuzz-live-")
os.makedirs(work, exist_ok=True)
settings = os.path.join(work, "settings")
stderr_path = os.path.join(work, "stderr.txt")
failures = []


def check(ok, what):
    print(("ok   " if ok else "FAIL ") + what, flush=True)
    if not ok:
        failures.append(what)


listing = subprocess.run([exe, "--list-devices"], capture_output=True, text=True, timeout=60)
print("devices:\n  " + "\n  ".join((listing.stdout or listing.stderr).strip().splitlines()), flush=True)

command = [exe, "--agent", "--settings", settings, "--audio", "--buffer", "256"] + (["--device", device] if device else [])
proc = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=open(stderr_path, "w"), text=True, bufsize=1)
next_id = 0


def request(method, params=None):
    global next_id
    next_id += 1
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params or {}}) + "\n")
    proc.stdin.flush()
    line = proc.stdout.readline()
    if not line:
        raise SystemExit("rebuzz-headless closed stdout; stderr:\n" + open(stderr_path).read()[-4000:])
    return json.loads(line)


def call(tool, **arguments):
    r = request("tools/call", {"name": tool, "arguments": arguments})
    res = r.get("result") or {}
    text = res.get("content", [{}])[0].get("text", "") if res else json.dumps(r.get("error"))
    print(f"     {tool}: {text[:200]}", flush=True)
    data = res.get("structuredContent") or {}
    return (not res.get("isError", True)) and "error" not in r, text, data


def master(data):
    meters = data.get("meters") or [{}]
    return meters[0]


init = request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "live", "version": "1"}})
check(init.get("result", {}).get("serverInfo", {}).get("name") == "rebuzz", "initialize")
proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
proc.stdin.flush()

ok, _, status = call("studio_status")
check(ok and status.get("mode") == "live", f"live mode ({status.get('mode')}, {status.get('sample_rate')} Hz)")

check(call("machine_add", gear="Neo Poly", name="Keys")[0], "machine_add Neo Poly")

# A phrase with the transport stopped: it sounds at once.
ok, _, _ = call("midi_play", machine="Keys", start="now",
                notes=[{"pitch": p, "start": 0, "length": 16} for p in ("C4", "E4", "G4")])
check(ok, "midi_play a chord now")
ok, _, m = call("meters_read", window_seconds=1)
level = master(m).get("rms_dbfs")
check(ok and not master(m).get("silent", True) and isinstance(level, (int, float)) and level > -60, f"meters_read hears the chord ({level} dBFS RMS)")
check(call("midi_play", stop=True)[0], "midi_play stop lets go")

# The arrangement, played in real time.
check(call("song_set", tempo_bpm=120, song_end_beats=16)[0], "song_set")
check(call("pattern_create", machine="Keys", name="A", length_beats=16)[0], "pattern_create")
check(call("notes_write", machine="Keys", pattern="A",
           notes=[{"pitch": p, "start": 0, "length": 16} for p in ("D4", "F4", "A4")])[0], "notes_write")
check(call("arrangement_place", machine="Keys", pattern="A", at=[0])[0], "arrangement_place")
ok, _, t = call("transport", action="play", at="1.1")
check(ok, "transport play")
ok, _, m = call("meters_read", window_seconds=1)
level = master(m).get("rms_dbfs")
check(ok and m.get("playing") is True and not master(m).get("silent", True), f"meters_read hears the song playing ({level} dBFS RMS)")
# 120 BPM: two beats a second, if the device takes audio in real time (a clocked device does;
# ALSA's bare null device doesn't, so this check needs a real or virtual sound device).
ok1, _, t1 = call("transport", action="status")
s1 = time.time()
time.sleep(1.0)
ok2, _, t2 = call("transport", action="status")
s2 = time.time()
rate = ((t2.get("position_beats") or 0) - (t1.get("position_beats") or 0)) / (s2 - s1)
check(ok1 and ok2 and t2.get("playing") is True and 1.4 < rate < 2.6, f"the song plays in real time ({rate:.2f} beats/s at 120 BPM)")
ok, _, t = call("transport", action="stop")
check(ok and t.get("playing") is False, "transport stop")

# Renders work with the device open (it pauses while they run).
ok, _, r = call("render", **{"from": 0, "to": 4, "tail_seconds": 0.5})
loud = (r.get("measure") or {}).get("loudness", {}).get("integrated_lufs")
check(ok and isinstance(loud, (int, float)) and loud > -45, f"render with the device open ({loud} LUFS)")
ok, _, m = call("meters_read", window_seconds=0.5)
check(ok and m.get("playing") is False, "the device plays on after the render (meters_read answers)")

proc.stdin.close()
try:
    code = proc.wait(30)
except subprocess.TimeoutExpired:
    proc.kill()
    code = None
check(code == 0, f"exits when stdin closes, closing the device (code {code})")

log = open(stderr_path).read()
for line in log.splitlines():
    if "playing through" in line or "PortAudio" in line:
        print("     " + line.strip())
print(f"\n{len(failures)} failed" if failures else "\nall passed")
if failures:
    print("stderr tail:\n" + log[-3000:])
sys.exit(1 if failures else 0)
