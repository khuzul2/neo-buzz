#!/usr/bin/env python3
# Fork (docs/specs/mac-engine.md M5, studio-mvp.md S9): ReBuzz playing through an audio device, as an
# MCP client drives it live: rebuzz-headless in live mode over stdio, or the studio (ReBuzz.Studio,
# the real app) in studio mode over Streamable HTTP with its token. It checks that:
#  - the device opens and the server says "live" (rebuzz-headless) or "studio";
#  - a phrase played with midi_play while the transport is stopped is heard (meters_read);
#  - the transport plays the arrangement in real time, and is heard, and stops;
#  - a Session View clip launches on its own (session_launch), plays and is heard, and stops;
#  - a take records what midi_play plays into an armed track (session_record), on the bar;
#  - a render still works while the device plays;
#  - studio_status reports the device, and (macOS, --expect-workgroup) render threads working in
#    the device's audio workgroup (M7) while four synths play in parallel;
#  - rebuzz-headless exits when stdin closes (closing the device); the studio started here quits
#    when the system asks it to (SIGTERM; it has unsaved changes, so it's the way CI stops it).
# Linux CI runs it on a PulseAudio null sink, macOS CI on the runner's virtual sound device.
#
#   python3 tools/headless-live.py <path to rebuzz-headless> [work folder] [--device <name>] [--expect-workgroup]
#   python3 tools/headless-live.py --studio <path to ReBuzz.Studio> [work folder] [--expect-workgroup]
#       (starts the app with --agent-port and --agent-token, a settings folder of its own, and audio)
#   python3 tools/headless-live.py --url <http://127.0.0.1:port/mcp> --token <token> [work folder]
#       (a studio already running, agents allowed)

import json
import os
import secrets
import signal
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

args = list(sys.argv[1:])


def option(name, flag=False):
    if name not in args:
        return None
    i = args.index(name)
    if flag:
        del args[i]
        return True
    value = args[i + 1]
    del args[i:i + 2]
    return value


expect_workgroup = option("--expect-workgroup", flag=True)
device = option("--device")
studio_exe = option("--studio")
url = option("--url")
token = option("--token")
exe = None if studio_exe or url else args.pop(0)
work = args[0] if args else tempfile.mkdtemp(prefix="rebuzz-live-")
os.makedirs(work, exist_ok=True)
settings = os.path.join(work, "settings")
stderr_path = os.path.join(work, "stderr.txt")
failures = []


def check(ok, what):
    print(("ok   " if ok else "FAIL ") + what, flush=True)
    if not ok:
        failures.append(what)


def log_tail():
    try:
        return open(stderr_path, errors="replace").read()[-4000:]
    except OSError:
        return ""


class Stdio:
    """rebuzz-headless --agent: one JSON-RPC message a line on stdin and stdout."""

    def __init__(self):
        listing = subprocess.run([exe, "--list-devices"], capture_output=True, text=True, timeout=60)
        print("devices:\n  " + "\n  ".join((listing.stdout or listing.stderr).strip().splitlines()), flush=True)
        command = [exe, "--agent", "--settings", settings, "--audio", "--buffer", "256"] + (["--device", device] if device else [])
        self.proc = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=open(stderr_path, "w"), text=True, bufsize=1)

    def send(self, message, answer=True):
        self.proc.stdin.write(json.dumps(message) + "\n")
        self.proc.stdin.flush()
        if not answer:
            return None
        line = self.proc.stdout.readline()
        if not line:
            raise SystemExit("rebuzz-headless closed stdout; stderr:\n" + log_tail())
        return json.loads(line)


class Http:
    """Streamable HTTP with a bearer token (the studio's server): a POST a message, JSON back."""

    def __init__(self, url, token):
        self.url, self.token, self.session = url, token, None
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))     # 127.0.0.1: never a proxy

    def send(self, message, answer=True):
        headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream",
                   "Authorization": "Bearer " + self.token}
        if self.session:
            headers["Mcp-Session-Id"] = self.session
        request = urllib.request.Request(self.url, data=json.dumps(message).encode(), headers=headers, method="POST")
        with self.opener.open(request, timeout=180) as response:
            self.session = response.headers.get("Mcp-Session-Id") or self.session
            body = response.read().decode()
        if not answer:
            return None
        if body.startswith("event:") or body.startswith("data:"):     # SSE (only with a progress token)
            body = [line[5:] for line in body.splitlines() if line.startswith("data:")][-1]
        return json.loads(body)


studio = None
if studio_exe:
    # The real app, with audio, agents allowed on a free port with a token of this run's own.
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]
    token = secrets.token_urlsafe(24)
    url = f"http://127.0.0.1:{port}/mcp"
    studio = subprocess.Popen([studio_exe, "--settings", settings, "--agent-port", str(port), "--agent-token", token],
                              stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=open(stderr_path, "w"))
    print(f"started {studio_exe} (pid {studio.pid}), agents on {url}", flush=True)

transport = Http(url, token) if url else Stdio()
next_id = 0


def request(method, params=None):
    global next_id
    next_id += 1
    return transport.send({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params or {}})


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


def track(state, machine):
    return next((t for t in (state or {}).get("session") or [] if t.get("machine") == machine), {})


init_params = {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "live", "version": "1"}}
if studio:
    # The app opens its window, starts the engine and then the agent server: wait for it.
    init, until = None, time.time() + 180
    while init is None:
        if studio.poll() is not None:
            raise SystemExit(f"the studio exited ({studio.returncode}) before agents could connect; its log:\n" + log_tail())
        try:
            init = request("initialize", init_params)
        except (urllib.error.URLError, ConnectionError, OSError):
            if time.time() > until:
                studio.kill()
                raise SystemExit("the studio's agent server didn't answer in 3 minutes; its log:\n" + log_tail())
            time.sleep(1)
else:
    init = request("initialize", init_params)
check(init.get("result", {}).get("serverInfo", {}).get("name") == "rebuzz", "initialize")
transport.send({"jsonrpc": "2.0", "method": "notifications/initialized"}, answer=False)

mode = "live" if isinstance(transport, Stdio) else "studio"
ok, _, status = call("studio_status")
check(ok and status.get("mode") == mode, f"{mode} mode ({status.get('mode')}, {status.get('sample_rate')} Hz)")

check(call("machine_add", gear="Neo Poly", name="Keys")[0], "machine_add Neo Poly")

# A phrase with the transport stopped: it sounds at once.
ok, _, _ = call("midi_play", machine="Keys", start="now",
                notes=[{"pitch": p, "start": 0, "length": 16} for p in ("C4", "E4", "G4")])
check(ok, "midi_play a chord now")
ok, _, m = call("meters_read", window_seconds=1)
level = master(m).get("rms_dbfs")
check(ok and not master(m).get("silent", True) and isinstance(level, (int, float)) and level > -60,
      f"meters_read hears the chord ({level} dBFS RMS over {master(m).get('seconds')} s)")
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
check(ok and m.get("playing") is True and not master(m).get("silent", True),
      f"meters_read hears the song playing ({level} dBFS RMS over {master(m).get('seconds')} s)")
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

# The Session View: a one-bar clip in scene 1, launched (it starts the transport), heard, stopped.
check(call("pattern_create", machine="Keys", name="Clip", length_beats=4)[0], "pattern_create a clip")
check(call("notes_write", machine="Keys", pattern="Clip",
           notes=[{"pitch": p, "start": 0, "length": 4} for p in ("E4", "G4", "B4")])[0], "notes_write the clip")
check(call("session_clip_set", machine="Keys", scene=1, pattern="Clip")[0], "session_clip_set")
ok, _, s = call("session_launch", scene=1)
check(ok and s.get("playing") is True, "session_launch scene 1 (the transport starts)")
playing, until = None, time.time() + 6
while playing != "Clip" and time.time() < until:
    time.sleep(0.25)
    ok, _, t = call("transport", action="status")
    playing = track(t, "Keys").get("playing")
check(playing == "Clip", f"the clip plays ({playing})")
ok, _, m = call("meters_read", window_seconds=1)
check(ok and not master(m).get("silent", True), f"meters_read hears the clip ({master(m).get('rms_dbfs')} dBFS RMS)")
ok, _, s = call("session_launch", stop=True, immediate=True)
check(ok, "session_launch stop")
until = time.time() + 4         # on the next tick the device renders
while playing and time.time() < until:
    time.sleep(0.2)
    ok, _, t = call("transport", action="status")
    playing = track(t, "Keys").get("playing")
check(ok and not playing, "the clip stopped")
check(call("session_launch", back_to_arrangement=True)[0], "back to the arrangement")
call("transport", action="stop")

# A take: session_record arms the track and starts a two-bar take (on the bar: it starts the
# transport); once it records, midi_play plays on the next bar into it (an armed track records what
# it's played); the take ends by itself; stop disarms.
ok, _, r = call("session_record", action="start", machine="Keys", scene=2, bars=2)
take = r.get("take")
check(ok and take and track(r.get("state"), "Keys").get("armed") is True, f"session_record start ({take})")
playing, until = None, time.time() + 6
while playing != take and time.time() < until:
    time.sleep(0.1)
    ok, _, t = call("transport", action="status")
    playing = track(t, "Keys").get("playing")
check(playing == take and track(t, "Keys").get("recording") is True, "the take records")
ok, _, p = call("midi_play", machine="Keys", start="bar",
                notes=[{"pitch": "C4", "start": 0, "length": 1, "velocity": 90}, {"pitch": "E4", "start": 1, "length": 1},
                       {"pitch": "G4", "start": 2, "length": 1}])
check(ok and p.get("recorded") is True and p.get("starts") != "now", f"midi_play into the take, from {p.get('starts')}")
recording, until = True, time.time() + 12      # two bars at 120 BPM: 4 s, after up to a bar's wait
while recording and time.time() < until:
    time.sleep(0.5)
    ok, _, r = call("session_record", action="status", machine="Keys")
    recording = track(r.get("state"), "Keys").get("recording") is True
check(not recording, "the take ended after its two bars")
ok, _, r = call("session_record", action="stop", machine="Keys")
check(ok and not track(r.get("state"), "Keys").get("armed"), "session_record stop disarms the track")
ok, _, n = call("notes_read", machine="Keys", pattern=take or "?")
pitches = sorted(x.get("pitch") for x in n.get("notes") or [])
check(ok and pitches == [60, 64, 67], f"the take has the notes played ({pitches})")
call("transport", action="stop")

# Four synths at once: the render workers take machines in parallel (the multi-threaded engine).
for i in range(2, 5):
    call("machine_add", gear="Neo Poly", name=f"Keys{i}")
    call("midi_play", machine=f"Keys{i}", start="now", notes=[{"pitch": 48 + 7 * i, "start": 0, "length": 16}])
call("midi_play", machine="Keys", start="now", notes=[{"pitch": "C3", "start": 0, "length": 16}])
ok, _, m = call("meters_read", window_seconds=1)
check(ok and not master(m).get("silent", True), "four synths play at once")
ok, _, status = call("studio_status")
audio = status.get("audio") or {}
print(f"     audio: {json.dumps(audio)}")
check(ok and audio.get("device") and audio.get("buffer_frames", 0) > 0 and audio.get("latency_ms", 0) > 0, "studio_status reports the audio output")
if expect_workgroup:
    check((audio.get("workgroup_threads") or 0) >= 1 and audio.get("render_threads", 1) > 1,
          f"render threads work in the device's audio workgroup ({audio.get('workgroup_threads')} of {audio.get('render_threads')})")
call("midi_play", stop=True)

# Renders work with the device open (it pauses while they run).
ok, _, r = call("render", **{"from": 0, "to": 4, "tail_seconds": 0.5})
loud = (r.get("measure") or {}).get("loudness", {}).get("integrated_lufs")
check(ok and isinstance(loud, (int, float)) and loud > -45, f"render with the device open ({loud} LUFS)")
ok, _, m = call("meters_read", window_seconds=0.5)
check(ok and m.get("playing") is False, "the device plays on after the render (meters_read answers)")

if isinstance(transport, Stdio):
    transport.proc.stdin.close()
    try:
        code = transport.proc.wait(30)
    except subprocess.TimeoutExpired:
        transport.proc.kill()
        code = None
    check(code == 0, f"exits when stdin closes, closing the device (code {code})")
elif studio:
    studio.send_signal(signal.SIGTERM)
    try:
        code = studio.wait(30)
    except subprocess.TimeoutExpired:
        studio.kill()
        code = None
    check(code is not None, f"the studio quits when the system asks (SIGTERM; code {code})")
else:
    print("     (the studio was already running: it stays open)")

log = log_tail() if not url or studio else ""
for line in log.splitlines():
    if "playing through" in line or "PortAudio" in line or "Agent:" in line:
        print("     " + line.strip())
if studio:
    check(f"Agent: listening on 127.0.0.1:{port}" in open(stderr_path, errors="replace").read(), "the studio's log says where agents connect")
print(f"\n{len(failures)} failed" if failures else "\nall passed")
if failures and log:
    print("log tail:\n" + log[-3000:])
sys.exit(1 if failures else 0)
