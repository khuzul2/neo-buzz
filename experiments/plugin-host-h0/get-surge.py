#!/usr/bin/env python3
# H0 CI: downloads and installs Surge XT (GPL-3.0) on the runner as a test fixture. It is never
# built into or shipped with anything. Writes the plugins found, one "path|" spec per line, to the
# output file (run-h0.py --surge-list). Needs gh (GH_TOKEN) for the release's asset list.
#
#   python3 get-surge.py <work folder> <output list>

import glob
import json
import os
import re
import subprocess
import sys
import zipfile

work, listfile = sys.argv[1], sys.argv[2]
os.makedirs(work, exist_ok=True)
rel = json.loads(subprocess.check_output(["gh", "release", "view", "--repo", "surge-synthesizer/releases-xt", "--json", "tagName,assets"], text=True))
names = [a["name"] for a in rel["assets"]]
print(f"Surge XT {rel['tagName']}: {len(names)} assets", flush=True)
for n in names:
    print("  " + n)


def pick(*patterns):
    for p in patterns:
        for n in names:
            if re.search(p, n, re.I):
                return n
    return None


def download(name):
    subprocess.check_call(["gh", "release", "download", rel["tagName"], "--repo", "surge-synthesizer/releases-xt", "--pattern", name, "--dir", work, "--clobber"])
    return os.path.join(work, name)


found = []
if sys.platform.startswith("linux"):
    deb = pick(r"linux.*(x64|x86_64|amd64).*\.deb$", r"\.deb$")
    if deb:
        subprocess.check_call(["sudo", "apt-get", "install", "-y", "-q", download(deb)])
    for base in ("/usr/lib/vst3", "/usr/lib/clap", "/usr/local/lib/vst3", "/usr/local/lib/clap"):
        found += sorted(glob.glob(os.path.join(base, "Surge XT*.vst3")) + glob.glob(os.path.join(base, "Surge XT*.clap")))
elif sys.platform == "darwin":
    dmg = pick(r"mac.*\.dmg$", r"\.dmg$")
    if dmg:
        path = download(dmg)
        mnt = os.path.join(work, "mnt")
        subprocess.check_call(["hdiutil", "attach", "-nobrowse", "-mountpoint", mnt, path])
        pkgs = glob.glob(os.path.join(mnt, "*.pkg"))
        print("packages:", pkgs, flush=True)
        for p in pkgs:
            subprocess.check_call(["sudo", "installer", "-pkg", p, "-target", "/"])
        subprocess.call(["hdiutil", "detach", mnt])
    for base in ("/Library/Audio/Plug-Ins/VST3", "/Library/Audio/Plug-Ins/CLAP"):
        found += sorted(glob.glob(os.path.join(base, "Surge XT*.vst3")) + glob.glob(os.path.join(base, "Surge XT*.clap")))
else:
    setup = pick(r"win.*64.*setup\.exe$", r"setup\.exe$")
    if setup:
        subprocess.call([download(setup), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/ALLUSERS"])
    cf = os.environ.get("CommonProgramFiles", r"C:\Program Files\Common Files")
    for base in (os.path.join(cf, "VST3"), os.path.join(cf, "CLAP")):
        found += sorted(glob.glob(os.path.join(base, "Surge XT*.vst3")) + glob.glob(os.path.join(base, "Surge XT*.clap")))
    if not found:
        z = pick(r"win.*64.*plugins?only.*\.zip$", r"win.*64.*\.zip$")
        if z:
            dest = os.path.join(work, "plugins")
            zipfile.ZipFile(download(z)).extractall(dest)
            found = sorted(glob.glob(os.path.join(dest, "**", "Surge XT*.vst3"), recursive=True) + glob.glob(os.path.join(dest, "**", "Surge XT*.clap"), recursive=True))
            # Only the bundles themselves, not the binaries inside them.
            found = [f for f in found if ".vst3" + os.sep not in os.path.normpath(os.path.dirname(f)).lower() + os.sep]
print("Surge XT plugins:", found, flush=True)
with open(listfile, "w", encoding="utf-8") as f:
    for p in found:
        f.write(p + "|\n")
