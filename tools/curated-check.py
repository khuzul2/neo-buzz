#!/usr/bin/env python3
# Fork (docs/specs/mcp-server.md §12.9, U3): the curated manifests against the machines themselves, and (U6, §12.13)
# the manifests generated from the machines' source (tools/source_manifests.py), which must agree with the machine
# as much. For every curated or generated manifest in Machines/Manifests whose gear the server has, add the machine, read
# its value texts (machine_describe value_table) and check each curated parameter:
#  - its range is the machine's (a curated manifest of another version is stale);
#  - where the manifest keeps the machine's own texts (own_texts, the default) and maps to a unit, every
#    text that reads as a number in that unit agrees with the mapping to a step of the text's last digit
#    (some machines truncate) or 1 %;
#  - choices kept from the machine are its texts.
# A curated mapping that overrides the machine's texts (own_texts false: texts that are bare numbers,
# or wrong) rests on its source instead and is not compared. Machines that don't load are reported.
# Runs on rebuzz-headless (Linux, macOS: the Pedal machines) and ReBuzz.exe (Windows: the natives).
#
#   python3 tools/curated-check.py <rebuzz-headless | ReBuzz.exe> [work folder]

import importlib.util
import json
import math
import os
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import machine_infer as mi  # noqa: E402

spec = importlib.util.spec_from_file_location("profiler", os.path.join(HERE, "machine-profiler.py"))
profiler = importlib.util.module_from_spec(spec)
spec.loader.exec_module(profiler)

if len(sys.argv) < 2:
    sys.exit(__doc__ or "usage: curated-check.py <rebuzz-headless | ReBuzz.exe> [work folder]")
exe = sys.argv[1]
work = sys.argv[2] if len(sys.argv) > 2 else tempfile.mkdtemp(prefix="rebuzz-curated-")
root = os.path.dirname(HERE)


def curated_manifests():
    out = {}
    for sub in ("Generators", "Effects"):
        folder = os.path.join(root, "Machines", "Manifests", sub)
        for f in sorted(os.listdir(folder)):
            if f.endswith(".machine.json"):
                with open(os.path.join(folder, f), encoding="utf-8") as h:
                    m = json.load(h)
                if m.get("status") in ("curated", "generated"):
                    out[m["gear"]] = m
    return out


def disagreements(entry, table):
    """The texts a curated entry's mapping doesn't reproduce: [(raw, text, mapped)]."""
    kind, unit = entry.get("kind"), entry.get("unit", "")
    lo, hi = entry["min"], entry["max"]
    bad = []
    if kind in ("enum", "switch"):
        names = entry.get("choices") or (["Off", "On"] if kind == "switch" else [])
        for raw, text in table:
            i = raw - lo
            if kind == "enum" and 0 <= i < len(names) and mi.normalise(text).lower() != mi.normalise(names[i]).lower():
                bad.append((raw, text, names[i]))
        return bad
    if not unit or unit in ("×",):
        return bad
    for raw, text in table:
        p = mi.parse(text)
        if p is None or p[1] != unit:
            continue
        got = mi.value_at(entry, raw, lo, hi)
        if got is None:
            continue
        if math.isinf(p[0]) or math.isinf(got):
            if p[0] != got:
                bad.append((raw, text, got))
            continue
        # A whole step of the text's last digit (some machines truncate: FSM Infector's "%d%%" of raw·100/240), or 1 %.
        if abs(got - p[0]) > max(2.4 * mi._resolution(p), 0.01 * abs(p[0]), 1e-9):
            bad.append((raw, text, round(got, 6)))
    return bad


def main():
    manifests = curated_manifests()
    c = profiler.Client(exe, work, 90)
    installed = {g["name"] for g in c.call("gear_list", limit=1000, include_unhealthy=True).get("gear", [])}
    failures, checked, compared, skipped = [], 0, 0, []
    for gear, m in manifests.items():
        if gear not in installed:
            continue
        try:
            c.call("song_new", discard_unsaved=True)
            try:
                c.call("machine_add", gear=gear, name="M", connect_to="Master")
            except profiler.ToolFailed:
                c.call("machine_add", gear=gear, name="M", connect_to="none")
            # Some natives' texts need the engine's sample rate, which they only have once the engine has run
            # (Oomek Exciter's corners read "1 Hz" before): a beat's render first.
            c.call("render", **{"from": 0, "to": 1, "tail_seconds": 0})
            d = c.call("machine_describe", machine="M", value_table=True, table_points=257)
        except (profiler.ToolFailed, profiler.ServerGone) as e:
            skipped.append(f"{gear}: {str(e)[:160]}")
            if isinstance(e, profiler.ServerGone):
                c.restart()
            continue
        checked += 1
        live = {}
        for p in d.get("parameters", []):
            live[(bool(p.get("per_track")), p["name"], p.get("index"))] = p
        problems = []
        for group, track in (("parameters", False), ("track_parameters", True)):
            for e in m.get(group, []):
                p = live.get((track, e["name"], None if track else e["index"])) or next(
                    (q for (t, n, _), q in live.items() if t == track and n == e["name"]), None)
                if p is None:
                    problems.append(f"{e['name']}: not a {'track ' if track else ''}parameter the tools show")
                    continue
                if not track and p.get("index") != e["index"]:
                    problems.append(f"{e['name']}: index {p.get('index')} on the machine, {e['index']} in the manifest")
                    continue
                if (p.get("min"), p.get("max")) != (e["min"], e["max"]):
                    problems.append(f"{e['name']}: the machine's range is {p.get('min')}…{p.get('max')}, the manifest's {e['min']}…{e['max']}")
                    continue
                if e.get("own_texts", True) is False:
                    continue
                bad = disagreements(e, p.get("table") or [])
                compared += 1
                if bad:
                    problems.append(f"{e['name']}: {len(bad)} texts disagree, e.g. raw {bad[0][0]} reads \"{bad[0][1]}\", the manifest maps it to {bad[0][2]}")
        for x in problems:
            failures.append(f"{gear} {x}")
        print(("ok   " if not problems else "FAIL ") + f"{gear}: {len(m.get('parameters', [])) + len(m.get('track_parameters', []))} parameters"
              + ("" if not problems else ": " + "; ".join(problems)), flush=True)
    c.close()
    for s in skipped:
        print("skip " + s, flush=True)
    print(f"\n{checked} curated and generated machines checked here ({compared} parameters against their own texts), {len(failures)} problems", flush=True)
    sys.exit(1 if failures or checked == 0 else 0)


main()
