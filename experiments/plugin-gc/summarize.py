#!/usr/bin/env python3
"""Turns the harness's RESULT/GCCFG/RT/WORST lines into markdown tables (printed to the job log)."""
import re, sys

def parse(line):
    return dict(re.findall(r'(\w+)=(\S+)', line))

def main(paths):
    rows, cfg, rt, worst = [], {}, {}, {}
    for p in paths:
        for line in open(p, encoding='utf-8', errors='replace'):
            line = line.strip()
            if line.startswith('RESULT '):
                rows.append(parse(line))
            elif line.startswith('GCCFG '):
                d = parse(line); cfg[d.get('label', '')] = line[6:]
            elif line.startswith('RT '):
                d = parse(line); rt[d.get('label', '')] = line[3:]
            elif line.startswith('WORST '):
                d = parse(line); worst.setdefault(d['label'], []).append(d)
    names = {'0': 'none', '1': '1 MB/s', '2': '20 MB/s', '3': '20 MB/s + 256 MB gen2'}
    print('### Callback timing (us) and GC, per configuration\n')
    print('| label | block | load | callbacks | mean | p99 | p99.9 | p99.99 | worst | misses | misses w/ GC | stalls>0.5ms (GC / all) | worst GC cb | worst non-GC cb | GCs (g1/g2) | pause total ms | pause max ms |')
    print('|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
    for r in rows:
        print('| {label} | {block} | {load} | {callbacks} | {mean_us} | {p99_us} | {p999_us} | {p9999_us} | {worst_us} | {misses} | {misses_gc} | {s_gc} / {s_all} | {worst_gc_cb_us} | {worst_nongc_cb_us} | {gc_count} ({gc_gen1}/{gc_gen2}) | {pause_total_ms} | {pause_max_ms} |'.format(
            s_gc=r['stalls500_gc'], s_all=r['stalls500_all'], **{**r, 'load': names.get(r['load'], r['load'])}))
    print('\n### Runner scheduling (start lateness, us) and priority\n')
    for r in rows:
        print('- {label}: late p99.9 = {late_p999_us}, late max = {late_max_us}; {rt}'.format(rt=rt.get(r['label'], ''), **r))
    print('\n### GC configuration seen by the plugin\n')
    for k, v in cfg.items():
        print('- ' + v)
    print('\n### Worst callbacks (top 3 per run: duration us, GC during callback)\n')
    for k, v in worst.items():
        print('- {}: {}'.format(k, '; '.join('{} us gc={}'.format(w['dur_us'], w['gc']) for w in v[:3])))

if __name__ == '__main__':
    main(sys.argv[1:])
