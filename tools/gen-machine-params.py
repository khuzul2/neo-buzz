#!/usr/bin/env python3
"""Generates a Neo machine's parameter code from its Params.def (docs/specs/machines.md §3).

    python3 tools/gen-machine-params.py Machines/NeoDrive [Machines/NeoComp ...]
    python3 tools/gen-machine-params.py --all
    python3 tools/gen-machine-params.py --all --check    (CI: writes nothing; fails if a generated file is out of date)

Writes, next to Params.def:
  Engine/Params.g.cs     the pure table (indices, ranges, mappings, value text): engine, tests, editor
  Machine.Params.g.cs    the machine's [ParameterDecl] properties (and Note/Velocity track parameters)
  <Name>.machine.json    the manifest (docs/specs/mcp-server.md §8.3): every parameter with its unit,
                         mapping, choices and description, for the agent server. Copied next to the DLL.

Params.def lines:
  namespace NeoBuzz.Drive          C# namespace
  class NeoDriveMachine            the machine class (a partial class)
  tracks 16                        optional: Note and Velocity track parameters for 16 tracks
  track Id "Name" min max | what   optional: another track parameter after Velocity (stateless: a value plays, an empty
  track Id "Name" switch | what    cell does nothing), passed to the machine's OnTrackParameter(TP.Id, track, value)
  summary What it is. Use it for…   optional: the machine in a sentence or two (the manifest's summary)
  import kind .ext[,.ext] | what     optional: a data file the machine loads (machine_data_import)
  notekey 36 "Kick" [Group]        optional: a key that makes a sound, with its name (and group): the manifest's note_map, the piano
  notekey 48 60 [Group]            roll's key names and Fold: playable (docs/specs/daw-modes.md §8.9); a second number makes a range of
                                   unnamed keys. For a machine with a fixed map and no code for it
  group Shape                      group shown in the editor for the following parameters
  reading key unit | what           optional: a value the machine measures (its public Readings, machine_readings)
  Id  "Name"  min max def  kind args...  | description
    id snake_case_id                 optional, indented, after a parameter: the id the agent and the manifest use (default: Id)
    how One sentence: how the control changes the sound      Explain text (neo-learn.md §5.3); the description defaults to it
    sound One sentence: what you hear
    was "Old name"                   optional, indented, once per former name: the name the parameter had before a rename. A user
                                     preset saved before presets stored ids names it so (BuzzGUI.Common's Preset matches it while the
                                     preset doesn't know the current name); the id, the index and saved state never change with a rename

Kinds and their arguments:
  lin A B [unit] [digits]          A + t(B-A)
  exp A B [unit] [digits] [detent=N]  A (B/A)^t; detent=N: the value rests on multiples of N (within 1.5%)
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
import json
import os
import re
import shlex
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import machine_catalog as mc  # noqa: E402  (category, tags and use_for for every manifest)

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
    spec = {'namespace': None, 'class': None, 'tracks': 0, 'summary': '', 'imports': [], 'params': [], 'track_params': [], 'note_map': []}
    group = ''
    with open(path, encoding='utf-8') as f:
        for lineno, line in enumerate(f, 1):
            line = line.rstrip('\n')
            if not line.strip() or line.lstrip().startswith('#'):
                continue
            if line[0] in ' \t' and line.split(None, 1)[0] in ('id', 'how', 'sound', 'was'):
                if not spec['params']:
                    sys.exit(f'{path}:{lineno}: {line.split(None, 1)[0]} needs a parameter before it')
                key, _, rest = line.strip().partition(' ')
                if key == 'was':
                    spec['params'][-1].setdefault('was', []).extend(shlex.split(rest))
                    continue
                spec['params'][-1]['pubid' if key == 'id' else key] = rest.strip()
                continue
            if line.lstrip().startswith('summary '):
                spec['summary'] = line.strip()[len('summary '):].strip()
                continue
            if line.lstrip().startswith('import '):
                head, _, what = line.strip()[len('import '):].partition('|')
                parts = head.split()
                spec['imports'].append({'kind': parts[0], 'extensions': [e.lower() for e in (parts[1].split(',') if len(parts) > 1 else [])],
                                        'description': what.strip()})
                continue
            if line.lstrip().startswith('notekey '):
                toks = shlex.split(line, comments=True)[1:]
                if len(toks) < 1 or not toks[0].isdigit() or not 0 <= int(toks[0]) <= 127:
                    sys.exit(f'{path}:{lineno}: expected notekey KEY "Name" [Group], or notekey FROM TO [Group] (keys 0 to 127)')
                entry = {'key': int(toks[0])}
                rest = toks[1:]
                if rest and rest[0].isdigit():
                    entry['to'] = int(rest[0])
                    if not entry['key'] <= entry['to'] <= 127:
                        sys.exit(f'{path}:{lineno}: a note range runs up from its first key to at most 127')
                    rest = rest[1:]
                elif rest:
                    entry['name'] = rest[0]
                    rest = rest[1:]
                if rest:
                    entry['group'] = rest[0]
                spec['note_map'].append(entry)
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
            if head == 'track':
                if len(toks) == 4 and toks[3] == 'switch':
                    lo, hi, switch = 0, 1, True
                elif len(toks) == 5:
                    lo, hi, switch = int(toks[3]), int(toks[4]), False
                else:
                    sys.exit(f'{path}:{lineno}: expected track Id "Name" min max, or track Id "Name" switch')
                if not (0 <= lo <= hi <= 254):
                    sys.exit(f'{path}:{lineno}: need 0 <= min <= max <= 254')
                spec['track_params'].append({'id': toks[1], 'name': toks[2], 'min': lo, 'max': hi, 'switch': switch, 'desc': desc or toks[2]})
                continue
            if head == 'group':
                group = ' '.join(toks[1:])
                continue
            if head == 'reading':
                spec.setdefault('readings', []).append({'key': toks[1], 'unit': toks[2] if len(toks) > 2 else '', 'description': desc})
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
    if spec['track_params'] and spec['tracks'] <= 0:
        sys.exit(f'{path}: track parameters need tracks')
    for p in spec['params']:
        if p.get('how') and p['desc'] == p['name']:
            p['desc'] = p['how']
    ids = [p.get('pubid', p['id']) for p in spec['params']]
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
        rest = []
        for t in a[2:]:
            if t.startswith('detent='):
                named.append(f'detent: {num(t[7:])}')   # exp only: the value rests on multiples of this (neo-learn.md 5.2)
            else:
                rest.append(t)
        take_unit_digits(rest)
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
    if p.get('how'):
        named.append(f"how: {cs_string(p['how'])}")
    if p.get('sound'):
        named.append(f"sound: {cs_string(p['sound'])}")
    if p.get('was'):
        named.append('formerNames: new[] { ' + ', '.join(cs_string(n) for n in p['was']) + ' }')
    args = [f"P.{p['id']}", cs_string(p.get('pubid', p['id'])), cs_string(p['name']), str(p['min']), str(p['max']), str(p['def']),
            f"MapKind.{KINDS[kind]}"]
    if pos:
        args += [f'a: {pos[0]}', f'b: {pos[1]}']
    args += named
    return 'new ParamDef(' + ', '.join(args) + ')'


MANIFEST_KINDS = {
    'lin': 'linear', 'exp': 'exp', 'pow': 'power', 'db': 'db', 'time': 'time', 'freq': 'freq',
    'pct': 'percent', 'bip': 'bipolar', 'int': 'int', 'enum': 'enum', 'switch': 'switch', 'ratio': 'ratio',
}
UNITS = {'db': 'dB', 'time': 's', 'freq': 'Hz', 'pct': '%', 'bip': '%', 'ratio': ':1'}


def manifest_param(index, p):
    """One parameter of the manifest: the same mapping as table_entry, as JSON."""
    kind, a = p['kind'], p['args']
    m = {'index': index, 'id': p.get('pubid', p['id']), 'name': p['name'], 'group': p['group'], 'min': p['min'], 'max': p['max'],
         'default': p['def'], 'kind': MANIFEST_KINDS[kind]}
    unit = UNITS.get(kind, '')
    num_or_digits = lambda t: t.lstrip('-').isdigit()
    if kind in ('lin', 'exp'):
        m['a'], m['b'] = float(a[0]), float(a[1])
        rest = [t for t in a[2:] if not num_or_digits(t) and not t.startswith('detent=')]
        unit = rest[0] if rest else ''
    elif kind == 'pow':
        m['a'], m['b'], m['exponent'] = float(a[0]), float(a[1]), float(a[2])
        rest = [t for t in a[3:] if not num_or_digits(t)]
        unit = rest[0] if rest else ''
    elif kind in ('db', 'time', 'freq', 'ratio'):
        m['a'], m['b'] = float(a[0]), float(a[1])
        if 'inf' in a[2:]:
            m['flag'] = True
    elif kind == 'int':
        m['a'] = float(a[0] if a else 0)
        rest = a[1:]
        if 'signed' in rest:
            m['flag'] = True
            rest = [t for t in rest if t != 'signed']
        unit = rest[0] if rest else ''
    elif kind == 'enum':
        m['choices'] = [t.replace('_', ' ') for t in a]
    elif kind == 'switch':
        m['choices'] = [t.replace('_', ' ') for t in a[:2]] if a else ['Off', 'On']
    if unit:
        m['unit'] = unit
    m['description'] = p['desc']
    if p.get('how'):
        m['how'] = p['how']
    if p.get('sound'):
        m['sound'] = p['sound']
    return m


def gear_of(folder):
    """The machine's gear name and kind ("generator" or "effect").

    Since the clean break (CB1) a Neo machine's folder holds only its sources; its builds are the studio and portable
    projects, Machines/Studio/<X>/<X>.Studio.csproj and Machines/Portable/<X>/<X>.Portable.csproj, which name the folder
    (NeoFolder), the gear (NeoName) and its kind (NeoKind: Generators or Effects). A folder with its own csproj
    (AssemblyName "Neo Poly.NET", output under Gear/Generators) still works, and the manifest already there is the last
    resort for a folder with neither."""
    name = os.path.basename(os.path.normpath(folder))
    machines = os.path.dirname(os.path.normpath(os.path.abspath(folder)))
    found = []
    for flavour in ('Studio', 'Portable'):
        base = os.path.join(machines, flavour)
        if not os.path.isdir(base):
            continue
        for d in sorted(os.listdir(base)):
            sub = os.path.join(base, d)
            if not os.path.isdir(sub):
                continue
            for f in sorted(os.listdir(sub)):
                if not f.endswith('.csproj'):
                    continue
                text = open(os.path.join(sub, f), encoding='utf-8-sig').read()
                nf = re.search(r'<NeoFolder>([^<]+)</NeoFolder>', text)
                gear = re.search(r'<NeoName>([^<]+)</NeoName>', text)
                kind = re.search(r'<NeoKind>([^<]+)</NeoKind>', text)
                if nf and nf.group(1).strip() == name and gear and kind:
                    found.append((gear.group(1).strip(), 'generator' if kind.group(1).strip() == 'Generators' else 'effect',
                                  os.path.join(flavour, d, f)))
    if found:
        if len({(g, k) for g, k, _ in found}) > 1:
            sys.exit(f'{folder}: its projects disagree on the gear: ' + ', '.join(f'{p} says {g} ({k})' for g, k, p in found))
        return found[0][0], found[0][1]
    for f in sorted(os.listdir(folder)):
        if f.endswith('.csproj'):
            text = open(os.path.join(folder, f), encoding='utf-8-sig').read()
            m = re.search(r'<AssemblyName>([^<]+)</AssemblyName>', text)
            if m:
                gear = re.sub(r'\.NET$', '', m.group(1))
                kind = 'generator' if 'Gear\\Generators' in text or 'Gear/Generators' in text else 'effect'
                return gear, kind
    for f in sorted(os.listdir(folder)):
        if f.endswith('.machine.json'):
            doc = json.load(open(os.path.join(folder, f), encoding='utf-8-sig'))
            if doc.get('gear') and doc.get('kind') in ('generator', 'effect'):
                return doc['gear'], doc['kind']
    sys.exit(f'{folder}: no studio or portable project names it (NeoFolder {name}), no csproj with an AssemblyName '
             f'and no manifest to take the gear from')


CHECK = False          # --check: compare instead of writing
STALE = []             # files --check found out of date


def emit(out, text, what=''):
    """Writes a generated file, or with --check compares it with the file on disk."""
    if CHECK:
        try:
            with open(out, encoding='utf-8', newline='') as f:
                same = f.read() == text
        except FileNotFoundError:
            same = False
        if not same:
            STALE.append(out)
        return
    with open(out, 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)
    print(f'wrote {out}{what}')


def write_manifest(folder, spec, rel):
    gear, kind = gear_of(folder)
    doc = {
        'format': 'rebuzz-machine-manifest/1',
        'gear': gear,
        'kind': kind,
        'source': f'generated by tools/gen-machine-params.py from {rel}',
        'summary': spec['summary'],
        'tracks': spec['tracks'],
        'track_parameters': ['Note', 'Velocity'] + [t['name'] for t in spec['track_params']] if spec['tracks'] > 0 else [],
        'parameters': [manifest_param(i, p) for i, p in enumerate(spec['params'])],
        **({'readings': spec['readings']} if spec.get('readings') else {}),
    }
    if spec['imports']:
        doc['imports'] = spec['imports']
    if spec['note_map']:
        doc['note_map'] = spec['note_map']
    mc.apply(doc)
    out = os.path.join(folder, gear + '.machine.json')
    emit(out, json.dumps(doc, ensure_ascii=False, indent=1) + '\n')


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
    engine += ['        };', '    }']
    if spec['track_params']:
        engine += ['', '    /// <summary>Track parameters after Note and Velocity, in declaration order (OnTrackParameter).</summary>',
                   '    internal static class TP', '    {']
        for i, t in enumerate(spec['track_params']):
            engine.append(f"        public const int {t['id']} = {i};")
        engine += [f"        public const int Count = {len(spec['track_params'])};", '    }']
    engine += ['}', '']

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
        for t in spec['track_params']:
            if t['switch']:
                machine += [f"        [ParameterDecl(Name = {cs_string(t['name'])}, IsStateless = true, Description = {cs_string(t['desc'])})]",
                            f"        public void Set{t['id']}(bool value, int track) => OnTrackParameter(TP.{t['id']}, track, value ? 1 : 0);", '']
            else:
                machine += [f"        [ParameterDecl(Name = {cs_string(t['name'])}, IsStateless = true, MinValue = {t['min']}, MaxValue = {t['max']}, "
                            f"Description = {cs_string(t['desc'])})]",
                            f"        public void Set{t['id']}(int value, int track) => OnTrackParameter(TP.{t['id']}, track, value);", '']
    if machine[-1] == '':
        machine.pop()
    machine += ['    }', '}', '']

    os.makedirs(os.path.join(folder, 'Engine'), exist_ok=True)
    for name, lines in (('Engine/Params.g.cs', engine), ('Machine.Params.g.cs', machine)):
        out = os.path.join(folder, name)
        text = '\n'.join(lines)
        emit(out, text, f' ({len(params)} parameters)')
    write_manifest(folder, spec, rel)


def main(argv):
    global CHECK
    root = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
    CHECK = '--check' in argv
    folders = [a for a in argv if a != '--check']
    if not folders or folders == ['--all']:
        mroot = os.path.join(root, 'Machines')
        folders = [os.path.join(mroot, d) for d in sorted(os.listdir(mroot))
                   if os.path.exists(os.path.join(mroot, d, 'Params.def'))]
    for folder in folders:
        generate(folder)
    if CHECK:
        if STALE:
            print('out of date (run python3 tools/gen-machine-params.py on its folder and commit the result):')
            for out in STALE:
                print('  ' + os.path.relpath(out, root).replace('\\', '/'))
            sys.exit(1)
        print(f'{len(folders)} machines: every generated file is up to date')


if __name__ == '__main__':
    main(sys.argv[1:])
