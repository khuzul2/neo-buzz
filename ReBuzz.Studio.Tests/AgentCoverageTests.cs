// Fork (docs/specs/agent-knowledge.md §11, K3b): the tools for the features built since K3, against the real headless studio:
// arrangement clips, lanes, automation lanes, takes and comping, song speed, and the machine menu's clone, groups, latency,
// attributes and commands. Each has its happy path, its undo step and one refusal that says what to do.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BuzzGUI.Common;
using ReBuzz.Agent;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Tests
{
    public class AgentCoverageTests
    {
        static StudioHost Host => Studio.Host;

        static void Settle(Window window = null)
        {
            Dispatcher.UIThread.RunJobs();
            window?.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        static (bool Error, string Text, JsonObject Data) Try(string tool, JsonObject args = null)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() },
            };
            var task = Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            var r = task.GetAwaiter().GetResult()!;
            if (r["error"] is JsonObject e) return (true, e["message"]!.GetValue<string>(), new JsonObject());
            var result = r["result"]!.AsObject();
            return (result["isError"]?.GetValue<bool>() == true, result["content"]![0]!["text"]!.GetValue<string>(), result["structuredContent"] as JsonObject ?? new JsonObject());
        }

        static JsonObject Call(string tool, JsonObject args = null)
        {
            var r = Try(tool, args);
            Assert.That(r.Error, Is.False, $"{tool}: {r.Text}");
            return r.Data;
        }

        static JsonObject Args(params (string Name, JsonNode Value)[] pairs)
        {
            var o = new JsonObject();
            foreach (var (n, v) in pairs) o[n] = v;
            return o;
        }

        static MainWindow NewSong()
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            SongFiles.New().Wait();
            var window = new MainWindow(Host) { Width = 1280, Height = 800 };
            window.Show();
            Settle(window);
            return window;
        }

        static int Steps => Call("history_list")["total"]!.GetValue<int>();

        static JsonArray Clips(JsonObject r) => r["clips"]!.AsArray();

        static JsonObject Lane(string machine, int lane = 0)
        {
            var lanes = Call("arrangement_get")["lanes"]!.AsArray().Where(l => l!["machine"]!.GetValue<string>() == machine).ToList();
            return lanes[lane]!.AsObject();
        }

        static string Err(string tool, JsonObject args)
        {
            var r = Try(tool, args);
            Assert.That(r.Error, Is.True, $"{tool} should have been refused");
            return r.Text;
        }

        /// <summary>Bass with a four-beat clip Verse; the studio open.</summary>
        static MainWindow Song(out System.IDisposable restore)
        {
            var window = NewSong();
            restore = Studio.AfterTest(() =>
            {
                Dialogs.Responder = null;
                window.Close();
            });
            Call("machine_add", Args(("gear", "Neo Poly"), ("name", "Bass")));
            Call("clip_create", Args(("machine", "Bass"), ("name", "Verse"), ("length_beats", 4)));
            Settle(window);
            return window;
        }

        [AvaloniaTest]
        public void ClipsAreAddedTrimmedSplitJoinedLoopedCopiedAndMadeUnique()
        {
            var window = Song(out var restore);
            using var _ = restore;
            var stack = Host.Core.SongCore.ActionStack;

            int steps = Steps;
            var added = Call("arrangement_clip", Args(("op", "add"), ("machine", "Bass"), ("clip", "Verse"), ("at", 0), ("length_beats", 8)));
            Assert.That(Steps, Is.EqualTo(steps + 1), "one undo step, the lane included");
            Assert.That(Clips(added), Has.Count.EqualTo(1));
            Assert.That(Clips(added)[0]!["ends"]!.GetValue<double>(), Is.EqualTo(8));
            Assert.That(Clips(added)[0]!["loop"]!["on"]!.GetValue<bool>(), Is.True, "a new clip takes the clip's loop (on)");
            var lane = Lane("Bass");
            Assert.That(lane["lane"]!.GetValue<int>(), Is.EqualTo(0));
            Assert.That(lane["role"]!.GetValue<string>(), Is.EqualTo("main"));
            stack.Undo();
            Assert.That(Call("arrangement_get")["lanes"]!.AsArray().Any(l => l!["machine"]!.GetValue<string>() == "Bass" && l["events"]!.AsArray().Count > 0), Is.False, "undo takes the clip back");
            stack.Redo();

            var trimmed = Call("arrangement_clip", Args(("op", "trim"), ("machine", "Bass"), ("at", 1), ("end", 6)));
            Assert.That(Clips(trimmed)[0]!["ends"]!.GetValue<double>(), Is.EqualTo(6));

            var split = Call("arrangement_clip", Args(("op", "split"), ("machine", "Bass"), ("at", 1), ("split_at", 4)));
            Assert.That(Clips(split), Has.Count.EqualTo(2));
            Assert.That(Clips(split)[1]!["at"]!.GetValue<double>(), Is.EqualTo(4));
            var joined = Call("arrangement_clip", Args(("op", "join"), ("machine", "Bass"), ("at", 1)));
            Assert.That(Clips(joined), Has.Count.EqualTo(1));

            var off = Call("arrangement_clip", Args(("op", "loop"), ("machine", "Bass"), ("at", 1), ("on", false)));
            Assert.That(Clips(off)[0]!["loop"]!["on"]!.GetValue<bool>(), Is.False);
            Assert.That(Clips(off)[0]!["ends"]!.GetValue<double>(), Is.LessThanOrEqualTo(4), "a clip that doesn't loop plays its clip once");

            var copied = Call("arrangement_clip", Args(("op", "copy"), ("machine", "Bass"), ("at", 0), ("to", 16)));
            Assert.That(Clips(copied), Has.Count.EqualTo(2));
            string copyClip = Clips(copied)[1]!["clip"]!.GetValue<string>();
            Assert.That(copyClip, Is.Not.EqualTo("Verse"), "an independent copy has a clip of its own");
            var linked = Call("arrangement_clip", Args(("op", "copy"), ("machine", "Bass"), ("at", 0), ("to", 24), ("link", true)));
            Assert.That(Clips(linked)[^1]!["clip"]!.GetValue<string>(), Is.EqualTo("Verse"), "linked: the same clip");

            var moved = Call("arrangement_clip", Args(("op", "move"), ("machine", "Bass"), ("at", 24), ("to", 32)));
            Assert.That(Clips(moved).Select(c => c!["at"]!.GetValue<double>()), Does.Contain(32.0).And.Not.Contain(24.0));

            var unique = Call("arrangement_clip", Args(("op", "make_unique"), ("machine", "Bass"), ("at", 32)));
            Assert.That(Clips(unique).Last()!["clip"]!.GetValue<string>(), Is.Not.EqualTo("Verse"));

            var drawn = Call("arrangement_clip", Args(("op", "draw"), ("machine", "Bass"), ("at", 40), ("length_beats", 4)));
            Assert.That(Clips(drawn).Select(c => c!["at"]!.GetValue<double>()), Does.Contain(40.0));

            // Refusals say where the clips are and what to do.
            Assert.That(Err("arrangement_clip", Args(("op", "split"), ("machine", "Bass"), ("at", 0), ("split_at", 500))), Does.Contain("Split inside the clip"));
            Assert.That(Err("arrangement_clip", Args(("op", "loop"), ("machine", "Bass"), ("at", 90), ("on", true))), Does.Contain("No clip at beat 90").And.Contain("Verse"));
            Assert.That(Err("arrangement_clip", Args(("op", "trim"), ("machine", "Bass"), ("at", 0))), Does.Contain("start and/or end"));
            Assert.That(Err("arrangement_clip", Args(("op", "move"), ("machine", "Bass"), ("at", 0), ("to", 4), ("to_lane", 7))), Does.Contain("lane"));
            Assert.That(Err("arrangement_clip", Args(("op", "join"), ("machine", "Bass"), ("at", 0))), Does.Contain("No clip starts where this one ends"));
        }

        [AvaloniaTest]
        public void LanesAreAddedWithTheirRoleAndRemovedOnlyWhenEmpty()
        {
            var window = Song(out var restore);
            using var _ = restore;
            var stack = Host.Core.SongCore.ActionStack;
            Call("arrangement_clip", Args(("op", "add"), ("machine", "Bass"), ("clip", "Verse"), ("at", 0)));

            int steps = Steps;
            var added = Call("arrangement_lane", Args(("op", "add"), ("machine", "Bass"), ("role", "take")));
            Assert.That(Steps, Is.EqualTo(steps + 1));
            Assert.That(added["lanes"]!.AsArray().Select(l => l!["role"]!.GetValue<string>()), Is.EqualTo(new[] { "main", "take" }));
            Assert.That(Lane("Bass", 1)["role"]!.GetValue<string>(), Is.EqualTo("take"));
            Call("arrangement_lane", Args(("op", "add"), ("machine", "Bass"), ("role", "automation")));
            Assert.That(Lane("Bass", 2)["role"]!.GetValue<string>(), Is.EqualTo("automation"));

            Assert.That(Err("arrangement_lane", Args(("op", "remove"), ("machine", "Bass"), ("lane", 0))), Does.Contain("still has").And.Contain("arrangement_clear"));
            var removed = Call("arrangement_lane", Args(("op", "remove"), ("machine", "Bass"), ("lane", 2)));
            Assert.That(removed["lanes"]!.AsArray().Count, Is.EqualTo(2));
            stack.Undo();
            Assert.That(Call("arrangement_get")["lanes"]!.AsArray().Count(l => l!["machine"]!.GetValue<string>() == "Bass"), Is.EqualTo(3), "undo brings the lane back");
            Assert.That(Err("arrangement_lane", Args(("op", "add"), ("machine", "Master"))), Does.Contain("Master"));
        }

        [AvaloniaTest]
        public void AutomationClipsAreWrittenListedClearedAndShown()
        {
            var window = Song(out var restore);
            using var _ = restore;
            var core = Host.Core;
            var machine = core.SongCore.MachinesList.Single(m => m.Name == "Bass");
            var param = SongModel.Params(machine).First(p => !p.IsTrack && p.Parameter.MaxValue - p.Parameter.MinValue > 20 &&
                AutomationParams.Find(machine, new ColumnKey(p.Group, p.Parameter.IndexInGroup, 0)) != null);
            int lo = param.Parameter.MinValue, hi = param.Parameter.MaxValue;
            JsonArray Points(int a, int b) => new(new JsonObject { ["at"] = 0, ["value"] = $"raw:{a}" }, new JsonObject { ["at"] = 8, ["value"] = $"raw:{b}" });

            int steps = Steps;
            var written = Call("arrangement_automation", Args(("op", "write"), ("machine", "Bass"), ("param", param.Name), ("from", 0), ("to", 8), ("points", Points(lo, hi))));
            Assert.That(Steps, Is.EqualTo(steps + 1), "one undo step, the lane and the clip included");
            Assert.That(written["clip"]!["points"]!.GetValue<int>(), Is.EqualTo(2));
            Assert.That(Lane("Bass", 0)["role"]!.GetValue<string>(), Is.EqualTo("automation"), "the clip is on an automation lane");

            var list = Call("arrangement_automation", Args(("op", "list"), ("machine", "Bass")))["lanes"]!.AsArray();
            Assert.That(list, Has.Count.EqualTo(1));
            var seg = list[0]!["segments"]![0]!;
            Assert.That(seg["source"]!.GetValue<string>(), Is.EqualTo("clip"));
            Assert.That(seg["points"]!.AsArray(), Has.Count.EqualTo(2));
            Assert.That(seg["points"]![0]!["ramp"]!.GetValue<bool>(), Is.True, "linear: a ramp to the next point");

            // The same range again edits the clip; it doesn't draw a second one.
            Call("arrangement_automation", Args(("op", "write"), ("machine", "Bass"), ("param", param.Name), ("from", 0), ("to", 8), ("shape", "step"), ("points", Points(hi, lo))));
            Assert.That(Call("arrangement_automation", Args(("op", "list"), ("machine", "Bass")))["lanes"]![0]!["segments"]!.AsArray(), Has.Count.EqualTo(1));

            var shown = Call("arrangement_automation", Args(("op", "add_lane"), ("machine", "Bass"), ("param", param.Name)));
            Assert.That(shown["lanes"]![0]!["shown"]!.GetValue<bool>(), Is.True);
            Assert.That(core.Arrangement.ParameterLanes, Has.Count.EqualTo(1));
            core.SongCore.ActionStack.Undo();
            Assert.That(core.Arrangement.ParameterLanes, Is.Empty, "undo hides the lane");

            var cleared = Call("arrangement_automation", Args(("op", "clear"), ("machine", "Bass"), ("param", param.Name), ("from", 0), ("to", 8)));
            Assert.That(cleared["removed"]!.GetValue<int>(), Is.GreaterThan(0));
            Assert.That(Call("arrangement_automation", Args(("op", "list"), ("machine", "Bass")))["lanes"]!.AsArray(), Is.Empty);
            core.SongCore.ActionStack.Undo();
            Assert.That(Call("arrangement_automation", Args(("op", "list"), ("machine", "Bass")))["lanes"]!.AsArray(), Has.Count.EqualTo(1), "undo brings the clip back");

            var late = new JsonArray(new JsonObject { ["at"] = 20, ["value"] = $"raw:{lo}" });
            Assert.That(Err("arrangement_automation", Args(("op", "write"), ("machine", "Bass"), ("param", param.Name), ("from", 0), ("to", 8), ("points", late))), Does.Contain("outside from"));
            Assert.That(Err("arrangement_automation", Args(("op", "write"), ("machine", "Bass"), ("param", "No Such Knob"), ("points", Points(lo, hi)))), Does.Contain("No Such Knob"));
        }

        [AvaloniaTest]
        public void ClipEnvelopesAreReadWrittenAndClearedForSessionAndArrangementClipsAsOneUndoStepEach()
        {
            var window = Song(out var restore);
            using var _ = restore;
            var core = Host.Core;
            var machine = core.SongCore.MachinesList.Single(m => m.Name == "Bass");
            var param = SongModel.Params(machine).First(p => !p.IsTrack && p.Parameter.MaxValue - p.Parameter.MinValue > 20 &&
                AutomationParams.Find(machine, new ColumnKey(p.Group, p.Parameter.IndexInGroup, 0)) != null);
            int lo = param.Parameter.MinValue, hi = param.Parameter.MaxValue;
            var key = new ColumnKey(param.Group, param.Parameter.IndexInGroup, 0);
            JsonArray Points(int a, int b) => new(new JsonObject { ["at"] = 0, ["value"] = $"raw:{a}" }, new JsonObject { ["at"] = 3, ["value"] = $"raw:{b}" });
            Call("arrangement_clip", Args(("op", "add"), ("machine", "Bass"), ("clip", "Verse"), ("at", 0), ("length_beats", 8)));
            var verse = machine.Clips.Single(p => p.Name == "Verse");

            // An arrangement clip, by a position inside it.
            int steps = Steps;
            var written = Call("clip_envelope", Args(("op", "write"), ("machine", "Bass"), ("lane", 0), ("at", 1), ("param", param.Name), ("points", Points(lo, hi))));
            Assert.That(Steps, Is.EqualTo(steps + 1), "one undo step");
            Assert.That(written["clip"]!.GetValue<string>(), Is.EqualTo("Verse"));
            Assert.That(written["envelopes"]![0]!["points"]!.AsArray(), Has.Count.EqualTo(2));
            Assert.That(written["envelopes"]![0]!["points"]![0]!["ramp"]!.GetValue<bool>(), Is.True, "linear: a ramp to the next point");
            // The lane's model holds the same points: what the agent wrote is what a person sees in the Detail lane.
            var model = ClipEnvelopes.Read(verse, key);
            Assert.That(model.Points.Select(p => p.Value), Is.EqualTo(new[] { lo, hi }));

            // Read: by clip, and the same envelope.
            var read = Call("clip_envelope", Args(("op", "read"), ("machine", "Bass"), ("clip", "Verse")));
            Assert.That(read["envelopes"]!.AsArray(), Has.Count.EqualTo(1));
            Assert.That(read["envelopes"]![0]!["param"]!.GetValue<string>(), Is.EqualTo(param.Parameter.Name));

            // Steps, and a second write between the same beats replaces those points (no third).
            Call("clip_envelope", Args(("op", "write"), ("machine", "Bass"), ("clip", "Verse"), ("param", param.Name), ("shape", "step"), ("points", Points(hi, lo))));
            model = ClipEnvelopes.Read(verse, key);
            Assert.That(model.Points.Select(p => (p.Value, p.Ramp)), Is.EqualTo(new[] { (hi, false), (lo, false) }));

            // A Session clip, by its scene.
            var layout = core.SessionLayout;
            layout.Ensure();
            int track = Enumerable.Range(0, layout.TrackCount).First(t => layout.SequenceOf(t)?.Machine == machine);
            var session = layout.CreateClip(track, 0);
            Assert.That(session, Is.Not.Null);
            var inSession = Call("clip_envelope", Args(("op", "write"), ("machine", "Bass"), ("scene", 1), ("param", param.Name), ("points", Points(lo, hi))));
            Assert.That(inSession["clip"]!.GetValue<string>(), Is.EqualTo(session.Name));
            Assert.That(ClipEnvelopes.Read(session, key).Points, Has.Count.EqualTo(2));
            Assert.That(ClipEnvelopes.Read(verse, key).Points.Select(p => p.Value), Is.EqualTo(new[] { hi, lo }), "the other clip is not touched");

            // Clear is one step; undo brings the points back.
            int before = Steps;
            Call("clip_envelope", Args(("op", "clear"), ("machine", "Bass"), ("clip", "Verse"), ("param", param.Name)));
            Assert.That(Steps, Is.EqualTo(before + 1));
            Assert.That(ClipEnvelopes.Read(verse, key).Points, Is.Empty);
            core.SongCore.ActionStack.Undo();
            Assert.That(ClipEnvelopes.Read(verse, key).Points, Has.Count.EqualTo(2), "undo");

            // The same refusals as the lane's limits.
            Assert.That(Err("clip_envelope", Args(("op", "write"), ("machine", "Bass"), ("clip", "Verse"), ("param", param.Name), ("points", new JsonArray(new JsonObject { ["at"] = 9, ["value"] = $"raw:{lo}" })))), Does.Contain("outside the clip"));
            Assert.That(Err("clip_envelope", Args(("op", "write"), ("machine", "Bass"), ("clip", "Verse"), ("param", "No Such Knob"), ("points", Points(lo, hi)))), Does.Contain("No Such Knob"));
            Assert.That(Err("clip_envelope", Args(("op", "read"), ("machine", "Bass"))), Does.Contain("Say which clip"));
            Assert.That(Err("clip_envelope", Args(("op", "read"), ("machine", "Bass"), ("scene", 9))), Does.Contain("scene"));
        }

        [AvaloniaTest]
        public void ATakeIsCompedOntoTheMainLaneAndRecordingNeedsAnArmedTrack()
        {
            var window = Song(out var restore);
            using var _ = restore;
            Call("arrangement_lane", Args(("op", "add"), ("machine", "Bass")));
            Call("arrangement_lane", Args(("op", "add"), ("machine", "Bass"), ("role", "take")));
            Call("arrangement_clip", Args(("op", "add"), ("machine", "Bass"), ("lane", 1), ("clip", "Verse"), ("at", 0)));

            int steps = Steps;
            var comp = Call("arrangement_takes", Args(("op", "comp"), ("machine", "Bass"), ("take_lane", 1), ("from", 0), ("to", 2), ("to_lane", 0)));
            Assert.That(Steps, Is.EqualTo(steps + 1));
            Assert.That(comp["lane"]!.GetValue<int>(), Is.EqualTo(0));
            var main = Lane("Bass", 0)["events"]!.AsArray();
            Assert.That(main, Has.Count.EqualTo(1));
            Assert.That(main[0]!["ends"]!.GetValue<double>(), Is.EqualTo(2), "the range of the take, trimmed");
            Host.Core.SongCore.ActionStack.Undo();
            Assert.That(Lane("Bass", 0)["events"]!.AsArray(), Is.Empty, "undo takes the comp back");

            Assert.That(Err("arrangement_takes", Args(("op", "comp"), ("machine", "Bass"), ("take_lane", 1), ("from", 100), ("to", 104), ("to_lane", 0))), Does.Contain("no take").And.Contain("take lane"));

            // Recording: arm, see it, and a start without an armed track says what to do.
            Assert.That(Err("arrangement_takes", Args(("op", "start"))), Does.Contain("Arm a track first"));
            var armed = Call("arrangement_takes", Args(("op", "arm"), ("machine", "Bass")));
            Assert.That(armed["armed"]!.AsArray().Select(x => x!.GetValue<string>()), Does.Contain("Bass"));
            Assert.That(armed["recording"]!.GetValue<bool>(), Is.False);
            var started = Call("arrangement_takes", Args(("op", "start")));
            Assert.That(started["recording"]!.GetValue<bool>(), Is.True);
            Assert.That(Err("arrangement_takes", Args(("op", "start"))), Does.Contain("Already recording"));
            var stopped = Call("arrangement_takes", Args(("op", "stop")));
            Assert.That(stopped["recording"]!.GetValue<bool>(), Is.False);
            var disarmed = Call("arrangement_takes", Args(("op", "disarm"), ("machine", "Bass")));
            Assert.That(disarmed["armed"]!.AsArray(), Is.Empty);
        }

        [AvaloniaTest]
        public void TheTakesToolListsChoosesCompsAndDeletesUnusedTakes()
        {
            var window = Song(out var restore);
            using var _ = restore;
            Call("arrangement_lane", Args(("op", "add"), ("machine", "Bass")));
            Call("arrangement_lane", Args(("op", "add"), ("machine", "Bass"), ("role", "take")));
            Call("arrangement_lane", Args(("op", "add"), ("machine", "Bass"), ("role", "take")));
            Call("arrangement_clip", Args(("op", "add"), ("machine", "Bass"), ("lane", 1), ("clip", "Verse"), ("at", 0)));
            Call("clip_create", Args(("machine", "Bass"), ("name", "Chorus"), ("length_beats", 4)));
            Call("arrangement_clip", Args(("op", "add"), ("machine", "Bass"), ("lane", 2), ("clip", "Chorus"), ("at", 0)));

            var list = Call("takes", Args(("op", "list"), ("machine", "Bass")));
            var takes = list["takes"]!.AsArray();
            Assert.That(takes.Count, Is.EqualTo(2), "one take on each take lane");
            Assert.That(takes.Select(t => t!["take"]!.GetValue<int>()).ToArray(), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(takes.Select(t => t!["used"]!.GetValue<bool>()).ToArray(), Is.EqualTo(new[] { false, false }), "nothing is comped yet");

            int steps = Steps;
            Call("takes", Args(("op", "choose"), ("machine", "Bass"), ("take", 2)));
            Assert.That(Steps, Is.EqualTo(steps + 1), "choosing a take is one undo step");
            Assert.That(Lane("Bass", 0)["events"]!.AsArray(), Has.Count.EqualTo(1));
            takes = Call("takes", Args(("op", "list"), ("machine", "Bass")))["takes"]!.AsArray();
            Assert.That(takes.Select(t => t!["used"]!.GetValue<bool>()).ToArray(), Is.EqualTo(new[] { false, true }), "take 2 plays now");

            Assert.That(Err("takes", Args(("op", "choose"), ("machine", "Bass"), ("take", 9))), Does.Contain("no take 9"));
            Assert.That(Err("takes", Args(("op", "comp"), ("machine", "Bass"))), Does.Contain("ranges"));
            Assert.That(Err("takes", Args(("op", "flatten"), ("machine", "Bass"))), Does.Contain("audio"));

            var deleted = Call("takes", Args(("op", "delete_unused"), ("machine", "Bass")));
            Assert.That(deleted["deleted"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(deleted["takes"]!.AsArray(), Has.Count.EqualTo(1), "the take nothing plays is gone");
            Host.Core.SongCore.ActionStack.Undo();
            Assert.That(Call("takes", Args(("op", "list"), ("machine", "Bass")))["takes"]!.AsArray(), Has.Count.EqualTo(2), "undo brings it back");
        }

        [AvaloniaTest]
        public void TheVerticalTrackerIsASettingTheAgentCanSet()
        {
            var window = Song(out var restore);
            using var _ = restore;
            using var put = Studio.AfterTest(() => ReBuzz.Studio.Views.Sequence.SequenceOrientation.Vertical = false);
            Assert.That(Call("settings_get", Args(("key", "appearance.VerticalTracker")))["settings"]![0]!["value"]!.ToString(), Is.EqualTo("False").IgnoreCase);
            Call("settings_set", Args(("key", "appearance.VerticalTracker"), ("value", true)));
            Assert.That(ReBuzz.Studio.Views.Sequence.SequenceOrientation.Vertical, Is.True);
            Call("settings_reset", Args(("key", "appearance.VerticalTracker")));
            Assert.That(ReBuzz.Studio.Views.Sequence.SequenceOrientation.Vertical, Is.False);
            Assert.That(Err("settings_set", Args(("key", "appearance.VerticalTracker"), ("value", "sideways"))), Does.Contain("VerticalTracker"));
        }

        [AvaloniaTest]
        public void TheSongsBarsAreReadAndSetWithOneUndoStep()
        {
            var window = Song(out var restore);
            using var _ = restore;
            var before = Call("song_meter")["meter"]!.AsArray();
            Assert.That(before, Has.Count.EqualTo(1));
            var set = Call("song_meter", Args(("from", 8), ("bar_beats", 3)));
            Assert.That(set["meter"]!.AsArray().Select(m => m!["bar_beats"]!.GetValue<double>()), Is.EqualTo(new[] { before[0]!["bar_beats"]!.GetValue<double>(), 3.0 }));
            Assert.That(Call("arrangement_get")["meter"]!.AsArray(), Has.Count.EqualTo(2), "arrangement_get shows the same bars");
            Host.Core.SongCore.ActionStack.Undo();
            Assert.That(Call("song_meter")["meter"]!.AsArray(), Has.Count.EqualTo(1));
            Assert.That(Try("song_meter", Args(("bar_beats", 0))).Error, Is.True);
        }

        [AvaloniaTest]
        public void SongSpeedIsSetAndUndone()
        {
            var window = Song(out var restore);
            using var _ = restore;
            int steps = Steps;
            var set = Call("song_set", Args(("speed", 12)));
            Assert.That(set["speed"]!.GetValue<int>(), Is.EqualTo(12));
            Assert.That(Host.Core.Speed, Is.EqualTo(12));
            Assert.That(Steps, Is.EqualTo(steps + 1));
            Host.Core.SongCore.ActionStack.Undo();
            Assert.That(Host.Core.Speed, Is.EqualTo(0));
            Assert.That(Try("song_set", Args(("speed", 5000))).Error, Is.True, "the dialog's range is -1000 to 1000");
        }

        [AvaloniaTest]
        public void MachineTracksListsTheEntriesTheirChainsAndWhatIsUnrouted()
        {
            // MT1 (ux-overhaul.md §11): the song's tracks as the Machines view's Tracks mode shows them; a read tool, built from the wires.
            var window = Song(out var restore);
            using var _ = restore;
            foreach (var name in new[] { "Kick", "Snare", "Pad", "Lead" }) Call("machine_add", Args(("gear", "Neo Poly"), ("name", name), ("connect_to", "none")));
            Call("machine_add", Args(("gear", "Neo Comp"), ("name", "Bus"), ("connect_to", "none")));
            Call("machine_add", Args(("gear", "Neo Verb"), ("name", "Verb"), ("connect_to", "none")));
            Call("machine_add", Args(("gear", "Neo Drive"), ("name", "Drive"), ("connect_to", "none")));
            Call("machine_add", Args(("gear", "Neo LFO"), ("name", "Wobble"), ("connect_to", "none")));
            void Wire(string from, string to) => Call("connect", Args(("from", from), ("to", to)));
            Wire("Kick", "Bus"); Wire("Snare", "Bus"); Wire("Bus", "Master");
            Wire("Bass", "Drive"); Wire("Drive", "Master");
            Wire("Pad", "Master"); Wire("Lead", "Master"); Wire("Pad", "Verb"); Wire("Lead", "Verb"); Wire("Verb", "Master");
            Settle(window);

            var data = Call("machine_tracks");
            var tracks = data["tracks"]!.AsArray();
            string[] Ids(JsonNode n) => n!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
            JsonNode Entry(string id) => tracks.First(t => t!["id"]!.GetValue<string>() == id)!;
            Assert.That(tracks.Select(t => t!["id"]!.GetValue<string>()), Is.EqualTo(new[] { "track:Bass", "track:Kick", "track:Snare", "track:Pad", "track:Lead", "group:Bus", "return:Verb" }));
            Assert.That(Entry("track:Bass")["chain"]!.GetValue<string>(), Is.EqualTo("Bass → Drive → Master"));
            Assert.That(Ids(Entry("track:Bass")["machines"]!), Is.EqualTo(new[] { "Bass", "Drive" }));
            Assert.That(Ids(Entry("group:Bus")["members"]!), Is.EqualTo(new[] { "track:Kick", "track:Snare" }));
            Assert.That(Entry("track:Kick")["member_of"]!.GetValue<string>(), Is.EqualTo("group:Bus"));
            Assert.That(Entry("track:Kick")["chain"]!.GetValue<string>(), Is.EqualTo("Kick → Bus → Master"));
            Assert.That(Ids(Entry("return:Verb")["receives_sends_from"]!), Is.EqualTo(new[] { "track:Pad", "track:Lead" }));
            Assert.That(Ids(Entry("track:Pad")["sends_to"]!), Is.EqualTo(new[] { "return:Verb" }));
            Assert.That(Entry("track:Pad")["chain"]!.GetValue<string>(), Is.EqualTo("Pad → Master"));
            var unrouted = data["unrouted"]!.AsArray();
            Assert.That(unrouted.Count, Is.EqualTo(1));
            Assert.That(unrouted[0]!["machine"]!.GetValue<string>(), Is.EqualTo("Wobble"));
            Assert.That(unrouted[0]!["why"]!.GetValue<string>(), Is.EqualTo("control only"));
            Assert.That(data["master"]!.GetValue<string>(), Is.EqualTo("Master"));

            // A read: no undo step, and the answer follows the wires.
            int steps = Steps;
            Call("disconnect", Args(("from", "Pad"), ("to", "Verb")));
            var after = Call("machine_tracks")["tracks"]!.AsArray().Select(t => t!["id"]!.GetValue<string>()).ToList();
            Assert.That(after, Does.Not.Contain("return:Verb"), "with one sender the Verb is a branch of Lead's track");
            Assert.That(Steps, Is.EqualTo(steps + 1), "only the disconnect is a step");
        }

        [AvaloniaTest]
        public void MachinesAreClonedWithTheirClipsAndGrouped()
        {
            var window = Song(out var restore);
            using var _ = restore;
            var core = Host.Core;
            Call("arrangement_clip", Args(("op", "add"), ("machine", "Bass"), ("clip", "Verse"), ("at", 0), ("length_beats", 8)));
            Call("machine_add", Args(("gear", "Neo Poly"), ("name", "Pad")));

            int machines = core.SongCore.MachinesList.Count;
            int steps = Steps;
            var plain = Call("machine_clone", Args(("machines", new JsonArray("Pad"))));
            Assert.That(Steps, Is.EqualTo(steps + 1));
            Assert.That(plain["copies"]![0]!["from"]!.GetValue<string>(), Is.EqualTo("Pad"));
            core.SongCore.ActionStack.Undo();
            Assert.That(core.SongCore.MachinesList.Count, Is.EqualTo(machines), "undo takes the copy away");

            var cloned = Call("machine_clone", Args(("machines", new JsonArray("Bass")), ("with_clips", true)));
            string copy = cloned["copies"]![0]!["name"]!.GetValue<string>();
            Assert.That(copy, Is.Not.EqualTo("Bass"));
            var copyMachine = core.SongCore.MachinesList.Single(m => m.Name == copy);
            Assert.That(copyMachine.Clips.Select(p => p.Name), Does.Contain("Verse"), "its clips");
            Assert.That(Lane(copy)["events"]!.AsArray(), Has.Count.GreaterThan(0), "its rows in the arrangement");
            core.SongCore.ActionStack.Undo();
            Assert.That(core.SongCore.MachinesList.Any(m => m.Name == copy), Is.False);
            Assert.That(Err("machine_clone", Args(("machines", new JsonArray("Master")))), Does.Contain("Master can't be cloned"));

            // Groups: create, fold, rename, add, remove, delete; membership is an undo step too.
            var group = Call("machine_group", Args(("op", "create"), ("machines", new JsonArray("Bass")), ("name", "Band")));
            Assert.That(group["groups"]![0]!["group"]!.GetValue<string>(), Is.EqualTo("Band"));
            Assert.That(group["groups"]![0]!["machines"]!.AsArray().Select(x => x!.GetValue<string>()), Is.EqualTo(new[] { "Bass" }));
            core.SongCore.ActionStack.Undo();
            Assert.That(core.SongCore.MachineGroups, Is.Empty, "undo takes the group away");
            core.SongCore.ActionStack.Redo();
            Assert.That(Call("machine_group", Args(("op", "list")))["groups"]![0]!["machines"]!.AsArray(), Has.Count.EqualTo(1), "redo makes it again, with its machine");
            var added = Call("machine_group", Args(("op", "add"), ("group", "Band"), ("machines", new JsonArray("Pad"))));
            Assert.That(added["groups"]![0]!["machines"]!.AsArray(), Has.Count.EqualTo(2));
            core.SongCore.ActionStack.Undo();
            Assert.That(Call("machine_group", Args(("op", "list")))["groups"]![0]!["machines"]!.AsArray(), Has.Count.EqualTo(1), "undo takes Pad out again");
            core.SongCore.ActionStack.Redo();
            Assert.That(Call("machine_group", Args(("op", "fold"), ("group", "Band")))["groups"]![0]!["folded"]!.GetValue<bool>(), Is.True);
            Assert.That(Call("machine_group", Args(("op", "unfold"), ("group", "Band")))["groups"]![0]!["folded"]!.GetValue<bool>(), Is.False);
            Assert.That(Call("machine_group", Args(("op", "rename"), ("group", "Band"), ("new_name", "Rhythm")))["groups"]![0]!["group"]!.GetValue<string>(), Is.EqualTo("Rhythm"));
            var out_ = Call("machine_group", Args(("op", "remove"), ("machines", new JsonArray("Pad"))));
            Assert.That(out_["groups"]![0]!["machines"]!.AsArray(), Has.Count.EqualTo(1));
            Assert.That(Call("machine_group", Args(("op", "delete"), ("group", "Rhythm")))["groups"]!.AsArray(), Is.Empty);
            core.SongCore.ActionStack.Undo();
            Assert.That(Call("machine_group", Args(("op", "list")))["groups"]!.AsArray(), Has.Count.EqualTo(1), "undo brings the group back");
            Assert.That(Err("machine_group", Args(("op", "fold"), ("group", "Nope"))), Does.Contain("no group \"Nope\"").And.Contain("Rhythm"));
            Assert.That(Err("machine_group", Args(("op", "create"), ("machines", new JsonArray("Master")))), Does.Contain("Master"));
        }

        [AvaloniaTest]
        public void TheMasterIsRecordedToAWavFileAndNeverOverwritten()
        {
            var window = Song(out var restore);
            using var _ = restore;
            string dir = Path.Combine(Path.GetTempPath(), "coverage-k3b-" + Guid.NewGuid().ToString("N")[..8]);
            var probe = ReBuzz.Studio.Shell.Tools.RecorderTools.OutputProbe;
            using var put = Studio.AfterTest(() =>
            {
                ReBuzz.Studio.Shell.Tools.RecorderTools.OutputProbe = probe;
                try { Directory.Delete(dir, true); } catch (IOException) { }
            });
            string path = Path.Combine(dir, "take.wav");

            // The headless studio has no audio output: the refusal says what to do instead.
            var refused = Err("master_record", Args(("op", "start"), ("path", path)));
            Assert.That(refused, Does.Contain("No audio output").And.Contain("export_audio"));
            Assert.That(Err("master_record", Args(("op", "stop"))), Does.Contain("Not recording"));

            ReBuzz.Studio.Shell.Tools.RecorderTools.OutputProbe = _ => true;
            Assert.That(Call("master_record", Args(("op", "status")))["recording"]!.GetValue<bool>(), Is.False);
            var started = Call("master_record", Args(("op", "start"), ("path", path), ("bits", "16")));
            Assert.That(started["recording"]!.GetValue<bool>(), Is.True);
            Assert.That(File.Exists(path), Is.True);
            Assert.That(Err("master_record", Args(("op", "start"), ("path", path))), Does.Contain("Already recording"));
            var stopped = Call("master_record", Args(("op", "stop")));
            Assert.That(stopped["recording"]!.GetValue<bool>(), Is.False);
            Assert.That(new FileInfo(path).Length, Is.GreaterThanOrEqualTo(44), "a WAV header at least");
            Assert.That(Err("master_record", Args(("op", "start"), ("path", path))), Does.Contain("exists").And.Contain("overwrite=true"));
            Call("master_record", Args(("op", "start"), ("path", path), ("overwrite", true)));
            Call("master_record", Args(("op", "stop")));
            Assert.That(Err("master_record", Args(("op", "start"), ("path", Path.Combine(dir, "x.mp3")))), Does.Contain(".wav"));
        }

        [AvaloniaTest]
        public void LatencyAttributesAndCommandsAreReadAndLatencyIsOverriddenAndUndone()
        {
            var window = Song(out var restore);
            using var _ = restore;
            var core = Host.Core;
            var bass = core.SongCore.MachinesList.Single(m => m.Name == "Bass");

            int steps = Steps;
            var set = Call("machine_latency", Args(("machine", "Bass"), ("override_samples", 96)));
            Assert.That(Steps, Is.EqualTo(steps + 1));
            Assert.That(bass.OverrideLatency, Is.EqualTo(96));
            Assert.That(set["machines"]![0]!["override_samples"]!.GetValue<int>(), Is.EqualTo(96));
            core.SongCore.ActionStack.Undo();
            Assert.That(bass.OverrideLatency, Is.EqualTo(-1));
            core.SongCore.ActionStack.Redo();
            Call("machine_latency", Args(("clear_all", true)));
            Assert.That(bass.OverrideLatency, Is.EqualTo(-1));
            Assert.That(Err("machine_latency", Args(("machine", "Bass"), ("clear", true))), Does.Contain("no override"));
            Assert.That(Err("machine_latency", Args(("override_samples", 10))), Does.Contain("Name the machine"));

            var attributes = Call("machine_attributes", Args(("machine", "Bass")));
            Assert.That(attributes["attributes"]!.AsArray(), Is.Empty, "a managed machine has none");
            Assert.That(Err("machine_attributes", Args(("machine", "Bass"), ("set", new JsonObject { ["Smooth"] = 1 }))), Does.Contain("no attribute \"Smooth\""));

            Call("machine_add", Args(("gear", "Neo LFO"), ("name", "Wobble"), ("connect_to", "none")));
            var commands = Call("machine_command", Args(("machine", "Wobble")));
            Assert.That(commands["commands"]!.AsArray().Select(c => c!["command"]!.GetValue<string>()), Has.Some.Contain("About"));
            Assert.That(Err("machine_command", Args(("machine", "Wobble"), ("command", "Self Destruct"))), Does.Contain("About"));
        }

        // ------------------------------------------------------------------ CU4 (clips.md §8): one layer over the store

        static JsonArray Notes(string machine, string clip, string input = null) =>
            Call("notes_read", input == null ? Args(("machine", machine), ("clip", clip)) : Args(("machine", machine), ("clip", clip), ("input", input)))["notes"]!.AsArray();

        static JsonArray Note(int pitch, double start) => new(new JsonObject { ["pitch"] = pitch, ["start"] = start, ["length"] = 1 });

        [AvaloniaTest]
        public void ClipGetReadsALanesNotesAndViewAndTheNoteAndAutomationToolsSeeEveryLane()
        {
            var window = Song(out var restore);
            using var _ = restore;
            var core = Host.Core;
            var bass = core.SongCore.MachinesList.Single(m => m.Name == "Bass");
            var verse = bass.Clips.Single(p => p.Name == "Verse");

            // Notes: one call is one undo step, on either route; both land in the store the clip plays.
            int steps = Steps;
            Call("notes_write", Args(("machine", "Bass"), ("clip", "Verse"), ("notes", Note(60, 0))));
            Assert.That(Steps, Is.EqualTo(steps + 1), "a MIDI note: one undo step");
            Call("notes_write", Args(("machine", "Bass"), ("clip", "Verse"), ("notes", Note(64, 1)), ("input", "tracks")));
            Assert.That(Steps, Is.EqualTo(steps + 2), "a track note: one undo step");
            Assert.That(Notes("Bass", "Verse"), Has.Count.EqualTo(1), "the MIDI route reads its Notes lane");
            Assert.That(Notes("Bass", "Verse", "tracks"), Has.Count.EqualTo(1));

            // A lane another writer made (a take's, marked) for the same Note parameter: the tracks route reads both lanes.
            var noteParameter = bass.ParameterGroups[2].Parameters.First(q => q.Type == BuzzGUI.Interfaces.ParameterType.Note);
            var key = new ColumnKey(2, noteParameter.IndexInGroup, 1);
            var marked = SessionLayout.ColumnFor(verse, key, create: true);
            Assert.That(marked, Is.Not.Null);
            marked.SetEvents(new[] { new BuzzGUI.Interfaces.ClipEvent(2 * 240 * 4 / 4 * 2, BuzzGUI.Common.BuzzNote.FromMIDINote(67), 0) }, true);
            var tracks = Notes("Bass", "Verse", "tracks");
            Assert.That(tracks.Select(n => n!["pitch"]!.GetValue<int>()).OrderBy(x => x), Is.EqualTo(new[] { 64, 67 }), "every lane of the Note parameter");

            // clip_get: the lanes with who made them, the notes, the view.
            var got = Call("clip_get", Args(("machine", "Bass"), ("clip", "Verse")));
            Assert.That(got["input"]!.GetValue<string>(), Is.EqualTo("midi"));
            Assert.That(got["notes"]!.AsArray(), Has.Count.EqualTo(1));
            var lanes = got["lanes"]!.AsArray();
            Assert.That(lanes.Any(l => l!["kind"]!.GetValue<string>() == "notes" && l["events"]!.GetValue<int>() == 1), Is.True, "the Notes lane");
            var noteLanes = lanes.Where(l => l!["param"]?.GetValue<string>() == noteParameter.Name).ToList();
            Assert.That(noteLanes.Select(l => l!["source"]!.GetValue<string>()).OrderBy(x => x), Is.EqualTo(new[] { "automation", "clip" }), "the tracker's lane and the take's");
            Assert.That(got["view"]!["placements"]!.GetValue<int>(), Is.EqualTo(0));

            // The view: a lane's rows and mute are kept with the lane.
            var lane = StoreLanes.All(verse, key).First();
            lane.Metadata[BuzzGUI.Common.ClipMeta.Grid] = "rpb=6";
            lane.Metadata[BuzzGUI.Common.ClipMeta.Mute] = "1";
            var view = Call("clip_get", Args(("machine", "Bass"), ("clip", "Verse")))["view"]!;
            Assert.That(view["rows_per_beat"]!.GetValue<int>(), Is.EqualTo(6));
            Assert.That(view["muted_lanes"]!.GetValue<int>(), Is.EqualTo(1));

            // Automation: a parameter written by the agent (marked lane) and the tracker's lane (unmarked) read as one entry.
            var param = SongModel.Params(bass).First(q => !q.IsTrack && q.Parameter.MaxValue - q.Parameter.MinValue > 20);
            int lo = param.Parameter.MinValue, hi = param.Parameter.MaxValue;
            Call("automation_write", Args(("machine", "Bass"), ("clip", "Verse"), ("param", param.Parameter.Name), ("points", new JsonArray(
                new JsonObject { ["at"] = 0, ["value"] = $"raw:{lo}" }, new JsonObject { ["at"] = 2, ["value"] = $"raw:{hi}" }))));
            var pkey = new ColumnKey(param.Group, param.Parameter.IndexInGroup, 0);
            Assert.That(StoreLanes.Home(verse, pkey, create: false), Is.Not.Null);
            verse.InsertColumn(verse.Columns.Count, param.Parameter, 0);
            var unmarked = StoreLanes.All(verse, pkey).First(c => !StoreLanes.IsMarked(c));
            unmarked.SetEvents(new[] { new BuzzGUI.Interfaces.ClipEvent(3 * 240, lo, 0) }, true);
            var read = Call("automation_read", Args(("machine", "Bass"), ("clip", "Verse"), ("param", param.Parameter.Name)))["automation"]!.AsArray();
            Assert.That(read, Has.Count.EqualTo(1), "one entry for the parameter");
            Assert.That(read[0]!["source"]!.GetValue<string>(), Is.EqualTo("both"));
            Assert.That(read[0]!["events"]!.GetValue<int>(), Is.GreaterThanOrEqualTo(3), "the events of both lanes");

            Assert.That(Err("clip_get", Args(("machine", "Bass"))), Does.Contain("Say which clip"));
            window.UpdateLayout();
        }

        [AvaloniaTest]
        public void ACloneWithClipsPlaysTheOriginalsDataAndASavedClipReopensWithEveryLane()
        {
            var window = Song(out var restore);
            using var _ = restore;
            var core = Host.Core;
            Call("notes_write", Args(("machine", "Bass"), ("clip", "Verse"), ("notes", Note(60, 0))));
            Call("notes_write", Args(("machine", "Bass"), ("clip", "Verse"), ("notes", Note(64, 2)), ("input", "tracks")));
            var bass = core.SongCore.MachinesList.Single(m => m.Name == "Bass");
            var param = SongModel.Params(bass).First(q => !q.IsTrack && q.Parameter.MaxValue - q.Parameter.MinValue > 20);
            Call("automation_write", Args(("machine", "Bass"), ("clip", "Verse"), ("param", param.Parameter.Name), ("points", new JsonArray(
                new JsonObject { ["at"] = 0, ["value"] = $"raw:{param.Parameter.MinValue}" }, new JsonObject { ["at"] = 2, ["value"] = $"raw:{param.Parameter.MaxValue}" }))));
            var original = Call("clip_get", Args(("machine", "Bass"), ("clip", "Verse")));

            // Clone with clips (what Copy then Paste does): the copy's clip has the same lanes and notes, so it plays them.
            var cloned = Call("machine_clone", Args(("machines", new JsonArray("Bass")), ("with_clips", true)));
            string copy = cloned["copies"]![0]!["name"]!.GetValue<string>();
            var copied = Call("clip_get", Args(("machine", copy), ("clip", "Verse")));
            Assert.That(copied["notes"]!.ToJsonString(), Is.EqualTo(original["notes"]!.ToJsonString()), "the MIDI notes");
            Assert.That(Notes(copy, "Verse", "tracks").ToJsonString(), Is.EqualTo(Notes("Bass", "Verse", "tracks").ToJsonString()), "the track notes");
            Assert.That(copied["lanes"]!.ToJsonString(), Is.EqualTo(original["lanes"]!.ToJsonString()), "every lane, with its events");

            // Save and open again: every lane of both clips is as it was.
            string path = NeoBuzz.TestSupport.TestTemp.Path($"cu4-lanes-{Environment.ProcessId}.nbl");
            try
            {
                var before = (Call("clip_get", Args(("machine", "Bass"), ("clip", "Verse"))).ToJsonString(), Call("clip_get", Args(("machine", copy), ("clip", "Verse"))).ToJsonString());
                core.SaveSongCopy(path);
                core.Modified = false;
                Assert.That(core.TryOpenSongFile(path), Is.True);
                Settle(window);
                var after = (Call("clip_get", Args(("machine", "Bass"), ("clip", "Verse"))).ToJsonString(), Call("clip_get", Args(("machine", copy), ("clip", "Verse"))).ToJsonString());
                Assert.That(after, Is.EqualTo(before), "saved and opened: every lane, its events and its view");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}
