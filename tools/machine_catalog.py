#!/usr/bin/env python3
# Fork (docs/specs/agent-knowledge.md K0): the machine catalogue. One file, Machines/Manifests/machine-catalog.json,
# says for every machine in the profile which category it is in (the studio browser's ten), its tags (a controlled
# vocabulary, defined in the same file) and one line of what to use it for. Every manifest generator calls
# apply() so the three fields are written into each manifest and survive regeneration:
#   tools/gen-machine-params.py   (the Neo family, beside their DLLs)
#   tools/source_manifests.py     (generated from source)
#   tools/native_manifests.py     (generated from the natives' sources)
#   tools/machine-profiler.py     (drafts, and curated manifests brought up to date)
#
#   python3 tools/machine_catalog.py            check the catalogue against the profile and the manifests
#   python3 tools/machine_catalog.py --write    also write the three fields into every manifest that lacks or differs

import glob
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "Machines", "Manifests", "machine-catalog.json")
FIELDS = ("category", "tags", "use_for")

_cache = None


def load():
    global _cache
    if _cache is None:
        with open(PATH, encoding="utf-8") as f:
            _cache = json.load(f)
    return _cache


def fields(gear):
    """{"category", "tags", "use_for"} for a machine, or None when the catalogue doesn't know it."""
    m = load()["machines"].get(gear)
    return None if m is None else {"category": m["category"], "tags": list(m["tags"]), "use_for": m["use_for"]}


def apply(manifest):
    """The manifest dict with the three fields written straight after its summary (or at the end without one)."""
    f = fields(manifest["gear"])
    if f is None:
        raise SystemExit(f"{manifest['gear']}: not in {os.path.relpath(PATH, ROOT)}; add it (category, tags, use_for)")
    out = {}
    placed = False
    for k, v in manifest.items():
        if k in FIELDS:
            continue
        out[k] = v
        if k == "summary":
            out.update(f)
            placed = True
    if not placed:
        out.update(f)
    manifest.clear()
    manifest.update(out)
    return manifest


def problems():
    """What is wrong with the catalogue itself."""
    c = load()
    out = []
    used = set()
    for gear, m in c["machines"].items():
        if m["category"] not in c["categories"]:
            out.append(f"{gear}: category \"{m['category']}\" is not one of {c['categories']}")
        if not m["tags"]:
            out.append(f"{gear}: no tags")
        for t in m["tags"]:
            used.add(t)
            if t not in c["tags"]:
                out.append(f"{gear}: tag \"{t}\" is not in the vocabulary")
        if len(set(m["tags"])) != len(m["tags"]):
            out.append(f"{gear}: a tag twice")
        if not m["use_for"].strip() or "\n" in m["use_for"]:
            out.append(f"{gear}: use_for must be one non-empty line")
    out += [f"tag \"{t}\" is in the vocabulary but no machine has it" for t in c["tags"] if t not in used]
    return out


def manifest_files():
    files = []
    for f in glob.glob(os.path.join(ROOT, "Machines", "**", "*.machine.json"), recursive=True):
        n = f.replace("\\", "/")
        if "/bin/" in n or "/obj/" in n:
            continue
        files.append(f)
    return sorted(files)


def main(argv):
    write = "--write" in argv
    found = problems()
    profile = json.load(open(os.path.join(ROOT, "Machines", "Manifests", "machine-profile.json"), encoding="utf-8"))["machines"]
    cat = load()["machines"]
    found += [f"{g}: in the profile, not in the catalogue" for g in profile if g not in cat]
    found += [f"{g}: in the catalogue, not in the profile" for g in cat if g not in profile]
    changed = 0
    seen = set()
    for f in manifest_files():
        with open(f, encoding="utf-8") as fh:
            m = json.load(fh)
        seen.add(m["gear"])
        want = fields(m["gear"])
        if want is None:
            continue
        if all(m.get(k) == want[k] for k in FIELDS) and list(m).index("category") == list(m).index("summary") + 1 if "summary" in m and "category" in m else False:
            continue
        if write:
            apply(m)
            with open(f, "w", encoding="utf-8", newline="\n") as fh:
                fh.write(json.dumps(m, ensure_ascii=False, indent=1) + "\n")
            changed += 1
        else:
            found.append(f"{os.path.relpath(f, ROOT)}: category/tags/use_for differ from the catalogue (python3 tools/machine_catalog.py --write)")
    found += [f"{g}: no manifest file" for g in cat if g not in seen]
    for p in found:
        print(p)
    print(f"{len(cat)} machines in the catalogue, {changed} manifests written, {len(found)} problems")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
