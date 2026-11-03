#!/usr/bin/env python3
# Fork (docs/specs/mcp-server.md §12.2, U1): the machine profiler. Drives an MCP server over stdio
# as any client does (rebuzz-headless --agent on Linux and macOS, ReBuzz.exe --agent on Windows,
# which hosts the native machines) and measures every machine in its Gear with the server's own
# tools:
#  - parameters: each one's value texts across its range (machine_describe value_table), read as a
#    unit, a mapping and choices (tools/machine_infer.py);
#  - generators: how they take notes (MIDI, or their Note track parameter), the pitch convention
#    (which note plays 440 Hz, from analyze's pitch metric), polyphony and level at the defaults;
#  - effects, fed noise and a sine from a Neo Poly: what kind they are, latency, tail, gain at the
#    defaults, whether gain depends on level, harmonics added, stereo and movement;
#  - health: load failures, crashes and hangs, NaN or runaway output, sound from silence, output
#    at parameter extremes, CPU per instance.
# It writes a draft manifest per machine (Neo machines keep their generated ones: for them it checks
# the inference against the manifest), a JSON summary and machine-health.md. With --repo it merges
# them into the repository: drafts into Machines/Manifests/<Generators|Effects>/, the summary into
# Machines/Manifests/machine-profile.json (entries from other platforms kept), and the report into
# docs/machine-health.md.
#
#   python3 tools/machine-profiler.py <rebuzz-headless | ReBuzz.exe> [--out DIR] [--only "Pedal*,Jeskola Delay"]
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
import machine_infer as mi  # noqa: E402

TEMPO = 120                 # beats per minute: a beat is 0.5 s
A4 = 69
FORMAT = "rebuzz-machine-manifest/1"


# ---- MCP client (as tools/null-test.py's, restartable) -----------------------------------------

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
        if os.path.basename(self.exe).lower() != "rebuzz.exe":
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
    """NaN, infinity or a runaway level (a diverging machine reaches hundreds of dB). The server
    writes a NaN peak as null and an infinite one as "inf"."""
    m = measure or {}
    p = (m.get("peaks") or {}).get("sample_peak_dbfs")
    if p == "inf":
        return True
    if p is None:
        return not m.get("silent", True)
    return isinstance(p, (int, float)) and p > 24


def loudness(measure):
    v = (measure or {}).get("loudness", {}).get("integrated_lufs")
    return v if isinstance(v, (int, float)) else None


# ---- one machine -----------------------------------------------------------------------------------

class Profile:
    def __init__(self, gear, kind):
        self.gear, self.kind = gear, kind
        self.data = {"gear": gear, "kind": kind}
        self.problems, self.warnings, self.notes = [], [], []

    def problem(self, text):
        self.problems.append(text)

    def warn(self, text):
        self.warnings.append(text)


def params_by_kind(describe):
    globals_, tracks = [], []
    for p in describe.get("parameters", []):
        (tracks if p.get("per_track") else globals_).append(p)
    return globals_, tracks


def manifest_entry(p, inferred):
    e = {"index": p.get("index", 0), "id": p["name"], "name": p["name"], "group": "",
         "min": p["min"], "max": p["max"], "default": p["default_raw"]}
    for k in ("kind", "a", "b", "exponent", "flag", "unit", "choices", "points"):
        if k in inferred and inferred[k] is not None and inferred[k] != "":
            e[k] = inferred[k]
    if inferred.get("kind") == "switch" and e.get("choices") == ["Off", "On"]:
        e.pop("choices")
    e["description"] = p.get("description", "")
    e["confidence"] = inferred.get("confidence", "values")
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
    a = c.call("analyze", source=render_id, metrics=["pitch"], from_seconds=0.05, to_seconds=until)
    p = a.get("pitch") or {}
    hz = p.get("median_hz")
    voiced = (p.get("voiced_percent") or 0) / 100
    if not hz or hz <= 0:
        return None, voiced
    return 69 + 12 * math.log2(hz / 440.0), voiced


def profile_generator(c, prof, info, baseline):
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
        prof.problem("NaN or runaway output with no notes")
    def try_modes():
        for m in modes:
            set_notes(c, "M", [note(A4)], m)
            r = render(c, 0, 4, 1.0)
            if blown(r["measure"]):
                prof.problem(f"NaN or runaway output playing A4 ({finite_peak(r['measure'])} dBFS)")
                return m, r
            if not r["measure"].get("silent", True) and (idle["measure"].get("silent", True) or (loudness(r["measure"]) or -99) > (loudness(idle["measure"]) or -99) + 3):
                return m, r
        return None, None

    mode, first = try_modes()
    if first is not None and blown(first["measure"]):
        prof.data["notes"] = {"input": mode}
        prof.data["type"] = "generator (blows up)"
        return
    if mode is None and note_param:
        # Samplers and trackers play the wavetable: give it a sine on A4 in slot 1 and try again.
        if add_test_wave(c):
            mode, first = try_modes()
            if mode is not None:
                prof.notes.append("plays the wavetable: silent until slot 1 has a wave (profiled with a sine rooted on A4)")
                prof.data["needs_wave"] = True
    notes = {"input": mode or "none"}
    prof.data["notes"] = notes
    if mode is None:
        prof.warn("no sound from a note at its defaults, as MIDI or in its tracks, even with a wave in slot 1" if note_param
                  else "no sound from a MIDI note at its defaults (it has no Note track parameter)")
        prof.data["type"] = "generator (silent at its defaults)"
        return
    m = first["measure"]
    prof.data["level"] = {"note": "A4 at velocity 100, 1.5 beats", "integrated_lufs": loudness(m), "sample_peak_dbfs": finite_peak(m),
                          "true_peak_dbtp": (m.get("peaks") or {}).get("true_peak_dbtp")}
    if (finite_peak(m) or -99) > 0:
        prof.warn(f"clips at the defaults: peak {finite_peak(m)} dBFS on one note")
    prof.data["cpu_ms_per_s"] = cpu(c, baseline["empty"])
    # Pitch: A4 sent, then A3 and A5 to see that it follows the keyboard.
    heard, voiced = heard_pitch(c, first["id"], 0.75)
    found = []
    if heard is not None and voiced >= 0.4:
        found.append((A4, heard))
        for sent in (A4 - 12, A4 + 12):
            set_notes(c, "M", [note(sent)], mode)
            r = render(c, 0, 4, 0.5)
            h, v = heard_pitch(c, r["id"], 0.75)
            if h is not None and v >= 0.4:
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
    """A 1 s sine on A4, rendered from a Neo Poly, into wave slot 1 (rooted on A4). False when it can't be made."""
    try:
        c.call("machine_add", gear="Neo Poly", name="WaveSrc", connect_to="Master")
        c.call("param_set", machine="WaveSrc", changes=[{"param": p, "value": v} for p, v in SOURCE_NOISE + SOURCE_SINE])
        set_notes(c, "WaveSrc", [note(A4, 0, 2.5, 127)], "midi", length=4)
        r = render(c, 0, 3, 0.1, probes=["WaveSrc"])
        c.call("wave_from_render", source=r["id"] + ":WaveSrc", from_seconds=0.05, to_seconds=1.05, slot=1, name="Profiler sine", root_note="A4")
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
        prof.problem("NaN or runaway output with silence in")
    elif not idle["measure"].get("silent", True):
        prof.warn(f"sound from silence ({finite_peak(idle['measure'])} dBFS peak): noise, hum or DC")
    # A noise burst of 1 beat (0.5 s), 3 s to ring out.
    set_notes(c, "Src", [note(A4, 0, 1, 127)], "midi", length=4)
    r = render(c, 0, 1, 3.0, probes=["Src", "M"], name="noise")
    if blown(r["measure"]):
        prof.problem(f"NaN or runaway output on noise ({finite_peak(r['measure'])} dBFS)")
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
        prof.problem(f"NaN or runaway output on a click ({finite_peak(rc['measure'])} dBFS)")
    else:
        _, cl, cr = read_wav(probe_file(rc, "M"))
        env = envelope([(a + b) / 2 for a, b in zip(cl, cr)], rate, 5)
        peak = max(env) if env else 0
        if peak > 0:
            start = int(0.025 / 0.005)    # after the click
            floor = peak * 0.001           # -60 dB
            last_loud = max((i for i, v in enumerate(env) if v > floor), default=start)
            fx["tail_seconds"] = round(max(0, last_loud + 1 - start) * 0.005, 2)
            if last_loud >= len(env) - 2:
                fx["tail_seconds_note"] = "rings longer than the 3 s rendered"
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
            prof.problem(f"NaN or runaway output on a {label} sine")
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


def classify_effect(prof, fx):
    traits = []
    loud, quiet = fx.get("sine", {}).get("loud", {}), fx.get("sine", {}).get("quiet", {})
    # A delay: repeated echoes, or one after a near-silent gap; a reverb: a long smooth tail.
    delay = fx.get("echoes", 0) >= 2 or (fx.get("echoes", 0) == 1 and (fx.get("gap_before_echo_db") or 0) < -40)
    if delay:
        traits.append("delay")
    elif (fx.get("tail_seconds") or 0) > 0.15:
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


def extremes(c, prof, globals_, has_notes, mode):
    """Every parameter at its minimum, then its maximum: does it blow up or go silent?"""
    out = {}
    for end in ("min", "max"):
        changes = [{"param": f"#{p['index']}", "value": end} for p in globals_ if "index" in p]
        if not changes:
            break
        try:
            c.call("param_set", machine="M", changes=changes)
        except ToolFailed as e:
            prof.notes.append(f"parameters at {end}: {e}")
            continue
        r = render(c, 0, 2, 0.5)
        out[end] = {"silent": r["measure"].get("silent"), "sample_peak_dbfs": finite_peak(r["measure"])}
        if blown(r["measure"]):
            prof.problem(f"NaN or runaway output with every parameter at its {end}imum")
    try:
        c.call("param_set", machine="M", changes=[{"param": f"#{p['index']}", "value": "default"} for p in globals_ if "index" in p])
    except ToolFailed:
        pass
    prof.data["extremes"] = out


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
        profile_generator(c, prof, info, baseline)
    else:
        profile_effect(c, prof, info, baseline)
        type_from_parameters(prof, globals_)
    if prof.data["type"] != "control":
        extremes(c, prof, globals_, kind == "generator", (prof.data.get("notes") or {}).get("input"))
    prof.data["entries"] = {"global": [manifest_entry(p, inferred[(False, p["name"])]) for p in globals_ if inferred[(False, p["name"])].get("kind")],
                            "track": [manifest_entry(p, inferred[(True, p["name"])]) for p in tracks if inferred[(True, p["name"])].get("kind")]}
    prof.data["seconds"] = round(time.time() - started, 1)
    return prof


# ---- outputs ---------------------------------------------------------------------------------------

def health_of(entry):
    problems, warnings = entry.get("problems", []), entry.get("warnings", [])
    return "unhealthy" if problems else "warn" if warnings else "ok"


def summary_line(e):
    t = e.get("type", "")
    k = e.get("kind")
    if k == "generator" and e.get("notes"):
        n = e["notes"]
        if n.get("input") == "none":
            return f"{t.capitalize()} (profiled draft): no sound from a note at its defaults."
        bits = []
        if n.get("pitched"):
            off = n.get("pitch_offset", 0)
            bits.append("pitched" + (f", plays {abs(off)} semitones {'low' if off > 0 else 'high'} (corrected by its pitch offset)" if off else ", plays A4 at 440 Hz as written"))
        else:
            bits.append("unpitched")
        p = n.get("polyphony", 1)
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
    m["health"] = {"status": health_of(e), "problems": e.get("problems", []) + e.get("warnings", [])}
    # Timings (CPU, load time) vary run to run: they stay in the summary and the report, not the draft.
    m["profile"] = {k: e[k] for k in ("level", "effect", "extremes") if k in e}
    m["parameters"] = (e.get("entries") or {}).get("global", [])
    m["track_parameters"] = (e.get("entries") or {}).get("track", [])
    return m


def subfolder(e):
    f = (e.get("gear_info") or {}).get("file", "")
    sub = f.split("/")[0] if "/" in f else ""
    return sub if sub in ("Generators", "Effects") else ("Generators" if e["kind"] == "generator" else "Effects")


def write_json(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=1, ensure_ascii=False)
        f.write("\n")


def health_report(summary):
    machines = summary["machines"]
    rows = sorted(machines.values(), key=lambda e: (e.get("kind", ""), e["gear"].lower()))
    by_platform = {}
    for e in rows:
        by_platform[e.get("platform", "?")] = by_platform.get(e.get("platform", "?"), 0) + 1
    unhealthy = [e for e in rows if health_of(e) == "unhealthy"]
    warn = [e for e in rows if health_of(e) == "warn"]
    crashes = [e for e in rows if any("crash" in p or "hung" in p for p in e.get("problems", []))]
    nan = [e for e in rows if any("NaN" in p for p in e.get("problems", []))]
    gens = [e for e in rows if e.get("kind") == "generator" and e.get("notes")]
    offsets = {}
    for e in gens:
        n = e["notes"]
        key = "no sound from a note" if n.get("input") == "none" else ("unpitched" if not n.get("pitched") else f"offset {n.get('pitch_offset', 0):+d}")
        offsets[key] = offsets.get(key, 0) + 1
    lines = [
        "# Machine health",
        "",
        "Generated by `tools/machine-profiler.py` (docs/specs/mcp-server.md §12.2) from `Machines/Manifests/machine-profile.json`. Do not edit by hand: run the profiler, then `--report`.",
        "",
        f"{len(rows)} machines profiled ({', '.join(f'{n} on {p}' for p, n in sorted(by_platform.items()))}). "
        f"{len(unhealthy)} unhealthy, {len(warn)} with warnings, {len(rows) - len(unhealthy) - len(warn)} clean. "
        f"Crashes or hangs: {len(crashes)}. NaN or runaway output: {len(nan)}.",
        "",
        "Pitch conventions of the generators (the offset `notes_write` applies so the written pitch plays): " +
        (", ".join(f"{k}: {v}" for k, v in sorted(offsets.items())) or "none profiled") + ".",
        "",
        "## Unhealthy",
        "",
        "Machines that don't load, crash or hang the server, or put out NaN or a runaway level. `gear_list` lists them last, with these reasons, and `machine_add` warns.",
        "",
    ]
    if unhealthy:
        lines += ["| Machine | Platform | Problems |", "|---|---|---|"]
        lines += [f"| {e['gear']} | {e.get('platform', '')} | {'; '.join(e['problems'])} |" for e in unhealthy]
    else:
        lines.append("None.")
    lines += ["", "## Warnings", ""]
    if warn:
        lines += ["| Machine | Platform | Warnings |", "|---|---|---|"]
        lines += [f"| {e['gear']} | {e.get('platform', '')} | {'; '.join(e['warnings'])} |" for e in warn]
    else:
        lines.append("None.")
    lines += ["", "## Generators", "", "| Machine | Type | Notes in | Pitch offset | Voices | Level (LUFS, one note) | CPU ms/s | Health |", "|---|---|---|---|---|---|---|---|"]
    for e in rows:
        if e.get("kind") != "generator":
            continue
        n = e.get("notes") or {}
        off = "" if not n.get("pitched") else f"{n.get('pitch_offset', 0):+d}" + (f" ({n.get('cents'):+.0f} ct)" if abs(n.get("cents") or 0) >= 1 else "")
        lvl = (e.get("level") or {}).get("integrated_lufs")
        lines.append(f"| {e['gear']} | {e.get('type', '')} | {n.get('input', '')} | {off} | {n.get('polyphony', '')} | "
                     f"{'' if lvl is None else lvl} | {e.get('cpu_ms_per_s', '')} | {health_of(e)} |")
    lines += ["", "## Effects", "", "| Machine | Type (traits) | Gain dB | Latency (samples) | Tail s | CPU ms/s | Health |", "|---|---|---|---|---|---|---|"]
    for e in rows:
        if e.get("kind") != "effect":
            continue
        fx = e.get("effect") or {}
        lines.append(f"| {e['gear']} | {', '.join(fx.get('traits', [])) or e.get('type', '')} | {fx.get('gain_db', '')} | {fx.get('latency_samples', '')} | "
                     f"{fx.get('tail_seconds', '')} | {e.get('cpu_ms_per_s', '')} | {health_of(e)} |")
    lines += ["", "## Not measured yet (U2)", "",
              "Per-parameter sweeps (what moving each parameter changes, for roles), velocity response and envelope length, denormal "
              "slowdowns, swept-sine and impulse responses (the source here is a Neo Poly's noise and sine), and whether `Work` reports "
              "silence (the engine doesn't expose it): §12.7.", ""]
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
                write_json(os.path.join(folder, subfolder(e), e["gear"] + ".machine.json"), json.load(f))
    write_report(root, merged)


def write_report(root, merged):
    with open(os.path.join(root, "docs", "machine-health.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(health_report(merged))


PACK = "MACHINE-PROFILE-PACK"


def pack(folder):
    """The drafts, summary and report of an --out folder, gzipped, as base64 lines in the log: a CI
    job's log can be read where its artifacts can't (--unpack takes them back out)."""
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w:gz") as tar:
        for base, _, files in os.walk(folder):
            for f in files:
                path = os.path.join(base, f)
                rel = os.path.relpath(path, folder).replace(os.sep, "/")
                if rel.startswith("manifests/") or rel in ("machine-profile.json", "machine-health.md"):
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
    ap.add_argument("exe", nargs="?", help="rebuzz-headless or ReBuzz.exe")
    ap.add_argument("--out", default=None, help="where drafts, the summary and the report go (default: a temp folder)")
    ap.add_argument("--only", default="", help="comma-separated gear names or patterns")
    ap.add_argument("--skip", default="", help="comma-separated gear names or patterns to leave out")
    ap.add_argument("--repo", default=None, help="merge the results into this repository checkout")
    ap.add_argument("--report", default=None, help="only rewrite ROOT/docs/machine-health.md from ROOT's summary")
    ap.add_argument("--merge", default=None, help="merge an earlier run's --out folder (a CI artifact) into --repo")
    ap.add_argument("--pack", default=None, help="print an --out folder into the log (base64 lines)")
    ap.add_argument("--unpack", default=None, help="a job log with packed lines: unpack them into --out")
    ap.add_argument("--timeout", type=float, default=90, help="seconds a call may take before the machine counts as hung")
    args = ap.parse_args()
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
    plat = "windows" if os.path.basename(args.exe).lower() == "rebuzz.exe" else sys.platform.replace("darwin", "macos")
    generated = generated_manifests(args.repo or root_default)
    c = Client(args.exe, os.path.join(out, "work"), args.timeout)
    gear = c.call("gear_list", limit=1000)["gear"]
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
            row = next((x for x in c.call("gear_list", search=name, limit=50)["gear"] if x["name"] == name), {})
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
        e.update({"platform": plat, "date": today, "problems": prof.problems, "warnings": prof.warnings, "notes_profiler": prof.notes,
                  "generated": name in generated, "health": health_of({"problems": prof.problems, "warnings": prof.warnings})})
        e.setdefault("type", "unknown")
        if name not in generated and ("entries" in e or prof.problems):
            write_json(os.path.join(out, "manifests", subfolder(e), name + ".machine.json"), draft_manifest(e))
        e.pop("entries", None)   # in the draft; the summary keeps the measurements
        summary["machines"][name] = e
        forget_renders(c)
        flag = {"ok": "ok  ", "warn": "warn", "unhealthy": "BAD "}[e["health"]]
        print(f"[{i + 1}/{len(chosen)}] {flag} {name:<32} {e.get('type', ''):<22} {time.time() - t:5.1f} s"
              + (f"  {'; '.join(prof.problems + prof.warnings)[:160]}" if prof.problems or prof.warnings else ""), flush=True)
    c.close()
    write_json(os.path.join(out, "machine-profile.json"), summary)
    with open(os.path.join(out, "machine-health.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(health_report(summary))
    if args.repo:
        merge_into_repo(os.path.abspath(args.repo), summary, out)
    bad = [m for m, e in summary["machines"].items() if e["health"] == "unhealthy"]
    head = f"{len(summary['machines'])} machines profiled on {plat} in {time.time() - started:.0f} s: {len(bad)} unhealthy" + (f" ({', '.join(bad)})" if bad else "")
    print(head, flush=True)
    if os.environ.get("GITHUB_ACTIONS"):
        print(f"::notice title=Machine profile ({plat})::{head[:3500]}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
