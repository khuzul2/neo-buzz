// Fork (docs/specs/ux-overhaul.md §11, MT1): tracks in the Machines view as coloured paths (option 2A). The rules on real songs (machines
// of the song, wires of any volume, muted and bypassed machines, a control machine, a Neo Patch, a folded group), every machine of
// the repo's songs in exactly one place, the Tracks toggle and its remembered choice, the chips and what a chip lights and dims, the
// selection a chip makes, the card's mute, solo, arm and meter, a rewire and a rename reaching the chips, and the canvas drawn as it was when
// the toggle is off. The rule tests proper, on built graphs, are ReBuzz.SessionTests/TrackModelTests.cs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using NeoBuzz.Editor;
using ReBuzz.Core;
using ReBuzz.Core.Tracks;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Machines;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class UxMt1Tests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static SongCore Song => Core.SongCore;

        static TrackModel Model => MachineTracksLive.For(Core).Model;

        static void Reset()
        {
            Studio.Host.Registry.Write(MachineTracksPreference.Key, false, MachineTracksPreference.Section);
            StudioTheme.Kind = AppearanceTheme.Dark;
        }

        // ------------------------------------------------------------------ building songs

        static IMachine Put(string dll, string name, double x, double y)
        {
            Song.CreateMachine(dll, null, name, null, null, null, -1, (float)x, (float)y);
            return Song.MachinesList.Last(m => m.Name == name);
        }

        static IMachine Master => Song.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);

        static IMachine M(string name) => name == "Master" ? Master : Song.MachinesList.First(m => m.Name == name);

        static void Wire(string chain, int amp = 0x4000)
        {
            var names = chain.Split('>');
            for (int i = 0; i + 1 < names.Length; i++) Song.ConnectMachines(M(names[i]), M(names[i + 1]), new[] { 0 }, new[] { 0 }, amp, 0x4000);
        }

        /// <summary>A song with a drum group, two sends into one return, a chain and a control machine (no machine is moved by the view).</summary>
        static void PopSong()
        {
            Put("Neo Poly", "Kick", -0.8, -0.65);
            Put("Neo Poly", "Snare", -0.8, -0.3);
            Put("Neo Comp", "Bus", -0.3, -0.5);
            Put("Neo Poly", "Bass", -0.8, 0.1);
            Put("Neo Drive", "Drive", -0.45, 0.1);
            Put("Neo EQ", "Eq", -0.1, 0.1);
            Put("Neo Poly", "Pad", -0.8, 0.5);
            Put("Neo Filter", "Filter", -0.45, 0.5);
            Put("Neo Poly", "Lead", -0.8, 0.85);
            Put("Neo Verb", "Verb", 0.3, 0.6);
            Put("Neo LFO", "Wobble", 0.55, -0.8);
            Wire("Kick>Bus>Master");
            Wire("Snare>Bus");
            Wire("Bass>Drive>Eq>Master");
            Wire("Pad>Filter>Master");
            Wire("Lead>Master");
            Wire("Pad>Verb>Master");
            Wire("Lead>Verb");
        }

        static string Ids(TrackModel m) => string.Join(",", m.Entries.Select(e => e.Id));

        static MainWindow ShowMachines(out MachineView view)
        {
            var (window, v) = OpenEmpty();
            view = v;
            return window;
        }

        static void Chip(MainWindow window, MachineView view, string name, bool click = true)
        {
            var chip = view.Tracks.ChipOf(M(name));
            Assert.That(chip, Is.Not.Null, "a chip for " + name);
            var p = chip.TranslatePoint(new Point(chip.Bounds.Width / 2, chip.Bounds.Height / 2), window)!.Value;
            if (click) Click(window, p);
            else { window.MouseMove(p, RawInputModifiers.None); Settle(); }
        }

        static Color Pixel(TopLevel top, Point at)
        {
            using var bitmap = top.CaptureRenderedFrame();
            using var fb = bitmap!.Lock();
            bool rgba = fb.Format == Avalonia.Platform.PixelFormat.Rgba8888;
            int p = System.Runtime.InteropServices.Marshal.ReadInt32(fb.Address + (int)at.Y * fb.RowBytes + (int)at.X * 4);
            return rgba ? Color.FromRgb((byte)(p & 0xFF), (byte)((p >> 8) & 0xFF), (byte)((p >> 16) & 0xFF)) : Color.FromRgb((byte)((p >> 16) & 0xFF), (byte)((p >> 8) & 0xFF), (byte)(p & 0xFF));
        }

        static int Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

        /// <summary>The nearest pixel to <paramref name="want"/> along a box's left border (the border is two pixels wide).</summary>
        static int BorderDistance(MainWindow window, MachineView view, IMachine m, Color want)
        {
            foreach (var v in window.GetVisualDescendants()) v.InvalidateVisual();
            Settle();
            var canvas = view.Canvas;
            var box = canvas.BoxOf(m);
            int best = int.MaxValue;
            using var bitmap = window.CaptureRenderedFrame();
            using var fb = bitmap!.Lock();
            bool rgba = fb.Format == Avalonia.Platform.PixelFormat.Rgba8888;
            for (int dy = 6; dy <= 20; dy += 2)
                for (double dx = 0; dx <= 2; dx += 1)
                {
                    var at = canvas.TranslatePoint(new Point((box.X + dx) * canvas.Zoom - canvas.Offset.X, (box.Center.Y + dy) * canvas.Zoom - canvas.Offset.Y), window)!.Value;
                    int p = System.Runtime.InteropServices.Marshal.ReadInt32(fb.Address + (int)at.Y * fb.RowBytes + (int)at.X * 4);
                    var c = rgba ? Color.FromRgb((byte)(p & 0xFF), (byte)((p >> 8) & 0xFF), (byte)((p >> 16) & 0xFF)) : Color.FromRgb((byte)((p >> 16) & 0xFF), (byte)((p >> 8) & 0xFF), (byte)(p & 0xFF));
                    best = Math.Min(best, Distance(c, want));
                }
            return best;
        }

        // ------------------------------------------------------------------ the rules, on the song's own machines

        [AvaloniaTest]
        public void TheRulesOnRealMachinesWiresAndAControlMachine()
        {
            var window = ShowMachines(out _);
            try
            {
                PopSong();
                Settle();
                var m = Model;
                Assert.That(Ids(m), Is.EqualTo("track:Kick,track:Snare,track:Bass,track:Pad,track:Lead,group:Bus,return:Verb"));
                Assert.That(m.EntryWithHead(m.Graph.Nodes.First(n => n.Name == "Bus")).Members.Select(e => e.Name), Is.EqualTo(new[] { "Kick", "Snare" }));
                Assert.That(m.EntryWithHead(m.Graph.Nodes.First(n => n.Name == "Verb")).Senders.Select(e => e.Name), Is.EqualTo(new[] { "Pad", "Lead" }));
                Assert.That(m.Entries.First(e => e.Name == "Bass").Chain, Is.EqualTo("Bass → Drive → Eq → Master"));
                Assert.That(m.Entries.First(e => e.Name == "Kick").Chain, Is.EqualTo("Kick → Bus → Master"));
                Assert.That(m.Unrouted.Select(u => (u.Node.Name, u.Why)), Is.EqualTo(new[] { ("Wobble", UnroutedReason.Control) }));
                // Every machine of the song is in exactly one place.
                foreach (var n in m.Graph.Nodes)
                    Assert.That((m.PlaceOf(n) == TrackPlace.Master ? 1 : 0) + m.Entries.Count(e => e.Machines.Contains(n)) + (m.Unrouted.Any(u => u.Node == n) ? 1 : 0), Is.EqualTo(1), n.Name);
            }
            finally { Close(window); }
        }

        [AvaloniaTest]
        public void AWireAtZeroVolumeAndAMutedOrBypassedMachineStillCount()
        {
            var window = ShowMachines(out _);
            try
            {
                PopSong();
                Settle();
                string before = MachineTracksLive.Signature(Model);
                // Pad's send at zero volume, Bass's Drive bypassed, Filter muted, Kick's wire to the bus at zero, Lead muted.
                Song.MachinesList.First(x => x.Name == "Pad").Outputs.First(c => c.Destination.Name == "Verb").Amp = 0;
                Song.MachinesList.First(x => x.Name == "Kick").Outputs.First().Amp = 0;
                M("Drive").IsBypassed = true;
                M("Filter").IsMuted = true;
                M("Lead").IsMuted = true;
                Settle();
                Assert.That(MachineTracksLive.Signature(Model), Is.EqualTo(before), "volume, mute and bypass change nothing about where a machine is");
            }
            finally { Close(window); }
        }

        [AvaloniaTest]
        public void ANeoPatchIsOneBoxAndAFoldedGroupChangesNothing()
        {
            var window = ShowMachines(out _);
            try
            {
                PopSong();
                bool patch = Core.MachineDLLs.ContainsKey("Neo Patch");
                if (patch)
                {
                    Put("Neo Patch", "Patch", 0.3, 0.1);
                    Wire("Patch>Master");
                }
                Settle();
                string before = MachineTracksLive.Signature(Model);
                if (patch)
                {
                    var e = Model.Entries.First(x => x.Name == "Patch");
                    Assert.That(e.Kind, Is.EqualTo(TrackKind.Track));
                    Assert.That(e.Machines.Count, Is.EqualTo(1), "a patch is one machine of the song: its nodes are not machines");
                    Assert.That(e.Chain, Is.EqualTo("Patch → Master"));
                }
                // Fold the drum group's machines into one box: the model is the same; the canvas draws it (a stripe of the members' colours).
                var group = MachineEdits.NewGroup(Core, new[] { M("Kick"), M("Snare"), M("Bus") });
                MachineEdits.Fold(Core, group);
                Settle();
                Assert.That(MachineTracksLive.Signature(Model), Is.EqualTo(before), "a machine folded into a group is where it was");
                MachineTracksPreference.On = true;
                Settle();
                var canvas = window.GetVisualDescendants().OfType<MachineCanvas>().First();
                Assert.That(canvas.TracksOn, Is.True);
                canvas.InvalidateVisual();
                Settle();
                Assert.That(canvas.PaintOf(M("Kick")), Is.Not.Null, "its paint is still the track's");
            }
            finally
            {
                MachineTracksPreference.On = false;
                Close(window);
            }
        }

        // ------------------------------------------------------------------ the repo's songs

        static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "ReBuzz.sln"))) return dir.FullName;
            Assert.Inconclusive("the repository isn't beside the tests");
            return null;
        }

        static void EveryMachineInOnePlace(string what)
        {
            var m = Model;
            Assert.That(m.Master, Is.Not.Null, what + ": a song has its Master");
            int machines = m.Graph.Nodes.Count;
            int placed = 1 + m.Entries.Sum(e => e.Machines.Count) + m.Unrouted.Count;
            Assert.That(placed, Is.EqualTo(machines), what + ": every machine is in exactly one place");
            var seen = new HashSet<TrackNode>();
            foreach (var n in m.Entries.SelectMany(e => e.Machines)) Assert.That(seen.Add(n), Is.True, what + ": " + n.Name + " is in two entries");
            foreach (var n in m.Graph.Nodes)
            {
                var place = m.PlaceOf(n);
                Assert.That(place == TrackPlace.Unrouted, Is.EqualTo(m.Unrouted.Any(u => u.Node == n)), what + ": " + n.Name);
                if (place == TrackPlace.Track || place == TrackPlace.Group || place == TrackPlace.Return) Assert.That(m.EntryOf(n).Machines, Does.Contain(n));
            }
            // Every unrouted machine really has no way to the Master, or makes no audio.
            foreach (var u in m.Unrouted.Where(u => u.Why != UnroutedReason.Control && u.Why != UnroutedReason.Loop))
                Assert.That(Reaches(u.Node, m.Master), Is.False, what + ": " + u.Node.Name + " is unrouted but reaches the Master");
        }

        static bool Reaches(TrackNode from, TrackNode to)
        {
            var seen = new HashSet<TrackNode>();
            var stack = new Stack<TrackNode>();
            stack.Push(from);
            while (stack.Count > 0)
            {
                var x = stack.Pop();
                if (!seen.Add(x)) continue;
                if (x == to) return true;
                foreach (var d in x.Destinations) stack.Push(d);
            }
            return false;
        }

        [AvaloniaTest]
        public void EveryMachineOfTheRepositorysSongsIsInExactlyOnePlace()
        {
            Dialogs.Responder = (mm, c, b) => UserAnswer.No;
            var opened = new List<string>();
            try
            {
                Core.Modified = false;
                Core.ExecuteCommand(BuzzCommand.NewFile);
                Settle();
                ReBuzz.Gallery.GallerySong.Build(Core);
                Settle();
                EveryMachineInOnePlace("the gallery song");
                opened.Add($"gallery song: {Model.Entries.Count} entries");

                var files = new List<string>();
                files.AddRange(Directory.GetFiles(Path.Combine(RepoRoot(), "Other", "Templates", "Songs"), "*.nbl"));
                files.AddRange(Directory.EnumerateFiles(Path.Combine(RepoRoot(), "ExternalMachines", "source"), "*.bmx", SearchOption.AllDirectories));
                foreach (var file in files.OrderBy(f => f))
                {
                    Core.Modified = false;
                    bool ok;
                    try { ok = Core.TryOpenSongFile(file); }
                    catch (Exception) { ok = false; }
                    Dispatcher.UIThread.RunJobs();
                    if (!ok) continue;
                    EveryMachineInOnePlace(Path.GetFileName(file));
                    var mm = Model;
                    opened.Add($"{Path.GetFileName(file)}: {mm.Graph.Nodes.Count} machines, {mm.Tracks.Count} tracks, {mm.Groups.Count} groups, {mm.Returns.Count} returns, {mm.Unrouted.Count} unrouted");
                }
                foreach (var line in opened) TestContext.Out.WriteLine("[mt1] " + line);
                Assert.That(opened.Count, Is.GreaterThanOrEqualTo(3), "the gallery song and at least the two templates opened");
                Assert.That(opened.Any(o => o.StartsWith("Bass House Template.nbl")), "the template opens: " + string.Join("; ", opened));
            }
            finally
            {
                Core.Modified = false;
                Core.ExecuteCommand(BuzzCommand.NewFile);
                Dialogs.Responder = null;
            }
        }

        // ------------------------------------------------------------------ the toggle

        [AvaloniaTest]
        public void TheTracksToggleIsOffUntilChosenAndTheChoiceIsRemembered()
        {
            using var restore = Studio.AfterTest(Reset);
            Reset();
            var window = ShowMachines(out var view);
            try
            {
                PopSong();
                Settle();
                Assert.That(view.TracksButton.Content, Is.EqualTo("Tracks"));
                Assert.That(view.Canvas.TracksOn, Is.False, "off until the user turns it on");
                Assert.That(view.Tracks.IsVisible, Is.False);
                var p = view.TracksButton.TranslatePoint(new Point(view.TracksButton.Bounds.Width / 2, view.TracksButton.Bounds.Height / 2), window)!.Value;
                Click(window, p);
                Assert.That(view.Canvas.TracksOn, Is.True);
                Assert.That(view.Tracks.IsVisible, Is.True);
                Assert.That(Studio.Host.Registry.Read(MachineTracksPreference.Key, false, MachineTracksPreference.Section), Is.True, "kept in the settings");
                // A Machines view opened later starts as it was left.
                Close(window);
                window = ShowMachines(out view);
                Assert.That(view.Canvas.TracksOn, Is.True, "remembered");
                PopSong();
                Settle();
                Assert.That(view.Tracks.Chips.Count, Is.EqualTo(8), "7 entries and Unrouted");
                p = view.TracksButton.TranslatePoint(new Point(view.TracksButton.Bounds.Width / 2, view.TracksButton.Bounds.Height / 2), window)!.Value;
                Click(window, p);
                Assert.That(view.Canvas.TracksOn, Is.False);
                Assert.That(Studio.Host.Registry.Read(MachineTracksPreference.Key, false, MachineTracksPreference.Section), Is.False);
            }
            finally { Close(window); }
        }

        [AvaloniaTest]
        public void ChipsStandForTheEntriesAndUnrouted()
        {
            using var restore = Studio.AfterTest(Reset);
            Reset();
            var window = ShowMachines(out var view);
            try
            {
                MachineTracksPreference.On = true;
                PopSong();
                Settle();
                var texts = view.Tracks.Chips.Select(c => Avalonia.Automation.AutomationProperties.GetName(c)).ToList();
                Assert.That(texts, Is.EqualTo(new[] { "Kick", "Snare", "Bass", "Pad", "Lead", "Bus · group", "Verb · return", "Unrouted 1" }));
                Assert.That(view.Tracks.Chips.All(c => c.Focusable), Is.True, "the keyboard reaches every chip");
                Assert.That(view.Tracks.ChipOf(M("Kick")).Tag, Is.EqualTo(TrackTarget.Entry(M("Kick"))));
                Assert.That(view.Tracks.UnroutedChip, Is.Not.Null);
                Studio.Screenshot(window, "mt1-tracks-on");
            }
            finally
            {
                MachineTracksPreference.On = false;
                Close(window);
            }
        }

        // ------------------------------------------------------------------ the paint

        [AvaloniaTest]
        public void BoxesAndWiresTakeTheirTracksColourAndAGroupShowsItsMembersAsAStripe()
        {
            using var restore = Studio.AfterTest(Reset);
            Reset();
            var window = ShowMachines(out var view);
            try
            {
                MachineTracksPreference.On = true;
                PopSong();
                Settle();
                var canvas = view.Canvas;
                var poly = StudioTokens.Identity(MachineCategory.Synth);
                Assert.That(canvas.PaintOf(M("Bass")).Colour, Is.EqualTo(poly), "Bass takes its generator's colour");
                Assert.That(canvas.PaintOf(M("Drive")).Colour, Is.EqualTo(poly), "and so does the effect it feeds");
                Assert.That(canvas.PaintOf(M("Eq")).Colour, Is.EqualTo(poly));
                Assert.That(canvas.PaintOf(M("Bus")).Colour, Is.EqualTo(MachineIdentity.ColourOf(M("Bus"))), "a group takes its machine's");
                Assert.That(canvas.PaintOf(M("Bus")).Stripe, Is.EqualTo(new[] { poly, poly }), "with its members' colours as a stripe, one for each");
                Assert.That(canvas.PaintOf(M("Verb")).Stripe, Is.Empty, "a return has no members");
                Assert.That(canvas.PaintOf(M("Wobble")).Routed, Is.False, "an unrouted machine is dashed grey");
                Assert.That(canvas.PaintOf(Master), Is.Null, "the Master keeps its own");
                Assert.That(BorderDistance(window, view, M("Drive"), poly), Is.LessThanOrEqualTo(8), "the border is the track's colour, in pixels");
                Studio.Screenshot(window, "mt1-paint");
            }
            finally
            {
                MachineTracksPreference.On = false;
                Close(window);
            }
        }

        [AvaloniaTest]
        public void AChipLightsItsTrackAndDimsTheRestWhileTheBoxesStayWhereTheyWere()
        {
            using var restore = Studio.AfterTest(Reset);
            Reset();
            var window = ShowMachines(out var view);
            try
            {
                MachineTracksPreference.On = true;
                PopSong();
                Settle();
                var canvas = view.Canvas;
                var positions = Song.Machines.ToDictionary(x => x, x => x.Position);
                Assert.That(canvas.Lit, Is.Null);
                Assert.That(Song.Machines.Any(canvas.IsDimmed), Is.False, "nothing is dimmed while nothing is lit");
                var poly = StudioTokens.Identity(MachineCategory.Synth);
                int lit = BorderDistance(window, view, M("Drive"), poly);

                Chip(window, view, "Bass", click: false);          // the pointer on the chip
                Assert.That(canvas.Lit, Is.EqualTo((TrackTarget?)TrackTarget.Entry(M("Bass"))));
                foreach (var name in new[] { "Bass", "Drive", "Eq", "Master" }) Assert.That(canvas.IsDimmed(M(name)), Is.False, name + " is on the lit path");
                foreach (var name in new[] { "Kick", "Snare", "Bus", "Pad", "Filter", "Lead", "Verb", "Wobble" }) Assert.That(canvas.IsDimmed(M(name)), Is.True, name + " is dimmed");
                Assert.That(BorderDistance(window, view, M("Drive"), poly), Is.EqualTo(lit), "the lit track keeps its colour");
                Assert.That(BorderDistance(window, view, M("Filter"), poly), Is.GreaterThan(BorderDistance(window, view, M("Drive"), poly) + 30), "a dimmed box is faded towards the canvas");
                Assert.That(view.Card.IsVisible, Is.True, "the card shows the lit track");

                // A group lights its members and everything downstream; a return what is downstream, and the sends stay as they are.
                Chip(window, view, "Bus", click: false);
                foreach (var name in new[] { "Kick", "Snare", "Bus", "Master" }) Assert.That(canvas.IsDimmed(M(name)), Is.False, name);
                Assert.That(canvas.IsDimmed(M("Bass")), Is.True);
                Chip(window, view, "Verb", click: false);
                foreach (var name in new[] { "Verb", "Master" }) Assert.That(canvas.IsDimmed(M(name)), Is.False, name);
                Assert.That(canvas.IsDimmed(M("Pad")), Is.True, "the senders are not on a return's path");
                // The unrouted chip lights what is unrouted.
                var un = view.Tracks.UnroutedChip;
                window.MouseMove(un.TranslatePoint(new Point(un.Bounds.Width / 2, un.Bounds.Height / 2), window)!.Value, RawInputModifiers.None);
                Settle();
                Assert.That(canvas.IsDimmed(M("Wobble")), Is.False);
                Assert.That(canvas.IsDimmed(M("Kick")), Is.True);

                // The pointer leaves: nothing is lit (no click yet).
                window.MouseMove(new Point(600, 600), RawInputModifiers.None);
                Settle();
                Assert.That(canvas.Lit, Is.Null);
                Assert.That(Song.Machines.Any(canvas.IsDimmed), Is.False);
                foreach (var (m, p) in positions) Assert.That(m.Position, Is.EqualTo(p), m.Name + " has not moved");
                // Keyboard focus lights too, and a chip's arrow keys move along the bar.
                var first = view.Tracks.Chips[0];
                first.Focus();
                Settle();
                Assert.That(canvas.Lit, Is.EqualTo((TrackTarget?)TrackTarget.Entry(M("Kick"))), "the focused chip is lit");
                window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
                Settle();
                Assert.That(canvas.Lit, Is.EqualTo((TrackTarget?)TrackTarget.Entry(M("Snare"))), "the right arrow moves to the next chip");
                Studio.Screenshot(window, "mt1-lit");
            }
            finally
            {
                MachineTracksPreference.On = false;
                Close(window);
            }
        }

        [AvaloniaTest]
        public void AClickSelectsTheGeneratorEveryViewFollowsAndKeepsTheTrackLitUntilAnotherOrEsc()
        {
            using var restore = Studio.AfterTest(Reset);
            Reset();
            var window = ShowMachines(out var view);
            try
            {
                MachineTracksPreference.On = true;
                PopSong();
                Settle();
                var canvas = view.Canvas;
                var selection = StudioSelection.For(Core);
                Chip(window, view, "Bass");
                Assert.That(selection.Machine, Is.SameAs(M("Bass")), "the track's generator is the studio's selection");
                Assert.That(canvas.Selection.Contains(M("Bass")), Is.True);
                Assert.That(canvas.Pinned, Is.EqualTo((TrackTarget?)TrackTarget.Entry(M("Bass"))));
                window.MouseMove(new Point(600, 600), RawInputModifiers.None);
                Settle();
                Assert.That(canvas.Lit, Is.EqualTo((TrackTarget?)TrackTarget.Entry(M("Bass"))), "a click keeps the track lit when the pointer leaves");
                Assert.That(view.Card.IsVisible, Is.True);
                // Another view selects a machine on the path: still lit. One off it: let go.
                selection.SelectMachine(M("Eq"), this);
                Settle();
                Assert.That(canvas.Pinned, Is.Not.Null, "a machine on the lit path keeps it");
                selection.SelectMachine(M("Lead"), this);
                Settle();
                Assert.That(canvas.Pinned, Is.Null, "a machine off it lets go");
                // A second click on the chip lets go too; Esc does as well.
                Chip(window, view, "Pad");
                Assert.That(canvas.Pinned, Is.Not.Null);
                Chip(window, view, "Pad");
                Assert.That(canvas.Pinned, Is.Null, "the same chip again");
                Chip(window, view, "Snare");
                canvas.Focus();
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Settle();
                Assert.That(canvas.Pinned, Is.Null, "Esc");
                Assert.That(selection.Machine, Is.Null);
                // The chip of the selected machine's track is outlined, whichever view selected it.
                selection.SelectMachine(M("Drive"), this);
                Settle();
                Assert.That(view.Tracks.ChipOf(M("Bass")).BorderThickness.Left, Is.EqualTo(2), "Drive is on Bass's track");
                Assert.That(view.Tracks.ChipOf(M("Kick")).BorderThickness.Left, Is.EqualTo(1));
            }
            finally
            {
                MachineTracksPreference.On = false;
                Close(window);
            }
        }

        // ------------------------------------------------------------------ the card

        [AvaloniaTest]
        public void TheCardShowsTheChainAndMutesSolosArmsAndMeters()
        {
            using var restore = Studio.AfterTest(Reset);
            Reset();
            var window = ShowMachines(out var view);
            try
            {
                MachineTracksPreference.On = true;
                PopSong();
                Settle();
                Chip(window, view, "Bass");
                var card = view.Card;
                Assert.That(card.IsVisible, Is.True);
                Assert.That(card.Title, Is.EqualTo("Track: Bass"));
                Assert.That(card.ChainText, Is.EqualTo("Bass → Drive → Eq → Master"));
                var bass = M("Bass");

                void Press(Button b)
                {
                    var p = b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), window)!.Value;
                    Click(window, p);
                }
                Press(card.MuteButton);
                Assert.That(bass.IsMuted, Is.True, "the card's M is the machine's mute");
                Press(card.MuteButton);
                Assert.That(bass.IsMuted, Is.False);
                Press(card.SoloButton);
                Assert.That(bass.IsSoloed, Is.True, "S solos the generator");
                Press(card.SoloButton);
                Assert.That(bass.IsSoloed, Is.False);
                Press(card.ArmButton);
                Assert.That(Core.SessionLayout.IsMachineArmed(bass), Is.True, "● arms it");
                Press(card.ArmButton);
                Assert.That(Core.SessionLayout.IsMachineArmed(bass), Is.False);

                // An outside change shows on the card; the meter follows the level of the track's wire.
                bass.IsMuted = true;
                Settle();
                Assert.That(card.MuteButton.Foreground, Is.SameAs(StudioTokens.TextPrimaryBrush), "lit while muted");
                bass.IsMuted = false;
                Settle();
                Assert.That(card.Meter.Level, Is.EqualTo(0));
                view.Canvas.PeakSource = c => (c.Source == bass ? 16384f : 0f, 0f);
                view.Canvas.TickMeters(1);
                Assert.That(card.Meter.Level, Is.GreaterThan(0.5), "−6 dB on the wire is about halfway up the −60 to 0 scale");

                // A group has a mute, no solo (not a generator).
                Chip(window, view, "Bus");
                Assert.That(card.Title, Is.EqualTo("Group: Bus"));
                Assert.That(card.SoloButton.IsEnabled, Is.False);
                Assert.That(card.MuteButton.IsEnabled, Is.True);
                Assert.That(card.SendsText, Does.Contain("Members: Kick, Snare"));
                Chip(window, view, "Verb");
                Assert.That(card.Title, Is.EqualTo("Return: Verb"));
                Assert.That(card.SendsText, Does.Contain("Receives sends from: Pad, Lead"));
                Studio.Screenshot(window, "mt1-card");
            }
            finally
            {
                MachineTracksPreference.On = false;
                Close(window);
            }
        }

        // ------------------------------------------------------------------ following the song

        [AvaloniaTest]
        public void ARewireARenameAndANewMachineReachTheChipsOnceAndAtOnce()
        {
            using var restore = Studio.AfterTest(Reset);
            Reset();
            var window = ShowMachines(out var view);
            try
            {
                MachineTracksPreference.On = true;
                PopSong();
                Settle();
                var live = MachineTracksLive.For(Core);
                Assert.That(view.Tracks.Chips.Count, Is.EqualTo(8));

                // Pad stops sending to the Verb: Lead alone feeds it, so it is a branch of Lead's track and the return is gone.
                int passes = live.Passes;
                Song.DisconnectMachines(M("Pad").Outputs.First(c => c.Destination.Name == "Verb"));
                Settle();
                Assert.That(Ids(Model), Is.EqualTo("track:Kick,track:Snare,track:Bass,track:Pad,track:Lead,group:Bus"));
                Assert.That(view.Tracks.Chips.Count, Is.EqualTo(7), "the chips follow the wires");
                Assert.That(live.Passes - passes, Is.LessThanOrEqualTo(2), "one pass for the burst (and the one the read made)");

                // The wire back, and a rename: the chip carries the new name.
                Wire("Pad>Verb");
                Settle();
                Assert.That(view.Tracks.Chips.Count, Is.EqualTo(8));
                Song.RenameMachineUndoable((MachineCore)M("Bass"), "Sub");
                Settle();
                Assert.That(view.Tracks.Chips.Select(c => Avalonia.Automation.AutomationProperties.GetName(c)), Does.Contain("Sub"));
                Assert.That(Model.Entries.First(e => e.Id == "track:Sub").Chain, Is.EqualTo("Sub → Drive → Eq → Master"));

                // A machine wired in, and deleted: twenty of them in a burst are one pass.
                passes = live.Passes;
                using (MachineEdits.Group(Core))
                    for (int i = 0; i < 20; i++)
                    {
                        Put("Neo Poly", "Extra " + i, 0.7, -0.9 + i * 0.05);
                        Wire("Extra " + i + ">Master");
                    }
                Settle();
                Assert.That(view.Tracks.EntryCount, Is.EqualTo(28));
                Assert.That(live.Passes - passes, Is.LessThanOrEqualTo(3), "a burst of events is one pass, not forty");
                Song.DeleteMachines(Song.Machines.Where(x => x.Name.StartsWith("Extra ")).ToList());
                Settle();
                Assert.That(view.Tracks.Chips.Count, Is.EqualTo(8));
            }
            finally
            {
                MachineTracksPreference.On = false;
                Close(window);
            }
        }

        [AvaloniaTest]
        public void ManyChipsStopAtTwoRowsAndTheRestIsBehindAPlusNChipThatFilters()
        {
            using var restore = Studio.AfterTest(Reset);
            Reset();
            var window = ShowMachines(out var view);
            try
            {
                MachineTracksPreference.On = true;
                for (int i = 0; i < 60; i++)
                {
                    Put("Neo Poly", "Voice number " + i, -0.9 + (i % 12) * 0.16, -0.8 + (i / 12) * 0.3);
                    Wire("Voice number " + i + ">Master");
                }
                Settle();
                var bar = view.Tracks;
                Assert.That(bar.EntryCount, Is.EqualTo(60));
                Assert.That(bar.MoreChip, Is.Not.Null, "what does not fit in two rows is behind a +N chip");
                Assert.That(bar.Chips[^1], Is.SameAs(bar.MoreChip), "the +N chip comes last in the keyboard order");
                int shown = bar.Chips.Count - 1;
                Assert.That(shown, Is.GreaterThan(4).And.LessThan(60));
                Assert.That(bar.HiddenCount, Is.EqualTo(60 - shown));
                Assert.That(Avalonia.Automation.AutomationProperties.GetName(bar.MoreChip), Is.EqualTo($"+{60 - shown} more tracks"));
                Assert.That(bar.Bounds.Height, Is.LessThanOrEqualTo(TrackBar.MaxHeight2Rows + 2), "two rows");
                Assert.That(bar.Bounds.Height, Is.GreaterThan(60), "and it wraps to the second");
                Assert.That(bar.Chips.Take(shown).All(c => c.Focusable), Is.True);
                for (int i = 0; i < shown; i++)
                    Assert.That(((TrackTarget)bar.Chips[i].Tag).Head, Is.SameAs(M("Voice number " + i)), "the bar keeps the model's order");

                // The keyboard: the arrow keys reach the +N chip, and Enter opens the list.
                bar.Chips[0].Focus();
                Settle();
                for (int i = 0; i < shown; i++) bar.Chips[i].RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
                Settle();
                Assert.That(bar.MoreChip.IsFocused, Is.True, "Right, to the end, lands on +N");
                bar.MoreChip.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle();
                Assert.That(bar.MoreOpen, Is.True);
                var items = bar.MoreItems;
                Assert.That(items.Count, Is.EqualTo(60 - shown), "every hidden track, in the same order");
                Assert.That(((TrackTarget)items[0].Tag).Head, Is.SameAs(M("Voice number " + shown)));
                Assert.That(((TrackTarget)items[^1].Tag).Head, Is.SameAs(M("Voice number 59")));

                bar.MoreFilter.Text = "number 59";
                Settle();
                Assert.That(bar.MoreItems.Count, Is.EqualTo(1));
                bar.MoreFilter.Text = "nothing like it";
                Settle();
                Assert.That(bar.MoreItems.Count, Is.EqualTo(0), "no match: a line says so");
                bar.MoreFilter.Text = "NUMBER 59";
                Settle();
                var item = bar.MoreItems.Single();
                item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle();
                Assert.That(bar.MoreOpen, Is.False, "choosing closes the list");
                Assert.That(view.Canvas.Pinned, Is.EqualTo((TrackTarget?)TrackTarget.Entry(M("Voice number 59"))), "and lights the track");
                Assert.That(StudioSelection.For(Core).Machine, Is.SameAs(M("Voice number 59")), "and selects its generator");
                Assert.That(bar.MoreChip.BorderThickness.Left, Is.EqualTo(2), "the +N chip is outlined while the selection is behind it");
                Studio.Screenshot(window, "mt1-many-chips");
            }
            finally
            {
                MachineTracksPreference.On = false;
                Close(window);
            }
        }

        [AvaloniaTest]
        public void TheMastersChainIsOneChipWithItsOwnCardAndTheTracksEndAtIt()
        {
            using var restore = Studio.AfterTest(Reset);
            Reset();
            var window = ShowMachines(out var view);
            try
            {
                MachineTracksPreference.On = true;
                Put("Neo Poly", "Kick", -0.8, -0.5);
                Put("Neo Poly", "Bass", -0.8, 0.3);
                Put("Neo EQ", "Eq", 0.1, -0.1);
                Put("Neo Comp", "Limiter", 0.5, -0.1);
                Wire("Kick>Eq");
                Wire("Bass>Eq");
                Wire("Eq>Limiter>Master");
                Settle();
                Assert.That(Ids(Model), Is.EqualTo("track:Kick,track:Bass,master:Master"));
                Assert.That(view.Tracks.Chips.Select(c => Avalonia.Automation.AutomationProperties.GetName(c)), Is.EqualTo(new[] { "Kick", "Bass", "Master" }), "a chip Master, not an Eq group");
                Chip(window, view, "Eq", click: false);
                var card = view.Card;
                Assert.That(card.IsVisible, Is.True);
                Assert.That(card.Title, Is.EqualTo("Master chain"));
                Assert.That(card.ChainText, Is.EqualTo("Eq → Limiter → Master"));
                Assert.That(view.Canvas.IsDimmed(M("Kick")), Is.True, "the Master's chain lights itself, not the tracks");
                Assert.That(view.Canvas.IsDimmed(M("Limiter")), Is.False);
                Chip(window, view, "Bass", click: false);
                Assert.That(card.ChainText, Is.EqualTo("Bass → Master chain"));
                Assert.That(view.Canvas.IsDimmed(M("Limiter")), Is.False, "a track lights the Master's chain after it");
                Assert.That(view.Canvas.IsDimmed(M("Kick")), Is.True);
            }
            finally
            {
                MachineTracksPreference.On = false;
                Close(window);
            }
        }

        [AvaloniaTest]
        public void TheCardSitsWhereItCoversNoBoxAndNoWire()
        {
            using var restore = Studio.AfterTest(Reset);
            Reset();
            var window = ShowMachines(out var view);
            try
            {
                MachineTracksPreference.On = true;
                PopSong();
                Settle();
                var canvas = view.Canvas;
                Rect Card() => new Rect(card_TopLeft(view), view.Card.Bounds.Size);
                foreach (var name in new[] { "Bass", "Kick", "Pad", "Lead", "Verb" })
                {
                    Chip(window, view, name, click: false);
                    Settle();
                    var r = Card();
                    Assert.That(view.Card.IsVisible, Is.True);
                    foreach (var m in Song.Machines)
                    {
                        var b = canvas.BoxOf(m);
                        var box = new Rect(canvas.ToView(b.TopLeft), canvas.ToView(b.BottomRight));
                        Assert.That(r.Intersects(box), Is.False, $"the card for {name} covers {m.Name}");
                    }
                    foreach (var c in canvas.Wires)
                    {
                        var a = canvas.ToView(canvas.BoxOf(c.Source).Center);
                        var z = canvas.ToView(canvas.BoxOf(c.Destination).Center);
                        for (int i = 0; i <= 40; i++)
                            Assert.That(r.Contains(new Point(a.X + (z.X - a.X) * i / 40, a.Y + (z.Y - a.Y) * i / 40)), Is.False, $"the card for {name} covers the wire {c.Source.Name} to {c.Destination.Name}");
                    }
                }
                // The spot is fixed: it does not follow the lit track.
                Chip(window, view, "Bass", click: false);
                Settle();
                var at = Card();
                Chip(window, view, "Lead", click: false);
                Settle();
                Assert.That(Card().TopLeft, Is.EqualTo(at.TopLeft));
            }
            finally
            {
                MachineTracksPreference.On = false;
                Close(window);
            }
        }

        static Point card_TopLeft(MachineView view) => new Point(Avalonia.Controls.Canvas.GetLeft(view.Card), Avalonia.Controls.Canvas.GetTop(view.Card));

        // ------------------------------------------------------------------ Classic and the off state

        [AvaloniaTest]
        public void WithTheToggleOffTheCanvasIsDrawnAsItWasInEveryTheme()
        {
            using var restore = Studio.AfterTest(Reset);
            foreach (var theme in new[] { AppearanceTheme.Dark, AppearanceTheme.Light, AppearanceTheme.Classic })
            {
                Reset();
                var window = ShowMachines(out var view);
                try
                {
                    StudioTheme.Kind = theme;
                    PopSong();
                    Settle();
                    foreach (var v in window.GetVisualDescendants()) v.InvalidateVisual();
                    Settle();
                    // The canvas's own pixels only: the window around it (top bar meters, focus on the toggle) may differ by platform.
                    byte[] Frame()
                    {
                        var origin = view.Canvas.TranslatePoint(default, window) ?? default;
                        using var bitmap = window.CaptureRenderedFrame();
                        using var fb = bitmap!.Lock();
                        double scale = window.RenderScaling;
                        int x0 = Math.Max(0, (int)Math.Ceiling(origin.X * scale)), y0 = Math.Max(0, (int)Math.Ceiling(origin.Y * scale));
                        int w = Math.Min((int)Math.Floor(view.Canvas.Bounds.Width * scale), fb.Size.Width - x0);
                        int h = Math.Min((int)Math.Floor(view.Canvas.Bounds.Height * scale), fb.Size.Height - y0);
                        Assert.That(w > 0 && h > 0, Is.True, "the canvas is on screen");
                        var data = new byte[w * 4 * h];
                        for (int y = 0; y < h; y++)
                            System.Runtime.InteropServices.Marshal.Copy(fb.Address + (y0 + y) * fb.RowBytes + x0 * 4, data, y * w * 4, w * 4);
                        return data;
                    }
                    var before = Frame();
                    MachineTracksPreference.On = true;
                    Settle();
                    Chip(window, view, "Bass");
                    MachineTracksPreference.On = false;
                    Settle();
                    Assert.That(view.Canvas.TracksOn, Is.False);
                    Assert.That(view.Card.IsVisible, Is.False);
                    foreach (var v in window.GetVisualDescendants()) v.InvalidateVisual();
                    Settle();
                    // Only the selection the chip's click made differs: clear it, as the user would with Esc.
                    view.Canvas.ClearSelection();
                    Settle();
                    foreach (var v in window.GetVisualDescendants()) v.InvalidateVisual();
                    Settle();
                    var after = Frame();
                    int diff = after.AsSpan().CommonPrefixLength(before), cw = (int)Math.Floor(view.Canvas.Bounds.Width * window.RenderScaling);
                    Assert.That(after.AsSpan().SequenceEqual(before), Is.True, $"{theme}: the Machines view is the same picture once the toggle is off again (first difference at canvas pixel {diff / 4 % Math.Max(1, cw)}, {diff / 4 / Math.Max(1, cw)})");
                }
                finally { Close(window); }
            }
        }
    }
}
