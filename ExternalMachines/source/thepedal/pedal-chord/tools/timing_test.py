#!/usr/bin/env python3
"""
Timing regression test for Pedal Chord v1.6.0 (sample-fraction step clock).

Models the ReBuzz 1827 audio loop at sample accuracy in two modes:
  * sub-tick ON  : chunks clamped to sub-tick boundaries; CurrentSubTick clean.
  * sub-tick OFF : chunks clamped only to 256/tick, but the engine STILL advances
                   CurrentSubTick and STILL reports SubTicksPerTick > 1.

It then runs two arp implementations against that engine:
  * FracArp  : v1.6.0 — step clock = accumulated PosInTick/SamplesPerTick.
  * EdgeArp  : pre-1.6.0 — counts CurrentSubTick edges against Speed*SubTicksPerTick.

Asserted for FracArp (the shipped clock):
  - gap == Speed ticks at every tempo, TPB, and sub-tick on/off (no drift);
  - a re-seed (NoteOn each bar) reproduces the free-run gap;
  - Swing splits a 2-step span into long+short summing to 2*Speed, tempo-locked.

EdgeArp is included only to demonstrate the bug it had: with sub-tick OFF its
gaps are wrong and tempo-dependent (the engine emits != SubTicksPerTick edges
per tick). Exit non-zero if any FracArp assertion fails.
"""
import sys

class Engine:
    SubTickSize = 260
    def __init__(self, sr, bpm, tpb, subres=2, subtick=True):
        self.subtick = subtick
        self.SamplesPerTick = int(60.0 * sr / (bpm * tpb))
        self.SubTicksPerTick = max(1, self.SamplesPerTick // self.SubTickSize // subres)
        self.Avg = self.SamplesPerTick / float(self.SubTicksPerTick)
        self.SamplesPerSubTick = int(self.Avg)
        self.PosInTick = 0; self.PosInSubTick = 0; self.CurrentSubTick = 0
        self.rem = 0; self.tickno = 0
    def _ustl(self):
        if self.subtick and self.PosInSubTick == 0:
            r = self.SamplesPerTick % self.SubTicksPerTick
            self.SamplesPerSubTick = int(self.Avg); self.rem += r
            if self.rem >= self.SubTicksPerTick:
                self.rem -= self.SubTicksPerTick; self.SamplesPerSubTick += 1
    def run(self, m, nticks, inject_bar=None):
        remaining = self.SamplesPerTick * nticks
        while remaining > 0:
            stp = min(remaining, 256); self._ustl()
            if self.PosInTick + stp > self.SamplesPerTick:
                stp = self.SamplesPerTick - self.PosInTick
            if self.subtick and self.PosInSubTick + stp > self.SamplesPerSubTick:
                stp = self.SamplesPerSubTick - self.PosInSubTick
            if stp <= 0: break
            if inject_bar and self.PosInTick == 0 and self.tickno % inject_bar == 0:
                m.has_new = True; m.pending = 60
            m.work(self)
            self.PosInSubTick += stp; self.PosInTick += stp; remaining -= stp
            if self.PosInSubTick >= self.SamplesPerSubTick:
                self.PosInSubTick = 0; self.CurrentSubTick += 1
            if self.PosInTick >= self.SamplesPerTick:
                self.PosInTick = 0; self.CurrentSubTick = 0; self.tickno += 1

class FracArp:
    def __init__(self, speed, swing=0, swingphase=0):
        self.speed = speed; self.swing = swing; self.swingphase = swingphase
        self.prevPit = None; self.acc = 0.0; self.steplen = 0.0; self.parity = 0
        self.has_new = False; self.pending = 0; self.fires = []; self.tickabs = 0
        self.started = False
    def _len(self):
        period = 2.0 * self.speed; ratio = 1.0 + self.swing / 100.0
        longL = period * ratio / (ratio + 1.0); shortL = period - longL
        isLong = ((self.parity + self.swingphase) % 2 == 0)
        self.parity = 1 - self.parity
        return max(1.0 / 256.0, longL if isLong else shortL)
    def _step(self, e):
        self.fires.append(self.tickabs + e.PosInTick / e.SamplesPerTick)
        self.steplen = self._len()
    def work(self, e):
        pit = e.PosInTick; spt = e.SamplesPerTick
        if self.prevPit is None: d = 0
        else:
            d = pit - self.prevPit
            if d < 0: d += spt; self.tickabs += 1
        self.prevPit = pit; self.acc += d / float(spt)
        if self.has_new:
            self.has_new = False
            if self.pending > 0:
                self.started = True; self.acc = 0.0; self.parity = 0; self._step(e)
            return
        if not self.started or self.speed <= 0: return
        g = 0
        while self.steplen > 0 and self.acc >= self.steplen and g < 64:
            self.acc -= self.steplen; self._step(e); g += 1

class EdgeArp:   # pre-1.6.0, for contrast only
    def __init__(self, speed):
        self.speed = speed; self.ArpTicks = 0; self.prevPit = 10**9; self.prevSub = -1
        self.fires = []; self.tickabs = 0; self.started = False
    def _step(self, e):
        self.fires.append(self.tickabs + e.PosInTick / e.SamplesPerTick)
        R = e.SubTicksPerTick if e.SubTicksPerTick > 1 else 1
        self.ArpTicks = max(1, self.speed * R)
    def work(self, e):
        pit = e.PosInTick; nt = pit < self.prevPit
        if nt: self.tickabs += 1
        self.prevPit = pit
        cs = e.CurrentSubTick; newStep = nt or cs != self.prevSub; self.prevSub = cs
        if not self.started: self.started = True; self._step(e); return
        if newStep and self.ArpTicks > 0:
            self.ArpTicks -= 1
            if self.ArpTicks == 0: self._step(e)

def gaps(m): return [round(m.fires[i+1]-m.fires[i], 3) for i in range(len(m.fires)-1)]

def run_frac(bpm, speed, tpb=4, subtick=True, swing=0, nticks=64, inject_bar=None):
    e = Engine(44100, bpm, tpb, subtick=subtick); a = FracArp(speed, swing)
    a.has_new = True; a.pending = 60
    e.run(a, nticks, inject_bar=inject_bar)
    return a

if __name__ == "__main__":
    fails = []
    TEMPOS = [90, 100, 126, 128, 140, 60, 174]

    def quantum(e):
        # placement resolution = audio chunk: sub-tick size when on, else 256.
        return (e.SamplesPerSubTick if e.subtick else 256) / float(e.SamplesPerTick) + 1e-6

    def run2(bpm, speed, tpb=4, subtick=True, swing=0, nticks=64, inject_bar=None):
        e = Engine(44100, bpm, tpb, subtick=subtick); a = FracArp(speed, swing)
        a.has_new = True; a.pending = 60
        e.run(a, nticks, inject_bar=inject_bar)
        return e, a

    def check_grid(bpm, speed, tpb=4, subtick=True, inject_bar=None):
        e, a = run2(bpm, speed, tpb, subtick, inject_bar=inject_bar)
        q = quantum(e); label = f"bpm{bpm} tpb{tpb} sub={subtick}" + (f" reseed{inject_bar}" if inject_bar else "")
        for f in a.fires:
            anchor = (f // inject_bar) * inject_bar if inject_bar else 0.0
            k = round((f - anchor) / speed)
            ideal = anchor + k * speed
            if abs(f - ideal) > q:
                fails.append(f"{label}: fire {f:.4f} off grid by {abs(f-ideal):.4f} > {q:.4f}")
                break

    # 1) tempo independence + no drift, sub-tick ON and OFF
    for sub in (True, False):
        for bpm in TEMPOS:
            check_grid(bpm, 8, subtick=sub)
    # 2) TPB variations
    for tpb in (4, 8, 16):
        for bpm in (90, 126):
            check_grid(bpm, 8, tpb=tpb)
    # 3) re-seed every bar — fires re-anchor to the bar, still on grid
    for bpm in (90, 126):
        check_grid(bpm, 8, inject_bar=32)
    # 4) swing: long+short == 2*Speed, tempo-locked (use exact fire positions)
    for bpm in (90, 126):
        e, a = run2(bpm, 8, swing=50)
        f = a.fires
        pairs = [round(f[i+2] - f[i], 2) for i in range(1, min(7, len(f) - 2))]
        if any(abs(p - 16.0) > 2 * quantum(e) for p in pairs):
            fails.append(f"swing bpm {bpm}: 2-step spans {pairs} (want 16)")

    print("FracArp (v1.6.0) timing:", "PASS" if not fails else "FAIL")
    for f in fails: print("  -", f)

    # Contrast: EdgeArp with sub-tick OFF is wrong + tempo-dependent.
    print("EdgeArp (pre-1.6.0) sub-tick OFF — demonstrates the old bug:")
    for bpm in (126, 90):
        e = Engine(44100, bpm, 4, subtick=False); a = EdgeArp(8)
        a.started = False; e.run(a, 48)
        print(f"  bpm {bpm}: gaps {sorted(set(gaps(a)))} (should have been 8)")

    sys.exit(1 if fails else 0)
