#!/usr/bin/env python3
# Fork (docs/specs/mcp-server.md §12.3, §12.11, U4): the curated machines' factory presets, on the machines
# themselves, through the agent server, in the spirit of the Neo preset test (machines.md §6). For every
# Machines/Manifests/<kind>/<Gear>.prs.xml whose gear the server has, each preset:
#  - loads through preset_load (with its comment) and sets the machine to exactly the file's values;
#  - sounds: a generator plays notes (a chord on a polyphonic one, a line on a monophonic one, hits on a drum),
#    an effect gets Neo Poly playing chords and a bass line;
#  - at a consistent level: a generator's loudest 400 ms between -32 and -8 LUFS, an effect's loudness within
#    -12 … +6 dB of its input's (a mono native effect, which ReBuzz feeds left + right summed, 6 dB more: its quirk);
#    and the machine's presets within 14 LU of each other;
#  - with its sample peak below -1 dBFS, and no NaN (the engine's guard would list it as diverged).
# With --theirs, the presets the curated machines ship themselves (Pedal Juno106's, SH101's, Drum Matrix's…)
# are measured too and reported, not judged. Runs on rebuzz-headless (Linux, macOS: the Pedal machines) and
# ReBuzz.exe (Windows: the natives).
#
#   python3 tools/preset-check.py <rebuzz-headless | ReBuzz.exe> [work folder] [--theirs]

import importlib.util
import json
import math
import os
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
spec = importlib.util.spec_from_file_location("profiler", os.path.join(HERE, "machine-profiler.py"))
profiler = importlib.util.module_from_spec(spec)
spec.loader.exec_module(profiler)

args = [a for a in sys.argv[1:] if not a.startswith("--")]
theirs = "--theirs" in sys.argv
if not args:
    sys.exit("usage: preset-check.py <rebuzz-headless | ReBuzz.exe> [work folder] [--theirs]")
exe = args[0]
work = args[1] if len(args) > 1 else tempfile.mkdtemp(prefix="rebuzz-presets-")
root = os.path.dirname(HERE)

PEAK_MAX = -1.0                   # dBFS, sample peak
GEN_LOUD = (-32.0, -8.0)          # LUFS, the loudest 400 ms (momentary max) of a generator's phrase
FX_GAIN = (-12.0, 6.0)            # dB, an effect's integrated loudness over its input's
SPREAD = 14.0                     # LU between a machine's quietest and loudest preset
SILENT = -50.0                    # LUFS: below this, it doesn't sound
BEATS = 8


def factory_files():
    """Our factory preset files beside the curated manifests: gear -> (path, kind, manifest)."""
    out = {}
    for sub in ("Generators", "Effects"):
        folder = os.path.join(root, "Machines", "Manifests", sub)
        for f in sorted(os.listdir(folder)):
            if f.endswith(".prs.xml"):
                gear = f[:-len(".prs.xml")]
                with open(os.path.join(folder, gear + ".machine.json"), encoding="utf-8") as h:
                    out[gear] = (os.path.join(folder, f), sub, json.load(h))
    return out


def curated_without_presets():
    out = {}
    for sub in ("Generators", "Effects"):
        folder = os.path.join(root, "Machines", "Manifests", sub)
        for f in sorted(os.listdir(folder)):
            if f.endswith(".machine.json") and not os.path.exists(os.path.join(folder, f[:-len(".machine.json")] + ".prs.xml")):
                with open(os.path.join(folder, f), encoding="utf-8") as h:
                    m = json.load(h)
                if m.get("status") == "curated":
                    out[m["gear"]] = (None, sub, m)
    return out


def read_presets(path):
    """[(name, comment, {key: value})] as ReBuzz's serializer reads the file: global parameters keyed (1, index),
    track parameters (2, name) (machine_describe gives track parameters no index)."""
    tree = ET.parse(path)
    out = []
    for item in tree.getroot().findall("Item"):
        p = item.find("Preset")
        values = {}
        for q in p.find("Parameters").findall("Parameter"):
            group = int(q.get("Group"))
            values[(1, int(q.get("Index"))) if group == 1 else (2, q.get("Name"))] = int(q.get("Value"))
        out.append((item.get("Key"), (p.findtext("Comment") or "").strip(), values))
    return out


def num(x):
    return x if isinstance(x, (int, float)) and math.isfinite(x) else None


def notes_for(manifest):
    """A phrase for a generator: a chord then a line (polyphonic), a line (monophonic), or hits (drums, unpitched)."""
    n = manifest.get("notes") or {}
    poly = (n.get("polyphony") or 1) > 1
    drum = manifest.get("type") in ("drums", "drum") or "kick" in manifest["gear"].lower()
    notes = []
    if drum:
        for b in range(BEATS):
            notes.append({"pitch": 36, "start": b, "length": 0.5, "velocity": 100})
    elif poly:
        for start, chord in ((0, (48, 55, 60, 64)), (2, (53, 57, 60, 65)), (4, (55, 59, 62, 67)), (6, (48, 55, 60, 64))):
            for p in chord:
                notes.append({"pitch": p, "start": start, "length": 1.75, "velocity": 100})
    else:
        line = (36, 36, 43, 48, 46, 36, 39, 41)
        for b, p in enumerate(line):
            notes.append({"pitch": p, "start": b, "length": 0.75, "velocity": 100})
    return notes


def build(c, gear, kind):
    """A fresh song: the machine as "M" (an effect fed by Neo Poly as "Src")."""
    c.call("song_new", discard_unsaved=True)
    if kind == "Effects":
        c.call("machine_add", gear="Neo Poly", name="Src")
        # Chords and a bass line peaking near -10 dBFS (-20 LUFS): a part's level in a mix, with room for boosts.
        c.call("param_set", machine="Src", changes=[{"param": "Volume", "value": "-12 dB"}, {"param": "Amp release", "value": "80 ms"}])
        c.call("machine_add", gear=gear, name="M", insert_after="Src")
        c.call("pattern_create", machine="Src", name="00", length_beats=BEATS)
        notes = []
        for start, chord in ((0, (48, 60, 64, 67)), (2, (53, 60, 65, 69)), (4, (55, 62, 67, 71)), (6, (43, 59, 62, 67))):
            for p in chord:
                notes.append({"pitch": p, "start": start, "length": 1.0, "velocity": 100})
        c.call("notes_write", machine="Src", pattern="00", notes=notes)
        c.call("arrangement_place", machine="Src", pattern="00", at=0)
    else:
        c.call("machine_add", gear=gear, name="M", connect_to="Master")


def phrase(c, manifest):
    c.call("pattern_create", machine="M", name="00", length_beats=BEATS)
    c.call("notes_write", machine="M", pattern="00", notes=notes_for(manifest))
    c.call("arrangement_place", machine="M", pattern="00", at=0)


def raw_values(c):
    """The machine's raw values now, keyed as read_presets keys them (track 0)."""
    d = c.call("machine_describe", machine="M")
    out = {}
    for p in d.get("parameters", []):
        out[(2, p.get("name")) if p.get("per_track") else (1, p.get("index"))] = (p.get("raw"), p.get("name"))
    return out


def measure(c, kind):
    probes = ["M", "Src"] if kind == "Effects" else ["M"]
    r = profiler.render(c, 0, BEATS, 1.5, probes=probes)
    by = {p["machine"]: p.get("measure") or {} for p in r.get("probes", [])}
    return r, by.get("M", {}), by.get("Src", {})


def main():
    files = factory_files()
    if theirs:
        files.update({g: v for g, v in curated_without_presets().items() if g not in files})
    c = profiler.Client(exe, work, 120)
    installed = {g["name"] for g in c.call("gear_list", limit=1000, include_unhealthy=True).get("gear", [])}
    failures, rows, checked = [], [], 0
    for gear, (path, kind, manifest) in sorted(files.items()):
        if gear not in installed:
            continue
        ours = path is not None
        try:
            build(c, gear, kind)
            if kind == "Generators":
                phrase(c, manifest)
            names = [(p["name"], p.get("comment", "")) for p in c.call("preset_list", machine="M").get("presets", [])]
            if ours:
                presets = read_presets(path)
                listed = {n for n, _ in names}
                for name, _, _ in presets:
                    if name not in listed:
                        failures.append(f"{gear}: preset_list doesn't list \"{name}\" (is the file beside the machine?)")
            else:
                presets = [(n, cm, None) for n, cm in names if n != "<default>"]
                if not presets:
                    rows.append((gear, "-", "no presets of its own", "", "", "", "own"))
                    continue
        except (profiler.ToolFailed, profiler.ServerGone) as e:
            failures.append(f"{gear}: {str(e)[:200]}")
            if isinstance(e, profiler.ServerGone):
                c.restart()
            continue
        levels = []
        for name, comment, values in presets:
            problems = []
            try:
                r = c.call("preset_load", machine="M", preset=name)
                if ours and comment and r.get("comment", "") != comment:
                    problems.append(f"comment \"{r.get('comment', '')}\"")
                if values is not None:
                    now = raw_values(c)
                    for key, want in values.items():
                        got = now.get(key)
                        if got is not None and got[0] != want:
                            problems.append(f"{got[1]} is raw {got[0]}, the preset says {want}")
                rr, out, src = measure(c, kind)
            except (profiler.ToolFailed, profiler.ServerGone) as e:
                failures.append(f"{gear} / {name}: {str(e)[:200]}")
                if isinstance(e, profiler.ServerGone):
                    c.restart()
                    break
                continue
            checked += 1
            peak = num((out.get("peaks") or {}).get("sample_peak_dbfs"))
            tp = num((out.get("peaks") or {}).get("true_peak_dbtp"))
            mom = num((out.get("loudness") or {}).get("momentary_max_lufs"))
            lufs = num((out.get("loudness") or {}).get("integrated_lufs"))
            src_lufs = num((src.get("loudness") or {}).get("integrated_lufs"))
            if rr.get("diverged") or profiler.blown(out):
                problems.append("diverged: " + profiler.peak_text(out))
            if mom is None or mom < SILENT:
                problems.append("silent")
            if peak is not None and peak >= PEAK_MAX:
                problems.append(f"peak {peak:+.1f} dBFS")
            if kind == "Effects":
                gain = lufs - src_lufs if lufs is not None and src_lufs is not None else None
                level = gain
                # ReBuzz feeds a mono native effect left + right summed: +6 dB at unity is the machine's, not the preset's.
                summed = 6.02 if any("left + right summed" in q for q in manifest.get("quirks", [])) else 0.0
                if gain is None or not FX_GAIN[0] + summed <= gain <= FX_GAIN[1] + summed:
                    problems.append(f"gain {gain if gain is None else round(gain, 1)} dB over its input")
                shown = f"{gain:+.1f} dB" if gain is not None else "?"
            else:
                level = mom
                if mom is None or not GEN_LOUD[0] <= mom <= GEN_LOUD[1]:
                    problems.append(f"loudest {mom if mom is None else round(mom, 1)} LUFS")
                shown = f"{mom:.1f} LUFS" if mom is not None else "?"
            if level is not None:
                levels.append(level)
            verdict = "ok" if not problems else ("FAIL" if ours else "warn")
            rows.append((gear, name, shown, f"{peak:+.1f}" if peak is not None else "?", f"{tp:+.1f}" if tp is not None else "?",
                         "; ".join(problems), "ours" if ours else "own"))
            print(f"{verdict:4} {gear} / {name}: {shown}, peak {rows[-1][3]} dBFS, true peak {rows[-1][4]} dBTP" +
                  ("" if not problems else " — " + "; ".join(problems)), flush=True)
            if problems and ours:
                failures.append(f"{gear} / {name}: " + "; ".join(problems))
        if levels and ours and max(levels) - min(levels) > SPREAD:
            failures.append(f"{gear}: its presets' levels spread over {max(levels) - min(levels):.1f} LU (at most {SPREAD})")
        profiler.forget_renders(c)
    c.close()
    with open(os.path.join(work, "presets.json"), "w", encoding="utf-8") as h:
        json.dump([dict(zip(("gear", "preset", "level", "peak_dbfs", "true_peak_dbtp", "problems", "whose"), r)) for r in rows], h, indent=1)
    print(f"\n{checked} presets checked here, {len(failures)} problems", flush=True)
    for f in failures:
        print("FAIL " + f, flush=True)
    sys.exit(1 if failures or checked == 0 else 0)


main()
