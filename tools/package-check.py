#!/usr/bin/env python3
# Fork (rename): the Windows package as people get it is NeoBuzz Live. Checks build.ps1's output in dist\:
#  - the zip is NeoBuzz-Live-<version>-x64.zip (no ReBuzz-*.zip), its one top folder named the same; with
#    -NoZip (a quick package run) the folder alone;
#  - the layout (build.ps1 -StudioMain, ux-overhaul.md P6c): the studio is NeoBuzzLive.exe at the package root and the WPF
#    app is NeoBuzz Live Classic, Classic\\NeoBuzzLiveClassic.exe, in its own folder (they share assembly names); with
#    -StudioMain:$false, the WPF app is NeoBuzzLive.exe at the root and the studio is Studio\\NeoBuzzLiveStudio.exe;
#  - the WPF app's exe sits beside ReBuzz.dll and its runtimeconfig (the assembly keeps its name, which
#    machines, plugins and tests load), with no ReBuzz.exe and no update checker (BuzzGUI.BuzzUpdate.dll);
#  - README-FIRST.txt says NeoBuzz Live, how to start it, that it's a fork of ReBuzz (its authors and link),
#    and the `claude mcp add neobuzz-live` line; BUILD-INFO.txt names it; ReBuzz's MIT licence ships intact;
#  - the WPF app's exe, started from the package (unzipped into a fresh folder) with --agent, answers MCP on stdio as
#    neobuzz-live ("NeoBuzz Live"), says so on stderr, and exits when stdin closes.
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
switched = os.path.isdir(os.path.join(app, "Classic"))
wpf_dir = os.path.join(app, "Classic") if switched else app            # the WPF app's folder
wpf_exe_name = "NeoBuzzLiveClassic.exe" if switched else "NeoBuzzLive.exe"
wfiles = set(os.listdir(wpf_dir))
check("NeoBuzzLive.exe" in files, "NeoBuzzLive.exe is at the package root")
if switched:
    check(wpf_exe_name in wfiles and "NeoBuzzLive.exe" not in wfiles, "NeoBuzz Live Classic is Classic\\NeoBuzzLiveClassic.exe (no second NeoBuzzLive.exe)")
    check("ReBuzz.Studio.dll" not in files and "ReBuzz.dll" not in files, "the root holds no libraries (the studio's are in App\\, the WPF app's in Classic\\)")
    check("Studio" not in files, "no Studio\\ folder in this layout")
    # A tidy root: the exe and a few folders and notes, nothing else (LinkHelper\\ is there when the Link helper was built).
    expected = {"NeoBuzzLive.exe", "App", "Gear", "bin32", "bin64", "Classic", "Licenses", "README-FIRST.txt", "BUILD-INFO.txt", "GEAR-INVENTORY.txt"}
    check(expected <= files and files <= expected | {"LinkHelper"}, f"the package root is tidy: {', '.join(sorted(files))}")
    exe_bytes = open(os.path.join(app, "NeoBuzzLive.exe"), "rb").read()
    check(b"App\\ReBuzz.Studio.dll\0" in exe_bytes and b"\0ReBuzz.Studio.dll\0" not in exe_bytes, "NeoBuzzLive.exe is an apphost that starts App\\ReBuzz.Studio.dll")
else:
    check(wpf_exe_name in wfiles, "the WPF app is NeoBuzzLive.exe (the preview layout)")
check("ReBuzz.exe" not in wfiles and "ReBuzz.exe" not in files, "no ReBuzz.exe")
check("BuzzGUI.BuzzUpdate.dll" not in wfiles and "BuzzGUI.BuzzUpdate.dll" not in files, "no update checker (upstream's offered upstream ReBuzz's installer)")
check({"ReBuzz.dll", "ReBuzz.runtimeconfig.json", "ReBuzz.deps.json"} <= wfiles, "the WPF app is beside ReBuzz.dll, its runtimeconfig and deps (the assembly keeps its name)")
check(os.path.isfile(os.path.join(wpf_dir, "bin64", "ReBuzzEngine64.exe")), "the engine hosts keep their names (bin64\\ReBuzzEngine64.exe)")
check(os.path.isdir(os.path.join(wpf_dir, "Themes", "Default")), "the WPF app has its Themes (the install check needs them)")


def read(name):
    try:
        with open(os.path.join(app, name), encoding="utf-8-sig") as f:
            return f.read()
    except OSError:
        return ""


readme = read("README-FIRST.txt")
agent_exe = "Classic\\" + wpf_exe_name if switched else wpf_exe_name
for text in ("NeoBuzz Live - portable build", "run NeoBuzzLive.exe", "NeoBuzz Live is a fork of ReBuzz, by WDE and the ReBuzz contributors",
             "https://github.com/wasteddesign/ReBuzz", 'claude mcp add neobuzz-live -- "<this folder>\\' + agent_exe + '" --agent'):
    check(text in readme, f"README-FIRST.txt says \"{text}\"")
check("ReBuzz.exe" not in readme and "ReBuzz (fork)" not in readme, "README-FIRST.txt doesn't call the app ReBuzz")
check(read("BUILD-INFO.txt").startswith("NeoBuzz Live build "), "BUILD-INFO.txt names NeoBuzz Live")
licence = read(os.path.join("Licenses", "ReBuzz-LICENSE.txt"))
check("MIT License" in licence and "Copyright (c) 2026 wasteddesign" in licence, "ReBuzz's MIT licence ships intact (Licenses\\ReBuzz-LICENSE.txt)")
check("NeoBuzz Live and ReBuzz (wasteddesign/ReBuzz)" in read(os.path.join("Licenses", "NOTICE.txt")), "Licenses\\NOTICE.txt names both")

# The studio (docs/specs/ux-overhaul.md P6): the main app at the root (or Studio\\ in the preview layout), with its own binaries and Gear,
# PortAudio built from source (no ASIO) with its licence, the native machine hosts and libraries it shares, and the licences and
# notes that say so. (Started by tools/studio-windows-smoke.py.)
studio = os.path.join(app, "App") if switched else os.path.join(app, "Studio")     # the studio's program files
sroot = app if switched else studio                                              # where its Gear, bin32 and bin64 are
sfiles = set(os.listdir(studio)) if os.path.isdir(studio) else set()
check(bool(sfiles) and os.path.isdir(studio), "the studio is in the package")
if sfiles:
    studio_exe = "NeoBuzzLive.exe" if switched else "NeoBuzzLiveStudio.exe"
    check((studio_exe in files if switched else studio_exe in sfiles) and "ReBuzz.Studio.exe" not in sfiles, f"the studio's executable is {studio_exe}")
    check({"ReBuzz.Studio.dll", "ReBuzz.Studio.runtimeconfig.json", "ReBuzz.Studio.deps.json", "ReBuzz.Engine.dll", "ReBuzz.Host.dll", "Avalonia.Base.dll"} <= sfiles,
          "Studio\\ has the studio, its engine and host, and Avalonia")
    check("ReBuzz.dll" not in sfiles and "WindowsBase.dll" not in sfiles and "PresentationFramework.dll" not in sfiles and "NeoBuzzLive.exe" not in sfiles,
          "the studio's folder doesn't hold the WPF app's files (the two can't share a folder)")
    check({"NAudio.Asio.dll", "NAudio.WinMM.dll", "NAudio.Midi.dll", "NAudio.Core.dll"} <= sfiles, "Studio\\ has NAudio (ASIO and WinMM MIDI)")
    check("portaudio.dll" in sfiles and "libsndfile-1.dll" in sfiles, "Studio\\ has portaudio.dll and libsndfile-1.dll")
    sread = lambda n: open(os.path.join(studio, n), encoding="utf-8-sig", errors="replace").read() if os.path.isfile(os.path.join(studio, n)) else ""
    pa_build = read(os.path.join("Licenses", "PortAudio-BUILD.txt")) if switched else sread("PortAudio-BUILD.txt")
    check("MME, DirectSound, WASAPI, WDM-KS" in pa_build and "No ASIO" in pa_build, "PortAudio is built with MME, DirectSound, WASAPI and WDM-KS, and no ASIO")
    pa_lic = read(os.path.join("Licenses", "PortAudio-LICENSE.txt"))
    check("PortAudio" in pa_lic and "Permission is hereby granted" in pa_lic, "PortAudio's licence text ships (Licenses\\PortAudio-LICENSE.txt)")
    check("ReBuzzLink.exe" in sfiles, "the Ableton Link helper is beside the studio (the Session View's Link)")
    check("PortAudio" in read(os.path.join("Licenses", "NOTICE.txt")), "Licenses\\NOTICE.txt names PortAudio")
    cfg = sread("ReBuzz.Studio.runtimeconfig.json")
    check(re.search(r'"System.Runtime.TieredCompilation.QuickJitForLoops":\s*false', cfg) is not None, "the studio's runtimeconfig has the audio thread's JIT setting")
    check(os.path.isfile(os.path.join(sroot, "bin64", "ReBuzzEngine64.exe")) and os.path.isfile(os.path.join(sroot, "bin32", "ReBuzzEngine32.exe")),
          "the native machine hosts are beside the studio's exe (bin32, bin64)")
    sgear = os.path.join(sroot, "Gear")
    neo = [n for n in os.listdir(os.path.join(sgear, "Generators"))] if os.path.isdir(os.path.join(sgear, "Generators")) else []
    check(any(n.startswith("Neo Poly") and n.endswith(".dll") for n in neo), "the studio's Gear has the Neo machines (their Avalonia builds)")
    check(os.path.isfile(os.path.join(sgear, "Effects", "Neo Audio.NET.dll")), "the studio's Gear has Neo Audio (the audio track), which plays imported audio through libsndfile")
    check(os.path.isdir(os.path.join(wpf_dir, "Gear", "Generators")), "the WPF app's Gear is still there")
    check("%APPDATA%\\NeoBuzz Live" in readme and ("NeoBuzz Live Classic" in readme if switched else "NeoBuzz Live Studio (preview)" in readme),
          "README-FIRST.txt explains the two apps and where the studio keeps its settings")
    if switched:
        check("File types" in readme, "README-FIRST.txt names the file types switch in Preferences")

# The app itself, from the package: MCP on stdio.
exe = os.path.join(wpf_dir, wpf_exe_name)
if os.name == "nt" and os.path.exists(exe):
    started = time.time()
    proc = subprocess.Popen([exe, "--agent"], cwd=wpf_dir, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            text=True, encoding="utf-8", errors="replace", bufsize=1)
    lines, errors = queue.Queue(), []
    threading.Thread(target=lambda: [lines.put(l) for l in proc.stdout], daemon=True).start()
    threading.Thread(target=lambda: [errors.append(l.rstrip()) for l in proc.stderr], daemon=True).start()

    def request(id, method, params, seconds):
        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": id, "method": method, "params": params}) + "\n")
        proc.stdin.flush()
        end = time.time() + seconds
        while time.time() < end:
            try:
                m = json.loads(lines.get(timeout=1))
            except queue.Empty:
                if proc.poll() is not None:
                    break
                continue
            if m.get("id") == id:
                return m
        return {}

    init = request(1, "initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "package-check", "version": "1"}}, 420)
    info = init.get("result", {}).get("serverInfo", {})
    check((info.get("name"), info.get("title")) == ("neobuzz-live", "NeoBuzz Live"),
          f"{wpf_exe_name} --agent answers as {info.get('name')} ({info.get('title')}), {time.time() - started:.0f} s from start")
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
    proc.stdin.flush()
    status = request(2, "tools/call", {"name": "studio_status", "arguments": {}}, 60).get("result", {})
    check(status.get("structuredContent", {}).get("mode") == "headless", "studio_status: headless")
    proc.stdin.close()
    try:
        proc.wait(60)
    except subprocess.TimeoutExpired:
        proc.kill()
    check(proc.returncode == 0, f"it exits when stdin closes ({proc.returncode})")
    check(any(l.startswith("[NeoBuzz Live] Headless agent server ready") for l in errors), "its console banner names NeoBuzz Live")
    if failures:
        print("stderr:\n  " + "\n  ".join(errors[-40:]))
else:
    print("     (not on Windows: the WPF app's exe not started)")

finish()
