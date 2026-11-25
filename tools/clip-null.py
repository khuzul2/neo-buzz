#!/usr/bin/env python3
# Fork (docs/specs/clips.md §10, wave CU1; clean-break.md CB1): the clip goldens. Every song in the corpus renders to the same
# bits as it did before, on each platform: SHA-256 of each render, per platform, in eval/clip-goldens.json (hashes only: no
# audio is committed).
#
# The corpus (after CB1, which removed the old song formats, the old machines and the songs built on them):
#  - every .nbl under Other (the two song templates);
#  - one generated song whose clips hold no tracker data: a session clip with loop on, its notes and automation, placed in the
#    arrangement as a looped clip. It is written by this file, in a fresh process, with the engine's own tools (MCP, as
#    null-test.py does), and saved as .nbl.
#
# A song is rendered the way a user hears it: opened, then rendered from the start to its end (at most 32 beats)
# and one second of tail, 32-bit float, by a fresh process each (renders start on a tick boundary, so they
# are bit-identical across processes on one platform, docs/specs/mcp-server.md §10). The hash is of the
# WAV file's bytes.
#
#   python3 tools/clip-null.py list
#   python3 tools/clip-null.py generate <exe> <out-dir> [--only a,b]
#   python3 tools/clip-null.py render   <exe> <work-dir> [--out observed.json] [--only a,b] [--twice]
#   python3 tools/clip-null.py check    <exe> <work-dir> [--goldens eval/clip-goldens.json] [--only a,b] [--twice]
#   python3 tools/clip-null.py merge    <observed.json>... --into eval/clip-goldens.json
#   python3 tools/clip-null.py opentime <exe> <work-dir> [--baseline eval/clip-goldens.json] [--max-ratio 1.3]
#
# <exe> is NeoBuzzLive.exe (Windows) or rebuzz-headless (Linux and macOS). --twice renders the whole corpus twice in separate
# processes and fails a song whose two hashes differ: that song isn't deterministic (it is named, and goes in EXCLUDED with the
# reason, or the test is fixed). check renders, compares with the goldens of this platform and fails naming each song that
# differs; with no goldens for the platform it fails too, and says where the observed hashes are (<work-dir>/observed.json).

import hashlib
import importlib.util
import json
import math
import os
import platform
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, file))
    mod = importlib.util.module_from_spec(spec)
    sys.modules[name] = mod
    spec.loader.exec_module(mod)
    return mod


nt = load("null_test", "null-test.py")           # the MCP client, the waves, the platform label

TEMPO = 120
BEATS_CAP = 32          # a render is at most this many beats (a long song is heard from its start)
TAIL = 1.0

# Songs that are not deterministic or can't be hashed, with the reason: {"name": "why"}. Empty: every song counts.
EXCLUDED = {}

# Songs whose clips hold no tracker data (engine columns only): these stay a bit-for-bit gate in every wave. For the others
# (old songs' PATT rows, the editor's data) the goldens are a change report from the wave that moves the tracker on
# (the coordinator's decision of 2026-11-23: one engine plays everything, no emulation of the editor's old timing).
ENGINE_ONLY = {"gen/session-loop"}


# ---- the corpus: repo songs --------------------------------------------------------------------

def repo_songs():
    """(name, path) of every song file in the repo, by name."""
    found = []
    for base in ("Other",):
        for folder, dirs, files in os.walk(os.path.join(ROOT, base)):
            dirs[:] = sorted(d for d in dirs if d not in ("bin", "obj", ".git"))
            for f in sorted(files):
                if os.path.splitext(f)[1].lower() == ".nbl":
                    found.append(os.path.join(folder, f))
    songs = {}
    for path in sorted(found):
        name = "repo/" + os.path.basename(path)
        if name in songs:
            raise SystemExit(f"two songs are named {name}: {songs[name]} and {path}")
        songs[name] = path
    return songs


# ---- the corpus: generated songs ---------------------------------------------------------------

class Build:
    """One generated song: a fresh engine process that the tools build in, then the file it saved."""

    def __init__(self, exe, work, name):
        self.name = name
        self.dir = os.path.join(work, "build", name)
        shutil.rmtree(self.dir, ignore_errors=True)
        os.makedirs(self.dir)
        self.client = nt.Client(exe, self.dir, name)
        self.client.initialize()

    def call(self, tool, **args):
        return self.client.call(tool, **args)

    def wave(self, key="tri-a3", slot=1):
        path = os.path.abspath(os.path.join(self.dir, key + ".wav"))
        nt.write_wave(path, nt.WAVES[key]())
        self.call("wave_import", path=path, slot=slot, name=key, root_note="A3")

    def save(self, path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        self.call("song_save", path=path, overwrite=True)
        self.client.close()
        return path

    def abort(self):
        self.client.close()


GENERATED = {}


def generated(name, features):
    def add(fn):
        GENERATED[name] = {"features": features, "build": fn}
        return fn
    return add


@generated("gen/session-loop", "a session clip with loop on, its notes and automation (a step and a ramp), placed in the arrangement as a looped clip over 4 bars")
def _(b, path):
    b.call("song_set", tempo_bpm=TEMPO)
    b.call("machine_add", gear="Neo Poly", name="Lead")
    b.call("pattern_create", machine="Lead", name="Loop", length_beats=4)
    b.call("notes_write", machine="Lead", pattern="Loop", notes=[nt.n("C3", 0, 1), nt.n("G3", 1, 0.5), nt.n("E4", 2, 1), nt.n("A3", 3, 0.75, 70)])
    b.call("automation_write", machine="Lead", pattern="Loop", param="F1 cutoff", shape="step",
           points=[{"at": 0, "value": "raw:20"}, {"at": 2, "value": "raw:60"}])
    b.call("automation_write", machine="Lead", pattern="Loop", param="Uni detune", shape="linear",
           points=[{"at": 0, "value": "raw:0"}, {"at": 4, "value": "raw:80"}])
    b.call("session_clip_set", machine="Lead", scene=1, pattern="Loop", mode="Toggle", quantization="Bar", loop=True)
    b.call("arrangement_place", machine="Lead", pattern="Loop", at=[0])
    b.call("song_set", song_end_beats=16)
    b.call("arrangement_clip", op="loop", machine="Lead", at=0, on=True)
    b.call("arrangement_clip", op="trim", machine="Lead", at=0, end=16)
    b.save(path)
    return path


def build_generated(exe, work, name):
    """The song file for a generated song, in work/songs, built by a fresh process (kept if it is there already)."""
    path = os.path.abspath(os.path.join(work, "songs", name.split("/", 1)[1] + ".nbl"))
    b = Build(exe, work, name.replace("/", "-"))
    try:
        GENERATED[name]["build"](b, path)
    except BaseException:
        b.abort()
        raise
    return path


# ---- rendering ---------------------------------------------------------------------------------

def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def render_song(exe, path, work, label):
    """Opens the song in a fresh process and renders it; the entry for the goldens."""
    c = nt.Client(exe, os.path.join(work, "run", label), label)
    try:
        c.initialize()
        t = time.time()
        try:
            status = c.call("song_open", path=os.path.abspath(path), discard_unsaved=True)
        except RuntimeError as e:
            return {"status": "open failed", "why": str(e)[:200]}
        opened = time.time() - t
        beats = min(float(status.get("length_beats") or 0), BEATS_CAP)
        if beats <= 0:
            return {"status": "no length", "why": "the song has no end to render to"}
        r = c.call("render", **{"from": 0, "to": beats, "tail_seconds": TAIL, "name": label[:40]})
        return {"sha256": sha256(r["file"]), "seconds": r["seconds"], "silent": bool((r.get("measure") or {}).get("silent")),
                "rate": r["sample_rate"], "open_ms": round(opened * 1000)}
    finally:
        c.close()


def corpus(exe, work, only):
    """name -> song file, building the generated songs."""
    songs = {}
    for name, path in repo_songs().items():
        if not only or name in only:
            songs[name] = path
    for name in GENERATED:
        if not only or name in only:
            songs[name] = build_generated(exe, work, name)
    return songs


def platform_key():
    system = {"Darwin": "macos", "Windows": "windows", "Linux": "linux"}.get(platform.system(), platform.system().lower())
    arch = {"amd64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64"}.get(platform.machine().lower(), platform.machine().lower())
    return f"{system}-{arch}"


def render_all(exe, work, only, twice):
    """{name: entry}, and the songs whose two renders differ when twice."""
    started = time.time()
    songs = corpus(exe, work, only)
    entries, unstable = {}, []
    for name, path in songs.items():
        if name in EXCLUDED:
            print(f"skip {name}: {EXCLUDED[name]}", flush=True)
            continue
        label = name.replace("/", "-")
        try:
            e = render_song(exe, path, work, label)
            if twice:
                e2 = render_song(exe, path, work, label + "-again")
                if e2.get("sha256") != e.get("sha256") or e2.get("status") != e.get("status"):
                    unstable.append(name)
                    e["second_sha256"] = e2.get("sha256")
        except Exception as ex:
            e = {"status": "render failed", "why": str(ex)[:300]}
        e["tracker"] = name not in ENGINE_ONLY      # tracker data (PATT rows, the editor's blob): a change report from CU3 on, not a gate
        entries[name] = e
        kind = e.get("sha256", e.get("status"))[:16]
        print(f"{'FAIL' if 'status' in e else 'ok  '} {name:<44} {kind}  {e.get('seconds', '')} s{'  silent' if e.get('silent') else ''}"
              + ("  NOT DETERMINISTIC" if name in unstable else ""), flush=True)
    print(f"{len(entries)} songs in {time.time() - started:.0f} s on {nt.platform_label()}", flush=True)
    return entries, unstable


def observed(entries):
    placeholders = platform.system() != "Windows"
    return {"version": 1, "platforms": {platform_key(): {"machines": "placeholders" if placeholders else "real", "songs": entries}}}


def same(a, b):
    return a.get("sha256") == b.get("sha256") and a.get("status") == b.get("status")


def summary(title, lines):
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if path:
        with open(path, "a", encoding="utf-8") as f:
            f.write(f"### {title}\n\n" + "\n".join(lines) + "\n\n")


def check(exe, work, goldens_path, only, twice):
    entries, unstable = render_all(exe, work, only, twice)
    obs = observed(entries)
    out = os.path.join(work, "observed.json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump(obs, f, indent=1, sort_keys=True)
        f.write("\n")
    key = platform_key()
    problems = [f"{n}: not deterministic (two renders differ)" for n in unstable]
    problems += [f"{n}: {e.get('status')}: {e.get('why', '')}" for n, e in entries.items() if e.get("status") == "render failed"]
    changed = []
    gold = None
    if os.path.exists(goldens_path):
        with open(goldens_path, encoding="utf-8") as f:
            gold = json.load(f)["platforms"].get(key)
    if gold is None:
        problems.append(f"no goldens for {key} in {goldens_path}: the observed hashes are in {out} (tools/clip-null.py merge)")
    else:
        for name, e in entries.items():
            g = gold["songs"].get(name)
            if g is None:
                problems.append(f"{name}: not in the goldens (a song added: tools/clip-null.py merge)")
            elif not same(g, e):
                line = f"{name}: render differs from the golden ({(e.get('sha256') or e.get('status'))[:12]} was {(g.get('sha256') or g.get('status'))[:12]})"
                if e.get("tracker"):
                    changed.append(line)      # tracker data: a change report (the engine plays it by design), not a gate
                else:
                    problems.append(line)
        if not only:
            problems += [f"{n}: in the goldens, not rendered" for n in gold["songs"] if n not in entries and n not in EXCLUDED]
    title = f"Clip goldens ({nt.platform_label()})"
    for c in changed:
        print("CHANGED (tracker data, change report) " + c, flush=True)
        if os.environ.get("GITHUB_ACTIONS"):
            print(f"::warning title={title}::{c}", flush=True)
    if os.environ.get("GITHUB_ACTIONS"):
        print(f"::notice title={title}::{len(entries)} songs rendered; {len(problems)} problems", flush=True)
        for p in problems:
            print(f"::error title={title}::{p}", flush=True)
    summary(title, [f"{len(entries)} songs, {len(problems)} problems, {len(changed)} changed tracker songs (report)"] + [f"- {p}" for p in problems] + [f"- changed: {c}" for c in changed])
    for p in problems:
        print("FAIL " + p, flush=True)
    print(f"observed hashes: {out}", flush=True)
    return 1 if problems else 0


def merge(files, into):
    data = {"version": 1, "note": "SHA-256 of each corpus song's render, per platform (docs/specs/clips.md §10.1). Made by tools/clip-null.py; no audio.",
            "render": {"beats_cap": BEATS_CAP, "tail_seconds": TAIL}, "platforms": {}}
    if os.path.exists(into):
        with open(into, encoding="utf-8") as f:
            data.update({k: v for k, v in json.load(f).items() if k != "platforms"})
            f.seek(0)
            data["platforms"] = json.load(f)["platforms"]
    for path in files:
        with open(path, encoding="utf-8") as f:
            for key, p in json.load(f)["platforms"].items():
                songs = {n: {k: v for k, v in e.items() if k in ("sha256", "seconds", "silent", "status", "tracker")} for n, e in p["songs"].items()}
                data["platforms"][key] = {"machines": p["machines"], "songs": dict(sorted(songs.items()))}
    with open(into, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=1, sort_keys=True)
        f.write("\n")
    print(f"{into}: {', '.join(sorted(data['platforms']))}")
    return 0


# ---- the open-time measure (clips.md §10.1, the cost gate) -----------------------------------------

# The songs whose open time is measured (the cost gate).
OPEN_SONGS = ["repo/Bass House Template.nbl", "repo/Tech House Template.nbl", "gen/session-loop"]


def opentime(exe, work, baseline, max_ratio, runs=5):
    """Opens each of the largest songs in a fresh process `runs` times; the best time (ms) of song_open."""
    songs = repo_songs()
    key = platform_key()
    gold = None
    if baseline and os.path.exists(baseline):
        with open(baseline, encoding="utf-8") as f:
            gold = json.load(f).get("open_ms", {}).get(key)
    result, problems = {}, []
    for name in OPEN_SONGS:
        path = songs.get(name) or (build_generated(exe, work, name) if name in GENERATED else None)
        if not path:
            continue
        times = []
        for i in range(runs):
            c = nt.Client(exe, os.path.join(work, "open", f"{name.replace('/', '-')}-{i}"), "open")
            try:
                c.initialize()
                t = time.perf_counter()
                c.call("song_open", path=os.path.abspath(path), discard_unsaved=True)
                times.append(round((time.perf_counter() - t) * 1000))
            except RuntimeError:
                times = []
                break
            finally:
                c.close()
        if times:
            result[name] = {"best_ms": min(times), "all_ms": times, "bytes": os.path.getsize(path)}
            was = (gold or {}).get(name, {}).get("best_ms")
            ratio = f"  x{min(times) / was:.2f} of {was} ms" if was else ""
            print(f"open {name:<44} best {min(times):>6} ms of {times}{ratio}", flush=True)
            if was and max_ratio and min(times) > was * max_ratio:
                problems.append(f"{name}: {min(times)} ms, over {max_ratio} x the baseline {was} ms")
    out = os.path.join(work, "open-times.json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump({"platform": key, "open_ms": result}, f, indent=1, sort_keys=True)
    summary(f"Clip open times ({nt.platform_label()})", ["| song | best ms | all |", "|---|---|---|"] + [f"| {n} | {r['best_ms']} | {r['all_ms']} |" for n, r in result.items()])
    for p in problems:
        print("FAIL " + p, flush=True)
    return 1 if problems else 0


# ---- main --------------------------------------------------------------------------------------

def option(argv, name, default=None):
    if name in argv:
        i = argv.index(name)
        value = argv[i + 1]
        del argv[i:i + 2]
        return value
    return default


def main(argv):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except AttributeError:
        pass
    only = option(argv, "--only")
    only = set(only.split(",")) if only else None
    twice = "--twice" in argv
    argv = [a for a in argv if a != "--twice"]
    out = option(argv, "--out")
    goldens = option(argv, "--goldens", os.path.join(ROOT, "eval", "clip-goldens.json"))
    into = option(argv, "--into")
    baseline = option(argv, "--baseline", os.path.join(ROOT, "eval", "clip-goldens.json"))
    max_ratio = option(argv, "--max-ratio")
    if argv and argv[0] == "list":
        for name in repo_songs():
            print(name)
        for name, g in GENERATED.items():
            print(f"{name}: {g['features']}")
        return 0
    if len(argv) >= 3 and argv[0] == "generate":
        for name in GENERATED:
            if not only or name in only:
                print(build_generated(argv[1], argv[2], name))
        return 0
    if len(argv) >= 3 and argv[0] == "render":
        entries, unstable = render_all(argv[1], argv[2], only, twice)
        path = out or os.path.join(argv[2], "observed.json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump(observed(entries), f, indent=1, sort_keys=True)
            f.write("\n")
        print(path)
        return 1 if unstable or any(e.get("status") == "render failed" for e in entries.values()) else 0
    if len(argv) >= 3 and argv[0] == "check":
        return check(argv[1], argv[2], goldens, only, twice)
    if len(argv) >= 2 and argv[0] == "merge" and into:
        return merge(argv[1:], into)
    if len(argv) >= 3 and argv[0] == "opentime":
        return opentime(argv[1], argv[2], baseline, float(max_ratio) if max_ratio else None)
    print(__doc__ if False else "usage: clip-null.py [--audio DIR] list | generate <exe> <out> | render <exe> <work> | check <exe> <work> | merge <observed.json>... --into <goldens> | opentime <exe> <work>")
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
