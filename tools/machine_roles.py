# Fork (docs/specs/mcp-server.md §12.12, U2 in depth): what the profiler's test signals say about a
# machine, and what moving one parameter alone changes, as a suggested role. Standard library only.
#
#  - The test signal for effects (machine-profiler.py's TestBench) is played by Neo Poly, which every
#    host has, and captured on both sides of the machine (render probes), so the measurement divides
#    out what really went in:
#      * a stepped sine sweep: one sine per third octave (or whole tone), 20 Hz – 21 kHz, each long
#        enough for six periods: the response, gain and phase per tone (Goertzel), and its harmonics;
#      * level steps of noise, -42 to -6 dB, then a jump up and back down: the static curve and how
#        fast the gain follows (attack and release);
#      * a click (a 1 ms noise burst) with time to ring out: latency, the direct sound, early and late
#        energy, the decay (Schroeder's backward integral), echoes and their spacing, per channel.
#  - Generators hold a note: level, attack, decay, sustain, release and length from the envelope;
#    brightness and pitch from analyze.
#  - suggest_effect_role and suggest_generator_role compare the features at a parameter's minimum and
#    maximum (and its default) and name a role from ManifestRoles.Known, with the evidence.
#  - pitch_sieve finds which equal-tempered pitch a note plays from its spectrum (harmonic sieve), where
#    analyze's YIN hears none (a decaying, resonant bass).

import cmath
import math
import re

# ---- the stepped sine sweep -------------------------------------------------------------------------

OCTAVE_UP = 12               # Neo Poly's O1 octave +1: the note sent is 12 below the one that sounds


def midi_hz(n):
    return 440.0 * 2 ** ((n - 69) / 12)


def sweep_tones(step=4, low=16, high=136, start=0.0):
    """The stepped sweep: (note sent, Hz, start s, length s) per tone, from MIDI note low to high in steps of
    `step` semitones (4: third octaves, 31 tones from 20.6 Hz to 21.1 kHz; 2: whole tones). Each tone lasts six
    periods, at least 40 ms."""
    out, t = [], start
    for n in range(low, high + 1, step):
        f = midi_hz(n)
        d = round(max(0.04, 6.0 / f), 4)
        out.append((n - OCTAVE_UP, f, round(t, 4), d))
        t += d
    return out


def goertzel_iq(x, a, b, rate, freq):
    """The complex amplitude of one frequency in x[a:b] (a sine of amplitude A reads |A|)."""
    w = 2 * math.pi * freq / rate
    k = 2 * math.cos(w)
    s1 = s2 = 0.0
    n = 0
    for i in range(max(0, a), min(len(x), b)):
        s1, s2 = x[i] + k * s1 - s2, s1
        n += 1
    if n == 0:
        return 0j
    re = s1 - s2 * math.cos(w)
    im = s2 * math.sin(w)
    return complex(re, im) * 2 / n


def response(tones, rate, inp, out_l, out_r, latency=0, wobble=None):
    """
    [(Hz, dB out over in, input dBFS)] per tone: the middle of each tone (from 35 % to 95 % of it, the
    output window shifted by the latency), out as the power mean of both channels. A tone whose input is
    under -70 dBFS reads None. With a list for `wobble`, each tone's gain in its two halves differing (dB,
    where a tone is long enough) goes into it: a modulated effect's gain moves.
    """
    rows = []
    for _, f, start, dur in tones:
        a, b = int((start + 0.35 * dur) * rate), int((start + 0.95 * dur) * rate)
        gi = abs(goertzel_iq(inp, a, b, rate, f))
        gl = abs(goertzel_iq(out_l, a + latency, b + latency, rate, f))
        gr = abs(goertzel_iq(out_r, a + latency, b + latency, rate, f)) if out_r is not out_l else gl
        lvl = _db(gi)
        if gi <= 0 or lvl < -70:
            rows.append((round(f, 1), None, None))
            continue
        go = math.sqrt((gl * gl + gr * gr) / 2)
        rows.append((round(f, 1), round(_db(go / gi), 2) if go > 0 else -120.0, round(lvl, 1)))
        if wobble is not None and (b - a) * f / rate >= 6 and go > 0:
            m = (a + b) // 2
            halves = []
            for x, y in ((a, m), (m, b)):
                i_ = abs(goertzel_iq(inp, x, y, rate, f))
                o_ = abs(goertzel_iq(out_l, x + latency, y + latency, rate, f))
                halves.append(_db(o_ / i_) if i_ > 0 and o_ > 0 else None)
            if None not in halves:
                wobble.append((_db(go / gi), abs(halves[1] - halves[0])))
    if wobble:
        top = max(g for g, _ in wobble)
        kept = [w for g, w in wobble if g >= top - 20]
        wobble[:] = kept
    return rows


def harmonics(rate, inp, out, a, b, f0, latency=0, count=5):
    """THD (%) of out and of inp over [a, b): harmonics 2..count+1 over the fundamental."""
    def thd(x, off):
        fund = abs(goertzel_iq(x, a + off, b + off, rate, f0))
        if fund <= 1e-9:
            return None
        h = math.sqrt(sum(abs(goertzel_iq(x, a + off, b + off, rate, f0 * k)) ** 2 for k in range(2, count + 2) if f0 * k < rate / 2))
        return 100 * h / fund
    return thd(out, latency), thd(inp, 0)


def rms_hz(x, a, b, rate):
    """The RMS frequency of x[a:b] (rate / 2π · √(E[Δx²] / E[x²])): the spectrum's second moment, a brightness that a
    pitched sound's fundamental doesn't dominate as its zero crossings do. None when silent."""
    a, b = max(1, a), min(len(x), b)
    e = d = 0.0
    prev = x[a - 1]
    for i in range(a, b):
        v = x[i]
        e += v * v
        dv = v - prev
        d += dv * dv
        prev = v
    if e <= 1e-12:
        return None
    return rate / (2 * math.pi) * math.sqrt(d / e)


def harmonic_peakiness(x, a, b, rate, f0, count=32):
    """Over a pitched note's harmonics 3…count (Goertzel at k·f0 over x[a:b]): the largest by which one stands over the
    louder of the harmonics two away on each side (dB, each floored 60 dB under the loudest), and which harmonic: a
    resonance's bump on the harmonic series, which a waveform's own slope or missing harmonics don't make."""
    if not f0 or f0 <= 0:
        return None
    amps = []
    for k in range(1, count + 1):
        if k * f0 >= rate / 2 - 500:
            break
        amps.append(_db(abs(goertzel_iq(x, a, b, rate, k * f0))))
    if len(amps) < 7:
        return None
    top = max(amps)
    amps = [max(v, top - 60) for v in amps]
    best = max(range(2, len(amps) - 2), key=lambda i: amps[i] - max(amps[i - 2], amps[i + 2]))
    return round(amps[best] - max(amps[best - 2], amps[best + 2]), 2), best + 1


def _db(x):
    return 20 * math.log10(x) if x > 0 else -math.inf


def _pdb(x):
    return 10 * math.log10(x) if x > 0 else -math.inf


def response_features(resp):
    """
    What a response says, from [(Hz, dB, in)]: level (power mean, 63 Hz – 8 kHz), spread (max − min, 40 Hz –
    16 kHz), the centroid of its power over log frequency (octaves from 1 kHz: a low-pass's corner pulls it
    down), its maximum and where, its prominence (max over the median), the -3 dB band around the maximum
    (low and high corners, width in octaves) and its tilt (above 5 kHz over below 300 Hz).
    """
    pts = [(f, d) for f, d, _ in resp if d is not None and math.isfinite(d)]
    if len(pts) < 5:
        return None
    mid = [d for f, d in pts if 63 <= f <= 8000] or [d for _, d in pts]
    level = _pdb(sum(10 ** (d / 10) for d in mid) / len(mid))
    band = [d for f, d in pts if 40 <= f <= 16000] or [d for _, d in pts]
    weights = [(math.log2(f / 1000), 10 ** (d / 10)) for f, d in pts]
    wsum = sum(w for _, w in weights)
    centroid = sum(o * w for o, w in weights) / wsum if wsum > 0 else 0.0
    imax = max(range(len(pts)), key=lambda i: pts[i][1])
    fmax, dmax = pts[imax]
    def at(f):
        if f < pts[0][0] / 1.05 or f > pts[-1][0] * 1.05:
            return None
        return min(pts, key=lambda q: abs(math.log2(q[0] / f)))[1]
    sides = [x for x in (at(fmax / 2), at(fmax * 2)) if x is not None]
    median = max(sides) if len(sides) == 2 else dmax         # a peak stands over both sides; at an edge it can't be told
    lo = hi = imax
    while lo > 0 and pts[lo - 1][1] >= dmax - 3:
        lo -= 1
    while hi < len(pts) - 1 and pts[hi + 1][1] >= dmax - 3:
        hi += 1
    lows = [d for f, d in pts if f < 300]
    highs = [d for f, d in pts if f > 5000]
    return {
        "level_db": round(level, 2) if math.isfinite(level) else None,
        "spread_db": round(max(band) - min(band), 2),
        "centroid_oct": round(centroid, 3),
        "max_db": round(dmax, 2),
        "max_hz": round(fmax, 1),
        "prominence_db": round(dmax - median, 2),
        "low_corner_hz": round(pts[lo][0], 1),
        "high_corner_hz": round(pts[hi][0], 1),
        "width_oct": round(math.log2(pts[hi][0] / pts[lo][0]), 2),
        "tilt_db": round(sum(highs) / len(highs) - sum(lows) / len(lows), 2) if lows and highs else None,
        "shape": [(f, round(d - level, 2)) for f, d in pts] if math.isfinite(level) else [],
    }


# ---- the click: latency, direct, early and late energy, decay, echoes ------------------------------------

def energy(x, a, b):
    s = 0.0
    for i in range(max(0, a), min(len(x), b)):
        v = x[i]
        s += v * v
    return s


def env_db(x, rate, a, b, ms=1.0):
    """RMS in dB per window of `ms` over x[a:b] (silence: -200)."""
    n = max(1, int(rate * ms / 1000))
    out = []
    for i in range(max(0, a), min(len(x), b) - n + 1, n):
        s = 0.0
        for j in range(i, i + n):
            v = x[j]
            s += v * v
        out.append(10 * math.log10(s / n) if s > 0 else -200.0)
    return out


def onset(x, a, b, fraction=0.1):
    """The first sample in x[a:b] above `fraction` of the peak there (None: silent)."""
    a, b = max(0, a), min(len(x), b)
    peak = 0.0
    for i in range(a, b):
        v = abs(x[i])
        if v > peak:
            peak = v
    if peak <= 1e-7:
        return None
    thr = peak * fraction
    for i in range(a, b):
        if abs(x[i]) > thr:
            return i
    return None


def decay_rate(env, ms=1.0, top=-5.0, bottom=-25.0):
    """
    (T60 in s, dB the fit spans) from a level envelope in dB per `ms` (Schroeder's backward integral of its
    power, then a straight line from `top` to `bottom` dB, extrapolated to 60 dB). When the integral doesn't
    fall to `bottom` it fits what it reaches (10 dB at least), else None.
    """
    p = [10 ** (e / 10) if e > -199 else 0.0 for e in env]
    total = sum(p)
    if total <= 0:
        return None, 0.0
    edc, run = [], total
    for v in p:
        edc.append(10 * math.log10(run / total) if run > 0 else -200.0)
        run -= v
    final = edc[-1]
    if final > top - 10.0 - 5.0:
        return None, round(max(0.0, -final), 1)          # under 10 dB to fit before the window ends
    lo_db = max(bottom, final + 5.0)                      # the integral's last 5 dB bend down (the window's end)
    xs, ys = [], []
    for i, e in enumerate(edc):
        if lo_db <= e <= top:
            xs.append(i * ms / 1000)
            ys.append(e)
    if len(xs) < 3:
        return None, 0.0
    mx, my = sum(xs) / len(xs), sum(ys) / len(ys)
    sxx = sum((x - mx) ** 2 for x in xs)
    if sxx <= 0:
        return None, 0.0
    slope = sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / sxx
    if slope >= -1e-6:
        return None, round(top - lo_db, 1)
    return round(-60.0 / slope, 3), round(top - lo_db, 1)


def echoes(env, ms=1.0, start_ms=8.0, floor_db=-60.0, rise_db=10.0, smooth=5):
    """
    Echo times in s from a click's envelope in dB (time 0 = the click's onset): from start_ms on, the level
    (its power over `smooth` windows) rising `rise_db` or more from a dip and above floor_db below the peak; a
    dense reverb tail doesn't, a delay does after each gap. Returns [(seconds, level dB from the peak)].
    """
    if not env:
        return []
    k = max(1, smooth)
    sm = []
    for i in range(0, len(env) - k + 1, k):
        pw = sum(10 ** (e / 10) for e in env[i:i + k] if e > -199) / k
        sm.append(10 * math.log10(pw) if pw > 0 else -200.0)
    peak = max(sm)
    floor = peak + floor_db
    found, low, low_i = [], None, None
    for j, v in enumerate(sm):
        t_ms = j * k * ms
        if t_ms < start_ms:
            continue
        v = max(v, floor)
        if low is None or v < low:
            low, low_i = v, j
        if v > low + rise_db and v > floor + 6:
            # the echo's peak: climb while rising
            jj = j
            while jj + 1 < len(sm) and sm[jj + 1] > sm[jj]:
                jj += 1
            found.append((round(jj * k * ms / 1000, 4), round(sm[jj] - peak, 1)))
            low = sm[jj]
    # one entry per echo: drop rises found while still climbing the same echo
    out = []
    for t, d in found:
        if out and t - out[-1][0] < 0.004:
            if d > out[-1][1]:
                out[-1] = (t, d)
            continue
        out.append((t, d))
    return out


def click_features(rate, inp, out_l, out_r, click_at, click_len, window, ms=1.0):
    """
    What a click (a ~1 ms burst at `click_at` s in `inp`, the machine's input) says through the machine
    (out_l, out_r), over `window` s: latency (samples), direct, early (to 80 ms) and late (after 30 ms past the
    burst) energy in dB over the burst's, the T60 of its decay, the time it rings until 60 dB down, echoes (per
    channel), and how bright the tail is (zero crossings per second, after 50 ms).
    """
    a = int(click_at * rate)
    i0 = onset(inp, a - int(0.01 * rate), a + int(0.05 * rate))
    if i0 is None:
        return None
    e_in = energy(inp, i0, i0 + int((click_len + 0.004) * rate))
    if e_in <= 0:
        return None
    mono = [(x + y) / 2 for x, y in zip(out_l[i0:i0 + int(window * rate)], out_r[i0:i0 + int(window * rate)])] if out_r is not out_l \
        else list(out_l[i0:i0 + int(window * rate)])
    o0 = onset(mono, 0, int(0.25 * rate), 0.1)
    feats = {"latency_samples": o0}
    if o0 is None:
        feats.update({"silent": True})
        return feats
    burst = int((click_len + 0.002) * rate)
    direct = energy(mono, o0 - int(0.0005 * rate), o0 + burst)
    early = energy(mono, o0 + burst, o0 + int(0.08 * rate))
    late = energy(mono, o0 + burst + int(0.03 * rate), len(mono))
    feats["direct_db"] = round(_pdb(direct / e_in), 2) if direct > 0 else -120.0
    feats["early_db"] = round(_pdb(early / e_in), 2) if early > 0 else -120.0
    feats["late_db"] = round(_pdb(late / e_in), 2) if late > 0 else -120.0
    env = env_db(mono, rate, o0, len(mono), ms)
    t60, span = decay_rate(env, ms)
    feats["t60_s"] = t60
    feats["t60_span_db"] = span
    peak = max(env) if env else -200
    last = max((i for i, e in enumerate(env) if e > peak - 60), default=0)
    feats["ring_s"] = round((last + 1) * ms / 1000, 3)
    feats["rings_past_window"] = last >= len(env) - 3
    for name, ch in (("echoes", mono), ("echoes_left", out_l), ("echoes_right", out_r)):
        if ch is out_r and out_r is out_l:
            continue
        seg = ch if ch is mono else ch[i0:i0 + int(window * rate)]
        e = env_db(seg, rate, o0, len(seg), ms) if ch is not mono else env
        feats[name] = echoes(e, ms)[:12]
    # a delayed copy close behind the direct sound (a chorus's or flanger's delay): the loudest peak 2.5–60 ms after the
    # onset with a dip of 6 dB or more between it and the direct sound
    best = None
    for i in range(int(2.5 / ms), min(len(env) - 1, int(60 / ms))):
        if env[i] >= env[i - 1] and env[i] >= env[i + 1] and env[i] > peak - 40:
            dip = min(env[1:i]) if i > 1 else env[i]
            if env[i] - dip >= 6 and (best is None or env[i] > env[best]):
                best = i
    feats["copy_s"] = round(best * ms / 1000, 4) if best is not None else None
    # late onset (predelay): the first window after the burst within 30 dB of the late part's peak
    tail = env[int((burst / rate) * 1000 / ms) + 1:]
    if tail:
        tp = max(tail)
        first = next((i for i, e in enumerate(tail) if e > tp - 20), None)
        feats["late_onset_s"] = round((first + int((burst / rate) * 1000 / ms) + 1) * ms / 1000, 4) if first is not None and tp > peak - 70 else None
    zstart, zend = o0 + int(0.05 * rate), min(len(mono), o0 + int(0.5 * rate))
    seg = mono[zstart:zend]
    total = energy(seg, 0, len(seg)) if len(seg) > 100 else 0.0
    if total > 1e-12:
        feats["tail_zcr_hz"] = round(rms_hz(seg, 0, len(seg), rate) or 0, 1)
        # its share under about 250 Hz (two one-pole low-passes): a reverb's low cut
        k = 1 - math.exp(-2 * math.pi * 250 / rate)
        y1 = y2 = 0.0
        low = 0.0
        for v in seg:
            y1 += k * (v - y1)
            y2 += k * (y1 - y2)
            low += y2 * y2
        feats["tail_low_db"] = round(_pdb(low / total), 2) if low > 0 else -120.0
    l_e, r_e = energy(out_l, i0, i0 + int(window * rate)), energy(out_r, i0, i0 + int(window * rate))
    feats["balance_db"] = round(_pdb(l_e / r_e), 2) if l_e > 0 and r_e > 0 else None
    return feats


# ---- level steps ------------------------------------------------------------------------------------------

STEP_VELOCITIES = [2, 4, 8, 16, 32, 64, 127]    # -36 … 0 dB on Neo Poly with Vel → amp at 100 %
STEP_SECONDS = 0.3
JUMP = [(4, 0.4), (127, 0.5), (4, 0.7)]          # a jump up 30 dB and back: attack and release


def step_notes(start):
    """(velocity, start s, length s) of the staircase, then the jump; the end time."""
    notes, t = [], start
    for v in STEP_VELOCITIES:
        notes.append((v, round(t, 4), STEP_SECONDS))
        t += STEP_SECONDS
    t += 0.1
    jump_at = t
    for v, d in JUMP:
        notes.append((v, round(t, 4), d))
        t += d
    return notes, jump_at, t


def step_features(rate, inp, out_l, out_r, notes, jump_at, latency=0):
    """
    The static curve (input dB → gain dB at each step, from its last 60 %), its compression (the gain's drop
    from the quietest steps to the loudest, and the slope out/in over the top steps), and from the jump: how
    long the gain takes to settle within 1 dB after it goes up (attack) and after it comes down (release).
    """
    def lvl(x, a, b):
        e = energy(x, a, b)
        return _pdb(e / max(1, b - a)) if e > 0 else -200.0
    curve = []
    for v, s, d in notes[:len(STEP_VELOCITIES)]:
        a, b = int((s + 0.4 * d) * rate), int((s + 0.95 * d) * rate)
        i_db = lvl(inp, a, b)
        o_db = _pdb((energy(out_l, a + latency, b + latency) + energy(out_r, a + latency, b + latency)) / 2 / max(1, b - a))
        if i_db > -90 and math.isfinite(o_db):
            curve.append((round(i_db, 2), round(o_db - i_db, 2)))
    feats = {"curve": curve}
    if len(curve) >= 4:
        quiet = sum(g for _, g in curve[:2]) / 2
        loud = sum(g for _, g in curve[-2:]) / 2
        feats["compression_db"] = round(quiet - loud, 2)
        (i1, g1), (i2, g2) = curve[-3], curve[-1]
        feats["top_slope"] = round(((i2 + g2) - (i1 + g1)) / (i2 - i1), 3) if i2 > i1 else None
        # the knee: the first step whose gain is 1 dB under the quietest's
        knee = next((i for i, g in curve if g < curve[0][1] - 1.0), None)
        feats["knee_in_db"] = knee
    # the jump: gain over time in 5 ms windows (out over in)
    n = int(0.005 * rate)
    up, mid_t, end_t = jump_at + JUMP[0][1], jump_at + JUMP[0][1] + JUMP[1][1], jump_at + sum(d for _, d in JUMP)

    def gains(t0, t1):
        g = []
        for i in range(int(t0 * rate), int(t1 * rate) - n, n):
            ei = energy(inp, i, i + n)
            eo = (energy(out_l, i + latency, i + n + latency) + energy(out_r, i + latency, i + n + latency)) / 2
            g.append(_pdb(eo / ei) if ei > 0 and eo > 0 else None)
        return g

    def settle(g):
        vals = [x for x in g if x is not None]
        if len(vals) < 10:
            return None
        final = sum(vals[-10:]) / 10
        last_off = max((i for i, x in enumerate(g) if x is not None and abs(x - final) > 1.0), default=-1)
        return round((last_off + 1) * 0.005, 3)
    def tau(g):
        """The time the gain takes to move 63 % of the way from where it starts to where it settles (2 dB or more)."""
        vals = [(i, x) for i, x in enumerate(g) if x is not None]
        if len(vals) < 12:
            return None
        start = vals[0][1]
        final = sum(x for _, x in vals[-10:]) / 10
        if abs(final - start) < 2:
            return None
        target = final + 0.37 * (start - final)
        hit = next((i for i, x in vals if (x - target) * (start - final) <= 0), None)
        return round(hit * 0.005, 3) if hit is not None else None
    g_up, g_down = gains(up, mid_t), gains(mid_t, end_t)
    feats["attack_s"] = settle(g_up)
    feats["release_s"] = settle(g_down)
    feats["attack_tau_s"] = tau(g_up)
    feats["release_tau_s"] = tau(g_down)
    return feats


# ---- generators: the held note ----------------------------------------------------------------------------

def note_envelope(rate, left, right, note_on, note_off, ms=5.0):
    """
    A held note's envelope in dB per `ms` (both channels): its peak; attack (onset to within 1 dB of the peak);
    final (the level over the last 50 ms before note-off, dB under the peak); half (the time from the peak until
    the level has fallen half way, in dB, to the final level, where it falls 3 dB or more: how fast it decays);
    length (onset until 40 dB under the peak while held: a drum's decay); release (note-off until 40 dB under the
    final level; past the window: release_past_window, and release_s the window as a lower bound); brightness
    early (10–60 ms) and late (the last 300 ms before note-off) as zero crossings per second. Times in s.
    """
    n = max(1, int(rate * ms / 1000))
    env = []
    count = min(len(left), len(right))
    for i in range(0, count - n + 1, n):
        s = 0.0
        for j in range(i, i + n):
            a, b = left[j], right[j]
            s += a * a + b * b
        env.append(10 * math.log10(s / (2 * n)) if s > 0 else -200.0)
    if not env or max(env) < -100:
        return None
    on, off = int(note_on * 1000 / ms), int(note_off * 1000 / ms)
    held = env[on:off]
    if not held:
        return None
    peak = max(held)
    start = next((i for i, e in enumerate(held) if e > peak - 40), 0)
    ipk = max(range(len(held)), key=lambda i: held[i])
    att = next((i for i, e in enumerate(held) if e >= peak - 1.0), ipk)
    tail = held[max(0, len(held) - max(2, int(50 / ms))):]
    final = 10 * math.log10(sum(10 ** (e / 10) for e in tail) / len(tail)) if tail else -200
    feats = {"peak_db": round(peak, 2), "attack_s": round(max(0, att - start) * ms / 1000, 3), "final_db": round(max(final - peak, -80.0), 2)}
    if peak - final >= 3:
        target = peak - (peak - final) / 2
        half = next((i for i in range(ipk, len(held)) if held[i] <= target), None)
        feats["half_s"] = round((half - ipk) * ms / 1000, 3) if half is not None else None
    end = next((i for i in range(ipk, len(held)) if held[i] < peak - 40), None)
    feats["length_s"] = round((end - start) * ms / 1000, 3) if end is not None else None
    if final > peak - 40:
        after = env[off:]
        rel = next((i for i, e in enumerate(after) if e < final - 40), None)
        feats["release_s"] = round(rel * ms / 1000, 3) if rel is not None else round(len(after) * ms / 1000, 3)
        feats["release_past_window"] = rel is None

    def bright(a, b):
        a, b = max(1, a), min(count, b)
        if b - a < 50:
            return None
        seg = [(x + y) for x, y in zip(left[a - 1:b], right[a - 1:b])]
        return rms_hz(seg, 1, len(seg), rate)
    s0 = int(note_on * rate)

    def loud(a, b):
        e = (energy(left, a, b) + energy(right, a, b)) / (2 * max(1, b - a))
        return e > 0 and 10 * math.log10(e) > peak - 30
    e0, e1 = s0 + int(0.01 * rate), s0 + int(0.06 * rate)
    l0, l1 = int((note_off - 0.3) * rate), int(note_off * rate)
    be = bright(e0, e1) if loud(e0, e1) else None
    bl = bright(l0, l1) if loud(l0, l1) else None
    feats["bright_early_hz"] = round(be, 1) if be else None
    feats["bright_late_hz"] = round(bl, 1) if bl else None
    # how much the held part moves (an LFO on the amp or the filter): level and brightness over 50 ms windows
    lv, br = [], []
    w = int(0.06 * rate)                     # three periods of a 55 Hz note
    for a in range(s0 + int(0.2 * rate), int((note_off - 0.02) * rate) - w, w):
        e = (energy(left, a, a + w) + energy(right, a, a + w)) / (2 * w)
        if e > 0:
            lv.append((a, 10 * math.log10(e)))
    top = max((v for _, v in lv), default=-200)
    lv = [(a, v) for a, v in lv if v > top - 30]
    for a, _ in lv:
        b_ = bright(a, a + w)
        if b_:
            br.append(b_)
    lw = _wobble([v for _, v in lv])
    bw = _wobble([math.log2(b_) for b_ in br])
    if lw is not None:
        feats["level_wobble_db"] = lw
    if bw is not None:
        feats["bright_wobble"] = round(2 ** bw - 1, 3)
    return feats


def _wobble(ys):
    """How much a series swings about its trend (a quadratic fitted by least squares), only when it swings back and
    forth (four or more crossings of the trend): an LFO's movement, not an envelope's. None otherwise."""
    n = len(ys)
    if n < 8:
        return None
    xs = [i / (n - 1) for i in range(n)]
    # normal equations for y = a + b x + c x²
    s0, s1, s2, s3, s4 = n, sum(xs), sum(x * x for x in xs), sum(x ** 3 for x in xs), sum(x ** 4 for x in xs)
    t0, t1, t2 = sum(ys), sum(x * y for x, y in zip(xs, ys)), sum(x * x * y for x, y in zip(xs, ys))
    m = [[s0, s1, s2, t0], [s1, s2, s3, t1], [s2, s3, s4, t2]]
    for i in range(3):
        piv = m[i][i]
        if abs(piv) < 1e-12:
            return None
        for j in range(i + 1, 3):
            f = m[j][i] / piv
            for k in range(i, 4):
                m[j][k] -= f * m[i][k]
    c = m[2][3] / m[2][2]
    b = (m[1][3] - m[1][2] * c) / m[1][1]
    a = (m[0][3] - m[0][1] * b - m[0][2] * c) / m[0][0]
    res = [y - (a + b * x + c * x * x) for x, y in zip(xs, ys)]
    crossings = sum(1 for u, v in zip(res, res[1:]) if (u < 0) != (v < 0))
    if crossings < 4:
        return 0.0
    return round(max(res) - min(res), 3)


def velocity_response(levels):
    """From {velocity: peak dB}: the range, the dB per doubling of velocity (a straight amplitude: 6), and a word."""
    pts = sorted((v, d) for v, d in levels.items() if d is not None and math.isfinite(d))
    if len(pts) < 2:
        return None
    rng = pts[-1][1] - pts[0][1]
    xs = [math.log2(v) for v, _ in pts]
    ys = [d for _, d in pts]
    mx, my = sum(xs) / len(xs), sum(ys) / len(ys)
    sxx = sum((x - mx) ** 2 for x in xs)
    slope = sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / sxx if sxx > 0 else 0.0
    word = ("none (velocity doesn't change its level)" if abs(rng) < 1.0 else
            "linear in amplitude (about 6 dB per doubling)" if 4.5 <= slope <= 7.5 else
            "steeper than linear (velocity squared or a dB curve)" if slope > 7.5 else
            "gentle (less than linear)")
    return {"range_db": round(rng, 2), "db_per_doubling": round(slope, 2), "curve": word}


# ---- pitch: a harmonic sieve over equal-tempered candidates ----------------------------------------------

def fft(re):
    """In-place radix-2 FFT of a real list (length a power of two): complex spectrum."""
    n = len(re)
    a = [complex(x, 0.0) for x in re]
    j = 0
    for i in range(1, n):
        bit = n >> 1
        while j & bit:
            j ^= bit
            bit >>= 1
        j |= bit
        if i < j:
            a[i], a[j] = a[j], a[i]
    size = 2
    while size <= n:
        w = cmath.exp(-2j * math.pi / size)
        half = size // 2
        for start in range(0, n, size):
            wk = 1.0 + 0j
            for k in range(start, start + half):
                t = a[k + half] * wk
                a[k + half] = a[k] - t
                a[k] = a[k] + t
                wk *= w
        size *= 2
    return a


def magnitude_spectrum(x, rate, n=16384):
    """Hann-windowed magnitude spectrum of the first n samples of x (zero-padded): (bin Hz, magnitudes)."""
    seg = list(x[:n]) + [0.0] * max(0, n - len(x))
    win = [0.5 - 0.5 * math.cos(2 * math.pi * i / (n - 1)) for i in range(n)]
    spec = fft([s * w for s, w in zip(seg, win)])
    return rate / n, [abs(c) for c in spec[:n // 2]]


def pitch_sieve(binhz, mags, expected_midi, span=24, harmonics_n=8):
    """
    The equal-tempered note (within ±span semitones of expected_midi) whose harmonic series explains the
    spectrum best, with cents and the share of the spectrum's energy (50 Hz – 10 kHz) its harmonics hold.
    Of candidates within 10 % of the best score, the highest wins (a series an octave down scores alike).
    Unpitched when no series holds 60 % of the energy (an inharmonic or noisy sound).
    """
    def peak_at(f):
        i = int(round(f / binhz))
        if i <= 0 or i + 2 >= len(mags):
            return 0.0, f
        j = max(range(i - 2, i + 3), key=lambda k: mags[k])
        a, b, c = mags[j - 1], mags[j], mags[j + 1]
        den = a - 2 * b + c
        off = 0.5 * (a - c) / den if den != 0 else 0.0
        return b, (j + off) * binhz

    lo_bin, hi_bin = int(50 / binhz), min(len(mags), int(10000 / binhz))
    total = sum(m * m for m in mags[lo_bin:hi_bin])
    if total <= 0:
        return None
    scores = []
    for k in range(-span, span + 1):
        f0 = midi_hz(expected_midi + k)
        if f0 < 25 or f0 * 1 > 10000:
            continue
        s = 0.0
        for h in range(1, harmonics_n + 1):
            if f0 * h > 10000:
                break
            m, _ = peak_at(f0 * h)
            s += (0.86 ** (h - 1)) * m
        scores.append((s, k, f0))
    if not scores:
        return None
    best = max(s for s, _, _ in scores)
    if best <= 0:
        return None
    s, k, f0 = max((x for x in scores if x[0] >= 0.9 * best), key=lambda x: x[2])
    _, fpk = peak_at(f0)
    held = 0.0
    for h in range(1, 40):
        if f0 * h > 10000:
            break
        i = int(round(f0 * h / binhz))
        w = max(2, int(f0 * h * 0.03 / binhz))
        held += sum(m * m for m in mags[max(lo_bin, i - w):min(hi_bin, i + w + 1)])
    share = held / total
    cents = 1200 * math.log2(fpk / f0) if fpk > 0 else 0.0
    if share < 0.6 or abs(cents) > 50:
        return {"midi": expected_midi + k, "cents": round(cents, 1), "share": round(share, 3), "pitched": False}
    return {"midi": expected_midi + k, "cents": round(cents, 1), "share": round(share, 3), "pitched": True}


# ---- suggested roles ------------------------------------------------------------------------------------

def _fmt_hz(f):
    if f is None:
        return "?"
    return f"{f / 1000:.3g} kHz" if f >= 1000 else f"{f:.3g} Hz"


def _fmt_s(t):
    if t is None:
        return "?"
    return f"{t * 1000:.3g} ms" if t < 1 else f"{t:.3g} s"


def _transparent(f, mono_gain):
    """A setting where the effect does nothing to the test signal: flat within 1.5 dB, at its dry level, no tail."""
    r, c = f.get("response"), f.get("click") or {}
    if not r or r.get("level_db") is None:
        return False
    flat = r["spread_db"] < 1.5
    dry = min(abs(r["level_db"]), abs(r["level_db"] - mono_gain)) < 2.0
    no_tail = (c.get("late_db") if c.get("late_db") is not None else -120) < -45 and not c.get("echoes")
    return flat and dry and no_tail


def _num(x):
    return x if isinstance(x, (int, float)) and math.isfinite(x) else None


def _ratio(a, b):
    a, b = _num(a), _num(b)
    if a is None or b is None or a <= 0 or b <= 0:
        return None
    return max(a, b) / min(a, b)


TIME_TEXTS = ("tick", "ms", "sample", "sec", "beat", "256")
FILTER_TEXTS = ("lp", "hp", "bp", "low", "high", "band", "notch", "peak", "pass", "br", "ap")


NEED_MID = "?mid"
ONOFF_TEXTS = ("off", "on", "bypass", "enable", "disable", "no", "yes", "active")


def _first(c, key="echoes"):
    e = c.get(key) or []
    return e[0][0] if e else None


def _tail(c):
    """A setting's tail: its T60 where the late sound is within 40 dB of the click, else 0."""
    if (c.get("late_db") if c.get("late_db") is not None else -120) < -40:
        return 0.0
    t = _num(c.get("t60_s"))
    return t if t is not None else _num(c.get("ring_s"))


def _dshape(ra, rb):
    return max((abs(a - b) for (_, a), (_, b) in zip(ra.get("shape") or [], rb.get("shape") or [])), default=0)


def _choice_role(p, texts, lo, hi, rl, rh, cl, ch, sl, sh, machine_type, t_lo, t_hi):
    """A choice's role: bypass (off/on texts, one setting does nothing), units of a delay, a filter's type or slope,
    a compressor's detection; None otherwise (a choice says what it is in its texts)."""
    if t_lo != t_hi and any(w in texts.split() or w in texts for w in ONOFF_TEXTS):
        return "bypass", f"one setting passes the signal unchanged (its {'min' if t_lo else 'max'}imum)"
    el, eh = _first(cl), _first(ch)
    if el and eh and abs(eh - el) >= 0.005 and any(t in texts for t in TIME_TEXTS):
        return "delay.unit", f"a choice of units that moves the first echo {_fmt_s(el)} → {_fmt_s(eh)}"
    if sl and sh and ("rms" in texts or "peak" in texts) and abs((sh.get("compression_db") or 0) - (sl.get("compression_db") or 0)) >= 1:
        return "dyn.detection", f"peak or RMS: the loudest steps lose {sl.get('compression_db')} → {sh.get('compression_db')} dB"
    if rl and rh:
        dshape = _dshape(rl, rh)
        dlow = math.log2(rh["low_corner_hz"] / rl["low_corner_hz"])
        dhigh = math.log2(rh["high_corner_hz"] / rl["high_corner_hz"])
        if dshape >= 6 and any(t in texts for t in FILTER_TEXTS):
            return "filter.type", (f"its choices change the response's shape (up to {dshape:.0f} dB), corners "
                                   f"{_fmt_hz(rl['low_corner_hz'])}–{_fmt_hz(rl['high_corner_hz'])} → {_fmt_hz(rh['low_corner_hz'])}–{_fmt_hz(rh['high_corner_hz'])}")
        if dshape >= 6 and max(abs(dlow), abs(dhigh)) < 1.0 and machine_type in ("filter", "filter or EQ"):
            return "filter.slope", f"the corners stay, the slope beyond them changes (up to {dshape:.0f} dB)"
    return None, "a choice: its texts say what it is; no role measured"


def suggest_effect_role(p, ends, default, machine_type, mono_gain=6.02, eq_band=None, mid=None):
    """
    A role for an effect's parameter from its features at each end ({"min": f, "max": f}) and at the default:
    (role or None, evidence). Features per setting: "response" (response_features, with "wobble_db"), "click"
    (click_features), "steps" (step_features, dynamics only) and "thd" (%). p has name, kind (the draft's
    mapping), choices, min and max. eq_band names the band for an EQ's frequency, gain or width ("low",
    "lowmid"…) when the caller measured it with that band raised: then only the response counts. Where one end
    does nothing and the other does something, a mix and a corner, loop or decay moving out of the way look alike:
    the role is NEED_MID until the caller passes the features half way (mid), where a mix keeps the wet end's
    character at a lower level and the others don't.
    """
    lo, hi = ends.get("min"), ends.get("max")
    if not lo or not hi:
        return None, "measured at one end only"
    if lo.get("diverged") or hi.get("diverged") or lo.get("error") or hi.get("error"):
        return None, "diverged or failed at an end"
    rl, rh = lo.get("response"), hi.get("response")
    cl, ch, cd = lo.get("click") or {}, hi.get("click") or {}, (default or {}).get("click") or {}
    sl, sh = lo.get("steps") or {}, hi.get("steps") or {}
    span = p.get("max", 0) - p.get("min", 0)
    choice = p.get("kind") in ("enum", "switch") or (span <= 4 and p.get("kind") in (None, "int"))
    texts = " ".join(str(t).lower() for t in (p.get("choices") or p.get("texts") or []))
    t_lo, t_hi = _transparent(lo, mono_gain), _transparent(hi, mono_gain)
    if choice:
        return _choice_role(p, texts, lo, hi, rl, rh, cl, ch, sl, sh, machine_type, t_lo, t_hi)
    is_delay = machine_type in ("delay", "multi-effect") or (machine_type not in ("reverb", "modulation") and
                                                              (len(cl.get("echoes") or []) >= 2 or len(ch.get("echoes") or []) >= 2))
    spectral_first = bool(eq_band) or machine_type in ("filter", "filter or EQ", "EQ")

    # mix: one end does nothing (wet at 0), the other end does something.
    if t_lo != t_hi and not eq_band:
        other = hi if t_lo else lo
        ro, co, so = other.get("response") or {}, other.get("click") or {}, other.get("steps") or {}
        timed = bool(co.get("echoes")) or (co.get("late_db") if co.get("late_db") is not None else -120) >= -35
        shaped = (ro.get("spread_db") or 0) >= 3 or (ro.get("wobble_db") or 0) >= 1.0
        squeezed = (so.get("compression_db") or 0) >= 3 and machine_type != "dynamics"
        dry_end = "min" if t_lo else "max"
        if timed or shaped or squeezed:
            what = (f"echoes from {_fmt_s(co['echoes'][0][0])}" if co.get("echoes") else
                    f"a tail ({_fmt_s(co.get('t60_s'))} T60)" if timed else
                    f"the response's shape ({ro.get('spread_db')} dB)" if (ro.get("spread_db") or 0) >= 3 else
                    f"compression ({so.get('compression_db')} dB)" if squeezed else "a moving response")
            if mid is None:
                return NEED_MID, "a mix, or something moving out of the way: measure half way"
            rm, cm, sm = mid.get("response") or {}, mid.get("click") or {}, mid.get("steps") or {}
            blend = True
            why = []
            if timed:
                tm, to_ = _tail(cm), _tail(co)
                if tm is not None and to_ is not None and to_ > 0.05 and not (0.6 <= (tm or 0.001) / to_ <= 1.6):
                    blend, _ = False, why.append(f"half way the tail is {_fmt_s(tm)} against {_fmt_s(to_)}")
                if machine_type != "reverb" and co.get("echoes") and cm.get("echoes") and abs(len(cm["echoes"]) - len(co["echoes"])) >= 2:
                    blend = False
            if shaped and not timed and rm and ro:
                moved = max(abs(math.log2(rm["high_corner_hz"] / ro["high_corner_hz"])), abs(math.log2(rm["low_corner_hz"] / ro["low_corner_hz"])))
                if moved >= 1.0:
                    blend = False
            if blend:
                return "mix.wet", f"at its {dry_end}imum the effect passes the signal unchanged; at the other end {what}; half way less of the same"
    # mix.dry: the direct sound comes and goes, the tail stays.
    dl, dh, ll, lh = _num(cl.get("direct_db")), _num(ch.get("direct_db")), _num(cl.get("late_db")), _num(ch.get("late_db"))
    if not eq_band and None not in (dl, dh, ll, lh) and abs(dh - dl) >= 12 and min(dl, dh) < -25 and abs(lh - ll) <= 4 and min(ll, lh) > -45:
        return "mix.dry", f"the direct sound {dl:+.0f} → {dh:+.0f} dB while the tail stays ({ll:+.0f} → {lh:+.0f} dB)"

    # modulation: the response moves while it is measured, at one end only
    wl, wh = _num((rl or {}).get("wobble_db")), _num((rh or {}).get("wobble_db"))
    tails = max(cl.get("late_db") if cl.get("late_db") is not None else -120, ch.get("late_db") if ch.get("late_db") is not None else -120)
    if not eq_band and machine_type not in ("reverb", "delay", "multi-effect") and tails < -40 and \
            wl is not None and wh is not None and max(wl, wh) >= 1.0 and min(wl, wh) < 0.4:
        return ("mod.depth" if machine_type == "modulation" else "lfo.depth"), f"the gain moves {wl:.1f} → {wh:.1f} dB within each tone"

    def spectral():
        # a reverb's, delay's or chorus's response to short tones holds its own tail and combs: no corners from it
        if not (rl and rh) or (machine_type in ("modulation", "reverb", "delay") and not eq_band):
            return None
        dc = rh["centroid_oct"] - rl["centroid_oct"]
        dlow = math.log2(rh["low_corner_hz"] / rl["low_corner_hz"])
        dhigh = math.log2(rh["high_corner_hz"] / rl["high_corner_hz"])
        dprom = (rh.get("prominence_db") or 0) - (rl.get("prominence_db") or 0)
        dwidth = (rh.get("width_oct") or 0) - (rl.get("width_oct") or 0)
        dmax = rh["max_db"] - rl["max_db"]
        unit = p.get("unit") or ""
        if eq_band:
            center = abs(math.log2(rh["max_hz"] / rl["max_hz"]))
            if abs(dmax) >= 6 and (p.get("gain") or unit == "dB"):
                at = rh["max_hz"] if dmax > 0 else rl["max_hz"]
                return f"eq.{eq_band}.gain", f"the band at {_fmt_hz(at)} {rl['max_db']:+.0f} → {rh['max_db']:+.0f} dB"
            if abs(dwidth) >= 0.5 and center < 0.5 and unit != "Hz":
                return f"eq.{eq_band}.q", f"the band's width at {_fmt_hz(rh['max_hz'])} {rl['width_oct']} → {rh['width_oct']} octaves"
            if (center >= 0.75 or abs(dc) >= 0.5 or max(abs(dlow), abs(dhigh)) >= 1.0) and unit != "dB":
                mover = "high" if abs(dhigh) >= abs(dlow) else "low"
                return f"eq.{eq_band}.freq", (f"the raised band moves {_fmt_hz(rl['max_hz'])} → {_fmt_hz(rh['max_hz'])} (its "
                                              f"{'upper' if mover == 'high' else 'lower'} corner {_fmt_hz(rl[mover + '_corner_hz'])} → {_fmt_hz(rh[mover + '_corner_hz'])})")
            return None
        if abs(dprom) >= 6:
            hi_end = rh if dprom > 0 else rl
            return "filter.resonance", f"a peak at {_fmt_hz(hi_end['max_hz'])}: its prominence {rl['prominence_db']:+.0f} → {rh['prominence_db']:+.0f} dB over the response"
        if abs(dhigh) >= 1.0 or abs(dlow) >= 1.0 or abs(dc) >= 0.7:
            mover = "high" if abs(dhigh) >= abs(dlow) else "low"
            word = (f"the response's {'upper' if mover == 'high' else 'lower'} corner moves "
                    f"{_fmt_hz(rl[mover + '_corner_hz'])} → {_fmt_hz(rh[mover + '_corner_hz'])}")
            if mover == "low" and abs(dhigh) < 0.34 and machine_type not in ("filter", "filter or EQ"):
                return "filter.highpass", word
            return "filter.cutoff", word
        if abs(dwidth) >= 0.5 and abs(dprom) < 6 and abs(dc) < 0.7 and _dshape(rl, rh) >= 3:
            return "filter.bandwidth", f"the band's width {rl['width_oct']} → {rh['width_oct']} octaves"
        return None

    if spectral_first:
        got = spectral()
        if got:
            return got
        if eq_band:
            return None, "no measurable change with its band raised"

    # time: the first echo (or the start of the late sound) moves: between the ends, or an end and the default.
    el, eh = _first(cl), _first(ch)
    if el is None or eh is None:
        ed = _first(cd)
        if ed is not None and (el is not None or eh is not None):
            el, eh = (el, ed) if el is not None else (ed, eh)
    if el is not None and eh is not None and abs(eh - el) >= 0.005 and (_ratio(el, eh) or 0) >= 1.3:
        if machine_type == "modulation":
            return "mod.delay", f"the delayed copy moves {_fmt_s(el)} → {_fmt_s(eh)}"
        if machine_type == "reverb":
            tl_, th_ = _tail(cl), _tail(ch)
            if tl_ and th_ and (_ratio(tl_, th_) or 0) >= 2.0:
                return "reverb.size", f"the reverb starts {_fmt_s(el)} → {_fmt_s(eh)} after the sound and its tail {_fmt_s(tl_)} → {_fmt_s(th_)}"
            return "reverb.predelay", f"the reverb starts {_fmt_s(el)} → {_fmt_s(eh)} after the sound, its tail alike"
        left = (_first(cl, "echoes_left"), _first(ch, "echoes_left"))
        right = (_first(cl, "echoes_right"), _first(ch, "echoes_right"))
        lm = None not in left and abs(left[1] - left[0]) >= 0.005
        rm_ = None not in right and abs(right[1] - right[0]) >= 0.005
        if lm and not rm_ and right[0] is not None:
            return "delay.time_left", f"the left echo moves {_fmt_s(left[0])} → {_fmt_s(left[1])}, the right stays"
        if rm_ and not lm and left[0] is not None:
            return "delay.time_right", f"the right echo moves {_fmt_s(right[0])} → {_fmt_s(right[1])}, the left stays"
        return "delay.time", f"the first echo moves {_fmt_s(el)} → {_fmt_s(eh)}"
    if machine_type == "modulation":
        ql, qh, qd = _num(cl.get("copy_s")), _num(ch.get("copy_s")), _num(cd.get("copy_s"))
        if (ql is None) != (qh is None) and qd is not None:
            ql, qh = (ql, qd) if ql is not None else (qd, qh)
        if ql is not None and qh is not None and abs(qh - ql) >= 0.003 and (_ratio(ql, qh) or 0) >= 1.4:
            return "mod.delay", f"the delayed copy moves {_fmt_s(ql)} → {_fmt_s(qh)}"
    ol, oh = _num(cl.get("late_onset_s")), _num(ch.get("late_onset_s"))
    if machine_type == "reverb" and ol is not None and oh is not None and abs(oh - ol) >= 0.008 and (_ratio(ol, oh) or 0) >= 1.5:
        return "reverb.predelay", f"the reverb starts {_fmt_s(ol)} → {_fmt_s(oh)} after the sound"
    zl, zh = _num(cl.get("tail_zcr_hz")), _num(ch.get("tail_zcr_hz"))
    if machine_type in ("delay", "multi-effect") and zl and zh and (_ratio(zl, zh) or 0) >= 1.6:
        return "delay.tone", f"the echoes' brightness {_fmt_hz(zl)} → {_fmt_hz(zh)} (RMS frequency)"
    # the tail: decay or feedback (against the default too: feedback runs -100 … +100 %).
    spans = [x for x in (_tail(cl), _tail(ch), _tail(cd)) if x is not None]
    if len(spans) >= 2 and max(spans) >= 0.1 and max(spans) >= 2.0 * max(min(spans), 0.02):
        word = f"the tail {_fmt_s(min(spans))} → {_fmt_s(max(spans))} (T60)"
        if is_delay:
            return "delay.feedback", word + ", the echo time unchanged"
        if machine_type == "reverb":
            return "reverb.decay", word
        if machine_type == "modulation":
            return "mod.feedback", word
        if rl and rh and abs((rh.get("prominence_db") or 0) - (rl.get("prominence_db") or 0)) >= 6:
            return "filter.resonance", word + f", a peak {rl.get('prominence_db')} → {rh.get('prominence_db')} dB"
    # the wet level: the late sound comes and goes while the direct stays.
    if None not in (dl, dh, ll, lh) and abs(lh - ll) >= 10 and abs(dh - dl) <= 3 and max(ll, lh) > -40:
        return "mix.wet", f"the late sound {ll:+.0f} → {lh:+.0f} dB, the direct sound unchanged"
    if machine_type == "reverb":
        el_, eh_ = _num(cl.get("early_db")), _num(ch.get("early_db"))
        if el_ is not None and eh_ is not None and abs(eh_ - el_) >= 8 and abs((lh or 0) - (ll or 0)) < 4:
            return "reverb.early", f"the early reflections {el_:+.0f} → {eh_:+.0f} dB"
    if machine_type == "reverb" and zl and zh and (_ratio(zl, zh) or 0) >= 1.4:
        return "reverb.damping", f"the tail's brightness {_fmt_hz(zl)} → {_fmt_hz(zh)} (RMS frequency)"
    bl_, bh_ = _num(cl.get("tail_low_db")), _num(ch.get("tail_low_db"))
    if machine_type == "reverb" and bl_ is not None and bh_ is not None and abs(bh_ - bl_) >= 6:
        return "reverb.lowcut", f"the tail's share under 250 Hz {bl_:+.0f} → {bh_:+.0f} dB"
    if not spectral_first and machine_type != "dynamics":
        got = spectral()
        if got:
            return got
    # dynamics, from the level steps: lookahead, a shift of the whole curve, the timing, the knee, the depth
    sd = (default or {}).get("steps") or {}
    if sl and sh:
        la, lb = _num(cl.get("latency_samples")), _num(ch.get("latency_samples"))
        if la is not None and lb is not None and abs(lb - la) >= 44:
            return "dyn.lookahead", f"the latency {la:.0f} → {lb:.0f} samples"
        gl = [g for _, g in sl.get("curve") or []][:4]
        gh = [g for _, g in sh.get("curve") or []][:4]
        if len(gl) == len(gh) == 4:
            shift = [b_ - a_ for a_, b_ in zip(gl, gh)]
            if abs(sum(shift) / 4) >= 3 and max(shift) - min(shift) < 2:
                return ("dyn.makeup" if machine_type == "dynamics" else "level"), f"the quiet steps {sum(shift) / 4:+.1f} dB, all alike"
        al, ah = _num(sl.get("attack_tau_s")), _num(sh.get("attack_tau_s"))
        rl_, rh_ = _num(sl.get("release_tau_s")), _num(sh.get("release_tau_s"))
        ra = (_ratio(al + 0.005, ah + 0.005) or 0) if al is not None and ah is not None and abs(ah - al) >= 0.01 else 0
        rr = (_ratio(rl_ + 0.005, rh_ + 0.005) or 0) if rl_ is not None and rh_ is not None and abs(rh_ - rl_) >= 0.02 else 0
        if max(ra, rr) >= 2:
            if ra >= rr:
                return "dyn.attack", f"the gain moves 63 % of the way in {_fmt_s(al)} → {_fmt_s(ah)} after a 30 dB jump up"
            return "dyn.release", f"the gain recovers 63 % of the way in {_fmt_s(rl_)} → {_fmt_s(rh_)} after the jump down"
        sets = [(n, x) for n, x in (("min", sl), ("max", sh), ("default", sd)) if x]
        squeezing = [(n, x) for n, x in sets if (_num(x.get("compression_db")) or 0) >= 2 and _num(x.get("knee_in_db")) is not None]
        knees = [x["knee_in_db"] for _, x in squeezing]
        if len(knees) >= 2 and max(knees) - min(knees) >= 6:
            lo_k, hi_k = _num(sl.get("knee_in_db")), _num(sh.get("knee_in_db"))
            return "dyn.threshold", (f"compression starts at {('%+.0f dB' % lo_k) if lo_k is not None else 'none'} → "
                                     f"{('%+.0f dB' % hi_k) if hi_k is not None else 'none'} in (the default: {('%+.0f dB' % _num(sd.get('knee_in_db'))) if _num(sd.get('knee_in_db')) is not None else 'none'})")
        depths = [_num(x.get("compression_db")) or 0 for _, x in sets]
        if max(depths) - min(depths) >= 4:
            # a ratio scales the gain reduction above the knee alike at every level; a wet/dry blend can't take more
            # than the dry part leaves (6 dB at half), so half way its reduction flattens at the loudest steps
            full = sl if (_num(sl.get("compression_db")) or 0) >= (_num(sh.get("compression_db")) or 0) else sh
            half = mid.get("steps") if mid else None
            at_end = sd and any(abs((_num(sd.get("compression_db")) or 0) - (_num(x.get("compression_db")) or 0)) < 0.3 for x in (sl, sh))
            if half is None and sd and not at_end:
                half = sd
            if half is None:
                return NEED_MID, "a ratio or a blend: measure half way"
            gf = [g for _, g in full.get("curve") or []]
            gm = [g for _, g in half.get("curve") or []]
            word = (f"the loudest steps lose {_num(sl.get('compression_db')) or 0:.0f} → {_num(sh.get('compression_db')) or 0:.0f} dB "
                    f"against the quietest, from the same knee")
            if len(gf) == len(gm) and len(gf) >= 3:
                rf = [gf[0] - g for g in gf[-2:]]
                rm = [gm[0] - g for g in gm[-2:]]
                if min(rf) >= 3 and rm[0] > 0.5:
                    r_lower, r_top = rm[0] / rf[0], rm[1] / rf[1]
                    if r_top < r_lower - 0.1:
                        return "mix.wet", (f"at one end nothing is compressed; half way the loudest steps lose {rm[1]:.1f} dB against "
                                           f"{rf[1]:.1f} at full, the next {rm[0]:.1f} against {rf[0]:.1f}: a blend with the dry signal")
            return "dyn.ratio", word
    if machine_type == "dynamics":
        got = spectral()
        if got:
            return got
    # distortion
    hl, hh = _num(lo.get("thd")), _num(hi.get("thd"))
    if hl is not None and hh is not None and max(hl, hh) >= 3 and (_ratio(hl + 0.1, hh + 0.1) or 0) >= 3:
        return ("filter.drive" if machine_type in ("filter", "filter or EQ") else "drive"), f"harmonics {hl:.1f} → {hh:.1f} % on a sine"
    # stereo
    bl, bh = _num(cl.get("balance_db")), _num(ch.get("balance_db"))
    if bl is not None and bh is not None and abs(bh - bl) >= 6:
        return "pan", f"left over right {bl:+.0f} → {bh:+.0f} dB"
    # level: everything moves together
    if rl and rh and rl.get("level_db") is not None and rh.get("level_db") is not None:
        dl_ = rh["level_db"] - rl["level_db"]
        if abs(dl_) >= 3 and _dshape(rl, rh) < 2:
            return "level", f"the level {rl['level_db']:+.1f} → {rh['level_db']:+.1f} dB, the response's shape unchanged"
    return None, "no measurable change between its ends with the others at their defaults"


def suggest_generator_role(p, ends, default, machine_type):
    """
    A role for a generator's parameter from a held note's features at each end and at the default: pitch
    (semitones heard), centroid (Hz), hpeak_db (harmonic_peakiness: a resonance), the envelope (note_envelope:
    attack, release, length, how fast and how far it falls, brightness at the start and held, how much the held
    part moves). Drums (machine_type "drums") name drum roles. A choice moves the pitch only where its texts say
    octaves (16', 8', oct); a continuous parameter whose sound jumps by an octave is mixing a sub-oscillator in.
    """
    lo, hi = ends.get("min"), ends.get("max")
    if not lo or not hi:
        return None, "measured at one end only"
    if lo.get("diverged") or hi.get("diverged") or lo.get("error") or hi.get("error"):
        return None, "diverged or failed at an end"
    span = p.get("max", 0) - p.get("min", 0)
    choice = p.get("kind") in ("enum", "switch") or (span <= 4 and p.get("kind") in (None, "int"))
    texts = " ".join(str(t).lower() for t in (p.get("choices") or p.get("texts") or []))
    drum = machine_type == "drums"
    d = default or {}
    if lo.get("silent") or hi.get("silent"):
        quiet = "min" if lo.get("silent") else "max"
        loud = hi if quiet == "min" else lo
        if loud.get("silent") or choice:
            return None, f"silent at its {quiet}imum"
        cl_, cd_ = _num(loud.get("centroid_hz")), _num(d.get("centroid_hz"))
        if not drum and cl_ and cd_ and abs(math.log2(cd_ / cl_)) >= 0.6 and not d.get("silent"):
            return "filter.cutoff", f"silent at its {quiet}imum; the brightness (centroid) {_fmt_hz(cd_)} at the default, {_fmt_hz(cl_)} at the other end"
        return ("drum.level" if drum else "level"), f"silent at its {quiet}imum, sounding at the other end"
    el, eh = lo.get("env") or {}, hi.get("env") or {}
    pl, ph = _num(lo.get("pitch")), _num(hi.get("pitch"))
    cl, ch = _num(lo.get("centroid_hz")), _num(hi.get("centroid_hz"))

    def t(e, k):
        return _num(e.get(k))
    dpeak = (t(eh, "peak_db") or 0) - (t(el, "peak_db") or 0)
    if choice:
        if pl is not None and ph is not None and abs(ph - pl) >= 11.5 and abs((ph - pl) / 12 - round((ph - pl) / 12)) < 0.05 \
                and any(w in texts for w in ("'", "oct", "ft")):
            return "pitch.octave", f"the pitch moves {ph - pl:+.1f} semitones ({round((ph - pl) / 12):+d} octaves)"
        return None, "a choice: its texts say what it is; no role measured"
    # an LFO: the held part moves at one end, not at the other
    lw, hw = t(el, "level_wobble_db"), t(eh, "level_wobble_db")
    bw, bh = t(el, "bright_wobble"), t(eh, "bright_wobble")
    if bw is not None and bh is not None and max(bw, bh) >= 0.25 and min(bw, bh) < 0.08:
        return "lfo.filter", f"the held note's brightness moves {bw:.0%} → {bh:.0%} of its mean"
    if lw is not None and hw is not None and max(lw, hw) >= 3 and min(lw, hw) < 1:
        return "lfo.amp", f"the held note's level moves {lw:.1f} → {hw:.1f} dB"
    # brightness at the start against the held part: a filter envelope's amount
    ea, eb_, la_, lb_ = t(el, "bright_early_hz"), t(eh, "bright_early_hz"), t(el, "bright_late_hz"), t(eh, "bright_late_hz")
    if None not in (ea, eb_, la_, lb_) and min(ea, eb_, la_, lb_) > 0 and abs(dpeak) < 10:
        early, late = math.log2(eb_ / ea), math.log2(lb_ / la_)
        if abs(early) >= 0.5 and abs(early) >= 2 * abs(late):
            return ("drum.tone" if drum else "filter.env_amount"), (f"the start's brightness {_fmt_hz(ea)} → {_fmt_hz(eb_)} (RMS frequency), "
                                                                   f"the held part's {_fmt_hz(la_)} → {_fmt_hz(lb_)}")
    # a resonance (self-oscillating, it moves the pitch heard): a bump on the harmonics that grows where it is
    kl, kh, kd = t(lo, "tprom_db"), t(hi, "tprom_db"), t(d, "tprom_db")
    big = hi if (kh or -99) >= (kl or -99) else lo
    k_big, k_def = _num(big.get("tprom_hz")), _num(d.get("tprom_hz"))
    stays = kd is None or kd < 6 or k_big is None or k_def is None or (_ratio(k_big, k_def) or 1) < 2.0
    if kl is not None and kh is not None and abs(kh - kl) >= 12 and stays:
        return ("drum.tone" if drum else "filter.resonance"), (f"a peak at {_fmt_hz(k_big)} stands {kl:+.0f} → {kh:+.0f} dB over the "
                                                               f"spectrum an octave either side")
    if pl is not None and ph is not None and abs(ph - pl) >= 0.4:
        dp = ph - pl
        if drum:
            return "drum.tune", f"the pitch moves {dp:+.1f} semitones"
        octaves = abs(dp) >= 11.5 and abs(dp / 12 - round(dp / 12)) < 0.05
        if octaves and p.get("kind") == "int" and span <= 8:
            return "pitch.octave", f"the pitch moves {dp:+.1f} semitones ({round(dp / 12):+d} octaves)"
        if not octaves or p.get("kind") == "int":
            return ("pitch.coarse" if abs(dp) >= 1.5 else "pitch.fine"), (f"the pitch moves {dp:+.1f} semitones" if abs(dp) >= 1.5
                                                                         else f"the pitch moves {dp * 100:+.0f} cents")
    lb1, lb2 = t(el, "bright_late_hz"), t(eh, "bright_late_hz")
    timbre_alike = lb1 is None or lb2 is None or (lb1 > 0 and lb2 > 0 and (_ratio(lb1, lb2) or 9) < 1.5)
    a, b = t(el, "attack_s"), t(eh, "attack_s")
    if max(a or 0, b or 0) >= 0.1 and a is not None and b is not None and abs(b - a) >= 0.02 and (_ratio(a + 0.005, b + 0.005) or 0) >= 3:
        return ("drum.attack" if drum else "env.attack"), f"the attack {_fmt_s(a)} → {_fmt_s(b)}"
    ra, rb = t(el, "release_s"), t(eh, "release_s")
    if abs(dpeak) < 6 and timbre_alike and ra is not None and rb is not None and abs(rb - ra) >= 0.05 and (_ratio(ra + 0.005, rb + 0.005) or 0) >= 2:
        past = el.get("release_past_window") or eh.get("release_past_window")
        return ("drum.release" if drum else "env.release"), f"the release {_fmt_s(ra)} → {_fmt_s(rb)}" + (" (or longer: past the window)" if past else "")
    la, lb = t(el, "length_s"), t(eh, "length_s")
    if drum and abs(dpeak) < 10 and la is not None and lb is not None and abs(lb - la) >= 0.03 and (_ratio(la, lb) or 0) >= 2:
        return "drum.decay", f"the hit lasts {_fmt_s(la)} → {_fmt_s(lb)} (to 40 dB down)"
    if abs(dpeak) < 6 and timbre_alike:
        # decay or sustain: against the default, a decay moves how fast the level falls, a sustain where it ends
        ed = d.get("env") or {}
        ha, hb, hd = t(el, "half_s"), t(eh, "half_s"), t(ed, "half_s")
        fa, fb, fd = t(el, "final_db"), t(eh, "final_db"), t(ed, "final_db")
        falls = el if ha is not None else eh if hb is not None else None
        if falls is not None and hd is not None and fd is not None:
            hf, ff = t(falls, "half_s"), t(falls, "final_db")
            if abs(fd - ff) >= 4 and (_ratio(hd + 0.005, hf + 0.005) or 9) < 1.5 and fa is not None and fb is not None and abs(fb - fa) >= 6:
                return ("drum.decay" if drum else "env.sustain"), f"the held level {fa:+.0f} → {fb:+.0f} dB under the peak, falling as fast"
            if abs(fd - ff) < 4 and (_ratio(hd + 0.005, hf + 0.005) or 0) >= 1.5:
                return ("drum.decay" if drum else "env.decay"), f"the level falls half way in {_fmt_s(ha)} → {_fmt_s(hb)} (the default {_fmt_s(hd)}) to the same held level"
        if ha is not None and hb is not None and abs(hb - ha) >= 0.03 and (_ratio(ha + 0.005, hb + 0.005) or 0) >= 2:
            return ("drum.decay" if drum else "env.decay"), f"the level falls half way to its held level in {_fmt_s(ha)} → {_fmt_s(hb)}"
        if fa is not None and fb is not None and abs(fb - fa) >= 6:
            return ("drum.decay" if drum else "env.sustain"), f"the held level {fa:+.0f} → {fb:+.0f} dB under the peak"
    if cl and ch and abs(math.log2(ch / cl)) >= 0.75:
        return ("drum.tone" if drum else "filter.cutoff"), f"the brightness (centroid) {_fmt_hz(cl)} → {_fmt_hz(ch)}"
    if abs(dpeak) >= 6:
        return ("drum.level" if drum else "level"), f"the level {dpeak:+.0f} dB, the sound otherwise alike"
    return None, "no measurable change between its ends with the others at their defaults"


def third_prominence(hz, db, f0=None):
    """A spectral peak in third octaves: the band standing most over the louder of the bands an octave either side (dB,
    each floored 60 dB under the loudest), from nine times the note's pitch up (where a low note's harmonics fill every
    band it is compared with), and its centre: (dB, Hz) or None."""
    vals = [(h, v if isinstance(v, (int, float)) else None) for h, v in zip(hz or [], db or [])]
    top = max((v for _, v in vals if v is not None), default=None)
    if top is None:
        return None
    vals = [(h, max(v, top - 60) if v is not None else top - 60) for h, v in vals]
    lo = 9 * (f0 or 55)        # from here a third octave and the bands an octave below it each hold a harmonic or more
    best = None
    for i in range(3, len(vals) - 3):
        h, v = vals[i]
        if h < lo or h > 12000 or v <= top - 40:
            continue
        d = v - max(vals[i - 3][1], vals[i + 3][1])
        if best is None or d > best[0]:
            best = (round(d, 2), h)
    return best


def peakiness(third_db):
    """The largest bump in third-octave levels: a band over the mean of the bands two away on each side (dB), each
    band floored 60 dB under the loudest (empty bands would make any band a peak)."""
    vals = [v if isinstance(v, (int, float)) else None for v in third_db]
    top = max((v for v in vals if v is not None), default=None)
    if top is None:
        return None
    vals = [max(v, top - 60) if v is not None else top - 60 for v in vals]
    best = None
    for i in range(2, len(vals) - 2):
        a, b, c = vals[i - 2], vals[i], vals[i + 2]
        if b <= top - 50:
            continue
        d = b - (a + c) / 2
        best = d if best is None else max(best, d)
    return round(best, 2) if best is not None else None


# ---- curated roles against suggested ones -------------------------------------------------------------------

FILTER_ROLES = ("filter.", "eq.")
TIME_ROLES = ("delay.time", "delay.time_left", "delay.time_right", "delay.feedback", "delay.unit", "reverb.decay", "reverb.predelay",
              "env.attack", "env.decay", "env.release", "filter.env.attack", "filter.env.decay", "filter.env.release",
              "dyn.attack", "dyn.release", "dyn.lookahead", "drum.decay", "drum.attack", "drum.release", "mod.delay", "env.follower_release")
MIX_ROLES = ("mix.wet", "mix.dry", "reverb.early")


def category(role):
    """filter, time, mix or None: the exit check's three (mcp-server.md §12.7, U2)."""
    if not role:
        return None
    if role in MIX_ROLES:
        return "mix"
    if role in TIME_ROLES:
        return "time"
    if role.startswith(FILTER_ROLES) and not role.startswith("filter.env."):
        return "filter"
    return None


def agrees(curated, suggested):
    """Whether a suggested role agrees with a curated one: the same, or the same family where the profile can't
    tell them apart (drum.decay for env.decay on a drum; delay.time for its left or right; eq.*.freq for a filter's cutoff)."""
    if not curated or not suggested:
        return False
    if curated == suggested:
        return True
    same = [("env.decay", "drum.decay"), ("env.release", "drum.release"), ("env.attack", "drum.attack"),
            ("delay.time", "delay.time_left"), ("delay.time", "delay.time_right"), ("delay.time_left", "delay.time_right"),
            ("dyn.release", "env.follower_release"), ("level", "drum.level"), ("filter.highpass", "filter.cutoff")]
    return (curated, suggested) in same or (suggested, curated) in same
