#nullable disable
// Neo Organ (docs/specs/neo-organ.md): a tonewheel organ. 91 sine wheels run all the time (the cost does not depend on how many keys
// are held); the keys only decide, per wheel, how loud it is in the sum: coefficient[wheel] = sum over drawbars of gain · (keys that
// switch that wheel onto that bus). Coefficients are recomputed every 16 samples when something moved and ramp linearly in between.
// Then: scanner vibrato/chorus, key click, expression, overdrive, rotary speaker, volume. Notes come from tracks and MIDI (any
// number of keys; velocity is ignored, as on the instrument). Pure.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Organ
{
    internal sealed class OrganEngine : INoteSink
    {
        public const int MaxBlock = 256;
        public const int Tick = 16;
        public const float ToneGain = 0.12f;
        const float LeakPerContact = 0.0015f;
        // The key click (docs/specs/neo-organ.md §3): a contact transient, not a burst of white noise. A key closing starts 2 to 4
        // contact bounces at random times (within about 3 ms) and levels; each is a smooth (Hann) pulse of 1.2 to 2.5 ms that
        // shapes its own noise, and a band-pass keeps the sum between about 1.2 and 3.5 kHz (a 4th-order 1.2 kHz high-pass, an
        // 8th-order 3.5 kHz low-pass: -48 dB at 6 kHz, nothing above 8 kHz). Its level is the key's own level (its drawbars) times
        // ClickAtFull times the square of Key click; a key opening makes one or two bounces 16 dB lower.
        const float ClickAtFull = 0.9f;
        const float ReleaseClick = 0.16f;
        const double ClickLowHz = 1200, ClickHighHz = 3500;
        const int MaxBounces = 128, HannSize = 256;
        // A key's contacts fade its wheels in and out along a smoothstep this long: smooth enough that the tone itself starts with
        // no more power above 6 kHz than it has once it sounds (the click, if asked for, is added on top).
        const float KeyAttack = 0.006f, KeyRelease = 0.010f;
        const float PercFast = 0.2f, PercSlow = 0.7f;   // seconds to fall by 20 dB
        const float PercSoft = 0.4f, PercNormal = 1f;
        const int Sources = 16 + 128;
        const int MidiKeyBase = 1000;                   // NoteInput.MidiKeyBase: a key id of 1000 + n is MIDI note n, below that a track

        readonly ParamValues values;
        float sr = 48000;

        // Wheels.
        readonly uint[] phase = new uint[Tonewheels.Slots], inc = new uint[Tonewheels.Slots];
        readonly float[] cur = new float[Tonewheels.Slots], target = new float[Tonewheels.Slots], step = new float[Tonewheels.Slots];
        // Key contacts: bus b's weight of wheel w (sum of the keys' smoothed envelopes) and the static count of contacts.
        readonly float[] kb = new float[Tonewheels.Bars * Tonewheels.Slots], contacts = new float[Tonewheels.Bars * Tonewheels.Slots];
        readonly float[] kp2 = new float[Tonewheels.Slots], kp3 = new float[Tonewheels.Slots];
        readonly int[,] wheelOf = new int[Tonewheels.Keys, Tonewheels.Bars];
        readonly int[] wp2 = new int[Tonewheels.Keys], wp3 = new int[Tonewheels.Keys];
        readonly int[] held = new int[Tonewheels.Keys];
        readonly float[] keyEnv = new float[Tonewheels.Keys];
        readonly int[] sourceKey = new int[Sources];
        int totalHeld;
        float attackStep, releaseStep;
        bool ramping, forceTarget = true;
        int tickLeft;

        // Smoothed settings (tick rate).
        readonly Sm[] bar = new Sm[Tonewheels.Bars];
        Sm perc2, perc3, percOnS, leakAmp, leakGate;
        // Percussion and click.
        float percEnv, percDecayTick;
        float clickGain, keyLevel;
        Rng clickRng = new Rng(0xC11C4u), bounceRng = new Rng(0xB0B0Cu);
        readonly int[] bWait = new int[MaxBounces], bPos = new int[MaxBounces], bLen = new int[MaxBounces];
        readonly float[] bAmp = new float[MaxBounces];
        int bounces, clickTail, clickTailSamples;
        readonly Biquad[] clickBand = new Biquad[6];
        static readonly float[] Hann = MakeHann();
        // Stages.
        readonly Scanner scanner = new Scanner();
        readonly TubeDrive drive = new TubeDrive();
        readonly RotarySpeaker rotary = new RotarySpeaker();
        Sm expression, volume;
        int tail, tailSamples;
        readonly float[] mono = new float[MaxBlock];
        float tempo = 120;

        public readonly LevelMeter OutMeter = new LevelMeter();

        public OrganEngine(ParamValues values)
        {
            this.values = values;
            for (int k = 0; k < Tonewheels.Keys; k++)
            {
                for (int b = 0; b < Tonewheels.Bars; b++)
                {
                    int w = Tonewheels.WheelFor(k, Tonewheels.BarOffset[b]);
                    wheelOf[k, b] = w;
                    contacts[b * Tonewheels.Slots + w] += 1f;
                }
                wp2[k] = Tonewheels.WheelFor(k, Tonewheels.Perc2Offset);
                wp3[k] = Tonewheels.WheelFor(k, Tonewheels.Perc3Offset);
            }
            for (int i = 0; i < Sources; i++) sourceKey[i] = -1;
            for (int w = 1; w <= Tonewheels.Count; w++) phase[w] = (uint)(w * 2654435761u);
            SetSampleRate(48000);
        }

        public double Tempo { get => tempo; set => tempo = value > 0 ? (float)value : 120; }
        public float SampleRate => sr;
        public bool IsActive => SourcesActive() || tail > 0;
        public int HeldKeys => totalHeld;
        public float HornHz => rotary.HornHz;
        public float DrumHz => rotary.DrumHz;

        public void SetSampleRate(float rate)
        {
            sr = rate;
            for (int w = 1; w <= Tonewheels.Count; w++)
                inc[w] = (uint)(long)Math.Round(Tonewheels.WheelHz(w) / rate * 4294967296.0);
            attackStep = Tick / (KeyAttack * rate);
            releaseStep = Tick / (KeyRelease * rate);
            for (int b = 0; b < Tonewheels.Bars; b++) bar[b].Setup(0.015, rate);
            perc2.Setup(0.01, rate); perc3.Setup(0.01, rate); percOnS.Setup(0.01, rate);
            leakAmp.Setup(0.02, rate); leakGate.Setup(0.005, rate);
            expression.Setup(0.02, rate); volume.Setup(0.02, rate);
            clickBand[0].Set(BiquadType.HighPass, ClickLowHz, rate, 0.5412);    // 4th-order Butterworth high-pass
            clickBand[1].Set(BiquadType.HighPass, ClickLowHz, rate, 1.3066);
            clickBand[2].Set(BiquadType.LowPass, ClickHighHz, rate, 0.5098);    // 8th-order Butterworth low-pass
            clickBand[3].Set(BiquadType.LowPass, ClickHighHz, rate, 0.6013);
            clickBand[4].Set(BiquadType.LowPass, ClickHighHz, rate, 0.9000);
            clickBand[5].Set(BiquadType.LowPass, ClickHighHz, rate, 2.5629);
            clickTailSamples = (int)(0.004 * rate);
            tailSamples = (int)(0.1 * rate);
            scanner.SetSampleRate(rate);
            drive.SetSampleRate(rate);
            rotary.SetSampleRate(rate);
            OutMeter.SetSampleRate(rate);
            Update(true);
            SnapAll();
        }

        void SnapAll()
        {
            for (int b = 0; b < Tonewheels.Bars; b++) bar[b].V = bar[b].T;
            perc2.V = perc2.T; perc3.V = perc3.T; percOnS.V = percOnS.T; leakAmp.V = leakAmp.T; leakGate.V = leakGate.T;
            expression.V = expression.T; volume.V = volume.T;
            scanner.Snap(); drive.Snap(); rotary.Snap();
            forceTarget = true;
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            for (int b = 0; b < Tonewheels.Bars; b++) bar[b].T = Tonewheels.BarGain((int)values.Value(P.B16 + b));
            bool percOn = values.On(P.PercOn);
            float amp = values.Choice(P.PercLevel) == 1 ? PercNormal : PercSoft;
            perc2.T = percOn && values.Choice(P.PercHarm) == 0 ? amp : 0f;
            perc3.T = percOn && values.Choice(P.PercHarm) == 1 ? amp : 0f;
            percOnS.T = percOn ? 1f : 0f;
            float t20 = values.Choice(P.PercDecay) == 0 ? PercFast : PercSlow;
            percDecayTick = (float)PortableMath.Exp(-Tick * 2.302585092994046 / (t20 * sr));
            float kc = values.F(P.KeyClick);
            clickGain = ClickAtFull * kc * kc;
            // A key's own level: its drawbars' gains summed by power (the 1' is silent while the percussion is on).
            double pow = 0;
            for (int b = 0; b < Tonewheels.Bars; b++)
            {
                double g = bar[b].T * (b == Tonewheels.Bars - 1 && percOn ? 0.0 : 1.0);
                pow += g * g;
            }
            keyLevel = ToneGain * (float)Math.Sqrt(pow);
            leakAmp.T = values.F(P.Leakage) * LeakPerContact;
            scanner.Set(values.On(P.VibOn), values.Choice(P.VibMode));
            drive.Set(values.F(P.Drive));
            int speed = values.Choice(P.Speed);
            double hs = values.Value(P.HornSlow), hf = values.Value(P.HornFast), ds = values.Value(P.DrumSlow), df = values.Value(P.DrumFast);
            double ht = speed == 0 ? hs : speed == 1 ? hf : -hs;       // Stop: a target below zero, so the rotor reaches rest in finite time
            double dt = speed == 0 ? ds : speed == 1 ? df : -ds;
            rotary.Set(values.On(P.Rotary), ht, dt, values.Value(P.HornAccel), values.Value(P.DrumAccel),
                values.F(P.RotDepth), values.F(P.Doppler) / 100f, values.F(P.Spread), values.F(P.Balance));
            double vol = values.Value(P.Volume);
            volume.T = double.IsNegativeInfinity(vol) ? 0f : (float)DspMath.DbToGain(vol);
            float e = values.F(P.Expression);
            expression.T = e * e;
            forceTarget = true;
            values.Clean();
        }

        // ── notes ───────────────────────────────────────────────────────────

        static int Slot(int key) => key >= MidiKeyBase ? 16 + Math.Clamp(key - MidiKeyBase, 0, 127) : Math.Clamp(key, 0, 15);

        public void NoteOn(int key, int note, float velocity)
        {
            Update(false);
            int s = Slot(key);
            if (sourceKey[s] >= 0) Release(s);
            int k = Tonewheels.KeyFor(note);
            sourceKey[s] = k;
            held[k]++;
            if (totalHeld == 0) percEnv = 1f;                  // single trigger: only when no other key is held
            totalHeld++;
            Contact(clickGain * keyLevel, 2 + (int)(bounceRng.NextUnipolar() * 3));
        }

        public void NoteOff(int key)
        {
            int s = Slot(key);
            if (sourceKey[s] >= 0) { Release(s); Contact(clickGain * keyLevel * ReleaseClick, 1 + (int)(bounceRng.NextUnipolar() * 2)); }
        }

        /// <summary>A key's contacts closing or opening: <paramref name="count"/> bounces at random times and levels, the first at
        /// <paramref name="amp"/> (about), each later one weaker.</summary>
        void Contact(float amp, int count)
        {
            if (amp <= 0f) return;
            float t = bounceRng.NextUnipolar() * 0.4f;                     // ms
            for (int i = 0; i < count && bounces < MaxBounces; i++)
            {
                int j = bounces++;
                bWait[j] = (int)(t * 0.001f * sr);
                bPos[j] = 0;
                bLen[j] = Math.Max(8, (int)((1.2f + 1.3f * bounceRng.NextUnipolar()) * 0.001f * sr));
                bAmp[j] = amp * (0.8f + 0.4f * bounceRng.NextUnipolar());
                t += 0.3f + 0.9f * bounceRng.NextUnipolar();
                amp *= 0.4f + 0.35f * bounceRng.NextUnipolar();
            }
        }

        static float[] MakeHann()
        {
            var h = new float[HannSize + 1];
            for (int i = 0; i <= HannSize; i++) h[i] = (float)(0.5 - 0.5 * PortableMath.Cos(2 * Math.PI * i / HannSize));
            return h;
        }

        /// <summary>The click for one sample: every sounding bounce's Hann pulse times its own noise, through the band-pass.</summary>
        float ClickSample()
        {
            float x = 0f;
            for (int j = 0; j < bounces; j++)
            {
                if (bWait[j] > 0) { bWait[j]--; continue; }
                float f = (float)bPos[j] * HannSize / bLen[j];
                int k = (int)f;
                float env = Hann[k] + (Hann[k + 1] - Hann[k]) * (f - k);
                x += clickRng.NextBipolar() * env * bAmp[j];
                if (++bPos[j] >= bLen[j])
                {
                    bounces--;
                    bWait[j] = bWait[bounces]; bPos[j] = bPos[bounces]; bLen[j] = bLen[bounces]; bAmp[j] = bAmp[bounces];
                    j--;
                }
            }
            for (int f = 0; f < clickBand.Length; f++) x = clickBand[f].Process(x);
            return x > -1e-20f && x < 1e-20f ? 0f : x;                 // the filters' tail never reaches a denormal
        }

        void ResetClick()
        {
            bounces = 0;
            clickTail = 0;
            for (int f = 0; f < clickBand.Length; f++) clickBand[f].Reset();
        }

        void Release(int s)
        {
            int k = sourceKey[s];
            sourceKey[s] = -1;
            if (held[k] > 0) { held[k]--; totalHeld--; }
        }

        public void ModWheel(float value) { }
        public void PitchBend(float value) { }
        public void Sustain(bool down) { }
        public void Aftertouch(float value) { }

        public void AllNotesOff(bool hard)
        {
            for (int s = 0; s < Sources; s++) if (sourceKey[s] >= 0) Release(s);
            if (!hard) return;
            Array.Clear(keyEnv);
            percEnv = 0;
            ResetClick();
            Array.Clear(cur); Array.Clear(target); Array.Clear(step);
            ramping = false; forceTarget = true;
            scanner.Reset();
            drive.Reset();
            tail = 0;
        }

        bool SourcesActive()
        {
            if (totalHeld > 0 || percEnv > 0 || bounces > 0 || clickTail > 0 || leakGate.V > 0 || ramping) return true;
            for (int k = 0; k < Tonewheels.Keys; k++) if (keyEnv[k] > 0) return true;
            return false;
        }

        // ── the tone generator ──────────────────────────────────────────────

        void StartTick()
        {
            tickLeft = Tick;
            bool dirty = forceTarget;
            forceTarget = false;
            bool keysChanged = false, any = false;
            for (int k = 0; k < Tonewheels.Keys; k++)
            {
                float t = held[k] > 0 ? 1f : 0f, e = keyEnv[k];
                if (e != t)
                {
                    e = t > e ? Math.Min(t, e + attackStep) : Math.Max(t, e - releaseStep);
                    keyEnv[k] = e;
                    keysChanged = true;
                }
                if (e > 0) any = true;
            }
            if (keysChanged) { RebuildKeys(); dirty = true; }
            if (percEnv > 0)
            {
                percEnv *= percDecayTick;
                if (percEnv < 1e-5f) percEnv = 0;
                dirty = true;
            }
            leakGate.T = any ? 1f : 0f;
            for (int b = 0; b < Tonewheels.Bars; b++) { float before = bar[b].V; bar[b].Advance(Tick); dirty |= before != bar[b].V; }
            dirty |= Moved(ref perc2) | Moved(ref perc3) | Moved(ref percOnS) | Moved(ref leakAmp) | Moved(ref leakGate);
            if (!dirty) { ramping = false; return; }

            Array.Clear(target);
            float leak = leakAmp.V * leakGate.V;
            for (int b = 0; b < Tonewheels.Bars; b++)
            {
                float g = bar[b].V * (b == Tonewheels.Bars - 1 ? 1f - percOnS.V : 1f);
                if (g <= 0f) continue;
                int o = b * Tonewheels.Slots;
                for (int w = 1; w <= Tonewheels.Count; w++) target[w] += g * (kb[o + w] + leak * contacts[o + w]);
            }
            float p2 = perc2.V * percEnv, p3 = perc3.V * percEnv;
            if (p2 > 0f) for (int w = 1; w <= Tonewheels.Count; w++) target[w] += p2 * kp2[w];
            if (p3 > 0f) for (int w = 1; w <= Tonewheels.Count; w++) target[w] += p3 * kp3[w];
            const float k16 = 1f / Tick;
            for (int w = 1; w <= Tonewheels.Count; w++)
            {
                target[w] *= Tonewheels.WheelGain[w] * ToneGain;
                step[w] = (target[w] - cur[w]) * k16;
            }
            ramping = true;
        }

        static bool Moved(ref Sm s) { float before = s.V; s.Advance(Tick); return before != s.V; }

        void RebuildKeys()
        {
            Array.Clear(kb); Array.Clear(kp2); Array.Clear(kp3);
            for (int k = 0; k < Tonewheels.Keys; k++)
            {
                float e = keyEnv[k];
                if (e <= 0f) continue;
                float s = e * e * (3f - 2f * e);                  // a smooth contact: no click unless Key click asks for one
                for (int b = 0; b < Tonewheels.Bars; b++) kb[b * Tonewheels.Slots + wheelOf[k, b]] += s;
                kp2[wp2[k]] += s;
                kp3[wp3[k]] += s;
            }
        }

        void RenderTone(int pos, int m)
        {
            var tab = Tonewheels.Sine;
            var ph = phase; var ic = inc; var c = cur; var st = step;
            const float frac = 1f / 1048576f;
            if (ramping)
            {
                for (int i = 0; i < m; i++)
                {
                    float sum = 0f;
                    for (int w = 1; w <= Tonewheels.Count; w++)
                    {
                        uint p = ph[w] + ic[w];
                        ph[w] = p;
                        int idx = (int)(p >> 20);
                        float a = tab[idx];
                        float s = a + (tab[idx + 1] - a) * ((p & 0xFFFFFu) * frac);
                        float cw = c[w] + st[w];
                        c[w] = cw;
                        sum += cw * s;
                    }
                    mono[pos + i] = sum;
                }
            }
            else
            {
                for (int i = 0; i < m; i++)
                {
                    float sum = 0f;
                    for (int w = 1; w <= Tonewheels.Count; w++)
                    {
                        uint p = ph[w] + ic[w];
                        ph[w] = p;
                        int idx = (int)(p >> 20);
                        float a = tab[idx];
                        float s = a + (tab[idx + 1] - a) * ((p & 0xFFFFFu) * frac);
                        sum += c[w] * s;
                    }
                    mono[pos + i] = sum;
                }
            }
        }

        void EndTick()
        {
            if (!ramping) return;
            Array.Copy(target, cur, cur.Length);
            ramping = false;
        }

        // ── a block ─────────────────────────────────────────────────────────

        /// <summary>Renders a block (overwrites the buffers). Whether anything sounded.</summary>
        public bool Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            if (SourcesActive()) tail = tailSamples;
            else if (tail <= 0)
            {
                // Silent: nothing to compute. The wheels, the scanner and the rotors turn on; settings land where they were set.
                for (int w = 1; w <= Tonewheels.Count; w++) phase[w] += inc[w] * (uint)n;
                scanner.Advance(n);
                rotary.Idle(n);
                SnapAll();
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
                OutMeter.Feed(left, right, n);
                return false;
            }
            else tail -= n;

            int pos = 0;
            while (pos < n)
            {
                if (tickLeft <= 0) StartTick();
                int m = Math.Min(tickLeft, n - pos);
                if (!ramping && !AnyCoefficient()) { Array.Clear(mono, pos, m); for (int w = 1; w <= Tonewheels.Count; w++) phase[w] += inc[w] * (uint)m; }
                else RenderTone(pos, m);
                tickLeft -= m;
                pos += m;
                if (tickLeft == 0) EndTick();
            }
            scanner.Process(mono, n);
            for (int i = 0; i < n; i++)
            {
                if (bounces > 0 || clickTail > 0)
                {
                    mono[i] += ClickSample();
                    if (bounces > 0) clickTail = clickTailSamples;
                    else if (--clickTail == 0) ResetClick();             // the filters have rung out: start clean next time
                }
                mono[i] = DspMath.Flush(mono[i] * expression.Next());     // a release's tail reaches the drive and the speaker as 0, not as denormals
            }
            drive.Process(mono, n);
            rotary.Process(mono, left, right, n);
            for (int i = 0; i < n; i++)
            {
                float v = volume.Next();
                left[i] = DspMath.Flush(SoftLimit(left[i] * v));
                right[i] = DspMath.Flush(SoftLimit(right[i] * v));
            }
            OutMeter.Feed(left, right, n);
            return true;
        }

        bool AnyCoefficient()
        {
            for (int w = 1; w <= Tonewheels.Count; w++) if (cur[w] != 0f) return true;
            return false;
        }

        /// <summary>Transparent below 0.9, a smooth knee to 1 above.</summary>
        static float SoftLimit(float x)
        {
            float a = Math.Abs(x);
            if (a <= 0.9f) return x;
            float y = 0.9f + 0.1f * DspMath.FastTanh((a - 0.9f) * 10f);
            return x < 0 ? -y : y;
        }
    }
}
