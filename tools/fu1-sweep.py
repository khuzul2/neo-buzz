#!/usr/bin/env python3
# FU1-DIAG (temporary): why native-compare's fraction cases are silent for FSM Infector and Jeskola Noise Generator.
# For each: the case as native-compare makes it (every parameter at a fraction), each parameter alone at the fraction,
# every parameter but one, and other fractions for all.   python tools/fu1-sweep.py <NeoBuzzLive.exe> <work>
import importlib.util
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("native_compare", os.path.join(HERE, "native-compare.py"))
nc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(nc)
nt = nc.nt


def peak_of(c, gear, man, changes, label):
    c.call("song_new", discard_unsaved=True, tempo_bpm=nc.TEMPO)
    c.call("song_set", song_end_beats=nc.BEATS)
    c.call("machine_add", gear=gear, name="G")
    if changes:
        c.call("param_set", machine="G", changes=changes, allow_unsafe=True)
    notes = man.get("notes") or {}
    mode = notes.get("input") if notes.get("input") not in (None, "none") else "tracks"
    nc.play_generator(c, "G", {"gear": gear}, (man.get("profile") or {}).get("trigger"), mode)
    r = c.call("render", **{"from": 0, "to": nc.BEATS, "tail_seconds": nc.TAIL, "name": label})
    p = nc.peak_db(r["file"])
    os.remove(r["file"])
    return p


def main(argv):
    exe, work = os.path.abspath(argv[0]), os.path.abspath(argv[1])
    if os.path.exists(work):
        shutil.rmtree(work)
    os.makedirs(work)
    gear_root = os.path.join(os.path.dirname(exe), "Gear", "Generators")
    for gear in ("FSM Infector", "Jeskola Noise Generator"):
        man = nc.manifest_of(gear_root, gear)
        c = nt.Client(exe, os.path.join(work, "server"), gear.replace(" ", "_"))
        try:
            c.initialize()
            c.call("song_new", discard_unsaved=True, tempo_bpm=nc.TEMPO)
            c.call("machine_add", gear=gear, name="G")
            _, glob, track = nc.describe(c, "G")
            params = [("g", p) for p in glob] + [("t", p) for p in track]
            t = time.time()
            print(f"=== {gear}: defaults {nc.fmt(peak_of(c, gear, man, [], 'd'))} dBFS", flush=True)
            for frac in (0.25, 0.75):
                every = nc.raw_changes(glob, track, frac)
                print(f"  all at {frac}: {nc.fmt(peak_of(c, gear, man, every, 'a'))} dBFS", flush=True)
                alone, but = [], []
                for k, (kind, p) in enumerate(params):
                    one = nc.raw_changes([p], [], frac) if kind == "g" else nc.raw_changes([], [p], frac)
                    alone.append(f"{p['name'].strip()} {nc.fmt(peak_of(c, gear, man, one, 'o'))}")
                    rest = [x for j, x in enumerate(every) if j != k]
                    but.append(f"{p['name'].strip()} {nc.fmt(peak_of(c, gear, man, rest, 'b'))}")
                print(f"  {frac} alone: " + "; ".join(alone), flush=True)
                print(f"  {frac} all but: " + "; ".join(but), flush=True)
            others = [f"{f}: {nc.fmt(peak_of(c, gear, man, nc.raw_changes(glob, track, f), 'f'))}" for f in (0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9)]
            print(f"  all at other fractions: " + "; ".join(others) + f" ({time.time() - t:.0f} s)", flush=True)
        except Exception as e:
            print(f"  failed: {str(e)[:400]}", flush=True)
        finally:
            c.close()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
