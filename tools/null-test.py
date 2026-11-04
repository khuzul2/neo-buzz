#!/usr/bin/env python3
# Fork (docs/specs/mac-engine.md decision 6, M3/M4): the null test. Neo machine renders on Linux and
# macOS (rebuzz-headless) must be bit-identical to the Windows package's (ReBuzz.exe --agent): every
# sample the same. The Neo machines compute only with IEEE basic operations (NeoBuzz.Dsp's portable
# maths, docs/specs/machines.md §4), so nothing may differ.
#
# A fixed set of cases covers every Neo machine, with presets and settings that take their distinct
# code paths (filters, oscillators, FM feedback, saturation, reverb tails). Each case runs in a fresh
# process: it builds a short song through the MCP tools, as a client does, and renders it (32-bit
# float WAV, full scale 1.0). Renders start on a tick boundary, so they are bit-identical across
# processes on one platform (docs/specs/mcp-server.md §10).
#
#   python3 tools/null-test.py render  <exe> <out-dir>              one <case>.wav per case
#   python3 tools/null-test.py compare <exe> <ref-dir> <work-dir>   renders, then compares to ref-dir
#   python3 tools/null-test.py diff    <ref-dir> <dir>               compares two folders of renders
#   ... [--only case,case]
#
# <exe> is ReBuzz.exe (Windows) or rebuzz-headless. compare fails a case on any differing sample, on a
# length or rate mismatch, or when it is missing from either side. A case that differs reports its
# first differing sample (frame, time, channel, both values, how many samples differ) and its largest
# difference in dBFS and where. Under GitHub Actions it writes the figures as a notice annotation
# (readable through the API) and to the step summary. The Windows package job renders the
# references; the Linux and macOS jobs compare.

import array
import json
import math
import os
import platform
import queue
import shutil
import struct
import subprocess
import sys
import threading
import time

TEMPO = 120   # 1 beat = 0.5 s

# Cases excluded on purpose, with the reason: {"case": "why"}. Empty: every case must match.
EXCLUDED = {}


# ---- the cases ------------------------------------------------------------------------------------

def n(pitch, start, length, velocity=100):
    return {"pitch": pitch, "start": start, "length": length, "velocity": velocity}


CHORDS = [n(p, 0, 1.75, 100) for p in ("C3", "E3", "G3", "B3")] + [n(p, 2, 2, 80) for p in ("A2", "E3", "A3", "C4")]
BASS = [n(p, i * 0.25, 0.2, 70 + (i * 7) % 57) for i, p in enumerate(
    ["A1", "A1", "A2", "A1", "C2", "A1", "G2", "A1", "A1", "E2", "A1", "A2", "D2", "A1", "C2", "E2"])]
LEAD = [n("E4", 0, 0.5), n("G4", 0.5, 0.5), n("A4", 1, 1), n("C5", 2, 0.25), n("B4", 2.25, 0.25), n("A4", 2.5, 1.5, 70)]
KEYS = [n("C4", 0, 1), n("E4", 0, 1), n("G4", 0, 1), n("D4", 1, 0.5, 60), n("F4", 1.5, 0.5, 90),
        n("A3", 2, 2, 110), n("C5", 2, 2, 110), n("E5", 3, 1, 40)]
BELLS = [n("C5", 0, 0.5), n("G5", 0.5, 0.5, 80), n("E5", 1, 1, 60), n("C6", 2, 0.25, 120), n("A4", 3, 1)]

# The source for the effects: Neo FM6 brass (feedback 7, bright, with a pitch envelope), a chord stab, a
# bass and a high note, then silence for the tails. FM6 rendered identically on Linux, macOS and
# Windows, so what an effect case measures is the effect's own difference.
SOURCE = ("Neo FM6", "Brass — Bright Section", [("Unison", 3), ("Uni spread", "100%")],
          [n(p, 0, 0.5, 120) for p in ("C3", "G3", "E4")] + [n("C2", 1, 1, 100), n("G5", 2, 0.25, 127), n("C4", 2.5, 0.75, 60)])
LONG = [n(p, 0, 15, 90) for p in ("A2", "E3", "C4", "G4")] + [n(p, 15, 13, 100) for p in ("F2", "C3", "A3", "E4")] + [n("C5", 2, 24, 50)]


# Neo EQ's graphic sliders, by their names' frequencies ("G 31.5 Hz" … "G 20 kHz").
GRAPHIC = ["20 Hz", "25 Hz", "31.5 Hz", "40 Hz", "50 Hz", "63 Hz", "80 Hz", "100 Hz", "125 Hz", "160 Hz", "200 Hz",
           "250 Hz", "315 Hz", "400 Hz", "500 Hz", "630 Hz", "800 Hz", "1 kHz", "1.25 kHz", "1.6 kHz", "2 kHz",
           "2.5 kHz", "3.15 kHz", "4 kHz", "5 kHz", "6.3 kHz", "8 kHz", "10 kHz", "12.5 kHz", "16 kHz", "20 kHz"]


def gen(name, gear, preset, params, notes, beats=4, tail=1.0):
    return {"name": name, "gen": (gear, preset, params, notes), "fx": [], "beats": beats, "tail": tail}


def fx(name, gear, preset, params=(), tail=1.0, extra=(), source=SOURCE, beats=4, auto=()):
    """An effect (or a chain: extra effects after it) on the shared source; auto: the effect's
    parameters automated in steps, [(param, [(beat, value), ...])]."""
    return {"name": name, "gen": source, "fx": [(gear, preset, list(params))] + list(extra), "beats": beats, "tail": tail,
            "auto": list(auto)}


CASES = [
    # Neo Poly: oscillators (blep saw, pulse, sync, FM, ring), ladder and SVF filters, unison, drift, noise.
    gen("poly-default", "Neo Poly", None, [], CHORDS),                     # drift on: per-voice random
    gen("poly-acid", "Neo Poly", "Bass — Acid", [], BASS),
    gen("poly-reese", "Neo Poly", "Bass — Reese", [], BASS[::2]),
    gen("poly-fm-growl", "Neo Poly", "Bass — FM Growl", [], BASS[::2]),
    gen("poly-sync-scream", "Neo Poly", "Lead — Sync Scream", [], LEAD),
    gen("poly-pwm-pad", "Neo Poly", "Pad — Evolving PWM", [], CHORDS),
    gen("poly-noise-sweep", "Neo Poly", "FX — Noise Sweep", [], [n("C4", 0, 3)]),
    gen("poly-self-osc", "Neo Poly", None,
        [("F1 reso", "100%"), ("F1 cutoff", "400 Hz"), ("F1 drive", "12 dB"), ("Ring 1×2", "40%"), ("O2 level", "60%"),
         ("O2 sync", "on"), ("O3 level", "50%"), ("Voice mode", "Legato"), ("Glide mode", "Always")], LEAD),
    gen("poly-svf-parallel", "Neo Poly", None,
        [("F1 type", "SVF BP"), ("F1 reso", "70%"), ("F2 type", "HP"), ("F2 reso", "60%"), ("Routing", "Parallel"),
         ("Noise", "30%"), ("Noise colour", "100%"), ("O1 shape", 3), ("O2 level", "50%"), ("FM 1 → 2", 1.5)], KEYS),
    # Neo Wave: tables built in the background, warps, PM, sync, its filter.
    gen("wave-init", "Neo Wave", "Init — Basic", [], CHORDS),
    gen("wave-vowel-choir", "Neo Wave", "Pad — Vowel Choir", [], CHORDS),
    gen("wave-window-sync", "Neo Wave", "Texture — Window Sync Sweep", [], [n("A2", 0, 3.5)]),
    gen("wave-pm-bloom", "Neo Wave", "Texture — PM Bloom", [], KEYS),
    gen("wave-grit-motion", "Neo Wave", "Texture — Grit Motion", [], [n("D3", 0, 3.5), n("A3", 1, 2.5)]),
    gen("wave-reso-pluck", "Neo Wave", "Bass — Reso Pluck", [], BASS),
    gen("wave-mirror-bell", "Neo Wave", "Pluck — Mirror Bell", [], BELLS),
    # Neo FM6: algorithms, feedback, the Clean 2x and Raw 1x engines, envelopes, LFO.
    gen("fm6-tine-piano", "Neo FM6", "Keys — Tine Piano", [], KEYS),
    gen("fm6-glass-bell", "Neo FM6", "Bell — Glass Bell", [], BELLS, tail=2.0),
    gen("fm6-random-chimes", "Neo FM6", "Bell — Random Chimes", [], BELLS),
    gen("fm6-brass", "Neo FM6", "Brass — Bright Section", [], CHORDS),
    gen("fm6-saw-lead", "Neo FM6", "Lead — Saw Lead", [], LEAD),
    gen("fm6-metal-sweep", "Neo FM6", "Texture — Metal Sweep", [], [n("C3", 0, 3.5)]),
    gen("fm6-raw-feedback", "Neo FM6", "Init — Sine", [("Feedback", 7), ("Engine", "Raw 1×"), ("LFO pitch", 40)], LEAD),
    # Neo Comp: the four characters, lookahead, knee, auto release.
    fx("comp-glue", "Neo Comp", "Glue — Mix Bus", [("Threshold", "-30 dB")]),
    fx("comp-fet", "Neo Comp", "FET — All Buttons"),
    fx("comp-opto", "Neo Comp", "Opto — Smooth Vocal", [("Threshold", "-30 dB")]),
    fx("comp-clean-brickwall", "Neo Comp", "Clean — Brickwall", [("Lookahead", "3 ms"), ("Auto release", "on")]),
    # Neo Drive: the five models, oversampling 2x/4x/8x.
    fx("drive-tape", "Neo Drive", "Tape — Hot Reels"),
    fx("drive-tube", "Neo Drive", "Tube — Preamp Push"),
    fx("drive-transistor", "Neo Drive", "Transistor — Fuzz Wall", [("Quality", "8×")]),
    fx("drive-diode", "Neo Drive", "Diode — Edge", [("Quality", "2×")]),
    fx("drive-fold", "Neo Drive", "Fold — West Coast"),
    # Neo EQ: bells, shelves, cuts at each slope, notch.
    fx("eq-vocal-clarity", "Neo EQ", "Mixing — Vocal Clarity"),
    fx("eq-telephone", "Neo EQ", "Mixing — Telephone"),
    fx("eq-hum-notch", "Neo EQ", "Fix — Hum Notch 50 Hz", [("B1 on", "on"), ("B1 slope", "48 dB/oct"), ("B8 on", "on")]),
    # Neo EQ's graphic mode (machine-safety.md §4): the solver's result (the sliders' median as a gain, the
    # telephone's at −18 dB), the zigzag at ±18 dB (bands near 54 dB), and sliders and the mode automated
    # (the gains gliding in segments, the crossfade between the modes).
    fx("eq-graphic-loudness", "Neo EQ", "Graphic — Loudness (Smile)"),
    fx("eq-graphic-telephone", "Neo EQ", "Graphic — Telephone", [("Output", "+12 dB")]),
    fx("eq-graphic-zigzag", "Neo EQ", "Graphic — Flat",
       [(f"G {f}", "+18 dB" if i % 2 == 0 else "-18 dB") for i, f in enumerate(GRAPHIC)] + [("Output", "-18 dB")]),
    fx("eq-graphic-automated", "Neo EQ", "Graphic — Air",
       auto=[("G 250 Hz", [(0, "-12 dB"), (1, "+12 dB"), (1.5, "-18 dB"), (2, "+6 dB")]),
             ("G 2 kHz", [(0, "+9 dB"), (0.75, "-9 dB"), (2.25, "+18 dB")]),
             ("Mode", [(0, "Graphic"), (2.5, "Parametric"), (3.25, "Graphic")])]),
    # Neo Limiter: pushed hard, auto and set release; Neo Analyzer after it (audio passes through).
    fx("limiter-loud", "Neo Limiter", "Mastering — Loud", [("Input gain", "+18 dB")], extra=[("Neo Analyzer", "Mix", [])]),
    fx("limiter-smash", "Neo Limiter", "Creative — Smash", [("Input gain", "+24 dB"), ("Auto release", "off")]),
    # Neo Utility: gain, pan law, width, bass mono, polarity, swap, DC removal.
    fx("utility-bass-mono-wide", "Neo Utility", "Mastering — Bass Mono 120 Hz", [("Width", "180%"), ("Gain", "-3.5 dB")]),
    fx("utility-pan-dc-swap", "Neo Utility", "Mastering — Clean Up (Bass Mono 80 Hz, DC)",
       [("Pan law", "Mono pan"), ("Pan", "-30%"), ("Swap", "on"), ("Invert R", "on")]),
    # Neo Verb: the four algorithms, modulation, shimmer, a 20-second decay, and freeze switched on
    # mid-render by automation (the tail held with no loss).
    fx("verb-hall", "Neo Verb", "Hall — Concert", tail=2.5),
    fx("verb-room", "Neo Verb", "Room — Live Drums", tail=1.5),
    fx("verb-chamber", "Neo Verb", "Chamber — Echo Chamber", tail=2.0),
    fx("verb-plate", "Neo Verb", "Plate — Snare", tail=1.5),
    fx("verb-shimmer", "Neo Verb", "Ambient — Shimmer", tail=2.5),
    fx("verb-infinite", "Neo Verb", "Ambient — Infinite Pad", tail=2.5),
    fx("verb-freeze", "Neo Verb", "Hall — Cathedral", [("Mix", "60%")], tail=1.5, auto=[("Freeze", [(0, "off"), (1.5, "on")])]),
    # Neo Ensemble: four chorus voices, a triangle LFO, the flanger at ±99 % feedback (its saturator
    # working) and swept fast, the phaser's twelve stages at 99 %, tempo sync, and a mode switch
    # mid-render by automation (the fade, the line kept).
    fx("ensemble-strings", "Neo Ensemble", "Ensemble — Strings"),
    fx("ensemble-poly-synth", "Neo Ensemble", "Chorus — Poly Synth I"),
    fx("ensemble-flanger-full", "Neo Ensemble", "Flanger — Metallic", [("Feedback", "99%"), ("Depth", "100%"), ("Rate", "8 Hz")]),
    fx("ensemble-flanger-negative", "Neo Ensemble", "Flanger — Tape Style (Negative)", [("Feedback", "-99%"), ("Tone", "2 kHz")]),
    fx("ensemble-phaser-12", "Neo Ensemble", "Phaser — 8-Stage Deep", [("Stages", 12), ("Feedback", "99%"), ("Rate", "3 Hz")]),
    fx("ensemble-synced-switch", "Neo Ensemble", "Phaser — Slow Sweep (4 Bars)", [("Sync", "1/4")],
       auto=[("Mode", [(0, "Phaser"), (2, "Flanger")])]),
    # Neo Delay: synced stereo, ping-pong and cross lines with both loop filters, the saturator driven at
    # 95 % feedback, the wow and ducking, a comb at −99 %, and the time automated (a glide, then a
    # crossfade, then a note value).
    fx("delay-dotted-throw", "Neo Delay", "Mix — Dotted Eighth Throw", tail=1.5),
    fx("delay-pingpong-send", "Neo Delay", "Send — Ping-Pong Eighths", tail=1.5),
    fx("delay-dub-driven", "Neo Delay", "Dub — Tape Echo (Saturated)", [("Feedback", "95%"), ("Drive", "18 dB")], tail=2.0),
    fx("delay-cross-wow-duck", "Neo Delay", "Send — Long Ambient Wash", [("Mod depth", "5 ms"), ("Mod rate", "5 Hz"), ("Duck", "18 dB"), ("Duck threshold", "-30 dB")], tail=2.0),
    fx("delay-comb-negative", "Neo Delay", "Texture — Comb Resonator", [("Feedback", "-99%")]),
    fx("delay-time-moves", "Neo Delay", "Mix — Quarter Note Echo", [("Sync", "Free"), ("Time", "100 ms"), ("Feedback", "60%")], tail=1.5,
       auto=[("Time", [(0, "100 ms"), (1, "130 ms"), (2, "450 ms")]), ("Sync", [(3, "1/8")])]),
    # Neo Multiband: the linear-phase crossovers (the partitioned FFT convolution, its FIR design), each band's dynamics
    # (compression, upward expansion, knee, range), mid/side unlinked, three bands, minimum phase, and the crossovers
    # automated (redesigns crossfading), the band count and the phase switched.
    fx("multiband-gentle-glue", "Neo Multiband", "Mastering — Gentle Glue (4 Bands)"),
    fx("multiband-squash-ms", "Neo Multiband", "Creative — Heavy Squash (Parallel 50 %)", [("Stereo", "M/S"), ("Link", "0%")]),
    fx("multiband-expand-3", "Neo Multiband", "Mastering — Transient Restore (Upward Expansion)"),
    fx("multiband-minimum", "Neo Multiband", "Mastering — Zero-Latency Glue (Minimum Phase)", [("B4 ratio", "0.6:1"), ("B4 range", "6 dB")]),
    fx("multiband-moves", "Neo Multiband", "Bus — Drum Bus Punch (3 Bands)",
       auto=[("Crossover 1", [(0, "120 Hz"), (1, "300 Hz"), (2, "80 Hz")]), ("Bands", [(2, "4 bands")]), ("Phase", [(3, "Minimum")])]),
    # Long: what drifts with time (oscillator and LFO phases, modulated delays) shows here. 16 s each.
    gen("long-poly", "Neo Poly", "Pad — Evolving PWM", [], LONG, beats=32),
    gen("long-wave", "Neo Wave", "Pad — Organ Drift", [], LONG, beats=32),
    gen("long-fm6", "Neo FM6", "Pad — FM Strings", [], LONG, beats=32),
    fx("long-verb", "Neo Verb", "Ambient — Long Dark Hall", tail=3.0, beats=32,
       source=("Neo FM6", "Keys — Tine Piano", [], [n(p, b, 0.5, 100) for b in range(0, 32, 2) for p in ("C4", "E4", "G4")])),
    fx("long-ensemble", "Neo Ensemble", "Flanger — Slow Jet", [("Rate", "0.3 Hz")], tail=1.0, beats=32,
       source=("Neo FM6", "Keys — Tine Piano", [], [n(p, b, 0.5, 100) for b in range(0, 32, 2) for p in ("C4", "E4", "G4")])),
    fx("long-delay", "Neo Delay", "Tape — Analog Wobble (Dark)", [("Feedback", "85%")], tail=2.0, beats=32,
       source=("Neo FM6", "Keys — Tine Piano", [], [n(p, b, 0.5, 100) for b in range(0, 32, 4) for p in ("C4", "E4", "G4")])),
]


# ---- MCP client -----------------------------------------------------------------------------------

class Client:
    """One request at a time over stdio, as headless-smoke.py does; binary pipes so Windows reads the same."""

    def __init__(self, exe, work, label):
        os.makedirs(work, exist_ok=True)
        args = [exe, "--agent"]
        if os.path.basename(exe).lower() != "rebuzz.exe":
            args += ["--settings", os.path.abspath(os.path.join(work, "settings"))]   # a clean one per case
        self.stderr_path = os.path.join(work, label + ".stderr.txt")
        self.stderr = open(self.stderr_path, "wb")
        self.proc = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.stderr)
        self.lines = queue.Queue()
        threading.Thread(target=self._read, daemon=True).start()
        self.next_id = 0

    def _read(self):
        for line in self.proc.stdout:
            self.lines.put(line)
        self.lines.put(None)

    def stderr_tail(self):
        self.stderr.flush()
        try:
            with open(self.stderr_path, "rb") as f:
                return f.read()[-3000:].decode("utf-8", "replace")
        except OSError:
            return ""

    def request(self, method, params=None, timeout=180):
        self.next_id += 1
        msg = {"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params or {}}
        self.proc.stdin.write((json.dumps(msg) + "\n").encode("utf-8"))
        self.proc.stdin.flush()
        deadline = time.time() + timeout
        while True:
            try:
                line = self.lines.get(timeout=max(0.1, deadline - time.time()))
            except queue.Empty:
                raise RuntimeError(f"{method}: no answer in {timeout} s; stderr:\n{self.stderr_tail()}")
            if line is None:
                raise RuntimeError(f"{method}: the server closed stdout; stderr:\n{self.stderr_tail()}")
            try:
                r = json.loads(line)
            except ValueError:
                continue
            if r.get("id") == self.next_id:
                return r

    def initialize(self):
        r = self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "null-test", "version": "1"}})
        if "result" not in r:
            raise RuntimeError(f"initialize: {r}")
        self.proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        self.proc.stdin.flush()

    def call(self, tool, **args):
        r = self.request("tools/call", {"name": tool, "arguments": args})
        res = r.get("result") or {}
        text = (res.get("content") or [{}])[0].get("text", "") if res else json.dumps(r.get("error"))
        if "error" in r or res.get("isError", True):
            raise RuntimeError(f"{tool}({json.dumps(args, ensure_ascii=False)[:300]}): {text[:600]}")
        return res.get("structuredContent") or {}

    def close(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(60)
        except Exception:
            self.proc.kill()
        self.stderr.close()


# ---- rendering ------------------------------------------------------------------------------------

def set_params(c, machine, params):
    if params:
        c.call("param_set", machine=machine, changes=[{"param": p, "value": v} for p, v in params])


def render_case(exe, case, out_dir, work):
    c = Client(exe, os.path.join(work, case["name"]), case["name"])
    try:
        c.initialize()
        c.call("song_set", tempo_bpm=TEMPO, song_end_beats=case["beats"])
        gear, preset, params, notes = case["gen"]
        c.call("machine_add", gear=gear, name="Src")
        if preset:
            c.call("preset_load", machine="Src", preset=preset)
        set_params(c, "Src", params)
        c.call("pattern_create", machine="Src", name="A", length_beats=case["beats"])
        c.call("notes_write", machine="Src", pattern="A", notes=notes)
        c.call("arrangement_place", machine="Src", pattern="A", at=[0])
        last = "Src"
        for i, (fgear, fpreset, fparams) in enumerate(case["fx"]):
            name = f"Fx{i + 1}"
            c.call("machine_add", gear=fgear, name=name, insert_after=last)
            if fpreset:
                c.call("preset_load", machine=name, preset=fpreset)
            set_params(c, name, fparams)
            if i == 0 and case.get("auto"):
                c.call("pattern_create", machine=name, name="A", length_beats=case["beats"])
                for param, points in case["auto"]:
                    c.call("automation_write", machine=name, pattern="A", param=param, shape="step",
                           points=[{"at": at, "value": v} for at, v in points])
                c.call("arrangement_place", machine=name, pattern="A", at=[0])
            last = name
        r = c.call("render", **{"from": 0, "to": case["beats"], "tail_seconds": case["tail"], "name": case["name"]})
        os.makedirs(out_dir, exist_ok=True)
        shutil.copyfile(r["file"], os.path.join(out_dir, case["name"] + ".wav"))
        return r
    finally:
        c.close()


def render_all(exe, out_dir, work, only):
    failed = []
    started = time.time()
    for case in CASES:
        if only and case["name"] not in only:
            continue
        t = time.time()
        try:
            r = render_case(exe, case, out_dir, work)
            loud = (r.get("measure") or {}).get("loudness", {}).get("integrated_lufs")
            print(f"rendered {case['name']:<26} {r.get('seconds')} s at {r.get('sample_rate')} Hz, {loud} LUFS ({time.time() - t:.1f} s)", flush=True)
            if (r.get("measure") or {}).get("silent"):
                failed.append(case["name"])
                print(f"FAIL {case['name']}: the render is silent", flush=True)
        except Exception as e:
            failed.append(case["name"])
            print(f"FAIL {case['name']}: {e}", flush=True)
    done = sum(1 for c in CASES if not only or c["name"] in only) - len(failed)
    print(f"{done} rendered in {time.time() - started:.0f} s on {platform_label()}", flush=True)
    if os.environ.get("GITHUB_ACTIONS"):
        print(f"::notice title=Null test renders ({platform_label()})::{done} cases rendered in {time.time() - started:.0f} s"
              + (f"; failed: {', '.join(failed)}" if failed else ""), flush=True)
    return failed


# ---- comparing ------------------------------------------------------------------------------------

def read_wav(path):
    """32-bit float WAV (what render writes): (rate, channels, samples interleaved)."""
    with open(path, "rb") as f:
        data = f.read()
    if data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        raise ValueError(f"{path}: not a WAV file")
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
    if fmt is None or samples is None:
        raise ValueError(f"{path}: no fmt or data chunk")
    tag, channels, rate, _, _, bits = fmt
    if tag not in (3, 0xFFFE) or bits != 32:
        raise ValueError(f"{path}: format {tag}, {bits} bits (expected 32-bit float)")
    return rate, channels, samples


def db(x):
    return 20 * math.log10(x) if x > 0 else float("-inf")


def compare_one(ref, got):
    """(identical, max difference dBFS, text)."""
    r_rate, r_ch, a = read_wav(ref)
    g_rate, g_ch, b = read_wav(got)
    if (r_rate, r_ch) != (g_rate, g_ch):
        return False, None, f"format differs: reference {r_rate} Hz × {r_ch}, this {g_rate} Hz × {g_ch}"
    if len(a) != len(b):
        return False, None, f"length differs: reference {len(a) // r_ch} frames, this {len(b) // g_ch}"
    peak = max((abs(x) for x in a), default=0.0)
    # The bits, not the values: a NaN or a -0.0 counts.
    if a.tobytes() == b.tobytes():
        return True, float("-inf"), f"identical ({len(a) // r_ch} frames, peak {db(peak):.1f} dBFS)"
    ia, ib = array.array("I", a.tobytes()), array.array("I", b.tobytes())
    worst, at, first, count = 0.0, 0, None, 0
    for i in range(len(ia)):
        if ia[i] != ib[i]:
            count += 1
            if first is None:
                first = at = i
            d = abs(a[i] - b[i])
            if d > worst or math.isnan(d):
                worst, at = d, i

    def where(i):
        frame, ch = divmod(i, r_ch)
        return f"frame {frame} ({frame / r_rate:.3f} s, {'LR'[ch] if r_ch == 2 else ch})"

    level = float("inf") if math.isnan(worst) else db(worst)
    return False, level, (f"differs: first at {where(first)}, reference {a[first]:.9g}, this {b[first]:.9g}; "
                          f"{count} of {len(a)} samples differ, by up to {level:.1f} dBFS (at {where(at)}); "
                          f"peak {db(peak):.1f} dBFS")


def compare_dirs(ref_dir, got_dir, only):
    rows, failed = [], []
    names = [c["name"] for c in CASES if not only or c["name"] in only]
    extra = sorted(f[:-4] for f in os.listdir(ref_dir) if f.endswith(".wav") and f[:-4] not in names and not only)
    for name in names + extra:
        ref, got = os.path.join(ref_dir, name + ".wav"), os.path.join(got_dir, name + ".wav")
        if name in EXCLUDED:
            rows.append((name, "excluded", None, EXCLUDED[name]))
            continue
        if not os.path.exists(ref) or not os.path.exists(got):
            missing = "the reference" if not os.path.exists(ref) else "this platform's render"
            if name in extra:
                missing = "the case (not in this script)"
            rows.append((name, "FAIL", None, f"missing: {missing}"))
            failed.append(name)
            continue
        try:
            ok, level, text = compare_one(ref, got)
        except ValueError as e:
            ok, level, text = False, None, str(e)
        rows.append((name, "ok" if ok else "FAIL", level, text))
        if not ok:
            failed.append(name)
    return rows, failed


def report(rows, failed, title):
    lines = []
    for name, status, level, text in rows:
        lines.append(f"{status:<8} {name:<26} {text}")
    print("\n".join(lines), flush=True)
    worst = max((lv for _, _, lv, _ in rows if lv is not None), default=float("-inf"))
    summary = (f"{len(rows) - len(failed)} of {len(rows)} cases identical"
               + ("" if worst == float("-inf") else f"; largest difference {worst:.1f} dBFS"))
    print(summary, flush=True)
    if os.environ.get("GITHUB_ACTIONS"):
        # One notice with a line per case (readable through the API), and the step summary.
        short = [f"{name}: " + ("identical" if lv == float("-inf") else text)
                 + ("" if status == "ok" else f" [{status}]") for name, status, lv, text in rows]
        body = summary + "\n" + "\n".join(short)
        kind = "error" if failed else "notice"
        title = title.replace("%", "%25").replace(":", "%3A").replace(",", "%2C")
        print(f"::{kind} title={title}::" + body.replace("%", "%25").replace("\r", "").replace("\n", "%0A"), flush=True)
        step = os.environ.get("GITHUB_STEP_SUMMARY")
        if step:
            with open(step, "a", encoding="utf-8") as f:
                f.write(f"### {title}\n\n{summary}\n\n| Case | Result | Samples |\n|---|---|---|\n")
                for name, status, lv, text in rows:
                    f.write(f"| {name} | {status} | {text} |\n")
                f.write("\n")


def platform_label():
    system = {"Darwin": "macOS", "Windows": "Windows", "Linux": "Linux"}.get(platform.system(), platform.system())
    return f"{system} {platform.machine().lower()}"


def main(argv):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except AttributeError:
        pass
    only = None
    if "--only" in argv:
        i = argv.index("--only")
        only = set(argv[i + 1].split(","))
        argv = argv[:i] + argv[i + 2:]
    if len(argv) >= 3 and argv[0] == "render":
        exe, out_dir = argv[1], argv[2]
        failed = render_all(exe, out_dir, os.path.join(out_dir, "work"), only)
        return 1 if failed else 0
    if len(argv) >= 4 and argv[0] == "compare":
        exe, ref_dir, work = argv[1], argv[2], argv[3]
        got_dir = os.path.join(work, "renders")
        render_failed = render_all(exe, got_dir, os.path.join(work, "work"), only)
        rows, failed = compare_dirs(ref_dir, got_dir, only)
        report(rows, failed, f"Null test, {platform_label()} vs Windows")
        return 1 if failed or render_failed else 0
    if len(argv) >= 3 and argv[0] == "diff":
        rows, failed = compare_dirs(argv[1], argv[2], only)
        report(rows, failed, "Null test")
        return 1 if failed else 0
    print("usage: null-test.py render <exe> <out-dir> | compare <exe> <ref-dir> <work-dir> | diff <ref-dir> <dir> [--only a,b]")
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
