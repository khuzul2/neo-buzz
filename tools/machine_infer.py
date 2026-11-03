# Fork (docs/specs/mcp-server.md §12.2, U1): what a parameter's value texts mean. Given the
# [raw, text] table machine_describe(value_table=true) returns, infer the unit, the mapping
# (linear, exponential, dB, a table of points) and whether it is a choice or a switch, in the
# manifest's own terms (ReBuzz.Agent/Model/Manifest.cs). Where the texts are bare numbers, a unit
# can still come from the parameter's name or description ("in ms", "(1 ms – 4 s exponential)"),
# marked "confidence": "description". Pure, standard library only; tools/test_machine_infer.py.

import math
import re

# Unit words as machines write them, to (manifest unit, factor to it).
UNITS = {
    "hz": ("Hz", 1.0), "khz": ("Hz", 1000.0),
    "db": ("dB", 1.0), "dbfs": ("dB", 1.0), "dbu": ("dB", 1.0),
    "ms": ("s", 0.001), "msec": ("s", 0.001), "milliseconds": ("s", 0.001), "millisecond": ("s", 0.001),
    "s": ("s", 1.0), "sec": ("s", 1.0), "secs": ("s", 1.0), "seconds": ("s", 1.0), "second": ("s", 1.0),
    "%": ("%", 1.0), "percent": ("%", 1.0),
    "st": ("st", 1.0), "semi": ("st", 1.0), "semis": ("st", 1.0), "semitone": ("st", 1.0), "semitones": ("st", 1.0),
    "ct": ("ct", 1.0), "cent": ("ct", 1.0), "cents": ("ct", 1.0),
    "oct": ("oct", 1.0), "octave": ("oct", 1.0), "octaves": ("oct", 1.0),
    "bpm": ("bpm", 1.0), "x": ("x", 1.0), "°": ("°", 1.0), "deg": ("°", 1.0),
    "tick": ("ticks", 1.0), "ticks": ("ticks", 1.0),
}
UNIT_WORDS = sorted(UNITS, key=len, reverse=True)
NUMBER = r"[+-]?(?:\d+(?:[.,]\d*)?|[.,]\d+)(?:[eE][+-]?\d+)?"
TEXT_RE = re.compile(r"^\s*(" + NUMBER + r")\s*(" + "|".join(re.escape(u) for u in UNIT_WORDS) + r")?\.?(?![a-z])\s*(.*)$", re.I)
INF_RE = re.compile(r"^\s*([+-]?)\s*(?:inf(?:inity)?|∞)\.?\s*(" + "|".join(re.escape(u) for u in UNIT_WORDS) + r")?\b", re.I)
PAN_RE = re.compile(r"^\s*([LR])\s*(\d+(?:\.\d+)?)\s*%?\s*$", re.I)
RATIO_RE = re.compile(r"^\s*(\d+(?:\.\d*)?)\s*:\s*1\s*$")
# Tracker style: the raw value in hex, its meaning in brackets ("0040  (-54.2 dB)", "0000 (silent)").
HEX_RE = re.compile(r"^\s*(?:0x)?[0-9A-Fa-f]{2,4}\s+\((.+)\)\s*$")
SILENT = {"silent", "silence", "off", "mute", "muted"}


def normalise(text):
    t = (text or "").strip().replace("−", "-").replace("–", "-").replace(" ", " ")
    return re.sub(r"^([+-])\s+(?=\d)", r"\1", t)   # "− 0.5 dBFS"


def parse(text):
    """
    (value, unit, decimals, factor) for a value text, the value in the manifest's base unit (Hz, s,
    dB…) and factor what the text's unit is of it (kHz: 1000); None when the text isn't a number.
    """
    t = normalise(text)
    m = HEX_RE.match(t)
    if m:
        inner = m.group(1).strip()
        if inner.lower() in SILENT:
            return (-math.inf, "dB", 0, 1.0)
        return parse(inner)
    m = RATIO_RE.match(t)
    if m:
        return (float(m.group(1)), ":1", len(m.group(1).split(".")[1]) if "." in m.group(1) else 0, 1.0)
    m = INF_RE.match(t)
    if m:
        unit = UNITS.get((m.group(2) or "").lower(), ("", 1.0))[0]
        return (-math.inf if m.group(1) == "-" else math.inf, unit, 0, 1.0)
    m = PAN_RE.match(t)
    if m:
        v = float(m.group(2))
        return (-v if m.group(1).upper() == "L" else v, "%", len(m.group(2).split(".")[1]) if "." in m.group(2) else 0, 1.0)
    if t.upper() == "C":
        return (0.0, "%", 0, 1.0)
    m = TEXT_RE.match(t)
    if not m:
        return None
    rest = m.group(3).strip()
    # "0 ms (instant)", "2 ticks (0.125 bars @4TPB)": a note in brackets after the number is fine;
    # other words make it a name ("2 Bars", "1: Linear", "12 dB / FIR" are choices).
    if rest and not rest.startswith("("):
        return None
    number = m.group(1).replace(",", ".")
    mantissa = re.split(r"[eE]", number)[0]
    decimals = len(mantissa.split(".")[1]) if "." in mantissa else 0
    unit, factor = UNITS.get((m.group(2) or "").lower(), ("", 1.0))
    return (float(number) * factor, unit, decimals, factor)


def _resolution(parsed):
    """Half a step of the text's last digit, in base units ("1.6 kHz" → 50 Hz)."""
    value, unit, decimals, factor = parsed
    return 0.5 * 10 ** (-decimals) * factor


def _fits(points, f):
    """Whether f(raw) reproduces every point to its text's precision (or 1%)."""
    worst = 0.0
    for raw, value, res in points:
        if math.isinf(value):
            return False, math.inf
        guess = f(raw)
        err = abs(guess - value)
        tol = max(1.2 * res, 0.01 * abs(value), 1e-9)
        if not err <= tol:
            return False, err
        worst = max(worst, err / tol)
    return True, worst


def _line(xs, ys, ws=None):
    """Least squares y = c + slope·x (weighted when ws are given)."""
    ws = ws or [1.0] * len(xs)
    n = sum(ws)
    mx, my = sum(w * x for w, x in zip(ws, xs)) / n, sum(w * y for w, y in zip(ws, ys)) / n
    sxx = sum(w * (x - mx) ** 2 for w, x in zip(ws, xs))
    if sxx == 0:
        return my, 0.0
    slope = sum(w * (x - mx) * (y - my) for w, x, y in zip(ws, xs, ys)) / sxx
    return my - slope * mx, slope


def _candidates(finite, lo, hi, log):
    """(value at lo, value at hi) to try: least squares, weighted by each text's precision, and through the end points."""
    xs = [p[0] for p in finite]
    ys = [math.log(p[1]) if log else p[1] for p in finite]
    tol = [max(1.2 * p[2], 0.01 * abs(p[1]), 1e-9) / (abs(p[1]) if log else 1.0) for p in finite]
    out = []
    for ws in (None, [1.0 / (t * t) for t in tol]):
        c, s = _line(xs, ys, ws)
        out.append((c + s * lo, c + s * hi))
    (x0, y0), (x1, y1) = (xs[0], ys[0]), (xs[-1], ys[-1])
    if x1 != x0:
        s = (y1 - y0) / (x1 - x0)
        out.append((y0 + s * (lo - x0), y0 + s * (hi - x0)))
    return [(math.exp(a), math.exp(b)) for a, b in out] if log else out


def fit(points, lo, hi, unit):
    """
    The simplest mapping that reproduces the points [(raw, value, resolution)], as manifest fields:
    linear (a at lo, b at hi), db (linear, -inf at lo), exp/freq/time (geometric), else None.
    """
    span = hi - lo
    if span <= 0 or len(points) < 2:
        return None
    finite = [p for p in points if not math.isinf(p[1])]
    if len(finite) < 2:
        return None
    t = lambda raw: (raw - lo) / span
    # Linear (with -inf at the bottom: a dB fader).
    floor_inf = any(math.isinf(p[1]) and p[1] < 0 and p[0] == lo for p in points)
    others_inf = any(math.isinf(p[1]) for p in points if not (p[0] == lo and floor_inf))
    if unit == ":1":
        others_inf = True   # ratios: geometric below
    if not others_inf:
        for a, b in _candidates(finite, lo, hi, log=False):
            ok, _ = _fits(finite, lambda raw: a + t(raw) * (b - a))
            if not ok:
                continue
            if floor_inf and unit == "dB":
                return {"kind": "db", "a": round(a, 6), "b": round(b, 6), "flag": True}
            if not floor_inf:
                if unit == "dB":
                    return {"kind": "db", "a": round(a, 6), "b": round(b, 6)}
                return {"kind": "linear", "a": round(a, 6), "b": round(b, 6)}
    # Ratios (compressors): geometric, often with "∞" or "Limit" at the top.
    if unit == ":1" and all(p[1] > 0 for p in finite):
        top_inf = any(math.isinf(p[1]) and p[1] > 0 and p[0] == hi for p in points)
        for a, b in _candidates(finite, lo, hi, log=True):
            ok, _ = _fits(finite, lambda raw: a * (b / a) ** t(raw))
            if ok:
                out = {"kind": "ratio", "a": _sig(a), "b": _sig(b)}
                if top_inf:
                    out["flag"] = True
                return out
        return None
    # Geometric, for values of one sign (Hz, seconds, Q…).
    if not floor_inf and not others_inf and all(p[1] > 0 for p in finite):
        for a, b in _candidates(finite, lo, hi, log=True):
            if a > 0 and b > 0 and abs(math.log(b / a)) > 1e-9:
                ok, _ = _fits(finite, lambda raw: a * (b / a) ** t(raw))
                if ok:
                    kind = {"Hz": "freq", "s": "time"}.get(unit, "exp")
                    return {"kind": kind, "a": _sig(a), "b": _sig(b)}
    # A power curve from end to end (glide, fade times): a + t^k·(b − a).
    if not floor_inf and not others_inf and finite[0][0] == lo and finite[-1][0] == hi:
        a, b = finite[0][1], finite[-1][1]
        ks = sorted(math.log((v - a) / (b - a)) / math.log(t(r)) for r, v, _ in finite
                    if 0 < t(r) < 1 and b != a and 0 < (v - a) / (b - a) < 1)
        if ks:
            k = ks[len(ks) // 2]
            if 0.05 < k < 20:
                ok, _ = _fits(finite, lambda raw: a + t(raw) ** k * (b - a))
                if ok:
                    return {"kind": "power", "a": _sig(a), "b": _sig(b), "exponent": _sig(k, 4)}
    return None


def _sig(x, digits=6):
    return float(f"{x:.{digits}g}")


# ------------------------------------------------------------------ description hints

DESC_RANGE = re.compile(r"(" + NUMBER + r")\s*(" + "|".join(re.escape(u) for u in UNIT_WORDS) + r")?\s*(?:-|–|to|\.\.\.?|…)\s*~?(" + NUMBER + r")\s*(" +
                        "|".join(re.escape(u) for u in UNIT_WORDS) + r")(?![a-z])", re.I)
DESC_UNIT = re.compile(r"(?:\bin\s+|\(\s*)(" + "|".join(re.escape(u) for u in UNIT_WORDS if len(u) > 1 or u == "%") + r")(?![a-z])", re.I)
ANCHOR = re.compile(r"(\d+)\s*(?:=|≈)\s*[^,;=≈()]*?\(?\s*~?(" + NUMBER + r")\s*(" + "|".join(re.escape(u) for u in UNIT_WORDS) + r")(?![a-z])", re.I)
ANY_ANCHOR = re.compile(r"\d+\s*(?:=|≈)")
NAMED = re.compile(r"(\d+)\s*=\s*([A-Za-z][\w&+/' -]*?)\s*(?=[,;.]|$|\s\d+\s*=)")
UNIT_TOKEN = re.compile(r"(?<![\w-])(Hz|kHz|ms|dB|dBFS|semitones|cents)(?![\w-])")


def _unit_of(word):
    return UNITS.get((word or "").lower(), (None, 1.0))


def description_hint(name, description, lo, hi, identity):
    """
    What a parameter's name and description say about its unit when its texts are bare numbers:
    {"unit": …} (the texts are in that unit) or {"unit", "kind", "a", "b"} (a range, "1 ms – 4 s
    exponential", over texts that are only raw numbers). None when they say nothing safe.
    """
    d = normalise(description or "")
    # Anchors: "60 = −60 dBFS" (raw, value as written, its unit).
    anchors = [(int(m.group(1)), float(m.group(2).replace(",", ".")), _unit_of(m.group(3))[0]) for m in ANCHOR.finditer(d)]
    stated = DESC_UNIT.search(d)
    stated_unit = _unit_of(stated.group(1))[0] if stated else None
    # "0 = slow (0.1 Hz), 100 = fast (5 Hz)" over texts 0…100: the texts aren't in Hz. Anchors in
    # another unit than the one stated ("in percent (100 = 0 dB)") say something else.
    conflict = [a for a in anchors if abs(a[1] - a[0]) > 1e-9 and (stated_unit is None or a[2] == stated_unit)]
    if identity and conflict:
        if stated_unit is None and len(anchors) >= 2 and len({u for _, _, u in anchors}) == 1 and anchors[0][2] == "dB":
            (r0, v0, _), (r1, v1, _) = anchors[0], anchors[-1]
            if r1 != r0:
                slope = (v1 - v0) / (r1 - r0)
                if all(abs(v0 + slope * (r - r0) - v) < 0.05 for r, v, _ in anchors):
                    return {"unit": "dB", "kind": "db", "a": round(v0 + slope * (lo - r0), 6), "b": round(v0 + slope * (hi - r0), 6)}
        return None
    # "0=Sine, 1=Triangle, 2=Square": every value named.
    named = {int(k): v.strip() for k, v in NAMED.findall(d)}
    if identity and hi - lo <= 32 and all(r in named for r in range(lo, hi + 1)):
        return {"choices": [named[r] for r in range(lo, hi + 1)]}
    m = DESC_RANGE.search(d)
    if m and identity:
        ua, fa = _unit_of(m.group(2) or m.group(4))
        ub, fb = _unit_of(m.group(4))
        if ua and ua == ub:
            a = float(m.group(1).replace(",", ".")) * fa
            b = float(m.group(3).replace(",", ".")) * fb
            curve = re.search(r"\b(exp|exponential|log|logarithmic)\b", d, re.I)
            if curve and a > 0 and b > 0:
                return {"unit": ua, "kind": {"Hz": "freq", "s": "time"}.get(ua, "exp"), "a": _sig(a), "b": _sig(b)}
            return {"unit": ua, "kind": "db" if ua == "dB" else "linear", "a": a, "b": b}
    u = DESC_UNIT.search(d)
    if u:
        return {"unit": _unit_of(u.group(1))[0], "scale": _unit_of(u.group(1))[1]}
    # A unit at the end of the name: "Attack ms", "Range dB".
    n = re.search(r"\s(" + "|".join(re.escape(x) for x in UNIT_WORDS if len(x) > 1 or x == "%") + r")\s*$", name or "", re.I)
    if n:
        return {"unit": _unit_of(n.group(1))[0], "scale": _unit_of(n.group(1))[1]}
    # A unit named on its own ("Filter cutoff Hz."), when nothing says what a value stands for.
    tokens = {_unit_of(x)[0] for x in UNIT_TOKEN.findall(d)}
    if len(tokens) == 1 and not ANY_ANCHOR.search(d):
        word = UNIT_TOKEN.search(d).group(1)
        return {"unit": _unit_of(word)[0], "scale": _unit_of(word)[1]}
    return None


# ------------------------------------------------------------------ one parameter

SWITCH_WORDS = [("off", "on"), ("no", "yes"), ("false", "true"), ("disabled", "enabled")]


def infer(param):
    """
    A draft manifest entry's mapping for a parameter from machine_describe(value_table=true):
    {"kind", unit, a, b, flag, choices, points, "confidence"}, or {"kind": None, "why"} when the
    texts say nothing (all the same).
    """
    table = [(int(r), str(t)) for r, t in param.get("table") or []]
    if not table:
        return {"kind": None, "why": "no value texts"}
    lo, hi = table[0][0], table[-1][0]
    texts = [t for _, t in table]
    full = hi - lo + 1 == len(table)
    if len(set(texts)) == 1:
        return {"kind": None, "why": f"every value reads \"{texts[0]}\""}
    parsed = [(r, parse(t)) for r, t in table]
    numeric = [(r, p) for r, p in parsed if p is not None]
    words = [t for (r, p), (_, t) in zip(parsed, table) if p is None]
    description = param.get("description") or ""

    # Two values: a switch ("Off"/"On", "no"/"yes", or 0/1 said to be on/off).
    if full and len(table) == 2:
        a, b = (x.strip().lower() for x in texts)
        if (a, b) in SWITCH_WORDS or ((a, b) == ("0", "1") and re.search(r"on\s*/\s*off|off\s*/\s*on|\bswitch\b|enable|toggle", description, re.I)):
            return {"kind": "switch", "choices": ["Off", "On"] if (a, b) == ("0", "1") else texts, "confidence": "values"}
    # Choices: names (some may be numbers: "Off", "1", "2"), every value listed, all different.
    if full and words and len(table) <= 64 and len(set(texts)) == len(texts):
        return {"kind": "enum", "choices": texts, "confidence": "values"}
    if len(numeric) < max(2, 0.8 * len(table)):
        if full and len(table) <= 256:
            return {"kind": "enum", "choices": texts, "confidence": "values"} if len(set(texts)) == len(texts) else {"kind": None, "why": "texts repeat and aren't numbers"}
        return {"kind": None, "why": "texts aren't numbers"}

    units = {}
    for _, p in numeric:
        units[p[1]] = units.get(p[1], 0) + 1
    unit = max(units, key=units.get)
    # A ratio's top word ("Limit", "∞"): infinitely hard.
    if unit == ":1" and parsed[-1][1] is None and len(words) == 1:
        numeric.append((hi, (math.inf, ":1", 0, 1.0)))
    points = [(r, p[0], _resolution(p)) for r, p in numeric if p[1] in (unit, "")]
    identity_offset = None
    if not unit and all(not math.isinf(v) for _, v, _ in points):
        offsets = {round(v - r, 9) for r, v, _ in points}
        if len(offsets) == 1:
            identity_offset = offsets.pop()
    confidence = "values"
    hint = None
    if not unit:
        hint = description_hint(param.get("name", ""), description, lo, hi, identity_offset is not None)
        if hint and "choices" in hint:
            return {"kind": "enum", "choices": hint["choices"], "confidence": "description"}
        if hint and "kind" in hint:
            out = {"kind": hint["kind"], "unit": hint["unit"], "a": hint["a"], "b": hint["b"], "confidence": "description"}
            return out
        if hint:
            unit = hint["unit"]
            scale = hint.get("scale", 1.0)
            points = [(r, v * scale, res * scale) for r, v, res in points]
            confidence = "description"
    mapping = fit(points, lo, hi, unit)
    if mapping is None:
        # No curve fits: the machine's own values as a table (at most 129 points, both ends).
        step = max(1, math.ceil(len(points) / 128))
        keep = points[::step]
        if keep[-1][0] != points[-1][0]:
            keep.append(points[-1])
        mapping = {"kind": "table", "points": [[r, _json_number(v)] for r, v, _ in keep]}
    mapping["unit"] = unit
    mapping["confidence"] = confidence
    return mapping


def _json_number(v):
    if math.isinf(v):
        return "-inf" if v < 0 else "inf"
    return _sig(v, 7)


def value_at(entry, raw, lo, hi):
    """A draft entry's value at a raw value (as ReBuzz.Agent's ParamMapping computes it), for checks."""
    kind = entry.get("kind")
    t = 0.0 if hi == lo else min(1.0, max(0.0, (raw - lo) / (hi - lo)))
    if kind in ("linear",):
        return entry["a"] + t * (entry["b"] - entry["a"])
    if kind == "db":
        if entry.get("flag") and raw <= lo:
            return -math.inf
        return entry["a"] + t * (entry["b"] - entry["a"])
    if kind in ("exp", "freq", "time"):
        return entry["a"] * (entry["b"] / entry["a"]) ** t
    if kind == "ratio":
        if entry.get("flag") and raw >= hi:
            return math.inf
        return entry["a"] * (entry["b"] / entry["a"]) ** t
    if kind == "power":
        return entry["a"] + t ** entry.get("exponent", 1) * (entry["b"] - entry["a"])
    if kind == "int":
        return raw + entry.get("a", 0)
    if kind in ("enum", "switch"):
        return raw - lo
    if kind == "percent":
        return t * 100
    if kind == "bipolar":
        return (t * 2 - 1) * 100
    if kind == "table":
        pts = [(r, -math.inf if v == "-inf" else math.inf if v == "inf" else v) for r, v in entry["points"]]
        if raw <= pts[0][0]:
            return pts[0][1]
        if raw >= pts[-1][0]:
            return pts[-1][1]
        for (r0, v0), (r1, v1) in zip(pts, pts[1:]):
            if r0 <= raw <= r1:
                if math.isinf(v0) or math.isinf(v1):
                    return v0 if raw - r0 < r1 - raw else v1
                return v0 + (v1 - v0) * (raw - r0) / (r1 - r0)
    return None


def agrees(entry, manifest_param, table):
    """Whether an inferred entry gives the same value as a generated manifest's entry at every listed raw (to the text's precision)."""
    lo, hi = manifest_param["min"], manifest_param["max"]
    kind = manifest_param.get("kind")
    if kind in ("enum", "switch"):
        return entry.get("kind") in ("enum", "switch") and len(entry.get("choices", [])) == hi - lo + 1
    for raw, text in table:
        p = parse(text)
        if p is None or math.isinf(p[0]):
            continue
        got = value_at(entry, raw, lo, hi)
        if got is None or math.isinf(got) or abs(got - p[0]) > max(1.2 * _resolution(p), 0.01 * abs(p[0]), 1e-9):
            return False
    return True
