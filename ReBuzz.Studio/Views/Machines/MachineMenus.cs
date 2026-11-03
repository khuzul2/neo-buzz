// Fork (docs/specs/studio-mvp.md S3): the machine view's context menus, as BuzzGUI.MachineView
// has them: on empty space, New (the installed machines by kind) and the view's commands; on a
// machine, mute, solo or bypass, its parameters, MIDI input channel, replace, rename, delete.
// Fork (machine-safety.md §3.2–3.3): Reset and unmute on a diverged machine; on the Master, clearing
// the clip indicator.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Views.Machines
{
    internal sealed class MachineMenus
    {
        readonly ReBuzzCore core;
        readonly MachineCanvas canvas;

        public MachineMenus(ReBuzzCore core, MachineCanvas canvas)
        {
            this.core = core;
            this.canvas = canvas;
        }

        static KeyModifiers CommandKey => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        static MenuItem Item(string header, Action run, bool enabled = true, KeyGesture key = null) =>
            new MenuItem { Header = header, Command = new Command(run), IsEnabled = enabled, InputGesture = key };

        static MenuItem Check(string header, bool on, Action run, bool enabled = true) =>
            new MenuItem { Header = header, Command = new Command(run), IsEnabled = enabled, ToggleType = MenuItemToggleType.CheckBox, IsChecked = on };

        static MenuItem Sub(string header, IEnumerable<object> items)
        {
            var m = new MenuItem { Header = header };
            foreach (var i in items) m.Items.Add(i);
            m.IsEnabled = m.Items.Count > 0;
            return m;
        }

        /// <summary>Submenus of the installed machines by kind; a kind with none is left out.</summary>
        IEnumerable<object> Gear(Action<IInstrument> chosen, bool effectsOnly = false) =>
            MachineEdits.GearByKind(core)
                .Where(k => k.Items.Count > 0 && (!effectsOnly || k.Kind == "Effects"))
                .Select(k => (object)Sub(k.Kind, k.Items.Select(g => (object)Item(g.DisplayName, () => chosen(g.Instrument)))));

        /// <summary>The menu on empty space (MachineView.xaml, the grid's ContextMenu), at a canvas point.</summary>
        public ContextMenu ForCanvas(Point logical)
        {
            var at = CanvasMap.Clamp(canvas.Map.PositionAt(logical));
            var menu = new ContextMenu();
            menu.Items.Add(Sub("New", Gear(i =>
            {
                var m = MachineEdits.Create(core, i, (float)at.X, (float)at.Y);
                if (m != null) canvas.Select(new[] { m });
            })));
            menu.Items.Add(Item("Unmute all machines", canvas.UnmuteAll, key: new KeyGesture(Key.U, CommandKey)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Snap Machines", () => MachineEdits.Snap(core, canvas.Selection.Count > 0 ? canvas.Selection.ToList() : core.SongCore.Machines.ToList(), 10)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Select All", canvas.SelectAll, key: new KeyGesture(Key.A, CommandKey)));
            menu.Items.Add(Item("Deselect", canvas.ClearSelection, canvas.Selection.Count > 0, new KeyGesture(Key.D, CommandKey)));
            return menu;
        }

        /// <summary>A machine's menu (MachineControl.CreateMenu); its commands act on the selection, as there.</summary>
        public ContextMenu ForMachine(IMachine machine, IReadOnlyList<IMachine> selection)
        {
            var menu = new ContextMenu();
            var type = machine.DLL.Info.Type;
            if (type == MachineType.Master)
            {
                menu.Items.Add(Item("Parameters...", () => MachineEdits.OpenParameters(machine)));
                if (core.MasterOver)
                    menu.Items.Add(Item($"Clear clip indicator ({core.MasterOverText})", core.ClearMasterOver));
                return menu;
            }

            if ((machine as MachineCore)?.IsDiverged == true)
            {
                // One undo step; the guard trips again if it is still broken.
                menu.Items.Add(Item("Reset and unmute", () => MachineEdits.Mute(core, new[] { machine }, false)));
                menu.Items.Add(new Separator());
            }

            bool generator = type == MachineType.Generator;
            menu.Items.Add(Check("Mute", machine.IsMuted, () => MachineEdits.Mute(core, selection, !machine.IsMuted)));
            if (generator)
                menu.Items.Add(Check("Solo", machine.IsSoloed, () => MachineEdits.Solo(core, selection, !machine.IsSoloed)));
            else
                menu.Items.Add(Check("Bypass", machine.IsBypassed, () => MachineEdits.Bypass(core, selection, !machine.IsBypassed)));
            menu.Items.Add(Check("Oversample", machine.OversampleFactor != 1, () => machine.OversampleFactor = machine.OversampleFactor != 1 ? 1 : 2,
                (generator && !machine.IsControlMachine) || machine.DLL.Info.Flags.HasFlag(MachineInfoFlags.MULTI_IO)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Parameters...", () => MachineEdits.OpenParameters(machine)));
            menu.Items.Add(Sub("MIDI Input Channel", MidiChannels(machine)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Sub("Replace", Gear(i => MachineEdits.Replace(core, machine, i))));
            menu.Items.Add(Item("Rename...", () => MachineEdits.Rename(machine)));
            menu.Items.Add(Item("Delete", () => MachineEdits.Delete(core, selection), key: new KeyGesture(Key.Delete)));
            return menu;
        }

        static IEnumerable<object> MidiChannels(IMachine machine)
        {
            MenuItem Channel(string header, int ch) => new MenuItem
            {
                Header = header,
                Command = new Command(() => machine.MIDIInputChannel = ch),
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "midi",
                IsChecked = machine.MIDIInputChannel == ch,
            };
            yield return Channel("None", -1);
            yield return Channel("All", 0);
            yield return new Separator();
            for (int ch = 1; ch <= 16; ch++) yield return Channel(ch.ToString(), ch);
        }

        /// <summary>The amp control's insert button: an effect into the wire (MachineView.InsertMachinePopup).</summary>
        public ContextMenu ForInsert(IMachineConnection wire)
        {
            var menu = new ContextMenu();
            foreach (var item in Gear(i => MachineEdits.InsertIntoWire(core, wire, i), effectsOnly: true))
                menu.Items.Add(item);
            return menu;
        }
    }
}
