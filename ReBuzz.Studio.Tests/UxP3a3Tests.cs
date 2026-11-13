// Fork (docs/specs/ux-overhaul.md P3a, third step: decisions 37-39): the segmented view switcher and the tab renames
// (Sequence is Arrangement; ShowView takes both), a 640 x 400 window with no overlap and no clipping (the switcher,
// the Session toolbar, the docked Tracker's chooser bar), and the inspector's Song tab stacking its editor over the notes.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Browser;
using ReBuzz.Studio.Views.Detail;
using ReBuzz.Studio.Views.Session;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class UxP3a3Tests
    {
        sealed class Rig : IDisposable
        {
            public SessionRig Song { get; }
            public MainWindow Window { get; }

            public Rig(double width, double height)
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                Song = new SessionRig("Drums", "Bass");
                var drums = Song.Machine("Drums");
                var beat = Song.Pattern(drums, "Beat", 16, (0, 60), (4, 62));
                Song.Layout.SetSlot(0, 0, beat);
                StudioSelection.For(Song.Core).Clear(this);
                Window = new MainWindow(Studio.Host) { Width = width, Height = height };
                Window.Show();
                Show("Machines");
            }

            public void Show(string view)
            {
                Window.ShowView(view);
                Settle();
            }

            public void Dispose()
            {
                Window.Close();
                Song.Dispose();
            }
        }

        static void PutBack()
        {
            var registry = Studio.Host.Registry;
            registry.Write(BrowserPanel.OpenKey, false, BrowserPanel.RegistrySection);
            registry.Write(DetailPanel.OpenKey, false, DetailPanel.RegistrySection);
            registry.Write(DetailPanel.HeightKey, DetailPanel.DefaultHeight, DetailPanel.RegistrySection);
        }

        static Rect Box(Visual v, Visual root) => new Rect(v.TranslatePoint(default, root)!.Value, v.Bounds.Size);

        static List<(string Name, Rect Box)> Visible<T>(Visual within, Visual root) where T : Avalonia.Controls.Control =>
            within.GetVisualDescendants().OfType<T>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0)
                .Select(c => (c.GetType().Name + ":" + (c is ContentControl cc ? cc.Content : c is TextBlock t ? t.Text : null), Box(c, root))).ToList();

        /// <summary>No two boxes overlap by more than a pixel, and none leaves the frame.</summary>
        static void AssertClean(IReadOnlyList<(string Name, Rect Box)> boxes, Rect frame, string what)
        {
            Assert.That(boxes, Is.Not.Empty, what);
            foreach (var (name, box) in boxes)
                Assert.That(frame.Inflate(1).Contains(box), Is.True, $"{what}: {name} {box} is clipped by {frame}");
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    var overlap = boxes[i].Box.Intersect(boxes[j].Box);
                    Assert.That(overlap.Width < 1.5 || overlap.Height < 1.5, Is.True, $"{what}: {boxes[i].Name} {boxes[i].Box} overlaps {boxes[j].Name} {boxes[j].Box}");
                }
        }

        [AvaloniaTest]
        public void TheSwitcherListsTheThreeViewsAndShowsTheSelectedOne()
        {
            using var rig = new Rig(1280, 800);
            var w = rig.Window;
            Assert.That(w.Switcher.Segments.Keys, Is.EqualTo(new[] { "Arrangement", "Session", "Machines" }));
            Assert.That(w.Tabs.Items.Cast<TabItem>().Select(t => t.Header), Is.EqualTo(new[] { "Arrangement", "Session", "Machines" }));
            foreach (var view in new[] { "Arrangement", "Session", "Machines" })
            {
                rig.Show(view);
                Assert.That(((TabItem)w.Tabs.SelectedItem!).Header, Is.EqualTo(view));
                foreach (var pair in w.Switcher.Segments)
                    Assert.That(pair.Value.BorderBrush == StudioTokens.AccentBrush, Is.EqualTo(pair.Key == view), $"{pair.Key} lit with {view} shown");
            }
            // A click on a segment shows the view; so does selecting the tab by code (tests, the agent).
            rig.Song.Click(w.Switcher.Segments["Session"]);
            Assert.That(((TabItem)w.Tabs.SelectedItem!).Header, Is.EqualTo("Session"));
            w.Tabs.SelectedItem = w.Tabs.Items.Cast<TabItem>().First(t => (string)t.Header! == "Arrangement");
            Settle();
            Assert.That(w.Switcher.Segments["Arrangement"].BorderBrush, Is.SameAs(StudioTokens.AccentBrush));
            // The key is in the tooltip, as the platform writes it.
            Assert.That(ToolTip.GetTip(w.Switcher.Segments["Machines"]) as string, Does.Contain(Shell.Commands.StudioKeymap.Get("view.machines").KeyLabel()));
        }

        [AvaloniaTest]
        public void ShowViewTakesTheOldNameToo()
        {
            using var rig = new Rig(1280, 800);
            rig.Show("Sequence");
            Assert.That(((TabItem)rig.Window.Tabs.SelectedItem!).Header, Is.EqualTo("Arrangement"));
            rig.Show("Session");
            rig.Show("Arrangement");
            Assert.That(((TabItem)rig.Window.Tabs.SelectedItem!).Header, Is.EqualTo("Arrangement"));
            Assert.That(Shell.Commands.StudioKeymap.Get("view.sequence").Title, Does.Contain("Arrangement"), "the command id stays, the label is renamed");
            Assert.That(Shell.Commands.StudioKeymap.Get("view.sequence").KeyLabel(), Is.Not.Empty);
        }

        [AvaloniaTest]
        public void TheTabControlsOwnStripIsHidden()
        {
            using var rig = new Rig(640, 400);
            Settle();
            Assert.That(rig.Window.Tabs.GetVisualDescendants().OfType<TabItem>().Any(t => t.IsEffectivelyVisible), Is.False, "no second strip of tabs");
            Assert.That(rig.Window.Switcher.Bounds.Height, Is.InRange(20, 48), "one row, not a wrapped strip");
        }

        [AvaloniaTest]
        public void At640By444TheSwitcherTheSessionToolbarAndTheTrackerBarHaveRoom()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig(640, 444);
            var w = rig.Window;
            w.Inspector.Open();
            Settle();

            // The switcher: one row, all three segments inside the main view.
            var centre = Box(w.Tabs, w);
            AssertClean(w.Switcher.Segments.Select(p => (p.Key, Box(p.Value, w))).ToList(), new Rect(centre.X, 0, centre.Width, Box(w.Switcher, w).Bottom + 100), "the switcher");
            Assert.That(w.Switcher.Segments.Values.Select(b => b.Bounds.Y).Distinct().Count(), Is.EqualTo(1), "the segments share a row");

            // The Session toolbar: every button inside the view, none over another.
            rig.Show("Session");
            var session = (SessionView)w.Tabs.SelectedContent!;
            var view = Box(session, w);
            var buttons = Visible<Avalonia.Controls.Control>(session, w).Where(b => b.Name.StartsWith("FlatButton") && b.Box.Bottom <= view.Y + 110).ToList();
            AssertClean(buttons, view, "the Session toolbar");

            // The docked Tracker's chooser bar.
            w.ShowView("Pattern");
            Settle();
            Assert.That(w.Detail.IsOpen, Is.True);
            var page = w.Detail.Page;
            var frame = Box(page, w);
            var bar = Visible<Avalonia.Controls.Control>(page, w).Where(c => c.Name.StartsWith("ComboBox") || c.Name.StartsWith("Button") || c.Name.StartsWith("TextBlock:Machine") || c.Name.StartsWith("TextBlock:Pattern")).Where(c => c.Box.Bottom <= frame.Y + 80).ToList();
            AssertClean(bar, frame, "the Tracker's chooser bar");
        }

        [AvaloniaTest]
        public void TheSongTabStacksItsEditorOverTheNotesAtTheDefaultWidth()
        {
            using var restore = Studio.AfterTest(PutBack);
            using var rig = new Rig(1280, 800);
            var inspector = rig.Window.Inspector;
            inspector.ShowTab(Views.Inspector.InspectorPanel.SongTab);
            Settle();
            var notes = inspector.SongNotes;
            Assert.That(notes.Stacked, Is.True, "288 pt is under the threshold");
            var editor = Box(notes.Editor, rig.Window);
            Assert.That(editor.Width, Is.GreaterThanOrEqualTo(200), "the editor has the inspector's width, not 90 pt");
            var page = notes.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s != null && s.IsEffectivelyVisible && !(s.Content is null) && s.Bounds.Height > 0 && s.GetVisualAncestors().All(a => a != notes.Editor)).Select(s => Box(s, rig.Window)).Where(b => b.Width > 150).OrderByDescending(b => b.Y).First();
            Assert.That(page.Y, Is.GreaterThan(editor.Bottom - 1), "the notes as they read are under the editor");
            Assert.That(editor.Height, Is.GreaterThanOrEqualTo(60));

        }

        [AvaloniaTest]
        public void TheNewTextMeetsTheContrastFloor()
        {
            var surface = StudioTokens.SurfacePanel;
            Assert.That(StudioTokens.Contrast(StudioTokens.TextSecondary, surface), Is.GreaterThanOrEqualTo(4.5), "an idle segment on the switcher bar");
            Assert.That(StudioTokens.Contrast(StudioTokens.TextSecondary, StudioTokens.SurfaceBase), Is.GreaterThanOrEqualTo(4.5), "an idle segment in its well");
            Assert.That(StudioTokens.Contrast(StudioTokens.TextPrimary, StudioTokens.SelectionFill), Is.GreaterThanOrEqualTo(4.5), "the lit segment");
        }
    }
}
