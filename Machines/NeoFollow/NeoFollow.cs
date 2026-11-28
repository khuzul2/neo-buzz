#nullable disable
// Neo Follow (docs/specs/neo-follow.md): an audio follower that moves up to 8 parameters of other machines from the level, the pitch or the hits
// of the audio passing through it (or of a sidechain wired to its second input). It is a canvas modulator (NeoModulatorBase,
// NeoBuzz.Machine/NeoModulator.cs, and ModulatorEditor, NeoModulator/ModulatorEditor.cs: the targets, their depth, polarity, range, curve and
// Write glide, their saving and the agent's tools are Neo Random's) that also sits in an audio chain: the audio out is the audio in, copied
// sample for sample, in every mode. It makes no sound of its own, so it has no Mix or Amount (machines.md, "Mix and Amount": only meters and
// analysers, which pass audio unchanged, have neither).

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace NeoBuzz.FollowMachine
{
    [MachineDecl(Name = "Neo Follow", ShortName = "Follow", Author = "neo-buzz", MaxTracks = 0, InputCount = 2, OutputCount = 1)]
    public partial class NeoFollowMachine : NeoModulatorBase, IBuzzMachine
    {
        readonly float[] left = new float[FollowEngine.MaxBlock], right = new float[FollowEngine.MaxBlock];
        readonly float[] keyLeft = new float[FollowEngine.MaxBlock], keyRight = new float[FollowEngine.MaxBlock];
        int rate;
        long quiet;

        public NeoFollowMachine(IBuzzMachineHost host) : base(host, Params.Table, "FOL")
        {
            Engine = new FollowEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal FollowEngine Engine { get; }

        public bool Work(IList<Sample[]> output, IList<Sample[]> input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            var main = input != null && input.Count > 0 ? input[0] : null;
            var key = input != null && input.Count > 1 ? input[1] : null;
            var outBuf = output != null && output.Count > 0 ? output[0] : null;

            // The audio passes through as it is: the same samples, not a converted copy.
            if (outBuf != null)
            {
                if (main == null) Array.Clear(outBuf, 0, n);
                else if (!ReferenceEquals(main, outBuf)) Array.Copy(main, outBuf, n);
            }

            bool hasMain = main != null && HasSignal(main, n);
            bool hasKey = key != null && HasSignal(key, n);
            if (hasMain || hasKey) quiet = 0;
            else quiet += n;

            // Silence is listened to as well (a release is still falling, a write glide still moving) until it has settled: 6 s.
            if (hasMain || hasKey || quiet < 6L * rate)
            {
                for (int pos = 0; pos < n; pos += FollowEngine.MaxBlock)
                {
                    int m = Math.Min(FollowEngine.MaxBlock, n - pos);
                    if (main != null) SplitAt(main, pos, left, right, m); else { Array.Clear(left, 0, m); Array.Clear(right, 0, m); }
                    if (key != null) SplitAt(key, pos, keyLeft, keyRight, m);
                    Engine.Process(left, right, key != null ? keyLeft : null, key != null ? keyRight : null, m);
                }
                Run();
            }
            return outBuf != null && (hasMain || quiet < 6L * rate);
        }

        static void SplitAt(Sample[] input, int offset, float[] l, float[] r, int n)
        {
            const float s = 1f / DspMath.BuzzScale;
            for (int i = 0; i < n; i++)
            {
                l[i] = input[offset + i].L * s;
                r[i] = input[offset + i].R * s;
            }
        }

        protected override float Advance(int samples, double dt) => Engine.Output;

        protected override double TargetGlide => Engine.Glide;

        public string GetChannelName(bool input, int index) => input ? (index == 0 ? "Input" : "Sidechain") : "Output";

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Follow...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoAbout.Show(
                        "Neo Follow — an audio follower that moves any parameter of any machine: Envelope (Peak or RMS detector, attack 0.1 to 500 ms, " +
                        "release 1 ms to 5 s, floor and ceiling in dB, curve), Pitch (low and high note, a confidence threshold, a glide) or Onset (each hit " +
                        "starts a pulse with a decay and a hold, scaled by the hit's level), listening to its own input or to a sidechain on its second input. " +
                        "Smooth, invert, depth, bias and a unipolar or bipolar range shape the control; up to 8 targets with individual depth, polarity, range " +
                        "and curve; writes are smoothed, are not undo steps, and keep safe ranges. The audio passes through unchanged.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-follow.md).", "Neo Follow"),
                },
            },
        };
    }

#if AVALONIA   // fork: the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoFollowEditor();
    }
#endif
}
