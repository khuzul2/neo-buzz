// Fork (docs/specs/studio-mvp.md S3): the machine view's context menus, as BuzzGUI.MachineView
// has them: on empty space, New (the installed machines by kind) and the view's commands; on a
// machine, mute, solo or bypass, its parameters, MIDI input channel, replace, rename, delete.
// Fork (mcp-server.md §12.5, U5): "Replace with <successor>..." on a machine with a Neo successor.
// Fork (machine-safety.md §3.2–3.3): Reset and unmute on a diverged machine; on the Master, clearing
// the clip indicator.
// Fork (ux-overhaul.md P6c, §9 row 14): Attributes, Cut / Copy / Paste, Search Online and the machine's own Commands (the list a native
// machine reports, with its submenus) in a machine's menu; Paste on empty space; a group's menu (Rename, Fold, Delete).
// Fork (mcp-server.md §12.11, U4): "Editor..." opens a machine's manifest editor, and "Double-click Opens
// Editor" is its per-gear setting (on by default for curated manifests); "Parameters..." stays the plain list.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using BuzzGUI.Common.InterfaceExtensions;
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

        static MenuItem Check(string header, bool on, Action run, bool enabled = true, KeyGesture key = null) =>
            new MenuItem { Header = header, Command = new Command(run), IsEnabled = enabled, ToggleType = MenuItemToggleType.CheckBox, IsChecked = on, InputGesture = key };

        /// <summary>Delete's key as the platform has it (ux-overhaul.md §4): ⌫ on a Mac (MacBooks have no ⌦), Delete elsewhere.</summary>
        static KeyGesture DeleteKey => new KeyGesture(OperatingSystem.IsMacOS() ? Key.Back : Key.Delete);

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
            menu.Items.Add(Item("Paste", () => canvas.PasteSelection(logical), MachineClipboard.CanPaste(core), new KeyGesture(Key.V, CommandKey)));
            menu.Items.Add(Item("Unmute All Machines", canvas.UnmuteAll, key: new KeyGesture(Key.U, CommandKey)));
            if (core.SongCore.MachineGroups.Count > 0)
            {
                var groups = core.SongCore.MachineGroups.ToList();
                menu.Items.Add(Sub("Rename Group", groups.Select(g => (object)Item(g.Name, () => RenameGroup(g)))));
                menu.Items.Add(Sub("Fold Group", groups.Select(g => (object)Check(g.Name, g.IsGrouped, () => { MachineEdits.ToggleFold(core, g, canvas); }))));
                menu.Items.Add(Sub("Delete Group", groups.Select(g => (object)Item(g.Name, () => { MachineEdits.DeleteGroup(core, g); canvas.Redraw(); }))));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Snap Machines to Grid", () => MachineEdits.Snap(core, canvas.Selection.Count > 0 ? canvas.Selection.ToList() : core.SongCore.Machines.ToList(), 10)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Select All", canvas.SelectAll, key: new KeyGesture(Key.A, CommandKey)));
            menu.Items.Add(Item("Deselect All", canvas.ClearSelection, canvas.Selection.Count > 0, new KeyGesture(Key.A, CommandKey | KeyModifiers.Shift)));
            return menu;
        }

        /// <summary>NW1: "Send notes to" (a machine that sends notes: the machines that take them, ticked when wired) and "Note chain" (a machine that takes notes: put it before an instrument or take it out).</summary>
        List<MenuItem> NoteItems(IMachine machine)
        {
            var store = core.NoteWires;
            var ports = store.PortsOf(machine.Name);
            var root = new List<MenuItem>();
            if (ports is not { Control: false } p || !(p.In || p.Out)) return root;
            if (p.Out)
            {
                var wired = store.State.Wires.Where(w => w.From == machine.Name).Select(w => w.To).ToHashSet();
                var items = store.NoteTargetCandidates(machine).Select(n =>
                {
                    string name = n;
                    string why = store.WhyNot(machine.Name, name);
                    bool on = wired.Contains(name);
                    var it = Check(name, on, () => { if (on) store.Disconnect(machine.Name, name); else store.Connect(machine.Name, name); }, enabled: on || why == null);
                    if (why != null && !on) ToolTip.SetTip(it, why);
                    return (object)it;
                }).ToList();
                if (items.Count == 0) items.Add(Item("No machine takes notes", () => { }, false));
                root.Add(Sub("Send notes to", items));
            }
            if (p.In && p.Out)
            {
                var inst = store.State.InstrumentOf(machine.Name);
                var targets = core.SongCore.Machines.Where(t => t != machine && store.PortsOf(t.Name) is { In: true, Control: false }).Select(t => t.Name).ToList();
                var items = targets.Select(t => (object)Item(t, () => store.AddInsert(t, machine.Name))).ToList();
                if (inst != null) items.Add(Item("Take out of " + inst + "'s chain", () => store.RemoveInsert(machine.Name)));
                root.Add(Sub("Note insert before", items));
            }
            return root;
        }

        /// <summary>NW1: a note wire's menu.</summary>
        public ContextMenu ForNoteWire(ReBuzz.Core.NoteWires.NoteEdge edge)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Remove", () => canvas.RemoveNoteWire(edge)));
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
                menu.Items.Add(Item("Signal Analysis...", () => Shell.Tools.SignalAnalysisWindow.Open(Shell.StudioHost.Current, machine)));
                if (core.MasterOver)
                    menu.Items.Add(Item($"Clear clip indicator ({core.MasterOverText})", core.ClearMasterOver));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Paste", () => canvas.PasteSelection(), MachineClipboard.CanPaste(core), new KeyGesture(Key.V, CommandKey)));
                return menu;
            }

            if ((machine as MachineCore)?.IsDiverged == true)
            {
                // One undo step; the guard trips again if it is still broken.
                menu.Items.Add(Item("Reset and Unmute", () => MachineEdits.Mute(core, new[] { machine }, false)));
                menu.Items.Add(new Separator());
            }

            bool generator = type == MachineType.Generator;
            menu.Items.Add(Check("Mute", machine.IsMuted, () => MachineEdits.Mute(core, selection, !machine.IsMuted), key: new KeyGesture(Key.M)));
            if (generator)
                menu.Items.Add(Check("Solo", machine.IsSoloed, () => MachineEdits.Solo(core, selection, !machine.IsSoloed), key: new KeyGesture(Key.S)));
            else
                menu.Items.Add(Check("Bypass", machine.IsBypassed, () => MachineEdits.Bypass(core, selection, !machine.IsBypassed), key: new KeyGesture(Key.B)));
            menu.Items.Add(Check("Oversample", machine.OversampleFactor != 1, () => machine.OversampleFactor = machine.OversampleFactor != 1 ? 1 : 2,
                (generator && !machine.IsControlMachine) || machine.DLL.Info.Flags.HasFlag(MachineInfoFlags.MULTI_IO)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Parameters...", () => MachineEdits.OpenParameters(machine)));
            menu.Items.Add(Sub("Attributes", Attributes(machine)));
            if (ReBuzz.Agent.ManifestEditorSettings.Available(machine.DLL))
            {
                menu.Items.Add(Item("Editor...", () => core.UiHost?.OpenEditor(machine)));
                bool opens = ReBuzz.Agent.ManifestEditorSettings.OpensEditor(machine.DLL);
                menu.Items.Add(Check("Double-click Opens Editor", opens, () => ReBuzz.Agent.ManifestEditorSettings.Set(machine.DLL, !opens)));
            }
            menu.Items.Add(Sub("MIDI Input Channel", MidiChannels(machine)));
            foreach (var note in NoteItems(machine)) menu.Items.Add(note);
            menu.Items.Add(Item("Latency...", () => _ = Shell.Tools.LatencyDialog.Ask(machine), !machine.IsControlMachine));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Signal Analysis...", () => Shell.Tools.SignalAnalysisWindow.Open(Shell.StudioHost.Current, machine)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Sub("Replace", Gear(i => MachineEdits.Replace(core, machine, i))));
            // BR1 (docs/specs/browser.md): the browser opens in swap mode for this machine; the next double-click on a result replaces it.
            menu.Items.Add(Item("Hot-swap", () => canvas.Browser?.BeginSwap(machine), machine.DLL.Info.Type != MachineType.Master));
            menu.Items.Add(Item("Duplicate", () => { var copies = MachineEdits.Duplicate(core, selection.Where(m => m.DLL.Info.Type != MachineType.Master).ToList()); if (copies.Count > 0) canvas.Select(copies); },
                key: new KeyGesture(Key.D, CommandKey)));
            menu.Items.Add(Item("Clone with patterns", () => CloneWithClips(selection)));
            menu.Items.Add(Sub("Group", GroupItems(machine, selection)));
            menu.Items.Add(Item("Rename...", () => MachineEdits.Rename(machine), key: new KeyGesture(Key.R, CommandKey)));
            menu.Items.Add(Item("Delete", () => MachineEdits.Delete(core, selection), key: DeleteKey));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Cut", () => MachineClipboard.Cut(core, selection), key: new KeyGesture(Key.X, CommandKey)));
            menu.Items.Add(Item("Copy", () => MachineClipboard.Copy(core, selection), key: new KeyGesture(Key.C, CommandKey)));
            menu.Items.Add(Item("Paste", () => canvas.PasteSelection(), MachineClipboard.CanPaste(core), new KeyGesture(Key.V, CommandKey)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Search Online", () => SearchOnline(machine)));
            // The machine's own commands (a native machine's list, with its submenus), as WPF appends them.
            var commands = Commands(machine);
            if (commands.Count > 0)
            {
                menu.Items.Add(new Separator());
                foreach (var c in commands) menu.Items.Add(c);
            }
            return menu;
        }

        /// <summary>A group's menu: rename, fold or unfold, delete (GroupControl.CreateMenu).</summary>
        public ContextMenu ForGroup(IMachineGroup group)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Rename...", () => RenameGroup(group)));
            menu.Items.Add(Item(group.IsGrouped ? "Unfold" : "Fold", () => MachineEdits.ToggleFold(core, group, canvas)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Delete Group", () => { MachineEdits.DeleteGroup(core, group); canvas.Redraw(); }));
            return menu;
        }

        /// <summary>Asks for a group's new name (the engine's prompt: IMachineUiHost.AskName) and renames it, undoably.</summary>
        void RenameGroup(IMachineGroup group)
        {
            group.ShowDialog(MachineGroupDialog.Rename, 0, 0);
            canvas.Redraw();
        }

        /// <summary>Where Search Online goes (Buzz's machine search, as the WPF menu does); tests replace the launcher.</summary>
        public static Action<Uri> Launch { get; set; }

        /// <summary>The Buzz machine search for a machine: its first word of the DLL name.</summary>
        public static Uri SearchUri(IMachine machine)
        {
            var words = (machine.DLL?.Name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length == 0 ? null : new Uri("https://buzz.robotplanet.dk/search.php?q=" + Uri.EscapeDataString(words[0]));
        }

        void SearchOnline(IMachine machine)
        {
            var uri = SearchUri(machine);
            if (uri == null) return;
            if (Launch != null) Launch(uri);
            else _ = TopLevel.GetTopLevel(canvas)?.Launcher.LaunchUriAsync(uri);
        }

        /// <summary>The machine's attributes: each a submenu of its values (the current ticked, the default in bold), "Other..." for a big range.</summary>
        IEnumerable<object> Attributes(IMachine machine) => AttributeItems(machine.Attributes, canvas);

        internal static IEnumerable<object> AttributeItems(IEnumerable<IAttribute> attributes, Visual owner)
        {
            foreach (var a in attributes)
            {
                var attribute = a;
                var values = attribute.GetPresentableNumberOfValues().ToList();
                var sub = new MenuItem { Header = attribute.Name };
                foreach (int v in values)
                {
                    int value = v;
                    sub.Items.Add(new MenuItem
                    {
                        Header = value.ToString(),
                        Command = new Command(() => attribute.Value = value),
                        ToggleType = MenuItemToggleType.Radio,
                        GroupName = "attribute " + attribute.Name,
                        IsChecked = value == attribute.Value,
                        FontWeight = value == attribute.DefValue ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal,
                    });
                }
                if (values.Count < attribute.MaxValue - attribute.MinValue + 1)
                {
                    sub.Items.Add(new Separator());
                    sub.Items.Add(Item("Other...", async () =>
                    {
                        var v = await Parameters.ParameterDialogs.AskValue(owner, new Point(40, 40), attribute.Value, attribute.MinValue, attribute.MaxValue, replace: true);
                        if (v is int value) attribute.Value = value;
                    }));
                }
                yield return sub;
            }
        }

        /// <summary>The machine's own commands as menu items (its Commands list; a native machine's, with the submenus it reports).</summary>
        public static List<object> Commands(IMachine machine)
        {
            var items = new List<object>();
            IEnumerable<IMenuItem> commands;
            try
            {
                commands = machine.Commands?.ToList();
            }
            catch (Exception)
            {
                return items;
            }
            if (commands == null) return items;
            foreach (var c in commands) items.Add(FromBuzz(c));
            return items;
        }

        /// <summary>One Buzz menu item (IMenuItem) as an Avalonia one, children and all.</summary>
        internal static object FromBuzz(IMenuItem item)
        {
            if (item.IsSeparator) return new Separator();
            var m = new MenuItem
            {
                Header = item.Text,
                IsEnabled = item.IsEnabled && !item.IsLabel,
                Command = item.Command,
                CommandParameter = item.CommandParameter,
            };
            if (item.IsCheckable)
            {
                m.ToggleType = MenuItemToggleType.CheckBox;
                m.IsChecked = item.IsChecked;
            }
            if (item.IsDefault) m.FontWeight = Avalonia.Media.FontWeight.Bold;
            if (item.Children != null)
                foreach (var child in item.Children) m.Items.Add(FromBuzz(child));
            return m;
        }

        /// <summary>The Group submenu (MachineControl's): a new group from the selection, into an existing one, out of its group.</summary>
        IEnumerable<object> GroupItems(IMachine machine, IReadOnlyList<IMachine> selection)
        {
            var song = core.SongCore;
            yield return Item("New Group From Selection", () =>
            {
                var g = MachineEdits.NewGroup(core, selection);
                if (g != null) canvas.Select(selection.Where(m => m.DLL.Info.Type != MachineType.Master).ToList());
                canvas.Redraw();
            });
            if (song.MachineGroups.Count > 0)
            {
                yield return new Separator();
                foreach (var g in song.MachineGroups.ToList())
                {
                    var group = g;
                    yield return Check("Add To " + group.Name, song.MachineToGroupDict.TryGetValue(machine, out var own) && own == group,
                        () => { MachineEdits.AddToGroup(core, selection, group); canvas.Redraw(); });
                }
            }
            yield return new Separator();
            yield return Item("Remove From Group", () => { MachineEdits.RemoveFromGroup(core, selection); canvas.Redraw(); }, song.MachineToGroupDict.ContainsKey(machine));
            if (song.MachineToGroupDict.TryGetValue(machine, out var mine))
            {
                yield return Item("Rename Group...", () => RenameGroup(mine));
                yield return Item(mine.IsGrouped ? "Unfold Group" : "Fold Group", () => MachineEdits.ToggleFold(core, mine, canvas));
            }
        }

        /// <summary>Clone with patterns on the selection (the machine's menu); the copies are selected.</summary>
        void CloneWithClips(IReadOnlyList<IMachine> selection)
        {
            var from = selection.Where(m => m.DLL.Info.Type != MachineType.Master).ToList();
            if (from.Count == 0) return;
            try
            {
                var copies = MachineEdits.CloneWithClips(core, from);
                if (copies.Count > 0) canvas.Select(copies);
            }
            catch (Exception e)
            {
                core.DCWriteLine("Clone failed: " + e.Message, DCLogLevel.Error);
            }
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
