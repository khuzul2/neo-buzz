// Fork: Space plays and stops the song in every view of the main window (Shell/TransportKeys.cs,
// ReBuzz/Core/TransportCommands.cs), F5 and F8 still do; Shift+Space plays the pattern editor's pattern
// from its cursor. Not while typing in a box, and a focused button doesn't also click. Headless, with
// the keys simulated.

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Machines;
using ReBuzz.Studio.Views.Notes;
using ReBuzz.Studio.Views.Sequence;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class TransportKeysTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static void Settle() => MachineViewTests.Settle();

        static void Space(MainWindow window, RawInputModifiers mods = RawInputModifiers.None)
        {
            window.KeyPressQwerty(PhysicalKey.Space, mods);
            window.KeyReleaseQwerty(PhysicalKey.Space, mods);
            Settle();
        }

        static Control Focused(MainWindow window) => window.FocusManager?.GetFocusedElement() as Control;

        static void Focus(Control c)
        {
            Assert.That(c.Focus(), Is.True, $"{c.GetType().Name} takes the focus");
            Settle();
        }

        /// <summary>The transport stopped and the song left unmodified, whatever the test did.</summary>
        static void PutBack(MainWindow window)
        {
            if (Core.SoloPattern is { } solo) solo.IsPlayingSolo = false;
            Core.Playing = false;
            Core.Modified = false;
            window?.Close();
            Settle();
            Dialogs.Responder = null;
        }

        /// <summary>A Neo Poly with a pattern, shown in the Pattern tab.</summary>
        static IPattern PatternShown(string name)
        {
            Core.SongCore.CreateMachine("Neo Poly", null, name, null, null, null, -1, 0.3f, 0.3f);
            var machine = Core.SongCore.MachinesList.First(m => m.Name == name);
            machine.CreatePattern("00", 16);
            var pattern = machine.Patterns.First(p => p.Name == "00");
            PatternView.Edit(pattern);
            Settle();
            Assert.That(PatternView.Page?.EditorControl, Is.Not.Null, "the Modern Pattern Editor's view is in the tab");
            return pattern;
        }

        static Control PatternGrid() => (Control)((dynamic)PatternView.Page.EditorControl).TrackerGrid.Body;

        [AvaloniaTest]
        public void SpacePlaysAndStopsTheSongInEveryView()
        {
            var (window, _) = MachineViewTests.OpenEmpty();
            try
            {
                PatternShown("Space Poly");
                Control Target(string title)
                {
                    window.ShowView(title);
                    Settle();
                    var content = window.Tabs.SelectedContent;
                    return title switch
                    {
                        "Machines" => ((MachineView)content).Canvas,
                        "Sequence" => (SequenceView)content,
                        "Session" => (SessionView)content,
                        "Pattern" => PatternGrid(),
                        _ => throw new ArgumentException(title),
                    };
                }

                foreach (var title in new[] { "Machines", "Pattern", "Sequence", "Session" })
                {
                    var target = Target(title);
                    Focus(target);
                    Assert.That(Core.PlayRequested, Is.False, title);
                    Space(window);
                    Assert.That(Core.PlayRequested, Is.True, $"{title}: Space plays");
                    Space(window);
                    Assert.That(Core.PlayRequested, Is.False, $"{title}: Space again stops");
                    Assert.That(Focused(window), Is.SameAs(target), $"{title}: the keyboard stays on the view");
                }

                // Held, the key repeats: one press is one start.
                window.ShowView("Machines");
                Focus(Target("Machines"));
                for (int i = 0; i < 3; i++) window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                Settle();
                Assert.That(Core.PlayRequested, Is.True, "a held Space starts once");
                Space(window);
                Assert.That(Core.PlayRequested, Is.False);

                // F5 and F8 still do.
                window.KeyPressQwerty(PhysicalKey.F5, RawInputModifiers.None);
                Assert.That(Core.PlayRequested, Is.True, "F5 plays");
                window.KeyPressQwerty(PhysicalKey.F8, RawInputModifiers.None);
                Assert.That(Core.PlayRequested, Is.False, "F8 stops");
            }
            finally
            {
                PutBack(window);
            }
        }

        [AvaloniaTest]
        public void SpaceInATextBoxTypesASpaceAndLeavesTheTransport()
        {
            var (window, _) = MachineViewTests.OpenEmpty();
            try
            {
                window.ShowView(SongNotesTab.TabTitle);
                Settle();
                var notes = (SongNotesView)window.Tabs.SelectedContent;
                Focus((TabItem)window.Tabs.SelectedItem);
                Space(window);
                Assert.That(Core.PlayRequested, Is.True, "Space on the Song Notes tab plays");

                // In the notes' editor Space types (the platform's text input follows the unhandled key).
                Focus(notes.Editor);
                window.KeyTextInput("a");
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyTextInput(" ");
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyTextInput("b");
                Settle();
                Assert.That(notes.Editor.Text, Is.EqualTo("a b"), "a space in the notes");
                Assert.That(Core.PlayRequested, Is.True, "and the song still plays");
                Space(window, RawInputModifiers.Shift);
                Assert.That(Core.PlayRequested, Is.True, "Shift+Space in a box: not the transport's either");

                // The transport's BPM box (a NumericUpDown's text box).
                var bpmBox = window.Transport.GetVisualDescendants().OfType<TextBox>().First();
                Focus(bpmBox);
                Space(window);
                Assert.That(Core.PlayRequested, Is.True, "Space in the BPM box leaves the transport");

                Focus((TabItem)window.Tabs.SelectedItem);
                Space(window);
                Assert.That(Core.PlayRequested, Is.False, "out of the box, Space stops");
            }
            finally
            {
                PutBack(window);
            }
        }

        [AvaloniaTest]
        public void SpaceWithAFocusedButtonDoesntClickIt()
        {
            var (window, _) = MachineViewTests.OpenEmpty();
            bool looping = Core.Looping;
            try
            {
                var buttons = window.Transport.GetVisualDescendants().OfType<Button>().ToList();
                var stop = buttons.Single(b => b.Content as string == "■");
                var loop = buttons.OfType<ToggleButton>().Single(b => b.Content as string == "Loop");
                int clicks = 0;
                stop.Click += (s, e) => clicks++;

                Focus(stop);
                Space(window);
                Assert.That(clicks, Is.EqualTo(0), "the focused ■ isn't clicked");
                Assert.That(Core.PlayRequested, Is.True, "Space plays, ■ focused or not");
                Space(window);
                Assert.That(Core.PlayRequested, Is.False);
                Assert.That(clicks, Is.EqualTo(0));

                Focus(loop);
                Space(window);
                Assert.That(Core.Looping, Is.EqualTo(looping), "the focused Loop isn't toggled");
                Assert.That(loop.IsChecked == true, Is.EqualTo(looping));
                Assert.That(Core.PlayRequested, Is.True);
                Space(window);
                Assert.That(Core.PlayRequested, Is.False);
            }
            finally
            {
                Core.Looping = looping;
                PutBack(window);
            }
        }

        [AvaloniaTest]
        public void ShiftSpacePlaysThePatternFromTheEditorsCursor()
        {
            var (window, _) = MachineViewTests.OpenEmpty();
            try
            {
                var pattern = PatternShown("Preview Poly");
                Focus(PatternGrid());
                var play = PatternView.Page.EditorControl.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Content as string == "▶");
                Assert.That(ToolTip.GetTip(play) as string, Does.Contain("Shift+Space"));

                Space(window, RawInputModifiers.Shift);
                Assert.That(pattern.IsPlayingSolo, Is.True, "Shift+Space plays the pattern on its own");
                Assert.That(Core.PlayRequested, Is.True);
                Assert.That(play.IsChecked, Is.True, "the editor's ▶ is on");
                Space(window, RawInputModifiers.Shift);
                Assert.That(pattern.IsPlayingSolo, Is.False, "again: it stops");
                Assert.That(Core.PlayRequested, Is.False);
                Assert.That(play.IsChecked, Is.False);

                // Space stops the pattern too, and its ▶ goes out (so the next Shift+Space plays again).
                Space(window, RawInputModifiers.Shift);
                Assert.That(pattern.IsPlayingSolo, Is.True);
                Space(window);
                Assert.That(Core.PlayRequested, Is.False, "Space stops everything");
                Assert.That(pattern.IsPlayingSolo, Is.False);
                Assert.That(play.IsChecked, Is.False);
                Space(window, RawInputModifiers.Shift);
                Assert.That(pattern.IsPlayingSolo, Is.True, "and Shift+Space plays it again at once");
                Space(window, RawInputModifiers.Shift);

                // Elsewhere Shift+Space isn't the transport's.
                window.ShowView("Machines");
                Settle();
                Focus(((MachineView)window.Tabs.SelectedContent).Canvas);
                Space(window, RawInputModifiers.Shift);
                Assert.That(Core.PlayRequested, Is.False, "Shift+Space in the machine view plays nothing");
            }
            finally
            {
                PutBack(window);
            }
        }

        [AvaloniaTest]
        public void TheMenusAndTooltipsSaySpace()
        {
            var (window, _) = MachineViewTests.OpenEmpty();
            try
            {
                // The macOS menu bar: Space in the text, not a key equivalent (it would take Space from text boxes).
                var mac = window.MenuModel(mac: true).ToList();
                var toggle = AppMenu.Find(mac, "Transport/Play or Stop (Space)");
                Assert.That(toggle, Is.Not.Null);
                Assert.That(toggle.NativeKey, Is.False);
                var transportMenu = AppMenu.ToNativeMenu(mac).Items.OfType<NativeMenuItem>().Single(i => i.Header == "Transport").Menu;
                var items = transportMenu.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToList();
                Assert.That(items.Select(i => i.Header), Is.EqualTo(new[] { "Play or Stop (Space)", "Play", "Stop", "Record", "Restart audio" }));
                Assert.That(items.Any(i => i.Gesture?.Key == Key.Space), Is.False, "no Space key equivalent");
                Assert.That(items.Single(i => i.Header == "Play").Gesture, Is.EqualTo(new KeyGesture(Key.F5)));
                Assert.That(items.Single(i => i.Header == "Stop").Gesture, Is.EqualTo(new KeyGesture(Key.F8)));

                // Elsewhere the window's menu shows it beside the item (a gesture shown, not a hot key).
                var other = window.MenuModel(mac: false).ToList();
                var entry = AppMenu.Find(other, "Transport/Play or Stop");
                Assert.That(entry.Gesture, Is.EqualTo(new KeyGesture(Key.Space)));
                if (!OperatingSystem.IsMacOS())
                {
                    var transport = window.WindowMenu.Items.OfType<MenuItem>().Single(i => (string)i.Tag == "Transport");
                    var item = transport.Items.OfType<MenuItem>().Single(i => (string)i.Tag == "Play or Stop");
                    Assert.That(item.InputGesture, Is.EqualTo(new KeyGesture(Key.Space)));
                    Assert.That(item.HotKey, Is.Null);
                }
                entry.Run();
                Assert.That(Core.PlayRequested, Is.True, "Transport ▸ Play or Stop plays");
                entry.Run();
                Assert.That(Core.PlayRequested, Is.False, "and stops");

                Assert.That(MainWindow.ShortcutsText(mac: false), Does.Contain("Space\tplay / stop").And.Contain("Shift+Space\tplay the pattern"));
                Assert.That(MainWindow.ShortcutsText(mac: true), Does.Contain("⇧Space"));
                var buttons = window.Transport.GetVisualDescendants().OfType<Button>().ToList();
                Assert.That(ToolTip.GetTip(buttons.Single(b => b.Content as string == "▶")) as string, Does.Contain("Space or F5"));
                Assert.That(ToolTip.GetTip(buttons.Single(b => b.Content as string == "■")) as string, Does.Contain("Space or F8"));
            }
            finally
            {
                PutBack(window);
            }
        }

        [AvaloniaTest]
        public void SpaceStaysWithTextEntryOpenDropDownsAndMenus()
        {
            var box = new TextBox();
            var readOnly = new TextBox { IsReadOnly = true };
            var number = new NumericUpDown();
            var editable = new ComboBox { IsEditable = true, ItemsSource = new[] { "a", "b" } };
            var combo = new ComboBox { ItemsSource = new[] { "a", "b" }, SelectedIndex = 0 };
            var complete = new AutoCompleteBox();
            var button = new Button { Content = "B" };
            var check = new CheckBox();
            var menu = new Menu();
            var menuItem = new MenuItem { Header = "M" };
            menu.Items.Add(menuItem);
            var panel = new StackPanel { Children = { box, readOnly, number, editable, combo, complete, button, check, menu } };
            var window = new Window { Content = panel, Width = 400, Height = 600 };
            window.Show();
            try
            {
                Settle();
                Assert.That(TransportKeys.KeepsSpace(box), Is.True, "a text box");
                Assert.That(TransportKeys.KeepsSpace(box.GetVisualDescendants().OfType<Control>().Last()), Is.True, "inside a text box");
                Assert.That(TransportKeys.KeepsSpace(readOnly), Is.False, "a read-only text box types nothing");
                Assert.That(TransportKeys.KeepsSpace(number), Is.True, "a number box");
                Assert.That(TransportKeys.KeepsSpace(number.GetVisualDescendants().OfType<TextBox>().First()), Is.True, "a number box's text");
                Assert.That(TransportKeys.KeepsSpace(editable), Is.True, "an editable drop-down");
                Assert.That(TransportKeys.KeepsSpace(complete), Is.True);
                Assert.That(TransportKeys.KeepsSpace(combo), Is.False, "a closed drop-down");
                combo.IsDropDownOpen = true;
                Settle();
                Assert.That(TransportKeys.KeepsSpace(combo), Is.True, "an open drop-down");
                var item = combo.GetRealizedContainers().OfType<ComboBoxItem>().First();
                Assert.That(TransportKeys.KeepsSpace(item), Is.True, "an open drop-down's item (in its popup, over the window)");
                combo.IsDropDownOpen = false;
                Assert.That(TransportKeys.KeepsSpace(button), Is.False, "a button");
                Assert.That(TransportKeys.KeepsSpace(check), Is.False, "a check box");
                Assert.That(TransportKeys.KeepsSpace(menuItem), Is.True, "a menu");
                Assert.That(TransportKeys.KeepsSpace(panel), Is.False);
                Assert.That(TransportKeys.KeepsSpace(window), Is.False);
                Assert.That(TransportKeys.KeepsSpace(null), Is.False);
            }
            finally
            {
                window.Close();
                Settle();
            }
        }
    }
}
