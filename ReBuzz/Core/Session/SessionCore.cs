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
    /// ReBuzzCore.AudioLock. The audio thread holds AudioLock while it synchronously invokes the
    /// UI thread (e.g. when the transport starts), so a UI request that waited for AudioLock
    /// could deadlock. Engine hooks run on the audio thread with AudioLock held and take the
    /// gate inside it (order AudioLock -> gate); UI requests take only the gate. Nothing is
    /// called out to machines or the dispatcher while the gate is held.
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
                _ => bar
            };
        }

        int QuantizeTicks(LaunchOptions options) =>
            GetQuantizationTicks(options?.Quantization ?? launchQuantization);

        // ------------------------------------------------------------------ requests (any thread)

        public void Launch(ISequence track, IPattern clip, LaunchOptions options = null)
        {
            var seq = ToSequence(track);
            var pat = ToClip(seq, clip);
            int quantize = QuantizeTicks(options);
            lock (gate)
            {
                scheduler.Launch(seq, pat, quantize, options?.Loop ?? true);
            }
            StartTransportIfStopped();
            RaiseTrackStateChanged(seq);
        }

        public void LaunchScene(IEnumerable<(ISequence Track, IPattern Clip)> slots, LaunchOptions options = null)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            var list = slots
                .Where(s => s.Track != null && s.Clip != null)
                .Select(s => { var seq = ToSequence(s.Track); return (seq, ToClip(seq, s.Clip)); })
                .ToList();
            if (list.Count == 0) return;
            int quantize = QuantizeTicks(options);
            lock (gate)
            {
                scheduler.LaunchScene(list, quantize, options?.Loop ?? true);
            }
            StartTransportIfStopped();
            RaiseTrackStateChanged(list.Select(s => s.Item1).ToArray());
        }

        public void Stop(ISequence track, bool immediate = false)
        {
            var seq = ToSequence(track);
            int quantize = GetQuantizationTicks(launchQuantization);
            lock (gate)
            {
                scheduler.Stop(seq, quantize, immediate);
            }
            RaiseTrackStateChanged(seq);
        }

        public void StopAll(bool immediate = false)
        {
            int quantize = GetQuantizationTicks(launchQuantization);
            lock (gate)
            {
                scheduler.StopAll(quantize, immediate);
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

        // ------------------------------------------------------------------ engine hooks

        /// <summary>Tick boundary while the transport plays (audio thread, AudioLock held).
        /// Starts the clock on the first tick.</summary>
        internal void OnTick(bool transportJustStarted)
        {
            int songPosition = buzz.SongCore.PlayPosition;
            tickChanges.Clear();
            lock (gate)
            {
                if (transportJustStarted || !scheduler.IsRunning)
                    scheduler.Start(songPosition);
                else
                    scheduler.Advance();
                tickChanges.AddRange(scheduler.Changes);
            }
            RecordTick(songPosition);
            if (tickChanges.Count == 0) return;

            bool anyOverrideChange = false;
            for (int i = 0; i < tickChanges.Count; i++)
            {
                var c = tickChanges[i];
                if (c.Kind == SessionChangeKind.Stopped || c.Kind == SessionChangeKind.Ended)
                    Silence(c.Track);
                if (c.Kind == SessionChangeKind.Launched && c.Previous == null)
                    anyOverrideChange = true;
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
                int position = scheduler.GetPosition(seq);
                if (position == 0)
                    recorder.ClipStarted(seq, clip, songPosition);
                else if (starting && position > 0 && songPosition - position >= 0)
                    recorder.ClipStarted(seq, clip, songPosition - position);
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
            }
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
