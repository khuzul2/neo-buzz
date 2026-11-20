// Synthetic instruments and drum loops for Neo Audio to MIDI's tests (docs/specs/audio-to-midi.md §7): a Karplus-Strong string with plucks, re-plucks,
// hammer-ons and slides (a guitar, a bass), a saw or sine lead with vibrato, and drum loops at a given tempo with ghost notes. Each comes with its truth:
// the notes (time, MIDI note) or the hits (time, class). Deterministic (SynthRng with a seed). Tests may use the C# maths.

using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    internal static class AudioToMidiSignals
    {
        public const double Rate = 48000;

        public enum Kind { Pluck, Replay, Hammer, Slide }
        public sealed record Ev(double T, double Midi, Kind Kind, double Amp = 1, double Glide = 0.06);

        static double Hz(double midi) => 440 * Math.Pow(2, (midi - 69) / 12.0);

        /// <summary>
        /// One Karplus-Strong string. Pluck and Replay refill the loop with a low-passed noise burst (Replay is a pluck of the pitch already sounding),
        /// Hammer changes the loop length at once and adds a small thump, Slide glides the length over <see cref="Ev.Glide"/> seconds.
        /// Truth: the note of each pluck, replay and hammer at its time; of a slide at the end of its glide.
        /// </summary>
        public static (float[] X, List<(double T, int Note)> Truth) String(double sr, IList<Ev> evs, double seconds, ulong seed, double decay = 0.9975, double noiseDb = -66, double level = 0.5)
        {
            var rng = new SynthRng(seed);
            int n = (int)(seconds * sr);
            var x = new float[n];
            const int cap = 1 << 16;
            var buf = new double[cap];
            long w = 0;
            double midi = evs.Count > 0 ? evs[0].Midi : 40;
            int ei = 0;
            double slideFrom = midi, slideTo = midi, slideT0 = 0, slideT1 = -1;
            var truth = new List<(double, int)>();
            double lp = 0;
            double na = Math.Pow(10, noiseDb / 20);
            for (int i = 0; i < n; i++)
            {
                double t = i / sr;
                while (ei < evs.Count && evs[ei].T <= t)
                {
                    var e = evs[ei++];
                    double len = sr / Hz(midi) - 0.5;
                    switch (e.Kind)
                    {
                        case Kind.Pluck:
                        case Kind.Replay:
                            {
                                if (e.Kind == Kind.Pluck) { midi = e.Midi; slideT1 = -1; }
                                len = sr / Hz(midi) - 0.5;
                                int n0 = (int)Math.Ceiling(len) + 2;
                                double ss = 0;
                                var tmp = new double[n0];
                                for (int k = 0; k < n0; k++) { lp += 0.6 * (rng.Gauss() - lp); tmp[k] = lp; ss += lp * lp; }
                                double sc = e.Amp / Math.Sqrt(ss / n0 + 1e-12);
                                for (int k = 0; k < n0; k++) buf[(w - n0 + k) & (cap - 1)] = tmp[k] * sc;
                                truth.Add((e.T, (int)Math.Round(midi)));
                                break;
                            }
                        case Kind.Hammer:
                            {
                                midi = e.Midi; slideT1 = -1;
                                len = sr / Hz(midi) - 0.5;
                                int n0 = (int)Math.Ceiling(len) + 2;
                                for (int k = 0; k < n0; k++) buf[(w - n0 + k) & (cap - 1)] += 0.25 * e.Amp * rng.Gauss() * 0.3;
                                truth.Add((e.T, (int)Math.Round(midi)));
                                break;
                            }
                        default:
                            slideFrom = midi; slideTo = e.Midi; slideT0 = e.T; slideT1 = e.T + e.Glide;
                            truth.Add((e.T + e.Glide, (int)Math.Round(e.Midi)));
                            break;
                    }
                }
                if (slideT1 > 0)
                {
                    double u = Math.Clamp((t - slideT0) / (slideT1 - slideT0), 0, 1);
                    midi = slideFrom + (slideTo - slideFrom) * u;
                    if (u >= 1) slideT1 = -1;
                }
                double l = sr / Hz(midi) - 0.5;
                double rp = w - l;
                long ri = (long)Math.Floor(rp);
                double fr = rp - ri;
                double a = buf[ri & (cap - 1)] * (1 - fr) + buf[(ri + 1) & (cap - 1)] * fr;
                double b = buf[(ri - 1) & (cap - 1)] * (1 - fr) + buf[ri & (cap - 1)] * fr;
                double v = 0.5 * (a + b) * decay;
                buf[w & (cap - 1)] = v;
                w++;
                x[i] = (float)v;
            }
            // Scale so the loudest pluck peaks at `level`, then the room noise.
            double peak = 1e-9;
            foreach (float s in x) peak = Math.Max(peak, Math.Abs(s));
            double g = level / peak;
            for (int i = 0; i < n; i++) x[i] = (float)(x[i] * g + na * rng.Gauss());
            return (x, truth);
        }

        /// <summary>
        /// A guitar line over E2 (40) to E5 (76): about <paramref name="count"/> events a bar apart of 0.35 to 0.6 s: plucks of a new note (65 %), replays of the same one (12 %),
        /// hammer-ons two to four semitones up (11 %) and slides of two to five semitones (12 %).
        /// </summary>
        public static (float[] X, List<(double T, int Note)> Truth) GuitarLine(ulong seed, int count = 48, int lo = 40, int hi = 76, double decay = 0.9975, double gapLo = 0.35, double gapHi = 0.6, bool slides = true, bool hammers = true)
        {
            var rng = new SynthRng(seed);
            var evs = new List<Ev>();
            double t = 0.3;
            double cur = lo + rng.Next() * (hi - lo);
            cur = Math.Round(cur);
            evs.Add(new Ev(t, cur, Kind.Pluck, rng.Range(0.5, 1)));
            for (int i = 1; i < count; i++)
            {
                t += rng.Range(gapLo, gapHi);
                double r = rng.Next();
                if (r < 0.12) evs.Add(new Ev(t, cur, Kind.Replay, rng.Range(0.5, 1)));
                else if (r < 0.23 && hammers && cur + 4 <= hi) { double up = cur + Math.Round(rng.Range(2, 4)); evs.Add(new Ev(t, up, Kind.Hammer, rng.Range(0.5, 1))); cur = up; }
                else if (r < 0.35 && slides)
                {
                    double d = Math.Round(rng.Range(2, 5)) * (rng.Next() < 0.5 ? 1 : -1);
                    double to = Math.Clamp(cur + d, lo, hi);
                    if (to == cur) to = cur + 2;
                    evs.Add(new Ev(t, to, Kind.Slide, 1, 0.06)); cur = to;
                }
                else
                {
                    double step = Math.Round(rng.Range(-7, 8));
                    cur = Math.Clamp(cur + step, lo, hi);
                    if (step == 0) cur = Math.Clamp(cur + 3, lo, hi);
                    evs.Add(new Ev(t, cur, Kind.Pluck, rng.Range(0.5, 1)));
                }
            }
            return String(Rate, evs, t + 1.2, seed + 100, decay);
        }

        /// <summary>A bass line over E1 (28) to G3 (55): plucks and some replays, 0.5 to 0.75 s apart.</summary>
        public static (float[] X, List<(double T, int Note)> Truth) BassLine(ulong seed, int count = 40) =>
            GuitarLine(seed, count, 28, 55, 0.9988, 0.5, 0.75, slides: false, hammers: false);

        /// <summary>
        /// A lead: a saw (or sine) with 5.5 Hz vibrato of ±25 cents, notes 0.3 to 0.5 s long, 40 ms apart (a few legato: no gap); the truth is each note's start.
        /// </summary>
        public static (float[] X, List<(double T, int Note)> Truth) Lead(ulong seed, int count = 36, bool saw = true, int lo = 55, int hi = 84)
        {
            var rng = new SynthRng(seed);
            var parts = new List<float>();
            var truth = new List<(double, int)>();
            parts.AddRange(new float[(int)(0.3 * Rate)]);
            int cur = lo + (int)(rng.Next() * (hi - lo));
            for (int i = 0; i < count; i++)
            {
                int step = (int)Math.Round(rng.Range(-7, 8));
                if (step == 0) step = 2;
                cur = Math.Clamp(cur + step, lo, hi);
                double len = rng.Range(0.3, 0.5);
                double f = Hz(cur);
                double vibPhase = rng.Next() * 6.28;
                var src = PitchSignals.Harmonic(Rate, t => f * Math.Pow(2, 25.0 / 1200 * Math.Sin(2 * Math.PI * 5.5 * t + vibPhase) * Math.Min(1, t / 0.15)), len,
                    h => saw ? 1.0 / h : (h == 1 ? 1 : 0.0), "lead", 0.4 * rng.Range(0.6, 1));
                var x = src.X;
                int att = (int)(0.008 * Rate), rel = (int)(0.03 * Rate);
                for (int k = 0; k < x.Length; k++)
                {
                    double env = Math.Min(1.0, k / (double)att) * Math.Min(1.0, (x.Length - k) / (double)rel);
                    x[k] = (float)(x[k] * env);
                }
                truth.Add((parts.Count / Rate, cur));
                parts.AddRange(x);
                bool legato = rng.Next() < 0.15;
                if (!legato) parts.AddRange(new float[(int)(0.04 * Rate)]);
            }
            parts.AddRange(new float[(int)(0.5 * Rate)]);
            var all = parts.ToArray();
            var rn = new SynthRng(seed + 7);
            double na = Math.Pow(10, -66.0 / 20);
            for (int i = 0; i < all.Length; i++) all[i] += (float)(na * rn.Gauss());
            return (all, truth);
        }

        /// <summary>
        /// A drum loop at <paramref name="bpm"/> for <paramref name="bars"/> bars of 4/4 on a sixteenth grid: kick on 1, the "and" of 2 and 3 (and sometimes before 1),
        /// snare on 2 and 4 with ghost notes (a fifth of the level) on the sixteenth before the backbeat, hats on the eighths (sixteenths from 140 bpm).
        /// The truth is (sample, class) with 0 kick, 1 snare, 2 hat; ghost notes are in it.
        /// </summary>
        public static (float[] X, List<(long Sample, int Class, bool Ghost)> Truth) DrumLoop(double bpm, int bars, ulong seed, bool ghosts = true, double noiseDb = -60)
        {
            var rng = new SynthRng(seed);
            double step = 60.0 / bpm / 4;
            var hits = new List<(double T, int Cls, double Level, bool Ghost)>();
            double t0 = 0.3;
            for (int bar = 0; bar < bars; bar++)
            {
                for (int s = 0; s < 16; s++)
                {
                    double t = t0 + (bar * 16 + s) * step + rng.Range(-0.002, 0.002);
                    if (s == 0 || s == 6 || s == 8 || (s == 14 && rng.Next() < 0.4) || (s == 10 && rng.Next() < 0.3)) hits.Add((t, 0, rng.Range(0.5, 0.7), false));
                    if (s == 4 || s == 12) hits.Add((t, 1, rng.Range(0.45, 0.65), false));
                    else if (ghosts && (s == 3 || s == 11 || s == 15 && rng.Next() < 0.5)) hits.Add((t, 1, rng.Range(0.10, 0.14), true));
                    bool hatStep = bpm >= 140 ? true : s % 2 == 0;
                    if (hatStep) hits.Add((t, 2, rng.Range(0.18, 0.32) * (s % 4 == 0 ? 1.2 : 1.0), false));
                }
            }
            double end = t0 + bars * 16 * step + 0.8;
            var x = new float[(int)(end * Rate)];
            double na = Math.Pow(10, noiseDb / 20);
            for (int i = 0; i < x.Length; i++) x[i] = (float)(na * rng.Gauss());
            var truth = new List<(long, int, bool)>();
            foreach (var h in hits)
            {
                long at = (long)(h.T * Rate);
                if (h.Cls == 1)
                {
                    // A snare with a body: a 190 Hz shell tone under the wires' noise (the shared PitchSignals snare is nearly all noise).
                    PitchSignals.Tone(x, (int)at, Rate, rng.Range(180, 230), rng.Range(170, 200), 0.01, 0.07, h.Level * 2.2, (int)(0.25 * Rate), rng);
                    PitchSignals.Burst(x, (int)at, Rate, rng, rng.Range(1500, 2500), rng.Range(5500, 7500), rng.Range(0.05, 0.09), h.Level * 0.5, (int)(0.3 * Rate));
                }
                else PitchSignals.Hit(x, (int)at, Rate, h.Cls, rng, h.Level);
                truth.Add((at, h.Cls, h.Ghost));
            }
            return (x, truth);
        }
    }
}
