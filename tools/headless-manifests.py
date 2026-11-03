#!/usr/bin/env python3
# Fork (docs/specs/mcp-server.md §12.2, U1): the profiled draft manifests at work in rebuzz-headless,
# as an MCP client drives it. Run after the build has copied Machines/Manifests beside the Pedal
# machines (ReBuzz.Host/AppGear.targets). It checks that:
#  - gear_list says what the Pedal machines are, from their drafts, and lists unhealthy ones last;
#  - a Pedal synth that takes notes only in its tracks plays A4 at 440 Hz through notes_write, where
#    MIDI alone leaves it silent, and notes_read gives back what was written;
#  - a pitch offset in a manifest moves what the machine gets, not what the agent reads (a copy of
#    the app with an edited draft);
#  - param_set takes units on Pedal machines (Hz, dB, ms, ratios, choices) and says the units are a draft's;
#  - an unhealthy draft (in the copy: Pedal Plate's profile has been clean since its fix) is flagged
#    and listed last by gear_list, and machine_add warns about it.
#
#   python3 tools/headless-manifests.py <path to rebuzz-headless> [work folder]

import json
import os
import shutil
import subprocess
import sys
import tempfile

exe = os.path.abspath(sys.argv[1])
work = sys.argv[2] if len(sys.argv) > 2 else tempfile.mkdtemp(prefix="rebuzz-manifests-")
os.makedirs(work, exist_ok=True)
failures = []


def check(ok, what):
    print(("ok   " if ok else "FAIL ") + what, flush=True)
    if not ok:
        failures.append(what)


class Server:
    def __init__(self, exe, label):
        self.stderr = os.path.join(work, label + ".stderr.txt")
        self.proc = subprocess.Popen([exe, "--agent", "--settings", os.path.join(work, label + "-settings")], stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE, stderr=open(self.stderr, "w"), text=True, bufsize=1, encoding="utf-8")
        self.next_id = 0
        self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "manifests", "version": "1"}})
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
        print(f"     {tool}: {text[:200]}", flush=True)
        return (not res.get("isError", True)) and "error" not in r, text, res.get("structuredContent") or {}

    def close(self):
        self.proc.stdin.close()
        try:
            return self.proc.wait(30)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            return None


def heard(s, render_id):
    _, _, a = s.call("analyze", source=render_id, metrics=["pitch"], from_seconds=0.05, to_seconds=0.9)
    return (a.get("pitch") or {}).get("median_hz"), (a.get("pitch") or {}).get("median_note")


def play_a4(s, machine, **extra):
    s.call("pattern_create", machine=machine, name="A", length_beats=4)
    s.call("arrangement_place", machine=machine, pattern="A", at=0)
    ok, _, w = s.call("notes_write", machine=machine, pattern="A", notes=[{"pitch": "A4", "start": 0, "length": 2}], **extra)
    ok2, _, r = s.call("render", **{"from": 0, "to": 4, "tail_seconds": 0.2})
    return ok and ok2, w, r


s = Server(exe, "main")

# What the drafts say.
ok, _, g = s.call("gear_list", search="Pedal", limit=100)
rows = {x["name"]: x for x in g.get("gear", [])}
juno = rows.get("Pedal Juno106", {})
check(juno.get("manifest") == "draft" and juno.get("type") and "profiled draft" in juno.get("summary", ""),
      f"gear_list: Pedal Juno106 has a draft manifest saying what it is ({juno.get('type')}: {juno.get('summary', '')[:80]})")
check(juno.get("exact_units") is False, "gear_list: a draft's units aren't called exact")
missing = [n for n, x in rows.items() if not x.get("manifest")]
check(not missing, f"every Pedal machine has a manifest beside it (missing: {missing}): Machines/Manifests is up to date")
plate = rows.get("Pedal Plate", {})
check(plate and "health" not in plate, f"gear_list: Pedal Plate's profile is clean since its reverb was fixed (studio-mvp.md S2) ({plate.get('health')}: {plate.get('problems')})")

# A Pedal synth plays the written pitch through notes_write: its draft says it takes notes in its tracks.
s.call("song_new", discard_unsaved=True, tempo_bpm=120)
s.call("machine_add", gear="Pedal SH101", name="Bass")
_, _, d = s.call("machine_describe", machine="Bass")
info = d.get("gear_info", {})
check(d.get("manifest") == "draft" and info.get("note_input") == "tracks" and info.get("takes_midi") is False,
      f"machine_describe: SH101's draft says notes go in its tracks ({info})")
ok, w, r = play_a4(s, "Bass")
check(ok and w.get("input") == "tracks", f"notes_write A4 to Pedal SH101 goes into its Note track column (input {w.get('input')})")
hz, name = heard(s, r.get("id", "r1"))
check(name == "A4" and hz is not None and abs(hz - 440) < 2, f"Pedal SH101 plays A4 at 440 Hz through notes_write ({hz} Hz, {name})")
_, _, back = s.call("notes_read", machine="Bass", pattern="A")
check([(n["pitch"], n["start"], n["length"]) for n in back.get("notes", [])] == [(69, 0, 2)], f"notes_read gives back what was written ({back.get('notes')})")
s.call("undo")
s.call("pattern_delete", machine="Bass", pattern="A")
ok, w, r = play_a4(s, "Bass", input="midi")
check(ok and r.get("measure", {}).get("silent") is True, "as MIDI alone (the old path) the same note is silent: the draft made it play")

# Units on Pedal machines, through their drafts.
s.call("machine_add", gear="Pedal EQ", name="EQ")
ok, _, p = s.call("param_set", machine="EQ", changes=[{"param": "LM Freq", "value": "800 Hz"}, {"param": "LM Gain", "value": "-3 dB"},
                                                       {"param": "Output", "by": "+2 dB"}, {"param": "LS Solo", "value": "On"}])
vals = {x["param"]: x["value"] for x in p.get("changed", [])}
check(ok and vals == {"LM Freq": "800 Hz", "LM Gain": "-3.0 dB", "Output": "+2.0 dB", "LS Solo": "On"} and p.get("manifest") == "draft",
      f"param_set in Hz, dB, by dB and a choice on Pedal EQ ({vals})")
s.call("machine_add", gear="Pedal MComp", name="MB", connect_to="none")
ok, _, p = s.call("param_set", machine="MB", changes=[{"param": "L Ratio", "value": "4:1"}])
got = (p.get("changed") or [{}])[0]
check(ok and (got.get("value", "").startswith("4.") or got.get("value") == "4:1"), f"param_set a ratio on Pedal MComp ({got.get('value')})")
s.call("machine_add", gear="Pedal Juno106", name="Keys", connect_to="none")
ok, _, p = s.call("param_set", machine="Keys", changes=[{"param": "Env A", "value": "250 ms"}, {"param": "DCO Saw", "value": "Off"}])
_, _, g2 = s.call("param_get", machine="Keys", params=["Env A"])
env = (g2.get("values") or [{}])[0]
check(ok and env.get("unit") == "s" and abs((env.get("number") or 0) - 0.25) < 0.03 and env.get("approximate") is True and g2.get("manifest") == "draft",
      f"param_set in ms on Pedal Juno106 from its description's range, marked approximate ({env})")

ok, text, a = s.call("machine_add", gear="Pedal Plate", name="Plate", connect_to="none")
check(ok and "warning" not in a, "machine_add adds Pedal Plate without a warning")
check(s.close() == 0, "exits when stdin closes")

# A copy of the app with edited drafts: SH101 plays an octave low (+12 on the way in), and Pedal
# Plate's profile found it blowing up, as it did before its fix.
app = os.path.dirname(exe)
copy = os.path.join(work, "app-copy")
shutil.rmtree(copy, ignore_errors=True)
shutil.copytree(app, copy)
draft = os.path.join(copy, "Gear", "Generators", "Pedal SH101.machine.json")
with open(draft, encoding="utf-8") as f:
    m = json.load(f)
m["notes"]["pitch_offset"] = 12
with open(draft, "w", encoding="utf-8") as f:
    json.dump(m, f)
draft = os.path.join(copy, "Gear", "Effects", "Pedal Plate.machine.json")
with open(draft, encoding="utf-8") as f:
    m = json.load(f)
m["health"] = {"status": "unhealthy", "problems": ["NaN or runaway output on noise (26.42 dBFS)"]}
with open(draft, "w", encoding="utf-8") as f:
    json.dump(m, f)
s = Server(os.path.join(copy, os.path.basename(exe)), "offset")
ok, _, g = s.call("gear_list", search="Pedal", limit=100)
rows = {x["name"]: x for x in g.get("gear", [])}
plate = rows.get("Pedal Plate", {})
names = [x["name"] for x in g.get("gear", [])]
check(plate.get("health") == "unhealthy" and plate.get("problems") and names[-1] == "Pedal Plate",
      f"gear_list: an unhealthy draft is flagged with why, and listed last ({plate.get('problems')})")
ok, text, a = s.call("machine_add", gear="Pedal Plate", name="Plate", connect_to="none")
check(ok and "warning" in a and "NaN" in a["warning"], "machine_add warns about an unhealthy profile")
s.call("machine_add", gear="Pedal SH101", name="Bass")
ok, w, r = play_a4(s, "Bass")
hz, name = heard(s, r.get("id", "r1"))
check(ok and w.get("pitch_offset") == 12 and name == "A5", f"with a +12 pitch offset, A4 written reaches the machine as A5 ({hz} Hz, {name})")
_, _, back = s.call("notes_read", machine="Bass", pattern="A")
_, _, sent = s.call("notes_read", machine="Bass", pattern="A", apply_pitch_offset=False)
check([n["pitch"] for n in back.get("notes", [])] == [69] and [n["pitch"] for n in sent.get("notes", [])] == [81],
      "notes_read gives the written pitch; without the offset, what the machine gets")
s.close()

print(f"\n{len(failures)} failed" if failures else "\nall passed")
sys.exit(1 if failures else 0)
