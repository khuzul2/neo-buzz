#nullable disable
// Neo Vocoder's own carrier (docs/specs/neo-vocoder.md §3): 8 voices of saw and pulse with a noise blend, a little detune (the two oscillators
// of a voice, spread left and right), glide and an amplitude envelope. Notes come through the shared note sink (velocity sets the level) and the
// shared voice allocator (poly, 8 notes, the oldest or the quietest released one is taken first). The oscillators are PolyBLEP saw and pulse,
// the tone goes through a fixed pre-emphasis (about +6 dB per octave above 40 Hz, which levels a saw's falling harmonics so that every band of the
// bank gets about what white noise would give it), each voice is scaled by sqrt(220 Hz / pitch) so the level of the carrier does not follow
// the pitch. A voice that is taken over or killed fades out over 2 ms first, so a steal can't click. 0 bytes per block.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Vocoder
{
    internal sealed class VocoderVoice : IVoice
    {
        const int Tick = VocoderBank.Control;
        readonly VocoderSynth owner;
        readonly Adsr env = new Adsr();
        Rng noise;
        double phaseA, phaseB;
        float pitch, target;
        float velocity;
        float fade = 1, fadeStep;
        bool fadingOut, pending, pendingOff, held;
        float pNote, pVelocity, pFrom;

        public VocoderVoice(VocoderSynth owner, uint seed)
        {
            this.owner = owner;
            noise = new Rng(seed);
        }

        public bool IsActive => env.IsActive || fadingOut;

        public float Loudness => env.Level * velocity;

        public bool Held => held;

        public void SetSampleRate(float rate)
        {
            env.SetSampleRate(rate);
            fadeStep = 1f / (0.002f * rate);
        }

        public void SetEnvelope(float attack, float decay, float sustain, float release) => env.Set(attack, decay, sustain, release);

        public void Reset()
        {
            env.Reset();
            fade = 1; fadingOut = pending = pendingOff = held = false;
            phaseA = phaseB = 0;
            velocity = 0;
            noise = new Rng(0x2545F491u ^ (uint)owner.IndexOf(this) * 0x9E3779B1u);
        }

        void Start(float note, float vel, float glideFrom)
        {
            phaseA = phaseB = 0;
            target = note;
            pitch = float.IsNaN(glideFrom) ? note : glideFrom;
            velocity = vel;
            fade = 1;
            held = true;
            owner.ApplyEnvelope(env);
            env.Gate(true);
        }

        public void NoteOn(float note, float vel, int unisonIndex, int unisonCount, float glideFrom)
        {
            if (env.IsActive)
            {
                pending = true; pendingOff = false;
                pNote = note; pVelocity = vel; pFrom = glideFrom;
                fadingOut = true;
                held = true;
            }
            else Start(note, vel, glideFrom);
        }

        public void Legato(float note, bool glide)
        {
            target = note;
            if (!glide) pitch = note;
        }

        public void NoteOff()
        {
            held = false;
            if (pending) pendingOff = true;
            else env.Gate(false);
        }

        public void Kill()
        {
            pending = false;
            held = false;
            if (env.IsActive) fadingOut = true;
        }

        /// <summary>Adds <paramref name="count"/> samples from <paramref name="from"/> to the tone sums and the noise sum (the tone leans left and right by the voice's two oscillators).</summary>
        public void Render(float[] toneL, float[] toneR, float[] noiseSum, int from, int count, float bend, float detuneRatio, bool wantNoise, double rate)
        {
            if (!IsActive) return;
            // Pitch: glide (in semitones), bend, and the two oscillators detuned apart.
            if (pitch != target)
            {
                float d = target - pitch;
                if (MathF.Abs(d) < 1e-4f) pitch = target;
                else pitch += d * owner.GlideStep;
            }
            double semis = pitch + bend;
            double hz = DspMath.NoteToHz(semis);
            double incA = Math.Min(0.45, hz / detuneRatio / rate), incB = Math.Min(0.45, hz * detuneRatio / rate);
            float wave = owner.Wave;
            float gainBase = velocity * (float)Math.Sqrt(220.0 / hz);
            for (int i = from; i < from + count; i++)
            {
                if (fadingOut)
                {
                    fade -= fadeStep;
                    if (fade <= 0)
                    {
                        fade = 1;
                        fadingOut = false;
                        if (pending)
                        {
                            pending = false;
                            Start(pNote, pVelocity, pFrom);
                            if (pendingOff) { env.Gate(false); held = false; }
                            pendingOff = false;
                            semis = pitch + bend;
                            hz = DspMath.NoteToHz(semis);
                            incA = Math.Min(0.45, hz / detuneRatio / rate); incB = Math.Min(0.45, hz * detuneRatio / rate);
                            gainBase = velocity * (float)Math.Sqrt(220.0 / hz);
                        }
                        else
                        {
                            env.Reset();
                            return;
                        }
                    }
                }
                float level = env.Next() * gainBase * (fadingOut ? fade : 1f);
                float a = Osc(phaseA, incA, wave), b = Osc(phaseB, incB, wave);
                toneL[i] += (0.75f * a + 0.25f * b) * level;
                toneR[i] += (0.75f * b + 0.25f * a) * level;
                if (wantNoise) noiseSum[i] += noise.NextBipolar() * level;
                phaseA += incA; if (phaseA >= 1) phaseA -= 1;
                phaseB += incB; if (phaseB >= 1) phaseB -= 1;
            }
        }

        /// <summary>The band-limited saw (wave 0) and pulse (wave 1): a PolyBLEP step at each edge.</summary>
        static float Osc(double p, double dt, float wave)
        {
            float saw = (float)(2 * p - 1) - Blep(p, dt);
            if (wave <= 0f) return saw;
            double q = p + 0.5; if (q >= 1) q -= 1;
            float sq = (p < 0.5 ? 1f : -1f) + Blep(p, dt) - Blep(q, dt);
            return wave >= 1f ? sq : saw + wave * (sq - saw);
        }

        static float Blep(double t, double dt)
        {
            if (t < dt)
            {
                double x = t / dt;
                return (float)(x + x - x * x - 1.0);
            }
            if (t > 1.0 - dt)
            {
                double x = (t - 1.0) / dt;
                return (float)(x * x + x + x + 1.0);
            }
            return 0f;
        }
    }

    internal sealed class VocoderSynth : INoteSink
    {
        public const int Voices = 8;
        public const int MaxBlock = 256;
        /// <summary>The tone's level: with it a note at full velocity gives the bands about a white carrier at the nominal level (measured, neo-vocoder.md §3).</summary>
        public const float ToneScale = 3.1f;
        public const float NoiseScale = 0.5f * VocoderBank.CarrierNominal / 0.2886751f;   // uniform ±1 has RMS 0.5774; ×0.5 makes ±0.5, RMS 0.2887
        const double BendRange = 2.0;

        readonly VocoderVoice[] voices = new VocoderVoice[Voices];
        readonly VoiceAllocator alloc;
        readonly float[] toneL = new float[MaxBlock], toneR = new float[MaxBlock], noiseSum = new float[MaxBlock];
        float preL, preR;                  // the pre-emphasis's memory (the last tone sample)
        double rate = 48000;
        float bend, bendTarget, bendStep;
        // Settings (read each block from the parameters).
        public float Wave, Noise, DetuneCents, GlideMs;
        public float Attack = 0.005f, Decay = 0.15f, SustainLevel = 1, Release = 0.11f;
        public float GlideStep { get; private set; }

        public VocoderSynth()
        {
            for (int i = 0; i < Voices; i++) voices[i] = new VocoderVoice(this, 0x2545F491u ^ (uint)(i + 1) * 0x9E3779B1u);
            alloc = new VoiceAllocator(voices) { Polyphony = Voices, Unison = 1 };
            SetSampleRate(48000);
        }

        public void SetSampleRate(double sr)
        {
            rate = sr;
            foreach (var v in voices) v.SetSampleRate((float)sr);
            bendStep = (float)(1 - PortableMath.Exp(-Tick / (0.01 * sr)));
            Apply();
        }

        const int Tick = VocoderBank.Control;

        public int IndexOf(VocoderVoice v) => Array.IndexOf(voices, v);

        public void ApplyEnvelope(Adsr env) => env.Set(Attack, Decay, SustainLevel, Release);

        /// <summary>Takes the settings: the envelope reaches the voices that sound, the glide time and mode reach the allocator.</summary>
        public void Apply()
        {
            foreach (var v in voices) v.SetEnvelope(Attack, Decay, SustainLevel, Release);
            alloc.GlideAlways = GlideMs > 0.5f;
            GlideStep = GlideMs > 0.5f ? (float)(1 - PortableMath.Exp(-4.6 * Tick / (GlideMs * 0.001 * rate))) : 1f;
        }

        public void Reset()
        {
            alloc.AllNotesOff(true);
            foreach (var v in voices) v.Reset();
            preL = preR = 0;
            bend = bendTarget = 0;
        }

        public bool Active
        {
            get
            {
                foreach (var v in voices) if (v.IsActive) return true;
                return false;
            }
        }

        public int ActiveVoices
        {
            get
            {
                int n = 0;
                foreach (var v in voices) if (v.IsActive) n++;
                return n;
            }
        }

        /// <summary>Notes held now (gate on).</summary>
        public int HeldNotes => alloc.HeldNotes;

        // ------------------------------------------------------------------------------------------------ notes

        public void NoteOn(int key, int note, float velocity) => alloc.NoteOn(key, note, Math.Clamp(velocity, 0.01f, 1f));
        public void NoteOff(int key) => alloc.NoteOff(key);
        public void ModWheel(float value) { }
        public void PitchBend(float value) => bendTarget = (float)(Math.Clamp(value, -1f, 1f) * BendRange);
        void INoteSink.Sustain(bool down) => alloc.Sustain(down);
        public void Aftertouch(float value) { }
        public void AllNotesOff(bool hard) => alloc.AllNotesOff(hard);

        // ------------------------------------------------------------------------------------------------ audio

        /// <summary>The carrier for <paramref name="n"/> samples (stereo, replacing what the buffers hold).</summary>
        public void Render(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Array.Clear(toneL, 0, n);
            Array.Clear(toneR, 0, n);
            bool wantNoise = Noise > 0f;
            if (wantNoise) Array.Clear(noiseSum, 0, n);
            double detuneRatio = PortableMath.Pow(2, DetuneCents / 2400.0);
            for (int from = 0; from < n; from += Tick)
            {
                int count = Math.Min(Tick, n - from);
                bend += (bendTarget - bend) * bendStep;
                if (Math.Abs(bendTarget - bend) < 1e-5f) bend = bendTarget;
                for (int v = 0; v < Voices; v++)
                {
                    var voice = voices[v];
                    if (!voice.IsActive) continue;
                    voice.Render(toneL, toneR, noiseSum, from, count, bend, (float)detuneRatio, wantNoise, rate);
                }
            }
            // Pre-emphasis on the tone, the noise mixed in by the blend, the nominal level.
            float k = 0.995f, nb = Noise, tb = 1f - nb;
            float zl = preL, zr = preR;
            for (int i = 0; i < n; i++)
            {
                float xl = toneL[i], xr = toneR[i];
                float yl = xl - k * zl, yr = xr - k * zr;
                zl = xl; zr = xr;
                float nz = wantNoise ? noiseSum[i] * NoiseScale : 0f;
                left[i] = tb * yl * ToneScale + nb * nz;
                right[i] = tb * yr * ToneScale + nb * nz;
            }
            preL = DspMath.Flush(zl); preR = DspMath.Flush(zr);
        }
    }
}
