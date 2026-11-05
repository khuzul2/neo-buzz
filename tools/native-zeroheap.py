#!/usr/bin/env python3
# Fork (docs/specs/mcp-server.md §12.14): upstream's binaries of the natives with a source fix, made to run on a zeroed heap,
# for the package job's comparison (tools/native-compare.py --zeroed). Upstream's code reads state it never initialised, so an
# instance of its binary starts as whatever its process's heap held: two instances can play the same song differently, and
# no case, however long, makes them all agree (WahMan's glide never reaches its cutoff from a start of 1e10). The fix starts
# that state at 0, where a fresh, zeroed allocation started it; this makes the zeroed allocation itself: the copy of each
# binary imports "ZEROHEAP.dll" where it imported "KERNEL32.dll" (the name patched in place, same length; nothing else in
# the file changes), and ZEROHEAP.dll (tools/native-zeroheap.cpp, the .def this script writes) forwards every function the
# binaries import to kernel32 but HeapAlloc, which it calls with HEAP_ZERO_MEMORY added. The binaries' C runtime (static,
# VS 2015) allocates every block (new, malloc) through HeapAlloc, so each instance starts as a fresh allocation does,
# every time: upstream's own code, deterministic, which a fixed build must render bit for bit.
#
#   python tools/native-zeroheap.py --from RunTime/Gear --to OUT/Gear --def OUT/zeroheap.def Effects/X.x64.dll ...
#     each DLL (relative to --from) copied to the same path under --to with its kernel32 import renamed; the .def lists
#     the union of their imports. tools/build-natives.ps1 -ZeroHeap runs it and builds ZEROHEAP.dll from the .def.

import argparse
import os
import struct
import sys

PROXY = b"ZEROHEAP.dll"
ZEROED = {"HeapAlloc": "NbZeroHeapAlloc"}


class Pe:
    def __init__(self, data):
        self.data = bytearray(data)
        if self.data[:2] != b"MZ":
            raise ValueError("not a PE file (no MZ)")
        pe = struct.unpack_from("<I", self.data, 0x3C)[0]
        if self.data[pe:pe + 4] != b"PE\0\0":
            raise ValueError("not a PE file (no PE signature)")
        n_sections, opt_size = struct.unpack_from("<H", self.data, pe + 6)[0], struct.unpack_from("<H", self.data, pe + 20)[0]
        opt = pe + 24
        magic = struct.unpack_from("<H", self.data, opt)[0]
        if magic not in (0x10B, 0x20B):
            raise ValueError(f"unknown optional header {magic:#x}")
        self.pe32plus = magic == 0x20B
        dirs = opt + (112 if self.pe32plus else 96)
        self.import_rva, self.import_size = struct.unpack_from("<II", self.data, dirs + 8)
        self.sections = []
        for i in range(n_sections):
            s = opt + opt_size + 40 * i
            vsize, va, rawsize, rawptr = struct.unpack_from("<IIII", self.data, s + 8)
            self.sections.append((va, max(vsize, rawsize), rawptr))

    def offset(self, rva):
        for va, size, raw in self.sections:
            if va <= rva < va + size:
                return raw + rva - va
        raise ValueError(f"RVA {rva:#x} in no section")

    def cstring(self, rva):
        o = self.offset(rva)
        return bytes(self.data[o:self.data.index(0, o)])

    def imports(self):
        """[(dll name, name offset in the file, [function names or '#ordinal'])]."""
        out = []
        if not self.import_rva:
            return out
        o = self.offset(self.import_rva)
        while True:
            oft, _, _, name_rva, ft = struct.unpack_from("<IIIII", self.data, o)
            if not (oft or name_rva or ft):
                break
            names, thunk = [], self.offset(oft or ft)
            size, flag = (8, 1 << 63) if self.pe32plus else (4, 1 << 31)
            while True:
                v = struct.unpack_from("<Q" if self.pe32plus else "<I", self.data, thunk)[0]
                if not v:
                    break
                names.append(f"#{v & 0xFFFF}" if v & flag else self.cstring((v & 0x7FFFFFFF) + 2).decode("ascii"))
                thunk += size
            out.append((self.cstring(name_rva).decode("ascii"), self.offset(name_rva), names))
            o += 20
        return out


def patch(src, dst):
    """Copies src to dst with its KERNEL32.dll import renamed to ZEROHEAP.dll; returns the functions it imports from it."""
    with open(src, "rb") as f:
        pe = Pe(f.read())
    k32 = [(n, off, fns) for n, off, fns in pe.imports() if n.lower() == "kernel32.dll"]
    if len(k32) != 1:
        raise ValueError(f"{src}: {len(k32)} KERNEL32.dll imports")
    name, off, fns = k32[0]
    if any(f.startswith("#") for f in fns):
        raise ValueError(f"{src}: imports from KERNEL32.dll by ordinal")
    if "HeapAlloc" not in fns:
        raise ValueError(f"{src}: doesn't import HeapAlloc (its allocations wouldn't be zeroed)")
    assert len(name) == len(PROXY)
    pe.data[off:off + len(PROXY)] = PROXY
    with open(dst, "wb") as f:
        f.write(pe.data)
    return fns


def write_def(path, functions):
    lines = ["LIBRARY ZEROHEAP", "EXPORTS"]
    for fn in sorted(set(functions)):
        lines.append(f"    {fn}={ZEROED[fn]}" if fn in ZEROED else f"    {fn}=kernel32.{fn}")
    with open(path, "w", encoding="ascii", newline="\r\n") as f:
        f.write("\n".join(lines) + "\n")


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--from", dest="src", required=True)
    ap.add_argument("--to", dest="dst", required=True)
    ap.add_argument("--def", dest="def_path", required=True)
    ap.add_argument("dlls", nargs="+")
    a = ap.parse_args(argv)
    every = []
    for rel in a.dlls:
        dst = os.path.join(a.dst, rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        fns = patch(os.path.join(a.src, rel), dst)
        every += fns
        print(f"{rel}: KERNEL32.dll -> {PROXY.decode()} ({len(fns)} functions)")
    write_def(a.def_path, every)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
