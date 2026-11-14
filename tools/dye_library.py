"""The data behind tools/dye-preset-gen.py: Dye Essence's generated factory presets (docs/specs/dye-essence.md section 7).

Every sound here is described from scratch in the machine's own terms (engines, tables, filters, envelopes, the rack); no name in
it, and no setting, is taken from another product. A base is a sound with its character; a modifier changes tone, time, layers
or motion; an effect recipe sets one of the rack's twelve types in units. Values are in the units of Presets.txt (Hz, seconds,
percent, cents, semitones, dB).
"""
import random

SEED = 20261113
DEFAULT_VOLUME = -10

# Every preset starts here, so a modifier can always scale what a base leaves alone.
COMMON = ("Voice mode=Poly;A engine=Wavetable;A table=Basic;A position=66.7;A octave=0;A semi=0;A fine=0;A level=100;A pan=0;"
          "A unison=1;A detune=10;A spread=70;F1 type=LP 12;F1 cutoff=4000;F1 reso=15;F1 env=0;F1 key track=50;"
          "Amp attack=0.005;Amp decay=0.5;Amp sustain=80;Amp release=0.3;Flt attack=0.005;Flt decay=0.5;Flt sustain=30;Flt release=0.3")


# ---- slot builders (one slot's parameters as text) ----

def WT(s='A', table='Basic', pos=66.7, uni=1, det=10, spr=70, oct=0, lvl=100, semi=0, fine=0, w1=None, w1a=0, w2=None, w2a=0, mod=None,
       rnd=None):
    t = (f"{s} engine=Wavetable;{s} table={table};{s} position={pos};{s} unison={uni};{s} detune={det};{s} spread={spr};"
         f"{s} octave={oct};{s} semi={semi};{s} fine={fine};{s} level={lvl}")
    if w1:
        t += f";{s} warp 1={w1};{s} warp 1 amt={w1a}"
    if w2:
        t += f";{s} warp 2={w2};{s} warp 2 amt={w2a}"
    if mod:
        t += f";{s} modulator={mod}"
    if rnd is not None:
        t += f";{s} random={rnd}"
    return t


def AN(s='A', shape='Saw', uni=1, det=12, spr=70, oct=0, lvl=100, semi=0, fine=0, pw=None, drift=20, rnd=None):
    t = (f"{s} engine=Analog;{s} shape={shape};{s} unison={uni};{s} detune={det};{s} spread={spr};{s} octave={oct};{s} semi={semi};"
         f"{s} fine={fine};{s} level={lvl};{s} drift={drift}")
    if pw is not None:
        t += f";{s} pulse width={pw}"
    if rnd is not None:
        t += f";{s} random={rnd}"
    return t


def SM(s='A', sample='Choir', loop='Forward', uni=1, det=8, spr=70, oct=0, lvl=100, semi=0, fine=0, start=None, fade=None, rev=None,
       root=None):
    t = (f"{s} engine=Sample;{s} sample={sample};{s} loop={loop};{s} unison={uni};{s} detune={det};{s} spread={spr};"
         f"{s} octave={oct};{s} semi={semi};{s} fine={fine};{s} level={lvl}")
    if start is not None:
        t += f";{s} position={start}"
    if fade is not None:
        t += f";{s} loop fade={fade}"
    if rev:
        t += f";{s} reverse=On"
    if root is not None:
        t += f";{s} root={root}"
    return t


def GR(s='A', sample='Choir', pos=40, size=0.2, rate=30, spray=20, scan=0.5, shape='Hann', uni=1, det=8, spr=80, oct=0, lvl=100,
       pitch=0, rev=0, width=60):
    return (f"{s} engine=Granular;{s} sample={sample};{s} position={pos};{s} grain size={size};{s} grain rate={rate};{s} spray={spray};"
            f"{s} scan={scan};{s} grain shape={shape};{s} unison={uni};{s} detune={det};{s} spread={spr};{s} octave={oct};"
            f"{s} level={lvl};{s} grain pitch={pitch};{s} grain reverse={rev};{s} grain width={width}")


def HM(s='A', a='Saw', b='Saw', pos=0, parts=32, tilt=0, oe=0, stretch=0, damp=0, uni=1, det=8, spr=70, oct=0, lvl=100, semi=0):
    return (f"{s} engine=Harmonic;{s} harmonics={a};{s} harmonics 2={b};{s} position={pos};{s} partials={parts};{s} tilt={tilt};"
            f"{s} odd even={oe};{s} stretch={stretch};{s} damp={damp};{s} unison={uni};{s} detune={det};{s} spread={spr};"
            f"{s} octave={oct};{s} semi={semi};{s} level={lvl}")


def MD(s='A', geom='Bar', exc='Struck', force=55, pos=25, modes=16, mat=50, ring=1.0, hf=35, size=50, uni=1, det=6, spr=60, oct=0,
       lvl=100, semi=0):
    return (f"{s} engine=Modal;{s} geometry={geom};{s} exciter={exc};{s} force={force};{s} position={pos};{s} modes={modes};"
            f"{s} material={mat};{s} ring time={ring};{s} hf damp={hf};{s} body size={size};{s} unison={uni};{s} detune={det};"
            f"{s} spread={spr};{s} octave={oct};{s} semi={semi};{s} level={lvl}")


def SP(s='A', sample='Choir', speed=100, key=100, formant=0, snap=0, bal=50, blur=0, parts=48, tilt=0, stretch=0, loop='Forward', uni=1, det=8,
       spr=70, oct=0, lvl=100, semi=0, pos=None, rev=None):
    t = (f"{s} engine=Spectral;{s} sample={sample};{s} loop={loop};{s} spectral speed={speed};{s} key tracking={key};{s} formant keep={formant};"
         f"{s} harmonic snap={snap};{s} tonal noise={bal};{s} blur={blur};{s} spectral partials={parts};{s} spectral tilt={tilt};"
         f"{s} spectral stretch={stretch};{s} unison={uni};{s} detune={det};{s} spread={spr};{s} octave={oct};{s} semi={semi};{s} level={lvl}")
    if pos is not None:
        t += f";{s} position={pos}"
    if rev:
        t += f";{s} reverse=On"
    return t


def FLT(cut, res=15, env=0, typ='LP 12', fd=None, fs=None, drive=None, key=None):
    t = f"F1 type={typ};F1 cutoff={cut};F1 reso={res};F1 env={env}"
    if fd is not None:
        t += f";Flt decay={fd}"
    if fs is not None:
        t += f";Flt sustain={fs}"
    if drive is not None:
        t += f";F1 drive={drive}"
    if key is not None:
        t += f";F1 key track={key}"
    return t


def ENV(a=0.005, d=0.5, s=80, r=0.3):
    return f"Amp attack={a};Amp decay={d};Amp sustain={s};Amp release={r}"


MONO = "Voice mode=Mono;Glide mode=Legato;Glide=0.05"


def seq(seed, n, scale=(0, 3, 5, 7, 10), rests=0.15, ties=0.1, lo=-12, hi=12, vel=(70, 115), gate=(40, 70), prob=None):
    """A deterministic step pattern (the sequencer's text: offset,velocity,gate,tie,probability)."""
    rng = random.Random(seed)
    steps = []
    for i in range(n):
        if i > 0 and rng.random() < rests:
            steps.append("0,0")
            continue
        off = (0 if i % 4 == 0 and rng.random() < 0.7 else rng.choice(scale) + rng.choice((-12, 0, 0, 12)))
        off = max(lo, min(hi, off))
        v = rng.randint(*vel)
        g = rng.randint(*gate)
        tie = 1 if rng.random() < ties and i < n - 1 else 0
        if tie:
            g = 100
        p = rng.choice(prob) if prob else 100
        steps.append(f"{off},{v},{g},{tie},{p}" if (tie or prob) else f"{off},{v},{g}")
    return ' '.join(steps)


def gate_shape(pattern):
    """A drawn LFO shape from a string of '1' (high) and '0' (low), one character a step across the cycle."""
    n = len(pattern)
    pts = []
    for i, ch in enumerate(pattern):
        v = 1 if ch == '1' else -1
        t0, t1 = i / n, (i + 1) / n - 0.004
        pts.append(f"{t0:.4f},{v}")
        pts.append(f"{t1:.4f},{v}")
    return ' '.join(pts)


# ---- effect recipes (the rack's twelve types) ----

FX = {}


def fx(key, typ, role, mix, p, ret=None, post='', desc='', bus='A'):
    FX[key] = dict(key=key, type=typ, role=role, mix=mix, p=p, ret=ret, post=post, desc=desc, bus=bus)


# Reverb (the rack's Verb: p1 mode, p2 pre-delay, p3 size, p4 decay, p5 low decay, p6 damping, p7 shimmer, p8 width, p9 low cut, p10 high cut)
fx('v_room', 'Verb', 'parallel', 100, {1: 62, 2: 35, 3: 35, 4: 32, 6: 45}, -8, 'in a Room', 'a small room reverb on the bus')
fx('v_hall', 'Verb', 'parallel', 100, {1: 25, 2: 50, 3: 55, 4: 56, 6: 50}, -8, 'in a Hall', 'a hall reverb of about three seconds on the bus')
fx('v_plate', 'Verb', 'parallel', 100, {1: 100, 2: 30, 3: 45, 4: 45, 6: 55, 10: 60}, -9, 'on a Plate', 'a bright plate on the bus')
fx('v_cathedral', 'Verb', 'parallel', 100, {1: 0, 2: 60, 3: 85, 4: 68, 5: 55, 6: 38}, -10, 'in a Cathedral', 'a huge, slow reverb on the bus')
fx('v_dark', 'Verb', 'parallel', 100, {1: 0, 4: 58, 6: 28, 10: 35}, -8, 'in a Dark Hall', 'a dark hall on the bus')
fx('v_shimmer', 'Verb', 'parallel', 100, {1: 0, 4: 62, 6: 55, 7: 70}, -11, 'with Shimmer', 'a reverb with an octave shimmer on the bus')
fx('v_chamber', 'Verb', 'parallel', 100, {1: 85, 2: 40, 3: 60, 4: 48}, -8, 'in a Chamber', 'a chamber reverb on the bus')
fx('v_hall_s', 'Verb', 'serial', 28, {1: 25, 2: 50, 3: 55, 4: 56, 6: 50}, None, 'in a Hall', 'a hall reverb at 28 % on the output')
fx('v_room_s', 'Verb', 'serial', 22, {1: 62, 2: 35, 3: 35, 4: 32, 6: 45}, None, 'in a Room', 'a small room at 22 % on the output')
# Convolution (p1 IR, p2 size, p3 pre-delay, p4 decay, p5 damping, p6 low cut, p7 high cut, p8 width, p9 reverse, p10 output)
fx('c_room_s', 'Convolution', 'serial', 24, {1: 0}, None, 'in a Small Room', 'a built-in small room response at 24 %')
fx('c_room', 'Convolution', 'parallel', 100, {1: 10, 3: 40}, -8, 'in a Room', 'a convolved room on the bus')
fx('c_chamber', 'Convolution', 'parallel', 100, {1: 22, 3: 45}, -8, 'in a Chamber', 'a convolved chamber on the bus')
fx('c_plate', 'Convolution', 'parallel', 100, {1: 40}, -9, 'on a Plate', 'a convolved plate on the bus')
fx('c_hall', 'Convolution', 'parallel', 100, {1: 52, 3: 60}, -8, 'in a Long Hall', 'a convolved hall with a pre-delay on the bus')
fx('c_cathedral', 'Convolution', 'parallel', 100, {1: 75, 4: 42, 6: 55}, -10, 'in a Stone Cathedral', 'the cathedral response, a little shortened, on the bus')
fx('c_reverse', 'Convolution', 'parallel', 100, {1: 52, 2: 60, 9: 100}, -9, 'with Reverse Hall', 'a stretched, reversed hall that swells into each note')
fx('c_dark', 'Convolution', 'parallel', 100, {1: 52, 5: 30, 7: 38}, -8, 'in a Dark Hall', 'a hall response darkening with time on the bus')
# Delay (p1 mode, p2 sync, p3 feel, p4 time, p5 offset, p6 feedback, p7 low cut, p8 high cut, p9 mod depth, p10 width)
fx('d_dotted', 'Delay', 'parallel', 100, {1: 0, 2: 50, 3: 50, 6: 52, 8: 45}, -9, 'with Dotted Echo', 'a dotted-eighth echo on the bus')
fx('d_quarter', 'Delay', 'parallel', 100, {2: 60, 3: 10, 6: 52, 8: 40}, -10, 'with Quarter Echo', 'a quarter-note echo on the bus')
fx('d_pingpong', 'Delay', 'parallel', 100, {1: 75, 2: 50, 3: 10, 6: 56, 8: 45}, -9, 'with Ping-Pong', 'a ping-pong eighth echo on the bus')
fx('d_slap', 'Delay', 'parallel', 100, {2: 30, 3: 10, 6: 40, 8: 50}, -10, 'with Slapback', 'a short slapback on the bus')
fx('d_long', 'Delay', 'parallel', 100, {2: 70, 3: 10, 6: 62, 8: 35, 10: 70}, -11, 'with Long Echo', 'a half-note echo with a dark tail on the bus')
fx('d_triplet', 'Delay', 'parallel', 100, {2: 60, 3: 85, 6: 55, 8: 42, 9: 60}, -10, 'with Triplet Echo', 'a triplet echo with a little modulation on the bus')
fx('d_cross', 'Delay', 'parallel', 100, {1: 95, 2: 50, 3: 50, 6: 54, 8: 45, 10: 80}, -10, 'with Cross Echo', 'a cross-fed dotted echo on the bus')
fx('d_dotted_s', 'Delay', 'serial', 30, {1: 0, 2: 50, 3: 50, 6: 50, 8: 45}, None, 'with Dotted Echo', 'a dotted echo at 30 % on the output')
fx('d_pingpong_s', 'Delay', 'serial', 28, {1: 75, 2: 50, 3: 10, 6: 52, 8: 45}, None, 'with Ping-Pong', 'a ping-pong echo at 28 % on the output')
# Dimension (p1 voices, p2 rate, p3 depth, p4 delay, p5 width, p6 shape, p7 tone, p8 low cut, p9 drift, p10 feedback)
fx('dim_wide', 'Dimension', 'parallel', 100, {1: 60, 2: 50, 3: 55, 5: 70}, -4, 'Widened', 'a three-voice Dimension widening it on the bus')
fx('dim_wide_s', 'Dimension', 'serial', 60, {1: 60, 2: 50, 3: 55, 5: 70}, None, 'Widened', 'a three-voice Dimension at 60 %')
fx('dim_flange', 'Dimension', 'serial', 50, {1: 0, 2: 30, 3: 70, 4: 15, 10: 75}, None, 'Flanged', 'a slow flange from a fed-back Dimension at 50 %')
fx('dim_chorus', 'Dimension', 'serial', 60, {1: 0, 2: 55, 3: 45, 4: 60}, None, 'Chorused', 'a two-voice chorus at 60 %')
fx('dim_four', 'Dimension', 'parallel', 100, {1: 100, 2: 40, 3: 65, 4: 55, 5: 90, 6: 100}, -5, 'in Four Voices', 'four triangle-modulated voices on the bus')
# Bitcrusher (p1 bits, p2 rate, p3 anti-alias, p4 drive, p5 quantizer, p6 bias, p7 dither, p8 tone, p9 wobble, p10 output)
fx('crush_lofi', 'Bitcrusher', 'serial', 100, {1: 38, 2: 38, 3: 70, 9: 40}, None, 'Crushed', 'six bits and about 4 kHz with a slow wow')
fx('crush_dust', 'Bitcrusher', 'serial', 70, {1: 60, 2: 52, 7: 30}, None, 'Dusted', 'a light crush to ten bits with dither at 70 %')
fx('crush_hard', 'Bitcrusher', 'serial', 100, {1: 25, 2: 30, 3: 70, 4: 60, 5: 85}, None, 'Crushed Hard', 'four and a half bits at 1.8 kHz, mid-rise')
fx('crush_tape', 'Bitcrusher', 'serial', 100, {1: 50, 2: 52, 8: 40, 9: 35}, None, 'on Worn Tape', 'a gentle crush with a darker tone and tape wow')
fx('crush_flutter', 'Bitcrusher', 'serial', 80, {1: 45, 2: 45, 3: 60, 9: 62, 8: 45}, None, 'with Flutter', 'a crush with a fast flutter at 80 %')
# Freq shift (p1 shift, p2 range, p3 mode, p4 feedback, p5 feedback tone, p6 stereo, p7 LFO rate, p8 LFO depth, p9 glide, p10 output)
fx('shift_barber', 'Freq shift', 'parallel', 70, {1: 54, 4: 60, 6: 62, 7: 30, 8: 58}, -6, 'Phased', 'a slow fed-back frequency shift on bus B', 'B')
fx('shift_barber_s', 'Freq shift', 'serial', 45, {1: 54, 4: 58, 7: 30, 8: 58}, None, 'Phased', 'a slow fed-back frequency shift at 45 %')
fx('shift_ring', 'Freq shift', 'serial', 35, {1: 55, 3: 70}, None, 'Ring-Modulated', 'a ring modulation at 35 %')
fx('shift_stereo', 'Freq shift', 'serial', 50, {1: 53, 6: 75}, None, 'Split Wide', 'a tiny shift pulling the channels apart')
fx('shift_up', 'Freq shift', 'parallel', 60, {1: 60, 2: 40, 4: 55}, -9, 'with Rising Echo', 'an upward shift with feedback on the bus')
# Pitch delay (p1 time, p2 pitch (50 is 0 st), p3 fine, p4 feedback, p5 grain, p6 mode, p7 tone, p8 low cut, p9 spread, p10 cross-feed)
fx('pd_shimmer', 'Pitch delay', 'parallel', 100, {1: 55, 2: 75, 4: 62, 7: 42, 9: 60, 10: 40}, -8, 'with Shimmer Echoes', 'octave-up echoes that climb on each repeat')
fx('pd_octdown', 'Pitch delay', 'parallel', 100, {1: 50, 2: 25, 4: 58, 6: 100, 7: 45}, -8, 'with Falling Echoes', 'one octave-down echo trail')
fx('pd_fifth', 'Pitch delay', 'parallel', 100, {1: 55, 2: 64.6, 4: 55, 7: 50}, -9, 'with Fifth Echoes', 'echoes a fifth up')
fx('pd_detune', 'Pitch delay', 'parallel', 100, {1: 40, 2: 50, 3: 66, 4: 45, 9: 70}, -8, 'with Detuned Echo', 'a detuned echo spread wide')
fx('pd_double', 'Pitch delay', 'serial', 40, {1: 22, 2: 50, 3: 60, 4: 0, 9: 60}, None, 'Doubled', 'a short, slightly sharp double at 40 %')
# Filter (p1 type, p2 slope, p3 cutoff, p4 resonance, p5 drive, p6 env amount, p7 LFO rate, p8 LFO sync, p9 LFO shape, p10 LFO depth)
fx('flt_sweep', 'Filter', 'serial', 100, {1: 70, 3: 45, 4: 55, 7: 40, 10: 70}, None, 'Swept', 'a low-pass swept by a slow LFO')
fx('flt_notch', 'Filter', 'serial', 80, {1: 90, 3: 55, 4: 70, 7: 45, 10: 65}, None, 'Phase-Swept', 'a notch swept by an LFO at 80 %')
fx('flt_hp', 'Filter', 'serial', 100, {1: 80, 3: 35, 4: 55}, None, 'Thinned', 'a high-pass at about 500 Hz')
fx('flt_dark', 'Filter', 'serial', 100, {1: 0, 3: 38, 4: 45}, None, 'Muffled', 'a ladder low-pass at about 1 kHz')
fx('flt_peak', 'Filter', 'serial', 70, {1: 100, 3: 52, 4: 62, 7: 35, 10: 60}, None, 'Resonant Sweep', 'a swept peak at 70 %')
# EQ (p1 low freq, p2 low gain, p3 mid 1 freq, p4 mid 1 gain, p5 mid 1 Q, p6 mid 2 freq, p7 mid 2 gain, p8 mid 2 Q, p9 high freq, p10 high gain)
fx('eq_air', 'EQ', 'serial', 100, {9: 55, 10: 62}, None, 'with Air', 'a high shelf adding air')
fx('eq_warm', 'EQ', 'serial', 100, {1: 58, 2: 62, 10: 40}, None, 'Warmed', 'a low shelf up and the top rolled down')
fx('eq_scoop', 'EQ', 'serial', 100, {3: 50, 4: 36, 5: 45, 2: 58, 10: 58}, None, 'Scooped', 'the middle scooped, lows and highs lifted')
fx('eq_presence', 'EQ', 'serial', 100, {6: 62, 7: 62, 8: 55}, None, 'with Presence', 'a bell lifting the upper middle')
fx('eq_thin', 'EQ', 'serial', 100, {1: 65, 2: 5, 4: 45}, None, 'Thinned Out', 'the low end shelved away')
# Comp (p1 mode, p2 threshold, p3 ratio, p4 knee, p5 attack, p6 release, p7 SC low cut, p8 makeup, p9 character, p10 link)
fx('comp_glue', 'Comp', 'serial', 100, {1: 65, 2: 40, 3: 50, 5: 45, 6: 50, 8: 60}, None, 'Glued', 'a glue compressor')
fx('comp_squash', 'Comp', 'serial', 100, {1: 85, 2: 25, 3: 70, 5: 25, 8: 72}, None, 'Squashed', 'a heavy compressor with makeup')
fx('comp_opto', 'Comp', 'serial', 100, {1: 100, 2: 40, 3: 45, 5: 60}, None, 'Smoothed', 'a slow opto compressor')
# Clipper (p1 drive, p2 ceiling, p3 shape, p4 knee, p5 oversampling, p6 bands, p7 crossover 1, p8 crossover 2, p9 stereo, p10 output)
fx('clip_soft', 'Clipper', 'serial', 100, {1: 60, 3: 15, 10: 45}, None, 'Saturated', 'a soft clipper')
fx('clip_hard', 'Clipper', 'serial', 100, {1: 72, 3: 0, 10: 40}, None, 'Hard-Clipped', 'a hard clipper')
fx('clip_tanh', 'Clipper', 'serial', 100, {1: 65, 3: 35, 10: 42}, None, 'Driven', 'a tanh clipper')
fx('clip_para', 'Clipper', 'parallel', 100, {1: 75, 3: 35, 6: 75, 10: 40}, -10, 'with Parallel Grit', 'a two-band clipper on the bus')
# Vocoder (p1 modulator, p2 bands, p3 formant, p4 low, p5 high, p6 attack, p7 release, p8 sibilance, p9 speed, p10 output)
fx('voc_robot', 'Vocoder', 'serial', 100, {1: 0, 2: 70, 6: 40, 7: 45}, None, 'Robotic', 'the internal voice vocoded, 22 bands')
fx('voc_deep', 'Vocoder', 'serial', 100, {1: 0, 3: 35, 2: 50, 9: 40}, None, 'Deep Robot', 'the vocoder with the formants five semitones down, slow')
fx('voc_chatter', 'Vocoder', 'serial', 100, {1: 0, 3: 70, 9: 60, 8: 0}, None, 'Chattering', 'the vocoder with raised formants and a fast voice')
fx('voc_choir', 'Vocoder', 'serial', 100, {1: 0, 2: 80, 3: 45, 6: 60, 7: 70, 8: 0}, None, 'Vocoded Choir', 'a slow 26-band vocoder, formants a little low, no sibilance')
fx('voc_bright', 'Vocoder', 'serial', 90, {1: 0, 3: 60, 6: 45, 8: 0, 10: 50}, None, 'Bright Vocoded', 'the vocoder with a little raised formants, 90 %')
fx('voc_wide', 'Vocoder', 'serial', 85, {1: 0, 2: 100, 6: 35, 8: 30}, None, 'Vocoded', 'a 32-band vocoder at 85 %')

# ---- modifiers ----

MODS = []


def mod(key, kind, pre='', post='', desc='', patch='', req=None, state=None):
    MODS.append(dict(key=key, kind=kind, pre=pre, post=post, desc=desc, patch=patch, req=req, state=state))


mod('dark', 'tone', 'Dark', desc='darker', patch='F1 cutoff *= 0.55')
mod('bright', 'tone', 'Bright', desc='brighter', patch='F1 cutoff *= 1.8;F1 reso += 4')
mod('warm', 'tone', 'Warm', desc='warmed by a 12 dB ladder with a little drive', patch='F1 type=Ladder 12;F1 cutoff *= 0.8;F1 drive=4')
mod('sharp', 'tone', 'Sharp', desc='a sharper filter envelope with more resonance', patch='F1 reso += 22;F1 env += 0.8;Flt decay *= 0.7')
mod('gritty', 'tone', 'Gritty', desc='driven in the filter', patch='F1 drive=10;F1 reso += 8')
mod('soft', 'time', 'Soft', desc='a slower, softer attack', patch='Amp attack >= 0.12;Flt attack >= 0.12;F1 cutoff *= 0.8')
mod('tight', 'time', 'Tight', desc='shorter decay and release', patch='Amp decay *= 0.5;Amp release *= 0.5;Flt decay *= 0.5')
mod('long', 'time', 'Long', desc='a longer decay and release', patch='Amp release *= 2.5;Amp decay *= 1.8')
mod('slow', 'time', 'Slow', desc='a slow attack', patch='Amp attack >= 0.35;Flt attack >= 0.35')
mod('snappy', 'time', 'Snappy', desc='a fast attack and a quick filter snap', patch='Amp attack <= 0.004;F1 env += 0.8;Flt decay *= 0.6;F1 vel=2')
mod('open', 'tone', 'Open', desc='the filter opened and following the keys', patch='F1 cutoff *= 1.4;F1 key track=80')
mod('muted', 'tone', 'Muted', desc='the filter closed down, a shorter release', patch='F1 cutoff *= 0.4;Amp release *= 0.6')
mod('sub', 'layer', 'Sub-Boosted', desc='a sine sub an octave down', patch='Sub level=40;Sub shape=Sine;Sub octave=-1')
mod('octup', 'layer', 'Octave-Doubled', desc='a second slot an octave up',
    patch='B engine=Wavetable;B table=Basic;B position=66.7;B octave=1;B level=28;B unison=2;B detune=10',
    req=lambda p: p.get('B engine', 'Off') == 'Off')
mod('fifth', 'layer', 'Fifth-Stacked', desc='a quiet analog saw a fifth up',
    patch='B engine=Analog;B shape=Saw;B semi=7;B level=22;B unison=2;B detune=10', req=lambda p: p.get('B engine', 'Off') == 'Off')
mod('pulse_under', 'layer', 'Pulse-Backed', desc='a narrow pulse an octave down underneath',
    patch='B engine=Analog;B shape=Pulse;B pulse width=30;B octave=-1;B level=35', req=lambda p: p.get('B engine', 'Off') == 'Off')
mod('breath', 'layer', 'Breathy', desc='a little coloured noise on top', patch='Noise level=6;Noise color=70')
mod('airy', 'layer', 'Airy', desc='a trace of white noise', patch='Noise level=3;Noise color=100')
mod('wider', 'layer', 'Wider', desc='more unison voices spread wider', patch='A unison >= 5;A detune += 6;A spread=90')
mod('mono', 'layer', 'Mono', desc='monophonic with legato glide', patch='Voice mode=Mono;Glide mode=Legato;Glide=0.05')
mod('glide', 'layer', 'Gliding', desc='glide on every note', patch='Glide mode=Always;Glide=0.12')
mod('vibrato', 'motion', 'Vibrato', desc='a vibrato fading in',
    patch='LFO{L} shape=Sine;LFO{L} rate=5.4;LFO{L} fade=0.5;R{R} source=LFO {L};R{R} dest=Pitch;R{R} amount=1.2')
mod('sweep', 'motion', 'Sweeping', desc='a slow free LFO on the cutoff',
    patch='LFO{L} shape=Triangle;LFO{L} rate=0.17;LFO{L} retrig=Off;R{R} source=LFO {L};R{R} dest=F1 cutoff;R{R} amount=22')
mod('morph', 'motion', 'Morphing', desc='a slow LFO moving the position',
    patch='LFO{L} shape=Smooth;LFO{L} rate=0.28;LFO{L} retrig=Off;R{R} source=LFO {L};R{R} dest=A position;R{R} amount=30',
    req=lambda p: p.get('A engine', 'Wavetable') in ('Wavetable', 'Harmonic', 'Modal', 'Granular', 'Sample'))
mod('wheel', 'motion', 'Wheel-Opened', desc='the mod wheel opening the filter', patch='R{R} source=Mod wheel;R{R} dest=F1 cutoff;R{R} amount=35')
mod('tremolo', 'motion', 'Tremolo', desc='a tremolo on the level',
    patch='LFO{L} shape=Sine;LFO{L} rate=5.5;R{R} source=LFO {L};R{R} dest=Amp;R{R} amount=18')
mod('stepped', 'motion', 'Stepped', desc='a stepped random filter in eighths',
    patch='LFO{L} shape=S&H;LFO{L} sync=1/8;LFO{L} retrig=Off;R{R} source=LFO {L};R{R} dest=F1 cutoff;R{R} amount=30')
mod('evolving', 'motion', 'Evolving', desc='a slow envelope moving the position',
    patch='Env{E} attack=1.2;Env{E} decay=2.5;Env{E} sustain=30;R{R} source=Mod env {E};R{R} dest=A position;R{R} amount=35',
    req=lambda p: p.get('A engine', 'Wavetable') in ('Wavetable', 'Harmonic', 'Modal', 'Granular', 'Sample'))
mod('velfilter', 'motion', 'Dynamic', desc='velocity opening the filter and the level',
    patch='R{R} source=Velocity;R{R} dest=F1 cutoff;R{R} amount=25;R{R2} source=Velocity;R{R2} dest=Amp;R{R2} amount=25')
mod('pw_motion', 'motion', 'Shimmering', desc='a slow LFO fanning the pitch of the layers a little',
    patch='LFO{L} shape=Sine;LFO{L} rate=0.4;LFO{L} retrig=Off;R{R} source=LFO {L};R{R} dest=Pitch;R{R} amount=0.4')

# ---- bases ----

_BASES = []


def base(cat, key, noun, desc, patch, n=2, fxs=(), duo=0.35, no=(), state=None, volume=-10, only=None):
    if cat in ('Arp', 'Seq'):
        no = tuple(no) + ('slow', 'soft')       # a step is shorter than a slow attack
    _BASES.append(dict(cat=cat, key=f"{cat}.{key}", noun=noun, desc=desc, patch=patch, n=n, fx=tuple(fxs), duo=duo, no=tuple(no),
                       state=state, volume=volume, only=only))


# Hits and one-shots take only the tone and time modifiers: no layers, motion or glide on a snare.
PERC_ONLY = ('dark', 'bright', 'tight', 'long', 'gritty', 'muted', 'sharp')


_GEN_FX_PADS = ('v_hall', 'v_cathedral', 'c_hall', 'c_cathedral', 'v_shimmer', 'dim_wide', 'pd_shimmer', 'd_long', 'flt_sweep', 'shift_barber',
                'dim_four', 'c_dark', 'eq_air', 'comp_opto', 'flt_notch', 'flt_peak', 'shift_stereo')
_GEN_FX_BASS = ('clip_soft', 'clip_tanh', 'clip_hard', 'comp_glue', 'comp_squash', 'eq_scoop', 'flt_sweep', 'clip_para', 'dim_wide', 'eq_warm',
                'v_room_s', 'crush_hard')
_GEN_FX_LEAD = ('d_dotted', 'd_pingpong', 'v_plate', 'v_hall', 'd_quarter', 'dim_wide', 'pd_fifth', 'pd_shimmer', 'c_plate', 'shift_stereo', 'eq_presence',
                'comp_glue', 'd_dotted_s', 'dim_chorus', 'flt_notch', 'clip_soft')
_GEN_FX_KEYS = ('v_room', 'c_room', 'd_dotted', 'dim_chorus', 'c_plate', 'v_plate', 'eq_warm', 'comp_opto', 'crush_dust', 'flt_notch', 'c_room_s',
                'v_hall_s', 'dim_wide', 'shift_barber', 'flt_peak', 'clip_soft', 'flt_hp', 'shift_barber_s', 'pd_double')
_GEN_FX_PLUCK = ('d_dotted', 'd_pingpong', 'v_plate', 'v_room', 'pd_double', 'pd_fifth', 'eq_air', 'c_chamber', 'd_slap', 'dim_chorus', 'comp_glue', 'flt_sweep', 'clip_soft')
_GEN_FX_BELL = ('v_hall', 'c_hall', 'v_shimmer', 'pd_shimmer', 'c_cathedral', 'd_long', 'v_cathedral', 'c_reverse', 'eq_air', 'dim_four', 'shift_ring')

# BASS
base('Bass', 'saw_sub', 'Saw Sub', 'a mono analog saw with a sine sub an octave down, through a 24 dB ladder',
     AN('A', 'Saw', 1, 0, 0, 0, rnd=0) + ';Sub level=35;Sub shape=Sine;Sub octave=-1;' + MONO + ';' + FLT(900, 25, 1.6, 'Ladder 24', 0.3, 10) + ';' + ENV(0.003, 0.4, 100, 0.12),
     n=3, fxs=_GEN_FX_BASS, no=('mono', 'glide', 'sub'))
base('Bass', 'pulse', 'Narrow Pulse Bass', 'a 22 % pulse an octave down, mono, a snapping ladder filter',
     AN('A', 'Pulse', 1, 0, 0, -1, pw=22, drift=10, rnd=0) + ';' + MONO + ';' + FLT(700, 35, 2.2, 'Ladder 24', 0.25, 8) + ';' + ENV(0.003, 0.3, 100, 0.1),
     n=3, fxs=_GEN_FX_BASS, no=('mono', 'glide'))
base('Bass', 'resonant_table', 'Resonant Table Bass', 'the Resonant table, bent, an octave down, with a filter envelope',
     WT('A', 'Resonant', 35, 1, 0, 0, -1, w1='Asym', w1a=40, rnd=0) + ';' + FLT(2400, 12, 1.6, 'Ladder 24', 0.3, 30) + ';' + ENV(0.002, 0.5, 100, 0.15) + ';' + MONO,
     n=2, fxs=_GEN_FX_BASS, no=('mono',))
base('Bass', 'triple', 'Detuned Triple Bass', 'three saws, two of them an octave down, drifting slightly, with a sub',
     AN('A', 'Saw', 3, 16, 60, 0) + ';' + AN('B', 'Saw', 3, 12, 50, -1, lvl=80) + ';Sub level=30;Sub shape=Triangle;Sub octave=-1;' + FLT(650, 25, 2.0, 'Ladder 24', 0.4, 15) + ';' + ENV(0.004, 0.5, 100, 0.2),
     n=3, fxs=_GEN_FX_BASS, no=('octup', 'fifth', 'pulse_under', 'wider'), duo=0.5)
base('Bass', 'reed', 'Reed Bass', 'the Reed partials morphing to Brass, an octave down, a 24 dB low-pass',
     HM('A', 'Reed', 'Brass', 20, 40, -3, -30, 0, 10, 1, 6, 50, -1) + ';' + FLT(1500, 20, 1.2, 'LP 24', 0.3, 40) + ';' + ENV(0.005, 0.5, 100, 0.15) + ';' + MONO,
     n=2, fxs=_GEN_FX_BASS, no=('mono',))
base('Bass', 'tube_boom', 'Tube Boom', 'a struck tube an octave down, long and round',
     MD('A', 'Tube', 'Struck', 70, 30, 16, 50, 1.4, 40, 60, 1, 4, 40, -1) + ';' + FLT(3000, 10, 0.5) + ';' + ENV(0.002, 0.8, 0, 0.5) + ';F1 key track=100',
     n=2, fxs=_GEN_FX_BASS)
base('Bass', 'pluck_sample', 'Pluck Sample Bass', 'the plucked sample played an octave down, short',
     SM('A', 'Pluck', 'Off', 1, 0, 0, 0, fine=-5) + ';' + SM('B', 'Pluck', 'Off', 1, 0, 0, 0, fine=5) + ';' + FLT(2200, 15, 0.6) + ';' + ENV(0.002, 0.5, 100, 0.2) + ';' + MONO,
     n=2, fxs=_GEN_FX_BASS, no=('octup', 'fifth', 'pulse_under', 'mono'))
base('Bass', 'sine_sub', 'Pure Sine Sub', 'a sine an octave down with a triangle sub underneath, nothing else',
     WT('A', 'Basic', 0, 1, 0, 0, -1, rnd=0) + ';Sub level=45;Sub shape=Triangle;Sub octave=-1;' + FLT(1500, 5, 0) + ';' + ENV(0.004, 0.3, 100, 0.2) + ';' + MONO,
     n=2, fxs=_GEN_FX_BASS, no=('mono', 'sub', 'dark', 'muted', 'sharp', 'gritty', 'bright', 'open'))
base('Bass', 'grit', 'Digital Grit Bass', 'the Grit table mirrored, two voices, a driven ladder',
     WT('A', 'Grit', 50, 2, 20, 50, -1, w1='Mirror', w1a=30, rnd=0) + ';' + FLT(1000, 30, 1.5, 'Ladder 24', 0.3, 20, drive=6) + ';' + ENV(0.003, 0.4, 100, 0.15),
     n=2, fxs=_GEN_FX_BASS, no=('gritty',))
base('Bass', 'fm', 'FM Sine Bass', 'a sine frequency-modulated by a silent sine an octave up, short and hollow',
     WT('A', 'Basic', 0, 1, 0, 0, -1, w1='FM', w1a=55, mod='Osc B', rnd=0) + ';' + WT('B', 'Basic', 0, 1, 0, 0, 1, lvl=0) + ';' + FLT(2500, 10, 0.8) + ';' + ENV(0.002, 0.45, 70, 0.15) + ';' + MONO,
     n=2, fxs=_GEN_FX_BASS, no=('octup', 'fifth', 'pulse_under', 'mono'))

# LEAD
base('Lead', 'saw_unison', 'Detuned Saw Lead', 'seven drifting saws and a quieter pulse an octave up',
     AN('A', 'Saw', 7, 20, 85) + ';' + AN('B', 'Pulse', 3, 14, 70, 1, lvl=25, pw=35) + ';' + FLT(5500, 18, 1.2, 'LP 24', 0.5, 40) + ';' + ENV(0.006, 0.5, 85, 0.3),
     n=3, fxs=_GEN_FX_LEAD, no=('octup', 'fifth', 'pulse_under', 'wider'), duo=0.5)
base('Lead', 'square', 'Hollow Square Lead', 'a square through a gentle low-pass',
     AN('A', 'Square', 1, 0, 0) + ';' + FLT(5000, 15, 0.8) + ';' + ENV(0.005, 0.4, 90, 0.25),
     n=2, fxs=_GEN_FX_LEAD, no=('vibrato',))
base('Lead', 'sync', 'Sync Sweep Lead', 'the Sync sweep table, a mod envelope sweeping its position',
     WT('A', 'Sync sweep', 30, 2, 10, 60, 0) + ';' + FLT(7000, 12, 0.8) + ';' + ENV(0.005, 0.4, 85, 0.25) + ';Env1 attack=0.02;Env1 decay=0.9;Env1 sustain=40;R1 source=Mod env 1;R1 dest=A position;R1 amount=45',
     n=3, fxs=_GEN_FX_LEAD, no=('evolving',))
base('Lead', 'pm', 'Phase Bell Lead', 'a sine phase-modulated by a sine an octave up, a bright singing line',
     WT('A', 'Basic', 0, 2, 6, 50, 0, w1='PM', w1a=30, mod='Osc B') + ';' + WT('B', 'Basic', 0, 1, 0, 0, 1, lvl=0) + ';' + FLT(8000, 10, 0.6) + ';' + ENV(0.004, 0.5, 85, 0.3),
     n=2, fxs=_GEN_FX_LEAD, no=('octup', 'fifth', 'pulse_under'))
base('Lead', 'reed', 'Reed Section Lead', 'the Reed and Brass partials, the position morphing under the wheel',
     HM('A', 'Reed', 'Brass', 20, 48, -1, 0, 0, 0, 2, 8, 60) + ';' + FLT(6000, 12, 0.8) + ';' + ENV(0.02, 0.4, 90, 0.25) + ';R15 source=Mod wheel;R15 dest=A position;R15 amount=60',
     n=2, fxs=_GEN_FX_LEAD)
base('Lead', 'breath_tube', 'Breath Tube Lead', 'a continuously excited tube, a breathy flute-like line',
     MD('A', 'Tube', 'Continuous', 40, 35, 12, 60, 2.0, 40, 40, 1, 4, 40) + ';' + FLT(7000, 10, 0.5) + ';' + ENV(0.06, 0.4, 100, 0.2) + ';Noise level=3;Noise color=60',
     n=2, fxs=_GEN_FX_LEAD, no=('breath', 'airy'))
base('Lead', 'vowel', 'Vowel Lead', 'the Formants table in two voices, a singing mid-range lead',
     WT('A', 'Formants', 40, 2, 8, 50, 0) + ';' + FLT(6500, 12, 0.6) + ';' + ENV(0.015, 0.4, 90, 0.3),
     n=2, fxs=_GEN_FX_LEAD)
base('Lead', 'mirror', 'Mirrored Digital Lead', 'the Digital table mirrored and squeezed, bright and metallic',
     WT('A', 'Digital', 55, 3, 12, 70, 0, w1='Mirror', w1a=35, w2='Squeeze', w2a=20) + ';' + FLT(7500, 15, 0.8) + ';' + ENV(0.005, 0.4, 85, 0.25),
     n=2, fxs=_GEN_FX_LEAD)
base('Lead', 'three_slot', 'Three-Oscillator Lead', 'a saw, a square an octave down and a pulse an octave up, tuned a fifth in the middle',
     AN('A', 'Saw', 3, 14, 70) + ';' + AN('B', 'Square', 2, 8, 50, -1, lvl=70) + ';' + AN('C', 'Pulse', 2, 10, 60, 1, lvl=40, pw=28, semi=7) + ';' + FLT(4500, 20, 1.2, 'LP 24', 0.4, 35) + ';' + ENV(0.006, 0.5, 85, 0.3),
     n=2, fxs=_GEN_FX_LEAD, duo=0.0, no=('octup', 'fifth', 'pulse_under'))
base('Lead', 'glide', 'Solo Glide Lead', 'one analog saw and a sub, mono with a glide on every note',
     AN('A', 'Saw', 1, 0, 0, 0) + ';Sub level=25;Sub shape=Sine;Sub octave=-1;Voice mode=Mono;Glide mode=Always;Glide=0.09;' + FLT(4200, 25, 1.4, 'Ladder 24', 0.35, 30) + ';' + ENV(0.004, 0.4, 95, 0.2),
     n=3, fxs=_GEN_FX_LEAD, no=('mono', 'glide', 'sub'))

# PAD
base('Pad', 'warm_saw', 'Warm Saw Pad', 'six drifting saws and a pulse an octave up through a 12 dB low-pass',
     AN('A', 'Saw', 6, 20, 90, 0, drift=45) + ';' + AN('B', 'Pulse', 2, 12, 80, 1, lvl=30, pw=30) + ';' + FLT(3200, 14, 0.8, 'LP 12', 1.5, 40) + ';' + ENV(0.4, 1.5, 85, 1.4),
     n=3, fxs=_GEN_FX_PADS, no=('octup', 'fifth', 'pulse_under', 'wider', 'slow', 'snappy', 'tight', 'muted'), duo=0.5)
base('Pad', 'glass', 'Glass Harmonic Pad', 'the Harmonics table in four voices with the Digital table an octave up',
     WT('A', 'Harmonics', 50, 4, 14, 90, 0) + ';' + WT('B', 'Digital', 40, 2, 10, 80, 1, lvl=25) + ';' + FLT(4800, 12, 0.6) + ';' + ENV(0.45, 1.8, 90, 1.6),
     n=2, fxs=_GEN_FX_PADS, no=('octup', 'fifth', 'pulse_under', 'wider', 'slow', 'snappy', 'tight'), duo=0.5)
base('Pad', 'strings_sample', 'Looped Strings Pad', 'the bowed strings sample looped in three voices',
     SM('A', 'Strings', 'Forward', 3, 10, 85, fade=80) + ';' + FLT(6000, 10, 0.4) + ';' + ENV(0.4, 1.2, 90, 1.3),
     n=2, fxs=_GEN_FX_PADS, no=('slow', 'snappy', 'tight', 'muted'))
base('Pad', 'choir_cloud', 'Choir Cloud Pad', 'a cloud of grains of the choir sample, scanning slowly',
     GR('A', 'Choir', 40, 0.18, 30, 20, 0.4, 'Gauss', 2, 10, 85) + ';' + FLT(6500, 10, 0.4) + ';' + ENV(0.4, 1.0, 100, 1.5),
     n=2, fxs=_GEN_FX_PADS, no=('slow', 'snappy', 'tight', 'muted'))
base('Pad', 'organ_swell', 'Organ Swell Pad', 'the Organ partials morphing to Strings, four voices',
     HM('A', 'Organ', 'Strings', 40, 48, -1, 0, 0, 0, 4, 12, 85) + ';' + FLT(6000, 10, 0.4) + ';' + ENV(0.4, 1.5, 100, 1.2),
     n=2, fxs=_GEN_FX_PADS, no=('slow', 'snappy', 'tight', 'muted'))
base('Pad', 'formant_motion', 'Moving Formant Pad', 'the Formants table in four voices, a slow LFO moving the position',
     WT('A', 'Formants', 45, 4, 14, 90, 0) + ';' + FLT(3500, 12, 0.5) + ';' + ENV(0.45, 1.5, 90, 1.5) + ';LFO1 shape=Smooth;LFO1 rate=0.18;LFO1 retrig=Off;R1 source=LFO 1;R1 dest=A position;R1 amount=30',
     n=2, fxs=_GEN_FX_PADS, no=('morph', 'slow', 'snappy', 'tight', 'muted'))
base('Pad', 'bowed_plate', 'Bowed Plate Pad', 'a continuously bowed metal plate in three voices, slowly ringing',
     MD('A', 'Plate', 'Continuous', 45, 30, 24, 60, 4.0, 40, 55, 3, 8, 80) + ';' + FLT(6500, 10, 0.4) + ';' + ENV(0.35, 1.5, 100, 1.6),
     n=2, fxs=_GEN_FX_PADS, no=('slow', 'snappy', 'tight', 'muted'))
base('Pad', 'pwm', 'Pulse Width Pad', 'two pulse waves at different widths, four voices each',
     AN('A', 'Pulse', 4, 12, 90, 0, pw=40, drift=40) + ';' + AN('B', 'Pulse', 4, 14, 90, 0, lvl=80, pw=22, drift=40) + ';' + FLT(3600, 12, 0.7, 'LP 12', 1.4, 45) + ';' + ENV(0.4, 1.4, 90, 1.3),
     n=2, fxs=_GEN_FX_PADS, no=('octup', 'fifth', 'pulse_under', 'wider', 'slow', 'snappy', 'tight', 'muted'), duo=0.5)
base('Pad', 'squeezed', 'Squeezed Digital Pad', 'the Digital table squeezed in three voices, slow and bright',
     WT('A', 'Digital', 60, 3, 12, 85, 0, w1='Squeeze', w1a=35) + ';' + FLT(4500, 12, 0.5) + ';' + ENV(0.4, 1.6, 90, 1.4),
     n=1, fxs=_GEN_FX_PADS)

# KEYS
base('Keys', 'percussive_organ', 'Percussive Organ', 'the Organ partials, a decaying upper end, a quick click on top',
     HM('A', 'Organ', 'Organ', 0, 32, 0, 0, 0, 20, 1, 6, 40) + ';' + HM('B', 'Bright', 'Bright', 0, 12, -3, 0, 0, 80, 1, 0, 0, 1, lvl=25) + ';' + FLT(8000, 8, 0.3) + ';' + ENV(0.003, 0.3, 100, 0.08),
     n=3, fxs=_GEN_FX_KEYS, no=('octup', 'fifth', 'pulse_under'))
base('Keys', 'tine', 'Tine Keys', 'a struck bar with a sine an octave up for the attack, a soft electric-piano feel',
     MD('A', 'Bar', 'Struck', 55, 25, 6, 40, 1.6, 40, 40, 1, 4, 40) + ';' + WT('B', 'Basic', 0, 1, 0, 0, 1, lvl=18) + ';' + FLT(7000, 8, 0.8) + ';' + ENV(0.002, 1.2, 0, 0.35),
     n=3, fxs=_GEN_FX_KEYS, no=('octup', 'fifth', 'pulse_under'))
base('Keys', 'felt', 'Felt Piano', 'saw partials with the top damped and slightly stretched, a decaying hammer tone',
     HM('A', 'Saw', 'Triangle', 15, 48, -6, 0, 4, 60, 2, 6, 40) + ';' + FLT(5000, 8, 1.2, 'LP 12', 0.5, 15) + ';' + ENV(0.002, 1.8, 0, 0.35) + ';F1 vel=1.5;F1 key track=70',
     n=3, fxs=_GEN_FX_KEYS)
base('Keys', 'reed', 'Reed Organ Keys', 'the Reed partials with a slow tremolo from LFO 1',
     HM('A', 'Reed', 'Reed', 0, 32, -2, 30, 0, 0, 2, 8, 50) + ';' + FLT(5500, 10, 0.4) + ';' + ENV(0.01, 0.3, 100, 0.2) + ';LFO1 shape=Sine;LFO1 rate=5;R1 source=LFO 1;R1 dest=Amp;R1 amount=12',
     n=2, fxs=_GEN_FX_KEYS, no=('tremolo',))
base('Keys', 'square', 'Hollow Square Keys', 'two squares, one an octave up, a short plucked decay',
     AN('A', 'Square', 2, 6, 50) + ';' + AN('B', 'Square', 1, 0, 0, 1, lvl=30) + ';' + FLT(3500, 12, 1.4, 'LP 12', 0.35, 10) + ';' + ENV(0.003, 0.6, 20, 0.25),
     n=2, fxs=_GEN_FX_KEYS, no=('octup', 'fifth', 'pulse_under'))
base('Keys', 'fm_electric', 'FM Electric Keys', 'a sine frequency-modulated two octaves up, a glassy electric piano',
     WT('A', 'Basic', 0, 2, 4, 50, 0, w1='FM', w1a=22, mod='Osc B') + ';' + WT('B', 'Basic', 0, 1, 0, 0, 2, lvl=0) + ';' + FLT(9000, 8, 0.5) + ';' + ENV(0.002, 1.4, 0, 0.35),
     n=3, fxs=_GEN_FX_KEYS, no=('octup', 'fifth', 'pulse_under'))
base('Keys', 'sample_pluck', 'Plucked Sample Keys', 'the plucked sample over a second an octave up',
     SM('A', 'Pluck', 'Off', 2, 6, 50, 0) + ';' + SM('B', 'Pluck', 'Off', 1, 0, 0, 1, lvl=100) + ';' + FLT(6500, 8, 0.6) + ';' + ENV(0.002, 0.9, 100, 0.3),
     n=2, fxs=_GEN_FX_KEYS, no=('octup', 'fifth', 'pulse_under'))
base('Keys', 'vibes', 'Soft Vibes', 'a struck bar with a long ring and a slow tremolo',
     MD('A', 'Bar', 'Struck', 45, 20, 8, 25, 2.8, 50, 50, 1, 4, 50) + ';' + FLT(8000, 8, 0.2) + ';' + ENV(0.002, 2.0, 0, 0.6) + ';LFO1 shape=Sine;LFO1 rate=4.5;R1 source=LFO 1;R1 dest=Amp;R1 amount=22',
     n=2, fxs=_GEN_FX_KEYS, no=('tremolo',))
base('Keys', 'stab', 'Chord Stab Keys', 'three detuned saws, a short stab with a snapping filter',
     AN('A', 'Saw', 3, 14, 80) + ';' + FLT(3200, 22, 2.0, 'LP 24', 0.3, 10) + ';' + ENV(0.002, 0.35, 0, 0.2),
     n=2, fxs=_GEN_FX_KEYS)

# PLUCK
base('Pluck', 'saw', 'Saw Pluck', 'two detuned saws, a fast filter envelope and no sustain',
     AN('A', 'Saw', 2, 10, 60, 0, rnd=0) + ';' + FLT(2400, 20, 2.2, 'LP 24', 0.22, 0) + ';' + ENV(0.002, 0.45, 0, 0.18),
     n=3, fxs=_GEN_FX_PLUCK)
base('Pluck', 'pulse', 'Pulse Pluck', 'a narrow pulse with a short decay, nasal and tight',
     AN('A', 'Pulse', 1, 0, 0, 0, pw=25, rnd=0) + ';' + FLT(2800, 25, 2.0, 'LP 12', 0.25, 0) + ';' + ENV(0.002, 0.4, 0, 0.15),
     n=3, fxs=_GEN_FX_PLUCK)
base('Pluck', 'string', 'Nylon-Style Pluck', 'a plucked string with the upper modes dying fast',
     MD('A', 'String', 'Plucked', 55, 30, 20, 40, 1.8, 55, 40, 1, 6, 50) + ';' + FLT(8000, 8, 0.2) + ';' + ENV(0.002, 1.2, 0, 0.3),
     n=3, fxs=_GEN_FX_PLUCK)
base('Pluck', 'sample_two', 'Double Sample Pluck', 'the plucked sample twice, a few cents apart',
     SM('A', 'Pluck', 'Off', 1, 0, 0, 0, fine=-6) + ';' + SM('B', 'Pluck', 'Off', 1, 0, 0, 0, fine=6, lvl=90) + ';' + FLT(7000, 8, 0.4) + ';' + ENV(0.002, 0.9, 100, 0.25),
     n=2, fxs=_GEN_FX_PLUCK, no=('octup', 'fifth', 'pulse_under'))
base('Pluck', 'harmonics_table', 'Harmonic Table Pluck', 'the Harmonics table with a quick filter snap',
     WT('A', 'Harmonics', 50, 2, 8, 60, 0, rnd=0) + ';' + FLT(3200, 18, 2.0, 'LP 24', 0.25, 0) + ';' + ENV(0.002, 0.55, 0, 0.2),
     n=3, fxs=_GEN_FX_PLUCK)
base('Pluck', 'wire', 'Damped Wire Pluck', 'saw partials that die from the top, a short woody tone',
     HM('A', 'Saw', 'Saw', 0, 80, -2, 0, 0, 75, 1, 6, 40) + ';' + FLT(9000, 8, 0.4) + ';' + ENV(0.002, 0.5, 0, 0.2),
     n=3, fxs=_GEN_FX_PLUCK)
base('Pluck', 'kalimba', 'Thumb Piano Pluck', 'a struck bar with a high mode count and a very short ring',
     MD('A', 'Bar', 'Struck', 60, 20, 10, 35, 0.9, 60, 30, 1, 4, 40, 1) + ';' + FLT(9000, 8, 0.2) + ';' + ENV(0.001, 0.6, 0, 0.2),
     n=3, fxs=_GEN_FX_PLUCK)

# BELL
base('Bell', 'glass', 'Glass Bell', 'bright partials, stretched and quickly damped at the top',
     HM('A', 'Bright', 'Bright', 0, 48, -3, 0, 35, 45, 1, 6, 50) + ';' + FLT(12000, 8, 0.2) + ';' + ENV(0.001, 2.5, 0, 1.2),
     n=3, fxs=_GEN_FX_BELL)
base('Bell', 'bronze', 'Bronze Bell', 'a bell body struck hard, a long ring',
     MD('A', 'Bell', 'Struck', 75, 25, 24, 50, 7.0, 40, 50, 1, 6, 60) + ';' + FLT(12000, 8, 0.2) + ';' + ENV(0.001, 3.0, 0, 2.0),
     n=3, fxs=_GEN_FX_BELL)
base('Bell', 'sample', 'Struck Sample Bell', 'the bell sample played once in two voices',
     SM('A', 'Bell', 'Off', 2, 5, 60, 0) + ';' + FLT(12000, 6, 0.2) + ';' + ENV(0.001, 3.0, 100, 1.2),
     n=3, fxs=_GEN_FX_BELL)
base('Bell', 'chime', 'Tube Chime', 'a struck tube ringing high and clean',
     MD('A', 'Tube', 'Struck', 65, 20, 12, 30, 3.5, 45, 25, 1, 4, 50) + ';' + FLT(14000, 6, 0.2) + ';' + ENV(0.001, 2.0, 0, 1.4),
     n=2, fxs=_GEN_FX_BELL)
base('Bell', 'grains', 'Grain Bell Shower', 'sparse grains of the bell sample, pitch scattered, a glittering tail',
     GR('A', 'Bell', 15, 0.12, 14, 40, 0.3, 'Percussive', 1, 6, 80, pitch=300, rev=15) + ';' + FLT(11000, 6, 0.2) + ';' + ENV(0.01, 1.0, 80, 1.4),
     n=2, fxs=_GEN_FX_BELL)
base('Bell', 'fm', 'FM Bell', 'a sine frequency-modulated by a second sine a little over an octave and a half up, a clangorous bell',
     WT('A', 'Basic', 0, 1, 0, 0, 0, w1='FM', w1a=45, mod='Osc B') + ';' + WT('B', 'Basic', 0, 1, 0, 0, 1, lvl=0, semi=7) + ';' + FLT(12000, 6, 0.2) + ';' + ENV(0.001, 2.2, 0, 1.4),
     n=3, fxs=_GEN_FX_BELL, no=('octup', 'fifth', 'pulse_under'))
base('Bell', 'celesta', 'Celesta Bell', 'a struck bar and a sine two octaves up, soft and sweet',
     MD('A', 'Bar', 'Struck', 40, 25, 6, 30, 1.4, 55, 30, 1, 4, 40) + ';' + WT('B', 'Basic', 0, 1, 0, 0, 2, lvl=20) + ';' + FLT(12000, 6, 0.2) + ';' + ENV(0.001, 1.6, 0, 1.0),
     n=2, fxs=_GEN_FX_BELL, no=('octup', 'fifth', 'pulse_under'))

# ARP
_ARP_FX = ('d_dotted', 'd_pingpong', 'v_plate', 'v_room', 'c_room', 'dim_wide', 'comp_glue', 'flt_sweep', 'pd_fifth', 'eq_air', 'crush_dust', 'clip_soft', 'flt_peak')
base('Arp', 'saw_up', 'Saw Climb', 'detuned saws arpeggiated upward over two octaves in sixteenths',
     AN('A', 'Saw', 3, 14, 70, rnd=0) + ';' + FLT(3000, 22, 1.4, 'LP 24', 0.18, 15) + ';' + ENV(0.002, 0.25, 70, 0.15) + ';Arp mode=Up;Arp rate=1/16;Arp octaves=2;Arp gate=55',
     n=3, fxs=_ARP_FX)
base('Arp', 'pluck_down', 'Falling Pluck Arp', 'a plucked string stepping down in eighths, long gates',
     MD('A', 'String', 'Plucked', 55, 30, 20, 40, 1.2, 55, 40, 1, 6, 50) + ';' + FLT(8000, 8, 0.2) + ';' + ENV(0.002, 0.8, 0, 0.3) + ';Arp mode=Down;Arp rate=1/8;Arp octaves=2;Arp gate=90',
     n=2, fxs=_ARP_FX)
base('Arp', 'bell_random', 'Scattered Bell Arp', 'the bell sample in a seeded random order over three octaves',
     SM('A', 'Bell', 'Off', 1, 0, 0, 0, fine=-4) + ';' + SM('B', 'Bell', 'Off', 1, 0, 0, 0, fine=4) + ';' + FLT(12000, 6, 0.2) + ';' + ENV(0.001, 1.2, 100, 0.8) + ';Arp mode=Random;Arp rate=1/16;Arp octaves=3;Arp gate=60;Arp seed=11',
     n=2, fxs=_ARP_FX)
base('Arp', 'pulse_updown', 'Pulse Bounce Arp', 'a narrow pulse bouncing up and down in swung eighths',
     AN('A', 'Pulse', 2, 8, 60, pw=28, rnd=0) + ';' + FLT(2600, 28, 1.6, 'LP 24', 0.2, 10) + ';' + ENV(0.002, 0.3, 60, 0.15) + ';Arp mode=Up Down;Arp rate=1/8;Arp octaves=2;Arp gate=55;Arp swing=30',
     n=2, fxs=_ARP_FX)
base('Arp', 'pad_latched', 'Latched Pad Arp', 'a soft pad arpeggiated in the order played, latched',
     WT('A', 'Harmonics', 50, 3, 10, 80, 0) + ';' + FLT(4200, 12, 0.8) + ';' + ENV(0.01, 0.8, 70, 0.8) + ';Arp mode=Played;Arp rate=1/8;Arp gate=80;Arp latch=On',
     n=2, fxs=_ARP_FX)
base('Arp', 'organ_chord', 'Organ Chord Arp', 'the Organ partials playing the whole chord in triplet eighths',
     HM('A', 'Organ', 'Organ', 0, 32, 0, 0, 0, 20, 1, 6, 40) + ';' + FLT(7000, 8, 0.3) + ';' + ENV(0.003, 0.3, 80, 0.12) + ';Arp mode=Chord;Arp rate=1/8;Arp feel=Triplet;Arp gate=45',
     n=2, fxs=_ARP_FX)
base('Arp', 'sample_dotted', 'Dotted Sample Arp', 'the plucked sample climbing in dotted sixteenths',
     SM('A', 'Pluck', 'Off', 1, 0, 0, 0, fine=-5) + ';' + SM('B', 'Pluck', 'Off', 1, 0, 0, 0, fine=5) + ';' + FLT(7000, 8, 0.4) + ';' + ENV(0.002, 0.6, 100, 0.25) + ';Arp mode=Up;Arp rate=1/16;Arp feel=Dotted;Arp octaves=3;Arp gate=70',
     n=2, fxs=_ARP_FX)
base('Arp', 'bar_updown', 'Marimba Run', 'a struck bar running up and down in sixteenths',
     MD('A', 'Bar', 'Struck', 55, 25, 8, 30, 1.0, 50, 40, 1, 4, 50) + ';' + FLT(9000, 8, 0.2) + ';' + ENV(0.001, 0.6, 0, 0.3) + ';Arp mode=Up Down;Arp rate=1/16;Arp octaves=2;Arp gate=70',
     n=2, fxs=_ARP_FX)

# SEQ
_SEQ_FX = ('d_dotted', 'd_pingpong', 'v_room', 'c_room', 'dim_chorus', 'comp_squash', 'clip_soft', 'flt_notch', 'eq_scoop', 'crush_hard', 'flt_peak', 'clip_para', 'voc_robot', 'shift_stereo')
base('Seq', 'acid_saw', 'Saw Line', 'a 16-step legato saw line with rests, ties and accents on the key held',
     AN('A', 'Saw', 1, 0, 0, -1) + ';Voice mode=Legato;Glide mode=Legato;Glide=0.05;' + FLT(750, 42, 2.6, 'Ladder 24', 0.22, 5) + ';' + ENV(0.001, 0.4, 100, 0.08) + ';Sub level=25;Seq mode=Notes;Seq length=16;Seq rate=1/16;R15 source=Seq velocity;R15 dest=F1 cutoff;R15 amount=30',
     n=3, fxs=_SEQ_FX, no=('mono', 'glide'), state={'SeqSteps': seq(1, 16, (0, 3, 7, 10), 0.15, 0.12, -12, 12, (70, 120), (45, 60))})
base('Seq', 'pulse_bass', 'Swung Pulse Bass Line', 'an 8-step swung pulse bass line',
     AN('A', 'Pulse', 1, 0, 0, -1, pw=30) + ';' + FLT(900, 30, 1.8, 'Ladder 24', 0.2, 8) + ';' + ENV(0.002, 0.3, 80, 0.1) + ';Seq mode=Notes;Seq length=8;Seq rate=1/8;Seq swing=35',
     n=2, fxs=_SEQ_FX, state={'SeqSteps': seq(2, 8, (0, 0, 7, 5, 12), 0.0, 0.0, -5, 12, (80, 115), (40, 60))})
base('Seq', 'pluck_prob', 'Chance Pluck Line', 'a plucked 16-step pattern, each step with its own probability',
     MD('A', 'String', 'Plucked', 55, 30, 20, 40, 0.9, 55, 40, 1, 6, 50) + ';' + FLT(8000, 8, 0.2) + ';' + ENV(0.002, 0.6, 0, 0.25) + ';Seq mode=Notes;Seq length=16;Seq rate=1/16',
     n=3, fxs=_SEQ_FX, state={'SeqSteps': seq(3, 16, (0, 3, 7, 10, 12), 0.0, 0.0, -5, 12, (65, 110), (45, 55), prob=(100, 90, 70, 50))})
base('Seq', 'mod_steps', 'Stepped Filter Pad', 'the sequencer as modulation: stepped, slewed cutoff and position under held notes',
     WT('A', 'Formants', 40, 3, 10, 80, 0) + ';' + FLT(2200, 18, 0.5) + ';' + ENV(0.2, 1.0, 100, 0.8) + ';Seq mode=Mod;Seq length=8;Seq rate=1/8;Seq slew=60;R1 source=Seq step;R1 dest=F1 cutoff;R1 amount=40;R2 source=Seq velocity;R2 dest=A position;R2 amount=25',
     n=2, fxs=_SEQ_FX, no=('sweep', 'morph', 'evolving'), state={'SeqSteps': seq(4, 8, (-24, -12, 0, 12, 24), 0.0, 0.0, -24, 24, (50, 110), (50, 60))})
base('Seq', 'bell_line', 'Bell Tone Line', 'the bell sample on a sparse 8-step line',
     SM('A', 'Bell', 'Off', 1, 0, 0, 0, fine=-4) + ';' + SM('B', 'Bell', 'Off', 1, 0, 0, 0, fine=4) + ';' + FLT(11000, 6, 0.2) + ';' + ENV(0.001, 1.4, 100, 0.8) + ';Seq mode=Notes;Seq length=8;Seq rate=1/8',
     n=2, fxs=_SEQ_FX, state={'SeqSteps': seq(5, 8, (0, 3, 7, 12), 0.3, 0.0, 0, 12, (70, 110), (50, 60))})
base('Seq', 'choir_chops', 'Choir Chop Line', 'short bursts of the choir sample on a 16-step pattern',
     SM('A', 'Choir', 'Forward', 2, 8, 70) + ';' + FLT(6500, 10, 0.3) + ';' + ENV(0.004, 0.2, 70, 0.1) + ';Seq mode=Notes;Seq length=16;Seq rate=1/16',
     n=2, fxs=_SEQ_FX, state={'SeqSteps': seq(6, 16, (0, 3, 5, 7), 0.25, 0.0, -5, 12, (70, 115), (30, 50))})

# TEXTURE
_TEX_FX = ('v_dark', 'c_dark', 'd_long', 'dim_four', 'flt_sweep', 'flt_notch', 'shift_barber', 'crush_dust', 'eq_air', 'pd_detune', 'v_shimmer', 'shift_ring', 'comp_opto', 'voc_deep', 'voc_wide', 'clip_soft', 'shift_barber_s', 'shift_up')
base('Texture', 'comb_drone', 'Comb Noise Drone', 'coloured noise through a tuned comb that follows the key',
     'A engine=Off;Noise level=70;Noise color=55;F1 type=Comb+;F1 cutoff=261.6;F1 key track=100;F1 reso=90;F1 env=0;F1 vel=0;' + ENV(0.02, 1.6, 60, 1.4),
     n=3, fxs=_TEX_FX, no=('dark', 'bright', 'warm', 'open', 'muted', 'sharp', 'gritty', 'sweep', 'wheel', 'stepped', 'velfilter', 'breath', 'airy', 'sub'))
base('Texture', 'cloud', 'Grain Cloud Texture', 'dense grains of the texture sample, wide and drifting',
     GR('A', 'Texture', 45, 0.15, 40, 30, 0.2, 'Gauss', 2, 10, 90) + ';' + FLT(7000, 10, 0.3) + ';' + ENV(0.3, 1.0, 100, 1.5),
     n=3, fxs=_TEX_FX, no=('slow', 'tight'))
base('Texture', 'partials', 'Drifting Partials', 'random partials morphing to a comb, five wide voices',
     HM('A', 'Random', 'Comb', 40, 48, 0, 0, 12, 0, 5, 16, 90) + ';' + FLT(6000, 12, 0.4) + ';' + ENV(0.3, 1.0, 100, 1.5) + ';LFO1 shape=Smooth;LFO1 rate=0.12;LFO1 retrig=Off;R1 source=LFO 1;R1 dest=A position;R1 amount=40',
     n=2, fxs=_TEX_FX, no=('morph', 'slow', 'tight', 'evolving'))
base('Texture', 'vowel_noise', 'Vowel Noise Wash', 'the Formants table over noise, a whispered chord',
     WT('A', 'Formants', 60, 3, 18, 90, 0) + ';Noise level=20;Noise color=80;' + FLT(4500, 12, 0.3) + ';' + ENV(0.5, 1.0, 100, 1.8),
     n=2, fxs=_TEX_FX, no=('breath', 'airy'))
base('Texture', 'sample_loop', 'Looped Texture Bed', 'the texture sample looped and detuned, a slow evolving bed',
     SM('A', 'Texture', 'Forward', 3, 14, 90, fade=120) + ';' + FLT(5500, 10, 0.3) + ';' + ENV(0.5, 1.0, 100, 1.8),
     n=2, fxs=_TEX_FX, no=('slow', 'tight'))
base('Texture', 'bowed_drone', 'Bowed Bell Drone', 'a continuously bowed bell body, two voices, ringing on',
     MD('A', 'Bell', 'Continuous', 50, 35, 20, 70, 6.0, 45, 60, 2, 6, 80) + ';' + FLT(6000, 10, 0.3) + ';' + ENV(0.5, 1.5, 100, 2.5),
     n=2, fxs=_TEX_FX, no=('slow', 'tight'))
base('Texture', 'digital_noise', 'Digital Grit Wash', 'the Grit table squeezed and mirrored, rough and slow',
     WT('A', 'Grit', 70, 3, 24, 90, 0, w1='Squeeze', w1a=40, w2='Mirror', w2a=30) + ';' + FLT(3500, 18, 0.6) + ';' + ENV(0.4, 1.5, 100, 1.5),
     n=2, fxs=_TEX_FX, no=('slow',))

# FX
_FX_FX = ('d_long', 'c_cathedral', 'v_cathedral', 'c_reverse', 'dim_four', 'flt_peak', 'shift_up', 'pd_octdown', 'crush_hard', 'shift_ring', 'v_dark', 'clip_hard', 'voc_chatter', 'voc_bright', 'flt_sweep')
base('FX', 'riser', 'Noise Riser', 'coloured noise with a long filter rise and a swelling level',
     'A engine=Off;Noise level=70;Noise color=70;F1 type=BP 12;F1 cutoff=300;F1 reso=45;F1 env=4.5;F1 key track=0;Flt attack=3.5;Flt decay=0.5;Flt sustain=100;Amp attack=0.4;Amp decay=0.4;Amp sustain=100;Amp release=0.6;Flt release=0.5',
     n=3, fxs=_FX_FX, only=('long', 'gritty'))
base('FX', 'plate_hit', 'Metal Plate Hit', 'a hard strike on a big plate, a long metallic wash',
     MD('A', 'Plate', 'Struck', 85, 20, 32, 70, 6.0, 30, 80, 1, 4, 60) + ';' + FLT(12000, 6, 0.2) + ';' + ENV(0.001, 3.0, 0, 2.5),
     n=2, fxs=_FX_FX, only=PERC_ONLY)

base('FX', 'laser', 'Falling Laser', 'a bright table falling in pitch from a mod envelope',
     WT('A', 'Sync sweep', 70, 1, 0, 0, 0, rnd=0) + ';' + FLT(9000, 15, 0.5) + ';' + ENV(0.001, 0.5, 0, 0.2) + ';Env1 attack=0.001;Env1 decay=0.35;Env1 sustain=0;R1 source=Mod env 1;R1 dest=Pitch;R1 amount=90',
     n=3, fxs=_FX_FX, only=PERC_ONLY)
base('FX', 'zap', 'Rising Zap', 'a square rising in pitch with a fast decay',
     AN('A', 'Square', 1, 0, 0, 0, rnd=0) + ';' + FLT(6000, 20, 0.5) + ';' + ENV(0.001, 0.3, 0, 0.15) + ';Env1 attack=0.001;Env1 decay=0.25;Env1 sustain=0;R1 source=Mod env 1;R1 dest=Pitch;R1 amount=-80',
     n=2, fxs=_FX_FX, only=PERC_ONLY)
base('FX', 'reverse_swell', 'Reverse Swell', 'the plucked sample backwards, swelling into the note',
     SM('A', 'Pluck', 'Off', 1, 0, 0, 0, fine=-5, rev=True) + ';' + SM('B', 'Pluck', 'Off', 1, 0, 0, 0, fine=5, rev=True) + ';' + FLT(8000, 10, 0.3) + ';' + ENV(0.01, 0.8, 100, 0.4),
     n=2, fxs=_FX_FX)
base('FX', 'wind', 'Wind Pass', 'noise through a moving band-pass, a long breath',
     'A engine=Off;Noise level=75;Noise color=60;F1 type=BP 12;F1 cutoff=900;F1 reso=40;F1 env=0;F1 key track=0;Amp attack=1.2;Amp decay=1.0;Amp sustain=100;Amp release=1.6;LFO1 shape=Smooth;LFO1 rate=0.22;LFO1 retrig=Off;R1 source=LFO 1;R1 dest=F1 cutoff;R1 amount=55',
     n=2, fxs=_FX_FX, only=('long', 'gritty'))
base('FX', 'glitch', 'Grain Glitch', 'tiny grains of the bell sample, scattered and reversed',
     GR('A', 'Bell', 30, 0.03, 60, 80, 0.0, 'Percussive', 1, 10, 100, pitch=700, rev=40) + ';' + FLT(10000, 8, 0.2) + ';' + ENV(0.005, 0.6, 90, 0.5),
     n=2, fxs=_FX_FX, only=PERC_ONLY)
# DRUM
_DR_FX = ('v_room', 'c_room', 'c_room_s', 'v_plate', 'comp_squash', 'clip_hard', 'clip_soft', 'crush_hard', 'eq_scoop', 'd_slap', 'comp_glue', 'c_plate', 'clip_para', 'flt_hp')

base('Drum', 'kick', 'Sine Kick', 'a sine with a fast pitch drop from the mod envelope, a round thump',
     AN('A', 'Sine', 1, 0, 0, -1, rnd=0) + ';' + FLT(8000, 0, 0) + ';' + ENV(0.001, 0.35, 0, 0.1) + ';Env1 attack=0.001;Env1 decay=0.08;Env1 sustain=0;R1 source=Mod env 1;R1 dest=Pitch;R1 amount=100;F1 key track=0',
     n=3, fxs=_DR_FX, only=PERC_ONLY)
base('Drum', 'membrane_kick', 'Skin Kick', 'a struck membrane with a large body, tuned low',
     MD('A', 'Membrane', 'Struck', 80, 15, 12, 50, 0.4, 60, 80, 1, 4, 50, -1) + ';' + FLT(6000, 0, 0) + ';' + ENV(0.001, 0.4, 0, 0.1) + ';F1 key track=0',
     n=2, fxs=_DR_FX, only=PERC_ONLY)
base('Drum', 'snare', 'Noise Snare', 'a short struck membrane with a band-passed noise burst on top',
     MD('A', 'Membrane', 'Struck', 85, 25, 12, 40, 0.18, 70, 30, 1, 4, 50, lvl=70) + ';Noise level=70;Noise color=85;' + FLT(5000, 10, 0.4, 'HP 12') + ';' + ENV(0.001, 0.22, 0, 0.1) + ';F1 key track=0',
     n=3, fxs=_DR_FX, only=PERC_ONLY)
base('Drum', 'hat', 'Closed Hat', 'white noise high-passed with a very short decay',
     'A engine=Off;Noise level=100;Noise color=100;F1 type=HP 12;F1 cutoff=7000;F1 reso=12;F1 env=0;F1 key track=0;Amp attack=0.001;Amp decay=0.08;Amp sustain=0;Amp release=0.05',
     n=3, fxs=_DR_FX, only=PERC_ONLY)
base('Drum', 'open_hat', 'Open Hat', 'white noise with a metallic plate, a longer shimmering decay',
     MD('A', 'Plate', 'Struck', 70, 15, 32, 85, 0.5, 20, 15, 1, 4, 50, lvl=35) + ';Noise level=80;Noise color=100;' + FLT(6500, 12, 0, 'HP 12') + ';' + ENV(0.001, 0.35, 0, 0.3) + ';F1 key track=0',
     n=2, fxs=_DR_FX, only=PERC_ONLY)
base('Drum', 'clap', 'Noise Clap', 'band-passed noise with a snappy decay and a short tail',
     'A engine=Off;Noise level=100;Noise color=70;F1 type=BP 12;F1 cutoff=1500;F1 reso=25;F1 env=0.5;F1 key track=0;Flt decay=0.1;Flt sustain=0;Amp attack=0.001;Amp decay=0.18;Amp sustain=0;Amp release=0.1',
     n=2, fxs=_DR_FX, only=PERC_ONLY)
base('Drum', 'tom', 'Skin Tom', 'a struck membrane, medium body, a short boomy ring',
     MD('A', 'Membrane', 'Struck', 75, 25, 12, 40, 0.5, 55, 55, 1, 4, 50) + ';' + FLT(6000, 0, 0) + ';' + ENV(0.001, 0.5, 0, 0.2) + ';F1 key track=50',
     n=3, fxs=_DR_FX, only=PERC_ONLY)
base('Drum', 'block', 'Wood Block', 'a very short struck bar, high and dry',
     MD('A', 'Bar', 'Struck', 85, 20, 6, 20, 0.12, 80, 20, 1, 4, 40, 2) + ';Noise level=60;Noise color=55;' + FLT(9000, 6, 0) + ';' + ENV(0.001, 0.16, 0, 0.1) + ';F1 key track=50',
     n=2, fxs=_DR_FX, only=PERC_ONLY)
base('Drum', 'cymbal', 'Wash Cymbal', 'a struck plate with noise, long and bright',
     MD('A', 'Plate', 'Struck', 80, 10, 40, 85, 2.5, 25, 20, 1, 4, 50, lvl=55) + ';Noise level=70;Noise color=100;' + FLT(5500, 10, 0, 'HP 12') + ';' + ENV(0.001, 1.8, 0, 1.4) + ';F1 key track=0',
     n=2, fxs=_DR_FX, only=PERC_ONLY)
base('Drum', 'fm_perc', 'FM Metal Perc', 'a short frequency-modulated hit with a fast pitch drop',
     WT('A', 'Basic', 0, 1, 0, 0, 0, w1='FM', w1a=60, mod='Osc B', rnd=0) + ';' + WT('B', 'Basic', 0, 1, 0, 0, 1, lvl=0, semi=5) + ';' + FLT(9000, 10, 0) + ';' + ENV(0.001, 0.2, 0, 0.1) + ';Env1 attack=0.001;Env1 decay=0.1;Env1 sustain=0;R1 source=Mod env 1;R1 dest=Pitch;R1 amount=30',
     n=2, fxs=_DR_FX, only=PERC_ONLY)
base('Drum', 'shaker', 'Soft Shaker', 'noise with a soft attack and a quick fall, filtered to the highs',
     'A engine=Off;Noise level=100;Noise color=90;F1 type=BP 12;F1 cutoff=6500;F1 reso=15;F1 env=0;F1 key track=0;Amp attack=0.012;Amp decay=0.12;Amp sustain=0;Amp release=0.06',
     n=2, fxs=_DR_FX, only=PERC_ONLY)

# VOCAL
_VOC_FX = ('c_hall', 'v_hall', 'v_cathedral', 'c_cathedral', 'v_plate', 'dim_wide', 'd_dotted', 'voc_robot', 'voc_deep', 'voc_chatter', 'voc_wide', 'voc_choir', 'voc_bright', 'eq_air', 'comp_opto', 'pd_detune', 'clip_soft')
base('Vocal', 'choir_sample', 'Sung Choir', 'the choir sample looped in three voices',
     SM('A', 'Choir', 'Forward', 3, 12, 80) + ';' + FLT(7000, 10, 0.3) + ';' + ENV(0.25, 1.0, 90, 1.2),
     n=3, fxs=_VOC_FX, no=('snappy',))
base('Vocal', 'choir_grains', 'Grain Choir', 'a cloud of grains of the choir, scanning at half speed',
     GR('A', 'Choir', 40, 0.2, 30, 20, 0.5, 'Gauss', 2, 10, 80) + ';' + FLT(6500, 10, 0.3) + ';' + ENV(0.3, 1.0, 100, 1.4),
     n=2, fxs=_VOC_FX, no=('snappy',))
base('Vocal', 'formant', 'Formant Voice', 'the Formants table morphing through vowels, two voices',
     WT('A', 'Formants', 30, 2, 8, 60, 0) + ';' + FLT(6500, 10, 0.4) + ';' + ENV(0.08, 0.5, 90, 0.5) + ';LFO1 shape=Smooth;LFO1 rate=0.5;LFO1 retrig=Off;R1 source=LFO 1;R1 dest=A position;R1 amount=40',
     n=3, fxs=_VOC_FX, no=('morph', 'evolving'))
base('Vocal', 'vowel_morph', 'Vowel Morph Choir', 'the Vowel A set morphing to Vowel O, four voices, a slow LFO',
     HM('A', 'Vowel A', 'Vowel O', 40, 48, -1, 0, 0, 0, 4, 12, 85) + ';' + FLT(7000, 10, 0.3) + ';' + ENV(0.3, 1.0, 100, 1.2) + ';LFO1 shape=Smooth;LFO1 rate=0.2;LFO1 retrig=Off;R1 source=LFO 1;R1 dest=A position;R1 amount=45',
     n=3, fxs=_VOC_FX, no=('morph', 'evolving'))
base('Vocal', 'breath', 'Whispered Vowel', 'a vowel table under a layer of breath noise',
     WT('A', 'Formants', 70, 2, 8, 70, 0) + ';Noise level=22;Noise color=85;' + FLT(5500, 14, 0.2) + ';' + ENV(0.15, 0.6, 90, 0.6),
     n=2, fxs=_VOC_FX, no=('breath', 'airy'))
base('Vocal', 'robot_voice', 'Robot Voice', 'a saw chord fed to the vocoder, speaking with the internal voice',
     AN('A', 'Saw', 3, 14, 70) + ';' + FLT(8000, 8, 0) + ';' + ENV(0.01, 0.5, 100, 0.3),
     n=3, fxs=('voc_robot', 'voc_deep', 'voc_chatter', 'voc_wide', 'voc_choir', 'voc_bright'), duo=0.4)
base('Vocal', 'choir_stack', 'Choir Stack', 'the choir sample with a grain cloud of it an octave up',
     SM('A', 'Choir', 'Forward', 2, 10, 80) + ';' + GR('B', 'Choir', 40, 0.18, 25, 20, 0.4, 'Gauss', 2, 8, 80, oct=1, lvl=40) + ';' + FLT(7000, 10, 0.3) + ';' + ENV(0.3, 1.0, 90, 1.3),
     n=2, fxs=_VOC_FX, no=('octup', 'fifth', 'pulse_under'))

# CINEMATIC
_CIN_FX = ('c_cathedral', 'v_cathedral', 'c_hall', 'v_dark', 'c_dark', 'pd_octdown', 'pd_shimmer', 'd_long', 'clip_hard', 'clip_para', 'comp_squash', 'shift_barber', 'dim_four', 'flt_dark', 'shift_stereo')
base('Cinematic', 'drone', 'Low Saw Drone', 'eight saws two octaves down, a slow filter breathing, a held fifth above',
     AN('A', 'Saw', 8, 18, 70, -2, drift=60) + ';' + AN('B', 'Saw', 2, 10, 50, -1, semi=7, lvl=35) + ';' + FLT(500, 18, 0.6, 'LP 24', 2.5, 60) + ';' + ENV(0.4, 2.0, 100, 2.0) + ';LFO1 shape=Triangle;LFO1 rate=0.1;LFO1 retrig=Off;R1 source=LFO 1;R1 dest=F1 cutoff;R1 amount=30',
     n=3, fxs=_CIN_FX, no=('octup', 'fifth', 'pulse_under', 'sweep', 'slow', 'snappy', 'tight', 'bright'), duo=0.5)
base('Cinematic', 'swell', 'Rising Swell', 'strings looped, the filter and level rising over three seconds',
     SM('A', 'Strings', 'Forward', 3, 12, 85, fade=80) + ';' + FLT(500, 12, 3.0, 'LP 24', 3.0, 100) + ';' + ENV(0.45, 0.5, 100, 1.8) + ';Flt attack=2.5;Flt release=1.0',
     n=2, fxs=_CIN_FX, no=('slow', 'snappy', 'tight', 'muted', 'dark'))
base('Cinematic', 'brass_blast', 'Brass Blast', 'the Brass partials in five voices with a slow swell and a hard stop',
     HM('A', 'Brass', 'Brass', 30, 48, -2, 0, 0, 0, 5, 12, 80, -1) + ';' + FLT(2500, 15, 1.5, 'LP 24', 0.6, 60) + ';' + ENV(0.12, 0.8, 90, 0.4) + ';Flt attack=0.15',
     n=3, fxs=_CIN_FX, no=('slow', 'snappy', 'tight'))
base('Cinematic', 'impact', 'Stone Impact', 'a struck bell body an octave down with a long ring, made for a cathedral',
     MD('A', 'Bell', 'Struck', 90, 20, 32, 70, 9.0, 35, 90, 1, 6, 70, -1) + ';' + FLT(8000, 6, 0.2) + ';' + ENV(0.001, 4.0, 0, 3.0),
     n=2, fxs=_CIN_FX, only=PERC_ONLY)

base('Cinematic', 'tension', 'Tension Strings', 'bowed strings with a slow pitch wobble and a rising filter',
     SM('A', 'Strings', 'Forward', 4, 18, 90, fade=100) + ';' + FLT(1500, 14, 1.6, 'LP 24', 2.0, 90) + ';' + ENV(0.5, 0.5, 100, 1.5) + ';Flt attack=1.5' + ';LFO1 shape=Smooth;LFO1 rate=0.35;R1 source=LFO 1;R1 dest=Pitch;R1 amount=1.5',
     n=2, fxs=_CIN_FX, no=('vibrato', 'slow'))
base('Cinematic', 'sub_drop', 'Sub Drop', 'a sine falling an octave and more under a mod envelope, a slow decay',
     AN('A', 'Sine', 1, 0, 0, -1, rnd=0) + ';' + FLT(4000, 0, 0) + ';' + ENV(0.001, 2.2, 0, 1.0) + ';Env1 attack=0.001;Env1 decay=1.4;Env1 sustain=0;R1 source=Mod env 1;R1 dest=Pitch;R1 amount=100;F1 key track=0',
     n=2, fxs=_CIN_FX, only=PERC_ONLY)
base('Cinematic', 'choir_hit', 'Choir Hit', 'the choir sample as a short hit, in a large space',
     SM('A', 'Choir', 'Forward', 3, 12, 85) + ';' + FLT(5500, 10, 0.8, 'LP 12', 0.4, 30) + ';' + ENV(0.02, 0.5, 20, 0.8),
     n=2, fxs=_CIN_FX)
base('Cinematic', 'metal', 'Metal Resonance', 'a bowed plate and a struck bell, inharmonic and cold',
     MD('A', 'Plate', 'Continuous', 55, 25, 32, 80, 6.0, 40, 70, 2, 8, 80) + ';' + MD('B', 'Bell', 'Struck', 70, 25, 24, 70, 5.0, 40, 70, 1, 4, 60, 1, lvl=40) + ';' + FLT(7000, 10, 0.4) + ';' + ENV(0.3, 1.5, 100, 2.0),
     n=2, fxs=_CIN_FX, no=('octup', 'fifth', 'pulse_under', 'slow'))

# LO-FI
_LOFI_FX = ('crush_lofi', 'crush_dust', 'crush_tape', 'crush_flutter', 'v_room_s', 'c_room_s', 'd_dotted_s', 'eq_warm', 'flt_dark', 'comp_glue', 'dim_chorus', 'pd_detune')
base('Lo-Fi', 'dusty_keys', 'Dusty Keys', 'a soft struck bar and an organ, dulled, ready for the crusher',
     MD('A', 'Bar', 'Struck', 40, 25, 6, 35, 1.2, 50, 40, 1, 4, 50) + ';' + HM('B', 'Organ', 'Organ', 0, 16, -3, 0, 0, 30, 1, 0, 0, 0, lvl=40) + ';' + FLT(3200, 8, 0.4) + ';' + ENV(0.004, 1.0, 20, 0.35),
     n=3, fxs=_LOFI_FX, no=('octup', 'fifth', 'pulse_under', 'bright'))
base('Lo-Fi', 'tape_pad', 'Tape Pad', 'looped strings, darkened, drifting like a worn tape',
     SM('A', 'Strings', 'Forward', 2, 18, 70, fade=60) + ';' + FLT(2200, 8, 0.2) + ';' + ENV(0.35, 1.0, 90, 1.1) + ';LFO1 shape=Smooth;LFO1 rate=0.7;R1 source=LFO 1;R1 dest=Pitch;R1 amount=0.5',
     n=3, fxs=_LOFI_FX, no=('vibrato', 'slow', 'bright', 'snappy', 'pw_motion'))
base('Lo-Fi', 'crushed_pluck', 'Crushed Pluck', 'a square pluck with a short decay',
     AN('A', 'Square', 1, 0, 0, 0, rnd=0) + ';' + FLT(3000, 14, 1.4, 'LP 12', 0.22, 0) + ';' + ENV(0.002, 0.4, 0, 0.15),
     n=3, fxs=_LOFI_FX)
base('Lo-Fi', 'dust_noise', 'Vinyl Dust Bed', 'low coloured noise with a slow, narrow filter, a bed for loops',
     'A engine=Off;Noise level=40;Noise color=35;F1 type=BP 12;F1 cutoff=1800;F1 reso=22;F1 env=0;F1 key track=0;Amp attack=0.4;Amp decay=1.0;Amp sustain=100;Amp release=1.0;LFO1 shape=Smooth;LFO1 rate=0.3;LFO1 retrig=Off;R1 source=LFO 1;R1 dest=F1 cutoff;R1 amount=25',
     n=2, fxs=_LOFI_FX, no=('dark', 'bright', 'warm', 'open', 'muted', 'sharp', 'gritty', 'soft', 'slow', 'snappy', 'sweep', 'breath', 'airy', 'sub', 'wheel', 'stepped', 'velfilter', 'tight', 'long'))
base('Lo-Fi', 'chip_lead', 'Chip Lead', 'a pulse with a fast vibrato, narrow and stripped like an old game console',
     AN('A', 'Pulse', 1, 0, 0, 0, pw=25, rnd=0, drift=0) + ';' + FLT(9000, 4, 0) + ';' + ENV(0.002, 0.2, 90, 0.1) + ';LFO1 shape=Sine;LFO1 rate=6.5;LFO1 fade=0.3;R1 source=LFO 1;R1 dest=Pitch;R1 amount=0.8',
     n=2, fxs=_LOFI_FX, no=('vibrato',))
base('Lo-Fi', 'warble_bell', 'Warbling Bell', 'a struck bell body with a slow pitch wobble and a dull top',
     MD('A', 'Bell', 'Struck', 55, 25, 16, 40, 3.0, 55, 40, 1, 6, 60) + ';' + FLT(4500, 8, 0.2) + ';' + ENV(0.002, 1.6, 0, 1.0) + ';LFO1 shape=Smooth;LFO1 rate=0.6;R1 source=LFO 1;R1 dest=Pitch;R1 amount=0.7',
     n=2, fxs=_LOFI_FX, no=('vibrato', 'bright'))
base('Lo-Fi', 'dull_bass', 'Dull Sub Bass', 'a triangle bass with the top taken off, soft and round',
     WT('A', 'Basic', 20, 1, 0, 0, -1, rnd=0) + ';Sub level=30;Sub shape=Sine;Sub octave=-1;' + FLT(900, 6, 0.3) + ';' + ENV(0.006, 0.4, 100, 0.2) + ';' + MONO,
     n=2, fxs=_LOFI_FX, no=('mono', 'sub', 'bright'))
base('Lo-Fi', 'arp', 'Dusty Pluck Arp', 'a plucked string arpeggio, dulled and swung',
     MD('A', 'String', 'Plucked', 50, 30, 16, 40, 0.9, 60, 40, 1, 6, 50) + ';' + FLT(3500, 8, 0.4) + ';' + ENV(0.002, 0.6, 0, 0.25) + ';Arp mode=Up Down;Arp rate=1/8;Arp octaves=2;Arp gate=60;Arp swing=40',
     n=1, fxs=_LOFI_FX)


# ---- The Spectral engine (P5a): the built-in samples resynthesised from their partials and noise.
base('Pad', 'frozen_choir', 'Frozen Choir Pad', 'one held moment of the choir sample, resynthesised and smeared in three voices',
     SP('A', 'Choir', 0, 100, 0, 0, 55, 40, 48, 0, 0, 'Forward', 3, 10, 85, pos=35) + ';' + FLT(6500, 10, 0.4) + ';' + ENV(0.5, 1.0, 100, 1.6),
     n=1, fxs=_GEN_FX_PADS, no=('slow', 'snappy', 'tight', 'muted'))
base('Pad', 'slow_strings', 'Slow Strings Pad', 'the strings sample read at an eighth of its speed, its pitch kept',
     SP('A', 'Strings', 12, 100, 0, 0, 50, 30, 48, -2, 0, 'Forward', 3, 10, 85) + ';' + FLT(6000, 10, 0.4) + ';' + ENV(0.6, 1.0, 100, 1.8),
     n=1, fxs=_GEN_FX_PADS, no=('slow', 'snappy', 'tight', 'muted'))
base('Texture', 'bell_wash', 'Stretched Bell Wash', 'the bell sample stretched sixteen times, its partials blurred into a wash',
     SP('A', 'Bell', 6, 100, 0, 0, 60, 55, 64, 0, 0, 'Forward', 2, 8, 90, pos=1) + ';' + FLT(8000, 8, 0.3) + ';' + ENV(0.8, 1.0, 100, 2.5),
     n=1, fxs=_TEX_FX, no=('snappy', 'tight', 'muted'))
base('Texture', 'noise_ghost', 'Noise Ghost', 'three readings of the texture sample at different speeds, its partials turned down: mostly the analysed noise, slowly moving',
     SP('A', 'Texture', 20, 100, 0, 0, 30, 35, 24, -2, 0, 'Forward', 2, 8, 90, pos=50) + ';' + SP('B', 'Texture', 14, 100, 0, 0, 30, 45, 24, -2, 0, 'Forward', 2, 8, 90, pos=20) + ';' + SP('C', 'Texture', 26, 100, 0, 0, 30, 25, 24, -2, 0, 'Forward', 2, 8, 90, pos=75) + ';' + FLT(5000, 8, 0.2) + ';' + ENV(0.7, 1.0, 100, 2.0),
     n=1, fxs=_TEX_FX, no=('snappy', 'tight', 'muted'))
base('Vocal', 'formant_choir', 'Formant Choir', 'the choir sample resynthesised with its formants kept where they were, so it plays across the keyboard',
     SP('A', 'Choir', 100, 100, 100, 0, 45, 0, 48, 0, 0, 'Forward', 2, 8, 80) + ';' + FLT(7000, 8, 0.3) + ';' + ENV(0.15, 0.8, 100, 0.8),
     n=1, fxs=_VOC_FX, no=('snappy', 'tight', 'muted'))
base('Keys', 'snapped_pluck', 'Snapped Pluck Keys', 'the pluck sample with its partials pulled onto a harmonic series, so it stays clean and in tune',
     SP('A', 'Pluck', 100, 100, 0, 100, 70, 0, 32, 0, 0, 'Off', 1, 6, 60, pos=0) + ';' + FLT(9000, 8, 0.3) + ';' + ENV(0.002, 1.2, 0, 0.5),
     n=1, fxs=_GEN_FX_KEYS, no=('slow', 'pad', 'mono'))
base('Lead', 'strings_voice', 'Singing Strings Lead', 'the strings sample resynthesised in two voices with half of its formants kept',
     SP('A', 'Strings', 100, 100, 60, 0, 60, 0, 40, 1, 0, 'Forward', 2, 10, 70) + ';' + FLT(8000, 10, 0.5) + ';' + ENV(0.02, 0.4, 85, 0.35),
     n=1, fxs=_GEN_FX_LEAD, no=('slow', 'pad', 'soft'))
base('Bell', 'stretched_pluck', 'Stretched Pluck Bell', 'the pluck sample with its partials spread apart, so a string rings like a bell',
     SP('A', 'Pluck', 60, 100, 0, 0, 80, 0, 48, 0, 40, 'Off', 1, 6, 60, pos=0) + ';' + FLT(9000, 8, 0.3) + ';' + ENV(0.002, 2.0, 0, 1.2),
     n=1, fxs=_GEN_FX_BELL, no=('slow', 'mono', 'pad'))
base('FX', 'reverse_choir', 'Reverse Choir Swell', 'the choir sample resynthesised backwards, a swell into the note',
     SP('A', 'Choir', 80, 100, 0, 0, 50, 20, 48, 0, 0, 'Off', 2, 8, 80, rev=True) + ';' + FLT(7000, 8, 0.3) + ';' + ENV(0.05, 0.6, 100, 0.8),
     n=1, fxs=_FX_FX, no=('mono', 'tight'))
base('Cinematic', 'time_smear', 'Time Smear', 'the strings sample at a quarter of its speed, smeared and tilted dark',
     SP('A', 'Strings', 25, 100, 0, 0, 50, 80, 56, -4, 0, 'Forward', 3, 14, 90) + ';' + FLT(4500, 12, 0.8, 'LP 24') + ';' + ENV(0.8, 1.0, 100, 2.5),
     n=1, fxs=_CIN_FX, no=('slow', 'snappy', 'tight', 'muted', 'bright'))


# ---- Harmonic and Modal imports (P5b): a sample's partials as the partial table, an impulse response's modes as the body.
def HI(s='A', sample='Choir', frm=50, span=0, ratio=100, parts=32, tilt=0, damp=0, uni=1, det=8, spr=70, oct=0, lvl=100):
    return (HM(s, 'Saw', 'Saw', 0, parts, tilt, 0, 0, damp, uni, det, spr, oct, lvl) + f";{s} sample={sample};{s} import partials=Sample;"
            f"{s} import from={frm};{s} import span={span};{s} import ratios={ratio}")


def MI(s='A', sample='Bell', start=0, length=0.5, decay=100, exc='Plucked', force=55, pos=25, modes=16, uni=1, det=6, spr=60, oct=0, lvl=100):
    return (MD(s, 'Bell', exc, force, pos, modes, 50, 1.0, 35, 50, uni, det, spr, oct, lvl) + f";{s} sample={sample};{s} mode table=Impulse response;"
            f"{s} ir start={start};{s} ir length={length};{s} ir decay={decay}")


base('Pad', 'imported_choir', 'Imported Choir Pad', 'the choir sample\'s partials, read from its middle, as a clean organ-like partial table in three voices',
     HI('A', 'Choir', 50, 30, 0, 40, -2, 0, 3, 10, 85) + ';' + FLT(6500, 10, 0.4) + ';' + ENV(0.4, 1.0, 100, 1.4),
     n=1, fxs=_GEN_FX_PADS, no=('slow', 'snappy', 'tight', 'muted'))
base('Keys', 'imported_pluck', 'Imported Pluck Keys', 'the pluck sample\'s own partials, spaced as it was recorded, held as a clean ringing string',
     HI('A', 'Pluck', 10, 30, 100, 24, -2, 25, 1, 6, 60) + ';' + FLT(9000, 8, 0.3) + ';' + ENV(0.002, 1.5, 0, 0.8),
     n=1, fxs=_GEN_FX_KEYS, no=('slow', 'pad', 'mono'))
base('Bell', 'impulse_bell', 'Impulse Bell Body', 'the modes found in the bell sample read as an impulse response, struck by a pluck',
     MI('A', 'Bell', 5, 0.8, 100, 'Plucked', 60, 25, 16, 1, 6, 60) + ';' + FLT(9000, 8, 0.3) + ';' + ENV(0.002, 2.5, 0, 1.4),
     n=1, fxs=_GEN_FX_BELL, no=('slow', 'mono', 'pad'))


def bases():
    return list(_BASES)

# Candidates measured as unable to reach -22 dB within the Volume range or to stay under -3 dBFS peak: skipped.
CURATED_OUT = {
    'FX — Long Noise Riser with Long Echo',
    'Bass — Resonant Table Bass Crushed Hard',
    'Bass — Soft Pluck Sample Bass Scooped',
    'Seq — Chance Pluck Line in a Room',
    'FX — Long Falling Laser Crushed Hard',
    'Drum — Closed Hat Glued',
    'Bass — Resonant Table Bass Squashed',
    'Bass — Pluck Sample Bass',
    'Bass — Vibrato Pluck Sample Bass Glued',
    'Bass — Vibrato FM Sine Bass Squashed',
    'Seq — Chance Pluck Line with Ping-Pong',
    'Seq — Tight Chance Pluck Line Robotic',
    'FX — Falling Laser Chattering',
    'FX — Rising Zap Chattering',
    'Drum — Dark Noise Snare Squashed',
    'Drum — Closed Hat',
    'Drum — Muted Closed Hat in a Room',
}
CURATED_OUT_SUFFIX = ' Chattering'  # the vocoder's sibilance path ignores Volume, so these cannot be levelled
CURATED_OUT_CONTAINS = ('Chance Pluck Line', 'Closed Hat')  # families whose gate or transient cannot meet the level and peak rules
