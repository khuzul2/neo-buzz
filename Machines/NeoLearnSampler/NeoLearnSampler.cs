#nullable disable
// Neo Learn Sampler (docs/specs/neo-learn.md): a teaching sampler. One Dye Essence patch (Machines/DyeEssence/Engine, hosted by
// NeoLearn/Engine/LearnHost.cs) driven by 12 controls through a macro map (SamplerMap). 16 tracks of Note and Velocity, plus MIDI.
// Parameters are generated from Params.def, with each control's Explain text; the editor is NeoLearnSamplerEditor.cs.
// The Source control chooses one of Dye's five built-in samples or the user's WAV (kept with the song, like Dye's own).

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

namespace NeoBuzz.LearnSampler
{
    /// <summary>What Neo Learn Sampler keeps with the song: the user's own sample (24-bit PCM, base 64).</summary>
    public class LearnSampleState
    {
        public string Name { get; set; } = "";
        public int Rate { get; set; }
        public bool Stereo { get; set; }
        public string Data { get; set; } = "";
    }

    [MachineDecl(Name = "Neo Learn Sampler", ShortName = "Learn Sampler", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class NeoLearnSamplerMachine : NeoSynthBase, IBuzzMachine
    {
        readonly float[] left = new float[DyeEngine.MaxBlock], right = new float[DyeEngine.MaxBlock];
        readonly SamplerMap map;
        int rate;

        public NeoLearnSamplerMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            map = new SamplerMap(Params.Table);
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
                ApplySource();
            }
        }

        /// <summary>The user sample's name ("" when none is loaded).</summary>
        internal string UserSampleName => state.Name;

        void ApplySource()
        {
            // User with no sample loaded would be silent: the map plays the first built-in instead.
            map.UserAvailable = !string.IsNullOrEmpty(state.Data);
            Host.Remap();
        }

        /// <summary>
        /// Agent server (mcp-server.md A3, machine_data_import): "sample" loads a WAV as the User source (item 0) and chooses it.
        /// </summary>
        public string ImportData(string kind, string path, int item, Action<int, int> setParameter)
        {
            if (kind != "sample") throw new ArgumentException($"Neo Learn Sampler doesn't import \"{kind}\"; it takes sample.");
            if (item != 0) throw new ArgumentException("Neo Learn Sampler has one source sample; item " + item + " doesn't exist (0)");
            string note = LoadUserSample(path);
            return $"Loaded the sample \"{UserSampleName}\" and chose it as the Source.{note}";
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
            state = new LearnSampleState { Name = name, Rate = srate, Stereo = r != null, Data = data };
            Host.Engine.SetUserSample(0, layer, name);
            Values.Set(P.Source, SampleSources.Count);
            ApplySource();
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
                items.Add(new MenuItemVM
                {
                    Text = "About Neo Learn Sampler...",
                    Command = new SimpleCommand
                    {
                        CanExecuteDelegate = _ => true,
                        ExecuteDelegate = _ => NeoAbout.Show(
                            "Neo Learn Sampler — a teaching sampler: one recording (five built-in samples, or your own WAV by the agent's data import) played " +
                            "across the keyboard from a start offset past its attack, a tone tilt around 1 kHz, a crossfaded loop, a vintage grit stage " +
                            "(12 bits, a lower sample rate, tape wow), a small room, an ADSR, unison and an output gain. Every control says what it does " +
                            "and what you will hear. 16 tracks (Note, Velocity) and MIDI.\n\n" +
                            "Part of neo-buzz (docs/specs/neo-learn.md); it runs on Dye Essence's engine.", "Neo Learn Sampler"),
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
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoLearnSamplerEditor();
    }
#endif
}
