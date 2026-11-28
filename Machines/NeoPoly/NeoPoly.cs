#nullable disable
// Neo Poly (docs/specs/machines.md §5): the polyphonic virtual-analog machine. 16 tracks of Note
// and Velocity, plus MIDI. DSP in Engine/, parameters generated from Params.def, editor in
// NeoPolyEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace NeoBuzz.Poly
{
    [MachineDecl(Name = "Neo Poly", ShortName = "Poly", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class NeoPolyMachine : NeoSynthBase, IBuzzMachine
    {
        readonly float[] left = new float[PolyEngine.MaxBlock], right = new float[PolyEngine.MaxBlock];
        int rate;

        public NeoPolyMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            Engine = new PolyEngine(Values);
        }

        internal PolyEngine Engine { get; private set; }

        // State versions 1 (acid) and 3 (SYN1, tempo sync): see PolyMigrations.
        internal override StateMigrations Migrations => PolyMigrations.Chain;

        internal override void Restart()
        {
            Engine = new PolyEngine(Values);
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
            n = Math.Min(n, PolyEngine.MaxBlock);
            if (!Engine.Process(left, right, n)) return false;
            return Join(left, right, output, n);
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Poly...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Poly — polyphonic virtual analog: three band-limited oscillators with sync, FM and " +
                        "ring, ladder, diode and state-variable filters, three envelopes, two LFOs and an 8-slot " +
                        "modulation matrix. 16 tracks (Note, Velocity) and MIDI (mod wheel, pitch bend, " +
                        "sustain, aftertouch).\n\nAcid: the Diode 18 filter, Accent (hard notes open the filter further, " +
                        "close faster and play louder; a run of accents swells) and Slide (an overlapping note glides " +
                        "without retriggering, in Mono or Legato mode).\n\nPart of neo-buzz (docs/specs/machines.md).", "Neo Poly"),
                },
            },
        };
    }

#if AVALONIA   // fork: the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoPolyEditor();
    }
#endif
}
