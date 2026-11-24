// Fork (docs/specs/neo-audio.md, NA2): audio clips in the Arrangement, driven as a user does it (mouse and keys on the canvas) and as the
// agent does it (arrangement_clip, audio_clip_set). Each gesture is one undo step, comes back with redo, and is saved in the .nbl.

using System;
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
using libsndfile;
using NUnit.Framework;
using ReBuzz.Agent;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Sequence;
using NeoBuzz.TestSupport;

namespace ReBuzz.Studio.Tests
{
    public class NeoAudioArrangementTests
    {
        static ReBuzzCore Core => Studio.Host.Core;
        static RawInputModifiers Cmd => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        static RawInputModifiers AltKey => RawInputModifiers.Alt;

        static void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        static string Work(string name)
        {
            string dir = TestTemp.Path($"neo-audio-arr-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        /// <summary>Eight seconds of two tones: long enough for several bars at any tempo.</summary>
        internal static string Tones(string name, int rate = 32000, double seconds = 8)
        {
            string path = Work(name);
            int n = (int)(rate * seconds);
            var inter = new float[n * 2];
            for (int i = 0; i < n; i++)
            {
                double env = 0.4 + 0.4 * Math.Sin(2 * Math.PI * 2 * i / rate);
                inter[2 * i] = (float)(env * Math.Sin(2 * Math.PI * 440 * i / rate));
                inter[2 * i + 1] = (float)(env * Math.Sin(2 * Math.PI * 660 * i / rate));
            }
            using var f = SoundFile.Create(path, rate, 2, Format.SF_FORMAT_WAV | Format.SF_FORMAT_FLOAT);
            f.WriteFloat(inter, 0, n);
            return path;
        }

        sealed class Rig : IDisposable
        {
            public MainWindow Window { get; }
            public SequenceView View { get; }
            public ArrangementCanvas Canvas => View.Arrangement;
            public ClipEditor Editor { get; } = new ClipEditor(Core);
            public ISequence LaneA { get; }
            public ISequence LaneB { get; }
            readonly bool looping;
            readonly IDisposable untitled;

            public Rig()
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                looping = Core.Looping;
                Core.ExecuteCommand(BuzzCommand.Stop);
                Core.Modified = false;
                Core.ExecuteCommand(BuzzCommand.NewFile);
                Settle();
                var layout = Core.SessionLayout;
                layout.Ensure();
                var a = layout.NewAudioTrack("A");
                var b = layout.NewAudioTrack("B");
                Assert.That(a, Is.Not.Null, "Neo Audio is installed");
                LaneA = Core.Song.Sequences.First(s => s.Machine == a);
                LaneB = Core.Song.Sequences.First(s => s.Machine == b);
                Window = new MainWindow(Studio.Host) { Width = 1440, Height = 800 };
                ToolTip.SetServiceEnabled(Window, false);
                Window.Show();
                var tab = Window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Arrangement");
                tab.IsSelected = true;
                Settle();
                View = (SequenceView)tab.Content!;
                Click(Of(View.ArrangementModeButton));
                Assert.That(View.IsArrangementMode, Is.True);
                Settle();
            }

            /// <summary>An audio clip of the tones, placed on the lane at the tick.</summary>
            public ClipItem Clip(ISequence lane, int time, int ticks = 0)
            {
                var layout = Core.SessionLayout;
                var pattern = layout.ImportAudio(layout.FirstTrackOf(lane.Machine), 0, Tones("tones.wav"), out string error);
                Assert.That(pattern, Is.Not.Null, error);
                Assert.That(Editor.Place(lane, pattern, time, ticks), Is.Null);
                Settle();
                return Canvas.ItemsOf(lane).Single(i => i.Time == time);
            }

            public Point Of(Avalonia.Controls.Control c) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), Window)!.Value;

            /// <summary>A point of a clip in window pixels: fx along its width, fy down its height.</summary>
            public Point In(ClipItem item, double fx, double fy)
            {
                Settle();
                var r = Canvas.ClipRect(item);
                return Canvas.TranslatePoint(new Point(r.X + fx * r.Width, r.Y + fy * r.Height), Window)!.Value;
            }

            /// <summary>A point of a clip, dx pixels from its left and dy down.</summary>
            public Point Px(ClipItem item, double dx, double dy)
            {
                Settle();
                var r = Canvas.ClipRect(item);
                return Canvas.TranslatePoint(new Point(r.X + dx, r.Y + dy), Window)!.Value;
            }

            public Point At(ISequence lane, int tick, double fy = 0.7)
            {
                Settle();
                var row = Canvas.RowOf(lane);
                var p = Canvas.TranslatePoint(new Point(Canvas.Geometry.XOf(tick), Canvas.YOf(row) + fy * row.Height), Window);
                return p!.Value;
            }

            public void Click(Point p, RawInputModifiers mods = RawInputModifiers.None)
            {
                Window.MouseMove(p, mods);
                Window.MouseDown(p, MouseButton.Left, mods);
                Window.MouseUp(p, MouseButton.Left, mods);
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

            public void Undo() => Key(PhysicalKey.Z, Cmd);
            public void Redo()
            {
                if (OperatingSystem.IsMacOS()) Key(PhysicalKey.Z, Cmd | RawInputModifiers.Shift);
                else Key(PhysicalKey.Y, Cmd);
            }

            public ClipItem Select(ClipItem item)
            {
                Click(In(item, 0.3, 0.8));
                return item;
            }

            public ClipItem Item(ISequence lane) { Settle(); return Canvas.ItemsOf(lane).OrderBy(i => i.Time).First(); }

            public ClipItem First(ISequence lane) { Settle(); return Editor.Read(lane).OrderBy(i => i.Time).First(); }

            public void Dispose()
            {
                Core.ExecuteCommand(BuzzCommand.Stop);
                Window.Close();
                Settle();
                Dialogs.Responder = null;
                Core.Looping = looping;
                Core.Modified = false;
                Core.ExecuteCommand(BuzzCommand.NewFile);
                Settle();
            }
        }

        /// <summary>Everything an audio lane holds, as text: the events, and each clip's own settings.</summary>
        static string Snap() => string.Join(" ; ", Core.Song.Sequences.Where(s => SessionLayout.IsClipPlayer(s.Machine)).Select(s =>
            s.Machine.Name + ": " + string.Join(" ", s.Events.OrderBy(e => e.Key).Select(e =>
            {
                var a = e.Value.Pattern == null ? null : Core.SessionLayout.AudioOf(e.Value.Pattern);
                return $"{e.Key}:{e.Value.Pattern?.Name}[{e.Value.Offset},{e.Value.End}]" +
                    (a == null ? "" : $"<in {Math.Round(a.FadeIn, 3)} out {Math.Round(a.FadeOut, 3)} tail {Math.Round(a.Tail, 3)} x {a.CrossIn} gain {Math.Round(a.GainDb, 2)} rev {a.Reverse} warp {a.Warp} bpm {Math.Round(a.Bpm, 2)} len {e.Value.Pattern.Length} {a.Asset.Length}>");
            })) + " loops " + string.Join(",", new ClipEditor(Core).Read(s).Select(i => i.Spec.Loop ? "on" : "off"))));

        static void Reopen(string path)
        {
            Core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Assert.That(Core.TryOpenSongFile(path), Is.True, "opens " + Path.GetFileName(path));
            Dialogs.Responder = null;
            Settle();
        }

        /// <summary>The state after saving and opening the song again, as a file of the extension.</summary>
        static string Saved(string extension)
        {
            string file = Work("saved" + extension);
            if (File.Exists(file)) File.Delete(file);
            Core.SaveSongCopy(file);
            Reopen(file);
            return Snap();
        }

        /// <summary>The gesture's result, its undo, its redo, and the saved song.</summary>
        static void Steps(Rig rig, Action gesture, string extension, Action<string> check = null, bool save = true)
        {
            string before = Snap();
            gesture();
            string after = Snap();
            Assert.That(after, Is.Not.EqualTo(before), "the gesture did something; status: " + rig.View.StatusLine.Text + " canvas " + rig.Canvas.Bounds + " clips " + string.Join(",", rig.Editor.Read(rig.LaneA).Select(i => i.Time + "-" + i.End + " " + rig.Canvas.ClipRect(i))));
            check?.Invoke(after);
            rig.Undo();
            Assert.That(Snap(), Is.EqualTo(before), "one undo takes the whole gesture back");
            rig.Redo();
            Assert.That(Snap(), Is.EqualTo(after), "redo does it again");
            if (save) Assert.That(Saved(extension), Is.EqualTo(after), "saved and opened again as " + extension);
        }

        // ------------------------------------------------------------------ the gestures

        [AvaloniaTest]
        public void AnAudioClipMovesAndMovesToAnotherNeoAudioTrack([Values(".nbl")] string ext)
        {
            using var rig = new Rig();
            var item = rig.Clip(rig.LaneA, 0);
            int length = item.End - item.Time;
            rig.Select(item);
            Steps(rig, () => rig.Drag(rig.In(item, 0.3, 0.8), rig.In(item, 0.3, 0.8) + new Vector(rig.Canvas.Geometry.TickWidth * 32, 0)), ext,
                _ => Assert.That(rig.First(rig.LaneA).Time, Is.GreaterThan(0)));
        }

        [AvaloniaTest]
        public void AnAudioClipMovesAcrossToAnotherNeoAudioTrack([Values(".nbl")] string ext)
        {
            using var rig = new Rig();
            var item = rig.Clip(rig.LaneA, 0);
            Steps(rig, () => rig.Drag(rig.In(item, 0.3, 0.8), rig.At(rig.LaneB, item.Time + 8)), ext, _ =>
            {
                Assert.That(rig.Editor.Read(rig.LaneA), Is.Empty, "it left the first track");
                var moved = rig.First(rig.LaneB);
                Assert.That(moved.Pattern.Machine, Is.SameAs(rig.LaneB.Machine), "its pattern belongs to the machine it plays on");
                Assert.That(Core.SessionLayout.IsAudioClip(moved.Pattern), Is.True);
            });
        }

        [AvaloniaTest]
        public void AltDragCopiesAnAudioClipAndTheCopyIsIndependent([Values(".nbl")] string ext)
        {
            using var rig = new Rig();
            var item = rig.Clip(rig.LaneA, 0);
            int length = item.End - item.Time;
            Steps(rig, () => rig.Drag(rig.In(item, 0.3, 0.8), rig.At(rig.LaneB, item.Time + 8), AltKey), ext, _ =>
            {
                Assert.That(rig.Editor.Read(rig.LaneA).Count, Is.EqualTo(1), "the original stays");
                Assert.That(rig.Editor.Read(rig.LaneB).Count, Is.EqualTo(1), "and the copy is on the other track");
                // Independent: the copy's gain doesn't touch the original.
                var original = rig.First(rig.LaneA);
                var copy = rig.First(rig.LaneB);
                Assert.That(copy.Pattern, Is.Not.SameAs(original.Pattern), "each has its own pattern");
                Assert.That(Core.SessionLayout.AudioOf(copy.Pattern).Asset, Is.EqualTo(Core.SessionLayout.AudioOf(original.Pattern).Asset), "they share the audio");
                Assert.That(rig.Editor.SetGain(copy, -9), Is.Null);
                Assert.That(Core.SessionLayout.AudioOf(original.Pattern).GainDb, Is.EqualTo(0), "the original is as it was");
                Assert.That(Core.SessionLayout.AudioOf(copy.Pattern).GainDb, Is.EqualTo(-9).Within(1e-9));
                rig.Undo();                                 // the gain was a step of its own
                Assert.That(Core.SessionLayout.AudioOf(rig.First(rig.LaneB).Pattern).GainDb, Is.EqualTo(0));
            });
        }

        [AvaloniaTest]
        public void TheRightEdgeTrimsAnAudioClip([Values(".nbl")] string ext)
        {
            using var rig = new Rig();
            var item = rig.Clip(rig.LaneA, 0);
            int end = item.End;
            rig.Select(item);
            Steps(rig, () => rig.Drag(rig.Px(item, rig.Canvas.ClipRect(item).Width - 2, 40), rig.Px(item, rig.Canvas.ClipRect(item).Width * 0.5, 40)), ext,
                _ => Assert.That(rig.First(rig.LaneA).End, Is.LessThan(end)));
        }

        [AvaloniaTest]
        public void AltDragOfTheRightEdgeStretchesAnAudioClip([Values(".nbl")] string ext)
        {
            using var rig = new Rig();
            var item = rig.Clip(rig.LaneA, 0);
            int end = item.End;
            rig.Select(item);
            var w = rig.Canvas.ClipRect(item).Width;
            Steps(rig, () => rig.Drag(rig.Px(item, w - 2, 40), rig.Px(item, w * 0.75, 40), AltKey | RawInputModifiers.Shift), ext, _ =>
            {
                var s = rig.First(rig.LaneA);
                Assert.That(s.End, Is.LessThan(end), "shorter");
                Assert.That(s.Spec.Start, Is.EqualTo(0), "but all of the audio still plays, warped");
            });
        }

        [AvaloniaTest]
        public void TheLoopSwitchAndDraggingTheEdgeRepeatAnAudioClip([Values(".nbl")] string ext)
        {
            using var rig = new Rig();
            var item = rig.Clip(rig.LaneA, 0);
            int end = item.End;
            rig.Select(item);
            Steps(rig, () => rig.Key(PhysicalKey.L), ext, _ => Assert.That(rig.First(rig.LaneA).Spec.Loop, Is.True), save: false);
            var looped = rig.Item(rig.LaneA);
            double w = rig.Canvas.ClipRect(looped).Width;
            Steps(rig, () => rig.Drag(rig.Px(looped, w - 2, 40), rig.Px(looped, w * 2.5, 40)), ext,
                _ => Assert.That(rig.First(rig.LaneA).End, Is.GreaterThan(end), "dragging the edge of a looping clip repeats it"));
        }

        [AvaloniaTest]
        public void TheFadeHandlesSetTheFadesOfAnAudioClip([Values(".nbl")] string ext)
        {
            using var rig = new Rig();
            var item = rig.Clip(rig.LaneA, 0);
            rig.Select(item);
            double w = rig.Canvas.ClipRect(item).Width;
            Steps(rig, () => rig.Drag(rig.Px(item, 6, 26), rig.Px(item, 6 + w * 0.2, 26)), ext,
                _ => Assert.That(Core.SessionLayout.AudioOf(rig.First(rig.LaneA).Pattern).FadeIn, Is.GreaterThan(0)), save: false);
            item = rig.Item(rig.LaneA);
            Steps(rig, () => rig.Drag(rig.Px(item, w - 8, 26), rig.Px(item, w - 8 - w * 0.2, 26)), ext,
                _ => Assert.That(Core.SessionLayout.AudioOf(rig.First(rig.LaneA).Pattern).FadeOut, Is.GreaterThan(0)));
        }

        [AvaloniaTest]
        public void TheGainHandleSetsTheGainInDecibels([Values(".nbl")] string ext)
        {
            using var rig = new Rig();
            var item = rig.Clip(rig.LaneA, 0);
            rig.Select(item);
            var r = rig.Canvas.ClipRect(item);
            double area = r.Height - 21, zero = 19 + area / 3, minus12 = 19 + area * 2 / 3;
            Steps(rig, () => rig.Drag(rig.Px(item, r.Width / 2, zero), rig.Px(item, r.Width / 2, minus12)), ext,
                _ => Assert.That(Core.SessionLayout.AudioOf(rig.First(rig.LaneA).Pattern).GainDb, Is.LessThan(-6)));
        }

        [AvaloniaTest]
        public void ClipsThatOverlapCrossfade([Values(".nbl")] string ext)
        {
            using var rig = new Rig();
            var first = rig.Clip(rig.LaneA, 0);
            int length = first.End - first.Time;
            var second = rig.Clip(rig.LaneB, length + 64);
            // Drag the second onto the end of the first (across to the lane of the first): the overlap is a crossfade.
            Steps(rig, () => rig.Drag(rig.In(second, 0.3, 0.8), rig.At(rig.LaneA, length - 16)), ext, _ =>
            {
                var a = Core.SessionLayout.AudioOf(rig.First(rig.LaneA).Pattern);
                Assert.That(a.Tail, Is.GreaterThan(0), "the first clip's audio goes on under the second");
            });
        }

        [AvaloniaTest]
        public void CtrlESplitsAtTheCursorAndCtrlJConsolidates([Values(".nbl")] string ext)
        {
            using var rig = new Rig();
            var item = rig.Clip(rig.LaneA, 0);
            int length = item.End - item.Time;
            rig.Select(item);
            Core.SongCore.PlayPosition = length / 2;        // Ctrl+E cuts where the play position is
            Steps(rig, () => rig.Key(PhysicalKey.E, Cmd), ext, _ =>
            {
                var clips = rig.Editor.Read(rig.LaneA);
                Assert.That(clips.Count, Is.EqualTo(2), "two clips");
                Assert.That(clips[0].Pattern, Is.Not.SameAs(clips[1].Pattern), "each with a pattern of its own");
            }, save: false);
            rig.Click(rig.In(rig.Item(rig.LaneA), 0.3, 0.8));
            int assets = Core.AudioAssets.Count;
            Steps(rig, () => rig.Key(PhysicalKey.J, Cmd), ext, _ =>
                Assert.That(Core.AudioAssets.Count, Is.EqualTo(assets + 1), "rendered to a new asset"));
        }

        [AvaloniaTest]
        public void RPlaysAnAudioClipBackwards([Values(".nbl")] string ext)
        {
            using var rig = new Rig();
            var item = rig.Clip(rig.LaneA, 0);
            rig.Select(item);
            Steps(rig, () => rig.Key(PhysicalKey.R), ext, _ =>
                Assert.That(Core.SessionLayout.AudioOf(rig.First(rig.LaneA).Pattern).Reverse, Is.True));
        }

        // ------------------------------------------------------------------ the agent

        static JsonObject Tool(string tool, JsonObject args = null)
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

        static JsonObject Ok(JsonObject r)
        {
            Assert.That(r["isError"]?.GetValue<bool>() ?? false, Is.False, r.ToJsonString());
            return r["structuredContent"]!.AsObject();
        }

        [AvaloniaTest]
        public void TheAgentEditsAudioClipsInTheArrangement([Values(".nbl")] string ext)
        {
            using var rig = new Rig();
            var item = rig.Clip(rig.LaneA, 0);
            string machine = rig.LaneA.Machine.Name;
            double beats = SongModel.Beats(Core, item.End - item.Time);
            JsonObject Clip(string op, JsonObject more)
            {
                var a = new JsonObject { ["op"] = op, ["machine"] = machine, ["at"] = 0.5 };
                foreach (var kv in more) a[kv.Key] = kv.Value!.DeepClone();
                return Ok(Tool("arrangement_clip", a));
            }
            string before = Snap();
            var fade = Clip("fade", new JsonObject { ["fade_in_beats"] = 1.0, ["fade_out_beats"] = 2.0 });
            Assert.That((double)fade["clips"]![0]!["audio"]!["fade_in_beats"]!, Is.EqualTo(1).Within(1e-6));
            Clip("gain", new JsonObject { ["gain_db"] = -6.0 });
            Clip("reverse", new JsonObject());
            string done = Snap();
            Assert.That(Core.SessionLayout.AudioOf(rig.First(rig.LaneA).Pattern).Reverse, Is.True);
            // Stretch to half the length; then consolidate it.
            Clip("stretch", new JsonObject { ["end"] = beats / 2 });
            Assert.That(SongModel.Beats(Core, rig.First(rig.LaneA).End), Is.EqualTo(beats / 2).Within(0.05));
            int assets = Core.AudioAssets.Count;
            var cons = Clip("consolidate", new JsonObject());
            Assert.That(Core.AudioAssets.Count, Is.EqualTo(assets + 1));
            // A bad one says why.
            var bad = Tool("arrangement_clip", new JsonObject { ["op"] = "gain", ["machine"] = machine, ["at"] = 0.5 });
            Assert.That(bad["isError"]?.GetValue<bool>() ?? false, Is.True, "gain needs gain_db");
            Assert.That(before, Is.Not.EqualTo(Snap()));
            string last = Snap();
            Assert.That(Saved(ext), Is.EqualTo(last), "the agent's edits are saved");
        }

        [AvaloniaTest]
        public void TheAgentSetsFadesReverseAndStretchOfAnAudioClip()
        {
            using var rig = new Rig();
            var item = rig.Clip(rig.LaneA, 0);
            string machine = rig.LaneA.Machine.Name, pattern = item.Pattern.Name;
            double beats = SongModel.Beats(Core, item.End - item.Time);
            var set = Ok(Tool("audio_clip_set", new JsonObject { ["machine"] = machine, ["pattern"] = pattern, ["fade_in_beats"] = 0.5, ["fade_out_beats"] = 1.5, ["reverse"] = true }));
            var a = Core.SessionLayout.AudioOf(rig.First(rig.LaneA).Pattern);
            Assert.That(a.FadeIn, Is.EqualTo(0.5).Within(1e-9));
            Assert.That(a.FadeOut, Is.EqualTo(1.5).Within(1e-9));
            Assert.That(a.Reverse, Is.True);
            var get = Ok(Tool("audio_clip_get", new JsonObject { ["machine"] = machine, ["pattern"] = pattern }));
            Assert.That(get.ToJsonString(), Does.Contain("fade_in_beats"));
            Ok(Tool("audio_clip_set", new JsonObject { ["machine"] = machine, ["pattern"] = pattern, ["stretch_to_beats"] = beats / 2 }));
            Assert.That(SongModel.Beats(Core, rig.First(rig.LaneA).End), Is.EqualTo(beats / 2).Within(0.05));
            var bad = Tool("audio_clip_set", new JsonObject { ["machine"] = machine, ["pattern"] = pattern, ["fade_in_beats"] = -1.0 });
            Assert.That(bad["isError"]?.GetValue<bool>() ?? false, Is.True, "a negative fade is refused");
        }
    }
}
