#nullable disable
// Neo Drums (docs/specs/machines.md §5): the engine. Twelve voices, each a channel: up to three hits sounding at once
// (a new hit starts while the last fades out over 3 ms; the closed hat fades the open one out over 5 ms), rendered
// into the voice's own mono bus, then the channel's level and pan (glides of 10 ms that run sample by sample) into the
// main stereo output, then Volume. The buses are where per-voice outputs would come from, were ReBuzz to give a
// managed machine more than one (machines.md, Neo Drums: what the engine lacks). Notes on the General MIDI drum keys,
// the machine's per-track hit columns and the editor's pads all become Hit(voice, velocity, accent) at a block start,
// so the output doesn't depend on how the host splits it. Pure; nothing allocates after construction.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Drums
{
    /// <summary>A gain that glides to its target in a straight line over a fixed number of samples, sample by sample,
    /// its value computed from the count, so moving it n samples at once lands where n single steps would.</summary>
    internal struct Glide
    {
        float start, target;
        int k, length;

        public float Value => k >= length ? target : start + (target - start) * ((float)k / length);
        public float Target => target;
        public bool Moving => k < length;

        public void Setup(int samples)
        {
            length = Math.Max(1, samples);
            k = length;
        }

        public void Reset(float v) { start = target = v; k = length; }

        public void Set(float v)
        {
            if (v == target) return;
            start = Value;
            target = v;
            k = 0;
        }

        public float Next()
        {
            float v = Value;
            if (k < length) k++;
            return v;
        }

        public void Skip(int n) => k = Math.Min(length, k + n);
    }

    internal sealed class DrumsEngine : INoteSink
    {
        public const int MaxBlock = 256;
        public const int VoiceCount = 12;
        public const int HitsPerVoice = 3;
        const int MidiKeyBase = 1000;
        /// <summary>Fades (s): a hit a newer one of its voice takes over from, a choked open hat, a panic.</summary>
        public const double RetriggerSeconds = 0.003, ChokeSeconds = 0.005, PanicSeconds = 0.005;
        const double GlideSeconds = 0.01;

        /// <summary>The voices, in their order (parameters, hit columns, the editor's pads).</summary>
        public static readonly string[] Names = { "Kick", "Snare", "Clap", "Rim", "Closed hat", "Open hat", "Low tom", "Mid tom", "High tom", "Cowbell", "Crash", "Ride" };
        /// <summary>Their hit columns' names, as Buzz drum machines name them.</summary>
        public static readonly string[] Short = { "BD", "SD", "CP", "RS", "CH", "OH", "LT", "MT", "HT", "CB", "CC", "RC" };
        /// <summary>Each voice's key (General MIDI from C2, as Neo Sampler's Kit mode; the toms as a TR-909's and TR-8's).</summary>
        public static readonly int[] Keys = { 36, 38, 39, 37, 42, 46, 43, 47, 50, 56, 49, 51 };

        /// <summary>
        /// The voice a MIDI note plays (−1: none): General MIDI's drum keys from B1, with the TR-909's pairs (B1 and C2 the
        /// kick, D2 and E2 the snare, F2 and G2 the low tom, A2 and B2 the mid tom, C3 and D3 the high tom, F#2 and G#2 the
        /// closed hat), C#3 and A3 the crash, D#3 and B3 the ride, G#3 the cowbell.
        /// </summary>
        public static int VoiceOfKey(int note) => note switch
        {
            35 or 36 => 0,
            38 or 40 => 1,
            39 => 2,
            37 => 3,
            42 or 44 => 4,
            46 => 5,
            41 or 43 => 6,
            45 or 47 => 7,
            48 or 50 => 8,
            56 => 9,
            49 or 57 => 10,
            51 or 59 => 11,
            _ => -1,
        };

        /// <summary>A voice's parameters: tune, decay, tone, then the extras (−1 where it has none), level and pan.</summary>
        internal readonly struct VoiceParams(int tune, int decay, int tone, int a, int b, int c, int d, int level, int pan)
        {
            public readonly int Tune = tune, Decay = decay, Tone = tone, A = a, B = b, C = c, D = d, Level = level, Pan = pan;
        }

        public static readonly VoiceParams[] Voice =
        {
            new(P.KickTune, P.KickDecay, -1, P.KickSweep, P.KickSweepT, P.KickClick, P.KickDrive, P.KickLevel, P.KickPan),
            new(P.SnareTune, P.SnareDecay, P.SnareTone, P.SnareSnap, -1, -1, -1, P.SnareLevel, P.SnarePan),
            new(P.ClapTune, P.ClapDecay, -1, P.ClapSpread, -1, -1, -1, P.ClapLevel, P.ClapPan),
            new(P.RimTune, P.RimDecay, -1, -1, -1, -1, -1, P.RimLevel, P.RimPan),
            new(P.ChTune, P.ChDecay, P.ChTone, -1, -1, -1, -1, P.ChLevel, P.ChPan),
            new(P.OhTune, P.OhDecay, P.OhTone, -1, -1, -1, -1, P.OhLevel, P.OhPan),
            new(P.LtTune, P.LtDecay, P.LtTone, -1, -1, -1, -1, P.LtLevel, P.LtPan),
            new(P.MtTune, P.MtDecay, P.MtTone, -1, -1, -1, -1, P.MtLevel, P.MtPan),
            new(P.HtTune, P.HtDecay, P.HtTone, -1, -1, -1, -1, P.HtLevel, P.HtPan),
            new(P.CbTune, P.CbDecay, -1, -1, -1, -1, -1, P.CbLevel, P.CbPan),
            new(P.CcTune, P.CcDecay, P.CcTone, -1, -1, -1, -1, P.CcLevel, P.CcPan),
            new(P.RcTune, P.RcDecay, P.RcTone, -1, -1, -1, -1, P.RcLevel, P.RcPan),
        };

        /// <summary>
        /// Each voice's own scale: a hit at velocity 127, its level at 0 dB, peaks about where a TR-909's sits against its
        /// kick (machines.md, Neo Drums: Levels).
        /// </summary>
        internal static readonly float[] Scale = { 0.60f, 0.62f, 2.2f, 0.42f, 1.6f, 1.6f, 0.55f, 0.55f, 0.55f, 0.75f, 1.9f, 1.6f };

        readonly ParamValues values;
        readonly DrumHit[] hits = new DrumHit[VoiceCount * HitsPerVoice];
        readonly float[][] bus = new float[VoiceCount][];
        readonly float[] residual = new float[VoiceCount];
        readonly Glide[] gainL = new Glide[VoiceCount], gainR = new Glide[VoiceCount];
        Glide volume;
        float residualK;
        double sampleRate = 48000;

        public DrumsEngine(ParamValues values)
        {
            this.values = values;
            for (int i = 0; i < hits.Length; i++) hits[i] = new DrumHit();
            for (int v = 0; v < VoiceCount; v++) bus[v] = new float[MaxBlock];
            SetSampleRate(48000);
        }

        public double SampleRate => sampleRate;

        public readonly LevelMeter OutMeter = new LevelMeter();

        /// <summary>For the editor (any thread): each voice's peak over its last block, falling about 20 dB a second.</summary>
        public readonly float[] VoicePeak = new float[VoiceCount];

        /// <summary>Accent for the hits of the block about to start from the song's tracks (the Accent column on this tick).</summary>
        public bool TickAccent { get; set; }

        /// <summary>Hits started so far (the editor lights its pads by it).</summary>
        public readonly long[] HitCount = new long[VoiceCount];

        public void SetSampleRate(double sr)
        {
            sampleRate = sr;
            foreach (var h in hits) h.SetSampleRate(sr);
            Array.Clear(residual);
            residualK = (float)PortableMath.Exp(-1.0 / (0.0005 * sr));
            int glide = (int)Math.Round(GlideSeconds * sr);
            for (int v = 0; v < VoiceCount; v++)
            {
                gainL[v].Setup(glide);
                gainR[v].Setup(glide);
            }
            volume.Setup(glide);
            OutMeter.SetSampleRate((float)sr);
            UpdateMix(true);
        }

        public bool IsActive
        {
            get
            {
                foreach (var h in hits) if (h.IsActive) return true;
                foreach (var r in residual) if (r != 0) return true;
                return false;
            }
        }

        /// <summary>Hits sounding now (fading ones included).</summary>
        public int ActiveHits
        {
            get
            {
                int n = 0;
                foreach (var h in hits) if (h.IsActive) n++;
                return n;
            }
        }

        internal DrumHit HitOf(int voice, int k) => hits[voice * HitsPerVoice + k];

        /// <summary>The level and pan targets (and Volume's), from the parameters.</summary>
        void UpdateMix(bool snap)
        {
            for (int v = 0; v < VoiceCount; v++)
            {
                var p = Voice[v];
                double db = values.Value(p.Level);
                float level = double.IsNegativeInfinity(db) ? 0f : (float)DspMath.DbToGain(db);
                DspMath.PanGains((float)(values.Value(p.Pan) / 100.0), out float pl, out float pr);
                if (snap)
                {
                    gainL[v].Reset(level * pl);
                    gainR[v].Reset(level * pr);
                }
                else
                {
                    gainL[v].Set(level * pl);
                    gainR[v].Set(level * pr);
                }
            }
            double vol = values.Value(P.Volume);
            float g = double.IsNegativeInfinity(vol) ? 0f : (float)DspMath.DbToGain(vol);
            if (snap) volume.Reset(g); else volume.Set(g);
        }

        /// <summary>What a hit of <paramref name="voice"/> at <paramref name="velocity"/> (0 … 1) latches now.</summary>
        public HitSettings Settings(int voice, float velocity, bool accent)
        {
            var p = Voice[voice];
            double Get(int i) => i >= 0 ? values.Value(i) : 0;
            float vel = Math.Clamp(velocity, 0f, 1f);
            float velLevel = values.F(P.VelLevel), velTone = values.F(P.VelTone);
            float gain = velLevel <= 0 ? 1f : PortableMathF.Pow(Math.Max(vel, 1e-4f), 2f * velLevel);
            if (accent) gain *= (float)DspMath.DbToGain(values.Value(P.Accent));
            float push = Math.Min(1.25f, vel + (accent ? 0.25f : 0f));
            return new HitSettings
            {
                Tune = Get(p.Tune),
                Decay = Get(p.Decay),
                Tone = Get(p.Tone),
                A = Get(p.A),
                B = Get(p.B),
                C = Get(p.C),
                D = Get(p.D),
                Gain = gain * Scale[voice],
                Bright = velTone * (push - 100f / 127f),
            };
        }

        /// <summary>Starts a hit of <paramref name="voice"/> (0 … 11) at <paramref name="velocity"/> (0 … 1), at the next block's start.</summary>
        public void Hit(int voice, float velocity, bool accent = false)
        {
            if ((uint)voice >= VoiceCount || !(velocity > 0)) return;
            var s = Settings(voice, velocity, accent);
            // The voice's hits that sound fade out; the closed hat chokes the open one.
            int b = voice * HitsPerVoice;
            for (int k = 0; k < HitsPerVoice; k++) hits[b + k].FadeOut(RetriggerSeconds);
            if (voice == (int)DrumVoice.ClosedHat)
                for (int k = 0; k < HitsPerVoice; k++) hits[(int)DrumVoice.OpenHat * HitsPerVoice + k].FadeOut(ChokeSeconds);
            // A free hit, else the oldest (its last sample goes to the declicker, which lets it down within a millisecond).
            DrumHit h = null;
            for (int k = 0; k < HitsPerVoice && h == null; k++) if (!hits[b + k].IsActive) h = hits[b + k];
            if (h == null)
            {
                h = hits[b];
                for (int k = 1; k < HitsPerVoice; k++) if (hits[b + k].Age > h.Age) h = hits[b + k];
                residual[voice] += h.Last;
                h.Kill();
            }
            h.Begin((DrumVoice)voice, s, 0x9E3779B9u * (uint)(voice + 1) + 12345u);
            HitCount[voice]++;
        }

        // ── notes ───────────────────────────────────────────────────────────

        /// <summary>A note (a track's Note column, or MIDI): its voice by key; a track's note takes the tick's accent.</summary>
        public void NoteOn(int key, int note, float velocity)
        {
            int v = VoiceOfKey(note);
            if (v >= 0) Hit(v, velocity, key < MidiKeyBase && TickAccent);
        }

        /// <summary>Drums are one-shots: a note's end changes nothing.</summary>
        public void NoteOff(int key) { }
        public void ModWheel(float value) { }
        public void PitchBend(float value) { }
        public void Sustain(bool down) { }
        public void Aftertouch(float value) { }

        /// <summary>Transport stop: the hits ring on (one-shots). A panic (<paramref name="hard"/>) fades everything out in 5 ms.</summary>
        public void AllNotesOff(bool hard)
        {
            if (!hard) return;
            foreach (var h in hits) h.FadeOut(PanicSeconds);
        }

        // ── audio ───────────────────────────────────────────────────────────

        /// <summary>Renders a block (overwrites the buffers). Whether anything sounded.</summary>
        public bool Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            if (values.AnyDirty)
            {
                UpdateMix(false);
                values.Clean();
            }
            Array.Clear(left, 0, n);
            Array.Clear(right, 0, n);
            bool any = false;
            float fall = PortableMathF.Pow(0.99952f, n);
            for (int v = 0; v < VoiceCount; v++)
            {
                int b = v * HitsPerVoice;
                bool sounding = residual[v] != 0;
                for (int k = 0; k < HitsPerVoice && !sounding; k++) sounding = hits[b + k].IsActive;
                if (!sounding)
                {
                    gainL[v].Skip(n);
                    gainR[v].Skip(n);
                    VoicePeak[v] *= fall;
                    continue;
                }
                any = true;
                var x = bus[v];
                Array.Clear(x, 0, n);
                for (int k = 0; k < HitsPerVoice; k++) hits[b + k].Render(x, n);
                float r = residual[v];
                if (r != 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        x[i] += r;
                        r *= residualK;
                        if (r > -1e-9f && r < 1e-9f) { r = 0; break; }
                    }
                    residual[v] = r;
                }
                float peak = VoicePeak[v] * fall;
                for (int i = 0; i < n; i++)
                {
                    float s = x[i];
                    left[i] += s * gainL[v].Next();
                    right[i] += s * gainR[v].Next();
                    float a = MathF.Abs(s);
                    if (a > peak) peak = a;
                }
                VoicePeak[v] = peak;
            }
            if (any || volume.Moving)
                for (int i = 0; i < n; i++)
                {
                    float g = volume.Next();
                    left[i] *= g;
                    right[i] *= g;
                }
            else volume.Skip(n);
            OutMeter.Feed(left, right, n);
            return any;
        }
    }
}
