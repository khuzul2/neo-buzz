#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4): smoothers, envelopes and LFOs.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    /// <summary>One-pole parameter smoother (no zipper noise on automation).</summary>
    internal struct Smoother
    {
        public float Value;
        public float Target;
        float coeff;

        public void Setup(double seconds, double sampleRate) => coeff = DspMath.OnePoleCoeff(seconds, sampleRate);

        public void Reset(float v) { Value = Target = v; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Next()
        {
            Value += (Target - Value) * coeff;
            return Value;
        }

        /// <summary>Advances <paramref name="n"/> samples at once (for control-rate values).</summary>
        public float Advance(int n)
        {
            Value += (Target - Value) * (1f - MathF.Pow(1f - coeff, n));
            return Value;
        }

        public bool Settled => MathF.Abs(Target - Value) <= 1e-6f * (1f + MathF.Abs(Target));
    }

    internal enum EnvStage { Idle, Attack, Decay, Sustain, Release }

    /// <summary>
    /// Analog-style ADSR. The attack is an RC charge towards 1.5 (convex like a capacitor),
    /// reaching 1 at exactly the attack time; decay and release are exponential, taking their
    /// time to fall 60 dB (decay: of the distance to the sustain level). Restarts from the current
    /// level, so retriggers never click.
    /// </summary>
    internal sealed class Adsr
    {
        const float AttackTarget = 1.5f;
        static readonly float AttackLn = MathF.Log(AttackTarget / (AttackTarget - 1f));
        static readonly float SixtyDb = MathF.Log(1000f);
        const float IdleLevel = 1e-5f;   // −100 dB

        float sampleRate = 48000;
        float attackCoeff, decayCoeff, releaseCoeff;
        float sustain = 1;

        public EnvStage Stage { get; private set; }
        public float Level { get; private set; }

        public bool IsActive => Stage != EnvStage.Idle;

        public void SetSampleRate(float sr) => sampleRate = sr;

        /// <summary>Times in seconds, sustain 0 … 1.</summary>
        public void Set(float attack, float decay, float sustainLevel, float release)
        {
            // Attack: dL = (T − L)·c per sample, from 0 reaches 1 after attack seconds when
            // (1 − c)^(n) = (T − 1)/T  →  c = 1 − exp(−ln(T/(T−1)) / n).
            attackCoeff = attack <= 0.0002f ? 1f : 1f - MathF.Exp(-AttackLn / (attack * sampleRate));
            decayCoeff = decay <= 0.0002f ? 1f : 1f - MathF.Exp(-SixtyDb / (decay * sampleRate));
            releaseCoeff = release <= 0.0002f ? 1f : 1f - MathF.Exp(-SixtyDb / (release * sampleRate));
            sustain = DspMath.Clamp(sustainLevel, 0f, 1f);
        }

        public void Gate(bool on)
        {
            if (on) Stage = EnvStage.Attack;
            else if (Stage != EnvStage.Idle) Stage = EnvStage.Release;
        }

        public void Reset()
        {
            Stage = EnvStage.Idle;
            Level = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Next()
        {
            switch (Stage)
            {
                case EnvStage.Attack:
                    Level += (AttackTarget - Level) * attackCoeff;
                    if (Level >= 1f)
                    {
                        Level = 1f;
                        Stage = EnvStage.Decay;
                    }
                    break;
                case EnvStage.Decay:
                    Level += (sustain - Level) * decayCoeff;
                    if (MathF.Abs(Level - sustain) < 1e-4f)
                    {
                        Level = sustain;
                        Stage = EnvStage.Sustain;
                    }
                    break;
                case EnvStage.Sustain:
                    Level = sustain;
                    if (sustain <= 0f)
                    {
                        Level = 0;
                        Stage = EnvStage.Idle;
                    }
                    break;
                case EnvStage.Release:
                    Level -= Level * releaseCoeff;
                    if (Level < IdleLevel)
                    {
                        Level = 0;
                        Stage = EnvStage.Idle;
                    }
                    break;
            }
            return Level;
        }

        /// <summary>Advances a block, returning the level at its end (control-rate use).</summary>
        public float Advance(int n)
        {
            for (int i = 0; i < n; i++) Next();
            return Level;
        }
    }

    internal enum LfoShape { Sine, Triangle, SawUp, SawDown, Square, SampleHold, SmoothRandom }

    /// <summary>Low-frequency oscillator, −1 … 1, with delay/fade-in and retrigger.</summary>
    internal sealed class Lfo
    {
        double phase;
        float held, prevHeld, nextHeld;
        Rng rng;
        float fade = 1, fadeInc;
        float delayLeft;

        public Lfo(uint seed = 1) { rng = new Rng(seed); held = rng.NextBipolar(); nextHeld = rng.NextBipolar(); }

        public LfoShape Shape;
        /// <summary>Cycles per second.</summary>
        public float Rate = 1;

        /// <summary>Restarts at <paramref name="startPhase"/>, silent for <paramref name="delay"/> s then fading in over <paramref name="fadeIn"/> s.</summary>
        public void Retrigger(double startPhase, float delay, float fadeIn, float sampleRate)
        {
            phase = startPhase - Math.Floor(startPhase);
            delayLeft = delay * sampleRate;
            fadeInc = fadeIn > 0 ? 1f / (fadeIn * sampleRate) : 1f;
            fade = delay > 0 || fadeIn > 0 ? 0f : 1f;
        }

        public double Phase => phase;

        /// <summary>Advances <paramref name="n"/> samples and returns the value (control rate).</summary>
        public float Advance(int n, float sampleRate)
        {
            double inc = Rate / sampleRate * n;
            phase += inc;
            if (phase >= 1)
            {
                phase -= Math.Floor(phase);
                prevHeld = held;
                held = nextHeld;
                nextHeld = rng.NextBipolar();
            }
            if (delayLeft > 0) delayLeft -= n;
            else if (fade < 1) fade = Math.Min(1f, fade + fadeInc * n);
            return Value() * fade;
        }

        float Value()
        {
            float p = (float)phase;
            switch (Shape)
            {
                case LfoShape.Sine: return MathF.Sin(DspMath.TwoPi * p);
                case LfoShape.Triangle: return p < 0.25f ? 4 * p : p < 0.75f ? 2 - 4 * p : 4 * p - 4;
                case LfoShape.SawUp: return 2 * p - 1;
                case LfoShape.SawDown: return 1 - 2 * p;
                case LfoShape.Square: return p < 0.5f ? 1 : -1;
                case LfoShape.SampleHold: return held;
                default:
                {
                    // Cosine interpolation between random points.
                    float t = 0.5f - 0.5f * MathF.Cos(DspMath.Pi * p);
                    return prevHeld + (held - prevHeld) * t;
                }
            }
        }
    }
}
