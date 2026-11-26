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

            // The events are the store's (CU7): a song's own are moved there on open by ClipMigration, which reads the same bytes. They are skipped here.
            int count = BCReadInt(machineClipsData, ref index);
            index += count * 2 * sizeof(int);

            if (parameter != null)
                pat.MPEClipColumns.Add(mpeColumn);
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

            // The events are the store's (CU7); a song's own are moved there on open by ClipMigration, which reads the same bytes. They are skipped here.
            int count = BCReadInt(machineClipsData, ref index);
            index += count * 2 * sizeof(int);

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

            // CU7: the blob holds the shape of the view (columns, their order, rows in each beat) and no events: they are lanes in the store.
            // The count stays in the layout, as 0, so a reader of the blob (ClipMigration, this loader) reads it as it always did.
            data.AddRange(BitConverter.GetBytes(0));

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

        #region Other

        /// <summary>The clip's column of a parameter, by its name, on a track.</summary>
        public static MPEClipColumn? GetColumn(MPEClip mpeClip, string name, int t)
        {
            foreach (var c in mpeClip.MPEClipColumns)
            {
                if (c.ParamTrack == t && c.Parameter.Name.ToLower() == name.ToLower())
                    return c;
            }
            return null;
        }

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
