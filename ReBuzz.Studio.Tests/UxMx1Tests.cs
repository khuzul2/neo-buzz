// Fork (docs/specs/ux-overhaul.md §11, MX1): one mixer everywhere, and a colour per track. The mixer in its three densities (Full: the
// Detail panel maximised or the Master's double click; Compact: Detail > Mixer and under the Session View; Header: the Arrangement's
// track headers), the Master's click and double click and the maximise key, every control one undo step, the Arrangement's header
// controls and the mixer's being the same values, a group folding, the Master's volume in dB, and track colours: given when a track is
// made and when a song without them opens, the user's colour winning, saved in .nbl, never an undo step of their own.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views;
using NeoBuzz.TestSupport;
using ReBuzz.Studio.Views.Machines;
using ReBuzz.Studio.Views.Mixer;
using ReBuzz.Studio.Views.Sequence;
using static ReBuzz.Studio.Tests.MachineViewTests;
using static ReBuzz.Studio.Tests.UxMt1Tests;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class UxMx1Tests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static SongCore Song => Core.SongCore;

        static int Depth => ((ManagedActionStack)Song.ActionStack).Actions.Count();

        // The history keeps the steps that were undone until a new one replaces them: where we stand is the count less those.
        static int undone;

        static int Pos => Depth - undone;

        static void Undo()
        {
            Song.ActionStack.Undo();
            undone++;
        }

        static void Redo()
        {
            Song.ActionStack.Redo();
            undone--;
        }

        /// <summary>Steps a gesture added to the history.</summary>
        static int Steps(Action gesture)
        {
            Settle();
            int before = Pos;
            gesture();
            Settle();
            undone = 0;
            return Depth - before;
        }

        static MainWindow Open(out MachineView view)
        {
            // The Detail panel remembers its page: each test starts on the Tracker, as a new install does.
            Studio.Host.Registry.Write(ReBuzz.Studio.Views.Detail.DetailPanel.MixerKey, false, ReBuzz.Studio.Views.Detail.DetailPanel.RegistrySection);
            undone = 0;
            var (window, v) = OpenEmpty();
            view = v;
            return window;
        }

        static Point CentreOf(Control c, TopLevel top) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), top)!.Value;

        static void Drag(TopLevel top, Control c, double dx, double dy)
        {
            var from = CentreOf(c, top);
            top.MouseMove(from, RawInputModifiers.None);
            top.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
            for (int i = 1; i <= 4; i++) top.MouseMove(new Point(from.X + dx * i / 4, from.Y + dy * i / 4), RawInputModifiers.LeftMouseButton);
            top.MouseUp(new Point(from.X + dx, from.Y + dy), MouseButton.Left, RawInputModifiers.None);
            Settle();
        }

        static MixerStripView Strip(MixerView mixer, string id) => mixer.Strips.Single(s => s.Spec.Id == id);

        static void Pump() => Settle();

        static string Ids(MixerView mixer) => string.Join(",", mixer.Strips.Select(s => s.Spec.Id));

        // ------------------------------------------------------------------ densities

        [AvaloniaTest]
        public void TheMixerHasThreeDensitiesAndTheMaximiseKeyMakesItFullAndBack()
        {
            var window = Open(out _);
            try
            {
                PopSong();
                Settle();
                var detail = window.Detail;
                detail.ShowMixer();
                Settle();
                var mixer = detail.MixerPage;
                Assert.That(detail.IsMixer && detail.IsOpen, Is.True, "Detail > Mixer");
                Assert.That(mixer.Density, Is.EqualTo(MixerDensity.Compact), "docked: Compact");
                string ids = Ids(mixer);
                foreach (var part in new[] { "track:Kick", "track:Snare", "group:Bus", "track:Bass", "track:Pad", "track:Lead", "return:Verb", "master:Master" })
                    Assert.That(ids, Does.Contain(part), "a strip for " + part + ": " + ids);
                Assert.That(mixer.Strips.Last().Kind, Is.EqualTo(StripKind.Master), "the Master last");
                Assert.That(ids.IndexOf("track:Kick"), Is.LessThan(ids.IndexOf("group:Bus")), "a group's members come before it");

                var bass = Strip(mixer, "track:Bass");
                Assert.That(bass.Density, Is.EqualTo(MixerDensity.Compact));
                Assert.That(bass.Fader, Is.Not.Null.And.Property("IsVisible").True, "fader");
                Assert.That(bass.Meter, Is.Not.Null, "meter");
                Assert.That(bass.PanBar, Is.Not.Null, "pan");
                Assert.That(bass.Mute, Is.Not.Null, "On");
                Assert.That(bass.Solo, Is.Not.Null, "Solo");
                Assert.That(bass.Arm, Is.Not.Null, "Arm");
                Assert.That(bass.InsertButtons, Is.Empty, "Compact has no inserts");

                // The maximise key: Compact becomes Full, and the same key makes it Compact again.
                var mods = Cmd | RawInputModifiers.Shift;
                window.KeyPressQwerty(PhysicalKey.F, mods);
                Settle();
                Assert.That(detail.IsMaximised, Is.True, "the maximise key");
                Assert.That(mixer.Density, Is.EqualTo(MixerDensity.Full), "maximised: Full");
                var full = Strip(mixer, "track:Bass");
                Assert.That(full.Density, Is.EqualTo(MixerDensity.Full));
                Assert.That(full.InsertButtons.Select(b => (string)b.Content).ToArray(), Is.EqualTo(new[] { "Drive", "Eq" }), "inserts in signal order");
                Assert.That(full.AddEffectButton, Is.Not.Null, "+ Effect");
                Assert.That(full.KindText, Is.EqualTo("track"));
                Assert.That(full.Cue, Is.Not.Null, "Cue");
                Assert.That(full.Sends, Has.Count.EqualTo(1), "a send to the return");
                Assert.That(Strip(mixer, "master:Master").PeakText, Does.StartWith("peak"), "the Master shows its peak");
                Assert.That(Strip(mixer, "master:Master").CueOutText, Does.StartWith("cue"), "and its cue output");

                window.KeyPressQwerty(PhysicalKey.F, mods);
                Settle();
                Assert.That(detail.IsMaximised, Is.False, "the same key collapses it");
                Assert.That(mixer.Density, Is.EqualTo(MixerDensity.Compact));

                // The expand glyph does the same.
                window.Detail.MaximiseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle();
                Assert.That(mixer.Density, Is.EqualTo(MixerDensity.Full), "the ⤢ button");
                window.Detail.MaximiseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle();
                Assert.That(mixer.Density, Is.EqualTo(MixerDensity.Compact));
            }
            finally { Close(window); }
        }

        [AvaloniaTest]
        public void TheMasterOpensTheCompactMixerOnASingleClick()
        {
            var window = Open(out var view);
            try
            {
                PopSong();
                Settle();
                var detail = window.Detail;
                Assert.That(detail.IsMixer, Is.False);
                Click(window, view.PointOf(Master, window));
                Settle();
                Assert.That(detail.IsOpen, Is.False, "it waits for a second click first (the machine view would move from under it)");
                System.Threading.Thread.Sleep(450);
                Settle();
                Assert.That(detail.IsMixer && detail.IsOpen, Is.True, "a single click shows Detail > Mixer");
                Assert.That(detail.IsMaximised, Is.False);
                Assert.That(detail.MixerPage.Density, Is.EqualTo(MixerDensity.Compact), "compact in the panel below");
                Assert.That(StudioSelection.For(Core).Machine, Is.SameAs(Master), "the Master is selected");

            }
            finally { Close(window); }
        }

        [AvaloniaTest]
        public void TheMasterOpensTheFullMixerOnADoubleClick()
        {
            var window = Open(out var view);
            try
            {
                PopSong();
                StudioSelection.For(Core).SelectMachine(M("Kick"), this);
                Settle();
                var detail = window.Detail;
                Assert.That(detail.IsMixer, Is.False);
                DoubleClick(window, view.PointOf(Master, window));
                Settle();
                System.Threading.Thread.Sleep(450);
                Settle();
                Assert.That(detail.IsMaximised, Is.True, "and the single click's panel did not follow it");
                Assert.That(detail.IsMixer && detail.IsMaximised, Is.True, "a double click opens the Full mixer");
                Assert.That(detail.MixerPage.Density, Is.EqualTo(MixerDensity.Full));
                var master = detail.MixerPage.MasterStrip;
                Assert.That(master, Is.Not.Null);
                Assert.That(master.IsEffectivelyVisible, Is.True, "the Master's strip is in view");
                Assert.That(master.DbText, Does.EndWith("dB"), "the Master's volume in dB");
                Assert.That(StudioSelection.For(Core).Machine, Is.SameAs(Master), "with the Master selected");
            }
            finally { Close(window); }
        }

        [AvaloniaTest]
        public void AGroupFoldsItsMembersIntoOneStrip()
        {
            var window = Open(out _);
            try
            {
                PopSong();
                Settle();
                window.Detail.ShowMixer();
                Settle();
                var mixer = window.Detail.MixerPage;
                Assert.That(Ids(mixer), Does.Contain("track:Kick").And.Contain("track:Snare"));
                var group = Strip(mixer, "group:Bus");
                Assert.That(group.KindText, Does.Contain("2 tracks"));
                var kind = (Control)group.GetVisualDescendants().First(c => c is TextBlock t && t.Text == group.KindText);
                Click(window, CentreOf(kind, window));
                Settle();
                Assert.That(Ids(mixer), Does.Not.Contain("track:Kick").And.Not.Contain("track:Snare"), "folded: the members are in the group's strip");
                Assert.That(Strip(mixer, "group:Bus").Spec.Folded, Is.True);
                Assert.That(Strip(mixer, "group:Bus").KindText, Does.Contain("▸"));
                kind = (Control)Strip(mixer, "group:Bus").GetVisualDescendants().First(c => c is TextBlock t && t.Text == Strip(mixer, "group:Bus").KindText);
                Click(window, CentreOf(kind, window));
                Settle();
                Assert.That(Ids(mixer), Does.Contain("track:Kick").And.Contain("track:Snare"), "unfolded");
            }
            finally { Close(window); }
        }

        // ------------------------------------------------------------------ one step per gesture

        [AvaloniaTest]
        public void EveryMixerControlIsOneUndoStepPerGesture()
        {
            var window = Open(out _);
            try
            {
                PopSong();
                Settle();
                window.Detail.ShowMixer(maximise: true);
                Settle();
                var mixer = window.Detail.MixerPage;
                var bass = Strip(mixer, "track:Bass");
                var toDrive = M("Bass").Outputs.Single();
                int amp0 = toDrive.Amp;

                // The fader: a drag is one step (and the sound followed the hand), undo takes the whole move back.
                Assert.That(Steps(() => Drag(window, bass.Fader, 0, 30)), Is.EqualTo(1), "a drag is one step");
                Assert.That(toDrive.Amp, Is.Not.EqualTo(amp0), "the fader moved the wire's volume");
                Undo();
                Assert.That(toDrive.Amp, Is.EqualTo(amp0), "undo takes the whole drag back");
                Redo();
                Assert.That(toDrive.Amp, Is.Not.EqualTo(amp0));
                Undo();

                // The wheel is a gesture of its own: one notch, one step.
                Assert.That(Steps(() => { window.MouseMove(CentreOf(bass.Fader, window), RawInputModifiers.None); window.MouseWheel(CentreOf(bass.Fader, window), new Vector(0, -1)); Settle(); }), Is.EqualTo(1), "a wheel notch is one step");
                Undo();

                // Pan.
                int pan0 = toDrive.Pan;
                Assert.That(Steps(() => Drag(window, bass.PanBar, 20, 0)), Is.EqualTo(1), "pan: one step");
                Assert.That(toDrive.Pan, Is.Not.EqualTo(pan0));
                Undo();
                Assert.That(toDrive.Pan, Is.EqualTo(pan0));

                // On and Solo are the machine's flags: a click is a step.
                Assert.That(Steps(() => Click(window, CentreOf(bass.Mute, window))), Is.EqualTo(1), "On: one step");
                Assert.That(M("Bass").IsMuted, Is.True);
                Undo();
                Assert.That(M("Bass").IsMuted, Is.False);
                Assert.That(Steps(() => Click(window, CentreOf(bass.Solo, window))), Is.EqualTo(1), "Solo: one step");
                Assert.That(M("Bass").IsSoloed, Is.True);
                Undo();
                Assert.That(M("Bass").IsSoloed, Is.False);

                // A send raised from nothing makes the wire and sets its level: the gesture is one step, not two.
                var verb = M("Verb");
                Assert.That(SessionMixer.GetSend(M("Bass"), verb), Is.EqualTo(0), "Bass has no send yet");
                Assert.That(Steps(() => Drag(window, bass.Sends.Single(), 30, 0)), Is.EqualTo(1), "a send's gesture is one step, the wire with it");
                Assert.That(SessionMixer.GetSend(M("Bass"), verb), Is.GreaterThan(0), "a send");
                Undo();
                Assert.That(SessionMixer.GetSend(M("Bass"), verb), Is.EqualTo(0));

                // The Master's volume: its Volume parameter, one step.
                var volume = MixerEdits.MasterVolume(Core);
                int v0 = volume.GetValue(0);
                mixer.RevealMaster();
                Settle();
                Assert.That(Steps(() => Drag(window, Strip(mixer, "master:Master").Fader, 0, 25)), Is.EqualTo(1), "the Master fader: one step");
                Assert.That(volume.GetValue(0), Is.Not.EqualTo(v0));
                Undo();
                Assert.That(volume.GetValue(0), Is.EqualTo(v0));
            }
            finally { Close(window); }
        }

        [AvaloniaTest]
        public void TheMasterVolumeIsInDecibelsEverywhere()
        {
            var window = Open(out _);
            try
            {
                var volume = MixerEdits.MasterVolume(Core);
                Assert.That(volume.DescribeValue(0), Is.EqualTo("0.0 dB"), "full volume");
                Assert.That(volume.DescribeValue(0x2000), Is.EqualTo("-6.0 dB"), "half the gain");
                Assert.That(volume.DescribeValue(volume.MaxValue), Does.Contain("-inf"), "none at the bottom");
                Assert.That(SessionMixer.MasterVolumeText(0x2000, 0x4000), Is.EqualTo("-6.0 dB"));
            }
            finally { Close(window); }
        }

        // ------------------------------------------------------------------ the Arrangement's headers

        [AvaloniaTest]
        public void TheArrangementHeadersHoldTheMixersControlsAndChangeTheSameValues()
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            SongFiles.New().Wait();
            undone = 0;
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 900 };
            window.Show();
            try
            {
                PopSong();
                foreach (var name in new[] { "Bass", "Pad" })
                    Song.AddSequence(M(name), Song.SequencesList.Count);
                var tab = window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Arrangement");
                tab.IsSelected = true;
                Settle();
                var seq = (SequenceView)tab.Content!;
                Click(window, CentreOf(seq.ArrangementModeButton, window));
                Settle();
                Assert.That(seq.IsArrangementMode, Is.True);
                var headers = seq.Headers;
                headers.Rebuild();
                Settle();
                var strips = headers.MixerStrips;
                Assert.That(strips.Select(s => s.Machine.Name), Is.SupersetOf(new[] { "Bass", "Pad" }), "a header strip for each track");
                var bass = strips.Single(s => s.Machine.Name == "Bass");
                Assert.That(bass.Density, Is.EqualTo(MixerDensity.Header));
                Assert.That(bass.Fader, Is.Not.Null);
                Assert.That(bass.Meter, Is.Not.Null);
                Assert.That(bass.PanBar, Is.Not.Null);
                Assert.That(strips.Single(s => s.Machine.Name == "Pad").Sends, Has.Count.EqualTo(1), "a send per return");

                // The same value as the mixer's strip: a header drag moves the wire, and the mixer reads it.
                var wire = M("Bass").Outputs.Single();
                int amp0 = wire.Amp;
                Assert.That(Steps(() => Drag(window, bass.Fader, -12, 0)), Is.EqualTo(1), "one step");
                Assert.That(wire.Amp, Is.Not.EqualTo(amp0), "the header's fader changed the volume");
                {
                    var mixer = new MixerView(Core, MixerDensity.Compact);
                    var host = new Window { Content = mixer, Width = 900, Height = 400 };
                    host.Show();
                    Settle();
                    var strip = Strip(mixer, "track:Bass");
                    strip.Refresh();
                    Assert.That(strip.DbText, Is.EqualTo(bass.DbText.Length > 0 ? bass.DbText : strip.DbText));
                    Assert.That(SessionMixer.FaderOf(wire.Amp), Is.EqualTo(strip.Fader.Value).Within(0.01), "the mixer's fader shows the header's move");
                    host.Close();
                }

                // On and Solo of the header are the mixer's too (one step each).
                var mute = headers.Find<ToggleButton>("Mute Bass")!;
                Assert.That(Steps(() => Click(window, CentreOf(mute, window))), Is.EqualTo(1), "the header's mute is one step");
                Assert.That(M("Bass").IsMuted, Is.True);
                Undo();
                Assert.That(M("Bass").IsMuted, Is.False);
            }
            finally
            {
                Core.Modified = false;
                window.Close();
                Settle();
                Dialogs.Responder = null;
            }
        }

        // ------------------------------------------------------------------ colours

        static void NewSong()
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Core.Modified = false;
            Core.ExecuteCommand(BuzzCommand.NewFile);
            Settle();
        }

        static IMachine Gen(string name)
        {
            Song.CreateMachine("Neo Poly", null, name, null, null, null, -1, 0f, 0f);
            var m = Song.MachinesList.Last(x => x.Name == name);
            Song.ConnectMachines(m, Master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            Song.AddSequence(m, Song.SequencesList.Count);
            Settle();
            return m;
        }

        [AvaloniaTest]
        public void ANewGeneratorGetsTheNextColourNotInUseAndTheUsersColourWins()
        {
            NewSong();
            try
            {
                var store = Core.TrackColours;
                var a = Gen("MX1 A");
                var b = Gen("MX1 B");
                var c = Gen("MX1 C");
                int ia = store.IndexOf(a), ib = store.IndexOf(b), ic = store.IndexOf(c);
                Assert.That(new[] { ia, ib, ic }, Has.All.GreaterThanOrEqualTo(0), "each track has a colour");
                Assert.That(new[] { ia, ib, ic }.Distinct().Count(), Is.EqualTo(3), "each its own: the next colour not in use");
                var choices = SessionColourChoices.Choices;
                foreach (int i in new[] { ia, ib, ic })
                    Assert.That(ReBuzz.Studio.Shell.Design.StudioTokens.IsReservedForState(Color.FromUInt32(choices[i].Argb)), Is.False, "never a state-near colour");

                // The user's colour wins, in one undo step; undo gives the old one back.
                int other = (ia + 5) % choices.Length;
                Assert.That(Steps(() => store.SetByUser(a, other)), Is.EqualTo(1), "one step");
                Assert.That(store.IndexOf(a), Is.EqualTo(other));
                Assert.That(TrackColour.IndexOf(a), Is.EqualTo(other), "what every view draws");
                // A colour picked in the Session View is the user's too, and is the one every view then shows.
                Core.SessionLayout.Ensure();
                int track = Enumerable.Range(0, Core.SessionLayout.TrackCount).First(t => Core.SessionLayout.SequenceOf(t)?.Machine == a);
                Core.SessionLayout.SetTrackColor(track, (other + 2) % choices.Length);
                Settle();
                Assert.That(store.IndexOf(a), Is.EqualTo((other + 2) % choices.Length), "the Session track's colour wins");
                Core.SessionLayout.SetTrackColor(track, -1);
                Settle();
                Assert.That(store.IndexOf(a), Is.EqualTo(other), "automatic again: the track's own colour");
                Undo();
                Undo();
                Undo();
                Assert.That(store.IndexOf(a), Is.EqualTo(ia), "undo");
                Assert.That(TrackColourStore.Parse("red"), Is.EqualTo(Array.FindIndex(choices, x => x.Name == "Red")), "by name");
            }
            finally { NewSong(); Dialogs.Responder = null; }
        }

        [AvaloniaTest]
        [TestCase(".nbl")]
        public void ColoursAreSavedWithTheSongAndASongWithoutThemGetsThemOnOpenWithNoStep(string extension)
        {
            NewSong();
            string path = TestTemp.Path("mx1-" + Guid.NewGuid().ToString("N") + extension);
            string bare = TestTemp.Path("mx1-bare-" + Guid.NewGuid().ToString("N") + extension);
            try
            {
                var store = Core.TrackColours;
                var a = Gen("MX1 A");
                var b = Gen("MX1 B");
                var c = Gen("MX1 C");
                int pick = (store.IndexOf(b) + 3) % SessionColourChoices.Choices.Length;
                store.SetByUser(b, pick);
                var saved = new[] { store.IndexOf(a), store.IndexOf(b), store.IndexOf(c) };
                Core.SaveSongCopy(path);
                NewSong();
                Assert.That(Core.TrackColours.Get("MX1 A"), Is.EqualTo(-1), "a new song has none");
                Core.Modified = false;
                Core.OpenSongFile(path);
                Pump();
                var opened = new[] { Core.TrackColours.IndexOf(M("MX1 A")), Core.TrackColours.IndexOf(M("MX1 B")), Core.TrackColours.IndexOf(M("MX1 C")) };
                Assert.That(opened, Is.EqualTo(saved), extension + ": the colours come back, the user's among them");

                // A song saved without colours (an older one): they are given when it opens, in track order, as a quiet change.
                Core.TrackColours.Clear();
                Core.SaveSongCopy(bare);
                NewSong();
                Core.Modified = false;
                Core.OpenSongFile(bare);
                Pump();
                var given = new[] { M("MX1 A"), M("MX1 B"), M("MX1 C") }.Select(m => Core.TrackColours.IndexOf(m)).ToArray();
                Assert.That(given, Has.All.GreaterThanOrEqualTo(0), "assigned on open");
                Assert.That(given.Distinct().Count(), Is.EqualTo(3), "in track order, each its own");
                Assert.That(((ManagedActionStack)Song.ActionStack).Actions.Count(), Is.EqualTo(0), "never an undo step");
                Assert.That(Core.Modified, Is.False, "and not a change to save by itself");
            }
            finally
            {
                Dialogs.Responder = null;
                foreach (var p in new[] { path, bare }) try { File.Delete(p); } catch (IOException) { }
                NewSong();
            }
        }

        static JsonObject Call(string tool, JsonObject args)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new ReBuzz.Agent.Mcp.McpSession(), _ => System.Threading.Tasks.Task.CompletedTask, System.Threading.CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                System.Threading.Thread.Sleep(2);
            }
            var result = task.GetAwaiter().GetResult()!["result"]!.AsObject();
            Assert.That(result["isError"]?.GetValue<bool>() == true, Is.False, result.ToJsonString());
            return result;
        }

        [AvaloniaTest]
        public void TheAgentReadsAndSetsATrackColourThroughMachineList()
        {
            NewSong();
            try
            {
                var a = Gen("MX1 Agent");
                var b = Gen("MX1 Other");
                var listed = Call("machine_list", new JsonObject())["structuredContent"]!["machines"]!.AsArray().Single(m => m!["name"]!.GetValue<string>() == "MX1 Agent")!;
                Assert.That(listed["colour"]?.GetValue<string>(), Is.Not.Null, "machine_list shows the colour");
                Assert.That(listed["colour_index"]!.GetValue<int>(), Is.EqualTo(Core.TrackColours.IndexOf(a)));
                Assert.That(Call("machine_list", new JsonObject())["structuredContent"]!["machines"]!.AsArray().First()!["colour"], Is.Null, "the Master has none");

                int depth = Depth;
                undone = 0;
                Call("machine_set", new JsonObject { ["machine"] = "MX1 Agent", ["colour"] = "Slate Blue" });
                Assert.That(NameOf(Core.TrackColours.IndexOf(a)), Is.EqualTo("Slate Blue"), "set by name");
                Assert.That(Depth, Is.EqualTo(depth + 1), "one undo step");
                Call("machine_set", new JsonObject { ["machine"] = "MX1 Agent", ["colour"] = "auto" });
                Assert.That(Core.TrackColours.IndexOf(a), Is.Not.EqualTo(Core.TrackColours.IndexOf(b)), "auto: a colour no other track uses");
                var bad = Studio.Host.Agent.Host.Server.HandleAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call",
                    ["params"] = new JsonObject { ["name"] = "machine_set", ["arguments"] = new JsonObject { ["machine"] = "MX1 Agent", ["colour"] = "Chartreuse" } },
                }, new ReBuzz.Agent.Mcp.McpSession(), _ => System.Threading.Tasks.Task.CompletedTask, System.Threading.CancellationToken.None);
                var until = DateTime.UtcNow.AddSeconds(30);
                while (!bad.IsCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(2); }
                Assert.That(bad.GetAwaiter().GetResult()!["result"]!["isError"]?.GetValue<bool>(), Is.True, "a colour that is not in the palette is refused");
            }
            finally { NewSong(); Dialogs.Responder = null; }
        }

        static string NameOf(int index) => TrackColourStore.NameOf(index);
    }

}
