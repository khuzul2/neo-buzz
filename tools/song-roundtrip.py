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
# The Modern Pattern Editor is rebuzz-headless's default pattern editor (studio-mvp.md S6), as on
# Windows: an old song's built-in pattern contents go to it, converted to PXP, and the first save
# keeps them as its data. So the check reads that data (the editor's own format) and compares it
# with the contents the original's PATT gives. Without the editor they stay in PATT and in BMXML's
# BuiltInPattern elements, as before (mac-engine.md M2), and that is checked instead.
# The Windows-style song (below) has editor data built here from the editor's format code
# (PatternEditorUtils.CreateModernPatternEditorData): it must open and save byte for byte.
#
#   python3 tools/song-roundtrip.py <path to rebuzz-headless> [work folder] [song ...]
#   python3 tools/song-roundtrip.py --dump <song.bmx|song.bmxml>     (the parsed song, as JSON)
#   python3 tools/song-roundtrip.py --compare <original.bmx> <A> <B>
#   python3 tools/song-roundtrip.py --windows-style <a song saved as BMXML> <out.bmxml>
#   python3 tools/song-roundtrip.py --check-windows-style <out.bmxml> <A.bmxml> <B.bmxml> <A.bmx> <B.bmx>
#       (the checks above on saves another program made: the studio's exit test, studio-mvp.md S9.
#       A is the song saved, B is A opened and saved again; they print JSON and exit 1 on a problem)
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

def visible_connections(song):
    """The song's wires between machines the user sees (pattern editors are wired to the Master)."""
    return sorted(tuple(c) for c in song["connections"] if not hidden(c[0]) and not hidden(c[1]))


# ---------------------------------------------------------------- the Modern Pattern Editor's data

TIME_BASE = 240         # PatternEvent.TimeBase: pattern time per tick
NOTE_OFF = 255


def parse_mpe(hexdata):
    """
    The Modern Pattern Editor's data (PatternEditorUtils: 255, version 1, size, then each pattern's
    name, beats, rows per beat and columns: machine, parameter index, track, graphical, events as
    (time, value), rows of each beat). None for anything else (PXP, empty).
    """
    if not hexdata:
        return None
    r = Reader(bytes.fromhex(hexdata))
    if r.u8() != 255 or r.u8() != 1:
        return None
    size = r.i32()
    if size != len(r.d):
        raise Unparseable(f"editor data says {size} bytes, has {len(r.d)}")
    patterns = []
    for _ in range(r.i32()):
        p = {"name": r.str(), "beats": r.i32(), "rpb": r.i32(), "columns": []}
        for _ in range(r.i32()):
            c = {"machine": r.str(), "index": r.i32(), "track": r.i32(), "graphical": r.u8()}
            c["events"] = [(r.i32(), r.i32()) for _ in range(r.i32())]
            c["beat_rows"] = [r.i32() for _ in range(p["beats"])]
            p["columns"].append(c)
        patterns.append(p)
    if r.pos != len(r.d):
        raise Unparseable(f"{len(r.d) - r.pos} bytes after the editor's patterns")
    return patterns


def build_mpe(patterns):
    """The same data, as PatternEditorUtils.CreateModernPatternEditorData writes it."""
    out = bytearray()
    out += struct.pack("<i", len(patterns))
    for p in patterns:
        out += p["name"].encode("ascii") + b"\0" + struct.pack("<iii", p["beats"], p["rpb"], len(p["columns"]))
        for c in p["columns"]:
            out += c["machine"].encode("ascii") + b"\0" + struct.pack("<iiBi", c["index"], c["track"], c["graphical"], len(c["events"]))
            for time, value in c["events"]:
                out += struct.pack("<ii", time, value)
            for rows in c["beat_rows"]:
                out += struct.pack("<i", rows)
    return bytes([255, 1]) + struct.pack("<i", len(out) + 6) + bytes(out)


def mpe_value(value, pdef):
    """A value as the editor keeps it: a note it can't read becomes the MIDI note's (MPEPatternColumn.SetEvents)."""
    if pdef[0] != 0 or value == NOTE_OFF:
        return value
    octave, note = value >> 4, value & 15
    if note < 1 or note > 12 or octave < 0 or octave > 9:
        value = max(0, min(value, 119))
        return ((value // 12) << 4) + value % 12 + 1
    return value


def builtin_as_mpe(machine, pattern, tracks):
    """
    The events a built-in pattern's rows give the editor: BuiltInPattern.ToPXP makes a column per
    parameter and an event per row with a value (4 rows a beat), and the editor reads them at 240
    pattern time a row. Columns of tracks the machine no longer has are dropped (MPEPattern.UpdateData).
    """
    want = {}
    globals_ = len(machine["gdefs"])
    for j, d in enumerate(machine["gdefs"]):
        want[(j, 0)] = [(i * TIME_BASE, mpe_value(row[j], d)) for i, row in enumerate(pattern["global_rows"]) if row[j] != d[4]]
    for l, rows in enumerate(pattern["track_rows"][:tracks]):
        for j, d in enumerate(machine["tdefs"]):
            want[(globals_ + j, l)] = [(i * TIME_BASE, mpe_value(row[j], d)) for i, row in enumerate(rows) if row[j] != d[4]]
    return want


def check_in_editor(o, op, editor_data, tracks, problems, tag):
    """A built-in pattern's contents, now in the editor's data."""
    try:
        pats = parse_mpe(editor_data)
    except (Unparseable, struct.error, ValueError) as e:
        problems.append(f"{tag}: the editor's data can't be read: {e}")
        return
    p = next((x for x in pats or [] if x["name"] == op["name"]), None)
    if p is None:
        problems.append(f"{tag}: pattern {op['name']!r} isn't in its editor's data")
        return
    have = {(c["index"], c["track"]): c["events"] for c in p["columns"] if c["machine"] == o["name"]}
    for key, events in builtin_as_mpe(o, op, tracks).items():
        if have.get(key, []) != events:
            problems.append(f"{tag}: pattern {op['name']!r} column {key} {short(events)} became {short(have.get(key))}")
            return


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
            editor = s["editors"][i] if s["editors"] is not None and i < len(s["editors"]) else 65535
            if editor != 65535:
                # The default pattern editor took them (converted, as on Windows): they're its data now.
                ed = by_name.get(editor)
                if ed is None or ed["dll"] != "Modern Pattern Editor":
                    problems.append(f"{tag}: pattern {op['name']!r} went to {editor!r}, not to a Modern Pattern Editor")
                else:
                    check_in_editor(o, op, ed["data"], s["tracks"], problems, tag)
                continue
            # No editor here: the built-in editor's contents stay in PATT, and XTAP still gives them to it.
            for key in ("inputs", "global_rows", "track_rows"):
                if op[key] != sp[key]:
                    problems.append(f"{tag}: pattern {op['name']!r} {key} changed")
        for pname, cols in (o.get("columns") or {}).items():
            if cols != (s.get("columns") or {}).get(pname):
                problems.append(f"{tag}: pattern {pname!r} columns changed")
    if visible_connections(orig) != visible_connections(saved):
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
            # Names as BMX holds them: Latin-1, '?' for anything beyond (BMXFile.WriteString), so "Vel → amp" is "Vel ? amp" there.
            latin1 = lambda s: s.encode("latin-1", "replace").decode("latin-1")
            defs = [[latin1(p["def"][1]), p["def"][2], p["def"][3], p["def"][4], p["def"][6]] for p in g["params"] + t["params"]]
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
            if s["editor"]:
                # The default pattern editor took them (as on Windows): they're its data now.
                ed = by_name.get(s["editor"])
                if ed is None or ed["dll"] != "Modern Pattern Editor":
                    problems.append(f"{tag}: pattern {op['name']!r} went to {s['editor']!r}, not to a Modern Pattern Editor")
                else:
                    check_in_editor(o, op, ed["data"], t["tracks"], problems, tag)
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
    if visible_connections(orig) != visible_connections(saved):
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
        if init.get("result", {}).get("serverInfo", {}).get("name") != "neobuzz-live":
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


MIDI_NOTE, MIDI_VELOCITY = -128, -129     # the editor's own MIDI note lanes (PatternEditorUtils.InternalParameter)


def synthetic_events(index, track, pdef, beats, rpb):
    """Events for a column: every few rows, values in the parameter's range (notes and offs for notes)."""
    kind, lo, hi = pdef[0], pdef[2], pdef[3]
    if lo > hi:                       # a placeholder's switches come without a range
        lo, hi = 0, 1
    step = 960 // rpb                 # a beat is 4 ticks of 240
    events = []
    for row in range(beats * rpb):
        if (row + 3 * index + track) % 5:
            continue
        if kind == "Note":
            value = NOTE_OFF if row % 7 == 6 else 0x31 + (row + index) % 12 + 16 * (row // 12 % 3)
        else:
            value = lo + (row * 37 + index * 11 + track) % (hi - lo + 1)
        events.append((row * step, value))
    return events


def synthetic_mpe(machine, rpbs):
    """
    The editor data ReBuzz on Windows saves for a machine's patterns, built from the editor's format
    code: a column per global parameter, then per track the MIDI note lanes and the track's
    parameters, in the order the editor keeps them (MPEPattern.UpdateData), each with some events.
    """
    groups = machine["groups"]
    globals_ = groups[1]["params"] if len(groups) > 1 else []
    tparams = groups[2]["params"] if len(groups) > 2 else []
    tracks = groups[2]["tracks"] if len(groups) > 2 else 0
    patterns = []
    for i, p in enumerate(machine["patterns"]):
        beats, rpb = (p["rows"] + 3) // 4, rpbs[i % len(rpbs)]
        cols = []

        def col(index, track, pdef):
            cols.append({"machine": machine["name"], "index": index, "track": track, "graphical": 0,
                         "events": synthetic_events(index, track, pdef, beats, rpb), "beat_rows": [rpb] * beats})

        for j, prm in enumerate(globals_):
            col(j, 0, prm["def"])
        for track in range(tracks):
            if track == 0:
                col(MIDI_NOTE, 0, ["Note", "MIDI Note", 1, 156, 0])
                col(MIDI_VELOCITY, 0, ["Byte", "MIDI Velocity", 1, 127, 0])
            for j, prm in enumerate(tparams):
                col(len(globals_) + j, track, prm["def"])
        patterns.append({"name": p["name"], "beats": beats, "rpb": rpb, "columns": cols})
    return build_mpe(patterns)


def windows_style_song(src, out):
    """
    A BMXML song as ReBuzz on Windows saves one, made from a song saved here, where every machine
    with patterns has its Modern Pattern Editor: each editor's data is replaced by data built here
    from the editor's format code (synthetic_mpe: notes, values, the MIDI note lanes, 4 and 8 rows
    a beat), and the first placeholder's first pattern gets an automation column (the engine's own).
    Returns the editors' data by editor name, and the machine given the column.
    """
    tree = ET.parse(src)
    root = tree.getroot()
    model = {m["name"]: m for m in parse_bmxml(src)["machines"]}

    def sub(parent, tag, text=None):
        e = ET.SubElement(parent, tag)
        if text is not None:
            e.text = str(text)
        return e

    def name_of(me):
        return model_name(me.findtext("Name"))

    elements = {name_of(me): me for me in root.find("Machines")}
    data, column = {}, None
    for name, m in model.items():
        if not m["editor"] or not m["patterns"] or m["editor"] not in elements:
            continue
        blob = synthetic_mpe(m, [4, 8])
        elements[m["editor"]].find("Data").text = base64.b64encode(blob).decode()
        data[m["editor"]] = blob.hex()

        me = elements[name]
        globals_ = me.findall("ParameterGroups/ParameterGroup")[1].findall("Parameters/Parameter")
        if column is None and m["type"] != "Master" and globals_:
            p0 = globals_[0]
            cols = me.find("Patterns")[0].find("Columns")
            col = sub(cols, "PatternColumn")
            for tag, text in (("Machine", me.findtext("Name")), ("Group", 1), ("IndexInGroup", 0), ("Track", 0)):
                sub(col, tag, text)
            events = sub(col, "Events")
            for time, value in ((0, p0.findtext("MinValue")), (240, p0.findtext("MaxValue"))):
                ev = sub(events, "ColumnEvent")
                sub(ev, "Time", time)
                sub(ev, "Value", value)
                sub(ev, "Duration", 0)
            md = sub(sub(col, "Metadata"), "Metadata")
            sub(md, "Key", "roundtrip")
            sub(md, "Value", "kept")
            column = name
    tree.write(out, encoding="utf-8", xml_declaration=True)
    return data, column


def model_name(s):
    import re
    return re.sub(r"_x([0-9A-Fa-f]{4})_", lambda mm: chr(int(mm.group(1), 16)), s or "")


def check_windows_style(h, src, work):
    """The Windows-style song round-trips in both formats, keeps its editors' data byte for byte, and the column."""
    song = os.path.join(work, "windows-style.bmxml")
    data, column = windows_style_song(src, song)
    if not data:
        return ["no machine with a pattern editor and patterns to give data"], 0, 0
    xml, err = roundtrip(h, song, work, ".bmxml")
    if xml is None:
        return [f"bmxml: {err}"], 0, 0
    bmx, err = roundtrip(h, song, work, ".bmx")
    problems = check_windows_style_saves(song, data, column, xml, bmx)
    if bmx is None:
        return problems + [f"bmx: {err}"], os.path.getsize(xml[0]), 0
    return problems, os.path.getsize(xml[0]), os.path.getsize(bmx[0])


def windows_style_data(song):
    """A Windows-style song's editors' data and the machine given the column, read back from the song (as windows_style_song made them)."""
    model = parse_bmxml(song)
    machines = {m["name"]: m for m in model["machines"]}
    data = {m["editor"]: machines[m["editor"]]["data"] for m in model["machines"] if m["editor"] and m["patterns"] and m["editor"] in machines}
    column = next((m["name"] for m in model["machines"] for p in m["patterns"] for c in p["columns"] if ["roundtrip", "kept"] in c["metadata"]), None)
    return data, column


def check_windows_style_saves(song, data, column, xml, bmx):
    """The checks on the Windows-style song's saves: xml and bmx are (A, B) pairs, or None."""
    problems = []
    want = parse_bmxml(song)
    wm = {m["name"]: m for m in want["machines"]}
    if xml is not None:
        if strip_stamps(open(xml[0], encoding="utf-8-sig").read()) != strip_stamps(open(xml[1], encoding="utf-8-sig").read()):
            problems.append("bmxml: A and B differ beyond the stamps")
        got = parse_bmxml(xml[0])
        gm = {m["name"]: m for m in got["machines"]}
        for name, m in wm.items():
            if gm.get(name) != m:
                diff = [k for k in m if gm.get(name, {}).get(k) != m[k]] if name in gm else ["machine lost"]
                problems.append(f"bmxml A: {name!r} ({m['dll']}): {', '.join(diff)} changed")
        for editor, blob in data.items():
            if gm.get(editor, {}).get("data") != blob:
                problems.append(f"bmxml A: {editor!r}'s data isn't the same bytes")
        if visible_connections(want) != visible_connections(got):
            problems.append("bmxml A: connections changed")
    if bmx is not None:
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
    return problems


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


def compare(original, a, b):
    """The checks main() makes on its own saves, for saves made elsewhere: A and B the same, nothing lost from the original."""
    problems = []
    try:
        orig = parse_bmx(original)
    except Unparseable as e:
        return {"problems": [], "unparseable": str(e)}
    if a.lower().endswith(".bmxml"):
        same = strip_stamps(open(a, encoding="utf-8-sig").read()) == strip_stamps(open(b, encoding="utf-8-sig").read())
        if not same:
            problems.append(f"bmxml: A ({os.path.getsize(a):,} bytes) and B ({os.path.getsize(b):,} bytes) differ beyond the stamps")
        compare_bmxml(orig, parse_bmxml(a), problems, "bmxml A")
    else:
        same = open(a, "rb").read() == open(b, "rb").read()
        if not same:
            problems.append(f"bmx: A ({os.path.getsize(a):,} bytes) and B ({os.path.getsize(b):,} bytes) differ")
        compare_bmx(orig, parse_bmx(a), problems, "bmx A")
    return {"problems": problems, "identical": same, "placeholders": sum(1 for m in orig["machines"] if m["type"] != 0),
            "para": bool(orig["para"])}


def main():
    if len(sys.argv) == 5 and sys.argv[1] == "--compare":
        result = compare(*sys.argv[2:5])
        print(json.dumps(result))
        return 1 if result["problems"] else 0
    if len(sys.argv) == 4 and sys.argv[1] == "--windows-style":
        data, column = windows_style_song(sys.argv[2], sys.argv[3])
        print(json.dumps({"editors": sorted(data), "column": column}))
        return 0 if data and column else 1
    if len(sys.argv) == 7 and sys.argv[1] == "--check-windows-style":
        song = sys.argv[2]
        data, column = windows_style_data(song)
        problems = check_windows_style_saves(song, data, column, sys.argv[3:5], sys.argv[5:7])
        print(json.dumps({"problems": problems, "editors": sorted(data), "column": column}))
        return 1 if problems or not data else 0
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
        print(f"{'ok  ' if not problems else 'FAIL'} windows-style.bmxml (from {os.path.basename(richest[1])}, with Modern Pattern Editor data): "
              f"bmxml {xa:,}, bmx {ba:,}; the editors' data unchanged, both formats stable", flush=True)
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
