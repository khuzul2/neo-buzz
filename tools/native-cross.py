#!/usr/bin/env python3
# Fork (docs/specs/cross-platform.md R1): the classic native machines rebuilt for macOS and Linux
# (ExternalMachines/native, run by the portable host ReBuzzEngine/portable), tested in rebuzz-headless as the Neo machines
# are (docs/specs/mac-engine.md M3/M4):
#
#   machines  each machine of the list that ships (machines.json "natives", "ship": "rebuilt") is in Gear and loads; its
#             parameter table is not empty; and it renders tools/native-compare.py's cases (the defaults, every parameter a
#             quarter and three quarters of the way up its raw range, a quiet input for effects), each probed: no divergence,
#             no non-finite sample. With --ref DIR, each case is compared with the Windows render of the same case that
#             `native-compare.py --save-renders DIR` wrote (the package's build of the machine, which the gate holds to upstream's
#             binary): a case passes when it is bit-identical or within -100 dBFS of it (the null test's line across platforms,
#             cross-platform.md §4.3), else when its level is within 0.5 dB and its third-octave bands (those within 50 dB of its
#             loudest) within 1 dB (the clones' tolerances: R1's compilers, libm and dsplib differ from the Windows build's,
#             and modulation and feedback spread a last-bit difference). Reported per case: how far apart, and which line it met.
#   songs     the classic songs of ExternalMachines/source that use these machines open, keep them as machines (not
#             placeholders) and play to their end: every sample finite, and sound.
#
#   python3 tools/native-cross.py <rebuzz-headless> <work> [--ref DIR] [--only gear,...] [--json out.json] [--no-songs] [--no-machines]

import importlib.util
import json
import math
import os
import shutil
import sys
import time

try:
    import numpy as np
except ImportError:
    np = None

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def _load(name, file):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, file))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


nc = _load("native_compare", "native-compare.py")
nt = nc.nt

TIGHT_DBFS = -100.0
LEVEL_DB = 0.5
BAND_DB = 1.0
BAND_RANGE_DB = 50.0       # bands further than this under the loudest compare nothing
CLASSIC_MAX_SECONDS = 120


def samples_of(path):
    rate, ch, a = nt.read_wav(path)
    x = np.frombuffer(a, dtype=np.float32).astype(np.float64) if np is not None else list(a)
    return rate, ch, x


def third_octave(x, ch, rate):
    """Power (dB) of the mono mix in each third-octave band from 25 Hz to 16 kHz: [(centre Hz, dB)]."""
    mono = x.reshape(-1, ch).mean(axis=1)
    spec = np.abs(np.fft.rfft(mono)) ** 2
    freqs = np.fft.rfftfreq(len(mono), 1.0 / rate)
    bands = []
    for k in range(-16, 14):
        centre = 1000.0 * 2 ** (k / 3.0)
        lo, hi = centre / 2 ** (1 / 6.0), centre * 2 ** (1 / 6.0)
        p = spec[(freqs >= lo) & (freqs < hi)].sum()
        bands.append((centre, 10 * math.log10(p) if p > 0 else -math.inf))
    return bands


def compare_renders(ref_path, got_path):
    """{"line": "identical" | "tight" | "loose" | "outside", "max_diff_dbfs", "level_db", "worst_band_db", "text"}"""
    r_rate, r_ch, a = samples_of(ref_path)
    g_rate, g_ch, b = samples_of(got_path)
    if (r_rate, r_ch, len(a)) != (g_rate, g_ch, len(b)):
        return {"line": "outside", "text": f"format differs: reference {r_rate} Hz x {r_ch}, {len(a) // r_ch} frames; this {g_rate} Hz x {g_ch}, {len(b) // g_ch} frames"}
    if a.tobytes() == b.tobytes():
        return {"line": "identical", "max_diff_dbfs": -math.inf, "text": "identical"}
    d = np.abs(a - b)
    worst = float(d.max())
    max_db = nt.db(worst) if worst > 0 else -math.inf
    if not np.isfinite(worst):
        return {"line": "outside", "max_diff_dbfs": math.inf, "text": "not finite"}
    out = {"max_diff_dbfs": max_db}
    if max_db <= TIGHT_DBFS:
        out.update(line="tight", text=f"{max_db:.1f} dBFS")
        return out
    ea, eb = float(np.mean(a * a)), float(np.mean(b * b))
    level = 10 * math.log10(eb / ea) if ea > 0 and eb > 0 else (0.0 if ea == eb else math.inf)
    ba, bb = third_octave(a, r_ch, r_rate), third_octave(b, g_ch, g_rate)
    loud = max(v for _, v in ba)
    worst_band = 0.0
    for (c, va), (_, vb) in zip(ba, bb):
        if va < loud - BAND_RANGE_DB or math.isinf(va):
            continue
        worst_band = max(worst_band, abs(vb - va) if not math.isinf(vb) else math.inf)
    out.update(level_db=level, worst_band_db=worst_band)
    if abs(level) <= LEVEL_DB and worst_band <= BAND_DB:
        out.update(line="loose", text=f"{max_db:.1f} dBFS apart; level {level:+.2f} dB, bands within {worst_band:.2f} dB")
    else:
        out.update(line="outside", text=f"{max_db:.1f} dBFS apart; level {level:+.2f} dB, a band {worst_band:.2f} dB off")
    return out


def shipped(gear_root):
    """The machines that ship rebuilt (machines.json) and are in Gear as shared libraries."""
    out = []
    for s in nc.natives():
        if s.get("ship") != "rebuilt":
            continue
        folder = os.path.join(gear_root, s["gearFolder"])
        if any(os.path.exists(os.path.join(folder, s["gear"] + e)) for e in (".dylib", ".so")):
            out.append(s)
    return out


def check_finite_render(path):
    _, _, x = samples_of(path)
    return bool(np.isfinite(x).all()), float(np.abs(x).max()) if len(x) else 0.0


def machines(exe, work, gear_root, ref, only, results):
    fails = []
    specs = shipped(gear_root)
    wanted = [s for s in nc.natives() if s.get("ship") == "rebuilt" and (not only or s["gear"] in only)]
    for s in wanted:
        if s not in specs:
            fails.append(f"{s['gear']}: not in Gear")
            print(f"FAIL {s['gear']}: not in Gear", flush=True)
    run = nc.Run(exe, os.path.join(work, "server"))
    for s in [s for s in specs if not only or s["gear"] in only]:
        gear = s["gear"]
        kind = "generator" if s["gearFolder"] == "Generators" else "effect"
        man = nc.manifest_of(os.path.join(gear_root, s["gearFolder"]), gear)
        entry = results.setdefault(gear, {"cases": {}})
        try:
            c = run.client()
            c.call("song_new", discard_unsaved=True, tempo_bpm=nc.TEMPO)
            c.call("machine_add", gear=gear, name="T")
            glob, track = nc.describe(c, "T")[1:]
            entry["parameters"] = len(glob) + len(track)
            if not glob and not track:
                fails.append(f"{gear}: no parameters")
                print(f"FAIL {gear}: no parameters", flush=True)
        except Exception as e:
            fails.append(f"{gear}: does not load: {str(e)[:200]}")
            print(f"FAIL {gear}: does not load: {str(e)[:300]}", flush=True)
            run.restart()
            continue
        for case in nc.case_list(kind, gear):
            t = time.time()
            name = case[0]
            try:
                files = nc.render_case(run, s, [("this", gear)], case, man)
            except Exception as e:
                fails.append(f"{gear} {name}: {str(e)[:200]}")
                print(f"FAIL {gear} {name}: {str(e)[:300]}", flush=True)
                entry["cases"][name] = {"error": str(e)[:200]}
                run.restart()
                continue
            status, path = files["this"]
            rec = {"status": status}
            entry["cases"][name] = rec
            if status != "ok" or not path:
                fails.append(f"{gear} {name}: {status}")
                print(f"FAIL {gear} {name}: {status}", flush=True)
                continue
            finite, peak = check_finite_render(path)
            rec["peak_dbfs"] = nt.db(peak)
            if not finite:
                fails.append(f"{gear} {name}: not finite")
                print(f"FAIL {gear} {name}: not finite", flush=True)
                continue
            line = ""
            refp = os.path.join(ref, gear, name.replace(" ", "_") + ".wav") if ref else None
            if refp and os.path.exists(refp):
                cmp = compare_renders(refp, path)
                rec.update(compare=cmp)
                line = f"  vs Windows: {cmp['line']} ({cmp['text']})"
                if cmp["line"] == "outside":
                    fails.append(f"{gear} {name}: {cmp['text']}")
            elif ref:
                rec["compare"] = {"line": "none", "text": "no reference"}
                fails.append(f"{gear} {name}: no Windows render to compare with")
                line = "  no Windows render"
            print(f"{gear:<24} {name:<24} peak {nc.pfmt(rec['peak_dbfs'])}{line}  ({time.time() - t:.1f} s)", flush=True)
            try:
                os.remove(path)
            except OSError:
                pass
    run.close()
    return fails


def classic_songs(gear_root):
    """Songs under ExternalMachines/source that name one of the shipped machines (the gear's name is in the file's
    MACH section as a library name)."""
    names = [s["gear"] for s in shipped(gear_root)]
    found = []
    base = os.path.join(ROOT, "ExternalMachines", "source")
    for dirpath, _, files in os.walk(base):
        for f in sorted(files):
            if not f.lower().endswith(".bmx"):
                continue
            p = os.path.join(dirpath, f)
            data = open(p, "rb").read()
            used = [n for n in names if n.encode("latin-1") in data]
            if used:
                found.append((p, used))
    return found


def songs(exe, work, gear_root, results):
    fails = []
    found = classic_songs(gear_root)
    if not found:
        fails.append("no classic songs found")
    run = nc.Run(exe, os.path.join(work, "server-songs"))
    audible = 0
    for path, used in found:
        rel = os.path.relpath(path, os.path.join(ROOT, "ExternalMachines", "source")).replace("\\", "/")
        rec = results.setdefault("songs", {}).setdefault(rel, {})
        try:
            c = run.client()
            st = c.call("song_open", path=path, discard_unsaved=True)
            listing = c.call("machine_list").get("machines") or []
            loaded = sorted({m.get("gear") for m in listing if m.get("gear") in used and m.get("health") != "missing"})
            missing = sorted({m.get("gear") for m in listing if m.get("health") == "missing"})
            rec.update(uses=used, loaded=loaded, missing=missing)
            gone = sorted(set(used) - set(loaded))
            if gone:
                fails.append(f"{rel}: {', '.join(gone)} did not load as machines")
                print(f"FAIL {rel}: {', '.join(gone)} did not load (missing: {missing})", flush=True)
                continue
            beats = min(float(st.get("length_beats") or 16), CLASSIC_MAX_SECONDS * (st.get("tempo_bpm") or 126) / 60.0)
            r = c.call("render", **{"from": 0, "to": beats, "tail_seconds": 1.0, "name": "classic"})
            f = r.get("file")
            if not f:
                raise RuntimeError("render wrote no file")
            finite, peak = check_finite_render(f)
            rec.update(peak_dbfs=nt.db(peak), finite=finite)
            # a song whose generators are machines this round does not rebuild (Rout 909, Dynamite 6 ...) plays silence
            # here: only the songs that miss nothing must sound, the others must load their rebuilt machines and stay finite
            ok = finite and (peak > 1e-4 or bool(missing))
            rec["audible"] = peak > 1e-4
            audible += peak > 1e-4
            print(f"{'ok  ' if ok else 'FAIL'} {rel}: {', '.join(used)} loaded; peak {nc.pfmt(nt.db(peak))}" + (f"; not rebuilt here: {', '.join(missing)}" if missing else ""), flush=True)
            if not ok:
                fails.append(f"{rel}: " + ("not finite" if not finite else "silent"))
            try:
                os.remove(f)
            except OSError:
                pass
        except Exception as e:
            fails.append(f"{rel}: {str(e)[:200]}")
            print(f"FAIL {rel}: {str(e)[:300]}", flush=True)
            run.restart()
    run.close()
    if audible < 3:
        fails.append(f"only {audible} classic songs sound (3 are expected)")
    return fails


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    exe, work = os.path.abspath(argv[0]), os.path.abspath(argv[1])
    ref, only, json_out, do_songs, do_machines = None, None, None, True, True
    i = 2
    while i < len(argv):
        a = argv[i]
        if a == "--ref":
            ref = os.path.abspath(argv[i + 1]); i += 2
        elif a == "--only":
            only = set(argv[i + 1].split(",")); i += 2
        elif a == "--json":
            json_out = os.path.abspath(argv[i + 1]); i += 2
        elif a == "--no-songs":
            do_songs = False; i += 1
        elif a == "--no-machines":
            do_machines = False; i += 1
        else:
            raise SystemExit(f"unknown argument {a}")
    if np is None:
        raise SystemExit("numpy is needed (pip install numpy)")
    os.makedirs(work, exist_ok=True)
    gear_root = os.path.join(os.path.dirname(exe), "Gear")
    results, fails = {}, []
    if do_machines:
        fails += machines(exe, work, gear_root, ref, only, results)
    if do_songs:
        fails += songs(exe, work, gear_root, results)
    if json_out:
        with open(json_out, "w") as f:
            json.dump(results, f, indent=1, default=lambda x: None if isinstance(x, float) and not math.isfinite(x) else str(x))
    lines = {}
    for gear, e in results.items():
        if gear == "songs":
            continue
        for case, r in e.get("cases", {}).items():
            line = (r.get("compare") or {}).get("line", "-")
            lines[line] = lines.get(line, 0) + 1
    print(f"\ncases by line met against Windows: {lines}" if ref else "\n(no Windows references given)")
    print(f"{len(fails)} failed" if fails else "all passed")
    for f in fails:
        print("  FAIL " + f)
    if os.environ.get("GITHUB_ACTIONS"):
        kind = "error" if fails else "notice"
        body = f"cases by line met against Windows: {lines}; " + (f"{len(fails)} failed: " + "; ".join(fails[:20]) if fails else "all passed")
        print(f"::{kind} title=native machines (R1)::{body.replace(chr(10), ' ')}")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
