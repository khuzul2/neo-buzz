#!/usr/bin/env python3
# Fork (docs/specs/mac-engine.md M6): rebuzz-headless's MIDI and Link on macOS, end to end, as
# another app on the same Mac sees them (CoreMIDI through ctypes). In live mode (--audio) it checks:
#  - a keyboard plugged in while ReBuzz runs (a virtual source this script makes, named with
#    --midi-in) opens, and its notes play the machine last added: meters_read hears them;
#  - another app sending to ReBuzz's virtual "ReBuzz" destination plays it too;
#  - MIDI clock out (--clock-out ReBuzz) reaches another app through ReBuzz's virtual "ReBuzz"
#    source: Start, 24 pulses a beat at the song's tempo, then Stop;
#  - --link starts the ReBuzzLink helper beside rebuzz-headless, and it ends with it.
# macOS CI runs it on the runner's virtual sound device.
#
#   python3 tools/headless-midi.py <path to rebuzz-headless> [work folder]

import ctypes
import json
import os
import platform
import struct
import subprocess
import sys
import tempfile
import time

exe = sys.argv[1]
work = sys.argv[2] if len(sys.argv) > 2 else tempfile.mkdtemp(prefix="rebuzz-midi-")
os.makedirs(work, exist_ok=True)
settings = os.path.join(work, "settings")
stderr_path = os.path.join(work, "stderr.txt")
failures = []
KEYS = "ReBuzz Test Keys"


def check(ok, what):
    print(("ok   " if ok else "FAIL ") + what, flush=True)
    if not ok:
        failures.append(what)


# ---------------------------------------------------------------- CoreMIDI, as another app

cm = ctypes.cdll.LoadLibrary("/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI")
cf = ctypes.cdll.LoadLibrary("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")
UTF8 = 0x08000100
Ref = ctypes.c_uint32
cf.CFStringCreateWithCString.restype = ctypes.c_void_p
cf.CFStringCreateWithCString.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_uint32]
cf.CFStringGetCString.restype = ctypes.c_bool
cf.CFStringGetCString.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_long, ctypes.c_uint32]
cf.CFRelease.argtypes = [ctypes.c_void_p]
cf.CFRunLoopRunInMode.restype = ctypes.c_int32
cf.CFRunLoopRunInMode.argtypes = [ctypes.c_void_p, ctypes.c_double, ctypes.c_bool]
DEFAULT_MODE = ctypes.c_void_p.in_dll(cf, "kCFRunLoopDefaultMode").value
READ = ctypes.CFUNCTYPE(None, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p)
NOTIFY = ctypes.CFUNCTYPE(None, ctypes.c_void_p, ctypes.c_void_p)
cm.MIDIClientCreate.argtypes = [ctypes.c_void_p, NOTIFY, ctypes.c_void_p, ctypes.POINTER(Ref)]
cm.MIDISourceCreate.argtypes = [Ref, ctypes.c_void_p, ctypes.POINTER(Ref)]
cm.MIDIOutputPortCreate.argtypes = [Ref, ctypes.c_void_p, ctypes.POINTER(Ref)]
cm.MIDIInputPortCreate.argtypes = [Ref, ctypes.c_void_p, READ, ctypes.c_void_p, ctypes.POINTER(Ref)]
cm.MIDIPortConnectSource.argtypes = [Ref, Ref, ctypes.c_void_p]
cm.MIDIGetNumberOfSources.restype = ctypes.c_ulong
cm.MIDIGetSource.restype = Ref
cm.MIDIGetSource.argtypes = [ctypes.c_ulong]
cm.MIDIGetNumberOfDestinations.restype = ctypes.c_ulong
cm.MIDIGetDestination.restype = Ref
cm.MIDIGetDestination.argtypes = [ctypes.c_ulong]
cm.MIDIObjectGetStringProperty.argtypes = [Ref, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
cm.MIDIReceived.argtypes = [Ref, ctypes.c_char_p]
cm.MIDISend.argtypes = [Ref, Ref, ctypes.c_char_p]
cm.MIDIEndpointDispose.argtypes = [Ref]
NAME = ctypes.c_void_p.in_dll(cm, "kMIDIPropertyName").value
ALIGNED = platform.machine() in ("arm64", "aarch64")


def cfstr(text):
    return cf.CFStringCreateWithCString(None, text.encode(), UTF8)


def name_of(endpoint):
    value = ctypes.c_void_p()
    if cm.MIDIObjectGetStringProperty(endpoint, NAME, ctypes.byref(value)) != 0 or not value.value:
        return None
    buf = ctypes.create_string_buffer(1024)
    cf.CFStringGetCString(value.value, buf, 1024, UTF8)
    cf.CFRelease(value.value)
    return buf.value.decode()


def names(sources):
    count = cm.MIDIGetNumberOfSources() if sources else cm.MIDIGetNumberOfDestinations()
    return [name_of(cm.MIDIGetSource(i) if sources else cm.MIDIGetDestination(i)) for i in range(count)]


def find(sources, name, seconds=5):
    # A client sees other processes' endpoints come and go as its run loop runs (the one current
    # when it was made: this thread's), so it runs while looking.
    end = time.monotonic() + seconds
    while True:
        count = cm.MIDIGetNumberOfSources() if sources else cm.MIDIGetNumberOfDestinations()
        for i in range(count):
            e = cm.MIDIGetSource(i) if sources else cm.MIDIGetDestination(i)
            if name_of(e) == name:
                return e
        if time.monotonic() > end:
            print(f"     the test sees sources {names(True)}, destinations {names(False)}")
            return None
        cf.CFRunLoopRunInMode(DEFAULT_MODE, 0.2, False)


def wait_for_log(text, seconds=5):
    start = time.monotonic()
    while time.monotonic() - start < seconds:
        if text in open(stderr_path).read():
            return time.monotonic() - start
        cf.CFRunLoopRunInMode(DEFAULT_MODE, 0.1, False)
    return None


def packets(data):
    # A MIDIPacketList of one packet: count, time stamp (0: now), length, bytes.
    return struct.pack("<IQH", 1, 0, len(data)) + bytes(data)


client = Ref()
notified = []
notify = NOTIFY(lambda message, ref_con: notified.append(time.monotonic()))
check(cm.MIDIClientCreate(cfstr("headless-midi test"), notify, None, ctypes.byref(client)) == 0, "a CoreMIDI client (the test's)")


def make_keys():
    source = Ref()
    status = cm.MIDISourceCreate(client, cfstr(KEYS), ctypes.byref(source))
    return source if status == 0 else None


# What rebuzz-headless lists, with the keyboard plugged in.
keys = make_keys()
check(keys is not None, f'a virtual source "{KEYS}" (the keyboard)')
listing = subprocess.run([exe, "--list-devices"], capture_output=True, text=True, timeout=60)
print("devices:\n  " + "\n  ".join((listing.stdout + listing.stderr).strip().splitlines()), flush=True)
check(("  " + KEYS) in listing.stdout, "--list-devices lists the keyboard as a MIDI input")
cm.MIDIEndpointDispose(keys)                       # unplugged until ReBuzz runs
time.sleep(0.5)

# ---------------------------------------------------------------- ReBuzz, live

command = [exe, "--agent", "--settings", settings, "--audio", "--buffer", "256",
           "--midi-in", KEYS, "--clock-out", "ReBuzz", "--link"]
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


def level(window=1.0):
    ok, _, m = call("meters_read", window_seconds=window)
    master = (m.get("meters") or [{}])[0]
    rms = master.get("rms_dbfs")
    heard = ok and not master.get("silent", True) and isinstance(rms, (int, float)) and rms > -60
    return heard, rms


init = request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "midi", "version": "1"}})
check(init.get("result", {}).get("serverInfo", {}).get("name") == "rebuzz", "initialize")
proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
proc.stdin.flush()
ok, _, status = call("studio_status")
check(ok and status.get("mode") == "live", f"live mode ({status.get('mode')})")
check(call("machine_add", gear="Neo Poly", name="Keys")[0], "machine_add Neo Poly (it takes the MIDI input)")
heard, rms = level(0.5)
check(not heard, f"silence before any note ({rms} dBFS RMS)")

# The keyboard, plugged in now: ReBuzz hears of it and opens it (it is in --midi-in).
keys = make_keys()
check(keys is not None, "the keyboard plugged in")
opened = wait_for_log(f"MIDI {KEYS}: open")
check(opened is not None, f"ReBuzz hears of it and opens it ({opened if opened is None else round(opened, 2)} s)")
time.sleep(0.2)
chord = [60, 64, 67]
for n in chord:
    cm.MIDIReceived(keys, packets([0x90, n, 100]))
heard, rms = level()
check(heard, f"the keyboard plays Keys: meters_read hears the chord ({rms} dBFS RMS)")
cm.MIDIReceived(keys, packets([b for n in chord for b in (0x80, n, 0)]))
time.sleep(1.5)
heard_after, rms_after = level(0.5)
check(not heard_after or (isinstance(rms, (int, float)) and isinstance(rms_after, (int, float)) and rms_after < rms - 20),
      f"note offs let go ({rms_after} dBFS RMS)")

# Another app, sending to ReBuzz's virtual destination.
rebuzz_in = find(False, "ReBuzz")
check(rebuzz_in is not None, 'ReBuzz\'s virtual destination "ReBuzz" is there for other apps')
if rebuzz_in is not None:
    out_port = Ref()
    cm.MIDIOutputPortCreate(client, cfstr("to ReBuzz"), ctypes.byref(out_port))
    cm.MIDISend(out_port, rebuzz_in, packets([0x90, 57, 100, 0x90, 60, 100, 0x90, 64, 100]))
    heard, rms = level()
    check(heard, f"another app plays ReBuzz through it ({rms} dBFS RMS)")
    cm.MIDISend(out_port, rebuzz_in, packets([0x80, 57, 0, 0x80, 60, 0, 0x80, 64, 0]))

# MIDI clock out, received from ReBuzz's virtual source.
received = []


def on_read(packet_list, ref_con, conn_ref_con):
    now = time.monotonic()
    count = ctypes.c_uint32.from_address(packet_list).value
    p = packet_list + 4
    for _ in range(count):
        length = ctypes.c_uint16.from_address(p + 8).value
        for b in ctypes.string_at(p + 10, length):
            if b >= 0xF8 or b in (0xF2,):
                received.append((b, now))
        p += 10 + length
        if ALIGNED:
            p = (p + 3) & ~3


reader = READ(on_read)
rebuzz_out = find(True, "ReBuzz")
check(rebuzz_out is not None, 'ReBuzz\'s virtual source "ReBuzz" is there for other apps')
if rebuzz_out is not None:
    in_port = Ref()
    cm.MIDIInputPortCreate(client, cfstr("from ReBuzz"), reader, None, ctypes.byref(in_port))
    cm.MIDIPortConnectSource(in_port, rebuzz_out, None)
    time.sleep(0.3)
    check(call("song_set", tempo_bpm=120, song_end_beats=64)[0], "song_set 120 BPM")
    check(call("transport", action="play", at="1.1")[0], "transport play")
    time.sleep(2.0)
    playing = list(received)
    check(call("transport", action="stop")[0], "transport stop")
    time.sleep(0.8)
    starts = [t for b, t in playing if b in (0xFA, 0xFB)]
    check(len(starts) > 0, f"clock out sent Start ({len(starts)})")
    pulses = [t for b, t in playing if b == 0xF8 and starts and t >= starts[0]]
    rate = (len(pulses) - 1) / (pulses[-1] - pulses[0]) if len(pulses) > 2 else 0
    check(40 < rate < 56, f"24 pulses a beat at 120 BPM: {rate:.1f} a second (48 expected), {len(pulses)} pulses")
    if len(pulses) > 10:
        gaps = sorted((b - a) * 1000 for a, b in zip(pulses, pulses[1:]))
        print(f"     pulse gaps as received here: median {gaps[len(gaps) // 2]:.2f} ms, 5% {gaps[len(gaps) // 20]:.2f}, 95% {gaps[-len(gaps) // 20 - 1]:.2f}")
    check(any(b == 0xFC for b, _ in received[len(playing):]), "and Stop when the transport stopped")

# Link: the helper runs beside rebuzz-headless while Link is on.
helper = subprocess.run(["pgrep", "-x", "ReBuzzLink"], capture_output=True, text=True).stdout.split()
check(len(helper) > 0, f"the ReBuzzLink helper runs ({' '.join(helper) or 'none'})")

proc.stdin.close()
try:
    code = proc.wait(30)
except subprocess.TimeoutExpired:
    proc.kill()
    code = None
check(code == 0, f"exits when stdin closes (code {code})")
time.sleep(0.5)
left = [p for p in subprocess.run(["pgrep", "-x", "ReBuzzLink"], capture_output=True, text=True).stdout.split() if p in helper]
check(not left, "the helper ended with it")
cm.MIDIEndpointDispose(keys)

log = open(stderr_path).read()
for line in log.splitlines():
    if "MIDI" in line or "Link" in line or "playing through" in line or "warning" in line or "error" in line:
        print("     " + line.strip())
print(f"     the test's client was notified {len(notified)} times")
print(f"\n{len(failures)} failed" if failures else "\nall passed")
if failures:
    print("stderr tail:\n" + log[-3000:])
sys.exit(1 if failures else 0)
