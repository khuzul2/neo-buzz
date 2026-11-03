#!/usr/bin/env python3
# Plugin census (docs/specs/plugin-host.md §4, H0): which plugins a machine has installed, and which
# ones its Buzz songs load through Polac VST/VSTi. Plain Python 3.8+, no packages; Windows and
# macOS (Linux folders too). It never loads a plugin: it reads file headers and metadata only.
#
# Installed plugins, from the standard folders (or --folder FORMAT=DIR, --root to re-root them):
#  - VST2: Windows: the common VstPlugins / VST2 folders, the registry's VSTPluginsPath (HKLM and
#          HKCU, 64- and 32-bit views) and Polac's own Plugin_Path settings; macOS: /Library and
#          ~/Library Audio/Plug-Ins/VST;
#  - VST3: Common Files\VST3 (and the per-user folder); /Library and ~/Library .../VST3;
#  - CLAP: Common Files\CLAP, CLAP_PATH; /Library and ~/Library .../CLAP;
#  - AU (macOS): .../Components.
# For each: format, bitness and architectures (PE or Mach-O header; ELF on Linux), name and vendor
# where cheap (moduleinfo.json, Info.plist, the PE version resource), and whether a DLL in a VST2
# folder really is a plugin (VSTPluginMain/main export).
#
# Songs (--songs DIR, recursive): every .bmx and .bmxml, the Polac VST/VSTi machines in them, and
# which plugin each loads. Songs don't store the plugin's name (Polac keeps it in its own data), so
# the plugin is guessed from strings in that data (a DLL path or name), matched against what is
# installed, with the machine's name as the fallback; the hints are in the JSON.
#
#   python3 tools/plugin-census.py [--songs DIR]... [--folder vst2=DIR]... [--no-standard]
#                                  [--os windows|macos|linux] [--root DIR] [--out report.md] [--json census.json]
#   python3 tools/plugin-census.py --self-test
#
# The report: counts by format and bitness; VST2 plugins that also come as VST3 or CLAP, and
# VST2-only ones; Polac use in songs (songs, machines, plugins, and whether each is installed and
# in which formats).

import argparse
import base64
import collections
import datetime
import json
import os
import platform
import plistlib
import re
import struct
import sys
import tempfile

# --- Binary headers ---------------------------------------------------------------------------

PE_MACHINES = {0x14C: ("x86", 32), 0x8664: ("x64", 64), 0xAA64: ("arm64", 64), 0xA641: ("arm64ec", 64), 0x1C4: ("arm", 32)}
MACHO_CPUS = {7: ("i386", 32), 0x01000007: ("x86_64", 64), 12: ("arm", 32), 0x0100000C: ("arm64", 64), 18: ("ppc", 32), 0x01000012: ("ppc64", 64)}
ELF_MACHINES = {3: "x86", 62: "x86_64", 183: "arm64", 40: "arm"}


def read_head(path, n=4096):
    try:
        with open(path, "rb") as f:
            return f.read(n)
    except OSError:
        return b""


def read_all(path, limit=64 * 1024 * 1024):
    try:
        if os.path.getsize(path) > limit:
            return None
        with open(path, "rb") as f:
            return f.read()
    except OSError:
        return None


class PE:
    """Just enough of a PE file: machine, exports, version strings."""

    def __init__(self, data):
        self.ok = False
        self.d = data
        if len(data) < 0x40 or data[:2] != b"MZ":
            return
        off = struct.unpack_from("<I", data, 0x3C)[0]
        if off + 24 > len(data) or data[off:off + 4] != b"PE\0\0":
            return
        self.machine, nsec, _, _, _, opt_size, _ = struct.unpack_from("<HHIIIHH", data, off + 4)
        opt = off + 24
        magic = struct.unpack_from("<H", data, opt)[0]
        dd = opt + (96 if magic == 0x10B else 112)
        self.dirs = []
        for i in range(16):
            if dd + 8 * i + 8 <= opt + opt_size:
                self.dirs.append(struct.unpack_from("<II", data, dd + 8 * i))
            else:
                self.dirs.append((0, 0))
        self.sections = []
        s = opt + opt_size
        for i in range(nsec):
            if s + 40 > len(data):
                break
            vsize, va, rsize, rptr = struct.unpack_from("<IIII", data, s + 8)
            self.sections.append((va, max(vsize, rsize), rptr, rsize))
            s += 40
        self.ok = True

    def arch(self):
        return PE_MACHINES.get(self.machine, ("0x%x" % self.machine, None))

    def off(self, rva):
        for va, size, ptr, rsize in self.sections:
            if va <= rva < va + size:
                o = ptr + (rva - va)
                return o if o < len(self.d) else None
        return None

    def cstr(self, o):
        if o is None:
            return ""
        e = self.d.find(b"\0", o)
        return self.d[o:e if e >= 0 else o + 256].decode("latin-1")

    def exports(self):
        rva, size = self.dirs[0] if self.dirs else (0, 0)
        o = self.off(rva) if rva else None
        if o is None or o + 40 > len(self.d):
            return []
        n_names, names_rva = struct.unpack_from("<I", self.d, o + 24)[0], struct.unpack_from("<I", self.d, o + 32)[0]
        no = self.off(names_rva)
        out = []
        for i in range(min(n_names, 4096)):
            if no is None or no + 4 * i + 4 > len(self.d):
                break
            out.append(self.cstr(self.off(struct.unpack_from("<I", self.d, no + 4 * i)[0])))
        return out

    def version_strings(self):
        rva = self.dirs[2][0] if len(self.dirs) > 2 else 0
        base = self.off(rva) if rva else None
        if base is None:
            return {}

        def entries(o):
            if o + 16 > len(self.d):
                return []
            named, ids = struct.unpack_from("<HH", self.d, o + 12)
            out = []
            for i in range(min(named + ids, 512)):
                e = o + 16 + 8 * i
                if e + 8 > len(self.d):
                    break
                out.append(struct.unpack_from("<II", self.d, e))
            return out

        for name, target in entries(base):
            if name == 16 and target & 0x80000000:          # RT_VERSION
                lvl2 = entries(base + (target & 0x7FFFFFFF))
                if not lvl2 or not lvl2[0][1] & 0x80000000:
                    return {}
                lvl3 = entries(base + (lvl2[0][1] & 0x7FFFFFFF))
                if not lvl3:
                    return {}
                de = base + lvl3[0][1]
                if de + 8 > len(self.d):
                    return {}
                data_rva, size = struct.unpack_from("<II", self.d, de)
                o = self.off(data_rva)
                if o is None:
                    return {}
                return parse_version_info(self.d[o:o + size])
        return {}


def parse_version_info(block):
    """VS_VERSIONINFO → {CompanyName, ProductName, FileDescription, ...}."""
    out = {}

    def node(o, end):
        if o + 6 > end:
            return end
        length, vlen, vtype = struct.unpack_from("<HHH", block, o)
        if length == 0:
            return end
        stop = min(o + length, end)
        k = o + 6
        ke = k
        while ke + 1 < stop and block[ke:ke + 2] != b"\0\0":
            ke += 2
        key = block[k:ke].decode("utf-16-le", "replace")
        p = (ke + 2 + 3) & ~3
        if vtype == 1 and vlen and key not in ("VS_VERSION_INFO", "StringFileInfo", "VarFileInfo"):
            val = block[p:p + vlen * 2].decode("utf-16-le", "replace").split("\0")[0]
            out[key] = val.strip()
            p += vlen * 2
        else:
            p += vlen
        p = (p + 3) & ~3
        while p < stop:
            n = node(p, stop)
            if n <= p:
                break
            p = (n + 3) & ~3
        return stop

    node(0, len(block))
    return out


def macho_archs(data):
    if len(data) < 8:
        return []
    magic_be = struct.unpack_from(">I", data, 0)[0]
    if magic_be in (0xCAFEBABE, 0xCAFEBABF):
        n = struct.unpack_from(">I", data, 4)[0]
        if n > 32:          # also the Java class magic; a fat header has few entries
            return []
        step = 20 if magic_be == 0xCAFEBABE else 32
        archs = []
        for i in range(n):
            o = 8 + i * step
            if o + 4 > len(data):
                break
            cpu = struct.unpack_from(">I", data, o)[0]
            archs.append(MACHO_CPUS.get(cpu, ("cpu%x" % cpu, None)))
        return archs
    magic = struct.unpack_from("<I", data, 0)[0]
    if magic in (0xFEEDFACE, 0xFEEDFACF):
        cpu = struct.unpack_from("<I", data, 4)[0]
        return [MACHO_CPUS.get(cpu, ("cpu%x" % cpu, 64 if magic == 0xFEEDFACF else 32))]
    return []


def elf_arch(data):
    if len(data) < 20 or data[:4] != b"\x7fELF":
        return None
    bits = 64 if data[4] == 2 else 32
    m = struct.unpack_from("<H", data, 18)[0]
    return (ELF_MACHINES.get(m, "elf%d" % m), bits)


def binary_info(path):
    """(archs, bits, pe) of a plugin binary: bits is 64, 32, 'both' or None."""
    head = read_head(path, 4096)
    if head[:2] == b"MZ":
        data = read_all(path)
        pe = PE(data or head)
        if pe.ok:
            a, b = pe.arch()
            return [a], b, pe
        return [], None, None
    archs = macho_archs(head)
    if archs:
        names = [a for a, _ in archs]
        bits = {b for _, b in archs if b}
        return names, ("both" if len(bits) > 1 else (bits.pop() if bits else None)), None
    e = elf_arch(head)
    if e:
        return [e[0]], e[1], None
    return [], None, None


# --- Plugin records ---------------------------------------------------------------------------

def loads_json5(text):
    text = re.sub(r"(?m)^\s*//.*$", "", text)
    text = re.sub(r",(\s*[}\]])", r"\1", text)
    return json.loads(text)


def bundle_plist(bundle):
    p = os.path.join(bundle, "Contents", "Info.plist")
    try:
        with open(p, "rb") as f:
            return plistlib.load(f)
    except Exception:
        return {}


def vendor_from_plist(pl):
    for k in ("NSHumanReadableCopyright", "CFBundleGetInfoString"):
        v = pl.get(k)
        if isinstance(v, str) and v.strip():
            v = re.sub(r"(?i)(copyright|\(c\)|©|\d{4}(-\d{4})?|all rights reserved\.?)", "", v).strip(" ,.")
            if v:
                return v
    bid = pl.get("CFBundleIdentifier", "")
    parts = bid.split(".")
    return parts[1] if len(parts) > 2 else ""


def record(fmt, path, name, vendor="", archs=(), bits=None, kind="", extra=None):
    r = {"format": fmt, "path": path, "name": name, "vendor": vendor or "", "archs": list(archs), "bits": bits, "kind": kind}
    if extra:
        r.update(extra)
    return r


def stem(path):
    b = os.path.basename(path.rstrip("/\\"))
    for ext in (".vst3", ".clap", ".component", ".vst", ".dll", ".so"):
        if b.lower().endswith(ext):
            return b[: -len(ext)]
    return b


def scan_vst3(path):
    name, vendor, kind, classes = stem(path), "", "", []
    archs, bits = [], None
    if os.path.isdir(path):
        contents = os.path.join(path, "Contents")
        for mi in (os.path.join(contents, "moduleinfo.json"), os.path.join(contents, "Resources", "moduleinfo.json")):
            if os.path.isfile(mi):
                try:
                    j = loads_json5(open(mi, encoding="utf-8", errors="replace").read())
                    name = j.get("Name") or name
                    vendor = (j.get("Factory Info") or {}).get("Vendor", "")
                    for c in j.get("Classes") or []:
                        if c.get("Category") == "Audio Module Class":
                            classes.append(c.get("Name", ""))
                            sub = c.get("Sub Categories") or []
                            kind = kind or ("instrument" if any("Instrument" in s for s in sub) else "effect")
                except Exception:
                    pass
                break
        pl = bundle_plist(path)
        if pl:
            name = pl.get("CFBundleName") or name
            vendor = vendor or vendor_from_plist(pl)
        bins = []
        for sub in ("x86_64-win", "x86-win", "arm64-win", "arm64ec-win", "arm64x-win", "MacOS", "x86_64-linux", "i386-linux", "aarch64-linux"):
            d = os.path.join(contents, sub)
            if os.path.isdir(d):
                for f in sorted(os.listdir(d)):
                    fp = os.path.join(d, f)
                    if os.path.isfile(fp):
                        bins.append(fp)
        allbits = set()
        for b in bins:
            a, bt, pe = binary_info(b)
            archs += a
            if bt == "both":
                allbits |= {32, 64}
            elif bt:
                allbits.add(bt)
            if pe and not vendor:
                vs = pe.version_strings()
                vendor = vs.get("CompanyName", "")
        archs = sorted(set(archs))
        bits = "both" if len(allbits) > 1 else (allbits.pop() if allbits else None)
    else:   # a VST 3.0 single-file module (Windows DLL)
        archs, bits, pe = binary_info(path)
        if pe:
            vs = pe.version_strings()
            vendor = vs.get("CompanyName", "")
            name = vs.get("ProductName") or name
    return record("VST3", path, name, vendor, archs, bits, kind, {"classes": classes} if classes else None)


def scan_clap(path):
    name, vendor = stem(path), ""
    if os.path.isdir(path):
        pl = bundle_plist(path)
        name = pl.get("CFBundleName") or name
        vendor = vendor_from_plist(pl)
        exe = pl.get("CFBundleExecutable") or stem(path)
        archs, bits, _ = binary_info(os.path.join(path, "Contents", "MacOS", exe))
    else:
        archs, bits, pe = binary_info(path)
        if pe:
            vs = pe.version_strings()
            vendor = vs.get("CompanyName", "")
            name = vs.get("ProductName") or name
            if "clap_entry" not in pe.exports():
                return None
    return record("CLAP", path, name, vendor, archs, bits)


def scan_vst2_bundle(path):
    pl = bundle_plist(path)
    exe = pl.get("CFBundleExecutable") or stem(path)
    archs, bits, _ = binary_info(os.path.join(path, "Contents", "MacOS", exe))
    return record("VST2", path, pl.get("CFBundleName") or stem(path), vendor_from_plist(pl), archs, bits)


def scan_vst2_file(path):
    archs, bits, pe = binary_info(path)
    if pe:
        exports = pe.exports()
        if not ({"VSTPluginMain", "main", "VstPluginMain", "main_plugin"} & set(exports)):
            return None   # a helper DLL, not a plugin
        vs = pe.version_strings()
        shell = "shell" if "shell" in stem(path).lower() else ""
        return record("VST2", path, stem(path), vs.get("CompanyName", ""), archs, bits, shell, {"product": vs.get("ProductName", "")})
    if archs:   # Linux .so
        return record("VST2", path, stem(path), "", archs, bits)
    return None


def scan_au(path):
    pl = bundle_plist(path)
    exe = pl.get("CFBundleExecutable") or stem(path)
    archs, bits, _ = binary_info(os.path.join(path, "Contents", "MacOS", exe))
    out = []
    comps = pl.get("AudioComponents") or []
    for c in comps or [{}]:
        full = c.get("name") or pl.get("CFBundleName") or stem(path)
        vendor, _, name = full.partition(": ") if ": " in full else ("", "", full)
        t = c.get("type", "")
        kind = {"aumu": "instrument", "aufx": "effect", "aumf": "effect", "aumi": "midi", "auou": "output", "augn": "generator"}.get(t, t)
        out.append(record("AU", path, name, vendor or vendor_from_plist(pl), archs, bits, kind))
    return out


# --- Folders --------------------------------------------------------------------------------------

def windows_registry_vst_paths():
    paths = []
    try:
        import winreg
    except ImportError:
        return paths
    views = [0, getattr(winreg, "KEY_WOW64_32KEY", 0x200), getattr(winreg, "KEY_WOW64_64KEY", 0x100)]
    for hive in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
        for view in views:
            try:
                with winreg.OpenKey(hive, r"SOFTWARE\VST", 0, winreg.KEY_READ | view) as k:
                    v, _ = winreg.QueryValueEx(k, "VSTPluginsPath")
                    if v:
                        paths.append(("registry VSTPluginsPath", v))
            except OSError:
                pass

    # Polac's own folders: SOFTWARE\Polac\VST Loader\<version>\<arch>\Plugin_Path (and others).
    def walk(hive, key, depth=0):
        try:
            with winreg.OpenKey(hive, key) as k:
                i = 0
                while True:
                    try:
                        name, val, _ = winreg.EnumValue(k, i)
                    except OSError:
                        break
                    if "path" in name.lower() and isinstance(val, str) and val and "ladspa" not in name.lower():
                        for part in re.split(r"[;|\n]", val):
                            if part.strip():
                                paths.append(("Polac " + key.split("\\", 3)[-1] + "\\" + name, part.strip()))
                    i += 1
                j = 0
                while depth < 4:
                    try:
                        sub = winreg.EnumKey(k, j)
                    except OSError:
                        break
                    walk(hive, key + "\\" + sub, depth + 1)
                    j += 1
        except OSError:
            pass

    for hive in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):
        walk(hive, r"SOFTWARE\Polac\VST Loader")
    return paths


def standard_folders(osname, root=None):
    """[(format, folder, why)] for an OS. With root, absolute paths are re-rooted (for tests)."""
    home = os.path.expanduser("~")
    out = []

    def add(fmt, p, why="standard"):
        if root:   # C:\Program Files\VST3 → <root>/C/Program Files/VST3
            p = re.sub(r"^([A-Za-z]):[\\/]", r"\1/", p).replace("\\", "/").lstrip("/")
            p = os.path.join(root, *p.split("/"))
        out.append((fmt, p, why))

    if osname == "windows":
        pf = os.environ.get("ProgramFiles", r"C:\Program Files") if not root else r"C:\Program Files"
        pf86 = os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)") if not root else r"C:\Program Files (x86)"
        cf = os.environ.get("CommonProgramFiles", pf + r"\Common Files") if not root else pf + r"\Common Files"
        cf86 = os.environ.get("CommonProgramFiles(x86)", pf86 + r"\Common Files") if not root else pf86 + r"\Common Files"
        local = os.environ.get("LOCALAPPDATA", home + r"\AppData\Local") if not root else r"C:\Users\user\AppData\Local"
        for base in (pf, pf86):
            for sub in (r"VstPlugins", r"Steinberg\VstPlugins", r"Common Files\VST2", r"Common Files\Steinberg\VST2", r"VSTPlugins64", r"Vstplugins64"):
                add("VST2", base + "\\" + sub)
        add("VST3", cf + r"\VST3")
        add("VST3", cf86 + r"\VST3")
        add("VST3", local + r"\Programs\Common\VST3")
        add("CLAP", cf + r"\CLAP")
        add("CLAP", local + r"\Programs\Common\CLAP")
        # Buzz-era installs that keep VSTs beside the gear.
        for base in (pf + r"\ReBuzz", pf86 + r"\Jeskola\Buzz", pf + r"\Jeskola\Buzz"):
            add("VST2", base + r"\Gear\Vst", "Buzz folder")
        if not root:
            for why, p in windows_registry_vst_paths():
                out.append(("VST2", p, why))
            for p in os.environ.get("CLAP_PATH", "").split(";"):
                if p:
                    out.append(("CLAP", p, "CLAP_PATH"))
    elif osname == "macos":
        for base in ("/Library/Audio/Plug-Ins", (home if not root else "/Users/user") + "/Library/Audio/Plug-Ins"):
            add("VST2", base + "/VST")
            add("VST3", base + "/VST3")
            add("CLAP", base + "/CLAP")
            add("AU", base + "/Components")
        if not root:
            for p in os.environ.get("CLAP_PATH", "").split(":"):
                if p:
                    out.append(("CLAP", p, "CLAP_PATH"))
    else:
        h = home if not root else "/home/user"
        for p in (h + "/.vst", "/usr/lib/vst", "/usr/local/lib/vst"):
            add("VST2", p)
        for p in (h + "/.vst3", "/usr/lib/vst3", "/usr/local/lib/vst3"):
            add("VST3", p)
        for p in (h + "/.clap", "/usr/lib/clap", "/usr/local/lib/clap"):
            add("CLAP", p)
    return out


def scan_folder(fmt, folder, seen):
    found, skipped = [], 0
    if not os.path.isdir(folder):
        return found, skipped
    for dirpath, dirnames, filenames in os.walk(folder, followlinks=True):
        keep = []
        for d in dirnames:
            full = os.path.join(dirpath, d)
            low = d.lower()
            if low.endswith(".vst3") and fmt == "VST3":
                found.append(scan_vst3(full))
            elif low.endswith(".clap") and fmt == "CLAP":
                r = scan_clap(full)
                if r:
                    found.append(r)
            elif low.endswith(".vst") and fmt == "VST2":
                found.append(scan_vst2_bundle(full))
            elif low.endswith(".component") and fmt == "AU":
                found += scan_au(full)
            elif low.endswith((".vst3", ".clap", ".vst", ".component", ".app", ".bundle")):
                pass   # another format's bundle: not ours to read
            else:
                keep.append(d)
        dirnames[:] = keep
        for f in filenames:
            full = os.path.join(dirpath, f)
            low = f.lower()
            r = None
            if fmt == "VST3" and low.endswith(".vst3"):
                r = scan_vst3(full)
            elif fmt == "CLAP" and low.endswith(".clap"):
                r = scan_clap(full)
            elif fmt == "VST2" and low.endswith((".dll", ".so")):
                r = scan_vst2_file(full)
                if r is None:
                    skipped += 1
            if r:
                found.append(r)
    out = []
    for r in found:
        key = (r["format"], os.path.normcase(os.path.realpath(r["path"])), r["name"])
        if key not in seen:
            seen.add(key)
            out.append(r)
    return out, skipped


# --- Songs ----------------------------------------------------------------------------------------

def strings_in(data, minlen=4):
    """Printable ASCII and UTF-16LE strings, in order, without duplicates."""
    out = []
    for m in re.finditer(rb"[\x20-\x7e]{%d,}" % minlen, data):
        out.append(m.group().decode("ascii"))
    for m in re.finditer(rb"(?:[\x20-\x7e]\x00){%d,}" % minlen, data):
        out.append(m.group().decode("utf-16-le"))
    seen, uniq = set(), []
    for s in out:
        s = s.strip()
        if s and s not in seen:
            seen.add(s)
            uniq.append(s)
    return uniq


POLAC_NOISE = re.compile(r"(?i)^(polac|vst|vsti|loader|preset|program|bank|default|chunk|ccnk|fxck|fpch|cnk|param|\W+)$")


def guess_plugin(data, machine_name, installed_names):
    strs = strings_in(data or b"")
    for s in strs:
        m = re.search(r"([^\\/:*?\"<>|]+)\.(dll|vst3|vst|clap|so)\b", s, re.I)
        if m:
            return m.group(1).strip(), "path in data", strs[:12]
    for s in strs:
        n = normalize(s)
        if n and n in installed_names:
            return installed_names[n], "installed name in data", strs[:12]
    for s in strs:
        if len(s) >= 3 and not POLAC_NOISE.match(s) and re.search(r"[A-Za-z]{3}", s):
            return s, "first string in data", strs[:12]
    return machine_name, "machine name", strs[:12]


def bmxml_machines(path):
    import xml.etree.ElementTree as ET
    out = []
    root = ET.parse(path).getroot()
    for m in root.iter():
        if m.tag.split("}")[-1] != "Machine":
            continue
        lib = m.findtext("Library") or ""
        if not lib:
            continue
        data = m.findtext("Data") or ""
        try:
            raw = base64.b64decode(data) if data else b""
        except Exception:
            raw = b""
        out.append((m.findtext("Name") or "", lib, raw))
    return out


def bmx_machines(path):
    """[(name, library, data)] from a .bmx: the MACH section, read with PARA's parameter layouts;
    without PARA (old songs) the machines are found by their library strings."""
    d = read_all(path)
    if not d or d[:4] != b"Buzz":
        return []
    n = struct.unpack_from("<I", d, 4)[0]
    sec = {}
    for i in range(min(n, 64)):
        magic, off, size = struct.unpack_from("<4sII", d, 8 + 12 * i)
        sec[magic] = (off, size)

    def cstr(o):
        e = d.find(b"\0", o)
        return d[o:e].decode("latin-1"), e + 1

    layouts = {}
    if b"PARA" in sec:
        try:
            o = sec[b"PARA"][0]
            cnt = struct.unpack_from("<I", d, o)[0]
            o += 4
            for _ in range(cnt):
                name, o = cstr(o)
                _, o = cstr(o)
                ng, nt = struct.unpack_from("<II", d, o)
                o += 8
                sizes = []
                for _ in range(ng + nt):
                    t = d[o]
                    _, o = cstr(o + 1)
                    o += 20
                    sizes.append(2 if t == 3 else 1 if t in (0, 1, 2) else 0)
                layouts[name] = (sizes[:ng], sizes[ng:])
        except Exception:
            layouts = {}
    out = []
    if b"MACH" not in sec:
        return out
    o, size = sec[b"MACH"]
    end = o + size
    try:
        count = struct.unpack_from("<H", d, o)[0]
        o += 2
        for _ in range(count):
            name, o = cstr(o)
            t = d[o]
            o += 1
            lib = "Master"
            if t != 0:
                lib, o = cstr(o)
            o += 8
            ds = struct.unpack_from("<i", d, o)[0]
            o += 4
            data = d[o:o + ds]
            o += ds
            na = struct.unpack_from("<H", d, o)[0]
            o += 2
            for _ in range(na):
                _, o = cstr(o)
                o += 4
            if name not in layouts:
                raise ValueError("no PARA layout for " + name)
            g, tr = layouts[name]
            o += sum(g)
            tracks = struct.unpack_from("<H", d, o)[0]
            o += 2 + tracks * sum(tr)
            out.append((name, lib, data))
            if o > end:
                raise ValueError("past MACH")
        return out
    except Exception:
        pass
    # Fallback: name\0 type library\0 x y size data, found by the library string.
    out = []
    mach = d[sec[b"MACH"][0]:end]
    for m in re.finditer(rb"(Polac[^\x00]{1,60})\x00", mach):
        s = m.start()
        lib = m.group(1).decode("latin-1")
        k = s - 2
        j = k
        while j > 0 and 0x20 <= mach[j - 1] < 0x7F and k - j < 64:
            j -= 1
        name = mach[j:k].decode("latin-1")
        p = m.end() + 8
        if p + 4 <= len(mach):
            ds = struct.unpack_from("<i", mach, p)[0]
            data = mach[p + 4:p + 4 + ds] if 0 <= ds <= len(mach) - p - 4 else b""
            out.append((name, lib, data))
    return out


def scan_songs(folders, installed_names):
    songs, errors = [], []
    for folder in folders:
        for dirpath, _, filenames in os.walk(folder):
            for f in filenames:
                low = f.lower()
                if not low.endswith((".bmx", ".bmxml", ".bmw")):
                    continue
                full = os.path.join(dirpath, f)
                try:
                    machines = bmxml_machines(full) if low.endswith(".bmxml") else bmx_machines(full)
                except Exception as e:
                    errors.append({"song": full, "error": str(e)})
                    continue
                polac = []
                for name, lib, data in machines:
                    if re.match(r"(?i)polac vsti?\b", lib):
                        plugin, how, hints = guess_plugin(data, name, installed_names)
                        polac.append({"machine": name, "library": lib, "plugin": plugin, "guessed_from": how, "hints": hints, "data_bytes": len(data)})
                songs.append({"song": full, "machines": len(machines), "polac": polac,
                              "other_polac": sorted({lib for _, lib, _ in machines if lib.lower().startswith("polac") and not re.match(r"(?i)polac vsti?\b", lib)})})
    return songs, errors


# --- Report ---------------------------------------------------------------------------------------

def normalize(name):
    n = (name or "").lower()
    n = re.sub(r"\(.*?\)|\[.*?\]", " ", n)
    n = re.sub(r"(?<![a-z])(x64|x86|x86_64|win64|win32|64[ -]?bit|32[ -]?bit|vst3|vst2|vsti|vst|clap|au|stereo|mono|64|32)(?![a-z])", " ", n)
    return re.sub(r"[^a-z0-9]+", "", n)


def build_report(plugins, songs, song_errors, folders, skipped, osname):
    by_fmt = collections.OrderedDict((f, collections.Counter()) for f in ("VST2", "VST3", "CLAP", "AU"))
    for p in plugins:
        b = p["bits"]
        col = "64+32" if b == "both" else ("64-bit" if b == 64 else "32-bit" if b == 32 else "unknown")
        by_fmt.setdefault(p["format"], collections.Counter())[col] += 1
    names = collections.defaultdict(set)
    for p in plugins:
        keys = {normalize(p["name"]), normalize(stem(p["path"]))}
        if p.get("product"):
            keys.add(normalize(p["product"]))
        for c in p.get("classes") or []:
            keys.add(normalize(c))
        for k in keys:
            if k:
                names[k].add(p["format"])
    vst2 = [p for p in plugins if p["format"] == "VST2"]
    also, only = [], []
    for p in vst2:
        keys = {normalize(p["name"]), normalize(stem(p["path"])), normalize(p.get("product", ""))} - {""}
        fmts = set().union(*(names[k] for k in keys)) if keys else set()
        (also if fmts & {"VST3", "CLAP"} else only).append((p, sorted(fmts - {"VST2"})))
    au_only = []
    if any(p["format"] == "AU" for p in plugins):
        for p in plugins:
            if p["format"] == "AU":
                fmts = names.get(normalize(p["name"]), set())
                if not fmts & {"VST3", "CLAP"}:
                    au_only.append(p)

    polac_songs = [s for s in songs if s["polac"]]
    use = collections.defaultdict(lambda: {"songs": set(), "machines": 0, "libraries": set()})
    for s in polac_songs:
        for m in s["polac"]:
            u = use[m["plugin"]]
            u["songs"].add(s["song"])
            u["machines"] += 1
            u["libraries"].add(m["library"])

    lines = []
    w = lines.append
    w(f"# Plugin census — {platform.node() or 'this machine'} ({osname}), {datetime.date.today().isoformat()}")
    w("")
    w("Made by `tools/plugin-census.py` (docs/specs/plugin-host.md §4). Nothing was loaded: formats and bitness come from file headers.")
    w("")
    w("## Installed plugins")
    w("")
    w("| Format | 64-bit | 32-bit | 64+32 | unknown | total |")
    w("|---|---:|---:|---:|---:|---:|")
    for f, c in by_fmt.items():
        w(f"| {f} | {c['64-bit']} | {c['32-bit']} | {c['64+32']} | {c['unknown']} | {sum(c.values())} |")
    w("")
    if skipped:
        w(f"{skipped} DLLs in VST2 folders are not plugins (no VSTPluginMain/main export) and are not counted.")
        w("")
    arm = [p for p in plugins if osname == "macos" and p["archs"] and "arm64" not in p["archs"]]
    if arm:
        w(f"{len(arm)} Mac plugins have no arm64 code (Intel only: they need Rosetta and an Intel host process).")
        w("")
    w("## VST2 plugins and their newer formats")
    w("")
    w(f"- **{len(also)}** VST2 plugins also come as VST3 or CLAP here (the new host can use those).")
    w(f"- **{len(only)}** are VST2-only ({sum(1 for p, _ in only if p['bits'] == 32)} of them 32-bit): only Polac (Windows) or a VST2 helper can load them.")
    w("")
    if only:
        w("| VST2-only | Vendor | Bits | Path |")
        w("|---|---|---|---|")
        for p, _ in sorted(only, key=lambda x: x[0]["name"].lower())[:200]:
            w(f"| {p['name']} | {p['vendor']} | {p['bits']} | `{p['path']}` |")
        w("")
    if also:
        w("<details><summary>VST2 plugins with a VST3 or CLAP version</summary>")
        w("")
        for p, f in sorted(also, key=lambda x: x[0]["name"].lower()):
            w(f"- {p['name']} ({p['bits']}-bit): also {', '.join(f)}")
        w("")
        w("</details>")
        w("")
    if au_only:
        w(f"**{len(au_only)}** AUs have no VST3 or CLAP version: " + ", ".join(sorted({p['name'] for p in au_only})[:60]))
        w("")
    w("## Polac VST/VSTi in songs")
    w("")
    if not songs and not song_errors:
        w("No song folders were given (`--songs DIR`).")
    else:
        machines = sum(len(s["polac"]) for s in polac_songs)
        w(f"- {len(songs)} songs read ({len(song_errors)} unreadable); **{len(polac_songs)}** use Polac VST/VSTi, with {machines} Polac machines in all.")
        w(f"- {len(use)} different plugins. The plugin is guessed from Polac's data (see the JSON's `hints`); \"machine name\" means nothing better was found.")
        w("")
        if use:
            w("| Plugin (guessed) | Songs | Machines | Installed as | Polac |")
            w("|---|---:|---:|---|---|")
            for plugin, u in sorted(use.items(), key=lambda kv: (-len(kv[1]["songs"]), kv[0].lower())):
                fmts = sorted(names.get(normalize(plugin), set()))
                w(f"| {plugin} | {len(u['songs'])} | {u['machines']} | {', '.join(fmts) or 'not found'} | {', '.join(sorted(u['libraries']))} |")
            w("")
            need_vst2 = [pl for pl in use if names.get(normalize(pl), set()) and not names[normalize(pl)] & {"VST3", "CLAP"}]
            missing = [pl for pl in use if not names.get(normalize(pl))]
            w(f"Of these, {len(use) - len(need_vst2) - len(missing)} are installed as VST3 or CLAP, {len(need_vst2)} only as VST2 and {len(missing)} weren't found installed.")
            w("")
    w("## Folders scanned")
    w("")
    for fmt, folder, why, n in folders:
        w(f"- {fmt}: `{folder}` ({why}): {n if n is not None else 'not there'}")
    w("")
    summary = {
        "counts": {f: dict(c) for f, c in by_fmt.items()},
        "vst2_with_newer_format": len(also), "vst2_only": len(only), "vst2_only_32bit": sum(1 for p, _ in only if p["bits"] == 32),
        "au_only": len(au_only), "songs": len(songs), "songs_with_polac": len(polac_songs),
        "polac_machines": sum(len(s["polac"]) for s in polac_songs), "polac_plugins": len(use),
        "polac_plugins_by_songs": {k: len(v["songs"]) for k, v in use.items()},
    }
    return "\n".join(lines), summary


def run(args):
    osname = args.os
    if osname == "auto":
        osname = {"win32": "windows", "darwin": "macos"}.get(sys.platform, "linux")
    folders = [] if args.no_standard else standard_folders(osname, args.root)
    for spec in args.folder:
        fmt, _, d = spec.partition("=")
        folders.append((fmt.upper(), d, "--folder"))
    seen, plugins, skipped, scanned = set(), [], 0, []
    for fmt, folder, why in folders:
        if not os.path.isdir(folder):
            scanned.append((fmt, folder, why, None))
            continue
        found, sk = scan_folder(fmt, folder, seen)
        plugins += found
        skipped += sk
        scanned.append((fmt, folder, why, len(found)))
    installed = {}
    for p in plugins:
        for k in (normalize(p["name"]), normalize(stem(p["path"]))):
            if k:
                installed.setdefault(k, p["name"])
    songs, errors = scan_songs(args.songs, installed)
    report, summary = build_report(plugins, songs, errors, scanned, skipped, osname)
    data = {"os": osname, "date": datetime.date.today().isoformat(), "summary": summary, "plugins": plugins, "songs": songs,
            "song_errors": errors, "folders": [{"format": f, "folder": d, "why": w, "found": n} for f, d, w, n in scanned]}
    if args.out:
        open(args.out, "w", encoding="utf-8").write(report + "\n")
    if args.json:
        json.dump(data, open(args.json, "w", encoding="utf-8"), indent=1, default=list)
    if not args.quiet:
        print(report)
    return data


# --- Self-test: synthetic folders and songs ------------------------------------------------------

def make_pe(machine=0x8664, exports=("VSTPluginMain",), company="Test Vendor", product="Test Product"):
    """A minimal PE with an export table and a version resource (not loadable, but parseable)."""
    pe64 = machine in (0x8664, 0xAA64)
    opt_size = 240 if pe64 else 224
    sec_rva, sec_raw = 0x1000, 0x400
    body = bytearray()
    # exports at sec_rva
    names = [e.encode() + b"\0" for e in exports]
    exp = bytearray(40)
    names_rva = sec_rva + 40
    name_ptrs = bytearray()
    strings_rva = names_rva + 4 * len(names)
    strings = bytearray()
    for nm in names:
        name_ptrs += struct.pack("<I", strings_rva + len(strings))
        strings += nm
    struct.pack_into("<I", exp, 24, len(names))
    struct.pack_into("<I", exp, 32, names_rva)
    body += exp + name_ptrs + strings
    while len(body) % 4:
        body += b"\0"

    # version resource
    def vs_node(key, value=None, children=b"", text=True):
        k = key.encode("utf-16-le") + b"\0\0"
        hdr_len = 6 + len(k)
        pad = (4 - hdr_len % 4) % 4
        val = b""
        vlen = 0
        if value is not None:
            val = value.encode("utf-16-le") + b"\0\0"
            vlen = len(val) // 2
        blob = k + b"\0" * pad + val
        while (6 + len(blob)) % 4:
            blob += b"\0"
        blob += children
        return struct.pack("<HHH", 6 + len(blob), vlen, 1 if text else 0) + blob

    strs = vs_node("CompanyName", company) + vs_node("ProductName", product)
    table = vs_node("040904B0", None, strs)
    sfi = vs_node("StringFileInfo", None, table)
    vinfo = vs_node("VS_VERSION_INFO", None, sfi, text=False)
    rsrc_rva = sec_rva + len(body)
    # directory: type(16) -> id(1) -> lang(0x409) -> data entry
    r = bytearray()
    lvl1 = struct.pack("<IIHHHH", 0, 0, 0, 0, 0, 1) + struct.pack("<II", 16, 0x80000000 | 24)
    lvl2 = struct.pack("<IIHHHH", 0, 0, 0, 0, 0, 1) + struct.pack("<II", 1, 0x80000000 | 48)
    lvl3 = struct.pack("<IIHHHH", 0, 0, 0, 0, 0, 1) + struct.pack("<II", 0x409, 72)
    data_entry_off = 72
    data_off = 88
    r += lvl1 + lvl2 + lvl3
    r += struct.pack("<IIII", rsrc_rva + data_off, len(vinfo), 0, 0)
    r += vinfo
    body += r
    sec_size = len(body)
    hdr = bytearray(0x400)
    hdr[0:2] = b"MZ"
    struct.pack_into("<I", hdr, 0x3C, 0x80)
    hdr[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<HHIIIHH", hdr, 0x84, machine, 1, 0, 0, 0, opt_size, 0x2022)
    opt = 0x98
    struct.pack_into("<H", hdr, opt, 0x20B if pe64 else 0x10B)
    dd = opt + (112 if pe64 else 96)
    struct.pack_into("<II", hdr, dd, sec_rva, 40)
    struct.pack_into("<II", hdr, dd + 16, rsrc_rva, len(r))
    s = opt + opt_size
    hdr[s:s + 8] = b".data\0\0\0"
    struct.pack_into("<IIII", hdr, s + 8, sec_size, sec_rva, sec_size, sec_raw)
    return bytes(hdr) + bytes(body)


def make_macho(archs):
    cpu = {"x86_64": 0x01000007, "arm64": 0x0100000C, "i386": 7}
    if len(archs) == 1:
        return struct.pack("<II", 0xFEEDFACF if archs[0] != "i386" else 0xFEEDFACE, cpu[archs[0]]) + b"\0" * 64
    out = struct.pack(">II", 0xCAFEBABE, len(archs))
    for i, a in enumerate(archs):
        out += struct.pack(">IIIII", cpu[a], 0, 4096 * (i + 1), 64, 12)
    return out + b"\0" * 64


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data if isinstance(data, bytes) else data.encode("utf-8"))


def mac_bundle(path, name, exe_bytes, extra=None, ident="com.testvendor.plugin"):
    pl = {"CFBundleName": name, "CFBundleExecutable": name, "CFBundleIdentifier": ident, "NSHumanReadableCopyright": "Copyright 2024 Test Vendor"}
    if extra:
        pl.update(extra)
    write(os.path.join(path, "Contents", "Info.plist"), plistlib.dumps(pl))
    write(os.path.join(path, "Contents", "MacOS", name), exe_bytes)


def make_bmx(machines):
    """A .bmx with MACH and PARA sections: machines = [(name, type, library, data, globals, tracks)]."""
    para = struct.pack("<I", len(machines))
    mach = struct.pack("<H", len(machines))
    for name, t, lib, data, ng, nt in machines:
        para += name.encode() + b"\0" + (lib or "Master").encode() + b"\0" + struct.pack("<II", ng, 1)
        for i in range(ng):
            para += bytes([3]) + f"g{i}".encode() + b"\0" + struct.pack("<iiiii", 0, 65534, 65535, 2, 0)
        para += bytes([2]) + b"t0\0" + struct.pack("<iiiii", 0, 254, 255, 2, 0)
        mach += name.encode() + b"\0" + bytes([t])
        if t != 0:
            mach += lib.encode() + b"\0"
        mach += struct.pack("<ffi", 0.1, 0.2, len(data)) + data
        mach += struct.pack("<H", 1) + b"attr\0" + struct.pack("<i", 7)
        mach += b"\x01\x00" * ng
        mach += struct.pack("<H", nt) + b"\x05" * nt
    secs = [(b"MACH", mach), (b"PARA", para)]
    off = 8 + 12 * len(secs)
    head = b"Buzz" + struct.pack("<I", len(secs))
    body = b""
    for magic, blob in secs:
        head += magic + struct.pack("<II", off + len(body), len(blob))
        body += blob
    return head + body


def self_test():
    failures = []

    def check(ok, what):
        print(("ok   " if ok else "FAIL ") + what)
        if not ok:
            failures.append(what)

    tmp = tempfile.mkdtemp(prefix="plugin-census-")
    # Windows layout under a fake root.
    win = os.path.join(tmp, "win")
    pf, pf86 = os.path.join(win, "C", "Program Files"), os.path.join(win, "C", "Program Files (x86)")
    write(os.path.join(pf, "VstPlugins", "Vendor A", "Serum_x64.dll"), make_pe(0x8664, ("VSTPluginMain",), "Xfer Records", "Serum"))
    write(os.path.join(pf, "VstPlugins", "Vendor A", "helper.dll"), make_pe(0x8664, ("SomethingElse",)))
    write(os.path.join(pf, "VstPlugins", "OldSynth.dll"), make_pe(0x14C, ("main",), "Old Freeware", "OldSynth"))
    write(os.path.join(pf86, "VstPlugins", "Retro.dll"), make_pe(0x14C, ("VSTPluginMain",), "Retro Co", "Retro"))
    write(os.path.join(pf, "Common Files", "VST3", "Serum.vst3", "Contents", "x86_64-win", "Serum.vst3"), make_pe(0x8664, ("GetPluginFactory",), "Xfer Records", "Serum"))
    write(os.path.join(pf, "Common Files", "VST3", "Serum.vst3", "Contents", "moduleinfo.json"),
          '{\n  "Name": "Serum",\n  // a comment\n  "Factory Info": {"Vendor": "Xfer Records",},\n  "Classes": [{"Category": "Audio Module Class", "Name": "Serum", "Sub Categories": ["Instrument", "Synth"],},],\n}')
    write(os.path.join(pf, "Common Files", "VST3", "Legacy.vst3"), make_pe(0x8664, ("GetPluginFactory",), "Legacy Inc", "Legacy"))
    write(os.path.join(pf, "Common Files", "CLAP", "Surge XT.clap"), make_pe(0x8664, ("clap_entry",), "Surge Synth Team", "Surge XT"))
    write(os.path.join(pf, "Common Files", "CLAP", "notaclap.clap"), make_pe(0x8664, ("nope",)))
    # Mac layout.
    mac = os.path.join(tmp, "mac")
    lib = os.path.join(mac, "Library", "Audio", "Plug-Ins")
    mac_bundle(os.path.join(lib, "VST3", "Diva.vst3"), "Diva", make_macho(["x86_64", "arm64"]), ident="com.u-he.Diva")
    mac_bundle(os.path.join(lib, "CLAP", "Diva.clap"), "Diva", make_macho(["x86_64", "arm64"]), ident="com.u-he.Diva")
    mac_bundle(os.path.join(lib, "VST", "OldIntel.vst"), "OldIntel", make_macho(["x86_64"]))
    mac_bundle(os.path.join(lib, "Components", "Diva.component"), "Diva", make_macho(["x86_64", "arm64"]),
               {"AudioComponents": [{"name": "u-he: Diva", "type": "aumu", "subtype": "Diva", "manufacturer": "UHfX"}]})
    mac_bundle(os.path.join(lib, "Components", "AUOnly.component"), "AUOnly", make_macho(["arm64"]),
               {"AudioComponents": [{"name": "Apple: AUOnly", "type": "aufx", "subtype": "auon", "manufacturer": "appl"}]})
    # Songs: bmxml with Polac (a DLL path in the data), bmx with Polac (UTF-16 path) and without.
    songs = os.path.join(tmp, "songs")
    polac_data = b"\x01\x00\x00\x00" + b"C:\\Program Files\\VstPlugins\\Vendor A\\Serum_x64.dll\x00" + b"\x00" * 16 + b"CcnK" + b"\x00" * 40
    write(os.path.join(songs, "a.bmxml"),
          "<?xml version=\"1.0\"?><ReBuzzSong><Machines>"
          "<Machine><Library>Master</Library><Name>Master</Name></Machine>"
          f"<Machine><Library>Polac VSTi 1.1</Library><Data>{base64.b64encode(polac_data).decode()}</Data><Name>Lead</Name></Machine>"
          f"<Machine><Library>Polac VST 1.1</Library><Data>{base64.b64encode(b'xx' + 'Retro'.encode('utf-16-le') + b'00').decode()}</Data><Name>Retro</Name></Machine>"
          "<Machine><Library>Jeskola Reverb</Library><Name>Rev</Name></Machine>"
          "</Machines></ReBuzzSong>")
    bmx = make_bmx([("Master", 0, None, b"", 3, 0),
                    ("Bass", 1, "Polac VSTi 1.1", b"\x02\x00" + "D:\\VST\\OldSynth.dll".encode("utf-16-le") + b"\x00\x00", 2, 2),
                    ("Kick", 1, "FSM Kick XP", b"", 4, 1)])
    write(os.path.join(songs, "sub", "b.bmx"), bmx)
    write(os.path.join(songs, "sub", "c.bmx"), make_bmx([("Master", 0, None, b"", 3, 0), ("Kick", 1, "FSM Kick XP", b"", 4, 1)]))
    # The same .bmx without PARA (old songs): the fallback finds Polac by its library string.
    nopara = bmx[:4] + struct.pack("<I", 1) + bmx[8:20]
    write(os.path.join(songs, "old", "d.bmx"), nopara + bmx[8 + 24:])
    # fix the MACH offset for the one-section table
    d = bytearray(open(os.path.join(songs, "old", "d.bmx"), "rb").read())
    mach_off_orig, mach_size = struct.unpack_from("<II", bmx, 12)
    struct.pack_into("<I", d, 12, mach_off_orig - 12)
    write(os.path.join(songs, "old", "d.bmx"), bytes(d))

    class A:
        pass

    a = A()
    a.os, a.root, a.no_standard, a.folder, a.songs, a.out, a.json, a.quiet = "windows", win, False, [], [songs], os.path.join(tmp, "win.md"), os.path.join(tmp, "win.json"), True
    data = run(a)
    s = data["summary"]
    print(open(a.out).read())
    check(s["counts"]["VST2"] == {"64-bit": 1, "32-bit": 2}, f"Windows VST2 counts by bitness {s['counts']['VST2']}")
    check(s["counts"]["VST3"] == {"64-bit": 2}, f"Windows VST3: a bundle and a single-file module {s['counts']['VST3']}")
    check(s["counts"]["CLAP"] == {"64-bit": 1}, f"Windows CLAP: one plugin, a .clap without clap_entry skipped {s['counts']['CLAP']}")
    serum = [p for p in data["plugins"] if p["name"] == "Serum" and p["format"] == "VST3"]
    check(bool(serum) and serum[0]["vendor"] == "Xfer Records" and serum[0]["kind"] == "instrument", "VST3 name, vendor and kind from moduleinfo.json (JSON5)")
    vst2 = {p["name"]: p for p in data["plugins"] if p["format"] == "VST2"}
    check(vst2.get("Serum_x64", {}).get("vendor") == "Xfer Records", "VST2 vendor from the PE version resource")
    check("helper" not in vst2, "a DLL without a VST entry point is not a plugin")
    check(s["vst2_with_newer_format"] == 1 and s["vst2_only"] == 2 and s["vst2_only_32bit"] == 2, f"VST2 with VST3 (Serum) vs VST2-only {s['vst2_with_newer_format']}/{s['vst2_only']}")
    check(s["songs"] == 4 and s["songs_with_polac"] == 3, f"songs with Polac: {s['songs_with_polac']} of {s['songs']}")
    byp = s["polac_plugins_by_songs"]
    check(byp.get("Serum_x64") == 1 and byp.get("Retro") == 1 and byp.get("OldSynth") == 2, f"Polac plugins guessed from data (path, installed name, UTF-16 path; old .bmx without PARA) {byp}")
    check(s["polac_machines"] == 4, f"Polac machines {s['polac_machines']}")

    a.os, a.root, a.out, a.json = "macos", mac, os.path.join(tmp, "mac.md"), os.path.join(tmp, "mac.json")
    a.songs = []
    data = run(a)
    s = data["summary"]
    print(open(a.out).read())
    check(s["counts"]["VST3"] == {"64-bit": 1} and s["counts"]["CLAP"] == {"64-bit": 1}, "macOS VST3 and CLAP (universal binaries are 64-bit)")
    check(s["counts"]["VST2"] == {"64-bit": 1} and s["counts"]["AU"] == {"64-bit": 2}, f"macOS VST2 and AU {s['counts']}")
    au = {p["name"]: p for p in data["plugins"] if p["format"] == "AU"}
    check(au.get("Diva", {}).get("vendor") == "u-he" and au["Diva"]["kind"] == "instrument", "AU name, vendor and type from AudioComponents")
    check(s["au_only"] == 1, f"AU-only plugins {s['au_only']}")
    diva = [p for p in data["plugins"] if p["format"] == "VST3"]
    check(diva and set(diva[0]["archs"]) == {"x86_64", "arm64"}, "fat Mach-O architectures")
    check("no arm64 code" in open(a.out).read(), "Intel-only Mac plugins are reported")
    print(f"\n{len(failures)} failed" if failures else "\nall passed")
    return 1 if failures else 0


def main():
    ap = argparse.ArgumentParser(description="Census of installed plugins and Polac use in Buzz songs (docs/specs/plugin-host.md §4).")
    ap.add_argument("--songs", action="append", default=[], help="a folder of songs (.bmx, .bmxml), searched recursively; repeatable")
    ap.add_argument("--folder", action="append", default=[], help="FORMAT=DIR: another plugin folder (VST2, VST3, CLAP, AU); repeatable")
    ap.add_argument("--no-standard", action="store_true", help="only the --folder folders")
    ap.add_argument("--os", default="auto", choices=["auto", "windows", "macos", "linux"], help="which standard folders (default: this OS)")
    ap.add_argument("--root", help="re-root the standard folders under this folder (tests)")
    ap.add_argument("--out", help="write the Markdown report here (default: print it)")
    ap.add_argument("--json", help="write everything as JSON here")
    ap.add_argument("--quiet", action="store_true")
    ap.add_argument("--self-test", action="store_true", help="build synthetic folders and songs and check the census")
    args = ap.parse_args()
    if args.self_test:
        sys.exit(self_test())
    run(args)


if __name__ == "__main__":
    main()
