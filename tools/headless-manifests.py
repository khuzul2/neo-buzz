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
#  - U2 (docs/specs/machine-safety.md §3.1, §3.4): Pedal Plate, profiled diverging at its maximum
#    Feedback, is flagged and listed last with a safe range; machine_add warns about it, and not about
#    a hot machine; param_set refuses Feedback outside its safe range with the reason unless
#    allow_unsafe; machine_describe and gear_list show the range. In the copy, with edited drafts: a
#    machine that needs a window says it works in ReBuzz on Windows with its window, and is listed
#    after the drafts but before the unhealthy ones; an unhealthy machine that doesn't diverge (a
#    crash) gets no machine_add warning; a safe range on a track parameter holds per track.
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
names = [x["name"] for x in g.get("gear", [])]
ranges = {r["param"]: r for r in plate.get("safe_ranges", [])}
check(plate.get("health") == "unhealthy" and plate.get("diverging") is True and "Feedback" in ranges and names[-1] == "Pedal Plate",
      f"gear_list: Pedal Plate diverges at its maximum Feedback (its Decay is fine since its fix, studio-mvp.md S2): flagged, listed last, "
      f"with a safe range ({ranges.get('Feedback')})")
hot = [n for n, x in rows.items() if x.get("health") == "warn" and any("hot" in p for p in x.get("problems", []))]
check("Pedal EQ" in hot and rows["Pedal EQ"].get("diverging") is None, f"gear_list: hot machines are a warning, not diverging ({hot})")

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

ok, _, a = s.call("machine_add", gear="Pedal EQ", name="EQ2", connect_to="none")
check(ok and "warning" not in a, "machine_add doesn't warn about a hot machine (Pedal EQ)")

# Safe ranges: Pedal Plate's Feedback diverges at its maximum.
ok, text, a = s.call("machine_add", gear="Pedal Plate", name="Plate", connect_to="none")
check(ok and "diverges" in a.get("warning", "") and "Feedback" in a.get("warning", ""), f"machine_add warns that Pedal Plate diverges, naming the safe range ({a.get('warning', '')[:160]})")
_, _, d = s.call("machine_describe", machine="Plate")
fb = next((p for p in d.get("parameters", []) if p["name"] == "Feedback"), {})
safe = fb.get("safe_range") or {}
check(safe.get("max_raw") is not None and safe.get("max_raw") < 95 and "diverges" in safe.get("reason", ""), f"machine_describe shows Feedback's safe range ({safe})")
ok, text, _ = s.call("param_set", machine="Plate", changes=[{"param": "Feedback", "value": "max"}])
check(not ok and "outside its safe range" in text and "diverges" in text and "allow_unsafe" in text, "param_set refuses Feedback at its maximum, with the reason")
_, _, now = s.call("param_get", machine="Plate", params=["Feedback"])
check((now.get("values") or [{}])[0].get("raw") == fb.get("raw"), "the refused change left Feedback as it was (and the call's other changes too)")
ok, _, p = s.call("param_set", machine="Plate", changes=[{"param": "Feedback", "value": f"raw:{safe.get('max_raw')}"}])
check(ok and "unsafe" not in ((p.get("changed") or [{}])[0]), "param_set sets Feedback at the top of its safe range")
ok, text, p = s.call("param_set", machine="Plate", changes=[{"param": "Feedback", "value": "max"}], allow_unsafe=True)
got = (p.get("changed") or [{}])[0]
check(ok and got.get("raw") == 95 and "diverges" in got.get("unsafe", "") and "unsafe" in text, f"allow_unsafe sets it anyway, and says so ({got.get('unsafe')})")
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


def edit_draft(sub, gear, change):
    path = os.path.join(copy, "Gear", sub, gear + ".machine.json")
    with open(path, encoding="utf-8") as f:
        m = json.load(f)
    change(m)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(m, f)


# Pedal Chorus needs a window (as Info does on Windows); Pedal Filter crashed (unhealthy, not diverging);
# Pedal M1's Volume track parameter has a safe range.
edit_draft("Effects", "Pedal Chorus", lambda m: m.update(health={"status": "needs_window", "problems": [], "needs_window": "a WPF window that is the machine (a test)"}))
edit_draft("Effects", "Pedal Filter", lambda m: m.update(health={"status": "unhealthy", "problems": ["crashed or hung the server (a test)"]}))


def volume_range(m):
    for p in m["track_parameters"]:
        if p["name"] == "Volume":
            p["safe_range"] = {"min": p["min"], "max": (p["min"] + p["max"]) // 2, "reason": "is too loud above half (a test)"}


edit_draft("Generators", "Pedal M1", volume_range)
s = Server(os.path.join(copy, os.path.basename(exe)), "offset")
ok, _, g = s.call("gear_list", search="Pedal", limit=100)
rows = {x["name"]: x for x in g.get("gear", [])}
names = [x["name"] for x in g.get("gear", [])]
chorus = rows.get("Pedal Chorus", {})
check(chorus.get("health") == "needs_window" and "works in rebuzz on windows with its window" in chorus.get("needs_window", "").lower(),
      f"gear_list: a machine that needs a window says it works in ReBuzz on Windows with its window ({chorus.get('needs_window')})")
unhealthy = [n for n in names if rows[n].get("health") == "unhealthy"]
check(unhealthy and names.index("Pedal Chorus") == len(names) - len(unhealthy) - 1,
      f"gear_list: it comes after the drafts and before the unhealthy ones ({names[-4:]})")
check(len(names) == 19, f"every Pedal machine is still listed ({len(names)})")
ok, _, a = s.call("machine_add", gear="Pedal Filter", name="F", connect_to="none")
check(ok and "warning" not in a, "machine_add warns only for diverging machines (not for a crash flagged in its profile)")
s.call("machine_add", gear="Pedal SH101", name="Bass")
ok, w, r = play_a4(s, "Bass")
hz, name = heard(s, r.get("id", "r1"))
check(ok and w.get("pitch_offset") == 12 and name == "A5", f"with a +12 pitch offset, A4 written reaches the machine as A5 ({hz} Hz, {name})")
s.call("machine_add", gear="Pedal M1", name="Lead", connect_to="none")
ok, text, _ = s.call("param_set", machine="Lead", changes=[{"param": "Volume", "track": 0, "value": "max"}])
check(not ok and "outside its safe range" in text and "too loud" in text, "a safe range on a track parameter holds too")
ok, _, _ = s.call("param_set", machine="Lead", changes=[{"param": "Volume", "track": 0, "value": "raw:60"}])
check(ok, "inside it, a track parameter is set as asked")
_, _, back = s.call("notes_read", machine="Bass", pattern="A")
_, _, sent = s.call("notes_read", machine="Bass", pattern="A", apply_pitch_offset=False)
check([n["pitch"] for n in back.get("notes", [])] == [69] and [n["pitch"] for n in sent.get("notes", [])] == [81],
      "notes_read gives the written pitch; without the offset, what the machine gets")
s.close()

print(f"\n{len(failures)} failed" if failures else "\nall passed")
sys.exit(1 if failures else 0)
