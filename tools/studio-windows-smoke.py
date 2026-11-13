#!/usr/bin/env python3
# Fork (docs/specs/ux-overhaul.md P6): a smoke test of NeoBuzz Live Studio (preview), the published exe on Windows
# (Studio\NeoBuzzLiveStudio.exe of the package, or build\Studio from tools/publish-studio-windows.ps1). It checks that:
#  - --list-devices loads PortAudio and lists the audio devices, and the WinMM MIDI ports (none is needed on a runner);
#  - the app starts (its window, the engine, the machine scan) and its MCP server, allowed for this run with
#    --agent-port and --agent-token (nothing saved), answers as "studio" (the runner has no sound card: --no-audio,
#    the null device: the song plays on the engine's clock, and renders offline);
#  - Neo Poly plays and renders audibly, and a saved song opens again and renders the same;
#  - with --song, a song of native Buzz machines (--native-gear: gear that must load, x64 in its host, x86 through the 32-bit
#    bridge) opens and renders, its other machines audible, and the native ones are not "missing";
#  - the transport plays and stops;
#  - the app quits when its window closes (WM_CLOSE: what the user's close button sends), with exit code 0.
#
#   python tools/studio-windows-smoke.py <NeoBuzzLiveStudio.exe> [work folder] [--song <bmx>] [--native-gear "FSM Infector,Jeskola Bass 3"]

import ctypes
import json
import os
import secrets
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

sys.stdout.reconfigure(encoding="utf-8", errors="replace")     # the runner's console is cp1252
args = list(sys.argv[1:])


def option(name):
    if name not in args:
        return None
    i = args.index(name)
    value = args[i + 1]
    del args[i:i + 2]
    return value


song_path = option("--song")
native_gear = [g for g in (option("--native-gear") or "").split(",") if g]
exe = os.path.abspath(args.pop(0))
work = os.path.abspath(args[0] if args else tempfile.mkdtemp(prefix="studio-smoke-"))
os.makedirs(work, exist_ok=True)
settings = os.path.join(work, "settings")
stderr_path = os.path.join(work, "studio-stderr.txt")
failures = []


def check(ok, what):
    print(("ok   " if ok else "FAIL ") + what, flush=True)
    if not ok:
        failures.append(what)
    return ok


def log_tail(n=60):
    try:
        with open(stderr_path, errors="replace") as f:
            return "".join(f.readlines()[-n:])
    except OSError:
        return ""


def finish(studio=None):
    if studio and studio.poll() is None:
        studio.kill()
    if failures:
        print(f"\n{len(failures)} failed")
        for f in failures:
            print("::error title=Studio smoke::" + f)
        print("studio log tail:\n" + log_tail())
    else:
        print("\nall passed")
    sys.exit(1 if failures else 0)


# --- devices: PortAudio (built from source, bundled) and WinMM
result = subprocess.run([exe, "--list-devices"], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120, cwd=os.path.dirname(exe))
print(result.stdout)
print(result.stderr)
check(result.returncode == 0 and "PortAudio" in result.stdout, f"--list-devices loads PortAudio ({result.stdout.splitlines()[0] if result.stdout else 'no output'})")
check("ASIO drivers (installed):" in result.stdout, "--list-devices lists the ASIO drivers (NAudio.Asio loads)")
check("MIDI inputs" in result.stdout and "MIDI outputs" in result.stdout, "--list-devices lists the WinMM MIDI ports")
apis = sorted({line.rsplit("(", 1)[-1].split(")")[0] for line in result.stdout.splitlines() if " out, " in line and "(" in line})
print(f"     audio host APIs with devices here: {', '.join(apis) or 'none (a runner without a sound card)'}")


class Http:
    def __init__(self, url, token):
        self.url, self.token, self.session = url, token, None
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def send(self, message, answer=True):
        headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream", "Authorization": "Bearer " + self.token}
        if self.session:
            headers["Mcp-Session-Id"] = self.session
        request = urllib.request.Request(self.url, data=json.dumps(message).encode(), headers=headers, method="POST")
        with self.opener.open(request, timeout=300) as response:
            self.session = response.headers.get("Mcp-Session-Id") or self.session
            body = response.read().decode()
        if not answer:
            return None
        if body.startswith("event:") or body.startswith("data:"):
            body = [line[5:] for line in body.splitlines() if line.startswith("data:")][-1]
        return json.loads(body)


with socket.socket() as s:
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
token = secrets.token_urlsafe(24)
url = f"http://127.0.0.1:{port}/mcp"
started = time.time()
studio = subprocess.Popen([exe, "--settings", settings, "--no-audio", "--agent-port", str(port), "--agent-token", token],
                          cwd=os.path.dirname(exe), stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=open(stderr_path, "w"))
print(f"started {exe} (pid {studio.pid}), agents on {url}", flush=True)
transport = Http(url, token)
next_id = 0


def request(method, params=None):
    global next_id
    next_id += 1
    return transport.send({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params or {}})


def call(tool, **arguments):
    r = request("tools/call", {"name": tool, "arguments": arguments})
    res = r.get("result") or {}
    text = res.get("content", [{}])[0].get("text", "") if res else json.dumps(r.get("error"))
    print(f"     {tool}: {text[:240]}", flush=True)
    return (not res.get("isError", True)) and "error" not in r, text, (res.get("structuredContent") or {})


init, until = None, time.time() + 600       # the first start scans every machine in Gear (native ones in their hosts)
while init is None:
    if studio.poll() is not None:
        check(False, f"the studio exited ({studio.returncode}) before agents could connect")
        finish()
    try:
        init = request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "studio-smoke", "version": "1"}})
    except (urllib.error.URLError, ConnectionError, OSError):
        if time.time() > until:
            check(False, "the studio's agent server didn't answer in 10 minutes")
            finish(studio)
        time.sleep(1)
check(init.get("result", {}).get("serverInfo", {}).get("name") == "neobuzz-live", f"MCP initialize answers ({time.time() - started:.0f} s from start, machine scan included)")
transport.send({"jsonrpc": "2.0", "method": "notifications/initialized"}, answer=False)
tools = request("tools/list")["result"]["tools"]
check(len(tools) >= 50, f"{len(tools)} tools")

ok, _, status = call("studio_status")
check(ok and status.get("mode") == "studio", f"studio mode ({status.get('mode')})")
ok, _, gear = call("gear_list", limit=1000, include_unhealthy=True)
names = {g.get("name"): g for g in gear.get("gear", [])}
check("Neo Poly" in names and "Neo Verb" in names, f"the Neo machines are installed ({len(names)} gear in all)")
natives = {n: g for n, g in names.items() if g.get("author") and not n.startswith("Neo ")}
print(f"     {len(natives)} other machines listed here")

# --- the null device: Neo Poly plays a song and renders
check(call("song_set", tempo_bpm=120, song_end_beats=8)[0], "song_set")
check(call("machine_add", gear="Neo Poly", name="Keys")[0], "machine_add Neo Poly")
check(call("pattern_create", machine="Keys", name="A", length_beats=8)[0], "pattern_create")
check(call("notes_write", machine="Keys", pattern="A", notes=[{"pitch": p, "start": 0, "length": 8} for p in ("C4", "E4", "G4")])[0], "notes_write")
check(call("arrangement_place", machine="Keys", pattern="A", at=[0])[0], "arrangement_place")
ok, _, r1 = call("render", **{"from": 0, "to": 8, "tail_seconds": 1})
loud = (r1.get("measure") or {}).get("loudness", {}).get("integrated_lufs")
check(ok and isinstance(loud, (int, float)) and -45 < loud < -6, f"render is audible ({loud} LUFS)")
ok, _, t = call("transport", action="play", at="1.1")
check(ok, "transport play")
time.sleep(1)
check(call("transport", action="stop")[0], "transport stop")

mine = os.path.join(work, "smoke.bmxml")
check(call("song_save", path=mine, overwrite=True)[0], "song_save")
check(call("song_open", path=mine, discard_unsaved=True)[0], "song_open (the saved song)")
ok, _, r2 = call("render", **{"from": 0, "to": 8, "tail_seconds": 1})
loud2 = (r2.get("measure") or {}).get("loudness", {}).get("integrated_lufs")
check(ok and loud2 == loud, f"the reopened song renders the same ({loud2} LUFS)")

# --- native Buzz machines: x64 in its host, x86 through the 32-bit bridge, and a song with them
for g in native_gear:
    row = names.get(g)
    check(row is not None and row.get("health") not in ("missing", "crashed"), f"native machine {g!r} is installed and not missing ({(row or {}).get('health', 'ok')})")
if song_path:
    check(call("song_open", path=os.path.abspath(song_path), discard_unsaved=True)[0], f"song_open a song of native machines ({os.path.basename(song_path)})")
    ok, _, listed = call("machine_list")
    gears = [m.get("gear") for m in listed.get("machines", [])]
    print(f"     its machines: {gears}")
    ok, _, r3 = call("render", **{"from": 0, "to": 8, "tail_seconds": 1})
    loud3 = (r3.get("measure") or {}).get("loudness", {}).get("integrated_lufs")
    check(ok and isinstance(loud3, (int, float)) and loud3 > -70, f"it renders, and is audible ({loud3} LUFS)")
    ok, _, again = call("gear_list", limit=1000, include_unhealthy=True)
    health = {g.get("name"): g.get("health") for g in again.get("gear", [])}
    states = ", ".join(g + ": " + str(health.get(g, "ok")) for g in native_gear)
    check(all(health.get(g) not in ("missing", "crashed") for g in native_gear), "no native machine crashed (" + states + ")")
bits = [g for g in native_gear if g]
for g in bits[1:]:      # the others (x86 ones, by the caller's choice), added to a new song with a note
    check(call("song_new", discard_unsaved=True)[0], "song_new")
    kind = names.get(g, {}).get("kind")
    ok, _, _ = call("machine_add", gear=g, name="N")
    check(ok, f"machine_add {g} (a {kind})")
    ok, _, rn = call("render", **{"from": 0, "to": 2, "tail_seconds": 0.5})
    check(ok, f"{g} renders in its host")

# --- the window closes: the exit code is the app's
if song_path:
    call("song_open", path=os.path.abspath(song_path), discard_unsaved=True)       # nothing unsaved: closing asks nothing
else:
    call("song_open", path=mine, discard_unsaved=True)
user32 = ctypes.windll.user32
WM_CLOSE = 0x0010
found = []
EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)


def each_window(hwnd, _):
    pid = ctypes.c_ulong()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    if pid.value == studio.pid and user32.IsWindowVisible(hwnd):
        found.append(hwnd)
    return True


user32.EnumWindows(EnumProc(each_window), 0)
check(len(found) > 0, f"the studio's window is up ({len(found)} visible)")
for hwnd in found:
    user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
try:
    code = studio.wait(60)
except subprocess.TimeoutExpired:
    code = None
check(code == 0, f"it quits when its window closes (exit code {code})")
log = open(stderr_path, errors="replace").read()
check("Agent: listening on 127.0.0.1:%d" % port in log, "its log says where agents connect")
finish(studio)
