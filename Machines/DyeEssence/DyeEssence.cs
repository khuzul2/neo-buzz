#nullable disable
// Dye Essence (docs/specs/dye-essence.md): the hybrid wavetable synth, a Neo-family machine. 16
// tracks of Note and Velocity, plus MIDI. Each engine slot can play a user wavetable (from a WAV
// file), and each LFO a drawn shape, both kept in the machine state, so songs and presets carry
// them. DSP in Engine/ (NoteBank, the lanes), parameters generated from Params.def, the editor in
// DyeEssenceEditor.cs (its pages) and the other *Editor.cs files.

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
    /// <summary>
    /// Saved with the song: each slot's user wavetable (32-bit float samples, base64) and each LFO's drawn
    /// shape (DrawnShape's text; empty: the default). Songs from before a field load it empty.
    /// </summary>
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
        public string Lfo1Shape { get; set; } = "";
        public string Lfo2Shape { get; set; } = "";
        public string Lfo3Shape { get; set; } = "";
        public string Lfo4Shape { get; set; } = "";

        internal string Shape(int lfo) => lfo switch { 0 => Lfo1Shape, 1 => Lfo2Shape, 2 => Lfo3Shape, _ => Lfo4Shape } ?? "";

        internal DyeEssenceState WithShape(int lfo, string text)
        {
            var s = (DyeEssenceState)MemberwiseClone();
            switch (lfo)
            {
                case 0: s.Lfo1Shape = text; break;
                case 1: s.Lfo2Shape = text; break;
                case 2: s.Lfo3Shape = text; break;
                default: s.Lfo4Shape = text; break;
            }
            return s;
        }

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
        internal static readonly int[] LfoShapeParam = { P.L1Shape, P.L2Shape, P.L3Shape, P.L4Shape };
        internal const int Lfos = 4;

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
                for (int l = 0; l < Lfos; l++) Engine.SetLfoShape(l, DrawnShape.Parse(state.Shape(l)));
                ShapesVersion++;
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

        /// <summary>LFO <paramref name="lfo"/>'s drawn shape (the default when none was drawn).</summary>
        internal DrawnShape LfoShape(int lfo) => Engine.LfoShape(lfo);

        /// <summary>
        /// Draws LFO <paramref name="lfo"/>'s shape (UI thread; the audio thread takes it at its next block). Null or the
        /// default shape clears it from the state.
        /// </summary>
        internal void SetLfoShape(int lfo, DrawnShape shape)
        {
            shape ??= DrawnShape.Default;
            state = state.WithShape(lfo, shape.SameAs(DrawnShape.Default) ? "" : shape.ToString());
            Engine.SetLfoShape(lfo, shape);
            ShapesVersion++;
        }

        /// <summary>Counts changes of the drawn shapes (drawn, a song or preset loaded, undone): the editor redraws when it moves.</summary>
        internal int ShapesVersion { get; private set; }

        /// <summary>
        /// Agent server (mcp-server.md A3, machine_data_import): "wavetable" loads a WAV as a slot's user table
        /// (item 0, 1, 2: slot A, B, C) and sets that slot's table to User; "lfo_shape" reads a drawn
        /// shape from a text file (item 0–3: LFO 1–4) and sets that LFO's shape to Drawn.
        /// </summary>
        public string ImportData(string kind, string path, int item, Action<int, int> setParameter)
        {
            if (kind == "lfo_shape")
            {
                if (item < 0 || item >= Lfos) throw new ArgumentException($"Dye Essence has {Lfos} LFOs; item {item} doesn't exist (0–{Lfos - 1}: LFO 1–{Lfos})");
                int lfo = item;
                if (!DrawnShape.TryParse(File.ReadAllText(path), out var shape, out string error)) throw new InvalidDataException(error);
                SetLfoShape(lfo, shape);
                setParameter?.Invoke(LfoShapeParam[lfo], (int)WaveShape.Drawn);
                return $"Drew LFO {lfo + 1}'s shape ({shape.Count} points: {shape}) and set LFO{lfo + 1} shape to Drawn.";
            }
            if (kind != "wavetable") throw new ArgumentException($"Dye Essence doesn't import \"{kind}\"; it takes wavetable and lfo_shape.");
            if (item < 0 || item >= SlotNames.Length) throw new ArgumentException($"Dye Essence has {SlotNames.Length} slots; item {item} doesn't exist (0 A, 1 B, 2 C)");
            int slot = item;
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
                        "Dye Essence — hybrid wavetable synth: three engine slots, each a morphing wavetable or an " +
                        "analog oscillator with 1–16 unison voices and two warps (bend, squeeze, mirror, asym, sync, " +
                        "window sync, FM, PM, AM, ring), a sub and a noise, two filters per note (ladder, state-variable, " +
                        "comb) from serial to parallel, oversampled 2× or 4×; two modulation envelopes, four LFOs " +
                        "(built-in or drawn shapes), four macros and sixteen routes. Ten generated tables plus your own " +
                        "(WAV files of 2048-sample frames, a clm chunk's frame size, or a single cycle).\n\nPart of " +
                        "neo-buzz (docs/specs/dye-essence.md).", "Dye Essence"),
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
