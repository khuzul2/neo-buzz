#!/usr/bin/env python3
# Fork (docs/specs/mcp-server.md §12.2, U1): the machine profiler. Drives an MCP server over stdio
# as any client does (rebuzz-headless --agent on Linux and macOS, NeoBuzzLive.exe --agent on Windows,
# which hosts the native machines) and measures every machine in its Gear with the server's own
# tools:
#  - parameters: each one's value texts across its range (machine_describe value_table), read as a
#    unit, a mapping and choices (tools/machine_infer.py);
#  - generators: how they take notes (MIDI, or their Note track parameter), the pitch convention
#    (which note plays 440 Hz, from analyze's pitch metric), polyphony and level at the defaults; one
#    silent on A4 is tried on its manifest's note map (notes.map: {name: key}), then on C2 and D2
#    (General MIDI's kick and snare: Neo Drums, Neo Sampler's Kit mode) and profiled on the key that
#    sounds, as a drum kit, else from its played (trigger) parameters, Note parameter or not;
#  - effects, fed noise and a sine from a Neo Poly: what kind they are, latency, tail, gain at the
#    defaults, whether gain depends on level, harmonics added, stereo and movement;
#  - health: load failures, crashes and hangs, NaN or runaway output, sound from silence, output
#    at parameter extremes, CPU per instance.
#  - U2 (docs/specs/machine-safety.md §3.1): a level test of 4 s renders, the machine fed noise or
#    holding a note: at its defaults, with every parameter at its minimum and at its maximum at once,
#    and, for machines that misbehave that way (and effects with a feedback, resonance, gain or Q
#    parameter), each parameter alone at each end. Each render is diverging (NaN or infinity, above
#    +60 dBFS, or still growing), hot (above 0 dBFS, steady) or fine; where a parameter's end
#    diverges, a bisection finds the edge and the draft gets that parameter's safe_range. A managed
#    machine that can't be built without its UI (WPF) needs a window: its own status.
# It writes a draft manifest per machine (Neo machines keep their generated ones: for them it checks
# the inference against the manifest), a JSON summary and machine-health.md. With --repo it merges
# them into the repository: drafts into Machines/Manifests/<Generators|Effects>/, the summary into
# Machines/Manifests/machine-profile.json (entries from other platforms kept), and the report into
# docs/machine-health.md.
#
#   python3 tools/machine-profiler.py <rebuzz-headless | NeoBuzzLive.exe> [--out DIR] [--only "Pedal*,Jeskola Delay"]
#                                     [--skip PATTERN,...] [--repo ROOT] [--timeout SECONDS]
#   python3 tools/machine-profiler.py --merge DIR --repo ROOT   (an earlier run's --out, e.g. a CI artifact, into ROOT)
#   python3 tools/machine-profiler.py --report ROOT              (rewrite docs/machine-health.md from the summary)
#   python3 tools/machine-profiler.py --pack DIR                 (print an --out folder into the log, for CI)
#   python3 tools/machine-profiler.py --unpack LOG --out DIR     (and take it back out of a downloaded job log)

import argparse
import array
import base64
import datetime
import io
import re
import tarfile
import fnmatch
import json
import math
import os
import platform
import queue
import struct
import subprocess
import sys
import tempfile
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import machine_catalog  # noqa: E402  (category, tags and use_for for every manifest)
import machine_infer as mi  # noqa: E402
import machine_roles as mr  # noqa: E402

TEMPO = 120                 # beats per minute: a beat is 0.5 s
# U2 in depth (mcp-server.md §12.12): which passes run (main() sets them from the flags).
DEPTH = {"on": True, "roles": True, "roles_generated": False, "denormal": True, "triggers": True}
A4 = 69
LEVEL_BEATS = 8             # the level test's render: 4 s
CONFIRM_BEATS = 60          # and 30 s, when the level grew over 4 s (an attack settles; a runaway doesn't)
WARM_BEATS = 1              # a 0.5 s render after a change, before the test: smoothed parameters arrive
HELD_BEATS = 64             # the level test's note, held through the longest render
SOURCE_LEVEL = "-6 dB"      # the level test's noise into effects: peaks near -9 dBFS, a mix's level
FORMAT = "rebuzz-machine-manifest/1"


# ---- MCP client (as tools/null-test.py's, restartable) -----------------------------------------

def windows_app(exe):
    """Whether exe is the Windows app (NeoBuzzLive.exe; ReBuzz.exe in builds from before the rename): it takes no --settings."""
    return os.path.basename(exe).lower() in ("neobuzzlive.exe", "rebuzz.exe")


class ServerGone(Exception):
    """The server died or stopped answering: the machine it was working on crashed or hung."""


class Client:
    def __init__(self, exe, work, timeout):
        self.exe, self.work, self.timeout = exe, work, timeout
        self.proc = None
        self.starts = 0
        self.start()

    def start(self):
        self.starts += 1
        self.renders = []
        os.makedirs(self.work, exist_ok=True)
        args = [self.exe, "--agent"]
        if not windows_app(self.exe):
            args += ["--settings", os.path.abspath(os.path.join(self.work, "settings"))]
        self.stderr_path = os.path.join(self.work, f"server-{self.starts}.stderr.txt")
        self.stderr = open(self.stderr_path, "wb")
        self.proc = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.stderr)
        self.lines = queue.Queue()
        threading.Thread(target=self._read, args=(self.proc, self.lines), daemon=True).start()
        self.next_id = 0
        r = self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "machine-profiler", "version": "1"}},
                         timeout=max(self.timeout, 240))
        if "result" not in r:
            raise RuntimeError(f"initialize: {r}")
        self.server_version = r["result"].get("serverInfo", {}).get("version", "")
        self.proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        self.proc.stdin.flush()

    @staticmethod
    def _read(proc, lines):
        for line in proc.stdout:
            lines.put(line)
        lines.put(None)

    def stderr_tail(self, n=1500):
        self.stderr.flush()
        try:
            with open(self.stderr_path, "rb") as f:
                return f.read()[-n:].decode("utf-8", "replace")
        except OSError:
            return ""

    def request(self, method, params=None, timeout=None):
        self.next_id += 1
        msg = {"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params or {}}
        try:
            self.proc.stdin.write((json.dumps(msg) + "\n").encode("utf-8"))
            self.proc.stdin.flush()
        except OSError:
            raise ServerGone(f"{method}: the server is gone")
        deadline = time.time() + (timeout or self.timeout)
        while True:
            try:
                line = self.lines.get(timeout=max(0.1, deadline - time.time()))
            except queue.Empty:
                raise ServerGone(f"{method}: no answer in {timeout or self.timeout} s")
            if line is None:
                raise ServerGone(f"{method}: the server closed stdout")
            try:
                r = json.loads(line)
            except ValueError:
                continue
            if r.get("id") == self.next_id:
                return r

    def call(self, tool, timeout=None, **args):
        """The tool's structured result; raises ToolFailed on a tool error (the server is fine)."""
        r = self.request("tools/call", {"name": tool, "arguments": args}, timeout)
        res = r.get("result") or {}
        text = (res.get("content") or [{}])[0].get("text", "") if res else json.dumps(r.get("error"))
        if "error" in r or res.get("isError", True):
            raise ToolFailed(f"{tool}: {text[:400]}")
        return res.get("structuredContent") or {}

    def restart(self):
        self.kill()
        self.start()

    def kill(self):
        try:
            self.proc.kill()
            self.proc.wait(20)
        except Exception:
            pass
        try:
            self.stderr.close()
        except Exception:
            pass

    def close(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(60)
        except Exception:
            self.proc.kill()
        self.stderr.close()


class ToolFailed(Exception):
    pass


# ---- audio from the render files -----------------------------------------------------------------

def read_wav(path):
    """A 32-bit float stereo WAV (what render writes): (rate, left, right)."""
    with open(path, "rb") as f:
        data = f.read()
    pos, fmt, samples = 12, None, None
    while pos + 8 <= len(data):
        cid, size = data[pos:pos + 4], struct.unpack_from("<I", data, pos + 4)[0]
        body = data[pos + 8:pos + 8 + size]
        if cid == b"fmt ":
            fmt = struct.unpack_from("<HHIIHH", body)
        elif cid == b"data":
            samples = array.array("f")
            samples.frombytes(body[:len(body) - len(body) % 4])
            if sys.byteorder != "little":
                samples.byteswap()
        pos += 8 + size + (size & 1)
    _, channels, rate, _, _, _ = fmt
    if channels == 1:
        return rate, samples, samples
    return rate, samples[0::2], samples[1::2]


def db(x):
    return 20 * math.log10(x) if x > 0 else -math.inf


def rms(x, a=0, b=None):
    seg = x[a:b]
    if not seg:
        return 0.0
    s = 0.0
    for v in seg:
        if v != v or math.isinf(v):
            return math.inf
        s += v * v
    return math.sqrt(s / len(seg))


def envelope(x, rate, ms=10):
    n = max(1, int(rate * ms / 1000))
    return [rms(x, i, i + n) for i in range(0, len(x) - n + 1, n)]


def first_above(x, threshold):
    for i, v in enumerate(x):
        if abs(v) > threshold:
            return i
    return None


def goertzel(x, rate, freq):
    """Amplitude of one frequency in x (a sine of amplitude A reads A)."""
    k = 2 * math.cos(2 * math.pi * freq / rate)
    s1 = s2 = 0.0
    for v in x:
        s1, s2 = v + k * s1 - s2, s1
    power = s1 * s1 + s2 * s2 - k * s1 * s2
    return 2 * math.sqrt(max(0.0, power)) / len(x)


def correlation(a, b):
    sab = sum(x * y for x, y in zip(a, b))
    saa = sum(x * x for x in a)
    sbb = sum(y * y for y in b)
    return sab / math.sqrt(saa * sbb) if saa > 0 and sbb > 0 else 1.0


def finite_peak(measure):
    p = (measure or {}).get("peaks", {}).get("sample_peak_dbfs")
    return p if isinstance(p, (int, float)) else None


def blown(measure):
    """NaN, infinity or a level above +60 dBFS (a diverging machine reaches hundreds of dB). The
    server writes a NaN peak as null and an infinite one as "inf". Since the engine's guard
    (docs/specs/machine-safety.md §3.2) no NaN or level above +60 dBFS reaches a render: the render
    lists the machine under "diverged" instead (render() below marks the measure)."""
    m = measure or {}
    if m.get("diverged"):
        return True
    p = (m.get("peaks") or {}).get("sample_peak_dbfs")
    if p == "inf":
        return True
    if p is None:
        return not m.get("silent", True)
    return isinstance(p, (int, float)) and p > mi.DIVERGE_DBFS


def peaks_per_second(path):
    """A render's peak in each whole second (both channels, linear), and whether it holds NaN or infinity."""
    rate, left, right = read_wav(path)
    peaks, nonfinite = [], False
    for a in range(0, max(1, len(left) - rate // 2), rate):
        p = 0.0
        for ch in (left, right):
            seg = ch[a:a + rate]
            if not seg:
                continue
            try:
                s = math.fsum(seg)
            except (OverflowError, ValueError):
                s = math.inf
            if s != s or math.isinf(s):
                nonfinite = True
                continue
            p = max(p, max(seg), -min(seg))
        peaks.append(p)
    return peaks, nonfinite


def peak_text(measure):
    """A render's sample peak as words: "+157 dBFS", "infinite", "NaN"."""
    m = measure or {}
    if m.get("diverged"):
        return f"{m['diverged'][0] or 'diverged'}: the engine's guard caught it"
    p = (m.get("peaks") or {}).get("sample_peak_dbfs")
    if p == "inf":
        return "infinite peak"
    if p is None:
        return "silent" if m.get("silent", True) else "NaN"
    return f"{p:+.0f} dBFS peak"


def loudness(measure):
    v = (measure or {}).get("loudness", {}).get("integrated_lufs")
    return v if isinstance(v, (int, float)) else None


# ---- one machine -----------------------------------------------------------------------------------

class Profile:
    def __init__(self, gear, kind):
        self.gear, self.kind = gear, kind
        self.data = {"gear": gear, "kind": kind}
        self.problems, self.warnings, self.notes = [], [], []
        self.hot = []               # loud but steady settings (machine-safety.md §3.1): warnings
        self.diverging = False      # some problem is a runaway level, not a crash or a load failure
        self.window = None          # why it needs a window (its own status)

    def problem(self, text):
        self.problems.append(text)

    def diverges(self, text):
        self.diverging = True
        self.problems.append(text)

    def warn(self, text):
        self.warnings.append(text)


def params_by_kind(describe):
    globals_, tracks = [], []
    for p in describe.get("parameters", []):
        (tracks if p.get("per_track") else globals_).append(p)
    return globals_, tracks


def manifest_entry(p, inferred, safe_range=None, role=None):
    e = {"index": p.get("index", 0), "id": p["name"], "name": p["name"], "group": "",
         "min": p["min"], "max": p["max"], "default": p["default_raw"]}
    if not inferred.get("kind"):
        inferred = {"kind": "int", "a": 0, "confidence": "values"}   # texts that say nothing: raw numbers, kept for its safe range
    for k in ("kind", "a", "b", "exponent", "flag", "unit", "choices", "points"):
        if k in inferred and inferred[k] is not None and inferred[k] != "":
            e[k] = inferred[k]
    if inferred.get("kind") == "switch" and e.get("choices") == ["Off", "On"]:
        e.pop("choices")
    e["description"] = p.get("description", "")
    e["confidence"] = inferred.get("confidence", "values")
    if safe_range:
        # machine-safety.md §3.4: param_set keeps agents inside it unless they pass allow_unsafe.
        e["safe_range"] = safe_range
    if role:
        # U2 in depth (mcp-server.md §12.12): what moving it alone changes, as a role from ManifestRoles.Known.
        e["suggested_role"], e["role_evidence"] = role
    return e


def render(c, frm, to, tail, probes=None, name=None):
    args = {"from": frm, "to": to, "tail_seconds": tail}
    if probes:
        args["probes"] = probes
    if name:
        args["name"] = name
    t = time.time()
    r = c.call("render", timeout=max(c.timeout, 120), **args)
    r["_wall"] = time.time() - t
    # The guard silenced and muted a machine that diverged (machine-safety.md §3.2): note it in the
    # measure, for blown(), and reset the machine for the next render.
    for d in r.get("diverged") or []:
        r.setdefault("measure", {}).setdefault("diverged", []).append(d.get("reason"))
        c.call("machine_set", machine=d.get("machine"), reset=True)
    # The WAVs are read here and then deleted (forget_renders): a run makes thousands.
    c.renders += [r.get("file")] + [p.get("file") for p in r.get("probes", [])]
    return r


def forget_renders(c):
    for f in c.renders:
        try:
            if f:
                os.remove(f)
        except OSError:
            pass
    c.renders = []


def seconds_per_second(c):
    """Render time per second of audio: the faster of two 4 s renders (rough: a few ms per second of noise)."""
    return min(render(c, 0, 8, 0.0)["_wall"] / 4.0 for _ in range(2))


def cpu(c, per_second):
    """Milliseconds of render time per second of audio, over what a render takes without the machine."""
    return round(max(0.0, seconds_per_second(c) - per_second) * 1000, 1)


def baselines(c):
    """Render time per second of audio for an empty song, and with only the effects' test source playing noise (after a warm-up)."""
    c.call("song_new", discard_unsaved=True, tempo_bpm=TEMPO)
    render(c, 0, 8, 0.0)
    empty = seconds_per_second(c)
    c.call("machine_add", gear="Neo Poly", name="Src")
    c.call("param_set", machine="Src", changes=[{"param": p, "value": v} for p, v in SOURCE_NOISE])
    set_notes(c, "Src", [note(A4, 0, 1, 127)], "midi", length=4)
    source = seconds_per_second(c)
    c.call("song_new", discard_unsaved=True, tempo_bpm=TEMPO)
    return {"empty": empty, "source": source}


def probe_file(r, machine):
    return next((p["file"] for p in r.get("probes", []) if p.get("machine") == machine), None)


def set_notes(c, machine, notes, mode, length=16):
    """A fresh pattern "P" of the machine with these notes (sent as written, through mode), placed at 0."""
    try:
        c.call("pattern_delete", machine=machine, pattern="P")
    except ToolFailed:
        pass
    c.call("pattern_create", machine=machine, name="P", length_beats=length)
    if notes:
        c.call("notes_write", machine=machine, pattern="P", notes=notes, input=mode, apply_pitch_offset=False)
    c.call("arrangement_place", machine=machine, pattern="P", at=0)


def note(pitch, start=0, length=1.5, velocity=100):
    return {"pitch": pitch, "start": start, "length": length, "velocity": velocity}


def heard_pitch(c, render_id, until):
    """(median MIDI pitch heard, voiced fraction) over the first note."""
    a = c.call("analyze", source=render_id, metrics=["pitch"], from_seconds=0.02, to_seconds=until)
    p = a.get("pitch") or {}
    hz = p.get("median_hz")
    voiced = (p.get("voiced_percent") or 0) / 100
    if not hz or hz <= 0:
        return None, voiced
    return 69 + 12 * math.log2(hz / 440.0), voiced


def note_number(x):
    """A MIDI note number from a number or a note name ("C2" = 36, "F#2" = 42; C4 = 60); None when it isn't one."""
    if isinstance(x, int) and 0 <= x <= 127:
        return x
    m = re.fullmatch(r"([A-Ga-g])([#b]?)(-?\d)", str(x).strip())
    if not m:
        return None
    n = {"C": 0, "D": 2, "E": 4, "F": 5, "G": 7, "A": 9, "B": 11}[m.group(1).upper()] + {"#": 1, "b": -1, "": 0}[m.group(2)]
    n += 12 * (int(m.group(3)) + 1)
    return n if 0 <= n <= 127 else None


def note_map_keys(manifest):
    """The keys of a manifest's note map (notes.map: {name: MIDI number or note name}, a drum kit's keys), in its order."""
    nm = ((manifest or {}).get("notes") or {}).get("map") or {}
    keys = [note_number(v) for v in (nm.values() if isinstance(nm, dict) else nm)]
    return [k for i, k in enumerate(keys) if k is not None and k not in keys[:i]]


# A drum kit's keys where A4 is silent: General MIDI's kick and snare (Neo Drums, and Neo Sampler's Kit mode, from C2).
KIT_KEYS = [(36, "General MIDI's kick"), (38, "General MIDI's snare")]


def key_of(prof):
    """The note a generator is played on: A4, or the key that made a kit sound where A4 is silent."""
    return (prof.data.get("notes") or {}).get("key") or A4


def profile_generator(c, prof, info, baseline, manifest=None):
    """How it takes notes, its pitch convention, polyphony and level."""
    note_param = info.get("note_parameter")
    takes_midi = info.get("takes_midi")
    modes = ["midi", "tracks"] if takes_midi is not False else ["tracks", "midi"]
    if not note_param:
        modes = ["midi"]
    # Silent until played?
    idle = render(c, 0, 4, 0.0)
    if not idle["measure"].get("silent", True):
        prof.warn(f"sounds with no notes ({finite_peak(idle['measure'])} dBFS peak): a drone, noise or a free-running sequencer")
    if blown(idle["measure"]):
        prof.diverges(f"diverges with no notes ({peak_text(idle['measure'])})")
    def try_modes(pitch=A4):
        for m in modes:
            set_notes(c, "M", [note(pitch)], m)
            r = render(c, 0, 4, 1.0)
            if blown(r["measure"]):
                prof.diverges(f"diverges playing {Pitch(pitch)} ({peak_text(r['measure'])})")
                return m, r
            if not r["measure"].get("silent", True) and (idle["measure"].get("silent", True) or (loudness(r["measure"]) or -99) > (loudness(idle["measure"]) or -99) + 3):
                return m, r
        return None, None

    mode, first = try_modes()
    key, why_key = A4, None
    if mode is None:
        # A drum kit is silent on A4: its manifest's note map first, then General MIDI's kick and snare (C2, D2).
        tries = [(k, "its manifest's note map") for k in note_map_keys(manifest)] + KIT_KEYS
        for k, why in [(k, w) for i, (k, w) in enumerate(tries) if k != A4 and k not in [x for x, _ in tries[:i]]]:
            mode, first = try_modes(k)
            if mode is not None:
                key, why_key = k, why
                break
    if first is not None and blown(first["measure"]):
        prof.data["notes"] = {"input": mode}
        prof.data["type"] = "generator (blows up)"
        return
    if mode is None and note_param:
        # Samplers and trackers play the wavetable: give it a sine on A4 in slot 1 and try again.
        if add_test_wave(c):
            mode, first = try_modes()
            if mode is not None:
                prof.notes.append("plays the wavetable: silent until slot 1 has a wave (profiled with a sawtooth rooted on A4)")
                prof.data["needs_wave"] = True
    notes = {"input": mode or "none"}
    prof.data["notes"] = notes
    if mode is None:
        prof.warn("no sound from a note at its defaults, as MIDI or in its tracks, even with a wave in slot 1" if note_param
                  else "no sound from a MIDI note at its defaults (it has no Note track parameter)")
        prof.data["type"] = "generator (silent at its defaults)"
        return
    m = first["measure"]
    prof.data["level"] = {"note": f"{Pitch(key)} at velocity 100, 1.5 beats", "integrated_lufs": loudness(m), "sample_peak_dbfs": finite_peak(m),
                          "true_peak_dbtp": (m.get("peaks") or {}).get("true_peak_dbtp")}
    prof.data["cpu_ms_per_s"] = cpu(c, baseline["empty"])
    if key != A4:
        # A drum kit: its keys are its drums, so neither A4's pitch nor chords of other keys say anything; the rest of
        # the profile plays this key.
        notes.update({"key": key, "pitched": False, "why_unpitched": f"a drum kit: silent on A4, sounds on {Pitch(key)} ({why_key})"})
        prof.notes.append(f"silent on A4; sounds on {Pitch(key)} ({why_key}), as a drum kit: profiled on that key")
        prof.data["type"] = "unpitched"
        return
    # Pitch: A4 sent, then A3 and A5 to see that it follows the keyboard.
    # Short, decaying notes (a 303's) are voiced for a fifth of the window: enough for a median.
    heard, voiced = heard_pitch(c, first["id"], 0.6)
    found = []
    if heard is not None and voiced >= 0.2:
        found.append((A4, heard))
        for sent in (A4 - 12, A4 + 12):
            set_notes(c, "M", [note(sent)], mode)
            r = render(c, 0, 4, 0.5)
            h, v = heard_pitch(c, r["id"], 0.6)
            if h is not None and v >= 0.2:
                found.append((sent, h))
    offsets = [h - s for s, h in found]
    if len(found) >= 2 and max(offsets) - min(offsets) < 0.6:
        shift = sum(offsets) / len(offsets)
        semis = round(shift)
        notes.update({"pitched": True, "pitch_offset": -semis, "cents": round((shift - semis) * 100, 1),
                      "heard": {Pitch(s): round(h, 2) for s, h in found}})
    else:
        notes.update({"pitched": False, "heard": {Pitch(s): round(h, 2) for s, h in found}})
        if found:
            notes["why_unpitched"] = "its pitch doesn't follow the note by octaves" if len(found) >= 2 else "a pitch only on some notes"
        # U2 in depth (mcp-server.md §12.12): where YIN hears no pitch (a decaying, resonant bass), the harmonic sieve.
        sv = sieve_pitch(c, prof, mode)
        notes["pitch_sieve"] = sv
        if sv.get("pitched"):
            notes.update({"pitched": True, "pitch_offset": sv["pitch_offset"], "cents": sv["cents"], "pitch_method": "harmonic sieve"})
            notes.pop("why_unpitched", None)
    # Polyphony: chords of 1, 2, 4, 8 notes; each voice adds about 3 dB. The pitches are a tritone,
    # a fourth or a major seventh apart, never an octave or a semitone (some synths merge those).
    most = min(8, int(info.get("max_tracks") or 1)) if mode == "tracks" else 8
    levels = {}
    for k in (1, 2, 4, 8):
        if k > most:
            break
        cluster = [note(p, 0, 1.5, 100) for p in CHORD[:k]]
        try:
            set_notes(c, "M", cluster, mode)
        except ToolFailed as e:
            prof.notes.append(f"{k} notes at once: {e}")
            break
        r = render(c, 0, 2, 0.2)
        levels[k] = loudness(r["measure"])
    poly = 1
    for k in (2, 4, 8):
        if k in levels and levels[k] is not None and levels.get(k // 2) is not None and levels[k] - levels[k // 2] >= 1.5:
            poly = k
        else:
            break
    tested = max(levels) if levels else 1
    tracks_max = int(info.get("max_tracks") or 1)
    if mode == "tracks" and poly > 1 and poly == tested and tracks_max > tested:
        poly = tracks_max   # every voice heard up to the largest chord tried: one per track
        notes["polyphony_note"] = f"one voice per track, up to its {tracks_max} tracks"
    notes["polyphony"] = poly
    notes["polyphony_levels"] = {str(k): v for k, v in levels.items()}
    if notes.get("pitched"):
        prof.data["type"] = "polysynth" if poly > 1 else "monosynth"
    else:
        # Drums and noise, or a pitch the analysis can't follow (FM, inharmonic): a person tells which.
        prof.data["type"] = "unpitched"


CHORD = [60, 66, 71, 77, 55, 49, 82, 44]


def add_test_wave(c):
    """A 2 s sawtooth on A4, rendered from a Neo Poly, into wave slot 1 (rooted on A4): harmonics for a sampler's filter
    to work on (U2 in depth; U1's was a sine). False when it can't be made."""
    try:
        c.call("machine_add", gear="Neo Poly", name="WaveSrc", connect_to="Master")
        c.call("param_set", machine="WaveSrc", changes=[{"param": p, "value": v} for p, v in SOURCE_NOISE + SOURCE_SINE + [("O1 shape", "2")]])
        set_notes(c, "WaveSrc", [note(A4, 0, 4.5, 127)], "midi", length=6)
        r = render(c, 0, 5, 0.1, probes=["WaveSrc"])
        c.call("wave_from_render", source=r["id"] + ":WaveSrc", from_seconds=0.05, to_seconds=2.05, slot=1, name="Profiler saw", root_note="A4")
        c.call("machine_remove", machines=["WaveSrc"])
        return True
    except ToolFailed:
        return False


def Pitch(midi):
    names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]
    return f"{names[midi % 12]}{midi // 12 - 1}"


SOURCE_NOISE = [("O1 level", "0%"), ("Noise", "100%"), ("Noise colour", "0%"), ("F1 cutoff", "20000 Hz"), ("F1 reso", "0%"),
                ("F1 env", "0%"), ("F1 key track", "0%"), ("F1 velocity", "0%"), ("F1 drive", "0 dB"), ("Amp attack", "min"),
                ("Amp decay", "max"), ("Amp sustain", "100%"), ("Amp release", "min"), ("Drift", "0%"), ("Vel → amp", "0%"),
                ("Volume", "-6 dB"), ("Unison", "1"), ("M1 dest", "Off"), ("M2 dest", "Off"), ("M3 dest", "Off"), ("M4 dest", "Off")]
SOURCE_SINE = [("O1 level", "100%"), ("O1 shape", "0"), ("Noise", "0%")]


def profile_effect(c, prof, info, baseline):
    """Noise and sines through it from a Neo Poly: kind, latency, tail, gain, level dependence, harmonics, stereo."""
    c.call("machine_add", gear="Neo Poly", name="Src", connect_to="M")
    c.call("param_set", machine="Src", changes=[{"param": p, "value": v} for p, v in SOURCE_NOISE])
    # Silence in: silence out?
    idle = render(c, 0, 2, 0.5, probes=["M"])
    if blown(idle["measure"]):
        prof.diverges(f"diverges with silence in ({peak_text(idle['measure'])})")
    elif not idle["measure"].get("silent", True):
        prof.warn(f"sound from silence ({finite_peak(idle['measure'])} dBFS peak): noise, hum or DC")
    # A noise burst of 1 beat (0.5 s), 3 s to ring out.
    set_notes(c, "Src", [note(A4, 0, 1, 127)], "midi", length=4)
    r = render(c, 0, 1, 3.0, probes=["Src", "M"], name="noise")
    if blown(r["measure"]):
        prof.diverges(f"diverges on noise at its defaults ({peak_text(r['measure'])})")
        prof.data["type"] = "effect (blows up)"
        return
    src_file, out_file = probe_file(r, "Src"), probe_file(r, "M")
    if not src_file or not out_file:
        prof.problem("no probe of its input or output (it feeds nothing?)")
        return
    rate, inl, inr = read_wav(src_file)
    _, outl, outr = read_wav(out_file)
    burst = (int(0.05 * rate), int(0.45 * rate))
    in_rms, out_rms = rms(inl, *burst), (rms(outl, *burst) + rms(outr, *burst)) / 2
    fx = {}
    prof.data["effect"] = fx
    # CPU: the same render without probes, against the source alone (baselines()).
    prof.data["cpu_ms_per_s"] = cpu(c, baseline["source"])
    if out_rms <= 1e-6:
        prof.warn("silent with noise through it at the defaults (a gate closed, a wet mix at 0, or it needs setting up)")
        fx["silent"] = True
        prof.data["type"] = "effect (silent at defaults)"
        return
    fx["gain_db"] = round(db(out_rms) - db(in_rms), 2)
    # Latency: the first sample over -40 dB of the burst's level, out against in.
    t_in = first_above(inl, in_rms * 0.01)
    t_out = first_above([max(abs(a), abs(b)) for a, b in zip(outl, outr)], out_rms * 0.01)
    if t_in is not None and t_out is not None:
        fx["latency_samples"] = max(0, t_out - t_in)
        fx["latency_ms"] = round(max(0, t_out - t_in) / rate * 1000, 2)
    # Tail and echoes from a click: 20 ms of noise, then 3 s to ring out.
    set_notes(c, "Src", [note(A4, 0, 0.04, 127)], "midi", length=4)
    rc = render(c, 0, 1, 3.0, probes=["M"], name="click")
    if blown(rc["measure"]):
        prof.diverges(f"diverges on a click at its defaults ({peak_text(rc['measure'])})")
    else:
        _, cl, cr = read_wav(probe_file(rc, "M"))
        mono = [(a + b) / 2 for a, b in zip(cl, cr)]
        env = envelope(mono, rate, 5)
        peak = max(env) if env else 0
        if peak > 0:
            start = int(0.025 / 0.005)    # after the click
            floor = peak * 0.001           # -60 dB
            last_loud = max((i for i, v in enumerate(env) if v > floor), default=start)
            fx["tail_seconds"] = round(max(0, last_loud + 1 - start) * 0.005, 2)
            if last_loud >= len(env) - 2:
                fx["tail_seconds_note"] = "rings longer than the 3 s rendered"
            # U6 (§12.13): the tail's brightness, so a DC blocker's subsonic decay isn't read as a reverb's tail.
            hz = mr.rms_hz(mono, int(start * 0.005 * rate), int((last_loud + 1) * 0.005 * rate), rate) if last_loud > start else None
            if hz is not None:
                fx["tail_hz"] = round(hz, 1)
            # Echoes: from 60 ms on (pre-delay and early reflections come sooner), the tail rising
            # again by 10 dB or more from a dip. A dense reverb tail doesn't; a delay does, after a gap.
            echoes, low, first, gap = 0, None, None, None
            for i in range(int(0.06 / 0.005), last_loud + 1):
                v = max(env[i], floor)
                low = v if low is None else min(low, v)
                if v > low * 3.16:
                    echoes += 1
                    if first is None:
                        first, gap = round(i * 0.005, 3), low
                    low = v
            fx["echoes"] = echoes
            if first:
                fx["first_echo_seconds"] = first
                fx["gap_before_echo_db"] = round(db(gap / peak), 1)
    # Stereo: correlation of the output's channels (the input is mono).
    fx["output_correlation"] = round(correlation(outl[burst[0]:burst[1]], outr[burst[0]:burst[1]]), 3)
    # Spectrum: third octaves out against in, the level difference taken off.
    a_in = c.call("analyze", source=r["id"] + ":Src", metrics=["spectrum"], from_seconds=0.05, to_seconds=0.45)
    a_out = c.call("analyze", source=r["id"] + ":M", metrics=["spectrum"], from_seconds=0.05, to_seconds=0.45)
    third_in = (a_in.get("spectrum") or {}).get("third_octave") or {}
    third_out = (a_out.get("spectrum") or {}).get("third_octave") or {}
    diffs = []
    for hz, v, o in zip(third_in.get("hz") or [], third_in.get("db") or [], third_out.get("db") or []):
        # Bands the noise fills (Neo Poly's noise, filter open), 40 Hz – 16 kHz.
        if isinstance(v, (int, float)) and isinstance(o, (int, float)) and v > -80 and 40 <= hz <= 16000:
            diffs.append((hz, o - v))
    if diffs:
        mean = sum(d for _, d in diffs) / len(diffs)
        shape = {hz: round(d - mean, 1) for hz, d in diffs}
        spread = max(shape.values()) - min(shape.values())
        fx["spectrum_spread_db"] = round(spread, 1)
        lows = [d for hz, d in shape.items() if hz < 300]
        highs = [d for hz, d in shape.items() if hz > 5000]
        if lows and highs:
            fx["tilt_db"] = round(sum(highs) / len(highs) - sum(lows) / len(lows), 1)
    # Sines at two levels 20 dB apart: level dependence and harmonics.
    c.call("param_set", machine="Src", changes=[{"param": p, "value": v} for p, v in SOURCE_SINE])
    set_notes(c, "Src", [note(A4, 0, 1, 127)], "midi", length=4)
    sine = {}
    for label, vol in (("loud", "-6 dB"), ("quiet", "-26 dB")):
        c.call("param_set", machine="Src", changes=[{"param": "Volume", "value": vol}])
        rs = render(c, 0, 1, 0.2, probes=["Src", "M"], name="sine-" + label)
        if blown(rs["measure"]):
            prof.diverges(f"diverges on a {label} sine at its defaults ({peak_text(rs['measure'])})")
            break
        _, il, _ = read_wav(probe_file(rs, "Src"))
        _, ol, orr = read_wav(probe_file(rs, "M"))
        seg = (int(0.15 * rate), int(0.45 * rate))
        i_r, o_r = rms(il, *seg), (rms(ol, *seg) + rms(orr, *seg)) / 2
        out = [(a + b) / 2 for a, b in zip(ol[seg[0]:seg[1]], orr[seg[0]:seg[1]])]
        f0 = 440.0
        fund = goertzel(out, rate, f0)
        harm = math.sqrt(sum(goertzel(out, rate, f0 * k) ** 2 for k in range(2, 7)))
        fund_in = goertzel(il[seg[0]:seg[1]], rate, f0)
        harm_in = math.sqrt(sum(goertzel(il[seg[0]:seg[1]], rate, f0 * k) ** 2 for k in range(2, 7)))
        env_s = [v for v in envelope(out, rate, 20) if v > 0]
        sine[label] = {"gain_db": round(db(o_r) - db(i_r), 2) if i_r > 0 and o_r > 0 else None,
                       "thd_percent": round(100 * harm / fund, 2) if fund > 1e-9 else None,
                       "thd_in_percent": round(100 * harm_in / fund_in, 2) if fund_in > 1e-9 else None,
                       "movement_db": round(db(max(env_s)) - db(min(env_s)), 2) if env_s else None}
    fx["sine"] = sine
    classify_effect(prof, fx)
    # U2 in depth (mcp-server.md §12.12): the test signal at the defaults: the response (a stepped sine sweep in whole
    # tones), the click's latency, decay and echoes, the level steps' curve and timing.
    f = TestBench(c, prof, "full").measure()
    if f.get("diverged") or f.get("error"):
        prof.notes.append(f"the test signal at the defaults: {f.get('diverged') or f.get('error')}")
        return
    fx["response"] = f.get("response_points")
    fx["response_summary"] = {k: v for k, v in (f.get("response") or {}).items() if k != "shape"}
    fx["impulse"] = {k: v for k, v in (f.get("click") or {}).items()}
    if f.get("steps"):
        fx["steps"] = f["steps"]
    if f.get("thd") is not None:
        fx["thd_percent"] = f["thd"]
    typed = effect_type(f.get("click"), prof.data.get("type"))
    if typed != prof.data.get("type"):
        prof.notes.append(f"typed {typed} from the click's decay and echoes (a 20 ms burst said {prof.data.get('type')})")
        fx["traits"] = [typed] + [t for t in fx.get("traits", []) if t not in ("delay", "reverb", typed)]
        prof.data["type"] = typed


def classify_effect(prof, fx):
    traits = []
    loud, quiet = fx.get("sine", {}).get("loud", {}), fx.get("sine", {}).get("quiet", {})
    # A delay: repeated echoes, or one after a near-silent gap; a reverb: a long smooth tail.
    delay = fx.get("echoes", 0) >= 2 or (fx.get("echoes", 0) == 1 and (fx.get("gap_before_echo_db") or 0) < -40)
    if delay:
        traits.append("delay")
    elif (fx.get("tail_seconds") or 0) > 0.15 and not mr.subsonic(fx.get("tail_hz")):
        traits.append("reverb")
    thd, thd_in = loud.get("thd_percent") or 0, loud.get("thd_in_percent") or 0
    if thd > thd_in + 1.5 and thd > 1.5 * thd_in:
        traits.append("distortion")
    movement = max(loud.get("movement_db") or 0, quiet.get("movement_db") or 0)
    if "delay" not in traits and "reverb" not in traits and (movement > 1.5 or (movement > 1.0 and (fx.get("spectrum_spread_db") or 0) > 6)):
        traits.append("modulation")
    # Level dependence, unless the output moves on its own (a chorus's gain at one instant isn't its gain).
    if movement < 1.0 and loud.get("gain_db") is not None and quiet.get("gain_db") is not None and abs(loud["gain_db"] - quiet["gain_db"]) > 2:
        traits.append("dynamics")
    if (fx.get("spectrum_spread_db") or 0) > 6:
        traits.append("filter or EQ")
    if fx.get("output_correlation", 1) < 0.9 and not traits:
        traits.append("stereo")
    if not traits:
        traits.append("utility")
    fx["traits"] = traits
    prof.data["type"] = traits[0]


def type_from_parameters(prof, globals_):
    """Effects that do nothing at their defaults (an EQ at 0 dB): what their parameters' units say they are."""
    if prof.data.get("type") != "utility":
        return
    units = [(p["name"].lower(), mi.infer(p).get("unit")) for p in globals_]
    hz = [n for n, u in units if u == "Hz"]
    gains = [n for n, u in units if u == "dB" and ("gain" in n or "boost" in n)]
    if any("ratio" in n or "threshold" in n for n, _ in units):
        prof.data["type"] = "dynamics"
        prof.notes.append("unchanged at its defaults; dynamics by its parameters (threshold, ratio)")
    elif len(hz) >= 2 and gains:
        prof.data["type"] = "EQ"
        prof.notes.append("flat at its defaults; an EQ by its parameters (frequencies and gains)")
    elif hz and any("reso" in n or n.endswith(" q") or n == "q" for n, _ in units):
        prof.data["type"] = "filter"
        prof.notes.append("unchanged at its defaults; a filter by its parameters")


class Bench:
    """
    The level test's song (machine-safety.md §3.1): the machine alone, fed noise at a mix's level
    (effects) or holding A4 (generators, as it takes notes; a drum kit hit on its key), through 4 s renders. A render that
    diverges leaves the machine's state running away (delay lines, filter memories), so the song is
    built again after one.
    """

    def __init__(self, c, prof, kind, mode):
        self.c, self.prof, self.kind, self.mode = c, prof, kind, mode
        self.texts = {}      # (param, raw) → the machine's text, from param_set's answers
        self.renders = 0
        self.build()

    def build(self):
        c = self.c
        c.call("song_new", discard_unsaved=True, tempo_bpm=TEMPO)
        c.call("machine_add", gear=self.prof.gear, name="M", connect_to="Master")
        if self.kind == "effect":
            c.call("machine_add", gear="Neo Poly", name="Src", connect_to="M")
            c.call("param_set", machine="Src", changes=[{"param": p, "value": v} for p, v in SOURCE_NOISE] + [{"param": "Volume", "value": SOURCE_LEVEL}])
            set_notes(c, "Src", [note(A4, 0, HELD_BEATS, 127)], "midi", length=HELD_BEATS)
        else:
            if self.prof.data.get("needs_wave"):
                add_test_wave(self.c)
            if self.mode not in (None, "none") and key_of(self.prof) != A4:
                # A drum kit is hit on its key every second, as a trigger is.
                set_notes(c, "M", [note(key_of(self.prof), b, 1, 100) for b in range(0, HELD_BEATS, 2)], self.mode, length=HELD_BEATS)
            elif self.mode not in (None, "none"):
                set_notes(c, "M", [note(A4, 0, HELD_BEATS, 100)], self.mode, length=HELD_BEATS)
            elif self.prof.data.get("trigger"):
                # U2 in depth: a generator that plays from a trigger is hit every second.
                play(c, "M", None, self.prof.data["trigger"], [note(A4, b, 1) for b in range(0, HELD_BEATS, 2)], length=HELD_BEATS)

    def level(self, changes=(), reset=True):
        """(verdict, facts) with these changes (the other parameters at their defaults), then put back.
        "crash" when the server died or hung on it: restarted, and the song built again."""
        try:
            return self._level(changes, reset)
        except ServerGone as e:
            self.c.restart()
            self.build()
            return "crash", {"error": str(e)[:200]}

    def _level(self, changes, reset):
        c = self.c
        if changes:
            try:
                r = c.call("param_set", machine="M", changes=list(changes), allow_unsafe=True)
            except ToolFailed as e:
                return "error", {"error": str(e)[:200]}
            for ch, got in zip(changes, r.get("changed", [])):
                self.texts[(ch["param"], ch.get("track"), ch["value"])] = got.get("value", "")
        # The engine's guard (machine-safety.md §3.2) silences a machine whose block holds NaN, infinity or
        # a peak above +60 dBFS, and render() resets it: the render lists it under "diverged" instead.
        guard = []
        if changes:
            # Machines that smooth parameter changes only while sound flows (Neo Utility's gain) get there first.
            guard += render(c, 0, WARM_BEATS, 0.0)["measure"].get("diverged") or []
        out = render(c, 0, LEVEL_BEATS, 0.0)
        self.renders += 1
        guard += out["measure"].get("diverged") or []
        peaks, nonfinite = peaks_per_second(out["file"])
        verdict, facts = mi.level_verdict(peaks, nonfinite or (out["measure"].get("peaks") or {}).get("sample_peak_dbfs") == "inf")
        if not guard and (verdict == "growing" or mi.piling_up(verdict, facts)):
            # Over 30 s: a runaway keeps growing; a lossless loop (feedback at ±100 %) piles noise up for good.
            longer = render(c, 0, CONFIRM_BEATS, 0.0)
            self.renders += 1
            peaks, nonfinite = peaks_per_second(longer["file"])
            verdict, facts = mi.growth_verdict(peaks, nonfinite)
            facts["over_seconds"] = round(CONFIRM_BEATS * 60 / TEMPO)
            guard += longer["measure"].get("diverged") or []
            if verdict != "diverging" and not guard and mi.never_decays(peaks):
                facts["never_decays"] = True
        forget_renders(c)
        if guard:
            verdict, facts = "diverging", dict(facts, guard=str(guard[0] or "diverged"))
        if verdict == "diverging":
            self.build()
        elif changes and reset:
            c.call("param_set", machine="M", changes=[{k: v for k, v in ch.items() if k != "value"} | {"value": "default"} for ch in changes],
                   allow_unsafe=True)
        return verdict, facts

    def text(self, change):
        return self.texts.get((change["param"], change.get("track"), change["value"]), change["value"])


def describe_level(verdict, facts):
    """ "+157 dBFS, growing", "+8.2 dBFS peak", "NaN" for a level test's result."""
    p = facts.get("peak_dbfs")
    if facts.get("nonfinite"):
        return "NaN or infinity"
    if facts.get("guard"):
        return f"{facts['guard']}: the engine's guard caught it"
    peak = "infinite" if p == "inf" else "silent" if p is None else f"{p:+.1f} dBFS peak"
    if facts.get("over_seconds") and p is not None and p != "inf":
        peak += f" over {facts['over_seconds']} s"
    if verdict == "diverging" and isinstance(facts.get("growth_db"), (int, float)) and facts["growth_db"] > mi.GROWTH_DB:
        return f"{peak}, growing {facts['growth_db']:+.0f} dB"
    if facts.get("never_decays"):
        return f"{peak}, never decays"
    return peak


def level_tests(c, prof, kind, globals_, tracks):
    """
    machine-safety.md §3.1: the machine's level at its defaults, with every global parameter at
    its minimum and at its maximum at once, then each parameter alone at each end (for machines
    that misbehave all at once, and effects with a feedback, resonance, gain or Q parameter),
    bisecting to the edge where an end diverges: that parameter's safe_range.
    """
    bench = Bench(c, prof, kind, (prof.data.get("notes") or {}).get("input"))
    out = {}
    prof.data["level_test"] = out
    verdict, facts = bench.level()
    out["defaults"] = dict(facts, verdict=verdict)
    if verdict == "diverging":
        prof.diverges(f"diverges at its defaults ({describe_level(verdict, facts)})")
        return
    if verdict in ("crash", "error"):
        prof.problem(f"the level test at its defaults failed: {facts.get('error')}")
        return
    if verdict == "hot":
        prof.hot.append(f"hot at its defaults ({describe_level(verdict, facts)})")
    elif facts.get("never_decays"):
        prof.warn(f"never decays at its defaults ({describe_level(verdict, facts)}): a lossless loop")
    together = {}
    for end in ("min", "max"):
        changes = [{"param": f"#{p['index']}", "value": f"raw:{p[end]}"} for p in globals_ if "index" in p]
        if not changes:
            break
        v, f = bench.level(changes)
        together[end] = dict(f, verdict=v)
    out["all_at_once"] = together
    misbehaved = [e for e, r in together.items() if r["verdict"] in ("diverging", "hot", "crash")]
    named = mi.sweep_worthy([p["name"] for p in globals_ + tracks]) if kind == "effect" else []
    if not misbehaved and not named:
        return
    out["swept_because"] = (["every parameter at its " + "/".join(f"{e}imum" for e in misbehaved) + " at once: " +
                             "; ".join(f"{together[e]['verdict']}" for e in misbehaved)] if misbehaved else []) + \
                           ([f"an effect with {', '.join(named[:4])}"] if named else [])
    sweeps, ranges, alone_diverges, alone_hot = {}, {}, False, False
    diverges_at = set()          # the ends ("min", "max") at which some parameter alone diverges
    hot, endless = [], []
    default_peak = out["defaults"].get("peak_dbfs")
    for p in globals_ + tracks:
        per_track = bool(p.get("per_track"))
        lo, hi = p["min"], p["max"]
        default = min(max(p["default_raw"], lo), hi)
        ref = {"param": p["name"], "track": 0} if per_track else {"param": f"#{p['index']}"}
        ends, bad, lossless, ringing = {}, {}, {}, {}
        for end, raw in (("min", lo), ("max", hi)):
            if raw == default:
                continue
            change = dict(ref, value=f"raw:{raw}")
            v, f = bench.level([change])
            text = bench.text(change)
            if v in ("diverging", "hot", "error", "crash") or f.get("never_decays"):
                ends[end] = dict(f, verdict=v, raw=raw, text=text)
            if v in ("diverging", "crash"):
                bad[end] = raw
            elif f.get("never_decays"):
                # A lossless loop: kept from agents by the safe range, but hot (a warning), not unhealthy.
                lossless[end] = raw
                (hot if v == "hot" else endless).append(f"{p['name']} at its {end}imum ({text}): {describe_level(v, f)}")
            elif v == "hot":
                hot.append(f"{p['name']} at its {end}imum ({text}): {describe_level(v, f)}")
                # U2 in depth: a loop's or a resonance's end that rings far past its level at the default.
                if mi.rings(p["name"], f.get("peak_dbfs"), default_peak):
                    ringing[end] = raw
        if ends:
            sweeps[p["name"]] = ends
        if not bad and not lossless and not ringing:
            continue
        if bad:
            alone_diverges = True
            diverges_at.update(bad)

        probes = []

        def unsafe(x, ref=ref):
            v, f = bench.level([dict(ref, value=f"raw:{x}")])
            peak = f.get("peak_dbfs")
            probes.append([x, bench.text(dict(ref, value=f"raw:{x}")), peak if peak is not None else None, v])
            return v in ("diverging", "crash") or bool(f.get("never_decays")) or \
                (bool(ringing) and isinstance(peak, (int, float)) and isinstance(default_peak, (int, float)) and peak - default_peak > mi.RING_DB)
        # The edge: the last raw value from the default towards each unsafe end that is safe.
        edges = {}
        for end, raw in list(bad.items()) + list(lossless.items()) + list(ringing.items()):
            if end in edges:
                continue
            del probes[:]
            edges[end] = mi.edge(default, raw, unsafe)
            # The measurements behind the edge: each value tried, its text, peak and verdict.
            ends[end]["edge_probes"] = sorted(probes)
        safe_lo, safe_hi = edges.get("min", lo), edges.get("max", hi)
        texts = {}
        for raw in (safe_lo, safe_hi):
            change = dict(ref, value=f"raw:{raw}")
            texts[raw] = bench.texts.get((change["param"], change.get("track"), change["value"])) or text_at(p, raw)
        how = {end: ("crashes or hangs the server" if ends[end]["verdict"] == "crash" else "diverges") for end in bad}
        how.update({end: "never decays" for end in lossless})
        how.update({end: "rings" for end in ringing if end not in how})
        def says(end):
            if how[end] == "rings":
                pk = ends[end].get("peak_dbfs")
                return (f"rings at its {end}imum ({ends[end]['text']}: {describe_level(ends[end]['verdict'], ends[end])}, "
                        f"{pk - default_peak:.0f} dB over its level at the default; the edge is where it passes {mi.RING_DB:.0f} dB over it)")
            return f"{how[end]} at its {end}imum ({ends[end]['text']}" + \
                (f": {describe_level(ends[end]['verdict'], ends[end])})" if how[end] != "crashes or hangs the server" else ")")
        where = " and ".join(says(end) for end in ("min", "max") if end in how)
        reason = where
        ranges[(per_track, p["name"])] = {"min": safe_lo, "max": safe_hi, "min_text": texts[safe_lo], "max_text": texts[safe_hi], "reason": reason}
        text = f"{p['name']} {reason}; safe from {texts[safe_lo]} to {texts[safe_hi]} (raw {safe_lo}–{safe_hi})"
        if "diverges" in how.values():
            prof.diverges(text)
        elif bad:
            prof.problem(text)
        else:
            prof.notes.append(f"safe range: {text}")
    alone_hot = bool(hot)
    hot.sort(key=lambda s: "never decays" not in s)   # the ones with a safe range first
    if hot:
        shown = hot[:8] + ([f"and {len(hot) - 8} more"] if len(hot) > 8 else [])
        prof.hot.append("hot (loud but steady) with " + "; ".join(shown))
    if endless:
        prof.warn("never decays (a lossless loop, kept out of its safe range) with " + "; ".join(endless))
    for end in misbehaved:
        r = together[end]
        # Every parameter at one end at once diverging, where no parameter alone diverges at that end: a
        # combination no safe range can describe (Jeskola Flanger's −100 % feedback with no delay).
        if r["verdict"] == "crash" and end not in diverges_at:
            prof.warn(f"every parameter at its {end}imum at once crashed or hung the server, but no parameter alone does at its {end}imum")
        elif r["verdict"] == "diverging" and end not in diverges_at:
            prof.warn(f"every parameter at its {end}imum at once diverges ({describe_level('diverging', r)}), but no parameter alone does at its {end}imum")
        elif r["verdict"] == "hot" and not alone_hot and not alone_diverges:
            prof.hot.append(f"hot with every parameter at its {end}imum at once ({describe_level('hot', r)}), but with no parameter alone")
    prof.data["sweeps"] = sweeps
    prof.data["safe_ranges"] = ranges
    out["renders"] = bench.renders


def text_at(p, raw):
    """A parameter's text at a raw value from its value table (the nearest listed raw)."""
    table = p.get("table") or []
    if not table:
        return str(raw)
    return min(table, key=lambda rt: abs(rt[0] - raw))[1]


# ---- U2 in depth (docs/specs/mcp-server.md §12.12) -------------------------------------------------------
# A test signal for effects, played by Neo Poly on three sources (a sine for the stepped sweep, noise for the
# level steps, a 1 ms noise burst for the click) and captured on both sides; per-parameter sweeps that name
# a suggested role; a generator's velocity response, envelope and pitch (harmonic sieve where YIN hears
# none); triggers for generators without notes; denormal slowdowns (the machine's own work time per second
# of audio after its input goes silent, against while it plays).

SWEEP_SRC = SOURCE_NOISE + SOURCE_SINE + [("O1 octave", "raw:4"), ("Amp attack", "min"), ("Amp release", "min")]
CLICK_SRC = SOURCE_NOISE + [("Amp attack", "min"), ("Amp release", "min")]
STEP_SRC = SWEEP_SRC + [("Vel → amp", "100%"), ("Volume", "0 dB")]
STEP_CHORD = [35, 71, 107]      # sent: B2, B5 and B8 sound (123 Hz, 988 Hz, 7.9 kHz), one in each band of a multiband
CLICK_SECONDS = 0.001
DYNAMIC_NAMES = re.compile(r"thresh|treshold|ratio|attack|release|knee|makeup|comp|limit|lookahead|decay lo|decay mid|decay hi", re.I)


def beats(seconds):
    return seconds * TEMPO / 60.0


class TestBench:
    """
    The test signal's song: the machine M fed by three Neo Polys, Ck (a click, first, with time to ring out),
    Sw (a stepped sine sweep) and St (level steps of a three-sine chord), each probed, and M probed. layout
    "full" (at the defaults: 3 s to ring out, whole-tone sweep, steps) or "quick" (per-parameter sweeps: 1.5 s
    to ring out, half-octave sweep, steps for dynamics only). A render that diverges leaves state running away,
    so the song is built again after one.
    """

    def __init__(self, c, prof, layout="quick", steps=False):
        self.c, self.prof, self.layout = c, prof, layout
        self.with_steps = steps or layout == "full"
        self.renders = 0
        self.texts = {}
        self.latency = None
        self.dry_latency = True   # a setting with no dry sound starts where the defaults' did (not a dynamics' lookahead)
        self.fresh = False        # a fresh instance per setting: a loop keeps what the last render left in it
        self.held = []          # a context's changes, set again when the song is built again
        self.click_at = 0.05
        self.ring = {"full": 3.0, "eq": 0.4}.get(layout, 1.5)
        t = self.click_at + self.ring
        if layout in ("full", "eq"):
            self.tones = mr.sweep_tones(step=2, low=16, high=134, start=t)
        else:
            self.tones = mr.sweep_tones(step=6, low=18, high=132, start=t)
        _, _, last_start, last_dur = self.tones[-1]
        t = last_start + last_dur + 0.1
        self.steps, self.jump_at = None, None
        if self.with_steps:
            self.steps, self.jump_at, t = mr.step_notes(t)
        self.end_beats = beats(t)
        self.build()

    def build(self):
        c = self.c
        c.call("song_new", discard_unsaved=True, tempo_bpm=TEMPO)
        c.call("machine_add", gear=self.prof.gear, name="M", connect_to="Master")
        length = int(math.ceil(self.end_beats)) + 1
        self.length = length
        for name, settings in (("Sw", SWEEP_SRC), ("Ck", CLICK_SRC)) + ((("St", STEP_SRC),) if self.with_steps else ()):
            c.call("machine_add", gear="Neo Poly", name=name, connect_to="M")
            c.call("param_set", machine=name, changes=[{"param": p, "value": v} for p, v in settings])
        set_notes(c, "Sw", [note(n, beats(s), beats(d), 127) for n, _, s, d in self.tones], "midi", length=length)
        set_notes(c, "Ck", [note(A4, beats(self.click_at), beats(CLICK_SECONDS), 127)], "midi", length=length)
        if self.with_steps:
            set_notes(c, "St", [note(n, beats(s), beats(d), v) for v, s, d in self.steps for n in STEP_CHORD], "midi", length=length)
        if getattr(self, "held", None):
            c.call("param_set", machine="M", changes=list(self.held), allow_unsafe=True)

    def renew(self):
        """A fresh instance of the machine in the same song (cheaper than building the song again): nothing a
        loop kept from the last render comes back."""
        c = self.c
        c.call("machine_remove", machines=["M"], keep_chain=False)
        c.call("machine_add", gear=self.prof.gear, name="M", connect_to="Master")
        for name in ("Sw", "Ck") + (("St",) if self.with_steps else ()):
            c.call("connect", **{"from": name, "to": "M"})
        if self.held:
            c.call("param_set", machine="M", changes=list(self.held), allow_unsafe=True)

    def measure(self, changes=(), reset=True, retry=True):
        """The features with these changes (the others at their defaults), then put back: response, click,
        steps, thd, peak; {"diverged": reason} when the guard tripped (the song is built again). A loop that
        still rings from the last render when the click comes (feedback near ±100 % never quite dies down in
        the render's pre-roll) spoils it: the song is built again and it is measured once more."""
        c = self.c
        if changes:
            if self.fresh:
                self.renew()
            r = c.call("param_set", machine="M", changes=list(changes), allow_unsafe=True)
            for ch, got in zip(changes, r.get("changed", [])):
                self.texts[(ch["param"], ch.get("track"), ch["value"])] = got.get("value", "")
            # past the patterns: smoothed parameters arrive with nothing played into a loop before the click
            render(c, self.length, self.length + WARM_BEATS, 0.0)
        probes = ["Sw", "Ck", "M"] + (["St"] if self.with_steps else [])
        try:
            r = render(c, 0, self.end_beats, 0.2, probes=probes)
        except ToolFailed as e:
            forget_renders(c)
            return {"error": str(e)[:200]}
        self.renders += 1
        out = {}
        try:
            if r["measure"].get("diverged"):
                out = {"diverged": str(r["measure"]["diverged"][0] or "diverged")}
            else:
                out = self.features(r)
        finally:
            forget_renders(c)
        if out.get("polluted") and retry and not out.get("diverged"):
            self.build()
            again = self.measure(changes, reset, retry=False)
            again["rebuilt"] = True
            return again
        if out.get("diverged"):
            self.build()
        elif changes and reset:
            c.call("param_set", machine="M", changes=[{k: v for k, v in ch.items() if k != "value"} | {"value": "default"} for ch in changes],
                   allow_unsafe=True)
        return out

    def features(self, r):
        files = {p.get("machine"): p.get("file") for p in r.get("probes", [])}
        if not files.get("M"):
            return {"error": "no probe of its output"}
        rate, out_l, out_r = read_wav(files["M"])
        _, sw, _ = read_wav(files["Sw"])
        _, ck, _ = read_wav(files["Ck"])
        peak = max(max(out_l), -min(out_l), max(out_r), -min(out_r)) if len(out_l) else 0.0
        f = {"peak_dbfs": round(db(peak), 2) if peak > 0 else None}
        a = int(self.click_at * rate)
        before = max((abs(v) for v in out_l[max(0, a - int(0.04 * rate)):a]), default=0.0)
        click = mr.click_features(rate, ck, out_l, out_r, self.click_at, CLICK_SECONDS, self.ring,
                                  expected_latency=self.latency if self.dry_latency else None)
        lat = (click or {}).get("latency_samples") or 0
        if self.latency is None and click and not click.get("silent"):
            self.latency = lat          # the defaults' latency: where the dry sound comes, for settings without one
        after = max((abs(v) for v in out_l[a:a + int(0.3 * rate)]), default=0.0)
        if after > 0 and before > after * 0.01:
            f["polluted"] = True        # the last render still rings 40 dB or less under the click
        if click is not None:
            f["click"] = click
        wob = []
        resp = mr.response(self.tones, rate, sw, out_l, out_r, lat, wobble=wob)
        f["response"] = mr.response_features(resp)
        if f["response"] is not None and wob:
            f["response"]["wobble_db"] = round(sorted(wob)[len(wob) // 2], 2)
        if self.layout == "full":
            f["response_points"] = [[hz, d] for hz, d, _ in resp if d is not None]
        mono = [(a + b) / 2 for a, b in zip(out_l, out_r)] if self.layout == "full" else None
        for fh in (1000.0, 250.0):
            tone = min(self.tones, key=lambda t: abs(t[1] - fh))
            _, f0, s, d = tone
            a, b = int((s + 0.35 * d) * rate), int((s + 0.95 * d) * rate)
            th, th_in = mr.harmonics(rate, sw, out_l if mono is None else mono, a, b, f0, lat)
            if th is not None:
                f.setdefault("thd", round(th, 2))
                f.setdefault("thd_in", round(th_in, 2) if th_in is not None else None)
                break
        if self.with_steps:
            _, st, _ = read_wav(files["St"])
            f["steps"] = mr.step_features(rate, st, out_l, out_r, self.steps, self.jump_at, lat)
        return f

    def text(self, change):
        return self.texts.get((change["param"], change.get("track"), change["value"]), change["value"])


def compact(f):
    """A setting's features for the draft: the numbers a person reads, not the curves."""
    if not f:
        return f
    out = {}
    if f.get("diverged") or f.get("error"):
        return {k: f[k] for k in ("diverged", "error") if k in f}
    r = f.get("response") or {}
    for k in ("level_db", "spread_db", "centroid_oct", "max_hz", "prominence_db", "low_corner_hz", "high_corner_hz", "width_oct", "wobble_db"):
        if r.get(k) is not None:
            out[k] = r[k]
    ck = f.get("click") or {}
    for k in ("latency_samples", "direct_db", "early_db", "late_db", "t60_s", "ring_s", "late_onset_s", "copy_s", "tail_zcr_hz", "tail_low_db"):
        if ck.get(k) is not None:
            out[k] = ck[k]
    if ck.get("echoes"):
        out["echoes"] = ck["echoes"][:4]
    st = f.get("steps") or {}
    for k in ("compression_db", "knee_in_db", "attack_tau_s", "release_tau_s"):
        if st.get(k) is not None:
            out[k] = st[k]
    for k in ("thd", "peak_dbfs", "pitch", "centroid_hz", "peakiness_db", "tprom_db", "tprom_hz"):
        if f.get(k) is not None:
            out[k] = f[k]
    env = f.get("env") or {}
    for k in ("attack_s", "half_s", "final_db", "release_s", "length_s", "peak_db", "bright_early_hz", "bright_mid_hz", "bright_late_hz", "level_wobble_db", "bright_wobble"):
        if env.get(k) is not None:
            out["env_" + k] = env[k]
    return out


def effect_type(click, prior):
    """Delay or reverb from the click (U1 typed from a 20 ms burst and a 5 ms envelope): regular, separated echoes
    are a delay; a smooth tail of 0.3 s or more without them a reverb. Otherwise the prior type stands."""
    if not click or click.get("silent"):
        return prior
    ech = click.get("echoes") or []
    times = [t for t, _ in ech]
    if len(times) >= 3:
        gaps = [b - a for a, b in zip(times, times[1:])]
        mean = sum(gaps) / len(gaps)
        if mean > 0.02 and max(abs(g - mean) for g in gaps) < 0.25 * mean:
            return "delay"
    t60 = click.get("t60_s")
    if prior in ("delay", "reverb") and isinstance(t60, (int, float)) and t60 >= 0.3 and len(times) < 3 and (click.get("late_db") or -120) > -40 \
            and not mr.subsonic(click.get("tail_zcr_hz")):
        return "reverb"
    return prior



def role_key(p, globals_):
    """A parameter's key in profile.roles: its name ("track Volume" for a track parameter), with its index where a
    global name repeats (FSM Infector's " - Decay #12")."""
    if p.get("per_track"):
        return "track " + p["name"]
    if sum(1 for q in globals_ if q["name"] == p["name"]) > 1:
        return f"{p['name']} #{p.get('index')}"
    return p["name"]


def eq_bands(names):
    """Band names for an EQ's n gain (or frequency) parameters in order."""
    n = len(names)
    words = {1: ["mid"], 2: ["low", "high"], 3: ["low", "mid", "high"], 4: ["low", "lowmid", "highmid", "high"],
             5: ["low", "lowmid", "mid", "highmid", "high"]}.get(n)
    return dict(zip(names, words)) if words else {}


def unit_of(p, inferred):
    return (inferred.get((bool(p.get("per_track")), p["name"])) or {}).get("unit")


def effect_role_sweeps(c, prof, globals_, tracks, inferred, ranges):
    """Each parameter alone at its minimum and maximum (inside its safe range), against the defaults, through the
    test signal: the features and a suggested role per parameter (prof.data["roles"]). An EQ's frequencies and widths
    are measured with their band raised (they do nothing at 0 dB); parameters that change nothing at the defaults
    and read as times or frequencies get a second look with what the sweep found to switch the effect on (a wet
    level, a feedback) half way up."""
    fx_type = prof.data.get("type", "")
    dyn = fx_type == "dynamics" or any(DYNAMIC_NAMES.search(p["name"]) for p in globals_ + tracks)
    gains = [p for p in globals_ if unit_of(p, inferred) == "dB" and re.search(r"gain|boost|level", p["name"], re.I)]
    freqs = [p for p in globals_ if unit_of(p, inferred) == "Hz" or re.search(r"freq|cutoff|\bcut\b", p["name"], re.I)]
    is_eq = len(gains) >= 2 and fx_type in ("EQ", "utility", "filter or EQ", "filter")
    # an EQ's bands are read on whole tones (a narrow band falls between half-octave tones)
    bench = TestBench(c, prof, "eq" if is_eq else "quick", steps=dyn)
    bench.dry_latency = fx_type != "dynamics"
    base = bench.measure()
    bc = base.get("click") or {}
    bench.fresh = fx_type in ("delay", "reverb", "modulation", "multi-effect") or bool(bc.get("echoes")) or \
        (bc.get("late_db") if bc.get("late_db") is not None else -120) > -60 or bool(re.search(r"feed\s*back|\bfb\b|regen", " ".join(p["name"] for p in globals_ + tracks), re.I))
    if base.get("diverged") or base.get("error") or not base.get("response"):
        prof.notes.append(f"per-parameter sweeps skipped: the test signal at the defaults {base.get('diverged') or base.get('error') or 'gave no response'}")
        return
    kind_type = effect_type(base.get("click"), fx_type)
    gain_band = eq_bands([p["name"] for p in gains]) if is_eq else {}
    freq_band = eq_bands([p["name"] for p in freqs]) if is_eq and len(freqs) <= 5 else {}

    def band_of(p):
        if p["name"] in gain_band:
            return gain_band[p["name"]]
        if p["name"] in freq_band:
            return freq_band[p["name"]]
        if is_eq and re.search(r"\bq\b|width|bandw|slope", p["name"], re.I):
            before = [f for f in freqs if f.get("index", 0) < p.get("index", -1)]
            return freq_band.get(before[-1]["name"]) if before else None
        return None

    def sweep(p, lo, hi, default, ref, info, context=None, band=None):
        """(role, why, ends, texts) for one parameter, in a context (changes held while it is measured)."""
        if context:
            c.call("param_set", machine="M", changes=context, allow_unsafe=True)
            bench.held = list(context)
        try:
            cbase = bench.measure(reset=False) if context else base
            ends, texts = {}, {}
            for end, raw in (("min", lo), ("max", hi)):
                if raw == default:
                    ends[end], texts[end] = cbase, text_at(p, raw)
                    continue
                ch = dict(ref, value=f"raw:{raw}")
                ends[end] = bench.measure([ch])
                texts[end] = bench.text(ch)
            role, why = mr.suggest_effect_role(info, ends, cbase, kind_type, eq_band=band)
            if role == mr.NEED_MID:
                raw = default if lo < default < hi else (lo + hi) // 2
                mid = cbase if raw == default else bench.measure([dict(ref, value=f"raw:{raw}")])
                role, why = mr.suggest_effect_role(info, ends, cbase, kind_type, eq_band=band, mid=mid)
                if role == mr.NEED_MID:
                    role, why = None, "undecided"
            return role, why, ends, texts
        finally:
            if context:
                bench.held = []
                c.call("param_set", machine="M", changes=[dict(x, value="default") for x in context], allow_unsafe=True)

    roles, plan, later = {}, [], []
    for p in globals_ + tracks:
        per_track = bool(p.get("per_track"))
        sr = ranges.get((per_track, p["name"]))
        lo, hi = (sr["min"], sr["max"]) if sr else (p["min"], p["max"])
        default = min(max(p["default_raw"], p["min"]), p["max"])
        ref = {"param": p["name"], "track": 0} if per_track else {"param": f"#{p['index']}"}
        entry = inferred.get((per_track, p["name"])) or {}
        info = {"name": p["name"], "kind": entry.get("kind"), "choices": entry.get("choices"), "min": lo, "max": hi, "unit": entry.get("unit"),
                "texts": [t for _, t in (p.get("table") or [])][:12] if (hi - lo) <= 12 else None, "gain": p in gains}
        plan.append((p, lo, hi, default, ref, info))
    for p, lo, hi, default, ref, info in plan:
        band = band_of(p) if not p.get("per_track") else None
        context, note_ = None, ""
        if band and p not in gains:
            # an EQ's frequency or width: its band raised (its gain at its maximum), the others as they are
            g = next((g for g in gains if gain_band.get(g["name"]) == band), None)
            if g:
                context = [{"param": f"#{g['index']}", "value": f"raw:{g['max']}"}]
                note_ = f" (with {g['name']} at its maximum)"
        role, why, ends, texts = sweep(p, lo, hi, default, ref, info, context, band)
        if role is None and context:
            # a band-split EQ's crossovers move nothing with one band raised alone: every other band raised instead
            alt = [{"param": f"#{g['index']}", "value": "raw:" + str(g["max"] if i % 2 == 0 else g["default_raw"])} for i, g in enumerate(gains)]
            role, why, ends, texts = sweep(p, lo, hi, default, ref, info, alt, band)
            note_ = " (with every other band raised)"
        if role:
            why += note_
        key = role_key(p, globals_)
        roles[key] = {"suggested_role": role, "evidence": why,
                      "min": dict(compact(ends.get("min")), raw=lo, text=texts.get("min")),
                      "max": dict(compact(ends.get("max")), raw=hi, text=texts.get("max")),
                      "_ends": ends}
        if role is None and not band and not any(e.get("diverged") for e in ends.values()):
            later.append((p, lo, hi, default, ref, info, key))
    # A second look, for what does nothing at the defaults because the effect is off there (no wet, no feedback).
    enablers = []
    for p, lo, hi, default, ref, info in plan:
        key = role_key(p, globals_)
        r = roles.get(key) or {}
        if r.get("suggested_role") in ("mix.wet", "delay.feedback", "reverb.decay"):
            ends = r["_ends"]
            # its active end: the one that isn't transparent, or with the longer tail
            active = hi
            if r["suggested_role"] == "mix.wet":
                active = lo if mr._transparent(ends["max"], 6.02) else hi
            else:
                active = hi if (mr._tail((ends["max"].get("click") or {})) or 0) >= (mr._tail((ends["min"].get("click") or {})) or 0) else lo
            raw = int(round(default + 0.6 * (active - default)))
            if raw != default:
                enablers.append(dict(ref, value=f"raw:{raw}"))
    timed = [x for x in later if unit_of(x[0], inferred) in ("s", "ms", "Hz") or any(w in " ".join(str(t) for t in (x[5].get("texts") or [])).lower() for w in ("tick", "ms"))]
    if enablers and timed:
        for p, lo, hi, default, ref, info, key in timed[:12]:
            role, why, ends, texts = sweep(p, lo, hi, default, ref, info, enablers, None)
            if role:
                roles[key] = {"suggested_role": role, "evidence": why + " (with the effect switched on: " +
                              ", ".join(f"{e['param']} {e['value']}" for e in enablers) + ")",
                              "min": dict(compact(ends.get("min")), raw=lo, text=texts.get("min")),
                              "max": dict(compact(ends.get("max")), raw=hi, text=texts.get("max"))}
    # A modulation effect's delay is read from where its copy sits: with the modulation's depth at the end where the
    # response holds still, for what the first pass couldn't place.
    steady = []
    for p, lo, hi, default, ref, info in plan:
        key = role_key(p, globals_)
        r = roles.get(key) or {}
        if r.get("suggested_role") in ("mod.depth", "lfo.depth") and r.get("_ends"):
            wl = ((r["_ends"]["min"].get("response") or {}).get("wobble_db") or 0)
            wh = ((r["_ends"]["max"].get("response") or {}).get("wobble_db") or 0)
            steady.append(dict(ref, value=f"raw:{lo if wl <= wh else hi}"))
    if steady and kind_type == "modulation":
        for p, lo, hi, default, ref, info, key in later[:12]:
            if roles.get(key, {}).get("suggested_role"):
                continue
            role, why, ends, texts = sweep(p, lo, hi, default, ref, info, steady, None)
            if role and role != "mod.depth":
                roles[key] = {"suggested_role": role, "evidence": why + " (with the modulation held still: " +
                              ", ".join(f"{e['param']} {e['value']}" for e in steady) + ")",
                              "min": dict(compact(ends.get("min")), raw=lo, text=texts.get("min")),
                              "max": dict(compact(ends.get("max")), raw=hi, text=texts.get("max"))}
    # And with a filter's cutoff half way (a resonance or a band's width does nothing while the filter is wide open).
    cut = next(((p, lo, hi, ref) for p, lo, hi, default, ref, info in plan
                if (roles.get(role_key(p, globals_)) or {}).get("suggested_role") == "filter.cutoff"), None)
    if cut:
        cp, clo, chi, cref = cut
        half = [dict(cref, value=f"raw:{(clo + chi) // 2}")]
        for p, lo, hi, default, ref, info, key in [x for x in later if not mr.is_choice(x[5]) and x[0] is not cp][:8]:
            if roles.get(key, {}).get("suggested_role"):
                continue
            role, why, ends, texts = sweep(p, lo, hi, default, ref, info, half, None)
            if role:
                roles[key] = {"suggested_role": role, "evidence": why + f" (with {cp['name']} half way)",
                              "min": dict(compact(ends.get("min")), raw=lo, text=texts.get("min")),
                              "max": dict(compact(ends.get("max")), raw=hi, text=texts.get("max"))}
    for r in roles.values():
        r.pop("_ends", None)
    prof.data["roles"] = {"default": compact(base), "type_from_click": kind_type, "params": roles, "renders": bench.renders}


# ---- generators -------------------------------------------------------------------------------------------

class NoteBench:
    """A generator's held note (A4 sent as written, or its trigger), 1 s held and 1 s to let go: the envelope,
    brightness and pitch per setting."""

    def __init__(self, c, prof, mode, trigger=None, held=1.0, tail=1.0, pitch=A4):
        self.c, self.prof, self.mode, self.trigger = c, prof, mode, trigger
        self.held, self.tail, self.pitch = held, tail, pitch
        self.renders = 0
        self.texts = {}
        self.build()

    def build(self):
        c = self.c
        c.call("song_new", discard_unsaved=True, tempo_bpm=TEMPO)
        c.call("machine_add", gear=self.prof.gear, name="M", connect_to="Master")
        if self.prof.data.get("needs_wave"):
            add_test_wave(c)
        play(c, "M", self.mode, self.trigger, [note(self.pitch, 0, beats(self.held), 100)], length=int(beats(self.held + self.tail)) + 2)

    def measure(self, changes=(), reset=True):
        c = self.c
        if changes:
            r = c.call("param_set", machine="M", changes=list(changes), allow_unsafe=True)
            for ch, got in zip(changes, r.get("changed", [])):
                self.texts[(ch["param"], ch.get("track"), ch["value"])] = got.get("value", "")
        try:
            # past the note's end, so its note-off plays (a tracker machine's note is let go by an event, not by stopping)
            r = render(c, 0, beats(self.held + 0.25), self.tail - 0.25)
        except ToolFailed as e:
            forget_renders(c)
            return {"error": str(e)[:200]}
        self.renders += 1
        out = {}
        try:
            if r["measure"].get("diverged"):
                out = {"diverged": str(r["measure"]["diverged"][0] or "diverged")}
            elif r["measure"].get("silent", True):
                out = {"silent": True}
            else:
                rate, left, right = read_wav(r["file"])
                out["env"] = mr.note_envelope(rate, left, right, 0.0, self.held)
                a = c.call("analyze", source=r["id"], metrics=["spectrum", "pitch"], from_seconds=0.03, to_seconds=max(0.1, self.held - 0.05))
                sp = a.get("spectrum") or {}
                out["centroid_hz"] = sp.get("centroid_hz")
                out["peakiness_db"] = mr.peakiness((sp.get("third_octave") or {}).get("db") or [])
                tp = mr.third_prominence((sp.get("third_octave") or {}).get("hz"), (sp.get("third_octave") or {}).get("db"), mr.midi_hz(self.pitch))
                if tp:
                    out["tprom_db"], out["tprom_hz"] = tp
                pt = a.get("pitch") or {}
                if pt.get("median_hz") and (pt.get("voiced_percent") or 0) >= 20:
                    out["pitch"] = round(69 + 12 * math.log2(pt["median_hz"] / 440.0), 2)
        finally:
            forget_renders(c)
        if out.get("diverged"):
            self.build()
        elif changes and reset:
            c.call("param_set", machine="M", changes=[{k: v for k, v in ch.items() if k != "value"} | {"value": "default"} for ch in changes],
                   allow_unsafe=True)
        return out

    def text(self, change):
        return self.texts.get((change["param"], change.get("track"), change["value"]), change["value"])


def play(c, machine, mode, trigger, notes, length=16):
    """A fresh pattern "P" playing these notes: as notes (MIDI or tracks), or each as the generator's trigger."""
    if not trigger:
        set_notes(c, machine, notes, mode, length=length)
        return
    try:
        c.call("pattern_delete", machine=machine, pattern="P")
    except ToolFailed:
        pass
    c.call("pattern_create", machine=machine, name="P", length_beats=length)
    args = {"machine": machine, "pattern": "P", "param": trigger["param"], "shape": "step",
            "points": [{"at": n["start"], "value": f"raw:{trigger['value']}"} for n in notes]}
    if trigger.get("track") is not None:
        args["track"] = trigger["track"]
    c.call("automation_write", **args)
    c.call("arrangement_place", machine=machine, pattern="P", at=0)


def find_trigger(c, prof, globals_):
    """A generator without notes: a played track parameter (machine_describe's played_parameters) or a global one
    named like a trigger that makes it sound when written as an event. None when none does."""
    d = c.call("machine_describe", machine="M")
    cands = [{"param": p["name"], "track": 0, "value": p.get("max_raw", 1)} for p in d.get("played_parameters") or []]
    cands += [{"param": p["name"], "track": None, "value": p["max"]} for p in globals_ if re.search(r"trig", p["name"], re.I)]
    tried, sounding = [], []
    for cand in cands[:16]:
        try:
            play(c, "M", None, cand, [note(A4, 0, 1)], length=8)
            r = render(c, 0, 4, 0.5)
            loud = not r["measure"].get("silent", True)
            forget_renders(c)
        except ToolFailed as e:
            tried.append(f"{cand['param']}: {str(e)[:80]}")
            continue
        tried.append(f"{cand['param']} {'sounds' if loud else 'silent'}")
        if loud:
            sounding.append(cand)
    if sounding:
        first = dict(sounding[0], tried=tried)
        # every trigger that sounds (a drum machine's drums): each parameter is swept with its own drum's
        first["all"] = [{k: x[k] for k in ("param", "track", "value")} for x in sounding]
        return first
    if tried:
        prof.notes.append("no trigger made it sound: " + "; ".join(tried))
    return None


def generator_depth(c, prof, globals_, tracks, inferred, ranges, roles=True):
    """Velocity response, envelope length and, unless roles is False, per-parameter sweeps of a held note."""
    notes = prof.data.get("notes") or {}
    mode, trigger = notes.get("input"), prof.data.get("trigger")
    if (mode in (None, "none")) and not trigger:
        return
    pitch = A4 - (notes.get("pitch_offset") or 0) if notes.get("pitched") else key_of(prof)
    # The envelope: 2 s held, 2 s to let go.
    eb = NoteBench(c, prof, mode, trigger, held=2.0, tail=2.0, pitch=pitch)
    full = eb.measure()
    if full.get("env"):
        prof.data["envelope"] = dict(full["env"], note=f"{Pitch(key_of(prof))} at velocity 100, held 2 s")
    # Velocity: four notes, 16 … 127 (triggers have no velocity).
    if not trigger:
        c.call("song_new", discard_unsaved=True, tempo_bpm=TEMPO)
        c.call("machine_add", gear=prof.gear, name="M", connect_to="Master")
        if prof.data.get("needs_wave"):
            add_test_wave(c)
        vels = [16, 32, 64, 127]
        set_notes(c, "M", [note(pitch, beats(i * 1.0), beats(0.5), v) for i, v in enumerate(vels)], mode, length=10)
        r = render(c, 0, beats(4.0), 0.5)
        rate, left, right = read_wav(r["file"])
        levels = {}
        for i, v in enumerate(vels):
            a, b = int(i * 1.0 * rate), int((i * 1.0 + 0.6) * rate)
            n = int(0.01 * rate)
            best = 0.0
            for k in range(a, b - n, n):
                e = mr.energy(left, k, k + n) + mr.energy(right, k, k + n)
                best = max(best, e / (2 * n))
            levels[v] = round(10 * math.log10(best), 2) if best > 0 else None
        forget_renders(c)
        vr = mr.velocity_response(levels)
        if vr:
            prof.data["velocity"] = dict(vr, levels_db={str(k): v for k, v in levels.items()})
    if not roles:
        return
    # a low note (A1, 55 Hz) fills every third octave from 500 Hz up with harmonics: a resonance's peak stands out (a kit:
    # its key, its drum)
    low = pitch if notes.get("key") else pitch - 36
    nb = NoteBench(c, prof, mode, trigger, pitch=low)
    base = nb.measure()
    if base.get("silent") or base.get("diverged") or base.get("error"):
        prof.notes.append(f"per-parameter sweeps skipped: the held note at the defaults {base}")
        return
    drum = prof.data.get("type") in ("unpitched",) or bool(trigger)
    gtype = "drums" if drum else prof.data.get("type", "")
    out, plan, ends_of = {}, [], {}
    # A drum machine's drums each have their trigger: a parameter is swept with the drum its name starts with ("BD Decay":
    # "BD Trigger"), the others with the first that sounds.
    triggers = prof.data.pop("_triggers", None) or []
    benches, current = {}, [nb]

    def bench_for(p):
        word = (p["name"].split() or [""])[0].lower()
        t = next((x for x in triggers if (x["param"].split() or [""])[0].lower() == word), None)
        if t is None or t["param"] == (trigger or {}).get("param"):
            b, bb = nb, base
        else:
            if t["param"] not in benches:
                b = NoteBench(c, prof, mode, t, pitch=low)
                benches[t["param"]] = (b, b.measure())
                current[0] = b
            b, bb = benches[t["param"]]
        if current[0] is not b:
            b.build()
            current[0] = b
        return b, bb

    def sweep(p, lo, hi, default, ref, info, context=None):
        b, bbase = bench_for(p)
        if context:
            c.call("param_set", machine="M", changes=context, allow_unsafe=True)
        try:
            cbase = b.measure(reset=False) if context else bbase
            ends, texts = {}, {}
            for end, raw in (("min", lo), ("max", hi)):
                if raw == default:
                    ends[end], texts[end] = cbase, text_at(p, raw)
                    continue
                ch = dict(ref, value=f"raw:{raw}")
                ends[end] = b.measure([ch])
                texts[end] = b.text(ch)
            role, why = mr.suggest_generator_role(info, ends, cbase, gtype)
            if b is not nb and role:
                why += f" (hit by {b.trigger['param']})"
            return role, why, ends, texts
        finally:
            if context:
                c.call("param_set", machine="M", changes=[dict(x, value="default") for x in context], allow_unsafe=True)

    for p in globals_ + tracks:
        per_track = bool(p.get("per_track"))
        sr = ranges.get((per_track, p["name"]))
        lo, hi = (sr["min"], sr["max"]) if sr else (p["min"], p["max"])
        default = min(max(p["default_raw"], p["min"]), p["max"])
        ref = {"param": p["name"], "track": 0} if per_track else {"param": f"#{p['index']}"}
        entry = inferred.get((per_track, p["name"])) or {}
        info = {"name": p["name"], "kind": entry.get("kind"), "choices": entry.get("choices"), "min": lo, "max": hi, "unit": entry.get("unit"),
                "texts": [t for _, t in (p.get("table") or [])][:12] if (hi - lo) <= 12 else None}
        role, why, ends, texts = sweep(p, lo, hi, default, ref, info)
        key = role_key(p, globals_)
        plan.append((p, lo, hi, default, ref, info, key))
        ends_of[key] = ends
        out[key] = {"suggested_role": role, "evidence": why,
                    "min": dict(compact(ends.get("min")), raw=lo, text=texts.get("min")),
                    "max": dict(compact(ends.get("max")), raw=hi, text=texts.get("max"))}
    # Key tracking reads as a cutoff on one note: what brightens the low note is tried on a note three octaves up too.
    cutlike = [x for x in plan if out[x[6]]["suggested_role"] == "filter.cutoff" and not (x[5].get("kind") in ("enum", "switch") or x[2] - x[1] <= 4)]
    if cutlike and not drum:
        hb = NoteBench(c, prof, mode, trigger, pitch=pitch)
        current[0] = hb
        hbase = hb.measure()
        if not (hbase.get("silent") or hbase.get("diverged") or hbase.get("error")):
            for p, lo, hi, default, ref, info, key in cutlike[:6]:
                hends = {end: (hbase if raw == default else hb.measure([dict(ref, value=f"raw:{raw}")])) for end, raw in (("min", lo), ("max", hi))}
                kt = mr.keytrack_role(ends_of[key], hends, 36, mr.midi_hz(A4 - 36), mr.midi_hz(A4))   # as they sound
                if kt:
                    out[key]["suggested_role"], out[key]["evidence"] = kt
        nb.renders += hb.renders
    # A second look at times that change nothing: with a choice that shapes the level by the envelope (a VCA's mode)
    # switched to its other setting, the amp follows the envelope instead of the gate.
    def shaped(f):
        e, b_ = (f or {}).get("env") or {}, (base.get("env") or {})
        return any(isinstance(e.get(k), (int, float)) and isinstance(b_.get(k), (int, float)) and abs(e[k] - b_[k]) >= th
                   for k, th in (("final_db", 3), ("half_s", 0.05), ("release_s", 0.1)))
    switch = None
    for p, lo, hi, default, ref, info, key in plan:
        if out[key]["suggested_role"] is None and (info.get("kind") in ("enum", "switch") or hi - lo <= 4):
            for end, raw in (("min", lo), ("max", hi)):
                if raw != default and shaped(ends_of[key].get(end)):
                    switch = [dict(ref, value=f"raw:{raw}")]
                    break
        if switch:
            break
    if switch:
        timed = [x for x in plan if out[x[6]]["suggested_role"] is None and (x[5].get("unit") in ("s", "ms") or
                                                                            re.search(r"attack|decay|release|sustain", x[0]["name"], re.I))]
        for p, lo, hi, default, ref, info, key in timed[:8]:
            role, why, ends, texts = sweep(p, lo, hi, default, ref, info, switch)
            if role:
                out[key] = {"suggested_role": role, "evidence": why + " (with " + ", ".join(f"{e['param']} {e['value']}" for e in switch) + ")",
                            "min": dict(compact(ends.get("min")), raw=lo, text=texts.get("min")),
                            "max": dict(compact(ends.get("max")), raw=hi, text=texts.get("max"))}
    # And with the filter part closed (its cutoff a third of the way up), for what does nothing while it is open: a
    # resonance above the band, a filter envelope's amount and times, key tracking.
    cut = next(((p, lo, hi, ref) for p, lo, hi, default, ref, info, key in plan if out[key]["suggested_role"] == "filter.cutoff"), None)
    if cut:
        cp, clo, chi, cref = cut
        closed = [dict(cref, value=f"raw:{int(clo + 0.35 * (chi - clo))}")]
        rest = [x for x in plan if out[x[6]]["suggested_role"] is None and x[0] is not cp and not (x[5].get("kind") in ("enum", "switch") or x[2] - x[1] <= 4)]
        for p, lo, hi, default, ref, info, key in rest[:12]:
            role, why, ends, texts = sweep(p, lo, hi, default, ref, info, closed)
            if role:
                out[key] = {"suggested_role": role, "evidence": why + f" (with {cp['name']} a third of the way up)",
                            "min": dict(compact(ends.get("min")), raw=lo, text=texts.get("min")),
                            "max": dict(compact(ends.get("max")), raw=hi, text=texts.get("max"))}
    prof.data["roles"] = {"default": compact(base), "params": out, "renders": nb.renders}


def sieve_pitch(c, prof, mode):
    """Where analyze's YIN hears no pitch (a decaying, resonant bass; FM): the harmonic sieve on A2, A3 and A4. The
    pitch convention when all three agree, else unpitched with what it heard."""
    heard = {}
    for sent in (A4 - 24, A4 - 12, A4):
        set_notes(c, "M", [note(sent, 0, 1.5)], mode)
        r = render(c, 0, 2, 0.2)
        rate, left, right = read_wav(r["file"])
        a = int(0.03 * rate)
        mono = [(x + y) / 2 for x, y in zip(left[a:a + 16384], right[a:a + 16384])]
        forget_renders(c)
        if mr.energy(mono, 0, len(mono)) <= 1e-9:
            continue
        binhz, mags = mr.magnitude_spectrum(mono, rate)
        s = mr.pitch_sieve(binhz, mags, sent)
        if s:
            heard[Pitch(sent)] = dict(s, sent=sent)
    pitched = [v for v in heard.values() if v.get("pitched")]
    offs = {v["midi"] - v["sent"] for v in pitched}
    result = {"method": "harmonic sieve (A2, A3, A4)",
              "heard": {k: {"midi": v["midi"], "cents": v["cents"], "share": v["share"]} for k, v in heard.items()}}
    share = sum(v["share"] for v in pitched) / len(pitched) if pitched else 0.0
    if len(pitched) >= 2 and len(offs) == 1 and len(pitched) == len(heard) and share >= 0.6:
        k = offs.pop()
        result.update({"pitched": True, "pitch_offset": -k, "cents": round(sum(v["cents"] for v in pitched) / len(pitched), 1)})
    else:
        result["pitched"] = False
        result["why"] = ("its harmonics hold less than 60 % of the sound (inharmonic or noisy)" if not pitched or share < 0.6
                         else "the notes don't agree on one offset" if len(offs) > 1 else "pitched on some notes only")
    return result


def denormal_test(c, prof, kind):
    """The machine's own work time per second of audio (render's cpu, the engine's counters) while its input plays
    (2 s of noise, or a held note) and over 20 s after it stops, in 2 s windows: a machine whose time climbs once
    its input is silent is working on denormal numbers."""
    notes = prof.data.get("notes") or {}
    c.call("song_new", discard_unsaved=True, tempo_bpm=TEMPO)
    c.call("machine_add", gear=prof.gear, name="M", connect_to="Master")
    if kind == "effect":
        c.call("machine_add", gear="Neo Poly", name="Src", connect_to="M")
        c.call("param_set", machine="Src", changes=[{"param": p, "value": v} for p, v in SOURCE_NOISE] + [{"param": "Amp release", "value": "min"}])
        set_notes(c, "Src", [note(A4, 0, beats(2.0), 127)], "midi", length=8)
    else:
        mode, trigger = notes.get("input"), prof.data.get("trigger")
        if mode in (None, "none") and not trigger:
            return None
        if prof.data.get("needs_wave"):
            add_test_wave(c)
        play(c, "M", mode, trigger, [note(key_of(prof), 0, beats(2.0), 100)], length=8)
    def windows():
        r = c.call("render", **{"from": 0, "to": beats(2.0), "tail_seconds": 20.0, "cpu": True, "cpu_window_seconds": 2.0}, timeout=max(c.timeout, 180))
        c.renders += [r.get("file")]
        forget_renders(c)
        row = next((x for x in r.get("cpu") or [] if x.get("machine") == "M"), None)
        return (row or {}).get("windows_ms_per_second", [])[:11]   # 2 s windows; the last, partial one is left out

    def judge(w):
        playing, after = w[0], w[1:]
        out = {"playing_ms_per_s": playing, "silent_ms_per_s": sorted(after)[len(after) // 2], "silent_worst_ms_per_s": max(after), "windows_ms_per_s": w}
        # slower for 4 s or more on end (two windows in a row), not one window's spike (a collection, a page fault)
        slow = 2 * max(playing, 0.05)
        runs = [min(a, b) for a, b in zip(after, after[1:]) if a > slow and b > slow and min(a, b) - playing >= 1.0]
        if runs:
            out["slowdown"] = round(max(runs) / max(playing, 0.05), 1)
            out["slow_seconds"] = 2 * sum(1 for x in after if x > slow)
        return out

    w = windows()
    if len(w) < 2:
        return None
    out = judge(w)
    if out.get("slowdown"):
        # a busy computer only adds time: a slowdown must show again, window by window, in a second render
        w2 = windows()
        if len(w2) == len(w):
            out = judge([round(min(a, b), 3) for a, b in zip(w, w2)])
            out["renders"] = 2
    return out


def profile_machine(c, gear, kind, baseline, generated):
    prof = Profile(gear, kind)
    started = time.time()
    c.call("song_new", discard_unsaved=True, tempo_bpm=TEMPO)
    t = time.time()
    try:
        try:
            c.call("machine_add", gear=gear, name="M", connect_to="Master")
        except ToolFailed as e:
            if "can't connect" not in str(e):
                raise
            c.call("machine_add", gear=gear, name="M", connect_to="none")   # no audio output (control machines)
            prof.notes.append("has no audio output")
            prof.data["no_output"] = True
    except ToolFailed as e:
        why = mi.needs_window(gear, str(e))
        if why:
            # machine-safety.md §3.1: not an audio fault; it works in NeoBuzz Live on Windows with its window.
            prof.window = why
            prof.notes.append(f"needs a window: {why} ({str(e)[:300]})")
            prof.data["needs_window"] = why
            prof.data["type"] = "needs a window"
        else:
            prof.problem(f"doesn't load: {e}")
            prof.data["type"] = "unknown"
        return prof
    prof.data["load_seconds"] = round(time.time() - t, 2)
    d = c.call("machine_describe", machine="M", value_table=True, table_points=1025)
    info = d.get("gear_info") or {}
    prof.data["gear_info"] = info
    globals_, tracks = params_by_kind(d)
    inferred = {}
    for p in globals_ + tracks:
        e = mi.infer(p)
        inferred[(p.get("per_track", False), p["name"])] = e
    prof.data["parameters"] = len(globals_)
    prof.data["track_parameters"] = len(tracks)
    prof.data["inferred"] = sum(1 for e in inferred.values() if e.get("kind"))
    prof.data["from_description"] = sum(1 for e in inferred.values() if e.get("confidence") == "description")
    if generated:
        byname = {p["name"]: p for p in globals_}
        agree = [mi.agrees(inferred[(False, mp["name"])], mp, byname[mp["name"]]["table"]) for mp in generated["parameters"] if mp["name"] in byname]
        prof.data["inference_check"] = {"agree": sum(agree), "of": len(agree)}
    if info.get("control") or prof.data.get("no_output"):
        prof.data["type"] = "control"
        prof.notes.append("a control machine (moves other machines' parameters): no audio test")
    elif kind == "generator":
        profile_generator(c, prof, info, baseline, generated)
        # U2 in depth: a generator no note made sound (deaf to MIDI, no Note parameter, or one that no key played) may play
        # from a trigger: a drum machine's hit columns.
        if (prof.data.get("notes") or {}).get("input") == "none" and DEPTH["triggers"]:
            trig = find_trigger(c, prof, globals_)
            if trig:
                prof.data["trigger"] = {k: trig[k] for k in ("param", "track", "value")}
                if len(trig.get("all", [])) > 1:
                    prof.data["trigger"]["others"] = [x["param"] for x in trig["all"][1:]]
                    prof.data["_triggers"] = trig["all"]
                prof.warnings = [w for w in prof.warnings if "no sound from a" not in w]
                prof.notes.append(f"plays from a trigger, not notes: {trig['param']}" + (" (track 0)" if trig.get("track") is not None else "") +
                                  f" written as an event (automation_write, raw {trig['value']}); tried: {'; '.join(trig.get('tried', []))}")
                prof.data["type"] = "triggered"
                play(c, "M", None, prof.data["trigger"], [note(A4, 0, 1)], length=8)
                m = render(c, 0, 4, 1.0)["measure"]
                forget_renders(c)
                prof.data["level"] = {"note": "one trigger", "integrated_lufs": loudness(m), "sample_peak_dbfs": finite_peak(m),
                                      "true_peak_dbtp": (m.get("peaks") or {}).get("true_peak_dbtp")}
    else:
        profile_effect(c, prof, info, baseline)
        type_from_parameters(prof, globals_)
    if prof.data["type"] != "control" and not prof.diverging:
        level_tests(c, prof, kind, globals_, tracks)
    elif prof.diverging and prof.data["type"] != "control":
        # Diverging at its defaults: again in a second fresh instance, or does it vary (uninitialised state)?
        try:
            again, facts = Bench(c, prof, kind, (prof.data.get("notes") or {}).get("input")).level()
        except ToolFailed as e:
            again, facts = "error", {"error": str(e)[:200]}
        prof.data["second_instance"] = dict(facts, verdict=again)
        prof.problems[-1] += (" (a second fresh instance too: " + describe_level(again, facts) + ")" if again == "diverging"
                              else f" (not a second fresh instance, {again} in the level test: it varies from instance to instance)")
    # U2 in depth (mcp-server.md §12.12): per-parameter sweeps (suggested roles), a generator's velocity and envelope,
    # denormal slowdowns. Neo machines keep their generated manifests: their sweeps only with --roles-generated.
    depth_ranges = prof.data.get("safe_ranges", {})
    if prof.data["type"] != "control" and not prof.diverging and DEPTH["on"]:
        roles = DEPTH["roles"] and (not generated or DEPTH["roles_generated"])
        try:
            if kind == "effect" and not (prof.data.get("effect") or {}).get("silent"):
                if roles:
                    effect_role_sweeps(c, prof, globals_, tracks, inferred, depth_ranges)
            elif kind == "generator":
                generator_depth(c, prof, globals_, tracks, inferred, depth_ranges, roles=roles)
            if DEPTH["denormal"]:
                dn = denormal_test(c, prof, kind)
                if dn:
                    prof.data["denormal"] = dn
                    if dn.get("slowdown"):
                        prof.warn(f"slows down after its input goes silent (denormal numbers, likely): up to {dn['silent_worst_ms_per_s']} ms of work "
                                  f"per second of audio, for about {dn.get('slow_seconds')} s of the 20 s after, against {dn['playing_ms_per_s']} while "
                                  f"it plays ({dn['slowdown']}× for 4 s or more)")
        except ToolFailed as e:
            prof.notes.append(f"the depth passes stopped: {e}")
    # The summary and the draft keep each parameter's role, its evidence and the ends measured; the features at each
    # end (a few dozen numbers a parameter) go to <out>/roles-detail/ for a person checking a suggestion.
    if prof.data.get("roles") and DEPTH.get("detail_dir"):
        try:
            write_json(os.path.join(DEPTH["detail_dir"], gear + ".json"), prof.data["roles"])
        except OSError:
            pass
    for r in ((prof.data.get("roles") or {}).get("params") or {}).values():
        for end in ("min", "max"):
            if isinstance(r.get(end), dict):
                r[end] = {k: r[end][k] for k in ("raw", "text", "diverged", "error") if k in r[end]}
    prof.data.pop("_triggers", None)
    suggested = {}
    keys = {role_key(p, globals_): p for p in globals_ + tracks}
    for key, r in ((prof.data.get("roles") or {}).get("params") or {}).items():
        p = keys.get(key)
        if r.get("suggested_role") and p is not None:
            suggested[id(p)] = (r["suggested_role"], r.get("evidence", ""))
    ranges = prof.data.pop("safe_ranges", {})
    prof.data["entries"] = {group: [manifest_entry(p, inferred[(per_track, p["name"])], ranges.get((per_track, p["name"])),
                                                   suggested.get(id(p)))
                                    for p in ps if inferred[(per_track, p["name"])].get("kind") or (per_track, p["name"]) in ranges
                                    or id(p) in suggested]
                            for group, ps, per_track in (("global", globals_, False), ("track", tracks, True))}
    if ranges:
        prof.data["safe_ranges"] = {("track " if pt else "") + n: r for (pt, n), r in ranges.items()}
    prof.data["seconds"] = round(time.time() - started, 1)
    return prof


# ---- outputs ---------------------------------------------------------------------------------------

def health_of(entry):
    """needs_window, unhealthy (diverging, crashes, load failures), warn (hot, other warnings) or ok."""
    return mi.health_status(entry.get("problems", []), entry.get("warnings", []) + entry.get("hot", []), bool(entry.get("needs_window")))


def category(entry):
    """The report's category (machine-safety.md §3.1): diverging, hot, needs a window, unhealthy (other), warn or ok."""
    if entry.get("needs_window"):
        return "needs a window"
    if entry.get("diverging"):
        return "diverging"
    if entry.get("problems"):
        return "unhealthy"
    if entry.get("hot"):
        return "hot"
    return "warn" if entry.get("warnings") else "ok"


def summary_line(e):
    t = e.get("type", "")
    k = e.get("kind")
    if k == "generator" and e.get("notes"):
        n = e["notes"]
        if n.get("input") == "none" and e.get("trigger"):
            tr = e["trigger"]
            return (f"Generator (profiled draft): plays from a trigger, not notes: its {tr['param']} column" +
                    (" (a track parameter)" if tr.get("track") is not None else "") + ", written with automation_write.")
        if n.get("input") == "none":
            return f"{t.capitalize()} (profiled draft): no sound from a note at its defaults."
        bits = []
        if n.get("key"):
            bits.append(f"a drum kit: silent on A4, sounds on {Pitch(n['key'])}")
        elif n.get("pitched"):
            off = n.get("pitch_offset", 0)
            bits.append("pitched" + (f", plays {abs(off)} semitones {'low' if off > 0 else 'high'} (corrected by its pitch offset)" if off else ", plays A4 at 440 Hz as written"))
        else:
            bits.append("unpitched")
        p = n.get("polyphony", None if n.get("key") else 1)
        if p is not None:
            bits.append("monophonic" if p == 1 else f"{p}+ voices" if p >= 8 else f"{p} voices")
        bits.append("takes notes in its tracks" if n.get("input") == "tracks" else "takes MIDI notes")
        lvl = (e.get("level") or {}).get("integrated_lufs")
        if isinstance(lvl, (int, float)):
            bits.append(f"one note at the defaults {lvl:.0f} LUFS")
        return f"{t.capitalize()} (profiled draft): " + "; ".join(bits) + "."
    if k == "effect" and e.get("effect"):
        fx = e["effect"]
        if fx.get("silent"):
            return f"{t.capitalize()} (profiled draft): silent at its defaults."
        bits = [f"traits: {', '.join(fx.get('traits', []))}"]
        if fx.get("tail_seconds") is not None:
            bits.append(f"tail {fx['tail_seconds']} s")
        if fx.get("latency_samples") is not None:
            bits.append(f"latency {fx['latency_samples']} samples")
        if fx.get("gain_db") is not None:
            bits.append(f"gain {fx['gain_db']:+.1f} dB at the defaults")
        return f"{t.capitalize()} (profiled draft): " + "; ".join(bits) + "."
    if t == "control":
        return "Control machine (profiled draft): moves other machines' parameters; makes no sound."
    if e.get("needs_window"):
        return f"Needs a window (profiled draft): {e['needs_window']}. Works in NeoBuzz Live on Windows with its window; it can't be built headless."
    return f"{t.capitalize() or 'Machine'} (profiled draft)."


def draft_manifest(e):
    info = e.get("gear_info") or {}
    m = {
        "format": FORMAT,
        "gear": e["gear"],
        "kind": e["kind"],
        "status": "draft",
        "source": f"profiled by tools/machine-profiler.py on {e.get('platform', '?')}, {e.get('date', '?')}; not curated",
        "type": e.get("type", ""),
        "summary": summary_line(e),
        "tracks": info.get("max_tracks", 0),
    }
    if e["kind"] == "generator" and e.get("notes"):
        n = e["notes"]
        m["notes"] = {k: n[k] for k in ("input", "pitch_offset", "pitched", "polyphony") if k in n}
    m["health"] = {"status": health_of(e), "problems": e.get("problems", []) + e.get("hot", []) + e.get("warnings", [])}
    if e.get("diverging"):
        m["health"]["diverging"] = True
    if e.get("hot"):
        m["health"]["hot"] = True
    if e.get("needs_window"):
        m["health"]["needs_window"] = e["needs_window"]
    # Timings (CPU, load time) vary run to run: they stay in the summary and the report, not the draft.
    m["profile"] = {k: e[k] for k in ("level", "effect", "level_test", "sweeps", "trigger", "envelope", "velocity", "roles", "denormal") if k in e}
    if m["profile"].get("denormal"):
        # timings vary run to run: the draft keeps whether it slows down, the summary the numbers
        m["profile"]["denormal"] = {k: v for k, v in m["profile"]["denormal"].items() if k == "slowdown"} or {"slowdown": None}
    m["parameters"] = (e.get("entries") or {}).get("global", [])
    m["track_parameters"] = (e.get("entries") or {}).get("track", [])
    return checked_draft(m)


def checked_draft(m):
    """A machine whose own default is outside its range (a native's DefValue): the draft takes the nearest
    end, and its health says so. Applied when a draft is written and when one is merged."""
    for p in m.get("parameters", []) + m.get("track_parameters", []):
        lo, hi, d = p.get("min", 0), p.get("max", 0), p.get("default", 0)
        if not lo <= d <= hi:
            p["default"] = min(max(d, lo), hi)
            note = f"{p['name']}: its own default (raw {d}) is outside its range ({lo}…{hi})"
            health = m.setdefault("health", {"status": "ok", "problems": []})
            if note not in health["problems"]:
                health["problems"].append(note)
            if health.get("status") == "ok":
                health["status"] = "warn"
    return machine_catalog.apply(m)


def subfolder(e):
    f = (e.get("gear_info") or {}).get("file", "")
    sub = f.split("/")[0] if "/" in f else ""
    return sub if sub in ("Generators", "Effects") else ("Generators" if e["kind"] == "generator" else "Effects")


def write_json(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=1, ensure_ascii=False)
        f.write("\n")


def fmt_s(t):
    return "" if not isinstance(t, (int, float)) else f"{t * 1000:.0f} ms" if t < 1 else f"{t:.2f} s"


def health_report(summary, curated=None, sourced=None, rebuilt=None):
    """The report. curated: {gear: {"name" or "track name": role}} from the curated manifests, for the roles' table;
    sourced: the gear with manifests generated from their source (U6), which have no draft's suggestions; rebuilt: the
    natives the package builds from source in place of upstream's binaries (U7)."""
    curated = curated or {}
    sourced = sourced or set()
    rebuilt = rebuilt or []
    machines = summary["machines"]
    rows = sorted(machines.values(), key=lambda e: (e.get("kind", ""), e["gear"].lower()))
    by_platform = {}
    for e in rows:
        by_platform[e.get("platform", "?")] = by_platform.get(e.get("platform", "?"), 0) + 1
    groups = {k: [e for e in rows if category(e) == k] for k in ("diverging", "hot", "needs a window", "unhealthy", "warn", "ok")}
    crashes = [e for e in rows if any("crash" in p or "hung" in p for p in e.get("problems", []))]
    swept = [e for e in rows if (e.get("level_test") or {}).get("swept_because")]
    ranges = sum(len(e.get("safe_ranges") or {}) for e in rows)
    gens = [e for e in rows if e.get("kind") == "generator" and e.get("notes")]
    offsets = {}
    for e in gens:
        n = e["notes"]
        key = "no sound from a note" if n.get("input") == "none" else ("unpitched" if not n.get("pitched") else f"offset {n.get('pitch_offset', 0):+d}")
        offsets[key] = offsets.get(key, 0) + 1
    lines = [
        "# Machine health",
        "",
        "Generated by `tools/machine-profiler.py` (docs/specs/mcp-server.md §12.2, docs/specs/machine-safety.md §3.1) from "
        "`Machines/Manifests/machine-profile.json`. Do not edit by hand: run the profiler, then `--report`.",
        "",
        f"{len(rows)} machines profiled ({', '.join(f'{n} on {p}' for p, n in sorted(by_platform.items()))}). "
        f"{len(groups['diverging'])} diverging, {len(groups['hot'])} hot, {len(groups['needs a window'])} needing a window, "
        f"{len(groups['unhealthy'])} with other problems, {len(groups['warn'])} with other warnings, {len(groups['ok'])} clean. "
        f"Crashes or hangs: {len(crashes)}. Swept parameter by parameter: {len(swept)} machines; {ranges} safe ranges. "
        f"Slow down after their input goes silent: {sum(1 for e in rows if (e.get('denormal') or {}).get('slowdown'))}. "
        f"Suggested roles: {sum(1 for e in rows for r in ((e.get('roles') or {}).get('params') or {}).values() if r.get('suggested_role'))} "
        f"parameters on {sum(1 for e in rows if any(r.get('suggested_role') for r in ((e.get('roles') or {}).get('params') or {}).values()))} machines.",
        "",
        "How it's measured: a level test renders 4 s of the machine alone, fed noise at a mix's level (effects, peaks near −9 dBFS) or "
        "holding A4 (generators; a drum kit silent on A4 hit on the key that sounds, every second): at its defaults, with every parameter at its minimum and at its maximum at once, and, for machines "
        "that misbehave that way and effects with a feedback, resonance, gain or Q parameter, each parameter alone at each end. A level "
        "that grew over 4 s is rendered for 30 s: an attack settles, a runaway keeps growing (its last 10 s more than 6 dB over the 10 s before). "
        "A loop's or a resonance's hot end that rings more than 24 dB over its level at the default gets a safe range by bisection too.",
        "",
        "In depth (mcp-server.md §12.12): a test signal for effects, played by Neo Poly and read on both sides: a click with time to ring out "
        "(latency, decay, echoes), a stepped sine sweep (the response) and, for dynamics, level steps of a three-sine chord (the curve, attack "
        "and release); each parameter alone at its ends through it, or a held note on generators, names a suggested role. Generators: velocity "
        "response, envelope length, a harmonic sieve where YIN hears no pitch, a drum kit's keys (its manifest's note map, else C2 and D2) and triggers where no note sounds. Every machine: its own work "
        "time per second of audio over the 20 s after its input stops, against while it plays.",
        "",
        "Pitch conventions of the generators (the offset `notes_write` applies so the written pitch plays): " +
        (", ".join(f"{k}: {v}" for k, v in sorted(offsets.items())) or "none profiled") + ".",
        "",
    ]
    if rebuilt:
        names = sorted(n if isinstance(n, str) else n["gear"] for n in rebuilt)
        fixed = sorted(n["gear"] for n in rebuilt if not isinstance(n, str) and n.get("fix"))
        excepted = sorted(n["gear"] for n in rebuilt if not isinstance(n, str) and n.get("licenseException"))
        lines += [
            f"Built from source (mcp-server.md §12.14, U7): {', '.join(names)} ship rebuilt in place of upstream's binaries, "
            "which they render as (`tools/native-compare.py`"
            + (f"; {', '.join(fixed)} as upstream's binary on a zeroed heap, the start their fix gives" if fixed else "") + "), "
            "and are profiled as rebuilt"
            + (f"; {', '.join(excepted)} are x64 builds of upstream's 32-bit binaries, from sources without a licence "
               "(shipped by user decision 2026-11-06 for private use)" if excepted else "")
            + "; the other natives are upstream's binaries.",
            "",
        ]
    lines += [
        "## Diverging",
        "",
        "The output runs away: NaN or infinity, above +60 dBFS, or still growing after 20 s. Unhealthy: `gear_list` lists them last, "
        "`machine_add` warns, and where a single parameter's end is the cause, its draft has a `safe_range` that `param_set` keeps agents inside "
        "(unless they pass `allow_unsafe`).",
        "",
    ]
    if groups["diverging"]:
        lines += ["| Machine | Platform | Where | Safe ranges |", "|---|---|---|---|"]
        for e in groups["diverging"]:
            sr = "; ".join(f"{n}: {r.get('min_text', r['min'])} … {r.get('max_text', r['max'])} (raw {r['min']}–{r['max']})" for n, r in (e.get("safe_ranges") or {}).items())
            lines.append(f"| {e['gear']} | {e.get('platform', '')} | {'; '.join(e['problems'])} | {sr or 'none: it diverges at its defaults'} |")
    else:
        lines.append("None.")
    lines += ["", "## Hot", "",
              "Loud but steady: above 0 dBFS, under +60 dBFS, not growing, at the setting named. A warning, not unhealthy; the master's "
              "safety limiter keeps it off the speakers (machine-safety.md §3.3). A parameter's end where the loop never decays (feedback "
              "at ±100 %: noise piles up 2 dB or more per doubling of time, for 30 s) gets a safe range too, which keeps agents off it.", ""]
    if groups["hot"]:
        lines += ["| Machine | Platform | Settings | Safe ranges |", "|---|---|---|---|"]
        for e in groups["hot"]:
            sr = "; ".join(f"{n}: {r.get('min_text', r['min'])} … {r.get('max_text', r['max'])} (raw {r['min']}–{r['max']})" for n, r in (e.get("safe_ranges") or {}).items())
            lines.append(f"| {e['gear']} | {e.get('platform', '')} | {'; '.join(e.get('hot', []))} | {sr} |")
    else:
        lines.append("None.")
    lines += ["", "## Needs a window", "",
              "Managed machines whose construction fails without a UI: not an audio fault. `gear_list` says they work in NeoBuzz Live on Windows with their window.", ""]
    if groups["needs a window"]:
        lines += ["| Machine | Platform | Why |", "|---|---|---|"]
        lines += [f"| {e['gear']} | {e.get('platform', '')} | {e['needs_window']} |" for e in groups["needs a window"]]
    else:
        lines.append("None.")
    if groups["unhealthy"]:
        lines += ["", "## Other problems", "", "Machines that don't load, or crash or hang the server: unhealthy, listed last by `gear_list`.", "",
                  "| Machine | Platform | Problems |", "|---|---|---|"]
        lines += [f"| {e['gear']} | {e.get('platform', '')} | {'; '.join(e['problems'])} |" for e in groups["unhealthy"]]
    lines += ["", "## Warnings", "", "Everything else the profile noticed (most are the profiler's own limits: triggers, audio inputs, a host without its plugin).", ""]
    warned = [e for e in rows if e.get("warnings") and category(e) not in ("needs a window",)]
    if warned:
        lines += ["| Machine | Platform | Warnings |", "|---|---|---|"]
        lines += [f"| {e['gear']} | {e.get('platform', '')} | {'; '.join(e['warnings'])} |" for e in warned]
    else:
        lines.append("None.")
    slow = [e for e in rows if (e.get("denormal") or {}).get("slowdown")]
    lines += ["", "## Slow after silence", "",
              "Machines whose own work time per second of audio, for 4 s or more of the 20 s after their input stops, is more than twice what "
              "it is while the input plays (and 1 ms/s more): denormal numbers in a decaying state, most likely. A warning: the engine's "
              "time, not the sound.", ""]
    if slow:
        lines += ["| Machine | Platform | Playing ms/s | After silence ms/s (worst) | For | × |", "|---|---|---|---|---|---|"]
        for e in slow:
            d = e["denormal"]
            lines.append(f"| {e['gear']} | {e.get('platform', '')} | {d.get('playing_ms_per_s')} | {d.get('silent_worst_ms_per_s')} | "
                         f"{d.get('slow_seconds', '')} s | {d.get('slowdown')} |")
    else:
        lines.append("None.")
    # Suggested roles: the filter, time and mix ones against the curated roles, then every draft's.
    lines += ["", "## Suggested roles", "",
              "What moving each parameter alone changes, as a role (`suggested_role` in the drafts, `machine_describe` shows it). Curated "
              "manifests keep their own roles: for their filter, time and mix parameters, the profile's suggestion is listed beside the "
              "curated role, and a disagreement is a note here, never a change to the manifest.", ""]
    agree = total = 0
    rows_c = []
    for e in rows:
        cur = curated.get(e["gear"])
        params = (e.get("roles") or {}).get("params") or {}
        if not cur:
            continue
        for name, role in cur.items():
            cat = mr.category(role)
            if not cat:
                continue
            r = params.get(name) or {}
            sug = r.get("suggested_role")
            ok_ = mr.agrees(role, sug)
            agree += ok_
            total += 1
            rows_c.append(f"| {e['gear']} | {name} | {cat} | `{role}` | {('`' + sug + '`') if sug else '—'} | {'yes' if ok_ else 'no'} | "
                          f"{(r.get('evidence') or 'not swept on this platform').replace('|', '/')} |")
    if rows_c:
        lines += [f"Curated filter, time and mix parameters: {agree} of {total} suggestions agree.", "",
                  "| Machine | Parameter | Kind | Curated | Suggested | Agrees | Evidence |", "|---|---|---|---|---|---|---|"] + rows_c
    drafts = [e for e in rows if not curated.get(e["gear"]) and not e.get("generated") and e["gear"] not in sourced and any(r.get("suggested_role") for r in ((e.get("roles") or {}).get("params") or {}).values())]
    if drafts:
        lines += ["", "Drafts:", "", "| Machine | Suggested roles |", "|---|---|"]
        for e in drafts:
            ps = (e.get("roles") or {}).get("params") or {}
            lines.append(f"| {e['gear']} | " + "; ".join(f"{n}: `{r['suggested_role']}`" for n, r in ps.items() if r.get("suggested_role")) + " |")
    lines += ["", "## Generators", "", "| Machine | Type | Notes in | Pitch offset | Voices | Level (LUFS, one note) | Velocity (dB per doubling) | "
              "Attack / release / length | CPU ms/s | After silence ms/s | Health |", "|---|---|---|---|---|---|---|---|---|---|---|"]
    for e in rows:
        if e.get("kind") != "generator":
            continue
        n = e.get("notes") or {}
        off = "" if not n.get("pitched") else f"{n.get('pitch_offset', 0):+d}" + (f" ({n.get('cents'):+.0f} ct)" if abs(n.get("cents") or 0) >= 1 else "")
        lvl = (e.get("level") or {}).get("integrated_lufs")
        vel = (e.get("velocity") or {}).get("db_per_doubling")
        env = e.get("envelope") or {}
        envs = " / ".join(fmt_s(env.get(k)) or "—" for k in ("attack_s", "release_s", "length_s")) if env else ""
        if env.get("release_past_window"):
            envs = envs.replace(fmt_s(env.get("release_s")), "> " + fmt_s(env.get("release_s")), 1)
        inn = n.get("input", "") + (f" (trigger: {e['trigger']['param']})" if e.get("trigger") else "") + (f" on {Pitch(n['key'])}" if n.get("key") else "")
        if n.get("pitch_method"):
            off += " (sieve)"
        lines.append(f"| {e['gear']} | {e.get('type', '')} | {inn} | {off} | {n.get('polyphony', '')} | "
                     f"{'' if lvl is None else lvl} | {'' if vel is None else vel} | {envs} | {e.get('cpu_ms_per_s', '')} | "
                     f"{(e.get('denormal') or {}).get('silent_ms_per_s', '')} | {category(e)} |")
    lines += ["", "## Effects", "", "| Machine | Type (traits) | Gain dB | Latency (samples) | Tail s | T60 (click) | First echo | Response −3 dB band | "
              "CPU ms/s | After silence ms/s | Health |", "|---|---|---|---|---|---|---|---|---|---|---|"]
    for e in rows:
        if e.get("kind") != "effect":
            continue
        fx = e.get("effect") or {}
        imp, rs = fx.get("impulse") or {}, fx.get("response_summary") or {}
        echo = (imp.get("echoes") or [[None]])[0][0]
        band = f"{rs['low_corner_hz']:.0f} Hz – {rs['high_corner_hz'] / 1000:.1f} kHz" if rs.get("low_corner_hz") and rs.get("high_corner_hz") else ""
        lines.append(f"| {e['gear']} | {', '.join(fx.get('traits', [])) or e.get('type', '')} | {fx.get('gain_db', '')} | {fx.get('latency_samples', '')} | "
                     f"{fx.get('tail_seconds', '')} | {fmt_s(imp.get('t60_s'))} | {fmt_s(echo)} | {band} | {e.get('cpu_ms_per_s', '')} | "
                     f"{(e.get('denormal') or {}).get('silent_ms_per_s', '')} | {category(e)} |")
    lines += ["", "## Not measured yet", "",
              "Whether `Work` reports silence (the engine keeps what it returns only for the activity LED, and a managed effect's false "
              "means \"pass my input\", not \"I'm silent\": mcp-server.md §12.13); a wave or a plugin to choose for "
              "Jeskola Tracker and Polac VSTi (a person's choice, not a measurement); LFO rates (a modulation's rate shows only over seconds of "
              "steady tone); what a parameter does only with another one set (a slot's effect, an envelope routed elsewhere) beyond the "
              "contexts the sweep tries: mcp-server.md §12.12.", ""]
    return "\n".join(lines)


# ---- main ------------------------------------------------------------------------------------------

def matches(name, patterns):
    return any(fnmatch.fnmatch(name.lower(), p.strip().lower()) for p in patterns if p.strip())


def generated_manifests(root):
    out = {}
    for base, _, files in os.walk(os.path.join(root, "Machines")):
        if "Manifests" in base.split(os.sep) or "bin" in base.split(os.sep) or "obj" in base.split(os.sep):
            continue
        for f in files:
            if f.endswith(".machine.json"):
                try:
                    with open(os.path.join(base, f), encoding="utf-8-sig") as h:
                        m = json.load(h)
                    if m.get("status") != "draft":
                        out[m["gear"]] = m
                except (OSError, ValueError):
                    pass
    return out


def merge_into_repo(root, summary, out_dir):
    folder = os.path.join(root, "Machines", "Manifests")
    path = os.path.join(folder, "machine-profile.json")
    merged = {"format": "rebuzz-machine-profile/1", "machines": {}}
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            merged = json.load(f)
    merged["machines"].update(summary["machines"])
    merged["machines"] = dict(sorted(merged["machines"].items(), key=lambda kv: kv[0].lower()))
    write_json(path, merged)
    for e in summary["machines"].values():
        if e.get("generated"):
            continue
        src = os.path.join(out_dir, "manifests", subfolder(e), e["gear"] + ".machine.json")
        if os.path.exists(src):
            with open(src, encoding="utf-8") as f:
                draft = checked_draft(json.load(f))
            dst = os.path.join(folder, subfolder(e), e["gear"] + ".machine.json")
            # U3 (mcp-server.md §12.9): a curated manifest keeps its curation and takes the new measurements;
            # U6 (§12.13): so does one generated from the machine's source (tools/source_manifests.py).
            existing = None
            if os.path.exists(dst):
                with open(dst, encoding="utf-8") as f:
                    existing = json.load(f)
            if existing and existing.get("status") in ("curated", "generated"):
                draft, warnings = mi.recurate(existing, draft)
                machine_catalog.apply(draft)
                for w in warnings:
                    print(f"  {existing['status']} {e['gear']}: {w}", flush=True)
            write_json(dst, draft)
    write_report(root, merged)


def from_source(root):
    """U6 (mcp-server.md §12.13): the gear whose manifests in ROOT/Machines/Manifests are generated from their source:
    not drafts, so the report lists no suggested roles for them."""
    out = set()
    for sub in ("Effects", "Generators"):
        folder = os.path.join(root, "Machines", "Manifests", sub)
        for f in sorted(os.listdir(folder)) if os.path.isdir(folder) else []:
            if f.endswith(".machine.json"):
                try:
                    with open(os.path.join(folder, f), encoding="utf-8-sig") as h:
                        if json.load(h).get("status") == "generated":
                            out.add(f[:-len(".machine.json")])
                except (OSError, ValueError):
                    pass
    return out


def curated_roles(root):
    """{gear: {"name" (or "track name"): role}} from the curated manifests in ROOT/Machines/Manifests."""
    out = {}
    for sub in ("Effects", "Generators"):
        folder = os.path.join(root, "Machines", "Manifests", sub)
        if not os.path.isdir(folder):
            continue
        for f in sorted(os.listdir(folder)):
            if not f.endswith(".machine.json"):
                continue
            try:
                with open(os.path.join(folder, f), encoding="utf-8-sig") as h:
                    m = json.load(h)
            except (OSError, ValueError):
                continue
            if m.get("status") != "curated":
                continue
            roles = {}
            for group, prefix in (("parameters", ""), ("track_parameters", "track ")):
                ps = m.get(group) or []
                for p in ps:
                    if p.get("role"):
                        repeated = not prefix and sum(1 for q in ps if q["name"] == p["name"]) > 1
                        roles[prefix + p["name"] + (f" #{p.get('index')}" if repeated else "")] = p["role"]
            out[m["gear"]] = roles
    return out


def write_report(root, merged):
    with open(os.path.join(root, "docs", "machine-health.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(health_report(merged, curated_roles(root), from_source(root), rebuilt_natives(root)))


# build.ps1's one exception to the licence gate (its $NativeLicenceException; tools/test_native_compare.py keeps the two the same).
NATIVE_LICENCE_EXCEPTION = "no licence in source; shipped by user decision 2026-11-06 for private use"


def rebuilt_natives(root):
    """U7 (mcp-server.md §12.14): the natives ExternalMachines/machines.json ships rebuilt from source (their entries): those
    whose licence allows it, and those whose source has none, shipped by build.ps1's one recorded exception."""
    try:
        with open(os.path.join(root, "ExternalMachines", "machines.json"), encoding="utf-8") as f:
            bt = next((m for m in json.load(f)["machines"] if m.get("id") == "buzztrax-buzzmachines"), {})
    except (OSError, ValueError):
        return []
    return [n for n in bt.get("natives", []) if n.get("ship") == "rebuilt"
            and (n.get("licenseException") == NATIVE_LICENCE_EXCEPTION if "licenseException" in n else n.get("redistribute", True))]


PACK = "MACHINE-PROFILE-PACK"


def pack(folder):
    """The drafts, summary and report of an --out folder (and the role sweeps' details), gzipped, as base64 lines in
    the log: a CI job's log can be read where its artifacts can't (--unpack takes them back out)."""
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w:gz") as tar:
        for base, _, files in os.walk(folder):
            for f in files:
                path = os.path.join(base, f)
                rel = os.path.relpath(path, folder).replace(os.sep, "/")
                if rel.startswith("manifests/") or rel.startswith("roles-detail/") or rel in ("machine-profile.json", "machine-health.md"):
                    tar.add(path, arcname=rel)
    data = base64.b64encode(buf.getvalue()).decode("ascii")
    print(f"{PACK}-BEGIN {len(data)}")
    for i in range(0, len(data), 3000):
        print(f"{PACK} {data[i:i + 3000]}")
    print(f"{PACK}-END")


def unpack(log, folder):
    chunks = []
    with open(log, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = re.search(PACK + r" ([A-Za-z0-9+/=]+)\s*$", line)
            if m:
                chunks.append(m.group(1))
    if not chunks:
        raise SystemExit(f"no {PACK} lines in {log}")
    os.makedirs(folder, exist_ok=True)
    with tarfile.open(fileobj=io.BytesIO(base64.b64decode("".join(chunks))), mode="r:gz") as tar:
        for member in tar.getmembers():
            if member.name.startswith("/") or ".." in member.name.split("/") or not member.isfile():
                continue
            try:
                tar.extract(member, folder, filter="data")
            except TypeError:   # Python before 3.12
                tar.extract(member, folder)
    print(f"unpacked {len(chunks)} lines into {folder}")


def main():
    ap = argparse.ArgumentParser(description="Profile every machine an MCP server offers (docs/specs/mcp-server.md §12.2).")
    ap.add_argument("exe", nargs="?", help="rebuzz-headless or NeoBuzzLive.exe")
    ap.add_argument("--out", default=None, help="where drafts, the summary and the report go (default: a temp folder)")
    ap.add_argument("--only", default="", help="comma-separated gear names or patterns")
    ap.add_argument("--skip", default="", help="comma-separated gear names or patterns to leave out")
    ap.add_argument("--repo", default=None, help="merge the results into this repository checkout")
    ap.add_argument("--report", default=None, help="only rewrite ROOT/docs/machine-health.md from ROOT's summary")
    ap.add_argument("--merge", default=None, help="merge an earlier run's --out folder (a CI artifact) into --repo")
    ap.add_argument("--pack", default=None, help="print an --out folder into the log (base64 lines)")
    ap.add_argument("--unpack", default=None, help="a job log with packed lines: unpack them into --out")
    ap.add_argument("--timeout", type=float, default=90, help="seconds a call may take before the machine counts as hung")
    ap.add_argument("--health-only", action="store_true", help="U1 and U2's health only: no test signal sweeps, roles, velocity, envelope, denormals or triggers")
    ap.add_argument("--no-roles", action="store_true", help="leave out the per-parameter sweeps (suggested roles)")
    ap.add_argument("--roles-generated", action="store_true", help="also sweep the machines with generated manifests (Neo): a check of the role inference")
    ap.add_argument("--no-denormal", action="store_true", help="leave out the denormal test (20 s of silence per machine)")
    args = ap.parse_args()
    DEPTH.update({"on": not args.health_only, "roles": not args.no_roles, "roles_generated": args.roles_generated,
                  "denormal": not args.no_denormal, "triggers": not args.health_only,
                  "detail_dir": os.path.join(os.path.abspath(args.out or "."), "roles-detail") if args.out else None})
    root_default = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    if args.report:
        with open(os.path.join(args.report, "Machines", "Manifests", "machine-profile.json"), encoding="utf-8") as f:
            write_report(args.report, json.load(f))
        print("wrote docs/machine-health.md")
        return 0
    if args.pack:
        pack(args.pack)
        return 0
    if args.unpack:
        if not args.out:
            ap.error("--unpack needs --out")
        unpack(args.unpack, args.out)
        return 0
    if args.merge:
        if not args.repo:
            ap.error("--merge needs --repo")
        with open(os.path.join(args.merge, "machine-profile.json"), encoding="utf-8") as f:
            merge_into_repo(os.path.abspath(args.repo), json.load(f), os.path.abspath(args.merge))
        print(f"merged {args.merge} into {args.repo}")
        return 0
    if not args.exe:
        ap.error("give the server to profile with")
    out = os.path.abspath(args.out or tempfile.mkdtemp(prefix="machine-profile-"))
    os.makedirs(out, exist_ok=True)
    plat = "windows" if windows_app(args.exe) else sys.platform.replace("darwin", "macos")
    generated = generated_manifests(args.repo or root_default)
    c = Client(args.exe, os.path.join(out, "work"), args.timeout)
    gear = c.call("gear_list", limit=1000, include_unhealthy=True)["gear"]
    baseline = baselines(c)
    only = [p for p in args.only.split(",") if p.strip()]
    skip = [p for p in args.skip.split(",") if p.strip()]
    chosen = [g for g in gear if (not only or matches(g["name"], only)) and not matches(g["name"], skip)]
    print(f"profiling {len(chosen)} of {len(gear)} machines with {args.exe} ({plat}, server {c.server_version}) into {out}", flush=True)
    today = datetime.date.today().isoformat()
    summary = {"format": "rebuzz-machine-profile/1", "platform": plat, "date": today, "machines": {}}
    started = time.time()
    for i, g in enumerate(chosen):
        name, kind = g["name"], g["kind"]
        t = time.time()
        try:
            prof = profile_machine(c, name, kind, baseline, generated.get(name))
        except ServerGone as e:
            prof = Profile(name, kind)
            prof.problem(f"crashed or hung the server ({e}); stderr: {c.stderr_tail(300).strip()[-200:]}")
            prof.data["type"] = "unknown"
            c.restart()
        except ToolFailed as e:
            prof = Profile(name, kind)
            prof.problem(f"profiling stopped: {e}")
            prof.data["type"] = "unknown"
        # A native machine that crashes takes its host process down, not the server: ReBuzz marks it crashed.
        try:
            row = next((x for x in c.call("gear_list", search=name, limit=50, include_unhealthy=True)["gear"] if x["name"] == name), {})
            if row.get("health") in ("crashed", "missing"):
                prof.problem(f"{row['health']} while being profiled (ReBuzz marked it)")
        except (ToolFailed, ServerGone):
            pass
        except Exception as e:   # a profiler bug shouldn't lose the other machines
            prof = Profile(name, kind)
            prof.notes.append(f"profiler error: {type(e).__name__}: {e}")
            prof.warn("not fully profiled (a profiler error)")
            prof.data.setdefault("type", "unknown")
        e = prof.data
        e.update({"platform": plat, "date": today, "problems": prof.problems, "warnings": prof.warnings, "hot": prof.hot, "notes_profiler": prof.notes,
                  "generated": name in generated})
        if prof.diverging:
            e["diverging"] = True
        if prof.window:
            e["needs_window"] = prof.window
        e["health"] = health_of(e)
        e.setdefault("type", "unknown")
        if name not in generated and ("entries" in e or prof.problems or prof.window):
            write_json(os.path.join(out, "manifests", subfolder(e), name + ".machine.json"), draft_manifest(e))
        e.pop("entries", None)   # in the draft; the summary keeps the measurements
        summary["machines"][name] = e
        forget_renders(c)
        flag = {"diverging": "DIV ", "unhealthy": "BAD ", "hot": "hot ", "needs a window": "win ", "warn": "warn", "ok": "ok  "}[category(e)]
        said = prof.problems + prof.hot + prof.warnings + ([f"needs a window: {prof.window}"] if prof.window else [])
        print(f"[{i + 1}/{len(chosen)}] {flag} {name:<32} {e.get('type', ''):<22} {time.time() - t:5.1f} s"
              + (f"  {'; '.join(said)[:220]}" if said else ""), flush=True)
    c.close()
    write_json(os.path.join(out, "machine-profile.json"), summary)
    with open(os.path.join(out, "machine-health.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(health_report(summary, curated_roles(args.repo or root_default), from_source(args.repo or root_default),
                                rebuilt_natives(args.repo or root_default)))
    if args.repo:
        merge_into_repo(os.path.abspath(args.repo), summary, out)
    cats = {}
    for m, e in summary["machines"].items():
        cats.setdefault(category(e), []).append(m)
    head = f"{len(summary['machines'])} machines profiled on {plat} in {time.time() - started:.0f} s: " + "; ".join(
        f"{k}: {len(v)}" + (f" ({', '.join(v)})" if k in ("diverging", "unhealthy", "hot", "needs a window") else "") for k, v in sorted(cats.items()))
    print(head, flush=True)
    if os.environ.get("GITHUB_ACTIONS"):
        print(f"::notice title=Machine profile ({plat})::{head[:3500]}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
