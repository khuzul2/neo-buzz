#nullable disable
// Neo Prism's engine (docs/specs/neo-prism.md): spectral pitch, scramble, freeze, blur and spectral delay on the shared Stft, with Mix and Amount
// from the shared FxStage (machines.md, "Mix and Amount").
//  - One chain per FFT size (1024, 2048, 4096, 8192), all built up front: the Stft, the processor with its buffers and its own FxStage (so its dry
//    path is delayed by its own latency, size − 1). Only the chosen size runs.
//  - A change of FFT size starts the new chain on the same input, lets it fill for a frame (so it has real output to give), then crossfades the
//    old chain's output into it over 30 ms and drops the old one: no gap, no click. Both chains read the same settings.
//  - Amount scales the pitch, the scramble amount, the blur time and smear, the delay times and the feedback from neutral (0 = nothing), so at 0 % the
//    wet path is out of the way and the output is the dry input delayed by the latency, bit for bit (FxStage).
//  - The audio thread allocates nothing; the editor reads the spectra through a triple buffer.

using System;
using System.Collections.Generic;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.Prism
{
    internal sealed class PrismEngine
    {
        public const int MaxBlock = 256;
        public static readonly int[] Sizes = { 1024, 2048, 4096, 8192 };
        public static readonly int MaxLatency = 8192 - 1;
        const double CrossfadeSeconds = 0.03;

        /// <summary>What Amount scales (docs/specs/machines.md, "Mix and Amount").</summary>
        public static readonly AmountRule[] Rules =
        {
            AmountRule.Lin(P.Semi, 0), AmountRule.Lin(P.Cents, 0),
            AmountRule.Lin(P.ScrAmount, 0),
            AmountRule.Lin(P.BlurTime, 0), AmountRule.Lin(P.Smear, 0),
            AmountRule.Lin(P.DelLow, 0), AmountRule.Lin(P.DelHigh, 0), AmountRule.Lin(P.DelFeedback, 0),
        };

        sealed class Chain
        {
            public readonly int Size;
            public readonly Stft Stft;
            public readonly PrismProcessor Proc;
            public readonly FxStage Fx;

            public Chain(int size, ParamValues raw, double rate, PrismSettings settings, TripleBuffer<PrismView> view)
            {
                Size = size;
                Stft = new Stft(size);
                Proc = new PrismProcessor(size, rate) { S = settings, View = view };
                Fx = new FxStage(raw, P.Amount, P.Mix, Rules, maxLatency: MaxLatency, maxBlock: MaxBlock);
                Fx.SetSampleRate(rate);
            }

            public int Latency => Size - 1;

            public void Reset()
            {
                Stft.Reset();
                Proc.Reset();
                Fx.Reset();
            }
        }

        readonly ParamValues raw;
        readonly PrismSettings settings = new PrismSettings();
        readonly TripleBuffer<PrismView> view = new TripleBuffer<PrismView>(() => new PrismView(), (a, b) => a.CopyTo(b));
        Chain[] chains;
        int active = 2, incoming = -1;
        int warm, xfPos, xfLen;
        double rate = 48000;
        readonly float[] bufL = new float[MaxBlock], bufR = new float[MaxBlock];
        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        // notes (a note wire or the track's column) and the song
        int held;
        readonly bool[] down = new bool[128];
        bool playing;
        double beat, spb = 24000;
        int serial;
        long lastPeriod = long.MinValue;
        int quiet;
        float outPeak;

        public PrismEngine(ParamValues values)
        {
            raw = values;
            Build(48000);
        }

        void Build(double sr)
        {
            rate = sr;
            chains = new Chain[Sizes.Length];
            for (int i = 0; i < Sizes.Length; i++) chains[i] = new Chain(Sizes[i], raw, sr, settings, view);
            active = Math.Clamp(raw.Choice(P.FftSize), 0, Sizes.Length - 1);
            incoming = -1;
            first = true;
        }

        bool first = true;

        public void SetSampleRate(float sr)
        {
            Build(sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            Reset();
        }

        public void Reset()
        {
            for (int i = 0; i < chains.Length; i++) chains[i].Reset();
            incoming = -1;
            held = 0;
            Array.Clear(down);
            lastPeriod = long.MinValue;
            quiet = 0;
            first = true;
            ReadSettings(chains[active].Fx.Effective);
            chains[active].Proc.Reset();
        }

        /// <summary>Samples from an input to the output now: the frame of the running size minus one. What the host compensates.</summary>
        public int Latency => chains[active].Latency;

        /// <summary>The FFT size running now (the chosen one, once a change has finished).</summary>
        public int Size => chains[active].Size;

        public bool Changing => incoming >= 0;

        /// <summary>The sections that are on now, as bits (pitch, scramble, freeze, blur, delay).</summary>
        public int Lights => chains[active].Proc.Lights;

        /// <summary>The song for this block: playing, the position in beats and the samples a beat takes.</summary>
        public void SetTransport(bool isPlaying, double beatPos, double samplesPerBeat)
        {
            playing = isPlaying;
            if (isPlaying) beat = beatPos;
            if (samplesPerBeat > 1) spb = samplesPerBeat;
        }

        public void NoteOn(int key)
        {
            key &= 127;
            if (!down[key]) { down[key] = true; held++; }
        }

        public void NoteOff(int key)
        {
            key &= 127;
            if (down[key]) { down[key] = false; held--; }
        }

        public void ClearNotes()
        {
            Array.Clear(down);
            held = 0;
        }

        public bool AnyNote => held > 0;

        /// <summary>Copies the newest spectra snapshot for the editor (any thread). True when it is new.</summary>
        public bool ReadView(PrismView into) => view.Read(into);

        /// <summary>The level of the last block's output, for resting when nothing is sounding.</summary>
        public bool Silent => quiet > 4;

        public void Process(float[] l, float[] r, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            var a = chains[active];
            a.Fx.Begin(l, r, n);
            if (first)
            {
                ReadSettings(a.Fx.Effective);
                a.Proc.Reset();
                first = false;
            }
            else ReadSettings(a.Fx.Effective);
            InMeter.Feed(l, r, n);

            // A new size asked for: start it, on the same input.
            int want = Math.Clamp(raw.Choice(P.FftSize), 0, Sizes.Length - 1);
            if (incoming < 0 && want != active) StartChange(want);
            else if (incoming >= 0 && want != incoming)
            {
                if (want == active) incoming = -1;
                else StartChange(want);
            }
            if (incoming >= 0)
            {
                Array.Copy(l, bufL, n);
                Array.Copy(r, bufR, n);
            }

            a.Stft.Process(l, r, 0, n, a.Proc);
            a.Fx.End(l, r, n, a.Latency);

            if (incoming >= 0)
            {
                var b = chains[incoming];
                b.Fx.Begin(bufL, bufR, n);
                b.Stft.Process(bufL, bufR, 0, n, b.Proc);
                b.Fx.End(bufL, bufR, n, b.Latency);
                if (warm > 0) warm -= n;
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        float c = xfPos >= xfLen ? 1f : (float)xfPos / xfLen;
                        // a raised-cosine ramp keeps the fade smooth at both ends
                        c = 0.5f - 0.5f * (float)PortableMath.Cos(Math.PI * c);
                        l[i] += (bufL[i] - l[i]) * c;
                        r[i] += (bufR[i] - r[i]) * c;
                        xfPos++;
                    }
                    if (xfPos >= xfLen)
                    {
                        active = incoming;
                        incoming = -1;
                    }
                }
            }

            OutMeter.Feed(l, r, n);
            float pk = 0;
            for (int i = 0; i < n; i++) pk = Math.Max(pk, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
            outPeak = pk;
            quiet = pk < 1e-6f ? quiet + 1 : 0;
            if (!playing) beat += n / spb;      // a stopped song still keeps the synced sections going
        }

        void StartChange(int want)
        {
            incoming = want;
            var b = chains[want];
            b.Reset();
            b.Proc.S = settings;
            // Its output is real once a frame has gone through (and the frame it holds is full).
            warm = b.Size + b.Size / 4;
            xfPos = 0;
            xfLen = Math.Max(1, (int)(CrossfadeSeconds * rate));
        }

        // ------------------------------------------------------------------------------------------------ settings

        static readonly double[] SyncBeats = { 0, 0.25, 0.5, 1, 2, 4, 8 };

        void ReadSettings(ParamValues v)
        {
            var s = settings;
            s.PitchOn = v.On(P.PitchOn);
            s.Semitones = v.Value(P.Semi) + v.Value(P.Cents) / 100.0;
            s.FormantKeep = v.On(P.FormantKeep);
            s.ScrOn = v.On(P.ScrOn);
            s.ScrAmount = v.F(P.ScrAmount);
            s.ScrWidth = (int)Math.Round(v.Value(P.ScrWidth));
            s.ScrSeed = (int)Math.Round(v.Value(P.ScrSeed));
            int sync = v.Choice(P.ScrSync);
            if (sync == 0) { s.ScrHz = v.Value(P.ScrRate); s.ScrBeats = 0; }
            else { s.ScrHz = 0; s.ScrBeats = SyncBeats[sync]; }
            s.Hold = v.F(P.Hold);
            s.BlurOn = v.On(P.BlurOn);
            s.BlurSeconds = v.Value(P.BlurTime) / 1000.0;
            s.Smear = (int)Math.Round(v.Value(P.Smear));
            s.DelayOn = v.On(P.DelayOn);
            double low = v.Value(P.DelLow) / 1000.0, high = v.Value(P.DelHigh) / 1000.0;
            int ds = v.Choice(P.DelSync);
            if (ds > 0)
            {
                double unit = SyncBeats[ds] * spb / rate;       // a note's length in seconds
                low = Math.Round(low / unit) * unit;
                high = Math.Round(high / unit) * unit;
            }
            s.DelLowS = low;
            s.DelHighS = high;
            s.Feedback = (float)Math.Min(0.99, v.Value(P.DelFeedback) / 100.0);
            s.Damping = v.F(P.DelDamping);
            s.Beat = beat;
            s.SamplesPerBeat = spb;
            // Freeze: the switch, a held note, or the song's period.
            bool req = raw.On(P.FreezeOn);
            int trig = raw.Choice(P.FreezeTrig);
            if (trig == 1 && held > 0) req = true;
            if (trig == 2)
            {
                double every = new[] { 1.0, 2.0, 4.0, 8.0, 16.0 }[Math.Clamp(raw.Choice(P.FreezeEvery), 0, 4)];   // in beats: 1, 2 beats, 1, 2, 4 bars
                double len = new[] { 0.25, 0.5, 1.0, 2.0, 4.0 }[Math.Clamp(raw.Choice(P.FreezeLen), 0, 4)];     // 1/16 … 1 bar, in beats
                long period = (long)Math.Floor(beat / every);
                if (period != lastPeriod)
                {
                    lastPeriod = period;
                    serial++;
                }
                if (beat - period * every < len) req = true;
            }
            s.FreezeReq = req;
            s.FreezeSerial = serial;
        }

        // ------------------------------------------------------------------------------------------------ for the tests and the editor

        /// <summary>The effective values of the running chain (Amount applied), for the editor's picture of the delay's curve.</summary>
        public ParamValues Effective => chains[active].Fx.Effective;

        public IReadOnlyDictionary<string, double> Readings() => new Dictionary<string, double>
        {
            ["latency_samples"] = Latency,
            ["fft_size"] = Size,
            ["output_peak_db"] = DspMath.GainToDb(Volatile.Read(ref outPeak)),
            ["sections_on"] = Lights,
        };
    }
}
