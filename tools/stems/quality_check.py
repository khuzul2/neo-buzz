#!/usr/bin/env python3
# NeoBuzz Live stem models (docs/specs/stem-separation.md): a quality check with the engine's own audio.
# rebuzz-headless renders a four-part song (Neo Drums, a Dye Essence bass, a Neo Poly pad, a Dye Essence lead) as a mix and
# each part on its own. audio_separate then splits the mix with each installed model, and the new tracks are rendered one by
# one. Reported per model and stem: the SDR of the separated stem against the part(s) that were mixed in (drums, bass,
# other = pad + lead; there is no vocal part, so vocals are reported as their level only), and the time per minute of audio.
# It asserts nothing: a failure of a step is printed and the script exits 0 (the report is what it is for).
#
#   python3 quality_check.py <rebuzz-headless> <models folder> <work folder> [--report <json>] [--beats 64]
import argparse
import json
import math
import os
import struct
import subprocess
import sys
import time

import numpy as np

ap = argparse.ArgumentParser()
ap.add_argument("exe")
ap.add_argument("models")
ap.add_argument("work")
ap.add_argument("--report")
ap.add_argument("--beats", type=int, default=64)
ap.add_argument("--bpm", type=int, default=120)
args = ap.parse_args()
os.makedirs(args.work, exist_ok=True)

# The headless engine looks for models in Gear/Models/Stems beside itself: link the folders in.
gear = os.path.join(os.path.dirname(os.path.abspath(args.exe)), "Gear", "Models", "Stems")
os.makedirs(gear, exist_ok=True)
for name in sorted(os.listdir(args.models)):
    src, dst = os.path.join(os.path.abspath(args.models), name), os.path.join(gear, name)
    if os.path.isdir(src) and not os.path.exists(dst):
        os.symlink(src, dst)

proc = subprocess.Popen([args.exe, "--agent", "--settings", os.path.join(args.work, "settings")], stdin=subprocess.PIPE,
                        stdout=subprocess.PIPE, stderr=open(os.path.join(args.work, "stderr.txt"), "w"), text=True, bufsize=1)
next_id = 0


def request(method, params=None):
    global next_id
    next_id += 1
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params or {}}) + "\n")
    proc.stdin.flush()
    line = proc.stdout.readline()
    if not line:
        raise SystemExit("headless closed stdout; stderr:\n" + open(os.path.join(args.work, "stderr.txt")).read()[-3000:])
    return json.loads(line)


def call(tool, **a):
    r = request("tools/call", {"name": tool, "arguments": a})
    res = r.get("result") or {}
    text = res.get("content", [{}])[0].get("text", "") if res else json.dumps(r.get("error"))
    ok = bool(res) and not res.get("isError", True)
    print(f"  {'ok  ' if ok else 'FAIL'} {tool}: {text[:200]}", flush=True)
    return ok, text, res.get("structuredContent") or {}


def read_wav(path):
    """A WAV (PCM 16/24 or float32) as float32 [channels, n]."""
    b = open(path, "rb").read()
    pos, fmt, data = 12, None, None
    while pos + 8 <= len(b):
        cid, size = b[pos:pos + 4], struct.unpack_from("<I", b, pos + 4)[0]
        body = b[pos + 8:pos + 8 + size]
        if cid == b"fmt ":
            fmt = struct.unpack_from("<HHIIHH", body)
        elif cid == b"data":
            data = body
        pos += 8 + size + (size & 1)
    tag, ch, rate, _, _, bits = fmt
    if bits == 32:
        x = np.frombuffer(data, "<f4").astype(np.float32)
    elif bits == 16:
        x = np.frombuffer(data, "<i2").astype(np.float32) / 32768
    else:
        a = np.frombuffer(data, np.uint8).reshape(-1, 3)
        x = ((a[:, 0].astype(np.int32) | (a[:, 1].astype(np.int32) << 8) | (a[:, 2].astype(np.int32) << 16)) << 8 >> 8).astype(np.float32) / 8388608
    n = len(x) // ch
    return x[:n * ch].reshape(n, ch).T.copy(), rate


def sdr(ref, est):
    n = min(ref.shape[-1], est.shape[-1])
    ref, est = ref[..., :n].astype(np.float64), est[..., :n].astype(np.float64)
    num, den = np.sum(ref ** 2), np.sum((ref - est) ** 2)
    if num < 1e-12:
        return None
    return float(10 * math.log10(num / max(den, 1e-20)))


def db(x):
    r = float(np.sqrt(np.mean(x.astype(np.float64) ** 2)))
    return round(20 * math.log10(max(r, 1e-9)), 1)


report = {"beats": args.beats, "bpm": args.bpm, "models": {}}
try:
    request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "stem-quality", "version": "1"}})
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
    proc.stdin.flush()

    B = args.beats
    call("song_set", tempo_bpm=args.bpm, song_end_beats=B)
    parts = [("Drums", "Neo Drums", None), ("Bass", "Dye Essence", "Bass — FM Growl"),
             ("Pad", "Neo Poly", None), ("Lead", "Dye Essence", "Lead — Wide Unison")]
    for name, gear_name, preset in parts:
        call("machine_add", gear=gear_name, name=name)
        if preset:
            call("preset_load", machine=name, preset=preset)
        call("pattern_create", machine=name, name="A", length_beats=B)
    kit = []
    for bar in range(B // 4):
        for beat, pitch in ((0, "C2"), (1, "F#2"), (2, "D2"), (3, "F#2")):
            kit.append({"pitch": pitch, "start": bar * 4 + beat, "length": 0.25})
    bass, pad, lead = [], [], []
    roots = ["A1", "F1", "C2", "G1"]
    chords = [("A3", "C4", "E4"), ("F3", "A3", "C4"), ("C4", "E4", "G4"), ("G3", "B3", "D4")]
    melody = ["E5", "C5", "G5", "D5"]
    for bar in range(B // 4):
        r = roots[bar % 4]
        for k in range(4):
            bass.append({"pitch": r, "start": bar * 4 + k, "length": 0.75})
        pad += [{"pitch": p, "start": bar * 4, "length": 4} for p in chords[bar % 4]]
        for k in range(4):
            lead.append({"pitch": melody[(bar + k) % 4], "start": bar * 4 + k, "length": 0.5})
    for name, notes in (("Drums", kit), ("Bass", bass), ("Pad", pad), ("Lead", lead)):
        call("notes_write", machine=name, pattern="A", notes=notes)
        call("arrangement_place", machine=name, pattern="A", at=[0])

    def render(label, **kw):
        ok, _, r = call("render", **{"from": 0, "to": B, "tail_seconds": 0, "name": label, **kw})
        return read_wav(r["file"])[0] if ok and r.get("file") else None

    mix = render("mix")
    solo = {n: render("solo-" + n.lower(), solo=[n]) for n, _, _ in parts}
    if mix is None or any(v is None for v in solo.values()):
        raise RuntimeError("the song did not render")
    n = min(x.shape[-1] for x in [mix, *solo.values()])
    refs = {"drums": solo["Drums"][:, :n], "bass": solo["Bass"][:, :n], "other": solo["Pad"][:, :n] + solo["Lead"][:, :n]}
    print("mixing check: mix vs sum of parts SDR", sdr(mix[:, :n], sum(solo[k][:, :n] for k in solo)))
    seconds = n / 44100
    report["audio_seconds"] = round(seconds, 1)
    mixfile = call("render", **{"from": 0, "to": B, "tail_seconds": 0, "name": "mix-for-separation"})[2].get("file")

    for quality in ("Fast", "Balanced", "Best"):
        print(f"== {quality}")
        t0 = time.time()
        ok, text, out = call("audio_separate", path=mixfile, quality=quality, slot=1)
        wall = time.time() - t0
        row = {"ok": ok}
        if not ok:
            row["error"] = text[:300]
            report["models"][quality] = row
            continue
        row.update({"model": out.get("model"), "seconds": out.get("seconds"), "wall_seconds": round(wall, 1),
                    "seconds_per_minute": round(float(out.get("seconds", wall)) * 60 / seconds, 1), "stems": {}})
        for t in out.get("tracks", []):
            est = render("sep-" + quality.lower() + "-" + t["stem"], solo=[t["machine"]])
            if est is None:
                continue
            m = min(n, est.shape[-1])
            entry = {"level_db": db(est)}
            if t["stem"] in refs:
                entry["sdr_db"] = None if (v := sdr(refs[t["stem"]][:, :m], est[:, :m])) is None else round(v, 2)
            row["stems"][t["stem"]] = entry
        report["models"][quality] = row
        call("undo")
except Exception as e:  # the check reports, it does not fail the run
    print("quality check stopped:", e)
    report["error"] = str(e)
finally:
    try:
        proc.stdin.close()
        proc.wait(30)
    except Exception:
        proc.kill()

print("\n| model | stem | SDR dB | level dBFS | seconds per minute of audio |\n|---|---|---|---|---|")
for q, row in report["models"].items():
    if not row.get("ok"):
        print(f"| {q} | - | failed: {row.get('error', '')[:60]} | | |")
        continue
    for stem, e in row["stems"].items():
        print(f"| {q} ({row['model']}) | {stem} | {e.get('sdr_db', 'n/a')} | {e['level_db']} | {row['seconds_per_minute']} |")
if args.report:
    json.dump(report, open(args.report, "w"), indent=1)
sys.exit(0)
