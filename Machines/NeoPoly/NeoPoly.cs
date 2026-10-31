#nullable disable
// Neo Poly (docs/specs/machines.md §5): the polyphonic virtual-analog machine. 16 tracks of Note
// and Velocity, plus MIDI. DSP in Engine/, parameters generated from Params.def, editor in
// NeoPolyEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
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

        internal PolyEngine Engine { get; }

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
                    ExecuteDelegate = _ => System.Windows.MessageBox.Show(
                        "Neo Poly — polyphonic virtual analog: three band-limited oscillators with sync, FM and " +
                        "ring, ladder and state-variable filters, three envelopes, two LFOs and an 8-slot " +
                        "modulation matrix. 16 tracks (Note, Velocity) and MIDI (mod wheel, pitch bend, " +
                        "sustain, aftertouch).\n\nPart of neo-buzz (docs/specs/machines.md).", "Neo Poly"),
                },
            },
        };
    }

    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoPolyEditor();
    }
}
