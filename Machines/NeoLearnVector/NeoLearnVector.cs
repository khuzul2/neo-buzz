#nullable disable
// Neo Learn Vector (docs/specs/neo-learn.md): a teaching vector synth. One Dye Essence patch (Machines/DyeEssence/Engine, hosted by
// NeoLearn/Engine/LearnHost.cs) driven by 12 controls through a macro map (VectorMap). 16 tracks of Note and Velocity, plus MIDI.
// Parameters are generated from Params.def, with each control's Explain text; the editor is NeoLearnVectorEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.DyeEssence;
using NeoBuzz.Learn;
using NeoBuzz.Machine;

namespace NeoBuzz.LearnVector
{
    [MachineDecl(Name = "Neo Learn Vector", ShortName = "Learn Vector", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class NeoLearnVectorMachine : NeoSynthBase, IBuzzMachine
    {
        readonly float[] left = new float[DyeEngine.MaxBlock], right = new float[DyeEngine.MaxBlock];
        int rate;

        public NeoLearnVectorMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            Host = new LearnHost(new VectorMap(Params.Table), Values);
        }

        internal LearnHost Host { get; }

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
                Text = "About Neo Learn Vector...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoAbout.Show(
                        "Neo Learn Vector — a teaching vector synth: four sources at the corners of a diamond (west, east, north, south) that a cursor mixes, in four palettes, " +
                        "with an orbit generator that moves the cursor around the point you set (circle, figure-8, diamond step or random drift), an ADSR, unison and an output gain. " +
                        "Every control says what it does and what you will hear. 16 tracks (Note, Velocity) and MIDI.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-learn.md); it runs on Dye Essence's engine.", "Neo Learn Vector"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoLearnVectorEditor();
    }
#endif
}
