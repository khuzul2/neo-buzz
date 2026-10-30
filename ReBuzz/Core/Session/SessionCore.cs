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
    internal sealed partial class SessionCore : ISession
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
                {
                    LatencyCompensation = registry.Read("LatencyCompensation", 1, "Session") != 0;
                    SelectOnLaunch = registry.Read("SelectOnLaunch", 1, "Session") != 0;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Launching a slot or scene selects it, from the mouse, keys or MIDI (Live's "Select on Launch", 0.6; a preference).</summary>
        internal bool SelectOnLaunch { get; private set; } = true;

        internal void SetSelectOnLaunch(bool on)
        {
            SelectOnLaunch = on;
            try
            {
                settings?.Write("SelectOnLaunch", on ? 1 : 0, "Session");
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
            // Follow actions (0.6): each track's column comes from the launch map; slot quantization codes are LaunchQuantization values.
            scheduler.ColumnOf = seq => seq.FollowColumn as SessionScheduler<SequenceCore, PatternCore>.Slot[];
            scheduler.QuantizeTicksOf = code => GetQuantizationTicks((LaunchQuantization)code);
            // 0.6.1: deleted patterns and removed sequences can't come back through stale references.
            scheduler.IsLiveClip = p => !p.IsDeleted;
            scheduler.IsLiveTrack = seq => !seq.IsRemoved;
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
                lock (gate)
                {
                    if (scheduler.IsRunning)
                    {
                        // 0.6.1: as a scene's change, the bar grid restarts, here on the downbeat
                        // of the bar playing now, so later bar lines follow the new length.
                        long bar = (long)Math.Max(1, buzz.TPB) * beatsPerBar;
                        long origin = scheduler.GridOrigin;
                        long bars = Math.Max(0, scheduler.Clock - origin) / bar;
                        System.Threading.Volatile.Write(ref gridOriginBar, gridOriginBar + bars);
                        scheduler.SetGridOrigin(origin + bars * bar);
                    }
                    beatsPerBar = value;
                }
                PropertyChanged.Raise(this, nameof(BeatsPerBar));
            }
        }

        /// <summary>Clock value the bar grid counts from (a scene's time signature change restarts it).</summary>
        internal long GridOrigin => scheduler.GridOrigin;

        long gridOriginBar;

        /// <summary>Bars counted before <see cref="GridOrigin"/> (for showing bar numbers across a time signature change).</summary>
        internal long GridOriginBar => System.Threading.Volatile.Read(ref gridOriginBar);

        /// <summary>Whether the track's clip repeats (Repeat mode, held).</summary>
        internal bool IsRepeating(ISequence track) => track is SequenceCore seq && scheduler.IsRepeating(seq);

        /// <summary>Clock value the track's follow action is due at, or -1 if none is (0.6).</summary>
        internal long GetFollowAt(ISequence track) => track is SequenceCore seq ? scheduler.GetFollowAt(seq) : -1;

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

        /// <summary>The scheduler's view of a launch: shape, legato, repeat, gain (0.5); follow actions and slot (0.6).</summary>
        static LaunchSpec SpecOf(LaunchOptions options, int quantize, bool forScene = false)
        {
            if (options == null) return LaunchSpec.Looped;
            var shape = new ClipShape(options.Start, options.LoopStart, options.LoopEnd, options.Loop);
            int repeat = !forScene && options.Mode == ClipLaunchMode.Repeat ? Math.Max(1, quantize) : 0;
            return new LaunchSpec(shape, options.Legato, repeat, options.Gain, FollowSpecOf(options.Follow, linkable: true), options.Slot);
        }

        static FollowSpec FollowSpecOf(FollowOptions f, bool linkable) =>
            f == null ? null : new FollowSpec((FollowAction)(int)f.A, (FollowAction)(int)f.B, f.ChanceA, linkable && f.Linked, f.Multiplier, f.TimeTicks, f.JumpA, f.JumpB);

        // Scene settings waiting for their launch tick (0.5; 0.6: its row and follow actions), under the gate.
        long sceneAt = -1;
        long sceneGrid = -1;           // 0.6.1: the grid point it is heard on (earlier than sceneAt for a late launch)
        int sceneTempo;
        int sceneBeatsPerBar;
        int sceneRowPending = -1;
        FollowSpec sceneFollowPending;

        // The scene that launched last and its follow action (0.6), under the gate.
        int sceneRow = -1;
        FollowSpec sceneFollow;
        long sceneFollowAt = -1;

        /// <summary>Clip and scene follow actions happen (Live's global switch, 0.6).</summary>
        internal bool FollowActionsEnabled
        {
            get => scheduler.FollowActionsEnabled;
            set => scheduler.FollowActionsEnabled = value;
        }

        /// <summary>Seeds the random choices of follow actions (tests).</summary>
        internal void SeedFollowActions(int seed)
        {
            lock (gate) scheduler.Seed(seed);
        }

        // ------------------------------------------------------------------ requests (any thread)

        /// <summary>A press of the clip's launch control: launches it, or with Toggle stops it if it plays.</summary>
        public void Launch(ISequence track, IPattern clip, LaunchOptions options = null)
        {
            var seq = ToSequence(track);
            var pat = ToClip(seq, clip);
            if (pat.IsDeleted || seq.IsRemoved) return;      // 0.6.1: a stale reference (a pad pressed as it was deleted)
            bool stopped = false;
            lock (gate)
            {
                int quantize = QuantizeTicks(options);
                if (options?.Mode == ClipLaunchMode.Toggle && ReferenceEquals(scheduler.GetPlaying(seq), pat) && !scheduler.IsStopQueued(seq))
                {
                    scheduler.Stop(seq, quantize, false, HeardClock());
                    stopped = true;
                }
                else
                {
                    Remember($"{pat.Name} on {seq.Machine?.Name}", new[] { (seq, pat, scheduler.Capture(seq)) });
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
            lock (gate)
            {
                int quantize = QuantizeTicks(options);
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
        /// scene launch: its settings (tempo, beats, follow actions) replace those of a scene still
        /// waiting to start, as its clips replace that scene's, and it stops the tracks in
        /// <see cref="SceneOptions.StopTracks"/>. Null: a group slot, no scene settings.
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
            var stops = (scene?.StopTracks ?? Array.Empty<ISequence>())
                .OfType<SequenceCore>()
                .Where(t => !list.Any(l => l.Item1 == t))
                .Distinct()
                .ToList();
            int tempo = Math.Clamp(scene?.Tempo ?? 0, 0, 500);
            int beatsPerBar = Math.Clamp(scene?.BeatsPerBar ?? 0, 0, 64);
            var follow = FollowSpecOf(scene?.Follow, linkable: false);
            if (list.Count == 0 && stops.Count == 0 && tempo <= 0 && beatsPerBar <= 0 && follow == null)
            {
                // An empty scene launches nothing, but it is still the latest scene launch.
                if (scene != null)
                    lock (gate) ClearScene();
                return;
            }
            lock (gate)
            {
                int quantize = GetQuantizationTicks(quantization ?? launchQuantization);
                string label = scene == null ? (list.Count == 1 ? $"{list[0].Item2.Name} on {list[0].Item1.Machine?.Name}" : $"{list.Count} clips")
                             : scene.Scene >= 0 ? $"scene {scene.Scene + 1}" : "scene";
                Remember(label, list.Select(l => (l.Item1, l.Item2, scheduler.Capture(l.Item1)))
                                    .Concat(stops.Select(t => (t, (PatternCore)null, scheduler.Capture(t)))).ToArray(), isScene: scene != null);
                var (at, grid) = scheduler.LaunchScene(list.Select(s => (s.Item1, s.Item2, SpecOf(s.Item3, quantize, forScene: true))), quantize, HeardClock());
                foreach (var t in stops)
                    scheduler.StopAt(t, at);
                if (scene != null)
                {
                    // 0.6.1: the scene that played last stops following: this one replaces it.
                    sceneFollow = null;
                    sceneFollowAt = -1;
                    sceneAt = at;
                    sceneGrid = grid;
                    sceneTempo = tempo;
                    sceneBeatsPerBar = beatsPerBar;
                    sceneRowPending = scene.Scene;
                    sceneFollowPending = follow;
                }
            }
            StartTransportIfStopped();
            if (list.Count > 0 || stops.Count > 0)
                RaiseTrackStateChanged(list.Select(s => s.Item1).Concat(stops).ToArray());
        }

        // ------------------------------------------------------------------ launch undo (0.6)

        sealed class LaunchRecord
        {
            public string Label;
            public bool IsScene;
            public (SequenceCore Track, PatternCore Clip, object Before)[] Entries;
        }

        const int HistoryLength = 32;
        readonly List<LaunchRecord> history = new List<LaunchRecord>();     // under the gate

        /// <summary>Call under the gate, before the launch.</summary>
        void Remember(string label, (SequenceCore, PatternCore, object)[] entries, bool isScene = false)
        {
            if (entries.Length == 0) return;
            history.Add(new LaunchRecord { Label = label, Entries = entries, IsScene = isScene });
            if (history.Count > HistoryLength) history.RemoveAt(0);
            lastLaunch = label;
        }

        volatile string lastLaunch;     // the newest record's label, for the UI without the gate

        /// <summary>Drops undo entries (under the gate); a launch left with none goes from the history.</summary>
        void ForgetHistory(Func<(SequenceCore Track, PatternCore Clip, object Before), bool> gone)
        {
            for (int i = history.Count - 1; i >= 0; i--)
            {
                var entries = history[i].Entries;
                if (!entries.Any(gone)) continue;
                history[i].Entries = entries.Where(e => !gone(e)).ToArray();
                if (history[i].Entries.Length == 0)
                    history.RemoveAt(i);
            }
            lastLaunch = history.Count > 0 ? history[history.Count - 1].Label : null;
        }

        /// <summary>What <see cref="UndoLaunch"/> would undo ("scene 3", "Beat on Drums"), or null.</summary>
        internal string LastLaunch
        {
            get => lastLaunch;
        }

        /// <summary>
        /// Undoes the last launch (0.6): a launch still waiting is cancelled; one that started puts
        /// back, on the launch grid, what its tracks played before, in step as if it had never
        /// stopped (tracks that played nothing stop). Tracks that moved on since are left alone.
        /// Returns what was undone, or null.
        /// </summary>
        internal string UndoLaunch()
        {
            LaunchRecord record;
            lock (gate)
            {
                int quantize = GetQuantizationTicks(launchQuantization);
                if (history.Count == 0) return null;
                record = history[history.Count - 1];
                history.RemoveAt(history.Count - 1);
                lastLaunch = history.Count > 0 ? history[history.Count - 1].Label : null;
                long heard = HeardClock();
                foreach (var (track, clip, before) in record.Entries)
                    scheduler.Revert(track, before, clip, quantize, heard);
                if (record.IsScene)
                {
                    // 0.6.1: only a scene's undo drops its settings (still waiting) and its follow action.
                    sceneAt = -1;
                    sceneFollowPending = null;
                    sceneFollow = null;
                    sceneFollowAt = -1;
                }
            }
            RaiseTrackStateChanged(record.Entries.Select(e => e.Track).ToArray());
            return record.Label;
        }

        public void Stop(ISequence track, bool immediate = false)
        {
            var seq = ToSequence(track);
            lock (gate)
            {
                int quantize = GetQuantizationTicks(launchQuantization);
                scheduler.Stop(seq, quantize, immediate, HeardClock());
            }
            RaiseTrackStateChanged(seq);
        }

        /// <summary>No scene waiting to start, and none following (under the gate).</summary>
        void ClearScene()
        {
            sceneAt = -1;
            sceneGrid = -1;
            sceneRowPending = -1;
            sceneFollowPending = null;
            sceneFollow = null;
            sceneFollowAt = -1;
        }

        public void StopAll(bool immediate = false)
        {
            lock (gate)
            {
                int quantize = GetQuantizationTicks(launchQuantization);
                scheduler.StopAll(quantize, immediate, HeardClock());
                ClearScene();                       // 0.6.1: a queued scene's tempo and follow action go with its clips
            }
            RaiseAllChanged();
        }

        public void BackToArrangement()
        {
            lock (gate)
            {
                scheduler.BackToArrangement();
                ClearScene();
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
                renderedAt = Timestamp();
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
            double heard = TakeTime.Heard(renderedTicks, renderedSamplesPerTick, latency, SamplesSinceRender(rate));
            return Math.Min(c, (long)Math.Floor(heard));
        }

        /// <summary>
        /// Session position the listener hears now, in ticks with the fraction (0.7, recording;
        /// call under the gate): the rendered position less the output latency still to play
        /// (none with compensation off); -1 while stopped.
        /// </summary>
        double HeardTicks()
        {
            long c = scheduler.Clock;
            if (c < 0) return -1;
            if (renderedTicks < 0) return c;
            int rate = ReBuzzCore.masterInfo.SamplesPerSec;
            int latency = LatencyCompensation ? buzz.OutputLatencySamples : 0;
            double heard = TakeTime.Heard(renderedTicks, renderedSamplesPerTick, Math.Max(0, latency), rate > 0 ? SamplesSinceRender(rate) : 0);
            return Math.Min(heard, renderedTicks);
        }

        /// <summary>Clock for latency estimates; tests replace it to stamp at exact moments.</summary>
        internal static Func<long> Timestamp = System.Diagnostics.Stopwatch.GetTimestamp;

        double SamplesSinceRender(int rate) =>
            (Timestamp() - renderedAt) * (double)rate / System.Diagnostics.Stopwatch.Frequency;

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
            int tempo = 0;
            bool beatsChanged = false;
            lock (gate)
            {
                if (transportJustStarted || !scheduler.IsRunning)
                {
                    long before = scheduler.Clock;
                    if (sceneAt >= 0)
                    {
                        long shifted = before < 0
                            ? songPosition                          // queued while stopped: on the first tick
                            : sceneAt + songPosition - before;      // restarted while running: shifts with the clips
                        sceneGrid = before < 0 ? songPosition : sceneGrid + (shifted - sceneAt);
                        sceneAt = shifted;
                    }
                    if (sceneFollowAt >= 0 && before >= 0)
                        sceneFollowAt += songPosition - before;
                    if (before >= 0)
                    {
                        // Restarted while running (0.6.1): what the undo history saw is from before the jump.
                        history.Clear();
                        lastLaunch = null;
                    }
                    scheduler.Start(songPosition);
                }
                else
                {
                    // Scene follow action (0.6): due on the tick about to start, so the scene it
                    // launches lands exactly on it (Live: follow actions aren't quantized).
                    long upcoming = scheduler.Clock + 1;
                    if (sceneFollowAt >= 0 && upcoming >= sceneFollowAt)
                        SceneFollowDue(upcoming);
                    scheduler.Advance();
                }
                tickChanges.AddRange(scheduler.Changes);

                if (sceneAt >= 0 && scheduler.Clock >= sceneAt)
                {
                    // 0.6.1: counted from the grid point the scene is heard on (a late launch
                    // starts its clips in step with it), not from the tick it was applied on.
                    long grid = sceneGrid >= 0 && sceneGrid <= scheduler.Clock ? sceneGrid : scheduler.Clock;
                    tempo = sceneTempo;
                    int beats = sceneBeatsPerBar;
                    sceneAt = -1;
                    sceneGrid = -1;
                    sceneRow = sceneRowPending;
                    sceneFollow = sceneFollowPending;
                    sceneFollowAt = sceneFollow != null ? grid + sceneFollow.TimeTicks : -1;
                    if (beats > 0 && beats != beatsPerBar)
                    {
                        // The bar grid restarts with the new time signature (bars so far are kept for display).
                        long bar = (long)Math.Max(1, buzz.TPB) * beatsPerBar;
                        long bars = Math.Max(0, grid - scheduler.GridOrigin) / bar;
                        System.Threading.Volatile.Write(ref gridOriginBar, gridOriginBar + bars);
                        scheduler.SetGridOrigin(grid);
                        beatsPerBar = beats;                         // with the grid, under the gate (0.6.1)
                        beatsChanged = true;
                    }
                }
            }
            if (tempo > 0)
                buzz.SetBPMFromEngine(tempo);                        // the engine picks it up on the next block
            if (beatsChanged)
                RaisePropertyChangedOnUi(nameof(BeatsPerBar));
            RecordTick(songPosition);
            ResolveGains(songPosition);
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

        // ------------------------------------------------------------------ scene follow actions (0.6)

        /// <summary>A scene as the audio thread launches it: resolved from the launch map, allocation-free to launch.</summary>
        sealed class SceneLaunch
        {
            public SequenceCore[] Tracks;
            public PatternCore[] Clips;
            public LaunchSpec[] Specs;
            public SequenceCore[] Stops;
            public int Tempo;
            public int BeatsPerBar;
            public FollowSpec Follow;
            public bool IsEmpty;
        }

        volatile SceneLaunch[] sceneLaunches = Array.Empty<SceneLaunch>();

        /// <summary>
        /// UI thread (0.6): takes the layout's launch map: each track's column for its clips'
        /// follow actions, and each scene for scene follow actions.
        /// </summary>
        internal void UseLaunchMap(LaunchMap map)
        {
            var columns = new Dictionary<SequenceCore, SessionScheduler<SequenceCore, PatternCore>.Slot[]>();
            for (int t = 0; t < map.Tracks.Length; t++)
            {
                if (map.Tracks[t] is not SequenceCore seq || columns.ContainsKey(seq)) continue;
                var entries = map.Slots[t];
                var column = new SessionScheduler<SequenceCore, PatternCore>.Slot[entries.Length];
                for (int s = 0; s < entries.Length; s++)
                {
                    var e = entries[s];
                    if (e?.Pattern is not PatternCore p || p.Machine != seq.Machine) continue;
                    // Follow actions circumvent global quantization, not a clip's own (Live).
                    var q = e.Options.Quantization;
                    int code = q == null || q == LaunchQuantization.None ? -1 : (int)q.Value;
                    column[s] = new SessionScheduler<SequenceCore, PatternCore>.Slot(p, SpecOf(e.Options, 0, forScene: true), code);
                }
                columns[seq] = column;
            }
            foreach (var seq in buzz.SongCore.SequencesList)
                seq.FollowColumn = columns.TryGetValue(seq, out var c) ? c : null;

            var scenes = new SceneLaunch[map.Scenes.Length];
            for (int s = 0; s < scenes.Length; s++)
            {
                var entry = map.Scenes[s];
                var clips = entry.Clips.Where(c => c.Track is SequenceCore && c.Slot.Pattern is PatternCore).ToArray();
                scenes[s] = new SceneLaunch
                {
                    Tracks = clips.Select(c => (SequenceCore)c.Track).ToArray(),
                    Clips = clips.Select(c => (PatternCore)c.Slot.Pattern).ToArray(),
                    Specs = clips.Select(c => SpecOf(c.Slot.Options, 0, forScene: true)).ToArray(),
                    Stops = (entry.Options.StopTracks ?? Array.Empty<ISequence>()).OfType<SequenceCore>().ToArray(),
                    Tempo = entry.Options.Tempo,
                    BeatsPerBar = entry.Options.BeatsPerBar,
                    Follow = FollowSpecOf(entry.Options.Follow, linkable: false),
                    IsEmpty = entry.IsEmpty
                };
            }
            sceneLaunches = scenes;
        }

        /// <summary>When the last scene's follow action is due (session clock), or -1.</summary>
        internal long SceneFollowAt { get { lock (gate) return sceneFollowAt; } }

        /// <summary>Under the gate, audio thread: the scene follow action is due on <paramref name="at"/>, the tick about to start.</summary>
        void SceneFollowDue(long at)
        {
            var f = sceneFollow;
            sceneFollowAt = -1;
            if (f == null) return;
            if (!scheduler.FollowActionsEnabled)
            {
                sceneFollowAt = at + f.TimeTicks;          // skipped: comes round again
                return;
            }
            var (action, jump) = f.Pick(scheduler.NextRandom(100));
            var scenes = sceneLaunches;
            int current = sceneRow >= 0 && sceneRow < scenes.Length && !scenes[sceneRow].IsEmpty ? sceneRow : -1;
            int target = -1;
            switch (action)
            {
                case FollowAction.None:
                    sceneFollow = null;
                    return;
                case FollowAction.Stop:
                    scheduler.StopAll(0, immediate: true);     // on the next tick: this one
                    sceneFollow = null;
                    return;
                case FollowAction.PlayAgain: target = current; break;
                case FollowAction.Jump: target = jump; break;
                default:
                    if (current < 0) break;
                    int lo = current, hi = current;
                    while (lo > 0 && !scenes[lo - 1].IsEmpty) lo--;
                    while (hi < scenes.Length - 1 && !scenes[hi + 1].IsEmpty) hi++;
                    int count = hi - lo + 1;
                    switch (action)
                    {
                        case FollowAction.Previous: target = current > lo ? current - 1 : hi; break;
                        case FollowAction.Next: target = current < hi ? current + 1 : lo; break;
                        case FollowAction.First: target = lo; break;
                        case FollowAction.Last: target = hi; break;
                        case FollowAction.Any: target = lo + scheduler.NextRandom(count); break;
                        case FollowAction.Other:
                            if (count == 1) { target = current; break; }
                            target = lo + scheduler.NextRandom(count - 1);
                            if (target >= current) target++;
                            break;
                    }
                    break;
            }
            if (target < 0 || target >= scenes.Length || scenes[target].IsEmpty) return;

            var scene = scenes[target];
            for (int i = 0; i < scene.Tracks.Length; i++)
                scheduler.LaunchAt(scene.Tracks[i], scene.Clips[i], at, long.MinValue, scene.Specs[i]);
            for (int i = 0; i < scene.Stops.Length; i++)
                scheduler.StopAt(scene.Stops[i], at);
            sceneAt = at;
            sceneGrid = at;
            sceneTempo = scene.Tempo;
            sceneBeatsPerBar = scene.BeatsPerBar;
            sceneRowPending = target;
            sceneFollowPending = scene.Follow;
        }

        // ------------------------------------------------------------------ crossfader (0.6)

        volatile (MachineCore Machine, int Side)[] crossfade = Array.Empty<(MachineCore, int)>();
        double crossfaderPosition = 0.5;
        int crossfaderCurve;               // 0 dipless, 1 constant power, 2 fast cut

        internal static readonly string[] CrossfaderCurves = { "Dipless", "Constant power", "Fast cut" };

        /// <summary>Crossfader position, 0 (all A) to 1 (all B).</summary>
        internal double CrossfaderPosition => System.Threading.Volatile.Read(ref crossfaderPosition);

        readonly object crossfadeLock = new object();   // 0.6.1: the UI (assignments) and MIDI (position) both set gains

        /// <summary>
        /// UI thread: the song's crossfader assignments and curve, and with
        /// <paramref name="applyPosition"/> (a song opened) its position. Otherwise the position
        /// stays the engine's, which MIDI may have moved since the document last heard of it.
        /// </summary>
        internal void UseCrossfader(SessionDocument doc, SongCore song, bool applyPosition = false)
        {
            var list = new List<(MachineCore, int)>();
            if (doc != null)
                foreach (var m in song.MachinesList)
                    if (doc.Crossfade.TryGetValue(m.Name, out var side))
                        list.Add((m, side == "B" ? 1 : 0));
            lock (crossfadeLock)
            {
                // Machines no longer assigned go back to full volume.
                foreach (var (m, _) in crossfade)
                    if (!list.Any(l => l.Item1 == m))
                        m.CrossfadeGain = 1f;
                crossfade = list.ToArray();
                crossfaderCurve = Math.Max(0, Array.IndexOf(CrossfaderCurves, doc?.CrossfaderCurve ?? "Dipless"));
                ApplyCrossfader(applyPosition ? doc?.CrossfaderPosition ?? 0.5 : CrossfaderPosition);
            }
        }

        /// <summary>Any thread (MIDI too): moves the crossfader; assigned machines change volume on their next block.</summary>
        internal void SetCrossfader(double position)
        {
            lock (crossfadeLock)
                ApplyCrossfader(position);
        }

        void ApplyCrossfader(double position)
        {
            position = Math.Clamp(double.IsNaN(position) ? 0.5 : position, 0, 1);
            System.Threading.Volatile.Write(ref crossfaderPosition, position);
            var (a, b) = CrossfadeGains(position, crossfaderCurve);
            foreach (var (m, side) in crossfade)
                m.CrossfadeGain = (float)(side == 0 ? a : b);
        }

        /// <summary>Gains of sides A and B at a position (0: all A), for a curve: dipless (both full in the middle), constant power, or fast cut.</summary>
        internal static (double A, double B) CrossfadeGains(double x, int curve)
        {
            x = Math.Clamp(x, 0, 1);
            switch (curve)
            {
                case 1: return (Math.Cos(x * Math.PI / 2), Math.Sin(x * Math.PI / 2));
                case 2: return (x < 0.95 ? 1 : (1 - x) / 0.05, x > 0.05 ? 1 : x / 0.05);
                default: return (Math.Min(1, 2 * (1 - x)), Math.Min(1, 2 * x));
            }
        }

        // ------------------------------------------------------------------ tempo nudge (0.6)

        /// <summary>
        /// Nudges the tempo while held, as Live's nudge buttons: -1 slower, +1 faster, 0 back.
        /// Buzz's tempo is a whole number of BPM, so the engine stretches or shortens ticks by
        /// <see cref="NudgeAmount"/> instead; the song's tempo doesn't change.
        /// </summary>
        internal void Nudge(int direction) =>
            buzz.TempoNudge = (float)(1 + NudgeAmount * Math.Sign(direction));

        internal const double NudgeAmount = 0.04;

        long gainStamp;     // audio thread: counts gain sources as they start

        /// <summary>
        /// Velocity (0.5, 0.5.1), audio thread on each tick: a machine plays at the gain of the
        /// most recently started of what plays on its tracks: a session clip (its launch
        /// velocity) or an arrangement event (the velocity it was recorded with). Two tracks of
        /// one machine are one signal, so they can't have two volumes; when the newest stops,
        /// the one still playing sets it again. Allocation-free.
        /// </summary>
        void ResolveGains(int songPosition)
        {
            var sequences = buzz.SongCore.SequencesList;
            for (int i = 0; i < sequences.Count; i++)
            {
                var m = sequences[i].MachineCore;
                if (m == null) continue;
                m.GainBestStamp = -1;
                m.GainBest = 1f;
            }
            for (int i = 0; i < sequences.Count; i++)
            {
                var seq = sequences[i];
                var m = seq.MachineCore;
                if (m == null) continue;

                object source = null;
                long key = 0;
                float gain = 1f;
                var clip = scheduler.GetPlaying(seq);
                if (clip != null)
                {
                    source = clip;
                    key = scheduler.GetLaunch(seq).At;
                    gain = (float)scheduler.GetGain(seq);
                }
                else if (!scheduler.IsOverridden(seq))
                {
                    var arranged = seq.Arranged;
                    int index = arranged.IndexAtOrBefore(songPosition);
                    if (index >= 0)
                    {
                        var e = arranged.Events[index];
                        int time = arranged.Times[index];
                        if (e.Type == SequenceEventType.PlayPattern && e.Pattern != null && songPosition < time + e.Span)
                        {
                            source = e;
                            key = time;
                            gain = (float)Math.Clamp(e.Gain, 0, 1);
                        }
                    }
                }

                if (!ReferenceEquals(source, seq.GainSource) || key != seq.GainSourceKey)
                {
                    seq.GainSource = source;
                    seq.GainSourceKey = key;
                    seq.GainStamp = ++gainStamp;
                }
                if (source != null && seq.GainStamp > m.GainBestStamp)
                {
                    m.GainBestStamp = seq.GainStamp;
                    m.GainBest = gain;
                }
            }
            for (int i = 0; i < sequences.Count; i++)
            {
                var m = sequences[i].MachineCore;
                if (m != null && m.ClipGain != m.GainBest)
                    m.ClipGain = m.GainBest;
            }
        }

        /// <summary>Transport stopped (0.5.1): every machine back to full volume, no sources.</summary>
        void ResetGains()
        {
            var sequences = buzz.SongCore.SequencesList;
            for (int i = 0; i < sequences.Count; i++)
            {
                var seq = sequences[i];
                seq.GainSource = null;
                seq.GainSourceKey = 0;
                if (seq.MachineCore != null)
                    seq.MachineCore.ClipGain = 1f;
            }
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

            // Launches and Repeat restarts on this tick. A late (catch-up) launch is written at its
            // grid point, where it is heard to start. Each start carries the part of the pattern
            // it plays (0.5.1: from a start marker or where legato picked it up, to the clip's
            // end marker) and its velocity, so the arrangement plays back what was heard.
            long clock = scheduler.Clock;
            for (int i = 0; i < tickChanges.Count; i++)
            {
                var c = tickChanges[i];
                if (c.Kind != SessionChangeKind.Launched || c.Current == null) continue;
                var (at, late) = scheduler.GetLaunch(c.Track);
                var (offset, end) = scheduler.GetRegionAt(c.Track, at - late);
                recorder.ClipStarted(c.Track, c.Current, (int)Math.Max(0, songPosition - late), offset, end, (float)scheduler.GetGain(c.Track));
            }

            // Loop wraps (from the loop start); and when recording starts, each playing clip from
            // where it is.
            var sequences = buzz.SongCore.SequencesList;
            for (int i = 0; i < sequences.Count; i++)
            {
                var seq = sequences[i];
                var clip = scheduler.GetPlaying(seq);
                if (clip == null || scheduler.GetLaunch(seq).At == clock) continue;
                long cycle = scheduler.GetCycleElapsed(seq);
                if (cycle == 0 || (starting && cycle > 0))
                {
                    var (offset, end) = scheduler.GetRegionAt(seq, clock);
                    recorder.ClipStarted(seq, clip, songPosition, offset, end, (float)scheduler.GetGain(seq));
                }
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
                sceneFollow = null;
                sceneFollowAt = -1;
                history.Clear();
                lastLaunch = null;
                System.Threading.Volatile.Write(ref gridOriginBar, 0);
            }
            ResetGains();                                           // velocity gain ends with the transport
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
                ForgetHistory(e => e.Track == seq);          // 0.6.1: undo can't bring a removed track back
            }
            if (playing != null)
                Silence(seq);
            // The machine may have no other track to set its gain on the next tick.
            if (seq.MachineCore != null) seq.MachineCore.ClipGain = 1f;
            seq.GainSource = null;
        }

        /// <summary>A pattern was deleted (AudioLock held).</summary>
        internal void OnPatternDeleted(PatternCore pattern)
        {
            var stopped = new List<SequenceCore>();
            lock (gate)
            {
                scheduler.ForgetClip(pattern, stopped);
                // 0.6.1: nor a deleted pattern (launched, or what a launch replaced or cancelled).
                ForgetHistory(e => e.Clip == pattern || scheduler.Refers(e.Before, pattern));
            }
            foreach (var seq in stopped)
                Silence(seq);           // the next tick resolves the machine's gain
            if (stopped.Count > 0)
                RaiseTrackStateChanged(stopped.ToArray());
        }

        /// <summary>A pattern's length changed (any thread: native editors change it from the IPC thread).</summary>
        internal void OnPatternLengthChanged(PatternCore pattern)
        {
            lock (gate)
            {
                scheduler.ClipLengthChanged(pattern);
            }
        }

        /// <summary>New song or song loaded.</summary>
        internal void Reset()
        {
            lock (gate)
            {
                scheduler.Reset();
                history.Clear();
                lastLaunch = null;
                sceneAt = -1;
                sceneFollow = null;
                sceneFollowAt = -1;
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
            // Outside any lock: setting Playing only flags the start for the next tick. Not while
            // a song loads (0.6.1): a pad pressed then would start a half-loaded song.
            if (!buzz.Playing && !buzz.SongLoading)
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
