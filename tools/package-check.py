#!/usr/bin/env python3
# Fork (rename): the Windows package as people get it is NeoBuzz Live. Checks build.ps1's output in dist\:
#  - the zip is NeoBuzz-Live-<version>-x64.zip (no ReBuzz-*.zip), its one top folder named the same; with
#    -NoZip (a quick package run) the folder alone;
#  - the app is NeoBuzzLive.exe, beside ReBuzz.dll and its runtimeconfig (the assembly keeps its name, which
#    machines, plugins and tests load), with no ReBuzz.exe and no update checker (BuzzGUI.BuzzUpdate.dll);
#  - README-FIRST.txt says NeoBuzz Live, how to start it, that it's a fork of ReBuzz (its authors and link),
#    and the `claude mcp add neobuzz-live` line; BUILD-INFO.txt names it; ReBuzz's MIT licence ships intact;
#  - NeoBuzzLive.exe, started from the package (unzipped into a fresh folder), answers MCP on stdio as
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
check("NeoBuzzLive.exe" in files, "the app is NeoBuzzLive.exe")
check("ReBuzz.exe" not in files, "no ReBuzz.exe")
check("BuzzGUI.BuzzUpdate.dll" not in files, "no update checker (upstream's offered upstream ReBuzz's installer)")
check({"ReBuzz.dll", "ReBuzz.runtimeconfig.json", "ReBuzz.deps.json"} <= files, "beside ReBuzz.dll, its runtimeconfig and deps (the assembly keeps its name)")
check(os.path.isfile(os.path.join(app, "bin64", "ReBuzzEngine64.exe")), "the engine hosts keep their names (bin64\\ReBuzzEngine64.exe)")


def read(name):
    try:
        with open(os.path.join(app, name), encoding="utf-8-sig") as f:
            return f.read()
    except OSError:
        return ""


readme = read("README-FIRST.txt")
for text in ("NeoBuzz Live - portable build", "run NeoBuzzLive.exe", "NeoBuzz Live is a fork of ReBuzz, by WDE and the ReBuzz contributors",
             "https://github.com/wasteddesign/ReBuzz", 'claude mcp add neobuzz-live -- "<this folder>\\NeoBuzzLive.exe" --agent'):
    check(text in readme, f"README-FIRST.txt says \"{text}\"")
check("ReBuzz.exe" not in readme and "ReBuzz (fork)" not in readme, "README-FIRST.txt doesn't call the app ReBuzz")
check(read("BUILD-INFO.txt").startswith("NeoBuzz Live build "), "BUILD-INFO.txt names NeoBuzz Live")
licence = read(os.path.join("Licenses", "ReBuzz-LICENSE.txt"))
check("MIT License" in licence and "Copyright (c) 2026 wasteddesign" in licence, "ReBuzz's MIT licence ships intact (Licenses\\ReBuzz-LICENSE.txt)")
check("NeoBuzz Live and ReBuzz (wasteddesign/ReBuzz)" in read(os.path.join("Licenses", "NOTICE.txt")), "Licenses\\NOTICE.txt names both")

# NeoBuzz Live Studio (preview), Studio\ (docs/specs/ux-overhaul.md P6): the studio beside the app, with its own binaries and Gear,
# PortAudio built from source (no ASIO) with its licence, the native machine hosts and libraries it shares, and the licences and
# notes that say so. (Started by tools/studio-windows-smoke.py.)
studio = os.path.join(app, "Studio")
sfiles = set(os.listdir(studio)) if os.path.isdir(studio) else set()
check(bool(sfiles), "Studio\\ is in the package (NeoBuzz Live Studio, preview)")
if sfiles:
    check("NeoBuzzLiveStudio.exe" in sfiles and "ReBuzz.Studio.exe" not in sfiles, "the studio's executable is NeoBuzzLiveStudio.exe")
    check({"ReBuzz.Studio.dll", "ReBuzz.Studio.runtimeconfig.json", "ReBuzz.Studio.deps.json", "ReBuzz.Engine.dll", "ReBuzz.Host.dll", "Avalonia.Base.dll"} <= sfiles,
          "Studio\\ has the studio, its engine and host, and Avalonia")
    check("ReBuzz.dll" not in sfiles and "NeoBuzzLive.exe" not in sfiles, "Studio\\ doesn't hold the WPF app's files (the two can't share a folder)")
    check({"NAudio.Asio.dll", "NAudio.WinMM.dll", "NAudio.Midi.dll", "NAudio.Core.dll"} <= sfiles, "Studio\\ has NAudio (ASIO and WinMM MIDI)")
    check("portaudio.dll" in sfiles and "libsndfile-1.dll" in sfiles, "Studio\\ has portaudio.dll and libsndfile-1.dll")
    sread = lambda n: open(os.path.join(studio, n), encoding="utf-8-sig", errors="replace").read() if os.path.isfile(os.path.join(studio, n)) else ""
    pa_build = sread("PortAudio-BUILD.txt")
    check("MME, DirectSound, WASAPI, WDM-KS" in pa_build and "No ASIO" in pa_build, "PortAudio is built with MME, DirectSound, WASAPI and WDM-KS, and no ASIO")
    check("PortAudio" in sread("PortAudio-LICENSE.txt") and "Permission is hereby granted" in sread("PortAudio-LICENSE.txt"), "PortAudio's licence text ships (Studio\\PortAudio-LICENSE.txt)")
    check("PortAudio" in read(os.path.join("Licenses", "PortAudio-LICENSE.txt")), "and in Licenses\\PortAudio-LICENSE.txt")
    check("PortAudio" in read(os.path.join("Licenses", "NOTICE.txt")), "Licenses\\NOTICE.txt names PortAudio")
    cfg = sread("ReBuzz.Studio.runtimeconfig.json")
    check(re.search(r'"System.Runtime.TieredCompilation.QuickJitForLoops":\s*false', cfg) is not None, "the studio's runtimeconfig has the audio thread's JIT setting")
    check(os.path.isfile(os.path.join(studio, "bin64", "ReBuzzEngine64.exe")) and os.path.isfile(os.path.join(studio, "bin32", "ReBuzzEngine32.exe")),
          "the native machine hosts are beside the studio (Studio\\bin32, Studio\\bin64)")
    sgear = os.path.join(studio, "Gear")
    neo = [n for n in os.listdir(os.path.join(sgear, "Generators"))] if os.path.isdir(os.path.join(sgear, "Generators")) else []
    check(any(n.startswith("Neo Poly") and n.endswith(".dll") for n in neo), "Studio\\Gear has the Neo machines (their Avalonia builds)")
    check(os.path.isdir(os.path.join(app, "Gear", "Generators")), "the app's Gear is still there")
    check("NeoBuzz Live Studio (preview)" in readme and "%APPDATA%\\NeoBuzz Live" in readme, "README-FIRST.txt explains the preview and where it keeps its settings")

# The app itself, from the package: MCP on stdio.
exe = os.path.join(app, "NeoBuzzLive.exe")
if os.name == "nt" and os.path.exists(exe):
    started = time.time()
    proc = subprocess.Popen([exe, "--agent"], cwd=app, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
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
          f"NeoBuzzLive.exe --agent answers as {info.get('name')} ({info.get('title')}), {time.time() - started:.0f} s from start")
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
    print("     (not on Windows: NeoBuzzLive.exe not started)")

finish()
