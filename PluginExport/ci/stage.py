#!/usr/bin/env python3
"""Copies the two NativeAOT libraries next to every module clap-wrapper built (the .clap, the .vst3 and the .component find
their libraries in their own folder): python3 stage.py <assets folder> <dsp publish folder> <editor publish folder>."""
import glob, os, platform, shutil, sys

assets, dsp, ed = (os.path.abspath(p) for p in sys.argv[1:4])
libs = []
for folder in (dsp, ed):
    libs += [f for f in glob.glob(os.path.join(folder, "*")) if os.path.splitext(f)[1] in (".so", ".dylib", ".dll")]
print("libraries:", [os.path.basename(l) for l in libs])
targets = set()
for root, dirs, files in os.walk(assets):
    for name in list(dirs) + files:
        path = os.path.join(root, name)
        ext = os.path.splitext(name)[1]
        if ext in (".clap", ".vst3", ".component"):
            if os.path.isdir(path):
                mac = os.path.join(path, "Contents", "MacOS")
                if os.path.isdir(mac): targets.add(mac)
                else:
                    for d in glob.glob(os.path.join(path, "Contents", "*-*")): targets.add(d)
            else:
                targets.add(os.path.dirname(path))
targets.add(assets)
for t in sorted(targets):
    for l in libs:
        shutil.copy2(l, t)
    print("staged into", t)
