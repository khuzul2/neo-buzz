// Fork (docs/specs/ux-overhaul.md P3g polish, P3h, P3i; decisions 84-90): the master meter against the engine's own peak on the demo song, the
// clip card by view, Sync's settings on the UI thread, the audio badge's menu; each machine's identity colour on its box, track and clips (kept
// out of the song, still recolourable, contrast held); an empty state with a next step in every view.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using NeoBuzz.Editor;
using NUnit.Framework;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Shell.Preferences;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Inspector;
using ReBuzz.Studio.Views.Machines;
using ReBuzz.Studio.Views.Sequence;
using ReBuzz.Studio.Views.Session;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class UxP3hTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        // ------------------------------------------------------------------ P3g polish

        /// <summary>
        /// The master meter against the engine's own peak, on the demo song (gallery mac-342 read +5.1 dB: that was the shot's own test signal, 1.8
        /// pushed through the safe master). The Drop scene is rendered as the audio thread renders it; the meter's bars must stand where the largest
        /// sample the engine produced stands, and the demo song must not clip.
        /// </summary>
        [AvaloniaTest]
        public void TheMasterMeterReadsTheEnginesOwnPeakOnTheDemoSongAndItDoesNotClip()
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Core.ExecuteCommand(BuzzCommand.Stop);
            Core.Modified = false;
            Core.ExecuteCommand(BuzzCommand.NewFile);
            Settle();
            ReBuzz.Gallery.GallerySong.Build(Core);
            Core.Modified = false;
            var loop = Core.Looping;
            try
            {
                Core.Looping = true;
                var layout = Core.SessionLayout;
                layout.Ensure();
                layout.LaunchScene(2);
                Core.Playing = true;
                var work = new WorkManager(Core, new WorkThreadEngine(1), 0, BuzzGUI.Common.Global.EngineSettings);
                Core.ClearMasterOver();
                Core.SafeMaster.Reset();
                var buffer = new float[512];
                float engine = 0;
                for (int tick = 0; tick < 160; tick++)
                {
                    var mi = ReBuzzCore.masterInfo;
                    int frames = Math.Max(1, mi.SamplesPerTick - mi.PosInTick);
                    while (frames > 0)
                    {
                        int n = Math.Min(frames, buffer.Length / 2);
                        lock (ReBuzzCore.AudioLock) work.MainAudioFillBuffer(buffer, 0, n * 2);
                        for (int i = 0; i < n * 2; i++) engine = Math.Max(engine, Math.Abs(buffer[i]));    // the engine's master, before the device's safe master
                        Core.SafeMaster.Process(buffer.AsSpan(0, n * 2), 2, 44100);                    // as the device callback does next
                        frames -= n;
                    }
                }
                double engineDb = 20 * Math.Log10(engine);
                var meter = new MasterMeter(() => Core);
                meter.Refresh();
                TestContext.Out.WriteLine($"[ux-measure] demo song, Drop scene, 160 ticks: engine master peak {engineDb:0.00} dBFS ({engine:0.0000}); meter bars L {meter.Levels.Left:0.00} R {meter.Levels.Right:0.00}; clip light '{meter.OverText}'");
                Assert.That(engine, Is.GreaterThan(0.01f), "the song makes sound");
                Assert.That(Math.Max(meter.Levels.Left, meter.Levels.Right), Is.EqualTo(Math.Min(0, engineDb)).Within(0.1), "the meter reads the engine's own peak (dBFS, the bars stop at 0)");
                Assert.That(engine, Is.LessThanOrEqualTo(1.0f), "the demo song stays under 0 dBFS at the master");
                Assert.That(meter.OverText, Is.EqualTo(""), "so the clip light stays off");
            }
            finally
            {
                Core.Playing = false;
                Core.Looping = loop;
                Core.ClearMasterOver();
                Core.SafeMaster.Reset();
                Core.Modified = false;
                Dialogs.Responder = null;
            }
        }

        [AvaloniaTest]
        public void TheClipCardNeverShowsInTheMachineViewEvenWithAClipSelected()
        {
            using var restore = Studio.AfterTest(() =>
            {
                Studio.Host.Registry.Write(InspectorPanel.OpenKey, true, InspectorPanel.RegistrySection);
                Studio.Host.Registry.Write(InspectorPanel.WidthKey, InspectorPanel.DefaultWidth, InspectorPanel.RegistrySection);
            });
            using var rig = new SessionRig("Lead");
            var lead = rig.Machine("Lead");
            var hook = rig.Pattern(lead, "Hook", SessionRig.Bar, (0, 72));
            rig.Layout.Ensure();
            rig.Layout.SetSlot(0, 0, hook);
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            try
            {
                var inspector = window.Inspector;
                inspector.Open();
                // A clip chosen in the Session View stays the selection (Lead, Hook) when the Machine View is shown.
                window.ShowView("Session");
                StudioSelection.For(Core).Set(lead, hook, this);
                Settle(window);
                Assert.That(inspector.ClipPanel.IsVisible, Is.True, "the Session View, where clips are");
                window.ShowView("Machines");
                Settle(window);
                Assert.That(StudioSelection.For(Core).Pattern, Is.SameAs(hook), "the selection still holds the pattern");
                Assert.That(inspector.ClipPanel.IsVisible, Is.False, "the Machine View picks machines: no Hook, Launch, Region, Follow action or Scene");
                // Where a pattern is the point (the Arrangement), a clip selection shows its card.
                window.ShowView("Sequence");
                Settle(window);
                Assert.That(inspector.ClipPanel.IsVisible, Is.True, "a clip selection in the Arrangement");
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaTest]
        public void SyncSettingsSetByTheAgentRunOnTheUiThread()
        {
            var sync = Core.SessionSync;
            Assert.That(sync, Is.Not.Null);
            int ui = Environment.CurrentManagedThreadId;
            var threads = new List<int>();
            void Seen() { lock (threads) threads.Add(Environment.CurrentManagedThreadId); }
            sync.Changed += Seen;
            using var restore = Studio.AfterTest(() =>
            {
                sync.Changed -= Seen;
                sync.SetFollowMidiClock(false);
                sync.SetClockOut("Ghost Port", false);
            });
            UxP3fTests.AgentTool("settings_set", new JsonObject { ["key"] = "sync.MidiClockIn", ["value"] = true });
            Assert.That(sync.FollowMidiClock, Is.True);
            UxP3fTests.AgentTool("settings_set", new JsonObject { ["key"] = "sync.MidiClockOut", ["value"] = "Ghost Port" });
            Assert.That(sync.ClockOutNames, Is.EqualTo(new[] { "Ghost Port" }));
            UxP3fTests.AgentTool("settings_set", new JsonObject { ["key"] = "sync.MidiClockIn", ["value"] = false });
            lock (threads)
            {
                Assert.That(threads, Is.Not.Empty, "SessionSync raised its Changed event");
                Assert.That(threads.Distinct().ToList(), Is.EqualTo(new[] { ui }), "every change ran on the UI thread, which SessionSync requires, not on the agent's engine thread");
            }
        }

        [AvaloniaTest]
        public void TheAudioBadgeMenuResetsTheDropoutCounterAndOpensAudioSettings()
        {
            var (window, _) = OpenEmpty();
            try
            {
                var menu = window.Health.Menu;
                Assert.That(menu, Is.Not.Null);
                var headers = menu.Items.OfType<MenuItem>().Select(i => (string)i.Header!).ToList();
                Assert.That(headers, Is.EqualTo(new[] { "Audio settings…", "Reset dropout counter" }));
                long before = ReBuzzCore.DeadlineMisses;
                System.Threading.Interlocked.Add(ref ReBuzzCore.DeadlineMisses, 3);
                window.Health.Refresh();
                window.Health.ResetDropouts();
                Assert.That(ReBuzzCore.DeadlineMisses, Is.EqualTo(0), "the counter is zero again");
                Assert.That(window.Health.Shown, Is.Not.EqualTo(AudioHealthBadge.State.Dropouts), "and the badge is not amber");
                ((MenuItem)menu.Items[0]!).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                Assert.That(PreferencesWindow.Current?.Selected.Title, Is.EqualTo("Audio"));
            }
            finally
            {
                PreferencesWindow.Current?.Close();
                Close(window);
            }
        }

        // ------------------------------------------------------------------ P3h: colour per machine

        [AvaloniaTest]
        public void EachMachinesBoxTrackAndClipsWearItsCategorysIdentityColour()
        {
            using var rig = new SessionRig("Lead", "Drums");
            var lead = rig.Machine("Lead");                         // Neo Poly: Synth
            var drums = rig.Add("Neo Drums", "Kit");                 // Drums
            var verb = rig.Add("Neo Verb", "Room", track: false);   // Delay and reverb
            var hook = rig.Pattern(lead, "Hook", SessionRig.Bar, (0, 72));
            rig.Layout.Ensure();
            rig.Layout.SetSlot(0, 0, hook);
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            try
            {
                Assert.That(MachineIdentity.CategoryOf(lead), Is.EqualTo(MachineCategory.Synth));
                Assert.That(MachineIdentity.CategoryOf(drums), Is.EqualTo(MachineCategory.Drums));
                Assert.That(MachineIdentity.CategoryOf(verb), Is.EqualTo(MachineCategory.DelayReverb));
                Assert.That(MachineIdentity.ColourOf(rig.Master), Is.Null, "the Master has no category: its box stays grey");
                Assert.That(MachineIdentity.ColourOf(lead), Is.EqualTo(StudioTokens.Identity(MachineCategory.Synth)));

                // The Machine View: the colour whole along the top of the box.
                window.ShowView("Machines");
                Settle(window);
                var canvas = window.Tabs.Items.Cast<TabItem>().Select(t => t.Content).OfType<MachineView>().First().Canvas;
                Color BandPixel(IMachine m)
                {
                    foreach (var v in window.GetVisualDescendants()) v.InvalidateVisual();
                    Settle(window);
                    var box = canvas.BoxOf(m);
                    var at = canvas.TranslatePoint(new Point(box.Center.X * canvas.Zoom - canvas.Offset.X, (box.Y + 2.5) * canvas.Zoom - canvas.Offset.Y), window)!.Value;
                    using var bitmap = window.CaptureRenderedFrame();
                    using var fb = bitmap!.Lock();
                    bool rgba = fb.Format == Avalonia.Platform.PixelFormat.Rgba8888;
                    int p = System.Runtime.InteropServices.Marshal.ReadInt32(fb.Address + (int)at.Y * fb.RowBytes + (int)at.X * 4);
                    return rgba ? Color.FromRgb((byte)(p & 0xFF), (byte)((p >> 8) & 0xFF), (byte)((p >> 16) & 0xFF)) : Color.FromRgb((byte)((p >> 16) & 0xFF), (byte)((p >> 8) & 0xFF), (byte)(p & 0xFF));
                }
                void Near(Color a, Color b, string what)
                {
                    Assert.That(Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B), Is.LessThanOrEqualTo(6), $"{what}: {a} against {b}");
                }
                Near(BandPixel(lead), StudioTokens.Identity(MachineCategory.Synth), "Lead's band is the Synth pink");
                Near(BandPixel(drums), StudioTokens.Identity(MachineCategory.Drums), "the kit's is the Drums amber");
                Near(BandPixel(verb), StudioTokens.Identity(MachineCategory.DelayReverb), "the verb's is the Delay and reverb violet");
                Studio.Screenshot(window, "identity-machines");

                // The Session View: the track's header strip and the clip with no colour of its own.
                window.ShowView("Session");
                Settle(window);
                var session = window.Tabs.Items.Cast<TabItem>().Select(t => t.Content).OfType<SessionView>().First();
                Assert.That(PatternColors.Get(hook), Is.EqualTo(-1), "the clip has no colour of its own");
                var cell = session.SlotAt(0, 0);
                Assert.That(((ISolidColorBrush)cell.Background!).Color, Is.EqualTo(StudioTokens.Identity(MachineCategory.Synth)), "its fill is its machine's identity colour");
                var strip = session.Headers[0].GetVisualDescendants().OfType<Border>().First(b => b.Height == 3);
                Assert.That(((ISolidColorBrush)strip.Background!).Color, Is.EqualTo(StudioTokens.Identity(MachineCategory.Synth)), "and so is its track's strip");

                // The Arrangement: a block with no colour of its own.
                window.ShowView("Sequence");
                Settle(window);
                var seq = window.Tabs.Items.Cast<TabItem>().Select(t => t.Content).OfType<SequenceView>().First();
                Assert.That(seq.Canvas.BlockColour(0, hook), Is.EqualTo(StudioTokens.Identity(MachineCategory.Synth)));

                // Nothing is written into the song (§3.4): the indices stay "none".
                Assert.That(PatternColors.Get(hook), Is.EqualTo(-1));
                Assert.That(rig.Layout.Ensure().Tracks[0].ColorIndex, Is.EqualTo(-1));

                // The user can still recolour, and take the colour back.
                rig.Layout.SetClipColor(hook, 4);
                rig.Layout.SetTrackColor(0, 7);
                window.ShowView("Session");
                Settle(window);
                var own = Color.FromUInt32(SessionColourChoices.Choices[4].Argb);
                Assert.That(((ISolidColorBrush)session.SlotAt(0, 0).Background!).Color, Is.EqualTo(own), "a colour the user picked wins");
                Assert.That(seq.Canvas.BlockColour(0, hook), Is.EqualTo(own), "in the Arrangement too");
                rig.Layout.SetClipColor(hook, -1);
                Settle(window);
                Assert.That(((ISolidColorBrush)session.SlotAt(0, 0).Background!).Color, Is.EqualTo(StudioTokens.Identity(MachineCategory.Synth)), "None gives the machine's colour back");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void TheIdentityColoursHoldTheContrastRulesOnBoxesClipsAndTracks()
        {
            var view = StudioTokens.Of(Tokens.SurfaceView);
            var panel = StudioTokens.Of(Tokens.SurfacePanel);
            var text = StudioTokens.TextPrimary;
            foreach (MachineCategory c in Enum.GetValues(typeof(MachineCategory)))
            {
                var id = StudioTokens.Identity(c);
                string name = c.ToString();
                Assert.That(StudioTokens.Contrast(id, view), Is.GreaterThanOrEqualTo(5), name + ": 5:1 on a view");
                Assert.That(StudioTokens.Contrast(text, MachineIdentity.BodyOf(id, false)), Is.GreaterThanOrEqualTo(4.5), name + ": the machine's name on its box");
                Assert.That(StudioTokens.Contrast(text, MachineIdentity.BodyOf(id, true)), Is.GreaterThanOrEqualTo(4.5), name + ": and on a muted box");
                Assert.That(StudioTokens.Contrast(id, MachineIdentity.BodyOf(id, false)), Is.GreaterThanOrEqualTo(3), name + ": the band against its box (a UI part)");
                Assert.That(StudioTokens.Contrast(MachineIdentity.BandOf(id, true), MachineIdentity.BodyOf(id, true)), Is.GreaterThanOrEqualTo(1.5), name + ": a muted band is still apart from its box");
                Assert.That(StudioTokens.Contrast(StudioTokens.TextOn(id), id), Is.GreaterThanOrEqualTo(4.58), name + ": a clip's label on the colour");
                Assert.That(StudioTokens.Contrast(id, StudioTokens.Of(Tokens.SurfacePanel)), Is.GreaterThanOrEqualTo(3), name + ": a track's strip on a header");
                Assert.That(StudioTokens.IsReservedForState(id), Is.False, name + " is no state colour");
            }
            // The catalogue's ten categories are the enum's ten, in order (the identity is found by position).
            Assert.That(ReBuzz.Agent.Guides.MachineCatalog.Categories.Count, Is.EqualTo(Enum.GetValues(typeof(MachineCategory)).Length));
            Assert.That(ReBuzz.Agent.Guides.MachineCatalog.Categories, Is.EqualTo(new[] { "Synths", "Drums", "Samplers", "Dynamics", "EQ & Filter", "Delay & Reverb", "Modulation", "Distortion", "Utility", "Control" }));
        }

        // ------------------------------------------------------------------ P3i: empty states

        static IEnumerable<string> Labels(EmptyState e) => e.Buttons.Select(b => (string)b.Content!);

        [AvaloniaTest]
        public void EveryViewOfAnEmptySongShowsItsNextStep()
        {
            var (window, machines) = OpenEmpty();
            try
            {
                var starters = new[] { "Neo Poly", "Neo Drums", "Neo Wave" };
                T Tab<T>(string header) where T : Control => window.Tabs.Items.Cast<TabItem>().Select(t => t.Content).OfType<T>().First();

                // Machines (as P3d made it): the starters.
                Assert.That(machines.Starters.IsVisible, Is.True);

                // Arrangement.
                window.ShowView("Sequence");
                Settle(window);
                var seq = Tab<SequenceView>("Sequence");
                seq.RefreshEmpty();
                Assert.That(seq.EmptyState.IsVisible, Is.True);
                Assert.That(seq.EmptyState.Title, Is.EqualTo("No tracks yet"));
                Assert.That(Labels(seq.EmptyState), Is.EqualTo(starters.Concat(new[] { "Add machine…" }).ToList()));

                // Session.
                window.ShowView("Session");
                Settle(window);
                var session = Tab<SessionView>("Session");
                Assert.That(session.EmptyState.IsVisible, Is.True);
                Assert.That(Labels(session.EmptyState), Is.EqualTo(starters.Concat(new[] { "Add machine…" }).ToList()));

                // Tracker and piano roll: the same state, in both views of a pattern.
                window.Detail.ShowPage(false, false);
                Settle(window);
                var page = PatternView.Page;
                Assert.That(page.EmptyState.IsVisible, Is.True, "the Tracker");
                Assert.That(page.EmptyState.Title, Is.EqualTo("No machine to edit"));
                Assert.That(Labels(page.EmptyState), Is.EqualTo(starters.Concat(new[] { "Add machine…" }).ToList()));
                window.Detail.ShowPage(true, false);
                Settle(window);
                Assert.That(page.EmptyState.IsVisible, Is.True, "the piano roll");
                Assert.That(page.Roll.IsVisible, Is.False, "and not the roll's own message under it");

                // Device (nothing selected: the Master, a click on it, has a device view of its own).
                StudioSelection.For(Core).Clear(this);
                window.Detail.ShowDevice();
                Settle(window);
                var dock = (ReBuzz.Studio.Views.Device.DeviceDock)window.Detail.DevicePage;
                Assert.That(dock.Empty.IsVisible, Is.True);
                Assert.That(Labels(dock.Empty), Is.EqualTo(starters.Concat(new[] { "Add machine…" }).ToList()));

                // History.
                window.Inspector.Open();
                window.Inspector.ShowTab(InspectorPanel.HistoryTabName);
                Settle(window);
                var history = window.Inspector.History;
                history.Rebuild();
                Assert.That(history.EmptyState.IsVisible, Is.True, "no steps");
                Assert.That(Labels(history.EmptyState), Is.EqualTo(starters.Concat(new[] { "Add machine…" }).ToList()));

                // Browser: the starters are its first rows, and an empty search says what to do.
                window.Browser.Open();
                window.Browser.Refresh();
                Assert.That(window.Browser.Rows.Take(4), Does.Contain("#Start here").Or.Contain("Start here"));
                window.Browser.Search.Text = "qzxjk";
                window.Browser.Refresh();
                var said = window.Browser.List.Items.OfType<ListBoxItem>().Select(i => Avalonia.Automation.AutomationProperties.GetName(i)).ToList();
                Assert.That(string.Join("|", said), Does.Contain("Nothing matches").And.Contain("clear the search"));
                window.Browser.Search.Text = "";

                // One click on a starter in any of them: a machine, one undo step, and every empty state that was about "no machine" changes.
                int steps = Core.SongCore.ActionStack.Steps.Count;
                var poly = (page.EmptyState.Step("Neo Poly") ?? throw new AssertionException("no Neo Poly step"));
                page.EmptyState.Step("Neo Poly")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle(window);
                Assert.That(Core.SongCore.Machines.Select(m => m.Name), Does.Contain("Poly").Or.Contain("Neo Poly"));
                Assert.That(Core.SongCore.ActionStack.Steps.Count, Is.EqualTo(steps + 1), "one undo step");
                var made = StudioSelection.For(Core).Machine;
                Assert.That(made, Is.Not.Null, "the new machine is selected");
                Assert.That(page.EmptyState.Title, Is.EqualTo($"{made!.Name} has no patterns"), "the Tracker now says to make a pattern");
                Assert.That(Labels(page.EmptyState), Is.EqualTo(new[] { "New pattern" }));
                seq.RefreshEmpty();
                Assert.That(seq.EmptyState.Title, Is.EqualTo("Nothing is arranged yet"), "the Arrangement has its track and asks for a pattern");
                Assert.That(Labels(seq.EmptyState), Is.EqualTo(new[] { "New pattern here" }));
                history.Rebuild();
                Assert.That(history.EmptyState.IsVisible, Is.False, "the History has its first step");
                Assert.That(machines.Starters.IsVisible, Is.False);

                // "New pattern" makes one (undoable) and the Tracker's empty state goes.
                page.EmptyState.Step("New pattern")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle(window);
                Assert.That(made.Patterns.Count, Is.EqualTo(1));
                Assert.That(page.EmptyState.IsVisible, Is.False, "the piano roll shows the new pattern");
                Assert.That(page.Roll.IsVisible, Is.True);
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaTest]
        public void TheSessionViewWithTracksAndNoClipsSaysHowToMakeOne()
        {
            using var rig = new SessionRig("Lead");
            var view = rig.Show();
            Assert.That(view.EmptyState.IsVisible, Is.True);
            Assert.That(view.EmptyState.Title, Is.EqualTo("No clips yet"));
            Assert.That(Labels(view.EmptyState), Is.EqualTo(new[] { "Create a clip" }));
            view.EmptyState.Step("Create a clip")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            rig.Settle();
            Assert.That(rig.Layout.Ensure().SceneSlots(0).Any(), Is.True, "a clip is in the first slot");
            Assert.That(view.EmptyState.IsVisible, Is.False);
        }

        [Test]
        public void TheEmptyStateTextHoldsTheContrastRulesOnViewsAndPanels()
        {
            foreach (var ground in new[] { Tokens.SurfaceView, Tokens.SurfacePanel, Tokens.SurfaceBase })
            {
                var g = StudioTokens.Of(ground);
                Assert.That(StudioTokens.Contrast(StudioTokens.TextPrimary, g), Is.GreaterThanOrEqualTo(4.5), "the title");
                Assert.That(StudioTokens.Contrast(StudioTokens.TextSecondary, g), Is.GreaterThanOrEqualTo(4.5), "the text");
            }
        }

        /// <summary>
        /// The usability script (the exit criterion: first sound from an empty song, 3 steps, from the review's 7): 1 the Neo Poly starter, 2 open the
        /// keyboard under its device view, 3 press a key. The machine is in the song, wired to the Master, selected; its keyboard is there to press.
        /// </summary>
        [AvaloniaTest]
        public void FirstSoundFromAnEmptySongTakesThreeSteps()
        {
            var (window, machines) = OpenEmpty();
            try
            {
                int steps = 0;
                // 1: a starter, on the Machine View a new song opens on.
                ((ILogical)machines.Starters).GetLogicalDescendants().OfType<Button>().First(b => (string)b.Content! == "Neo Poly").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                steps++;
                Settle(window);
                var poly = StudioSelection.For(Core).Machine!;
                Assert.That(poly.Outputs.Select(o => o.Destination.DLL.Info.Type), Is.EqualTo(new[] { MachineType.Master }), "wired to the Master: it can be heard");
                // 2: open the keyboard under the device view (the Device page follows the selection).
                window.Detail.ShowDevice();
                Settle(window);
                var dock = (ReBuzz.Studio.Views.Device.DeviceDock)window.Detail.DevicePage;
                Assert.That(dock.Page, Is.Not.Null, "the device view of the new machine");
                var keyboard = NeoBuzz.Editor.MachineKeyboardFrame.Of(dock.Frame);
                Assert.That(keyboard, Is.Not.Null, "with its keyboard under it");
                keyboard!.SetShown(true);
                steps++;
                Settle(window);
                // 3: a key, and the sound is the master's.
                var key = keyboard.Keys.ElementOf(keyboard.Keys.Layout.First + 24);
                Assert.That(key, Is.Not.Null);
                var onKey = key!.TranslatePoint(new Point(key.Bounds.Width / 2, key.Bounds.Height * 0.8), window)!.Value;
                window.MouseDown(onKey, Avalonia.Input.MouseButton.Left);
                steps++;
                var work = new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings);
                var buffer = new float[512];
                double sum = 0;
                int count = 0;
                lock (ReBuzzCore.AudioLock)
                    for (int i = 0; i < 100; i++)
                    {
                        work.MainAudioFillBuffer(buffer, 0, 512);
                        foreach (var x in buffer) { sum += x * (double)x; count++; }
                    }
                window.MouseUp(onKey, Avalonia.Input.MouseButton.Left);
                double db = 20 * Math.Log10(Math.Max(1e-12, Math.Sqrt(sum / count)));
                TestContext.Out.WriteLine($"[ux-measure] first sound from an empty song: {steps} steps (the review counted 7), the master at {db:0.0} dB RMS over the first 0.6 s");
                Assert.That(db, Is.GreaterThan(-70), "the key is heard at the master");
                Assert.That(steps, Is.LessThanOrEqualTo(3));
            }
            finally
            {
                Close(window);
            }
        }

        static void Settle() => MachineViewTests.Settle();

        static void Settle(Window window)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
