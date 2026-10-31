#!/usr/bin/env python3
"""Builds a Neo machine's factory presets (docs/specs/machines.md §3) from readable values.

    python3 tools/gen-machine-presets.py Machines/NeoDrive [...]
    python3 tools/gen-machine-presets.py --all

Reads Params.def and Presets.txt in the machine folder and writes "<Display Name>.prs.xml", the
file ReBuzz loads presets from (next to the machine DLL). Presets.txt:

  machine "Neo Drive"            the machine's display name (the DLL name without .NET)

  preset "Tape — Warm Bus"       "Group — Name" files the preset in a group in the editor
  Mode = Tape                    enum and switch values by name (On/Off)
  Drive = 9                      physical units: dB, seconds, Hz, %, ratio, semitones…
  Mix = 60                       percentages are 0–100; bipolar −100…100
  Cutoff = raw:512               or a raw parameter value

Parameters a preset doesn't name take their defaults when it loads.
"""
import importlib.util
import math
import os
import shlex
import sys
from xml.sax.saxutils import quoteattr

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location('genparams', os.path.join(HERE, 'gen-machine-params.py'))
genparams = importlib.util.module_from_spec(spec)
spec.loader.exec_module(genparams)


def to_raw(p, text):
    lo, hi = p['min'], p['max']
    kind, a = p['kind'], p['args']
    if text.startswith('raw:'):
        v = int(text[4:])
        if not lo <= v <= hi:
            raise ValueError(f"{p['id']}: raw {v} outside {lo}..{hi}")
        return v

    def from_t(t):
        t = min(1.0, max(0.0, t))
        return int(round(lo + t * (hi - lo)))

    if kind == 'enum':
        names = [n.replace('_', ' ') for n in a]
        keys = [n.lower() for n in names]
        if text.lower() not in keys:
            raise ValueError(f"{p['id']}: '{text}' not one of {names}")
        return lo + keys.index(text.lower())
    if kind == 'switch':
        on = text.lower() in ('on', '1', 'true', 'yes') or (len(a) == 2 and text.lower() == a[1].lower())
        return lo + (1 if on else 0)
    if kind == 'ratio' and text.lower() in ('inf', '∞'):
        return hi
    if kind == 'db' and text.lower() in ('-inf', '−∞'):
        return lo
    v = float(text)
    if kind in ('lin', 'db'):
        A, B = float(a[0]), float(a[1])
        return from_t((v - A) / (B - A))
    if kind in ('exp', 'time', 'freq', 'ratio'):
        A, B = float(a[0]), float(a[1])
        return from_t(math.log(v / A) / math.log(B / A))
    if kind == 'pow':
        A, B, E = float(a[0]), float(a[1]), float(a[2])
        return from_t(((v - A) / (B - A)) ** (1 / E) if v > A else 0)
    if kind == 'pct':
        return from_t(v / 100)
    if kind == 'bip':
        return from_t((v / 100 + 1) / 2)
    if kind == 'int':
        off = float(a[0]) if a else 0
        r = int(round(v - off))
        if not lo <= r <= hi:
            raise ValueError(f"{p['id']}: {v} outside the range")
        return r
    raise ValueError(f'unknown kind {kind}')


def build(folder):
    params = genparams.parse(os.path.join(folder, 'Params.def'))['params']
    by_name = {}
    for i, p in enumerate(params):
        p['index'] = i
        by_name[p['id'].lower()] = p
        by_name[p['name'].lower()] = p
    machine = None
    presets = []
    cur = None
    with open(os.path.join(folder, 'Presets.txt'), encoding='utf-8') as f:
        for lineno, line in enumerate(f, 1):
            s = line.strip()
            if not s or s.startswith('#'):
                continue
            if s.startswith('machine '):
                machine = shlex.split(s)[1]
                continue
            if s.startswith('preset '):
                cur = {'name': shlex.split(s)[1], 'values': {}}
                presets.append(cur)
                continue
            if '=' not in s or cur is None:
                sys.exit(f'{folder}/Presets.txt:{lineno}: expected "Param = value"')
            k, v = (x.strip() for x in s.split('=', 1))
            p = by_name.get(k.lower())
            if p is None:
                sys.exit(f'{folder}/Presets.txt:{lineno}: no parameter "{k}"')
            try:
                cur['values'][p['index']] = to_raw(p, v)
            except ValueError as e:
                sys.exit(f'{folder}/Presets.txt:{lineno}: {e}')
    if not machine:
        sys.exit(f'{folder}/Presets.txt: machine "<Display Name>" is required')
    names = [p['name'] for p in presets]
    dup = {n for n in names if names.count(n) > 1}
    if dup:
        sys.exit(f'{folder}/Presets.txt: duplicate presets {sorted(dup)}')

    out = ['<?xml version="1.0" encoding="utf-8"?>', '<PresetDictionary>']
    for pr in presets:
        out.append(f'  <Item Key={quoteattr(pr["name"])}>')
        out.append(f'    <Preset Machine={quoteattr(machine)}>')
        out.append('      <Parameters>')
        for i, p in enumerate(params):
            raw = pr['values'].get(i, p['def'])
            out.append(f'        <Parameter Name={quoteattr(p["name"])} Group="1" Index="{i}" Track="0" Value="{raw}" />')
        out.append('      </Parameters>')
        out.append('      <Attributes />')
        out.append('      <Comment></Comment>')
        out.append('    </Preset>')
        out.append('  </Item>')
    out.append('</PresetDictionary>')
    path = os.path.join(folder, f'{machine}.prs.xml')
    with open(path, 'w', encoding='utf-8-sig', newline='\r\n') as f:
        f.write('\n'.join(out) + '\n')
    print(f'wrote {path} ({len(presets)} presets)')


def main(argv):
    root = os.path.join(HERE, '..')
    folders = argv
    if not folders or folders == ['--all']:
        mroot = os.path.join(root, 'Machines')
        folders = [os.path.join(mroot, d) for d in sorted(os.listdir(mroot))
                   if os.path.exists(os.path.join(mroot, d, 'Presets.txt'))]
    for folder in folders:
        build(folder)


if __name__ == '__main__':
    main(sys.argv[1:])
