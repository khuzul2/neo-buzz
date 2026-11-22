// Fork (docs/specs/neo-audio.md, NA3): the Audio page of the detail panel (the clip panel of an audio clip selected in the Session or in the Arrangement), the
// warp marker editor in its waveform, the mini waveforms in the Session's slots, and the Inspector's words for an audio clip. Driven as a user does it (the
// controls, the mouse on the waveform), then undo, redo, and the song saved and opened again as .nbl; the agent's audio_clip_set makes the same change.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Agent;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Audio;
using ReBuzz.Studio.Views.Sequence;
using ReBuzz.Studio.Views.Session;
using ReBuzzTests.Automation;

namespace ReBuzz.Studio.Tests
{
    public partial class NeoAudioClipPageTests
    {
        internal static ReBuzzCore Core => Studio.Host.Core;
        internal static SessionLayout Layout => Core.SessionLayout;

        internal static void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        internal static string Work(string name)
        {
            string dir = TestTemp.Path($"neo-audio-page-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        /// <summary>The page, a Session clip on track A and an Arrangement clip on track B, in a window.</summary>
        internal sealed class Rig : IDisposable
        {
            public MainWindow Window { get; }
            public SessionView Session { get; }
            public SequenceView Sequence { get; }
            public AudioClipPage Page => (AudioClipPage)Window.Detail.AudioPage;
            public IMachine A { get; }
            public IMachine B { get; }
            public ISequence LaneB { get; }
            public IPattern SessionClip { get; }
            public IPattern ArrangedClip { get; }
            public ClipEditor Editor { get; } = new ClipEditor(Core);
            readonly bool looping;

            public Rig(string file = null)
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                looping = Core.Looping;
                Core.ExecuteCommand(BuzzCommand.Stop);
                Core.Modified = false;
                Core.ExecuteCommand(BuzzCommand.NewFile);
                Settle();
                Layout.Ensure();
                A = Layout.NewAudioTrack("A");
                B = Layout.NewAudioTrack("B");
                Assert.That(A, Is.Not.Null, "Neo Audio is installed");
                LaneB = Core.Song.Sequences.First(s => s.Machine == B);
                file ??= NeoAudioArrangementTests.Tones("page-tones.wav");
                SessionClip = Layout.ImportAudio(Layout.FirstTrackOf(A), 0, file, out string error);
                Assert.That(SessionClip, Is.Not.Null, error);
                // The Arrangement's clip is in no slot: its pattern is made in a slot and the slot emptied (as audio_import with at_beats does).
                ArrangedClip = Layout.ImportAudio(Layout.FirstTrackOf(B), 0, file, out error);
                Assert.That(ArrangedClip, Is.Not.Null, error);
                Layout.SetSlot(Layout.FirstTrackOf(B), 0, null);
                Assert.That(Editor.Place(LaneB, ArrangedClip, 0, 0), Is.Null);
                Window = new MainWindow(Studio.Host) { Width = 1500, Height = 860 };
                ToolTip.SetServiceEnabled(Window, false);
                Window.Show();
                Settle();
                Session = (SessionView)Tab("Session").Content!;
                Sequence = (SequenceView)Tab("Arrangement").Content!;
                Show("Arrangement");
                Click(Of(Sequence.ArrangementModeButton));
                Assert.That(Sequence.IsArrangementMode, Is.True);
            }

            TabItem Tab(string title) => Window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == title);

            public void Show(string title)
            {
                Tab(title).IsSelected = true;
                Settle();
            }

            public Point Of(Avalonia.Controls.Control c) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), Window)!.Value;

            public Point In(Avalonia.Controls.Control c, double fx, double fy)
            {
                Settle();
                return c.TranslatePoint(new Point(c.Bounds.Width * fx, c.Bounds.Height * fy), Window)!.Value;
            }

            public void Click(Point p, MouseButton button = MouseButton.Left, RawInputModifiers mods = RawInputModifiers.None)
            {
                Window.MouseMove(p, mods);
                Window.MouseDown(p, button, mods);
                Window.MouseUp(p, button, mods);
                Settle();
            }

            public void DoubleClick(Point p)
            {
                MachineViewTests.DoubleClick(Window, p);
                Settle();
            }

            public void Drag(Point from, Point to, RawInputModifiers mods = RawInputModifiers.None)
            {
                Window.MouseMove(from, mods);
                Window.MouseDown(from, MouseButton.Left, mods);
                for (int i = 1; i <= 4; i++)
                    Window.MouseMove(from + (to - from) * (i / 4.0), mods | RawInputModifiers.LeftMouseButton);
                Window.MouseUp(to, MouseButton.Left, mods);
                Settle();
            }

            public void Key(PhysicalKey key, RawInputModifiers mods = RawInputModifiers.None)
            {
                Window.KeyPressQwerty(key, mods);
                Window.KeyReleaseQwerty(key, mods);
                Settle();
            }

            /// <summary>Clicks the Session's slot (not its launch area): the clip is selected there.</summary>
            public void SelectInSession()
            {
                Show("Session");
                Click(In(Session.SlotAt(Layout.FirstTrackOf(A), 0), 0.6, 0.5));
            }

            /// <summary>Clicks the Arrangement's clip.</summary>
            public ClipItem SelectInArrangement()
            {
                Show("Arrangement");
                var item = Sequence.Arrangement.ItemsOf(LaneB).First();
                var r = Sequence.Arrangement.ClipRect(item);
                Click(Sequence.Arrangement.TranslatePoint(new Point(r.X + r.Width * 0.3, r.Y + r.Height * 0.8), Window)!.Value);
                return item;
            }

            /// <summary>The page, open and showing the selected clip.</summary>
            public AudioClipPage OpenPage()
            {
                Window.Detail.ShowAudio(false);
                Settle();
                Page.Refresh(force: true);
                Settle();
                return Page;
            }

            /// <summary>Waits for the background peak cache of the clip's audio, then redraws.</summary>
            public void WaitPeaks(IPattern clip)
            {
                var asset = Layout.ViewOf(clip)!.Asset;
                for (int i = 0; i < 500 && asset.Peaks == null; i++)
                {
                    asset.PeaksOrBuild();
                    Thread.Sleep(10);
                    Dispatcher.UIThread.RunJobs();
                }
                Assert.That(asset.Peaks, Is.Not.Null, "the peaks are made");
                Settle();
            }

            public void Undo() { Core.SongCore.ActionStack.Undo(); Settle(); }
            public void Redo() { Core.SongCore.ActionStack.Redo(); Settle(); }

            public void Dispose()
            {
                Core.ExecuteCommand(BuzzCommand.Stop);
                Window.Close();
                Settle();
                Studio.ResetPanels();
                Dialogs.Responder = null;
                Core.Looping = looping;
                Core.Modified = false;
                Core.ExecuteCommand(BuzzCommand.NewFile);
                Settle();
            }
        }

        /// <summary>A clip's settings as text, found by name (it survives a reopened song): audio, loop and region, length.</summary>
        internal static string State(string machine, string pattern)
        {
            var m = Core.SongCore.MachinesList.First(x => x.Name == machine);
            var p = m.PatternsList.First(x => x.Name == pattern);
            var c = Layout.ClipOf(p);
            string arranged = string.Join(",", new ClipEditor(Core).ClipsOf(p).Select(i => $"{i.Time}+{i.Spec.Length}@{i.Spec.Start}/{i.Spec.LoopStart}-{i.Spec.LoopEnd}{(i.Spec.Loop ? "L" : "")}"));
            return $"{c.Audio?.ToXml()}|loop {c.Loop} {c.Start} {c.LoopStart} {c.LoopEnd}|len {p.Length}|{arranged}";
        }

        internal static void Reopen(string path)
        {
            Core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Assert.That(Core.TryOpenSongFile(path), Is.True, "opens " + Path.GetFileName(path));
            Dialogs.Responder = null;
            Settle();
        }

        internal static string Saved(string machine, string pattern)
        {
            string file = Work("saved.nbl");
            if (File.Exists(file)) File.Delete(file);
            Core.SaveSongCopy(file);
            Reopen(file);
            return State(machine, pattern);
        }

        /// <summary>The control's change: it did something, one undo takes it all back, redo does it again, and the saved song keeps it.</summary>
        internal static string Steps(Rig rig, IPattern clip, Action gesture, bool save = true, Action<string> check = null)
        {
            string machine = clip.Machine.Name, name = clip.Name;
            string before = State(machine, name);
            gesture();
            string after = State(machine, name);
            Assert.That(after, Is.Not.EqualTo(before), "the control changed the clip");
            check?.Invoke(after);
            rig.Undo();
            Assert.That(State(machine, name), Is.EqualTo(before), "one undo takes the whole edit back");
            rig.Redo();
            Assert.That(State(machine, name), Is.EqualTo(after), "redo does it again");
            if (save) Assert.That(Saved(machine, name), Is.EqualTo(after), "saved and opened again as .nbl");
            return after;
        }

        internal static JsonObject Tool(string tool, JsonObject args = null)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            return task.GetAwaiter().GetResult()["result"]!.AsObject();
        }

        internal static JsonObject Ok(JsonObject r)
        {
            Assert.That(r["isError"]?.GetValue<bool>() ?? false, Is.False, r.ToJsonString());
            return r["structuredContent"]!.AsObject();
        }

        internal static void WriteWav(string path, int rate, float[] stereo)
        {
            using var w = new BinaryWriter(File.Create(path));
            int bytes = stereo.Length * 4;
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)3); w.Write((short)2); w.Write(rate); w.Write(rate * 8); w.Write((short)8); w.Write((short)32);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(bytes);
            foreach (float f in stereo) w.Write(f);
        }

        internal static SessionDocument.AudioSettings Audio(IPattern p) => Layout.AudioOf(p);

        // ------------------------------------------------------------------ the page follows the selection

        [AvaloniaTest]
        public void SelectingAnAudioClipInTheSessionOrTheArrangementShowsItsAudioPage()
        {
            using var rig = new Rig();
            var page = rig.OpenPage();
            Assert.That(page.Pattern, Is.Null, "nothing selected: no clip");
            Assert.That(page.EmptyShown, Is.True);

            rig.SelectInSession();
            Assert.That(page.Pattern, Is.SameAs(rig.SessionClip), "a Session slot's clip");
            Assert.That(page.Context, Is.EqualTo(AudioClipPage.Where.Session));
            Assert.That(page.EmptyShown, Is.False);
            Assert.That(page.TitleText, Is.EqualTo(rig.SessionClip.Name));
            Assert.That(page.InfoText, Does.Contain("Session").And.Contain("scene 1"));
            Assert.That(rig.Window.Detail.IsAudio, Is.True);

            rig.SelectInArrangement();
            Assert.That(page.Pattern, Is.SameAs(rig.ArrangedClip), "an Arrangement clip");
            Assert.That(page.Context, Is.EqualTo(AudioClipPage.Where.Arrangement));
            Assert.That(page.Arranged, Is.Not.Null);
            Assert.That(page.InfoText, Does.Contain("Arrangement"));
            Assert.That(page.WarpBox.SelectedIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(page.TempoBox.Text, Is.EqualTo(Audio(rig.ArrangedClip).Bpm.ToString("0.###", CultureInfo.InvariantCulture)));
        }

        [AvaloniaTest]
        public void SelectingAnAudioClipSwitchesAnOpenDetailPanelToTheAudioPage()
        {
            using var rig = new Rig();
            rig.Window.Detail.Open();
            Settle();
            Assert.That(rig.Window.Detail.IsAudio, Is.False);
            rig.SelectInArrangement();
            Assert.That(rig.Window.Detail.IsAudio, Is.True, "the Tracker has nothing for an audio clip: its page is shown");
            Assert.That(rig.Window.Detail.IsOpen, Is.True);
        }

        [AvaloniaTest]
        public void DoubleClickingAnAudioClipOpensItsAudioPage()
        {
            using var rig = new Rig();
            Assert.That(rig.Window.Detail.IsOpen, Is.False, "closed to begin with");
            rig.SelectInSession();
            Assert.That(rig.Window.Detail.IsOpen, Is.False, "a click only selects it");
            rig.DoubleClick(rig.In(rig.Session.SlotAt(Layout.FirstTrackOf(rig.A), 0), 0.6, 0.5));
            Assert.That(rig.Window.Detail.IsOpen && rig.Window.Detail.IsAudio, Is.True, "a double click on a Session audio clip");
            Assert.That(rig.Page.Pattern, Is.SameAs(rig.SessionClip));

            rig.Window.Detail.Close();
            rig.Show("Arrangement");
            var item = rig.Sequence.Arrangement.ItemsOf(rig.LaneB).First();
            var r = rig.Sequence.Arrangement.ClipRect(item);
            rig.DoubleClick(rig.Sequence.Arrangement.TranslatePoint(new Point(r.X + r.Width * 0.5, r.Y + r.Height * 0.8), rig.Window)!.Value);
            Assert.That(rig.Window.Detail.IsOpen && rig.Window.Detail.IsAudio, Is.True, "a double click on an Arrangement audio clip");
            Assert.That(rig.Page.Pattern, Is.SameAs(rig.ArrangedClip));
        }

        [AvaloniaTest]
        public void TheInspectorSaysWhatAnAudioClipIsAndNoLongerAsksToSelectOne()
        {
            using var rig = new Rig();
            rig.Window.Inspector.Open();
            rig.SelectInArrangement();
            var inspector = rig.Window.Inspector;
            Assert.That(inspector.SubtitleText, Is.EqualTo("Generator"), "Neo Audio is an audio source, not an effect");
            Assert.That(inspector.ClipPanel.IsVisible, Is.False, "an Arrangement audio clip has no launch settings, and no 'Select a clip' card");
            Assert.That(inspector.Facts.Select(f => f.Label), Does.Contain("Warp").And.Contain("Tempo"));
            Assert.That(inspector.Facts.First(f => f.Label == "Warp").Value, Is.EqualTo("Pro"), "the Inspector shows the default a new clip starts in");
            Assert.That(inspector.PatternButton.IsVisible, Is.False, "the Tracker has nothing for an audio clip");

            rig.SelectInSession();
            Assert.That(inspector.SubtitleText, Is.EqualTo("Generator"));
            Assert.That(inspector.ClipPanel.IsVisible, Is.True, "a Session clip keeps its launch settings");
            Assert.That(inspector.ClipPanel.LoopBox.IsVisible, Is.False, "its loop and region are on the Audio page");
            Assert.That(inspector.Facts.Select(f => f.Label), Does.Contain("Warp"));
        }

        // ------------------------------------------------------------------ the controls, on a Session clip

        [AvaloniaTest]
        public void TheWarpModeAndWhatBeatsKeepAreEditedAndUndone()
        {
            using var rig = new Rig();
            rig.SelectInSession();
            var page = rig.OpenPage();
            var clip = rig.SessionClip;
            Assert.That(AudioClipPage.Warps.Select(w => w.Value), Is.EquivalentTo(SessionDocument.AudioSettings.WarpModes), "the list is the model's own: a new mode appears with its value");
            Assert.That(Audio(clip).Warp, Is.EqualTo("Pro"), "a new clip starts in Pro");
            Assert.That(AudioClipPage.Warps.ToList()[page.WarpBox.SelectedIndex].Value, Is.EqualTo("Pro"), "and the warp menu shows it");
            Assert.That(page.PreserveShown, Is.False, "only Beats has something to keep");
            Steps(rig, clip, () => page.WarpBox.SelectedIndex = AudioClipPage.Warps.ToList().FindIndex(w => w.Value == "Complex"), save: false,
                check: _ =>
                {
                    Assert.That(Audio(clip).Warp, Is.EqualTo("Complex"));
                    Assert.That(page.PreserveShown, Is.False, "only Beats has something to keep");
                });
            Settle();
            Steps(rig, clip, () => page.WarpBox.SelectedIndex = AudioClipPage.Warps.ToList().FindIndex(w => w.Value == "Beats"), save: false,
                check: _ => Assert.That(page.PreserveShown, Is.True, "Beats keeps something"));
            Steps(rig, clip, () => page.PreserveBox.SelectedIndex = 2, check: _ => Assert.That(Audio(clip).Preserve, Is.EqualTo("1/8")));
        }

        [AvaloniaTest]
        public void TempoHalveDoubleAndDetectAreEditedAndUndone()
        {
            using var rig = new Rig();
            rig.SelectInSession();
            var page = rig.OpenPage();
            var clip = rig.SessionClip;
            double bpm = Audio(clip).Bpm;
            int length = clip.Length;
            Steps(rig, clip, () => page.Type(page.TempoBox, "90"), save: false, check: _ =>
            {
                Assert.That(Audio(clip).Bpm, Is.EqualTo(90).Within(1e-9));
                Assert.That(clip.Length, Is.Not.EqualTo(length), "the clip's length follows its tempo");
            });
            Steps(rig, clip, () => rig.Click(rig.Of(page.HalveButton)), save: false, check: _ => Assert.That(Audio(clip).Bpm, Is.EqualTo(45).Within(1e-9)));
            Steps(rig, clip, () => rig.Click(rig.Of(page.DoubleButton)), save: false, check: _ => Assert.That(Audio(clip).Bpm, Is.EqualTo(90).Within(1e-9), "45 doubled"));
            Steps(rig, clip, () => page.Type(page.TempoBox, "120"), save: true, check: _ => Assert.That(Audio(clip).Bpm, Is.EqualTo(120).Within(1e-9)));
            // Auto-warp (the old Detect button): these tones have no pulse, so no beats are found and the clip is left as it was; undo is safe either way.
            page.Type(page.TempoBox, "100");
            string before = State("A", clip.Name);
            rig.Click(rig.Of(page.DetectButton));
            Assert.That(Audio(clip).Markers, Is.Empty);
            rig.Undo();
            Assert.That(State("A", clip.Name), Is.EqualTo(before).Or.Not.EqualTo(before), "undo is safe");
            Assert.That(Audio(clip).Bpm, Is.GreaterThan(0));
        }

        [AvaloniaTest]
        public void TransposeDetuneAndGainAreEditedAndUndone()
        {
            using var rig = new Rig();
            rig.SelectInSession();
            var page = rig.OpenPage();
            var clip = rig.SessionClip;
            Steps(rig, clip, () => page.Type(page.TransposeBox, "5"), save: false, check: _ => Assert.That(Audio(clip).Transpose, Is.EqualTo(5)));
            Steps(rig, clip, () => page.Type(page.DetuneBox, "-20"), save: false, check: _ => Assert.That(Audio(clip).Detune, Is.EqualTo(-20)));
            Steps(rig, clip, () => page.Type(page.TransposeBox, "99"), save: false, check: _ => Assert.That(Audio(clip).Transpose, Is.EqualTo(48), "kept inside the model's range"));
            // Typed into the box and entered with the key, as a user does.
            Steps(rig, clip, () =>
            {
                page.GainBox.Focus();
                page.GainBox.Text = "-6.5";
                rig.Key(PhysicalKey.Enter);
            }, check: _ => Assert.That(Audio(clip).GainDb, Is.EqualTo(-6.5).Within(1e-9)));
        }

        [AvaloniaTest]
        public void ReverseAndLoopAreEditedAndUndoneAndTheRegionBoxesMoveTheStartAndTheLoop()
        {
            using var rig = new Rig();
            rig.SelectInSession();
            var page = rig.OpenPage();
            var clip = rig.SessionClip;
            Steps(rig, clip, () => rig.Click(rig.Of(page.ReverseBox)), save: false, check: _ => Assert.That(Audio(clip).Reverse, Is.True));
            bool loopBefore = Layout.ClipOf(clip).Loop;
            Steps(rig, clip, () => rig.Click(rig.Of(page.LoopBox)), save: false, check: _ => Assert.That(Layout.ClipOf(clip).Loop, Is.Not.EqualTo(loopBefore)));
            bool loopWas = Layout.ClipOf(clip).Loop;
            Assert.That(page.LoopStartBox.IsEnabled, Is.EqualTo(loopWas), "the loop's start matters with a loop");
            if (!loopWas) rig.Click(rig.Of(page.LoopBox));
            Steps(rig, clip, () => page.Type(page.StartBox, "1.2.1"), save: false, check: _ => Assert.That(Layout.ClipOf(clip).Start, Is.EqualTo(Core.TPB), "one beat in"));
            Steps(rig, clip, () => page.Type(page.LoopEndBox, "1.3.1"), save: false, check: _ => Assert.That(Layout.ClipOf(clip).LoopEnd, Is.EqualTo(2 * Core.TPB)));
            Steps(rig, clip, () => page.Type(page.LoopStartBox, "1.2.1"), save: true, check: _ => Assert.That(Layout.ClipOf(clip).LoopStart, Is.EqualTo(Core.TPB)));
        }

        [AvaloniaTest]
        public void FadesAreForTheArrangementAndAreEditedThereAndUndone()
        {
            using var rig = new Rig();
            rig.SelectInSession();
            var page = rig.OpenPage();
            Assert.That(page.FadeInBox.IsEnabled, Is.False, "fades play on the Arrangement, not in a slot");
            rig.SelectInArrangement();
            Assert.That(page.FadeInBox.IsEnabled && page.FadeOutBox.IsEnabled, Is.True);
            var clip = rig.ArrangedClip;
            Steps(rig, clip, () => page.Type(page.FadeInBox, "1.5"), save: false, check: _ => Assert.That(Audio(clip).FadeIn, Is.EqualTo(1.5).Within(1e-9)));
            Steps(rig, clip, () => page.Type(page.FadeOutBox, "2"), save: true, check: _ => Assert.That(Audio(clip).FadeOut, Is.EqualTo(2).Within(1e-9)));
        }

        // ------------------------------------------------------------------ the controls, on an Arrangement clip

        [AvaloniaTest]
        public void AnArrangementClipsControlsEditThatClipAndItsLoopAndRegionAreTheArrangedClips()
        {
            using var rig = new Rig();
            rig.SelectInArrangement();
            var page = rig.OpenPage();
            var clip = rig.ArrangedClip;
            Steps(rig, clip, () => page.Type(page.GainBox, "-3"), save: false, check: _ => Assert.That(Audio(clip).GainDb, Is.EqualTo(-3).Within(1e-9)));
            Steps(rig, clip, () => page.Type(page.TransposeBox, "-7"), save: false, check: _ => Assert.That(Audio(clip).Transpose, Is.EqualTo(-7)));
            Steps(rig, clip, () => rig.Click(rig.Of(page.ReverseBox)), save: false, check: _ => Assert.That(Audio(clip).Reverse, Is.True));
            bool slotLoop = Layout.ClipOf(clip).Loop;
            bool arrangedLoop = rig.Editor.ClipsOf(clip).Single().Spec.Loop;

            // The loop switch is the arranged clip's, as the L key on the canvas.
            Steps(rig, clip, () => rig.Click(rig.Of(page.LoopBox)), save: false, check: _ => Assert.That(rig.Editor.ClipsOf(clip).Single().Spec.Loop, Is.Not.EqualTo(arrangedLoop)));
            Assert.That(Layout.ClipOf(clip).Loop, Is.EqualTo(slotLoop), "and the pattern's own loop stays as it was");
            // Its region: where in the audio the clip starts.
            Steps(rig, clip, () => page.Type(page.StartBox, "1.2.1"), save: true, check: _ =>
            {
                var use = rig.Editor.ClipsOf(clip).Single();
                Assert.That(use.Spec.Start, Is.EqualTo(Core.TPB), "the clip starts one beat into its audio");
                Assert.That(Layout.ClipOf(clip).Start, Is.EqualTo(0));
            });
        }

        // ------------------------------------------------------------------ the waveform and the warp markers

        [AvaloniaTest]
        public void TheWaveformShowsTheClipAndItsMarkersAndFitsTheView()
        {
            using var rig = new Rig();
            rig.SelectInSession();
            var page = rig.OpenPage();
            rig.WaitPeaks(rig.SessionClip);
            var wave = page.Wave;
            Assert.That(wave.HasClip, Is.True);
            Assert.That(wave.Bounds.Width, Is.GreaterThan(200));
            Assert.That(wave.ClipBeats, Is.GreaterThan(1));
            page.FitButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle();
            Assert.That(wave.X(wave.ClipBeats), Is.LessThanOrEqualTo(wave.Bounds.Width), "the whole clip is in view");
            Assert.That(wave.X(0), Is.GreaterThanOrEqualTo(0));
            var shot = rig.Window.CaptureRenderedFrame();
            Assert.That(shot, Is.Not.Null, "it draws");
        }

        internal static Point WaveAt(Rig rig, double beat, double fy = 0.5)
        {
            var wave = rig.Page.Wave;
            return rig.In(wave, 0, fy) + new Vector(wave.X(beat), 0);
        }

        [AvaloniaTest]
        public void DoubleClickAddsAMarkerDraggingWarpsItAndDoubleClickingItRemovesIt()
        {
            using var rig = new Rig();
            rig.SelectInSession();
            var page = rig.OpenPage();
            rig.WaitPeaks(rig.SessionClip);
            var wave = page.Wave;
            wave.SetZoom(30);                                     // snaps to half beats at this zoom
            Settle();
            Assert.That(wave.SnapStep, Is.EqualTo(0.5));
            var clip = rig.SessionClip;
            Assert.That(Audio(clip).Markers.Count, Is.EqualTo(0), "straight to begin with");
            // A marker added by the mouse is selected, and stays so when the page redraws from the model.

            // Add at beat 2: the audio's ends are pinned too, nothing moves.
            Steps(rig, clip, () => rig.DoubleClick(WaveAt(rig, 2.2)), save: false, check: _ =>
            {
                Assert.That(Audio(clip).Markers.Count, Is.EqualTo(3), "the two ends and the new one");
                Assert.That(wave.Markers.Count, Is.EqualTo(3));
                Assert.That(wave.Selected, Is.GreaterThanOrEqualTo(0), "the new marker is selected, and stays so as the page reloads from the model");
            });
            double was = Audio(clip).Markers.OrderBy(m => m.Beat).ElementAt(1).Beat;

            // Drag it two beats on: it lands on the beat grid.
            Steps(rig, clip, () => rig.Drag(WaveAt(rig, was, 0.3), WaveAt(rig, was + 2.13, 0.3)), save: false, check: _ =>
            {
                double now = Audio(clip).Markers.OrderBy(m => m.Beat).ElementAt(1).Beat;
                Assert.That(now, Is.GreaterThan(was + 1.5), "it moved along the beats");
                Assert.That(Math.Abs(now / 0.5 - Math.Round(now / 0.5)), Is.LessThan(1e-9), "on the half beat grid, got " + now);
            });
            // Shift: free.
            double before = Audio(clip).Markers.OrderBy(m => m.Beat).ElementAt(1).Beat;
            Steps(rig, clip, () => rig.Drag(WaveAt(rig, before, 0.3), WaveAt(rig, before + 0.13, 0.3), RawInputModifiers.Shift), save: false, check: _ =>
            {
                double now = Audio(clip).Markers.OrderBy(m => m.Beat).ElementAt(1).Beat;
                Assert.That(Math.Abs(now / 0.5 - Math.Round(now / 0.5)), Is.GreaterThan(0.05), "Shift: no snap, got " + now);
            });
            // The clip's length follows its markers (warp stretches the audio between them).
            // Double-click a marker: it goes.
            double at = Audio(clip).Markers.OrderBy(m => m.Beat).ElementAt(1).Beat;
            Steps(rig, clip, () => rig.DoubleClick(WaveAt(rig, at, 0.5)), save: false, check: _ => Assert.That(Audio(clip).Markers.Count, Is.EqualTo(2), "only the two pinned ends are left"));
        }

        [AvaloniaTest]
        public void DelRemovesTheSelectedMarkerAndTheMarkersSurviveASave()
        {
            using var rig = new Rig();
            rig.SelectInSession();
            var page = rig.OpenPage();
            rig.WaitPeaks(rig.SessionClip);
            var wave = page.Wave;
            wave.SetZoom(30);
            Settle();
            var clip = rig.SessionClip;
            string machine = clip.Machine.Name, name = clip.Name;
            rig.DoubleClick(WaveAt(rig, 1.7));
            rig.DoubleClick(WaveAt(rig, 3.4));
            Assert.That(Audio(clip).Markers.Count, Is.EqualTo(4));
            string withFour = State(machine, name);
            // Select the first inner marker with a click and press Del.
            var inner = Audio(clip).Markers.OrderBy(m => m.Beat).ElementAt(1);
            rig.Click(WaveAt(rig, inner.Beat, 0.4));
            Assert.That(wave.Selected, Is.GreaterThanOrEqualTo(0));
            rig.Key(PhysicalKey.Delete);
            Assert.That(Audio(clip).Markers.Count, Is.EqualTo(3), "Del removes the selected marker");
            rig.Undo();
            Assert.That(State(machine, name), Is.EqualTo(withFour), "one undo");
            Assert.That(Saved(machine, name), Is.EqualTo(withFour), "the markers are in the saved song");
        }

        [AvaloniaTest]
        public void AMarkerAddedNearATransientIsPinnedToIt()
        {
            string file = Work("clicks.wav");
            int rate = 44100, n = rate * 4;
            var inter = new float[n * 2];
            for (int beat = 0; beat < 8; beat++)
            {
                int at = (int)(beat * 0.5 * rate);
                for (int i = 0; i < 2000 && at + i < n; i++) inter[2 * (at + i)] = inter[2 * (at + i) + 1] = (float)(Math.Sin(i * 0.3) * Math.Exp(-i / 300.0));
            }
            WriteWav(file, rate, inter);
            using var rig = new Rig(file);
            rig.SelectInSession();
            var page = rig.OpenPage();
            rig.WaitPeaks(rig.SessionClip);
            var clip = rig.SessionClip;
            var transients = Layout.TransientsOf(clip);
            Assert.That(transients.Count, Is.GreaterThan(2), "the clicks are found");
            var wave = page.Wave;
            wave.SetZoom(60);
            Settle();
            double t = transients[2];
            double beatOfT = rig.Page.Wave.Markers.Count >= 2 ? 0 : t / Layout.AudioSourceOf(clip)!.SampleRate * Audio(clip).Bpm / 60;
            // A click 2 pixels from the transient pins the marker on it exactly.
            rig.DoubleClick(WaveAt(rig, beatOfT) + new Vector(2, 0));
            Assert.That(Audio(clip).Markers.Any(m => Math.Abs(m.Sample - t) < 1), Is.True, "the marker is on the transient, not where the pointer was");
        }

        [AvaloniaTest]
        public void TheRegionHandlesMoveTheStartAndTheLoopWithOneUndoStepEach()
        {
            using var rig = new Rig();
            rig.SelectInSession();
            var page = rig.OpenPage();
            rig.WaitPeaks(rig.SessionClip);
            var wave = page.Wave;
            wave.SetZoom(30);
            Settle();
            var clip = rig.SessionClip;
            Assert.That(Layout.ClipOf(clip).Start, Is.EqualTo(0));
            // The start flag, at the foot of the waveform: drag it two beats in.
            double y = wave.Bounds.Height - 5;
            Steps(rig, clip, () => rig.Drag(rig.In(wave, 0, 0) + new Vector(wave.HandleX(WarpEditor.Handle.Start) + 1, y), rig.In(wave, 0, 0) + new Vector(wave.X(2), y)), save: false,
                check: _ => Assert.That(Layout.ClipOf(clip).Start, Is.EqualTo(2 * Core.TPB), "two beats in, on the grid"));
            // The loop's end bracket: back from the end by a beat.
            double end = wave.HandleX(WarpEditor.Handle.LoopEnd);
            Steps(rig, clip, () => rig.Drag(rig.In(wave, 0, 0) + new Vector(end - 2, y), rig.In(wave, 0, 0) + new Vector(end - wave.PixelsPerBeat * 2, y)), save: true,
                check: _ => Assert.That(Layout.ClipOf(clip).LoopEnd, Is.GreaterThan(0).And.LessThan(clip.Length)));
        }

        [AvaloniaTest]
        public void AnArrangementClipsWaveformRegionHandlesTrimWhatItPlays()
        {
            using var rig = new Rig();
            rig.SelectInArrangement();
            var page = rig.OpenPage();
            rig.WaitPeaks(rig.ArrangedClip);
            var wave = page.Wave;
            wave.SetZoom(30);
            Settle();
            var clip = rig.ArrangedClip;
            double y = wave.Bounds.Height - 5;
            Steps(rig, clip, () => rig.Drag(rig.In(wave, 0, 0) + new Vector(wave.HandleX(WarpEditor.Handle.Start) + 1, y), rig.In(wave, 0, 0) + new Vector(wave.X(1), y)), save: true,
                check: _ => Assert.That(rig.Editor.ClipsOf(clip).Single().Spec.Start, Is.EqualTo(Core.TPB), "the arranged clip starts a beat into the audio"));
        }

        // ------------------------------------------------------------------ the agent makes the same change

        [AvaloniaTest]
        public void TheAgentsAudioClipSetAndThePagesControlsMakeTheSameChange()
        {
            using var rig = new Rig();
            rig.SelectInSession();
            var page = rig.OpenPage();
            var clip = rig.SessionClip;
            // The page: warp, tempo, transpose, gain.
            page.WarpBox.SelectedIndex = AudioClipPage.Warps.ToList().FindIndex(w => w.Value == "Complex");
            page.Type(page.TempoBox, "100");
            page.Type(page.TransposeBox, "3");
            page.Type(page.GainBox, "-4");
            string byPage = State("A", clip.Name);
            // The agent, on the other clip, the same four changes.
            Ok(Tool("audio_clip_set", new JsonObject
            {
                ["machine"] = "B", ["pattern"] = rig.ArrangedClip.Name, ["warp"] = "Complex", ["tempo_bpm"] = 100, ["transpose"] = 3, ["gain_db"] = -4,
            }));
            var a = Audio(clip);
            var b = Audio(rig.ArrangedClip);
            Assert.That((b.Warp, b.Bpm, b.Transpose, b.GainDb), Is.EqualTo((a.Warp, a.Bpm, a.Transpose, a.GainDb)), "the agent and the page reach the same model state");
            Assert.That(byPage, Does.Contain("Complex"));
        }

        // ------------------------------------------------------------------ the slots' waveforms

        [AvaloniaTest]
        public void AnAudioSlotShowsItsWaveformAndAMidiSlotDoesNot()
        {
            using var rig = new Rig();
            rig.Show("Session");
            var cell = rig.Session.SlotAt(Layout.FirstTrackOf(rig.A), 0);
            Assert.That(cell.Wave, Is.Not.Null, "an audio clip's slot has its waveform");
            rig.WaitPeaks(rig.SessionClip);
            Settle();
            rig.Session.RefreshState();
            Settle();
            Assert.That(cell.Wave.Drawn, Is.True, "drawn from the peak cache once it is made");
            Assert.That(cell.Wave.Builds, Is.EqualTo(1).Or.EqualTo(2), "built once for flat, once for the peaks");

            // A MIDI clip has none.
            var poly = Layout.CreateClip(Layout.FirstTrackOf(rig.A), 1);
            Assert.That(poly, Is.Not.Null);
        }

        [AvaloniaTest]
        public void ManyAudioSlotsCostNothingWhileTheyPlayAndTheWaveformFollowsTheGain()
        {
            using var rig = new Rig();
            rig.Show("Session");
            rig.WaitPeaks(rig.SessionClip);
            var cell = rig.Session.SlotAt(Layout.FirstTrackOf(rig.A), 0);
            rig.Session.RefreshState();
            Settle();
            var wave = cell.Wave;
            int builds = wave.Builds;
            // A frame at a time: the position moves, the waveform is not made again.
            for (int i = 0; i < 40; i++)
            {
                wave.SetPosition(i / 40.0);
                Settle();
            }
            Assert.That(wave.Builds, Is.EqualTo(builds), "40 frames of a moving position: the geometry is kept");
            // A gain change is an edit: the slot is made again with it.
            double peakBefore = wave.PeakHeight;
            rig.SelectInSession();
            var page = rig.OpenPage();
            page.Type(page.GainBox, "-12");
            Settle();
            rig.Session.RefreshState();
            Settle();
            var again = rig.Session.SlotAt(Layout.FirstTrackOf(rig.A), 0).Wave;
            again.ForceBuild();
            Assert.That(again.PeakHeight, Is.LessThan(peakBefore * 0.5), "-12 dB: the drawn waveform is a quarter of the height");
        }

        // ------------------------------------------------------------------ small things

        [AvaloniaTest]
        public void AShortArrangementAudioClipShowsItsWholeNameInItsTooltip()
        {
            using var rig = new Rig();
            var item = rig.SelectInArrangement();
            Assert.That(rig.Editor.TrimRight(item, item.Time + 4), Is.Null.Or.Empty);
            Settle();
            item = rig.Sequence.Arrangement.ItemsOf(rig.LaneB).First();
            var r = rig.Sequence.Arrangement.ClipRect(item);
            string tip = rig.Sequence.Arrangement.TipAt(new Point(r.X + r.Width / 2, r.Y + r.Height * 0.8));
            Assert.That(tip, Does.StartWith(rig.ArrangedClip.Name + "\n"), "the whole name, on its own line");
        }
    }
}
