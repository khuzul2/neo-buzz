// Fork (docs/specs/ux-overhaul.md P3g, decisions 58 and 79-82): Feedback. No type-to-search on the canvas (the Add Machine key opens the
// browser); Sync's settings are in the agent's list; the top bar is the one transport (metronome, time signature, a tempo to drag, Tap, the
// clock in bars and in minutes and seconds); the master meter has its scale; the machine boxes and wires brighten with the level through them.

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
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Agent.App;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Commands;
using ReBuzz.Studio.Shell.Preferences;
using ReBuzz.Studio.Views.Machines;
using ReBuzz.Studio.Views.Session;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class UxP3gTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        // ------------------------------------------------------------------ no type-to-search (decision 58, the user's choice)

        [AvaloniaTest]
        public void LettersOnTheCanvasKeepTheirShortcutsAndAddMachineOpensTheBrowser()
        {
            var (window, view) = OpenEmpty();
            try
            {
                Click(window, view.PointOf(0.5, 0.5, window));
                // With nothing selected the letters are shortcuts, not a search: K is the metronome, as everywhere.
                bool click = window.Transport.MetronomeOn;
                window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.K, RawInputModifiers.None);
                Settle();
                Assert.That(window.Transport.MetronomeOn, Is.EqualTo(!click), "K on the empty canvas is the metronome");
                Assert.That(view.Popup.IsOpen, Is.False);
                window.Transport.ToggleMetronome();
                window.KeyTextInput("rev");
                Settle();
                Assert.That(view.Popup.IsOpen, Is.False, "typing opens nothing");

                // The dedicated key, from another view too, and from the menu.
                window.ShowView("Sequence");
                Settle();
                var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
                window.KeyPressQwerty(PhysicalKey.N, command | RawInputModifiers.Shift);
                window.KeyReleaseQwerty(PhysicalKey.N, command | RawInputModifiers.Shift);
                Settle();
                Assert.That(window.Tabs.SelectedContent, Is.SameAs(view), "it shows the Machine View");
                Assert.That(view.Popup.IsOpen, Is.True, "and opens the browser there");
                Assert.That(view.Popup.Bounds.Width, Is.GreaterThan(100));
                view.Popup.Hide();
                Settle();

                foreach (bool mac in new[] { true, false })
                {
                    var item = AppMenu.Find(window.MenuModel(mac).ToList(), "View/Add Machine…");
                    Assert.That(item, Is.Not.Null, "in the View menu");
                    Assert.That(item.Gesture, Is.EqualTo(KeyText.Parse("Shift+Cmd+N", mac)), mac ? "on a Mac" : "elsewhere");
                }
                Assert.That(StudioKeymap.Get("view.addmachine").KeyLabel(false), Is.EqualTo("Ctrl+Shift+N"));
                Assert.That(StudioKeymap.Get("view.addmachine").KeyLabel(true), Is.EqualTo("⇧⌘N"));
                Assert.That(StudioKeymap.ViewKeys(false).Count(), Is.GreaterThan(0));
            }
            finally
            {
                Close(window);
            }
        }

        // ------------------------------------------------------------------ Sync is in the settings list

        [AvaloniaTest]
        public void SyncSettingsAreTheAgentsToo()
        {
            using var restore = Studio.AfterTest(() =>
            {
                PreferencesWindow.Current?.Close();
                var sync = Core.SessionSync;
                sync?.SetFollowMidiClock(false);
                sync?.SetLink(false);
                sync?.SetClockOut("Ghost Port", false);
                Settle();
            });
            var sync = Core.SessionSync;
            Assert.That(sync, Is.Not.Null);
            var all = UxP3fTests.AgentTool("settings_get", new JsonObject { ["section"] = "sync" })["settings"]!.AsArray();
            Assert.That(all.Select(s => s!["key"]!.GetValue<string>()), Is.EquivalentTo(new[] { "sync.Link", "sync.LinkStartStop", "sync.MidiClockIn", "sync.MidiClockOut" }));

            // The agent's tool changes it, and the history says Agent.
            var set = UxP3fTests.AgentTool("settings_set", new JsonObject { ["key"] = "sync.MidiClockIn", ["value"] = true });
            Assert.That(set["changed"]!.GetValue<bool>(), Is.True);
            Assert.That(sync.FollowMidiClock, Is.True);
            var entry = UxP3fTests.AgentTool("settings_history", new JsonObject { ["key"] = "sync.MidiClockIn", ["limit"] = 1 })["entries"]![0]!;
            Assert.That(entry["source"]!.GetValue<string>(), Is.EqualTo("Agent"));

            // The open window shows it, and its own change is logged as Manual, with the same key.
            var prefs = PreferencesWindow.Open(Studio.Host, "Sync");
            Dispatcher.UIThread.RunJobs(); Settle();
            var page = prefs.Page<SyncPage>();
            Assert.That(page.ClockInBox.IsChecked, Is.True);
            Assert.That(page.SettingControl("sync.MidiClockIn"), Is.SameAs(page.ClockInBox), "the control is the setting's, by its key");
            page.ClockInBox.IsChecked = false;
            Assert.That(sync.FollowMidiClock, Is.False);
            entry = UxP3fTests.AgentTool("settings_history", new JsonObject { ["key"] = "sync.MidiClockIn", ["limit"] = 1 })["entries"]![0]!;
            Assert.That((entry["source"]!.GetValue<string>(), entry["to"]!.GetValue<string>()), Is.EqualTo(("Manual", "False")));

            // The outputs for MIDI clock: a name list, by the tool and by the window.
            UxP3fTests.AgentTool("settings_set", new JsonObject { ["key"] = "sync.MidiClockOut", ["value"] = "Ghost Port" });
            Assert.That(sync.ClockOutNames, Is.EqualTo(new[] { "Ghost Port" }));
            page.Refresh();
            var ghost = page.ClockOutRows.Single(r => r.Name == "Ghost Port");
            Assert.That(ghost.Box.IsChecked, Is.True);
            ghost.Box.IsChecked = false;
            Assert.That(sync.ClockOutNames, Is.Empty);
            Assert.That(UxP3fTests.AgentTool("settings_history", new JsonObject { ["key"] = "sync.MidiClockOut", ["limit"] = 1 })["entries"]![0]!["source"]!.GetValue<string>(), Is.EqualTo("Manual"));

            // Restore Defaults on the page goes through the same reset: MIDI clock in back off.
            sync.SetFollowMidiClock(true);
            _ = prefs.RestoreDefaults();
            Settle();
            Assert.That(sync.FollowMidiClock, Is.False);

            // The one exception of decision 73 is gone: Sync is a page of the one model like the others.
            Assert.That(prefs.Pages.Select(p => p.Title), Does.Contain("Sync"));
            Assert.That(prefs.Page<SyncPage>().Sections, Is.EqualTo(new[] { "sync" }));
        }

        // ------------------------------------------------------------------ the top bar, the one transport

        [AvaloniaTest]
        public void TheTopBarHasMetronomeSignatureClockTapAndTempo()
        {
            var (window, _) = OpenEmpty();
            int bpm = Core.BPM;
            try
            {
                var bar = window.Transport;
                window.ShowView("Machines");
                Core.BPM = 120;
                window.RefreshNow();
                Assert.That(bar.MetronomeButton.IsVisible, Is.True);
                Assert.That(bar.SignatureBox.SelectedItem, Is.EqualTo("4/4"), "the time signature, beats over four");
                Assert.That(bar.Tempo.Value, Is.EqualTo(120));
                Assert.That(bar.PositionText, Is.EqualTo("1.1.1"));
                Assert.That(bar.MinutesText, Is.EqualTo("0:00"));

                // Minutes and seconds follow the position and the tempo: 240 beats at 120 BPM is two minutes.
                Core.SongCore.PlayPosition = Core.TPB * 240;
                window.RefreshNow();
                Assert.That(bar.MinutesText, Is.EqualTo("2:00"));
                Assert.That(bar.PositionText, Is.EqualTo("61.1.1"), "bar.beat.tick of the same place");
                Core.SongCore.PlayPosition = 0;

                // The time signature sets the session's beats per bar (the grid's and the clock's).
                bar.SignatureBox.SelectedIndex = 2;
                Assert.That(Core.SessionCore.BeatsPerBar, Is.EqualTo(3));
                Core.SongCore.PlayPosition = Core.TPB * 3;
                window.RefreshNow();
                Assert.That(bar.PositionText, Is.EqualTo("2.1.1"), "a bar is three beats now");
                Core.SongCore.PlayPosition = 0;
                bar.SignatureBox.SelectedIndex = 3;
                Assert.That(Core.SessionCore.BeatsPerBar, Is.EqualTo(4));

                // Tap: two taps just before it; the third, the button's, sets the tempo from the three (the time it takes depends on the machine).
                Core.BPM = 77;
                var layout = Core.SessionLayout;
                long now = System.Diagnostics.Stopwatch.GetTimestamp(), second = System.Diagnostics.Stopwatch.Frequency;
                layout.TapTempo(now - second * 3 / 10);
                layout.TapTempo(now - second * 15 / 100);
                Assert.That(Core.BPM, Is.EqualTo(77), "two taps are not a tempo");
                Click(window, bar.TapButton.TranslatePoint(new Point(10, 10), window)!.Value);
                Assert.That(Core.BPM, Is.Not.EqualTo(77).And.InRange(16, 500), "the third tap sets the tempo");
                Studio.Screenshot(window, "top-bar");
            }
            finally
            {
                Core.BPM = bpm;
                Close(window);
            }
        }

        [AvaloniaTest]
        public void TheTempoIsDraggedScrolledAndTyped()
        {
            var (window, _) = OpenEmpty();
            int bpm = Core.BPM;
            try
            {
                var field = window.Transport.Tempo;
                Core.BPM = 100;
                window.RefreshNow();
                var at = field.TranslatePoint(new Point(field.Bounds.Width / 2, field.Bounds.Height / 2), window)!.Value;

                // Up raises it: 3 pixels a BPM, and the whole drag is one undo step.
                int steps = Core.SongCore.ActionStack.Steps.Count;
                window.MouseMove(at);
                window.MouseDown(at, MouseButton.Left);
                window.MouseMove(at + new Vector(0, -3 * 7));
                Assert.That(Core.BPM, Is.EqualTo(107));
                window.MouseMove(at + new Vector(0, -3 * 12));
                window.MouseUp(at + new Vector(0, -3 * 12), MouseButton.Left);
                Assert.That(Core.BPM, Is.EqualTo(112));
                Assert.That(field.Box.IsFocused, Is.False, "a drag doesn't start typing");
                Assert.That(Core.SongCore.ActionStack.Steps.Count - steps, Is.EqualTo(1), "one undo step for the drag");
                Core.SongCore.ActionStack.Undo();
                Assert.That(Core.BPM, Is.EqualTo(100));
                window.RefreshNow();                      // the bar follows the song (ten times a second)
                Assert.That(field.Value, Is.EqualTo(100));

                // Down lowers it, Shift ten at a time.
                window.MouseMove(at);
                window.MouseDown(at, MouseButton.Left, RawInputModifiers.Shift);
                window.MouseMove(at + new Vector(0, 3 * 2), RawInputModifiers.Shift);
                window.MouseUp(at + new Vector(0, 3 * 2), MouseButton.Left, RawInputModifiers.Shift);
                Assert.That(Core.BPM, Is.EqualTo(80), "2 steps down at ten times");

                // The wheel.
                window.MouseWheel(at, new Vector(0, 1));
                Assert.That(Core.BPM, Is.EqualTo(81));

                // Never out of 16 to 500.
                window.MouseMove(at);
                window.MouseDown(at, MouseButton.Left);
                window.MouseMove(at + new Vector(0, 3 * 1000));
                window.MouseUp(at + new Vector(0, 3 * 1000), MouseButton.Left);
                Assert.That(Core.BPM, Is.EqualTo(TempoField.Min));

                // A double click types: the number selected, Enter keeps it, Esc puts it back.
                DoubleClick(window, at);
                Assert.That(field.Box.IsFocused, Is.True);
                field.Box.Text = "150";
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Settle();
                Assert.That(Core.BPM, Is.EqualTo(150));
                Assert.That(field.Box.IsFocused, Is.False);
                field.BeginEdit();
                field.Box.Text = "99";
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Settle();
                Assert.That(Core.BPM, Is.EqualTo(150), "Esc keeps the old tempo");
                Assert.That(field.Box.Text, Is.EqualTo("150"));
            }
            finally
            {
                Core.BPM = bpm;
                Close(window);
            }
        }

        [AvaloniaTest]
        public void TheSessionViewHasNoTransportOfItsOwn()
        {
            using var rig = new SessionRig("Drums");
            rig.Layout.Ensure();
            var view = rig.Show();
            var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            var all = string.Join("|", texts);
            Assert.That(all, Does.Not.Contain("BPM"), "the tempo is the top bar's");
            Assert.That(texts, Does.Not.Contain("Tap"));
            Assert.That(texts, Does.Not.Contain("♩"), "nor the metronome");
            Assert.That(all, Does.Not.Contain("Rec"));
            Assert.That(all, Does.Not.Contain("dropout"), "nor the audio's health: the badge is the window's");
            Assert.That(texts, Does.Contain("■  Stop all"), "its own stop for clips stays: it is not a transport");
            Assert.That(texts.Any(t => t.Contains("Overdub")), Is.True);
        }

        // ------------------------------------------------------------------ the master meter

        [AvaloniaTest]
        public void TheMasterMeterHasAScaleAndALatchingClipLight()
        {
            var (window, _) = OpenEmpty();
            try
            {
                var meter = window.Transport.Meter;
                Assert.That((MasterMeter.FloorDb, MasterMeter.TopDb), Is.EqualTo((-60.0, 0.0)), "the scale: -60 to 0 dB");
                Core.ClearMasterOver();
                meter.Refresh();
                Core.SafeMaster.TakePeaks();

                // A -20 dB signal in both channels: the bars stand at -20 dB.
                var buffer = new float[1024];
                Array.Fill(buffer, 0.1f);
                Core.SafeMaster.Process(buffer, 2, 44100);
                meter.Refresh();
                Assert.That(meter.Levels.Left, Is.EqualTo(-20).Within(1), "the left bar");
                Assert.That(meter.Levels.Right, Is.EqualTo(-20).Within(1), "the right bar");
                Assert.That(meter.OverText, Is.EqualTo(""), "no clip light");

                // They fall back at about 60 dB a second.
                System.Threading.Thread.Sleep(300);
                meter.Refresh();
                Assert.That(meter.Levels.Left, Is.LessThan(-30));

                // Over 0 dB: the clip light latches, and stays lit when the signal is gone, until it is clicked.
                Array.Fill(buffer, 2f);
                Core.SafeMaster.Process(buffer, 2, 44100);
                meter.Refresh();
                Assert.That(meter.OverText, Does.StartWith("+").And.EndWith("dB"));
                Assert.That(meter.Levels.Left, Is.EqualTo(0).Within(0.01), "the bar stops at 0 dB");
                Array.Fill(buffer, 0f);
                Core.SafeMaster.Process(buffer, 2, 44100);
                meter.Refresh();
                Assert.That(meter.OverText, Does.StartWith("+"), "latched");
                Click(window, meter.ClipButton.TranslatePoint(new Point(10, 8), window)!.Value);
                Assert.That(meter.OverText, Is.EqualTo(""), "a click clears it");
            }
            finally
            {
                Core.ClearMasterOver();
                Close(window);
            }
        }

        // ------------------------------------------------------------------ the audio-health badge

        [AvaloniaTest]
        public void TheWindowsAudioBadgeOpensThePreferencesAudioPage()
        {
            var (window, _) = OpenEmpty();
            try
            {
                window.Health.Refresh();
                var centre = window.Health.TranslatePoint(new Point(window.Health.Bounds.Width / 2, window.Health.Bounds.Height / 2), window)!.Value;
                Click(window, centre);
                var prefs = PreferencesWindow.Current;
                Assert.That(prefs, Is.Not.Null, "a click opens Preferences");
                Assert.That(prefs.Selected.Title, Is.EqualTo("Audio"));
            }
            finally
            {
                PreferencesWindow.Current?.Close();
                Close(window);
            }
        }

        // ------------------------------------------------------------------ meters on the boxes and wires

        [AvaloniaTest]
        public void BoxesAndWiresBrightenWithTheLevelThroughThem()
        {
            using var rig = new SessionRig("Drums", "Lead");
            var drums = rig.Machine("Drums");
            var lead = rig.Machine("Lead");
            rig.Pattern(drums, "Beat", SessionRig.Bar, (0, 48), (4, 50), (8, 48), (12, 50));
            rig.Pattern(lead, "Hook", SessionRig.Bar, (0, 72), (2, 76), (4, 79), (8, 84));
            rig.Layout.Ensure();
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            try
            {
                window.ShowView("Machines");
                Settle();
                var view = window.Tabs.Items.Cast<TabItem>().Select(t => t.Content).OfType<MachineView>().First();
                var canvas = view.Canvas;
                var wire = canvas.Wires.First(c => c.Source == drums);
                canvas.TickMeters(1);
                Assert.That(canvas.WireLevel(wire), Is.EqualTo(0), "a silent song looks as it always did");
                Assert.That(canvas.BoxLevel(drums), Is.EqualTo(0));

                // The engine's own peaks, while a scene plays.
                rig.Layout.LaunchScene(0);
                for (int round = 0; round < 40 && canvas.WireLevel(wire) <= 0; round++)      // a slower runner needs more ticks before the first peak
                {
                    rig.RunTicks(6);
                    canvas.TickMeters(1.0 / MachineCanvas.MeterRate);
                }
                Assert.That(canvas.WireLevel(wire), Is.GreaterThan(0), "the wire Drums → Master carries a level");
                Assert.That(canvas.BoxLevel(drums), Is.GreaterThan(0), "the box shows the loudest wire out of it");
                Assert.That(canvas.BoxLevel(Core.SongCore.Machines.First(m => m.DLL.Info.Type == MachineType.Master)), Is.GreaterThan(0), "the Master, what comes into it");
                Studio.Screenshot(window, "meters-playing");

                // The scale: 0 dB is a full level, -60 and below none; a level falls by itself.
                Assert.That(MachineCanvas.LevelOf(32768f), Is.EqualTo(1).Within(1e-9));
                Assert.That(MachineCanvas.LevelOf(32768f / 1000), Is.EqualTo(0).Within(1e-6), "-60 dB");
                Assert.That(MachineCanvas.LevelOf(32768f / 10), Is.EqualTo(40.0 / 60).Within(1e-6), "-20 dB");
                canvas.PeakSource = c => (32768f / 10, 0);
                canvas.TickMeters(1);
                double lit = canvas.WireLevel(wire);
                Assert.That(lit, Is.EqualTo(40.0 / 60).Within(1e-6));
                canvas.PeakSource = c => (0, 0);
                canvas.TickMeters(0.25);
                Assert.That(canvas.WireLevel(wire), Is.EqualTo(lit - 0.25).Within(1e-6), "it falls a quarter of the scale in a quarter second");
                canvas.TickMeters(5);
                Assert.That(canvas.WireLevel(wire), Is.EqualTo(0));

                // It is drawn: the wire's pixels are lighter with a level on it than without.
                double lastLuminance = 0, lastBoxLuminance = 0;
                static double Linear(int v) { double c = v / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
                int Brightness()
                {
                    foreach (var v in window.GetVisualDescendants()) v.InvalidateVisual();
                    Settle();
                    var shape = canvas.WireOf(wire);
                    var spot = canvas.TranslatePoint(new Point((shape.Start.X * 13 + shape.End.X * 7) / 20 * canvas.Zoom - canvas.Offset.X, (shape.Start.Y * 13 + shape.End.Y * 7) / 20 * canvas.Zoom - canvas.Offset.Y), window)!.Value;
                    var bitmap = window.CaptureRenderedFrame();
                    Assert.That(bitmap, Is.Not.Null);
                    using var fb = bitmap!.Lock();
                    bool rgba = fb.Format == Avalonia.Platform.PixelFormat.Rgba8888;
                    int best = 0;
                    double bestLum = 0;
                    for (int y = (int)spot.Y - 2; y <= (int)spot.Y + 2; y++)
                        for (int x = (int)spot.X - 2; x <= (int)spot.X + 2; x++)
                        {
                            int p = System.Runtime.InteropServices.Marshal.ReadInt32(fb.Address + y * fb.RowBytes + x * 4);
                            int r = rgba ? p & 0xFF : (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, bl = rgba ? (p >> 16) & 0xFF : p & 0xFF;
                            best = Math.Max(best, r + g + bl);
                            bestLum = Math.Max(bestLum, 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(bl));
                        }
                    lastLuminance = bestLum;
                    // The box: its body's mean luminance, a block in its top right (clear of the name, the mode text and the strip).
                    var boxRect = canvas.BoxOf(drums);
                    var corner = canvas.TranslatePoint(new Point(boxRect.Right * canvas.Zoom - canvas.Offset.X - 14, boxRect.Y * canvas.Zoom - canvas.Offset.Y + 8), window)!.Value;
                    double sum = 0; int count = 0;
                    for (int y = (int)corner.Y; y < (int)corner.Y + 8; y++)
                        for (int x = (int)corner.X; x < (int)corner.X + 8; x++)
                        {
                            int p = System.Runtime.InteropServices.Marshal.ReadInt32(fb.Address + y * fb.RowBytes + x * 4);
                            int r = rgba ? p & 0xFF : (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, bl = rgba ? (p >> 16) & 0xFF : p & 0xFF;
                            sum += 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(bl);
                            count++;
                        }
                    lastBoxLuminance = sum / count;
                    return best;
                }
                canvas.PeakSource = c => (32768f * 0.7f, 32768f * 0.7f);
                canvas.TickMeters(1);
                int bright = Brightness();
                canvas.PeakSource = c => (0, 0);
                canvas.TickMeters(5);
                int dark = Brightness();
                Assert.That(bright, Is.GreaterThan(dark), "a wire with a level is lighter than a silent one");

                // On a dB curve: -3, -20 and -40 dB look clearly different (each step several shades), not all "lit".
                int At(double db)
                {
                    float peak = (float)(32768 * Math.Pow(10, db / 20));
                    canvas.PeakSource = c => (peak, peak);
                    canvas.TickMeters(1);
                    return Brightness();
                }
                int loud = At(-3);
                double loudLum = lastLuminance, loudBox = lastBoxLuminance;
                int middle = At(-20);
                double middleLum = lastLuminance;
                int quiet = At(-40);
                canvas.PeakSource = c => (0, 0);
                canvas.TickMeters(5);
                Brightness();
                double silentLum = lastLuminance, silentBox = lastBoxLuminance;
                TestContext.Out.WriteLine($"[ux-measure] relative luminance: wire silent {silentLum:0.000}, -3 dB {loudLum:0.000} (x{loudLum / silentLum:0.00}), -20 dB {middleLum:0.000} (x{middleLum / silentLum:0.00}); box body silent {silentBox:0.0000}, -3 dB {loudBox:0.0000} (x{loudBox / silentBox:0.00})");
                Assert.That(loudLum / silentLum, Is.GreaterThanOrEqualTo(1.8), "P3j: a loud wire is about twice as bright as a silent one (relative luminance)");
                Assert.That(loudBox / silentBox, Is.GreaterThanOrEqualTo(1.8), "and a loud box");
                Assert.That(middleLum / silentLum, Is.GreaterThanOrEqualTo(1.3), "a wire at -20 dB is clearly lit too");
                TestContext.Out.WriteLine($"[ux-measure] wire brightness (R+G+B): silent {dark}, -40 dB {quiet}, -20 dB {middle}, -3 dB {loud}");
                Assert.That(loud, Is.GreaterThan(middle + 30), "-3 dB against -20 dB");
                Assert.That(middle, Is.GreaterThan(quiet + 8), "-20 dB against -40 dB");
                Assert.That(quiet, Is.GreaterThan(dark), "-40 dB against silence");
                Assert.That(MachineCanvas.Glow(MachineCanvas.LevelOf(32768f * 0.03162f)) / MachineCanvas.Glow(1), Is.EqualTo(0.25).Within(0.01), "-30 dB glows a quarter of 0 dB");
            }
            finally
            {
                window.Close();
                Dispatcher_RunJobs();
            }
        }

        static void Dispatcher_RunJobs() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}
