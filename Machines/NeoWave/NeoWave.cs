#nullable disable
// Neo Wave (docs/specs/machines.md §5): the wavetable machine. 16 tracks of Note and Velocity,
// plus MIDI. A user wavetable (from a WAV file) is kept in the machine state, so songs and presets
// carry it. DSP in Engine/, parameters generated from Params.def, editor in NeoWaveEditor.cs.

using System;
using System.Collections.Generic;
using System.IO;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace NeoBuzz.Wave
{
    /// <summary>Saved with the song: the user wavetable (32-bit float samples, base64).</summary>
    public class NeoWaveState
    {
        public string UserTableName { get; set; } = "";
        public int UserFrameSize { get; set; }
        public string UserTableData { get; set; } = "";
    }

    [MachineDecl(Name = "Neo Wave", ShortName = "Wave", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class NeoWaveMachine : NeoSynthBase, IBuzzMachine
    {
        readonly float[] left = new float[WaveEngine.MaxBlock], right = new float[WaveEngine.MaxBlock];
        int rate;
        NeoWaveState state = new NeoWaveState();

        /// <summary>The longest single cycle a file may hold (longer ones are sounds, not cycles).</summary>
        internal const int MaxSingleCycle = 8192;

        public NeoWaveMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            Engine = new WaveEngine(Values);
            // Build the built-in tables in the background now, not when the audio thread first asks.
            for (int i = 0; i < Tables.Names.Length; i++) Tables.Get(i);
        }

        internal WaveEngine Engine { get; }

        /// <summary>The user table's name ("" when none is loaded).</summary>
        internal string UserTableName => state.UserTableName;

        public NeoWaveState MachineState
        {
            get => state;
            set
            {
                state = value ?? new NeoWaveState();
                try
                {
                    if (!string.IsNullOrEmpty(state.UserTableData) && state.UserTableData.Length >= 64)
                    {
                        var bytes = Convert.FromBase64String(state.UserTableData);
                        var samples = new float[bytes.Length / 4];
                        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
                        Engine.SetUserTable(WavReader.ToTable(state.UserTableName, samples, state.UserFrameSize));
                    }
                    else Engine.SetUserTable(null);
                }
                catch (Exception)
                {
                    // A damaged state loads as "no user table" rather than failing the song.
                    Engine.SetUserTable(null);
                }
            }
        }

        /// <summary>
        /// Agent server (mcp-server.md A3): true while the built-in tables are still being built in
        /// the background. Offline renders wait for it, so they don't start silent.
        /// </summary>
        public bool Busy => Engine.TablesBuilding;

        /// <summary>
        /// Agent server (mcp-server.md A3, machine_data_import): "wavetable" loads a WAV as the user
        /// table. Pick it with A table or B table = User.
        /// </summary>
        public string ImportData(string kind, string path, int item, Action<int, int> setParameter)
        {
            if (kind != "wavetable") throw new ArgumentException($"Neo Wave doesn't import \"{kind}\"; it takes wavetable.");
            LoadUserTable(path);
            return $"Loaded the wavetable \"{state.UserTableName}\" as the User table; set A table or B table to User to play it.";
        }

        /// <summary>Loads a WAV file as the user table (UI thread). Throws with a readable message on bad files.</summary>
        internal void LoadUserTable(string path)
        {
            var (samples, frameSize) = WavReader.Read(File.ReadAllBytes(path));
            if (samples == null || samples.Length < 16) throw new InvalidDataException("the file holds no audio");
            int size = frameSize > 0 ? frameSize : samples.Length >= 2 * Wavetable.FrameSize && samples.Length % Wavetable.FrameSize == 0 ? Wavetable.FrameSize : samples.Length;
            if (size > MaxSingleCycle)
                throw new InvalidDataException($"it reads as one cycle of {samples.Length} samples; wavetables are frames of 2048 samples (or say their frame size in a clm chunk) or one short cycle (up to {MaxSingleCycle})");
            int keep = Math.Min(samples.Length, size * Wavetable.MaxFrames);
            var kept = new float[keep];
            Array.Copy(samples, kept, keep);
            var table = WavReader.ToTable(Path.GetFileNameWithoutExtension(path), kept, frameSize);
            var bytes = new byte[keep * 4];
            Buffer.BlockCopy(kept, 0, bytes, 0, bytes.Length);
            state = new NeoWaveState
            {
                UserTableName = Path.GetFileNameWithoutExtension(path),
                UserFrameSize = frameSize,
                UserTableData = Convert.ToBase64String(bytes),
            };
            Engine.SetUserTable(table);
        }

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            Engine.Tempo = BeatsPerMinute;
            BeginBlock(Engine);
            n = Math.Min(n, WaveEngine.MaxBlock);
            if (!Engine.Process(left, right, n)) return false;
            return Join(left, right, output, n);
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Wave...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Wave — wavetable synth: two morphing wavetable oscillators with warp modes, a sub " +
                        "oscillator, ladder and state-variable filters, three envelopes, two LFOs and an " +
                        "8-slot matrix, oversampled 2×. Ten generated tables plus your own (WAV, Serum-style " +
                        "2048-sample frames or a single cycle).\n\nPart of neo-buzz (docs/specs/machines.md).", "Neo Wave"),
                },
            },
        };
    }

#if WINDOWS   // fork (mac-engine.md M2): the editor is WPF; portable builds have none
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoWaveEditor();
    }
#endif
}
