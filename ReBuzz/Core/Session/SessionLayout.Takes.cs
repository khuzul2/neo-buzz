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
            if (on && sessionRecord && Engine.GetPlayingCore(seq) != null)
                BeginOverdub(seq);
            RecordingChanged?.Invoke();
        }

        // ------------------------------------------------------------------ state

        /// <summary>Session Record (Live's): armed tracks overdub what they play.</summary>
        public bool IsSessionRecording => sessionRecord;

        /// <summary>Whether the track records now (or waits to, a take that hasn't started).</summary>
        public bool IsRecording(ISequence track) => track is SequenceCore seq && recordings.ContainsKey(seq);

        /// <summary>Whether the clip is a take waiting or recording on the track, or the clip the track overdubs.</summary>
        public bool IsRecordingInto(ISequence track, IPattern clip)
        {
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
            if (column != null || !create) return column;
            clip.InsertColumn(clip.Columns.Count, parameter, key.Track);
            var made = clip.Columns.LastOrDefault();
            if (made == null || !ReferenceEquals(made.Parameter, parameter)) return null;
            made.Metadata[PatternColumnCore.SessionAutomationKey] = "1";
            return made;
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
            var machine = seq.Machine;
            string name = machine?.GetNewPatternName();
            if (name == null) return null;
            HookTakes();
            var doc = Ensure();
            int bar = BarTicks;
            int bars = doc.RecordBars;
            int length = (bars > 0 ? bars : SessionDocument.MaxRecordBars) * bar;

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
            r.Window = new SessionCore.RecordWindow(seq, take, length, long.MinValue, long.MaxValue);
            recordings[seq] = r;
            PublishWindows();
            Engine.LaunchTake(seq, take, countIn);
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
                    if (recordings.ContainsKey(seq)) continue;
                    if (Engine.GetPlayingCore(seq) != null)
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
                foreach (var seq in recordings.Keys.ToList())
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
        }

        /// <summary>
        /// Ends the track's recording now. A take recorded until stopped gets its length: the
        /// bars recorded, rounded up, so it plays on looping in step.
        /// </summary>
        public void StopRecording(ISequence track)
        {
            if (track is not SequenceCore seq || !recordings.TryGetValue(seq, out var r)) return;
            DrainInputs();
            long now = Engine.HeardTimeNow();
            if (r.Take != null)
            {
                long start = Engine.StartOf(seq, r.Take);
                if (start >= 0) r.Start = start;
            }
            Finish(r, now < 0 ? long.MaxValue : now);
            Trim(r, now);
        }

        /// <summary>A take recorded until stopped gets the bars it recorded, rounded up (at least one).</summary>
        void Trim(Recording r, long now)
        {
            if (r.Take == null || !r.UntilStopped || r.Take.IsDeleted) return;
            int bar = BarTicks;
            long ticks = r.Start >= 0 && now >= 0 ? Math.Max(1, now / TakeTime.TimeBase - r.Start + 1) : bar;
            long bars = Math.Clamp((ticks + bar - 1) / bar, 1, SessionDocument.MaxRecordBars);
            if (r.Take.Length != bars * bar)
                r.Take.Length = (int)(bars * bar);
        }

        /// <summary>The track's recording is over: held notes end at <paramref name="end"/>, an overdub becomes undo steps.</summary>
        void Finish(Recording r, long end)
        {
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
            // Session Record goes on: an armed track's finished take is overdubbed from here.
            if (sessionRecord && r.Take != null && Engine.IsArmed(r.Track) && ReferenceEquals(Engine.GetPlayingCore(r.Track), r.Take))
                BeginOverdub(r.Track);
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
                    Write(r, input.Clip, new ColumnKey(group, input.Parameter.IndexInGroup, input.ParameterTrack), takeEvents);
                    continue;
                }
                r.WriterFor(input.Clip).Add(input.Stamp, takeEvents);
                if (takeEvents.Count > 0)
                    Write(r, input.Clip, ColumnKey.Midi, takeEvents);
            }
            takeInputs.Clear();
        }

        void Write(Recording r, PatternCore clip, ColumnKey key, List<TakeEvent> events)
        {
            var column = ColumnFor(clip, key, create: true);
            if (column == null) return;
            var batch = events.Select(e => new PatternEvent(e.Time, e.Value, e.Duration)).ToArray();
            column.SetEvents(batch, true);
            if (!ReferenceEquals(clip, r.Take))
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
            if (recordings.Count == 0 || Engine == null) return;
            long now = Engine.HeardTimeNow();
            foreach (var r in recordings.Values.ToList())
            {
                if (r.Track.IsRemoved)
                {
                    Finish(r, long.MaxValue);
                    continue;
                }
                if (r.Take == null)
                {
                    if (!sessionRecord || !Engine.IsArmed(r.Track)) Finish(r, now < 0 ? long.MaxValue : now);
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
                    // Not playing: still waiting to start, or stopped or replaced.
                    if (!Engine.IsPlayingOrQueued(r.Track, r.Take))
                    {
                        Finish(r, now < 0 ? long.MaxValue : now);
                        Trim(r, now);
                    }
                    continue;
                }
                r.Start = start;
                long passEnd = (start + r.FirstPassTicks) * TakeTime.TimeBase;
                if (now >= passEnd)
                {
                    DrainInputs();
                    if (recordings.TryGetValue(r.Track, out var still) && ReferenceEquals(still, r))
                        Finish(r, passEnd);
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
            if (now < 0) return made;
            var doc = Ensure();
            int bar = BarTicks;
            long origin = engine.BarGridOrigin;
            long nowTick = now / TakeTime.TimeBase;
            long earliestBar = origin + Math.Max(0, (nowTick - origin) / bar - (MaxCaptureBars - 1)) * bar;

            UndoStack.BeginActionGroup();
            try
            {
                foreach (var seq in engine.ArmedTracks.ToList())
                {
                    var played = engine.TakeCaptured(seq, earliestBar * TakeTime.TimeBase);
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

                    StopRecording(seq);
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
                        writer.Add(new TakeStamp(data, time, (int)((time - from) % room), (int)room), takeEvents);
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
                RecordingChanged?.Invoke();
            }
            return made;
        }

        /// <summary>New song or song loaded: nothing records, Session Record is off.</summary>
        void ResetTakes()
        {
            recordings.Clear();
            sessionRecord = false;
            Engine?.SetRecordWindows(null);
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
