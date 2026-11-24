// Fork (docs/specs/ux-overhaul.md P6c, §9): the last Windows-parity items, headless: a native machine's own window in a studio window
// (the decision, the fallback, the platform seam; the Win32 reparenting on the Windows runner); Cut, Copy and Paste of machines; the
// machine's Attributes, Search Online and own Commands in its menu; the on-screen piano keyboard; groups folded, moved and renamed.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Tools;
using ReBuzz.Studio.Views.Machines;
using static ReBuzz.Studio.Tests.MachineViewTests;
using static ReBuzz.Studio.Tests.UxP4aTests;

namespace ReBuzz.Studio.Tests
{
    public class UxP6cTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static IMachine M(string name) => Core.SongCore.Machines.First(m => m.Name == name);

        static int Listeners(object target, string eventName)
        {
            var f = target.GetType().GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return (f.GetValue(target) as Delegate)?.GetInvocationList().Length ?? 0;
        }

        static string Headers(ItemsControl menu) => string.Join("|", menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()));

        static MenuItem Item(ItemsControl menu, string header) => menu.Items.OfType<MenuItem>().First(i => i.Header?.ToString() == header);

        // ------------------------------------------------------------------ Cut, Copy and Paste of machines

        static (MainWindow Window, MachineView View) MachinesWithEcho()
        {
            var window = SongWith("Neo Poly", 8);
            Call("machine_add", Args(("gear", "Neo Delay"), ("name", "Echo")));
            Call("connect", Args(("from", "Voice"), ("to", "Echo")));
            window.ShowView("Machines");
            Settle();
            var view = window.Tabs.Items.Cast<TabItem>().Select(t => t.Content).OfType<MachineView>().First();
            MachineClipboard.Clear();
            return (window, view);
        }

        [AvaloniaTest]
        public void CopyAndPasteMakeTheMachinesAgainWithPatternsRowsAndWiresInOneUndoStepAndPasteAgainWorks()
        {
            var (window, view) = MachinesWithEcho();
            using var restore = Studio.AfterTest(() => { MachineClipboard.Clear(); Restore(window, null); });
            var song = Core.SongCore;
            int machines = song.Machines.Count, sequences = song.Sequences.Count;
            Assert.That(MachineClipboard.CanPaste(Core), Is.False, "nothing held");
            Assert.That(view.Paste(), Is.False);
            Assert.That(MachineClipboard.Copy(Core, new[] { M("Master") }), Is.False, "the Master is never copied");
            view.Canvas.Select(new[] { M("Voice"), M("Echo") });
            Assert.That(view.Copy(), Is.True);
            Assert.That(MachineClipboard.Count, Is.EqualTo(2));
            Assert.That(song.Machines.Count, Is.EqualTo(machines), "copying changes nothing");
            Assert.That(view.Paste(), Is.True);
            Assert.That(song.Machines.Count, Is.EqualTo(machines + 2));
            Assert.That(song.Sequences.Count, Is.EqualTo(sequences + 1), "the sequence row came along");
            var copies = view.Canvas.Selection.ToList();
            Assert.That(copies, Has.Count.EqualTo(2));
            Assert.That(copies.All(m => m.Name != "Voice" && m.Name != "Echo"), "named apart from the originals");
            Assert.That(copies.First(m => m.Patterns.Count > 0).Patterns.Select(p => p.Name), Is.EqualTo(new[] { "P" }));
            Assert.That(copies[0].Outputs.Select(c => c.Destination), Contains.Item(copies[1]), "the wire between the two");
            song.ActionStack.Undo();
            Assert.That(song.Machines.Count, Is.EqualTo(machines), "one undo step");
            Assert.That(song.Sequences.Count, Is.EqualTo(sequences));
            Assert.That(view.Paste(), Is.True, "a copy pastes again and again");
            Assert.That(song.Machines.Count, Is.EqualTo(machines + 2));
        }

        [AvaloniaTest]
        public void PastingAtAPointPutsTheMiddleOfTheCopiesThereAndKeepsTheirLayout()
        {
            var (window, view) = MachinesWithEcho();
            using var restore = Studio.AfterTest(() => { MachineClipboard.Clear(); Restore(window, null); });
            var voice = M("Voice"); var echo = M("Echo");
            Core.SongCore.MoveMachines(new[] { Tuple.Create(voice, Tuple.Create(-0.4f, -0.3f)), Tuple.Create(echo, Tuple.Create(-0.2f, -0.3f)) });
            MachineClipboard.Copy(Core, new[] { voice, echo });
            var at = new Point(0.5, 0.4);
            var copies = MachineClipboard.Paste(Core, at);
            Assert.That(copies, Has.Count.EqualTo(2));
            Assert.That((copies[0].Position.Item1 + copies[1].Position.Item1) / 2, Is.EqualTo(0.5).Within(0.001));
            Assert.That((copies[0].Position.Item2 + copies[1].Position.Item2) / 2, Is.EqualTo(0.4).Within(0.001));
            Assert.That(copies[1].Position.Item1 - copies[0].Position.Item1, Is.EqualTo(0.2).Within(0.001), "the layout between them is kept");
        }

        [AvaloniaTest]
        public void CutDeletesNothingUntilThePasteThenTheCopiesTakeTheOriginalsPlaceAndNames()
        {
            var (window, view) = MachinesWithEcho();
            using var restore = Studio.AfterTest(() => { MachineClipboard.Clear(); Restore(window, null); });
            var song = Core.SongCore;
            int machines = song.Machines.Count;
            view.Canvas.Select(new[] { M("Voice"), M("Echo") });
            Assert.That(view.Cut(), Is.True);
            Assert.That(MachineClipboard.IsCut);
            Assert.That(song.Machines.Count, Is.EqualTo(machines), "cut waits for the paste");
            Assert.That(M("Voice").Patterns.Count, Is.EqualTo(1));
            var copies = MachineClipboard.Paste(Core, new Point(0.3, -0.2));
            Assert.That(song.Machines.Count, Is.EqualTo(machines), "the same number: moved");
            Assert.That(copies.Select(m => m.Name), Is.EqualTo(new[] { "Voice", "Echo" }), "with their names");
            Assert.That(copies[0].Patterns.Select(p => p.Name), Is.EqualTo(new[] { "P" }));
            Assert.That(copies[0].Outputs.Select(c => c.Destination), Contains.Item(copies[1]));
            Assert.That(MachineClipboard.Count, Is.EqualTo(0), "a cut is pasted once");
            song.ActionStack.Undo();
            Assert.That(song.Machines.Count, Is.EqualTo(machines));
            Assert.That(M("Voice").Patterns.Count, Is.EqualTo(1), "one undo step brings the originals back");
        }

        [AvaloniaTest]
        public void MachinesNoLongerInTheSongCannotBePasted()
        {
            var (window, view) = MachinesWithEcho();
            using var restore = Studio.AfterTest(() => { MachineClipboard.Clear(); Restore(window, null); });
            MachineClipboard.Copy(Core, new[] { M("Echo") });
            Assert.That(MachineClipboard.CanPaste(Core));
            MachineEdits.Delete(Core, new[] { M("Echo") });
            Assert.That(MachineClipboard.CanPaste(Core), Is.False, "the copy is of machines that are gone");
            Assert.That(MachineClipboard.Paste(Core, null), Is.Empty);
        }

        [AvaloniaTest]
        public void TheMenusOfAMachineTheMasterAndEmptySpaceOfferCutCopyAndPaste()
        {
            var (window, view) = MachinesWithEcho();
            using var restore = Studio.AfterTest(() => { MachineClipboard.Clear(); Restore(window, null); });
            var menus = view.Menus;
            var voice = menus.ForMachine(M("Voice"), new[] { M("Voice") });
            Assert.That(Headers(voice).Split('|'), Is.SupersetOf(new[] { "Attributes", "Cut", "Copy", "Paste", "Search Online", "Group" }));
            Assert.That(Item(voice, "Paste").IsEnabled, Is.False, "nothing to paste yet");
            Assert.That(Item(voice, "Attributes").IsEnabled, Is.False, "a machine without attributes: nothing to open");
            Item(voice, "Copy").Command.Execute(null);
            Assert.That(MachineClipboard.Count, Is.EqualTo(1));
            var again = menus.ForMachine(M("Echo"), new[] { M("Echo") });
            Assert.That(Item(again, "Paste").IsEnabled);
            var canvasMenu = menus.ForCanvas(new Point(300, 300));
            Assert.That(Item(canvasMenu, "Paste").IsEnabled);
            int machines = Core.SongCore.Machines.Count;
            Item(canvasMenu, "Paste").Command.Execute(null);
            Assert.That(Core.SongCore.Machines.Count, Is.EqualTo(machines + 1), "the menu pastes where it was opened");
            Assert.That(Item(menus.ForMachine(M("Master"), new[] { M("Master") }), "Paste").IsEnabled);
        }

        [AvaloniaTest]
        public void TheEditMenuCommandsReachTheMachineViewAndTheKeysAreTheKeymaps()
        {
            var (window, view) = MachinesWithEcho();
            using var restore = Studio.AfterTest(() => { MachineClipboard.Clear(); Restore(window, null); });
            window.ShowView("Machines");
            Settle();
            view.Canvas.Select(new[] { M("Echo") });
            int machines = Core.SongCore.Machines.Count;
            window.Focus();
            window.KeyPressQwerty(PhysicalKey.C, Cmd);
            window.KeyPressQwerty(PhysicalKey.V, Cmd);
            Settle();
            Assert.That(Core.SongCore.Machines.Count, Is.EqualTo(machines + 1), "Ctrl/Cmd+C then +V in the Machine View");
        }

        // ------------------------------------------------------------------ Attributes, Search Online, a machine's own commands

        sealed class FakeAttribute : IAttribute
        {
            public FakeAttribute(string name, int min, int max, int def, int value) { Name = name; MinValue = min; MaxValue = max; DefValue = def; Value = value; }
            public string Name { get; }
            public int MinValue { get; }
            public int MaxValue { get; }
            public int DefValue { get; }
            public int Value { get; set; }
            public bool HasUserDefValue => false;
            public int UserDefValue => 0;
            public bool UserDefValueOverridesPreset => false;
        }

        [AvaloniaTest]
        public void AttributesAreSubmenusOfTheirValuesWithTheCurrentTickedTheDefaultBoldAndOtherForABigRange()
        {
            var small = new FakeAttribute("Mode", 0, 3, 1, 2);
            var big = new FakeAttribute("Length", 0, 100000, 4000, 8000);
            var items = MachineMenus.AttributeItems(new IAttribute[] { small, big }, new Border()).OfType<MenuItem>().ToList();
            Assert.That(items.Select(i => i.Header), Is.EqualTo(new[] { "Mode", "Length" }));
            var values = items[0].Items.OfType<MenuItem>().ToList();
            Assert.That(values.Select(i => i.Header), Is.EqualTo(new[] { "0", "1", "2", "3" }));
            Assert.That(values.Select(i => i.IsChecked), Is.EqualTo(new[] { false, false, true, false }));
            Assert.That(values[1].FontWeight, Is.EqualTo(Avalonia.Media.FontWeight.Bold), "the default");
            values[3].Command.Execute(null);
            Assert.That(small.Value, Is.EqualTo(3));
            Assert.That(items[1].Items.OfType<MenuItem>().Last().Header, Is.EqualTo("Other..."));
            Assert.That(items[0].Items.OfType<MenuItem>().Any(i => i.Header?.ToString() == "Other..."), Is.False, "a small range has every value");
        }

        [AvaloniaTest]
        public void TheMachinesCommandsBecomeMenuItemsWithSubmenusSeparatorsChecksAndTheirCommands()
        {
            int run = 0;
            var tree = new MenuItemVM
            {
                Text = "Tools",
                Children = new IMenuItem[]
                {
                    new MenuItemVM { Text = "_Reset", Command = new SimpleCommand { ExecuteDelegate = x => run++ } },
                    new MenuItemVM { IsSeparator = true },
                    new MenuItemVM { Text = "Loud", IsCheckable = true, IsChecked = true },
                    new MenuItemVM { Text = "Off", IsEnabled = false },
                },
            };
            var item = (MenuItem)MachineMenus.FromBuzz(tree);
            Assert.That(item.Header, Is.EqualTo("Tools"));
            Assert.That(item.Items.Count, Is.EqualTo(4));
            var reset = (MenuItem)item.Items[0];
            Assert.That(reset.Header, Is.EqualTo("_Reset"));
            reset.Command.Execute(null);
            Assert.That(run, Is.EqualTo(1));
            Assert.That(item.Items[1], Is.TypeOf<Separator>());
            var loud = (MenuItem)item.Items[2];
            Assert.That((loud.ToggleType, loud.IsChecked), Is.EqualTo((MenuItemToggleType.CheckBox, true)));
            Assert.That(((MenuItem)item.Items[3]).IsEnabled, Is.False);
            // A machine with no commands adds nothing to its menu.
            Assert.That(MachineMenus.Commands(M("Master")), Is.Empty);
        }

        [AvaloniaTest]
        public void SearchOnlineSearchesTheBuzzSiteForTheFirstWordOfTheGearsName()
        {
            var (window, view) = MachinesWithEcho();
            var before = MachineMenus.Launch;
            using var restore = Studio.AfterTest(() => { MachineMenus.Launch = before; MachineClipboard.Clear(); Restore(window, null); });
            Uri opened = null;
            MachineMenus.Launch = u => opened = u;
            var uri = MachineMenus.SearchUri(M("Voice"));
            Assert.That(uri.Host, Is.EqualTo("buzz.robotplanet.dk"));
            Assert.That(uri.Query, Is.EqualTo("?q=" + Uri.EscapeDataString(M("Voice").DLL.Name.Split(' ')[0])));
            Item(view.Menus.ForMachine(M("Voice"), new[] { M("Voice") }), "Search Online").Command.Execute(null);
            Assert.That(opened, Is.EqualTo(uri));
        }

        // ------------------------------------------------------------------ the on-screen piano keyboard

        [AvaloniaTest]
        public void ThePianoKeyboardPlaysTheMachineWithTheMidiFocusFollowsItAndHoldsNothingWhenClosed()
        {
            var (window, view) = MachinesWithEcho();
            using var restore = Studio.AfterTest(() => { PianoKeyboardWindow.Current?.Close(); Restore(window, null); });
            Assert.That(window.Transport.MidiFocus.Keys.IsVisible);
            int listeners = Listeners(Core, "PropertyChanged");
            Core.MIDIFocusMachine = M("Voice");
            Settle();
            window.Transport.MidiFocus.Keys.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle();
            var keys = PianoKeyboardWindow.Current;
            Assert.That(keys, Is.Not.Null, "the Keys button opens it");
            Assert.That(keys.Playing, Is.SameAs(M("Voice")));
            Assert.That(keys.Heading, Is.EqualTo("Playing Voice"));
            Assert.That(keys.Keyboard, Is.Not.Null);
            Assert.That(keys.Keyboard.Shown, Is.True, "always open here");
            Assert.That(Listeners(Core, "PropertyChanged"), Is.EqualTo(listeners + 1), "one subscription while open");
            Core.MIDIFocusMachine = M("Echo");
            Settle();
            keys.Rebuild();
            Assert.That(keys.Playing, Is.SameAs(M("Echo")), "it follows the focus");
            Assert.That(keys.Heading, Does.Contain("Echo"));
            Core.MIDIFocusMachine = M("Voice");
            keys.Rebuild();
            Assert.That(PianoKeyboardWindow.Open(Studio.Host), Is.SameAs(keys), "one window");
            keys.Close();
            Settle();
            Assert.That(PianoKeyboardWindow.Current, Is.Null);
            Assert.That(Listeners(Core, "PropertyChanged"), Is.EqualTo(listeners), "closed: no subscription left");
        }

        [AvaloniaTest]
        public void ThePianoKeyboardSaysWhenNoMachineHasTheFocusOrTheMachinePlaysNoNotes()
        {
            var window = SongWith("Neo Poly", 8);
            using var restore = Studio.AfterTest(() => { PianoKeyboardWindow.Current?.Close(); Restore(window, null); });
            Call("machine_add", Args(("gear", "Neo Utility"), ("name", "Plain")));
            Core.MIDIFocusMachine = M("Plain");
            var keys = PianoKeyboardWindow.Open(Studio.Host);
            Settle();
            Assert.That(keys.Keyboard, Is.Null);
            Assert.That(keys.Heading, Is.EqualTo("Plain plays no notes."));
            Core.MIDIFocusMachine = null;
            keys.Rebuild();
            Assert.That(keys.Heading, Does.StartWith("No machine has the MIDI focus"));
        }

        // ------------------------------------------------------------------ groups: fold, move, rename

        static (MainWindow Window, MachineView View, IMachineGroup Group) Grouped()
        {
            var (window, view) = MachinesWithEcho();
            var group = MachineEdits.NewGroup(Core, new[] { M("Voice"), M("Echo") });
            view.Canvas.Redraw();
            Settle();
            return (window, view, group);
        }

        [AvaloniaTest]
        public void FoldingAGroupMovesItsMachinesOntoOneBoxAndOpeningPutsThemBackEachOneUndoStep()
        {
            var (window, view, group) = Grouped();
            using var restore = Studio.AfterTest(() => Restore(window, null));
            var voice = M("Voice"); var echo = M("Echo");
            var voiceAt = voice.Position; var echoAt = echo.Position;
            var canvas = view.Canvas;
            canvas.Select(new[] { voice });
            Assert.That(canvas.IsFolded(voice), Is.False);
            MachineEdits.ToggleFold(Core, group, canvas);
            Settle();
            Assert.That(group.IsGrouped, Is.True);
            Assert.That(canvas.IsFolded(voice) && canvas.IsFolded(echo));
            Assert.That(voice.Position, Is.EqualTo(group.Position), "onto the box");
            Assert.That(echo.Position, Is.EqualTo(group.Position));
            Assert.That(canvas.MachineAt(canvas.FoldedBoxOf(group).Center), Is.Null, "a folded machine can't be pointed at");
            Assert.That(canvas.GroupAt(canvas.FoldedBoxOf(group).Center), Is.SameAs(group));
            Assert.That(canvas.Wires, Does.Not.Contain(voice.Outputs.First(c => c.Destination == echo)), "a wire inside the group is not drawn");
            Assert.That(canvas.Wires, Contains.Item(echo.Outputs.First(c => c.Destination.DLL.Info.Type == MachineType.Master)), "one out of it is");
            Assert.That(canvas.Selection, Does.Not.Contain(voice), "folded machines leave the selection");
            MachineEdits.ToggleFold(Core, group, canvas);
            Assert.That(group.IsGrouped, Is.False);
            Assert.That(voice.Position, Is.EqualTo(voiceAt), "back where it was");
            Assert.That(echo.Position, Is.EqualTo(echoAt));
            // Each is one undo step: undo of the fold puts the open state back, with the machines where they were.
            MachineEdits.Fold(Core, group);
            Assert.That(group.IsGrouped);
            Core.SongCore.ActionStack.Undo();
            Assert.That(group.IsGrouped, Is.False);
            Assert.That(voice.Position, Is.EqualTo(voiceAt));
            Core.SongCore.ActionStack.Redo();
            Assert.That(group.IsGrouped);
            Assert.That(voice.Position, Is.EqualTo(group.Position));
            MachineEdits.Unfold(Core, group);
        }

        [AvaloniaTest]
        public void ADoubleClickOnTheTabOrTheFoldedBoxFoldsAndOpensTheGroupAndTheTabTakesNothingElse()
        {
            var (window, view, group) = Grouped();
            using var restore = Studio.AfterTest(() => Restore(window, null));
            var canvas = view.Canvas;
            var tab = canvas.TabOf(group);
            Assert.That(tab, Is.Not.Null);
            Assert.That(canvas.GroupAt(tab.Value.Center), Is.SameAs(group));
            var frame = canvas.FrameOf(group).Value;
            Assert.That(canvas.GroupAt(new Point(frame.Center.X, frame.Bottom - 2)), Is.Null, "the frame's body lets the band and the machines through");
            DoubleClick(window, view.FromCanvas(tab.Value.Center, window));
            Assert.That(group.IsGrouped, Is.True, "a double click on the tab folds");
            DoubleClick(window, view.FromCanvas(canvas.FoldedBoxOf(group).Center, window));
            Assert.That(group.IsGrouped, Is.False, "and on the box opens");
        }

        [AvaloniaTest]
        public void DraggingAGroupMovesItAndItsMachinesAsOneUndoStepFoldedOrOpen()
        {
            var (window, view, group) = Grouped();
            using var restore = Studio.AfterTest(() => Restore(window, null));
            var canvas = view.Canvas;
            var voice = M("Voice"); var echo = M("Echo");
            var v0 = voice.Position; var e0 = echo.Position;
            var tab = canvas.TabOf(group).Value;
            var from = view.FromCanvas(tab.Center, window);
            Drag(window, from, from + new Vector(60, 40));
            Settle();
            Assert.That(voice.Position.Item1, Is.GreaterThan(v0.Item1), "the machines moved with their group");
            Assert.That(echo.Position.Item2, Is.GreaterThan(e0.Item2));
            Assert.That(echo.Position.Item1 - voice.Position.Item1, Is.EqualTo(e0.Item1 - v0.Item1).Within(0.001), "keeping their layout");
            Core.SongCore.ActionStack.Undo();
            Assert.That(voice.Position, Is.EqualTo(v0), "one undo step");
            Assert.That(echo.Position, Is.EqualTo(e0));

            // A folded group moves as a box; opened again, its machines are where the box went, in their old layout.
            MachineEdits.Fold(Core, group);
            canvas.Redraw();
            Settle();
            var gp = group.Position;
            var box = view.FromCanvas(canvas.FoldedBoxOf(group).Center, window);
            Drag(window, box, box + new Vector(-50, 30));
            Settle();
            Assert.That(group.Position.Item1, Is.LessThan(gp.Item1));
            Assert.That(voice.Position, Is.EqualTo(group.Position), "its machines stay under the box");
            var movedBy = (group.Position.Item1 - gp.Item1, group.Position.Item2 - gp.Item2);
            MachineEdits.Unfold(Core, group);
            Assert.That(voice.Position.Item1, Is.EqualTo(v0.Item1 + movedBy.Item1).Within(0.001), "opened where it went");
            Assert.That(echo.Position.Item1 - voice.Position.Item1, Is.EqualTo(e0.Item1 - v0.Item1).Within(0.001));
        }

        [AvaloniaTest]
        public void ARenameIsAnUndoStepTheMenusOfferItAndFoldAndAnEmptyOrUnchangedNameIsRefused()
        {
            var (window, view, group) = Grouped();
            using var restore = Studio.AfterTest(() => { Dialogs.TextResponder = null; Restore(window, null); });
            string old = group.Name;
            Assert.That(MachineEdits.RenameGroup(Core, group, "  "), Is.False);
            Assert.That(MachineEdits.RenameGroup(Core, group, old), Is.False);
            Assert.That(MachineEdits.RenameGroup(Core, group, " Drums "), Is.True);
            Assert.That(group.Name, Is.EqualTo("Drums"));
            Core.SongCore.ActionStack.Undo();
            Assert.That(group.Name, Is.EqualTo(old));
            // The menus: the prompt is the engine's (the host's AskName).
            Dialogs.TextResponder = (title, current) => "Strings";
            var menus = view.Menus;
            var groupMenu = menus.ForGroup(group);
            Assert.That(Headers(groupMenu), Is.EqualTo("Rename...|Fold||Delete Group".Replace("||", "|")));
            Item(groupMenu, "Rename...").Command.Execute(null);
            Assert.That(group.Name, Is.EqualTo("Strings"));
            Item(groupMenu, "Fold").Command.Execute(null);
            Assert.That(group.IsGrouped);
            Assert.That(Headers(menus.ForGroup(group)), Does.Contain("Unfold"));
            var voice = menus.ForMachine(M("Voice"), new[] { M("Voice") });
            var sub = Item(voice, "Group");
            Assert.That(Headers(sub).Split('|'), Is.SupersetOf(new[] { "Rename Group...", "Unfold Group" }));
            var canvasMenu = menus.ForCanvas(new Point(10, 10));
            Assert.That(Headers(canvasMenu).Split('|'), Is.SupersetOf(new[] { "Rename Group", "Fold Group", "Delete Group" }));
            // Deleting a folded group opens it first: its machines are not left stacked.
            var at = M("Voice").Position;
            Item(canvasMenu, "Delete Group").Items.OfType<MenuItem>().First().Command.Execute(null);
            Assert.That(Core.SongCore.MachineToGroupDict.ContainsKey(M("Voice")), Is.False);
            Assert.That(M("Voice").Position, Is.Not.EqualTo(at), "back to its own place");
        }

        [AvaloniaTest]
        public void AMachineAddedToAFoldedGroupGoesOntoItsBoxAndOutOfItBackToItsPlace()
        {
            var (window, view, group) = Grouped();
            using var restore = Studio.AfterTest(() => Restore(window, null));
            Call("machine_add", Args(("gear", "Neo Delay"), ("name", "Third")));
            var third = M("Third");
            var at = third.Position;
            MachineEdits.Fold(Core, group);
            MachineEdits.AddToGroup(Core, new[] { third }, group);
            Assert.That(third.Position, Is.EqualTo(group.Position));
            MachineEdits.RemoveFromGroup(Core, new[] { third });
            Assert.That(third.Position, Is.EqualTo(at));
            MachineEdits.Unfold(Core, group);
        }
    }
}
