using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Session View engine (fork addition): ReBuzz side of <see cref="SessionScheduler{TTrack,TClip}"/>.
    /// Tracks are sequences, clips are patterns. See docs/specs/session-view.md, section 4.1.
    ///
    /// Locking: the scheduler is guarded by its own small lock (<see cref="gate"/>), never by
    /// ReBuzzCore.AudioLock, so a request (UI or MIDI thread) never waits for a whole audio
    /// buffer. Engine hooks run on the audio thread with AudioLock held and take the gate
    /// inside it (order AudioLock -> gate); requests take only the gate. Nothing is called out
    /// to machines or the dispatcher while the gate is held.
    ///
    /// Latency compensation (0.4.1): the engine renders ahead of what the listener hears by the
    /// output latency. Requests are quantized from what is heard (<see cref="HeardClock"/>), so
    /// a launch pressed just before a bar line, as heard, lands on that bar line: if the engine
    /// has rendered past it already, the clip starts on the next tick in step with the bar
    /// (catch-up) instead of a whole bar late.
    ///
    /// Engine hooks:
    ///  - <see cref="OnTick"/> from WorkManager at every tick boundary while playing
    ///  - <see cref="OnTransportStopped"/> from ReBuzzCore.Playing
    ///  - <see cref="OnSequenceRemoved"/>, <see cref="OnPatternDeleted"/>, <see cref="Reset"/>
    /// </summary>
    internal sealed class SessionCore : ISession
    {
        const int DoNotRecordFlag = 1 << 16;    // ParameterCore.SetValue: don't record into patterns
        const int MidiAllNotesOff = 123;

        readonly ReBuzzCore buzz;
        readonly SessionScheduler<SequenceCore, PatternCore> scheduler;
        readonly object gate = new object();

        // Audio thread only (OnTick): copy of the scheduler's changes, reused to avoid allocation.
        readonly List<SessionChange<SequenceCore, PatternCore>> tickChanges = new List<SessionChange<SequenceCore, PatternCore>>();

        int beatsPerBar = 4;
        LaunchQuantization launchQuantization = LaunchQuantization.Bar;

        // Rendered position at the end of the last audio callback (under the gate).
        double renderedTicks = -1;
        double renderedSamplesPerTick;
        long renderedAt;

        /// <summary>Quantize requests from what is heard rather than what is rendered (default on).</summary>
        internal volatile bool LatencyCompensation = true;

        IRegistryEx settings;

        /// <summary>Reads the session's engine settings (latency compensation) from the registry.</summary>
        internal void UseSettings(IRegistryEx registry)
        {
            settings = registry;
            try
            {
                if (registry != null)
                    LatencyCompensation = registry.Read("LatencyCompensation", 1, "Session") != 0;
            }
            catch (Exception)
            {
            }
        }

        internal void SetLatencyCompensation(bool on)
        {
            LatencyCompensation = on;
            try
            {
                settings?.Write("LatencyCompensation", on ? 1 : 0, "Session");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Arrangement record (0.3); set by ReBuzzCoreInitialization.</summary>
        internal SessionRecorder Recorder { get; set; }
        volatile bool wasRecording;       // audio thread: recording on the previous tick

        internal SessionCore(ReBuzzCore buzz)
        {
            this.buzz = buzz;
            scheduler = new SessionScheduler<SequenceCore, PatternCore>(p => p.Length);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<ISequence> TrackStateChanged;

        // ------------------------------------------------------------------ settings

        public long Clock => scheduler.Clock;

        public int BeatsPerBar
        {
            get => beatsPerBar;
            set
            {
                value = Math.Clamp(value, 1, 64);
                if (value == beatsPerBar) return;
                beatsPerBar = value;
                PropertyChanged.Raise(this, nameof(BeatsPerBar));
            }
        }

        /// <summary>
        /// Audio thread (scene launch, 0.5): sets beats per bar without raising PropertyChanged
        /// there (listeners update UI); posts it instead.
        /// </summary>
        void SetBeatsPerBarFromEngine(int value)
        {
            value = Math.Clamp(value, 1, 64);
            if (value == beatsPerBar) return;
            beatsPerBar = value;
            RaisePropertyChangedOnUi(nameof(BeatsPerBar));
        }

        /// <summary>Clock value the bar grid counts from (a scene's time signature change restarts it).</summary>
        internal long GridOrigin => scheduler.GridOrigin;

        long gridOriginBar;

        /// <summary>Bars counted before <see cref="GridOrigin"/> (for showing bar numbers across a time signature change).</summary>
        internal long GridOriginBar => System.Threading.Volatile.Read(ref gridOriginBar);

        /// <summary>Whether the track's clip repeats (Repeat mode, held).</summary>
        internal bool IsRepeating(ISequence track) => track is SequenceCore seq && scheduler.IsRepeating(seq);

        public LaunchQuantization LaunchQuantization
        {
            get => launchQuantization;
            set
            {
                if (value == launchQuantization) return;
                launchQuantization = value;
                PropertyChanged.Raise(this, nameof(LaunchQuantization));
            }
        }

        public int GetQuantizationTicks(LaunchQuantization quantization)
        {
            int beat = Math.Max(1, buzz.TPB);
            int bar = beat * beatsPerBar;
            return quantization switch
            {
                LaunchQuantization.None => 0,
                LaunchQuantization.Beat => beat,
                LaunchQuantization.Bar => bar,
                LaunchQuantization.TwoBars => 2 * bar,
                LaunchQuantization.FourBars => 4 * bar,
                LaunchQuantization.EightBars => 8 * bar,
                LaunchQuantization.HalfNote => 2 * beat,
                LaunchQuantization.Eighth => Math.Max(1, beat / 2),
                LaunchQuantization.Sixteenth => Math.Max(1, beat / 4),
                _ => bar
            };
        }

        int QuantizeTicks(LaunchOptions options) =>
            GetQuantizationTicks(options?.Quantization ?? launchQuantization);

        /// <summary>The scheduler's view of a launch (0.5): shape, legato, repeat, gain.</summary>
        static LaunchSpec SpecOf(LaunchOptions options, int quantize, bool forScene = false)
        {
            if (options == null) return LaunchSpec.Looped;
            var shape = new ClipShape(options.Start, options.LoopStart, options.LoopEnd, options.Loop);
            int repeat = !forScene && options.Mode == ClipLaunchMode.Repeat ? Math.Max(1, quantize) : 0;
            return new LaunchSpec(shape, options.Legato, repeat, options.Gain);
        }

        // Scene settings waiting for their launch tick (0.5), under the gate.
        long sceneAt = -1;
        int sceneTempo;
        int sceneBeatsPerBar;

        // ------------------------------------------------------------------ requests (any thread)

        /// <summary>A press of the clip's launch control: launches it, or with Toggle stops it if it plays.</summary>
        public void Launch(ISequence track, IPattern clip, LaunchOptions options = null)
        {
            var seq = ToSequence(track);
            var pat = ToClip(seq, clip);
            int quantize = QuantizeTicks(options);
            bool stopped = false;
            lock (gate)
            {
                if (options?.Mode == ClipLaunchMode.Toggle && ReferenceEquals(scheduler.GetPlaying(seq), pat) && !scheduler.IsStopQueued(seq))
                {
                    scheduler.Stop(seq, quantize, false, HeardClock());
                    stopped = true;
                }
                else
                {
                    scheduler.Launch(seq, pat, quantize, HeardClock(), SpecOf(options, quantize), Math.Max(0, options?.StartOffset ?? 0));
                }
            }
            if (!stopped)
                StartTransportIfStopped();
            RaiseTrackStateChanged(seq);
        }

        /// <summary>Release of the clip's launch control (0.5): Gate stops it, Repeat stops repeating.</summary>
        public void Release(ISequence track, IPattern clip, LaunchOptions options = null)
        {
            var mode = options?.Mode ?? ClipLaunchMode.Trigger;
            if (mode != ClipLaunchMode.Gate && mode != ClipLaunchMode.Repeat) return;
            var seq = ToSequence(track);
            var pat = ToClip(seq, clip);
            int quantize = QuantizeTicks(options);
            lock (gate)
            {
                scheduler.Release(seq, pat, quantize, HeardClock(), stop: mode == ClipLaunchMode.Gate);
            }
            RaiseTrackStateChanged(seq);
        }

        /// <summary>A playing or queued clip's markers changed (clip panel): it follows at once.</summary>
        internal void Reshape(ISequence track, IPattern clip, LaunchOptions options)
        {
            if (track is not SequenceCore seq || clip is not PatternCore pat || options == null) return;
            lock (gate)
            {
                scheduler.Reshape(seq, pat, new ClipShape(options.Start, options.LoopStart, options.LoopEnd, options.Loop));
            }
        }

        /// <summary>
        /// Clips launched together with their own options (0.5). With <paramref name="scene"/>, a
        /// scene launch: its settings replace those of a scene still waiting to start, as its
        /// clips replace that scene's. Null: a group slot, no scene settings.
        /// </summary>
        public void LaunchScene(IEnumerable<(ISequence Track, IPattern Clip, LaunchOptions Options)> slots, SceneOptions scene) =>
            LaunchTogether(slots, scene?.Quantization, scene);

        /// <summary>Clips launched together with the same options (a group slot); no scene settings.</summary>
        public void LaunchScene(IEnumerable<(ISequence Track, IPattern Clip)> slots, LaunchOptions options = null)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            LaunchTogether(slots.Select(s => (s.Track, s.Clip, options)), options?.Quantization, null);
        }

        void LaunchTogether(IEnumerable<(ISequence Track, IPattern Clip, LaunchOptions Options)> slots, LaunchQuantization? quantization, SceneOptions scene)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            var list = slots
                .Where(s => s.Track != null && s.Clip != null)
                .Select(s => { var seq = ToSequence(s.Track); return (seq, ToClip(seq, s.Clip), s.Options); })
                .ToList();
            int tempo = Math.Clamp(scene?.Tempo ?? 0, 0, 500);
            int beatsPerBar = Math.Clamp(scene?.BeatsPerBar ?? 0, 0, 64);
            if (list.Count == 0 && tempo <= 0 && beatsPerBar <= 0)
            {
                // An empty scene launches nothing, but it is still the latest scene launch.
                if (scene != null)
                    lock (gate) sceneAt = -1;
                return;
            }
            int quantize = GetQuantizationTicks(quantization ?? launchQuantization);
            lock (gate)
            {
                long at = scheduler.LaunchScene(list.Select(s => (s.Item1, s.Item2, SpecOf(s.Item3, quantize, forScene: true))), quantize, HeardClock());
                if (scene != null)
                {
                    sceneAt = tempo > 0 || beatsPerBar > 0 ? at : -1;
                    sceneTempo = tempo;
                    sceneBeatsPerBar = beatsPerBar;
                }
            }
            StartTransportIfStopped();
            if (list.Count > 0)
                RaiseTrackStateChanged(list.Select(s => s.Item1).ToArray());
        }

        public void Stop(ISequence track, bool immediate = false)
        {
            var seq = ToSequence(track);
            int quantize = GetQuantizationTicks(launchQuantization);
            lock (gate)
            {
                scheduler.Stop(seq, quantize, immediate, HeardClock());
            }
            RaiseTrackStateChanged(seq);
        }

        public void StopAll(bool immediate = false)
        {
            int quantize = GetQuantizationTicks(launchQuantization);
            lock (gate)
            {
                scheduler.StopAll(quantize, immediate, HeardClock());
            }
            RaiseAllChanged();
        }

        public void BackToArrangement()
        {
            lock (gate)
            {
                scheduler.BackToArrangement();
            }
            RaiseAllChanged();
            RaisePropertyChangedOnUi(nameof(IsAnyArrangementOverridden));
        }

        /// <summary>
        /// Upstream's ISequence.TriggerEvent, re-implemented on the session: <paramref name="songTime"/>
        /// is a song position (ticks), converted to a session clock value. Time 0 or a null event
        /// cancels, as upstream.
        /// </summary>
        internal void TriggerFromSequence(SequenceCore seq, int songTime, SequenceEvent e, bool loop)
        {
            var song = buzz.SongCore;
            int songPosition = song.PlayPosition;
            int loopLength = song.LoopEnd - song.LoopStart;
            bool launch = songTime != 0 && e != null && e.Type == SequenceEventType.PlayPattern
                          && e.Pattern is PatternCore && e.Pattern.Machine == seq.Machine;

            lock (gate)
            {
                if (!launch)
                {
                    scheduler.Stop(seq, 0, immediate: true);
                }
                else
                {
                    long at = 0;
                    if (scheduler.IsRunning)
                    {
                        int delta = songTime - songPosition;
                        if (delta <= 0 && loopLength > 0) delta += loopLength;
                        at = scheduler.Clock + Math.Max(1, delta);
                    }
                    scheduler.LaunchAt(seq, (PatternCore)e.Pattern, at, loop);
                }
            }
            RaiseTrackStateChanged(seq);
        }

        // ------------------------------------------------------------------ reads (any thread, lock-free)

        public IPattern GetPlaying(ISequence track) => track is SequenceCore seq ? scheduler.GetPlaying(seq) : null;

        internal PatternCore GetPlayingCore(SequenceCore seq) => scheduler.GetPlaying(seq);

        public IPattern GetQueued(ISequence track) => track is SequenceCore seq ? scheduler.GetQueued(seq) : null;

        public ClipState GetState(ISequence track, IPattern clip) =>
            track is SequenceCore seq && clip is PatternCore pat ? (ClipState)scheduler.GetState(seq, pat) : ClipState.Stopped;

        public long GetClipStartClock(ISequence track) => track is SequenceCore seq ? scheduler.GetStartClock(seq) : -1;

        public int GetClipPosition(ISequence track) => track is SequenceCore seq ? scheduler.GetPosition(seq) : -1;

        public bool IsArrangementOverridden(ISequence track) => track is SequenceCore seq && scheduler.IsOverridden(seq);

        // These two iterate the scheduler's track list, which Reset/Forget change: take the gate.
        public bool IsAnyArrangementOverridden { get { lock (gate) return scheduler.IsAnyOverridden; } }

        /// <summary>True while a clip plays or waits; the song then keeps running at its loop end.</summary>
        internal bool HasActivity { get { lock (gate) return scheduler.HasActivity; } }

        // ------------------------------------------------------------------ latency compensation

        /// <summary>
        /// Audio thread, end of each callback (AudioLock held): where the render is now, for
        /// <see cref="HeardClock"/>.
        /// </summary>
        internal void PublishRenderedPosition()
        {
            var mi = ReBuzzCore.masterInfo;
            long c = scheduler.Clock;
            int spt = Math.Max(1, mi.SamplesPerTick);
            lock (gate)
            {
                renderedTicks = c < 0 ? -1 : c + (double)mi.PosInTick / spt;
                renderedSamplesPerTick = spt;
                renderedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        }

        /// <summary>
        /// Tick the listener hears now (call under the gate): the rendered position minus the
        /// part of the output latency that hasn't played out since the last callback. Equals
        /// <see cref="Clock"/> without a latency estimate, with compensation off, or when no
        /// audio has been rendered recently.
        /// </summary>
        long HeardClock()
        {
            long c = scheduler.Clock;
            if (c < 0 || !LatencyCompensation || renderedTicks < 0) return c;
            int latency = buzz.OutputLatencySamples;
            int rate = ReBuzzCore.masterInfo.SamplesPerSec;
            if (latency <= 0 || rate <= 0) return c;
            double sinceRender = (System.Diagnostics.Stopwatch.GetTimestamp() - renderedAt) * (double)rate / System.Diagnostics.Stopwatch.Frequency;
            double behind = latency - Math.Max(0, sinceRender);
            if (behind <= 0) return c;
            double heard = renderedTicks - behind / renderedSamplesPerTick;
            return Math.Min(c, (long)Math.Floor(heard));
        }

        /// <summary>Output latency used for compensation, in milliseconds (0 when unknown or off).</summary>
        internal double CompensatedLatencyMs
        {
            get
            {
                int rate = ReBuzzCore.masterInfo.SamplesPerSec;
                return LatencyCompensation && rate > 0 ? buzz.OutputLatencySamples * 1000.0 / rate : 0;
            }
        }

        // ------------------------------------------------------------------ engine hooks

        /// <summary>Tick boundary while the transport plays (audio thread, AudioLock held).
        /// Starts the clock on the first tick.</summary>
        internal void OnTick(bool transportJustStarted)
        {
            int songPosition = buzz.SongCore.PlayPosition;
            tickChanges.Clear();
            int tempo = 0, beats = 0;
            lock (gate)
            {
                if (transportJustStarted || !scheduler.IsRunning)
                {
                    long before = scheduler.Clock;
                    if (sceneAt >= 0)
                        sceneAt = before < 0
                            ? songPosition                          // queued while stopped: on the first tick
                            : sceneAt + songPosition - before;      // restarted while running: shifts with the clips
                    scheduler.Start(songPosition);
                }
                else
                {
                    scheduler.Advance();
                }
                tickChanges.AddRange(scheduler.Changes);

                if (sceneAt >= 0 && scheduler.Clock >= sceneAt)
                {
                    tempo = sceneTempo;
                    beats = sceneBeatsPerBar;
                    sceneAt = -1;
                    if (beats > 0 && beats != beatsPerBar)
                    {
                        // The bar grid restarts with the new time signature (bars so far are kept for display).
                        long bar = (long)Math.Max(1, buzz.TPB) * beatsPerBar;
                        long bars = Math.Max(0, scheduler.Clock - scheduler.GridOrigin) / bar;
                        System.Threading.Volatile.Write(ref gridOriginBar, gridOriginBar + bars);
                        scheduler.SetGridOrigin(scheduler.Clock);
                    }
                }
            }
            if (tempo > 0)
                buzz.SetBPMFromEngine(tempo);                        // the engine picks it up on the next block
            if (beats > 0)
                SetBeatsPerBarFromEngine(beats);
            RecordTick(songPosition);
            if (tickChanges.Count == 0) return;

            bool anyOverrideChange = false;
            for (int i = 0; i < tickChanges.Count; i++)
            {
                var c = tickChanges[i];
                if (c.Kind == SessionChangeKind.Stopped || c.Kind == SessionChangeKind.Ended)
                {
                    Silence(c.Track);
                    if (c.Track.MachineCore != null)
                        c.Track.MachineCore.ClipGain = 1f;           // velocity gain ends with the clip
                }
                if (c.Kind == SessionChangeKind.Launched && c.Previous == null)
                    anyOverrideChange = true;
            }
            // Velocity (0.5): the machine plays at the launched clip's gain (after the stops of
            // this tick, which may be on another track of the same machine).
            for (int i = 0; i < tickChanges.Count; i++)
            {
                var c = tickChanges[i];
                if (c.Kind == SessionChangeKind.Launched && c.Track.MachineCore != null)
                    c.Track.MachineCore.ClipGain = (float)scheduler.GetGain(c.Track);
            }
            RaiseTrackStateChanged(tickChanges.Select(c => c.Track).ToArray());
            if (anyOverrideChange)
                RaisePropertyChangedOnUi(nameof(IsAnyArrangementOverridden));
        }

        /// <summary>
        /// Arrangement record (audio thread, after the tick's changes): reports clip starts
        /// (launches and loop restarts) and stops while ReBuzz records. When recording starts
        /// with clips already playing, their current loop is written from where it began.
        /// </summary>
        void RecordTick(int songPosition)
        {
            var recorder = Recorder;
            if (recorder == null) return;
            if (!buzz.Recording)
            {
                if (wasRecording)
                {
                    wasRecording = false;
                    recorder.EndPass(songPosition);
                }
                return;
            }
            bool starting = !wasRecording;
            wasRecording = true;

            for (int i = 0; i < tickChanges.Count; i++)
            {
                var c = tickChanges[i];
                if (c.Kind == SessionChangeKind.Moved)
                {
                    recorder.ClipCut(c.Track, songPosition);
                    continue;
                }
                if (c.Kind != SessionChangeKind.Stopped && c.Kind != SessionChangeKind.Ended) continue;
                if (scheduler.IsOverridden(c.Track))
                    recorder.ClipStopped(c.Track, songPosition);
                else
                    recorder.TrackReleased(c.Track, songPosition);   // Back to Arrangement
            }

            var sequences = buzz.SongCore.SequencesList;
            for (int i = 0; i < sequences.Count; i++)
            {
                var seq = sequences[i];
                var clip = scheduler.GetPlaying(seq);
                if (clip == null) continue;
                // 0.5: (re)starts are launches, Repeat restarts and loop wraps (cycle back at 0).
                // A clip that starts after its first row (start marker, loop start) is written from
                // its first row: Buzz sequences can't start a pattern partway.
                long cycle = scheduler.GetCycleElapsed(seq);
                if (cycle == 0)
                    recorder.ClipStarted(seq, clip, songPosition);
                else if (starting && cycle > 0 && songPosition - cycle >= 0)
                    recorder.ClipStarted(seq, clip, (int)(songPosition - cycle));
            }
        }

        /// <summary>Transport stopped (any thread; machines are stopped by the caller).</summary>
        internal void OnTransportStopped()
        {
            if (wasRecording)
            {
                wasRecording = false;
                Recorder?.EndPass(buzz.SongCore.PlayPosition);
            }

            SequenceCore[] changed;
            bool wasOverridden;
            lock (gate)
            {
                wasOverridden = scheduler.IsAnyOverridden;
                scheduler.Halt();
                changed = scheduler.Changes.Select(c => c.Track).ToArray();
                sceneAt = -1;
                System.Threading.Volatile.Write(ref gridOriginBar, 0);
            }
            foreach (var seq in changed)
                if (seq.MachineCore != null)
                    seq.MachineCore.ClipGain = 1f;                    // 0.5: velocity gain ends with the transport
            RaiseTrackStateChanged(changed);
            if (wasOverridden)
                RaisePropertyChangedOnUi(nameof(IsAnyArrangementOverridden));
        }

        /// <summary>A sequence was removed (AudioLock held).</summary>
        internal void OnSequenceRemoved(SequenceCore seq)
        {
            PatternCore playing;
            lock (gate)
            {
                playing = scheduler.Forget(seq);
            }
            if (playing != null)
                Silence(seq);
        }

        /// <summary>A pattern was deleted (AudioLock held).</summary>
        internal void OnPatternDeleted(PatternCore pattern)
        {
            var stopped = new List<SequenceCore>();
            lock (gate)
            {
                scheduler.ForgetClip(pattern, stopped);
            }
            foreach (var seq in stopped)
                Silence(seq);
            if (stopped.Count > 0)
                RaiseTrackStateChanged(stopped.ToArray());
        }

        /// <summary>New song or song loaded.</summary>
        internal void Reset()
        {
            lock (gate)
            {
                scheduler.Reset();
            }
            wasRecording = false;
            Recorder?.Reset();
        }

        /// <summary>Note-offs for a track whose clip stopped mid-note (audio thread).</summary>
        void Silence(SequenceCore seq)
        {
            var machine = seq.MachineCore;
            if (machine == null || !machine.Ready || machine.DLL == null || machine.DLL.IsCrashed)
                return;

            try
            {
                // Buzz-style note columns: every note parameter to Off (sent on the next tick).
                var groups = machine.ParameterGroupsList;
                for (int g = 1; g < groups.Count; g++)
                {
                    var group = groups[g];
                    var parameters = group.ParametersList;
                    for (int p = 0; p < parameters.Count; p++)
                    {
                        var parameter = parameters[p];
                        if (parameter.Type != ParameterType.Note) continue;
                        for (int t = 0; t < group.TrackCount; t++)
                            parameter.SetValue(t | DoNotRecordFlag, BuzzNote.Off);
                    }
                }

                // MIDI-driven machines (VSTs via Polac, piano roll...).
                buzz.MachineManager.SendMIDIControlChange(machine, MidiAllNotesOff, machine.MIDIInputChannel, 0);
            }
            catch (Exception ex)
            {
                // Never let a misbehaving machine take the audio thread down.
                buzz.DCWriteLine($"Session: note-offs for '{machine.Name}' failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ helpers

        void StartTransportIfStopped()
        {
            // Outside any lock: setting Playing only flags the start for the next tick.
            if (!buzz.Playing)
                buzz.Playing = true;
        }

        static SequenceCore ToSequence(ISequence track)
        {
            if (track is SequenceCore seq) return seq;
            throw new ArgumentException("Not a ReBuzz sequence.", nameof(track));
        }

        static PatternCore ToClip(SequenceCore seq, IPattern clip)
        {
            if (clip is not PatternCore pat)
                throw new ArgumentException("Not a ReBuzz pattern.", nameof(clip));
            if (pat.Machine != seq.Machine)
                throw new ArgumentException($"Pattern '{pat.Name}' belongs to '{pat.Machine?.Name}', not to the track's machine '{seq.Machine?.Name}'.", nameof(clip));
            return pat;
        }

        void RaiseTrackStateChanged(params SequenceCore[] tracks)
        {
            if (TrackStateChanged == null || tracks.Length == 0) return;
            buzz.dispatcher.BeginInvoke(() =>
            {
                foreach (var t in tracks.Distinct())
                    TrackStateChanged?.Invoke(t);
            });
        }

        void RaiseAllChanged()
        {
            if (TrackStateChanged == null) return;
            RaiseTrackStateChanged(buzz.SongCore.SequencesList.ToArray());
        }

        void RaisePropertyChangedOnUi(string name)
        {
            if (PropertyChanged == null) return;
            buzz.dispatcher.BeginInvoke(() => PropertyChanged.Raise(this, name));
        }
    }
}
