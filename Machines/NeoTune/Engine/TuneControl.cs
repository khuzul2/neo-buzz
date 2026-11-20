#nullable disable
// Neo Tune's control stage (docs/specs/neo-tune.md section 11.1). Per tracker estimate:
//  - history: the pitch in MIDI (absolute) and in cents from the target note in force (relative; when the target changes the frames since
//    the change are re-expressed against the new note, so the average does not mix two notes), both as running sums;
//  - vibrato: zero crossings of the deviation (a Schmitt trigger so the tracker's noise does not count) give the vibrato period; the centre
//    is the average over one period (two when the vibrato is fast), which cancels the vibrato and its harmonics exactly, against a fixed
//    low-pass whose leakage at 5 Hz is a quarter of the swing; without a vibrato the window is 50-100 ms;
//  - target: the note nearest to the centre, kept until another is nearer by a hysteresis (15-70 cents past the midpoint) for a minimum time
//    (30-180 ms): the Note stickiness control; so a wide vibrato does not flip between neighbouring notes;
//  - shift: -(centre deviation x zone weight x movement weight), smoothed with the retune speed (slower on a sustained note: Humanize),
//    plus (1 - Keep) of the expression (the instantaneous deviation less the centre) to flatten it, scaled by Amount, plus Transpose and the
//    added vibrato. At Keep 100 % the singer's vibrato, drift and scoops are exactly as sung.

using System;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.Tune
{
    internal sealed partial class TuneEngine
    {
        const int HistBits = 11, HistSize = 1 << HistBits, HistMask = HistSize - 1;
        const double HoldGap = 0.07, RampSeconds = 0.020, SilenceRms = 0.002, SlewRate = 500.0;

        // ---- history of the current run
        readonly double[] hAbs = new double[HistSize], hDev = new double[HistSize];
        readonly double[] csAbs = new double[HistSize], csDev = new double[HistSize];
        readonly double[] cAbsTrack = new double[64], mcTrack = new double[64];
        long hCount, hStart, winFloor;
        int stepRun;
        double hBase;

        // ---- vibrato
        double slowDev, devSmooth, prevX, vibBand = 10, vibAmp, lastRise, runTime, zeroUp;
        readonly double[] vibPeriods = new double[4];
        int vibN;
        double vibPeriod;
        bool vibrato;
        int vibDir, vibBad; double vibExt, vibExtT, vibLastT, vibLastV; bool vibHaveLast;
        readonly double[] vibSwings = new double[4];

        // ---- target and smoothing
        readonly KeyListener listener = new KeyListener();
        bool runActive, firstFrame;
        double unvoicedFor, realFor;
        int curNote = -1, pendNote = -1;
        double pendTime, noteTime;
        double sSmooth, devInst, exprAbs;
        bool smoothInit;
        double lfoPhase, lastEnd;
        int listenRoot = -1;
        double heldF0, heldShift;
        long evCentreT;
        bool evPrevVoiced;

        void ResetControl()
        {
            hCount = hStart = winFloor = 0; stepRun = 0; hBase = 0; exprAbs = 0;
            ResetVibrato();
            runActive = false; unvoicedFor = 1; realFor = 1;
            curNote = pendNote = -1; pendTime = noteTime = 0;
            sSmooth = devInst = 0; smoothInit = false; lfoPhase = 0; lastEnd = 0;
            listener.Reset(); listenRoot = -1;
            heldF0 = heldShift = 0;
            Array.Clear(cAbsTrack, 0, cAbsTrack.Length); Array.Clear(mcTrack, 0, mcTrack.Length);
        }

        void ResetVibrato()
        {
            slowDev = devSmooth = prevX = 0; vibBand = 10; vibAmp = 0; lastRise = zeroUp = runTime = 0;
            vibN = 0; vibPeriod = 0; vibrato = false; vibDir = 0; vibBad = 0; vibHaveLast = false; vibExt = vibExtT = vibLastT = vibLastV = 0;
        }

        static double Smooth(double a, double b, double x)
        {
            double t = Math.Clamp((x - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }

        /// <summary>Whether a new target note beats the one in force: nearer to the centre by the hysteresis (cents).</summary>
        int PickNote(double mc, int mask, double hystCents)
        {
            if (target == 1)
            {
                if (heldCount == 0) return -1;
                int cand;
                if (midiOctave == 0) cand = TuneScales.Nearest(mc, mask);
                else
                {
                    cand = -1;
                    double bd = 1e9;
                    for (int n = 0; n < 128; n++)
                    {
                        if (!held[n]) continue;
                        double d = Math.Abs(mc - n);
                        if (d < bd) { bd = d; cand = n; }
                    }
                }
                if (cand < 0) return -1;
                if (curNote >= 0 && curNote != cand && curNote < 128 && held[curNote] && (midiOctave == 1 || ((mask >> (curNote % 12)) & 1) != 0)
                    && Math.Abs(mc - curNote) * 100 <= Math.Abs(mc - cand) * 100 + hystCents) return curNote;
                return cand;
            }
            int nearest = TuneScales.Nearest(mc, mask);
            if (nearest < 0) return -1;
            if (curNote >= 0 && curNote != nearest && curNote < 128 && ((mask >> (curNote % 12)) & 1) != 0
                && Math.Abs(mc - curNote) * 100 <= Math.Abs(mc - nearest) * 100 + hystCents) return curNote;
            return nearest;
        }

        void Control(in PitchEstimate e, PitchTracker tracker)
        {
            double dt = lastEnd == 0 ? tracker.Hop / sr : (e.EndSample - lastEnd) / sr;
            lastEnd = e.EndSample;
            if (dt <= 0 || dt > 0.1) dt = tracker.Hop / sr;
            double tau = e.Voiced && e.F0 > 0 ? sr / e.F0 : sr / Math.Max(e.RawF0, 100.0);
            long centreT = e.EndSample - (long)((tracker.Window + tau) * 0.5);
            evCentreT = centreT;

            long slot = cCount & ContourMask;
            cT[slot] = centreT;
            bool voiced = e.Voiced && e.F0 > 0;
            if (!voiced)
            {
                unvoicedFor += dt;
                bool silence = e.Rms < SilenceRms;
                if (runActive && !silence && unvoicedFor <= HoldGap && heldF0 > 0)
                {
                    // A gap inside a note (a low-confidence frame, a consonant's edge): keep the last pitch and ratio.
                    cF[slot] = (float)heldF0; cShift[slot] = (float)heldShift;
                    if (evOn && unvoicedFor - dt <= 0) Log(centreT, TuneEventKind.PitchHold, heldShift);
                    cCount++;
                    Display(dt, dispIn0, dispOut0, dispCentre0);
                    return;
                }
                // A real unvoiced sound or silence: the ratio ramps to 1 in 20 ms and the grains give way to the input.
                if (evOn && realFor == 0) Log(centreT, TuneEventKind.VoicedEnd, heldShift);
                evPrevVoiced = false;
                realFor += dt;
                cF[slot] = 0;
                cShift[slot] = (float)(heldShift * Math.Max(0.0, 1.0 - realFor / RampSeconds));
                cCount++;
                Volatile.Write(ref pubVoiced, 0);
                Volatile.Write(ref pubCentre, 0);
                Display(dt, 0, 0, 0);
                if (unvoicedFor > 0.12) runActive = false;
                return;
            }

            double m = e.Midi;
            if (!runActive)
            {
                // A new run (after a real silence or unvoiced stretch): the history, the vibrato and the target start afresh.
                hStart = winFloor = hCount; hBase = m; stepRun = 0;
                ResetVibrato(); exprAbs = 0;
                curNote = pendNote = -1; pendTime = 0; noteTime = 0; smoothInit = false; lfoPhase = 0; devInst = 0;
                runActive = true; firstFrame = true;
                if (evOn) Log(centreT, TuneEventKind.VoicedStart, m);
            }
            unvoicedFor = 0; realFor = 0;
            runTime += dt;
            listener.Add(m, e.Confidence, dt);
            if (source == 1) listenRoot = listener.Estimate(scale);
            int mask = EffectiveMask();
            Volatile.Write(ref pubMask, mask);

            // The window the centre is averaged over: one vibrato period (or two when it is fast), else 50-100 ms.
            double stickN = Math.Max(0.0, (stick - 0.15) / 0.85);
            // No vibrato found (yet): a short window, longer the more the pitch moves about its mean (a vibrato not yet measured must not leak into the centre).
            double wSec = vibrato ? vibPeriod * (vibPeriod < 0.15 ? 2.0 : 1.0) : 0.025 + 0.11 * stickN;
            long age = hCount - hStart;     // frames of this run before the new one
            int win = (int)Math.Max(1, Math.Min(Math.Round(wSec / dt), hCount - Math.Max(hStart, winFloor) + 1));
            win = Math.Min(win, HistSize - 8);

            // 1. append the frame (its deviation against the note in force; the first frame of a run has no note yet)
            int idx = (int)(hCount & HistMask);
            double dev0 = curNote >= 0 ? (m - curNote) * 100 : 0;
            hAbs[idx] = m;
            hDev[idx] = dev0;
            double pa = age > 0 ? csAbs[(int)((hCount - 1) & HistMask)] : 0, pd = age > 0 ? csDev[(int)((hCount - 1) & HistMask)] : 0;
            csAbs[idx] = pa + (m - hBase);
            csDev[idx] = pd + dev0;
            hCount++;
            double MeanAbs(int n)
            {
                long lastI = hCount - 1, firstI = hCount - n;
                double hiS = csAbs[(int)(lastI & HistMask)];
                double loS = firstI - 1 >= hStart ? csAbs[(int)((firstI - 1) & HistMask)] : 0;
                return hBase + (hiS - loS) / n;
            }

            // 2. the target: the note nearest the centre, with the hysteresis and the minimum time
            double mc = MeanAbs(win);
            // A step or a slide in the middle of a vibrato: the pitch has left the vibrato's range around the centre for 25 ms. The centre, a mean over a whole
            // period, would still be on the old note for half a period: it starts afresh from the step, growing back to the period.
            if (vibrato)
            {
                int nS = Math.Max(2, (int)Math.Round(0.025 / dt));
                if (win > 2 * nS && age > 2 * nS)
                {
                    if (Math.Abs(MeanAbs(nS) - mc) * 100 > vibAmp + 100) { if (++stepRun >= nS) { winFloor = hCount - 2 * nS; stepRun = 0; win = 2 * nS; mc = MeanAbs(win); } }
                    else stepRun = 0;
                }
            }
            double hyst = 5 + 50 * stickN + (vibrato ? (hCount - winFloor < vibPeriod / dt ? 20.0 + 0.2 * vibAmp : 20.0) : vibN >= 1 ? Math.Min(30.0, 0.4 * vibSwings[0]) : 0.0);     // 5 ct at Natural 0; plus part of the swing while a vibrato is forming and not yet measured
            double dwell = 0.005 + 0.130 * stickN;
            if (vibrato) dwell = Math.Max(dwell, 0.3 * vibPeriod);
            mcTrack[(int)(hCount & 63)] = mc;
            int back40 = Math.Clamp((int)Math.Round(0.06 / dt), 1, 60);
            double mcSpeed = age > back40 ? Math.Abs(mc - mcTrack[(int)((hCount - back40) & 63)]) * 100 / (back40 * dt) : 0;
            int cand = PickNote(mc, mask, hyst);
            if (curNote < 0 || cand < 0)
            {
                if (cand != curNote) { SwitchNote(cand, 0, win, dt); }
                pendNote = -1; pendTime = 0;
            }
            else if (cand != curNote)
            {
                if (cand == pendNote) pendTime += dt; else { pendNote = cand; pendTime = dt; }
                // A jump of two notes or more waits until the centre has settled (else the note it passes on the way would be picked); neighbours go on at once.
                if (glide > 0.3 && Math.Abs(cand - curNote) >= 2 && mcSpeed > 800) pendTime = 0;      // only the natural settings wait; the hard effect follows at once (NT3 measured waiting at Natural 0: the step delay went from 90 to 150 ms and the notes were not flat, so it stays instant)
                if (pendTime >= dwell) SwitchNote(cand, pendTime, win, dt);
            }
            else { pendNote = -1; pendTime = 0; }
            noteTime += dt;
            if (firstFrame && curNote >= 0) { devInst = (m - curNote) * 100; devSmooth = slowDev = m * 100; firstFrame = false; }
            // the frame just written was measured against the old note: after a switch it is re-expressed in SwitchNote

            // 3. the vibrato: zero crossings of the deviation from its slow mean
            UpdateVibrato(m * 100, dt);      // on the absolute pitch: the target does not have to be right for the vibrato to be found

            // 4. the centre and the shift
            double outMidi = m, shiftSemis;
            double centreAbs = 0;
            if (curNote < 0)
            {
                // No note to go to (an empty scale, no key held): the voice as sung.
                sSmooth = 0; smoothInit = true;
                shiftSemis = 0;
                centreAbs = mc;
            }
            else
            {
                long lastI = hCount - 1, firstI = hCount - win;
                double loD = firstI - 1 >= hStart ? csDev[(int)((firstI - 1) & HistMask)] : 0;
                double c = (csDev[(int)(lastI & HistMask)] - loD) / win;          // centre deviation from the note, cents
                double inst = (m - curNote) * 100;
                devInst = inst;
                exprAbs += (Math.Min(Math.Abs(devInst - c), 90.0) - exprAbs) * (1 - PortableMath.Exp(-dt / 0.25));
                centreAbs = curNote + c / 100.0;
                // the centre's speed (cents per second over the last ~40 ms)
                int back = Math.Clamp((int)Math.Round(0.04 / dt), 1, 60);
                double cNow = curNote * 100.0 + c;
                cAbsTrack[(int)(hCount & 63)] = cNow;
                double speed = hCount - hStart > back ? Math.Abs(cNow - cAbsTrack[(int)((hCount - back) & 63)]) / (back * dt) : 0;
                // the dead band: a centre within a few cents of the note is left alone (Keep high)
                double db = Math.Clamp((vibKeep - 0.5) * 2, 0, 1) * (2.5 + (vibrato ? 0.0 : 0.8 * Math.Max(0.0, exprAbs - 4.0)));    // before the vibrato is measured the centre leaks some of it
                double cEff = Math.Abs(c) <= db ? 0 : c - Math.Sign(c) * db;
                double wz = zoneCents >= 99.5 || target == 1 ? 1.0 : 1.0 - Smooth(zoneCents, zoneCents + 30, Math.Abs(c));
                double wv = 1.0 - glide * Smooth(120, 360, speed);
                double kf = Math.Clamp((vibKeep - 0.5) * 2, 0, 1);
                // Natural: the centre is trusted only once the note has settled. NT2 trusted it after 50-200 ms of a run, and the first, still unsettled centre (a scoop up to the
                // note, a breath) was corrected as if it were the note: a bend of 10 - 20 ct at the very start of a phrase, which the blind test heard as "the tuned effect at the
                // beginning". Now nothing is corrected for the first 250 ms and the correction grows in until 600 ms from Natural 50 up (from 30 it waits a little less: ho).
                double ho = Math.Clamp((humanize - 0.3) / 0.2, 0, 1);
                double wa = 1.0 - Math.Max(kf, ho) * (1.0 - Smooth(0.05 + 0.20 * ho, 0.2 + 0.40 * ho, runTime));
                double corr = -cEff * wz * wv * wa / 100.0;
                double tauEff = retuneSeconds * (1.0 + 3.0 * humanize * Math.Min(1.0, noteTime / 0.4));
                if (corr == 0) tauEff = Math.Min(tauEff, 0.04);     // nothing to correct: the correction is released quickly, so the voice is the input again
                if (!smoothInit || tauEff <= 1e-6) { sSmooth = corr; smoothInit = true; }
                else sSmooth += (corr - sSmooth) * (1 - PortableMath.Exp(-dt / tauEff));
                double flatten = -(1.0 - vibKeep) * (devInst - c) / 100.0;
                double add = 0;
                if (vibDepth > 0)
                {
                    lfoPhase += vibRate * dt;
                    lfoPhase -= Math.Floor(lfoPhase);
                    double ramp = Math.Clamp((noteTime - vibDelay) / 0.3, 0, 1);
                    add = ramp * vibDepth / 100.0 * PortableMath.Sin(2 * Math.PI * lfoPhase);
                }
                // The centre is far from the note (the voice has left it and the new one is not decided yet): the correction lets go, so the
                // voice is as sung while it travels and no large shift (and its artefacts) is made on the way.
                double wj = target == 1 ? 1.0 : 1.0 - glide * Smooth(110, 170, Math.Abs(c));   // a held MIDI note is a target the voice is meant to travel to
                shiftSemis = wj * amount * Math.Clamp(sSmooth + flatten, target == 1 ? -12.0 : -(1.0 + 1.5 * (1 - glide)), target == 1 ? 12.0 : 1.0 + 1.5 * (1 - glide)) + add;
                outMidi = m + shiftSemis;
            }
            if (Math.Abs(shiftSemis) < 0.004) shiftSemis = 0;      // under 0.4 cent: nothing (the bypass: the output is the aligned input)
            double shift = Math.Clamp(shiftSemis + transpose, -48, 48);
            // The ratio may not change faster than SlewRate semitones a second: an instant retune is a ramp of 2 ms per semitone, which still sounds instant, but is not
            // a step in the reader's speed (a step in speed is a step in the period of the next cycle: the cycle before it and the cycle after it are not alike, and that is
            // heard as a tick at every hard note change). The first frame of a run starts from 0 (the ramp from the unvoiced stretch was made above).
            if (evPrevVoiced)
            {
                double maxD = SlewRate * dt;
                shift = heldShift + Math.Clamp(shift - heldShift, -maxD, maxD);
            }
            if (evOn && Math.Abs(shift - heldShift) > 0.12 && heldF0 > 0 && evPrevVoiced) Log(centreT, TuneEventKind.ShiftStep, heldShift, shift);
            cF[slot] = (float)e.F0;
            cShift[slot] = (float)shift;
            cCount++;
            heldF0 = e.F0; heldShift = shift; evPrevVoiced = true;

            Volatile.Write(ref pubInHz, e.F0);
            Volatile.Write(ref pubNote, curNote < 0 ? 0 : curNote);
            Volatile.Write(ref pubCorrection, shiftSemis * 100);
            Volatile.Write(ref pubCentre, centreAbs);
            Volatile.Write(ref pubZone, zoneCents);
            Volatile.Write(ref pubVoiced, 1);
            Display(dt, m, m + shift, centreAbs);
        }

        /// <summary>The target changes: the frames since the change (the minimum time and half the averaging window back) are re-expressed against the new note.</summary>
        void SwitchNote(int cand, double pend, int win, double dt)
        {
            if (curNote >= 0 && cand >= 0)
            {
                double delta = (cand - curNote) * 100.0;
                long back = (long)Math.Ceiling((pend + 0.5 * win * dt) / dt);
                long from = Math.Max(hStart, hCount - back);
                if (hCount - from > HistSize - 8) from = hCount - (HistSize - 8);
                for (long i = from; i < hCount; i++) hDev[(int)(i & HistMask)] -= delta;
                for (long i = from; i < hCount; i++)
                {
                    int k = (int)(i & HistMask);
                    double prev = i > hStart ? csDev[(int)((i - 1) & HistMask)] : 0;
                    csDev[k] = prev + hDev[k];
                }
                devInst -= delta;
            }
            else if (cand >= 0)
            {
                // The first note of a run, or after the target was lost: every frame so far is against it.
                double dd = 0;
                for (long i = hStart; i < hCount; i++)
                {
                    int k = (int)(i & HistMask);
                    hDev[k] = (hAbs[k] - cand) * 100;
                    dd += hDev[k];
                    csDev[k] = dd;
                }
            }
            if (evOn) Log(evCentreT, TuneEventKind.TargetChange, curNote, cand, pend, win);
            curNote = cand;
            pendNote = -1; pendTime = 0;
            noteTime = Math.Max(0, pend);
        }

        /// <summary>
        /// The vibrato, by its extrema (a vibrato is a run of alternating peaks and troughs about a tenth of a second apart, each swing at least 22 cents, whatever the
        /// note it is on; a step or a slide gives one long half-swing and is not one). A peak or trough is confirmed when the pitch has come back from it by 18 cents.
        /// Four alike (in length and size) make a vibrato; the period is twice the mean half-swing. Once found, it stays until two half-swings in a row are out of range or 0.35 s pass with no extremum.
        /// </summary>
        void UpdateVibrato(double y, double dt)
        {
            devSmooth += (y - devSmooth) * 0.6;
            double v = devSmooth;
            const double Delta = 18.0;
            if (vibDir == 0) { vibExt = v; vibExtT = runTime; vibDir = 1; }
            bool confirmed = false;
            if (vibDir > 0)
            {
                if (v > vibExt) { vibExt = v; vibExtT = runTime; }
                else if (vibExt - v >= Delta) { confirmed = true; }
            }
            else
            {
                if (v < vibExt) { vibExt = v; vibExtT = runTime; }
                else if (v - vibExt >= Delta) { confirmed = true; }
            }
            if (confirmed)
            {
                if (vibHaveLast)
                {
                    double half = vibExtT - vibLastT, swing = Math.Abs(vibExt - vibLastV);
                    bool ok = half >= 0.06 && half <= 0.19 && swing >= 22.0 && swing <= 700.0;
                    if (ok)
                    {
                        for (int i = vibPeriods.Length - 1; i > 0; i--) { vibPeriods[i] = vibPeriods[i - 1]; vibSwings[i] = vibSwings[i - 1]; }
                        vibPeriods[0] = half; vibSwings[0] = swing;
                        if (vibN < vibPeriods.Length) vibN++;
                        vibBad = 0;
                    }
                    else if (++vibBad >= 2) { vibN = 0; vibrato = false; }
                    if (vibN >= 4)
                    {
                        // four half-swings alike in length and size: two periods of a vibrato (a scoop and its overshoot are not)
                        double mn = 1e9, mx = 0, sum = 0, smn = 1e9, smx = 0, sw = 0;
                        for (int i = 0; i < 4; i++) { mn = Math.Min(mn, vibPeriods[i]); mx = Math.Max(mx, vibPeriods[i]); sum += vibPeriods[i]; smn = Math.Min(smn, vibSwings[i]); smx = Math.Max(smx, vibSwings[i]); sw += vibSwings[i]; }
                        if (mx / mn < 1.35 && smx / smn < 2.0) { vibrato = true; vibPeriod = 2.0 * sum / 4; vibAmp = 0.5 * sw / 4; }
                    }
                    else if (vibrato && vibN > 0) { vibPeriod = 2.0 * vibPeriods[0] * 0.5 + vibPeriod * 0.5; }
                }
                vibLastT = vibExtT; vibLastV = vibExt; vibHaveLast = true; lastRise = runTime;
                vibDir = -vibDir; vibExt = v; vibExtT = runTime;
            }
            if (vibrato && runTime - lastRise > 0.35) { vibrato = false; vibN = 0; }
        }

        void Display(double dt, double input, double corrected, double centre)
        {
            dispClock += dt;
            dispIn0 = input; dispOut0 = corrected; dispCentre0 = centre;
            while (dispClock >= dispStep)
            {
                dispClock -= dispStep;
                int i = (int)(dispCount & (DisplaySize - 1));
                dispIn[i] = (float)dispIn0;
                dispOut[i] = (float)dispOut0;
                dispCentre[i] = (float)dispCentre0;
                Volatile.Write(ref dispCount, dispCount + 1);
            }
        }
    }
}
