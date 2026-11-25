#!/usr/bin/env python3
# Fork (rename): the Windows package as people get it is NeoBuzz Live. Checks build.ps1's output in dist\:
#  - the zip is NeoBuzz-Live-<version>-x64.zip (no ReBuzz-*.zip), its one top folder named the same; with
#    -NoZip (a quick package run) the folder alone;
#  - the layout (clean-break.md CB1): the studio is NeoBuzzLive.exe at the package root, an apphost that starts
#    App\\ReBuzz.Studio.dll, with Gear\\, Licenses\\ and the notes beside it; no Classic app, no native machine host, no old machine;
#  - App\\ has the studio, its engine, Avalonia, NAudio, PortAudio built from source (no ASIO), libsndfile, the Link helper and
#    the Pro warp mode; Gear\\ has the Neo machines (their Avalonia builds) and Neo Audio;
#  - README-FIRST.txt says NeoBuzz Live, how to start it, that it's a fork of ReBuzz (its authors and link) and how agents
#    connect; BUILD-INFO.txt names it; ReBuzz's MIT licence ships intact.
#
#   python tools/package-check.py <dist folder> [work folder]

import json
import os
import queue
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import zipfile

dist = sys.argv[1] if len(sys.argv) > 1 else "dist"
work = sys.argv[2] if len(sys.argv) > 2 else tempfile.mkdtemp(prefix="neobuzz-package-")
failures = []


def check(ok, what):
    print(("ok   " if ok else "FAIL ") + what, flush=True)
    if not ok:
        failures.append(what)


def finish():
    print(f"\n{len(failures)} problem(s)" if failures else "\nthe package is NeoBuzz Live")
    for f in failures:
        print("::error title=Package check::" + f)
    sys.exit(1 if failures else 0)


names = sorted(os.listdir(dist))
zips = [n for n in names if n.lower().endswith(".zip")]
folders = [n for n in names if os.path.isdir(os.path.join(dist, n))]
check(not any(n.lower().startswith("rebuzz") for n in names), f"nothing in {dist} is named ReBuzz ({', '.join(names)})")
ours = [n for n in zips if n.startswith("NeoBuzz-Live-") and n.endswith("-x64.zip")]
if zips:
    check(len(ours) == 1 and len(zips) == 1, f"one zip, NeoBuzz-Live-<version>-x64.zip ({', '.join(zips)})")
    if not ours:
        finish()
    top = ours[0][:-len(".zip")]
    with zipfile.ZipFile(os.path.join(dist, ours[0])) as z:
        entries = [e.replace("\\", "/") for e in z.namelist()]
        tops = {e.split("/", 1)[0] for e in entries}
        check(tops == {top}, f"its one top folder is {top} ({', '.join(sorted(tops)[:5])})")
        root = os.path.join(work, "unzipped")
        shutil.rmtree(root, ignore_errors=True)
        z.extractall(root)
    app = os.path.join(root, top)
    print(f"     {ours[0]}: {len(entries)} files, {os.path.getsize(os.path.join(dist, ours[0])) / 1e6:.0f} MB")
else:
    ours = [n for n in folders if n.startswith("NeoBuzz-Live-") and n.endswith("-x64")]
    check(len(ours) == 1, f"no zip (-NoZip): one package folder, NeoBuzz-Live-<version>-x64 ({', '.join(folders)})")
    if not ours:
        finish()
    app = os.path.join(dist, ours[0])

files = set(os.listdir(app))
check("NeoBuzzLive.exe" in files, "NeoBuzzLive.exe is at the package root")
expected = {"NeoBuzzLive.exe", "App", "Gear", "Licenses", "README-FIRST.txt", "BUILD-INFO.txt", "GEAR-INVENTORY.txt"}
check(expected <= files and files <= expected | {"LinkHelper"}, f"the package root is tidy: {', '.join(sorted(files))}")
check(not ({"Classic", "Studio", "bin32", "bin64"} & files), "no Classic\\, Studio\\, bin32\\ or bin64\\ (clean break)")
exe_bytes = open(os.path.join(app, "NeoBuzzLive.exe"), "rb").read()
check(b"App\\ReBuzz.Studio.dll\0" in exe_bytes and b"\0ReBuzz.Studio.dll\0" not in exe_bytes, "NeoBuzzLive.exe is an apphost that starts App\\ReBuzz.Studio.dll")


def read(name):
    try:
        with open(os.path.join(app, name), encoding="utf-8-sig") as f:
            return f.read()
    except OSError:
        return ""


readme = read("README-FIRST.txt")
for text in ("NeoBuzz Live - portable build", "run NeoBuzzLive.exe", "NeoBuzz Live is a fork of ReBuzz, by WDE and the ReBuzz contributors",
             "https://github.com/wasteddesign/ReBuzz", "Preferences > Agent", "%APPDATA%\\NeoBuzz Live", "File types", ".nbl"):
    check(text in readme, f"README-FIRST.txt says \"{text}\"")
check("ReBuzz.exe" not in readme and "Classic" not in readme and ".bmx" not in readme, "README-FIRST.txt names no Classic app and no old song format")
check(read("BUILD-INFO.txt").startswith("NeoBuzz Live build "), "BUILD-INFO.txt names NeoBuzz Live")
licence = read(os.path.join("Licenses", "ReBuzz-LICENSE.txt"))
check("MIT License" in licence and "Copyright (c) 2026 wasteddesign" in licence, "ReBuzz's MIT licence ships intact (Licenses\\ReBuzz-LICENSE.txt)")
notice = read(os.path.join("Licenses", "NOTICE.txt"))
check("NeoBuzz Live and ReBuzz (wasteddesign/ReBuzz)" in notice, "Licenses\\NOTICE.txt names both")

studio = os.path.join(app, "App")
sfiles = set(os.listdir(studio)) if os.path.isdir(studio) else set()
check({"ReBuzz.Studio.dll", "ReBuzz.Studio.runtimeconfig.json", "ReBuzz.Studio.deps.json", "ReBuzz.Engine.dll", "ReBuzz.Host.dll", "Avalonia.Base.dll"} <= sfiles,
      "App\\ has the studio, its engine and host, and Avalonia")
check("ReBuzz.Studio.exe" not in sfiles and "NeoBuzzLive.exe" not in sfiles and "ReBuzz.dll" not in sfiles, "App\\ has no second executable and no Classic app")
check({"NAudio.Asio.dll", "NAudio.WinMM.dll", "NAudio.Midi.dll", "NAudio.Core.dll"} <= sfiles, "App\\ has NAudio (ASIO and WinMM MIDI)")
check("portaudio.dll" in sfiles and "libsndfile-1.dll" in sfiles, "App\\ has portaudio.dll and libsndfile-1.dll")
pa_build = read(os.path.join("Licenses", "PortAudio-BUILD.txt"))
check("MME, DirectSound, WASAPI, WDM-KS" in pa_build and "No ASIO" in pa_build, "PortAudio is built with MME, DirectSound, WASAPI and WDM-KS, and no ASIO")
pa_lic = read(os.path.join("Licenses", "PortAudio-LICENSE.txt"))
check("PortAudio" in pa_lic and "Permission is hereby granted" in pa_lic, "PortAudio's licence text ships (Licenses\\PortAudio-LICENSE.txt)")
check("PortAudio" in notice, "Licenses\\NOTICE.txt names PortAudio")
check("ReBuzzLink.exe" in sfiles, "the Ableton Link helper is beside the studio (the Session View's Link)")
check("neostretch.dll" in sfiles, "neostretch.dll (Neo Audio's Pro warp mode) is beside the studio")
stretch = read(os.path.join("Licenses", "SignalsmithStretch-LICENSE.txt"))
check("Signalsmith" in stretch and "MIT License" in stretch, "Signalsmith Stretch's MIT licence ships (Licenses\\SignalsmithStretch-LICENSE.txt)")
cfg = open(os.path.join(studio, "ReBuzz.Studio.runtimeconfig.json"), encoding="utf-8-sig").read() if "ReBuzz.Studio.runtimeconfig.json" in sfiles else ""
check(re.search(r'"System.Runtime.TieredCompilation.QuickJitForLoops":\s*false', cfg) is not None, "the studio's runtimeconfig has the audio thread's JIT setting")
gear = os.path.join(app, "Gear")
gens = os.listdir(os.path.join(gear, "Generators")) if os.path.isdir(os.path.join(gear, "Generators")) else []
effs = os.listdir(os.path.join(gear, "Effects")) if os.path.isdir(os.path.join(gear, "Effects")) else []
check(any(n.startswith("Neo Poly") and n.endswith(".dll") for n in gens), "Gear has the Neo machines (their Avalonia builds)")
check("Neo Audio.NET.dll" in effs, "Gear has Neo Audio (the audio track), which plays imported audio through libsndfile")
check(os.path.isfile(os.path.join(gear, "NeoPlug", "neoplug.dll")), "Gear\\NeoPlug has the plug-in host behind Neo Plug")
old = [n for n in gens + effs if n.lower().endswith(".dll") and not (n.startswith("Neo ") or n.startswith("Dye ") or n.startswith("Modern Pattern Editor")
                                                                      or n.startswith("NeoBuzz.") or n.startswith("BuzzGUI."))]
check(not old, "Gear holds only Neo machines, the pattern editor and their libraries" + (f" (also: {', '.join(sorted(old)[:12])})" if old else ""))
total = sum(os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(app) for f in fs)
print(f"     the package folder: {total:,} bytes")

finish()
