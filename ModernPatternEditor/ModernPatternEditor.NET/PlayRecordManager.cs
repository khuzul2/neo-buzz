using BuzzGUI.Common;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WDE.ModernPatternEditor.Actions;
using WDE.ModernPatternEditor.MPEStructures;
using static WDE.ModernPatternEditor.ClipEditorUtils;

namespace WDE.ModernPatternEditor
{
    internal class PlayRecordManager
    {
        public ClipEditor Editor { get; }

        Dictionary<ISequence, PlayInfo> playInfoDictionary = new Dictionary<ISequence, PlayInfo>();
        List<PlayingTrackInfo> PlayingNotesList = new List<PlayingTrackInfo>();
        List<CollectedEventState> midiEventsToTriggerLater = new List<CollectedEventState>();

        // Fork (neo-buzz audio hygiene, docs/specs/daw-modes.md §8.1): what Play needs, made once and
        // reused (it runs on the audio thread, under the editor's syncLock, on every tick and subtick).
        // It made a list, a sequence collection, a dictionary and a closure per column per call.
        readonly List<CollectedEventState> collectEvents = new List<CollectedEventState>();
        readonly List<IMachine> controlChangeMachines = new List<IMachine>();
        readonly Stack<PlayingTrackInfo> freeTrackInfos = new Stack<PlayingTrackInfo>();
        readonly List<ClipEvent> midiColumnEvents = new List<ClipEvent>();

        struct PlayInfo
        {
            public int PreviousPosition;
            public IClip PreviousClip { get; internal set; }
        }

        internal struct MidiColumnData
        {
            internal MPEClipColumn column;
            internal int value;
        }

        internal struct MidiEventData
        {
            internal MidiColumnData midiNote;
            internal MidiColumnData midiVelocity;
            internal MidiColumnData midiDelay;
            internal MidiColumnData midiCut;
            internal MidiColumnData midiPitchWheel;
            internal MidiColumnData midiCC;
            internal MidiColumnData midiChannel;
            internal bool noteOff;
            internal int midiNoteTimeQ;
        }

        public void Release()
        {
            PlayingNotesList.Clear();
            midiEventsToTriggerLater.Clear();
            playInfoDictionary.Clear();
        }

        class PlayingTrackInfo
        {
            internal int track;
            internal int note;
            internal IClip clip;
            internal IParameter parameter;
            internal int channel;
        }
        public PlayRecordManager(ClipEditor editor)
        {
            Editor = editor;
        }

        internal enum CollectedEventType
        {
            Buzz,
            MidiNote,
            MidiCC
        }

        // Fork (audio hygiene): a struct, so collecting events allocates nothing (it was a class).
        internal struct CollectedEventState
        {
            internal IParameter parameter;
            internal int time;
            internal int ParamTrack;
            internal int value;
            internal int midiNote;
            internal int midiVolume;
            internal IMachine machine;
            internal CollectedEventType eventType;
            internal int midiChannel;
        }

        // Fork (docs/specs/clips.md §5.6, CU3): one store, one engine. In the Studio build the engine plays every lane of a clip (parameter lanes before
        // the tick, the Notes lane through SendMidiInput) and the editor plays nothing: Play and what it fires are Classic's (WPF) build only.
        internal void RefreshPlayPosData()
        {
            lock (Editor.syncLock)
            {
                if (Editor.Song != null)
                {
                    foreach (var seq in Editor.Song.Sequences)
                    {
                        playInfoDictionary[seq] = new PlayInfo();
                    }
                }
            }
        }


        internal void Stop()
        {
            lock (Editor.syncLock)
            {
                foreach (var e in midiEventsToTriggerLater)
                {
                    var machine = e.machine;
                    machine.SendMIDINote(machine.MIDIInputChannel, e.midiNote, 0);
                }
                midiEventsToTriggerLater.Clear();
                foreach (var note in PlayingNotesList)
                {
                    var machine = note.parameter.Group.Machine;
                    machine.SendMIDINote(note.channel, BuzzNote.ToMIDINote(note.note), 0);
                }
                PlayingNotesList.Clear();
            }
        }

        internal void StopNonEditorMidiNotes()
        {
            lock (Editor.syncLock)
            {
                for (int i = 0; i < PlayingNotesList.Count; i++)
                {
                    var note = PlayingNotesList[i];

                    if (note.track == -1)
                    {
                        var machine = note.parameter.Group.Machine;
                        machine.SendMIDINote(note.channel, BuzzNote.ToMIDINote(note.note), 0);
                        PlayingNotesList.RemoveAt(i);
                        i--;
                    }
                }
                PlayingNotesList.Clear();
            }
        }


        internal int GetPositionInClip(out IClip? pat, ISequence seq, IMachine target)
        {
            pat = null;

            if (seq.Machine == target && seq.PlayingClip != null && !seq.IsDisabled)
            {
                pat = seq.PlayingClip;
                if (pat != null && pat.PlayPosition >= 0)
                {
                    return pat.PlayPosition;
                }
                else return -1;
            }
            return -1;
        }

        internal ReadOnlyCollection<ISequence> GetPlayingSequences(IMachine target)
        {
            List<ISequence> sequences = new List<ISequence>();
            foreach (ISequence seq in Global.Buzz.Song.Sequences)
            {
                if (seq.Machine == target)
                {
                    IClip pat = seq.PlayingClip;
                    if (pat != null)
                    {
                        sequences.Add(seq);
                    }
                }
            }
            return sequences.AsReadOnly();
        }

        internal IClip? GetPlayingClip(IMachine target)
        {
            IClip? clip = null;
            foreach (ISequence seq in Global.Buzz.Song.Sequences)
            {
                if (seq.Machine == target)
                {
                    clip = seq.PlayingClip;
                    if (clip != null)
                    {
                        break;
                    }
                }
            }
            return clip;
        }

        internal void Init()
        {
            if (Editor.TargetMachine == null)
                return;

            lock (Editor.syncLock)
            {
                PlayingNotesList = new List<PlayingTrackInfo>(Editor.TargetMachine.TrackCount);
                if (Editor.Song != null)
                {
                    for (int i = 0; i < playInfoDictionary.Keys.Count; i++)
                    {
                        var info = playInfoDictionary.ElementAt(i).Value;
                        info.PreviousPosition = Editor.Song.PlayPosition;
                    }
                }
            }
        }

        internal void RecordControlChange(IParameter param, int track, int value)
        {
            IMachine mac = param.Group.Machine;
            IClip pat = mac.Clips.FirstOrDefault(x => x.PlayPosition >= 0);
            if (pat != null)
            {
                int playPostion = pat.PlayPosition;
                if (playPostion >= 0)
                {
                    //Task.Run(() =>
                    {
                        RecordControlChangeToClip(pat, playPostion, param, track, value);
                    }
                    //);
                }
            }
        }

        private void RecordControlChangeToClip(IClip pat, int playPosition, IParameter parameter, int track, int value)
        {
            var vmClip = Editor.SelectedMachine.Clips.FirstOrDefault(x => x.Clip == pat);

            foreach (var columnSet in vmClip.ColumnSets)
            {
                foreach (var column in columnSet.Columns)
                {
                    var parameterColumn = (ParameterColumn)column;
                    if (parameterColumn.ClipColumn.Parameter == parameter && parameterColumn.ClipColumn.Track == track)
                    {
                        // Touching so UI elements so dispatcher...
                        Editor.Dispatcher.BeginInvoke((Action)(() =>
                        {
                            var digit = parameterColumn.GetDigitAtTime(playPosition);
                            foreach (var a in parameterColumn.EditValue(digit, value))
                                Editor.EditContext.ActionStack.Do(a);
                        }));
                    }
                }
            }
        }

        internal void RecordPlayingMidiNote(int channel, int value, int velocity)
        {
            foreach (var clip in Editor.SelectedMachine.Machine.Clips)
            {
                int playPosition = clip.PlayPosition;
                if (playPosition >= 0)
                {
                    RecordPlayingMidiNoteClip(clip, playPosition, value, velocity, channel);
                }
            }
        }

        private void RecordPlayingMidiNoteClip(IClip clip, int playPosition, int value, int velocity, int channel)
        {
            int availableTrack = -1;
            int buzzNote = -1;
            try
            {
                buzzNote = BuzzNote.FromMIDINote(value);
            }
            catch (Exception)
            {
            }

            if (buzzNote == -1)
                return;

            IMachine selectedColumnMachine;
            MidiEventData machineClipColumnData;

            // 1. Note off event. Is there a note already playing?
            if (velocity == 0)
            {
                // Find playing note
                PlayingTrackInfo pti = PlayingNotesList.FirstOrDefault(x => x.note == buzzNote);
                if (pti != null)
                {
                    PlayingNotesList.Remove(pti);

                    // Recording for the "main" pattern machine for now. ToDo: When start recording, save the target machine to Editor and reference to it here
                    // IMachine selectedColumnMachine = Editor.patternControl.Pattern.CursorPosition.ParameterColumn.PatternColumn.Pattern.Machine;

                    selectedColumnMachine = clip.Machine;
                    machineClipColumnData = GetMidiDataParameterIndexes(clip, selectedColumnMachine, pti.track);
                    RecordMidiNoteOff(clip, playPosition, machineClipColumnData, channel);
                }
                return;
            }

            // 2. Find available track
            for (int i = 0; i < clip.Machine.TrackCount; i++)
            {
                bool trackAvailable = true;
                foreach (var playingNote in PlayingNotesList)
                {
                    // Is same note playing in track, then we can use the same track
                    if (playingNote.track == i)
                    {
                        if (playingNote.note == buzzNote)
                        {
                            break;
                        }
                        else
                        {
                            trackAvailable = false;
                            break;
                        }
                    }
                }

                if (trackAvailable)
                {
                    availableTrack = i;
                    break;
                }
            }

            // Not found, use the oldest track
            if (availableTrack == -1)
            {
                PlayingTrackInfo pti = PlayingNotesList.ElementAtOrDefault(0);
                if (pti == null)
                {
                    availableTrack = 0;
                }
                else
                {
                    availableTrack = pti.track;
                    PlayingNotesList.RemoveAt(0);
                }
            }

            selectedColumnMachine = clip.Machine;
            machineClipColumnData = GetMidiDataParameterIndexes(clip, selectedColumnMachine, availableTrack);

            if (machineClipColumnData.midiNote.column != null)
            {
                IParameter noteParameter = machineClipColumnData.midiNote.column.Parameter;
                Editor.Dispatcher.Invoke(() =>
                {
                    using (new ActionGroup(Editor.EditContext.ActionStack))
                    {
                        // Note
                        if (machineClipColumnData.midiNote.column != null)
                        {
                            var mpeColumn = machineClipColumnData.midiNote.column;
                            int timeq = mpeColumn.GetTimeQuantized(playPosition);
                            Editor.DoAction(new MPESetOrClearEventsAction(clip, mpeColumn, new ClipEvent[] { new ClipEvent() { Time = timeq, Value = buzzNote } }, true));
                        }
                        // Volume
                        if (machineClipColumnData.midiVelocity.column != null)
                        {
                            var mpeColumn = machineClipColumnData.midiVelocity.column;
                            int timeq = mpeColumn.GetTimeQuantized(playPosition);
                            int vol = (int)(velocity * ((mpeColumn.Parameter.MaxValue - mpeColumn.Parameter.MinValue) / 127.0));
                            Editor.DoAction(new MPESetOrClearEventsAction(clip, mpeColumn, new ClipEvent[] { new ClipEvent() { Time = timeq, Value = vol } }, true));
                        }
                        // Delay
                        if (machineClipColumnData.midiDelay.column != null)
                        {
                            var mpeColumn = machineClipColumnData.midiDelay.column;
                            int timeq = mpeColumn.GetTimeQuantized(playPosition);
                            int delay = (int)((mpeColumn.Parameter.MaxValue - mpeColumn.Parameter.MinValue) * mpeColumn.GetRelativeTimeInRow(playPosition));
                            Editor.DoAction(new MPESetOrClearEventsAction(clip, mpeColumn, new ClipEvent[] { new ClipEvent() { Time = timeq, Value = delay } }, true));
                        }

                        if (machineClipColumnData.midiChannel.column != null)
                        {
                            var mpeColumn = machineClipColumnData.midiChannel.column;
                            int timeq = mpeColumn.GetTimeQuantized(playPosition);
                            Editor.DoAction(new MPESetOrClearEventsAction(clip, mpeColumn, new ClipEvent[] { new ClipEvent() { Time = timeq, Value = channel + 1 } }, true));
                        }
                    }
                });

                PlayingTrackInfo ptinew = new PlayingTrackInfo();
                ptinew.clip = clip;
                ptinew.note = buzzNote;
                ptinew.track = availableTrack;
                ptinew.channel = channel;
                ptinew.parameter = noteParameter;
                PlayingNotesList.Add(ptinew);
            }
        }

        internal void RecordMidiNoteOff(IClip clip, int playPosition, MidiEventData machineClipColumnData, int channel)
        {
            if (machineClipColumnData.midiNote.column != null)
            {
                var columnNote = machineClipColumnData.midiNote.column;

                int noteTimeQ = columnNote.GetTimeQuantized(playPosition);
                var noteEvents = columnNote.GetEvents(noteTimeQ, noteTimeQ + 1);

                // If there is a note and playPosition > timeq + delay, then edit cut
                int delay = 0;
                int delayTimeQ = 0;
                var columnDelay = machineClipColumnData.midiDelay.column;
                if (columnDelay != null)
                {
                    delayTimeQ = columnDelay.GetTimeQuantized(playPosition);
                    var delayEvents = columnDelay.GetEvents(delayTimeQ, delayTimeQ + 1);
                    int rowLenght = columnDelay.RowLenghtAt(delayTimeQ);
                    if (delayEvents.Count() > 0)
                    {
                        var delayEvent = delayEvents.First();
                        delay = (int)((delayEvent.Value - columnDelay.Parameter.MinValue) /
                            (columnDelay.Parameter.MaxValue - (double)columnDelay.Parameter.MinValue)) * rowLenght;
                    }
                }

                if (noteEvents.Count() > 0)
                {
                    if (machineClipColumnData.midiCut.column != null && playPosition > noteTimeQ + delay)
                    {
                        var columnCut = machineClipColumnData.midiCut.column;
                        if (columnCut != null)
                        {
                            int cutTimeQ = columnCut.GetTimeQuantized(playPosition);
                            var cutEvents = columnCut.GetEvents(cutTimeQ, cutTimeQ + 1);
                            if (cutEvents.Count() == 0)
                            {
                                int cutTime = columnCut.GetRelativeTimeInRowToParamValue(playPosition);
                                Editor.Dispatcher.Invoke(() =>
                                {
                                    Editor.DoAction(new MPESetOrClearEventsAction(clip, columnCut, new ClipEvent[] { new ClipEvent() { Time = cutTimeQ, Value = cutTime } }, true));
                                });
                            }
                        }
                    }
                }
                else
                {
                    Editor.Dispatcher.Invoke(() =>
                    {
                        using (new ActionGroup(Editor.EditContext.ActionStack))
                        {
                            Editor.DoAction(new MPESetOrClearEventsAction(clip, columnNote, new ClipEvent[] { new ClipEvent() { Time = noteTimeQ, Value = BuzzNote.Off } }, true));


                            if (columnDelay != null)
                            {
                                int delayTime = columnDelay.GetRelativeTimeInRowToParamValue(playPosition);
                                Editor.DoAction(new MPESetOrClearEventsAction(clip, columnDelay, new ClipEvent[] { new ClipEvent() { Time = delayTimeQ, Value = delayTime } }, true));
                            }

                            var columnChannel = machineClipColumnData.midiChannel.column;
                            if (columnChannel != null)
                            {
                                int channelTimeQ = columnChannel.GetTimeQuantized(playPosition);
                                Editor.DoAction(new MPESetOrClearEventsAction(clip, columnChannel, new ClipEvent[] { new ClipEvent() { Time = channelTimeQ, Value = channel + 1 } }, true));
                            }
                        }
                    });
                    //lock (Editor.syncLock)
                    {
                        PlayingNotesList.RemoveAll(x => x.track == columnNote.ParamTrack && x.parameter == columnNote.Parameter);
                    }
                }
            }
        }

        /// <summary>The first playing note on <paramref name="track"/> of <paramref name="parameter"/>, or null.</summary>
        PlayingTrackInfo FindPlayingNote(int track, IParameter parameter)
        {
            for (int i = 0; i < PlayingNotesList.Count; i++)
            {
                var x = PlayingNotesList[i];
                if (x.track == track && x.parameter == parameter)
                    return x;
            }
            return null;
        }

        public void UpdatePlayingNoteClip(IParameter parameter, int track, int buzzNote, int volume)
        {
            // Stop notes on target track
            // ToDo: if midinote, always do SendMidiNote
            // Fork (audio hygiene): loops instead of FirstOrDefault and RemoveAll (their closures
            // were made on every call, on the audio thread), and the notes' records reused.
            PlayingTrackInfo pti = FindPlayingNote(track, parameter);
            if (pti != null &&
                parameter is MPEInternalParameter &&
                ((MPEInternalParameter)parameter).InternalType == InternalParameter.MidiNote)
            {
                parameter.Group.Machine.SendMIDINote(pti.channel, BuzzNote.ToMIDINote(pti.note), 0);
            }

            for (int i = 0; i < PlayingNotesList.Count; i++)
            {
                var x = PlayingNotesList[i];
                if (x.track == track && x.parameter == parameter)
                {
                    PlayingNotesList.RemoveAt(i--);
                    freeTrackInfos.Push(x);
                }
            }

            if (buzzNote != BuzzNote.Off)
            {
                PlayingTrackInfo ptinew = freeTrackInfos.Count > 0 ? freeTrackInfos.Pop() : new PlayingTrackInfo();
                ptinew.clip = null;
                ptinew.channel = 0;
                ptinew.parameter = parameter;
                ptinew.note = buzzNote;
                ptinew.track = track;
                PlayingNotesList.Add(ptinew);
                if (parameter is MPEInternalParameter &&
                ((MPEInternalParameter)parameter).InternalType == InternalParameter.MidiNote)
                {
                    int midiNote = -1;
                    try
                    {
                        midiNote = BuzzNote.ToMIDINote(buzzNote);
                    }
                    catch { }

                    if (midiNote != -1)
                    {
                        parameter.Group.Machine.SendMIDINote(0, midiNote, volume);
                    }
                }
            }
        }

        private MidiEventData GetMidiDataParameterIndexes(IClip clip, IMachine machine, int track)
        {
            MidiEventData midiData = new MidiEventData();

            // If there is a midi note, then try to find rest of the midi columns.
            // Otherwise handle special cases (pVST...)
            var mpeClip = Editor.MPEClipsDB.GetMPEClip(clip);
            var midiNote = mpeClip.GetColumn(machine, (int)InternalParameter.MidiNote, track);
            if (midiNote != null)
            {
                midiData.midiNote.column = midiNote;
                midiData.midiVelocity.column = mpeClip.GetColumn(machine, (int)InternalParameter.MidiVelocity, track);
                midiData.midiCut.column = mpeClip.GetColumn(machine, (int)InternalParameter.MidiNoteCut, track);
                midiData.midiDelay.column = mpeClip.GetColumn(machine, (int)InternalParameter.MidiNoteDelay, track);
                midiData.midiPitchWheel.column = mpeClip.GetColumn(machine, (int)InternalParameter.MidiPitchWheel, track);
                midiData.midiCC.column = mpeClip.GetColumn(machine, (int)InternalParameter.MidiCC, track);
            }
            else
            {
                // Maybe xml file that includes MIDI mappings...
                if (machine.DLL.Name == "Polac VSTi 1.1")
                {
                    midiData.midiNote.column = mpeClip.GetColumn(machine, 0, track);
                    midiData.midiVelocity.column = mpeClip.GetColumn(machine, 1, track);
                    midiData.midiDelay.column = mpeClip.GetColumn(machine, 2, track);
                    midiData.midiCut.column = mpeClip.GetColumn(machine, 3, track);
                    midiData.midiChannel.column = mpeClip.GetColumn(machine, 8, track);
                }
            }

            return midiData;
        }

        internal MidiEventData GetMidiDataForNote(IClip clip, MPEClipColumn mpeColumn, IMachine machine, int start, int end, int track)
        {
            var machineClipColumnData = GetMidiDataParameterIndexes(clip, machine, track);
            machineClipColumnData.midiNote.value = -1;
            machineClipColumnData.midiVelocity.value = MPEInternalParameter.DefaultMidiVolume;

            // Fork (audio hygiene): each column's events into a list made once (GetEvents made an array).
            var events = midiColumnEvents;
            if (machineClipColumnData.midiNote.column != null)
            {
                var columnNote = machineClipColumnData.midiNote.column;
                var noteEvents = columnNote.GetEventsInto(start, end, events);
                machineClipColumnData.midiNoteTimeQ = columnNote.GetTimeQuantized(end);
                if (noteEvents.Count > 0)
                {
                    int val = noteEvents[0].Value;
                    if (val == BuzzNote.Off)
                        machineClipColumnData.noteOff = true;
                    else
                        machineClipColumnData.midiNote.value = BuzzNote.ToMIDINote(noteEvents[0].Value);
                }
            }
            if (machineClipColumnData.midiVelocity.column != null)
            {
                var columnVol = machineClipColumnData.midiVelocity.column;
                var volEvents = columnVol.GetEventsInto(start, end, events);
                if (volEvents.Count > 0)
                {
                    int vol = volEvents[0].Value;
                    machineClipColumnData.midiVelocity.value = vol;
                    machineClipColumnData.midiVelocity.column.Parameter.SetValue(track, vol);
                }
                else
                {
                    machineClipColumnData.midiVelocity.value = machineClipColumnData.midiVelocity.column.Parameter.GetValue(track);
                }
            }
            if (machineClipColumnData.midiDelay.column != null)
            {
                var columnDelay = machineClipColumnData.midiDelay.column;
                var delayEvents = columnDelay.GetEventsInto(start, end, events);

                if (delayEvents.Count > 0)
                {
                    int delayTimeQ = columnDelay.GetTimeQuantized(end);
                    int rowLenght = columnDelay.RowLenghtAt(delayTimeQ);
                    machineClipColumnData.midiDelay.value = (int)(rowLenght * (delayEvents[0].Value - columnDelay.Parameter.MinValue) / ((double)columnDelay.Parameter.MaxValue - columnDelay.Parameter.MinValue));
                }
            }
            if (machineClipColumnData.midiCut.column != null)
            {
                var columnCut = machineClipColumnData.midiCut.column;
                var cutEvents = columnCut.GetEventsInto(start, end, events);

                if (cutEvents.Count > 0)
                {
                    int cutTimeQ = columnCut.GetTimeQuantized(end);
                    int rowLenght = columnCut.RowLenghtAt(cutTimeQ);
                    machineClipColumnData.midiCut.value = (int)(rowLenght * (cutEvents[0].Value - columnCut.Parameter.MinValue) / ((double)columnCut.Parameter.MaxValue - columnCut.Parameter.MinValue));
                }
            }
            if (machineClipColumnData.midiChannel.column != null)
            {
                var columnChannel = machineClipColumnData.midiChannel.column;
                var chanEvents = columnChannel.GetEventsInto(start, end, events);
                if (chanEvents.Count > 0)
                {
                    machineClipColumnData.midiChannel.value = chanEvents[0].Value;
                }
            }
            else
            {
                machineClipColumnData.midiChannel.value = mpeColumn.Machine.MIDIInputChannel + 1;
            }


            return machineClipColumnData;
        }

        internal void UpdatePlayingMidiNote(int channel, int value, int velocity)
        {
            int buzzNote = -1;
            try
            {
                buzzNote = BuzzNote.FromMIDINote(value);
            }
            catch
            {
                return;
            }

            if (velocity == 0)
            {
                PlayingNotesList.RemoveAll(x => x != null && x.note == buzzNote);
            }
            else if (Editor.SelectedMachine != null)
            {
                PlayingTrackInfo pti = new PlayingTrackInfo();
                pti.note = buzzNote;
                pti.track = -1;
                pti.channel = channel;
                pti.parameter = Editor.SelectedMachine.Machine.ParameterGroups[0].Parameters[0]; // Just get a parameter
                PlayingNotesList.Add(pti);
            }
        }
    }
}
