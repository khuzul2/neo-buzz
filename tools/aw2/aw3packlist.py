#!/usr/bin/env python3
"""The list of the AW3 listening pack v2 (docs/specs/neo-audio.md, AW3 as built): the 8 clips of the AW2 blind test (fixed) and candidates for 8 new held-out ones: test items of
GMD, GuitarSet, Candombe and AAM that are not among the 8, in the order of a hash of the name; AutoWarpEval --pack3 keeps the first 2 per set where AW1 and the build differ.

    aw3packlist.py ITEMS_DIR list.txt readme.txt
"""
import json, sys, os, glob
from common import fnv

FIXED = [("gmd_drummer5_session2_1", 100), ("gmd_drummer5_session2_2", 110), ("gs_04_BN3-154-E_solo", 105), ("gs_04_Funk3-112-C#_comp", 115),
         ("cd_csic.1995_ansina2_03", 90), ("cd_csic.1995_cuareim_03", 140), ("aam_1006", 105), ("aam_1066", 100)]
SETS = {"gmd": "gmd_", "guitarset": "gs_", "candombe": "cd_", "aam": "aam_"}
d, lst, rd = sys.argv[1:4]
rows, readme = [], ["Auto-warp listening pack v2 (AW3): 40 s windows of held-out clips (never trained on), played at a render tempo against a click on the grid (a higher, louder tick on each bar line), warped with Signalsmith Stretch", ""]
def setof(name):
    for s, pre in SETS.items():
        if name.startswith(pre):
            return s
for name, bpm in FIXED:
    m = json.load(open(f"{d}/{name}.json")); assert m["split"] == "test", name
    rows.append(f"{d}/{name}.wav\t{bpm}\tfixed\t{setof(name)}")
    readme.append(f"{name}: {m['source']}; licence {m['licence']}; render tempo {bpm} BPM")
fixed = {n for n, _ in FIXED}
cands = []
for j in glob.glob(f"{d}/*.json"):
    name = os.path.basename(j)[:-5]
    m = json.load(open(j))
    if m["split"] != "test" or name in fixed or not setof(name):
        continue
    cands.append((fnv(name), name, m))
for _, name, m in sorted(cands):
    bpm = 90 + 10 * (fnv(name + "t") % 6)
    rows.append(f"{d}/{name}.wav\t{bpm}\tcand\t{setof(name)}")
    readme.append(f"(candidate) {name}: {m['source']}; licence {m['licence']}; render tempo {bpm} BPM")
open(lst, "w").write("\n".join(rows) + "\n"); open(rd, "w").write("\n".join(readme) + "\n")
print(len(rows), "rows")
