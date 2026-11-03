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
    /// Fork addition (0.4.1): a stub generator that outputs a constant level and does nothing
    /// else: no host calls, no allocation, no console output. Engine tests use it to measure
    /// what the engine itself costs (allocations and time per buffer).
    /// Compiled at test time from this file, like <see cref="Synth"/>.
    /// Engine hardening: parameters for automation (a global with a range, a track parameter, up
    /// to 4 tracks) that change nothing it plays; a listener given through its "Listen" command
    /// hears each value its host sets, as (parameter index, track or -1, value).
    /// </summary>
    [MachineDecl(Name = "Hum", ShortName = "Hum", Author = "WDE", MaxTracks = 4, InputCount = 0, OutputCount = 1)]
    public class Hum(IBuzzMachineHost host) : IBuzzMachine
    {
        private readonly IBuzzMachineHost host = host;
        private Action<int, int, int> listener = (parameter, track, value) => { };
        private bool bypass;
        private int level = 64;

        public static string GetSourceCode() => AbsoluteFilePath.OfThisFile().ReadAllText();

        public static MachineDecl GetMachineDecl() => typeof(Hum).GetCustomAttributes(false).OfType<MachineDecl>().Single();

        [ParameterDecl(ValueDescriptions = ["no", "yes"])]
        public bool Bypass { get => bypass; set { bypass = value; listener(0, -1, value ? 1 : 0); } }

        [ParameterDecl(MaxValue = 127, DefValue = 64)]
        public int Level { get => level; set { level = value; listener(1, -1, value); } }

        [ParameterDecl(MaxValue = 127, DefValue = 0)]
        public void Gate(int value, int track) => listener(2, track, value);

        public Sample Work() => new Sample(1000, 1000);

        public IEnumerable<IMenuItem> Commands =>
        [
            new MenuItemVM
            {
                Text = "Listen",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = o => listener = (Action<int, int, int>)o
                }
            }
        ];
    }
}
