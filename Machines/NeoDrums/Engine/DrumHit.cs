#nullable disable
// Neo Drums (docs/specs/machines.md §5): one hit of one voice, from its first sample to silence. A voice has three of
// these, so a hit that comes while the last one sounds starts on its own while the old one fades out (3 ms; a choke
// 5 ms). Everything a hit needs is set when it starts (its tune, decay and tone latch); per sample there are only
// multiplications, the sines of a phase and the filters. Every hit of a voice with the same settings and velocity is
// the same, sample for sample: its noise is seeded and its oscillators start at fixed phases. Pure; no allocation
// after construction.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Drums
{
    internal enum DrumVoice { Kick, Snare, Clap, Rim, ClosedHat, OpenHat, LowTom, MidTom, HighTom, Cowbell, Crash, Ride }

    /// <summary>What a hit latches when it starts.</summary>
    internal struct HitSettings
    {
        /// <summary>Hz (kick, snare, clap, rim, toms, cowbell) or semitones (hats, crash, ride).</summary>
        public double Tune;
        /// <summary>Seconds to fall 60 dB.</summary>
        public double Decay;
        /// <summary>Hz (snare, hats, crash, ride) or 0 … 1 (toms' attack noise).</summary>
        public double Tone;
        /// <summary>Kick: sweep (semitones), sweep time (s), click (0 … 1), drive (dB), punch (0 … 1). Snare: snap (0 … 1). Clap: spread (s).</summary>
        public double A, B, C, D, E;
        /// <summary>The hit's gain (level, velocity, accent, the voice's own scale).</summary>
        public float Gain;
        /// <summary>How far velocity and accent open the hit: 0 at velocity 100 without accent.</summary>
        public float Bright;
    }

    internal sealed class DrumHit
    {
        const double Ln1000 = 6.907755278982137;
        const int MaxPhases = 3;

        readonly MetalBank metal = new MetalBank();
        readonly double[] hz = new double[MetalBank.Max];
        readonly double[] sinePhase = new double[MaxPhases];
        DrumVoice voice;
        double fs = 48000;
        bool active;

        // Fading out (retrigger, choke, panic): the gain falls to 0 in a straight line.
        float fade = 1f, fadeStep;
        float gain;
        long age;

        // Oscillators and their envelopes.
        double baseHz, sweepOct, sweep, sweepK, ratio, sweep2Oct, sweep2, sweep2K;
        double env1, k1, env2, k2, env3, k3, env4, k4;
        float mix1, mix3;
        float attack, attackStep;
        float clickLevel, noiseLevel, metalLevel, bodyLevel;
        float driveGain, driveNorm;
        bool drive;
        Rng rng;
        Svf f1, f2, f3;
        OnePole wide;
        double punch, punchK;

        // Clap: its bursts.
        int burstEvery, burstsLeft, untilBurst, tailAt;
        double burstK;

        public bool IsActive => active;
        /// <summary>Fading out (a newer hit took over, or a choke).</summary>
        public bool Fading => active && fadeStep > 0;
        public long Age => age;
        /// <summary>The last sample it gave (so a hit cut short can hand it to the declicker).</summary>
        public float Last { get; private set; }

        public void SetSampleRate(double sampleRate)
        {
            fs = sampleRate;
            active = false;
        }

        /// <summary>Fades out over <paramref name="seconds"/>; a hit already fading keeps the faster of the two.</summary>
        public void FadeOut(double seconds)
        {
            if (!active) return;
            float step = (float)(1.0 / Math.Max(1.0, seconds * fs));
            if (step > fadeStep) fadeStep = step;
        }

        /// <summary>Stops at once (the caller takes its last sample into the declicker).</summary>
        public void Kill() => active = false;

        static double Decay(double seconds, double sampleRate) => PortableMath.Exp(-Ln1000 / (Math.Max(1e-4, seconds) * sampleRate));

        static float Ramp(double seconds, double sampleRate) => (float)(1.0 / Math.Max(1.0, seconds * sampleRate));

        /// <summary>Starts a hit of <paramref name="v"/> with <paramref name="s"/>; <paramref name="seed"/> seeds its noise.</summary>
        public void Begin(DrumVoice v, in HitSettings s, uint seed)
        {
            voice = v;
            active = true;
            fade = 1f;
            fadeStep = 0f;
            age = 0;
            gain = s.Gain;
            Last = 0;
            rng = new Rng(seed);
            f1.Reset();
            f2.Reset();
            f3.Reset();
            wide.Reset();
            punch = 0;
            for (int i = 0; i < MaxPhases; i++) sinePhase[i] = 0;
            env1 = env2 = env3 = 0;
            sweep = sweep2 = 0;
            attack = 0f;
            attackStep = Ramp(0.0003, fs);
            drive = false;
            float b = s.Bright;
            float rate = (float)fs;
            double top = 0.45 * fs;
            void Set(ref Svf f, SvfMode mode, double hz, double q)
            {
                f.Mode = mode;
                f.SetFast(Svf.Prewarp((float)Math.Min(top, hz), rate), (float)(1 / q));
            }
            switch (v)
            {
                case DrumVoice.Kick:
                {
                    // A sine falling from Sweep above its tune (a TR-909's), a click of high-passed noise, drive on the body.
                    baseHz = s.Tune;
                    sweepOct = Math.Max(0, s.A / 12.0 * (1 + 0.5 * b));
                    sweep = 1;
                    sweepK = PortableMath.Exp(-1.0 / (Math.Max(1e-4, s.B) * fs));
                    env1 = 1; k1 = Decay(s.Decay, fs);
                    // Punch: the body starts up to 4 times (+12 dB) louder, falling back with the sweep.
                    punch = 3 * s.E; punchK = sweepK;
                    env2 = 1; k2 = Decay(0.015, fs);
                    clickLevel = (float)Math.Min(1.5, s.C * PortableMath.Exp2(2 * b)) * 0.35f;
                    attackStep = Ramp(0.0001, fs);
                    double g = DspMath.DbToGain(s.D);
                    drive = s.D > 0.05;
                    driveGain = (float)g;
                    driveNorm = drive ? (float)(1.0 / PortableMath.Tanh(g)) : 1f;
                    Set(ref f1, SvfMode.HighPass, 900, 0.7);
                    Set(ref f2, SvfMode.LowPass, 6000, 0.7);
                    break;
                }
                case DrumVoice.Snare:
                {
                    // Two tones an octave apart, falling 8 st (a TR-909's: most of it in 15 ms), and the snappy noise: a band round Tone, and some across the mids.
                    baseHz = s.Tune;
                    ratio = 2.0;
                    sweepOct = 8.0 / 12;
                    sweep = 1;
                    sweepK = PortableMath.Exp(-1.0 / (0.014 * fs));
                    env1 = 1; k1 = Decay(0.6 * s.Decay + 0.04, fs);
                    env2 = 1; k2 = Decay(0.4 * s.Decay + 0.02, fs);
                    env3 = 1; k3 = Decay(s.Decay, fs);
                    double snap = Math.Min(1, s.A * PortableMath.Exp2(1.5 * b));
                    bodyLevel = (float)(1 - 0.5 * snap);
                    noiseLevel = (float)(0.6 * snap);
                    double tone = s.Tone * PortableMath.Exp2(b);
                    Set(ref f1, SvfMode.HighPass, 0.55 * tone, 0.7);
                    Set(ref f3, SvfMode.LowPass, 1.3 * tone, 0.7);
                    Set(ref f2, SvfMode.HighPass, 600, 0.7);
                    break;
                }
                case DrumVoice.Clap:
                {
                    // Noise in three bursts and a tail, through a band-pass (and a little of it above).
                    double centre = s.Tune * PortableMath.Exp2(0.5 * b);
                    Set(ref f1, SvfMode.BandPass, centre, 0.8);
                    Set(ref f2, SvfMode.HighPass, 3 * centre, 0.7);
                    burstEvery = Math.Max(1, (int)Math.Round(s.A * fs));
                    burstsLeft = 2;
                    untilBurst = burstEvery;
                    tailAt = 2 * burstEvery;
                    env1 = 1; burstK = Decay(1.3 * s.A, fs);
                    env2 = 0; k2 = Decay(s.Decay, fs);
                    attackStep = Ramp(0.0002, fs);
                    break;
                }
                case DrumVoice.Rim:
                {
                    // Three pings (a TR-909's rim shot: Tune, half and 0.22 of it) and a tick.
                    baseHz = s.Tune;
                    env1 = 1; k1 = Decay(s.Decay, fs);
                    env2 = 1; k2 = Decay(0.8 * s.Decay, fs);
                    env3 = 1; k3 = Decay(s.Decay, fs);
                    env4 = 1; k4 = Decay(0.006, fs);
                    Set(ref f1, SvfMode.BandPass, 2000, 0.5);
                    break;
                }
                case DrumVoice.ClosedHat:
                case DrumVoice.OpenHat:
                case DrumVoice.Crash:
                case DrumVoice.Ride:
                {
                    // The TR-808's six square waves and noise through a band round Tone, some of the noise across (a cymbal's),
                    // then a low-pass.
                    double t = PortableMath.Exp2(s.Tune / 12.0);
                    double scale = v == DrumVoice.Ride ? 1.25 : 1.0;
                    for (int i = 0; i < MetalBank.Max; i++) hz[i] = MetalBank.Tr808[i] * t * scale;
                    metal.Start(hz, MetalBank.StartPhases, MetalBank.Max, fs);
                    double tone = s.Tone * PortableMath.Exp2(b);
                    env1 = 1; k1 = Decay(s.Decay, fs);
                    if (v == DrumVoice.ClosedHat || v == DrumVoice.OpenHat)
                    {
                        Set(ref f1, SvfMode.BandPass, tone, 0.8);
                        wide.Set(300, fs);
                        Set(ref f2, SvfMode.LowPass, 16000, 0.7);
                        metalLevel = 0.6f;
                        noiseLevel = MetalOnly ? 0f : 0.8f;
                        mix1 = 1f; mix3 = 0.35f;
                        attackStep = Ramp(0.0002, fs);
                    }
                    else if (v == DrumVoice.Crash)
                    {
                        Set(ref f1, SvfMode.BandPass, tone, 0.5);
                        wide.Set(250, fs);
                        Set(ref f2, SvfMode.LowPass, 8000, 0.7);
                        metalLevel = 0.5f;
                        noiseLevel = MetalOnly ? 0f : 1f;
                        mix1 = 1f; mix3 = 0.25f;
                        attackStep = Ramp(0.001, fs);
                    }
                    else
                    {
                        Set(ref f1, SvfMode.BandPass, tone, 1.0);
                        wide.Set(300, fs);
                        Set(ref f2, SvfMode.LowPass, 11000, 0.7);
                        metalLevel = 0.8f;
                        noiseLevel = MetalOnly ? 0f : 0.5f;
                        mix1 = 1f; mix3 = 0.3f;
                        attackStep = Ramp(0.0005, fs);
                    }
                    break;
                }
                case DrumVoice.LowTom:
                case DrumVoice.MidTom:
                case DrumVoice.HighTom:
                {
                    // A sine (and its octave, quieter) falling as a TR-909's tom does: an octave fast, then slowly, to its tune.
                    baseHz = s.Tune;
                    sweepOct = 1.0 * (1 + 0.5 * b);
                    sweep = 1;
                    sweepK = PortableMath.Exp(-1.0 / (0.012 * fs));
                    sweep2Oct = 0.2;
                    sweep2 = 1;
                    sweep2K = PortableMath.Exp(-1.0 / (0.3 * fs));
                    env1 = 1; k1 = Decay(s.Decay, fs);
                    env2 = 1; k2 = Decay(0.06, fs);
                    noiseLevel = (float)(Math.Min(1.5, s.Tone * PortableMath.Exp2(1.5 * b)) * 0.5);
                    Set(ref f3, SvfMode.LowPass, 4000, 0.7);
                    attackStep = Ramp(0.006, fs);
                    break;
                }
                case DrumVoice.Cowbell:
                {
                    // The TR-808's two square waves (1 : 1.48) through a band-pass, a fast attack and a tail.
                    hz[0] = s.Tune;
                    hz[1] = s.Tune * 1.48;
                    metal.Start(hz, MetalBank.StartPhases, 2, fs);
                    env1 = 1; k1 = Decay(0.05, fs);
                    env2 = 1; k2 = Decay(s.Decay, fs);
                    Set(ref f1, SvfMode.BandPass, 3.3 * s.Tune, 1.0);
                    attackStep = Ramp(0.0002, fs);
                    break;
                }
            }
        }

        /// <summary>For tests: the hats and cymbals without their noise (the oscillators alone are what could alias).</summary>
        internal static bool MetalOnly;

        /// <summary>sin(2π p), from a polynomial on a quarter wave (IEEE basic operations only: the same bits everywhere).</summary>
        internal static double Sin2Pi(double p)
        {
            p -= Math.Floor(p);
            double x = p < 0.25 ? p : p < 0.75 ? 0.5 - p : p - 1.0;
            double t = 2 * Math.PI * x, t2 = t * t;
            return t * (1 + t2 * (-1.0 / 6 + t2 * (1.0 / 120 + t2 * (-1.0 / 5040 + t2 * (1.0 / 362880 + t2 * (-1.0 / 39916800 + t2 * (1.0 / 6227020800.0)))))));
        }

        /// <summary>Adds the hit's next <paramref name="n"/> samples into <paramref name="bus"/> (mono). Whether it still sounds.</summary>
        public bool Render(float[] bus, int n)
        {
            if (!active) return false;
            switch (voice)
            {
                case DrumVoice.Kick: RenderKick(bus, n); break;
                case DrumVoice.Snare: RenderSnare(bus, n); break;
                case DrumVoice.Clap: RenderClap(bus, n); break;
                case DrumVoice.Rim: RenderRim(bus, n); break;
                case DrumVoice.LowTom:
                case DrumVoice.MidTom:
                case DrumVoice.HighTom: RenderTom(bus, n); break;
                case DrumVoice.Cowbell: RenderCowbell(bus, n); break;
                default: RenderMetal(bus, n); break;
            }
            age += n;
            return active;
        }

        /// <summary>The fade's gain for the next sample; false once it reaches zero (the hit ends).</summary>
        bool Fade(out float g)
        {
            g = fade;
            if (fadeStep <= 0) return true;
            fade -= fadeStep;
            if (fade > 0) return true;
            fade = 0;
            active = false;
            return false;
        }

        /// <summary>Below this (−90 dB) a hit has ended: it stops on that sample, whatever the blocks.</summary>
        const double EndLevel = 3e-5;

        void RenderKick(float[] bus, int n)
        {
            double inv = 1.0 / fs, top = 0.45 * fs;
            float y = Last;
            for (int i = 0; i < n; i++)
            {
                if (!Fade(out float fg)) { y = 0; break; }
                double f = baseHz * PortableMath.Exp2(sweepOct * sweep);
                if (f > top) f = top;
                sweep *= sweepK;
                sinePhase[0] += f * inv;
                if (sinePhase[0] >= 1) sinePhase[0] -= 1;
                float body = (float)Sin2Pi(sinePhase[0]);
                if (drive) body = (float)PortableMath.Tanh(driveGain * body) * driveNorm;
                if (attack < 1f) { attack += attackStep; if (attack > 1f) attack = 1f; }
                float click = env2 > 1e-6 ? f2.Process(f1.Process(rng.NextBipolar())) * (float)env2 * clickLevel * attack : 0f;
                y = (body * (float)(env1 * (1 + punch)) + click) * gain * fg;
                bus[i] += y;
                env1 *= k1;
                env2 *= k2;
                punch *= punchK;
                if (env1 < EndLevel) { active = false; break; }
            }
            Last = y;
        }

        void RenderSnare(float[] bus, int n)
        {
            double inv = 1.0 / fs;
            float y = Last;
            for (int i = 0; i < n; i++)
            {
                if (!Fade(out float fg)) { y = 0; break; }
                double f = baseHz * PortableMath.Exp2(sweepOct * sweep);
                sweep *= sweepK;
                sinePhase[0] += f * inv;
                if (sinePhase[0] >= 1) sinePhase[0] -= 1;
                sinePhase[1] += f * ratio * inv;
                if (sinePhase[1] >= 1) sinePhase[1] -= 1;
                float body = (float)(Sin2Pi(sinePhase[0]) * env1 + 0.55 * Sin2Pi(sinePhase[1]) * env2);
                if (attack < 1f) { attack += attackStep; if (attack > 1f) attack = 1f; }
                float white = rng.NextBipolar();
                float noise = (f3.Process(f1.Process(white)) + 0.45f * f2.Process(white)) * (float)env3 * attack;
                y = (body * bodyLevel + noise * noiseLevel) * gain * fg;
                bus[i] += y;
                env1 *= k1;
                env2 *= k2;
                env3 *= k3;
                if (env1 < EndLevel && env3 < EndLevel) { active = false; break; }
            }
            Last = y;
        }

        void RenderClap(float[] bus, int n)
        {
            float y = Last;
            for (int i = 0; i < n; i++)
            {
                if (!Fade(out float fg)) { y = 0; break; }
                if (burstsLeft > 0 && --untilBurst <= 0)
                {
                    burstsLeft--;
                    untilBurst = burstEvery;
                    env1 = 1;
                }
                if (tailAt > 0 && --tailAt == 0) env2 = 0.55;
                if (attack < 1f) { attack += attackStep; if (attack > 1f) attack = 1f; }
                float x = rng.NextBipolar() * (float)(env1 + env2) * attack;
                y = (f1.Process(x) + 0.05f * f2.Process(x)) * gain * fg;
                bus[i] += y;
                env1 *= burstK;
                env2 *= k2;
                if (burstsLeft == 0 && tailAt == 0 && env1 < EndLevel && env2 < EndLevel) { active = false; break; }
            }
            Last = y;
        }

        void RenderRim(float[] bus, int n)
        {
            double inv = 1.0 / fs;
            float y = Last;
            for (int i = 0; i < n; i++)
            {
                if (!Fade(out float fg)) { y = 0; break; }
                sinePhase[0] += baseHz * inv;
                if (sinePhase[0] >= 1) sinePhase[0] -= 1;
                sinePhase[1] += baseHz * 0.5 * inv;
                if (sinePhase[1] >= 1) sinePhase[1] -= 1;
                sinePhase[2] += baseHz * 0.233 * inv;
                if (sinePhase[2] >= 1) sinePhase[2] -= 1;
                if (attack < 1f) { attack += attackStep; if (attack > 1f) attack = 1f; }
                float tick = env4 > 1e-6 ? f1.Process(rng.NextBipolar()) * (float)env4 * 1.6f * attack : 0f;
                float x = (float)(Sin2Pi(sinePhase[0]) * env1 + 0.8 * Sin2Pi(sinePhase[1]) * env2 + 1.0 * Sin2Pi(sinePhase[2]) * env3) + tick;
                y = x * gain * fg;
                bus[i] += y;
                env1 *= k1;
                env2 *= k2;
                env3 *= k3;
                env4 *= k4;
                if (env1 < EndLevel && env3 < EndLevel) { active = false; break; }
            }
            Last = y;
        }

        void RenderTom(float[] bus, int n)
        {
            double inv = 1.0 / fs;
            float y = Last;
            for (int i = 0; i < n; i++)
            {
                if (!Fade(out float fg)) { y = 0; break; }
                double f = baseHz * PortableMath.Exp2(sweepOct * sweep + sweep2Oct * sweep2);
                sweep *= sweepK;
                sweep2 *= sweep2K;
                sinePhase[0] += f * inv;
                if (sinePhase[0] >= 1) sinePhase[0] -= 1;
                if (attack < 1f) { attack += attackStep; if (attack > 1f) attack = 1f; }
                float noise = env2 > 1e-6 ? f3.Process(rng.NextBipolar()) * (float)env2 * noiseLevel * attack : 0f;
                double body = Sin2Pi(sinePhase[0]) + 0.25 * Sin2Pi(2 * sinePhase[0]);
                y = ((float)(body * env1) + noise) * gain * fg;
                bus[i] += y;
                env1 *= k1;
                env2 *= k2;
                if (env1 < EndLevel) { active = false; break; }
            }
            Last = y;
        }

        void RenderCowbell(float[] bus, int n)
        {
            float y = Last;
            for (int i = 0; i < n; i++)
            {
                if (!Fade(out float fg)) { y = 0; break; }
                if (attack < 1f) { attack += attackStep; if (attack > 1f) attack = 1f; }
                float m = metal.Next();
                float x = (f1.Process(m) + 0.3f * m) * (float)(0.75 * env1 + 0.25 * env2) * attack;
                y = x * gain * fg;
                bus[i] += y;
                env1 *= k1;
                env2 *= k2;
                if (env1 < EndLevel && env2 < EndLevel) { active = false; break; }
            }
            Last = y;
        }

        void RenderMetal(float[] bus, int n)
        {
            float y = Last;
            for (int i = 0; i < n; i++)
            {
                if (!Fade(out float fg)) { y = 0; break; }
                if (attack < 1f) { attack += attackStep; if (attack > 1f) attack = 1f; }
                float noise = noiseLevel > 0 ? rng.NextBipolar() * noiseLevel : 0f;
                float x = metal.Next() * metalLevel + noise;
                float z = f2.Process(f1.Process(x) * mix1 + wide.HighPass(noise) * mix3);
                y = z * (float)env1 * attack * gain * fg;
                bus[i] += y;
                env1 *= k1;
                if (env1 < EndLevel) { active = false; break; }
            }
            Last = y;
        }
    }
}
