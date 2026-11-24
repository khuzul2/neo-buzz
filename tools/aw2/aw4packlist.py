#!/usr/bin/env python3
"""The list of the AW4 listening pack (round 3, docs/specs/neo-audio.md, AW4 as built): the 16 clips of round 2 (the AW3 pack v2) at the render tempo each had there.

    aw4packlist.py ITEMS_DIR list.txt readme.txt

AutoWarpEval --pack4 renders each twice (the new source and AW2 as shipped); aw4shuffle.py makes the blind pack of the renders.
"""
import json, sys

CLIPS = [("gmd_drummer5_session2_1", 100), ("gmd_drummer5_session2_2", 110), ("gs_04_BN3-154-E_solo", 105), ("gs_04_Funk3-112-C#_comp", 115),
         ("cd_csic.1995_ansina2_03", 90), ("cd_csic.1995_cuareim_03", 140), ("aam_1006", 105), ("aam_1066", 100),
         ("gs_05_Rock1-130-A_solo", 120), ("gs_04_Funk1-114-Ab_comp", 140), ("aam_2362", 110), ("aam_2458", 130),
         ("gmd_drummer9_session1_19", 110), ("gmd_drummer9_session1_18", 140), ("cd_zavala.muniz.2014_54", 110), ("cd_zavala.muniz.2014_50", 110)]

d, lst, rd = sys.argv[1:4]
rows = []
readme = ["Auto-warp listening pack, round 3 (AW4): 40 s windows of held-out clips, played at a render tempo against a click on the grid (a higher, louder tick on each bar line), warped with Signalsmith Stretch", ""]
for name, bpm in CLIPS:
    m = json.load(open(f"{d}/{name}.json"))
    rows.append(f"{d}/{name}.wav\t{bpm}")
    readme.append(f"{name}: {m['source']}; licence {m['licence']}; render tempo {bpm} BPM")
open(lst, "w").write("\n".join(rows) + "\n")
open(rd, "w").write("\n".join(readme) + "\n")
print(len(rows), "rows")
