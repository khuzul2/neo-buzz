#!/usr/bin/env python3
# Fork (docs/specs/studio-mvp.md S2): the 19 Pedal machines, built for net10.0 without their WPF
# GUIs, in rebuzz-headless, driven as an MCP client drives it. It checks that:
#  - every one is installed and loads;
#  - each synth (M1, Juno106, SH101, FM, Tracker) sounds when its pattern plays a note;
#  - each effect passes a Neo Poly chord and sounds;
#  - Pedal Chord plays a chord on its target, and Pedal LFO moves its target's parameter;
#  - Pedal Peer Control loads and the whole song renders with it.
# The synths take notes in their Note track parameter only (no MIDI), which the agent's tools
# don't write (notes_write sends MIDI), so the script adds the note columns to the saved song and
# reopens it; the targets of Chord and LFO (set by their WPF dialogs on Windows) go into their
# saved state the same way. Tracker plays a wave cut from a render (wave_from_render).
#
#   python3 tools/headless-pedal.py <path to rebuzz-headless> [work folder]

import base64
import json
import math
import os
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

# Pedal Plate's tank diverges at its default Decay (75), on any platform: its modulated all-pass
# reads 1-17 samples behind its write position, not its length behind, which makes it a comb
# with a gain of up to 5.7 inside the tank's loop. Checked here at Decay 0; see studio-mvp.md S2.
check(call("param_set", machine="E-Plate", changes=[{"param": "Decay", "value": "raw:0"}])[0], "Pedal Plate at Decay 0")

# Each effect's own output, probed in one render (solo would keep the source's other outputs too).
effects = ["E-" + short(g) for g in EFFECTS]
ok, _, r = call("render", **{"from": 0, "to": 4, "tail_seconds": 0.5, "probes": effects})
probes = {p.get("machine"): p.get("measure") for p in r.get("probes", [])}
for gear, name in zip(EFFECTS, effects):
    check(ok and audible(probes.get(name)), f"{gear} sounds with the chord through it ({peak_of(probes.get(name))} dBFS peak)")

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
