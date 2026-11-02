#!/usr/bin/env python3
# Fork (docs/specs/mac-engine.md M2): songs with missing machines survive save and open.
#
# A missing machine (a placeholder: off Windows, every native machine) must keep everything the song
# says about it. For each song this drives rebuzz-headless as an MCP client does, one request at a
# time over stdio, and checks:
#  - BMX: open the original, save A.bmx, open A, save B.bmx. A and B are byte-identical.
#  - BMXML: the same with .bmxml. A and B are identical apart from the root's Name, Build and
#    BuildString stamps.
#  - Nothing lost: A.bmx and A.bmxml, parsed here, keep what the original says about every machine:
#    parameters and their values, the data blob, attributes, tracks, patterns and their contents,
#    connections and sequences.
# A song the engine can't open is reported and skipped: "super old" BMX files have no PARA section,
# so their parameters can't be read without the machine's DLL (as on Windows without the machine).
#
#   python3 tools/song-roundtrip.py <path to rebuzz-headless> [work folder] [song ...]
#   python3 tools/song-roundtrip.py --dump <song.bmx|song.bmxml>     (the parsed song, as JSON)
#
# With no songs given it takes the first 12 demos under ExternalMachines/source. Linux CI runs it.

import base64
import copy
import json
import os
import struct
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# ParameterType: Note, Switch, Byte (1 byte), Word (2 bytes).
TYPE_SIZE = {0: 1, 1: 1, 2: 1, 3: 2}
# The Master's globals (MachineManager.GetMaster): the engine reads them with its own definitions.
MASTER_GLOBALS = [(3, "Volume", 0, 0x4000, 65535, 2, 0), (3, "BPM", 10, 0x200, 65535, 2, 126), (2, "TPB", 1, 0x20, 255, 2, 4)]


class Unparseable(Exception):
    pass


class Reader:
    def __init__(self, data):
        self.d, self.pos = data, 0

    def take(self, fmt):
        v = struct.unpack_from("<" + fmt, self.d, self.pos)
        self.pos += struct.calcsize("<" + fmt)
        return v[0]

    def u8(self): return self.take("B")
    def u16(self): return self.take("H")
    def i32(self): return self.take("i")
    def u32(self): return self.take("I")
    def f32(self): return self.take("f")

    def raw(self, n):
        if n < 0 or self.pos + n > len(self.d):
            raise Unparseable(f"{n} bytes at {self.pos} run past the end")
        b = self.d[self.pos:self.pos + n]
        self.pos += n
        return b

    def str(self):
        end = self.d.index(b"\0", self.pos)
        s = self.d[self.pos:end].decode("latin-1")
        self.pos = end + 1
        return s

    def val(self, size):
        return {1: self.u8, 2: self.u16, 4: self.u32}[size]() if size else 0


def hidden(name):
    return name.startswith("\x01")


def parse_bmx(path):
    """The parts of a BMX file that describe machines, as plain data."""
    r = Reader(open(path, "rb").read())
    if r.u32() != 0x7A7A7542:
        raise Unparseable("not a Buzz file")
    secs = {}  # by the magic as the file spells it: ReBuzz's XTAP is "PATX", TAP2 "PAT2" and so on
    for _ in range(r.u32()):
        m = r.raw(4).decode("latin-1")
        secs[m] = (r.u32(), r.u32())
    song = {"sections": list(secs), "machines": [], "connections": [], "sequences": None}

    para = {}
    if "PARA" in secs:
        r.pos = secs["PARA"][0]
        for _ in range(r.u32()):
            name, dll, ng, nt = r.str(), r.str(), r.u32(), r.u32()
            defs = [(r.u8(), r.str(), r.i32(), r.i32(), r.i32(), r.i32(), r.i32()) for _ in range(ng + nt)]
            para[name] = {"dll": dll, "globals": defs[:ng], "tracks": defs[ng:]}
    song["para"] = {k: {"dll": v["dll"], "globals": [list(p) for p in v["globals"]], "tracks": [list(p) for p in v["tracks"]]}
                    for k, v in para.items()}

    machines = song["machines"]
    r.pos = secs["MACH"][0]
    for _ in range(r.u16()):
        m = {"name": r.str(), "type": r.u8()}
        m["dll"] = r.str() if m["type"] else "Master"
        m["x"], m["y"] = r.f32(), r.f32()
        m["data"] = r.raw(r.i32()).hex()
        m["attributes"] = [[r.str(), r.i32()] for _ in range(r.u16())]
        if m["type"] == 0:
            gdefs, tdefs = MASTER_GLOBALS, []
        elif m["name"] in para:
            gdefs, tdefs = para[m["name"]]["globals"], para[m["name"]]["tracks"]
        else:
            raise Unparseable(f"no PARA for {m['name']!r}: its parameters need the machine's DLL")
        m["gdefs"], m["tdefs"] = gdefs, tdefs
        m["globals"] = [r.val(TYPE_SIZE.get(p[0], 0)) for p in gdefs]
        m["tracks"] = r.u16()
        m["track_values"] = [[r.val(TYPE_SIZE.get(p[0], 0)) for p in tdefs] for _ in range(m["tracks"])]
        machines.append(m)

    if "CONN" in secs:
        r.pos = secs["CONN"][0]
        for _ in range(r.u16()):
            song["connections"].append([r.u16(), r.u16(), r.u16(), r.u16()])
    names = [m["name"] for m in machines]

    # Which patterns the built-in editor owns: all of them without XTAP, else those marked 65535.
    editors = {}
    if "PATX" in secs:
        r.pos = secs["PATX"][0]
        if r.u8() == 1:
            for i in range(len(machines)):
                editors[i] = [r.u16() for _ in range(r.u16())]
    for i, m in enumerate(machines):
        m["editors"] = [names[e] if e < len(names) else e for e in editors.get(i, [])] if "PATX" in secs else None

    if "PATT" in secs:
        r.pos = secs["PATT"][0]
        for i, m in enumerate(machines):
            # The engine reads one input column per connection into the machine from a visible machine.
            if m["type"] == 0:
                inputs = sum(1 for c in song["connections"] if c[1] == 0 and not hidden(names[c[0]]))
            else:
                inputs = len({c[0] for c in song["connections"] if c[1] == i and not hidden(names[c[0]])})
            npat, tracks = r.u16(), r.u16()
            m["pattern_tracks"] = tracks
            m["patterns"] = []
            for _ in range(npat):
                p = {"name": r.str(), "rows": r.u16(), "inputs": []}
                for _ in range(inputs):
                    src = r.u16()
                    p["inputs"].append([names[src] if src < len(names) else src, [r.u16() for _ in range(2 * p["rows"])]])
                p["global_rows"] = [[r.val(TYPE_SIZE.get(d[0], 0)) for d in m["gdefs"]] for _ in range(p["rows"])]
                p["track_rows"] = [[[r.val(TYPE_SIZE.get(d[0], 0)) for d in m["tdefs"]] for _ in range(p["rows"])] for _ in range(tracks)]
                m["patterns"].append(p)

    if "PAT2" in secs:
        r.pos = secs["PAT2"][0]
        if r.u8() == 2:
            for m in machines:
                cols = {}
                for _ in range(r.u16()):
                    pname = r.str()
                    cols[pname] = []
                    for _ in range(r.i32()):
                        c = [r.u16(), r.i32(), r.i32(), r.i32()]
                        c[0] = names[c[0]] if c[0] < len(names) else c[0]
                        events = [[r.i32(), r.i32(), r.i32()] for _ in range(r.i32())]
                        meta = [[r.str(), r.str()] for _ in range(r.i32())]
                        cols[pname].append({"target": c, "events": events, "metadata": meta})
                m["columns"] = cols

    if "SEQU" in secs:
        r.pos = secs["SEQU"][0]
        song["sequences"] = {"end": r.i32(), "loop": [r.i32(), r.i32()], "tracks": []}
        for _ in range(r.u16()):
            mi, n = r.u16(), r.u32()
            ps, es = (r.u8(), r.u8()) if n else (0, 0)
            song["sequences"]["tracks"].append([names[mi], [[r.val(ps), r.val(es)] for _ in range(n)]])

    song["connections"] = [[names[a], names[b], amp, pan] for a, b, amp, pan in song["connections"]]
    for m in machines:
        m["gdefs"] = [list(p) for p in m["gdefs"]]
        m["tdefs"] = [list(p) for p in m["tdefs"]]
    return song


def kids(e, tag):
    x = e.find(tag)
    return list(x) if x is not None else []


def parse_bmxml(path):
    """The same model from a BMXML file, for what BMXML carries."""
    root = ET.parse(path).getroot()

    def dec(s):  # XmlConvert.DecodeName
        if not s:
            return s or ""
        import re
        return re.sub(r"_x([0-9A-Fa-f]{4})_", lambda mm: chr(int(mm.group(1), 16)), s)

    def ints(e, tag):
        x = e.find(tag)
        return int(x.text) if x is not None and x.text is not None else 0

    song = {"root": dict(root.attrib), "machines": [], "connections": []}
    for me in kids(root, "Machines"):
        m = {"name": dec(me.findtext("Name")), "dll": me.findtext("Library") or "", "type": me.findtext("Type")}
        m["x"], m["y"] = float(me.findtext("X") or 0), float(me.findtext("Y") or 0)
        m["data"] = base64.b64decode(me.findtext("Data") or "").hex()
        m["editor"] = dec(me.findtext("EditorMachine"))
        m["attributes"] = [[a.findtext("Name"), int(a.findtext("Value"))] for a in kids(me, "Attributes")]
        groups = []
        for ge in kids(me, "ParameterGroups"):
            g = {"type": ge.findtext("Type"), "tracks": ints(ge, "TrackCount"), "params": []}
            for pe in kids(ge, "Parameters"):
                g["params"].append({
                    "def": [pe.findtext("Type"), dec(pe.findtext("Name")), int(pe.findtext("MinValue")), int(pe.findtext("MaxValue")),
                            int(pe.findtext("NoValue")), pe.findtext("Flags"), int(pe.findtext("DefValue"))],
                    "values": {int(v.findtext("Track")): int(v.findtext("Value")) for v in kids(pe, "Values")}})
            groups.append(g)
        m["groups"] = groups
        m["patterns"] = []
        for pe in kids(me, "Patterns"):
            p = {"name": pe.findtext("Name"), "rows": int(pe.findtext("Length")), "columns": []}
            for ce in kids(pe, "Columns"):
                p["columns"].append({"target": [dec(ce.findtext("Machine")), int(ce.findtext("Group")), int(ce.findtext("IndexInGroup")), int(ce.findtext("Track"))],
                                     "events": [[int(e.findtext("Time")), int(e.findtext("Value")), int(e.findtext("Duration"))] for e in kids(ce, "Events")],
                                     "metadata": [[m.findtext("Key"), m.findtext("Value")] for m in kids(ce, "Metadata")]})
            b = pe.find("BuiltInPattern")
            p["builtin"] = None if b is None else {
                "tracks": int(b.findtext("Tracks") or 0),
                "values": base64.b64decode(b.findtext("Values") or "").hex(),
                "inputs": [[dec(i.findtext("Source")), base64.b64decode(i.findtext("AmpPan") or "").hex()] for i in kids(b, "Inputs")]}
            m["patterns"].append(p)
        song["machines"].append(m)
    for ce in kids(root, "MachineConnections"):
        song["connections"].append([dec(ce.findtext("Source")), dec(ce.findtext("Destination")), int(ce.findtext("Amp")), int(ce.findtext("Pan"))])
    song["sequences"] = []
    for se in kids(root, "Sequences"):
        song["sequences"].append([dec(se.findtext("Machine")), [[int(e.findtext("Time")), e.findtext("Type"), e.findtext("Pattern")] for e in kids(se, "Events")]])
    return song


def pack_rows(rows, defs):
    """Pattern rows packed as BMX writes them (one value per parameter, by its type's size)."""
    out = bytearray()
    for row in rows:
        for v, d in zip(row, defs):
            out += struct.pack({1: "<B", 2: "<H"}[TYPE_SIZE[d[0]]], v) if TYPE_SIZE.get(d[0]) else b""
    return out


# ---------------------------------------------------------------- comparisons

def compare_bmx(orig, saved, problems, where):
    """What the original says about each machine, the saved BMX must say too."""
    by_name = {m["name"]: m for m in saved["machines"]}
    if [m["name"] for m in orig["machines"]] != [m["name"] for m in saved["machines"] if m["name"] in {o["name"] for o in orig["machines"]}]:
        problems.append(f"{where}: machine order changed")
    for o in orig["machines"]:
        s = by_name.get(o["name"])
        tag = f"{where}: {o['name']!r} ({o['dll']})"
        if s is None:
            problems.append(f"{tag}: machine lost")
            continue
        for key in ("type", "dll", "x", "y", "data", "attributes", "globals", "tracks", "track_values"):
            if o[key] != s[key]:
                problems.append(f"{tag}: {key} {short(o[key])} became {short(s[key])}")
        if o["type"] != 0 and orig["para"] and (o["gdefs"], o["tdefs"]) != (s["gdefs"], s["tdefs"]):
            problems.append(f"{tag}: parameter definitions changed")
        opats, spats = o.get("patterns", []), s.get("patterns", [])
        if [(p["name"], p["rows"]) for p in opats] != [(p["name"], p["rows"]) for p in spats]:
            problems.append(f"{tag}: patterns {[(p['name'], p['rows']) for p in opats]} became {[(p['name'], p['rows']) for p in spats]}")
            continue
        for i, (op, sp) in enumerate(zip(opats, spats)):
            builtin = o["editors"] is None or (i < len(o["editors"]) and o["editors"][i] == 65535)
            if not builtin:
                continue
            # The built-in editor's contents stay in PATT, and XTAP still gives them to it.
            if s["editors"] is not None and not (i < len(s["editors"]) and s["editors"][i] == 65535):
                problems.append(f"{tag}: pattern {op['name']!r} is no longer the built-in editor's (XTAP {s['editors']})")
            for key in ("inputs", "global_rows", "track_rows"):
                if op[key] != sp[key]:
                    problems.append(f"{tag}: pattern {op['name']!r} {key} changed")
        for pname, cols in (o.get("columns") or {}).items():
            if cols != (s.get("columns") or {}).get(pname):
                problems.append(f"{tag}: pattern {pname!r} columns changed")
    if sorted(map(tuple, orig["connections"])) != sorted(map(tuple, saved["connections"])):
        problems.append(f"{where}: connections changed")
    if orig["sequences"] and orig["sequences"] != saved["sequences"]:
        problems.append(f"{where}: sequences changed")


def compare_bmxml(orig, saved, problems, where):
    """The same for a BMXML save of a BMX original."""
    by_name = {m["name"]: m for m in saved["machines"]}
    for o in orig["machines"]:
        s = by_name.get(o["name"])
        tag = f"{where}: {o['name']!r} ({o['dll']})"
        if s is None:
            problems.append(f"{tag}: machine lost")
            continue
        for key in ("x", "y"):  # BMX has them as float32, BMXML as the shortest text that reads back the same
            if f32(o[key]) != f32(s[key]):
                problems.append(f"{tag}: {key} {o[key]} became {s[key]}")
        for key in ("dll", "data", "attributes"):
            if o[key] != s[key]:
                problems.append(f"{tag}: {key} {short(o[key])} became {short(s[key])}")
        g, t = (s["groups"] + [None, None, None])[1:3]
        if g is None or t is None:
            problems.append(f"{tag}: parameter groups lost")
            continue
        gvals = [p["values"].get(0) for p in g["params"]]
        if gvals != o["globals"]:
            problems.append(f"{tag}: global values {o['globals']} became {gvals}")
        if t["tracks"] != o["tracks"]:
            problems.append(f"{tag}: tracks {o['tracks']} became {t['tracks']}")
        tvals = [[p["values"].get(k) for p in t["params"]] for k in range(o["tracks"])]
        if tvals != o["track_values"]:
            problems.append(f"{tag}: track values {short(o['track_values'])} became {short(tvals)}")
        if o["type"] != 0 and orig["para"]:
            defs = [[p["def"][1], p["def"][2], p["def"][3], p["def"][4], p["def"][6]] for p in g["params"] + t["params"]]
            odefs = [[d[1], d[2], d[3], d[4], d[6]] for d in o["gdefs"] + o["tdefs"]]
            if defs != odefs:
                problems.append(f"{tag}: parameter definitions changed")
        opats, spats = o.get("patterns", []), s.get("patterns", [])
        if [(p["name"], p["rows"]) for p in opats] != [(p["name"], p["rows"]) for p in spats]:
            problems.append(f"{tag}: patterns changed")
            continue
        for i, (op, sp) in enumerate(zip(opats, spats)):
            builtin = o["editors"] is None or (i < len(o["editors"]) and o["editors"][i] == 65535)
            if not builtin:
                continue
            b = sp["builtin"]
            empty = all(v == d[4] for row in op["global_rows"] for v, d in zip(row, o["gdefs"])) and \
                all(v == d[4] for tr in op["track_rows"] for row in tr for v, d in zip(row, o["tdefs"])) and \
                all(v == 65535 for _, ap in op["inputs"] for v in ap)
            if b is None:
                if not empty:
                    problems.append(f"{tag}: pattern {op['name']!r} lost its contents")
                continue
            values = pack_rows(op["global_rows"], o["gdefs"]) + b"".join(pack_rows(tr, o["tdefs"]) for tr in op["track_rows"])
            if b["values"] != values.hex() or b["tracks"] != len(op["track_rows"]):
                problems.append(f"{tag}: pattern {op['name']!r} contents changed")
            inputs = [[src, struct.pack(f"<{len(ap)}H", *ap).hex()] for src, ap in op["inputs"]]
            if b["inputs"] != inputs:
                problems.append(f"{tag}: pattern {op['name']!r} input columns changed")
    if sorted(map(tuple, orig["connections"])) != sorted(map(tuple, saved["connections"])):
        problems.append(f"{where}: connections changed")


def f32(v):
    return struct.unpack("<f", struct.pack("<f", v))[0]


def short(v):
    s = json.dumps(v)
    return s if len(s) < 80 else s[:77] + "..."


def strip_stamps(text):
    """A BMXML file without its root's Name, Build and BuildString (the file name, build and date)."""
    import re
    head, sep, rest = text.partition("<ReBuzzSong")
    tag, close, body = rest.partition(">")
    tag = re.sub(r'\s(Name|Build|BuildString)="[^"]*"', "", tag)
    return head + sep + tag + close + body


# ---------------------------------------------------------------- driving rebuzz-headless

class Headless:
    def __init__(self, exe, work):
        self.work = work
        self.err = open(os.path.join(work, "stderr.txt"), "w")
        env = dict(os.environ, REBUZZ_TRACE="1")
        self.proc = subprocess.Popen([exe, "--agent", "--settings", os.path.join(work, "settings")], stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE, stderr=self.err, text=True, bufsize=1, env=env)
        self.next_id = 0
        init = self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "roundtrip", "version": "1"}})
        if init.get("result", {}).get("serverInfo", {}).get("name") != "rebuzz":
            raise SystemExit(f"initialize failed: {init}")
        self.proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        self.proc.stdin.flush()

    def request(self, method, params=None):
        self.next_id += 1
        self.proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params or {}}) + "\n")
        self.proc.stdin.flush()
        line = self.proc.stdout.readline()
        if not line:
            self.err.flush()
            raise SystemExit("rebuzz-headless closed stdout; stderr:\n" + open(os.path.join(self.work, "stderr.txt")).read()[-4000:])
        return json.loads(line)

    def call(self, tool, **args):
        r = self.request("tools/call", {"name": tool, "arguments": args})
        res = r.get("result") or {}
        text = res.get("content", [{}])[0].get("text", "") if res else json.dumps(r.get("error"))
        return (not res.get("isError", True)) and "error" not in r, text

    def close(self):
        self.proc.stdin.close()
        try:
            return self.proc.wait(30)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            return None


def windows_style_song(src, out):
    """
    A BMXML song as ReBuzz on Windows saves one, made from a song saved here: every machine with
    patterns, and the Master, has a hidden Modern Pattern Editor machine that holds them, and the
    first placeholder's first pattern has an automation column. Off Windows the editors are missing
    too, with no default to replace them, so they must stay as placeholders that keep their data.
    """
    tree = ET.parse(src)
    root = tree.getroot()
    machines, conns = root.find("Machines"), root.find("MachineConnections")

    def sub(parent, tag, text=None):
        e = ET.SubElement(parent, tag)
        if text is not None:
            e.text = str(text)
        return e

    editors, column = 0, None
    for me in list(machines):
        pats = me.find("Patterns")
        if me.findtext("Type") != "Master" and (pats is None or len(pats) == 0):
            continue
        editors += 1
        ename = f"_x0001_pe{editors}"
        for p in list(pats if pats is not None else []):
            b = p.find("BuiltInPattern")
            if b is not None:
                p.remove(b)
        link = ET.Element("EditorMachine")
        link.text = ename
        before = me.find("Data") if me.find("Data") is not None else me.find("Library")
        me.insert(list(me).index(before) + 1, link)     # the class's order: Library, Data, EditorMachine

        ed = sub(machines, "Machine")
        sub(ed, "Library", "Modern Pattern Editor")
        sub(ed, "Data", base64.b64encode(bytes((editors * 7 + i) % 256 for i in range(64))).decode())
        sub(ed, "Name", ename)
        sub(ed, "Patterns")
        groups = sub(ed, "ParameterGroups")
        inputs = copy.deepcopy(me.find("ParameterGroups/ParameterGroup"))    # Amp and Pan, here with no inputs
        for v in inputs.findall("Parameters/Parameter/Values"):
            for x in list(v):
                v.remove(x)
        inputs.find("TrackCount").text = "0"
        groups.append(inputs)
        for t in ("Global", "Track"):
            g = sub(groups, "ParameterGroup")
            sub(g, "Type", t)
            sub(g, "Parameters")
            sub(g, "TrackCount", 0)
        sub(ed, "Attributes")
        for tag, text in (("Type", "Generator"), ("X", 0), ("Y", 0), ("IsMuted", "false"), ("MIDIInputChannel", -1), ("OverrideLatency", -1),
                          ("OversampleFactor", 1), ("IsWireless", "false"), ("BaseOctave", 4), ("ParameterWindowVisible", "false"),
                          ("GUIWindowVisible", "false"), ("IsBypassed", "false")):
            sub(ed, tag, text)
        c = sub(conns, "MachineConnection")
        for tag, text in (("Source", ename), ("Destination", "Master"), ("Amp", 16384), ("Pan", 16384), ("SourceChannel", 0), ("DestinationChannel", 0)):
            sub(c, tag, text)
        sub(sub(c, "SourceChannels"), "int", 0)
        sub(sub(c, "DestinationChannels"), "int", 0)

        globals_ = me.findall("ParameterGroups/ParameterGroup")[1].findall("Parameters/Parameter")
        if column is None and me.findtext("Type") != "Master" and pats is not None and len(pats) and globals_:
            p0 = globals_[0]
            cols = pats[0].find("Columns")
            col = sub(cols, "PatternColumn")
            for tag, text in (("Machine", me.findtext("Name")), ("Group", 1), ("IndexInGroup", 0), ("Track", 0)):
                sub(col, tag, text)
            events = sub(col, "Events")
            for t, v in ((0, p0.findtext("MinValue")), (240, p0.findtext("MaxValue"))):
                ev = sub(events, "ColumnEvent")
                sub(ev, "Time", t)
                sub(ev, "Value", v)
                sub(ev, "Duration", 0)
            md = sub(sub(col, "Metadata"), "Metadata")
            sub(md, "Key", "roundtrip")
            sub(md, "Value", "kept")
            column = me.findtext("Name")
    tree.write(out, encoding="utf-8", xml_declaration=True)
    return editors, column


def check_windows_style(h, src, work):
    """The Windows-style song round-trips in both formats, and keeps its editors and the column."""
    problems = []
    song = os.path.join(work, "windows-style.bmxml")
    editors, column = windows_style_song(src, song)
    want = parse_bmxml(song)

    xml, err = roundtrip(h, song, work, ".bmxml")
    if xml is None:
        return [f"bmxml: {err}"], 0, 0
    if strip_stamps(open(xml[0], encoding="utf-8-sig").read()) != strip_stamps(open(xml[1], encoding="utf-8-sig").read()):
        problems.append("bmxml: A and B differ beyond the stamps")
    got = parse_bmxml(xml[0])
    wm, gm = {m["name"]: m for m in want["machines"]}, {m["name"]: m for m in got["machines"]}
    for name, m in wm.items():
        if gm.get(name) != m:
            diff = [k for k in m if gm.get(name, {}).get(k) != m[k]] if name in gm else ["machine lost"]
            problems.append(f"bmxml A: {name!r} ({m['dll']}): {', '.join(diff)} changed")
    if sorted(map(tuple, want["connections"])) != sorted(map(tuple, got["connections"])):
        problems.append("bmxml A: connections changed")

    bmx, err = roundtrip(h, song, work, ".bmx")
    if bmx is None:
        return problems + [f"bmx: {err}"], os.path.getsize(xml[0]), 0
    if open(bmx[0], "rb").read() != open(bmx[1], "rb").read():
        problems.append("bmx: A and B differ")
    b = parse_bmx(bmx[0])
    bm = {m["name"]: m for m in b["machines"]}
    for name, m in wm.items():
        s = bm.get(name)
        if s is None:
            problems.append(f"bmx A: {name!r} lost")
            continue
        if s["data"] != m["data"]:
            problems.append(f"bmx A: {name!r} data changed")
        if m["editor"] and s["editors"] != [m["editor"]] * len(m["patterns"]):
            problems.append(f"bmx A: {name!r} editor {m['editor']!r} became {s['editors']}")
        for p in m["patterns"]:
            have = (s.get("columns") or {}).get(p["name"], [])
            cols = p["columns"]
            if cols != have:
                problems.append(f"bmx A: {name!r} pattern {p['name']!r} columns {short(cols)} became {short(have)}")
    if column is None:
        problems.append("no placeholder with patterns and global parameters to give a column")
    return problems, os.path.getsize(xml[0]), os.path.getsize(bmx[0])


def roundtrip(h, song, work, ext):
    """Open the original, save A, open A, save B. Returns (A, B) or a reason it couldn't."""
    base = os.path.join(work, os.path.splitext(os.path.basename(song))[0])
    a, b = f"{base}.A{ext}", f"{base}.B{ext}"
    for step, args in (("song_open", dict(path=song, discard_unsaved=True)), ("song_save", dict(path=a, overwrite=True)),
                       ("song_open", dict(path=a, discard_unsaved=True)), ("song_save", dict(path=b, overwrite=True))):
        ok, text = h.call(step, **args)
        if not ok:
            return None, f"{step} {os.path.basename(args['path'])}: {text[:200]}"
    return (a, b), None


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "--dump":
        p = sys.argv[2]
        print(json.dumps(parse_bmxml(p) if p.lower().endswith(".bmxml") else parse_bmx(p), indent=1))
        return 0
    if len(sys.argv) < 2:
        print(__doc__ or "usage: song-roundtrip.py <rebuzz-headless> [work folder] [song ...]")
        return 2
    exe = sys.argv[1]
    work = sys.argv[2] if len(sys.argv) > 2 else tempfile.mkdtemp(prefix="rebuzz-roundtrip-")
    songs = sys.argv[3:]
    if not songs:
        import glob
        songs = sorted(glob.glob(os.path.join(ROOT, "ExternalMachines", "source", "**", "*.bmx"), recursive=True))[:12]
    os.makedirs(work, exist_ok=True)

    started = time.time()
    h = Headless(exe, work)
    failures, opened = [], 0
    richest = (0, None)         # the BMXML save with the most placeholders, for the Windows-style song
    for song in songs:
        name = os.path.basename(song)
        try:
            orig = parse_bmx(song)
        except Unparseable as e:
            orig, why = None, str(e)
        problems = []
        (bmx, err) = roundtrip(h, song, work, ".bmx")
        if bmx is None:
            print(f"skip {name}: the engine can't open it ({err})" + ("" if orig else f"; the checker can't either: {why}"), flush=True)
            if orig is not None and orig["para"]:
                failures.append(f"{name}: has PARA but didn't open: {err}")
            continue
        opened += 1
        a, b = bmx
        sa, sb = os.path.getsize(a), os.path.getsize(b)
        same = open(a, "rb").read() == open(b, "rb").read()
        if not same:
            problems.append(f"bmx: A ({sa:,} bytes) and B ({sb:,} bytes) differ")
        if orig is not None:
            compare_bmx(orig, parse_bmx(a), problems, "bmx A")

        (xml, err) = roundtrip(h, song, work, ".bmxml")
        if xml is None:
            problems.append(f"bmxml: {err}")
            xa = xb = 0
            xsame = False
        else:
            xa, xb = os.path.getsize(xml[0]), os.path.getsize(xml[1])
            xsame = strip_stamps(open(xml[0], encoding="utf-8-sig").read()) == strip_stamps(open(xml[1], encoding="utf-8-sig").read())
            if not xsame:
                problems.append(f"bmxml: A ({xa:,} bytes) and B ({xb:,} bytes) differ beyond the stamps")
            if orig is not None:
                compare_bmxml(orig, parse_bmxml(xml[0]), problems, "bmxml A")

        missing = sum(1 for m in (orig or {"machines": []})["machines"] if m["type"] != 0)
        print(f"{'ok  ' if not problems else 'FAIL'} {name}: {os.path.getsize(song):,} -> bmx {sa:,} / {sb:,} "
              f"({'identical' if same else 'DIFFERENT'}); bmxml {xa:,} / {xb:,} ({'identical' if xsame else 'DIFFERENT'} but stamps); "
              f"{missing} placeholders", flush=True)
        if xml is not None and missing > richest[0]:
            richest = (missing, xml[0])
        for p in problems[:12]:
            print("     " + p)
        if len(problems) > 12:
            print(f"     ... and {len(problems) - 12} more")
        failures += [f"{name}: {p}" for p in problems]

    if richest[1]:
        problems, xa, ba = check_windows_style(h, richest[1], work)
        print(f"{'ok  ' if not problems else 'FAIL'} windows-style.bmxml (from {os.path.basename(richest[1])}, with pattern editor machines): "
              f"bmxml {xa:,}, bmx {ba:,}; editors kept as placeholders, both formats stable", flush=True)
        for p in problems[:12]:
            print("     " + p)
        failures += [f"windows-style.bmxml: {p}" for p in problems]

    code = h.close()
    print(f"\n{opened} of {len(songs)} songs opened; {time.time() - started:.0f} s")
    if code != 0:
        failures.append(f"rebuzz-headless exited with {code}")
    print(f"{len(failures)} problems" if failures else "all round trips stable, nothing lost")
    return 1 if failures or opened == 0 else 0


if __name__ == "__main__":
    sys.exit(main())
