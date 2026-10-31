#!/usr/bin/env python3
"""Generates a Neo machine's parameter code from its Params.def (docs/specs/machines.md §3).

    python3 tools/gen-machine-params.py Machines/NeoDrive [Machines/NeoComp ...]
    python3 tools/gen-machine-params.py --all

Writes, next to Params.def:
  Engine/Params.g.cs     the pure table (indices, ranges, mappings, value text): engine, tests, editor
  Machine.Params.g.cs    the machine's [ParameterDecl] properties (and Note/Velocity track parameters)

Params.def lines:
  namespace NeoBuzz.Drive          C# namespace
  class NeoDriveMachine            the machine class (a partial class)
  tracks 16                        optional: Note and Velocity track parameters for 16 tracks
  group Shape                      group shown in the editor for the following parameters
  Id  "Name"  min max def  kind args...  | description

Kinds and their arguments:
  lin A B [unit] [digits]          A + t(B-A)
  exp A B [unit] [digits]          A (B/A)^t
  pow A B EXP [unit] [digits]      A + t^EXP (B-A)
  db A B [inf]                     dB, linear in dB; "inf": the bottom is -inf
  time A B                         seconds, exponential
  freq A B                         Hz, exponential
  pct [digits]                     0..1 shown as %
  bip [digits]                     -1..1 shown as signed %
  int OFFSET [unit] [signed]       raw + OFFSET
  enum Name1 Name2 ...             choices (underscores become spaces)
  switch [OffName OnName]          off/on
  ratio A B [inf]                  n:1, exponential; "inf": the top is infinity
"""
import os
import shlex
import sys

KINDS = {
    'lin': 'Linear', 'exp': 'Exp', 'pow': 'Power', 'db': 'Db', 'time': 'Time', 'freq': 'Freq',
    'pct': 'Percent', 'bip': 'Bipolar', 'int': 'Int', 'enum': 'Enum', 'switch': 'Switch', 'ratio': 'Ratio',
}


def cs_string(s):
    return '"' + s.replace('\\', '\\\\').replace('"', '\\"') + '"'


def num(x):
    v = float(x)
    if v == int(v) and abs(v) < 1e15:
        return f'{int(v)}'
    return repr(v)


def parse(path):
    spec = {'namespace': None, 'class': None, 'tracks': 0, 'params': []}
    group = ''
    with open(path, encoding='utf-8') as f:
        for lineno, line in enumerate(f, 1):
            line = line.rstrip('\n')
            if not line.strip() or line.lstrip().startswith('#'):
                continue
            desc = ''
            if '|' in line:
                line, desc = line.split('|', 1)
                desc = desc.strip()
            toks = shlex.split(line, comments=True)
            if not toks:
                continue
            head = toks[0]
            if head in ('namespace', 'class'):
                spec[head] = toks[1]
                continue
            if head == 'tracks':
                spec['tracks'] = int(toks[1])
                continue
            if head == 'group':
                group = ' '.join(toks[1:])
                continue
            if len(toks) < 6:
                sys.exit(f'{path}:{lineno}: expected Id "Name" min max def kind args...')
            pid, name, lo, hi, de, kind = toks[:6]
            args = toks[6:]
            lo, hi, de = int(lo), int(hi), int(de)
            if kind not in KINDS:
                sys.exit(f'{path}:{lineno}: unknown kind {kind}')
            if not (0 <= lo <= de <= hi <= 65534):
                sys.exit(f'{path}:{lineno}: need 0 <= min <= def <= max <= 65534')
            p = {'id': pid, 'name': name, 'min': lo, 'max': hi, 'def': de, 'kind': kind, 'args': args,
                 'group': group, 'desc': desc or name, 'line': lineno}
            spec['params'].append(p)
    if not spec['namespace'] or not spec['class']:
        sys.exit(f'{path}: namespace and class are required')
    ids = [p['id'] for p in spec['params']]
    names = [p['name'] for p in spec['params']]
    for seq, what in ((ids, 'id'), (names, 'name')):
        dup = {x for x in seq if seq.count(x) > 1}
        if dup:
            sys.exit(f'{path}: duplicate {what}: {sorted(dup)}')
    return spec


def table_entry(p):
    kind = p['kind']
    a = p['args']
    named = []
    pos = []
    unit = ''
    digits = None

    def take_unit_digits(rest):
        nonlocal unit, digits
        for t in rest:
            if t.lstrip('-').isdigit():
                digits = int(t)
            else:
                unit = t

    if kind in ('lin', 'exp'):
        pos = [num(a[0]), num(a[1])]
        take_unit_digits(a[2:])
    elif kind == 'pow':
        pos = [num(a[0]), num(a[1])]
        named.append(f'exponent: {num(a[2])}')
        take_unit_digits(a[3:])
    elif kind == 'db':
        pos = [num(a[0]), num(a[1])]
        if 'inf' in a[2:]:
            named.append('flag: true')
        for t in a[2:]:
            if t.isdigit():
                digits = int(t)
    elif kind in ('time', 'freq', 'ratio'):
        pos = [num(a[0]), num(a[1])]
        if kind == 'ratio' and 'inf' in a[2:]:
            named.append('flag: true')
    elif kind in ('pct', 'bip'):
        if a:
            digits = int(a[0])
    elif kind == 'int':
        pos = [num(a[0] if a else 0), '0']
        rest = a[1:]
        if 'signed' in rest:
            named.append('flag: true')
            rest = [t for t in rest if t != 'signed']
        if rest:
            unit = rest[0]
    elif kind == 'enum':
        names = [t.replace('_', ' ') for t in a]
        if len(names) != p['max'] - p['min'] + 1:
            sys.exit(f"line {p['line']}: {p['id']}: {len(names)} names for range {p['min']}..{p['max']}")
        named.append('names: new[] { ' + ', '.join(cs_string(n) for n in names) + ' }')
    elif kind == 'switch':
        if p['max'] - p['min'] != 1:
            sys.exit(f"line {p['line']}: {p['id']}: a switch needs a range of two values")
        if a:
            named.append('names: new[] { ' + ', '.join(cs_string(n.replace('_', ' ')) for n in a[:2]) + ' }')
    if unit:
        named.append(f'unit: {cs_string(unit)}')
    if digits is not None:
        named.append(f'digits: {digits}')
    if p['group']:
        named.append(f"group: {cs_string(p['group'])}")
    named.append(f"description: {cs_string(p['desc'])}")
    args = [f"P.{p['id']}", cs_string(p['id']), cs_string(p['name']), str(p['min']), str(p['max']), str(p['def']),
            f"MapKind.{KINDS[kind]}"]
    if pos:
        args += [f'a: {pos[0]}', f'b: {pos[1]}']
    args += named
    return 'new ParamDef(' + ', '.join(args) + ')'


def generate(folder):
    path = os.path.join(folder, 'Params.def')
    spec = parse(path)
    ns, cls = spec['namespace'], spec['class']
    params = spec['params']
    rel = os.path.relpath(path, os.path.join(os.path.dirname(__file__), '..')).replace('\\', '/')
    header = f'// <auto-generated> by tools/gen-machine-params.py from {rel}. Do not edit; edit Params.def and regenerate.\n#nullable disable\n'

    engine = [header, 'using NeoBuzz.Dsp;', '', f'namespace {ns}', '{',
              '    /// <summary>Parameter indices, in declaration order (the order ReBuzz shows and saves them in).</summary>',
              '    internal static class P', '    {']
    for i, p in enumerate(params):
        engine.append(f"        public const int {p['id']} = {i};")
    engine += [f'        public const int Count = {len(params)};', '    }', '',
               '    internal static class Params', '    {',
               f'        /// <summary>Tracks with Note and Velocity (0 for effects).</summary>',
               f"        public const int Tracks = {spec['tracks']};", '',
               '        public static readonly ParamDef[] Table =', '        {']
    for p in params:
        engine.append('            ' + table_entry(p) + ',')
    engine += ['        };', '    }', '}', '']

    machine = [header, 'using Buzz.MachineInterface;', '', f'namespace {ns}', '{',
               f'    public partial class {cls}', '    {']
    for p in params:
        machine.append(f"        [ParameterDecl(Name = {cs_string(p['name'])}, Description = {cs_string(p['desc'])}, "
                       f"MinValue = {p['min']}, MaxValue = {p['max']}, DefValue = {p['def']})]")
        machine.append(f"        public int {p['id']} {{ get => Values[P.{p['id']}]; set => Values.Set(P.{p['id']}, value); }}")
        machine.append('')
    if spec['tracks'] > 0:
        machine += [
            '        [ParameterDecl(Name = "Note", IsStateless = true, Description = "Note (off releases the track\'s note)")]',
            '        public void SetNote(Note value, int track) => Notes.SetNote(track, value.Value);',
            '',
            '        [ParameterDecl(Name = "Velocity", IsStateless = true, MinValue = 1, MaxValue = 127, '
            'Description = "Velocity of the note on the same row (100 when empty)")]',
            '        public void SetVelocity(int value, int track) => Notes.SetVelocity(track, value);',
            '']
    if machine[-1] == '':
        machine.pop()
    machine += ['    }', '}', '']

    os.makedirs(os.path.join(folder, 'Engine'), exist_ok=True)
    for name, lines in (('Engine/Params.g.cs', engine), ('Machine.Params.g.cs', machine)):
        out = os.path.join(folder, name)
        text = '\n'.join(lines)
        with open(out, 'w', encoding='utf-8', newline='\n') as f:
            f.write(text)
        print(f'wrote {out} ({len(params)} parameters)')


def main(argv):
    root = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
    folders = argv
    if not folders or folders == ['--all']:
        mroot = os.path.join(root, 'Machines')
        folders = [os.path.join(mroot, d) for d in sorted(os.listdir(mroot))
                   if os.path.exists(os.path.join(mroot, d, 'Params.def'))]
    for folder in folders:
        generate(folder)


if __name__ == '__main__':
    main(sys.argv[1:])
