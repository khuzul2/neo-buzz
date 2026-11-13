#nullable disable
// Neo Learn Wavetable (docs/specs/neo-learn.md): a teaching wavetable synth. One Dye Essence patch (Machines/DyeEssence/Engine, hosted by
// NeoLearn/Engine/LearnHost.cs) driven by 12 controls through a macro map (WavetableMap). 16 tracks of Note and Velocity, plus MIDI.
// Parameters are generated from Params.def, with each control's Explain text; the editor is NeoLearnWavetableEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System.IO;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Learn;
using NeoBuzz.Machine;
using NeoBuzz.Wave;

namespace NeoBuzz.LearnWt
{
    /// <summary>What Neo Learn Wavetable keeps with the song: the user's table (a WAV's samples as 32-bit floats, base 64).</summary>
    public class LearnTableState
    {
        public string Name { get; set; } = "";
        public int FrameSize { get; set; }
        public string Data { get; set; } = "";
    }

    [MachineDecl(Name = "Neo Learn Wavetable", ShortName = "Learn WT", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class NeoLearnWavetableMachine : NeoSynthBase, IBuzzMachine
    {
        readonly float[] left = new float[DyeEngine.MaxBlock], right = new float[DyeEngine.MaxBlock];
        int rate;

        public NeoLearnWavetableMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            Host = new LearnHost(new WavetableMap(Params.Table), Values);
            // Build the built-in tables in the background now, not when the audio thread first asks.
            for (int i = 0; i < TableBank.Count; i++) TableBank.Get(i);
        }

        internal LearnHost Host { get; }

        LearnTableState state = new LearnTableState();

        /// <summary>The user's own table (the "User" choice of Wavetable): its name, frame size and samples, kept with the song.</summary>
        public LearnTableState MachineState
        {
            get => state;
            set
            {
                state = value ?? new LearnTableState();
                try
                {
                    if (!string.IsNullOrEmpty(state.Data) && state.Data.Length >= 64)
                    {
                        var bytes = Convert.FromBase64String(state.Data);
                        var samples = new float[bytes.Length / 4];
                        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
                        Host.Engine.SetUserTable(0, new TableArena(WavReader.ToTable(state.Name, samples, state.FrameSize)), state.Name);
                    }
                    else Host.Engine.SetUserTable(0, null, "");
                }
                catch (Exception)
                {
                    // A damaged table loads as "no user table" rather than failing the song.
                    Host.Engine.SetUserTable(0, null, "");
                }
            }
        }

        /// <summary>The user table's name ("" when none is loaded).</summary>
        internal string UserTableName => state.Name;

        /// <summary>The longest single cycle a file may hold (longer ones are sounds, not cycles).</summary>
        internal const int MaxSingleCycle = 8192;

        /// <summary>
        /// Agent server (mcp-server.md A3, machine_data_import): "wavetable" loads a WAV as the machine's user table (item 0) and sets
        /// Wavetable to User.
        /// </summary>
        public string ImportData(string kind, string path, int item, Action<int, int> setParameter)
        {
            if (kind != "wavetable") throw new ArgumentException($"Neo Learn Wavetable doesn't import \"{kind}\"; it takes wavetable.");
            if (item != 0) throw new ArgumentException("Neo Learn Wavetable has one user table; item " + item + " doesn't exist (0)");
            LoadUserTable(path);
            setParameter?.Invoke(P.TableSelect, WavetableMap.UserTable);
            return $"Loaded the wavetable \"{UserTableName}\" as the User table, and set Wavetable to User.";
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
            string name = Path.GetFileNameWithoutExtension(path);
            var table = new TableArena(WavReader.ToTable(name, kept, frameSize));
            var bytes = new byte[keep * 4];
            Buffer.BlockCopy(kept, 0, bytes, 0, bytes.Length);
            state = new LearnTableState { Name = name, FrameSize = frameSize, Data = Convert.ToBase64String(bytes) };
            Host.Engine.SetUserTable(0, table, name);
        }

        /// <summary>Agent server (mcp-server.md A3): true while the built-in tables are still being built. Offline renders wait for it.</summary>
        public bool Busy => Host.Engine.TablesBuilding;

        /// <summary>Agent server (machine_readings): what the output measures, and the notes sounding.</summary>
        public IReadOnlyDictionary<string, double> Readings => Host.Readings();

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Host.SetSampleRate(rate);
            }
            Host.Engine.Tempo = BeatsPerMinute;
            BeginBlock(Host.Engine);
            n = Math.Min(n, DyeEngine.MaxBlock);
            if (!Host.Process(left, right, n)) return false;
            return Join(left, right, output, n);
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Learn Wavetable...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoAbout.Show(
                        "Neo Learn Wavetable \u2014 a teaching wavetable synth: ten built-in tables (or your own WAV) read at a frame " +
                        "position that an LFO can sweep, Bend on the left of the phase warp and hard Sync on the right (run at High quality), " +
                        "a sine sub, an ADSR, unison and an output gain. Every control says what it does and what you will hear. " +
                        "16 tracks (Note, Velocity) and MIDI.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-learn.md); it runs on Dye Essence's engine.", "Neo Learn Wavetable"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoLearnWavetableEditor();
    }
#endif
}
