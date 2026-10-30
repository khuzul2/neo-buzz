using AtmaFileSystem;
using AtmaFileSystem.IO;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using SimpleCommand = AttachedCommandBehavior.SimpleCommand;

namespace ReBuzzTests.Automation.TestMachines
{
    /// <summary>
    /// Fork addition (0.6.3): a stub instrument that reports the MIDI it receives. Tests give it
    /// a listener through its "Listen" command, which it calls with (kind, channel, data1, data2)
    /// for each message: kind 0 is a note (data2 the velocity, 0 for a release), kind 1 a
    /// controller. Outputs silence. Compiled at test time from this file, like Synth.
    /// </summary>
    [MachineDecl(Name = "Keys", ShortName = "Keys", Author = "WDE", MaxTracks = 1, InputCount = 0, OutputCount = 1)]
    public class Keys(IBuzzMachineHost host) : IBuzzMachine
    {
        private readonly IBuzzMachineHost host = host;
        private Action<int, int, int, int> listener = (kind, channel, data1, data2) => { };

        public static string GetSourceCode() => AbsoluteFilePath.OfThisFile().ReadAllText();

        public static MachineDecl GetMachineDecl() => typeof(Keys).GetCustomAttributes(false).OfType<MachineDecl>().Single();

        [ParameterDecl(ValueDescriptions = ["no", "yes"])]
        public bool Bypass { get; set; }

        public Sample Work() => new Sample(0, 0);

        public void MidiNote(int channel, int value, int velocity) => listener(0, channel, value, velocity);

        public void MidiControlChange(int ctrl, int channel, int value) => listener(1, channel, ctrl, value);

        public IEnumerable<IMenuItem> Commands =>
        [
            new MenuItemVM
            {
                Text = "Listen",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = o => listener = (Action<int, int, int, int>)o
                }
            }
        ];
    }
}
