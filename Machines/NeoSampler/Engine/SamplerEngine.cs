#nullable disable
// Neo Sampler (docs/specs/machines.md §5): the engine. A note finds its zones (Instrument: by key
// and velocity range; Kit: zone n on key Kit base + n − 1), each zone its wave slot and in it a
// layer (by root; layers with one root alternate round robin), and each takes a voice: a read of
// the layer (Interpolation, at a step from the pitch), the region, loop and crossfade, a filter with
// its envelope, the amp envelope, level, velocity and pan. Control values (pitch, gains, filter
// coefficients) are set every 16 samples on a grid that runs across blocks, gains move in straight
// lines within each 16, and notes apply at the block start, so the output doesn't depend on how the
// host splits it. Nothing allocates after construction: waves come in as SampleLayers built
// elsewhere, and a voice keeps the layer it started with. Pure.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Sampler
{
    internal enum ZoneLoop { Wave, Off, Forward, PingPong }
    internal enum SamplerFilter { Off, Lp24, Lp12, BandPass, HighPass }

    /// <summary>One zone's decoded parameters.</summary>
    internal struct ZoneSettings
    {
        public int Wave, KeyLo, KeyHi, VelLo, VelHi, Root;
        /// <summary>Transposition in semitones, the fine tune included.</summary>
        public float Tune;
        /// <summary>Level as a gain (0 at −∞).</summary>
        public float Level;
        public float Pan;
        /// <summary>Fractions of the wave (0 … 1).</summary>
        public double Start, End, LoopStart, LoopEnd;
        public ZoneLoop Loop;
        /// <summary>Crossfade in seconds.</summary>
        public float Xfade;
        public bool OneShot, Reverse;
        public int Choke;
    }

    /// <summary>The decoded parameters voices read (updated at a block start when something changed).</summary>
    internal sealed class SamplerSettings
    {
        public bool Kit;
        public int KitBase, Polyphony, VelXfade;
        public float BendRange, VelAmp, Volume;
        public readonly float[] AmpEnv = new float[4], FltEnv = new float[4];
        public SamplerFilter Filter;
        public float Cutoff, Reso, FEnv, FKey, FVel;
        public readonly ZoneSettings[] Zones = new ZoneSettings[SamplerEngine.ZoneCount];
        public int Version;
    }

    internal sealed class SamplerEngine : INoteSink
    {
        public const int MaxBlock = 256;
        public const int VoiceCount = 32;
        public const int ControlRate = 16;
        public const int ZoneCount = 16;
        /// <summary>Parameters per zone; zone z's field f is <c>P.Z1Wave + z · ZoneStride + f</c>.</summary>
        public const int ZoneStride = P.Z2Wave - P.Z1Wave;
        public const int FWave = 0, FKeyLo = 1, FKeyHi = 2, FVelLo = 3, FVelHi = 4, FRoot = 5, FTune = 6, FFine = 7, FLevel = 8, FPan = 9,
            FStart = 10, FEnd = 11, FLoop = 12, FLoopS = 13, FLoopE = 14, FXfade = 15, FTrig = 16, FRev = 17, FChoke = 18;
        /// <summary>A zone's Root that takes each layer's own.</summary>
        public const int RootFromWave = 128;
        /// <summary>Fades: a stolen voice's, a choked one's, the end of a region's (seconds).</summary>
        public const float StealSeconds = 0.003f, ChokeSeconds = 0.005f, EndFadeSeconds = 0.002f;

        public static int ZoneParam(int zone, int field) => P.Z1Wave + zone * ZoneStride + field;

        readonly ParamValues values;
        readonly SamplerVoice[] voices = new SamplerVoice[VoiceCount];
        internal readonly SamplerSettings S = new SamplerSettings();
        readonly int[] roundRobin = new int[ZoneCount];
        // A note's zones, gathered before voices start (no allocation).
        readonly int[] startZone = new int[2 * ZoneCount];
        readonly float[] startGain = new float[2 * ZoneCount];
        readonly bool[] chokeDone = new bool[9];
        float sampleRate = 48000;
        Smoother volume;
        int segment;          // samples left in the current 16-sample control segment
        long clock, serial;
        float bend;
        bool sustainDown;

        public SamplerEngine(ParamValues values, SampleBank bank = null)
        {
            this.values = values;
            Bank = bank ?? new SampleBank();
            for (int i = 0; i < VoiceCount; i++) voices[i] = new SamplerVoice(this);
            SetSampleRate(48000);
        }

        /// <summary>The waves zones reach (the song's slots, set by the machine, and the built-ins).</summary>
        public SampleBank Bank { get; }

        public readonly LevelMeter OutMeter = new LevelMeter();

        public float SampleRate => sampleRate;
        internal float BendSemis => bend * S.BendRange;

        public bool IsActive
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

        internal SamplerVoice Voice(int i) => voices[i];

        /// <summary>For the editor (any thread): voice <paramref name="i"/>'s zone (−1: silent), note and position in its wave (0 … 1).</summary>
        public bool VoiceInfo(int i, out int zone, out int note, out float position)
        {
            var v = voices[i];
            zone = v.InfoZone;
            note = v.InfoNote;
            position = v.InfoPosition;
            return zone >= 0;
        }

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            volume.Setup(0.02, sr);
            OutMeter.SetSampleRate(sr);
            foreach (var v in voices) v.SetSampleRate(sr);
            Update(true);
            volume.Reset(S.Volume);
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            var s = S;
            s.Kit = values.Choice(P.Mode) == 1;
            s.KitBase = (int)values.Value(P.KitBase);
            s.Polyphony = (int)values.Value(P.Polyphony);
            s.BendRange = values.F(P.BendRange);
            s.VelAmp = values.F(P.VelAmp);
            s.VelXfade = (int)values.Value(P.VelXfade);
            double vol = values.Value(P.Volume);
            s.Volume = double.IsNegativeInfinity(vol) ? 0f : (float)DspMath.DbToGain(vol);
            s.AmpEnv[0] = values.F(P.AmpA); s.AmpEnv[1] = values.F(P.AmpD); s.AmpEnv[2] = values.F(P.AmpS); s.AmpEnv[3] = values.F(P.AmpR);
            s.FltEnv[0] = values.F(P.FltA); s.FltEnv[1] = values.F(P.FltD); s.FltEnv[2] = values.F(P.FltS); s.FltEnv[3] = values.F(P.FltR);
            s.Filter = (SamplerFilter)values.Choice(P.FType);
            s.Cutoff = values.F(P.FCutoff);
            s.Reso = values.F(P.FReso);
            s.FEnv = values.F(P.FEnv) * 6f;
            s.FKey = values.F(P.FKey);
            s.FVel = values.F(P.FVel) * 3f;
            for (int z = 0; z < ZoneCount; z++) s.Zones[z] = Zone(values, z);
            volume.Target = s.Volume;
            s.Version++;
            values.Clean();
        }

        /// <summary>A zone's parameters, decoded (the editor and tests use it too).</summary>
        public static ZoneSettings Zone(ParamValues v, int z)
        {
            int b = ZoneParam(z, 0);
            double level = v.Value(b + FLevel);
            return new ZoneSettings
            {
                Wave = v[b + FWave],
                KeyLo = (int)v.Value(b + FKeyLo),
                KeyHi = (int)v.Value(b + FKeyHi),
                VelLo = (int)v.Value(b + FVelLo),
                VelHi = (int)v.Value(b + FVelHi),
                Root = (int)v.Value(b + FRoot),
                Tune = (float)(v.Value(b + FTune) + v.Value(b + FFine) / 100.0),
                Level = double.IsNegativeInfinity(level) ? 0f : (float)DspMath.DbToGain(level),
                Pan = (float)(v.Value(b + FPan) / 100.0),
                Start = v.Value(b + FStart) / 100.0,
                End = v.Value(b + FEnd) / 100.0,
                Loop = (ZoneLoop)v.Choice(b + FLoop),
                LoopStart = v.Value(b + FLoopS) / 100.0,
                LoopEnd = v.Value(b + FLoopE) / 100.0,
                Xfade = (float)v.Value(b + FXfade),
                OneShot = v.Choice(b + FTrig) == 1,
                Reverse = v.On(b + FRev),
                Choke = (int)v.Value(b + FChoke),
            };
        }

        /// <summary>
        /// A zone's share of a velocity (0: it doesn't play it): 1 inside its range; with a
        /// crossfade of <paramref name="width"/> steps, equal-power ramps centred half a step
        /// outside each edge that isn't the end of the velocity range, so two zones that meet
        /// sum to constant power.
        /// </summary>
        public static float VelocityShare(in ZoneSettings z, int velocity, int width)
        {
            int lo = Math.Min(z.VelLo, z.VelHi), hi = Math.Max(z.VelLo, z.VelHi);
            if (width <= 0) return velocity >= lo && velocity <= hi ? 1f : 0f;
            float g = 1f;
            if (lo > 1)
            {
                float t = (velocity - (lo - 0.5f - 0.5f * width)) / width;
                if (t <= 0) return 0f;
                if (t < 1) g *= SinQuarter(t);
            }
            if (hi < 127)
            {
                float t = ((hi + 0.5f + 0.5f * width) - velocity) / width;
                if (t <= 0) return 0f;
                if (t < 1) g *= SinQuarter(t);
            }
            return g;
        }

        /// <summary>sin(π t / 2) for t in 0 … 1 (a table, straight lines between its 256 steps).</summary>
        public static float SinQuarter(float t)
        {
            if (t <= 0) return 0f;
            if (t >= 1) return 1f;
            float x = t * 256f;
            int i = (int)x;
            return quarter[i] + (x - i) * (quarter[i + 1] - quarter[i]);
        }

        static readonly float[] quarter = Quarter();

        static float[] Quarter()
        {
            var q = new float[258];
            for (int i = 0; i < q.Length; i++) q[i] = (float)PortableMath.Sin(0.5 * Math.PI * Math.Min(1.0, i / 256.0));
            return q;
        }

        /// <summary>The layer a zone plays for a note: by root (the highest at or below the note, else the lowest); layers of one root alternate.</summary>
        SampleLayer PickLayer(int z, in ZoneSettings zs, SampleSlot slot, int note)
        {
            var layers = slot.Layers;
            if (layers.Length == 1) return layers[0];
            int from = 0, count = layers.Length;
            if (!S.Kit && zs.Root == RootFromWave)
            {
                int pick = 0;
                for (int i = 0; i < layers.Length; i++)
                {
                    if (layers[i].Root > note) break;
                    pick = i;
                }
                int root = layers[pick].Root;
                from = pick;
                while (from > 0 && layers[from - 1].Root == root) from--;
                count = 0;
                while (from + count < layers.Length && layers[from + count].Root == root) count++;
            }
            int k = roundRobin[z]++ % count;
            if (roundRobin[z] > 1 << 20) roundRobin[z] = 0;
            return layers[from + k];
        }

        // ── notes ───────────────────────────────────────────────────────────

        public void NoteOn(int key, int note, float velocity)
        {
            Update(false);
            clock++;
            serial++;
            ReleaseKey(key);
            int vel = Math.Clamp((int)Math.Round(velocity * 127f), 1, 127);
            var s = S;
            int count = 0;
            if (s.Kit)
            {
                int z = note - s.KitBase;
                if (z >= 0 && z < ZoneCount && s.Zones[z].Wave != 0)
                {
                    startZone[count] = z;
                    startGain[count++] = 1f;
                }
            }
            else
            {
                for (int z = 0; z < ZoneCount; z++)
                {
                    ref var zs = ref s.Zones[z];
                    if (zs.Wave == 0) continue;
                    int lo = Math.Min(zs.KeyLo, zs.KeyHi), hi = Math.Max(zs.KeyLo, zs.KeyHi);
                    if (note < lo || note > hi) continue;
                    float share = VelocityShare(zs, vel, s.VelXfade);
                    if (share <= 0) continue;
                    startZone[count] = z;
                    startGain[count++] = share;
                }
            }
            if (count == 0) return;
            float velGain = s.VelAmp <= 0 ? 1f : PortableMathF.Pow(vel / 127f, 2f * s.VelAmp);
            Array.Clear(chokeDone, 0, chokeDone.Length);
            for (int i = 0; i < count; i++)
            {
                int z = startZone[i];
                ref var zs = ref s.Zones[z];
                // A wave that isn't there (an empty slot, a built-in still being made) plays silence.
                var slot = Bank.Get(zs.Wave);
                if (slot == null || slot.Layers.Length == 0) continue;
                var layer = PickLayer(z, zs, slot, note);
                if (layer.Length < 2) continue;
                if (zs.Choke > 0 && !chokeDone[zs.Choke])
                {
                    chokeDone[zs.Choke] = true;
                    foreach (var v in voices) if (v.Serial != serial && v.ChokeGroup == zs.Choke) v.Choke();
                }
                var voice = Allocate();
                voice.Begin(key, note, z, layer, velGain * startGain[i], vel / 127f, serial, clock, s.Kit);
            }
        }

        /// <summary>A voice for a new start: a free one while under the polyphony; else the quietest released (or one-shot) one, else the oldest held.</summary>
        SamplerVoice Allocate()
        {
            int active = 0;
            SamplerVoice free = null;
            foreach (var v in voices)
            {
                if (v.IsActive || v.Pending) active++;
                else if (free == null) free = v;
            }
            if (free != null && active < Math.Max(1, S.Polyphony)) return free;
            SamplerVoice best = null;
            float bestLevel = float.MaxValue;
            long bestTime = long.MaxValue;
            foreach (var v in voices)
            {
                if (v.Held || v.Serial == serial || (!v.IsActive && !v.Pending)) continue;
                float level = v.Loudness;
                if (level < bestLevel - 1e-4f || (Math.Abs(level - bestLevel) <= 1e-4f && v.StartTime < bestTime))
                {
                    best = v;
                    bestLevel = level;
                    bestTime = v.StartTime;
                }
            }
            if (best != null) return best;
            foreach (var v in voices)
            {
                if (v.Serial == serial || (!v.IsActive && !v.Pending)) continue;
                if (v.StartTime < bestTime)
                {
                    best = v;
                    bestTime = v.StartTime;
                }
            }
            return best ?? free ?? voices[0];
        }

        void ReleaseKey(int key)
        {
            foreach (var v in voices)
                if (v.Key == key) v.Release();
        }

        public void NoteOff(int key)
        {
            clock++;
            foreach (var v in voices)
            {
                if (v.Key != key || !v.Held) continue;
                if (sustainDown) v.Sustained = true;
                else v.Release();
            }
        }

        public void ModWheel(float value) { }
        public void PitchBend(float value) => bend = Math.Clamp(value, -1f, 1f);
        public void Aftertouch(float value) { }

        public void Sustain(bool down)
        {
            if (sustainDown == down) return;
            sustainDown = down;
            if (down) return;
            foreach (var v in voices)
                if (v.Sustained) v.Release();
        }

        /// <summary>Releases every note (transport stop); <paramref name="hard"/> silences everything within a few milliseconds.</summary>
        public void AllNotesOff(bool hard)
        {
            foreach (var v in voices)
            {
                if (hard) v.Kill();
                else v.Release();
            }
        }

        /// <summary>Renders a block (overwrites the buffers). Whether anything sounded.</summary>
        public bool Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            Array.Clear(left, 0, n);
            Array.Clear(right, 0, n);
            bool any = false;
            int pos = 0;
            while (pos < n)
            {
                bool fresh = segment <= 0;
                if (fresh) segment = ControlRate;
                int m = Math.Min(segment, n - pos);
                foreach (var v in voices)
                {
                    if (!v.IsActive && !v.Pending) continue;
                    v.Render(left, right, pos, m, fresh ? ControlRate : 0);
                    any = true;
                }
                segment -= m;
                pos += m;
            }
            for (int i = 0; i < n; i++)
            {
                float g = volume.Next();
                left[i] *= g;
                right[i] *= g;
            }
            OutMeter.Feed(left, right, n);
            return any;
        }

        /// <summary>Samples left in the current control segment (a voice starting now sets its ramps over them).</summary>
        internal int SegmentLeft => segment > 0 ? segment : ControlRate;
    }
}
