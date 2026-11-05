using ReBuzz.AppViews;
using ReBuzz.Core;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Command = ReBuzz.AppViews.TransportKeys.Command;
using Rig = ReBuzzTests.SessionViewTests.Rig;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork: Space plays and stops the song in every view of ReBuzz.exe's main window, F5 and F8 still
    /// do; Shift+Space in the pattern view plays the pattern from the editor's cursor. Not while typing
    /// in a box. The main window isn't built in the tests, so its PreviewKeyDown's decision is tested
    /// here (AppViews/TransportKeys.cs), with the command both apps run (Core/TransportCommands.cs).
    /// The studio's tests press the keys in its window (ReBuzz.Studio.Tests/TransportKeysTests.cs).
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class TransportKeysTests
    {
        const int Bar = 16;

        [Test]
        public void SpaceIsTheTransportsInEveryViewAndShiftSpaceInThePatternView()
        {
            var button = new Button();
            Assert.That(TransportKeys.For(Key.Space, ModifierKeys.None, false, false, button), Is.EqualTo(Command.TogglePlaying), "a focused button doesn't take it");
            Assert.That(TransportKeys.For(Key.Space, ModifierKeys.None, false, true, button), Is.EqualTo(Command.TogglePlaying), "the pattern view too (upstream: the pattern solo)");
            Assert.That(TransportKeys.For(Key.Space, ModifierKeys.None, false, false, null!), Is.EqualTo(Command.TogglePlaying), "nothing focused");
            Assert.That(TransportKeys.For(Key.Space, ModifierKeys.None, true, false, button), Is.EqualTo(Command.Repeat), "held: one toggle");
            Assert.That(TransportKeys.For(Key.Space, ModifierKeys.Shift, false, true, button), Is.EqualTo(Command.PatternPreview));
            Assert.That(TransportKeys.For(Key.Space, ModifierKeys.Shift, false, false, button), Is.EqualTo(Command.None), "Shift+Space elsewhere isn't the transport's");
            Assert.That(TransportKeys.For(Key.Space, ModifierKeys.Control, false, false, button), Is.EqualTo(Command.None));
            Assert.That(TransportKeys.For(Key.F5, ModifierKeys.None, false, false, button), Is.EqualTo(Command.None), "F5 and F8 stay the window's own");

            var box = new TextBox();
            Assert.That(TransportKeys.For(Key.Space, ModifierKeys.None, false, false, box), Is.EqualTo(Command.None), "typing: a space");
            Assert.That(TransportKeys.For(Key.Space, ModifierKeys.Shift, false, true, box), Is.EqualTo(Command.None));
            Assert.That(TransportKeys.For(Key.Space, ModifierKeys.None, true, false, box), Is.EqualTo(Command.None), "held in a box: spaces");
        }

        [Test]
        public void SpaceStaysWithTextEntryOpenDropDownsAndMenus()
        {
            var box = new TextBox();
            var panel = new StackPanel();
            panel.Children.Add(box);
            Assert.That(TransportKeys.KeepsSpace(box), Is.True, "a text box");
            Assert.That(TransportKeys.KeepsSpace(panel), Is.False);
            box.ApplyTemplate();
            if (VisualTreeHelper.GetChildrenCount(box) > 0)
                Assert.That(TransportKeys.KeepsSpace(VisualTreeHelper.GetChild(box, 0)), Is.True, "inside a text box");
            Assert.That(TransportKeys.KeepsSpace(new TextBox { IsReadOnly = true }), Is.False, "a read-only text box types nothing");
            Assert.That(TransportKeys.KeepsSpace(new RichTextBox()), Is.True, "Song Info's kind of box");
            Assert.That(TransportKeys.KeepsSpace(new BuzzGUI.Common.SearchTextBox()), Is.True, "the machine list's search");
            Assert.That(TransportKeys.KeepsSpace(new PasswordBox()), Is.True);
            Assert.That(TransportKeys.KeepsSpace(new ComboBox { IsEditable = true }), Is.True, "an editable drop-down");
            Assert.That(TransportKeys.KeepsSpace(new ComboBox()), Is.False, "a closed drop-down");
            Assert.That(TransportKeys.KeepsSpace(new MenuItem()), Is.True, "a menu");
            Assert.That(TransportKeys.KeepsSpace(new Button()), Is.False);
            Assert.That(TransportKeys.KeepsSpace(new CheckBox()), Is.False);
            Assert.That(TransportKeys.KeepsSpace(new ListBox()), Is.False);
            Assert.That(TransportKeys.KeepsSpace(null!), Is.False);
        }

        [Test]
        public void TheCommandPlaysStopsAndStopsAPatternPlayingOnItsOwn()
        {
            using var rig = new Rig();
            var core = rig.Core;
            core.Playing = false;
            TransportCommands.TogglePlaying(core);
            Assert.That(core.PlayRequested, Is.True, "plays");
            TransportCommands.TogglePlaying(core);
            Assert.That(core.PlayRequested, Is.False, "a start waiting for its tick stops again");

            var pattern = rig.Pattern(rig.Bass, "Solo", Bar);
            pattern.IsPlayingSolo = true;
            Assert.That(core.PlayRequested, Is.True);
            TransportCommands.TogglePlaying(core);
            Assert.That(core.PlayRequested, Is.False, "Space stops a pattern playing on its own");
            Assert.That(pattern.IsPlayingSolo, Is.False, "and its flag (the editor's ▶) with it");
        }
    }
}
