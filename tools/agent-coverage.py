#!/usr/bin/env python3
# Fork (docs/specs/agent-knowledge.md, K0): docs/agent-coverage.md, what an agent can find out about every machine and every tool.
# Generated, like docs/machine-health.md, so the figures can't drift from the repository:
#   per machine: category, how far its manifest is trusted (curated, generated, draft), whether it has a hand-written page,
#   the share of its parameters that carry a role (and, for drafts, a suggested one), its factory presets, and its safe ranges;
#   per tool: how many guides and pages name it, how many calls with arguments they show, and whether its own description has an example.
# The CatalogTests staleness test runs `--check`.
#
#   python3 tools/agent-coverage.py            rewrite docs/agent-coverage.md
#   python3 tools/agent-coverage.py --check    exit 1 when the committed file differs from what the repository generates

import glob
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "docs", "agent-coverage.md")
GUIDES = os.path.join(ROOT, "ReBuzz.Agent", "Guides")


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read().replace("\r\n", "\n")


def manifests():
    found = {}
    for f in glob.glob(os.path.join(ROOT, "Machines", "**", "*.machine.json"), recursive=True):
        n = f.replace("\\", "/")
        if "/bin/" in n or "/obj/" in n:
            continue
        m = json.loads(read(f))
        found[m["gear"]] = m
    return found


def presets():
    counts = {}
    files = glob.glob(os.path.join(ROOT, "Machines", "*", "Presets.txt")) + glob.glob(os.path.join(ROOT, "Machines", "Manifests", "*", "*.presets.txt"))
    for f in files:
        gear = None
        for line in read(f).split("\n"):
            m = re.match(r'^machine "(.+)"', line)
            if m:
                gear = m.group(1)
                counts[gear] = 0
            elif gear and re.match(r'^preset "(.+)"', line):
                counts[gear] += 1
    return counts


def status(m):
    s = m.get("status", "")
    if s:
        return s
    return "generated" if m.get("source", "").startswith("generated") else "curated"


def tools():
    """Every tool the app registers (a ToolDef with Name, Title and Input) in ReBuzz/Agent and ReBuzz.Agent."""
    found = {}
    files = glob.glob(os.path.join(ROOT, "ReBuzz", "Agent", "*.cs")) + glob.glob(os.path.join(ROOT, "ReBuzz.Agent", "**", "*.cs"), recursive=True)
    for f in sorted(files):
        n = f.replace("\\", "/")
        if "/obj/" in n or "/bin/" in n:
            continue
        text = read(f)
        # A tool is a ToolDef initialiser: Name, Title, Description, Input (prompts and resources have Render and Read instead).
        spots = [m for m in re.finditer(r'Name\s*=\s*(?:"([a-z][a-z0-9_]*)"|(?:NotesTransform\.)?Name)\s*,\s*Title\s*=', text)]
        for i, m in enumerate(spots):
            end = spots[i + 1].start() if i + 1 < len(spots) else len(text)
            block = text[m.end():end]
            head = block.split("Input =")[0]
            if "Input =" not in block or "Render =" in head:
                continue
            found[m.group(1) or "notes_transform"] = "Example" in head
    return found


def texts():
    out = {}
    for f in sorted(glob.glob(os.path.join(GUIDES, "*.md"))):
        out["guide " + os.path.basename(f)[:-3]] = read(f)
    for f in sorted(glob.glob(os.path.join(GUIDES, "machines", "*.md"))):
        out["page " + os.path.basename(f)[:-3]] = read(f)
    out["prompts"] = read(os.path.join(GUIDES, "Prompts.cs"))
    return out


def pct(n, d):
    return "-" if d == 0 else f"{round(100 * n / d)}%"


def generate():
    cat = json.loads(read(os.path.join(ROOT, "Machines", "Manifests", "machine-catalog.json")))
    profile = json.loads(read(os.path.join(ROOT, "Machines", "Manifests", "machine-profile.json")))["machines"]
    ms = manifests()
    pre = presets()
    pages = {os.path.basename(f)[:-3] for f in glob.glob(os.path.join(GUIDES, "machines", "*.md"))}
    order = {c: i for i, c in enumerate(cat["categories"])}
    rows = []
    for gear in profile:
        m = ms.get(gear)
        params = []
        if m:
            for key in ("parameters", "track_parameters"):
                params += [p for p in m.get(key, []) if isinstance(p, dict)]
        roled = sum(1 for p in params if p.get("role"))
        suggested = sum(1 for p in params if p.get("role") or p.get("suggested_role"))
        safe = sum(1 for p in params if p.get("safe_range"))
        rows.append({
            "gear": gear, "category": m.get("category", "") if m else "", "status": status(m) if m else "none",
            "page": gear in pages, "roles": pct(roled, len(params)), "suggested": pct(suggested, len(params)),
            "presets": pre.get(gear, 0), "safe": safe, "params": len(params), "roled": roled, "suggested_n": suggested,
        })
    rows.sort(key=lambda r: (order.get(r["category"], 99), r["gear"].lower()))

    tool_examples = tools()
    corpus = texts()
    trows = []
    for name in sorted(tool_examples):
        named = [k for k, t in corpus.items() if re.search(r"`%s`|(?<![\w_])%s\(" % (name, name), t)]
        calls = sum(len(re.findall(r"(?<![\w_])%s\(" % name, t)) for t in corpus.values())
        trows.append((name, len(named), calls, tool_examples[name]))

    n = len(rows)
    by_status = {s: sum(1 for r in rows if r["status"] == s) for s in ("curated", "generated", "draft", "none")}
    cats = ", ".join(f"{c} {sum(1 for r in rows if r['category'] == c)}" for c in cat["categories"])
    out = []
    out.append("# Agent coverage\n")
    out.append("Generated by `tools/agent-coverage.py` (docs/specs/agent-knowledge.md, K0) from the machine manifests, the profile, the factory presets, the guides and the tool definitions in the source. Do not edit by hand: run the script. The `CatalogTests` staleness test fails when this file is out of date.\n")
    out.append(f"**Machines.** {n} in the profile: {by_status['curated']} curated manifests, {by_status['generated']} generated, {by_status['draft']} draft" + (f", {by_status['none']} with no manifest" if by_status["none"] else "") + f". Hand-written pages: {sum(1 for r in rows if r['page'])}. Factory presets: {sum(r['presets'] for r in rows)} on {sum(1 for r in rows if r['presets'])} machines. Parameters with a role: {sum(r['roled'] for r in rows)} of {sum(r['params'] for r in rows)}; with a role or a suggested one: {sum(r['suggested_n'] for r in rows)}. Safe ranges: {sum(r['safe'] for r in rows)}. Categories: {cats}.\n")
    never = [t[0] for t in trows if t[1] == 0]
    out.append(f"**Tools.** {len(trows)} registered. Named by at least one guide or page: {sum(1 for t in trows if t[1] > 0)}. Shown in a call with arguments: {sum(1 for t in trows if t[2] > 0)}. With an example in their own description: {sum(1 for t in trows if t[3])}." + (f" Named nowhere: {', '.join(never)}." if never else "") + "\n")
    out.append("## Machines\n")
    out.append("Manifest: how far its units and roles are trusted. Page: a hand-written page in the manual. Roles: parameters with a curated role (with drafts' suggested roles counted in brackets when they add any). Presets: factory presets. Safe: parameters with a safe range.\n")
    out.append("| Machine | Category | Manifest | Page | Roles | Presets | Safe | Params |")
    out.append("|---|---|---|---|---|---|---|---|")
    for r in rows:
        roles = r["roles"] + (f" ({r['suggested']})" if r["suggested_n"] > r["roled"] else "")
        out.append(f"| {r['gear']} | {r['category'] or '-'} | {r['status']} | {'yes' if r['page'] else 'no'} | {roles} | {r['presets']} | {r['safe']} | {r['params']} |")
    out.append("\n## Tools\n")
    out.append("Guides: how many guides, pages and prompts name the tool. Calls: how many calls with arguments they show. Example: whether the tool's own description carries one.\n")
    out.append("| Tool | Guides | Calls | Example |")
    out.append("|---|---|---|---|")
    for name, g, c, e in trows:
        out.append(f"| {name} | {g} | {c} | {'yes' if e else 'no'} |")
    return "\n".join(out) + "\n"


def main(argv):
    text = generate()
    if "--check" in argv:
        have = read(OUT) if os.path.exists(OUT) else ""
        if have != text:
            print("docs/agent-coverage.md is stale: run python3 tools/agent-coverage.py")
            return 1
        print("docs/agent-coverage.md is current")
        return 0
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("wrote docs/agent-coverage.md")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
