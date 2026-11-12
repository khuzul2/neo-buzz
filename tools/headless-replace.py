#!/usr/bin/env python3
# Fork (docs/specs/mcp-server.md §12.5, §12.7 U5): machine_replace in rebuzz-headless, as an MCP client
# drives it, with the portable machines (the Pedal set and the Neo successors). It checks that:
#  - gear_list and machine_describe name a machine's successor;
#  - a dry run reports the mapping and the expected differences and changes nothing;
#  - Pedal EQ → Neo EQ and Pedal Gain → Neo Utility (U5's exit check, the portable half), Pedal Comp →
#    Neo Comp, Pedal Chorus → Neo Ensemble and Pedal Filter → Neo Filter convert within their stated
#    tolerance, on noise played through them, each read against its input; Pedal M1 → Neo Sampler (its key
#    zones, both oscillators, envelopes and filter) playing a line from the song's waves, its notes moved;
#  - the successor takes the old machine's place: its name, connections (gain and pan), parameters in
#    units (param_get), its patterns' automation (ramps kept), the arrangement and the Session View's track;
#  - one undo brings the original back exactly (machines, parameters, automation, arrangement, session),
#    and redo makes the replacement again;
#  - keep_old keeps the old machine, muted, beside the successor;
#  - a machine with no successor, or a successor it has no mapping to, is refused with the choices.
#
#   python3 tools/headless-replace.py <path to rebuzz-headless> [work folder]

import json
import math
import os
import struct
import subprocess
import sys
import tempfile

exe = os.path.abspath(sys.argv[1])
work = sys.argv[2] if len(sys.argv) > 2 else tempfile.mkdtemp(prefix="rebuzz-replace-")
os.makedirs(work, exist_ok=True)
failures = []


def check(ok, what):
    print(("ok   " if ok else "FAIL ") + what, flush=True)
    if not ok:
        failures.append(what)


class Server:
    def __init__(self, label):
        self.stderr = os.path.join(work, label + ".stderr.txt")
        self.proc = subprocess.Popen([exe, "--agent", "--settings", os.path.join(work, label + "-settings")], stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE, stderr=open(self.stderr, "w"), text=True, bufsize=1, encoding="utf-8")
        self.next_id = 0
        self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "replace", "version": "1"}})
        self.proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        self.proc.stdin.flush()

    def request(self, method, params=None):
        self.next_id += 1
        self.proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params or {}}) + "\n")
        self.proc.stdin.flush()
        line = self.proc.stdout.readline()
        if not line:
            raise SystemExit("rebuzz-headless closed stdout; stderr:\n" + open(self.stderr).read()[-4000:])
        return json.loads(line)

    def call(self, tool, **args):
        r = self.request("tools/call", {"name": tool, "arguments": args})
        res = r.get("result") or {}
        text = res.get("content", [{}])[0].get("text", "") if res else json.dumps(r.get("error"))
        print(f"     {tool}: {text[:400]}", flush=True)
        return (not res.get("isError", True)) and "error" not in r, text, res.get("structuredContent") or {}

    def ok(self, tool, **args):
        good, text, data = self.call(tool, **args)
        check(good, f"{tool} succeeds")
        return data

    def close(self):
        self.proc.stdin.close()
        try:
            return self.proc.wait(60)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            return None


s = Server("replace")


def song(effect, settings, noise=True):
    """A new song: Neo Poly (noise, or its saw) playing a figure twice into the effect → Master, the effect's pattern beside it."""
    s.ok("song_new", discard_unsaved=True)
    s.ok("machine_add", gear="Neo Poly", name="Synth")
    if noise:
        s.ok("param_set", machine="Synth", changes=[{"param": "Noise", "value": "100%"}, {"param": "O1 level", "value": "0%"},
                                                     {"param": "F1 cutoff", "value": "20 kHz"}, {"param": "F1 env", "value": "0%"},
                                                     {"param": "Amp sustain", "value": "100%"}, {"param": "Amp release", "value": "50 ms"}])
    s.ok("machine_add", gear=effect, name="FX", insert_after="Synth", gain_db=-3)
    if settings:
        s.ok("param_set", machine="FX", changes=settings)
    s.ok("pattern_create", machine="Synth", name="00", length_beats=8)
    s.ok("notes_write", machine="Synth", pattern="00",
         notes=[{"pitch": p, "start": b, "length": 1.5, "velocity": v} for b, p, v in [(0, 48, 100), (2, 55, 60), (4, 60, 127), (6, 52, 90)]])
    s.ok("arrangement_place", machine="Synth", pattern="00", at=[0, 8])


def verdict(r):
    c = r.get("comparison") or {}
    m = c.get("machine") or {}
    return c.get("within_tolerance"), (m.get("loudness_db") or {}).get("difference"), (m.get("worst_band") or {})


# --- successors are named ---------------------------------------------------------------------------
_, _, gear = s.call("gear_list", search="Pedal", limit=100)
rows = {g["name"]: g for g in gear.get("gear", [])}
for name, successor in [("Pedal EQ", "Neo EQ"), ("Pedal Gain", "Neo Utility"), ("Pedal Comp", "Neo Comp"), ("Pedal Chorus", "Neo Ensemble"), ("Pedal M1", "Neo Sampler"),
                        ("Pedal Filter", "Neo Filter"), ("Pedal Gate", "Neo Gate")]:
    check(successor in (rows.get(name, {}).get("successor") or ""), f"gear_list: {name}'s successor is {successor}")

# --- Pedal EQ → Neo EQ: settings, automation with a ramp, arrangement, session, undo, redo ---------------
song("Pedal EQ", [{"param": "LM Gain", "value": "+6 dB"}, {"param": "HS Gain", "value": "-4 dB"}, {"param": "LS Freq", "value": "100 Hz"},
                  {"param": "LS Gain", "value": "+3 dB"}, {"param": "HM Q", "value": "3"}, {"param": "HM Gain", "value": "-9 dB"},
                  {"param": "Output", "value": "-1.5 dB"}])
s.ok("pattern_create", machine="FX", name="Sweep", length_beats=8)
s.ok("automation_write", machine="FX", pattern="Sweep", param="LM Gain", points=[{"at": 0, "value": "0 dB"}, {"at": 4, "value": "+9 dB"}, {"at": 6, "value": "-3 dB"}])
s.ok("arrangement_place", machine="FX", pattern="Sweep", at=[0, 8])
s.ok("session_clip_set", machine="FX", scene=1, pattern="Sweep", mode="Gate", loop=False)
check(any(t.get("machine") == "FX" for t in s.ok("session_get").get("tracks", [])), "the effect has a Session View track with a clip")
_, _, describe = s.call("machine_describe", machine="FX")
check(any(x.get("gear") == "Neo EQ" for x in describe.get("successors", [])), "machine_describe: Pedal EQ's successors include Neo EQ")


def snapshot():
    return {
        "machines": s.ok("machine_list")["machines"],
        "params": s.ok("machine_describe", machine="FX")["parameters"],
        "automation": s.ok("automation_read", machine="FX", pattern="Sweep"),
        "arrangement": s.ok("arrangement_get"),
        "patterns": s.ok("pattern_list", machine="FX"),
        "session": s.ok("session_get"),
    }


before = snapshot()
good, text, dry = s.call("machine_replace", machine="FX", dry_run=True)
check(good and dry.get("dry_run") is True, "a dry run answers")
check(any(m["param"] == "B3 gain" and m["value"] == "+6.0 dB" for m in dry.get("mapping", [])), "dry run: LM Gain +6 dB → B3 gain +6.0 dB")
check(any(m["param"] == "B2 type" and m["value"] == "Low shelf" for m in dry.get("mapping", [])), "dry run: band 2 is the low shelf")
check(any(u["param"] == "LS Solo" for u in dry.get("unmapped", [])), "dry run: the solos are listed as unmapped, with why")
check("loudness" in (dry.get("expected") or {}), "dry run: the expected differences (the stated tolerance)")
check(dry.get("patterns", [{}])[0].get("events", 0) > 0 or any(p.get("events", 0) > 0 for p in dry.get("patterns", [])), "dry run: the automation that would move")
check(snapshot() == before, "a dry run changes nothing")

good, text, r = s.call("machine_replace", machine="FX")
within, loud, worst = verdict(r)
check(good and within is True, f"Pedal EQ → Neo EQ within tolerance (loudness {loud} dB, worst band {worst})")
_, _, listed = s.call("machine_list")
fx = next((m for m in listed.get("machines", []) if m["name"] == "FX"), {})
check(fx.get("gear") == "Neo EQ", "the successor takes the old name")
check([i.get("from") for i in fx.get("inputs", [])] == ["Synth"] and [o.get("to") for o in fx.get("outputs", [])] == ["Master"], "the same connections in and out")
check(any(abs((i.get("gain_db") or 0) + 3) < 0.05 for i in fx.get("inputs", [])), "the input connection keeps its gain (−3 dB)")
check(not any(m["gear"] == "Pedal EQ" for m in listed.get("machines", [])), "the old machine is gone")
mapped = {m["param"]: m["value"] for m in r.get("mapping", [])}
check(mapped.get("B3 gain") == "+6.0 dB" and mapped.get("B2 freq") == "100 Hz" and mapped.get("B4 Q") in ("3", "3.00") and mapped.get("Output") == "-1.5 dB",
      f"its parameters in units ({mapped.get('B3 gain')}, {mapped.get('B2 freq')}, {mapped.get('B4 Q')}, {mapped.get('Output')})")
_, text, _ = s.call("param_get", machine="FX", params=["B2 freq", "B4 Q", "Output"])
check("B2 freq = 100 Hz" in text and "B4 Q = 3.00" in text and "Output = −1.5 dB" in text.replace("-", "−"), "param_get reads them on the successor")
_, _, auto = s.call("automation_read", machine="FX", pattern="Sweep")
check("B3 gain" in json.dumps(auto) and "LM Gain" not in json.dumps(auto), "the pattern's LM Gain automation is B3 gain's now")
check(json.dumps(s.ok("arrangement_get")["lanes"]).replace("Neo EQ", "Pedal EQ") == json.dumps(before["arrangement"]["lanes"]), "the arrangement plays the same patterns")
sess = s.ok("session_get")
check(json.dumps(sess.get("tracks")) == json.dumps(before["session"].get("tracks")), "the Session View's tracks and slots as before")

s.ok("undo")
after_undo = snapshot()
for k in before:
    if before[k] != after_undo[k]:
        print(f"     differs after undo: {k}:\n       {json.dumps(before[k])[:1500]}\n       {json.dumps(after_undo[k])[:1500]}")
check(after_undo == before, "one undo brings the original back exactly")
s.ok("redo")
_, _, listed = s.call("machine_list")
check(any(m["name"] == "FX" and m["gear"] == "Neo EQ" for m in listed.get("machines", [])), "redo replaces it again")

# --- Pedal Gain → Neo Utility, Pedal Comp → Neo Comp, Pedal Gate → Neo Gate -------------------------------------------------
for effect, settings, label in [
    ("Pedal Gain", [{"param": "Gain", "value": "50%"}], "Pedal Gain 50% → Neo Utility"),
    ("Pedal Gain", [{"param": "Gain", "value": "150%"}], "Pedal Gain 150% → Neo Utility"),
    ("Pedal Comp", [{"param": "Threshold", "value": "-24 dB"}, {"param": "Ratio", "value": "4"}, {"param": "Attack", "value": "5 ms"}, {"param": "Release", "value": "100 ms"}], "Pedal Comp → Neo Comp"),
    ("Pedal Comp", [{"param": "Threshold", "value": "-30 dB"}, {"param": "Ratio", "value": "8"}, {"param": "Attack", "value": "1 ms"}, {"param": "Release", "value": "300 ms"}, {"param": "Knee", "value": "6 dB"}], "Pedal Comp, harder → Neo Comp"),
    ("Pedal Chorus", [], "Pedal Chorus → Neo Ensemble"),
    ("Pedal Chorus", [{"param": "Depth", "value": "6 ms"}, {"param": "Delay", "value": "20 ms"}, {"param": "Spread", "value": "100%"}, {"param": "Mix", "value": "60%"}, {"param": "Rate", "value": "1 Hz"}],
     "Pedal Chorus, deep and wide → Neo Ensemble"),
    ("Pedal Chorus", [{"param": "Spread", "value": "0%"}, {"param": "Tone", "value": "6 kHz"}, {"param": "Mix", "value": "100%"}], "Pedal Chorus, mono, dark, wet only → Neo Ensemble"),
    ("Pedal Filter", [], "Pedal Filter (low pass 2 kHz) → Neo Filter"),
    ("Pedal Filter", [{"param": "Mode", "value": "High Pass"}, {"param": "Slope", "value": "24 dB / IIR"}, {"param": "Cutoff", "value": "500 Hz"}, {"param": "Resonance", "value": "60%"}],
     "Pedal Filter, high pass 24 dB at 500 Hz, resonant → Neo Filter"),
    ("Pedal Filter", [{"param": "Mode", "value": "Band Pass"}, {"param": "Cutoff", "value": "800 Hz"}, {"param": "Resonance", "value": "85%"}],
     "Pedal Filter, band pass 800 Hz, resonance 85 % (its compensation on Output) → Neo Filter"),
    ("Pedal Gate", [], "Pedal Gate (defaults) → Neo Gate"),
    ("Pedal Gate", [{"param": "Threshold", "value": "-30 dB"}, {"param": "Hold ms", "value": "40 ms"}, {"param": "Release ms", "value": "120 ms"}, {"param": "Range dB", "value": "-24 dB"}],
     "Pedal Gate, -30 dB, 24 dB range → Neo Gate"),
]:
    song(effect, settings)
    good, text, r = s.call("machine_replace", machine="FX")
    within, loud, worst = verdict(r)
    check(good and within is True, f"{label} within tolerance (loudness {loud} dB, worst band {worst})")

# --- Pedal M1 → Neo Sampler: the song's waves, its key zones and oscillators, a line of notes ------------
def wave(name, hz, harmonics, step=1):
    """Two seconds of a band-limited saw (step 1) or square (step 2) at 48 kHz, 32-bit float."""
    path = os.path.join(work, name)
    data = struct.pack("<96000f", *[0.3 * sum(math.sin(2 * math.pi * k * hz * i / 48000) / k for k in range(1, harmonics, step)) for i in range(96000)])
    with open(path, "wb") as f:
        f.write(b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 3, 1, 48000, 192000, 4, 32) + b"data" + struct.pack("<I", len(data)) + data)
    return path


saw, square = wave("saw-a3.wav", 220, 91), wave("square-a4.wav", 440, 45, 2)
for settings, label in [
    ([], "Pedal M1 → Neo Sampler"),
    ([{"param": "OSC1 Wave 2", "value": "raw:2"}, {"param": "OSC1 Split 2", "value": "raw:66"}, {"param": "OSC Mode", "value": "Both"}, {"param": "OSC2 Wave", "value": "raw:1"},
      {"param": "OSC2 Octave", "value": "raw:3"}, {"param": "OSC2 Level", "value": "raw:64"}], "Pedal M1 with a key zone and both oscillators → Neo Sampler"),
    ([{"param": "Amp Attack", "value": "raw:60"}, {"param": "Amp Sustain", "value": "raw:64"}, {"param": "Amp Release", "value": "raw:70"},
      {"param": "Cutoff", "value": "raw:90"}, {"param": "Filter EG Int", "value": "raw:40"}, {"param": "Filter Decay", "value": "raw:60"}], "Pedal M1 with its envelopes and filter → Neo Sampler"),
]:
    s.ok("song_new", discard_unsaved=True)
    s.ok("wave_import", path=saw, slot=1, name="Saw A3", root_note="A3")
    s.ok("wave_import", path=square, slot=2, name="Square A4", root_note="A4")
    s.ok("machine_add", gear="Pedal M1", name="Keys")
    if settings:
        s.ok("param_set", machine="Keys", changes=settings)
    s.ok("pattern_create", machine="Keys", name="00", length_beats=8)
    s.ok("notes_write", machine="Keys", pattern="00", notes=[{"pitch": p, "start": 2 * i, "length": 1} for i, p in enumerate([57, 69, 52, 45])])
    s.ok("arrangement_place", machine="Keys", pattern="00", at=[0])
    good, text, r = s.call("machine_replace", machine="Keys")
    within, loud, worst = verdict(r)
    check(good and within is True and (r.get("patterns") or [{}])[0].get("notes") == 4, f"{label} within tolerance, its 4 notes moved (loudness {loud} dB, worst band {worst})")

# --- keep_old, a given name --------------------------------------------------------------------------
song("Pedal Gain", [{"param": "Gain", "value": "50%"}])
good, text, r = s.call("machine_replace", machine="FX", keep_old=True, name="Trim", compare=False)
_, _, listed = s.call("machine_list")
names = {m["name"]: m for m in listed.get("machines", [])}
check(good and names.get("Trim", {}).get("gear") == "Neo Utility" and names.get("FX", {}).get("muted") is True, "keep_old: the old one stays, muted, beside the successor")

# --- songs with closed natives, which are silent placeholders here (as on a Mac) ---------------------
# Their values are the song's, their units from the manifests Gear carries for them: the successor plays them.
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
songs = os.path.join(root, "ExternalMachines", "source", "buzztrax-buzzmachines")
good, text, _ = s.call("song_open", path=os.path.join(songs, "Oomek", "Masterizer", "Masterizer_test.bmx"), discard_unsaved=True)
if good:
    good, text, r = s.call("machine_replace", machine="Reverb")
    mapped = {m["param"]: m["value"] for m in r.get("mapping", [])}
    check(good and mapped.get("Mix") == "6.7%" and mapped.get("Decay") == "801 ms" and mapped.get("High damping") == "11.98 kHz",
          f"a placeholder Jeskola Reverb's song values in units: Mix {mapped.get('Mix')}, Decay {mapped.get('Decay')}, High damping {mapped.get('High damping')}")
    check(r.get("comparison", {}).get("within_tolerance") is None, "a silent placeholder: nothing to compare, and it says so")
    _, _, listed = s.call("machine_list")
    eq3 = next((m for m in listed.get("machines", []) if m["name"] == "EQ-3"), {})
    check(any(o.get("to") == "Reverb" and abs((o.get("gain_db") or 0) - 6.02) < 0.05 for o in eq3.get("outputs", [])),
          "the connection into it takes +6 dB (a mono native got left + right)")
    s.ok("undo")
    _, _, listed = s.call("machine_list")
    check(any(m["name"] == "Reverb" and m["gear"] == "Jeskola Reverb" for m in listed.get("machines", [])), "undo brings the placeholder back")
else:
    check(False, "Masterizer_test.bmx opens")
good, text, _ = s.call("song_open", path=os.path.join(songs, "Elak", "SVF", "Elak_SVF.bmx"), discard_unsaved=True)
if good:
    good, text, r = s.call("machine_replace", machine="Bass")
    pats = {p["pattern"]: p for p in r.get("patterns", [])}
    check(good and pats.get("00", {}).get("notes") == 5 and pats.get("01", {}).get("notes") == 5,
          f"Jeskola Bass's tracker patterns (the pattern editor's columns) become Neo Poly's notes, a slide two: {pats}")
    _, text, _ = s.call("automation_read", machine="Bass", pattern="00")
    check("Glide: 0.238 s at 2" in text, "the slide's two ticks at 126 BPM become a 0.238 s glide where it starts")
    _, text, _ = s.call("notes_read", machine="Bass", pattern="00")
    check("5 notes" in text, "notes_read reads them on Neo Poly")
else:
    check(False, "Elak_SVF.bmx opens")

# --- refusals ----------------------------------------------------------------------------------------
song("Pedal Shaper", None)
good, text, _ = s.call("machine_replace", machine="FX")
check(not good and "no successor mapping" in text, "a machine without a mapping is refused, with the gear that has one")
song("Pedal EQ", None)
good, text, _ = s.call("machine_replace", machine="FX", **{"with": "Neo Verb"})
check(not good and "Neo EQ" in text, "a successor it has no mapping to is refused, with its successors")

s.close()
print(f"\n{len(failures)} failure(s)" + ("" if not failures else ":\n  " + "\n  ".join(failures)))
sys.exit(1 if failures else 0)
