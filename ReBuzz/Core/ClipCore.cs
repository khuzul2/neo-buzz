using BuzzGUI.Common;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using static ReBuzz.Core.ParameterCore;

namespace ReBuzz.Core
{
    internal class ClipCore : IClip
    {
        private static int clipHandleCounter = 100;
        public IMachine Machine { get; set; }

        string name;

        private void UpdateSequences()
        {
            foreach(var seq in Machine.Graph.Buzz.Song.Sequences)
            {
                var seqCore = seq as SequenceCore;
                if (seqCore == null) continue;
                var mine = seqCore.EventsOfClip(this);        // fork (0.6.1): a copy taken safely (this runs on the IPC thread too)
                if (mine.Count == 0) continue;
                // Fork (0.5.1): spans are the part each event plays, cut at the next event
                // (upstream set the whole length, so a pattern that grew ran over the next event).
                seqCore.RefreshSpans();
                foreach (var seqEventKeyValue in mine)
                    seqCore.SequenceEventChanged(seqEventKeyValue.Key, seqEventKeyValue.Value);
            }
        }

        public string Name 
        { 
            get => name; 
            set 
            { 
                name = value; 
                PropertyChanged.Raise(this, "Name");
                UpdateSequences();
            } 
        }

        int length;
        public int Length
        {
            get => length;
            set
            {   
                length = value;
                PropertyChanged.Raise(this, "Length");
                UpdateSequences();
                // Fork (0.6.1): a linked follow action moves with the clip's end.
                (Machine?.Graph?.Buzz as ReBuzzCore)?.SessionCore?.OnClipLengthChanged(this);
            }
        }

        /// <summary>
        /// Fork (0.6.1): set (under AudioLock) when the pattern is deleted. Deleted patterns never
        /// come back (undo creates a new one), so the Session View rejects launches of one that a
        /// stale reference still holds.
        /// </summary>
        internal volatile bool IsDeleted;

        /// <summary>
        /// Fork (mac-engine.md M2): the built-in pattern editor's contents of an old song's pattern,
        /// kept when no pattern editor took them (a host without one), so the song saves back with
        /// them. Null otherwise. See <see cref="FileOps.BuiltInPattern"/>.
        /// </summary>
        internal FileOps.BuiltInClip BuiltInData;

        // Fork (0.6.3): copy on write. The audio thread walks the list without a lock; a change
        // makes a new list, so a walk in progress never sees one inserted or removed under it.
        volatile List<IClipColumn> columns;

        /// <summary>Fork (0.4.1): the columns without a read-only wrapper (one per call), for the audio thread.</summary>
        internal List<IClipColumn> ColumnsList => columns;
        public ReadOnlyCollection<IClipColumn> Columns { get => columns.AsReadOnly(); }

        // Fork (0.4.1): starts "not playing". It was 0 until the engine's next block cleared every
        // pattern; the engine now clears only the patterns it positioned (WorkManager).
        int playPosition = int.MinValue;

        /// <summary>
        /// Fork (0.4.1): the track whose session clip gave this pattern its play position in the
        /// current block, or null. Audio thread (WorkManager.UpdatePatternPositions).
        /// </summary>
        internal SequenceCore SessionTrack;

        /// <summary>
        /// Fork (Session View 1.0): the pattern's audio clip, for its machine if that is a Session
        /// Clip Player (the layout makes it once and keeps it in step; the audio thread plays it).
        /// </summary>
        internal volatile Clips.ClipPlayback SessionAudio;

        /// <summary>
        /// Fork (neo-audio.md NA2), audio thread: while an arrangement event plays this pattern as an audio clip with fades, the ticks since
        /// its clip started and until it ends (a clip is the events of one clip id; Session clips and the rest: <see cref="int.MinValue"/>).
        /// </summary>
        internal int ArrangementSinceClipStart = int.MinValue, ArrangementUntilClipEnd = int.MinValue;

        public event Action<IClip> OnClipPlayStart;
        public event Action<IClip> OnClipPlayPositionChange;
        public event Action<IClip> OnClipPlayEnd;

        public int PlayPosition
        {
            get => playPosition;
            set
            {
                bool playStart = (playPosition == int.MinValue);
                bool posChange = (playPosition != value);
                bool playEnd = (value == int.MinValue);

                playPosition = value;

                if (posChange)
                {
                    if(playStart)
                    {
                        if(OnClipPlayStart != null)
                        {
                            try
                            {
                                OnClipPlayStart(this);
                            }
                            catch
                            { }
                        }
                    }
                    else if(playEnd)
                    {
                        if (OnClipPlayEnd != null)
                        {
                            try
                            {
                                OnClipPlayEnd(this);
                            }
                            catch
                            { }
                        }
                    }
                    else if(OnClipPlayPositionChange != null)
                    {
                        if (OnClipPlayPositionChange != null)
                        {
                            try
                            {
                                OnClipPlayPositionChange(this);
                            }
                            catch
                            { }
                        }
                    }
                }
            }
        }

        bool isPlayingSolo;
        public bool IsPlayingSolo
        {
            get => isPlayingSolo;
            set
            {
                var bc = Global.Buzz as ReBuzzCore;
                isPlayingSolo = value;
                if (isPlayingSolo)
                {
                    bc.SoloClip = this;
                    bc.Playing = true;
                    playPosition = -1;
                    nextTick = 0;
                }
                else
                {
                    nextTick = 0;
                    playPosition = -1;
                    bc.Playing = false;
                }

                PropertyChanged.Raise(this, "IsPlayingSolo");
            }
        }

        internal void PlayFormClipEditorPosition()
        {
            var bc = Global.Buzz as ReBuzzCore;
            int pos = bc.MachineManager.GetEditorClipPosition(Machine as MachineCore);

            lock (ReBuzzCore.AudioLock)
            {
                IsPlayingSolo = true;
                PlayPosition = pos * ClipEvent.TimeBase;
                nextTick = pos;
            }
        }

        public int[] ClipEditorMachineMIDIEvents
        {
            get
            {
                var buzz = Machine.Graph.Buzz as ReBuzzCore;
                return buzz.MachineManager.GetClipEditorMachineMIDIEvents(Machine as MachineCore, this);
            }
            set
            {
                var buzz = Machine.Graph.Buzz as ReBuzzCore;
                buzz.MachineManager.SetClipEditorMachineMIDIEvents(Machine as MachineCore, this, value);
            }
        }

        public IntPtr CClip { get; set; }
        public byte[] Data { get; internal set; }

        public IEnumerable<IClipEditorColumn> ClipEditorMachineEvents
        {
            get
            {
                var buzz = Machine.Graph.Buzz as ReBuzzCore;
                var peme = buzz.MachineManager.ClipEditorMachineEvents(Machine as MachineCore, this);
                return peme;
            }
        }

        public ClipCore(MachineCore machineCore, string name, int length, IUiDispatcher dispatcher)
        {
            Machine = machineCore;
            Name = name;
            Length = length;

            CClip = new IntPtr(clipHandleCounter++);
            columns = new List<IClipColumn>();

            //Get list of sequences that use this pattern
            lock (_owningSequences)
            {
                foreach (var seq in Global.Buzz.Song.Sequences)
                {
                    var mach = seq.Machine;
                    if (mach != null)
                    {
                        foreach (var pat in mach.Clips.Where(p => p == this))
                        {
                            _owningSequences.Add(seq);
                            break;
                        }
                    }
                }
            }

            subscribedSong = Global.Buzz.Song;
            subscribedSong.SequenceAdded += Song_SequenceAdded;
            subscribedSong.SequenceRemoved += Song_SequenceRemoved;
            subscribedSong.SequenceChanged += Song_SequenceAdded;

            /*
            foreach (var parTrackTuple in Machine.AllParametersAndTracks())
            {
                PatternColumnCore pc = new PatternColumnCore(PatternColumnType.Parameter, this, Machine, parTrackTuple.Item1, parTrackTuple.Item2, null, null);
                columns.Add(pc);
            }
            */
            this.dispatcher = dispatcher;
        }

        HashSet<ISequence> _owningSequences = new HashSet<ISequence>();
        ISong subscribedSong;



        ~ClipCore()
        {
            Global.Buzz.Song.SequenceAdded -= Song_SequenceAdded;
            Global.Buzz.Song.SequenceRemoved -= Song_SequenceRemoved;
            Global.Buzz.Song.SequenceChanged -= Song_SequenceChanged;
        }

        
        private void Song_SequenceAdded(int obj, ISequence seq)
        {
            lock (_owningSequences)
            {   //If the added sequence refers to the same machine as the
                //one associated with the pattern, then associate the sequence with this pattern.
                if(seq.Machine == Machine)
                    _owningSequences.Add((ISequence)seq);
            }
        }

        private void Song_SequenceChanged(int obj, ISequence seq)
        {
            lock (_owningSequences)
            {
                //If the added sequence refers to the same machine as the
                //one associated with the pattern, then associate the sequence with this pattern.
                if (seq.Machine == Machine)
                    _owningSequences.Add((ISequence)seq);
            }
        }

        private void Song_SequenceRemoved(int obj, ISequence seq)
        {
            lock (_owningSequences)
            {
                _owningSequences.Remove((ISequence)seq);
            }
        }
        public IReadOnlyCollection<ISequence> Sequences
        {
            get
            {
                IReadOnlyCollection<ISequence> ret;

                lock (_owningSequences)
                {
                    ret = _owningSequences.ToReadOnlyCollection();
                }

                return ret;
            }
        }

        public event Action<IClipColumn> ColumnAdded;
        public event Action<IClipColumn> ColumnRemoved;
        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<IClipColumn> ClipChanged;
        Lock clipLock = new();

        public void DeleteColumn(int index)
        {
            lock (clipLock)
            {
                if (index < columns.Count)
                {
                    var pcc = columns[index];
                    var changed = new List<IClipColumn>(columns);
                    changed.RemoveAt(index);
                    columns = changed;
                    ColumnRemoved?.Invoke(pcc);
                }
            }
        }

        public void InsertColumn(int index, IParameter p, int track)
        {
            lock (clipLock)
            {
                ClipColumnType type = p.IndexInGroup == (int)InternalParameter.MidiNote || p.IndexInGroup == -1 ? ClipColumnType.MIDI : ClipColumnType.Parameter;
                ClipColumnCore pcc = new ClipColumnCore(type, this, p.Group.Machine, p, track, null, null);
                //int newIndex = Math.Min(index, columns.Count);
                //columns.Insert(newIndex, pcc);
                var changed = new List<IClipColumn>(columns);
                changed.Insert(index, pcc);
                columns = changed;
                ColumnAdded?.Invoke(pcc);
            }
        }

        /// <summary>
        /// Fork (0.7): a parameter column with its metadata set before the audio thread can see
        /// the column (the engine reads the session automation mark while it plays).
        /// </summary>
        internal void InsertColumn(int index, IParameter p, int track, Dictionary<string, string> metadata)
        {
            lock (clipLock)
            {
                ClipColumnType type = p.IndexInGroup == (int)InternalParameter.MidiNote || p.IndexInGroup == -1 ? ClipColumnType.MIDI : ClipColumnType.Parameter;
                ClipColumnCore pcc = new ClipColumnCore(type, this, p.Group.Machine, p, track, null, metadata);
                var changed = new List<IClipColumn>(columns);
                changed.Insert(Math.Clamp(index, 0, changed.Count), pcc);
                columns = changed;
                ColumnAdded?.Invoke(pcc);
            }
        }

        public void InsertColumn(int index, IMachine? m)
        {
            lock (clipLock)
            {
                if (m != null)
                {
                    if (index < m.AllNonInputParameters().Count())
                    {
                        var p = m.AllNonInputParameters().ElementAt(index);
                        InsertColumn(columns.Count, p, 0);
                    }
                }
                else
                {
                    ParameterCore parameter = ParameterCore.GetMidiParameter(Machine as MachineCore, dispatcher);

                    ClipColumnCore pcc = new ClipColumnCore(ClipColumnType.MIDI, this, Machine, parameter, 0, null, null);
                    var changed = new List<IClipColumn>(columns);
                    changed.Insert(index, pcc);
                    columns = changed;
                    ColumnAdded?.Invoke(pcc);
                }
            }
        }

        public void UpdatePEMachineWaveReferences(IDictionary<int, int> map)
        {
        }

        double nextTick = 0;
        private readonly IUiDispatcher dispatcher;
        internal IUiDispatcher Dispatcher => dispatcher;

        internal void UpdateSoloPlayPosition(int sampleCount)
        {
            var masterInfo = ReBuzzCore.masterInfo;

            if (nextTick >= Length)
            {
                nextTick = 0;
            }
            playPosition = (int)(ClipEvent.TimeBase * nextTick);

            nextTick += sampleCount / (double)masterInfo.SamplesPerTick;
        }

        public void NotifyClipChanged()
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                ClipChanged?.Invoke(null);
            }));
        }

        internal void ClearEvents()
        {
            // Fork (0.6.3): off the song's events. Upstream left that to the finalizer, which the
            // song's own reference to this pattern kept from ever running: every deleted pattern
            // lived (and listened) as long as the song.
            var song = subscribedSong;
            subscribedSong = null;
            if (song != null)
            {
                song.SequenceAdded -= Song_SequenceAdded;
                song.SequenceRemoved -= Song_SequenceRemoved;
                song.SequenceChanged -= Song_SequenceAdded;
            }
            ClipChanged = null;
            ColumnAdded = null;
            ColumnRemoved = null;
            PropertyChanged = null;
        }
    }
}
