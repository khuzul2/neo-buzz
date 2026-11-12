using BuzzGUI.Common;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Session recording, layout side (docs/specs/session-view.md 0.7). Arms tracks, starts
    /// takes and overdubs, drains the engine's stamped input into clips, finishes them.
    ///
    /// Recorded notes go to the clip's engine MIDI column (<see cref="PatternColumnType.MIDI"/>),
    /// which the engine plays itself for any machine that takes MIDI, and which songs save. The
    /// pattern editors keep their own data and don't show it; the clip panel shows the count.
    ///
    /// Undo: a new take is one step (its pattern, made with the column, and its slot; undo
    /// keeps what was recorded for a redo). Each overdub pass is one step per clip it recorded
    /// into, holding the clip itself.
    /// </summary>
    internal sealed partial class SessionLayout
    {
        sealed class Recording
        {
            public SequenceCore Track;
            /// <summary>A new take; null while overdubbing whatever plays.</summary>
            public PatternCore Take;
            /// <summary>A take records its first pass: its length (until stopped: the longest take).</summary>
            public int FirstPassTicks;
            public bool UntilStopped;
            /// <summary>The clock the take counts from, once it has started (-1 before).</summary>
            public long Start = -1;
            /// <summary>Heard time last seen while it recorded (1/240 tick), -1 before.</summary>
            public long LastHeard = -1;
            public SessionCore.RecordWindow Window;
            public readonly Dictionary<PatternCore, TakeWriter> Writers = new Dictionary<PatternCore, TakeWriter>(ReferenceEqualityComparer.Instance);
            /// <summary>Overdub: what this pass wrote into each column of each clip, for its undo step.</summary>
            public readonly Dictionary<(PatternCore Clip, ColumnKey Column), List<PatternEvent>> Written = new Dictionary<(PatternCore, ColumnKey), List<PatternEvent>>();

            public TakeWriter WriterFor(PatternCore clip)
            {
                if (!Writers.TryGetValue(clip, out var w))
                    Writers[clip] = w = new TakeWriter();
                return w;
            }
        }

        readonly Dictionary<SequenceCore, Recording> recordings = new Dictionary<SequenceCore, Recording>(ReferenceEqualityComparer.Instance);
        readonly List<SessionCore.TakeInput> takeInputs = new List<SessionCore.TakeInput>();
        readonly List<TakeEvent> takeEvents = new List<TakeEvent>();
        bool sessionRecord;
        bool takesHooked;

        SessionCore Engine => Session as SessionCore;

        /// <summary>Arm, Session Record or a track's recording started or stopped.</summary>
        public event Action RecordingChanged;

        void HookTakes()
        {
            if (takesHooked || Engine == null) return;
            takesHooked = true;
            Engine.TakeInputArrived += DrainTakes;
        }

        // ------------------------------------------------------------------ arm

        public bool IsArmed(int track) => SequenceOf(track) is SequenceCore seq && Engine?.IsArmed(seq) == true;

        public bool IsArmedTrack(ISequence track) => Engine?.IsArmed(track) == true;

        /// <summary>Arms or disarms a track (not undoable, not saved). Disarming stops its recording.</summary>
        public void SetArmed(int track, bool on)
        {
            if (SequenceOf(track) is not SequenceCore seq || Engine == null) return;
            HookTakes();
            if (!on) StopRecording(seq);
            Engine.SetArmed(seq, on);
            if (on && sessionRecord && Engine.GetPlayingCore(seq) != null && !IsClipPlayer(seq.Machine))
                BeginOverdub(seq);
            RecordingChanged?.Invoke();
        }

        // ------------------------------------------------------------------ state

        /// <summary>Session Record (Live's): armed tracks overdub what they play.</summary>
        public bool IsSessionRecording => sessionRecord;

        /// <summary>Whether the track records now (or waits to, a take that hasn't started).</summary>
        public bool IsRecording(ISequence track) => track is SequenceCore seq && (recordings.ContainsKey(seq) || audioTakes.ContainsKey(seq));

        /// <summary>Whether any track records into the clip (a take of it, or overdubbing it).</summary>
        public bool IsRecordingClip(IPattern clip)
        {
            if (clip == null) return false;
            if (IsAudioTake(clip)) return true;                             // 1.0
            foreach (var (seq, r) in recordings)
                if (r.Take != null ? ReferenceEquals(r.Take, clip) : ReferenceEquals(Engine?.GetPlayingCore(seq), clip)) return true;
            return false;
        }

        /// <summary>Whether the clip is a take waiting or recording on the track, or the clip the track overdubs.</summary>
        public bool IsRecordingInto(ISequence track, IPattern clip)
        {
            if (track is SequenceCore audio && audioTakes.TryGetValue(audio, out var a)) return ReferenceEquals(a.Take, clip);   // 1.0
            if (track is not SequenceCore seq || clip == null || !recordings.TryGetValue(seq, out var r)) return false;
            return r.Take != null ? ReferenceEquals(r.Take, clip) : ReferenceEquals(Engine?.GetPlayingCore(seq), clip);
        }

        /// <summary>Notes recorded into a clip (its engine MIDI columns).</summary>
        public int RecordedNoteCount(IPattern clip) =>
            clip?.Columns.Where(IsMidiColumn).Sum(c => c.GetEvents(0, int.MaxValue).Count(e => (e.Value & 0xF0) == 0x90 && ((e.Value >> 16) & 0x7F) > 0)) ?? 0;

        static bool IsMidiColumn(IPatternColumn c) =>
            c.Type == PatternColumnType.MIDI && (c.Parameter == null || c.Parameter.IndexInGroup < 0);

        static IPatternColumn MidiColumnOf(IPattern clip) =>
            clip?.Columns.FirstOrDefault(c => IsMidiColumn(c) && ReferenceEquals(c.Machine, clip.Machine));

        /// <summary>The clip's MIDI column for its own machine, made if it has none (copy-on-write: safe while it plays).</summary>
        internal static IPatternColumn EnsureMidiColumn(IPattern clip)
        {
            var column = MidiColumnOf(clip);
            if (column != null || clip == null) return column;
            clip.InsertColumn(clip.Columns.Count, (IMachine)null);
            return MidiColumnOf(clip);
        }

        /// <summary>
        /// A column recording writes to: the MIDI column, or an automation column (a parameter
        /// column the engine plays) of a parameter of the clip's machine; made if
        /// <paramref name="create"/>. Null if there is none (or no such parameter).
        /// </summary>
        internal static IPatternColumn ColumnFor(IPattern clip, ColumnKey key, bool create)
        {
            if (clip?.Machine == null) return null;
            if (key.IsMidi) return create ? EnsureMidiColumn(clip) : MidiColumnOf(clip);
            var groups = clip.Machine.ParameterGroups;
            if (key.Group < 0 || key.Group >= groups.Count || key.Index < 0 || key.Index >= groups[key.Group].Parameters.Count) return null;
            var parameter = groups[key.Group].Parameters[key.Index];
            IPatternColumn Find() => clip.Columns.FirstOrDefault(c => c.Type == PatternColumnType.Parameter && ReferenceEquals(c.Parameter, parameter)
                && c.Track == key.Track && c.Metadata.ContainsKey(PatternColumnCore.SessionAutomationKey));
            var column = Find();
            if (column != null || !create || clip is not PatternCore core) return column;
            // Marked before the engine can see it: the audio thread reads the mark (EnginePlays).
            core.InsertColumn(clip.Columns.Count, parameter, key.Track, new Dictionary<string, string> { [PatternColumnCore.SessionAutomationKey] = "1" });
            var made = clip.Columns.LastOrDefault();
            return made != null && ReferenceEquals(made.Parameter, parameter) ? made : null;
        }

        /// <summary>Automation recorded into a clip: parameter events in its automation columns.</summary>
        public int RecordedAutomationCount(IPattern clip) =>
            clip?.Columns.Where(c => c.Type == PatternColumnType.Parameter && c.Metadata.ContainsKey(PatternColumnCore.SessionAutomationKey))
                .Sum(c => c.GetEvents(0, int.MaxValue).Count()) ?? 0;

        // ------------------------------------------------------------------ settings (saved, not undoable)

        public void SetRecordBars(int bars)
        {
            var doc = Ensure();
            bars = Math.Clamp(bars, 0, SessionDocument.MaxRecordBars);
            if (doc.RecordBars == bars) return;
            doc.RecordBars = bars;
            ContentChanged();
        }

        public void SetCountInBars(int bars)
        {
            var doc = Ensure();
            bars = Math.Clamp(bars, 0, SessionDocument.MaxCountInBars);
            if (doc.CountInBars == bars) return;
            doc.CountInBars = bars;
            ContentChanged();
        }

        public void SetMetronome(bool on)
        {
            var doc = Ensure();
            if (doc.Metronome == on) return;
            doc.Metronome = on;
            if (Engine != null) Engine.MetronomeOn = on;
            ContentChanged();
        }

        // ------------------------------------------------------------------ takes

        int BarTicks => Math.Max(1, buzz.TPB) * Math.Max(1, Session.BeatsPerBar);

        /// <summary>A take recorded until stopped starts with room for this many bars, and grows by as many when it nears its end (0.7.1).</summary>
        public const int TakeRoomBars = 64;
        /// <summary>A take recorded until stopped grows while fewer than this many bars are left.</summary>
        const int TakeGrowMarginBars = 8;
        /// <summary>The longest take, in bars.</summary>
        public const int MaxTakeBars = 4096;

        /// <summary>
        /// Records a new take into an empty slot of an armed track: a new pattern there (the
        /// record length, or the longest take while recording until stopped), launched on the
        /// grid, recording its first pass. From a stopped transport the metronome counts in.
        /// Returns the take, or null.
        /// </summary>
        public IPattern RecordTake(int track, int scene)
        {
            if (SequenceOf(track) is not SequenceCore seq || Engine == null || !Engine.IsArmed(seq) || scene < 0) return null;
            if (PatternAt(track, scene) != null) return null;
            if (IsClipPlayer(seq.Machine)) return RecordAudioTake(track, scene, seq);      // 1.0: an audio take
            var machine = seq.Machine;
            string name = machine?.GetNewPatternName();
            if (name == null) return null;
            HookTakes();
            var doc = Ensure();
            int bar = BarTicks;
            int bars = doc.RecordBars;
            int length = (bars > 0 ? bars : TakeRoomBars) * bar;

            StopRecording(seq);
            PatternCore take;
            UndoStack.BeginActionGroup();
            try
            {
                var create = new SessionCreatePatternAction(this, machine, name, length, midiColumn: true);
                UndoStack.Do(create);
                take = create.Created as PatternCore;
                if (take == null) return null;
                Edit(d => d.SetSlot(track, scene, take.Name));
            }
            finally
            {
                UndoStack.EndActionGroup();
            }

            int countIn = 0;
            if (!buzz.PlayRequested && doc.CountInBars > 0)
            {
                // Counted in to a bar line of the song's grid at least that many bars ahead.
                long from = song.PlayPosition;
                long target = (from + (long)doc.CountInBars * bar + bar - 1) / bar * bar;
                countIn = (int)Math.Min(int.MaxValue, target - from);
            }
            var r = new Recording { Track = seq, Take = take, FirstPassTicks = length, UntilStopped = bars == 0 };
            r.Window = new SessionCore.RecordWindow(seq, take, length, long.MinValue, long.MaxValue, thenOverdub: sessionRecord);
            recordings[seq] = r;
            PublishWindows();
            Engine.LaunchTake(seq, take, countIn);
            UpdatePollTimer();
            RecordingChanged?.Invoke();
            return take;
        }

        /// <summary>
        /// Session Record on: armed tracks playing a clip overdub it; armed tracks playing none
        /// record a take into their first empty slot from <paramref name="scene"/> on (a scene is
        /// added if there is none). Off: every recording ends.
        /// </summary>
        public void SetSessionRecord(bool on, int scene = 0)
        {
            if (Engine == null || on == sessionRecord) return;
            HookTakes();
            sessionRecord = on;
            if (on)
            {
                var doc = Ensure();
                foreach (var seq in Engine.ArmedTracks.ToList())
                {
                    if (audioTakes.ContainsKey(seq)) continue;                // 1.0: an audio take records on
                    if (recordings.TryGetValue(seq, out var running))
                    {
                        // A take recording now goes on as an overdub after its first pass.
                        var w = running.Window;
                        if (running.Take != null && !w.ThenOverdub)
                        {
                            running.Window = new SessionCore.RecordWindow(w.Track, w.Take, w.FirstPassTicks, w.From, w.Until, thenOverdub: true);
                            PublishWindows();
                        }
                        continue;
                    }
                    if (Engine.GetPlayingCore(seq) != null && !IsClipPlayer(seq.Machine))   // audio isn't overdubbed: a new take
                    {
                        BeginOverdub(seq);
                        continue;
                    }
                    int track = -1;
                    for (int t = 0; t < doc.Tracks.Count && track < 0; t++)
                        if (ReferenceEquals(SequenceOf(t), seq)) track = t;
                    if (track < 0) continue;
                    int s = Math.Max(0, scene);
                    while (s < doc.Scenes.Count && PatternAt(track, s) != null) s++;
                    if (s >= doc.Scenes.Count) InsertScene(doc.Scenes.Count);
                    RecordTake(track, s);
                }
            }
            else
            {
                foreach (var seq in recordings.Keys.Concat(audioTakes.Keys).ToList())
                    StopRecording(seq);
            }
            RecordingChanged?.Invoke();
        }

        void BeginOverdub(SequenceCore seq)
        {
            long now = Engine.HeardTimeNow();
            var r = new Recording { Track = seq };
            r.Window = new SessionCore.RecordWindow(seq, null, 0, now < 0 ? long.MinValue : now, long.MaxValue);
            recordings[seq] = r;
            PublishWindows();
            UpdatePollTimer();
        }

        /// <summary>
        /// Ends the track's recording now. A take recorded until stopped gets its length: the
        /// bars recorded, rounded up, so it plays on looping in step.
        /// </summary>
        public void StopRecording(ISequence track)
        {
            if (track is SequenceCore audio && audioTakes.ContainsKey(audio))
            {
                StopAudioTake(audio);                                        // 1.0
                return;
            }
            if (track is not SequenceCore seq || !recordings.TryGetValue(seq, out var r)) return;
            DrainInputs();
            if (!recordings.TryGetValue(seq, out var still) || !ReferenceEquals(still, r)) return;
            long end = EndOf(r, Engine.HeardTimeNow());
            Finish(r, end);
            Trim(r, end);
        }

        /// <summary>
        /// When the recording ends (1/240 tick of the session clock): where its take stopped, if
        /// it did (the engine notes the tick); else what is heard now; else the last tick it was
        /// seen playing; long.MaxValue when it never played.
        /// </summary>
        long EndOf(Recording r, long now)
        {
            if (r.Take != null)
            {
                long start = Engine.StartOf(r.Track, r.Take);
                if (start >= 0) r.Start = start;
                if (start < 0 && ReferenceEquals(r.Track.SessionStoppedClip, r.Take))
                {
                    long stopped = System.Threading.Volatile.Read(ref r.Track.SessionStoppedAt);
                    if (r.Start >= 0 && stopped > r.Start) return stopped * TakeTime.TimeBase;
                }
            }
            if (now >= 0) return now;
            return r.LastHeard >= 0 ? r.LastHeard : long.MaxValue;
        }

        /// <summary>
        /// A take recorded until stopped nearing the end of its room grows (0.7.1): longer by
        /// <see cref="TakeRoomBars"/>, so it never wraps onto itself. Lengthening a playing clip
        /// keeps its position; the record window moves its end with it.
        /// </summary>
        void Grow(Recording r, long elapsedTicks)
        {
            int bar = BarTicks;
            if (r.FirstPassTicks - elapsedTicks >= (long)TakeGrowMarginBars * bar || r.FirstPassTicks >= MaxTakeBars * bar) return;
            int grown = (int)Math.Min((long)r.FirstPassTicks + (long)TakeRoomBars * bar, (long)MaxTakeBars * bar);
            r.Take.Length = grown;
            r.FirstPassTicks = grown;
            var w = r.Window;
            r.Window = new SessionCore.RecordWindow(w.Track, w.Take, grown, w.From, w.Until, w.ThenOverdub);
            PublishWindows();
        }

        /// <summary>A take recorded until stopped gets the bars it recorded, rounded up (at least one).</summary>
        void Trim(Recording r, long end)
        {
            if (r.Take == null || !r.UntilStopped || r.Take.IsDeleted) return;
            int bar = BarTicks;
            // A tick heard partly counts; a stop on a tick boundary ends before it.
            long ticks = r.Start >= 0 && end != long.MaxValue
                ? Math.Max(1, (end + TakeTime.TimeBase - 1) / TakeTime.TimeBase - r.Start)
                : bar;
            long bars = Math.Clamp((ticks + bar - 1) / bar, 1, MaxTakeBars);
            if (r.Take.Length != bars * bar)
                r.Take.Length = (int)(bars * bar);
        }

        /// <summary>
        /// The track's recording is over: held notes end at <paramref name="end"/>
        /// (long.MaxValue: dropped), an overdub becomes undo steps. Finishing twice does nothing.
        /// </summary>
        void Finish(Recording r, long end)
        {
            if (!recordings.TryGetValue(r.Track, out var current) || !ReferenceEquals(current, r)) return;   // finished already
            recordings.Remove(r.Track);
            PublishWindows();
            foreach (var (clip, writer) in r.Writers)
            {
                takeEvents.Clear();
                if (end == long.MaxValue) writer.Drop();
                else writer.CloseAll(end, takeEvents);
                if (takeEvents.Count > 0 && !clip.IsDeleted)
                    Write(r, clip, ColumnKey.Midi, takeEvents);
            }
            var passes = r.Written.Where(w => w.Value.Count > 0 && !w.Key.Clip.IsDeleted).ToList();
            if (passes.Count > 0)
            {
                UndoStack.BeginActionGroup();
                try
                {
                    foreach (var (key, events) in passes)
                        UndoStack.Do(new ClipEventsAction(this, key.Clip, key.Column, events.ToArray(), add: true, done: true));
                }
                finally
                {
                    UndoStack.EndActionGroup();
                }
            }
            UpdatePollTimer();
            RecordingChanged?.Invoke();
        }

        void PublishWindows() =>
            Engine?.SetRecordWindows(recordings.Values.Select(r => r.Window).ToArray());

        // ------------------------------------------------------------------ input

        /// <summary>UI thread, posted by the engine: stamped input into the clips.</summary>
        void DrainTakes()
        {
            DrainInputs();
            PollTakes();
        }

        void DrainInputs()
        {
            if (Engine == null) return;
            takeInputs.Clear();
            Engine.TakeInputs(takeInputs);
            foreach (var input in takeInputs)
            {
                if (!recordings.TryGetValue(input.Track, out var r) || input.Clip == null || input.Clip.IsDeleted) continue;
                takeEvents.Clear();
                if (input.Parameter != null)
                {
                    // Automation: the value where it was moved.
                    var machine = input.Clip.Machine;
                    int group = machine?.ParameterGroups.IndexOf(input.Parameter.Group) ?? -1;
                    if (group < 0) continue;
                    takeEvents.Add(new TakeEvent(input.Stamp.Position, input.Stamp.Data, 0));
                    Write(r, input.Clip, new ColumnKey(group, input.Parameter.IndexInGroup, input.ParameterTrack), takeEvents, input.PastFirstPass);
                    continue;
                }
                r.WriterFor(input.Clip).Add(input.Stamp, takeEvents);
                if (takeEvents.Count > 0)
                    Write(r, input.Clip, ColumnKey.Midi, takeEvents, input.PastFirstPass);
            }
            takeInputs.Clear();
        }

        /// <param name="overdub">An overdub of the take (after its first pass): its own undo step.</param>
        void Write(Recording r, PatternCore clip, ColumnKey key, List<TakeEvent> events, bool overdub = false)
        {
            var column = ColumnFor(clip, key, create: true);
            if (column == null) return;
            var batch = events.Select(e => new PatternEvent(e.Time, e.Value, e.Duration)).ToArray();
            column.SetEvents(batch, true);
            if (overdub || !ReferenceEquals(clip, r.Take))
            {
                if (!r.Written.TryGetValue((clip, key), out var list))
                    r.Written[(clip, key)] = list = new List<PatternEvent>();
                list.AddRange(batch);
            }
            buzz.SetModifiedFlag();
        }

        /// <summary>
        /// UI thread (the view's timer, and after each drain): takes whose first pass is over,
        /// and recordings whose clip stopped, went away or lost its track, finish.
        /// </summary>
        public void PollTakes()
        {
            if (!polling) PollAudioTakes();                                  // 1.0
            if (polling || recordings.Count == 0 || Engine == null) return;
            polling = true;
            try
            {
                Poll();
            }
            finally
            {
                polling = false;
            }
            UpdatePollTimer();
        }

        bool polling;

        void Poll()
        {
            long now = Engine.HeardTimeNow();
            foreach (var r in recordings.Values.ToList())
            {
                if (!recordings.TryGetValue(r.Track, out var current) || !ReferenceEquals(current, r)) continue;
                if (now >= 0) r.LastHeard = now;
                if (r.Track.IsRemoved)
                {
                    Finish(r, long.MaxValue);
                    continue;
                }
                if (r.Take == null)
                {
                    if (!sessionRecord || !Engine.IsArmed(r.Track)) Finish(r, EndOf(r, now));
                    continue;
                }
                if (r.Take.IsDeleted)
                {
                    Finish(r, long.MaxValue);
                    continue;
                }
                long start = Engine.StartOf(r.Track, r.Take);
                if (start < 0)
                {
                    // Not playing: still waiting to start, or stopped (the transport too) or replaced.
                    if (!Engine.IsPlayingOrQueued(r.Track, r.Take))
                    {
                        long end = EndOf(r, now);
                        Finish(r, end);
                        Trim(r, end);
                    }
                    continue;
                }
                r.Start = start;
                if (r.UntilStopped && now >= 0)
                    Grow(r, now / TakeTime.TimeBase - start);
                long passEnd = (start + r.FirstPassTicks) * TakeTime.TimeBase;
                if (now >= passEnd)
                {
                    DrainInputs();
                    if (!recordings.TryGetValue(r.Track, out var still) || !ReferenceEquals(still, r)) continue;
                    if (r.Window.ThenOverdub && sessionRecord && Engine.IsArmed(r.Track))
                    {
                        // Session Record: the take goes on as an overdub of whatever the track
                        // plays (its window took everything since its first pass ended).
                        r.Take = null;
                        r.UntilStopped = false;
                        r.Window = new SessionCore.RecordWindow(r.Track, null, 0, passEnd, long.MaxValue);
                        PublishWindows();
                        RecordingChanged?.Invoke();
                    }
                    else
                    {
                        Finish(r, passEnd);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ Capture MIDI

        /// <summary>The longest phrase Capture MIDI keeps, in bars.</summary>
        public const int MaxCaptureBars = 8;

        /// <summary>
        /// Capture MIDI (Live's): what each armed track was played lately, recording or not,
        /// becomes a clip in its first empty slot from <paramref name="scene"/>: from the bar
        /// line before its first note, 1, 2, 4 or 8 bars long (the last 8 bars at most), and plays
        /// on at once, in phase with what was played. Needs the transport running. One undo step
        /// for all of it. Returns the clips made.
        /// </summary>
        public IReadOnlyList<IPattern> CaptureMidi(int scene = 0)
        {
            var made = new List<IPattern>();
            var engine = Engine;
            if (engine == null) return made;
            long now = engine.HeardTimeNow();
            if (now < 0) return CaptureWhileStopped(scene);
            var doc = Ensure();
            int bar = BarTicks;
            long origin = engine.BarGridOrigin;
            long nowTick = now / TakeTime.TimeBase;
            long earliestBar = origin + Math.Max(0, (nowTick - origin) / bar - (MaxCaptureBars - 1)) * bar;
            var armed = engine.ArmedTracks.ToList();
            // Recordings on these tracks end first: their undo steps stay their own.
            foreach (var seq in armed)
                StopRecording(seq);

            UndoStack.BeginActionGroup();
            try
            {
                foreach (var seq in armed)
                {
                    var played = engine.TakeCaptured(seq, earliestBar * TakeTime.TimeBase);
                    played.RemoveAll(m => m.Time > now);              // a clock that jumped back meanwhile
                    int first = played.FindIndex(m => (m.Data & 0xF0) == 0x90 && ((m.Data >> 16) & 0x7F) > 0);
                    if (first < 0) continue;
                    long firstTick = played[first].Time / TakeTime.TimeBase;
                    long startBar = origin + Math.Max(0, (firstTick - origin) / bar) * bar;
                    long span = nowTick - startBar + 1;
                    int bars = 1;
                    while (bars < MaxCaptureBars && (long)bars * bar < span) bars *= 2;
                    int length = bars * bar;

                    int track = -1;
                    for (int t = 0; t < doc.Tracks.Count && track < 0; t++)
                        if (ReferenceEquals(SequenceOf(t), seq)) track = t;
                    if (track < 0 || seq.Machine?.GetNewPatternName() is not string name) continue;
                    int s = Math.Max(0, scene);
                    while (s < doc.Scenes.Count && PatternAt(track, s) != null) s++;
                    if (s >= doc.Scenes.Count) InsertScene(doc.Scenes.Count);

                    var create = new SessionCreatePatternAction(this, seq.Machine, name, length, midiColumn: true);
                    UndoStack.Do(create);
                    if (create.Created is not PatternCore clip) continue;
                    int slot = s;
                    Edit(d => d.SetSlot(track, slot, clip.Name));

                    // The phrase, from the bar line, wrapped into the clip's length.
                    var writer = new TakeWriter();
                    takeEvents.Clear();
                    long from = startBar * TakeTime.TimeBase;
                    long room = (long)length * TakeTime.TimeBase;
                    foreach (var (time, data) in played.Skip(first))
                    {
                        long at = Math.Clamp(time - from, 0, room - 1);
                        writer.Add(new TakeStamp(data, time, (int)at, (int)room), takeEvents);
                    }
                    writer.CloseAll(now, takeEvents);
                    var column = EnsureMidiColumn(clip);
                    column?.SetEvents(takeEvents.Select(e => new PatternEvent(e.Time, e.Value, e.Duration)).ToArray(), true);
                    takeEvents.Clear();

                    engine.LaunchInPhase(seq, clip, startBar);
                    made.Add(clip);
                }
            }
            finally
            {
                UndoStack.EndActionGroup();
            }
            if (made.Count > 0)
            {
                buzz.SetModifiedFlag();
                // Session Record goes on, into the captured clips.
                if (sessionRecord)
                    foreach (var seq in armed)
                        if (!recordings.ContainsKey(seq)) BeginOverdub(seq);
                RecordingChanged?.Invoke();
            }
            return made;
        }

        /// <summary>
        /// Capture MIDI with the transport stopped (0.7.1, Live's): each armed track's phrase,
        /// timed by when it was played at the current tempo, becomes a clip from its first note,
        /// 1, 2, 4 or 8 bars long (later notes are left out). It doesn't start playing.
        /// </summary>
        IReadOnlyList<IPattern> CaptureWhileStopped(int scene)
        {
            var made = new List<IPattern>();
            var engine = Engine;
            var doc = Ensure();
            int bar = BarTicks;
            double ticksPerSecond = Math.Max(1, buzz.BPM) * Math.Max(1, buzz.TPB) / 60.0;
            double frequency = System.Diagnostics.Stopwatch.Frequency;

            UndoStack.BeginActionGroup();
            try
            {
                foreach (var seq in engine.ArmedTracks.ToList())
                {
                    var played = engine.TakeCapturedWhileStopped(seq);
                    int first = PhraseStart(played, bar * frequency / ticksPerSecond, MaxCaptureBars * bar * frequency / ticksPerSecond);
                    if (first < 0) continue;
                    long t0 = played[first].Wall;
                    long TimeOf(long wall) => (long)Math.Round((wall - t0) / frequency * ticksPerSecond * TakeTime.TimeBase);
                    long last = TimeOf(played[^1].Wall);
                    long span = last / TakeTime.TimeBase + 1;
                    int bars = 1;
                    while (bars < MaxCaptureBars && (long)bars * bar < span) bars *= 2;
                    int length = bars * bar;
                    long room = (long)length * TakeTime.TimeBase;

                    int track = -1;
                    for (int t = 0; t < doc.Tracks.Count && track < 0; t++)
                        if (ReferenceEquals(SequenceOf(t), seq)) track = t;
                    if (track < 0 || seq.Machine?.GetNewPatternName() is not string name) continue;
                    int s = Math.Max(0, scene);
                    while (s < doc.Scenes.Count && PatternAt(track, s) != null) s++;
                    if (s >= doc.Scenes.Count) InsertScene(doc.Scenes.Count);

                    var create = new SessionCreatePatternAction(this, seq.Machine, name, length, midiColumn: true);
                    UndoStack.Do(create);
                    if (create.Created is not PatternCore clip) continue;
                    int slot = s;
                    Edit(d => d.SetSlot(track, slot, clip.Name));

                    var writer = new TakeWriter();
                    takeEvents.Clear();
                    foreach (var (wall, data) in played.Skip(first))
                    {
                        long time = TimeOf(wall);
                        bool release = (data & 0xF0) == 0x80 || ((data & 0xF0) == 0x90 && ((data >> 16) & 0x7F) == 0);
                        if (time >= room && !release) continue;          // past the clip: left out (a release ends its note)
                        writer.Add(new TakeStamp(data, time, (int)Math.Clamp(time, 0, room - 1), (int)room), takeEvents);
                    }
                    writer.CloseAll(Math.Min(last, room), takeEvents);
                    EnsureMidiColumn(clip)?.SetEvents(takeEvents.Select(e => new PatternEvent(e.Time, e.Value, e.Duration)).ToArray(), true);
                    takeEvents.Clear();
                    made.Add(clip);
                }
            }
            finally
            {
                UndoStack.EndActionGroup();
            }
            if (made.Count > 0)
            {
                buzz.SetModifiedFlag();
                RecordingChanged?.Invoke();
            }
            return made;
        }

        static bool IsNoteOn(int data) => (data & 0xF0) == 0x90 && ((data >> 16) & 0x7F) > 0;

        static bool IsRelease(int data) => (data & 0xF0) == 0x80 || ((data & 0xF0) == 0x90 && ((data >> 16) & 0x7F) == 0);

        /// <summary>
        /// Where the phrase played while stopped starts (0.7.1 hardening): the first note after
        /// the last silence of a bar or more (no note held, none played), and no more than the
        /// longest capture before the last thing played. -1 if no note was played.
        /// </summary>
        internal static int PhraseStart(IReadOnlyList<(long Wall, int Data)> played, double barWall, double maxWall)
        {
            if (played.Count == 0) return -1;
            long end = played[played.Count - 1].Wall;
            int start = -1, held = 0;
            long lastNoteEvent = long.MinValue;
            for (int i = 0; i < played.Count; i++)
            {
                var (wall, data) = played[i];
                if (IsNoteOn(data))
                {
                    bool afterSilence = held == 0 && (lastNoteEvent == long.MinValue || wall - lastNoteEvent >= barWall);
                    if (start < 0 || afterSilence) start = i;
                    held++;
                    lastNoteEvent = wall;
                }
                else if (IsRelease(data))
                {
                    held = Math.Max(0, held - 1);
                    lastNoteEvent = wall;
                }
            }
            if (start < 0) return -1;
            // Too long ago: the first note within the longest capture.
            while (start < played.Count && (end - played[start].Wall > maxWall || !IsNoteOn(played[start].Data))) start++;
            return start < played.Count ? start : -1;
        }

        /// <summary>New song or song loaded: nothing records, Session Record is off.</summary>
        void ResetTakes()
        {
            recordings.Clear();
            ResetAudioTakes();                                              // 1.0
            sessionRecord = false;
            Engine?.SetRecordWindows(null);
            UpdatePollTimer();
        }

        IUiTimer pollTimer;

        /// <summary>
        /// Polls takes while anything records, whether a Session View shows or not (its timer
        /// polls too): a first pass ends, a take stops. The timer is made on the UI thread (in
        /// tests the fake dispatcher's timer never ticks: they poll themselves).
        /// </summary>
        void UpdatePollTimer()
        {
            bool needed = recordings.Count > 0 || audioTakes.Count > 0;
            if (pollTimer == null)
            {
                if (!needed) return;
                if (!dispatcher.CheckAccess()) return;
                pollTimer = dispatcher.CreateTimer(TimeSpan.FromMilliseconds(40), PollTakes);
            }
            if (needed && !pollTimer.IsEnabled) pollTimer.Start();
            else if (!needed && pollTimer.IsEnabled) pollTimer.Stop();
        }

        /// <summary>Removes the automation recorded into a clip (undoable).</summary>
        public void ClearRecordedAutomation(IPattern clip)
        {
            var machine = clip?.Machine;
            if (machine == null) return;
            var columns = clip.Columns.Where(c => c.Type == PatternColumnType.Parameter && c.Parameter != null
                && c.Metadata.ContainsKey(PatternColumnCore.SessionAutomationKey)).ToList();
            if (columns.Count == 0) return;
            UndoStack.BeginActionGroup();
            try
            {
                foreach (var c in columns)
                {
                    var events = c.GetEvents(int.MinValue, int.MaxValue).ToArray();
                    int group = machine.ParameterGroups.IndexOf(c.Parameter.Group);
                    if (events.Length > 0 && group >= 0)
                        UndoStack.Do(new ClipEventsAction(this, clip, new ColumnKey(group, c.Parameter.IndexInGroup, c.Track), events, add: false, done: false));
                }
            }
            finally
            {
                UndoStack.EndActionGroup();
            }
            buzz.SetModifiedFlag();
            RaiseChanged();
        }

        /// <summary>Removes the notes recorded into a clip (undoable).</summary>
        public void ClearRecordedNotes(IPattern clip)
        {
            var column = MidiColumnOf(clip);
            var events = column?.GetEvents(int.MinValue, int.MaxValue).ToArray();
            if (events == null || events.Length == 0) return;
            UndoStack.Do(new ClipEventsAction(this, clip, ColumnKey.Midi, events, add: false, done: false));
            buzz.SetModifiedFlag();
            RaiseChanged();
        }
    }

    /// <summary>
    /// Adds events to a clip's engine MIDI column, or removes them (0.7): an overdub pass, or
    /// clearing recorded notes. Holds the clip itself (<see cref="SessionLayout.Current{T}"/>
    /// follows one an undo made again). <c>done</c>: already applied (an overdub, written as
    /// it was played), so the first Do does nothing.
    /// </summary>
    [HistoryName("Edit clip events")]
    internal sealed class ClipEventsAction : IAction
    {
        readonly SessionLayout layout;
        readonly IPattern clip;
        readonly ColumnKey column;
        readonly PatternEvent[] events;
        readonly bool add;
        bool skip;

        public ClipEventsAction(SessionLayout layout, IPattern clip, ColumnKey column, PatternEvent[] events, bool add, bool done)
        {
            this.layout = layout;
            this.clip = clip;
            this.column = column;
            this.events = events;
            this.add = add;
            skip = done;
        }

        public void Do()
        {
            if (skip) { skip = false; return; }
            Apply(add);
        }

        public void Undo() => Apply(!add);

        void Apply(bool set)
        {
            var p = layout.Current(clip);
            if (p == null || (p is PatternCore core && core.IsDeleted)) return;
            SessionLayout.ColumnFor(p, column, create: set)?.SetEvents(events, set);
        }
    }

    /// <summary>
    /// A column recording writes to (0.7): the clip's MIDI column, or the automation column of a
    /// parameter (group and index in the clip's machine, and its track). By position, not by
    /// object, so it holds for a clip or machine made again by an undo.
    /// </summary>
    internal readonly record struct ColumnKey(int Group, int Index, int Track)
    {
        public static readonly ColumnKey Midi = new ColumnKey(-1, -1, 0);
        public bool IsMidi => Group < 0;
    }
}
