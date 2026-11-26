#nullable disable
// Neo Chip (docs/specs/neo-chip.md): four-channel sound-chip instrument with three pulse channels,
// one 15-bit/7-bit LFSR noise channel, 16-step ADSR and hardware-style envelopes, per-track
// arpeggio, pitch sweep, vibrato and an optional 4-bit DAC stage. Tracks 1-3 drive pulse channels
// 1-3, track 4 drives noise; MIDI/piano roll routes via Voice mode and Noise MIDI ch.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace NeoBuzz.Chip
{
    [MachineDecl(Name = "Neo Chip", ShortName = "Chip", Author = "neo-buzz", MaxTracks = 4, InputCount = 0, OutputCount = 1)]
    public partial class NeoChipMachine : NeoSynthBase, IBuzzMachine
    {
        readonly float[] left = new float[ChipEngine.MaxBlock], right = new float[ChipEngine.MaxBlock];
        int rate;

        public NeoChipMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            Engine = new ChipEngine(Values);
        }

        internal ChipEngine Engine { get; private set; }

        internal override StateMigrations Migrations => ChipEngine.Migrations;

        internal override void Restart()
        {
            Engine = new ChipEngine(Values);
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
            n = Math.Min(n, ChipEngine.MaxBlock);
            if (!Engine.Process(left, right, n)) return false;
            return Join(left, right, output, n);
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Chip...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Chip — four-channel sound-chip instrument: three pulse channels (12.5/25/50/75 % " +
                        "duty, continuous PWM, band-limited or raw edges), one 15-bit/7-bit LFSR noise channel, " +
                        "16-step ADSR and hardware-style envelopes, per-track arpeggio, pitch sweep, vibrato " +
                        "and an optional 4-bit DAC stage. Tracks 1–3 drive pulse channels, track 4 drives noise.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-chip.md).", "Neo Chip"),
                },
            },
        };
    }

#if AVALONIA   // the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoChipEditor();
    }
#endif
}
