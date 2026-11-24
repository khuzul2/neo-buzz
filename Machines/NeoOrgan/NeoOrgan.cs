#nullable disable
// Neo Organ (docs/specs/neo-organ.md): the tonewheel organ. 16 tracks of Note, plus MIDI. DSP in Engine/, parameters generated from Params.def, editor in
// NeoOrganEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Organ
{
    [MachineDecl(Name = "Neo Organ", ShortName = "Organ", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class NeoOrganMachine : NeoSynthBase, IBuzzMachine
    {
        readonly float[] left = new float[OrganEngine.MaxBlock], right = new float[OrganEngine.MaxBlock];
        int rate;

        public NeoOrganMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            Engine = new OrganEngine(Values);
        }

        internal OrganEngine Engine { get; private set; }

        internal override void Restart()
        {
            Engine = new OrganEngine(Values);
            rate = 0;
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
            n = Math.Min(n, OrganEngine.MaxBlock);
            if (!Engine.Process(left, right, n)) return false;
            return Join(left, right, output, n);
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Organ...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Organ — a tonewheel organ: 91 shared sine wheels, nine drawbars, single-trigger percussion, key click, scanner vibrato and chorus, " +
                        "tube-style overdrive and a rotary speaker with horn and drum. Every key can sound; 16 tracks (Note) and MIDI.\n\nPart of neo-buzz (docs/specs/neo-organ.md).", "Neo Organ"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoOrganEditor();
    }
#endif
}
