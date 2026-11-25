using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WDE.ModernPatternEditor.MPEStructures
{
    public class MPEClipColumn
    {
        public static readonly int BUZZ_TICKS_PER_BEAT = 4;

        public MPEClip MPEClip { get; }
        public Action<IEnumerable<ClipEvent>, bool> EventsChanged { get; internal set; }
        public IMachine Machine { get; internal set; }

        // public int IndexInGroup { get; internal set; }
        public int ParamTrack { get; internal set; }
        public bool Graphical { get; internal set; }
        //public string MachineName { get; internal set; }

        List<ClipEvent> eventList = new List<ClipEvent>();

        // This is only for loading/saving pxp format
        // public int ParamIndex { get; internal set; }
        public List<int> BeatRowsList { get; private set; }
        public ParameterGroupType GroupType { get; internal set; }
        public IParameter Parameter { get; internal set; }
        public object VisualIndexInGroup
        {
            get
            {
                if (Parameter is MPEInternalParameter)
                {
                    return ClipEditorUtils.LastMidiTrackParameter - Parameter.IndexInGroup - 1;
                }
                else
                {
                    return Parameter.IndexInGroup;
                }
            }
        }

        public MPEClipColumn(MPEClip pat)
        {
            this.MPEClip = pat;
            BeatRowsList = new List<int>();
        }

        List<ClipEvent> clipEventsList = new List<ClipEvent>(0);
        ClipEvent[] emptyClipEventList = Array.Empty<ClipEvent>();
        Lock clipEventsLock = new Lock();

        // ---- Fork (docs/specs/clips.md §6, CU3): the seam. Where the song's clips are lanes in the pattern's columns (the Studio), this column is a view
        // of one lane, the unmarked column of the pattern for its parameter and track: it reads and writes the lane's events, and keeps none of its
        // own. The lane is made on the first write (a template's thousand empty columns are not in the song). Classic keeps its list.

        /// <summary>The pattern editor views the store for this column: its parameter is a machine's (not one of the editor's own lanes).</summary>
        internal bool Bound => MPEClip?.Editor != null && MPEClip.Editor.StoreAttached && Parameter != null && Parameter is not MPEInternalParameter;

        IClipColumn store;
        Action<IEnumerable<ClipEvent>, bool> storeChanged;

        /// <summary>The store's lane of this column (the pattern's unmarked parameter column for the parameter and track), made when asked to.</summary>
        internal IClipColumn StoreLane(bool create)
        {
            var clip = MPEClip.Clip;
            if (clip == null) return null;
            var s = store;
            if (s != null && ReferenceEquals(s.Clip, clip) && clip.Columns.Contains(s)) return s;
            ReleaseStore();
            foreach (var c in clip.Columns)
                if (c.Type == ClipColumnType.Parameter && ReferenceEquals(c.Parameter, Parameter) && c.Track == ParamTrack && !c.Metadata.ContainsKey(ClipMeta.SessionAutomation))
                {
                    Attach(c);
                    return c;
                }
            if (!create) return null;
            clip.InsertColumn(clip.Columns.Count, Parameter, ParamTrack);
            foreach (var c in clip.Columns.Reverse())
                if (c.Type == ClipColumnType.Parameter && ReferenceEquals(c.Parameter, Parameter) && c.Track == ParamTrack && !c.Metadata.ContainsKey(ClipMeta.SessionAutomation))
                {
                    SyncGrid(c);
                    Attach(c);
                    return c;
                }
            return null;
        }

        void Attach(IClipColumn c)
        {
            store = c;
            // What others write into the lane (a take, the agent, an envelope, an undo) reaches the editor's view.
            storeChanged = (events, set) => EventsChanged?.Invoke(events, set);
            c.EventsChanged += storeChanged;
        }

        void ReleaseStore()
        {
            if (store != null && storeChanged != null) store.EventsChanged -= storeChanged;
            store = null;
            storeChanged = null;
        }

        /// <summary>The lane's Grid metadata says this column's rows per beat (view state kept with the lane).</summary>
        void SyncGrid(IClipColumn c)
        {
            if (BeatRowsList.Count == 0) return;
            string grid = ClipGrid.Format(MPEClip.RowsPerBeat > 0 ? MPEClip.RowsPerBeat : BUZZ_TICKS_PER_BEAT, BeatRowsList);
            if (!c.Metadata.TryGetValue(ClipMeta.Grid, out var have) || have != grid) c.Metadata[ClipMeta.Grid] = grid;
        }

        /// <summary>The editor starts viewing the store: what it held of its own is in the store already (the migration), so it is let go.</summary>
        internal void LetGoOfOwnEvents()
        {
            lock (clipEventsLock) eventList.Clear();
        }

        /// <summary>Fork (clips.md §6): ask for the store's lane without making one, to read the lane's Grid and Mute into this column.</summary>
        internal void TakeViewFromStore()
        {
            var lane = StoreLane(false);
            if (lane == null) return;
            if (lane.Metadata.TryGetValue(ClipMeta.Grid, out var grid) && ClipGrid.TryParse(grid, out int rpb, out _))
            {
                int beats = Math.Max(1, (MPEClip.Clip.Length + BUZZ_TICKS_PER_BEAT - 1) / BUZZ_TICKS_PER_BEAT);
                BeatRowsList = ClipGrid.BeatRows(grid, beats, rpb);
            }
        }

        /// <summary>Fork (ux-overhaul.md P3c): whether the column holds any event (the editor hides columns that hold none).</summary>
        internal bool HasEvents
        {
            get
            {
                if (Bound) { var lane = StoreLane(false); return lane != null && lane.GetEvents(int.MinValue, int.MaxValue).Any(); }
                lock (clipEventsLock) return eventList.Count > 0;
            }
        }

        public IEnumerable<ClipEvent> GetEvents(int start, int end)
        {
            if (Bound)
                return StoreLane(false)?.GetEvents(start, end) ?? emptyClipEventList;
            if (eventList.Count == 0)
                return emptyClipEventList;

            clipEventsList.Clear();
            lock (clipEventsLock)
            {
                for (int i = 0; i < eventList.Count; i++)
                {
                    var pe = eventList[i];

                    if (pe.Time >= start && pe.Time < end)
                        clipEventsList.Add(pe);
                }
            }
            return clipEventsList.ToArray();
        }

        /// <summary>
        /// Fork (neo-buzz audio hygiene): the events in [<paramref name="start"/>, <paramref name="end"/>)
        /// into <paramref name="into"/> (cleared first), which it returns: GetEvents without its array,
        /// for playback on the audio thread.
        /// </summary>
        internal List<ClipEvent> GetEventsInto(int start, int end, List<ClipEvent> into)
        {
            into.Clear();
            if (Bound)
            {
                var lane = StoreLane(false);
                if (lane != null) into.AddRange(lane.GetEvents(start, end));
                return into;
            }
            lock (clipEventsLock)
            {
                for (int i = 0; i < eventList.Count; i++)
                {
                    var pe = eventList[i];
                    if (pe.Time >= start && pe.Time < end)
                        into.Add(pe);
                }
            }
            return into;
        }

        internal void SetEvents(ClipEvent[] events, bool set, bool play = true)
        {
            int clipLenght = int.MaxValue;
            var clip = MPEClip.Clip;
            if (clip != null)
                clipLenght = clip.Length * ClipEvent.TimeBase;

            bool bound = Bound;
            lock (clipEventsLock)
            {
                // Paste might introduce wrong numbers for notes, so check.
                if (Parameter.Type == ParameterType.Note)
                {
                    for (int i = 0; i < events.Length; i++)
                    {
                        int value = events[i].Value;
                        if (value != BuzzNote.Off)
                        {
                            int o = value >> 4;
                            if ((value & 15) < 1 || (value & 15) > 12 || o < BuzzNote.MinOctave || o > BuzzNote.MaxOctave)
                            {
                                // convert from midinote
                                value = Math.Max(0, Math.Min(value, 119));
                                value = BuzzNote.FromMIDINote(value);
                            }
                        }
                        events[i].Value = value;
                    }
                }

                if (bound)
                {
                    // The lane takes the events; its own EventsChanged tells the view (and everyone else watching the lane).
                    if (events == null || events.Length == 0) return;
                    var lane = StoreLane(set);
                    if (lane == null) return;
                    var changing = set ? events.Where(e => e.Time < clipLenght).ToArray() : events;
                    if (changing.Length > 0) lane.SetEvents(changing, set);
                    return;
                }
                if (!set)
                {
                    foreach (var e in events)
                        eventList.Remove(e);
                }
                else
                {
                    foreach (var e in events)
                    {
                        if (e.Time < clipLenght)
                            eventList.Add(e);
                    }
                }
            }

            EventsChanged?.Invoke(events, set);
        }

        internal void SetEvents(ClipEvent[] events, int timeOffset, bool set)
        {
            for (int i = 0; i < events.Length; i++)
            {
                events[i].Time += timeOffset;
            }

            SetEvents(events, set);
        }

        int BuzzTicksToBeats(int x)
        {
            return (x + BUZZ_TICKS_PER_BEAT - 1) / BUZZ_TICKS_PER_BEAT;
        }

        internal void UpdateLength()
        {
            int newNumbBeats = BuzzTicksToBeats(MPEClip.Clip.Length);
            List<int> newBeatRows = new List<int>();

            for (int i = 0; i < newNumbBeats; i++)
            {
                if (i < BeatRowsList.Count)
                {
                    newBeatRows.Add(BeatRowsList[i]);
                }
                else
                    newBeatRows.Add(MPEClip.RowsPerBeat);
            }

            BeatRowsList = newBeatRows;
        }


        internal void SetRPB(int rpb)
        {
            if (BeatRowsList.Count == 0)
            {
                for (int i = 0; i < BuzzTicksToBeats(MPEClip.Clip.Length); i++)
                    BeatRowsList.Add(rpb);
            }
            else
            {
                for (int i = 0; i < BeatRowsList.Count; i++)
                {
                    BeatRowsList[i] = rpb;
                }
            }
        }

        internal void ClearRegion(int regionLenght, int offsetFromTop, Digit topLeftDigit)
        {
            if (Bound)
            {
                var gone = GetEvents(offsetFromTop, offsetFromTop + regionLenght).ToArray();
                if (gone.Length > 0) StoreLane(false)?.SetEvents(gone, false);
            }
            else
            for (int i = 0; i < eventList.Count; i++)
            {
                ClipEvent e = eventList[i];
                if (e.Time >= offsetFromTop && e.Time < offsetFromTop + regionLenght)
                {
                    eventList.RemoveAt(i);
                    i--;
                }
            }

            // Need to refresh editor view
            //topLeftDigit.PatternVM.Editor.InvalidateVisual();
            HashSet<int> beats = new HashSet<int>();
            Digit iterator = topLeftDigit;
            while (true)
            {
                beats.Add(iterator.Beat);
                if (iterator.ParameterColumn.GetDigitTime(iterator) >= offsetFromTop + regionLenght ||
                    iterator.IsLastBeat)
                    break;
                iterator = iterator.NextBeat;
            }
            topLeftDigit.ParameterColumn.Set.FireBeatsInvalidated(beats);
        }

        internal void Quantize(Digit startBeat, Digit endBeat)
        {
            HashSet<int> beats = new HashSet<int>();
            Digit iterator = startBeat.FirstRowInBeat;
            Digit iteratorEnd = endBeat.FirstRowInBeat;
            while (true)
            {
                beats.Add(iterator.Beat);
                int start = startBeat.ParameterColumn.GetDigitTime(iterator);
                
                int beatTime = ClipControl.BUZZ_TICKS_PER_BEAT * ClipEvent.TimeBase;
                int end = start + beatTime;
                var events = GetEvents(start, end);

                Dictionary<int, ClipEvent> cleanedEvents = new Dictionary<int, ClipEvent>();

                for (int i = 0; i < events.Count(); i++)
                {
                    var e = events.ElementAt(i);
                    Digit nearestRow = startBeat.NearestRow(e.Time - start);
                    double rowTime = beatTime / (float)startBeat.ClipVM.GetBeat(startBeat).Rows.Count;
                    e.Time = (int)(nearestRow.RowInBeat * rowTime + start);
                    cleanedEvents[e.Time] = e;
                }

                this.ClearRegion(end - start, start, iterator);
                this.SetEvents(cleanedEvents.Values.ToArray(), true);

                if (iterator.TimeInBeat >= iteratorEnd.TimeInBeat)
                    break;

                iterator = iterator.NextBeat;
            }
            startBeat.ParameterColumn.Set.FireBeatsInvalidated(beats);
        }

        public int GetTimeQuantized(int time)
        {
            //int beats = BuzzTicksToBeats(MPEPattern.Pattern.Length);
            int beatTime = ClipControl.BUZZ_TICKS_PER_BEAT * ClipEvent.TimeBase;
            int beat = time / beatTime;
            int beatRows = beat < BeatRowsList.Count ? BeatRowsList[beat] : BeatRowsList[BeatRowsList.Count - 1];
            double rowTime = beatTime / (double)beatRows;
            int row = (int)((time - beat * beatTime) / rowTime);

            return (int)(beat * beatTime + row * rowTime);
        }

        public int GetTimeQuantized(int time, int rpb)
        {
            //int beats = BuzzTicksToBeats(MPEPattern.Pattern.Length);
            int beatTime = ClipControl.BUZZ_TICKS_PER_BEAT * ClipEvent.TimeBase;
            int beat = time / beatTime;
            double rowTime = beatTime / rpb;
            int row = (int)((time - beat * beatTime) / rowTime);

            return (int)(beat * beatTime + row * rowTime);
        }

        internal void SetBeats(List<int> beatRows)
        {
            this.BeatRowsList = beatRows;
        }

        internal double GetRelativeTimeInRow(int time)
        {
            //int beats = MPEPattern.Pattern.Length / MPEPattern.RowsPerBeat;
            int beatTime = ClipControl.BUZZ_TICKS_PER_BEAT * ClipEvent.TimeBase;
            int beat = time / beatTime;
            double rowTime = beatTime / BeatRowsList[beat];
            int row = (int)((time - beat * beatTime) / rowTime);

            return (time - beat * beatTime) / rowTime - row;
        }

        internal void SetBeatCount(int beats)
        {
            int newNumbBeats = beats;
            List<int> newBeatRows = new List<int>();

            for (int i = 0; i < newNumbBeats; i++)
            {
                if (i < BeatRowsList.Count)
                {
                    newBeatRows.Add(BeatRowsList[i]);
                }
                else
                    newBeatRows.Add(MPEClip.RowsPerBeat);
            }

            BeatRowsList = newBeatRows;
        }

        internal MPEClipColumn Clone(MPEClip clip)
        {
            var column = new MPEClipColumn(clip);
            //column.MachineName = MachineName;
            column.Machine = Machine;
            column.Graphical = Graphical;
            column.GroupType = GroupType;
            // column.ParamIndex = ParamIndex;
            // column.IndexInGroup = IndexInGroup;
            column.Parameter = Parameter;
            column.ParamTrack = ParamTrack;

            return column;

        }

        internal int RowLenghtAt(int time)
        {
            //int beats = BuzzTicksToBeats(MPEPattern.Pattern.Length);
            int beatTime = ClipControl.BUZZ_TICKS_PER_BEAT * ClipEvent.TimeBase;
            int beat = time / beatTime;
            int beatRows = beat < BeatRowsList.Count ? BeatRowsList[beat] : BeatRowsList[BeatRowsList.Count - 1];
            int rowTime = beatTime / beatRows;

            return rowTime;
        }

        internal IEnumerable<ClipEvent> GetEventsQuantized(int start, int end, int rpb)
        {
            var events = (Bound ? GetEvents(start, end) : eventList.Where(e => e.Time >= start && e.Time < end)).ToArray();
            for (int i = 0; i < events.Length; i++)
            {
                events[i].Time = GetTimeQuantized(events[i].Time, rpb);
            }
            return events;
        }

        internal int GetRelativeTimeInRowToParamValue(int playPosition)
        {
            double relativePos = GetRelativeTimeInRow(playPosition);
            return (int)(relativePos * (Parameter.MaxValue - Parameter.MinValue)) + Parameter.MinValue;
        }

        internal void Clear()
        {
            if (Bound)
            {
                var all = GetEvents(int.MinValue, int.MaxValue).ToArray();
                if (all.Length > 0) StoreLane(false)?.SetEvents(all, false);
            }
            else
                eventList.Clear();
            MPEClip.Editor.clipControl.InvalidateVisual();
        }
    }
}
