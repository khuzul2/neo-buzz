#nullable disable
// Neo Keys (docs/specs/neo-keys.md): electric keys by physical modelling. A generator that plays by notes (16 tracks of Note and Velocity, MIDI, note
// wires): each note strikes a tine, a steel reed or a string, read by a pickup. DSP in Engine/, parameters generated from Params.def, editor in
// NeoKeysEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Keys
{
    [MachineDecl(Name = "Neo Keys", ShortName = "Keys", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class NeoKeysMachine : NeoSynthBase, IBuzzMachine
    {
        readonly float[] left = new float[KeysEngine.MaxBlock], right = new float[KeysEngine.MaxBlock];
        int rate;

        public NeoKeysMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            Engine = new KeysEngine(Values);
        }

        internal KeysEngine Engine { get; private set; }

        internal override void Restart()
        {
            Engine = new KeysEngine(Values);
            rate = 0;
        }

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            BeginBlock(Engine);
            n = Math.Min(n, KeysEngine.MaxBlock);
            Array.Clear(left, 0, n);
            Array.Clear(right, 0, n);
            if (!Engine.Process(left, right, n)) return false;
            return Join(left, right, output, n);
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Keys...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Keys — electric keys by physical modelling: a hammer strikes a tine, a steel reed or a string and a pickup reads it. Tine and reed " +
                        "are decaying beam modes beside a slower tone bar or body, the string is a stiff string with a damper and a neck and a bridge pickup. " +
                        "The pickup's curve makes the bark, key noise adds the hammer and damper thumps, plus a preamp drive, a tone tilt, a tremolo with auto-pan, " +
                        "16 voices and a sustain pedal.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-keys.md).", "Neo Keys"),
                },
            },
        };
    }

#if AVALONIA   // the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoKeysEditor();
    }
#endif
}
