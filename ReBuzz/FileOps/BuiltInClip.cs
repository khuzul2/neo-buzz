using BuzzGUI.Interfaces;
using ReBuzz.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReBuzz.FileOps
{
    /// <summary>
    /// Fork (docs/specs/mac-engine.md M2): a clip's contents as Buzz's built-in clip editor
    /// keeps them, the per-clip part of a BMX file's PATT section: an amp/pan column for each
    /// input, then a row of values per tick for the global parameters and for each track's.
    ///
    /// On load they go to the default clip editor, converted to its PXP format (<see cref="ToPXP"/>).
    /// A host without one (rebuzz-headless off Windows: the Modern Pattern Editor is WPF) keeps them
    /// on the clip instead (<see cref="ClipCore.BuiltInData"/>), so the song saves back with
    /// them: into PATT for BMX (XTAP still gives the clips to the built-in editor), and as the
    /// clip's BuiltInClip element for BMXML. A host with the editor converts them as it does
    /// for any old song.
    /// </summary>
    internal sealed class BuiltInClip
    {
        internal sealed class Input
        {
            public MachineCore Source;
            public ushort[] AmpPan;                 // amp and pan for each row
        }

        public List<Input> Inputs = new List<Input>();
        public int[][] Global = Array.Empty<int[]>();               // [row][parameter]
        public int[][][] Tracks = Array.Empty<int[][]>();           // [track][row][parameter]

        static List<ParameterCore> Group(MachineCore machine, int group) =>
            group < machine.ParameterGroupsList.Count ? machine.ParameterGroupsList[group].ParametersList : new List<ParameterCore>();

        /// <summary>A group's rows, read as BMX stores them: each parameter's value by its type's size.</summary>
        internal static int[][] ReadRows(Func<int, int> read, MachineCore machine, int group, int rows)
        {
            var parameters = Group(machine, group);
            var result = new int[rows][];
            for (int i = 0; i < rows; i++)
            {
                result[i] = new int[parameters.Count];
                for (int j = 0; j < parameters.Count; j++)
                    result[i][j] = read(parameters[j].GetTypeSize());
            }
            return result;
        }

        /// <summary>The cells of the connections' amp and pan that hold a value, by the name of the source machine (null: none).</summary>
        internal Dictionary<string, int> InputCells()
        {
            Dictionary<string, int> cells = null;
            foreach (var input in Inputs)
            {
                int n = input.AmpPan.Count(v => v != 65535);
                if (n > 0)
                {
                    cells ??= new Dictionary<string, int>();
                    string name = input.Source?.Name ?? "?";
                    cells[name] = cells.TryGetValue(name, out int before) ? before + n : n;
                }
            }
            return cells;
        }

        /// <summary>The value to write at a row and parameter: the kept one, or no value.</summary>
        internal int ValueAt(int group, int track, int row, int index, ParameterCore parameter)
        {
            var rows = group == 1 ? Global : track < Tracks.Length ? Tracks[track] : null;
            return rows != null && row < rows.Length && index < rows[row].Length ? rows[row][index] : parameter.NoValue;
        }

        /// <summary>An input's amp/pan at a row (65535: no value).</summary>
        internal ushort AmpPanAt(MachineCore source, int i)
        {
            var ampPan = Inputs.FirstOrDefault(input => input.Source == source)?.AmpPan;
            return ampPan != null && i < ampPan.Length ? ampPan[i] : (ushort)65535;
        }

        /// <summary>The value rows for a clip of <paramref name="rows"/> rows and the machine's tracks, as BMX writes them.</summary>
        internal void WriteValues(Action<int, int> write, MachineCore machine, int rows, int tracks)
        {
            void WriteGroup(int group, int track)
            {
                var parameters = Group(machine, group);
                for (int i = 0; i < rows; i++)
                    for (int j = 0; j < parameters.Count; j++)
                    {
                        int size = parameters[j].GetTypeSize();
                        if (size > 0)
                            write(size, ValueAt(group, track, i, j, parameters[j]));
                    }
            }

            WriteGroup(1, 0);
            for (int l = 0; l < tracks; l++)
                WriteGroup(2, l);
        }

        static void Write(Stream s, int size, int value)
        {
            s.WriteByte((byte)value);
            if (size == 2)
                s.WriteByte((byte)(value >> 8));
        }

        internal byte[] PackValues(MachineCore machine, int rows, int tracks)
        {
            using var ms = new MemoryStream();
            WriteValues((size, value) => Write(ms, size, value), machine, rows, tracks);
            return ms.ToArray();
        }

        internal static BuiltInClip UnpackValues(MachineCore machine, int rows, int tracks, byte[] values)
        {
            int pos = 0;
            int Read(int size)
            {
                int v = 0;
                if (size >= 1 && pos < values.Length) v = values[pos];
                if (size == 2 && pos + 1 < values.Length) v |= values[pos + 1] << 8;
                pos += size;
                return v;
            }

            var data = new BuiltInClip { Global = ReadRows(Read, machine, 1, rows), Tracks = new int[tracks][][] };
            for (int l = 0; l < tracks; l++)
                data.Tracks[l] = ReadRows(Read, machine, 2, rows);
            return data;
        }

        internal static ushort[] UnpackAmpPan(byte[] bytes)
        {
            var ampPan = new ushort[(bytes?.Length ?? 0) / 2];
            for (int i = 0; i < ampPan.Length; i++)
                ampPan[i] = (ushort)(bytes[2 * i] | bytes[2 * i + 1] << 8);
            return ampPan;
        }

        internal static byte[] PackAmpPan(BuiltInClip data, MachineCore source, int rows)
        {
            var bytes = new byte[rows * 4];
            for (int i = 0; i < rows * 2; i++)
            {
                ushort v = data != null ? data.AmpPanAt(source, i) : (ushort)65535;
                bytes[2 * i] = (byte)v;
                bytes[2 * i + 1] = (byte)(v >> 8);
            }
            return bytes;
        }

        /// <summary>
        /// The clips as the default clip editor's data (PXP version 3), as upstream's BMX loader
        /// converted them: a column per parameter, an event per row with a value. Input columns aren't
        /// in the PXP format, so they don't carry over.
        /// </summary>
        internal static byte[] ToPXP(MachineCore machine, IList<(string Name, BuiltInClip Data)> clips)
        {
            var ms = new MemoryStream();
            NblCoreSections.WriteByte(ms, 3);                                   // PXP version
            NblCoreSections.WriteInt(ms, clips.Count);                       // clip count
            int globals = Group(machine, 1).Count;
            foreach (var (name, data) in clips)
            {
                NblCoreSections.WriteString(ms, name, System.Text.Encoding.Latin1);                          // clip name
                NblCoreSections.WriteInt(ms, 4);                                // rows per beat
                NblCoreSections.WriteInt(ms, globals + Group(machine, 2).Count * data.Tracks.Length);   // columns (global + tracks)
                WriteColumns(ms, machine, 1, 0, data.Global, 0);
                for (int l = 0; l < data.Tracks.Length; l++)
                    WriteColumns(ms, machine, 2, l, data.Tracks[l], globals);
            }
            return ms.ToArray();
        }

        static void WriteColumns(MemoryStream ms, MachineCore machine, int group, int track, int[][] rows, int index)
        {
            var parameters = Group(machine, group);
            for (int j = 0; j < parameters.Count; j++)
            {
                NblCoreSections.WriteString(ms, machine.Name, System.Text.Encoding.Latin1);                  // name
                NblCoreSections.WriteInt(ms, index);                            // parameter index
                NblCoreSections.WriteInt(ms, track);                            // track
                NblCoreSections.WriteByte(ms, 0);                               // bool graphical
                var events = Enumerable.Range(0, rows.Length).Where(i => j < rows[i].Length && rows[i][j] != parameters[j].NoValue).ToArray();
                NblCoreSections.WriteInt(ms, events.Length);                    // number of events in column
                foreach (int i in events)
                {
                    NblCoreSections.WriteInt(ms, i);
                    NblCoreSections.WriteInt(ms, rows[i][j]);
                }
                index++;
            }
        }
    }
}
