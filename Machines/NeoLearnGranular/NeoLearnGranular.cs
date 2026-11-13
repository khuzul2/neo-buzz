#nullable disable
// Neo Learn Granular (docs/specs/neo-learn.md): a teaching granular synth. One Dye Essence patch (Machines/DyeEssence/Engine, hosted by
// NeoLearn/Engine/LearnHost.cs) driven by 12 controls through a macro map (GranularMap). 16 tracks of Note and Velocity, plus MIDI.
// Parameters are generated from Params.def, with each control's Explain text; the editor is NeoLearnGranularEditor.cs.
// The source the grains are cut from is not one of the 12 controls: it is one of Dye's five built-in samples or the user's WAV, kept with the song.

using System;
using System.Collections.Generic;
using System.IO;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Learn;
using NeoBuzz.Machine;

namespace NeoBuzz.LearnGrain
{
    /// <summary>What Neo Learn Granular keeps with the song: which source the grains come from (0 to 4 the built-in samples, 5 the user's own) and that sample (24-bit PCM, base 64).</summary>
    public class LearnSampleState
    {
        public int Source { get; set; }
        public string Name { get; set; } = "";
        public int Rate { get; set; }
        public bool Stereo { get; set; }
        public string Data { get; set; } = "";
    }

    [MachineDecl(Name = "Neo Learn Granular", ShortName = "Learn Grain", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class NeoLearnGranularMachine : NeoSynthBase, IBuzzMachine
    {
        readonly float[] left = new float[DyeEngine.MaxBlock], right = new float[DyeEngine.MaxBlock];
        readonly GranularMap map;
        int rate;

        public NeoLearnGranularMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            map = new GranularMap(Params.Table);
            Host = new LearnHost(map, Values);
            // Build the built-in samples in the background now, not when the audio thread first asks.
            for (int i = 0; i < SampleSources.Count; i++) SampleSources.Get(i);
        }

        internal LearnHost Host { get; }

        LearnSampleState state = new LearnSampleState();

        /// <summary>The user's own sample ("User", source 5) and the choice of source, kept with the song.</summary>
        public LearnSampleState MachineState
        {
            get => state;
            set
            {
                state = value ?? new LearnSampleState();
                try
                {
                    if (!string.IsNullOrEmpty(state.Data) && state.Rate > 0)
                    {
                        var (l, r) = SampleSources.Decode(state.Data, state.Stereo);
                        Host.Engine.SetUserSample(0, l.Length >= 16 ? SampleSources.Layer(state.Name, l, r, state.Rate) : null, state.Name);
                    }
                    else Host.Engine.SetUserSample(0, null, "");
                }
                catch (Exception)
                {
                    // A damaged sample loads as "no user sample" rather than failing the song.
                    Host.Engine.SetUserSample(0, null, "");
                }
                ApplySource(state.Source);
            }
        }

        /// <summary>The source now: 0 to 4 a built-in sample, 5 the user's.</summary>
        internal int Source => map.Source;

        /// <summary>The user sample's name ("" when none is loaded).</summary>
        internal string UserSampleName => state.Name;

        void ApplySource(int source)
        {
            // User with no sample loaded would be silent: it falls back to the first built-in.
            if (source < 0 || source > SampleSources.Count || source == SampleSources.Count && string.IsNullOrEmpty(state.Data)) source = 0;
            map.Source = source;
            state.Source = source;
            Host.Remap();
        }

        /// <summary>Chooses the source (UI thread): 0 to 4 a built-in sample, 5 the user's.</summary>
        internal void SetSource(int source) => ApplySource(source);

        /// <summary>
        /// Agent server (mcp-server.md A3, machine_data_import): "sample" loads a WAV as the source (item 0); "source" chooses a built-in sample
        /// by name (Choir, Strings, Bell, Pluck, Texture) or "User".
        /// </summary>
        public string ImportData(string kind, string path, int item, Action<int, int> setParameter)
        {
            if (kind == "source")
            {
                string name = (path ?? "").Trim();
                int index = Array.FindIndex(SampleSources.Names, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                if (index < 0 && string.Equals(name, "User", StringComparison.OrdinalIgnoreCase)) index = SampleSources.Count;
                if (index < 0) throw new ArgumentException($"\"{name}\" is not a source; the sources are {string.Join(", ", SampleSources.Names)} and User");
                if (index == SampleSources.Count && string.IsNullOrEmpty(state.Data)) throw new ArgumentException("no sample is loaded yet; import one with kind \"sample\" first");
                SetSource(index);
                return $"The grains are cut from {(index == SampleSources.Count ? "your sample \"" + state.Name + "\"" : "the built-in " + SampleSources.Names[index])}.";
            }
            if (kind != "sample") throw new ArgumentException($"Neo Learn Granular doesn't import \"{kind}\"; it takes sample and source.");
            if (item != 0) throw new ArgumentException("Neo Learn Granular has one source sample; item " + item + " doesn't exist (0)");
            string note = LoadUserSample(path);
            return $"Loaded the sample \"{UserSampleName}\" and set it as the source.{note}";
        }

        /// <summary>
        /// Loads a WAV file as the source (UI thread): mono or stereo, kept as 24-bit PCM, at most <see cref="SampleSources.MaxSeconds"/> s (the rest is cut; the
        /// text says so). Throws with a readable message on bad files. Returns "" or a note.
        /// </summary>
        internal string LoadUserSample(string path)
        {
            var (l, r, srate) = SampleSources.ReadWav(File.ReadAllBytes(path));
            int keep = (int)Math.Min(l.Length, (long)SampleSources.MaxSeconds * srate);
            string note = keep < l.Length ? $" The file is longer than {SampleSources.MaxSeconds} s; the first {SampleSources.MaxSeconds} s are kept." : "";
            if (keep < l.Length)
            {
                Array.Resize(ref l, keep);
                if (r != null) Array.Resize(ref r, keep);
            }
            string name = Path.GetFileNameWithoutExtension(path);
            string data = SampleSources.Encode(l, r);
            // What plays is what the state keeps (24-bit), so a reloaded song sounds the same.
            var (dl, dr) = SampleSources.Decode(data, r != null);
            var layer = SampleSources.Layer(name, dl, dr, srate);
            state = new LearnSampleState { Source = SampleSources.Count, Name = name, Rate = srate, Stereo = r != null, Data = data };
            Host.Engine.SetUserSample(0, layer, name);
            ApplySource(SampleSources.Count);
            return note;
        }

        /// <summary>Agent server (mcp-server.md A3): true while the built-in samples are still being built. Offline renders wait for it.</summary>
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

        public IEnumerable<IMenuItem> Commands
        {
            get
            {
                var items = new List<IMenuItem>();
                for (int i = 0; i < SampleSources.Count; i++)
                {
                    int index = i;
                    items.Add(new MenuItemVM
                    {
                        Text = "Source: " + SampleSources.Names[i] + (Source == i ? "  (current)" : ""),
                        Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => SetSource(index) },
                    });
                }
                items.Add(new MenuItemVM
                {
                    Text = "About Neo Learn Granular...",
                    Command = new SimpleCommand
                    {
                        CanExecuteDelegate = _ => true,
                        ExecuteDelegate = _ => NeoAbout.Show(
                            "Neo Learn Granular — a teaching granular synth: a source recording (five built-in samples, or your own WAV by the agent's " +
                            "data import) cut into short windowed grains around a frozen scan position, with grain size, density, spray, a pitch shimmer " +
                            "(micro-pitch wander and grains an octave up), a grain contour that morphs the window from trapezoid to Gaussian, an ADSR, " +
                            "unison and an output gain. At most 10 grains sound per note. Every control says what it does and what you will hear. " +
                            "16 tracks (Note, Velocity) and MIDI.\n\n" +
                            "Part of neo-buzz (docs/specs/neo-learn.md); it runs on Dye Essence's engine.", "Neo Learn Granular"),
                    },
                });
                return items;
            }
        }
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoLearnGranularEditor();
    }
#endif
}
