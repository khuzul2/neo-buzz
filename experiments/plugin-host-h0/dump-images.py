#!/usr/bin/env python3
# H0 CI: prints the GUI snapshots under a folder as small base64 PNGs between markers, so a run can
# be read from its log alone (macOS: sips makes the thumbnails).
#   python3 dump-images.py <folder>
import base64
import glob
import os
import subprocess
import sys
import tempfile

for path in sorted(glob.glob(os.path.join(sys.argv[1], "**", "*.png"), recursive=True)):
    thumb = os.path.join(tempfile.gettempdir(), "thumb.png")
    if subprocess.call(["sips", "-Z", "480", path, "--out", thumb], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) != 0:
        continue
    data = base64.b64encode(open(thumb, "rb").read()).decode()
    print(f"=====IMG {os.path.relpath(path, sys.argv[1])} {len(data)}=====")
    for i in range(0, len(data), 4000):
        print(data[i:i + 4000])
    print("=====END=====")
