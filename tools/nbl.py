#!/usr/bin/env python3
# Fork (docs/specs/native-format.md, clean-break.md CB1): read and write a .nbl song for tests and tools: its JSON manifest,
# its content-addressed blobs and the core sections they make up (ReBuzz/FileOps/Nbl/NblContainer.cs has the layout).
#
#   8 bytes 89 'N' 'B' 'L' 0D 0A 1A 0A, u32 container version, u32 flags, u64 manifest length, u64 blob count,
#   the manifest (UTF-8 JSON), per blob sorted by hash: 32 bytes SHA-256, 1 byte codec (0 raw, 1 deflate), u64 stored length,
#   the blobs in index order, then a SHA-256 of everything before it. Little-endian.

import hashlib
import json
import re
import struct
import zlib

MAGIC = b"\x89NBL\r\n\x1a\n"


def read(path):
    """(manifest, {hash: raw bytes}) of a .nbl file."""
    data = open(path, "rb").read()
    if data[:8] != MAGIC:
        raise ValueError(f"{path} is not a .nbl song")
    if hashlib.sha256(data[:-32]).digest() != data[-32:]:
        raise ValueError(f"{path} is damaged (its hash)")
    _, _, mlen, count = struct.unpack_from("<IIQQ", data, 8)
    at = 32
    manifest = json.loads(data[at:at + mlen].decode("utf-8"))
    at += mlen
    index = []
    for i in range(count):
        h = data[at:at + 32].hex()
        codec = data[at + 32]
        (n,) = struct.unpack_from("<Q", data, at + 33)
        index.append((h, codec, n))
        at += 41
    blobs = {}
    for h, codec, n in index:
        stored = data[at:at + n]
        at += n
        blobs[h] = zlib.decompress(stored, -15) if codec == 1 else stored
    return manifest, blobs


def write(path, manifest, blobs):
    """Writes the file: the manifest as given, each blob deflated where that is smaller."""
    m = json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    keys = sorted(blobs)
    stored = {}
    for h in keys:
        raw = blobs[h]
        c = zlib.compressobj(1, zlib.DEFLATED, -15)
        z = c.compress(raw) + c.flush()
        stored[h] = (1, z) if len(z) < len(raw) else (0, raw)
    out = bytearray(MAGIC + struct.pack("<IIQQ", 1, 0, len(m), len(keys)) + m)
    for h in keys:
        out += bytes.fromhex(h) + bytes([stored[h][0]]) + struct.pack("<Q", len(stored[h][1]))
    for h in keys:
        out += stored[h][1]
    out += hashlib.sha256(out).digest()
    open(path, "wb").write(bytes(out))


def section(manifest, blobs, sid):
    """The bytes of one core section ("MACH", "PATT", ...), or None."""
    for s in manifest.get("core", {}).get("sections", []):
        if s.get("id") == sid:
            return b"".join(blobs[c["blob"]] for c in s.get("chunks", []))
    return None


def set_section(manifest, blobs, sid, data):
    """Replaces a core section's bytes (one blob). Blobs no longer referenced stay; the reader ignores them."""
    h = hashlib.sha256(data).hexdigest()
    blobs[h] = data
    for s in manifest["core"]["sections"]:
        if s.get("id") == sid:
            s["size"] = len(data)
            s["chunks"] = [{"blob": h, "size": len(data)}]
            return
    raise KeyError(sid)


def machine_data_at(mach, name, dlls):
    """(offset of the length field, data) of a machine's saved data in the MACH section: name, type, DLL name, x, y, length, data."""
    alts = b"|".join(re.escape(d.encode("utf-8")) for d in dlls)
    m = re.search(re.escape(name.encode("utf-8")) + rb"\x00[\x01\x02](?:" + alts + rb")\x00", mach)
    if not m:
        return None, None
    at = m.end() + 8
    (n,) = struct.unpack_from("<i", mach, at)
    return at, mach[at + 4:at + 4 + n]
