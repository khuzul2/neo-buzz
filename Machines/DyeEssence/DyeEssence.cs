#nullable disable
// Dye Essence (docs/specs/dye-essence.md): the hybrid wavetable synth, a Neo-family machine. 16
// tracks of Note and Velocity, plus MIDI. Each engine slot can play a user wavetable (from a WAV
// file) kept in the machine state, so songs and presets carry it. DSP in Engine/ (NoteBank, the
// lanes), parameters generated from Params.def, the editor in DyeEssenceEditor.cs.

using System;
using System.Collections.Generic;
using System.IO;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;
using NeoBuzz.Wave;

namespace NeoBuzz.DyeEssence
{
    /// <summary>Saved with the song: each slot's user wavetable (32-bit float samples, base64).</summary>
    public class DyeEssenceState
    {
        public string TableAName { get; set; } = "";
        public int TableAFrameSize { get; set; }
        public string TableAData { get; set; } = "";
        public string TableBName { get; set; } = "";
        public int TableBFrameSize { get; set; }
        public string TableBData { get; set; } = "";
        public string TableCName { get; set; } = "";
        public int TableCFrameSize { get; set; }
        public string TableCData { get; set; } = "";

        internal (string Name, int FrameSize, string Data) Get(int slot) => slot switch
        {
            0 => (TableAName, TableAFrameSize, TableAData),
            1 => (TableBName, TableBFrameSize, TableBData),
            _ => (TableCName, TableCFrameSize, TableCData),
        };

        internal DyeEssenceState With(int slot, string name, int frameSize, string data)
        {
            var s = (DyeEssenceState)MemberwiseClone();
            switch (slot)
            {
                case 0: s.TableAName = name; s.TableAFrameSize = frameSize; s.TableAData = data; break;
                case 1: s.TableBName = name; s.TableBFrameSize = frameSize; s.TableBData = data; break;
                default: s.TableCName = name; s.TableCFrameSize = frameSize; s.TableCData = data; break;
            }
            return s;
        }
    }

    [MachineDecl(Name = "Dye Essence", ShortName = "Dye", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class DyeEssenceMachine : NeoSynthBase, IBuzzMachine
    {
        internal static readonly string[] SlotNames = { "A", "B", "C" };
        static readonly int[] TableParam = { P.ATable, P.BTable, P.CTable };

        readonly float[] left = new float[DyeEngine.MaxBlock], right = new float[DyeEngine.MaxBlock];
        int rate;
        DyeEssenceState state = new DyeEssenceState();

        /// <summary>The longest single cycle a file may hold (longer ones are sounds, not cycles).</summary>
        internal const int MaxSingleCycle = 8192;

        public DyeEssenceMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            Engine = new DyeEngine(Values);
            // Build the built-in tables in the background now, not when the audio thread first asks.
            for (int i = 0; i < TableBank.Count; i++) TableBank.Get(i);
        }

        internal DyeEngine Engine { get; }

        /// <summary>Slot <paramref name="slot"/>'s user table's name ("" when none is loaded).</summary>
        internal string UserTableName(int slot) => state.Get(slot).Name;

        /// <summary>The "User" choice of a slot's table parameter.</summary>
        internal static int UserChoice => TableBank.Count;

        internal static int TableParameter(int slot) => TableParam[slot];

        public DyeEssenceState MachineState
        {
            get => state;
            set
            {
                state = value ?? new DyeEssenceState();
                for (int slot = 0; slot < SlotNames.Length; slot++)
                {
                    var (name, frameSize, data) = state.Get(slot);
                    try
                    {
                        if (!string.IsNullOrEmpty(data) && data.Length >= 64)
                        {
                            var bytes = Convert.FromBase64String(data);
                            var samples = new float[bytes.Length / 4];
                            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
                            Engine.SetUserTable(slot, new TableArena(WavReader.ToTable(name, samples, frameSize)), name);
                        }
                        else Engine.SetUserTable(slot, null, "");
                    }
                    catch (Exception)
                    {
                        // A damaged table loads as "no user table" rather than failing the song.
                        Engine.SetUserTable(slot, null, "");
                    }
                }
            }
        }

        /// <summary>
        /// Agent server (mcp-server.md A3): true while the built-in tables are still being built in
        /// the background. Offline renders wait for it, so they don't start silent.
        /// </summary>
        public bool Busy => Engine.TablesBuilding;

        /// <summary>
        /// Agent server (mcp-server.md A3, machine_data_import): "wavetable" loads a WAV as a slot's
        /// user table (item 0, 1, 2: slot A, B, C) and sets that slot's table to User.
        /// </summary>
        public string ImportData(string kind, string path, int item, Action<int, int> setParameter)
        {
            if (kind != "wavetable") throw new ArgumentException($"Dye Essence doesn't import \"{kind}\"; it takes wavetable.");
            int slot = item >= 0 && item < SlotNames.Length ? item : 0;
            LoadUserTable(slot, path);
            setParameter?.Invoke(TableParam[slot], UserChoice);
            return $"Loaded the wavetable \"{UserTableName(slot)}\" as slot {SlotNames[slot]}'s User table, and set {SlotNames[slot]} table to User.";
        }

        /// <summary>Loads a WAV file as a slot's user table (UI thread). Throws with a readable message on bad files.</summary>
        internal void LoadUserTable(int slot, string path)
        {
            var (samples, frameSize) = WavReader.Read(File.ReadAllBytes(path));
            if (samples == null || samples.Length < 16) throw new InvalidDataException("the file holds no audio");
            int size = frameSize > 0 ? frameSize : samples.Length >= 2 * Wavetable.FrameSize && samples.Length % Wavetable.FrameSize == 0 ? Wavetable.FrameSize : samples.Length;
            if (size > MaxSingleCycle)
                throw new InvalidDataException($"it reads as one cycle of {samples.Length} samples; wavetables are frames of 2048 samples (or say their frame size in a clm chunk) or one short cycle (up to {MaxSingleCycle})");
            int keep = Math.Min(samples.Length, size * Wavetable.MaxFrames);
            var kept = new float[keep];
            Array.Copy(samples, kept, keep);
            string name = Path.GetFileNameWithoutExtension(path);
            var table = new TableArena(WavReader.ToTable(name, kept, frameSize));
            var bytes = new byte[keep * 4];
            Buffer.BlockCopy(kept, 0, bytes, 0, bytes.Length);
            state = state.With(slot, name, frameSize, Convert.ToBase64String(bytes));
            Engine.SetUserTable(slot, table, name);
        }

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            Engine.Tempo = BeatsPerMinute > 0 ? BeatsPerMinute : 120;
            BeginBlock(Engine);
            n = Math.Min(n, DyeEngine.MaxBlock);
            if (!Engine.Process(left, right, n)) return false;
            return Join(left, right, output, n);
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Dye Essence...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoAbout.Show(
                        "Dye Essence — hybrid wavetable synth: three engine slots, each a morphing wavetable with " +
                        "1–16 unison voices and two warps (bend, squeeze, mirror, asym, sync, window sync, FM, PM, " +
                        "AM, ring), a sub and a noise, two filters per note (ladder, state-variable, comb) from serial " +
                        "to parallel, oversampled 2× or 4×. Ten generated tables plus your own (WAV files of " +
                        "2048-sample frames, a clm chunk's frame size, or a single cycle).\n\nPart of neo-buzz " +
                        "(docs/specs/dye-essence.md).", "Dye Essence"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new DyeEssenceEditor();
    }
#endif
}
