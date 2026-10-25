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
    /// Engine hooks, all called with ReBuzzCore.AudioLock held:
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
        readonly List<SequenceCore> stoppedScratch = new List<SequenceCore>();

        int beatsPerBar = 4;
        LaunchQuantization launchQuantization = LaunchQuantization.Bar;

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
            bool startTransport;
            lock (ReBuzzCore.AudioLock)
            {
                scheduler.Launch(seq, pat, QuantizeTicks(options), options?.Loop ?? true);
                startTransport = !buzz.Playing;
            }
            if (startTransport)
                buzz.Playing = true;
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
            bool startTransport;
            lock (ReBuzzCore.AudioLock)
            {
                scheduler.LaunchScene(list, QuantizeTicks(options), options?.Loop ?? true);
                startTransport = !buzz.Playing;
            }
            if (startTransport)
                buzz.Playing = true;
            foreach (var (seq, _) in list)
                RaiseTrackStateChanged(seq);
        }

        public void Stop(ISequence track, bool immediate = false)
        {
            var seq = ToSequence(track);
            lock (ReBuzzCore.AudioLock)
            {
                scheduler.Stop(seq, GetQuantizationTicks(launchQuantization), immediate);
            }
            RaiseTrackStateChanged(seq);
        }

        public void StopAll(bool immediate = false)
        {
            lock (ReBuzzCore.AudioLock)
            {
                scheduler.StopAll(GetQuantizationTicks(launchQuantization), immediate);
            }
            RaiseAllChanged();
        }

        public void BackToArrangement()
        {
            lock (ReBuzzCore.AudioLock)
            {
                scheduler.BackToArrangement();
            }
            RaiseAllChanged();
            PropertyChanged.Raise(this, nameof(IsAnyArrangementOverridden));
        }

        /// <summary>
        /// Upstream's ISequence.TriggerEvent, re-implemented on the session: <paramref name="songTime"/>
        /// is a song position (ticks), converted to a session clock value.
        /// </summary>
        internal void TriggerFromSequence(SequenceCore seq, int songTime, SequenceEvent e, bool loop)
        {
            if (songTime == 0 || e == null)
            {
                lock (ReBuzzCore.AudioLock)
                    scheduler.Stop(seq, 0, immediate: true);
                RaiseTrackStateChanged(seq);
                return;
            }

            lock (ReBuzzCore.AudioLock)
            {
                long at;
                if (!scheduler.IsRunning)
                {
                    at = 0;
                }
                else
                {
                    var song = buzz.SongCore;
                    int delta = songTime - song.PlayPosition;
                    int loopLength = song.LoopEnd - song.LoopStart;
                    if (delta <= 0 && loopLength > 0) delta += loopLength;
                    at = scheduler.Clock + Math.Max(1, delta);
                }

                if (e.Type == SequenceEventType.PlayPattern && e.Pattern is PatternCore pat && pat.Machine == seq.Machine)
                    scheduler.LaunchAt(seq, pat, at, loop);
                else
                    scheduler.Stop(seq, 0, immediate: true);
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

        public bool IsAnyArrangementOverridden => scheduler.IsAnyOverridden;

        /// <summary>True while a clip plays or waits; the song then keeps running at its loop end.</summary>
        internal bool HasActivity => scheduler.HasActivity;

        // ------------------------------------------------------------------ engine hooks (AudioLock held)

        /// <summary>Tick boundary while the transport plays. Starts the clock on the first tick.</summary>
        internal void OnTick(bool transportJustStarted)
        {
            if (transportJustStarted || !scheduler.IsRunning)
                scheduler.Start(buzz.SongCore.PlayPosition);
            else
                scheduler.Advance();

            var changes = scheduler.Changes;
            if (changes.Count == 0) return;

            for (int i = 0; i < changes.Count; i++)
            {
                var c = changes[i];
                if (c.Kind == SessionChangeKind.Stopped || c.Kind == SessionChangeKind.Ended)
                    Silence(c.Track);
            }
            RaiseTrackStateChanged(changes.Select(c => c.Track).ToArray());
        }

        /// <summary>Transport stopped (machines are stopped by the caller).</summary>
        internal void OnTransportStopped()
        {
            SequenceCore[] changed;
            lock (ReBuzzCore.AudioLock)
            {
                bool wasOverridden = scheduler.IsAnyOverridden;
                scheduler.Halt();
                changed = scheduler.Changes.Select(c => c.Track).ToArray();
                if (wasOverridden)
                    buzz.dispatcher.BeginInvoke(() => PropertyChanged.Raise(this, nameof(IsAnyArrangementOverridden)));
            }
            RaiseTrackStateChanged(changed);
        }

        internal void OnSequenceRemoved(SequenceCore seq)
        {
            if (scheduler.Forget(seq) != null)
                Silence(seq);
        }

        internal void OnPatternDeleted(PatternCore pattern)
        {
            stoppedScratch.Clear();
            scheduler.ForgetClip(pattern, stoppedScratch);
            foreach (var seq in stoppedScratch)
                Silence(seq);
            if (stoppedScratch.Count > 0)
                RaiseTrackStateChanged(stoppedScratch.ToArray());
        }

        internal void Reset()
        {
            lock (ReBuzzCore.AudioLock)
            {
                scheduler.Reset();
            }
        }

        /// <summary>Note-offs for a track whose clip stopped mid-note.</summary>
        void Silence(SequenceCore seq)
        {
            var machine = seq.MachineCore;
            if (machine == null || !machine.Ready || machine.DLL == null || machine.DLL.IsCrashed)
                return;

            // Buzz-style note columns: set every note parameter to Off (sent on the next tick).
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

            // MIDI-driven machines (VST via Polac, piano roll...).
            buzz.MachineManager.SendMIDIControlChange(machine, MidiAllNotesOff, machine.MIDIInputChannel, 0);
        }

        // ------------------------------------------------------------------ helpers

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
    }
}
