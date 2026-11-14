#!/usr/bin/env python3
# Fork (docs/specs/mcp-server.md §12.14, U7): manifests from code for the classic native machines with sources that had
# profiled drafts (the Buzztrax collection, ExternalMachines/source/buzztrax-buzzmachines; tools/source_manifests.py does
# the C# machines). What the code declares is read from it as ReBuzz reads it: each CMachineParameter (type, name,
# description, range, default, flags), the parameter list's order, MacInfo's counts and track limit. Units and value
# texts come only from what the machine's DescribeValue proves: each case of its switch(param) is named here with its
# kind and the case's own expression (`expr`, the raw value as v) and the source text it rests on (`code`: each must be
# in the source, whitespace aside), and the generator evaluates the expression at every raw value and fits it exactly
# (sm.mapping), or fails. A parameter whose DescribeValue case doesn't exist is a bare number (kind int, its own texts);
# a switch is Off/On. The script fails when the declarations, the cases DescribeValue has or the code it rests on
# disagree with this file. The profiled measurements (type, health, profile, notes, safe ranges) stay from the committed
# manifest, as tools/source_manifests.py keeps them.
#
#   python3 tools/native_manifests.py            check: every committed manifest is what its source generates
#   python3 tools/native_manifests.py --write    write them (Machines/Manifests/<Effects|Generators>/<Gear>.machine.json)
#   python3 tools/native_manifests.py --table <gear>   print what the source declares (for checking a curated manifest)

import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import source_manifests as sm  # noqa: E402
import jeskola_knowledge as jk  # noqa: E402  (K2 batch b: the three generated Jeskola manifests curated, and every Jeskola parameter's Explain text)

ROOT = sm.ROOT
BT = "ExternalMachines/source/buzztrax-buzzmachines/"

# MachineInterface.h's constants the declarations use.
BUILTIN = {"MI_VERSION": 15, "NOTE_NO": 0, "NOTE_OFF": 255, "NOTE_MIN": 1, "NOTE_MAX": 16 * 9 + 12, "SWITCH_OFF": 0, "SWITCH_ON": 1,
           "SWITCH_NO": 255, "WAVE_MIN": 1, "WAVE_MAX": 200, "WAVE_NO": 0, "MPF_WAVE": 1, "MPF_STATE": 2, "MPF_TICK_ON_EDIT": 4,
           "MT_GENERATOR": 1, "MT_EFFECT": 2, "MIF_MONO_TO_STEREO": 1, "MIF_PLAYS_WAVES": 2, "MIF_DOES_INPUT_MIXING": 16}
TYPES = {"pt_note": "note", "pt_switch": "switch", "pt_byte": "byte", "pt_word": "word"}


def C(case, kind, expr=None, code=(), unit=None, flag=None, choices=None, note=None, expr_scale=None):
    """A DescribeValue case: its param number, the manifest kind, the case's expression (raw value v), the source text, and
    expr_scale for a case that prints percent (the manifest's percent and bipolar kinds are fractions)."""
    return {k: v for k, v in dict(case=case, kind=kind, expr=expr, code=list(code) if isinstance(code, (list, tuple)) else [code],
                                  unit=unit, flag=flag, choices=choices, note=note, expr_scale=expr_scale).items() if v is not None and v != []}


LFO_FSM = "0.05*pow(800.0,v/255.0)"
LFO_TICKS = "240–254 lock the LFO to the tempo instead: 1, 2, 3, 4, 6, 8, 12, 16, 24, 28, 32, 48, 64, 96 or 128 ticks a cycle, as its own text says"


SPECS = [
    {
        "gear": "FSM WahMan", "files": ["FSM/WahMan/WahMan.cpp"],
        "summary": "Auto-wah: a resonant low-pass filter whose cutoff an LFO sweeps up and down (sine, rate and depth, its phase "
                   "settable from the pattern), the cutoff gliding at an inertia rate. Mono, one track.",
        "cases": [C(2, "freq", LFO_FSM, ["#define LFOPAR2TIME(value) (0.05*pow(800.0,value/255.0))",
                                         'sprintf(txt, "%5.3f Hz", (double)LFOPAR2TIME(value));'], unit="Hz")],
    },
    {
        "gear": "FSM WahManPro", "files": ["FSM/WahManPro/WahManPro.cpp"],
        "summary": "Auto-wah with a choice of 2- and 4-pole low-pass and peak filters and nine LFO shapes sweeping the cutoff, "
                   "with depth, rate, phase and cutoff inertia. It starts its LFO phase and its cutoff glide at 0 until Set phase "
                   "sets the phase (this build's fix: upstream's binary started them as its heap held, so its sweep could start "
                   "anywhere).",
        "cases": [
            C(2, "enum", choices=["2pole LP", "2pole Peak I", "2pole Peak II", "4pole LP", "4pole Peak"]),
            C(3, "freq", LFO_FSM, ["#define LFOPAR2TIME(value) (0.05*pow(800.0,value/255.0))",
                                   'sprintf(txt, "%5.3f Hz", (double)LFOPAR2TIME(value));'], unit="Hz"),
            C(5, "enum", choices=["sine", "saw up", "saw down", "square", "triangle", "weird 1", "weird 2", "weird 3", "weird 4"]),
        ],
    },
    {
        "gear": "FSM WahPro2", "files": ["FSM/WahPro2/WahPro2.cpp"],
        "summary": "Auto-wah with eight filters (2- and 4-pole low-pass and peaks, two vocal formant filters) and 17 LFO shapes "
                   "(steps, saws, sample and hold) sweeping the cutoff, the LFO free in Hz or locked to the tempo in ticks.",
        "cases": [
            C(2, "enum", choices=["2pole LP", "2pole Peak I", "2pole Peak II", "4pole LP", "4pole Peak", "4pole Peak II", "Vocal I", "Vocal II"]),
            C(3, "freq", LFO_FSM, ["#define LFOPAR2TIME(value) (0.05*pow(800.0,value/255.0))", "if (value<240)",
                                   'sprintf(txt, "%5.3f Hz", (double)LFOPAR2TIME(value));', 'sprintf(txt, "%d ticks", times[value-240]);'],
              unit="Hz", note="0.05–26.3 Hz exponential up to 239; " + LFO_TICKS),
            C(5, "enum", choices=["sine", "saw up", "saw down", "square", "triangle", "weird 1", "weird 2", "weird 3", "weird 4", "steps up",
                                  "steps down", "upsaws up", "upsaws down", "dnsaws up", "dnsaws down", "S'n'H 1", "S'n'H 2"]),
        ],
    },
    {
        "gear": "FSM Chorus2", "files": ["FSM/Chorus2/Chorus2.cpp"],
        "summary": "Stereo chorus of 1 to 6 modulated delay taps (Fatness), with the minimum delay, modulation depth and rate, and "
                   "the stereo phase between the two sides' LFOs.",
        "cases": [
            # Cases 0 and 1 print milliseconds of a count of samples: the manifest's seconds are at 44.1 kHz, ReBuzz's rate.
            C(0, "linear", "(2+1102.5*v/240)/44100", ["float v=float((2+1102.5*value/240)/pMasterInfo->SamplesPerSec);",
                                                     'sprintf(txt,"%0.2f ms",1000*v);', "float MinDelay=float(2+1120.5*Tracks[0].mindelay/240);"],
              unit="s", note="(2 + 1102.5·v/240) samples as its text says, in seconds at 44.1 kHz (they scale with the rate); its Work "
                             "delays 2 + 1120.5·v/240 samples, 1.6 % more"),
            C(1, "linear", "110.25*v/240/44100", ["float v=float(110.25*value/240/pMasterInfo->SamplesPerSec);", 'sprintf(txt,"%0.2f ms",1000*v);',
                                                 "float ModDepth=float(110.25*Tracks[0].moddepth/240);"],
              unit="s", note="110.25·v/240 samples either way of the minimum delay's, in seconds at 44.1 kHz"),
            C(2, "linear", "6.6*v/240.0", 'sprintf(txt,"%0.2f Hz",6.6*value/240.0);', unit="Hz"),
            C(4, "linear", "v*180/240.0", 'sprintf(txt,"%0.2f deg",value*180/240.0);', unit="°"),
        ],
    },
    {
        "gear": "FSM PhatMan", "files": ["FSM/PhatMan/PhatMan.cpp"], "extra": ["FSM/dspchips/DSPChips.h"],
        "summary": "Stereo chorus and flanger: up to 8 modulated delay lines (a minimum delay plus an LFO's depth in ms), feedback "
                   "from −100 to +100 %, the LFO's stereo phase, and dry and wet levels in dB; the LFO runs in Hz or locked to the "
                   "tempo in ticks.",
        "cases": [
            C(0, "db", "v/10.0-24.0", ["if (value)", 'sprintf(txt, "%4.1f dB", (double)(value/10.0-24.0) );', 'sprintf(txt, "-inf dB");'],
              unit="dB", flag="min_off"),
            C(5, "db", "v/10.0-24.0", ["if (value)", 'sprintf(txt, "%4.1f dB", (double)(value/10.0-24.0) );', 'sprintf(txt, "-inf dB");'],
              unit="dB", flag="min_off"),
            C(6, "bipolar", "v*100.0/64.0-100.0", 'sprintf(txt, "%4.1f %%", (double)(value*100.0/64.0-100.0) );', unit="%",
              expr_scale=0.01),
            C(2, "linear", "v/10.0/1000", 'sprintf(txt, "%4.1f ms", (double)(value/10.0) );', unit="s"),
            C(3, "linear", "v/10.0/1000", 'sprintf(txt, "%4.1f ms", (double)(value/10.0) );', unit="s"),
            C(4, "freq", LFO_FSM, ["LfoRateDesc(txt,value);", "#define LFOPAR2TIME_CHIPS(value) (0.05*pow(800.0,value/255.0))",
                                   "if (lforate<240)", 'sprintf(buf, "%5.3f Hz", (double)LFOPAR2TIME_CHIPS(lforate));',
                                   'sprintf(buf, "%d ticks", GetNoOfTicks(lforate-240));'],
              unit="Hz", note="0.05–26.3 Hz exponential up to 239; " + LFO_TICKS),
            C(7, "linear", "(v-64)*180/64.0", 'sprintf(txt, "%4.1f deg", (double)((value-64)*180/64.0) );', unit="°"),
        ],
    },
    {
        "gear": "Jeskola NiNjA dElaY", "files": ["Jeskola/Ninja/Ninja.cpp"],
        "summary": "Delay with a filter in its feedback loop: up to 8 taps, each with a length in ticks, ms, samples or 1/256 "
                   "ticks, feedback from −100 to +100 %, a low-pass, high-pass, band-pass or band-reject filter (cutoff and "
                   "bandwidth in Hz) and a wet level; the dry signal passes or not.",
        "cases": [
            C(2, "enum", choices=["tick", "ms", "sample", "tick/256"]),
            C(3, "bipolar", "(v-64) * (100.0 / 64.0)", 'sprintf(txt, "%.1f%%", (double)(value-64) * (100.0 / 64.0));', unit="%", expr_scale=0.01),
            C(4, "enum", choices=["lowpass", "highpass", "bandpass", "bandreject"],
              code=['case FILTER_LP: return "lowpass";', 'case FILTER_BR: return "bandreject";', "#define FILTER_LP\t\t0", "#define FILTER_BR\t\t3"]),
            C(5, "linear", "v", 'sprintf(txt, "%dHz", value);', unit="Hz"),
            C(6, "linear", "v", 'sprintf(txt, "%dcps", value);', unit="Hz", note="Its text says cps, cycles per second: Hz"),
            C(7, "percent", "v * (100.0 / 128.0)", 'sprintf(txt, "%.1f%%", (double)value * (100.0 / 128.0));', unit="%", expr_scale=0.01),
        ],
        "explicit_null": [1],
    },
    {
        "gear": "Jeskola Noise Generator", "files": ["Jeskola/Noise/Noise.cpp"],
        "summary": "Noise with an attack, sustain and release envelope per track and a colour from dark to white; a track sounds "
                   "when its Trigger is written (a played parameter: automation_write), up to 8 tracks.",
        "cases": [],
    },
    {
        "gear": "Jeskola Trilok", "files": ["Jeskola/Trilok/Trilok.cpp"],
        "summary": "Synthesised bass drum: BD Trigger fires it (written as an event: automation_write), with its tone, decay and "
                   "volume.",
        "cases": [],
    },
]


# ---- reading the C++ declarations --------------------------------------------------------------------------------------

def cint(text, names):
    t = text.strip()
    t = re.sub(r"\b0[xX]([0-9a-fA-F]+)\b", lambda m: str(int(m.group(1), 16)), t)
    t = re.sub(r"[A-Za-z_]\w*", lambda m: str(names[m.group(0)]) if m.group(0) in names else m.group(0), t)
    if not re.fullmatch(r"[\d\s+\-*/()]+", t):
        raise sm.Problem(f"can't evaluate {text!r}")
    return int(eval(t, {"__builtins__": {}}, {}))


def read_source(spec):
    texts = [open(os.path.join(ROOT, BT + f), encoding="latin-1").read().replace("\r", "") for f in spec["files"]]
    return sm.strip_comments("\n".join(texts))


def declarations(src):
    """[(global params), (track params)] as ReBuzz builds them, each with its DescribeValue number, and the track limit."""
    names = dict(BUILTIN)
    for m in re.finditer(r"^\s*#define\s+(\w+)\s+(\d+|0[xX][0-9a-fA-F]+)\s*$", src, re.M):
        names[m.group(1)] = int(m.group(2), 0)
    decl = {}
    for m in re.finditer(r"CMachineParameter\s+const\s+(\w+)\s*=\s*\{(.*?)\}\s*;", src, re.S):
        a = sm.split_args(m.group(2))
        if len(a) < 8:
            raise sm.Problem(f"{m.group(1)}: {len(a)} fields")
        decl[m.group(1)] = {"type": TYPES[a[0]], "name": sm.literal(a[1]), "description": sm.literal(a[2]), "min": cint(a[3], names),
                            "max": cint(a[4], names), "novalue": cint(a[5], names), "flags": cint(a[6], names), "def": cint(a[7], names),
                            "var": m.group(1)}
    arr = re.search(r"CMachineParameter\s+const\s*\*\s*\w+\s*\[\s*\]\s*=\s*\{(.*?)\}\s*;", src, re.S)
    order = [decl[x.strip().lstrip("&").strip()] for x in sm.split_args(arr.group(1))]
    info = sm.split_args(re.search(r"CMachineInfo\s+const\s+MacInfo\s*=\s*\{(.*?)\}\s*;", src, re.S).group(1))
    n_glob, n_track, max_tracks = cint(info[5], names), cint(info[6], names), cint(info[4], names)
    if n_glob + n_track > len(order):
        raise sm.Problem(f"MacInfo says {n_glob} + {n_track} parameters, the list has {len(order)}")
    out = ([], [])
    for i, d in enumerate(order[:n_glob + n_track]):
        group = 0 if i < n_glob else 1
        p = dict(d, describe=i, index=i if group == 0 else i - n_glob, group=group)
        if p["type"] == "switch":       # ReBuzz: a switch runs 0…1
            p["min"], p["max"] = 0, 1
        if p["type"] == "note":
            continue
        if group == 1 and not p["flags"] & BUILTIN["MPF_STATE"]:
            continue                     # a played track parameter (a trigger): not set, so not in the manifest
        p["declared_default"] = p["def"]
        p["default"] = min(max(p["def"], p["min"]), p["max"])
        out[group].append(p)
    return out[0], out[1], max_tracks


def describe_cases(src):
    """The param numbers DescribeValue's switch(param) has cases for, and its body."""
    m = re.search(r"char\s+const\s*\*\s*\w+::DescribeValue\s*\(\s*int\s+const\s+param\s*,\s*int\s+const\s+value\s*\)\s*\{", src)
    if not m:
        return set(), ""
    i, depth = m.end(), 1
    while depth:
        depth += {"{": 1, "}": -1}.get(src[i], 0)
        i += 1
    body = src[m.end():i - 1]
    sw = re.search(r"switch\s*\(\s*param\s*\)\s*\{", body)
    cases, j, depth = set(), sw.end(), 1
    while depth:
        c = body[j]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
        elif depth == 1:
            k = re.match(r"case\s+(\d+)\s*:", body[j:])
            if k and (j == 0 or not (body[j - 1].isalnum() or body[j - 1] == "_")):
                cases.add(int(k.group(1)))
                j += k.end()
                continue
            k = re.match(r"switch\s*\(", body[j:])
            if k:                          # a nested switch(value): skip to its end
                k2 = body.index("{", j)
                d2, j = 1, k2 + 1
                while d2:
                    d2 += {"{": 1, "}": -1}.get(body[j], 0)
                    j += 1
                continue
        j += 1
    return cases, body


# ---- the manifest ------------------------------------------------------------------------------------------------------

def entry(spec, p, case, body, text):
    e = {"index": p["index"], "id": p["var"][4:] if p["var"].startswith("para") else p["var"], "name": p["name"],
         "min": p["min"], "max": p["max"], "default": p["default"]}
    notes = []
    if p["declared_default"] != p["default"]:
        notes.append(f"Declared default {p['declared_default']} is outside its range; NeoBuzz Live takes {p['default']}")
    if case is None:
        if p["type"] == "switch":
            e.update(kind="switch", choices=["Off", "On"], own_texts=False)
        else:
            e.update(kind="int", a=0)
    else:
        for c in case.get("code", []):
            if sm.normalise(c) not in text:
                raise sm.Problem(f"{spec['gear']} {p['name']}: the code no longer says {c!r}")
        if case["kind"] == "enum":
            names = case["choices"]
            if len(names) != p["max"] - p["min"] + 1:
                raise sm.Problem(f"{spec['gear']} {p['name']}: {len(names)} choices for {p['min']}…{p['max']}")
            for n in names:
                if json.dumps(n) not in body and ('"' + n + '"') not in body:
                    raise sm.Problem(f"{spec['gear']} {p['name']}: DescribeValue doesn't say {n!r}")
            e.update(kind="enum", choices=list(names))
        else:
            expr = case["expr"]
            if case.get("expr_scale"):           # the case prints percent: the manifest's fraction
                expr = f"({expr})*{case['expr_scale']}"
            fit = {"kind": case["kind"], "expr": expr, "flag": case.get("flag"), "unit": case.get("unit")}
            e.update(sm.mapping(fit, {"name": p["name"], "min": p["min"], "max": p["max"], "texts": None}, {}))
            for k in ("a", "b"):
                if e.get(k) == 0:
                    e[k] = 0.0           # not -0.0
        if case.get("note"):
            notes.append(case["note"])
    desc = p["description"] or p["name"]
    for n in notes:
        desc = desc.rstrip(".") + ". " + n
    e["description"] = desc
    return e


def generate(spec, existing):
    src = read_source(spec)
    text = sm.normalise(src + "\n".join(open(os.path.join(ROOT, BT + f), encoding="latin-1").read().replace("\r", "")
                                         for f in spec.get("extra", [])))
    glob, track, max_tracks = declarations(src)
    cases, body = describe_cases(src)
    by_case = {c["case"]: c for c in spec["cases"]}
    numbers = {p["describe"] for p in glob + track}
    # Every case DescribeValue has for a manifest parameter is named here; every one named here is there.
    missing = sorted((cases & numbers) - set(by_case) - set(spec.get("explicit_null", [])))
    if missing:
        raise sm.Problem(f"{spec['gear']}: DescribeValue has cases {missing} this file doesn't map")
    extra = sorted(set(by_case) - cases)
    if extra:
        raise sm.Problem(f"{spec['gear']}: this file maps cases {extra} DescribeValue doesn't have")
    sub = "Generators" if existing.get("kind") == "generator" else "Effects"
    m = {"format": sm.FORMAT, "gear": spec["gear"], "kind": existing.get("kind", "effect"), "status": "generated",
         "source": f"generated by tools/native_manifests.py from {BT}{spec['files'][0]} (mcp-server.md §12.14, U7)"}
    if "type" in existing:
        m["type"] = existing["type"]
    m["summary"] = spec["summary"]
    m["tracks"] = max(1, max_tracks) if track else 0
    for k in ("notes", "health", "profile"):
        if k in existing:
            m[k] = existing[k]
    for group, params in (("parameters", glob), ("track_parameters", track)):
        out = []
        for p in params:
            e = entry(spec, p, by_case.get(p["describe"]), body, text)
            old = next((q for q in existing.get(group, []) if q.get("name") == p["name"]
                        and (q.get("index") in (None, 0, p["index"]))), None)
            if old and old.get("safe_range"):
                e["safe_range"] = old["safe_range"]
            out.append(e)
        m[group] = out
    jk.curate(m)
    jk.explain(m)
    return sm.mc.apply(m), sub


def target(spec, sub):
    return os.path.join(ROOT, "Machines", "Manifests", sub, spec["gear"] + ".machine.json")


def existing_of(spec):
    for sub in ("Effects", "Generators"):
        p = target(spec, sub)
        if os.path.exists(p):
            with open(p, encoding="utf-8-sig") as f:
                return json.load(f), sub
    return {}, None


def run(write):
    problems = []
    for spec in SPECS:
        existing, sub = existing_of(spec)
        try:
            m, sub2 = generate(spec, existing)
        except (sm.Problem, OSError, ValueError, KeyError, AttributeError) as e:
            problems.append(f"{spec['gear']}: {e}")
            continue
        path = target(spec, sub or sub2)
        text = sm.dump(m)
        if write:
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(text)
        elif sm.dump(existing) != text:
            problems.append(f"{spec['gear']}: {os.path.relpath(path, ROOT)} isn't what its source generates (python3 tools/native_manifests.py --write)")
    return problems


def table(gear):
    """What the source of one of the natives (machines.json's list) declares, for checking a curated manifest by hand."""
    with open(os.path.join(ROOT, "ExternalMachines", "machines.json"), encoding="utf-8") as f:
        bt = next(x for x in json.load(f)["machines"] if x["id"] == "buzztrax-buzzmachines")
    n = next(x for x in bt["natives"] if x["gear"] == gear)
    heads = sorted(f for f in os.listdir(os.path.join(ROOT, BT + n["dir"])) if f.endswith(".h"))
    src = read_source({"files": [n["dir"] + "/" + s for s in heads + n["sources"]]})
    glob, track, mt = declarations(src)
    cases, _ = describe_cases(src)
    print(f"{gear}: max tracks {mt}; DescribeValue cases {sorted(cases)}")
    for p in glob + track:
        print(f"  {'G' if p['group'] == 0 else 'T'}{p['index']:<3} #{p['describe']:<3} {p['name']!r:24} {p['min']}…{p['max']} default {p['default']}"
              + (f" (declared {p['declared_default']})" if p['declared_default'] != p['default'] else "") + f"  {p['type']}  {p['description']!r}")


def gears():
    return [s["gear"] for s in SPECS]


if __name__ == "__main__":
    if "--table" in sys.argv:
        table(sys.argv[sys.argv.index("--table") + 1])
        sys.exit(0)
    write = "--write" in sys.argv[1:]
    found = run(write)
    for p in found:
        print(p)
    print(("wrote" if write else "checked") + f" {len(SPECS)} native manifests from source, {len(found)} problems")
    sys.exit(1 if found else 0)
