#nullable disable
// Neo Sampler (docs/specs/machines.md §5): one voice. It reads its layer at a position that moves
// by a step per sample (the pitch: 2^(semitones/12) × the wave's rate over the output's), in the
// layer's level that keeps the step at or under 2 (Interpolation); plays its zone's region forward
// or reversed; loops (the wave's own loop through the layer's playback copy, without a seam; or the
// zone's points, forward with an equal-power crossfade from before loop start, or back and forth);
// fades out over 2 ms before the region's end; runs its filter, filter envelope and amp envelope;
// and fades out in 3 ms when stolen (then starts the new note) or 5 ms when choked or killed.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.Sampler
{
    internal enum VoiceLoop { None, WaveCopy, Forward, PingPong }

    internal sealed class SamplerVoice
    {
        readonly SamplerEngine engine;
        readonly Adsr amp = new Adsr(), flt = new Adsr();
        Ladder ladderL, ladderR;
        Svf svfL, svfR;
        StealFade fade;
        float sr = 48000;
        int envVersion = -1;

        // What it plays.
        SampleLayer layer;
        float[][] dataL, dataR;
        int zone, note;
        float velocity, velGain;
        bool kit, oneShot, stereo;
        VoiceLoop loop;
        int dir = 1;
        double pos;
        bool playing;

        // A steal: the note to start when the fade ends.
        SampleLayer pendLayer;
        int pendKey, pendNote, pendZone;
        float pendVelGain, pendVelocity;
        long pendSerial, pendClock;
        bool pendKit, pendRelease;

        // Control values (every 16 samples).
        double step, scale;
        bool exact;
        int level;
        float[] kernel;
        double regionStart, regionEnd, ls, le, length;
        double xfade, invXfade, invEndFade;
        bool looping;
        float gainL, gainR, dgL, dgR;
        SamplerFilter filter;

        public SamplerVoice(SamplerEngine engine)
        {
            this.engine = engine;
        }

        public int Key { get; private set; } = -1;
        /// <summary>A gate note whose key is still down.</summary>
        public bool Held { get; private set; }
        /// <summary>Released while the sustain pedal was down: releases when it comes up.</summary>
        public bool Sustained { get; set; }
        public bool Pending { get; private set; }
        public long Serial { get; private set; }
        public long StartTime { get; private set; }
        public int ChokeGroup { get; private set; }
        public bool IsActive => playing || fade.Fading;
        public float Loudness => playing ? amp.Level * Math.Max(Math.Abs(gainL), Math.Abs(gainR)) : 0f;

        /// <summary>For the editor: the zone it plays (−1: none) and where, as a fraction of the wave.</summary>
        public volatile int InfoZone = -1, InfoNote = -1;
        public volatile float InfoPosition;

        /// <summary>The position in the wave (frames), for tests.</summary>
        internal double Position => pos;
        internal int Level => level;
        internal double Step => step;
        internal SampleLayer Layer => layer;

        public void SetSampleRate(float rate)
        {
            sr = rate;
            amp.SetSampleRate(rate);
            flt.SetSampleRate(rate);
            envVersion = -1;
        }

        /// <summary>A note on this voice: now, or (it is sounding) after a 3 ms fade.</summary>
        public void Begin(int key, int n, int z, SampleLayer l, float gain, float vel, long serial, long clock, bool isKit)
        {
            var zs = engine.S.Zones[z];
            bool shot = isKit || zs.OneShot;
            Key = key;
            Serial = serial;
            StartTime = clock;
            ChokeGroup = zs.Choke;
            Held = !shot;
            Sustained = false;
            if (playing && amp.Level > 0.0005f && !Pending)
            {
                Pending = true;
                pendRelease = false;
                pendLayer = l; pendKey = key; pendNote = n; pendZone = z; pendVelGain = gain; pendVelocity = vel;
                pendSerial = serial; pendClock = clock; pendKit = isKit;
                fade.Start(sr, SamplerEngine.StealSeconds);
                return;
            }
            if (Pending)
            {
                pendLayer = l; pendKey = key; pendNote = n; pendZone = z; pendVelGain = gain; pendVelocity = vel;
                pendSerial = serial; pendClock = clock; pendKit = isKit;
                pendRelease = false;
                return;
            }
            fade.Cancel();
            Start(n, z, l, gain, vel, isKit);
        }

        void Start(int n, int z, SampleLayer l, float gain, float vel, bool isKit)
        {
            var s = engine.S;
            ref var zs = ref s.Zones[z];
            layer = l;
            note = n;
            zone = z;
            velGain = gain;
            velocity = vel;
            kit = isKit;
            oneShot = isKit || zs.OneShot;
            stereo = l.Stereo;
            length = l.Length;
            dir = zs.Reverse ? -1 : 1;
            regionStart = Math.Clamp(Math.Floor(zs.Start * length), 0, length - 1);
            regionEnd = Math.Clamp(Math.Round(zs.End * length), regionStart + 1, length);
            loop = VoiceLoop.None;
            dataL = l.Left;
            dataR = l.Right;
            if (!oneShot)
            {
                if (zs.Loop == ZoneLoop.Wave && l.Loop != WaveLoop.None)
                {
                    if (dir > 0 && l.LoopEnd <= regionEnd && regionStart < l.LoopEnd)
                    {
                        loop = VoiceLoop.WaveCopy;
                        dataL = l.LoopLeft;
                        dataR = l.LoopRight;
                    }
                    else loop = l.Loop == WaveLoop.PingPong ? VoiceLoop.PingPong : VoiceLoop.Forward;
                }
                else if (zs.Loop == ZoneLoop.Forward) loop = VoiceLoop.Forward;
                else if (zs.Loop == ZoneLoop.PingPong) loop = VoiceLoop.PingPong;
            }
            pos = dir > 0 ? regionStart : regionEnd - 1;
            playing = true;
            Pending = false;
            ladderL.Reset(); ladderR.Reset();
            svfL.Reset(); svfR.Reset();
            amp.Reset();
            flt.Reset();
            ApplyEnvelopes(true);
            amp.Gate(true);
            flt.Gate(true);
            Control(0, true);
        }

        public void Release()
        {
            if (Pending)
            {
                if (!pendKit && !engine.S.Zones[pendZone].OneShot) pendRelease = true;
                Held = false;
                Sustained = false;
                return;
            }
            if (!playing || oneShot) return;
            Held = false;
            Sustained = false;
            amp.Gate(false);
            flt.Gate(false);
        }

        /// <summary>Choke group, all sound off: fades out in 5 ms, then stops.</summary>
        public void Choke()
        {
            if (!playing && !Pending) return;
            Pending = false;
            pendRelease = false;
            Held = false;
            Sustained = false;
            Key = -1;
            ChokeGroup = 0;
            fade.Start(sr, SamplerEngine.ChokeSeconds);
        }

        public void Kill() => Choke();

        void Stop()
        {
            playing = false;
            Held = false;
            Sustained = false;
            Key = -1;
            ChokeGroup = 0;
            amp.Reset();
            flt.Reset();
            InfoZone = -1;
            InfoNote = -1;
            layer = null;
        }

        void ApplyEnvelopes(bool force)
        {
            var s = engine.S;
            if (!force && envVersion == s.Version) return;
            envVersion = s.Version;
            amp.Set(s.AmpEnv[0], s.AmpEnv[1], s.AmpEnv[2], s.AmpEnv[3]);
            flt.Set(s.FltEnv[0], s.FltEnv[1], s.FltEnv[2], s.FltEnv[3]);
        }

        /// <summary>
        /// Control values for the next <paramref name="count"/> samples: the pitch and level, the
        /// region and loop, the filter, and the gains' ramps (<paramref name="initial"/>: no ramp).
        /// </summary>
        void Control(int count, bool initial)
        {
            var s = engine.S;
            ref var zs = ref s.Zones[zone];
            ApplyEnvelopes(false);

            int root = zs.Root == SamplerEngine.RootFromWave ? layer.Root : zs.Root;
            double semis = (kit ? 0 : note - root) + zs.Tune + engine.BendSemis;
            double ratio = PortableMath.Pow(2.0, semis / 12.0) * layer.Rate / sr;
            int k = 0;
            double r = ratio;
            while (r > 2.0 && k < layer.Levels - 1)
            {
                r *= 0.5;
                k++;
            }
            step = ratio;
            exact = ratio == 1.0;
            level = k;
            scale = 1.0 / (1 << k);
            kernel = Interpolation.Kernel(Interpolation.CutoffFor(r));

            // The region and the zone's loop follow their parameters while the note plays.
            regionStart = Math.Clamp(Math.Floor(zs.Start * length), 0, length - 1);
            regionEnd = Math.Clamp(Math.Round(zs.End * length), regionStart + 1, length);
            looping = false;
            if (loop == VoiceLoop.WaveCopy) looping = true;
            else if (loop != VoiceLoop.None)
            {
                bool fromWave = zs.Loop == ZoneLoop.Wave;
                double a = fromWave ? layer.LoopStart : Math.Round(zs.LoopStart * length);
                double b = fromWave ? layer.LoopEnd : Math.Round(zs.LoopEnd * length);
                ls = Math.Max(Math.Min(a, b), regionStart);
                le = Math.Min(Math.Max(a, b), regionEnd);
                looping = le - ls >= 2;
                if (looping && loop == VoiceLoop.Forward)
                {
                    double room = dir > 0 ? ls : length - le;
                    xfade = Math.Floor(Math.Min(Math.Min(zs.Xfade * (double)layer.Rate, le - ls), room));
                    invXfade = xfade >= 1 ? 1.0 / xfade : 0;
                }
                else xfade = 0;
            }
            invEndFade = 1.0 / Math.Max(1.0, step * sr * SamplerEngine.EndFadeSeconds);

            // Gains: level, velocity (and its crossfade share), the wave's own volume, pan.
            float g = zs.Level * velGain * layer.Gain;
            float pl, pr;
            if (stereo)
            {
                pl = zs.Pan > 0 ? 1f - zs.Pan : 1f;
                pr = zs.Pan < 0 ? 1f + zs.Pan : 1f;
            }
            else DspMath.PanGains(zs.Pan, out pl, out pr);
            float tl = g * pl, tr = g * pr;
            if (initial || count <= 0)
            {
                gainL = tl;
                gainR = tr;
                dgL = dgR = 0;
            }
            else
            {
                dgL = (tl - gainL) / count;
                dgR = (tr - gainR) / count;
            }

            // The filter: cutoff in octaves from the knob, the envelope, the keyboard and velocity.
            filter = s.Filter;
            float fenv = flt.Level;
            if (count > 0) flt.Advance(count);
            if (filter != SamplerFilter.Off)
            {
                float oct = fenv * s.FEnv + (note - 60) / 12f * s.FKey + s.FVel * (velocity - 1f);
                float cut = DspMath.Clamp(s.Cutoff * PortableMathF.Pow(2f, oct), 16f, sr * 0.48f);
                float gc = Svf.Prewarp(cut, sr);
                if (filter == SamplerFilter.Lp24 || filter == SamplerFilter.Lp12)
                {
                    ladderL.Set(gc, s.Reso);
                    ladderR.Set(gc, s.Reso);
                }
                else
                {
                    var mode = filter == SamplerFilter.BandPass ? SvfMode.BandPass : SvfMode.HighPass;
                    svfL.Mode = mode;
                    svfR.Mode = mode;
                    float kd = 2f - 1.96f * s.Reso;
                    svfL.SetFast(gc, kd);
                    svfR.SetFast(gc, kd);
                }
            }
            InfoZone = zone;
            InfoNote = note;
            InfoPosition = (float)(pos / Math.Max(1, length));
        }

        /// <summary>The layer at <paramref name="x"/> (frames of level 0) in the current level.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Read(double x, out float l, out float r)
        {
            var a = dataL[level];
            double y = x * scale;
            double max = a.Length - SampleLayer.Pad - Interpolation.Half - 1;
            double min = -(SampleLayer.Pad - Interpolation.Half);
            if (y < min) y = min;
            else if (y > max) y = max;
            if (exact)
            {
                // A step of 1 from a whole sample: the samples themselves.
                int i = (int)Math.Floor(y);
                if (i == y)
                {
                    l = a[i + SampleLayer.Pad];
                    r = stereo ? dataR[level][i + SampleLayer.Pad] : l;
                    return;
                }
            }
            if (stereo) Interpolation.Read(a, dataR[level], y, kernel, out l, out r);
            else
            {
                l = Interpolation.Read(a, y, kernel);
                r = l;
            }
        }

        /// <summary>Renders into the buffers; <paramref name="segment"/> &gt; 0 starts a control segment of that length first.</summary>
        public void Render(float[] left, float[] right, int offset, int count, int segment)
        {
            if (segment > 0 && playing) Control(segment, false);
            var s = engine.S;
            bool lp12 = filter == SamplerFilter.Lp12;
            for (int i = 0; i < count; i++)
            {
                if (!playing && !fade.Fading) break;
                float g = 1f;
                if (fade.Fading)
                {
                    if (!fade.Next(out float fg))
                    {
                        // The fade is over: a stolen voice starts its new note, a choked one stops.
                        if (Pending)
                        {
                            Pending = false;
                            Key = pendKey;
                            Serial = pendSerial;
                            StartTime = pendClock;
                            Start(pendNote, pendZone, pendLayer, pendVelGain, pendVelocity, pendKit);
                            ChokeGroup = engine.S.Zones[pendZone].Choke;
                            Held = !oneShot;
                            if (pendRelease)
                            {
                                pendRelease = false;
                                Release();
                            }
                            lp12 = filter == SamplerFilter.Lp12;
                            continue;
                        }
                        Stop();
                        break;
                    }
                    g = fg;
                }
                if (!playing) break;

                Read(pos, out float a, out float b);
                if (looping && loop == VoiceLoop.Forward && xfade >= 1)
                {
                    // The crossfade: the loop's end blends into the samples before its start (after its end, reversed).
                    double t = dir > 0 ? (pos - (le - xfade)) * invXfade : ((ls + xfade) - pos) * invXfade;
                    if (t > 0)
                    {
                        Read(dir > 0 ? pos - (le - ls) : pos + (le - ls), out float c, out float d);
                        float tin = SamplerEngine.SinQuarter((float)t), tout = SamplerEngine.SinQuarter((float)(1 - t));
                        a = a * tout + c * tin;
                        b = b * tout + d * tin;
                    }
                }
                if (filter != SamplerFilter.Off)
                {
                    if (filter == SamplerFilter.Lp24 || lp12)
                    {
                        a = ladderL.Process(a, 1f, lp12);
                        b = stereo ? ladderR.Process(b, 1f, lp12) : a;
                    }
                    else
                    {
                        a = svfL.Process(a);
                        b = stereo ? svfR.Process(b) : a;
                    }
                }
                float env = amp.Next();
                g *= env;
                if (!looping)
                {
                    double left0 = dir > 0 ? regionEnd - pos : pos - regionStart;
                    double eg = left0 * invEndFade;
                    if (eg < 1) g *= (float)Math.Max(0, eg);
                }
                gainL += dgL;
                gainR += dgR;
                left[offset + i] += a * gainL * g;
                right[offset + i] += b * gainR * g;

                // Move on, looping or ending.
                pos += dir * step;
                if (looping)
                {
                    switch (loop)
                    {
                        case VoiceLoop.WaveCopy:
                            if (pos >= layer.LoopJumpAt) pos -= layer.LoopJumpBy;
                            break;
                        case VoiceLoop.Forward:
                            if (dir > 0) { if (pos >= le) pos -= le - ls; }
                            else if (pos < ls) pos += le - ls;
                            break;
                        default:
                            if (dir > 0 && pos >= le) { pos = 2 * le - pos; dir = -1; }
                            else if (dir < 0 && pos < ls) { pos = 2 * ls - pos; dir = 1; }
                            break;
                    }
                }
                else if (dir > 0 ? pos >= regionEnd : pos < regionStart)
                {
                    Stop();
                    break;
                }
                if (!amp.IsActive && !fade.Fading)
                {
                    Stop();
                    break;
                }
            }
            ladderL.Flush(); ladderR.Flush();
            svfL.Flush(); svfR.Flush();
        }
    }
}
