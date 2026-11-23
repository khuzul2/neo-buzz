#!/usr/bin/env python3
# Fork (docs/specs/clips.md §10, wave CU1): the clip goldens. Every song in the corpus renders to the same
# bits as it did before the clip store moved, on each platform: SHA-256 of each render, per platform, in
# eval/clip-goldens.json (hashes only: no audio is committed). Later waves (CU3 on) must keep every hash.
#
# The corpus:
#  - every song file in the repo (the Buzz-era demos under ExternalMachines, the two templates under Other);
#  - one generated song for each feature a clip can hold (clips.md §3, §10): classic multi-track Note, velocity
#    and command columns; ticks per beat 1, 4 and 8; patterns of 1 and 256 rows; 4 and 6 rows per beat and
#    subdivided beats; MIDI lanes with delay and cut; an explicit Off and an Off with no note before it; a column
#    of another machine's parameter; 8 tracks and a change of track count; a looped session clip with
#    automation. They are written by this file, in a fresh process each: the engine's tools build the song
#    (MCP, as null-test.py does), save it as BMXML, and the tracker's own data (the Modern Pattern Editor's
#    blob, which the tools can't write) is put in the file here, from the editor's format code
#    (song-roundtrip.py's build_mpe). Nothing but this file is checked in.
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
# <exe> is NeoBuzzLive.exe (Windows: the real machines) or rebuzz-headless (Linux and macOS: every classic
# machine is a placeholder, REBUZZ_NATIVE_MACHINES=off, so the old songs render what a placeholder does;
# the generated songs use the Pedal and Neo machines, which are managed code and sound everywhere).
# --twice renders the whole corpus twice in separate processes and fails a song whose two hashes differ:
# that song isn't deterministic (it is named, and goes in EXCLUDED with the reason, or the test is fixed).
# check renders, compares with the goldens of this platform and fails naming each song that differs; with no
# goldens for the platform it fails too, and says where the observed hashes are (<work-dir>/observed.json).

import base64
import hashlib
import importlib.util
import json
import math
import os
import platform
import shutil
import struct
import sys
import time
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, file))
    mod = importlib.util.module_from_spec(spec)
    sys.modules[name] = mod
    spec.loader.exec_module(mod)
    return mod


nt = load("null_test", "null-test.py")           # the MCP client, the waves, the platform label
rt = load("song_roundtrip", "song-roundtrip.py")   # the BMXML reader and the editor's format code

TEMPO = 120
BEATS_CAP = 32          # a render is at most this many beats (a long song is heard from its start)
TAIL = 1.0

# Songs that are not deterministic or can't be hashed, with the reason: {"name": "why"}. Empty: every song counts.
EXCLUDED = {}

# Songs whose clips hold no tracker data (engine columns only): these stay a bit-for-bit gate in every wave. For the others
# (old songs' PATT rows, the editor's data) the goldens are a change report from the wave that moves the tracker on
# (the coordinator's decision of 2026-11-23: one engine plays everything, no emulation of the editor's old timing).
ENGINE_ONLY = {"gen/session-loop"}

TIME_BASE = rt.TIME_BASE        # pattern time per tick (240); the editor's beat is 4 ticks (960)
BEAT = 4 * TIME_BASE
OFF = rt.NOTE_OFF
MIDI_NOTE, MIDI_VELOCITY, MIDI_DELAY, MIDI_CUT, MIDI_PITCH_WHEEL, MIDI_CC = -128, -129, -130, -131, -132, -133

# Buzz notes (octave << 4 | 1..12): C-4 is 0x41.
SCALE = [0x41, 0x44, 0x47, 0x4A, 0x51, 0x4A, 0x47, 0x44]


# ---- the corpus: repo songs --------------------------------------------------------------------

def repo_songs():
    """(name, path) of every song file in the repo, by name."""
    found = []
    for base in ("ExternalMachines", "Other"):
        for folder, dirs, files in os.walk(os.path.join(ROOT, base)):
            dirs[:] = sorted(d for d in dirs if d not in ("bin", "obj", ".git"))
            for f in sorted(files):
                if os.path.splitext(f)[1].lower() in (".bmx", ".bmw", ".nbl", ".bmxml"):
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


def column(machine, index, track, events, beat_rows):
    """One column of the editor's data: events are (time, value)."""
    return {"machine": machine, "index": index, "track": track, "graphical": 0, "events": sorted(events), "beat_rows": list(beat_rows)}


def row_times(beat_rows):
    """The start of each row, in pattern time, for beats that hold the given rows each (the editor's beat is 960)."""
    times = []
    for b, rows in enumerate(beat_rows):
        times += [b * BEAT + k * BEAT // rows for k in range(rows)]
    return times


def build_pxp(patterns):
    """PatternXP's data, version 3 (PatternEditorUtils.ParsePatternXPData): rows, not pattern time. patterns: [{name, rpb, columns}], events (row, value)."""
    out = bytearray(struct.pack("<i", len(patterns)))
    for p in patterns:
        out += p["name"].encode("ascii") + b"\0" + struct.pack("<ii", p["rpb"], len(p["columns"]))
        for c in p["columns"]:
            out += c["machine"].encode("ascii") + b"\0" + struct.pack("<iiBi", c["index"], c["track"], 0, len(c["events"]))
            for row, value in c["events"]:
                out += struct.pack("<ii", row, value)
    return bytes([3]) + bytes(out)


def patch_editor(path, machine, patterns, data=None):
    """Put the editor's data (patterns: [{name, beats, rpb, columns}], or data as it is) in the song saved at path, as the editor of machine keeps it."""
    tree = ET.parse(path)
    root = tree.getroot()
    model = {m["name"]: m for m in rt.parse_bmxml(path)["machines"]}
    editor = model[machine]["editor"]
    if not editor:
        raise RuntimeError(f"{machine} has no pattern editor in {path}")
    for me in root.find("Machines"):
        if rt.model_name(me.findtext("Name")) == editor:
            me.find("Data").text = base64.b64encode(data if data is not None else rt.build_mpe(patterns)).decode()
            tree.write(path, encoding="utf-8", xml_declaration=True)
            return
    raise RuntimeError(f"{editor} is not in {path}")


def param_index(path, machine, name):
    """The editor's index of a machine's parameter: globals first, then the track parameters (MPEPattern's order)."""
    m = {x["name"]: x for x in rt.parse_bmxml(path)["machines"]}[machine]
    allp = []
    for g in m["groups"][1:3]:
        allp += [p["def"] for p in g["params"]]
    for i, d in enumerate(allp):
        if d[1] == name:
            return i, d
    raise RuntimeError(f"{machine} has no parameter {name!r}: {[d[1] for d in allp]}")


def tracker_columns(path, machine, tracks, beat_rows, note_rows, off_rows=(), cmd=True, volume=True):
    """
    The classic tracker columns of a Pedal Tracker (Note, Volume, Cmd, Arg, Cmd2, Arg2, per track): on each track a
    note every few rows (a different one each, by track), Offs on off_rows, volumes and commands between.
    """
    idx = {n: param_index(path, machine, n)[0] for n in ("Volume", "Cmd", "Arg", "Cmd2", "Arg2")}
    note_col = param_index(path, machine, "Wave")[0] - 1        # the Note column comes first among the track parameters
    times = row_times(beat_rows)
    cols = []
    for t in range(tracks):
        notes, vols, cmds, args, cmds2, args2 = [], [], [], [], [], []
        for k in note_rows:
            if k < len(times):
                notes.append((times[k], SCALE[(k // 2 + t) % len(SCALE)]))
                vols.append((times[k], 96 + (13 * (k + t)) % 100))
        for k in off_rows:
            if k < len(times):
                notes.append((times[k], OFF))
        if cmd:
            for j, k in enumerate(note_rows):
                if k < len(times) and j % 2 == 1:
                    cmds.append((times[k], 1 + (j + t) % 6))
                    args.append((times[k], 8 + 4 * ((j + t) % 5)))
                    cmds2.append((times[k], 2))
                    args2.append((times[k], 3 + j % 4))
        cols.append(column(machine, note_col, t, notes, beat_rows))
        if volume:
            cols.append(column(machine, idx["Volume"], t, vols, beat_rows))
        if cmd:
            cols += [column(machine, idx["Cmd"], t, cmds, beat_rows), column(machine, idx["Arg"], t, args, beat_rows),
                     column(machine, idx["Cmd2"], t, cmds2, beat_rows), column(machine, idx["Arg2"], t, args2, beat_rows)]
    return cols


def tracker_song(b, path, *, tpb=4, rows=32, tracks=2, rpb=4, beat_rows=None, note_rows=None, off_rows=(), patterns=None, cmd=True):
    """
    A Pedal Tracker (classic Note, Wave, Volume and two command columns per track) playing one or more patterns
    of the editor's data. patterns: [(name, rows, note_rows, off_rows)] (default one, "A").
    """
    patterns = patterns or [("A", rows, note_rows if note_rows is not None else list(range(0, rows, 4)), off_rows)]
    b.call("song_set", tempo_bpm=TEMPO, ticks_per_beat=tpb)
    b.call("machine_add", gear="Pedal Tracker", name="Trk")
    b.wave()
    if tracks != 1:
        b.call("machine_set", machine="Trk", tracks=tracks)
    at, total = 0, 0.0
    for name, n, _, _ in patterns:
        b.call("pattern_create", machine="Trk", name=name, length_beats=n / tpb)
        b.call("arrangement_place", machine="Trk", pattern=name, at=[total])
        total += n / tpb
    b.call("song_set", song_end_beats=total)
    b.save(path)
    blobs = []
    for name, n, nr, orows in patterns:
        beats = max(1, -(-n // 4))
        br = list(beat_rows) if beat_rows else [rpb] * beats
        blobs.append({"name": name, "beats": beats, "rpb": rpb, "columns": tracker_columns(path, "Trk", tracks, br, nr, orows, cmd=cmd)})
    patch_editor(path, "Trk", blobs)
    return path


GENERATED = {}


def generated(name, features):
    def add(fn):
        GENERATED[name] = {"features": features, "build": fn}
        return fn
    return add


@generated("gen/classic-tracks", "classic multi-track Note, Volume and command columns (Cmd/Arg, Cmd2/Arg2), 3 tracks, 4 rows a beat")
def _(b, path):
    return tracker_song(b, path, tracks=3, rows=32)


for _tpb in (1, 4, 8):
    @generated(f"gen/tpb{_tpb}", f"ticks per beat {_tpb}: the editor's rows are ticks, so the same rows play at another speed")
    def _(b, path, _tpb=_tpb):
        return tracker_song(b, path, tpb=_tpb, rows=32, tracks=2)


@generated("gen/rows1", "a pattern of one row between two of eight rows (the 1-row pattern plays on its own tick)")
def _(b, path):
    return tracker_song(b, path, tracks=2, patterns=[("A", 8, [0, 4], ()), ("One", 1, [0], ()), ("B", 8, [0, 2, 4, 6], ())])


@generated("gen/rows256", "a pattern of 256 rows (64 beats), a note every 8 rows, on 2 tracks")
def _(b, path):
    return tracker_song(b, path, tracks=2, rows=256, note_rows=list(range(0, 256, 8)))


@generated("gen/rpb6", "6 rows a beat: a row is 1/6 of the editor's beat (160 pattern time), between ticks")
def _(b, path):
    return tracker_song(b, path, tracks=2, rows=32, rpb=6, note_rows=list(range(0, 48, 3)))


@generated("gen/subdivided", "beats of 4, 3, 6 and 4 rows (the editor's per-beat rows), notes on every row of some beats")
def _(b, path):
    return tracker_song(b, path, tracks=2, rows=16, beat_rows=[4, 3, 6, 4], note_rows=[0, 2, 4, 5, 7, 9, 10, 11, 13, 14, 16])


@generated("gen/explicit-off", "a note ended by an explicit Off, and in the next pattern an Off with no note before it")
def _(b, path):
    return tracker_song(b, path, tracks=2, patterns=[("A", 16, [0, 8], [4]), ("B", 16, [4, 12], [0, 8])])


def midi_lanes_song(b, path, rpb, rows_per_note, cut_every, label):
    """A Neo Poly (MIDI) whose pattern is in the editor's MIDI note lanes: Note, Velocity, Delay and Cut on two tracks (voices)."""
    b.call("song_set", tempo_bpm=TEMPO)
    b.call("machine_add", gear="Neo Poly", name="Lead")
    b.call("pattern_create", machine="Lead", name="A", length_beats=8)
    b.call("arrangement_place", machine="Lead", pattern="A", at=[0])
    b.call("song_set", song_end_beats=8)
    b.save(path)
    beat_rows = [rpb] * 8
    times = row_times(beat_rows)
    cols = []
    for t in range(2):
        notes, vels, delays, cuts = [], [], [], []
        for j, k in enumerate(range(0, len(times), rows_per_note + t)):
            notes.append((times[k], SCALE[(j + 3 * t) % len(SCALE)]))
            vels.append((times[k], 40 + (29 * (j + t)) % 87))
            delays.append((times[k], (7 * j + 11 * t) % 96))              # a fraction of the row: 0..95 of 96
            if j % cut_every == cut_every - 1:
                cuts.append((times[k], 55 + (13 * j) % 41))               # the note ends this far into the row
        cols += [column("Lead", MIDI_NOTE, t, notes, beat_rows), column("Lead", MIDI_VELOCITY, t, vels, beat_rows),
                 column("Lead", MIDI_DELAY, t, delays, beat_rows), column("Lead", MIDI_CUT, t, cuts, beat_rows)]
    patch_editor(path, "Lead", [{"name": "A", "beats": 8, "rpb": rpb, "columns": cols}])
    return path


@generated("gen/midi-lanes", "MIDI note lanes with velocity, delay and cut on two tracks (voices), 2 rows a beat (a row is 2 ticks, so delay and cut reach the audio)")
def _(b, path):
    return midi_lanes_song(b, path, 2, 1, 2, "midi-lanes")


@generated("gen/midi-lanes-rpb4", "the same at 4 rows a beat (a row is a tick: the editor fires a lane note on a tick, so a delay within the row is not heard and a cut within it ends the note at once)")
def _(b, path):
    return midi_lanes_song(b, path, 4, 3, 3, "midi-lanes-rpb4")


@generated("gen/other-machine", "a pattern of one machine with columns of another machine's parameters (an effect's cutoff and a gain), and a MIDI lane")
def _(b, path):
    b.call("song_set", tempo_bpm=TEMPO)
    b.call("machine_add", gear="Neo Poly", name="Lead")
    b.call("machine_add", gear="Neo Filter", name="Filt", insert_after="Lead")
    b.call("pattern_create", machine="Lead", name="A", length_beats=8)
    b.call("arrangement_place", machine="Lead", pattern="A", at=[0])
    b.call("song_set", song_end_beats=8)
    b.save(path)
    beat_rows = [4] * 8
    times = row_times(beat_rows)
    cutoff, d = param_index(path, "Filt", "Cutoff")
    lo, hi = d[2], d[3]
    notes = [(times[k], SCALE[j % len(SCALE)]) for j, k in enumerate(range(0, 32, 4))]
    vels = [(times[k], 100) for k in range(0, 32, 4)]
    cut_events = [(times[k], lo + (hi - lo) * (j % 8) // 8) for j, k in enumerate(range(0, 32, 2))]
    cols = [column("Lead", MIDI_NOTE, 0, notes, beat_rows), column("Lead", MIDI_VELOCITY, 0, vels, beat_rows),
            column("Filt", cutoff, 0, cut_events, beat_rows)]
    patch_editor(path, "Lead", [{"name": "A", "beats": 8, "rpb": 4, "columns": cols}])
    return path


@generated("gen/tracks8", "8 tracks, a note on every one (Note, Volume, commands)")
def _(b, path):
    return tracker_song(b, path, tracks=8, rows=32, note_rows=[0, 4, 8, 12, 16, 20, 24, 28])


@generated("gen/track-change", "the machine has 3 tracks but the editor's data holds columns for 6 (a track-count change), and engine columns on 6 written before it dropped to 3")
def _(b, path):
    b.call("song_set", tempo_bpm=TEMPO)
    b.call("machine_add", gear="Pedal Tracker", name="Trk")
    b.wave()
    b.call("machine_set", machine="Trk", tracks=6)
    b.call("pattern_create", machine="Trk", name="A", length_beats=8)
    b.call("pattern_create", machine="Trk", name="E", length_beats=8)
    # Engine columns (the agent's notes) on 6 tracks, then the machine goes down to 3.
    b.call("notes_write", machine="Trk", pattern="E", input="tracks",
           notes=[nt.n(p, i * 0.5, 0.5, 90) for i in range(6) for p in ("C4",)] + [nt.n(p, 3, 1, 100) for p in ("E4", "G4", "B4", "D5", "F5", "A5")])
    b.call("arrangement_place", machine="Trk", pattern="A", at=[0])
    b.call("arrangement_place", machine="Trk", pattern="E", at=[8])
    b.call("song_set", song_end_beats=16)
    b.call("machine_set", machine="Trk", tracks=3)
    b.save(path)
    beat_rows = [4] * 8
    cols = tracker_columns(path, "Trk", 6, beat_rows, list(range(0, 32, 4)))
    patch_editor(path, "Trk", [{"name": "A", "beats": 8, "rpb": 4, "columns": cols}, {"name": "E", "beats": 8, "rpb": 4, "columns": []}])
    return path


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


@generated("gen/chord4", "a chord of 4 notes on one row (4 tracks of a tracker), and 4 notes at one time on MIDI lanes (4 voices)")
def _(b, path):
    return tracker_song(b, path, tracks=4, rows=16, note_rows=[0, 8], cmd=False)


@generated("gen/chord4-midi", "4 notes on one row of the editor's MIDI note lanes, 4 voices (a Neo Poly), with a cut on the last")
def _(b, path):
    b.call("song_set", tempo_bpm=TEMPO)
    b.call("machine_add", gear="Neo Poly", name="Lead")
    b.call("pattern_create", machine="Lead", name="A", length_beats=4)
    b.call("arrangement_place", machine="Lead", pattern="A", at=[0])
    b.call("song_set", song_end_beats=4)
    b.save(path)
    beat_rows = [2] * 4
    times = row_times(beat_rows)
    cols = []
    for t, note in enumerate([0x41, 0x44, 0x47, 0x51]):
        cols += [column("Lead", MIDI_NOTE, t, [(times[0], note), (times[4], note + 2)], beat_rows),
                 column("Lead", MIDI_VELOCITY, t, [(times[0], 90 - 10 * t), (times[4], 100)], beat_rows)]
    cols.append(column("Lead", MIDI_CUT, 3, [(times[0], 70)], beat_rows))
    patch_editor(path, "Lead", [{"name": "A", "beats": 4, "rpb": 2, "columns": cols}])
    return path


@generated("gen/pxp-blob", "PatternXP's data (version 3, rows) on a tracker: ordinary columns, and its special columns (global and track Trigger, Effect, Effect Data) the editor can't place")
def _(b, path):
    tracker_song(b, path, tracks=2, rows=16, cmd=False)
    idx = {n: param_index(path, "Trk", n)[0] for n in ("Wave", "Volume")}
    note = idx["Wave"] - 1
    cols = []
    for t in range(2):
        cols.append({"machine": "Trk", "index": note, "track": t, "events": [(0 + t, SCALE[t]), (4 + t, SCALE[t + 2]), (8, OFF), (10, SCALE[t + 4])]})
        cols.append({"machine": "Trk", "index": idx["Volume"], "track": t, "events": [(0 + t, 120), (4 + t, 160)]})
    # PatternXP's own columns (PatternEditorUtils.InternalParameter): global and track Trigger, Effect 1, Effect 1 Data.
    for index, track in ((-10, 0), (-11, 0), (-12, 0), (-110, 0), (-111, 0), (-112, 0), (-110, 1)):
        cols.append({"machine": "Trk", "index": index, "track": track, "events": [(2, 3), (6, 1)]})
    patch_editor(path, "Trk", None, data=build_pxp([{"name": "A", "rpb": 4, "columns": cols}]))
    return path


def build_generated(exe, work, name):
    """The song file for a generated song, in work/songs, built by a fresh process (kept if it is there already)."""
    path = os.path.abspath(os.path.join(work, "songs", name.split("/", 1)[1] + ".bmxml"))
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

# The largest songs: what the NF1 measures (native-format.md §10) take, and the longest generated one.
OPEN_SONGS = ["repo/FSM_Infector_Demo.bmx", "repo/MadBrain_Dynamite6_Demo.bmx", "repo/FrequencyUnknown_O-Delay_delaytest.bmx",
              "repo/Jeskola_NiNjA_dElaY.bmx", "repo/Bass House Template.nbl", "repo/Tech House Template.nbl", "gen/rows256", "gen/tracks8"]


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
    if platform.system() != "Windows":
        os.environ.setdefault("REBUZZ_NATIVE_MACHINES", "off")     # placeholders, as the goldens say
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
    print(__doc__ if False else "usage: clip-null.py list | generate <exe> <out> | render <exe> <work> | check <exe> <work> | merge <observed.json>... --into <goldens> | opentime <exe> <work>")
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
