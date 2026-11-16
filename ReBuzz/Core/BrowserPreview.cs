// Fork (docs/specs/browser.md, BR1): the browser's audio preview. One sample file at a time is decoded off the audio thread and mixed into the
// master by the work loop (BrowserPreview.MixInto, after the wave preview, before the master tap's neighbours), raw from the start at
// its own rate; or, with Sync and the transport running, a loop played in time with the song through the warp engine (ReBuzz/Core/Clips:
// ClipVoice, the Session View's): its tempo from the index (the file or its name) or the engine's tempo detection, and its beat
// phase the song's, so the loop's downbeat lands on a beat of the song.
// The cue output: not routed (it is a machine the Session View connects tracks to); the preview plays through the master. Audio thread:
// no allocation, no lock; the voice is one immutable object swapped by reference.

using System;
using System.IO;
using libsndfile;
using ReBuzz.Core.Clips;

namespace ReBuzz.Core
{
    internal sealed class BrowserPreview
    {
        /// <summary>The clip the work loop mixes (null: nothing playing).</summary>
        static volatile BrowserPreview active;

        public static BrowserPreview Active => active;

        sealed class Voice
        {
            public float[] Left, Right;
            public int Rate;
            public bool Loop, Sync;
            public AudioClipParams Params;
            public readonly ClipVoice Clip = new ClipVoice();
            public double Position;               // raw: the source sample of the next output sample
            public float[] L = new float[256], R = new float[256];
        }

        Voice voice;
        volatile bool playing;

        /// <summary>Decoded and ready.</summary>
        public bool Loaded => voice != null;
        public double Seconds { get; private set; }
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public string Path { get; private set; }

        /// <summary>The tempo to play in sync at: from the index or the file's name, else the engine's tempo detection (0 until loaded).</summary>
        public double Bpm { get; private set; }

        /// <summary>The source's peaks, for the waveform strip: <paramref name="bins"/> values 0 to 1.</summary>
        float[] full;

        public bool IsPlaying => playing && active == this;

        /// <summary>Decodes a file (at most two minutes), UI or worker thread. False when it can't be read.</summary>
        public bool Load(string path, double knownBpm)
        {
            Stop();
            try
            {
                if (!Decode(path, out var l, out var r, out int rate, out int channels)) return false;
                var v = new Voice { Left = l, Right = r, Rate = rate };
                voice = v;
                Path = path;
                Seconds = l.Length / (double)rate;
                SampleRate = rate;
                Channels = channels;
                Bpm = knownBpm;
                full = Peaks(l, r, 2048);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or DllNotFoundException)
            {
                voice = null;
                return false;
            }
        }

        public float[] Peaks(int bins)
        {
            var f = full ?? Array.Empty<float>();
            if (f.Length == 0 || bins <= 0) return new float[Math.Max(0, bins)];
            var o = new float[bins];
            for (int i = 0; i < bins; i++)
            {
                int a = (int)((long)i * f.Length / bins), b = Math.Max(a + 1, (int)((long)(i + 1) * f.Length / bins));
                float m = 0;
                for (int k = a; k < b && k < f.Length; k++) m = Math.Max(m, f[k]);
                o[i] = m;
            }
            return o;
        }

        static float[] Peaks(float[] l, float[] r, int bins)
        {
            var o = new float[bins];
            if (l.Length == 0) return o;
            for (int i = 0; i < bins; i++)
            {
                int a = (int)((long)i * l.Length / bins), b = Math.Max(a + 1, (int)((long)(i + 1) * l.Length / bins));
                float m = 0;
                for (int k = a; k < b && k < l.Length; k++) m = Math.Max(m, Math.Max(Math.Abs(l[k]), Math.Abs(r[k])));
                o[i] = Math.Min(1f, m);
            }
            return o;
        }

        /// <summary>
        /// Starts playing from the start. With <paramref name="sync"/> and the transport running, a loop in time with the song (its tempo found now
        /// if it is not known); else raw. Returns whether it played in sync.
        /// </summary>
        public bool Play(ReBuzzCore core, bool sync, bool loop)
        {
            var v = voice;
            if (v == null) return false;
            bool inSync = sync && core != null && core.Playing;
            Voice next = new Voice { Left = v.Left, Right = v.Right, Rate = v.Rate, Loop = loop || inSync, Sync = inSync };
            if (inSync)
            {
                var source = new AudioSource(v.Left, v.Right, v.Rate);
                double bpm = Bpm;
                if (bpm <= 0)
                {
                    var (b, _) = AudioAnalysis.Tempo(source);
                    bpm = Bpm = b;
                }
                double beats = Math.Max(1, Math.Round(source.Seconds * bpm / 60.0));
                var p = new AudioClipParams(source, WarpMap.Straight(source.Length, beats), WarpMode.Complex, null, 1f, 0);
                next.Clip.Prepare(p);
                next.Params = p;
            }
            voice = next;
            active = this;
            playing = true;
            return inSync;
        }

        public void Stop()
        {
            playing = false;
            if (active == this) active = null;
        }

        /// <summary>The work loop's call, every block: adds the preview to the master's buffer (Buzz's scale, interleaved stereo).</summary>
        internal static void MixInto(ReBuzzCore core, float[] buffer, int offset, int frames)
        {
            var a = active;
            if (a == null) return;
            a.Mix(core, buffer, offset, frames);
        }

        void Mix(ReBuzzCore core, float[] buffer, int offset, int frames)
        {
            var v = voice;
            if (v == null || !playing) return;
            int outRate = ReBuzzCore.masterInfo.SamplesPerSec > 0 ? ReBuzzCore.masterInfo.SamplesPerSec : 44100;
            if (v.Sync && v.Params != null && core.Playing)
            {
                var mi = ReBuzzCore.masterInfo;
                int spt = mi.SamplesPerTick > 0 ? mi.SamplesPerTick : 1;
                int tpb = mi.TicksPerBeat > 0 ? mi.TicksPerBeat : 4;
                double bps = 1.0 / ((double)spt * tpb);
                double songBeat = (core.SongCore.PlayPosition + mi.PosInTick / (double)spt) / tpb;
                double length = v.Params.EndBeat;
                int done = 0;
                while (done < frames)
                {
                    int n = Math.Min(frames - done, v.L.Length);
                    double beat = songBeat + done * bps;
                    double inLoop = beat - Math.Floor(beat / length) * length;
                    int toEnd = (int)Math.Max(1, Math.Min(n, Math.Ceiling((length - inLoop) / bps)));
                    Array.Clear(v.L, 0, toEnd);
                    Array.Clear(v.R, 0, toEnd);
                    v.Clip.Render(v.Params, inLoop, bps, outRate, v.L, v.R, 0, toEnd);
                    for (int i = 0; i < toEnd; i++)
                    {
                        buffer[offset + (done + i) * 2] += v.L[i] * 32768f;
                        buffer[offset + (done + i) * 2 + 1] += v.R[i] * 32768f;
                    }
                    done += toEnd;
                }
                return;
            }
            // Raw: from the start at the file's own rate, linear between samples.
            double step = v.Rate / (double)outRate;
            double pos = v.Position;
            int len = v.Left.Length;
            for (int i = 0; i < frames; i++)
            {
                if (pos >= len - 1)
                {
                    if (!v.Loop) { playing = false; if (active == this) active = null; break; }
                    pos -= len - 1;
                }
                int k = (int)pos;
                float f = (float)(pos - k);
                buffer[offset + i * 2] += (v.Left[k] + (v.Left[k + 1] - v.Left[k]) * f) * 32768f;
                buffer[offset + i * 2 + 1] += (v.Right[k] + (v.Right[k + 1] - v.Right[k]) * f) * 32768f;
                pos += step;
            }
            v.Position = pos;
        }

        /// <summary>How far a raw preview has played, 0 to 1 (the strip's playhead).</summary>
        public double Fraction
        {
            get
            {
                var v = voice;
                if (v == null || !playing || v.Left.Length == 0) return 0;
                return v.Sync ? 0 : Math.Min(1, v.Position / v.Left.Length);
            }
        }

        // ------------------------------------------------------------------ decoding

        const int MaxSeconds = 120;

        static bool Decode(string path, out float[] left, out float[] right, out int rate, out int channels)
        {
            left = right = Array.Empty<float>();
            rate = 0;
            channels = 0;
            if (string.Equals(System.IO.Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase) && DecodeWav(path, out left, out right, out rate, out channels))
                return true;
            using var sf = SoundFile.OpenRead(path);
            rate = sf.SampleRate;
            channels = sf.ChannelCount;
            long frames = Math.Min(sf.FrameCount > 0 ? sf.FrameCount : rate * (long)MaxSeconds, rate * (long)MaxSeconds);
            left = new float[frames];
            right = new float[frames];
            var buf = new float[4096 * channels];
            long at = 0;
            while (at < frames)
            {
                int want = (int)Math.Min(4096, frames - at);
                int got = (int)sf.ReadFloat(buf, want);
                if (got <= 0) break;
                for (int i = 0; i < got && at < frames; i++, at++)
                {
                    left[at] = buf[i * channels];
                    right[at] = channels > 1 ? buf[i * channels + 1] : buf[i * channels];
                }
            }
            if (at < frames) { Array.Resize(ref left, (int)at); Array.Resize(ref right, (int)at); }
            return at > 0;
        }

        /// <summary>PCM 8, 16, 24 and 32 bit and 32/64-bit float WAV, managed (no native decoder needed).</summary>
        static bool DecodeWav(string path, out float[] left, out float[] right, out int rate, out int channels)
        {
            left = right = Array.Empty<float>();
            rate = channels = 0;
            using var f = new FileStream(path, System.IO.FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(f);
            if (f.Length < 44 || br.ReadUInt32() != 0x46464952 /*RIFF*/) return false;
            br.ReadUInt32();
            if (br.ReadUInt32() != 0x45564157 /*WAVE*/) return false;
            int tag = 0, bits = 0, align = 0;
            while (f.Position + 8 <= f.Length)
            {
                uint id = br.ReadUInt32();
                long size = br.ReadUInt32();
                long next = f.Position + size + (size & 1);
                if (id == 0x20746D66 /*fmt */)
                {
                    tag = br.ReadUInt16(); channels = br.ReadUInt16(); rate = br.ReadInt32(); br.ReadInt32(); align = br.ReadUInt16(); bits = br.ReadUInt16();
                    if (tag == 0xFFFE && size >= 26) { br.ReadUInt16(); br.ReadUInt16(); br.ReadUInt32(); tag = br.ReadUInt16(); }
                }
                else if (id == 0x61746164 /*data*/)
                {
                    if (channels <= 0 || rate <= 0 || align <= 0 || !(tag == 1 || tag == 3)) return false;
                    long avail = Math.Min(size == 0xFFFFFFFF ? f.Length : size, f.Length - f.Position);
                    long frames = Math.Min(avail / align, rate * (long)MaxSeconds);
                    var data = br.ReadBytes((int)(frames * align));
                    frames = data.Length / align;
                    left = new float[frames];
                    right = new float[frames];
                    int bytes = bits / 8;
                    for (long i = 0; i < frames; i++)
                    {
                        float a = Sample(data, (int)(i * align), tag, bits), b = channels > 1 ? Sample(data, (int)(i * align) + bytes, tag, bits) : a;
                        left[i] = a;
                        right[i] = b;
                    }
                    return frames > 0;
                }
                f.Position = Math.Min(next, f.Length);
            }
            return false;
        }

        static float Sample(byte[] d, int at, int tag, int bits)
        {
            if (tag == 3) return bits == 64 ? (float)BitConverter.ToDouble(d, at) : BitConverter.ToSingle(d, at);
            return bits switch
            {
                8 => (d[at] - 128) / 128f,
                16 => BitConverter.ToInt16(d, at) / 32768f,
                24 => ((d[at] << 8 | d[at + 1] << 16 | d[at + 2] << 24) >> 8) / 8388608f,
                32 => BitConverter.ToInt32(d, at) / 2147483648f,
                _ => 0f,
            };
        }
    }
}
