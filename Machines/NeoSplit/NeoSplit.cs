#nullable disable
// Neo Split (docs/specs/neo-split.md): one stereo input, four outputs that add up to it (frequency bands, transient and body, mid and
// side). The first Neo machine with several outputs (docs/specs/multi-output.md). DSP in Engine/, parameters generated from Params.def,
// editor in NeoSplitEditor.cs. Its latency is the lookahead of Transient and Body (0 in the other modes) and is reported to NeoBuzz Live.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace NeoBuzz.Split
{
    // The studio reads the names of a machine's outputs once, when the machine is made, so they cannot follow the mode: the outputs are
    // "Out 1" to "Out 4" and the editor and the guide say what each carries in each mode (docs/specs/neo-split.md section 2).
    [MachineDecl(Name = "Neo Split", ShortName = "Split", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 4)]
    [NeoOutputs("Out 1", "Out 2", "Out 3", "Out 4")]
    public partial class NeoSplitMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[SplitEngine.MaxBlock], right = new float[SplitEngine.MaxBlock];
        int rate;
        long silent;

        public NeoSplitMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new SplitEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal SplitEngine Engine { get; }

        /// <summary>
        /// One input, four outputs: an output that is not wired has a null buffer and is not written (the engine runs all of its state either way).
        /// Neo Split only routes, so it has no Mix and no Amount: the outputs summed are the input (docs/specs/neo-split.md).
        /// </summary>
        public bool Work(IList<Sample[]> output, IList<Sample[]> input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            Engine.Poll();
            var main = input != null && input.Count > 0 ? input[0] : null;
            bool hasInput = main != null && HasSignal(main, n);
            if (!hasInput)
            {
                // Run until the sound in flight (the crossovers' ringing, the lookahead) has come out, then rest.
                silent += n;
                if (silent > Engine.TailSamples)
                {
                    Engine.Rest(Math.Min(n, SplitEngine.MaxBlock));
                    return false;
                }
            }
            else silent = 0;
            bool sound = false;
            for (int pos = 0; pos < n; pos += SplitEngine.MaxBlock)
            {
                int m = Math.Min(SplitEngine.MaxBlock, n - pos);
                if (hasInput)
                {
                    const float s = 1f / DspMath.BuzzScale;
                    for (int i = 0; i < m; i++)
                    {
                        left[i] = main[pos + i].L * s;
                        right[i] = main[pos + i].R * s;
                    }
                }
                else
                {
                    Array.Clear(left, 0, m);
                    Array.Clear(right, 0, m);
                }
                Engine.Process(left, right, m);
                for (int o = 0; o < SplitEngine.Outputs; o++)
                {
                    var buffer = output != null && o < output.Count ? output[o] : null;
                    if (buffer == null) continue;      // not wired
                    sound |= Write(Engine.OutL[o], Engine.OutR[o], buffer, pos, m);
                }
            }
            return sound || hasInput;
        }

        static bool Write(float[] l, float[] r, Sample[] buffer, int pos, int n)
        {
            const float s = DspMath.BuzzScale;
            bool sound = false;
            for (int i = 0; i < n; i++)
            {
                float a = l[i], b = r[i];
                if (!float.IsFinite(a)) a = 0;
                if (!float.IsFinite(b)) b = 0;
                buffer[pos + i].L = a * s;
                buffer[pos + i].R = b * s;
                if (!sound && (MathF.Abs(a) > 1e-6f || MathF.Abs(b) > 1e-6f)) sound = true;
            }
            return sound;
        }

        /// <summary>Samples ReBuzz compensates: the lookahead of Transient and Body, 0 in the other modes; the same on every output.</summary>
        public int GetLatency() => Engine.Latency;

        public string GetChannelName(bool input, int index) => NeoOutputs.ChannelName(this, input, index);

        /// <summary>For agents (machine_readings, docs/specs/mcp-server.md §3.5): out1_db … out4_db (each output's peak, dBFS) and latency_samples. Safe from any thread.</summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Split...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Split — one input, four outputs that add up to it: two to four frequency bands (Linkwitz-Riley, 24 or 48 dB/oct), " +
                        "the transients and the body of a sound, or its mid and side. Wire each output to its own chain for parallel processing.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-split.md).", "Neo Split"),
                },
            },
        };
    }

#if AVALONIA   // the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoSplitEditor();
    }
#endif
}
