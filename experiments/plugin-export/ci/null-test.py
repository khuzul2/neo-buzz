#!/usr/bin/env python3
# Null test of the exported Neo Filter (CLAP and VST3) against the machine itself, in our own plug-in host (Neo Plug FX,
# docs/specs/plugin-host.md) inside rebuzz-headless, driven over MCP like tools/plugin-host-test.py.
#
# One Neo Poly feeds four filters in parallel: the native Neo Filter and the exported one as CLAP and as VST3 (the VST3 is
# clap-wrapper's module around the same CLAP), all with the same parameters. One render captures each filter's output as a probe;
# the probes are subtracted. Then the song is saved, reopened and rendered again: the exported filter must come back the same.
#
#   python3 null-test.py <rebuzz-headless> <work folder> --dir <plug-in folder> --lib neoplug.so --scan neoplug-scan
#
# Results: <work>/null-test.json and a table on stdout. Exit 1 if a check fails.
import argparse, glob, json, math, os, queue, struct, subprocess, sys, threading

ap = argparse.ArgumentParser()
ap.add_argument("exe"); ap.add_argument("work")
ap.add_argument("--dir", action="append", default=[]); ap.add_argument("--lib"); ap.add_argument("--scan")
ap.add_argument("--tolerance", type=float, default=3e-6, help="largest allowed |difference| (full scale 1.0): -110 dBFS")
args = ap.parse_args()
work = os.path.abspath(args.work); os.makedirs(work, exist_ok=True)
stderr_path = os.path.join(work, "stderr.txt"); result_path = os.path.join(work, "null-test.json")
failures, results = [], {"checks": [], "nulls": {}}


def check(ok, what):
    print(("ok   " if ok else "FAIL ") + what, flush=True)
    results["checks"].append({"ok": bool(ok), "what": what})
    if not ok: failures.append(what)


env = dict(os.environ)
env["NEOBUZZ_PLUGIN_DIR"] = os.path.join(work, "plugin-data")
env["NEOBUZZ_PLUGIN_PATH"] = os.pathsep.join(os.path.abspath(d) for d in args.dir)
env["NEOBUZZ_PLUGIN_STANDARD"] = "0"
if args.lib: env["NEOPLUG_LIB"] = os.path.abspath(args.lib)
if args.scan: env["NEOPLUG_SCAN"] = os.path.abspath(args.scan)
env["REBUZZ_NATIVE_MACHINES"] = "off"
env.setdefault("NEOPLUG_DEBUG", "1")
env["XDG_CONFIG_HOME"] = os.path.join(work, "config"); env["APPDATA"] = os.path.join(work, "appdata")
proc = subprocess.Popen([args.exe, "--agent", "--settings", os.path.join(work, "settings")], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=open(stderr_path, "w"), text=True, bufsize=1, env=env)
next_id = 0
lines = queue.Queue()
threading.Thread(target=lambda: [lines.put(l) for l in iter(proc.stdout.readline, "")] + [lines.put("")], daemon=True).start()


def finish(extra):
    tail = open(stderr_path, encoding="utf-8", errors="replace").read()[-4000:]
    json.dump({"ok": False, "failures": failures + [extra], "checks": results["checks"], "stderr_tail": tail}, open(result_path, "w"), indent=1)
    return tail


def request(method, params=None, timeout=600):
    global next_id
    next_id += 1
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params or {}}) + "\n"); proc.stdin.flush()
    try: line = lines.get(timeout=timeout)
    except queue.Empty:
        proc.kill(); print(f"FAIL {method} timed out; stderr:\n{finish(method + ' timed out')}"); sys.exit(1)
    if not line: raise SystemExit("rebuzz-headless closed stdout; stderr:\n" + finish("closed stdout"))
    return json.loads(line)


def call(tool, **a):
    r = request("tools/call", {"name": tool, "arguments": a})
    res = r.get("result") or {}
    text = res.get("content", [{}])[0].get("text", "") if res else json.dumps(r.get("error"))
    print(f"     {tool}: {text[:220]}", flush=True)
    return (not res.get("isError", True)) and "error" not in r, text, res.get("structuredContent") or {}


def read_wav(path):
    """32-bit float or 16/24-bit PCM WAV -> (channels, [list per channel])."""
    d = open(path, "rb").read()
    assert d[:4] == b"RIFF" and d[8:12] == b"WAVE", path
    pos, fmt, data = 12, None, None
    while pos + 8 <= len(d):
        cid, size = d[pos:pos + 4], struct.unpack("<I", d[pos + 4:pos + 8])[0]
        body = d[pos + 8:pos + 8 + size]
        if cid == b"fmt ": fmt = struct.unpack("<HHIIHH", body[:16])
        elif cid == b"data": data = body
        pos += 8 + size + (size & 1)
    tag, ch, rate, _, _, bits = fmt
    if tag in (3, 0xFFFE) and bits == 32: vals = struct.unpack("<%df" % (len(data) // 4), data[:len(data) // 4 * 4])
    else: raise SystemExit(f"unsupported wav format {tag}/{bits} in {path}")
    return ch, [list(vals[c::ch]) for c in range(ch)]


def where(a_path, b_path, rate=44100):
    """Diagnostics: where in time the two probes differ (per 0.25 s window: differing samples, largest difference)."""
    _, a = read_wav(a_path); _, b = read_wav(b_path)
    n = min(len(a[0]), len(b[0])); win = rate // 4
    out = []
    for w in range(0, n, win):
        cnt, mx = 0, 0.0
        for c in range(2):
            for i in range(w, min(n, w + win)):
                d = abs(a[c][i] - b[c][i])
                if d > 0: cnt += 1
                mx = max(mx, d)
        pk = max(abs(a[0][i]) for i in range(w, min(n, w + win)))
        out.append("%.2fs:%d/%.2g/pk%.2g" % (w / rate, cnt, mx, pk))
    first = next((i for i in range(n) if a[0][i] != b[0][i] or a[1][i] != b[1][i]), None)
    head = ""
    if first is not None:
        head = " first differing frame %d: native %r %r, exported %r %r" % (first, a[0][first], a[1][first], b[0][first], b[1][first])
    return "windows (differing samples / max diff / native peak): " + " ".join(out) + head


def null(a_path, b_path, skip_seconds=0.0, rate=44100):
    ca, a = read_wav(a_path); cb, b = read_wav(b_path)
    n = min(len(a[0]), len(b[0]))
    k0 = int(skip_seconds * rate)
    a = [ch[k0:n] for ch in a]; b = [ch[k0:n] for ch in b]; n = n - k0
    peak_a = max(max(abs(x) for x in ch[:n]) for ch in a)
    worst, diff_samples, sumsq, sumsq_a = 0.0, 0, 0.0, 0.0
    for c in range(min(ca, cb)):
        for i in range(n):
            d = abs(a[c][i] - b[c][i])
            if d > 0: diff_samples += 1
            if d > worst: worst = d
            sumsq += d * d; sumsq_a += a[c][i] * a[c][i]
    rms_db = 10 * math.log10(sumsq / max(sumsq_a, 1e-30)) if sumsq > 0 else float("-inf")
    return {"samples": n * min(ca, cb), "len_a": len(a[0]), "len_b": len(b[0]), "peak_native": peak_a, "max_abs_diff": worst, "max_abs_diff_dbfs": 20 * math.log10(worst) if worst > 0 else float("-inf"),
            "differing_samples": diff_samples, "residual_rel_rms_db": rms_db, "bit_identical": diff_samples == 0 and len(a[0]) == len(b[0])}


init = request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "px0", "version": "1"}})
check(init.get("result", {}).get("serverInfo", {}).get("name") == "neobuzz-live", "initialize")
proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n"); proc.stdin.flush()

ok, _, data = call("gear_list", search="", limit=1000)
fx_names = []
for g in data.get("gear", []):
    if g["name"] == "Neo Plug FX": fx_names = g.get("plugins") or []
mine = [n for n in fx_names if "neo filter" in n.lower()]
print("     Neo Plug FX lists:", mine)
clap = next((n for n in mine if "(CLAP)" in n), None); vst3 = next((n for n in mine if "(VST3)" in n), None)
check(clap is not None, "the scan found Neo Filter as CLAP")
check(vst3 is not None, "the scan found Neo Filter as VST3 (clap-wrapper)")
results["plugins"] = {"clap": clap, "vst3": vst3}
if not clap:
    print(finish("no CLAP")); sys.exit(1)

call("song_set", tempo_bpm=120, song_end_beats=8)
check(call("machine_add", gear="Neo Poly", name="Src")[0], "Neo Poly source")
call("pattern_create", machine="Src", name="A", length_beats=8)
check(call("notes_write", machine="Src", pattern="A", notes=[{"pitch": "C3", "start": 0, "length": 4}, {"pitch": "G3", "start": 0, "length": 4}, {"pitch": "D#4", "start": 0, "length": 4},
                                                         {"pitch": "A#3", "start": 4, "length": 3}, {"pitch": "F4", "start": 4, "length": 3}])[0], "notes")
call("arrangement_place", machine="Src", pattern="A", at=[0])
check(call("machine_add", gear="Neo Filter", name="NativeF", insert_after="Src")[0], "native Neo Filter after the source")
filters = ["NativeF"]
for nm, plugin in (("ClapF", clap), ("VstF", vst3)):
    if not plugin: continue
    ok, _, _ = call("machine_add", gear="Neo Plug FX", plugin=plugin, name=nm)
    check(ok, f"machine_add {nm} ({plugin})")
    if ok:
        check(call("connect", **{"from": "Src", "to": nm})[0], f"connect Src -> {nm}")
        call("connect", **{"from": nm, "to": "Master"})
        filters.append(nm)

def render(label):
    ok, text, r = call("render", **{"from": 0, "to": 8, "tail_seconds": 1, "probes": filters, "name": label})
    check(ok and not (r.get("measure") or {}).get("silent", True), f"render {label} is audible")
    return probe_files(r)


def probe_files(r):
    base = os.path.splitext(r["file"])[0]
    out = {}
    for nm in filters:
        hits = glob.glob(base + "-" + nm + ".wav") or glob.glob(base + "-*" + nm + "*.wav")
        out[nm] = hits[0] if hits else None
    return out


def compare(pf, tag, skip=0.0):
    check(all(pf.values()), f"{tag}: probe files found")
    for nm in filters[1:]:
        if not (pf.get(nm) and pf.get("NativeF")): continue
        full = null(pf["NativeF"], pf[nm]); res = null(pf["NativeF"], pf[nm], skip) if skip else full
        results["nulls"][f"{tag}/{nm}"] = {"whole": full, "after_start": res if skip else None, "skip_seconds": skip}
        print("     " + where(pf["NativeF"], pf[nm]))
        print(f"     {tag}: {nm} - NativeF whole render: max |diff| {full['max_abs_diff']:.3g} ({full['max_abs_diff_dbfs']:.1f} dBFS), {full['differing_samples']}/{full['samples']} samples differ, "
              f"bit identical: {full['bit_identical']}, native peak {full['peak_native']:.3f}")
        if skip: print(f"     {tag}: {nm} - NativeF after {skip} s: max |diff| {res['max_abs_diff']:.3g} ({res['max_abs_diff_dbfs']:.1f} dBFS), {res['differing_samples']}/{res['samples']} samples differ")
        check(full["peak_native"] > 0.01, f"{tag}: {nm}: the native signal is audible (peak {full['peak_native']:.3f})")
        check(res["max_abs_diff"] <= args.tolerance, f"{tag}: {nm} nulls against the native machine within {args.tolerance:g}" + (f" after {skip} s" if skip else "") + f" (max |diff| {res['max_abs_diff']:.3g})")


# 1. default parameters: nothing glides on either side
compare(render("defaults"), "defaults")

# 2. the same changed parameters on all: Type SVF LP, Cutoff 5000 (a slot's 32767 of 65534 is the same position), Resonance 500.
#    The engines glide from the defaults to them (a smoother of ~50 ms) and each host path starts that glide at its own block: the
#    first half second is a start-up transient and is reported, the rest has to null.
for nm in filters:
    slot = nm != "NativeF"
    ch = [{"param": "Type", "value": "raw:2"}, {"param": "Cutoff", "value": "raw:32767" if slot else "raw:5000"}, {"param": "Resonance", "value": "raw:32767" if slot else "raw:500"}]
    ok, _, _ = call("param_set", machine=nm, changes=ch)
    check(ok, f"{nm}: parameters set")
    ok, _, d = call("machine_describe", machine=nm, all=True)
    got = {p.get("name"): (p.get("value"), p.get("raw"), p.get("unit")) for p in d.get("parameters", [])}
    print(f"     {nm}: Cutoff={got.get('Cutoff')} Resonance={got.get('Resonance')} Type={got.get('Type')}")
    results.setdefault("params", {})[nm] = {k: got.get(k) for k in ("Cutoff", "Resonance", "Type")}
    if slot:
        check(got.get("Cutoff", (None, None, None))[2] not in (None, ""), f"{nm}: Cutoff shows a unit ({got.get('Cutoff')})")
compare(render("changed"), "changed", skip=0.5)

song = os.path.join(work, "px0.bmxml")
check(call("song_save", path=song, overwrite=True)[0], "song_save")
check(call("song_open", path=song, discard_unsaved=True)[0], "song_open")
compare(render("reopened"), "reopened", skip=0.5)

proc.stdin.close()
try: proc.wait(timeout=60)
except Exception: proc.kill()
json.dump({"ok": not failures, "failures": failures, "checks": results["checks"], "nulls": results["nulls"], "plugins": results.get("plugins"), "params": results.get("params"),
           "tolerance": args.tolerance}, open(result_path, "w"), indent=1)
print("NULL-TEST " + ("ok" if not failures else f"FAILED ({len(failures)}): " + "; ".join(failures)))
sys.exit(1 if failures else 0)
