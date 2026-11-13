#!/usr/bin/env python3
# Fork (docs/specs/mcp-server.md §12.14): songs saved with upstream's binaries of the natives the package ships rebuilt
# open with the rebuilt ones, with the same parameters, and play the same. Run in each full package run, after
# tools/native-compare.py, on the package as built:
#
#   phase A  upstream's binary of each such machine (RunTime/Gear: Jeskola's 32-bit DLLs, the FSM and Oomek x64 ones) in
#            Gear in place of the package's; three songs built with them through NeoBuzzLive.exe --agent (every machine fed by
#            the null test's Neo FM6 phrase or playing its notes or triggers, at its defaults, or its global and track
#            parameters a quarter or three quarters of the way up their raw ranges and a second track the other way where it
#            has tracks; its first parameter automated in its pattern), each saved as .bmx and .bmxml; every saved song then
#            opened again and rendered (each machine probed), its parameters read, and saved once more. So do the songs in the
#            repository that classic Buzz saved with these machines (ExternalMachines/source, *.bmx), start to end.
#   phase B  the package's own Gear (the rebuilt DLLs); the same files opened, read, rendered and saved again.
#
# For each machine and song: the file the app loaded (gear_info: upstream's in A, the package's in B, and a *.x64.dll is an
# AMD64 DLL, which the app runs in its 64-bit host, bin64/ReBuzzEngine64.exe), every parameter of every track the same raw
# value in A and B, and the probe's samples the same bits, or within -100 dBFS (native-compare's line). A song saved again
# in B is the same file as in A (BMXML: but its root's name and build stamps). The run fails on any of these for a machine
# shipped rebuilt, and on one that sounds in none of the songs built here (a silent probe proves nothing; FSM Infector is
# silent at both fractions); a machine with a source fix ("fix": upstream's binary starts state as its heap happens to be) is reported
# for its sound, not judged; so is a machine's sound in a classic song where a machine of that kind feeds it, and FSM Infector's
# in a classic song and that of what it feeds there (PROCESS_DEPENDENT: its fast releases sound as its process's heap lands;
# the songs built here play notes that never take that path, native-compare.py's CHORD, and judge it).
#
# --candidate DIR: the x64 rebuilds (build-natives.ps1's x64 Gear) of the natives the package keeps upstream's 32-bit
# binary of (Cross Delay, since 2026-11-12: Jeskola Delay and NiNjA dElaY ship rebuilt): in phase B in place of upstream's, reported, never judged, so
# what shipping one would do to songs is measured before it ships.
# --profile FILE: the package run's machine profile (machine-profile.json): every machine shipped rebuilt profiled from
# the package's file, with no crash or hang; its problems against the committed profile's are reported.
#
#   python tools/native-songs.py <NeoBuzzLive.exe> <work> [--candidate DIR] [--profile FILE] [--only gear,...] [--json out.json]

import importlib.util
import json
import os
import shutil
import struct
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def _load(name, file):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, file))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


nc = _load("native_compare", "native-compare.py")
rt = _load("song_roundtrip", "song-roundtrip.py")
nt = nc.nt

SETTINGS = (("defaults", None), ("quarter", 0.25), ("three-quarters", 0.75))
FORMATS = (".bmx", ".bmxml")
CLASSIC_BEATS = None           # a classic song plays to its end (its machines come in late: the Aggressor demo's 3o3 at beat 16)
CLASSIC_MAX_BEATS = 192
JUDGED = ("rebuilt",)          # roles whose sound is judged; "fixed" and "candidate" are reported
SILENT_DBFS = -140.0           # a probe that never comes above this proves nothing by being the same
# Machines whose sound in a classic song depends on the process their instance runs in, upstream's binary and the rebuild
# alike: FSM Infector renders the fast release of a note played over a sounding one with the track past the end of its
# array (Infector.cpp, DoWorkChannel: Tracks[MAX_TRACKS+1] of MAX_TRACKS+1), whose vibrato depth is the low half of a heap
# pointer: a NaN or infinity in about one process in 128, and that release then plays NaN, zeroed by its Work. Its demo
# differed so in 1 of 12 fresh processes (run 42272808783) and in 2 of 4 package runs (Infector2 -41.4 dBFS from 41.91 s);
# docs/specs/mcp-server.md §12.14 has the evidence.
PROCESS_DEPENDENT = ("FSM Infector",)
DLL_MACHINE = {0x14C: "x86", 0x8664: "x64"}


def pk(x):
    """A peak in dBFS as text."""
    return "-" if x is None else "-inf" if x == float("-inf") else f"{x:.1f}"


def gear_key(name):
    """'Foo.x64.dll' and 'Foo.dll' are the same machine to the app's scanner (ReBuzz's MachineDLLScanner)."""
    n = name.lower()
    for ext in (".x64.dll", ".dll"):
        if n.endswith(ext):
            return n[:-len(ext)]
    return n


def pe_machine(path):
    """x86 or x64, from a DLL's PE header (IMAGE_FILE_HEADER.Machine)."""
    with open(path, "rb") as f:
        data = f.read(4096)
    if data[:2] != b"MZ":
        return "not a PE file"
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        return "not a PE file"
    m = struct.unpack_from("<H", data, pe + 4)[0]
    return DLL_MACHINE.get(m, f"machine {m:#x}")


def plan(gear_root, shipped_dir, candidate_dir=None, only=None):
    """The machines this covers: [{gear, folder, role, upstream: (path, name), ours: (path, name)}]. A native the package
    ships rebuilt (its file in Gear isn't upstream's) is "rebuilt", or "fixed" with a source fix; one it keeps upstream's
    32-bit binary of is a "candidate" when DIR has its x64 rebuild."""
    out = []
    for s in nc.natives():
        if only and s["gear"] not in only:
            continue
        folder = s["gearFolder"]
        up_name = s["gear"] + (".x64.dll" if s.get("upstreamBinary") == "x64" else ".dll")
        up = os.path.join(shipped_dir, folder, up_name)
        if not os.path.exists(up):
            continue
        pkg = next((f for f in (s["gear"] + ".x64.dll", s["gear"] + ".dll") if os.path.exists(os.path.join(gear_root, folder, f))), None)
        if not pkg:
            continue
        with open(up, "rb") as a, open(os.path.join(gear_root, folder, pkg), "rb") as b:
            same = a.read() == b.read()
        e = {"gear": s["gear"], "folder": folder, "upstream": (up, up_name), "kind": "generator" if folder == "Generators" else "effect"}
        if not same:
            e.update(role="fixed" if s.get("fix") else "rebuilt", ours=(os.path.join(gear_root, folder, pkg), pkg))
        elif candidate_dir and s.get("upstreamBinary") == "x86":
            cand = os.path.join(candidate_dir, folder, s["gear"] + ".x64.dll")
            if not os.path.exists(cand):
                continue
            e.update(role="candidate", ours=(cand, s["gear"] + ".x64.dll"))
        else:
            continue
        out.append(e)
    return out


class Swap:
    """Puts DLLs into Gear in place of the package's of the same machine, and takes them out again."""

    def __init__(self, gear_root, hold):
        self.gear_root, self.hold = gear_root, hold
        self.held, self.added = [], []

    def put(self, folder, src, name):
        d = os.path.join(self.gear_root, folder)
        for f in sorted(os.listdir(d)):
            if f.lower().endswith(".dll") and gear_key(f) == gear_key(name):
                os.makedirs(os.path.join(self.hold, folder), exist_ok=True)
                dst = os.path.join(self.hold, folder, f)
                shutil.move(os.path.join(d, f), dst)
                self.held.append((dst, os.path.join(d, f)))
        dst = os.path.join(d, name)
        shutil.copyfile(src, dst)
        self.added.append(dst)

    def restore(self):
        for p in self.added:
            try:
                os.remove(p)
            except OSError:
                pass
        for src, dst in reversed(self.held):
            shutil.move(src, dst)
        self.held, self.added = [], []


def forget_cache():
    cache = os.path.join(os.environ.get("APPDATA", ""), "ReBuzz", "MachineDLLCache.xml")
    if os.path.exists(cache):
        os.remove(cache)


# ---- the songs ----------------------------------------------------------------------------------------------------------

def build_song(c, entries, gear_root, frac):
    """A song with each machine (named N1, N2…) at frac of its raw ranges (a second track at 1 - frac), or at its
    defaults (frac None), where every one of them sounds: FSM Infector is silent at both fractions (native-compare's cases
    too), so each machine must sound in one of the songs, not in each."""
    c.call("song_new", discard_unsaved=True, tempo_bpm=nc.TEMPO)
    c.call("song_set", song_end_beats=nc.BEATS)
    gear, preset, params, notes = nt.SOURCE
    c.call("machine_add", gear=gear, name="Src", connect_to="none")
    c.call("preset_load", machine="Src", preset=preset)
    nt.set_params(c, "Src", params)
    c.call("pattern_create", machine="Src", name="A", length_beats=nc.BEATS)
    c.call("notes_write", machine="Src", pattern="A", notes=notes)
    c.call("arrangement_place", machine="Src", pattern="A", at=[0])
    for e in entries:
        m = e["name"]
        c.call("machine_add", gear=e["gear"], name=m)
        d, glob, track = nc.describe(c, m)
        if track and ((d.get("gear_info") or {}).get("max_tracks") or 1) > 1:
            c.call("machine_set", machine=m, tracks=2)
        changes = nc.raw_changes(glob, track, frac) if frac is not None else []
        if frac is not None and track and ((d.get("gear_info") or {}).get("max_tracks") or 1) > 1:
            changes += [dict(x, track=1) for x in nc.raw_changes([], track, 1 - frac)]
        if changes:
            c.call("param_set", machine=m, changes=changes, allow_unsafe=True)
        if e["kind"] == "effect":
            c.call("connect", **{"from": "Src", "to": m})
            c.call("pattern_create", machine=m, name="P", length_beats=nc.BEATS)
            p = glob[0] if glob else track[0]
            lo, hi = p["min"], p["max"]
            args = {"machine": m, "pattern": "P", "param": f"#{p['index']}" if glob else p["name"], "shape": "step", "allow_unsafe": True,
                    "points": [{"at": 2, "value": f"raw:{lo + int((hi - lo) * (1 - (frac if frac is not None else 0.25)))}"}]}
            if not glob:
                args["track"] = 0
            c.call("automation_write", **args)
            c.call("arrangement_place", machine=m, pattern="P", at=[0])
        else:
            man = nc.manifest_of(os.path.join(gear_root, e["folder"]), e["gear"])
            mn = man.get("notes") or {}
            mode = mn.get("input") if mn.get("input") not in (None, "none") else "tracks"
            nc.play_generator(c, m, {"gear": e["gear"]}, (man.get("profile") or {}).get("trigger"), mode)


def read_song(c, path, names, beats, label, resave):
    """Open path, then per machine (song name -> gear): the file it loaded, every parameter's raw value on every track, its
    health; render it with each machine probed; save it again to resave. {"machines": {...}, "status": ..., "graph": ...}"""
    st = c.call("song_open", path=path, discard_unsaved=True)
    if beats is None:
        beats = min(CLASSIC_MAX_BEATS, max(nc.BEATS, round(st.get("length_beats") or 0)))
    listing = {m["name"]: m for m in c.call("machine_list").get("machines") or []}
    out = {"warnings": len(st.get("warnings") or []), "machines": {}, "beats": beats}
    out["graph"] = {n: [i.get("from") for i in (m.get("inputs") or [])] for n, m in listing.items()}
    for name in names:
        m = listing.get(name)
        if m is None:
            out["machines"][name] = {"error": "not in the song"}
            continue
        e = {"health": m.get("health", "ok"), "tracks": m.get("tracks", 0)}
        try:
            d = c.call("machine_describe", machine=name)
            e["file"] = (d.get("gear_info") or {}).get("file")
            values = {}
            for t in range(max(1, e["tracks"])):
                for v in c.call("param_get", machine=name, track=t).get("values") or []:
                    if t > 0 and "track" not in v:
                        continue
                    values[f"{v['param']}@{v.get('track', '-')}"] = v.get("raw")
            e["params"] = values
        except RuntimeError as ex:
            e["error"] = str(ex)[:300]
        out["machines"][name] = e
    r = c.call("render", **{"from": 0, "to": beats, "tail_seconds": nc.TAIL, "probes": list(names), "name": label})
    files = {p["machine"]: p["file"] for p in r.get("probes") or []}
    diverged = {d.get("machine") for d in r.get("diverged") or []}
    for name in names:
        e = out["machines"].get(name)
        if e is not None and "error" not in e:
            e["wav"] = files.get(name)
            e["status"] = "diverged" if name in diverged else "ok"
            e["peak_dbfs"] = nc.peak_db(e["wav"]) if e["wav"] else None
    try:
        os.remove(r["file"])
    except (OSError, KeyError, TypeError):
        pass
    os.makedirs(os.path.dirname(resave), exist_ok=True)
    try:
        c.call("song_save", path=resave, overwrite=True)
    except RuntimeError as ex:
        out["resave_error"] = str(ex)[:300]
    return out


def classic_songs(entries):
    """The songs in the repository classic Buzz saved with these machines: [(path, {song name: gear})]."""
    import subprocess
    gears = {e["gear"].lower(): e["gear"] for e in entries}
    try:
        tracked = subprocess.run(["git", "-C", ROOT, "ls-files", "ExternalMachines/source"], capture_output=True, text=True, check=True).stdout.split("\n")
    except (OSError, subprocess.CalledProcessError):
        return []
    out = []
    for rel in sorted(f for f in tracked if f.lower().endswith(".bmx")):
        path = os.path.join(ROOT, rel)
        try:
            song = rt.parse_bmx(path)
        except Exception:
            continue
        names = {m["name"]: gears[m["dll"].lower()] for m in song["machines"] if m.get("type") and m["dll"].lower() in gears}
        if names:
            out.append((path, names))
    return out


def fed_by(graph, name, roles):
    """The machines with a role in `roles` that feed `name` (its inputs, theirs and so on)."""
    seen, stack, found = set(), list(graph.get(name) or []), []
    while stack:
        x = stack.pop()
        if x in seen:
            continue
        seen.add(x)
        if x in roles:
            found.append(x)
        stack += graph.get(x) or []
    return found


# ---- the verdicts -------------------------------------------------------------------------------------------------------

def judge(entry, a, b, sound, classic_feeders=(), process_dependent=False):
    """One machine in one song: (failures, notes). a, b: read_song's entry of it in phase A and B; sound: the probes'
    [the same bits, max difference dBFS, first differing s] or None. Failures fail the run only for a judged role. A
    silent probe is a note ("silent in phase …"): the run fails a machine that sounds in none of the songs built here."""
    fails, notes = [], []
    up_file = entry["folder"] + "/" + entry["upstream"][1]
    our_file = entry["folder"] + "/" + entry["ours"][1]
    for ph, e, want in (("A", a, up_file), ("B", b, our_file)):
        if e is None or "error" in e:
            fails.append(f"phase {ph}: {(e or {}).get('error', 'not read')}")
            continue
        if e.get("health") != "ok":
            fails.append(f"phase {ph}: {e['health']}")
        if e.get("file") != want:
            fails.append(f"phase {ph} loaded {e.get('file')}, not {want}")
        if e.get("status") not in (None, "ok"):
            if ph == "A" and entry["role"] == "fixed":     # upstream's binary from heap garbage: WahPro2's has blown up so
                notes.append(f"phase A: {e.get('status')} (upstream's binary from its heap's start: reported)")
            elif classic_feeders:                           # what it was fed differs (run 42187730991: PhatMan after Cross Delay's x64 build)
                notes.append(f"phase {ph}: {e.get('status')} (fed by {', '.join(classic_feeders)}: reported)")
            else:
                fails.append(f"phase {ph}: {e.get('status')}")
    if fails:
        return fails, notes
    for ph, e in (("A", a), ("B", b)):
        peak = e.get("peak_dbfs")
        if peak is not None and peak < SILENT_DBFS:
            notes.append(f"silent in phase {ph} (peak {pk(peak)} dBFS): nothing to compare")
    if a.get("params") != b.get("params"):
        diff = [k for k in sorted(set(a["params"]) | set(b["params"])) if a["params"].get(k) != b["params"].get(k)]
        fails.append("parameters differ: " + ", ".join(f"{k} {a['params'].get(k)} -> {b['params'].get(k)}" for k in diff[:8]))
    if sound is None:
        fails.append("no probe to compare")
    elif not sound[0]:
        text = f"plays {nc.fmt(sound[1])} dBFS apart" + (f" from {sound[2]:.2f} s" if sound[2] is not None else "")
        if entry["role"] not in JUDGED:
            notes.append(text + (" (upstream's binary starts state as its heap happens to be: reported)" if entry["role"] == "fixed" else " (not shipped: reported)"))
        elif process_dependent:
            notes.append(text + " (in a classic song its fast releases sound as its process lands: reported)")
        elif classic_feeders:
            notes.append(text + f" (fed by {', '.join(classic_feeders)}, whose upstream binary isn't deterministic: reported)")
        elif sound[1] > nc.TOLERANCE_DBFS:
            fails.append(text)
        else:
            notes.append(text + f" (within {nc.TOLERANCE_DBFS:.0f} dBFS)")
    return fails, notes


def resaved_name(label):
    return label.replace("/", "_").replace(" ", "_")


def same_file(a, b):
    """Two saves of one song: the same bytes (a BMXML file: but its root's name and build stamps)."""
    with open(a, "rb") as f:
        x = f.read()
    with open(b, "rb") as f:
        y = f.read()
    if a.lower().endswith(".bmxml"):
        return rt.strip_stamps(x.decode("utf-8-sig")) == rt.strip_stamps(y.decode("utf-8-sig"))
    return x == y


def check_profile(entries, profile_path):
    """Each machine shipped rebuilt in the run's profile: (failures, notes)."""
    fails, notes = [], []
    try:
        with open(profile_path, encoding="utf-8-sig") as f:
            got = json.load(f).get("machines") or {}
    except (OSError, ValueError) as ex:
        return [f"profile {profile_path}: {ex}"], notes
    try:
        with open(os.path.join(ROOT, "Machines", "Manifests", "machine-profile.json"), encoding="utf-8-sig") as f:
            committed = json.load(f).get("machines") or {}
    except (OSError, ValueError):
        committed = {}
    for e in entries:
        if e["role"] == "candidate":
            continue
        p = got.get(e["gear"])
        if p is None:
            notes.append(f"{e['gear']}: not in the run's profile (profile_only?)")
            continue
        want = e["folder"] + "/" + e["ours"][1]
        if (p.get("gear_info") or {}).get("file") != want:
            fails.append(f"{e['gear']}: profiled from {(p.get('gear_info') or {}).get('file')}, not {want}")
        bad = [x for x in p.get("problems") or [] if "crash" in x or "hung" in x]
        if bad:
            fails.append(f"{e['gear']}: {'; '.join(bad)[:300]}")
        old = committed.get(e["gear"]) or {}
        new = [(k, p.get(k) or []) for k in ("problems", "warnings") if (p.get(k) or []) != (old.get(k) or [])]
        notes.append(f"{e['gear']}: profiled from {want} ({pe_machine(e['ours'][0])}); "
                     + ("; ".join(f"{k} now {v}, committed {old.get(k) or []}" for k, v in new) if new else "the committed profile's problems and warnings"))
    return fails, notes


def main(argv):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except AttributeError:
        pass
    if len(argv) < 2:
        print("usage: native-songs.py <NeoBuzzLive.exe> <work> [--candidate DIR] [--profile FILE] [--only a,b] [--json out.json]")
        return 2
    exe, work = os.path.abspath(argv[0]), os.path.abspath(argv[1])
    candidate, profile, only, json_out, shipped = None, None, None, None, os.path.join(ROOT, "RunTime", "Gear")
    i = 2
    while i < len(argv):
        a = argv[i]
        if a == "--candidate":
            candidate = os.path.abspath(argv[i + 1])
        elif a == "--profile":
            profile = os.path.abspath(argv[i + 1])
        elif a == "--only":
            only = set(argv[i + 1].split(","))
        elif a == "--json":
            json_out = os.path.abspath(argv[i + 1])
        elif a == "--shipped":
            shipped = os.path.abspath(argv[i + 1])
        else:
            raise SystemExit(f"unknown argument {a}")
        i += 2
    if os.path.exists(work):
        shutil.rmtree(work)
    os.makedirs(work)
    gear_root = os.path.join(os.path.dirname(exe), "Gear")
    started = time.time()
    entries = plan(gear_root, shipped, candidate, only)
    for k, e in enumerate(entries, 1):
        e["name"] = f"N{k}"
    roles = {e["name"]: e["role"] for e in entries}
    print(f"{len(entries)} machines: " + ", ".join(f"{e['gear']} ({e['role']}: {e['upstream'][1]} -> {e['ours'][1]}, {pe_machine(e['ours'][0])})" for e in entries), flush=True)
    songs = []     # (label, path, {song name: gear}, beats)
    for name, frac in SETTINGS:
        for ext in FORMATS:
            songs.append((name + ext, os.path.join(work, "saved", "natives-" + name + ext), {e["name"]: e["gear"] for e in entries}, nc.BEATS))
    classics = classic_songs(entries)
    for path, names in classics:
        songs.append((os.path.relpath(path, ROOT).replace(os.sep, "/"), path, names, CLASSIC_BEATS))
    by_gear = {e["gear"]: e for e in entries}

    reads = {"A": {}, "B": {}}
    swap = Swap(gear_root, os.path.join(work, "held"))
    try:
        for phase in ("A", "B"):
            swap.restore()
            for e in entries:
                if phase == "A" and e["role"] != "candidate":
                    swap.put(e["folder"], e["upstream"][0], e["upstream"][1])
                elif phase == "B" and e["role"] == "candidate":
                    swap.put(e["folder"], e["ours"][0], e["ours"][1])
            forget_cache()
            c = nt.Client(exe, os.path.join(work, "server"), f"phase{phase}")
            try:
                c.initialize()
                if phase == "A":
                    for name, frac in SETTINGS:
                        build_song(c, entries, gear_root, frac)
                        for ext in FORMATS:
                            c.call("song_save", path=os.path.join(work, "saved", "natives-" + name + ext), overwrite=True)
                for label, path, names, beats in songs:
                    t = time.time()
                    try:
                        reads[phase][label] = read_song(c, path, list(names), beats, f"{phase}-{label}".replace("/", "_").replace(" ", "_"),
                                                        os.path.join(work, "resaved-" + phase, resaved_name(label)))
                    except RuntimeError as ex:
                        reads[phase][label] = {"error": str(ex)[:400]}
                        print(f"phase {phase} {label}: {str(ex)[:300]}", flush=True)
                        c.close()
                        c = nt.Client(exe, os.path.join(work, "server"), f"phase{phase}-again{len(reads[phase])}")
                        c.initialize()
                        continue
                    print(f"phase {phase} {label}: {len(names)} machines read and rendered, {reads[phase][label]['beats']} beats "
                          f"({time.time() - t:.1f} s)", flush=True)
            finally:
                c.close()
    finally:
        swap.restore()
        forget_cache()

    # ---- report
    rows, failed, notes_out, results = [], [], [], {}
    sounded = set()      # the machines that sounded in phase B (and A, but for a fixed one) of a song built here
    for e in entries:
        want = "x64" if e["ours"][1].lower().endswith(".x64.dll") else "x86"
        got = pe_machine(e["ours"][0])
        if got != want:
            (notes_out if e["role"] == "candidate" else failed).append(f"{e['gear']}: {e['ours'][1]} is {got}, not {want}: the app hosts it by its name")
    for label, path, names, beats in songs:
        a_song, b_song = reads["A"].get(label) or {}, reads["B"].get(label) or {}
        classic = beats is CLASSIC_BEATS
        if "error" in a_song:
            rows.append((label, "-", "-", "not judged: it doesn't open with upstream's binaries either: " + a_song["error"][:200]))
            continue
        if "error" in b_song:
            failed.append(f"{label}: opens with upstream's binaries, not with the package's: {b_song['error'][:300]}")
            continue
        song_roles = {n: by_gear[g]["role"] for n, g in names.items()}
        for name, gear in names.items():
            entry = by_gear[gear]
            a, b = a_song["machines"].get(name), b_song["machines"].get(name)
            sound = None
            if a and b and a.get("wav") and b.get("wav"):
                same, worst, _, first = nc.diff(a["wav"], b["wav"])
                sound = [same, worst, first]
            unjudged = {n for n, r in song_roles.items() if r not in JUDGED or names[n] in PROCESS_DEPENDENT}
            feeders = fed_by(a_song.get("graph") or {}, name, unjudged) if classic else []
            fails, notes = judge(entry, a, b, sound, feeders, classic and gear in PROCESS_DEPENDENT)
            if not classic and not fails and not any(n.startswith("silent in phase B") or (n.startswith("silent in phase A") and entry["role"] != "fixed") for n in notes):
                sounded.add(gear)
            peak = f" (peak {pk((a or {}).get('peak_dbfs'))} dBFS)"
            verdict = "; ".join(fails) if fails else ("the same parameters, the same bits" + peak if sound and sound[0] and not notes
                                                      else "the same parameters; " + "; ".join(notes) + peak)
            rows.append((label, f"{gear} ({name})", entry["role"], verdict))
            results.setdefault(gear, []).append({"song": label, "role": entry["role"], "failures": fails, "notes": notes,
                                                 "sound": sound, "file_a": (a or {}).get("file"), "file_b": (b or {}).get("file")})
            if fails and entry["role"] != "candidate":     # judge() reports, not fails, the sound of the roles it doesn't judge
                failed += [f"{label}: {gear}: {f}" for f in fails]
        for w in (a_song, b_song):
            for x in (w.get("machines") or {}).values():
                if x.get("wav"):
                    try:
                        os.remove(x["wav"])
                    except OSError:
                        pass
        ra = os.path.join(work, "resaved-A", resaved_name(label))
        rb = os.path.join(work, "resaved-B", resaved_name(label))
        if os.path.exists(ra) and not os.path.exists(rb):
            failed.append(f"{label}: saves with upstream's binaries, not with the package's: {b_song.get('resave_error')}")
        elif os.path.exists(ra) and os.path.exists(rb):
            if same_file(ra, rb):
                rows.append((label, "the song saved again", "-", "the same file with upstream's binaries and the package's"))
            elif any(r == "candidate" for r in song_roles.values()):
                rows.append((label, "the song saved again", "-", "differs (it has a candidate machine: reported)"))
            else:
                failed.append(f"{label}: saved again with the package's machines, it isn't the file upstream's save")
                rows.append((label, "the song saved again", "-", "DIFFERS from the save with upstream's binaries"))
    for e in entries:
        if e["role"] != "candidate" and e["gear"] not in sounded:
            failed.append(f"{e['gear']}: silent (or failed) in every song built here: its sound was never compared")
    if profile:
        pf, pn = check_profile(entries, profile)
        failed += pf
        notes_out += pn
    lines = [f"{s:<56} {m:<34} {r:<9} {v}" for s, m, r, v in rows]
    lines += [f"profile: {x}" for x in notes_out]
    lines += [f"FAIL {x}" for x in failed]
    print("\n".join(lines), flush=True)
    n_rebuilt = sum(1 for e in entries if e["role"] != "candidate")
    summary = (f"{n_rebuilt} machines shipped rebuilt and {len(entries) - n_rebuilt} candidates in {len(songs)} songs saved with upstream's "
               f"binaries ({len(classics)} classic Buzz songs); "
               + (f"failed: {len(failed)}" if failed else "every one opens with the package's machines with the same parameters and plays the same")
               + f" ({time.time() - started:.0f} s)")
    print(summary, flush=True)
    if json_out:
        with open(json_out, "w", encoding="utf-8") as f:
            json.dump({"machines": entries, "results": results, "failures": failed, "profile": notes_out}, f, indent=1, default=str)
    if os.environ.get("GITHUB_ACTIONS"):
        kind = "error" if failed else "notice"
        chunk, k = [], 0
        for line in [summary] + [f"FAIL {x}" for x in failed] + lines:
            chunk.append(line)
            if sum(len(x) + 1 for x in chunk) > 3300:
                k += 1
                print(f"::{kind} title=Natives: songs saved with upstream's binaries ({k})::" + "%0A".join(x.replace("%", "%25") for x in chunk), flush=True)
                chunk = []
        if chunk:
            k += 1
            print(f"::{kind} title=Natives: songs saved with upstream's binaries ({k})::" + "%0A".join(x.replace("%", "%25") for x in chunk), flush=True)
        step = os.environ.get("GITHUB_STEP_SUMMARY")
        if step:
            with open(step, "a", encoding="utf-8") as f:
                f.write(f"### Natives: songs saved with upstream's binaries\n\n{summary}\n\n")
                for x in failed:
                    f.write(f"- FAIL {x}\n")
                f.write("\n| Song | Machine | Role | Verdict |\n|---|---|---|---|\n")
                for s, m, r, v in rows:
                    f.write(f"| {s} | {m} | {r} | {v} |\n")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
