#!/usr/bin/env python3
"""The list of the AW5 listening pack (round 5, docs/specs/neo-audio.md, AW5 as built): the eight clips of round 4 at the render tempo each had there.

    aw5packlist.py ITEMS_DIR list.txt readme.txt

AutoWarpEval --pack5 renders each twice (plain auto-warp and auto-warp to grid); aw5shuffle.py makes the blind pack of the renders.
"""
import json, sys

CLIPS = [("cd_csic.1995_cuareim_03", 140), ("gs_04_BN3-154-E_solo", 105), ("gmd_drummer5_session2_2", 110), ("aam_2458", 130),
         ("aam_1006", 105), ("gs_04_Funk1-114-Ab_comp", 140), ("gs_05_Rock1-130-A_solo", 120), ("aam_1066", 100)]

d, lst, rd = sys.argv[1:4]
rows = []
readme = ["Auto-warp listening pack, round 5 (AW5): 15 s excerpts of the round-4 clips, played at a render tempo against a click on the grid (a higher, louder tick on each bar line), warped with Signalsmith Stretch", ""]
for name, bpm in CLIPS:
    m = json.load(open(f"{d}/{name}.json"))
    rows.append(f"{d}/{name}.wav\t{bpm}")
    readme.append(f"{name}: {m['source']}; licence {m['licence']}; render tempo {bpm} BPM")
open(lst, "w").write("\n".join(rows) + "\n")
open(rd, "w").write("\n".join(readme) + "\n")
print(len(rows), "rows")
