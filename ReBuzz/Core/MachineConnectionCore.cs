using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Common.Settings;
using BuzzGUI.Interfaces;
using ReBuzz.Audio.BurstProtection;
using ReBuzz.Common;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;

namespace ReBuzz.Core
{
    internal class MachineConnectionCore : IMachineConnection, IBlockLevel
    {
        private static IntPtr connectionHandleCounter = 100;

        public IntPtr CMachineConnection { get; private set; }
        public IMachine Source { get; set; }
        public IMachine Destination { get; set; }

        int sourceChannel = 0;
        float panL = 1.0f;
        float panR = 1.0f;

        BurstProtectionEngine burstProtection;

        public int SourceChannel
        {
            get => sourceChannel;
            set { sourceChannels.Clear(); SetSourceChannel(value, true); }
        }

        int destinationChannel = 0;
        public int DestinationChannel { get => destinationChannel; set { destinationChannels.Clear(); SetDestinationChannel(value, true); } }

        HashSet<int> sourceChannels = new HashSet<int>();
        public IEnumerable<int> SourceChannels => sourceChannels;

        /// <summary>
        /// Fork (follow-ups): the channels for the audio thread, as arrays made anew (on the thread that changes them) whenever
        /// they change, in the sets' order. The multi-I/O paths enumerated the sets with Count() and ElementAt(), which boxed
        /// an enumerator each time: Neo Comp, the multi-I/O machine among the Neo ones, allocated about 320 bytes a buffer.
        /// </summary>
        internal int[] SourceChannelArray { get; private set; } = Array.Empty<int>();

        /// <inheritdoc cref="SourceChannelArray"/>
        internal int[] DestinationChannelArray { get; private set; } = Array.Empty<int>();

        public void SetSourceChannel(int channel, bool set)
        {
            if (set)
            {
                sourceChannels.Add(channel);
            }
            else
            {
                if (sourceChannels.Count > 1)
                {
                    // Ensure at least one channel is selected
                    sourceChannels.Remove(channel);
                }
            }
            int lowest = sourceChannels.Count > 0
                ? SourceChannels.Min()
                : 0; // Get the lowest channel or default to 0 if none are selected
            sourceChannel = lowest;
            SourceChannelArray = sourceChannels.ToArray();
            // Fork (docs/specs/multi-output.md): the output this wire now reads has its buffer before the audio thread asks for it.
            (Source as MachineCore)?.PrepareMultiIo();
            PropertyChanged.Raise(this, "SourceChannel");
            PropertyChanged.Raise(this, "SourceChannels");
        }

        HashSet<int> destinationChannels = new HashSet<int>();
        public IEnumerable<int> DestinationChannels => destinationChannels;
        public void SetDestinationChannel(int channel, bool set)
        {
            if (set)
            {
                destinationChannels.Add(channel);
            }
            else
            {
                // Ensure at least one channel is selected
                if (destinationChannels.Count > 1)
                {
                    destinationChannels.Remove(channel);
                }
            }

            int lowest = destinationChannels.Count > 0
                ? DestinationChannels.Min()
                : 0; // Get the lowest channel or default to 0 if none are selected
            destinationChannel = lowest;
            DestinationChannelArray = destinationChannels.ToArray();
            PropertyChanged.Raise(this, "DestinationChannel");
            PropertyChanged.Raise(this, "DestinationChannels");
        }

        readonly Interpolator interpolatorAmp = new Interpolator();

        /// <summary>Fork: jumps to the current amp without a ramp (for a new connection).</summary>
        internal void SnapAmp()
        {
            rampLength = 0;
            interpolatorAmp.SetTarget(amp, 0);
            clipGain = Source is MachineCore source ? source.ClipGain * source.CrossfadeGain : 1f;   // fork (0.5): no fade-in either
        }

        // Fork (engine hardening): Destination.Inputs.IndexOf(this) without building Inputs (automation
        // of an input's amp or pan sets it on the audio thread).
        int InputIndex() => Destination is MachineCore destination ? destination.VisibleInputIndexOf(this) : Destination.Inputs.IndexOf(this);

        // Fork (HS3): the amp ramp of a live restore. The engine's own ramp is four steps, one for each time the
        // machines are worked, and a block is cut into pieces at every tick and subtick, so its length in
        // samples depends on where the cuts fall (16-sample pieces made a fade-in of 64 samples: a click).
        // This ramp is a fixed number of samples, however the block is cut.
        float rampFrom, rampTo;
        int rampLength, rampPos;

        float RampValue(int pos) => pos >= rampLength ? rampTo : rampFrom + (rampTo - rampFrom) * pos / rampLength;

        /// <summary>Audio thread (AudioLock held): sets the amp and moves to it linearly over <paramref name="samples"/>, whatever the block sizes.</summary>
        internal void RampAmp(int value, int samples)
        {
            float from = rampLength > 0 ? RampValue(rampPos) : interpolatorAmp.Value;
            Amp = value;
            if (samples > 0 && from != value)
            {
                rampFrom = from;
                rampTo = value;
                rampPos = 0;
                rampLength = samples;
            }
            else rampLength = 0;
        }

        int amp = 0x4000;
        public int Amp
        {
            get
            {
                return amp;
            }
            set
            {
                if (amp != value)
                {
                    if (rampLength > 0) { interpolatorAmp.Value = RampValue(rampPos); rampLength = 0; }     // a change of its own takes over from where the ramp is
                    amp = value;
                    interpolatorAmp.SetTarget(value, 4); // 4 "steps"

                    if (Destination != null)
                    {
                        int index = InputIndex();
                        if (index != -1)
                        {
                            var paramAmp = Destination is MachineCore dm ? dm.ParameterGroupsList[0].ParametersList[0] : Destination.ParameterGroups[0].Parameters[0];     // fork (MM2): no facade copy, so a fader moves with 0 bytes
                            paramAmp.SetValue(index, amp);
                        }
                    }
                }
            }
        }

        int pan = 0x4000;
        public int Pan
        {
            get => pan;
            set
            {
                if (pan != value)
                {
                    pan = value;

                    if (Destination != null)
                    {
                        int index = InputIndex();
                        if (index != -1)
                        {
                            var paramPan = Destination is MachineCore dm ? dm.ParameterGroupsList[0].ParametersList[1] : Destination.ParameterGroups[0].Parameters[1];
                            paramPan.SetValue(index, pan);
                        }

                        double panScaled = pan / ((double)0x4000);
                        double pPan = panScaled / 2.0 * Math.PI / 2.0;
                        panL = (float)(2.0 / Math.Sqrt(2.0) * Math.Cos(pPan));
                        panR = (float)(2.0 / Math.Sqrt(2.0) * Math.Sin(pPan));

                        if (engineSettings.EqualPowerPanning)
                        {
                            if (Pan <= 0x4000)
                            {
                                panL = 1;
                            }
                            if (Pan >= 0x4000)
                            {
                                panR = 1;
                            }
                        }
                    }
                }
            }
        }

        public bool HasPan { get; set; }

        public void DoTap(Sample[] sampleBuffer, int nSamples, bool stereo, SongTime songTime)
        {
            if (Tap != null)
            {
                float[] samples = new float[nSamples * 2];
                int j = 0;
                for (int i = 0; i < nSamples; i++)
                {
                    samples[j] = sampleBuffer[i].L;
                    j++;
                    samples[j] = sampleBuffer[i].R;
                    j++;
                }

                //dispatcher.BeginInvoke(() =>
                //{
                    Tap?.Invoke(samples, stereo, songTime);
                //});
            }
        }

        // Tap-gated variant: fetches the SongTime only when a tap is actually
        // registered, so the common no-tap path allocates nothing (SongTime is a
        // class, and it was previously constructed per output connection per
        // chunk as the call-site argument - before the Tap null check inside
        // DoTap could run). Each invocation that does fire still gets its own
        // fresh SongTime instance, preserving the previous per-call semantics
        // for tap consumers.
        internal void DoTap(Sample[] sampleBuffer, int nSamples, bool stereo, ReBuzzCore buzz)
        {
            if (Tap == null)
                return;
            DoTap(sampleBuffer, nSamples, stereo, buzz.GetSongTime());
        }

        // Fork (docs/specs/multi-output.md): a wire's own mute and solo, for the mixer's strip of an extra output. Solo silences the other
        // wires of the same source machine (the way a generator's solo silences the other generators). The UI thread sets Muted and Soloed
        // and RefreshSilence works out which wires are silenced; the audio thread reads that one flag and fades to it over the block.
        bool muted, soloed;
        int silenced;
        float gateLast = 1f;

        /// <summary>This wire's own mute (the mixer's strip of an extra output).</summary>
        public bool Muted => muted;

        /// <summary>This wire's own solo: the source's other wires are silent while one is soloed.</summary>
        public bool Soloed => soloed;

        /// <summary>Whether the engine is silencing this wire now (muted, or another wire of its source is soloed and this one is not).</summary>
        public bool Silenced => Volatile.Read(ref silenced) != 0;

        /// <summary>Sets the wire's mute or solo (UI thread); the source's wires' silence follows.</summary>
        public void SetFlag(bool solo, bool on)
        {
            if (solo) soloed = on; else muted = on;
            RefreshSilence(Source);
            PropertyChanged.Raise(this, solo ? "Soloed" : "Muted");
        }

        /// <summary>Works out which of a machine's output wires are silenced by wire mutes and solos.</summary>
        internal static void RefreshSilence(IMachine source)
        {
            if (source == null) return;
            bool any = false;
            foreach (var o in source.Outputs) if (o is MachineConnectionCore c && c.soloed) any = true;
            foreach (var o in source.Outputs)
                if (o is MachineConnectionCore c)
                    Volatile.Write(ref c.silenced, c.muted || (any && !c.soloed) ? 1 : 0);
        }

        float ampStart;
        float ampCurrent;
        float ampStep;
        float clipGain = 1f;     // fork (0.5): the source machine's ClipGain, as of the last block
        internal void PreUpdateBufferActions(int nSamples)
        {
            // Fork (0.5): the source's session clip gain (launch velocity) ramps over the block
            // along with the amp.
            float gainStart = clipGain;
            clipGain = Source is MachineCore source ? source.ClipGain * source.CrossfadeGain : 1f;   // 0.6: and the crossfader
            ampStart = interpolatorAmp.Value / 0x4000 * gainStart;
            ampCurrent = interpolatorAmp.Tick() / 0x4000 * clipGain;
            if (rampLength > 0)
            {
                ampStart = RampValue(rampPos) / 0x4000 * gainStart;
                rampPos += nSamples;
                ampCurrent = RampValue(rampPos) / 0x4000 * clipGain;
                if (rampPos >= rampLength) { rampLength = 0; interpolatorAmp.Value = rampTo; }
            }
            // Fork (docs/specs/multi-output.md): a wire's own mute fades over the block (the factors are exactly 1 while it isn't muted).
            float gate = Volatile.Read(ref silenced) != 0 ? 0f : 1f;
            ampStart *= gateLast;
            ampCurrent *= gate;
            gateLast = gate;
            // Fork: ramp from the block's start amp to its end amp. Upstream had the difference
            // the wrong way round, so during an amp change each block ramped away from the target
            // and the next one jumped back: a click per block while a fader moves
            // (SessionMixerTests.AmpChangesRampSmoothly).
            ampStep = (ampCurrent - ampStart) / nSamples;
        }

        internal void UpdateBuffer(Sample[] samples, int nSamples)
        {
            // Zero-latency fast path: with no delay and none on the way (docs/specs/mastering-tools.md
            // §4.1), the amp/pan-scaled samples go straight into buffer, as before the ring existed.
            if (!Ring.Live)
            {
                for (int i = 0; i < nSamples; i++)
                {
                    buffer[i].L += samples[i].L * ampStart * panL;
                    buffer[i].R += samples[i].R * ampStart * panR;
                    ampStart += ampStep;
                }
            }
            else
            {
                // Latency-compensated path: into the ring at the block's position (ClearBuffer cleared
                // it); PostUpdateBufferActions reads the delayed tap out. Positions wrap with the ring's
                // mask (a power of two). The same adds as before, so a settled delay renders as it did.
                var ring = Ring.Ring;
                int mask = Ring.Mask;
                int w = Ring.Position;
                for (int i = 0; i < nSamples; i++)
                {
                    ring[w].L += samples[i].L * ampStart * panL;
                    ring[w].R += samples[i].R * ampStart * panR;
                    w = (w + 1) & mask;
                    ampStart += ampStep;
                }
            }
        }

        internal void PostUpdateBufferActions(int nSamples)
        {
            // The delayed tap out of the ring (crossfading while a delay changes), or the block passed directly.
            if (Ring.Live) Ring.Read(buffer, nSamples);
            else Ring.Passed();

            burstProtection.Process(buffer, 0, nSamples, true, true);

            if (Volatile.Read(ref peakMeterUsers) > 0)
                MeasurePeak(nSamples);

            // Fork (neo-patch.md §3): the Neo Envelope's follower reads the level of the block just delivered, while one runs.
            if (ModulationBoard.Followers > 0)
                MeasureBlockPeak(nSamples);
        }

        float blockPeak;

        /// <summary>The peak of the last block this connection delivered (full scale 1.0); measured only while a follower runs.</summary>
        public float BlockPeak => Volatile.Read(ref blockPeak);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        void MeasureBlockPeak(int nSamples)
        {
            float peak = 0;
            for (int i = 0; i < nSamples; i++)
            {
                float a = Math.Abs(buffer[i].L);
                if (a > peak) peak = a;
                a = Math.Abs(buffer[i].R);
                if (a > peak) peak = a;
            }
            Volatile.Write(ref blockPeak, peak * (1f / 32768f));
        }

        // Fork addition (docs/specs/session-view.md 0.4): post-fader peak of what this
        // connection delivers, for the Session View's meters. Measured on the audio thread only
        // while a meter is shown (one pass over the block, no allocation); the UI takes the peak
        // since its last look with TakePeak.
        static int peakMeterUsers;
        float peakL;
        float peakR;

        /// <summary>Turns peak metering of all connections on (+1) or off (-1); counted, for several views.</summary>
        internal static void UsePeakMeters(bool use)
        {
            if (use) Interlocked.Increment(ref peakMeterUsers);
            else if (Interlocked.Decrement(ref peakMeterUsers) < 0) Interlocked.Exchange(ref peakMeterUsers, 0);
        }

        internal static bool PeakMetersOn => Volatile.Read(ref peakMeterUsers) > 0;

        void MeasurePeak(int nSamples)
        {
            float l = peakL, r = peakR;
            for (int i = 0; i < nSamples; i++)
            {
                float a = Math.Abs(buffer[i].L);
                if (a > l) l = a;
                a = Math.Abs(buffer[i].R);
                if (a > r) r = a;
            }
            peakL = l;
            peakR = r;
        }

        /// <summary>Peak (sample units, 32768 = 0 dBFS) per channel since the last call; resets it.</summary>
        internal (float Left, float Right) TakePeak() =>
            (Interlocked.Exchange(ref peakL, 0f), Interlocked.Exchange(ref peakR, 0f));

        internal void ClearBuffer(int nSamples)
        {
            for (int i = 0; i < nSamples; i++)
            {
                buffer[i].L = 0;
                buffer[i].R = 0;
            }

            Ring.Clear(nSamples);
        }

        public MachineConnectionCore(IUiDispatcher dispatcher, EngineSettings engineSettings)
        {
            interpolatorAmp.Value = amp;

            this.engineSettings = engineSettings;
            CMachineConnection = connectionHandleCounter++;
            this.dispatcher = dispatcher;

            burstProtection = new BurstProtectionEngine(Global.Buzz as ReBuzzCore, false);
        }

        public MachineConnectionCore(MachineCore source, IEnumerable<int> sourceChannels, MachineCore destination, IEnumerable<int> destinationChannels, int amp, int pan, IUiDispatcher dispatcher, EngineSettings engineSettings)
        {
            this.engineSettings = engineSettings;
            this.dispatcher = dispatcher;
            Source = source;
            Destination = destination;

            foreach (var channel in sourceChannels)
            {
                SetSourceChannel(channel, true);
            }

            foreach (var channel in destinationChannels)
            {
                SetDestinationChannel(channel, true);
            }

            Amp = amp;
            Pan = pan;
        }

        internal void ClearEvents()
        {
            Tap = null;
            PropertyChanged = null;
            burstProtection.Release();
        }

        /// <summary>
        /// Fork (docs/specs/mastering-tools.md §4.1): the delay machine delay compensation gives this connection, changed by a
        /// crossfade (or at once while nothing is in flight) instead of reallocating and resetting a buffer, which dropped what
        /// was in flight on every recompute, changed or not. Grown on the UI thread; the audio lock is held by the caller.
        /// </summary>
        internal readonly LatencyRing Ring = new LatencyRing();

        Sample[] buffer = new Sample[256];
        private readonly IUiDispatcher dispatcher;
        private readonly EngineSettings engineSettings;

        public Sample[] Buffer { get
            {
                return buffer;
            } 
        }

        public event Action<float[], bool, SongTime> Tap;
        public event PropertyChangedEventHandler PropertyChanged;
    }
}
