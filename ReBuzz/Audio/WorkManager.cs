using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Common.Settings;
using BuzzGUI.Interfaces;
using ReBuzz.Audio.Engine;
using ReBuzz.Common;
using ReBuzz.Core;
using ReBuzz.MachineManagement;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ReBuzz.Audio
{
    internal class WorkManager
    {
        // Update this struct and array in the beginning of Thread read.
        // This is optimization. Avoids memory allocations and speeds up data copy.

        [StructLayout(LayoutKind.Sequential)]
        public struct BuzzMasterInfo
        {
            public int BeatsPerMin;
            public int TicksPerBeat;
            public int SamplesPerSec;
            public int SamplesPerTick;
            public int PosInTick;
            public float TicksPerSec;
        };

        [StructLayout(LayoutKind.Sequential)]
        public struct BuzzSubTickInfo
        {
            public int SubTicksPerTick;
            public int CurrentSubTick;
            public int SamplesPerSubTick;
            public int PosInSubTick;
        };

        internal static BuzzMasterInfo MasterInfoStruct;
        unsafe internal static byte[] MasterInfoData = new byte[sizeof(BuzzMasterInfo)];
        internal static BuzzSubTickInfo SubTickInfoStruct;
        unsafe internal static byte[] SubTickInfoData = new byte[sizeof(BuzzSubTickInfo)];

        bool multiThreadingEnabled = false;

        private readonly ReBuzzCore buzzCore;
        readonly WorkThreadEngine workEngine;

        readonly int algorithm;

        // Use HashSet to ensure do duplicate list items
        readonly HashSet<MachineCore> workSet = new HashSet<MachineCore>();
        private bool stopped;
        readonly bool SpeedAdjustLinear = false;

        float[] playWaveBuffer = new float[256 * 2];
        // Audio-thread-owned reusable buffer for PlayPatternColumnEvents (zero-alloc
        // GetEventsInto). Single audio thread, fully consumed each column iteration.
        // Fork (follow-ups): sized ahead, so the transport's first play doesn't grow them on the audio thread.
        readonly List<PatternEvent> playColumnEvents = new List<PatternEvent>(256);

        // Fork (Session View): patterns given a play position by the last UpdatePatternPositions
        // call, each once - session clips and arranged patterns. Audio thread only.
        readonly List<PatternCore> activePatterns = new List<PatternCore>(256);
        readonly HashSet<PatternCore> activePatternSet = new HashSet<PatternCore>(256);

        public WorkManager(ReBuzzCore buzzCore, WorkThreadEngine workEngine, int algorithm, EngineSettings settings)
        {
            this.buzzCore = buzzCore;
            this.workEngine = workEngine;
            this.algorithm = algorithm;
            engineSettings = settings;
            buzzCore.PrebuildWorkOrder += PrebuildOrder;       // every work manager the song has had builds its own order

            workOneList = WorkOneList;
            workOneGroups = WorkOneGroups;
            workOneRecursive = WorkOneRecursive;

            var master = buzzCore.SongCore.MachinesList.FirstOrDefault(m => m.DLL.Info.Type == MachineType.Master);
            var masterGlobalParameters = master.ParameterGroupsList[1].ParametersList;
        }

        internal void CopyMasterInfo() => CopyEngineMasterInfo();

        /// <summary>
        /// Fork: a tick has copied the master info (<see cref="MasterInfoData"/>) in this process. Until one has, it is all
        /// zeros (no audio device: the agent server, the UI gallery, a test host), and native machines set up meanwhile were
        /// told 0 Hz, 0 BPM and 0 samples a tick (see <see cref="CopyEngineMasterInfo"/>).
        /// </summary>
        internal static volatile bool MasterInfoCopied;

        /// <summary>
        /// The engine's master info (tempo, rate, tick length, position) into what native machines are sent: each tick, and,
        /// Fork, for a native machine's Init before the engine has ticked (UIMessage.UIInit). Without it a machine set up
        /// before the first tick computed its state for 0 Hz: Jeskola Delay's and Cross Delay's lines were 0 samples long, and
        /// their first Work looped for ever (its host stopped answering, the UI gallery's "Jeskola Delay stopped working"), and
        /// FSM Infector played silence until the song was opened again (a process's first render, native-songs' warm-up).
        /// </summary>
        internal static void CopyEngineMasterInfo()
        {
            var masterInfo = ReBuzzCore.masterInfo;
            MasterInfoStruct.BeatsPerMin = masterInfo.BeatsPerMin;
            MasterInfoStruct.TicksPerBeat = masterInfo.TicksPerBeat;
            MasterInfoStruct.SamplesPerSec = masterInfo.SamplesPerSec;
            MasterInfoStruct.SamplesPerTick = masterInfo.SamplesPerTick;
            MasterInfoStruct.PosInTick = masterInfo.PosInTick;
            MasterInfoStruct.TicksPerSec = masterInfo.TicksPerSec;

            // Update array
            MasterInfoData = Utils.SerializeValueTypeChangePointer(MasterInfoStruct, ref MasterInfoData);
            MasterInfoCopied = true;
        }

        internal void CopySubTickInfo()
        {
            var subtickInfo = ReBuzzCore.subTickInfo;
            if (engineSettings.SubTickTiming)
            {   
                SubTickInfoStruct.CurrentSubTick = subtickInfo.CurrentSubTick;
                SubTickInfoStruct.PosInSubTick = subtickInfo.PosInSubTick;
                SubTickInfoStruct.SubTicksPerTick = subtickInfo.SubTicksPerTick;
                SubTickInfoStruct.SamplesPerSubTick = subtickInfo.SamplesPerSubTick;
            }
            else
            {
                SubTickInfoStruct.CurrentSubTick = 0;
                SubTickInfoStruct.PosInSubTick = 0;
                SubTickInfoStruct.SubTicksPerTick = 0;
                SubTickInfoStruct.SamplesPerSubTick = 0;
            }

            // Update array
            SubTickInfoData = Utils.SerializeValueTypeChangePointer(SubTickInfoStruct, ref SubTickInfoData);
        }

        internal int workBufferOffset;
        // Avoid new object creation to minimize GC.
        /// <summary>Diagnosis only (tests): told which stage of a block allocated on the audio thread, and how much. Null in normal use.</summary>
        internal static Action<string, long> StageProbe;
        long markBase;
        void Mark(string stage)
        {
            var p = StageProbe;
            if (p == null) return;
            long now = GC.GetAllocatedBytesForCurrentThread();
            if (now != markBase) p(stage, now - markBase);
            markBase = now;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public int MainAudioFillBuffer(float[] buffer, int offset, int count)
        {
            // Override audio driver and call workManager.ThreadRead outside of Read
            if (stopped)
            {
                // Array.Clear(buffer, offset, count) is wrongly casted when using ASIO
                for (int i = 0; i < count; i++)
                {
                    buffer[offset + i] = 0;
                }
                return count;
            }

            lock (ReBuzzCore.AudioLock)
            {
                long time = Stopwatch.GetTimestamp();

                buzzCore.AudioEngine.ClearChannels();

                multiThreadingEnabled = engineSettings.Multithreading;

                var subTickInfo = ReBuzzCore.subTickInfo;
                var masterInfo = ReBuzzCore.masterInfo;

                // Fork (0.8): the audio clock against host time, for following Link or MIDI clock.
                var sync = buzzCore.SyncClock;
                sync.OnCallback(global::ReBuzz.Core.Sync.HostClock.Micros(time), masterInfo.SamplesPerSec);
                double latencyMicros = masterInfo.SamplesPerSec > 0 ? buzzCore.OutputLatencySamples * 1e6 / masterInfo.SamplesPerSec : 0;

                int reminingBuffer = count;
                workBufferOffset = offset;

                while (reminingBuffer > 0)
                {   
                    int framesToProcess = Math.Min(reminingBuffer / 2, 256);

                    // More accurate samples per tick?
                    //if (engineSettings.AccurateBPM)
                    //{
                    //    UpdateMasterSamplesPerTick();
                    //}

                    if (masterInfo.PosInTick == 0)
                    {
                        var grooveMul = GetGrooveMul();
                        // Fork (0.6): a held tempo nudge shortens or stretches the tick.
                        float nudge = buzzCore.TempoNudge;
                        // Fork (0.8 groundwork): ticks are whole samples, so the fraction left
                        // over is carried to the next tick: the tempo is exact on average (at
                        // 44.1 kHz and 120 BPM a tick is 5512.5 samples; dropping the half ran
                        // 163 ms ahead in 30 minutes). From the first tick of a start, the same.
                        double exact = masterInfo.AverageSamplesPerTick * grooveMul / nudge + tickRemainder;
                        int samples = Math.Max(1, (int)Math.Floor(exact));
                        tickRemainder = buzzCore.Playing ? Math.Clamp(exact - samples, 0, 0.999999) : 0;
                        // Fork (0.8): following Link or MIDI clock, the tick ends where the clock's
                        // phase says (groove and nudge give way); a start waits for its bar line.
                        var session = buzzCore.SessionCore;
                        int synced = sync.TickLength(samples, buzzCore.Playing, buzzCore.PlayRequested && !buzzCore.Playing,
                            session.Clock, session.GridOrigin, buzzCore.SongCore.PlayPosition, masterInfo.TicksPerBeat, session.BeatsPerBar, latencyMicros);
                        if (synced != samples)
                        {
                            samples = synced;
                            tickRemainder = 0;
                        }
                        masterInfo.SamplesPerTick = samples;
                    }

                    if (StageProbe != null) markBase = GC.GetAllocatedBytesForCurrentThread();
                    UpdateMasterParams();

                    // Initiate master info for Audio Messages
                    CopyMasterInfo();

                    // Update SubTick
                    UpdateSubTickLength();

                    // Initiate master info for Audio Messages
                    CopySubTickInfo();

                    // Ensure we don't go over tick
                    if (masterInfo.PosInTick + framesToProcess > masterInfo.SamplesPerTick)
                    {
                        // Make sure tick will be zero
                        framesToProcess = masterInfo.SamplesPerTick - masterInfo.PosInTick;
                        if (framesToProcess < 0)
                            return 0;
                    }

                    // Ensure we don't go over subtick
                    if (engineSettings.SubTickTiming && 
                        subTickInfo.PosInSubTick + framesToProcess > subTickInfo.SamplesPerSubTick)
                    {
                        // Make sure tick will be zero
                        framesToProcess = subTickInfo.SamplesPerSubTick - subTickInfo.PosInSubTick;
                        if (framesToProcess < 0)
                            return 0;
                    }

                    // Fork (history-snapshots.md HS3): a live restore's parameter glide moves where machines take values.
                    var restore = buzzCore.ActiveRestore;
                    if (restore != null)
                        restore.OnBlock(framesToProcess, masterInfo.PosInTick == 0 || (engineSettings.SubTickTiming && subTickInfo.PosInSubTick == 0));

                    Mark("tick setup");
                    // Call Pattern Column Events
                    UpdatePatternColumnEvents(framesToProcess);
                    Mark("pattern column events");

                    // Update Managed Machine host info
                    buzzCore.MachineManager.UpdateMasterAndSubTickInfoToHost();

                    // Tick all machines first and then call tick again if sendcontrolchanges flag set?
                    Mark("master info to host");
                    CallTick();
                    Mark("CallTick");

                    // Fork (docs/specs/mastering-tools.md §4.1): a latency a machine reported at its tick reaches the
                    // delay compensation before this block's work (the same samples every run).
                    buzzCore.ApplyLatencyIfDirty();

                    // Call work
                    ReadWork(buffer, workBufferOffset, framesToProcess);
                    Mark("ReadWork");

                    // Mix waves playing from wavetable 
                    if (buzzCore.SongCore.WavetableCore.IsPlayingWave())
                    {
                        buzzCore.SongCore.WavetableCore.GetPlayWaveSamples(playWaveBuffer, offset, framesToProcess);
                        // Elementwise multiply-add in single precision with the
                        // same two per-element roundings as the scalar loop
                        // (separate * then +, matching operand order; RyuJIT
                        // performs no FP contraction, so neither form uses FMA;
                        // no reduction) => bit-exact. ("wvw" to avoid CS0136
                        // with the output-scale block's "vw" below.)
                        int n = framesToProcess * 2;
                        Vector<float> waveMulVec = new Vector<float>(32768.0f);
                        int wvw = Vector<float>.Count;
                        int i = 0;
                        for (; i <= n - wvw; i += wvw)
                        {
                            Vector<float> pv = new Vector<float>(playWaveBuffer, i);
                            Vector<float> bv = new Vector<float>(buffer, workBufferOffset + i);
                            (bv + pv * waveMulVec).CopyTo(buffer, workBufferOffset + i);
                        }
                        for (; i < n; i++)
                        {
                            buffer[workBufferOffset + i] += playWaveBuffer[i] * 32768.0f;
                        }
                    }

                    // Master Tap
                    buzzCore.MasterTapSamples(buffer, workBufferOffset, framesToProcess * 2);
                    buzzCore.MasterTapSamplesMultiChannel(buffer, workBufferOffset, framesToProcess * 2);
                    // Session View (fork, 1.0): audio takes: resampling records what was just mixed.
                    global::ReBuzz.Core.Session.SessionClipEngine.AfterMix(buzzCore, buffer, workBufferOffset, framesToProcess);

                    // Session View (fork, 0.7): the metronome, after the tap (heard, not recorded).
                    Mark("wave, taps, clip engine");
                    buzzCore.SessionCore.MixMetronome(buffer, workBufferOffset, framesToProcess);
                    Mark("metronome");

                    workBufferOffset += framesToProcess * 2;
                    reminingBuffer -= framesToProcess * 2;

                    subTickInfo.PosInSubTick += framesToProcess;
                    masterInfo.PosInTick += framesToProcess;

                    if (subTickInfo.PosInSubTick >= subTickInfo.SamplesPerSubTick)
                    {
                        subTickInfo.PosInSubTick = 0;
                        subTickInfo.CurrentSubTick++;
                    }

                    if (masterInfo.PosInTick >= masterInfo.SamplesPerTick)
                    {
                        masterInfo.PosInTick = 0;

                        // Reset subtick counter at tick boundary
                        subTickInfo.CurrentSubTick = 0;

                        // Advance song position (Buzz behavior). Fork (0.8): a start following
                        // a clock waits for the tick that ends on its bar line.
                        if (!(sync.StartDue && buzzCore.StartPlaying()))
                        {
                            if (buzzCore.Playing && buzzCore.SoloPattern == null)
                            {
                                buzzCore.SongCore.UpdatePlayPosition(1);

                                // Session View (fork): advance the session clock, apply due launches/stops.
                                // UpdatePlayPosition may have stopped the transport at the song end.
                                if (buzzCore.Playing)
                                    buzzCore.SessionCore.OnTick(transportJustStarted: false);
                            }
                        }
                        else if (buzzCore.SoloPattern == null)
                        {
                            // Session View (fork): the session clock starts with the transport.
                            buzzCore.SessionCore.OnTick(transportJustStarted: true);
                        }
                    }

                    Mark("tick end (position, session OnTick)");
                    // Update position info in patterns
                    UpdatePatternPositions(framesToProcess);
                    Mark("pattern positions");

                    // Update frame count
                    unchecked { ReBuzzCore.GlobalState.AudioFrame += framesToProcess; }
                    sync.OnFrames(framesToProcess);
                }

                float audioOutMul = 1 / 32768.0f;
                // Vectorised scale to audio output range. Each lane computes the identical
                // single-precision (in[i] * audioOutMul); no reduction, no FMA => bit-exact
                // vs the scalar loop, which the tail below runs verbatim for the remainder.
                Vector<float> audioOutMulVec = new Vector<float>(audioOutMul);
                int vw = Vector<float>.Count;
                int vi = 0;
                for (; vi <= count - vw; vi += vw)
                {
                    Vector<float> v = new Vector<float>(buffer, offset + vi);
                    (v * audioOutMulVec).CopyTo(buffer, offset + vi);
                }
                for (; vi < count; vi++)
                {
                    buffer[offset + vi] = buffer[offset + vi] * audioOutMul; // From Buzz to audio output scale
                }

                // Update sample counters
                unchecked
                {
                    if (ReBuzzCore.GlobalState.ADPlayPos == ReBuzzCore.GlobalState.ADWritePos)
                    {
                        ReBuzzCore.GlobalState.ADWritePos += count / 2;
                    }
                    else
                    {
                        ReBuzzCore.GlobalState.ADPlayPos += count / 2;
                        ReBuzzCore.GlobalState.ADWritePos += count / 2;
                    }
                }

                buzzCore.PerformanceCurrent.EnginePerformanceCount += (Stopwatch.GetTimestamp() - time);

                // Session View (fork, 0.4.1): where the render is, for latency-compensated launches.
                buzzCore.SessionCore.PublishRenderedPosition();

                return count;
            }
        }

        internal float GetGrooveMul()
        {
            // ------------------------------------------------------------
            // GROOVE: Compute effective tick length
            // ------------------------------------------------------------
            float grooveMul = 1.0f;
            var masterInfo = ReBuzzCore.masterInfo;

            if (masterInfo.GrooveSize > 0 && masterInfo.GrooveData != IntPtr.Zero)
            {
                if (buzzCore.SoloPattern == null)
                {
                    masterInfo.PosInGroove = buzzCore.SongCore.PlayPosition % masterInfo.GrooveSize;
                }
                else
                {
                    masterInfo.PosInGroove = (buzzCore.SoloPattern.PlayPosition / PatternEvent.TimeBase) % masterInfo.GrooveSize;
                }
                int gi = masterInfo.PosInGroove;

                if (gi < 0) gi = 0;
                if (gi >= masterInfo.GrooveSize) gi = masterInfo.GrooveSize - 1;

                unsafe
                {
                    float* gptr = (float*)masterInfo.GrooveData.ToPointer();
                    grooveMul = gptr[gi];
                }
            }

            return grooveMul;
        }

        internal void UpdateMasterParams()
        {
            var masterInfo = ReBuzzCore.masterInfo;
            // Update master params on the next tick
            var master = buzzCore.SongCore.MachinesList[0];
            var masterGlobalParameters = master.ParameterGroupsList[1].ParametersList;
            var bpm = masterGlobalParameters[1].GetValue(0);
            var tpb = masterGlobalParameters[2].GetValue(0);

            if (tpb != masterInfo.TicksPerBeat ||
                bpm != masterInfo.BeatsPerMin)
            {
                buzzCore.BPM = bpm;
                buzzCore.TPB = tpb;
                buzzCore.UpdateMasterInfo();
                buzzCore.MachineManager.RefreshMachineParams();
            }
        }
        internal void UpdateSubTickLength()
        {
            var masterInfo = ReBuzzCore.masterInfo;
            var subTickInfo = ReBuzzCore.subTickInfo;

            if (subTickInfo.PosInSubTick == 0 && subTickInfo.SubTicksPerTick > 0)   // fork (0.6.3): never divide by 0
            {
                var subTickRemainder = masterInfo.SamplesPerTick % subTickInfo.SubTicksPerTick;

                subTickInfo.SamplesPerSubTick = (int)subTickInfo.AverageSamplesPerSubTick;
                subTickInfo.SubTickReminderCounter += subTickRemainder;

                if (subTickInfo.SubTickReminderCounter >= subTickInfo.SubTicksPerTick)
                {
                    subTickInfo.SubTickReminderCounter -= subTickInfo.SubTicksPerTick;
                    subTickInfo.SamplesPerSubTick++;
                }
                ReBuzzCore.masterInfoStructDirty = true;
            }
        }

        /// <summary>Fork: the fraction of a sample the last tick left over (see the tick length).</summary>
        double tickRemainder;

        private void UpdateMasterSamplesPerTick()
        {
            var masterInfo = ReBuzzCore.masterInfo;

            if (masterInfo.PosInTick == 0)
            {
                var ticksPerMin = masterInfo.BeatsPerMin * masterInfo.TicksPerBeat;
                var samplesPerTickRemainder = 60 * masterInfo.SamplesPerSec % ticksPerMin;

                masterInfo.SamplesPerTick = (int)masterInfo.AverageSamplesPerTick;
                masterInfo.SamplesPerTickReminderCounter += samplesPerTickRemainder;

                if (masterInfo.SamplesPerTickReminderCounter >= ticksPerMin)
                {
                    masterInfo.SamplesPerTickReminderCounter -= ticksPerMin;
                    masterInfo.SamplesPerTick++;
                }
            }
        }

        void AddActivePattern(PatternCore pattern)
        {
            if (activePatternSet.Add(pattern))
            {
                activePatterns.Add(pattern);
                buzzCore.SongCore.PositionedPatterns.Add(pattern);
            }
        }

        internal void UpdatePatternPositions(int sampleCount)
        {
            activePatterns.Clear();
            activePatternSet.Clear();

            // Clear the play positions set last time. Fork (0.4.1): only those patterns, not every
            // pattern of every machine (that was O(song size) per 256-sample block; the result is
            // the same, since only this method and solo playback give patterns a position, and
            // both add them to the list).
            var positioned = buzzCore.SongCore.PositionedPatterns;
            for (int i = 0; i < positioned.Count; i++)
            {
                positioned[i].PlayPosition = int.MinValue;
                positioned[i].SessionTrack = null;
            }
            positioned.Clear();

            if (!buzzCore.Playing)
                return;

            var masterInfo = ReBuzzCore.masterInfo;

            if (buzzCore.SoloPattern != null)
            {
                var pattern = buzzCore.SoloPattern;
                pattern.UpdateSoloPlayPosition(sampleCount);
                AddActivePattern(pattern);
            }
            else
            {
                var song = buzzCore.SongCore;
                var session = buzzCore.SessionCore;
                int songPlayPosition = song.PlayPosition;
                int posInTickOffset = masterInfo.PosInTick * PatternEvent.TimeBase / masterInfo.SamplesPerTick;
                var sequences = buzzCore.SongCore.SequencesList;

                // Session View (fork): a launched clip plays from where it was launched (not
                // songPosition % length). Session clips go first: a pattern has one play position,
                // and when the arrangement of another track holds the same pattern, the session
                // clip keeps it (0.4.1; before, whichever track came last won).
                for (int s = 0; s < sequences.Count; s++)
                {
                    var seq = sequences[s];
                    var clip = session.GetPlayingCore(seq);
                    if (clip == null) continue;
                    int clipTick = session.GetClipPosition(seq);
                    if (clipTick >= 0)
                    {
                        clip.PlayPosition = clipTick * PatternEvent.TimeBase + posInTickOffset;
                        clip.SessionTrack = seq;
                        AddActivePattern(clip);
                    }
                }

                for (int s = 0; s < sequences.Count; s++)
                {
                    var seq = sequences[s];
                    // An overridden track ignores its arrangement.
                    if (session.IsArrangementOverridden(seq))
                        continue;

                    // Fork (0.4.1): the event at the song position by binary search over a sorted
                    // snapshot, instead of walking (and allocating an enumerator over) every event
                    // of every sequence each block. Spans are cut at the next event, so only the
                    // last event at or before the position can cover it.
                    var arranged = seq.Arranged;
                    int index = arranged.IndexAtOrBefore(songPlayPosition);
                    if (index >= 0)
                    {
                        int eventTime = arranged.Times[index];
                        var seqEvent = arranged.Events[index];
                        var pc = (seqEvent.Pattern as PatternCore);

                        if (songPlayPosition >= eventTime && songPlayPosition < eventTime + seqEvent.Span &&
                            (pc == null || pc.SessionTrack == null))
                        {
                            // Event is playing in sequece
                            if (seqEvent.Type == SequenceEventType.PlayPattern &&
                                seqEvent.Pattern != null)
                            {
                                int timeBase = PatternEvent.TimeBase;
                                int newPatternPos = int.MinValue;
                                // Fork (0.5.1): an event can start partway into its pattern.
                                int startTick = songPlayPosition - eventTime + seqEvent.PlayedOffset;

                                // Hack. Pattern editor needs to get adjusted position if play position moved by user or control machine
                                if (song.AdjustPositionOnTick && masterInfo.PosInTick == 0)
                                {
                                    newPatternPos = startTick * timeBase +
                                        (ReBuzzCore.masterInfo.PosInTick - 1) * timeBase / masterInfo.SamplesPerTick;

                                    if (newPatternPos < 0)
                                        newPatternPos = int.MinValue;
                                }
                                else
                                {
                                    newPatternPos = startTick * timeBase +
                                        (masterInfo.PosInTick * timeBase / masterInfo.SamplesPerTick);
                                }
                                pc.PlayPosition = newPatternPos;
                                if (newPatternPos != int.MinValue)
                                    AddActivePattern(pc);
                            }
                        }
                    }
                }
                if (masterInfo.PosInTick == 0)
                {
                    song.AdjustPositionOnTick = false;
                }
            }
        }

        internal void CallTick()
        {
            // First control, then other?
            foreach (var machine in buzzCore.SongCore.MachinesList)
            {
                // Tick should be inexpensive operation so no tasks?
                // Some old machines don't support subtick
                if (machine.IsControlMachine && (ReBuzzCore.masterInfo.PosInTick == 0 ||
                    (engineSettings.SubTickTiming && ReBuzzCore.subTickInfo.PosInSubTick == 0 && machine.DLL.Info.Version > MachineManager.BUZZ_MACHINE_INTERFACE_VERSION_42 &&
                    !buzzCore.Gear.IsSubTickDisabled(machine))))
                {
                    var workInstance = buzzCore.MachineManager.GetMachineWorkInstance(machine);
                    workInstance.Tick(false, false);
                }
            }

            foreach (var machine in buzzCore.SongCore.MachinesList)
            {
                // Tick should be inexpensive operation so no tasks?
                // Some old machines don't support subtick
                if (!machine.IsControlMachine && (ReBuzzCore.masterInfo.PosInTick == 0 ||
                    (engineSettings.SubTickTiming && ReBuzzCore.subTickInfo.PosInSubTick == 0 && machine.DLL.Info.Version > MachineManager.BUZZ_MACHINE_INTERFACE_VERSION_42 &&
                    !buzzCore.Gear.IsSubTickDisabled(machine))))
                {
                    var workInstance = buzzCore.MachineManager.GetMachineWorkInstance(machine);
                    workInstance.Tick(false, false);
                }
            }
        }

        struct PlayingEventsStruct
        {
            public MachineCore Machine { get; internal set; }
            public int Channel { get; internal set; }
            public int MidiNote { get; internal set; }
            internal PatternEvent Pe { get; set; }
            internal IPattern Pattern { get; set; }
        }

        readonly List<PlayingEventsStruct> playingEvents = new List<PlayingEventsStruct>(256);   // fork (follow-ups): sized ahead
        internal void UpdatePatternColumnEvents(int sampleCount)
        {
            if (!buzzCore.Playing)
            {
                if (playingEvents.Count > 0)
                {
                    // Iterate in reverse so RemoveAt is safe during iteration (and O(1)
                    // at the tail); avoids the ToArray() snapshot allocation.
                    for (int i = playingEvents.Count - 1; i >= 0; i--)
                    {
                        var pes = playingEvents[i];
                        var machine = pes.Machine;
                        buzzCore.MachineManager.SendMIDINote(machine, pes.Channel, pes.MidiNote, 0);
                        playingEvents.RemoveAt(i);
                    }
                }
                return;
            }

            int windowTicks = BlockLength(ReBuzzCore.masterInfo, sampleCount);

            // Send note offs. Iterate in reverse: RemoveAt(i) in a forward loop
            // shifts the next element into slot i and skips it this pass, delaying
            // a due note-off by a chunk or more.
            // Fork (docs/specs/mcp-server.md A1): note-offs due in this block go out before its
            // note-ons, as note-ons go out at the block's start. Upstream sent them a block late
            // and after the note-ons, so a note ending where the next note of the same pitch
            // starts (repeated bass notes, a loop wrap) cut the new note off after ~6 ms.
            for (int i = playingEvents.Count - 1; i >= 0; i--)
            {
                var pes = playingEvents[i];
                var pe = pes.Pe;
                var machine = pes.Machine;

                // Fork: also release notes whose pattern stopped (clip stopped or switched, span
                // ended) or jumped back before the note started (loop wrap, relaunch). Upstream
                // only released notes the play position had passed, so those hung.
                int pos = pes.Pattern.PlayPosition;
                if (pos == int.MinValue || pe.Time > pos + windowTicks || pe.Time + pe.Duration < pos + windowTicks)
                {
                    buzzCore.MachineManager.SendMIDINote(machine, pes.Channel, pes.MidiNote, 0);
                    playingEvents.RemoveAt(i);
                }
            }

            if (buzzCore.SoloPattern != null)
            {
                PlayPatternColumnEvents(buzzCore.SoloPattern, sampleCount);
            }
            else
            {
                // Fork: play each pattern that has a position once - session clips included (G5).
                // Upstream called this for every arranged event, so a pattern arranged N times
                // in one sequence sent its MIDI column events N times.
                for (int i = 0; i < activePatterns.Count; i++)
                {
                    PlayPatternColumnEvents(activePatterns[i], sampleCount);
                }
            }
        }

        /// <summary>
        /// Fork (0.6.3): length of the block starting at PosInTick, in pattern time (1/240 tick),
        /// as the difference of the positions UpdatePatternPositions gives its start and end.
        /// </summary>
        static int BlockLength(MasterInfoExtended masterInfo, int sampleCount)
        {
            int spt = Math.Max(1, masterInfo.SamplesPerTick);
            return (masterInfo.PosInTick + sampleCount) * PatternEvent.TimeBase / spt
                 - masterInfo.PosInTick * PatternEvent.TimeBase / spt;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void PlayPatternColumnEvents(IPattern pattern, int sampleCount)
        {
            if (pattern != null)
            {
                var masterInfo = ReBuzzCore.masterInfo;
                // Fork (0.6.3): the window is this block, [PlayPosition, PlayPosition + its length).
                // PlayPosition already includes the part of the tick before the block; adding
                // PosInTick again made windows overlap, so events late in a tick were sent up to
                // a dozen times. Ends are computed as UpdatePatternPositions computes starts, so
                // one block's end is the next block's start: every position is sent exactly once.
                int from = pattern.PlayPosition;
                int to = from + BlockLength(masterInfo, sampleCount);

                // Fork (docs/specs/daw-modes.md S3): ramps in engine automation columns move on each
                // tick and subtick, where machines take parameter values. Columns without ramps
                // (every song before them) skip this, so they play exactly as before.
                bool rampPoint = masterInfo.PosInTick == 0 || (engineSettings.SubTickTiming && ReBuzzCore.subTickInfo.PosInSubTick == 0);

                // Fork (0.4.1): the list itself; pattern.Columns wraps it in a new read-only
                // collection on every call (per pattern per block, on the audio thread).
                IList<IPatternColumn> columns = pattern is PatternCore core ? core.ColumnsList : pattern.Columns;

                for (int i = 0; i < columns.Count; i++)
                {
                    var column = columns[i];
                    if (column.Machine == null)
                        continue;

                    // Fork (0.7): session automation, recorded into a parameter column the engine plays
                    // (pattern editors play their own parameter data). Never recorded again.
                    if (column.Type == PatternColumnType.Parameter)
                    {
                        if (column is not PatternColumnCore automation || !automation.EnginePlays || column.Parameter == null)
                            continue;
                        // A ramp sets its value where it changed: a machine isn't told the same value again.
                        if (rampPoint && automation.HasRamps && automation.TryRampValue(from, out int ramped) && column.Parameter.GetValue(column.Track) != ramped)
                            column.Parameter.SetValue(column.Track | (1 << 16), ramped);
                        column.GetEventsInto(from, to, playColumnEvents);
                        for (int e = 0; e < playColumnEvents.Count; e++)
                            column.Parameter.SetValue(column.Track | (1 << 16), playColumnEvents[e].Value);
                        continue;
                    }

                    column.GetEventsInto(from, to, playColumnEvents);
                    foreach (var pe in playColumnEvents)
                    {
                        if (column.Type == PatternColumnType.MIDI)
                        {
                            var data = pe.Value;
                            buzzCore.MachineManager.SendMidiInput(column.Machine, data, false);

                            int b = MIDI.DecodeStatus(data);
                            int data1 = MIDI.DecodeData1(data);
                            int data2 = MIDI.DecodeData2(data);
                            int channel = 0;
                            int commandCode = MIDI.ControlChange;

                            if ((b & 0xF0) == 0xF0)
                            {
                                // both bytes are used for command code in this case
                                commandCode = b;
                            }
                            else
                            {
                                commandCode = (b & 0xF0);
                                channel = (b & 0x0F);
                            }

                            if (commandCode == MIDI.NoteOn)
                            {
                                playingEvents.Add(new PlayingEventsStruct()
                                {
                                    Pe = pe,
                                    Pattern = pattern,
                                    Machine = column.Machine as MachineCore,
                                    Channel = channel,
                                    MidiNote = data1
                                });
                            }
                        }
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public int ReadWork(float[] buffer, int offset, int workSamplesCount)
        {
            foreach (var m in buzzCore.SongCore.MachinesList)
                m.workDone = false;

            // Iterate machine connection graph staring from Master
            if (buzzCore.SongCore.MachinesList.Count == 0)
                return workSamplesCount;
            var master = buzzCore.SongCore.MachinesList[0];

            // Fork (audio hygiene): a job for every dispatch this buffer can make (each machine
            // once, each input once), made once and reused (RenderJobs).
            if (multiThreadingEnabled)
                renderJobs.BeginBuffer(RenderCapacity());

            if (!multiThreadingEnabled)
            {
                if (engineSettings.UseCachedWorkOrder)
                    HandleWorkAlgorithmSingleThreadCached(master, workSamplesCount);
                else
                    HandleWorkAlgorithmSingleThread(master, workSamplesCount);
            }
            else if (algorithm == 0)
            {
                if (engineSettings.UseCachedWorkOrder)
                    HandleWorkAlgorithmGroupsCached(master, workSamplesCount);
                else
                    HandleWorkAlgorithmGroups(master, workSamplesCount);
            }
            else if (algorithm == 1)
            {
                // Recursive processing of audio paths
                HandleWorkAlgorithmRecursive(master, workSamplesCount);
            }
            else if (algorithm == 2)
            {
                if (engineSettings.UseCachedWorkOrder && b2Enabled)
                    HandleWorkAlgorithm2Dependency(master, workSamplesCount);
                else if (engineSettings.UseCachedWorkOrder)
                    HandleWorkAlgorithm2Cached(master, workSamplesCount);
                else
                    HandleWorkAlgorithm2(master, workSamplesCount);
            }

            // Mix all to single L + R signal
            Sample[] samples = master.GetStereoSamples(workSamplesCount);
            for (int i = 0; i < workSamplesCount; i++)
            {
                buffer[offset + i * 2] = samples[i].L * (float)buzzCore.MasterVolume;
                buffer[offset + i * 2 + 1] = samples[i].R * (float)buzzCore.MasterVolume;
            }

            master.UpdateIsActive();

            return workSamplesCount;
        }

        /// <summary>
        /// Fork (0.6.3): the work set's first machine through its own struct enumerator. LINQ's
        /// First() boxed one (40 bytes per call, once or twice per block) unless the JIT's
        /// profile-guided optimisation happened to remove the box: that was the allocation test
        /// failing now and then since 0.4.1.
        /// </summary>
        MachineCore FirstOfWorkSet()
        {
            foreach (var m in workSet)
                return m;
            return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void HandleWorkAlgorithmGroups(MachineCore master, int workSamplesCount)
        {
            while (true)
            {
                workSet.Clear();

                // 1. Get machines that can be processed
                CollectEditorMachinesThatCanWork();
                HandleWorkList(workSamplesCount);
                CollectControlMachinesThatCanWork();
                HandleWorkList(workSamplesCount);
                CollectMachinesThatCanWork(master);

                if (workSet.Count == 1 && FirstOfWorkSet().DLL.Info.Type == MachineType.Master) // Master
                {
                    break;
                }

                // If workList.Count == 1 then call work from this thread
                if (workSet.Count == 1)
                {
                    var machine = FirstOfWorkSet();

                    // Call work and update machine activity flag

                    var workInstance = buzzCore.MachineManager.GetMachineWorkInstance(machine);
                    workInstance.TickAndWork(workSamplesCount, true);

                    machine.workDone = true;
                }
                else
                {
                    dispatchSamples = workSamplesCount;
                    int n = FillAndSortDesc();
                    RunAll(workOneGroups, sortScratch, n);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal void HandleWorkAlgorithmRecursive(MachineCore master, int numRead)
        {
            recursiveSamples = numRead;
            workSet.Clear();

            // 1. Get machines that can be processed
            CollectEditorMachinesThatCanWork();
            HandleWorkList(numRead);
            CollectControlMachinesThatCanWork();
            HandleWorkList(numRead);

            // Recursively go through the rest
            HandleWorkRecursive(master, numRead);
        }

        private readonly EngineSettings engineSettings;

        // Fork (audio hygiene): dispatch without allocating (RenderJobs): the jobs, the join the
        // lists and groups wait on, and the jobs a list or group queued (audio thread only).
        readonly RenderJobs renderJobs = new RenderJobs();
        readonly RenderJoin listJoin = new RenderJoin();
        RenderJobs.Job[] listJobs = new RenderJobs.Job[64];
        private readonly Action<object> workOneRecursive;
        private int recursiveSamples;

        /// <summary>Dispatches this buffer can make: each machine once (lists, groups) and each input once (recursive).</summary>
        int RenderCapacity()
        {
            var machines = buzzCore.SongCore.MachinesList;
            int n = machines.Count + 1;
            for (int i = 0; i < machines.Count; i++)
                n += machines[i].AllInputs.Count;
            return n;
        }

        /// <summary>
        /// Runs <paramref name="body"/> for <paramref name="items"/>[0..<paramref name="n"/>) on the
        /// render threads and waits for them all, helping with those not yet started (Task.WaitAll's
        /// inlining). Audio thread.
        /// </summary>
        void RunAll(Action<object> body, MachineCore[] items, int n)
        {
            if (listJobs.Length < n)
                listJobs = new RenderJobs.Job[Math.Max(n, listJobs.Length * 2)];
            listJoin.Begin(n);
            for (int i = 0; i < n; i++)
                listJobs[i] = renderJobs.Queue(body, items[i], listJoin);
            for (int i = n - 1; i >= 0; i--)
            {
                listJobs[i]?.TryRun();
                listJobs[i] = null;
            }
            listJoin.Wait();
        }

        // Interim §5: reused scratch + cached non-capturing dispatch delegates
        // (kills per-chunk OrderByDescending alloc and per-task display-class capture).
        private MachineCore[] sortScratch = new MachineCore[64];
        private int dispatchSamples;
        private readonly Action<object> workOneList;
        private readonly Action<object> workOneGroups;

        // Copy workSet into sortScratch and stable-descending insertion-sort by
        // performanceLastCount. Only one algorithm path runs per ReadWork (single
        // audio thread), so a single shared scratch is safe.
        private int FillAndSortDesc()
        {
            int n = workSet.Count;
            if (sortScratch.Length < n)
                sortScratch = new MachineCore[Math.Max(n, sortScratch.Length * 2)];

            int i = 0;
            foreach (var m in workSet)
                sortScratch[i++] = m;

            for (int a = 1; a < n; a++)
            {
                var key = sortScratch[a];
                long kc = key.performanceLastCount;
                int b = a - 1;
                while (b >= 0 && sortScratch[b].performanceLastCount < kc)
                {
                    sortScratch[b + 1] = sortScratch[b];
                    b--;
                }
                sortScratch[b + 1] = key;
            }
            return n;
        }

        // HandleWorkList body: work only; caller sets workDone on the dispatch thread.
        private void WorkOneList(object state)
        {
            var machine = (MachineCore)state;
            var workInstance = buzzCore.MachineManager.GetMachineWorkInstance(machine);
            workInstance.TickAndWork(dispatchSamples, true);
        }

        // Groups body: guard + set workDone inside, matching the original lambda.
        private void WorkOneGroups(object state)
        {
            var machine = (MachineCore)state;
            if (machine.workDone)
                return;
            var workInstance = buzzCore.MachineManager.GetMachineWorkInstance(machine);
            workInstance.TickAndWork(dispatchSamples, true);
            machine.workDone = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void HandleWorkList(int numRead)
        {
            dispatchSamples = numRead;
            int n = 0;
            if (sortScratch.Length < workSet.Count)
                sortScratch = new MachineCore[Math.Max(workSet.Count, sortScratch.Length * 2)];
            foreach (var machine in workSet)
            {
                sortScratch[n++] = machine;
                machine.workDone = true;
            }
            RunAll(workOneList, sortScratch, n);
            workSet.Clear();
        }

        // Recursive body: an input's branch, on a render thread or the waiting one.
        private void WorkOneRecursive(object state) => HandleWorkRecursive((MachineCore)state, recursiveSamples);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal void HandleWorkRecursive(MachineCore machine, int numRead)
        {
            lock (machine.workLock)
            {
                // Was work called for this machine already?
                if (machine.workDone)
                    return;

                // Process most time consuming branches first (a stable sort, as OrderByDescending
                // was). Fork (audio hygiene): into the machine's own scratch, each input's branch
                // a job made once (RenderJobs), not a Task, a closure and LINQ's sorter per input.
                var inputs = machine.AllInputs;
                int count = inputs.Count;
                if (machine.workInputs.Length < count)
                {
                    int size = Math.Max(count, machine.workInputs.Length * 2);
                    machine.workInputs = new MachineCore[size];
                    machine.workInputKeys = new long[size];
                    machine.workJobs = new RenderJobs.Job[size];
                }
                var order = machine.workInputs;
                var keys = machine.workInputKeys;
                for (int i = 0; i < count; i++)
                {
                    var source = inputs[i].Source as MachineCore;
                    long key = source.performanceBranchCount;
                    int b = i - 1;
                    while (b >= 0 && keys[b] < key)
                    {
                        order[b + 1] = order[b];
                        keys[b + 1] = keys[b];
                        b--;
                    }
                    order[b + 1] = source;
                    keys[b + 1] = key;
                }

                // Was work called for the input's machine already?
                int pending = 0;
                for (int i = 0; i < count; i++)
                    if (!order[i].workDone)
                        order[pending++] = order[i];

                if (pending > 0)
                {
                    // Handle inputs, then wait for them all (helping with those not yet started).
                    var join = machine.workJoin;
                    var jobs = machine.workJobs;
                    join.Begin(pending);
                    for (int i = 0; i < pending; i++)
                        jobs[i] = renderJobs.Queue(workOneRecursive, order[i], join);
                    for (int i = pending - 1; i >= 0; i--)
                    {
                        jobs[i]?.TryRun();
                        jobs[i] = null;
                    }
                    join.Wait();
                }
                for (int i = 0; i < count; i++)
                    order[i] = null;

                // All inputs handled (if any)

                // Call work and update machine activity flag
                var workInstance = buzzCore.MachineManager.GetMachineWorkInstance(machine);
                workInstance.TickAndWork(numRead, true);

                // Count overall branch process time
                machine.performanceBranchCount = machine.performanceLastCount;
                foreach (var input in machine.AllInputs)
                {
                    machine.performanceBranchCount += (input.Source as MachineCore).performanceBranchCount;
                }

                machine.workDone = true;
            }

            // Some machines report the active state wrong, double check
            machine.IsActive = machine.GetActivity(numRead);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void HandleWorkAlgorithm2(MachineCore master, int workSamplesCount)
        {
            workEngine.PrepareEngine(workSamplesCount);
            while (true)
            {
                workSet.Clear();

                // 1. Get machines that can be processed
                CollectEditorMachinesThatCanWork();
                HandleWorkList(workSamplesCount);
                CollectControlMachinesThatCanWork();
                HandleWorkList(workSamplesCount);
                CollectMachinesThatCanWork(master);

                if ((workSet.Count == 1) && (FirstOfWorkSet().DLL.Info.Type == MachineType.Master)) // Master
                {
                    break;
                }

                int n = FillAndSortDesc();
                for (int s = 0; s < n; s++)
                {
                    var machine = sortScratch[s];
                    // Add work instances to be processed
                    var workInstance = buzzCore.MachineManager.GetMachineWorkInstance(machine);
                    workEngine.AddWork(workInstance);
                    machine.workDone = true;
                }

                // Notify workEngine that all jobs are added and processing can start. This way we can be sure that
                // all threads are properly finished
                workEngine.AllJobsAdded();

                // Wait all tasks to complete
                if (workSet.Count > 0)
                {
                    var jobs = workEngine.AllDoneEvent();
                    jobs.WaitOne();
                }

                if (stopped)
                {
                    break;
                }
            }
        }

        // ===== #107 prototype: cached topological work order =====
        // Gated by engineSettings.UseCachedWorkOrder. Shares the cache build
        // (CachedWorkOrder) across dispatch styles; only consumption differs.
        // Algorithm 1 (Recursive) is intentionally untouched: it has no per-chunk
        // re-scan, so the cache does not apply to it.

        private readonly CachedWorkOrder cachedOrder = new CachedWorkOrder();

        // Runs on the audio thread under AudioLock (held for the whole buffer),
        // so the rebuild can never race a topology mutation. Rebuild is lazy:
        // at most one rebuild per buffer, and only when the topology changed.
        /// <summary>UI thread: builds the cached order after a topology change (under AudioLock), so the audio thread's next buffer doesn't allocate it.</summary>
        internal void PrebuildOrder()
        {
            if (!engineSettings.UseCachedWorkOrder) return;
            lock (ReBuzzCore.AudioLock) GetOrBuildOrder();
        }

        private CachedWorkOrder GetOrBuildOrder()
        {
            var song = buzzCore.SongCore;
            long gen = buzzCore.TopologyGeneration;
            if (cachedOrder.BuiltGeneration != gen ||
                cachedOrder.BuiltMachineCount != song.MachinesList.Count ||
                // Self-heal a stale EMPTY order: if the cache was built mid-load off a
                // not-yet-wired graph (empty audio cone), it would otherwise stick until
                // the machine count next changes. Rebuild while AudioWaves is empty but
                // audio machines exist (count beyond prefix + Master), so it recovers on
                // the next chunk once connections resolve.
                (cachedOrder.AudioWaves.Length == 0 &&
                 song.MachinesList.Count > cachedOrder.EditorPrefix.Length + cachedOrder.ControlPrefix.Length + 1))
            {
                cachedOrder.Rebuild(song);
                cachedOrder.BuiltGeneration = gen;
            }
            return cachedOrder;
        }

        // Copy a wave into sortScratch and stable-descending insertion-sort by
        // performanceLastCount (kept per-chunk/dynamic, like FillAndSortDesc).
        private int FillAndSortDescFrom(MachineCore[] wave)
        {
            int n = wave.Length;
            if (sortScratch.Length < n)
                sortScratch = new MachineCore[Math.Max(n, sortScratch.Length * 2)];

            for (int i = 0; i < n; i++)
                sortScratch[i] = wave[i];

            for (int a = 1; a < n; a++)
            {
                var key = sortScratch[a];
                long kc = key.performanceLastCount;
                int b = a - 1;
                while (b >= 0 && sortScratch[b].performanceLastCount < kc)
                {
                    sortScratch[b + 1] = sortScratch[b];
                    b--;
                }
                sortScratch[b + 1] = key;
            }
            return n;
        }

        // Dispatch a prefix partition (editors or controls) as one parallel
        // batch + barrier, mirroring CollectEditor/Control + HandleWorkList.
        // Ready is re-tested here (a native crash can clear it mid-buffer).
        private void DispatchPrefixCached(MachineCore[] prefix, int numRead)
        {
            workSet.Clear();
            foreach (var m in prefix)
                if (m.Ready && !m.workDone)
                    workSet.Add(m);

            if (workSet.Count > 0)
                HandleWorkList(numRead); // dispatches workSet, sets workDone, clears workSet
            else
                workSet.Clear();
        }

        // ==== Dependency-driven dispatch (#107) ==================================
        // Replaces the per-DAG-level barrier loop with ONE barrier per buffer. A machine
        // starts the instant its own inputs are done rather than when its whole level is,
        // so workers stay fed across what used to be wave boundaries.
        //
        // Used only when the cached order is valid AND its self-check passed; anything
        // unexpected falls back to the wave path, which is unchanged.
        private bool b2Installed;
        private long b2InstalledGeneration = long.MinValue;
        private int b2InstalledCount = -1;
        private bool b2Enabled = true;

        // The dispatcher's wait is bounded. A correct buffer completes in well under a
        // millisecond, so 2s is absurdly generous - the point is that a completion bug
        // degrades into a fallback plus a recorded diagnostic rather than freezing the
        // audio thread. (The completion invariant is enforced by construction in
        // WorkThreadEngine and the graph is validated by CachedWorkOrder.SelfCheck, so
        // this should be unreachable; it is cheap insurance, not a substitute.)
        private const int B2_WAIT_MS = 2000;
        internal static int B2StallCount;
        internal static string B2LastStall = "(none)";

        // Build the engine-side graph once per topology, not per buffer.
        private bool B2EnsureGraph(CachedWorkOrder order)
        {
            if (order.HasCycle || order.SelfCheckFailed || order.Cone.Length == 0)
                return false;

            if (b2Installed
                && b2InstalledGeneration == order.BuiltGeneration
                && b2InstalledCount == order.BuiltMachineCount)
                return true;

            var items = new MachineWorkInstance[order.Cone.Length];
            for (int i = 0; i < order.Cone.Length; i++)
                items[i] = buzzCore.MachineManager.GetMachineWorkInstance(order.Cone[i]);

            workEngine.B2SetGraph(items, order.Indeg, order.Succ);
            b2Installed = true;
            b2InstalledGeneration = order.BuiltGeneration;
            b2InstalledCount = order.BuiltMachineCount;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void HandleWorkAlgorithm2Dependency(MachineCore master, int workSamplesCount)
        {
            var order = GetOrBuildOrder();
            if (!B2EnsureGraph(order))
            {
                HandleWorkAlgorithm2Cached(master, workSamplesCount);
                return;
            }

            workEngine.PrepareEngine(workSamplesCount);

            // Prefix unchanged: editors, then controls, before the audio cone.
            DispatchPrefixCached(order.EditorPrefix, workSamplesCount);
            DispatchPrefixCached(order.ControlPrefix, workSamplesCount);

            // The wave path marks workDone as it dispatches and other code reads it, so we
            // mark the same set. Every cone machine runs this buffer, so marking up front
            // is equivalent.
            var cone = order.Cone;
            for (int i = 0; i < cone.Length; i++)
                cone[i].workDone = true;

            if (stopped)
                return;

            if (!workEngine.B2StartBuffer(order.Seed, workSamplesCount))
                return;

            workEngine.AllJobsAdded();

            // ONE barrier for the whole buffer (the wave path takes one per level).
            if (!workEngine.AllDoneEvent().WaitOne(B2_WAIT_MS))
            {
                System.Threading.Interlocked.Increment(ref B2StallCount);
                B2LastStall = workEngine.B2DescribePending();

                // Drop to the wave path for the rest of the session rather than risk
                // stalling again on the next buffer.
                workEngine.B2Disable();
                b2Installed = false;
                b2Enabled = false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void HandleWorkAlgorithm2Cached(MachineCore master, int workSamplesCount)
        {
            var order = GetOrBuildOrder();
            if (order.HasCycle)
            {
                // Deterministic fallback: legacy walk (its CanConnect gate keeps
                // valid songs acyclic; this only fires on a malformed graph).
                HandleWorkAlgorithm2(master, workSamplesCount);
                return;
            }

            workEngine.PrepareEngine(workSamplesCount);

            DispatchPrefixCached(order.EditorPrefix, workSamplesCount);
            DispatchPrefixCached(order.ControlPrefix, workSamplesCount);

            foreach (var wave in order.AudioWaves)
            {
                if (stopped)
                    break;

                int n = FillAndSortDescFrom(wave);

                // A one-machine wave has no intra-wave parallelism to gain, so the
                // AddWork/AllJobsAdded/WaitOne barrier round-trip is pure overhead.
                // Run it inline on the dispatch thread instead (bit-neutral: same
                // TickAndWork, same workDone). Mirrors HandleWorkAlgorithmGroupsCached.
                if (n == 1)
                {
                    var machine = sortScratch[0];
                    var workInstance = buzzCore.MachineManager.GetMachineWorkInstance(machine);
                    workInstance.TickAndWork(workSamplesCount, true);
                    machine.workDone = true;
                    continue;
                }

                for (int s = 0; s < n; s++)
                {
                    var machine = sortScratch[s];
                    var workInstance = buzzCore.MachineManager.GetMachineWorkInstance(machine);
                    workEngine.AddWork(workInstance);
                    machine.workDone = true;
                }

                workEngine.AllJobsAdded();
                if (n > 0)
                    workEngine.AllDoneEvent().WaitOne();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void HandleWorkAlgorithmGroupsCached(MachineCore master, int workSamplesCount)
        {
            var order = GetOrBuildOrder();
            if (order.HasCycle)
            {
                HandleWorkAlgorithmGroups(master, workSamplesCount);
                return;
            }

            // Groups uses no workEngine (it is null for algorithm 0); prefix and
            // waves dispatch on the render threads (RenderJobs) and wait for them all,
            // matching the legacy path. workOneGroups guards/sets workDone.
            DispatchPrefixCached(order.EditorPrefix, workSamplesCount);
            DispatchPrefixCached(order.ControlPrefix, workSamplesCount);

            foreach (var wave in order.AudioWaves)
            {
                int n = FillAndSortDescFrom(wave);
                if (n == 1)
                {
                    var machine = sortScratch[0];
                    var workInstance = buzzCore.MachineManager.GetMachineWorkInstance(machine);
                    workInstance.TickAndWork(workSamplesCount, true);
                    machine.workDone = true;
                }
                else if (n > 1)
                {
                    dispatchSamples = workSamplesCount;
                    RunAll(workOneGroups, sortScratch, n);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void HandleWorkAlgorithmSingleThreadCached(MachineCore master, int workSamplesCount)
        {
            var order = GetOrBuildOrder();
            if (order.HasCycle)
            {
                HandleWorkAlgorithmSingleThread(master, workSamplesCount);
                return;
            }

            // Controls-first prefix (Ready-gated), then audio waves in topo order.
            // This also tightens the legacy single-thread path, which works
            // editors/controls/audio together in HashSet order with no guaranteed
            // controls-before-generators ordering.
            WorkSerialCached(order.EditorPrefix, workSamplesCount, readyGated: true);
            WorkSerialCached(order.ControlPrefix, workSamplesCount, readyGated: true);
            foreach (var wave in order.AudioWaves)
                WorkSerialCached(wave, workSamplesCount, readyGated: false);
        }

        private void WorkSerialCached(MachineCore[] list, int numRead, bool readyGated)
        {
            foreach (var m in list)
            {
                if (m.workDone)
                    continue;
                if (readyGated && !m.Ready)
                    continue;
                var workInstance = buzzCore.MachineManager.GetMachineWorkInstance(m);
                workInstance.TickAndWork(numRead, true);
                m.workDone = true;
            }
        }
        // ===== end #107 prototype =====

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void HandleWorkAlgorithmSingleThread(MachineCore master, int workSamplesCount)
        {
            while (true)
            {
                workSet.Clear();

                // 1. Get machines that can be processed
                CollectEditorMachinesThatCanWork();
                CollectControlMachinesThatCanWork();
                CollectMachinesThatCanWork(master);

                if (workSet.Count == 1 && FirstOfWorkSet().DLL.Info.Type == MachineType.Master) // Master
                {
                    break;
                }

                foreach (var machine in workSet)
                {
                    var workInstance = buzzCore.MachineManager.GetMachineWorkInstance(machine);
                    workInstance.TickAndWork(workSamplesCount, true);
                    machine.workDone = true;
                }
            }
        }

        private void CollectMachinesThatCanWork(MachineCore machine)
        {
            bool machineCanWork = true;
            foreach (var input in machine.AllInputs)
            {
                var sourceMachine = input.Source as MachineCore;

                // Was work called for this machine already?
                if (!sourceMachine.workDone)
                {
                    machineCanWork = false;
                    CollectMachinesThatCanWork(sourceMachine);
                }
            }

            if (machineCanWork)
            {
                workSet.Add(machine);
            }
        }
        private void CollectEditorMachinesThatCanWork()
        {
            foreach (var mac in (buzzCore.Song as SongCore).MachinesList)
            {
                var machine = mac;
                // Was work called for this editor machine already?
                // Fork (audio hygiene): a bit test; HasFlag boxes in unoptimized (tier-0, Debug) code.
                if ((machine.DLL.Info.Flags & MachineInfoFlags.CONTROL_MACHINE) != 0 && machine.Hidden && !machine.workDone && machine.Ready)
                {
                    workSet.Add(machine);
                }
            }
        }

        private void CollectControlMachinesThatCanWork()
        {
            // Iterate the backing list directly instead of the Machines facade,
            // which allocates via Where(...).Cast<IMachine>().ToReadOnlyCollection()
            // on every access. The facade filters !Hidden && Ready; that predicate
            // is inlined here (Ready was already re-tested below) so the visible
            // set is identical to the previous facade-based loop.
            foreach (var machine in (buzzCore.Song as SongCore).MachinesList)
            {
                // Was work called for this control machine already?
                if (!machine.Hidden && (machine.DLL.Info.Flags & MachineInfoFlags.CONTROL_MACHINE) != 0 && !machine.workDone && machine.Ready)
                {
                    workSet.Add(machine);
                }
            }
        }

        private void CollectInputMachines(MachineCore machine)
        {
            foreach (var input in machine.AllInputs)
            {
                var sourceMachine = input.Source as MachineCore;
                workSet.Add(machine);
            }
        }

        internal void Stop()
        {
            stopped = true;
            workSet.Clear();
        }

        internal int ThreadReadSpeedAdjust(float[] buffer, int offset, int count)
        {
            if (buzzCore.Speed == 0)
            {
                return MainAudioFillBuffer(buffer, offset, count);
            }
            else
            {
                double div = (Math.Abs(buzzCore.Speed) / 20.0 + 1.0);

                int sCount = (int)(count / div);
                sCount = sCount % 2 != 0 ? sCount - 1 : sCount;

                if (sCount == 0)
                    return 0;

                float[] sBuffer = new float[sCount];
                MainAudioFillBuffer(sBuffer, 0, sCount);

                SpeedDown(buffer, offset, count, sBuffer, sCount, SpeedAdjustLinear);
            }
            return count;
        }

        internal static void SpeedDown(float[] buffer, int offset, int count, float[] sBuffer, int sCount, bool SpeedAdjustLinear)
        {
            float step = sCount / (float)count;
            float pos = 0;

            // Linear interpolation. FIXME
            for (int i = 0; i < count; i += 2)
            {
                if (SpeedAdjustLinear)
                {
                    int index = (int)pos * 2; // Stereo
                    float t = pos - (int)pos; // pos between x1 & x2

                    float x0 = sBuffer[index];
                    float x1 = index < sBuffer.Length - 2 ? sBuffer[index + 2] : x0;
                    float k = x1 - x0;

                    buffer[offset + i] = x0 + t * k;

                    index++;
                    x0 = sBuffer[index];
                    x1 = index < sBuffer.Length - 2 ? sBuffer[index + 2] : x0;
                    k = x1 - x0;

                    buffer[offset + i + 1] = x0 + t * k;

                    pos += step;
                }
                else
                {
                    int index = (int)pos * 2; // Stereo
                    float t = pos - (int)pos; // pos between x1 & x2

                    // Left
                    float x0 = index < 2 ? 0 : sBuffer[index - 2];
                    float x1 = sBuffer[index];
                    float x2 = index < sBuffer.Length - 2 ? sBuffer[index + 2] : x1;
                    float x3 = index < sBuffer.Length - 4 ? sBuffer[index + 4] : x2;

                    buffer[offset + i] = InterpolateHermite(x0, x1, x2, x3, t);

                    // Right
                    index++;
                    x0 = index < 2 ? 0 : sBuffer[index - 2];
                    x1 = sBuffer[index];
                    x2 = index < sBuffer.Length - 2 ? sBuffer[index + 2] : x1;
                    x3 = index < sBuffer.Length - 4 ? sBuffer[index + 4] : x2;

                    buffer[offset + i + 1] = InterpolateHermite(x0, x1, x2, x3, t);

                    pos += step;
                }
            }
        }

        internal static float InterpolateHermite(float x0, float x1, float x2, float x3, float t)
        {
            float diff = x1 - x2;
            float c1 = x2 - x0;
            float c3 = x3 - x0 + 3 * diff;
            float c2 = -(2 * diff + c1 + c3);
            return 0.5f * ((c3 * t + c2) * t + c1) * t + x1;
        }
    }
}
