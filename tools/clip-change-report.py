#!/usr/bin/env python3
# Fork (docs/specs/clips.md §5.6 and §10.1, waves CU1 to CU3): the change report. CU3 moves every lane of a clip to one player, so a song with
# tracker data may sound a little different; this says which songs, from which sample, by how much, and why.
#
# It compares the renders of two builds, the before (CU1's goldens: `clip-null.py render --audio <dir>` on a build before the change) and the
# after (the same on the build with it), song by song, for one platform. For each song:
#   - identical or not (the bytes of the two renders, or their hashes when only hashes are at hand);
#   - the first differing sample (a frame number, 0-based) and the time of it;
#   - the peak and the RMS of the difference in dB (full scale = 1.0), the RMS of the signal (the before render), and the difference's RMS
#     against the signal's, in dB: how loud the change is next to the music;
#   - the cause tags the migration log wrote for the song (tests: ClipRoundTripTests with CLIP_MIGRATION_LOG; docs/specs/clips.md §5.6).
# A song is flagged UNEXPLAINED when its render differs and its log has no tag that explains a change (a log line of kind "change" or "loss").
# A song with no render on one side is MISSING. The exit code is 1 if any song is unexplained or missing (--no-fail: always 0): an unexplained
# difference is investigated before the wave merges (§10.1). The report is for a person to read, not a pass/fail gate for tracker data.
#
#   python3 tools/clip-change-report.py --before <dir> --after <dir> --log migration-log.json [--platform windows-x64]
#          [--before-hashes eval/clip-goldens.json] [--after-hashes observed.json] [--only a,b] [--out report.json] [--markdown report.md] [--no-fail]
#
# <dir> holds one WAV a song, named as clip-null.py names a render: the song's name with "/" as "-" (repo-Bass House Template.bmx.wav,
# gen-midi-lanes.wav). The log is the JSON the round-trip tests write: {"version": 1, "songs": {name: [{"tag", "kind", "machine", "clip",
# "lane", "count", "detail"}]}}. Hash files are clip-null.py's observed.json or eval/clip-goldens.json (platforms.<platform>.songs.<name>.sha256).
# No audio is committed anywhere.

import array
import hashlib
import json
import math
import os
import struct
import sys

try:
    import numpy as np
except ImportError:      # the report runs without it, a little slower
    np = None

EXPLAINING = ("change", "loss")


# ---- WAV ---------------------------------------------------------------------------------------------------------------

def read_wav(path):
    """(sample rate, channels, samples as floats in -1..1, interleaved). PCM 8/16/24/32 and float 32/64, RIFF and WAVE_FORMAT_EXTENSIBLE."""
    with open(path, "rb") as f:
        data = f.read()
    if data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        raise ValueError(f"{path}: not a RIFF WAVE file")
    pos, fmt, body = 12, None, None
    while pos + 8 <= len(data):
        tag, size = data[pos:pos + 4], struct.unpack_from("<I", data, pos + 4)[0]
        chunk = data[pos + 8:pos + 8 + size]
        if tag == b"fmt ":
            fmt = struct.unpack_from("<HHIIHH", chunk, 0)
            if fmt[0] == 0xFFFE and len(chunk) >= 26:
                fmt = (struct.unpack_from("<H", chunk, 24)[0],) + fmt[1:]       # the sub-format's first two bytes: 1 PCM, 3 float
        elif tag == b"data":
            body = chunk if pos + 8 + size <= len(data) else data[pos + 8:]
        pos += 8 + size + (size & 1)
    if fmt is None or body is None:
        raise ValueError(f"{path}: no fmt or data chunk")
    tag, channels, rate, _, _, bits = fmt
    if tag == 3 and bits in (32, 64):
        code = "f" if bits == 32 else "d"
        n = len(body) // (bits // 8)
        values = struct.unpack("<%d%s" % (n, code), body[:n * (bits // 8)])
        samples = list(values)
    elif tag == 1 and bits == 16:
        n = len(body) // 2
        samples = [v / 32768.0 for v in struct.unpack("<%dh" % n, body[:n * 2])]
    elif tag == 1 and bits == 8:
        samples = [(v - 128) / 128.0 for v in body]
    elif tag == 1 and bits == 24:
        n = len(body) // 3
        samples = []
        for i in range(n):
            v = body[3 * i] | body[3 * i + 1] << 8 | body[3 * i + 2] << 16
            samples.append((v - (1 << 24) if v >= 1 << 23 else v) / 8388608.0)
    elif tag == 1 and bits == 32:
        n = len(body) // 4
        samples = [v / 2147483648.0 for v in struct.unpack("<%di" % n, body[:n * 4])]
    else:
        raise ValueError(f"{path}: format {tag} with {bits} bits is not supported")
    return rate, channels, samples


def db(x):
    return None if x <= 0 else 20.0 * math.log10(x)


def compare_samples(before, after, channels):
    """The difference of two sample lists (interleaved): first differing frame, peak, rms, the signal's rms, and the lengths in frames."""
    n = min(len(before), len(after))
    ch = max(1, channels)
    if np is not None:
        a = np.asarray(before[:n], dtype=np.float64)
        b = np.asarray(after[:n], dtype=np.float64)
        d = b - a
        nz = np.nonzero(d)[0]
        first = int(nz[0]) // ch if len(nz) else None
        peak = float(np.max(np.abs(d))) if n else 0.0
        rms = float(np.sqrt(np.mean(d * d))) if n else 0.0
        sig = float(np.sqrt(np.mean(a * a))) if n else 0.0
    else:
        first, peak, sq, ssig = None, 0.0, 0.0, 0.0
        for i in range(n):
            d = after[i] - before[i]
            if d != 0.0:
                if first is None:
                    first = i // ch
                if abs(d) > peak:
                    peak = abs(d)
            sq += d * d
            ssig += before[i] * before[i]
        rms = math.sqrt(sq / n) if n else 0.0
        sig = math.sqrt(ssig / n) if n else 0.0
    if first is None and len(before) != len(after):
        first = n // ch                                    # identical as far as both go: the difference starts where the shorter one ends
    return {"first_diff_frame": first, "peak": peak, "rms": rms, "signal_rms": sig, "frames_before": len(before) // ch, "frames_after": len(after) // ch}


# ---- the inputs --------------------------------------------------------------------------------------------------------

def label(name):
    return name.replace("/", "-")


def load_log(path):
    """{song: [entry]}; an entry without a kind counts as a change."""
    if not path:
        return {}
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    return data.get("songs", data)


def load_hashes(path, platform):
    """{song: sha256} from an observed.json or the goldens file, for one platform."""
    if not path:
        return {}
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    plat = data.get("platforms", {})
    entry = plat.get(platform) if platform else (next(iter(plat.values())) if len(plat) == 1 else None)
    if entry is None:
        raise SystemExit(f"{path} has no platform {platform!r}: {sorted(plat)}")
    return {n: e.get("sha256") for n, e in entry.get("songs", {}).items() if e.get("sha256")}


def file_hash(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def causes(entries):
    """The cause tags of a song's log that explain a change, with the events each covers: {tag: events}."""
    tags = {}
    for e in entries or []:
        if e.get("kind", "change") in EXPLAINING:
            tags[e["tag"]] = tags.get(e["tag"], 0) + int(e.get("count") or 0)
    return tags


# ---- the report --------------------------------------------------------------------------------------------------------

def report_song(name, before_dir, after_dir, before_hash, after_hash, log, rate_hint=None):
    entry = {"song": name, "tags": causes(log.get(name)), "info": sorted({e["tag"] for e in log.get(name, []) if e.get("kind") == "info"})}
    b = os.path.join(before_dir, label(name) + ".wav") if before_dir else None
    a = os.path.join(after_dir, label(name) + ".wav") if after_dir else None
    have_b, have_a = bool(b and os.path.exists(b)), bool(a and os.path.exists(a))
    bh = file_hash(b) if have_b else before_hash
    ah = file_hash(a) if have_a else after_hash
    if bh and ah and bh == ah:
        entry.update(status="identical", identical=True)
    elif have_b and have_a:
        rate_b, ch_b, sb = read_wav(b)
        rate_a, ch_a, sa = read_wav(a)
        if (rate_a, ch_a) != (rate_b, ch_b):
            entry.update(status="differs", identical=False, note=f"format {rate_b} Hz x{ch_b} against {rate_a} Hz x{ch_a}")
        else:
            c = compare_samples(sb, sa, ch_b)
            if c["first_diff_frame"] is None:
                entry.update(status="identical", identical=True, note="the samples are equal (the files are not)")
            else:
                entry.update(status="differs", identical=False, sample_rate=rate_b, first_diff_frame=c["first_diff_frame"],
                             first_diff_seconds=c["first_diff_frame"] / rate_b, peak_db=db(c["peak"]), rms_db=db(c["rms"]),
                             signal_rms_db=db(c["signal_rms"]),
                             relative_db=(db(c["rms"]) - db(c["signal_rms"])) if db(c["rms"]) is not None and db(c["signal_rms"]) is not None else None,
                             frames_before=c["frames_before"], frames_after=c["frames_after"])
    elif bh and ah:
        entry.update(status="differs", identical=False, note="the hashes differ and the audio is not at hand")
    else:
        entry.update(status="missing", identical=None, note="no render before" if not (have_b or bh) else "no render after")
    differs = entry["status"] == "differs"
    entry["explained"] = bool(entry["tags"]) if differs else None
    entry["unexplained"] = differs and not entry["tags"]
    return entry


def fmt_db(v):
    return "-inf" if v is None else f"{v:+.1f}"


def markdown(entries, platform):
    lines = [f"# Clip change report{f' ({platform})' if platform else ''}", "",
             "| Song | Result | First differing sample | Peak dB | RMS dB | RMS vs signal dB | Cause tags |", "|---|---|---|---|---|---|---|"]
    for e in entries:
        if e["status"] == "differs" and "first_diff_frame" in e:
            first = f"{e['first_diff_frame']} ({e['first_diff_seconds']:.3f} s)"
            row = [first, fmt_db(e["peak_db"]), fmt_db(e["rms_db"]), fmt_db(e.get("relative_db"))]
        else:
            row = ["", "", "", ""]
        result = "UNEXPLAINED" if e["unexplained"] else e["status"] + (" (explained)" if e["explained"] else "")
        tags = ", ".join(f"{t} {n}" for t, n in sorted(e["tags"].items())) or (e.get("note", "") if e["status"] != "identical" else "")
        lines.append(f"| {e['song']} | {result} | " + " | ".join(row) + f" | {tags} |")
    n = len(entries)
    ident = sum(1 for e in entries if e["status"] == "identical")
    diff = sum(1 for e in entries if e["status"] == "differs")
    unexpl = sum(1 for e in entries if e["unexplained"])
    miss = sum(1 for e in entries if e["status"] == "missing")
    lines += ["", f"{n} songs: {ident} identical, {diff} differ ({diff - unexpl} explained, {unexpl} unexplained), {miss} missing."]
    return "\n".join(lines) + "\n"


def option(argv, name, default=None):
    if name in argv:
        i = argv.index(name)
        value = argv[i + 1]
        del argv[i:i + 2]
        return value
    return default


def main(argv):
    argv = list(argv)
    no_fail = "--no-fail" in argv
    argv = [a for a in argv if a != "--no-fail"]
    before = option(argv, "--before")
    after = option(argv, "--after")
    log_path = option(argv, "--log")
    platform = option(argv, "--platform")
    before_hashes = option(argv, "--before-hashes")
    after_hashes = option(argv, "--after-hashes")
    only = option(argv, "--only")
    out = option(argv, "--out")
    md = option(argv, "--markdown")
    if argv or not (before or before_hashes) or not (after or after_hashes):
        print(__doc__ if False else "usage: clip-change-report.py --before <dir> --after <dir> [--log <migration-log.json>] [--platform <p>] "
              "[--before-hashes <json>] [--after-hashes <json>] [--only a,b] [--out <json>] [--markdown <md>] [--no-fail]")
        return 2
    log = load_log(log_path)
    bh, ah = load_hashes(before_hashes, platform), load_hashes(after_hashes, platform)
    names = set(log) | set(bh) | set(ah)
    for d in (before, after):
        if d and os.path.isdir(d):
            names |= {f[:-4] for f in os.listdir(d) if f.endswith(".wav")}
    # A WAV's name is the song's with "/" as "-": a name from a file stays as it is, unless the log or a hash file knows it with a "/".
    known = {label(n): n for n in set(log) | set(bh) | set(ah)}
    names = {known.get(n, n) for n in names}
    if only:
        wanted = set(only.split(","))
        names = {n for n in names if n in wanted}
    entries = [report_song(n, before, after, bh.get(n), ah.get(n), log) for n in sorted(names)]
    text = markdown(entries, platform)
    print(text)
    if out:
        with open(out, "w", encoding="utf-8") as f:
            json.dump({"version": 1, "platform": platform, "songs": {e["song"]: e for e in entries}}, f, indent=1, sort_keys=True)
            f.write("\n")
    if md:
        with open(md, "w", encoding="utf-8") as f:
            f.write(text)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write(text)
    bad = [e["song"] for e in entries if e["unexplained"] or e["status"] == "missing"]
    for n in bad:
        print(f"{'MISSING' if next(e for e in entries if e['song'] == n)['status'] == 'missing' else 'UNEXPLAINED'}: {n}", file=sys.stderr)
    return 0 if no_fail or not bad else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
