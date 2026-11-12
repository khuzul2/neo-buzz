// Fork (docs/specs/ux-overhaul.md P2c): one selection across the Machines, Sequence, Session and Pattern views, and the
// windows on the base surface. A view publishes what the user selects here and follows what the others select; a view
// whose tab isn't shown catches up when it is.

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Machines;
using ReBuzz.Studio.Views.Sequence;
using ReBuzz.Studio.Views.Session;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class UxP2cTests
    {
        sealed class Rig : IDisposable
        {
            public SessionRig Song { get; }
            public MainWindow Window { get; }
            public MachineCore Drums, Bass;
            public PatternCore Beat, Line;
            public StudioSelection Selection => StudioSelection.For(Song.Core);

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
                if (tab == "Pattern")
                {
                    Window.ShowView("Pattern");         // the Tracker: the detail panel's page (P3a)
                    Settle();
                    return Window.Detail.Page;
                }
                var item = Window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == tab);
                item.IsSelected = true;
                Settle();
                return (Avalonia.Controls.Control)item.Content!;
            }

            public MachineCanvas Machines => ((MachineView)Show("Machines")).Canvas;
            public SequenceCanvas Sequence => ((SequenceView)Show("Sequence")).Canvas;
            public SessionView Session => (SessionView)Show("Session");
            public PatternPage Pattern => (PatternPage)Show("Pattern");

            public void Dispose()
            {
                Window.Close();
                Song.Dispose();
            }
        }

        // ------------------------------------------------------------------ the model

        [AvaloniaTest]
        public void TheSelectionIsOneMachineAndItsPattern()
        {
            using var rig = new Rig();
            var s = rig.Selection;
            Assert.That(s.Machine, Is.Null, "nothing selected: the song");
            Assert.That(s.Set(rig.Bass, rig.Line, this, 2), Is.True);
            Assert.That((s.Machine, s.Pattern, s.Scene), Is.EqualTo(((IMachine)rig.Bass, (IPattern)rig.Line, (int?)2)));
            Assert.That(s.Set(rig.Bass, rig.Line, this, 2), Is.False, "the same selection raises nothing");
            Assert.That(s.Set(rig.Drums, rig.Line, this), Is.True);
            Assert.That(s.Pattern, Is.Null, "another machine's pattern isn't the selection's");
            Assert.That(s.SelectMachine(rig.Drums, this), Is.False);
            s.Set(rig.Drums, rig.Beat, this);
            Assert.That(s.SelectMachine(rig.Drums, this), Is.False, "selecting the machine again keeps its pattern");
            Assert.That(s.Pattern, Is.SameAs(rig.Beat));
        }

        // ------------------------------------------------------------------ a view pair each way

        [AvaloniaTest]
        public void SelectingAMachineSelectsItsTrackInEveryView()
        {
            using var rig = new Rig();
            rig.Machines.Select(new IMachine[] { rig.Bass });
            Assert.That(rig.Selection.Machine, Is.SameAs(rig.Bass));
            Assert.That(rig.Pattern.ShownMachine, Is.SameAs(rig.Bass), "the Pattern view shows its pattern (it follows while its tab is hidden)");
            Assert.That(rig.Pattern.ShownPattern, Is.SameAs(rig.Line));
            Assert.That(rig.Sequence.CursorSequence.Machine, Is.SameAs(rig.Bass), "the Sequence's cursor is on its track");
            Assert.That(rig.Session.Selected?.Track, Is.EqualTo(1), "the Session's column");

            rig.Machines.Select(new IMachine[] { rig.Drums });
            Assert.That(rig.Sequence.CursorSequence.Machine, Is.SameAs(rig.Drums));
            Assert.That(rig.Session.Selected?.Track, Is.EqualTo(0));
            Assert.That(rig.Pattern.ShownPattern, Is.SameAs(rig.Beat));
        }

        [AvaloniaTest]
        public void MovingTheSequenceCursorSelectsTheMachineAndThePatternUnderIt()
        {
            using var rig = new Rig();
            var seq = rig.Sequence;
            int row = Enumerable.Range(0, seq.Rows).First(i => seq.SequenceAt(i).Machine == rig.Bass);
            seq.MoveCursor(0, row);
            Assert.That(rig.Selection.Machine, Is.SameAs(rig.Bass));
            Assert.That(rig.Machines.Selection, Is.EqualTo(new IMachine[] { rig.Bass }), "the Machines view shows it selected");
            Assert.That(rig.Session.Selected?.Track, Is.EqualTo(1));
        }

        [AvaloniaTest]
        public void SelectingAClipInTheSessionSelectsItsMachinePatternAndScene()
        {
            using var rig = new Rig();
            var session = rig.Session;
            session.Select(1, 0);
            Assert.That(rig.Selection.Machine, Is.SameAs(rig.Bass));
            Assert.That(rig.Selection.Pattern, Is.SameAs(rig.Line), "the clip's pattern");
            Assert.That(rig.Selection.Scene, Is.EqualTo(0));
            Assert.That(rig.Pattern.ShownPattern, Is.SameAs(rig.Line));
            Assert.That(rig.Machines.Selection, Is.EqualTo(new IMachine[] { rig.Bass }));
            Assert.That(rig.Sequence.CursorSequence.Machine, Is.SameAs(rig.Bass));
        }

        [AvaloniaTest]
        public void ChoosingAPatternInThePatternViewSelectsItsMachineEverywhere()
        {
            using var rig = new Rig();
            rig.Show("Pattern");
            PatternView.Edit(rig.Beat);
            Settle();
            Assert.That(rig.Selection.Machine, Is.SameAs(rig.Drums));
            Assert.That(rig.Selection.Pattern, Is.SameAs(rig.Beat));
            Assert.That(rig.Machines.Selection, Is.EqualTo(new IMachine[] { rig.Drums }));
            Assert.That(rig.Session.Selected?.Track, Is.EqualTo(0));
            Assert.That(rig.Sequence.CursorSequence.Machine, Is.SameAs(rig.Drums));
        }

        [AvaloniaTest]
        public void SelectingNothingInTheMachinesViewIsTheSong()
        {
            using var rig = new Rig();
            rig.Machines.Select(new IMachine[] { rig.Bass });
            rig.Machines.ClearSelection();
            Assert.That(rig.Selection.Machine, Is.Null);
            Assert.That(rig.Machines.Selection, Is.Empty);
        }

        [AvaloniaTest]
        public void AFollowingViewDoesNotEchoTheSelectionBack()
        {
            using var rig = new Rig();
            rig.Show("Pattern");                    // the Pattern page follows from the first
            int raised = 0;
            void Count(object source) => raised++;
            rig.Selection.Changed += Count;
            try
            {
                rig.Machines.Select(new IMachine[] { rig.Bass });
                Assert.That(raised, Is.EqualTo(1), "one change: the Pattern view follows without publishing its own");
                rig.Machines.Select(new IMachine[] { rig.Bass });
                Assert.That(raised, Is.EqualTo(1), "the same selection again changes nothing");
            }
            finally
            {
                rig.Selection.Changed -= Count;
            }
        }

        [AvaloniaTest]
        public void TheSessionKeepsItsSceneWhenAnotherViewSelectsAnotherTrack()
        {
            using var rig = new Rig();
            rig.Song.Layout.SetSlot(0, 1, rig.Beat);
            var session = rig.Session;
            session.Select(0, 1);
            rig.Machines.Select(new IMachine[] { rig.Bass });
            Assert.That(rig.Session.Selected, Is.EqualTo((1, 1)), "track 1, scene 1 as before");
        }

        // ------------------------------------------------------------------ the window's ground

        [AvaloniaTest]
        public void TheWindowAndItsTabStripAreOnTheBaseSurface()
        {
            using var rig = new Rig();
            Settle();
            var frame = rig.Window.CaptureRenderedFrame()!;
            using var fb = frame.Lock();
            Color At(int x, int y)
            {
                var px = new int[1];
                System.Runtime.InteropServices.Marshal.Copy(fb.Address + y * fb.RowBytes + x * 4, px, 0, 1);
                uint p = (uint)px[0];
                bool rgba = fb.Format == Avalonia.Platform.PixelFormat.Rgba8888;
                return rgba ? Color.FromRgb((byte)p, (byte)(p >> 8), (byte)(p >> 16)) : Color.FromRgb((byte)(p >> 16), (byte)(p >> 8), (byte)p);
            }
            // The tab strip, right of the last tab, and the status bar's end.
            Assert.That(At(1200, 40), Is.EqualTo(StudioTokens.SurfaceBase), "the tab strip");
            Assert.That(At(1270, 790), Is.EqualTo(StudioTokens.SurfaceBase), "the status bar");
        }
    }
}
