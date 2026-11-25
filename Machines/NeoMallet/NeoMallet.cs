#nullable disable
// Neo Mallet (docs/specs/neo-mallet.md): a modal synth for mallets, bars, bells and plucks, with a resonator mode. One machine that is
// an effect (one audio input, Mix and Amount) and plays by notes (16 tracks of Note and Velocity, MIDI, note wires): the notes strike
// or pluck the bodies, or in Resonator mode tune them to the audio coming in. DSP in Engine/, parameters generated from Params.def,
// editor in NeoMalletEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Mallet
{
    [MachineDecl(Name = "Neo Mallet", ShortName = "Mallet", Author = "neo-buzz", MaxTracks = 16, InputCount = 1, OutputCount = 1)]
    public partial class NeoMalletMachine : NeoSynthBase, IBuzzMachine
    {
        readonly float[] left = new float[MalletEngine.MaxBlock], right = new float[MalletEngine.MaxBlock];
        int rate;

        public NeoMalletMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            Engine = new MalletEngine(Values);
        }

        internal MalletEngine Engine { get; private set; }

        internal override void Restart()
        {
            Engine = new MalletEngine(Values);
            rate = 0;
        }

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            BeginBlock(Engine);
            n = Math.Min(n, MalletEngine.MaxBlock);
            if ((mode & WorkModes.WM_READ) != 0 && input != null) Split(input, left, right, n);
            else
            {
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            Engine.Process(left, right, n);
            return Join(left, right, output, n);
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Mallet...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Mallet — a modal synth for mallets, bars, bells and plucks: up to 32 decaying modes a note, eleven bodies (bars, tube, bell, tine, " +
                        "stiff string, glass, plate, custom), a mallet whose hardness and strike position shape the attack, tremolo, release damping, " +
                        "16 voices. In Resonator mode the audio coming in rings the same bodies, tuned to the notes you hold, with Mix and Amount.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-mallet.md).", "Neo Mallet"),
                },
            },
        };
    }

#if AVALONIA   // the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoMalletEditor();
    }
#endif
}
