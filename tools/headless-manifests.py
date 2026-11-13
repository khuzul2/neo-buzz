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
#  - U2 (docs/specs/machine-safety.md §3.1, §3.4): gear_list shows each committed draft's health and
#    safe ranges (Pedal Plate's as its profile measured them, whatever that is now), unhealthy last;
#    machine_add warns about a diverging machine and not about a hot one; where Pedal Plate's draft has
#    a safe range, param_set keeps to it. In the copy, with edited drafts: a diverging machine with a
#    safe range (Pedal Gain) is warned about, and param_set and automation_write refuse values outside
#    the range with the reason (nothing in the call changes) unless allow_unsafe, which says so; a hot
#    machine whose loop never decays (Pedal Gate) has a range but no warning; a machine that needs a
#    window says it works in NeoBuzz Live on Windows with its window, and is listed after the drafts but
#    before the unhealthy ones; an unhealthy machine that doesn't diverge (a crash) gets no machine_add
#    warning; a safe range on a track parameter holds per track.
#  - U2 in depth (mcp-server.md §12.12): gear_list leaves unhealthy machines (the crash and the diverging
#    one, in the copy) out and names them, and lists them with include_unhealthy; machine_describe gives a
#    draft's suggested roles and a machine's played track parameters; automation_write writes a played
#    parameter as events; render's cpu gives each machine's work time, window by window.
#  - U3 (mcp-server.md §12.9): curated manifests (Pedal Juno106, SH101, EQ, Comp…) say they are curated,
#    with exact units, quirks, plain names and roles; param_set takes a plain name; a machine whose own
#    texts are wrong (Pedal Comp's Attack) reads and writes the real values; guide(machine=…) and the
#    rebuzz://machines/<slug>/manual resources cover drafts and curated machines alike.
#  - U6 (mcp-server.md §12.13): the Pedal effects that had drafts have manifests generated from their source
#    (Pedal Shaper: exact units, dB read from its code where its own texts are bare numbers).
#  - U6 for the Pedal synths (mcp-server.md §12.15): Pedal Tracker's manifest is generated from its source too, so no
#    Pedal machine has a draft left: the drafts' checks run in the copy, on Pedal Tracker made a draft again. The
#    synths take MIDI notes: notes_write's default for Pedal SH101 is its MIDI column, and its Note track still plays.
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

# What the drafts say (unhealthy ones too: U2 in depth leaves them out unless asked; checked in the copy below).
ok, _, g = s.call("gear_list", search="Pedal", limit=100, include_unhealthy=True)
rows = {x["name"]: x for x in g.get("gear", [])}
tracker = rows.get("Pedal Tracker", {})
check(tracker.get("manifest") == "curated" and tracker.get("exact_units") is True and "Matilde" in tracker.get("summary", ""),
      f"gear_list: Pedal Tracker's manifest is curated (K2: from its source, with its quirks), exact ({tracker.get('manifest')}: {tracker.get('summary', '')[:80]})")
shaper = rows.get("Pedal Shaper", {})
check(shaper.get("manifest") == "curated" and shaper.get("exact_units") is True and "waveshaping" in shaper.get("summary", ""),
      f"gear_list: Pedal Shaper's manifest is curated (K2: from its source, with its quirks), exact ({shaper.get('manifest')}: {shaper.get('summary', '')[:80]})")
juno = rows.get("Pedal Juno106", {})
check(juno.get("manifest") == "curated" and juno.get("exact_units") is True and "polysynth" in juno.get("summary", "") and juno.get("quirks"),
      f"gear_list: Pedal Juno106 is curated, exact, says what it's for and its quirks ({juno.get('summary', '')[:80]})")
curated = sorted(n for n, x in rows.items() if x.get("manifest") == "curated")
check(curated == ["Pedal Chord", "Pedal Chorus", "Pedal Comp", "Pedal Drum Matrix", "Pedal EQ", "Pedal FM", "Pedal Filter", "Pedal Gain", "Pedal Gain Multi", "Pedal Gate", "Pedal Juno106",
                  "Pedal LFO", "Pedal M1", "Pedal MComp", "Pedal Peer Control", "Pedal Plate", "Pedal SH101", "Pedal Shaper", "Pedal Tracker"],
      f"gear_list: the curated Pedal machines ({curated})")
missing = [n for n, x in rows.items() if not x.get("manifest")]
check(not missing, f"every Pedal machine has a manifest beside it (missing: {missing}): Machines/Manifests is up to date")


def draft_of(gear):
    """The committed draft beside the machine in the app's Gear."""
    for sub in ("Effects", "Generators"):
        path = os.path.join(os.path.dirname(exe), "Gear", sub, gear + ".machine.json")
        if os.path.exists(path):
            with open(path, encoding="utf-8") as f:
                return json.load(f)
    return {}


# U2: gear_list says what the drafts say. Pedal Plate's is what its profile measured (in this build:
# its Feedback diverges near the top); the checks follow the draft, so they hold after a source fix too.
names = [x["name"] for x in g.get("gear", [])]
plate, plate_draft = rows.get("Pedal Plate", {}), draft_of("Pedal Plate")
ph = plate_draft.get("health", {})
plate_params = {p["name"]: p for p in plate_draft.get("parameters", [])}
plate_ranges = {n: p["safe_range"] for n, p in plate_params.items() if p.get("safe_range")}
check(plate.get("health", "ok") == ph.get("status") and bool(plate.get("diverging")) == bool(ph.get("diverging"))
      and {r["param"] for r in plate.get("safe_ranges", [])} == set(plate_ranges),
      f"gear_list: Pedal Plate as its profile measured it ({ph.get('status')}, diverging {bool(ph.get('diverging'))}, safe ranges {plate_ranges})")
order = [rows[n].get("health") == "unhealthy" for n in names]
check(order == sorted(order), f"gear_list: unhealthy machines come last ({[n for n in names if rows[n].get('health') == 'unhealthy']})")
hot = [n for n, x in rows.items() if x.get("health") == "warn" and any(p.startswith("hot") for p in x.get("problems", []))]
check(hot and all(not rows[n].get("diverging") for n in hot), f"gear_list: hot machines are a warning, not diverging ({hot})")

# A Pedal synth plays the written pitch through notes_write: as MIDI (§12.15), and in its Note track as old songs have it.
s.call("song_new", discard_unsaved=True, tempo_bpm=120)
s.call("machine_add", gear="Pedal SH101", name="Bass")
_, _, d = s.call("machine_describe", machine="Bass")
info = d.get("gear_info", {})
check(d.get("manifest") == "curated" and info.get("note_input") == "midi" and info.get("takes_midi") is True,
      f"machine_describe: SH101 takes MIDI notes, and its curated manifest sends them as MIDI ({info})")
cut = next((p for p in d.get("parameters", []) if p["name"] == "Cutoff"), {})
check(cut.get("plain_name") == "Filter cutoff" and cut.get("role") == "filter.cutoff" and d.get("quirks"),
      f"machine_describe: plain names, roles and quirks ({cut.get('plain_name')}, {cut.get('role')}, {len(d.get('quirks') or [])} quirks)")
ok, w, r = play_a4(s, "Bass")
check(ok and w.get("input") == "midi", f"notes_write A4 to Pedal SH101 goes into the pattern's MIDI column (input {w.get('input')})")
hz, name = heard(s, r.get("id", "r1"))
check(name == "A4" and hz is not None and abs(hz - 440) < 2, f"Pedal SH101 plays A4 at 440 Hz through notes_write ({hz} Hz, {name})")
_, _, back = s.call("notes_read", machine="Bass", pattern="A")
check([(n["pitch"], n["start"], n["length"]) for n in back.get("notes", [])] == [(69, 0, 2)], f"notes_read gives back what was written ({back.get('notes')})")
s.call("undo")
s.call("pattern_delete", machine="Bass", pattern="A")
ok, w, r = play_a4(s, "Bass", input="tracks")
hz, name = heard(s, r.get("id", "r1"))
check(ok and w.get("input") == "tracks" and name == "A4" and hz is not None and abs(hz - 440) < 2,
      f"in its Note track (input tracks, as old songs have it) the same note plays as well ({hz} Hz, {name})")

# Units on Pedal machines, through their drafts.
s.call("machine_add", gear="Pedal EQ", name="EQ")
ok, _, p = s.call("param_set", machine="EQ", changes=[{"param": "LM Freq", "value": "800 Hz"}, {"param": "LM Gain", "value": "-3 dB"},
                                                       {"param": "Output", "by": "+2 dB"}, {"param": "LS Solo", "value": "On"}])
vals = {x["param"]: x["value"] for x in p.get("changed", [])}
check(ok and vals == {"LM Freq": "800 Hz", "LM Gain": "-3.0 dB", "Output": "+2.0 dB", "LS Solo": "On"} and "manifest" not in p,
      f"param_set in Hz, dB, by dB and a choice on Pedal EQ, curated ({vals})")
s.call("machine_add", gear="Pedal MComp", name="MB", connect_to="none")
ok, _, p = s.call("param_set", machine="MB", changes=[{"param": "L Ratio", "value": "4:1"}])
got = (p.get("changed") or [{}])[0]
check(ok and (got.get("value", "").startswith("4.") or got.get("value") == "4:1"), f"param_set a ratio on Pedal MComp ({got.get('value')})")
s.call("machine_add", gear="Pedal Juno106", name="Keys", connect_to="none")
ok, _, p = s.call("param_set", machine="Keys", changes=[{"param": "Env A", "value": "250 ms"}, {"param": "DCO Saw", "value": "Off"}])
_, _, g2 = s.call("param_get", machine="Keys", params=["Env A"])
env = (g2.get("values") or [{}])[0]
check(ok and env.get("unit") == "s" and abs((env.get("number") or 0) - 0.25) < 0.03 and "approximate" not in env and "manifest" not in g2
      and env.get("value", "").endswith(" ms"),
      f"param_set in ms on Pedal Juno106, exact from its source, the value in ms ({env})")
ok, _, p = s.call("param_set", machine="Keys", changes=[{"param": "Filter cutoff", "value": "1 kHz"}])
got = (p.get("changed") or [{}])[0]
check(ok and got.get("param") == "VCF Freq" and abs((got.get("number") or 0) - 1000) < 60,
      f"param_set takes a curated plain name (Filter cutoff = VCF Freq: {got})")
s.call("machine_add", gear="Pedal Comp", name="Comp", connect_to="none")
ok, _, p = s.call("param_set", machine="Comp", changes=[{"param": "Attack", "value": "10 ms"}, {"param": "Makeup Gain", "value": "+6 dB"}])
vals = {x["param"]: (x["value"], x.get("raw")) for x in p.get("changed", [])}
check(ok and vals == {"Attack": ("10 ms", 10), "Makeup Gain": ("+6.0 dB", 6)},
      f"Pedal Comp's own texts are wrong; its curated manifest reads and writes the real values ({vals})")
ok, _, gd = s.call("guide", machine="Pedal Juno106")
md = gd.get("markdown", "")
check(ok and "## Quirks" in md and "**Filter cutoff** (`filter.cutoff`)" in md and "| VCF Freq |" in md and "## Sources" in md,
      "guide(machine=\"Pedal Juno106\"): the curated manual")
ok, _, gd = s.call("guide", machine="Pedal Tracker")
check(ok and gd.get("markdown", "").startswith("# Pedal Tracker manual") and "exact units from a curated manifest" in gd.get("markdown", "")
      and "Pedal Tracker" in gd.get("manuals", []),
      "guide(machine=\"Pedal Tracker\"): the curated manual, and the list names it")
# U6: a manifest generated from the source. Pedal Shaper's Drive reads bare numbers (48); its code makes it dB.
s.call("machine_add", gear="Pedal Shaper", name="Shp", connect_to="none")
ok, _, p = s.call("param_set", machine="Shp", changes=[{"param": "Drive", "value": "+6 dB"}, {"param": "Mix", "value": "50%"}])
vals = {x["param"]: (x["value"], x.get("raw")) for x in p.get("changed", [])}
_, _, dsh = s.call("machine_describe", machine="Shp")
check(ok and vals.get("Drive") == ("+6.0 dB", 60) and vals.get("Mix", (None, None))[1] in (63, 64) and p.get("manifest") != "draft"
      and dsh.get("exact_units") is True,
      f"param_set on a manifest from source: Drive in dB, Mix in %, exact ({vals}, exact_units {dsh.get('exact_units')})")
ok, _, gd = s.call("guide", machine="Pedal Shaper")
check(ok and "| Drive |" in gd.get("markdown", "") and "profiled draft" not in gd.get("markdown", ""), "guide(machine=\"Pedal Shaper\"): the manual of a manifest from source")
res = s.request("resources/list")
uris = {r["uri"] for r in (res.get("result") or {}).get("resources", [])}
check({"rebuzz://machines/pedal-shaper/manual", "rebuzz://machines/pedal-juno106/manual"} <= uris,
      f"resources/list has a manual for drafts and curated machines ({len([u for u in uris if u.startswith('rebuzz://machines/')])} manuals)")

some_hot = (hot or ["Pedal EQ"])[0]
ok, _, a = s.call("machine_add", gear=some_hot, name="Hot", connect_to="none")
check(ok and "warning" not in a, f"machine_add doesn't warn about a hot machine ({some_hot})")

# Pedal Plate as measured: machine_add warns exactly when its draft says it diverges, and param_set keeps
# to each safe range its profile found.
ok, text, a = s.call("machine_add", gear="Pedal Plate", name="Plate", connect_to="none")
check(ok and ("diverges" in a.get("warning", "")) == bool(ph.get("diverging")),
      f"machine_add warns about Pedal Plate only if its profile says it diverges ({a.get('warning', 'no warning')[:120]})")
_, _, d = s.call("machine_describe", machine="Plate")
shown = {p["name"]: p.get("safe_range") for p in d.get("parameters", []) if p.get("safe_range")}
check(set(shown) == set(plate_ranges), f"machine_describe shows Pedal Plate's safe ranges ({shown})")
for name, sr in plate_ranges.items():
    end = "max" if sr["max"] < plate_params[name]["max"] else "min"
    ok, text, _ = s.call("param_set", machine="Plate", changes=[{"param": name, "value": end}])
    check(not ok and "outside its safe range" in text and "allow_unsafe" in text, f"param_set refuses Pedal Plate's {name} at its {end}imum, with the reason")
    ok, _, p = s.call("param_set", machine="Plate", changes=[{"param": name, "value": f"raw:{sr[end]}"}])
    check(ok and "unsafe" not in ((p.get("changed") or [{}])[0]), f"param_set sets {name} at the edge of its safe range (raw {sr[end]})")
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


def gain_diverges(m):
    m["health"] = {"status": "unhealthy", "diverging": True, "problems": ["Gain diverges at its maximum (200%: a test)"]}
    for p in m["parameters"]:
        if p["name"] == "Gain":
            p["safe_range"] = {"min": 0, "max": 150, "min_text": "0%", "max_text": "150%", "reason": "diverges at its maximum (200%: a test)"}


def gate_never_decays(m):
    m["health"] = {"status": "warn", "hot": True, "problems": ["hot (loud but steady) with Hold ms at its maximum: never decays (a test)"]}
    for p in m["parameters"]:
        if p["name"] == "Hold ms":
            p["safe_range"] = {"min": p["min"], "max": 1000, "reason": "never decays at its maximum (a test)"}


def tracker_draft(m):
    # Pedal Tracker as the profiled draft it was before §12.15 (the drafts' checks: no Pedal machine has one now).
    m["status"] = "draft"
    m["source"] = "profiled by tools/machine-profiler.py (a test); not curated"
    m["summary"] = "Monosynth (profiled draft): pitched, plays A4 at 440 Hz as written; takes MIDI notes (a test)."
    for p in m["parameters"] + m["track_parameters"]:
        # K2 batch a curated it (roles and plain names, Explain text): the draft it stands for has none of them.
        for k in ("role", "plain_name", "how", "sound"):
            p.pop(k, None)
        if p["name"] in ("Master", "Volume"):
            p["suggested_role"], p["role_evidence"] = "level", "silent at its minimum, sounding at the other end (a test)"


edit_draft("Effects", "Pedal Gain", gain_diverges)
edit_draft("Effects", "Pedal Gate", gate_never_decays)
edit_draft("Generators", "Pedal Tracker", tracker_draft)
s = Server(os.path.join(copy, os.path.basename(exe)), "offset")
ok, _, g0 = s.call("gear_list", search="Pedal", limit=100)
if ok: g0["gear"] = [x for x in g0.get("gear", []) if x["name"].startswith("Pedal ")]  # Neo Gate's text names Pedal Gate
check(ok and sorted(g0.get("hidden_unhealthy", [])) == ["Pedal Filter", "Pedal Gain"] and len(g0.get("gear", [])) == 17
      and not {"Pedal Filter", "Pedal Gain"} & {x["name"] for x in g0.get("gear", [])},
      f"gear_list leaves the unhealthy machines out and names them ({g0.get('hidden_unhealthy')}, {len(g0.get('gear', []))} listed)")
ok, _, g = s.call("gear_list", search="Pedal", limit=100, include_unhealthy=True)
rows = {x["name"]: x for x in g.get("gear", [])}
names = [x["name"] for x in g.get("gear", []) if x["name"].startswith("Pedal ")]
tracker = rows.get("Pedal Tracker", {})
check(tracker.get("manifest") == "draft" and tracker.get("type") and "profiled draft" in tracker.get("summary", ""),
      f"gear_list: a draft manifest says what the machine is ({tracker.get('type')}: {tracker.get('summary', '')[:80]})")
check(tracker.get("exact_units") is False, "gear_list: a draft's units aren't called exact")
ok, _, gd = s.call("guide", machine="Pedal Tracker")
check(ok and gd.get("markdown", "").startswith("# Pedal Tracker manual") and "profiled draft" in gd.get("markdown", ""),
      "guide(machine=\"Pedal Tracker\"): a draft's manual")
chorus = rows.get("Pedal Chorus", {})
check(chorus.get("health") == "needs_window" and "works in neobuzz live on windows with its window" in chorus.get("needs_window", "").lower(),
      f"gear_list: a machine that needs a window says it works in NeoBuzz Live on Windows with its window ({chorus.get('needs_window')})")
unhealthy = [n for n in names if rows[n].get("health") == "unhealthy"]
check(unhealthy and names.index("Pedal Chorus") == len(names) - len(unhealthy) - 1,
      f"gear_list: it comes after the drafts and before the unhealthy ones ({names[-4:]})")
check(len(names) == 19, f"with include_unhealthy, every Pedal machine is listed ({len(names)})")
ok, _, a = s.call("machine_add", gear="Pedal Filter", name="F", connect_to="none")
check(ok and "warning" not in a, "machine_add warns only for diverging machines (not for a crash flagged in its profile)")

# A diverging machine with a safe range (Pedal Gain's Gain, edited): warned about, kept to by param_set and automation_write.
gain = rows.get("Pedal Gain", {})
check(gain.get("diverging") is True and [(r["param"], r["range"]) for r in gain.get("safe_ranges", [])] == [("Gain", "0% … 150%")],
      f"gear_list: a diverging machine's safe ranges, in its profiled texts ({gain.get('safe_ranges')})")
ok, _, a = s.call("machine_add", gear="Pedal Gain", name="G", connect_to="none")
check(ok and "diverges" in a.get("warning", "") and "Gain" in a.get("warning", "") and "safe range" in a.get("warning", ""),
      f"machine_add warns that it diverges, naming its safe range ({a.get('warning', '')[:140]})")
_, _, d = s.call("machine_describe", machine="G")
sr = next((p.get("safe_range") for p in d.get("parameters", []) if p["name"] == "Gain"), None) or {}
check((sr.get("min_raw"), sr.get("max_raw")) == (0, 150) and "diverges" in sr.get("reason", "") and "…" in sr.get("range", ""),
      f"machine_describe shows the safe range in the machine's own texts ({sr})")
_, _, before = s.call("param_get", machine="G", params=["Gain", "Inertia"])
ok, text, _ = s.call("param_set", machine="G", changes=[{"param": "Inertia", "value": "raw:100"}, {"param": "Gain", "value": "max"}])
check(not ok and "changes[1]" in text and "outside its safe range" in text and "diverges at its maximum" in text and "allow_unsafe=true" in text,
      "param_set refuses a value outside the safe range, with the reason")
_, _, after = s.call("param_get", machine="G", params=["Gain", "Inertia"])
check([v.get("raw") for v in after.get("values", [])] == [v.get("raw") for v in before.get("values", [])], "nothing in the refused call changed")
ok, _, p = s.call("param_set", machine="G", changes=[{"param": "Gain", "value": "raw:150"}])
check(ok and "unsafe" not in ((p.get("changed") or [{}])[0]), "the edge of the safe range is set as asked")
ok, text, p = s.call("param_set", machine="G", changes=[{"param": "Gain", "value": "max"}], allow_unsafe=True)
got = (p.get("changed") or [{}])[0]
check(ok and got.get("raw") == 200 and "diverges" in got.get("unsafe", "") and "unsafe" in text, f"allow_unsafe sets it anyway, and says so ({got.get('unsafe')})")
s.call("pattern_create", machine="G", name="Swell", length_beats=8)
ok, text, _ = s.call("automation_write", machine="G", pattern="Swell", param="Gain", points=[{"at": 0, "value": "raw:50"}, {"at": 8, "value": "max"}])
check(not ok and "points[1].value" in text and "outside its safe range" in text, "automation_write refuses a point outside the safe range, with the reason")
_, _, au = s.call("automation_read", machine="G", pattern="Swell")
check(not au.get("automation"), "and writes nothing")
ok, _, w = s.call("automation_write", machine="G", pattern="Swell", param="Gain", points=[{"at": 0, "value": "raw:50"}, {"at": 8, "value": "raw:150"}])
check(ok and all("Unsafe" not in x for x in w.get("problems", [])), "automation inside the safe range is written as asked")
ok, _, w = s.call("automation_write", machine="G", pattern="Swell", param="Gain", points=[{"at": 0, "value": "raw:50"}, {"at": 8, "value": "max"}], allow_unsafe=True)
check(ok and any("Unsafe" in x for x in w.get("problems", [])), "allow_unsafe writes it anyway, and says so")

# A hot machine whose loop never decays at a parameter's end (Pedal Gate's Hold ms, edited): a range, no warning.
ok, _, a = s.call("machine_add", gear="Pedal Gate", name="Gt", connect_to="none")
check(ok and "warning" not in a, "machine_add doesn't warn about a hot machine with a safe range")
ok, text, _ = s.call("param_set", machine="Gt", changes=[{"param": "Hold ms", "value": "max"}])
check(not ok and "never decays" in text, "param_set keeps agents off an end where the loop never decays")
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

# U2 in depth: a draft's suggested roles in machine_describe, as the draft has them; played track parameters,
# written as events; each machine's work time in a render.
with open(os.path.join(copy, "Gear", "Generators", "Pedal Tracker.machine.json"), encoding="utf-8") as f:
    tracker_m = json.load(f)
want = {p["name"]: p["suggested_role"] for p in tracker_m["parameters"] + tracker_m["track_parameters"] if p.get("suggested_role")}
s.call("machine_add", gear="Pedal Tracker", name="Sh", connect_to="none")
_, _, d = s.call("machine_describe", machine="Sh")
got = {p["name"]: p.get("suggested_role") for p in d.get("parameters", []) if p.get("suggested_role")}
check(got == want and want and all(p.get("role_evidence") for p in d.get("parameters", []) if p.get("suggested_role")),
      f"machine_describe gives a draft's suggested roles with their evidence ({len(got)}: {sorted(got.items())[:3]})")
s.call("machine_add", gear="Neo Poly", name="Pad")
_, _, d = s.call("machine_describe", machine="Pad")
played = [p["name"] for p in d.get("played_parameters", [])]
check("Velocity" in played, f"machine_describe lists the track parameters that hold no value ({played})")
s.call("pattern_create", machine="Pad", name="T", length_beats=4)
ok, _, w = s.call("automation_write", machine="Pad", pattern="T", param="Velocity", track=0, points=[{"at": 0, "value": "raw:100"}, {"at": 2, "value": "raw:50"}])
check(ok and w.get("played") is True and w.get("events") == 2, f"automation_write writes a played parameter as one event per point ({w.get('events')})")
ok, _, r = s.call("render", **{"from": 0, "to": 8, "tail_seconds": 1, "cpu": True, "cpu_window_seconds": 1})
cpu = {x["machine"]: x for x in r.get("cpu", [])}
check(ok and "Bass" in cpu and isinstance(cpu["Bass"].get("ms_per_second"), (int, float)) and len(cpu["Bass"].get("windows_ms_per_second", [])) >= 4,
      f"render's cpu gives each machine's work time, window by window ({cpu.get('Bass')})")
ok, text, _ = s.call("render", **{"from": 0, "to": 4, "cpu_window_seconds": 1})
check(not ok and "cpu=true" in text, "cpu_window_seconds goes with cpu=true")
s.close()

print(f"\n{len(failures)} failed" if failures else "\nall passed")
sys.exit(1 if failures else 0)
