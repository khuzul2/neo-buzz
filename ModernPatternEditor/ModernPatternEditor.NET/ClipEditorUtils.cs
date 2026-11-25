using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor
{
    public static class ClipEditorUtils
    {
        static readonly byte PATTERNXP_DATA_VERSION = 3;
        static readonly byte NOT_PATTERNXP_DATA = 255;
        static readonly byte MODERN_PATTERN_EDITOR_DATA_VERSION = 1;

        public enum InternalParameter
        {
            SPGlobalTrigger = -10,
            SPGlobalEffect1 = -11,
            SPGlobalEffect1Data = -12,

            //FirstInternalTrackParameter = -101,
            SPTrackTrigger = -110,
            SPTrackEffect1 = -111,
            SPTrackEffect1Data = -112,

            //FirstMidiTrackParameter = -128,
            MidiNote = -128,
            MidiVelocity = -129,
            MidiNoteDelay = -130,
            MidiNoteCut = -131,
            MidiPitchWheel = -132,
            MidiCC = -133
        };

        public static readonly int LastMidiTrackParameter = -133;


        public static List<MPEClip> ProcessEditorData(ClipEditor editor, byte[] data)
        {
            List<MPEClip> clips = new List<MPEClip>();
            if (data.Length == 0)
                return clips;

            int index = 0;
            // Decode clip editor data
            byte[] machineClipsData = data;

            byte version = machineClipsData[index++];

            if (index >= data.Length)
                return clips;

            if (version >= 1 && version <= PATTERNXP_DATA_VERSION)
            {
                return ParsePatternXPData(editor, machineClipsData, index, version);
            }
            else if (version == NOT_PATTERNXP_DATA)
            {
                byte mpeVersion = machineClipsData[index++];
                index += sizeof(int); // Skip data size
                if (mpeVersion == MODERN_PATTERN_EDITOR_DATA_VERSION)
                    return ParseModernPatternEditorData(editor, machineClipsData, index, version);
            }
            return clips;
        }

        #region PatternXPData Read
        private static List<MPEClip> ParsePatternXPData(ClipEditor editor, byte[] machineClipsData, int index, byte version)
        {
            List<MPEClip> clips = new List<MPEClip>();
            int numpat = BCReadInt(machineClipsData, ref index);

            for (int i = 0; i < numpat; i++)
            {
                string name = GetStringFromByteArray(machineClipsData, ref index);
                MPEClip mpeClip = new MPEClip(editor, name);
                mpeClip.ClipName = name;
                LoadClip(mpeClip, machineClipsData, ref index, version);

                clips.Add(mpeClip);
            }

            return clips;
        }

        private static void LoadClip(MPEClip pat, byte[] machineClipsData, ref int index, byte ver)
        {
            int rowsPerBeat = 4;
            if (ver > 1)
                rowsPerBeat = BCReadInt(machineClipsData, ref index);

            int count = BCReadInt(machineClipsData, ref index);

            pat.RowsPerBeat = rowsPerBeat;

            for (int i = 0; i < count; i++)
            {
                LoadColumn(pat, machineClipsData, ref index, ver);
            }
        }

        public static void LoadColumn(MPEClip pat, byte[] machineClipsData, ref int index, byte ver)
        {
            MPEClipColumn mpeColumn = new MPEClipColumn(pat);

            string machineName = GetStringFromByteArray(machineClipsData, ref index);
            machineName = GetMachineCurrentName(machineName);
            int paramIndex = BCReadInt(machineClipsData, ref index); // Can be negative if internal param
            int paramTrack = BCReadInt(machineClipsData, ref index);
            bool graphical = false;

            if (ver >= 3)
            {
                graphical = BitConverter.ToBoolean(machineClipsData, index);
                index += sizeof(bool);
            }

            mpeColumn.Machine = Global.Buzz.Song.Machines.FirstOrDefault(x => x.Name == machineName);
            mpeColumn.ParamTrack = paramTrack;
            mpeColumn.Graphical = graphical;

            var parameter = GetParameter(mpeColumn.Machine, paramIndex, paramTrack);
            mpeColumn.Parameter = parameter;

            if (parameter != null)
            {
                mpeColumn.GroupType = parameter.Group.Type;
            }

            int count = BCReadInt(machineClipsData, ref index);

            List<ClipEvent> events = new List<ClipEvent>();
            for (int i = 0; i < count; i++)
            {
                int eventTime = BCReadInt(machineClipsData, ref index);
                int eventValue = BCReadInt(machineClipsData, ref index);

                ClipEvent pe = new ClipEvent();
                pe.Time = eventTime * ClipEvent.TimeBase * 4 / pat.RowsPerBeat;
                pe.Value = eventValue;

                events.Add(pe);
            }

            if (parameter != null)
            {
                mpeColumn.SetEvents(events.ToArray(), true);
                pat.MPEClipColumns.Add(mpeColumn);
            }
        }

        #endregion

        #region Modern Pattern Editor Data Read
        private static List<MPEClip> ParseModernPatternEditorData(ClipEditor editor, byte[] machineClipsData, int index, byte version)
        {
            List<MPEClip> clips = new List<MPEClip>();
            int numpat = BCReadInt(machineClipsData, ref index);

            for (int i = 0; i < numpat; i++)
            {
                string name = GetStringFromByteArray(machineClipsData, ref index);
                MPEClip mpeClip = new MPEClip(editor, name);
                mpeClip.ClipName = name;
                LoadModernPatternEditorClip(mpeClip, machineClipsData, ref index, version);

                clips.Add(mpeClip);
            }

            return clips;
        }


        private static void LoadModernPatternEditorClip(MPEClip pat, byte[] machineClipsData, ref int index, byte ver)
        {
            int numberOfBeats = BCReadInt(machineClipsData, ref index);
            int rowsPerBeat = BCReadInt(machineClipsData, ref index);

            int count = BCReadInt(machineClipsData, ref index);

            pat.RowsPerBeat = rowsPerBeat;

            for (int i = 0; i < count; i++)
            {
                LoadModernPatternEditorColumn(pat, machineClipsData, ref index, ver, numberOfBeats);
            }

        }

        public static void LoadModernPatternEditorColumn(MPEClip pat, byte[] machineClipsData, ref int index, byte ver, int numberOfBeats)
        {
            MPEClipColumn mpeColumn = new MPEClipColumn(pat);

            string machineName = GetStringFromByteArray(machineClipsData, ref index);
            machineName = GetMachineCurrentName(machineName);
            int paramIndex = BCReadInt(machineClipsData, ref index);
            int paramTrack = BCReadInt(machineClipsData, ref index);
            bool graphical = false;

            if (ver >= 3)
            {
                graphical = BitConverter.ToBoolean(machineClipsData, index);
                index += sizeof(bool);
            }

            mpeColumn.Machine = Global.Buzz.Song.Machines.FirstOrDefault(x => x.Name == machineName);
            mpeColumn.ParamTrack = paramTrack;
            mpeColumn.Graphical = graphical;

            var parameter = GetParameter(mpeColumn.Machine, paramIndex, paramTrack);

            // If machine is missing or parametercount is incorrect, param can be null
            if (parameter != null)
            {
                mpeColumn.GroupType = parameter.Group.Type;
            }
            mpeColumn.Parameter = parameter;

            int count = BCReadInt(machineClipsData, ref index);

            List<ClipEvent> events = new List<ClipEvent>();
            for (int i = 0; i < count; i++)
            {
                int first = BCReadInt(machineClipsData, ref index);
                int second = BCReadInt(machineClipsData, ref index);

                ClipEvent pe = new ClipEvent();
                pe.Time = first;
                pe.Value = second;
                events.Add(pe);
            }

            if (parameter != null)
            {
                mpeColumn.SetEvents(events.ToArray(), true);
            }

            List<int> beatRows = new List<int>();
            // Read rows in beat
            for (int i = 0; i < numberOfBeats; i++)
            {
                beatRows.Add(BCReadInt(machineClipsData, ref index));
            }

            if (parameter != null)
            {
                mpeColumn.SetBeats(beatRows);
                pat.MPEClipColumns.Add(mpeColumn);
            }
        }

        #endregion

        #region PatternXPData Write
        public static byte[] CreatePatternXPClipData(IEnumerable<MPEClip> clips)
        {
            // Encode clip editor data
            List<byte> data = new List<byte>();

            data.Add(PATTERNXP_DATA_VERSION);

            int numpat = clips.Count();
            data.AddRange(BitConverter.GetBytes(numpat));

            for (int i = 0; i < numpat; i++)
            {
                MPEClip mpeClip = clips.ElementAt(i);
                data.AddRange(Encoding.ASCII.GetBytes(mpeClip.ClipName));
                data.Add(0);

                CreateClipData(mpeClip, data, PATTERNXP_DATA_VERSION);
            }
            
            byte[] dataret = data.ToArray();
            return dataret;
        }

        private static void CreateClipData(MPEClip pat, List<byte> data, byte ver)
        {
            if (ver > 1)
                data.AddRange(BitConverter.GetBytes(pat.RowsPerBeat));

            int count = pat.MPEClipColumns.Count;
            data.AddRange(BitConverter.GetBytes(count));

            for (int i = 0; i < count; i++)
            {
                SaveColumn(pat.MPEClipColumns[i], data, ver, pat.RowsPerBeat);
            }
        }

        public static void SaveColumn(MPEClipColumn mpeColumn, List<byte> data, byte ver, int rpb)
        {
            data.AddRange(Encoding.ASCII.GetBytes(mpeColumn.Machine.Name));
            data.Add(0);
            data.AddRange(BitConverter.GetBytes(GetParamIndex(mpeColumn.Parameter, mpeColumn.ParamTrack)));
            data.AddRange(BitConverter.GetBytes(mpeColumn.ParamTrack));
            data.Add(Convert.ToByte(mpeColumn.Graphical));

            IEnumerable<ClipEvent> events = mpeColumn.GetEventsQuantized(0, int.MaxValue, rpb);
            data.AddRange(BitConverter.GetBytes(events.Count()));

            foreach (var e in events.OrderBy(x => x.Time).ToList())
            {
                int time = (int)(e.Time / (ClipEvent.TimeBase * 4 / mpeColumn.MPEClip.RowsPerBeat));
                data.AddRange(BitConverter.GetBytes(time));
                data.AddRange(BitConverter.GetBytes(e.Value));
            }
        }
        #endregion

        #region Modern Pattern Editor Data Write

        private static IParameter GetParameter(IMachine machine, int paramIndex, int paramTrack)
        {
            IParameter ret = null;

            if (paramIndex == (int)InternalParameter.MidiNote)
            {
                ret = MPEInternalParameter.GetInternalParameter(machine, InternalParameter.MidiNote);
            }
            else if (paramIndex == (int)InternalParameter.MidiVelocity)
            {
                ret = MPEInternalParameter.GetInternalParameter(machine, InternalParameter.MidiVelocity);
            }
            else if (paramIndex == (int)InternalParameter.MidiNoteDelay)
            {
                ret = MPEInternalParameter.GetInternalParameter(machine, InternalParameter.MidiNoteDelay);
            }
            else if (paramIndex == (int)InternalParameter.MidiNoteCut)
            {
                ret = MPEInternalParameter.GetInternalParameter(machine, InternalParameter.MidiNoteCut);
            }
            else if (paramIndex == (int)InternalParameter.MidiPitchWheel)
            {
                ret = MPEInternalParameter.GetInternalParameter(machine, InternalParameter.MidiPitchWheel);
            }
            else if (paramIndex == (int)InternalParameter.MidiCC)
            {
                ret = MPEInternalParameter.GetInternalParameter(machine, InternalParameter.MidiCC);
            }
            else if (paramIndex >= 0)
            {
                int group0ParamsCount = machine.ParameterGroups[0].Parameters.Count;
                int group1ParamsCount = machine.ParameterGroups[1].Parameters.Count;
                int group2ParamsCount = machine.ParameterGroups[2].Parameters.Count;

                if (paramIndex < group1ParamsCount && paramTrack == 0)
                {
                    // Global
                    ret = machine.ParameterGroups[1].Parameters[paramIndex];
                }
                else
                {   
                    int index = paramIndex - group1ParamsCount;
                    if (index < machine.ParameterGroups[2].Parameters.Count)
                    {
                        ret = machine.ParameterGroups[2].Parameters[index];
                    }
                }
            }

            return ret;
        }

        public static byte[] CreateModernPatternEditorData(IEnumerable<MPEClip> clips)
        {
            // Encode clip editor data
            List<byte> data = new List<byte>();

            data.Add(NOT_PATTERNXP_DATA);
            data.Add(MODERN_PATTERN_EDITOR_DATA_VERSION);

            int numpat = clips.Count();
            data.AddRange(BitConverter.GetBytes(numpat));

            for (int i = 0; i < numpat; i++)
            {
                MPEClip mpeClip = clips.ElementAt(i);
                data.AddRange(Encoding.ASCII.GetBytes(mpeClip.ClipName));
                data.Add(0);

                CreateMPEClipData(mpeClip, data, PATTERNXP_DATA_VERSION);
            }

            int dataSize = data.Count + sizeof(int);
            data.InsertRange(2, BitConverter.GetBytes(dataSize));

            byte[] dataret = data.ToArray();
            return dataret;
        }
        private static void CreateMPEClipData(MPEClip pat, List<byte> data, byte ver)
        {
            // MPE Beats
            data.AddRange(BitConverter.GetBytes((int)(pat.Clip.Length + ClipControl.BUZZ_TICKS_PER_BEAT - 1) / ClipControl.BUZZ_TICKS_PER_BEAT));
            data.AddRange(BitConverter.GetBytes(pat.RowsPerBeat));

            int count = pat.MPEClipColumns.Count;
            data.AddRange(BitConverter.GetBytes(count));

            for (int i = 0; i < count; i++)
            {
                SaveMPEColumn(pat.MPEClipColumns[i], data, ver);
            }
        }

        public static void SaveMPEColumn(MPEClipColumn mpeColumn, List<byte> data, byte ver)
        {
            data.AddRange(Encoding.ASCII.GetBytes(mpeColumn.Machine.Name));
            data.Add(0);
            data.AddRange(BitConverter.GetBytes(GetParamIndex(mpeColumn.Parameter, mpeColumn.ParamTrack)));
            data.AddRange(BitConverter.GetBytes(mpeColumn.ParamTrack));
            data.Add(Convert.ToByte(mpeColumn.Graphical));

            IEnumerable<ClipEvent> events = mpeColumn.GetEvents(0, int.MaxValue);
            data.AddRange(BitConverter.GetBytes(events.Count()));

            foreach (var e in events)
            {
                data.AddRange(BitConverter.GetBytes((int)(e.Time)));
                data.AddRange(BitConverter.GetBytes(e.Value));
            }

            // MPE Save beats

            foreach (var beatRow in mpeColumn.BeatRowsList)
            {
                data.AddRange(BitConverter.GetBytes(beatRow));
            }
        }
        #endregion

        #region Read Write Utils
        private static string GetStringFromByteArray(byte[] machineClipsData, ref int index)
        {
            string result = "";

            while (true)
            {
                if (machineClipsData[index] == 0)
                    break;

                result += (char)(machineClipsData[index]);
                index++;
            }

            index++;

            return result;
        }

        public static int BCReadInt(byte[] data, ref int index)
        {
            int ret = 0;
            if (index < data.Length)
            {
                ret = BitConverter.ToInt32(data, index);
                index += sizeof(int);
            }

            return ret;
        }

        public static IEnumerable<IParameter> GetInternalParameters(IMachine mac)
        {
            List<IParameter> pars = new List<IParameter>();

            pars.Add(MPEInternalParameter.GetInternalParameter(mac, InternalParameter.MidiNote));
            pars.Add(MPEInternalParameter.GetInternalParameter(mac, InternalParameter.MidiVelocity));
            pars.Add(MPEInternalParameter.GetInternalParameter(mac, InternalParameter.MidiNoteDelay));
            pars.Add(MPEInternalParameter.GetInternalParameter(mac, InternalParameter.MidiNoteCut));
            pars.Add(MPEInternalParameter.GetInternalParameter(mac, InternalParameter.MidiPitchWheel));
            pars.Add(MPEInternalParameter.GetInternalParameter(mac, InternalParameter.MidiCC));

            return pars;
        }

        public static int GetParamIndex(IParameter par, int track)
        {
            int index = 0;
            IMachine machine = par.Group.Machine;
            int group = machine.ParameterGroups.IndexOf(par.Group);

            if (par is MPEInternalParameter)
            {
                index = par.IndexInGroup;
            }
            else if (group == 1)
            {
                index = par.IndexInGroup;
            }
            else if (group == 2)
            {
                int group1Size = machine.ParameterGroups[1].Parameters.Count;
                index = group1Size + par.IndexInGroup;
            }
            return index;
        }

        #endregion

        #region MIDI Import Export
        public static int[] ExportMidiEvents(MPEClip clip)
        {
            const int MidiTimeBase = 960;

            List<Tuple<int, int, int>> midiEvents = new List<Tuple<int, int, int>>();

            // Todo: delay & cut

            foreach (var mpeColumn in clip.MPEClipColumns)
            {
                if (mpeColumn.Parameter.Type == ParameterType.Note)
                {
                    int previousNote = 0;

                    foreach (var e in mpeColumn.GetEvents(0, int.MaxValue).OrderBy(x => x.Time).ToList())
                    {
                        int midiTime = (e.Time / ClipEvent.TimeBase) * MidiTimeBase;
                        if (e.Value != BuzzNote.Off)
                        {
                            int newNote = BuzzNote.ToMIDINote(e.Value);
                            if (previousNote != 0)
                            {
                                // Add note off before new note
                                var msgOff = MIDI.EncodeNoteOff(previousNote);
                                midiEvents.Add(new Tuple<int, int, int>(midiTime, msgOff, previousNote));
                            }
                            var msg = MIDI.EncodeNoteOn(newNote, 70);
                            midiEvents.Add(new Tuple<int, int, int>(midiTime, msg, newNote));
                            previousNote = newNote;
                        }
                        else
                        {
                            var msg = MIDI.EncodeNoteOff(previousNote);
                            midiEvents.Add(new Tuple<int, int, int>(midiTime, msg, previousNote));
                            previousNote = 0;
                        }
                    }
                }
            }

            midiEvents = midiEvents.OrderBy(x => x.Item1).ToList();
            List<int> ordereEvents = new List<int>();
            foreach (var t in midiEvents)
            {
                ordereEvents.Add(t.Item1);
                ordereEvents.Add(t.Item2);
            }
            ordereEvents.Add(-1);
            int[] intArray = ordereEvents.ToArray();
            //byte[] result = new byte[intArray.Length * sizeof(int)];
            //Buffer.BlockCopy(intArray, 0, result, 0, result.Length);

            return intArray;
        }

        internal struct ActiveMidiNote
        {
            internal enum State { note_on_pending, playing, note_off_pending, recording, pw_or_cc };

            internal State state;
            internal int note;
            internal int velocity;
            internal int previousNote;
            internal int delaytime;
            internal int cuttime;
            internal int pw;
            internal int cc;
            internal int rowInBeat;
            internal int RPB;
        };

        // Use pvst param names. ToDo: Make it configurable
        private static string noteParameterName = "note";
        private static string noteVelocityParameterName = "note velocity";
        private static string noteDelayParameterName = "note delay";
        private static string noteCutParameterName = "note cut";
        private static string midiCCParameterName = "MidiCC";
        private static string midiPitchWheelParameterName = "MidiPitchWheel";
        public static bool ImportMidiEvents(MPEClip clip, int [] data)
        {
            int tc = clip.Clip.Machine.TrackCount;
            if (tc < 1) return false;

            List<MPEClipColumn> notecol = new List<MPEClipColumn>();
            List<MPEClipColumn> velcol = new List<MPEClipColumn>();
            List<MPEClipColumn> delaycol = new List<MPEClipColumn>();
            List<MPEClipColumn> cutcol = new List<MPEClipColumn>();

            for (int t = 0; t < tc; t++)
            {
                // try to find these columns
                notecol.Add(GetColumn(clip, noteParameterName, t));
                velcol.Add(GetColumn(clip, noteVelocityParameterName, t));
                delaycol.Add(GetColumn(clip, noteDelayParameterName, t));
                cutcol.Add(GetColumn(clip, noteCutParameterName, t));
            }

            for (int t = 0; t < tc; t++)
            {   
                notecol[t]?.Clear();
                velcol[t]?.Clear();
                delaycol[t]?.Clear();
                cutcol[t]?.Clear();
            }

            Dictionary<Tuple<IMachine, int>, ActiveMidiNote> notes = new Dictionary<Tuple<IMachine, int>, ActiveMidiNote>();
            int index = 0;
            while (true)
            {
                // we read 2 ints per loop
                if (index + 1 >= data.Length)
                    return false;

                int time = data[index++];
                if (time < 0) break;

                time = time * clip.RowsPerBeat / (10 * MPEClipColumn.BUZZ_TICKS_PER_BEAT);

                int mididata = data[index++];

                int row = time / 96;
                int delay = time % 96;

                if (row >= clip.Clip.Length) continue;

                int status = mididata & 0xff;
                int data1 = (mididata >> 8) & 0xff;
                int data2 = (mididata >> 16) & 0xff;

                int clipRow = row * ClipEvent.TimeBase;

                if (status == 0x90)
                {
                    int t = AllocateMidiTrack(clip, row, delay, notes, tc);
                    if (t < 0) continue;

                    if (notecol[t] == null) continue;

                    // note
                    ClipEvent e = new ClipEvent();
                    e.Value = BuzzNote.FromMIDINote(data1);
                    e.Time = clipRow;
                    notecol[t].SetEvents([e], true, false);

                    // velocity
                    e = new ClipEvent();
                    e.Value = data2;
                    e.Time = clipRow;
                    if (velcol[t] != null) velcol[t].SetEvents([e], true, false);

                    // delay
                    e = new ClipEvent();
                    e.Value = delay;
                    e.Time = clipRow;
                    if (delaycol[t] != null && delay != 0) delaycol[t].SetEvents([e], true, false);

                    ActiveMidiNote n = new ActiveMidiNote();
                    n.note = data1;
                    n.velocity = data2;
                    n.state = ActiveMidiNote.State.recording;
                    notes[new Tuple<IMachine, int>(clip.Clip.Machine, t)] = n;
                }
                else if (status == 0x80)
                {
                    int t = FreeMidiTrack(clip, data1, notes);
                    if (t < 0) continue;

                    if (notecol[t] == null) continue;
                    

                    if (notecol[t].GetEvents(clipRow, clipRow + 1).Count() > 0)
                    {
                        ClipEvent e = new ClipEvent();
                        e.Value = delay;
                        e.Time = clipRow;
                        if (cutcol[t] != null && delay != 0) cutcol[t].SetEvents([e], true, false);

                    }
                    else
                    {
                        ClipEvent e = new ClipEvent();
                        e.Value = BuzzNote.Off;
                        e.Time = clipRow;
                        notecol[t].SetEvents([e], true, false);

                        e = new ClipEvent();
                        e.Value = delay;
                        e.Time = clipRow;
                        if (delaycol[t] != null && delay != 0) delaycol[t].SetEvents([e], true, false);
                    }
                }
            }

            return false;
        }

        public static MPEClipColumn? GetColumn(MPEClip mpeClip, string name, int t)
        {
            foreach (var c in mpeClip.MPEClipColumns)
            {
                if (c.ParamTrack == t && c.Parameter.Name.ToLower() == name.ToLower())
                    return c;
            }
            return null;
        }

        private static int AllocateMidiTrack(MPEClip mpeClip, int row, int delay, Dictionary<Tuple<IMachine, int>, ActiveMidiNote> notes, int tc)
        {
            int clipTime = row * ClipEvent.TimeBase;
            for (int t = 0; t < tc; t++)
            {   
                foreach (var i in notes)
                {
                    if (i.Key.Item2 == t)
                    {
                        break;
                    }
                    else if (i.Key == notes.Last().Key)
                    {
                        var pwcol = GetColumn(mpeClip, midiPitchWheelParameterName, t);
                        if (pwcol != null && pwcol.GetEvents(clipTime, clipTime + 1).Count() > 0) continue;
                        var cccol = GetColumn(mpeClip, midiCCParameterName, t);
                        if (cccol != null && cccol.GetEvents(clipTime, clipTime + 1).Count() > 0) continue;

                        var notecol = GetColumn(mpeClip, noteParameterName, t);
                        if (notecol != null)
                        {
                            if (notecol.GetEvents(clipTime, clipTime + 1).Count() > 0)
                            {
                                if (delay == 0 && notecol.GetEvents(clipTime, clipTime + 1).First().Value == BuzzNote.Off)
                                {
                                    // note off can be overwritten except when it's delayed or cut

                                    var delaycol = GetColumn(mpeClip, noteDelayParameterName, t);
                                    var cutcol = GetColumn(mpeClip, noteCutParameterName, t);

                                    bool dval = delaycol != null && delaycol.GetEvents(clipTime, clipTime + 1).Count() > 0;
                                    bool cval = cutcol != null && cutcol.GetEvents(clipTime, clipTime + 1).Count() > 0;

                                    if (!dval && !cval) return t;
                                }
                            }
                            else
                            {
                                return t;
                            }
                        }
                    }

                }
            }

            return tc > 0 ? 0 : -1;
        }

        private static int FreeMidiTrack(MPEClip mpeClip, int note, Dictionary<Tuple<IMachine, int>, ActiveMidiNote> notes)
        {
            foreach (var i in notes.Keys.ToArray())
            {
                if (i.Item1 == mpeClip.Clip.Machine && notes[i].note == note)
                {
                    int t = i.Item2;
                    notes.Remove(i);
                    return t;
                }
            }

            return -1;
        }

        #endregion

        #region Clip notes (fork: neo-buzz Session View 0.7.1)

        // A clip's MIDI note lanes (the MIDI Note, Velocity, Note Delay and Note Cut columns
        // of each track of the clip's own machine) as notes, and back: the Session View moves
        // a clip's recorded notes into the Clip View and back with these. Notes are int
        // triples (time in 1/240 tick, note-on message, duration); the layout on rows is
        // BuzzGUI.Common.TrackerNotes'. Notes the lanes can hold: MIDI 0-119 (BuzzNote's range).

        public const int MaxClipNote = 119;

        static readonly InternalParameter[] NoteLanes =
            { InternalParameter.MidiNote, InternalParameter.MidiVelocity, InternalParameter.MidiNoteDelay, InternalParameter.MidiNoteCut };

        static bool IsLane(MPEClipColumn c, IMachine machine, InternalParameter type) =>
            c.Machine == machine && c.Parameter is MPEInternalParameter p && p.InternalType == type;

        static bool IsAnyLane(MPEClipColumn c, IMachine machine) =>
            c.Machine == machine && c.Parameter is MPEInternalParameter p && NoteLanes.Contains(p.InternalType);

        static MPEClipColumn Lane(MPEClip clip, InternalParameter type, int track) =>
            clip.MPEClipColumns.FirstOrDefault(c => IsLane(c, clip.Clip.Machine, type) && c.ParamTrack == track);

        static int ValueAt(MPEClipColumn column, int time) =>
            column == null ? 0 : column.GetEvents(time, time + 1).Select(e => e.Value).FirstOrDefault();

        static int BeatsOf(MPEClip clip) =>
            Math.Max(1, (clip.Clip.Length + MPEClipColumn.BUZZ_TICKS_PER_BEAT - 1) / MPEClipColumn.BUZZ_TICKS_PER_BEAT);

        /// <summary>Rows in each beat of a lane (its own subdivisions, else the clip's rows per beat).</summary>
        static List<int> BeatRows(MPEClip clip, MPEClipColumn lane)
        {
            int beats = BeatsOf(clip);
            var list = new List<int>(beats);
            for (int b = 0; b < beats; b++)
                list.Add(lane != null && b < lane.BeatRowsList.Count ? lane.BeatRowsList[b] : Math.Max(1, clip.RowsPerBeat));
            return list;
        }

        internal static List<MPEClipColumn> LaneColumns(MPEClip clip) =>
            clip.MPEClipColumns.Where(c => IsAnyLane(c, clip.Clip.Machine)).ToList();

        #endregion

        #region Other


        #endregion

        #region Registry

        static readonly string regpath = Global.RegistryRoot;
        internal static readonly string regPathBuzzSettings = "Settings";
        internal static readonly string regPathBuzzMachineDefaultPE = "DefaultPE";
        internal static readonly string regDefaultPE = "DefaultPE";
        public static void WriteRegistry<T>(string key, T x, string path)
        {
            try
            {
                var regkey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(regpath + "\\" + path);
                if (regkey == null) return;
                regkey.SetValue(key, x.ToString());
            }
            catch (Exception) { }

        }

        #endregion


        public static IDictionary<string, string> MachineNameMap { get; internal set; }

        public static string GetMachineCurrentName(string machineName)
        {
            if (MachineNameMap != null)
            {
                if (MachineNameMap.ContainsKey(machineName))
                    return MachineNameMap[machineName];
            }

            return machineName;
        }

        internal static IList<IClipEditorColumn> GetClipEditorColumnEvents(MPEClip mpeClip, int tbegin, int tend)
        {
            List<IClipEditorColumn> pecList = new List<IClipEditorColumn>();

            foreach (var column in mpeClip.MPEClipColumns)
            {
                ClipEditorColumn pec = new ClipEditorColumn();
                if (column != null)
                {
                    pec.Parameter = column.Parameter;
                    pec.Events = column.GetEvents(tbegin, tend).ToList();
                    pecList.Add(pec);
                }
            }
            return pecList;
        }
    }

    internal class ClipEditorColumn : IClipEditorColumn
    {
        public IParameter Parameter { get; set; }

        public IEnumerable<ClipEvent> Events { get; set; }
    }

}
