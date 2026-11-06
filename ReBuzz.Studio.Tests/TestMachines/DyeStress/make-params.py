#!/usr/bin/env python3
"""Writes Dye Stress's Params.def (docs/specs/dye-essence.md §5.2): a parameter table shaped as Dye
Essence's will be (option A of the analysis: each engine slot holds every engine's parameters), about
700 parameters, for the G0 stress test. Not a design: the counts, kinds and ranges are what matter.

    python3 ReBuzz.Studio.Tests/TestMachines/DyeStress/make-params.py
    python3 tools/gen-machine-params.py ReBuzz.Studio.Tests/TestMachines/DyeStress
"""
import os

ENGINES = 'Off Wavetable Analog Sample Multisample Granular Harmonic Modal Spectral'
WARPS = 'None Bend+ Bend− Squeeze Mirror Asym Sync Window Quantise Remap FM PM AM Ring Fold Distort Filter'
FILTERS = ('Off Ladder_24 Ladder_12 Diode Acid SVF_LP SVF_BP SVF_HP SVF_Notch SVF_Peak Comb+ Comb− Formant '
           'Phaser DJ Lo-fi Cluster Rage Ripple Diffuser')
SHAPES = 'Sine Triangle Saw_up Saw_down Square S&H Smooth_random Drawn'
SYNC = 'Free 1/32 1/16T 1/16 1/16D 1/8T 1/8 1/8D 1/4T 1/4 1/4D 1/2 1_bar 2_bars 4_bars 8_bars'
FX = 'Off Drive Chorus Flanger Phaser Delay Reverb Compressor Multiband EQ Filter Utility Dimension Shifter Convolve Bitcrusher'

lines = [
    '# Dye Stress parameters (docs/specs/dye-essence.md §5.2), written by make-params.py: a table shaped as',
    '# Dye Essence\'s will be, for the G0 stress test. Regenerate: python3 tools/gen-machine-params.py',
    '# ReBuzz.Studio.Tests/TestMachines/DyeStress',
    'namespace ReBuzz.Studio.Tests.DyeStress',
    'class DyeStressMachine',
    'tracks 16',
    'summary Test machine for the Dye Essence G0 gate: about 700 parameters and a large state. Not shipped.',
    '',
]
count = 0


def p(pid, name, lo, hi, de, kind, desc=''):
    global count
    lines.append(f'{pid:<14} "{name}" {lo} {hi} {de} {kind}' + (f' | {desc}' if desc else ''))
    count += 1


def group(name):
    lines.append('')
    lines.append(f'group {name}')


group('Voice')
p('Layout', 'Layout', 0, 1, 0, 'enum Trio Duo', 'Template: three sources, or two engines and the utility block')
p('VoiceMode', 'Voice mode', 0, 2, 0, 'enum Poly Mono Legato')
p('Polyphony', 'Polyphony', 1, 16, 8, 'int 0')
p('GlideMode', 'Glide mode', 0, 2, 0, 'enum Off Always Legato')
p('Glide', 'Glide', 0, 1000, 200, 'pow 0 2 3 s 3')
p('BendUp', 'Bend up', 0, 48, 2, 'int 0 st')
p('BendDown', 'Bend down', 0, 48, 2, 'int 0 st')
p('Quality', 'Quality', 0, 2, 1, 'enum Eco Standard High', '1×, 2× or 4× voices')
p('Volume', 'Volume', 0, 1000, 739, 'db -40 6 inf')
p('VelAmp', 'Vel → amp', 0, 1000, 700, 'pct')
p('MasterTune', 'Master tune', 0, 1000, 500, 'lin -100 100 ct 1')
p('Pan', 'Pan', 0, 1000, 500, 'bip')
p('Width', 'Width', 0, 1000, 1000, 'pct')
p('Drift', 'Drift', 0, 1000, 0, 'pct')
p('Seed', 'Seed', 0, 1000, 0, 'int 0')

slot_params = [
    ('Type', 'type', 0, 8, 1, 'enum ' + ENGINES), ('Level', 'level', 0, 1000, 800, 'pct'), ('Pan', 'pan', 0, 1000, 500, 'bip'),
    ('Oct', 'octave', 0, 8, 4, 'int -4 oct signed'), ('Semi', 'semi', 0, 24, 12, 'int -12 st signed'),
    ('Fine', 'fine', 0, 1000, 500, 'lin -100 100 ct 1'), ('Unison', 'unison', 1, 16, 1, 'int 0'),
    ('Detune', 'detune', 0, 1000, 200, 'pct'), ('Blend', 'blend', 0, 1000, 500, 'pct'), ('Spread', 'spread', 0, 1000, 600, 'pct'),
    ('Phase', 'phase', 0, 1000, 0, 'lin 0 360 ° 0'), ('RandPh', 'rand phase', 0, 1000, 1000, 'pct'),
    ('ToF1', '→ filter 1', 0, 1000, 1000, 'pct'), ('ToF2', '→ filter 2', 0, 1000, 0, 'pct'), ('Direct', 'direct', 0, 1000, 0, 'pct'),
    # wavetable
    ('Table', 'table', 0, 255, 0, 'int 1'), ('Pos', 'position', 0, 1000, 0, 'pct'), ('W1', 'warp 1', 0, 16, 0, 'enum ' + WARPS),
    ('W1Amt', 'warp 1 amt', 0, 1000, 0, 'pct'), ('W2', 'warp 2', 0, 16, 0, 'enum ' + WARPS), ('W2Amt', 'warp 2 amt', 0, 1000, 0, 'pct'),
    ('FmSrc', 'FM source', 0, 4, 0, 'enum Off Slot_1 Slot_2 Slot_3 Sub'), ('FmAmt', 'FM amount', 0, 1000, 0, 'pct'),
    ('Interp', 'interpolation', 0, 1, 0, 'enum Cubic Smooth'),
    # analog
    ('Shape1', 'shape 1', 0, 1000, 500, 'pct'), ('Shape2', 'shape 2', 0, 1000, 500, 'pct'), ('Shape3', 'shape 3', 0, 1000, 500, 'pct'),
    ('Pw', 'pulse width', 0, 1000, 500, 'pct'), ('Sync', 'sync', 0, 1, 0, 'switch'), ('SyncPitch', 'sync pitch', 0, 1000, 0, 'lin 0 48 st 1'),
    ('Mix2', 'osc 2 level', 0, 1000, 0, 'pct'), ('Mix3', 'osc 3 level', 0, 1000, 0, 'pct'), ('Ring', 'ring', 0, 1000, 0, 'pct'),
    # sample and multisample
    ('Wave', 'wave', 0, 226, 0, 'int 0'), ('Root', 'root', 0, 127, 60, 'int 0'), ('Start', 'start', 0, 65534, 0, 'lin 0 100 % 3'),
    ('End', 'end', 0, 65534, 65534, 'lin 0 100 % 3'), ('Loop', 'loop', 0, 3, 0, 'enum Wave Off Forward Ping-pong'),
    ('LoopS', 'loop start', 0, 65534, 0, 'lin 0 100 % 3'), ('LoopE', 'loop end', 0, 65534, 65534, 'lin 0 100 % 3'),
    ('XFade', 'crossfade', 0, 1000, 0, 'time 0.001 2'), ('Rate', 'rate', 0, 1000, 500, 'lin -2 2 oct 2'),
    ('Reverse', 'reverse', 0, 1, 0, 'switch'), ('Zone', 'zone mode', 0, 3, 0, 'enum Key Velocity Round_robin Random'),
    # granular
    ('Density', 'density', 0, 1000, 300, 'exp 1 500 Hz 1'), ('Size', 'grain size', 0, 1000, 400, 'time 0.002 1'),
    ('GPos', 'grain position', 0, 1000, 0, 'pct'), ('Spray', 'spray', 0, 1000, 0, 'pct'), ('PSpray', 'pitch spray', 0, 1000, 0, 'lin 0 24 st 1'),
    ('PanSpray', 'pan spray', 0, 1000, 0, 'pct'), ('Window', 'window', 0, 4, 0, 'enum Hann Tukey Triangle Rect Gauss'),
    ('Dir', 'direction', 0, 2, 0, 'enum Forward Reverse Random'), ('Grains', 'grains', 1, 256, 64, 'int 0'),
    ('Scan', 'scan', 0, 1000, 500, 'lin -4 4 x 2'),
    # harmonic
    ('Partials', 'partials', 1, 512, 64, 'int 0'), ('OddEven', 'odd/even', 0, 1000, 500, 'bip'), ('Ratio', 'ratio', 0, 1000, 0, 'lin 0 1 x 3'),
    ('HDecay', 'decay', 0, 1000, 500, 'pct'), ('Fmt1', 'formant 1', 0, 1000, 500, 'freq 100 8000'), ('Fmt1Q', 'formant 1 Q', 0, 1000, 300, 'exp 0.5 20'),
    ('Fmt1Amt', 'formant 1 amt', 0, 1000, 0, 'pct'), ('Fmt2', 'formant 2', 0, 1000, 600, 'freq 100 8000'),
    ('Fmt2Q', 'formant 2 Q', 0, 1000, 300, 'exp 0.5 20'), ('Fmt2Amt', 'formant 2 amt', 0, 1000, 0, 'pct'),
    ('HWindow', 'partial window', 0, 1000, 1000, 'pct'), ('Cluster', 'cluster', 0, 1000, 0, 'pct'), ('Shepard', 'shepard', 0, 1000, 0, 'pct'),
    ('PPan', 'partial pan', 0, 1000, 0, 'pct'),
    # modal
    ('Model', 'model', 0, 3, 0, 'enum String Beam Plate Tube'), ('Exciter', 'exciter', 0, 3, 0, 'enum Strike Noise Bow Audio'),
    ('Strike', 'strike', 0, 1000, 500, 'pct'), ('Bow', 'bow pressure', 0, 1000, 300, 'pct'), ('Stiff', 'stiffness', 0, 1000, 0, 'pct'),
    ('Damp', 'damping', 0, 1000, 300, 'pct'), ('MDecay', 'mode decay', 0, 1000, 500, 'time 0.01 20'), ('Modes', 'modes', 1, 128, 32, 'int 0'),
    ('ExPos', 'excite position', 0, 1000, 300, 'pct'), ('Pickup', 'pickup', 0, 1000, 700, 'pct'), ('Quant', 'quantise', 0, 1, 1, 'switch'),
    # spectral
    ('SFrame', 'scan rate', 0, 1000, 500, 'lin -4 4 x 2'), ('Timbre', 'timbre shift', 0, 1000, 500, 'lin -24 24 st 1'),
    ('LoCut', 'low limit', 0, 1000, 0, 'freq 20 20000'), ('HiCut', 'high limit', 0, 1000, 1000, 'freq 20 20000'),
    ('Transient', 'transients', 0, 1000, 1000, 'pct'), ('Formant', 'keep formants', 0, 1, 1, 'switch'),
    ('Noise', 'residual', 0, 1000, 500, 'pct'),
    # per slot, every engine
    ('KeyTrack', 'key track', 0, 1000, 1000, 'pct'), ('VelLevel', 'vel → level', 0, 1000, 0, 'pct'),
    ('PEnv', 'pitch env', 0, 4, 0, 'enum Off Env_1 Env_2 Env_3 Env_4'), ('PEnvAmt', 'pitch env amt', 0, 1000, 500, 'lin -48 48 st 1'),
    ('Retrig', 'retrigger', 0, 1, 1, 'switch'), ('Mono', 'mono', 0, 1, 0, 'switch'), ('SDrift', 'drift', 0, 1000, 0, 'pct'),
    ('Tuning', 'tuning', 0, 3, 0, 'enum Semitones Harmonics Ratio Fixed'),
]
for s in (1, 2, 3):
    group(f'Slot {s}')
    for pid, name, lo, hi, de, kind in slot_params:
        p(f'S{s}{pid}', f'S{s} {name}', lo, hi, de if not (pid == 'Type' and s > 1) else 0, kind)

group('Utility')
for i, (pid, name, lo, hi, de, kind) in enumerate([
        ('SubShape', 'Sub shape', 0, 4, 0, 'enum Sine Triangle Saw Square Pulse'), ('SubOct', 'Sub octave', 0, 2, 0, 'enum -1 -2 -3'),
        ('SubLvl', 'Sub level', 0, 1000, 0, 'pct'), ('SubPan', 'Sub pan', 0, 1000, 500, 'bip'), ('SubDirect', 'Sub direct', 0, 1000, 0, 'pct'),
        ('N1Type', 'Noise 1', 0, 7, 0, 'enum White Pink Brown Crackle Vinyl Air Digital Metal'), ('N1Lvl', 'Noise 1 level', 0, 1000, 0, 'pct'),
        ('N1Tone', 'Noise 1 tone', 0, 1000, 500, 'bip'), ('N1Loop', 'Noise 1 loop', 0, 1, 1, 'switch'),
        ('N2Type', 'Noise 2', 0, 7, 1, 'enum White Pink Brown Crackle Vinyl Air Digital Metal'), ('N2Lvl', 'Noise 2 level', 0, 1000, 0, 'pct'),
        ('N2Tone', 'Noise 2 tone', 0, 1000, 500, 'bip'), ('N2Loop', 'Noise 2 loop', 0, 1, 1, 'switch'),
        ('InLvl', 'Input level', 0, 1000, 0, 'pct'), ('InGate', 'Input gate', 0, 1, 0, 'switch')]):
    p(pid, name, lo, hi, de, kind)

for f in (1, 2):
    group(f'Filter {f}')
    p(f'F{f}Type', f'F{f} type', 0, 19, 1 if f == 1 else 0, 'enum ' + FILTERS)
    p(f'F{f}Cut', f'F{f} cutoff', 0, 1000, 750, 'freq 20 20000')
    p(f'F{f}Res', f'F{f} resonance', 0, 1000, 150, 'pct')
    p(f'F{f}Drive', f'F{f} drive', 0, 1000, 0, 'lin 0 24 dB 1')
    p(f'F{f}Key', f'F{f} key track', 0, 1000, 500, 'pct')
    p(f'F{f}Env', f'F{f} env', 0, 1000, 500, 'lin -6 6 oct 2')
    p(f'F{f}Vel', f'F{f} velocity', 0, 1000, 0, 'pct')
    p(f'F{f}Morph', f'F{f} morph', 0, 1000, 0, 'pct')
    p(f'F{f}Spread', f'F{f} spread', 0, 1000, 0, 'lin -2 2 oct 2')
    p(f'F{f}Mix', f'F{f} mix', 0, 1000, 1000, 'pct')
    p(f'F{f}Pan', f'F{f} pan', 0, 1000, 500, 'bip')
p('Routing', 'Filter routing', 0, 1000, 0, 'pct', 'Serial (0 %) to parallel (100 %)')

group('Amp')
p('AmpLvl', 'Amp level', 0, 1000, 800, 'db -40 6 inf')
p('AmpVel', 'Amp velocity', 0, 1000, 700, 'pct')
p('AmpPan', 'Amp pan', 0, 1000, 500, 'bip')
p('AmpDrive', 'Amp drive', 0, 1000, 0, 'lin 0 24 dB 1')
p('AmpKeyPan', 'Amp key pan', 0, 1000, 500, 'bip')
p('AmpEnv', 'Amp envelope', 0, 3, 0, 'enum Env_1 Env_2 Env_3 Env_4')

for e in (1, 2, 3, 4):
    group(f'Envelope {e}')
    p(f'E{e}Delay', f'E{e} delay', 0, 1000, 0, 'time 0.0005 10')
    p(f'E{e}A', f'E{e} attack', 0, 1000, 100, 'time 0.0005 20')
    p(f'E{e}Hold', f'E{e} hold', 0, 1000, 0, 'time 0.0005 10')
    p(f'E{e}D', f'E{e} decay', 0, 1000, 400, 'time 0.001 30')
    p(f'E{e}S', f'E{e} sustain', 0, 1000, 700, 'pct')
    p(f'E{e}R', f'E{e} release', 0, 1000, 400, 'time 0.001 30')
    p(f'E{e}Curve', f'E{e} curve', 0, 1000, 500, 'bip')
    p(f'E{e}Sync', f'E{e} sync', 0, 1, 0, 'switch')

for l in range(1, 11):
    group(f'LFO {l}')
    p(f'L{l}Shape', f'L{l} shape', 0, 7, 0, 'enum ' + SHAPES)
    p(f'L{l}Rate', f'L{l} rate', 0, 1000, 400, 'exp 0.01 1000 Hz 2')
    p(f'L{l}Sync', f'L{l} sync', 0, 15, 0, 'enum ' + SYNC)
    p(f'L{l}Phase', f'L{l} phase', 0, 1000, 0, 'lin 0 360 ° 0')
    p(f'L{l}Fade', f'L{l} fade', 0, 1000, 0, 'time 0.001 10')
    p(f'L{l}Retrig', f'L{l} retrig', 0, 2, 0, 'enum Free Note Song')
    p(f'L{l}Smooth', f'L{l} smooth', 0, 1000, 0, 'pct')
    p(f'L{l}Uni', f'L{l} unipolar', 0, 1, 0, 'switch')

for kind, n, params in (('Function', 3, [('Rate', 'rate', 0, 1000, 400, 'exp 0.01 100 Hz 2'), ('Sync', 'sync', 0, 15, 0, 'enum ' + SYNC),
                                          ('Mode', 'mode', 0, 2, 0, 'enum Loop One-shot Envelope'), ('Smooth', 'smooth', 0, 1000, 0, 'pct'),
                                          ('Steps', 'steps', 1, 64, 16, 'int 0')]),
                        ('Random', 3, [('Type', 'type', 0, 3, 0, 'enum S&H Turing Binary Smooth'), ('Rate', 'rate', 0, 1000, 400, 'exp 0.01 100 Hz 2'),
                                       ('Prob', 'probability', 0, 1000, 500, 'pct'), ('Smooth', 'smooth', 0, 1000, 0, 'pct'),
                                       ('Len', 'length', 1, 32, 8, 'int 0')]),
                        ('Combinator', 3, [('Algo', 'algorithm', 0, 8, 0, 'enum Add Multiply Min Max Lag Quantise Crossfade Follow Scale'),
                                           ('A', 'A amount', 0, 1000, 500, 'bip'), ('B', 'B amount', 0, 1000, 500, 'bip'),
                                           ('Param', 'param', 0, 1000, 0, 'pct'), ('Out', 'output', 0, 1000, 1000, 'bip')])):
    for i in range(1, n + 1):
        group(f'{kind} {i}')
        for pid, name, lo, hi, de, k in params:
            p(f'{kind[:3]}{i}{pid}', f'{kind[:3]} {i} {name}', lo, hi, de, k)

group('MPE and tuning')
for pid, name, lo, hi, de, kind in [
        ('Mpe', 'MPE', 0, 1, 0, 'switch'), ('MpeZone', 'MPE zone', 0, 1, 0, 'enum Lower Upper'), ('MpeChannels', 'MPE channels', 1, 15, 15, 'int 0'),
        ('MpeBend', 'MPE bend range', 0, 96, 48, 'int 0 st'), ('MpeSlide', 'MPE slide CC', 0, 127, 74, 'int 0'),
        ('MpePress', 'MPE pressure curve', 0, 1000, 500, 'bip'), ('MpeSlideCurve', 'MPE slide curve', 0, 1000, 500, 'bip'),
        ('MpeGlide', 'MPE glide', 0, 1000, 0, 'pct'), ('Scale', 'Scale', 0, 11, 0, 'enum Chromatic Major Minor Dorian Phrygian Lydian Mixolydian Locrian Pentatonic Blues Whole_tone Diminished'),
        ('ScaleRoot', 'Scale root', 0, 11, 0, 'enum C Db D Eb E F Gb G Ab A Bb B'), ('TuneTable', 'Tuning table', 0, 15, 0, 'int 0'),
        ('A4', 'A4', 0, 1000, 500, 'lin 415 465 Hz 2'), ('Snap', 'Snap to scale', 0, 1, 0, 'switch'), ('Octaves', 'Octave stretch', 0, 1000, 500, 'lin -50 50 ct 1'),
        ('Voices', 'Voice limit', 1, 32, 32, 'int 0')]:
    p(pid, name, lo, hi, de, kind)

group('Macros')
for m in range(1, 9):
    p(f'Macro{m}', f'Macro {m}', 0, 1000, 0, 'pct')

group('Routes')
for r in range(1, 65):
    p(f'R{r}Amt', f'Route {r} amount', 0, 1000, 500, 'bip')

for bus in ('A', 'B', 'Main'):
    for s in (1, 2, 3, 4) if bus == 'Main' else (1, 2, 3):
        group(f'FX {bus} {s}')
        p(f'Fx{bus}{s}Type', f'FX {bus}{s} type', 0, 15, 0, 'enum ' + FX)
        p(f'Fx{bus}{s}Mix', f'FX {bus}{s} mix', 0, 1000, 1000, 'pct')
        for k in range(1, 9):
            p(f'Fx{bus}{s}P{k}', f'FX {bus}{s} P{k}', 0, 1000, 500, 'pct')
    if bus != 'Main':
        group(f'Bus {bus}')
        p(f'Bus{bus}Lvl', f'Bus {bus} level', 0, 1000, 800, 'db -40 6 inf')
        p(f'Bus{bus}Send', f'Bus {bus} to main', 0, 1000, 1000, 'pct')

group('Arpeggiator')
for pid, name, lo, hi, de, kind in [
        ('ArpOn', 'Arp', 0, 1, 0, 'switch'), ('ArpMode', 'Arp mode', 0, 6, 0, 'enum Up Down Up-down Order Random Chord Sequence'),
        ('ArpRate', 'Arp rate', 0, 15, 9, 'enum ' + SYNC), ('ArpOct', 'Arp octaves', 1, 4, 1, 'int 0'), ('ArpGate', 'Arp gate', 0, 1000, 500, 'pct'),
        ('ArpSwing', 'Arp swing', 0, 1000, 0, 'pct'), ('ArpSteps', 'Arp steps', 1, 32, 16, 'int 0'), ('ArpChance', 'Arp chance', 0, 1000, 1000, 'pct'),
        ('ArpVel', 'Arp velocity', 0, 1000, 0, 'pct'), ('ArpRepeat', 'Arp repeats', 1, 8, 1, 'int 0'), ('ArpTrans', 'Arp transpose', 0, 48, 24, 'int -24 st signed'),
        ('ArpHold', 'Arp hold', 0, 1, 0, 'switch'), ('ArpRetrig', 'Arp retrig', 0, 1, 1, 'switch'), ('ArpOffset', 'Arp offset', 0, 31, 0, 'int 0'),
        ('ArpScale', 'Arp scale', 0, 11, 0, 'enum Chromatic Major Minor Dorian Phrygian Lydian Mixolydian Locrian Pentatonic Blues Whole_tone Diminished')]:
    p(pid, name, lo, hi, de, kind)

lines.append('')
here = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(here, 'Params.def'), 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(lines))
print(f'wrote Params.def ({count} parameters)')
