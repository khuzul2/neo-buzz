#!/usr/bin/env python3
# Fork (docs/specs/studio-mvp.md S2): the 19 Pedal machines, built for net10.0 without their WPF
# GUIs, in rebuzz-headless, driven as an MCP client drives it. It checks that:
#  - every one is installed and loads;
#  - each synth (M1, Juno106, SH101, FM, Tracker) sounds when its pattern plays a note;
#  - each effect passes a Neo Poly chord and sounds;
#  - Pedal Chord plays a chord on its target, and Pedal LFO moves its target's parameter;
#  - Pedal Peer Control loads and the whole song renders with it;
#  - Pedal Plate stays bounded and its tail decays at full Decay and modulation, and with Feedback
#    at 80 and at its maximum (95), where its outer loop diverged (machine-safety.md §1).
# The synths take notes in their Note track parameter only (no MIDI), which the agent's tools
# don't write (notes_write sends MIDI), so the script adds the note columns to the saved song and
# reopens it; the targets of Chord and LFO (set by their WPF dialogs on Windows) go into their
# saved state the same way. Tracker plays a wave cut from a render (wave_from_render).
#
#   python3 tools/headless-pedal.py <path to rebuzz-headless> [work folder]

import array
import base64
import json
import math
import os
import struct
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

exe = sys.argv[1]
work = sys.argv[2] if len(sys.argv) > 2 else tempfile.mkdtemp(prefix="rebuzz-pedal-")
os.makedirs(work, exist_ok=True)
settings = os.path.join(work, "settings")
song = os.path.join(work, "pedal.bmxml")
failures = []

SYNTHS = ["Pedal M1", "Pedal Juno106", "Pedal SH101", "Pedal FM", "Pedal Tracker"]
EFFECTS = ["Pedal Chorus", "Pedal Comp", "Pedal Drum Matrix", "Pedal EQ", "Pedal Filter", "Pedal Gain",
           "Pedal Gain Multi", "Pedal Gate", "Pedal MComp", "Pedal Plate", "Pedal Shaper"]
CONTROLS = ["Pedal Chord", "Pedal LFO", "Pedal Peer Control"]
TPB = 960            # pattern column time per beat (BMXML)
C4, OFF = 0x41, 0xFF  # Buzz notes: octave << 4 | 1..12; note off


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


def audible(measure):
    """Sound, and not blown up (a diverging machine reaches hundreds of dB, then infinity)."""
    peak = (measure or {}).get("peaks", {}).get("sample_peak_dbfs")
    return isinstance(peak, (int, float)) and not math.isnan(peak) and -60 < peak < 24 and not measure.get("silent", True)


def peak_of(measure):
    return (measure or {}).get("peaks", {}).get("sample_peak_dbfs")


def wav_levels(path, windows):
    """RMS (dBFS) of a render's 32-bit float WAV over each (from, to) window in seconds, both channels."""
    with open(path, "rb") as f:
        data = f.read()
    pos, channels, rate, samples = 12, 2, 48000, array.array("f")
    while pos + 8 <= len(data):
        cid, size = data[pos:pos + 4], struct.unpack_from("<I", data, pos + 4)[0]
        body = data[pos + 8:pos + 8 + size]
        if cid == b"fmt ":
            _, channels, rate = struct.unpack_from("<HHI", body)
        elif cid == b"data":
            samples.frombytes(body[:len(body) - len(body) % 4])
            if sys.byteorder != "little":
                samples.byteswap()
        pos += 8 + size + (size & 1)
    out = []
    for a, b in windows:
        x = samples[int(a * rate) * channels:int(b * rate) * channels]
        s = sum(v * v for v in x) / max(1, len(x))
        out.append(10 * math.log10(s) if s > 0 and math.isfinite(s) else (-math.inf if s == 0 else math.inf))
    return out


def short(gear):
    # Song files encode spaces in names (XmlConvert.EncodeName): names here have none.
    return gear.replace("Pedal ", "").replace(" ", "")


request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "pedal", "version": "1"}})
proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
proc.stdin.flush()

ok, text, data = call("gear_list", search="Pedal", limit=100)
installed = sorted(g["name"] for g in data.get("gear", []))
expected = sorted(SYNTHS + EFFECTS + CONTROLS)
check(installed == expected, f"the 19 Pedal machines are installed ({len(installed)}; missing: {sorted(set(expected) - set(installed))})")

check(call("song_set", tempo_bpm=120, song_end_beats=4)[0], "song_set")

# A Neo Poly chord: the effects' input, and (rendered once) the wave Pedal Tracker plays.
check(call("machine_add", gear="Neo Poly", name="Src")[0], "machine_add Neo Poly (the source)")
call("pattern_create", machine="Src", name="A", length_beats=4)
call("notes_write", machine="Src", pattern="A", notes=[{"pitch": p, "start": 0, "length": 3} for p in ("C4", "E4", "G4")])
call("arrangement_place", machine="Src", pattern="A", at=[0])
ok, _, r = call("render", **{"from": 0, "to": 4, "tail_seconds": 0.5})
check(ok and audible(r.get("measure")), "the source renders")
check(call("wave_from_render", source=r.get("id", "r1"), from_seconds=0, to_seconds=1.5, slot=1, name="Chord", root_note="C4")[0],
      "wave_from_render: a wave for Pedal Tracker")
check(call("disconnect", **{"from": "Src", "to": "Master"})[0], "the source feeds only the effects")

for gear in SYNTHS:
    name = "P-" + short(gear)
    check(call("machine_add", gear=gear, name=name)[0], f"machine_add {gear}")
    call("pattern_create", machine=name, name="A", length_beats=4)
    call("arrangement_place", machine=name, pattern="A", at=[0])

for gear in EFFECTS:
    name = "E-" + short(gear)
    check(call("machine_add", gear=gear, name=name, gain_db=-12)[0], f"machine_add {gear}")
    check(call("connect", **{"from": "Src", "to": name})[0], f"the source feeds {gear}")

# Pedal Chord plays chords on "ChordJuno"; Pedal LFO moves "LfoJuno"'s VCF Freq. The targets come
# first in the song, so they are there when the controllers' saved targets resolve on load.
check(call("machine_add", gear="Pedal Juno106", name="ChordJuno")[0], "machine_add Pedal Chord's target")
check(call("machine_add", gear="Pedal Juno106", name="LfoJuno")[0], "machine_add Pedal LFO's target")
check(call("machine_add", gear="Pedal Chord", name="Chord", connect_to="none")[0], "machine_add Pedal Chord")
call("pattern_create", machine="Chord", name="A", length_beats=4)
call("arrangement_place", machine="Chord", pattern="A", at=[0])
check(call("machine_add", gear="Pedal LFO", name="LFO", connect_to="none")[0], "machine_add Pedal LFO")
call("pattern_create", machine="LFO", name="A", length_beats=4)
call("arrangement_place", machine="LFO", pattern="A", at=[0])
check(call("machine_add", gear="Pedal Peer Control", name="Peer", connect_to="none")[0], "machine_add Pedal Peer Control")
check(call("song_save", path=song, overwrite=True)[0], "song_save")

# The notes and the targets, written into the saved song.
tree = ET.parse(song)
machines = {m.findtext("Name"): m for m in tree.getroot().find("Machines")}


def note_column(machine_name, events):
    m = machines[machine_name]
    groups = list(m.find("ParameterGroups"))
    index = [p.findtext("Type") for p in groups[2].find("Parameters")].index("Note")
    pattern = next(p for p in m.find("Patterns") if p.findtext("Name") == "A")
    columns = pattern.find("Columns")
    if columns is None:
        columns = ET.SubElement(pattern, "Columns")
    c = ET.SubElement(columns, "PatternColumn")
    ET.SubElement(c, "Machine").text = machine_name
    ET.SubElement(c, "Group").text = "2"
    ET.SubElement(c, "IndexInGroup").text = str(index)
    ET.SubElement(c, "Track").text = "0"
    ev = ET.SubElement(c, "Events")
    for time, value in events:
        e = ET.SubElement(ev, "ColumnEvent")
        ET.SubElement(e, "Time").text = str(time)
        ET.SubElement(e, "Value").text = str(value)
        ET.SubElement(e, "Duration").text = "0"
    # The engine plays a pattern's parameter columns that carry this mark (as automation_write's).
    md = ET.SubElement(ET.SubElement(c, "Metadata"), "Metadata")
    ET.SubElement(md, "Key").text = "SessionAutomation"
    ET.SubElement(md, "Value").text = "1"


def machine_state(machine_name, fields):
    """Sets fields of a managed machine's saved state (version byte, int32 length, XML)."""
    m = machines[machine_name]
    raw = base64.b64decode(m.findtext("Data"))
    xml = raw[5:].decode("utf-8-sig")
    state = ET.fromstring(xml)
    for k, v in fields.items():
        state.find(k).text = v
    body = b"\xef\xbb\xbf" + ET.tostring(state, encoding="utf-8", xml_declaration=True)
    m.find("Data").text = base64.b64encode(raw[:1] + len(body).to_bytes(4, "little") + body).decode()


for gear in SYNTHS:
    note_column("P-" + short(gear), [(0, C4), (3 * TPB, OFF)])
note_column("Chord", [(0, C4), (3 * TPB, OFF)])
machine_state("Chord", {"TargetMachine": "ChordJuno"})
machine_state("LFO", {"TargetMachineName": "LfoJuno", "TargetParameterName": "VCF Freq"})
tree.write(song, encoding="utf-8", xml_declaration=True)

check(call("song_open", path=song, discard_unsaved=True)[0], "song_open with the notes and targets")
_, _, before = call("param_get", machine="LfoJuno", params=["VCF Freq"])    # as saved: Juno's default

for gear in SYNTHS:
    name = "P-" + short(gear)
    ok, _, r = call("render", **{"from": 0, "to": 4, "tail_seconds": 0.5, "solo": [name]})
    check(ok and audible(r.get("measure")), f"{gear} plays a note ({peak_of(r.get('measure'))} dBFS peak)")

# Each effect's own output, probed in one render (solo would keep the source's other outputs too).
effects = ["E-" + short(g) for g in EFFECTS]
ok, _, r = call("render", **{"from": 0, "to": 4, "tail_seconds": 0.5, "probes": effects})
probes = {p.get("machine"): p.get("measure") for p in r.get("probes", [])}
for gear, name in zip(EFFECTS, effects):
    check(ok and audible(probes.get(name)), f"{gear} sounds with the chord through it ({peak_of(probes.get(name))} dBFS peak)")

# Pedal Plate's tank diverged at its default Decay until its modulated all-pass was fixed (studio-mvp.md S2):
# the check above runs it at the default; here at full Decay and modulation, with a long tail.
check(call("param_set", machine="E-Plate", changes=[{"param": "Decay", "value": "raw:100"}, {"param": "ModDepth", "value": "raw:100"},
                                                  {"param": "ModRate", "value": "raw:100"}])[0], "Pedal Plate at full Decay and modulation")
ok, _, r = call("render", **{"from": 0, "to": 4, "tail_seconds": 3, "probes": ["E-Plate"]})
probe = next((p for p in r.get("probes", []) if p.get("machine") == "E-Plate"), {})
check(ok and audible(probe.get("measure")), f"Pedal Plate stays bounded at full Decay ({peak_of(probe.get('measure'))} dBFS peak)")


def plate_decays(what, changes):
    """Pedal Plate with these changes: bounded, and its tail decays (the chord ends at 1.5 s; 6 s of tail)."""
    ok, text, _ = call("param_set", machine="E-Plate", changes=changes)
    if not ok:   # a draft manifest's safe_range keeps agents inside it (machine-safety.md §3.4)
        ok, text, _ = call("param_set", machine="E-Plate", changes=changes, allow_unsafe=True)
    check(ok, f"Pedal Plate {what}: param_set")
    ok, _, r = call("render", **{"from": 0, "to": 4, "tail_seconds": 6, "probes": ["E-Plate"]})
    probe = next((p for p in r.get("probes", []) if p.get("machine") == "E-Plate"), {})
    early, late = wav_levels(probe["file"], [(3, 4), (7, 8)]) if probe.get("file") else (math.nan, math.nan)
    check(ok and audible(probe.get("measure")) and late < early - 3,
          f"Pedal Plate {what}: bounded ({peak_of(probe.get('measure'))} dBFS peak) and decaying ({early:.1f} dB at 3 s, {late:.1f} dB at 7 s)")


# Its outer Feedback loop diverged above about 50 at the default Decay (the profiler saw 80) and
# above about 10 at full Decay: the feedback is now capped below the loop's edge.
plate_decays("Feedback 80 at the default Decay", [{"param": "Decay", "value": "default"}, {"param": "Feedback", "value": "raw:80"}])
plate_decays("Feedback 95 (its maximum) at the default Decay", [{"param": "Feedback", "value": "raw:95"}])
plate_decays("Feedback 95 at full Decay and modulation", [{"param": "Decay", "value": "raw:100"}])
check(not call("param_set", machine="E-Plate", changes=[{"param": "Feedback", "value": "raw:100"}])[0],
      "Pedal Plate's Feedback stops at 95 (raw:100 is refused)")
call("param_set", machine="E-Plate", changes=[{"param": "Feedback", "value": "default"}, {"param": "Decay", "value": "default"}])

ok, _, r = call("render", **{"from": 0, "to": 4, "tail_seconds": 0.5, "probes": ["ChordJuno"]})
probe = next((p for p in r.get("probes", []) if p.get("machine") == "ChordJuno"), {})
check(ok and audible(r.get("measure")), f"the whole song renders with Chord, LFO and Peer Control in it ({peak_of(r.get('measure'))} dBFS peak)")
check(audible(probe.get("measure")), f"Pedal Chord plays its target ({peak_of(probe.get('measure'))} dBFS peak)")
_, _, after = call("param_get", machine="LfoJuno", params=["VCF Freq"])
check(json.dumps(before) != json.dumps(after), "Pedal LFO moved its target's VCF Freq while the song played")

stderr = open(os.path.join(work, "stderr.txt")).read()
check("Exception" not in stderr, "no exceptions on stderr")

proc.stdin.close()
try:
    code = proc.wait(30)
except subprocess.TimeoutExpired:
    proc.kill()
    code = None
check(code == 0, f"exits when stdin closes (code {code})")

print(f"\n{len(failures)} failed" if failures else "\nall passed")
if failures:
    print("stderr tail:\n" + stderr[-3000:])
sys.exit(1 if failures else 0)
