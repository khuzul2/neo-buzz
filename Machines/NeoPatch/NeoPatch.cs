#nullable disable
// Neo Patch (docs/specs/neo-patch.md, docs/specs/neo-patch-engine.md): a modular synth inside one machine. The patch (nodes, wires, node
// settings, macro mappings and a name) is the machine's state, kept as one line of text, so songs and presets carry it. 16 tracks of Note and
// Velocity, plus MIDI and MPE. Parameters (Volume, Voices, Glide, the audio-rate switch, 16 macros) are generated from Params.def. The inner canvas
// editor is NP2 (NeoPatchEditor.cs and Editor/, on the Neo editor kit): a patch is built there, or from an imported text file, a preset or an agent.

using System;
using System.Collections.Generic;
using System.IO;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Machine;

namespace NeoBuzz.Patch
{
    /// <summary>Saved with the song: the patch as text (PatchText), and the user's own sample (24-bit PCM, base64) for Sample nodes whose source is "user".</summary>
    public class NeoPatchState
    {
        public string Patch { get; set; } = "";
        public string SampleName { get; set; } = "";
        public int SampleRate { get; set; }
        public bool SampleStereo { get; set; }
        public string SampleData { get; set; } = "";
        /// <summary>The inner canvas's view (zoom and pan), so a song reopens as it was left. The nodes' places and the macros' names are in <see cref="Patch"/>.</summary>
        public string View { get; set; } = "";
    }

    [MachineDecl(Name = "Neo Patch", ShortName = "Patch", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class NeoPatchMachine : NeoSynthBase, IBuzzMachine
    {
        readonly float[] left = new float[PatchEngine.MaxBlock], right = new float[PatchEngine.MaxBlock];
        int rate;
        NeoPatchState state = new NeoPatchState();

        public NeoPatchMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            Engine = new PatchEngine(Values);
            Engine.SetGraph(PatchPresets.Build(0));
            state.Patch = Engine.Text();
        }

        internal PatchEngine Engine { get; }

        /// <summary>The inner canvas's view (zoom and pan) as text; the editor keeps it here and the song saves it.</summary>
        internal string View { get; set; } = "";

        /// <summary>Raised on the thread that replaced the patch from outside the editor (an undo step, a preset, a song opening): the open inner canvas redraws from it.</summary>
        internal event Action PatchReplaced;

        /// <summary>Puts a patch back from its text (an undo or redo step of the inner canvas): compiled off the audio thread, swapped in at a block boundary.</summary>
        internal void ApplyPatchText(string text)
        {
            Engine.SetPatch(text);
            state.Patch = Engine.Text();
            PatchReplaced?.Invoke();
        }

        /// <summary>Agent server (mcp-server.md A3): true while a table or sample the patch plays is still being built. Offline renders wait for it.</summary>
        public bool Busy => Engine.Busy;

        /// <summary>Agent server (machine_readings): what the output measures, and the notes sounding.</summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        public NeoPatchState MachineState
        {
            get
            {
                // The patch as it is now (edits made through the engine are in the graph, not in the stored text).
                state.Patch = Engine.Text();
                state.View = View ?? "";
                return state;
            }
            set
            {
                state = value ?? new NeoPatchState();
                View = state.View ?? "";
                try
                {
                    if (string.IsNullOrEmpty(state.Patch)) Engine.SetGraph(new PatchGraph());
                    else Engine.SetPatch(state.Patch);
                }
                catch (Exception)
                {
                    // A damaged patch loads as an empty one rather than failing the song.
                    Engine.SetGraph(new PatchGraph());
                }
                try
                {
                    if (!string.IsNullOrEmpty(state.SampleData) && state.SampleRate > 0)
                    {
                        var (l, r) = SampleSources.Decode(state.SampleData, state.SampleStereo);
                        Engine.Runtime.UserSample = l.Length >= 16 ? SampleSources.Layer(state.SampleName, l, r, state.SampleRate) : null;
                    }
                    else Engine.Runtime.UserSample = null;
                }
                catch (Exception)
                {
                    Engine.Runtime.UserSample = null;
                }
                PatchReplaced?.Invoke();
            }
        }

        /// <summary>
        /// Agent server (mcp-server.md A3, machine_data_import): "patch" replaces the machine's patch with a text file (the text the machine saves with the
        /// song); "sample" loads a WAV as the user sample the Sample nodes can play.
        /// </summary>
        public string ImportData(string kind, string path, int item, Action<int, int> setParameter)
        {
            if (kind == "patch")
            {
                string text = File.ReadAllText(path).Trim();
                var g = PatchText.Load(text);
                if (g == null) throw new InvalidDataException("the file is not a Neo Patch patch (it should begin with \"neopatch 1\")");
                string error = Engine.SetGraph(g);
                if (error != null) throw new InvalidDataException(error);
                state.Patch = Engine.Text();
                return "";
            }
            if (kind == "sample")
            {
                var (l, r, rt) = SampleSources.ReadWav(File.ReadAllBytes(path));
                int keep = (int)Math.Min(l.Length, (long)SampleSources.MaxSeconds * rt);
                string note = keep < l.Length ? $" The file is longer than {SampleSources.MaxSeconds} s; the first {SampleSources.MaxSeconds} s are kept." : "";
                if (keep < l.Length) { Array.Resize(ref l, keep); if (r != null) Array.Resize(ref r, keep); }
                string name = Path.GetFileNameWithoutExtension(path);
                string data = SampleSources.Encode(l, r);
                var (dl, dr) = SampleSources.Decode(data, r != null);
                state.SampleName = name; state.SampleRate = rt; state.SampleStereo = r != null; state.SampleData = data;
                Engine.Runtime.UserSample = SampleSources.Layer(name, dl, dr, rt);
                return note;
            }
            throw new ArgumentException($"Neo Patch imports \"patch\" and \"sample\", not \"{kind}\"");
        }

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            BeginBlock(Engine);
            n = Math.Min(n, PatchEngine.MaxBlock);
            if (!Engine.Process(left, right, n)) return false;
            return Join(left, right, output, n);
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Patch...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoAbout.Show(
                        "Neo Patch — a modular synth inside one machine: oscillators (band-limited, wavetable, sample, noise), envelopes, LFOs, filters, shapers, delays, " +
                        "mixers and math nodes wired together (up to 128 nodes), sixteen voices that each run their own copy of the patch, global nodes that run once, " +
                        "and sixteen macros. The patch is saved with the song. Notes from patterns, MIDI and MPE.\n\nPart of neo-buzz (docs/specs/neo-patch.md).", "Neo Patch"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // the inner canvas on WPF, or Avalonia in the studio (the Neo editor kit); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoPatchEditor();
    }
#endif
}
