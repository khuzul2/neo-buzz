#nullable disable
// Neo Learn Subtractive (docs/specs/neo-learn.md): a teaching subtractive synth, the pilot of the Neo Learn family. One Dye
// Essence patch (Machines/DyeEssence/Engine, hosted by NeoLearn/Engine/LearnHost.cs) driven by 12 controls through a macro map
// (SubtractiveMap). 16 tracks of Note and Velocity, plus MIDI. Parameters are generated from Params.def, with each control's
// Explain text; the editor is NeoLearnSubtractiveEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.DyeEssence;
using NeoBuzz.Learn;
using NeoBuzz.Machine;

namespace NeoBuzz.LearnSub
{
    [MachineDecl(Name = "Neo Learn Subtractive", ShortName = "Learn Sub", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class NeoLearnSubtractiveMachine : NeoSynthBase, IBuzzMachine
    {
        readonly float[] left = new float[DyeEngine.MaxBlock], right = new float[DyeEngine.MaxBlock];
        int rate;

        public NeoLearnSubtractiveMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            Host = new LearnHost(new SubtractiveMap(Params.Table), Values);
            // Build the two tables the Analog engine reads (Basic and PWM) in the background now, not when the audio thread first asks.
            TableBank.Get(0);
            TableBank.Get(1);
        }

        internal LearnHost Host { get; }

        /// <summary>Agent server (mcp-server.md A3): true while the tables the Analog engine reads are still being built. Offline renders wait for it.</summary>
        public bool Busy => TableBank.Get(0) == null || TableBank.Get(1) == null || Host.Engine.TablesPending;

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
                Text = "About Neo Learn Subtractive...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoAbout.Show(
                        "Neo Learn Subtractive — a teaching synth: a wave that morphs from sine through triangle and saw to pulse, " +
                        "a sub oscillator, a 24 dB/oct low-pass with resonance, drive and a filter envelope, an ADSR, unison and an " +
                        "output gain. Every control says what it does and what you will hear. 16 tracks (Note, Velocity) and MIDI.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-learn.md); it runs on Dye Essence's engine.", "Neo Learn Subtractive"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoLearnSubtractiveEditor();
    }
#endif
}
