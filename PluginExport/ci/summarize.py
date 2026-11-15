#!/usr/bin/env python3
"""RESULT/GCCFG/RT/WORST lines of clapbench -> markdown tables: python3 summarize.py results.txt [...]"""
import re, sys

def parse(line):
    return dict(re.findall(r'(\w+)=(\S+)', line))

def main(paths):
    rows, cfg, rt, worst = [], {}, {}, {}
    for p in paths:
        for line in open(p, encoding='utf-8', errors='replace'):
            line = line.strip()
            if line.startswith('RESULT '): rows.append(parse(line))
            elif line.startswith('GCCFG '): cfg[parse(line).get('label', '')] = line[6:]
            elif line.startswith('RT '): rt[parse(line).get('label', '')] = line[3:]
            elif line.startswith('WORST '): d = parse(line); worst.setdefault(d['label'], []).append(d)
    names = {'0': 'none', '1': '1 MB/s', '2': '20 MB/s', '3': '20 MB/s + 256 MB gen2'}
    print('### Audio-thread block times (us), one callback = all instances\n')
    print('| label | where the load runs | load | callbacks | mean | p99 | p99.9 | p99.99 | worst | misses | non-GC p99.9 | stalls > non-GC p99.9 + 0.5 ms (GC callbacks) | worst GC callback |')
    print('|---|---|---|---|---|---|---|---|---|---|---|---|---|')
    for r in rows:
        print('| {label} | {mode} | {load} | {callbacks} | {mean_us} | {p99_us} | {p999_us} | {p9999_us} | {worst_us} | {misses} | {base_p999_us} | {gc_attrib_stalls} | {worst_gc_cb_us} |'.format(**{**r, 'load': names.get(r['load'], r['load'])}))
    print('\n### Garbage collections during the recording, per runtime\n')
    print('| label | DSP runtime: GCs (g1/g2) | DSP pause total ms | DSP pause max ms | DSP alloc MB | callbacks overlapping a DSP GC | editor runtime: GCs (g1/g2) | editor pause total ms | editor pause max ms | editor alloc MB | callbacks overlapping an editor GC (1 ms sampler) |')
    print('|---|---|---|---|---|---|---|---|---|---|---|')
    for r in rows:
        print('| {label} | {dsp_gc} ({dsp_gc1}/{dsp_gc2}) | {dsp_pause_total_ms} | {dsp_pause_max_ms} | {dsp_alloc_mb} | {dsp_gc_cbs} | {ed_gc} ({ed_gc1}/{ed_gc2}) | {ed_pause_total_ms} | {ed_pause_max_ms} | {ed_alloc_mb} | {ed_gc_cbs} |'.format(**r))
    print('\n### Runner scheduling and priority\n')
    for r in rows:
        print('- {label}: start lateness p99.9 = {late_p999_us} us, max = {late_max_us} us, resyncs {resyncs}; {rt}'.format(rt=rt.get(r['label'], ''), **r))
    print('\n### GC settings seen by the DSP runtime\n')
    for k, v in cfg.items(): print('- ' + v)
    print('\n### Worst callbacks (top 3 per run: duration us, GC in the DSP runtime, GC in the editor runtime)\n')
    for k, v in worst.items():
        print('- {}: {}'.format(k, '; '.join('{} us dspgc={} edgc={}'.format(w['dur_us'], w['dspgc'], w['edgc']) for w in v[:3])))

if __name__ == '__main__':
    main(sys.argv[1:])
