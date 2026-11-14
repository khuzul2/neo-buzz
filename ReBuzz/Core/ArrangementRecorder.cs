// Fork (docs/specs/daw-modes.md AR5, §4.4, S6): MIDI recorded into the arrangement. An armed track records at the play
// position: a new pattern placed as a clip from where recording began and ending where it stops; over a clip with overdub
// on, into that clip's pattern at pattern time. With loop recording, each pass of the song's loop is a take: its own pattern
// on a take lane (a sequence with the take role, which the engine never plays), to be comped onto the main lane.
//
// Timing is the MIDI thread's: SessionCore stamps each message with when it was heard and where the song was then (the same
// stamps and record-window rule as a session take, with a take target - a pattern to be and the song tick its time zero is -
// instead of a slot); this class, on the UI thread, drains them into TakeWriters and, when recording ends, writes the
// patterns and clips as one step of the song's history. The UI clock is never read for a note's time.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;

namespace ReBuzz.Core
{
    /// <summary>A take lane (a sequence with the take role) added to a machine, on the song's history.</summary>
    [HistoryName("Add take lane")]
    internal sealed class AddTakeLaneAction : IAction
    {
        readonly ReBuzzCore core;
        readonly string machine;
        int index = -1;

        public AddTakeLaneAction(ReBuzzCore core, IMachine machine)
        {
            this.core = core;
            this.machine = machine.Name;
        }

        public ISequence Lane { get; private set; }

        public void Do()
        {
            var m = core.SongCore.MachinesList.FirstOrDefault(x => x.Name == machine);
            if (m == null) return;
            index = core.SongCore.Sequences.Count;
            core.SongCore.AddSequence(m, index);
            Lane = core.SongCore.Sequences[index];
            core.Arrangement?.SetRole(Lane, LaneRole.Take);
        }

        public void Undo()
        {
            if (index < 0 || index >= core.SongCore.Sequences.Count) return;
            core.SongCore.RemoveSequence(core.SongCore.Sequences[index]);
            Lane = null;
        }
    }

    internal sealed class ArrangementRecorder
    {
        sealed class TrackTake
        {
            public ISequence Lane;                       // the main lane
            public IMachine Machine;
            public SessionCore.ArrangementWindow Window;
            public readonly Dictionary<int, TakeWriter> Writers = new();
            public readonly SortedDictionary<int, List<TakeEvent>> Passes = new();
            public long LastTime = -1;

            public List<TakeEvent> EventsOf(int pass)
            {
                if (!Passes.TryGetValue(pass, out var list)) Passes[pass] = list = new List<TakeEvent>();
                return list;
            }

            public void Add(SessionCore.TakeInput input)
            {
                var stamp = input.Stamp;
                LastTime = Math.Max(LastTime, stamp.Time);
                int status = stamp.Data & 0xFF;
                int kind = status & 0xF0;
                bool release = kind == 0x80 || (kind == 0x90 && ((stamp.Data >> 16) & 0x7F) == 0);
                if (release)
                {
                    // The release goes to the pass its press began in.
                    foreach (int pass in Writers.Keys.OrderByDescending(k => k))
                        if (Writers[pass].Holds(stamp.Data))
                        {
                            Writers[pass].Add(stamp, EventsOf(pass));
                            return;
                        }
                    return;
                }
                if (!Writers.TryGetValue(input.Pass, out var writer)) Writers[input.Pass] = writer = new TakeWriter();
                writer.Add(stamp, EventsOf(input.Pass));
            }

            public void Close(long time)
            {
                foreach (var (pass, writer) in Writers)
                    writer.CloseAll(time, EventsOf(pass));
            }
        }

        readonly ReBuzzCore buzz;
        readonly Dictionary<SequenceCore, TrackTake> tracks = new();
        readonly List<SessionCore.TakeInput> inputs = new();
        bool loop;
        bool finishing;
        long lastClock = -1;
        SessionCore listening;
        SongCore listeningSong;

        public ArrangementRecorder(ReBuzzCore buzz)
        {
            this.buzz = buzz;
        }

        /// <summary>Recording is on, or its state changed (headers and buttons redraw).</summary>
        public event Action Changed;

        public bool IsRecording => tracks.Count > 0;

        public bool IsLoopRecording => IsRecording && loop;

        /// <summary>Over a clip, record into its pattern (at pattern time) instead of a new one.</summary>
        public bool Overdub { get; set; }

        /// <summary>What happened last, in words (null when nothing needs saying).</summary>
        public string Status { get; private set; }

        /// <summary>The tracks recording now: their main lane and the song tick their take begins at.</summary>
        public IReadOnlyList<(ISequence Lane, int Origin)> Live => tracks.Values.Select(t => (t.Lane, t.Window.Origin)).ToList();

        // ------------------------------------------------------------------ arming

        ISong Song => buzz.Song;

        /// <summary>The lane a track's recording goes to: the machine's first main lane.</summary>
        public ISequence MainLaneOf(IMachine machine) =>
            Song.Sequences.FirstOrDefault(s => s.Machine == machine && (buzz.Arrangement?.RoleOf(s) ?? LaneRole.Main) == LaneRole.Main);

        public bool IsArmed(IMachine machine) => buzz.SessionCore?.IsArmedMachine(machine) == true;

        /// <summary>Arms or disarms a track (a machine) for recording; it also hears the keyboard then, as a session track does.</summary>
        public void SetArmed(IMachine machine, bool on)
        {
            if (MainLaneOf(machine) is not SequenceCore lane || buzz.SessionCore == null) return;
            buzz.SessionCore.SetArmed(lane, on);
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ start and stop

        /// <summary>
        /// Starts recording on the armed tracks at the play position (the transport starts if it is stopped). With <paramref name="loopRecording"/>
        /// the song's loop repeats and each pass is a take. Null when started, else why not.
        /// </summary>
        public string Start(bool loopRecording)
        {
            if (IsRecording) return "Already recording.";
            var session = buzz.SessionCore;
            if (session == null || buzz.SongLoading) return "The song isn't ready.";
            var armed = session.ArmedTracks.Select(s => s.Machine).Where(m => m != null).Distinct().ToList();
            if (armed.Count == 0) return "Arm a track first.";
            var song = buzz.SongCore;
            int loopStart = song.LoopStart, loopEnd = song.LoopEnd;
            if (loopRecording && loopEnd <= loopStart) return "Set a loop first.";
            if (loopRecording) buzz.Looping = true;
            bool running = session.Clock >= 0;
            int position = song.PlayPosition;
            int offset = running ? session.SongMinusClock : 0;
            int origin = loopRecording ? loopStart : position;
            var windows = new List<SessionCore.ArrangementWindow>();
            foreach (var machine in armed)
            {
                if (MainLaneOf(machine) is not SequenceCore lane) continue;
                var window = new SessionCore.ArrangementWindow(lane, origin, position, offset, loopStart, loopEnd, loopRecording);
                windows.Add(window);
                tracks[lane] = new TrackTake { Lane = lane, Machine = machine, Window = window };
            }
            if (windows.Count == 0) return "The armed tracks have no main lane.";
            loop = loopRecording;
            Status = null;
            lastClock = -1;
            listening = session;
            listeningSong = song;
            session.TakeInputArrived += OnArrived;
            buzz.PropertyChanged += OnBuzzChanged;
            song.PropertyChanged += OnSongChanged;
            session.SetArrangementWindows(windows.ToArray());
            if (!buzz.PlayRequested) buzz.Playing = true;
            Changed?.Invoke();
            return null;
        }

        /// <summary>Stops recording and the transport; writes what was played. Null when done, else why not.</summary>
        public string Stop()
        {
            if (!IsRecording) return "Not recording.";
            long end = buzz.SessionCore.HeardTimeNow();
            Drain();
            if (end < 0) end = lastClock;
            Finish(end);
            if (buzz.Playing) buzz.Playing = false;
            return null;
        }

        /// <summary>A new song or an opened one: whatever was recording is let go, unwritten.</summary>
        public void Reset()
        {
            if (!IsRecording) return;
            Detach();
            tracks.Clear();
            Changed?.Invoke();
        }

        void Detach()
        {
            if (listening != null)
            {
                listening.TakeInputArrived -= OnArrived;
                listening.SetArrangementWindows(Array.Empty<SessionCore.ArrangementWindow>());
            }
            buzz.PropertyChanged -= OnBuzzChanged;
            if (listeningSong != null) listeningSong.PropertyChanged -= OnSongChanged;
            listening = null;
            listeningSong = null;
        }

        void OnArrived()
        {
            if (!IsRecording) return;
            Drain();
        }

        void OnBuzzChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "Playing" || buzz.PlayRequested || !IsRecording || finishing) return;
            // The transport stopped (a stop button, the song's end): the recording is over where it stopped.
            if (buzz.dispatcher.CheckAccess()) FinishStopped();
            else buzz.dispatcher.BeginInvoke(FinishStopped);
        }

        void FinishStopped()
        {
            if (!IsRecording || buzz.PlayRequested) return;
            Drain();
            Finish(lastClock);
        }

        void OnSongChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "PlayPosition" || !IsRecording) return;
            long now = buzz.SessionCore.HeardTimeNow();
            if (now >= 0) lastClock = now;
        }

        void Drain()
        {
            var session = buzz.SessionCore;
            inputs.Clear();
            session.TakeInputs(inputs, true);
            foreach (var input in inputs)
                if (tracks.TryGetValue(input.Track, out var t))
                    t.Add(input);
            inputs.Clear();
        }

        // ------------------------------------------------------------------ writing

        void Finish(long endClock)
        {
            if (finishing || !IsRecording) return;
            finishing = true;
            var done = tracks.Values.ToList();
            Detach();
            tracks.Clear();
            try
            {
                var stack = buzz.SongCore.ActionStack;
                int written = 0;
                stack.BeginActionGroup();
                try
                {
                    stack.Label(loop ? "Record takes" : "Record MIDI");
                    foreach (var t in done)
                    {
                        long end = endClock >= 0 ? endClock : Math.Max(t.LastTime, 0);
                        t.Close(end);
                        t.Window.Locate(end, out int lastPass, out long endSong);
                        written += loop ? WriteTakes(t, lastPass) : WriteClip(t, endSong);
                    }
                }
                finally
                {
                    stack.EndActionGroup();
                }
                if (written > 0) buzz.SetModifiedFlag();
                else Status = "Nothing was played.";
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NullReferenceException || ex is KeyNotFoundException)
            {
                buzz.DCWriteLine("Arrangement recording: " + ex.Message, DCLogLevel.Warning);
                Status = "The recording couldn't be written: " + ex.Message;
            }
            finally
            {
                finishing = false;
                Changed?.Invoke();
            }
        }

        static PatternEvent[] Batch(IEnumerable<TakeEvent> events) =>
            events.OrderBy(e => e.Time).Select(e => new PatternEvent(e.Time, e.Value, e.Duration)).ToArray();

        /// <summary>One pass, or a single recording: a new pattern with the events, and the clip over it. Returns 1 if written.</summary>
        int WriteClip(TrackTake t, long endSong)
        {
            var events = t.Passes.Values.SelectMany(l => l).ToList();
            if (events.Count == 0) return 0;
            var layout = buzz.SessionLayout;
            var editor = new ClipEditor(buzz);
            int origin = t.Window.Origin;
            if (Overdub && OverdubInto(t, editor, events, origin)) return 1;
            int length = Math.Max(1, (int)((endSong - (long)origin * TakeTime.TimeBase + TakeTime.TimeBase - 1) / TakeTime.TimeBase));
            length = Math.Max(length, 1 + events.Max(e => e.Time + Math.Max(e.Duration, 1) - 1) / TakeTime.TimeBase);
            var pattern = NewTake(t.Machine, length, events, layout);
            if (pattern == null) return 0;
            Status = editor.Place(t.Lane, pattern, origin, length);
            return Status == null ? 1 : 0;
        }

        IPattern NewTake(IMachine machine, int length, IReadOnlyList<TakeEvent> events, SessionLayout layout)
        {
            string name = machine.GetNewPatternName();
            if (name == null) return null;
            var stack = buzz.SongCore.ActionStack;
            var create = new SessionCreatePatternAction(layout, machine, name, length, midiColumn: true);
            stack.Do(create);
            var pattern = create.Created;
            if (pattern == null) return null;
            stack.Do(new ClipEventsAction(layout, pattern, ColumnKey.Midi, Batch(events), add: true, done: false));
            return pattern;
        }

        /// <summary>Over a clip at the start, with overdub on: the notes go into that clip's pattern at pattern time. False when no clip is there.</summary>
        bool OverdubInto(TrackTake t, ClipEditor editor, List<TakeEvent> events, int origin)
        {
            var item = editor.Read(t.Lane).FirstOrDefault(i => i.Time <= origin && origin < i.End);
            if (item == null) return false;
            var pattern = item.Pattern;
            var add = new List<TakeEvent>();
            foreach (var e in events)
            {
                long song = (long)origin * TakeTime.TimeBase + e.Time;
                int tick = (int)(song / TakeTime.TimeBase);
                if (item.Spec.PatternPositionAt(tick) is not int inPattern) continue;
                int time = inPattern * TakeTime.TimeBase + (int)(song % TakeTime.TimeBase);
                int room = pattern.Length * TakeTime.TimeBase - time;
                add.Add(new TakeEvent(time, e.Value, e.Duration == 0 ? 0 : Math.Clamp(e.Duration, 1, Math.Max(1, room))));
            }
            if (add.Count == 0) return false;
            buzz.SongCore.ActionStack.Do(new ClipEventsAction(buzz.SessionLayout, pattern, ColumnKey.Midi, Batch(add), add: true, done: false));
            int uses = editor.Uses(pattern);
            Status = uses > 1 ? $"{pattern.Name} is used in {uses} places: the notes went into all of them (Make Unique first to keep the others)." : null;
            return true;
        }

        /// <summary>Loop recording: each pass that was played is a take on a take lane. Returns how many.</summary>
        int WriteTakes(TrackTake t, int lastPass)
        {
            var layout = buzz.SessionLayout;
            var editor = new ClipEditor(buzz);
            int origin = t.Window.Origin;
            int length = Math.Max(1, t.Window.LoopEnd - t.Window.LoopStart);
            int count = 0;
            foreach (var (pass, events) in t.Passes)
            {
                if (events.Count == 0) continue;
                var pattern = NewTake(t.Machine, length, events, layout);
                if (pattern == null) continue;
                var lane = FreeTakeLane(t.Machine, editor, origin, origin + length);
                if (lane == null) continue;
                Status = editor.Place(lane, pattern, origin, length);
                if (Status == null) count++;
            }
            return count;
        }

        ISequence FreeTakeLane(IMachine machine, ClipEditor editor, int from, int to)
        {
            foreach (var lane in Song.Sequences.Where(s => s.Machine == machine && buzz.Arrangement.RoleOf(s) == LaneRole.Take).ToList())
                if (editor.Read(lane).All(i => i.End <= from || i.Time >= to)) return lane;
            var add = new AddTakeLaneAction(buzz, machine);
            buzz.SongCore.ActionStack.Do(add);
            return add.Lane;
        }
    }
}
