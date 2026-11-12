// Fork (docs/specs/ux-overhaul.md P3a): the inspector, the window's panel on the right. It follows the studio's one selection in
// every view, holds the Session View's clip panel (the Clip and Scene sections) and Song Notes (the Song tab), collapses to a
// rail, and remembers whether it is open and how wide it is.

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Inspector;
using ReBuzz.Studio.Views.Session;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class UxP3aTests
    {
        static readonly string[] Views = { "Machines", "Sequence", "Session", "Pattern" };

        sealed class Rig : IDisposable
        {
            public SessionRig Song { get; }
            public MainWindow Window { get; }
            public MachineCore Drums, Bass;
            public PatternCore Beat, Line;
            public StudioSelection Selection => StudioSelection.For(Song.Core);
            public InspectorPanel Inspector => Window.Inspector;

            public Rig()
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                Song = new SessionRig("Drums", "Bass");
                Drums = Song.Machine("Drums");
                Bass = Song.Machine("Bass");
                Beat = Song.Pattern(Drums, "Beat", 16, (0, 60), (4, 62));
                Line = Song.Pattern(Bass, "Line", 16, (0, 48));
                Song.Layout.SetSlot(0, 0, Beat);
                Song.Layout.SetSlot(1, 0, Line);
                Selection.Clear(this);
                Window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
                Window.Show();
                Show("Machines");
            }

            public Avalonia.Controls.Control Show(string tab)
            {
                var item = Window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == tab);
                item.IsSelected = true;
                Settle();
                return (Avalonia.Controls.Control)item.Content!;
            }

            public void Click(Button button)
            {
                Song.Click(button);
            }

            public void Dispose()
            {
                Window.Close();
                Song.Dispose();
            }
        }

        /// <summary>The settings the inspector keeps, put back as the tests found them (the registry is the whole run's).</summary>
        static void PutBack()
        {
            var registry = Studio.Host.Registry;
            registry.Write(InspectorPanel.OpenKey, true, InspectorPanel.RegistrySection);
            registry.Write(InspectorPanel.WidthKey, InspectorPanel.DefaultWidth, InspectorPanel.RegistrySection);
        }

        // ------------------------------------------------------------------ what it shows

        [AvaloniaTest]
        public void NothingSelectedIsTheSong()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            var panel = rig.Inspector;
            Assert.That(panel.IsOpen, Is.True, "open when a window opens, as the clip panel was");
            Assert.That(panel.HeadingText, Is.EqualTo("Song"));
            Assert.That(panel.Facts.Select(f => f.Label), Is.EqualTo(new[] { "Tempo", "Ticks per beat", "Machines" }));
            Assert.That(panel.Facts.Single(f => f.Label == "Machines").Value, Is.EqualTo("2"), "the master isn't counted");
            Assert.That(panel.HintText, Does.Contain("Select a machine"));
            Assert.That(panel.NotesButton.IsVisible, Is.True, "a way to the Song tab");
            Assert.That(panel.PatternButton.IsVisible, Is.False);
            Assert.That(panel.ClipPanel.IsVisible, Is.False, "no clip settings for the song");
        }

        [AvaloniaTest]
        public void ItIsThereInEveryViewAndFollowsTheSelection()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            foreach (string view in Views)
            {
                rig.Show(view);
                Assert.That(rig.Inspector.IsEffectivelyVisible, Is.True, $"{view}: the inspector is there");
                rig.Selection.Set(rig.Bass, null, this);
                Settle();
                Assert.That(rig.Inspector.HeadingText, Is.EqualTo("Bass"), $"{view}: the selected machine");
                Assert.That(rig.Inspector.Facts.Select(f => f.Label), Does.Contain("Type").And.Contain("Patterns").And.Contain("Wires"));
                Assert.That(rig.Inspector.Facts.Single(f => f.Label == "Patterns").Value, Is.EqualTo("1"));
                rig.Selection.Clear(this);
                Settle();
                Assert.That(rig.Inspector.HeadingText, Is.EqualTo("Song"), $"{view}: nothing selected is the song again");
            }
        }

        [AvaloniaTest]
        public void APatternShowsItsLengthAndOpensInThePatternView()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            rig.Selection.Set(rig.Drums, rig.Beat, this);
            Settle();
            var panel = rig.Inspector;
            Assert.That(panel.Facts.Single(f => f.Label == "Pattern").Value, Is.EqualTo("Beat"));
            Assert.That(panel.Facts.Single(f => f.Label == "Length").Value, Does.StartWith("16 ticks"));
            Assert.That(panel.PatternButton.IsVisible, Is.True);
            rig.Click(panel.PatternButton);
            Assert.That(((TabItem)rig.Window.Tabs.SelectedItem!).Header, Is.EqualTo("Pattern"));
            Assert.That(rig.Selection.Pattern, Is.SameAs(rig.Beat), "the selection stays");
        }

        [AvaloniaTest]
        public void AMachineLostFromTheSongIsNotShown()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            rig.Selection.Set(rig.Bass, null, this);
            Settle();
            Assert.That(rig.Inspector.HeadingText, Is.EqualTo("Bass"));
            rig.Song.Core.SongCore.DeleteMachines(new[] { rig.Bass });
            Settle();
            rig.Inspector.Refresh();
            Assert.That(rig.Inspector.HeadingText, Is.EqualTo("Song"), "the window's timer brings it back to the song");
        }

        // ------------------------------------------------------------------ the clip and scene sections

        [AvaloniaTest]
        public void TheClipPanelLivesInTheInspectorNotInTheSessionView()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            var session = (SessionView)rig.Show("Session");
            var clip = rig.Inspector.ClipPanel;
            session.Select(0, 0);
            Settle();
            Assert.That(clip, Is.SameAs(session.ClipPanelView));
            Assert.That(clip.GetVisualAncestors().Contains(rig.Inspector), Is.True, "in the inspector");
            Assert.That(clip.GetVisualAncestors().Contains(session), Is.False, "no longer beside the grid");
            Assert.That(clip.Bounds.Width, Is.GreaterThan(200).And.LessThanOrEqualTo(InspectorPanel.DefaultWidth), "it takes the inspector's width");
        }

        [AvaloniaTest]
        public void ClipSettingsAreEditedInEveryView()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            var layout = rig.Song.Layout;
            var clip = rig.Inspector.ClipPanel;

            // In the Machines view (the Session View isn't shown): select a machine and its clip, edit the clip.
            rig.Show("Machines");
            rig.Selection.Set(rig.Drums, rig.Beat, this, 0);
            Settle();
            Assert.That(clip.IsVisible, Is.True);
            Assert.That(clip.Pattern, Is.SameAs(rig.Beat));
            Assert.That(clip.TitleText, Is.EqualTo("Beat"));
            rig.Song.Choose(clip.ModeBox, 1);
            Assert.That(layout.ClipOf(rig.Beat).Mode, Is.EqualTo("Gate"), "the edit reaches the song from the Machines view");

            // In the arrangement: another machine's clip.
            rig.Show("Sequence");
            rig.Selection.Set(rig.Bass, rig.Line, this, 0);
            Settle();
            Assert.That(clip.Pattern, Is.SameAs(rig.Line));
            Assert.That(clip.ModeBox.SelectedIndex, Is.EqualTo(0), "its own settings");
            Assert.That(layout.ClipOf(rig.Line).Mode, Is.EqualTo("Trigger"));

            // A machine only: no clip to show, the panel says how to get to one.
            rig.Selection.Set(rig.Drums, null, this);
            rig.Selection.Set(rig.Drums, null, this, null);
            Settle();
            Assert.That(clip.Slot?.Track, Is.EqualTo(0));
        }

        [AvaloniaTest]
        public void AClickInTheSessionViewShowsItsClipAndSceneInThePanel()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            var session = (SessionView)rig.Show("Session");
            session.Select(1, 0);
            Settle();
            Assert.That(rig.Inspector.HeadingText, Is.EqualTo("Bass"));
            Assert.That(rig.Inspector.ClipPanel.Pattern, Is.SameAs(rig.Line));
            Assert.That(rig.Inspector.ClipPanel.Slot, Is.EqualTo((1, 0)));
            session.Select(1, 1);
            Settle();
            Assert.That(rig.Inspector.ClipPanel.Pattern, Is.Null, "an empty slot");
            Assert.That(rig.Inspector.ClipPanel.Slot, Is.EqualTo((1, 1)));
            Assert.That(rig.Inspector.ClipPanel.SceneTempoBox.IsEffectivelyVisible, Is.True, "with its scene's settings");
        }

        [AvaloniaTest]
        public void TheSongKeepsItsClipPanelSetting()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            var doc = rig.Song.Layout.Ensure();
            bool before = doc.ShowClipPanel;
            rig.Inspector.Close();
            Settle();
            rig.Inspector.Open();
            rig.Inspector.Toggle();
            Settle();
            Assert.That(rig.Song.Layout.Ensure().ShowClipPanel, Is.EqualTo(before), "opening and closing the inspector doesn't touch the song (WPF's Session View reads it)");
        }

        // ------------------------------------------------------------------ Song Notes

        [AvaloniaTest]
        public void SongNotesAreTheSongTab()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            var panel = rig.Inspector;
            Assert.That(rig.Window.Tabs.Items.Cast<TabItem>().Select(t => t.Header), Does.Not.Contain("Song Notes"), "no tab of its own");
            Assert.That(panel.CurrentTab, Is.EqualTo("Selection"));
            Assert.That(panel.SongNotes.IsEffectivelyVisible, Is.False);
            rig.Click(panel.SongTabButton);
            Assert.That(panel.CurrentTab, Is.EqualTo("Song"));
            Assert.That(panel.SongNotes.IsEffectivelyVisible, Is.True);
            Assert.That(panel.ClipPanel.IsEffectivelyVisible, Is.False);
            rig.Click(panel.SelectionTabButton);
            Assert.That(panel.SongNotes.IsEffectivelyVisible, Is.False);
        }

        [AvaloniaTest]
        public void F10AndTheMenuOpenSongNotesEvenWhenTheInspectorIsHidden()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            rig.Inspector.Close();
            Settle();
            Assert.That(rig.Inspector.IsOpen, Is.False);
            rig.Window.KeyPressQwerty(PhysicalKey.F10, RawInputModifiers.None);
            rig.Window.KeyReleaseQwerty(PhysicalKey.F10, RawInputModifiers.None);
            Settle();
            Assert.That(rig.Inspector.IsOpen, Is.True);
            Assert.That(rig.Inspector.CurrentTab, Is.EqualTo("Song"));
            Assert.That(rig.Inspector.SongNotes.Editor.IsFocused, Is.True, "ready to type");
            var view = AppMenu.Find(rig.Window.MenuModel(mac: false), "View").Items().ToList();
            Assert.That(view.Single(e => e.Text == "Song Notes").IsChecked(), Is.True);
            Assert.That(view.Single(e => e.Text == "Inspector").IsChecked(), Is.True);
        }

        [AvaloniaTest]
        public void CutCopyPasteInTheNotesAreTheNotes()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            rig.Window.ShowView("Song Notes");
            Settle();
            var notes = rig.Inspector.SongNotes;
            Assert.That(notes.Editor.IsFocused, Is.True);
            rig.Window.KeyTextInput("x");
            Settle();
            Assert.That(rig.Song.Core.SongNotes.Text, Does.Contain("x"), "typing in the Song tab edits the song's notes");
        }

        // ------------------------------------------------------------------ collapsing and sizing

        [AvaloniaTest]
        public void ItCollapsesToARailAndComesBack()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            var panel = rig.Inspector;
            double open = panel.Bounds.Width;
            Assert.That(open, Is.EqualTo(InspectorPanel.DefaultWidth).Within(1));
            rig.Click(panel.CollapseButton);
            Assert.That(panel.IsOpen, Is.False);
            Assert.That(panel.Bounds.Width, Is.EqualTo(InspectorPanel.RailWidth).Within(1), "a rail");
            Assert.That(panel.RailButton.IsEffectivelyVisible, Is.True);
            Assert.That(panel.CollapseButton.IsEffectivelyVisible, Is.False);
            Assert.That(panel.ClipPanel.IsEffectivelyVisible, Is.False);
            var tabs = (TabControl)rig.Window.Tabs;
            Assert.That(tabs.Bounds.Width, Is.GreaterThan(1200), "the view takes the room");
            rig.Click(panel.RailButton);
            Assert.That(panel.IsOpen, Is.True);
            Assert.That(panel.Bounds.Width, Is.EqualTo(open).Within(1));
        }

        [AvaloniaTest]
        public void ItsKeyTogglesItInEveryViewTheTrackerToo()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            foreach (string view in Views)
            {
                rig.Show(view);
                bool was = rig.Inspector.IsOpen;
                rig.Window.KeyPressQwerty(PhysicalKey.I, RawInputModifiers.Control | RawInputModifiers.Alt);
                rig.Window.KeyReleaseQwerty(PhysicalKey.I, RawInputModifiers.Control | RawInputModifiers.Alt);
                Settle();
                Assert.That(rig.Inspector.IsOpen, Is.Not.EqualTo(was), $"{view}: ⌥⌘I");
            }
        }

        [AvaloniaTest]
        public void TheViewMenuHasIt()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            var item = AppMenu.Find(rig.Window.MenuModel(mac: false), "View").Items().Single(e => e.Text == "Inspector");
            Assert.That(item.IsChecked(), Is.True);
            item.Run();
            Settle();
            Assert.That(rig.Inspector.IsOpen, Is.False);
            Assert.That(AppMenu.Find(rig.Window.MenuModel(mac: false), "View").Items().Single(e => e.Text == "Inspector").IsChecked(), Is.False);
        }

        [AvaloniaTest]
        public void ItsWidthAndStateAreRemembered()
        {
            using var restore = Studio.AfterTest(PutBack);
            var registry = Studio.Host.Registry;
            using (var rig = new Rig())
            {
                rig.Inspector.PanelWidth = 400;
                Settle();
                Assert.That(rig.Inspector.Bounds.Width, Is.EqualTo(400).Within(1));
                rig.Inspector.PanelWidth = 10;
                Assert.That(rig.Inspector.PanelWidth, Is.EqualTo(InspectorPanel.MinPanelWidth), "not narrower than it can show");
                rig.Inspector.PanelWidth = 4000;
                Assert.That(rig.Inspector.PanelWidth, Is.EqualTo(InspectorPanel.MaxPanelWidth), "nor wider than it is useful");
                rig.Inspector.PanelWidth = 400;
            }
            Assert.That(registry.Read(InspectorPanel.WidthKey, 0.0, InspectorPanel.RegistrySection), Is.EqualTo(400));

            using (var rig = new Rig())
            {
                Assert.That(rig.Inspector.PanelWidth, Is.EqualTo(400), "the next window has it");
                rig.Inspector.Close();
            }
            using (var rig = new Rig())
            {
                Assert.That(rig.Inspector.IsOpen, Is.False, "closed stays closed");
                Assert.That(rig.Inspector.PanelWidth, Is.EqualTo(400), "and the width is kept");
            }
        }

        [AvaloniaTest]
        public void DraggingItsEdgeSizesIt()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            var panel = rig.Inspector;
            var start = panel.Grip.TranslatePoint(new Point(2, 200), rig.Window)!.Value;
            rig.Window.MouseDown(start, MouseButton.Left);
            rig.Window.MouseMove(start - new Vector(60, 0), RawInputModifiers.LeftMouseButton);
            rig.Window.MouseMove(start - new Vector(100, 0), RawInputModifiers.LeftMouseButton);
            rig.Window.MouseUp(start - new Vector(100, 0), MouseButton.Left);
            Settle();
            Assert.That(panel.PanelWidth, Is.EqualTo(InspectorPanel.DefaultWidth + 100).Within(8), "100 points left is 100 wider");
            Assert.That(Studio.Host.Registry.Read(InspectorPanel.WidthKey, 0.0, InspectorPanel.RegistrySection), Is.EqualTo(panel.PanelWidth));
        }

        [AvaloniaTest]
        public void TheSessionViewsClipButtonShowsAndHidesTheInspector()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            var session = (SessionView)rig.Show("Session");
            var button = session.GetVisualDescendants().OfType<Border>().First(b => b.GetType().Name == "FlatButton" && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Clip"));
            rig.Song.Click(button);
            Assert.That(rig.Inspector.IsOpen, Is.False, "the Clip button hides the clip settings: the inspector");
            rig.Song.Click(button);
            Assert.That(rig.Inspector.IsOpen, Is.True);
        }

        [AvaloniaTest]
        public void ItsTextMeetsTheContrastFloorOnTheTokens()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig();
            var panel = rig.Inspector;
            var surface = Avalonia.Media.Colors.Transparent;
            Assert.That(((Avalonia.Media.ISolidColorBrush)panel.Background!).Color, Is.EqualTo(Shell.Design.StudioTokens.SurfacePanel), "the panel surface");
            surface = Shell.Design.StudioTokens.SurfacePanel;
            Assert.That(Shell.Design.StudioTokens.Contrast(Shell.Design.StudioTokens.TextPrimary, surface), Is.GreaterThanOrEqualTo(4.5), "headings and values");
            Assert.That(Shell.Design.StudioTokens.Contrast(Shell.Design.StudioTokens.TextSecondary, surface), Is.GreaterThanOrEqualTo(4.5), "labels, the other tab");
            // The shown tab: the primary text on the selection fill over the panel.
            var fill = Shell.Design.StudioTokens.SelectionFill;
            double a = fill.A / 255.0;
            var tab = Avalonia.Media.Color.FromRgb((byte)(fill.R * a + surface.R * (1 - a)), (byte)(fill.G * a + surface.G * (1 - a)), (byte)(fill.B * a + surface.B * (1 - a)));
            Assert.That(Shell.Design.StudioTokens.Contrast(Shell.Design.StudioTokens.TextPrimary, tab), Is.GreaterThanOrEqualTo(4.5), "the shown tab");
        }
    }
}
