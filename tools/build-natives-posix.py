#!/usr/bin/env python3
# Fork (docs/specs/cross-platform.md R1): the 17 open-source classics as shared libraries for macOS and Linux, from
# the same `natives` list of ExternalMachines/machines.json that tools/build-natives.ps1 reads on Windows (sources,
# defines, libraries), with Buzztrax's own windef, mdk and dsplib. Output: <out>/Gear/<Generators|Effects>/<Name>.dylib
# (macOS) or .so (Linux), plus the files the list names (presets, docs), and <out>/natives.json (what was built).
#
#   python3 tools/build-natives-posix.py --out <folder> [--cxx c++] [--only "FSM Infector"] [--keep-going]
#
# Floating point: -ffp-contract=off (clang on arm64 fuses a*b+c into FMAs by default) and no fast-math, so the
# arithmetic is the Windows build's wherever the library functions agree (sin, pow and exp are the C library's).
import argparse
import json
import os
import platform
import shutil
import subprocess
import sys
import tempfile

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ap = argparse.ArgumentParser()
ap.add_argument("--out", required=True)
ap.add_argument("--cxx", default=os.environ.get("CXX", "c++"))
ap.add_argument("--only", action="append", default=[], help="build only this machine (its gear name)")
ap.add_argument("--keep-going", action="store_true", help="build the rest when one fails")
ap.add_argument("--arch", default=None, help="macOS: -arch value (default: the machine's)")
ap.add_argument("--no-notice", action="store_true", help="don't write Licenses/NOTICE.txt")
args = ap.parse_args()

manifest = json.load(open(os.path.join(root, "ExternalMachines", "machines.json"), encoding="utf-8-sig"))
entry = next(m for m in manifest["machines"] if "natives" in m)
src = os.path.join(root, "ExternalMachines", entry["path"])
natives = entry["natives"]
if args.only:
    natives = [n for n in natives if n["gear"] in args.only]
    if not natives:
        sys.exit("no such machine: " + ", ".join(args.only))

mac = sys.platform == "darwin"
ext = ".dylib" if mac else ".so"
common = os.path.join(src, "common")
out = os.path.abspath(args.out)
obj = os.path.join(out, "obj")
shutil.rmtree(os.path.join(out, "Gear"), ignore_errors=True)
os.makedirs(obj, exist_ok=True)

base = [args.cxx, "-O2", "-fPIC", "-ffp-contract=off", "-fno-fast-math", "-fno-strict-aliasing",
        "-Wno-non-virtual-dtor", "-Wno-unknown-pragmas", "-Wno-deprecated", "-Wno-narrowing", "-w",
        "-I" + common, "-I" + os.path.join(common, "windef")]
if args.cxx.endswith("g++") or args.cxx == "c++" and not mac:
    base += ["-fpermissive"]
if mac:
    base += ["-arch", args.arch or ("arm64" if platform.machine() == "arm64" else platform.machine())]
    link_flags = ["-dynamiclib", "-undefined", "dynamic_lookup"]
else:
    link_flags = ["-shared"]


def run(cmd):
    r = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if r.returncode != 0:
        print(" ".join(cmd))
        print(r.stdout[-3000:])
    return r.returncode == 0


def compile_obj(source, dest, defines):
    return run(base + ["-D" + d for d in defines] + ["-c", source, "-o", dest])


# the static libraries every machine of the list may use: the MDK and the open dsplib
statics = {}
for lib, source in (("mdk", os.path.join(common, "mdk", "mdkimp.cpp")), ("dsplib", os.path.join(common, "dsplib", "dsplib.cpp"))):
    dest = os.path.join(obj, lib + ".o")
    if not compile_obj(source, dest, ["NOMINMAX", "_USE_MATH_DEFINES"]):
        sys.exit("could not build " + lib)
    statics[lib] = dest

built, failed = [], []
for n in natives:
    folder = os.path.join(out, "Gear", n["gearFolder"])
    os.makedirs(folder, exist_ok=True)
    mobj = os.path.join(obj, "".join(c if c.isalnum() else "_" for c in n["gear"]))
    os.makedirs(mobj, exist_ok=True)
    defines = list(n.get("defines") or [])
    objs, ok = [], True
    for s in n["sources"]:
        dest = os.path.join(mobj, os.path.splitext(os.path.basename(s))[0] + ".o")
        if not compile_obj(os.path.join(src, n["dir"], s), dest, defines):
            ok = False
            break
        objs.append(dest)
    libs = [statics[l] for l in (n.get("libs") or []) if l in statics]
    target = os.path.join(folder, n["gear"] + ext)
    if ok:
        ok = run(base + link_flags + objs + libs + ["-o", target])
    if ok:
        for f, to in (n.get("files") or {}).items():
            shutil.copyfile(os.path.join(src, n["dir"], f), os.path.join(folder, to))
        built.append(n["gear"])
        print("built", n["gear"])
    else:
        failed.append(n["gear"])
        print("FAILED", n["gear"])
        if not args.keep_going:
            break

# What ships (as build.ps1 decides for the Windows package): a machine whose "ship" is "rebuilt" and whose licence allows it
# ("redistribute" with its text in "licenseTexts") or carries one of the two recorded exceptions (sources with no licence, by the
# user's decision, for private use). The others are built, for the comparison, but kept out of Gear (into <out>/NotShipped).
EXCEPTIONS = ["no licence in source; shipped by user decision 2026-11-06 for private use",
              "no licence in source; shipped by user decision 2026-11-12 for private use (NOT FOR PUBLIC RELEASE AS IS)"]
texts = manifest.get("licenseTexts", {})


def licence_id(licence):
    head = licence.split(" ")[0]
    for suffix in ("-or-later", "-only"):
        if head.endswith(suffix):
            head = head[:-len(suffix)]
    return head


shipped, by_exception, kept_out = [], [], []
for n in natives:
    if n["gear"] not in built:
        continue
    folder = os.path.join(out, "Gear", n["gearFolder"])
    lid = licence_id(n["license"])
    exc = n.get("licenseException")
    ok = n.get("ship") == "rebuilt" and (
        (n.get("redistribute") and lid in texts and not exc) or (exc in EXCEPTIONS))
    if ok:
        shipped.append(n)
        if exc:
            by_exception.append(n["gear"])
    else:
        kept_out.append(n["gear"])
        side = os.path.join(out, "NotShipped", n["gearFolder"])
        os.makedirs(side, exist_ok=True)
        shutil.move(os.path.join(folder, n["gear"] + ext), os.path.join(side, n["gear"] + ext))
        for f, to in (n.get("files") or {}).items():
            shutil.move(os.path.join(folder, to), os.path.join(side, to))

if shipped and not args.no_notice:
    lic = os.path.join(out, "Licenses")
    os.makedirs(lic, exist_ok=True)
    lines = ["Classic Buzz machines rebuilt for this platform", "",
             "Built from the sources in this fork's ExternalMachines folder (neo-buzz repository), with Buzztrax's windef, mdk and dsplib,",
             "each with its licence, its source and upstream, and what this fork changed. A licence's text is in this folder."]
    if by_exception:
        lines += ["", "NOT FOR PUBLIC RELEASE AS IS: " + ", ".join(by_exception) + " are built from sources that carry no licence (no licence file, no",
                  "header), and ship only by an exception: " + "; or ".join(EXCEPTIONS) + ". Before any public release they must be",
                  're-cleared with their author, or left out (machines.json: "ship": "upstream").']
    lines += ["", "ExternalMachines/" + entry["path"], "  upstream: %s (commit %s)" % (entry["origin"], entry["commit"])]
    if entry.get("patches"):
        lines.append("  changed here: " + "; ".join(entry["patches"]))
    used = {}
    for n in shipped:
        lid = licence_id(n["license"])
        if lid in texts:
            used[lid] = texts[lid]["file"]
        lines.append("  Gear/%s/%s%s" % (n["gearFolder"], n["gear"], ext))
        lines.append("    licence: " + (n["license"] + "; text: Licenses/%s.txt" % lid if lid in texts else n["license"] + ": no licence text to ship"))
        if n.get("licenseException") in EXCEPTIONS:
            lines.append("    exception: %s. Its source carries no licence: re-clear it with its author, or leave it out (machines.json: \"ship\": \"upstream\"), before any public release" % n["licenseException"])
        lines.append("    source:  ExternalMachines/%s/%s (%s); upstream's %s" % (entry["path"], n["dir"], ", ".join(n["sources"]), n["dir"]))
        if n.get("fix"):
            lines.append("    fixed:   " + n["fix"])
    for lid, rel in used.items():
        shutil.copyfile(os.path.join(root, "ExternalMachines", rel), os.path.join(lic, lid + ".txt"))
    open(os.path.join(lic, "NOTICE.txt"), "w", encoding="utf-8").write("\n".join(lines) + "\n")

json.dump({"shipped": [n["gear"] for n in shipped], "notShipped": kept_out, "byException": by_exception,
           "built": built, "failed": failed, "platform": sys.platform, "machine": platform.machine(), "compiler": args.cxx},
          open(os.path.join(out, "natives.json"), "w"), indent=1)
shutil.rmtree(obj, ignore_errors=True)
sys.exit(1 if failed else 0)
