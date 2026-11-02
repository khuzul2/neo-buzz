using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzzTests.Automation;
using Rig = ReBuzzTests.AgentServerTests.Rig;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (docs/specs/mcp-server.md §9, A7): playing live with the user in studio mode.
    /// The transport, scenes launched on the bar, live meters while the song plays, phrases played
    /// into an armed track and recorded into its take, analysis machines' readings, and headless
    /// mode pointing to render. The tests pump the audio as the device would.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class AgentA7Tests
    {
        static JsonObject Ok(Rig rig, string tool, JsonObject args = null)
        {
            var r = rig.Call(tool, args);
            Assert.That(r.Error, Is.False, $"{tool}: {r.Text}");
            TestContext.Out.WriteLine($"{tool}: {r.Text}");
            return r.Data;
        }

        static string Fails(Rig rig, string tool, JsonObject args = null)
        {
            var r = rig.Call(tool, args);
            Assert.That(r.Error, Is.True, $"{tool} should fail: {r.Text}");
            TestContext.Out.WriteLine($"{tool} (fails): {r.Text}");
            return r.Text;
        }

        /// <summary>Renders tick by tick (one engine, as the device), with the agent's phrases polled after each.</summary>
        sealed class Pump
        {
            readonly Rig rig;
            readonly Driver.AudioReader reader;

            public Pump(Rig rig)
            {
                this.rig = rig;
                reader = rig.Driver.CreateAudioReader(256);
            }

            public SessionCore Session => rig.Driver.Core.SessionCore;

            public void Ticks(int ticks)
            {
                for (int i = 0; i < ticks; i++)
                {
                    long clock = Session.Clock;
                    int guard = 0;
                    while (Session.Clock == clock && guard++ < 100000)
                    {
                        var mi = ReBuzzCore.masterInfo;
                        reader.Read(Math.Clamp(mi.SamplesPerTick - mi.PosInTick, 1, 256));
                        if (!rig.Driver.Core.PlayRequested) return;
                    }
                    rig.Host.Player.Poll();
                }
            }

            /// <summary>Until the session clock reaches <paramref name="clock"/> (a tick boundary).</summary>
            public void To(long clock)
            {
                int guard = 0;
                while (Session.Clock < clock && guard++ < 100000) Ticks(1);
            }
        }

        static void Song(Rig rig, params string[] machines)
        {
            Ok(rig, "song_set", new JsonObject { ["tempo_bpm"] = 120 });
            foreach (var m in machines)
            {
                Ok(rig, "machine_add", new JsonObject { ["gear"] = "Neo Poly", ["name"] = m });
                foreach (var p in new[] { "A", "B" })
                {
                    Ok(rig, "pattern_create", new JsonObject { ["machine"] = m, ["name"] = p, ["length_beats"] = 4 });
                    Ok(rig, "notes_write", new JsonObject
                    {
                        ["machine"] = m, ["pattern"] = p,
                        ["notes"] = new JsonArray(new JsonObject { ["pitch"] = p == "A" ? "C3" : "G3", ["start"] = 0, ["length"] = 4 }),
                    });
                }
            }
        }

        [Test]
        public void HeadlessPointsToRender()
        {
            using var rig = new Rig(NeoMachineTests.Poly);
            Song(rig, "Keys");
            Assert.That(Fails(rig, "transport", new JsonObject { ["action"] = "play" }), Does.Contain("render"));
            Assert.That(Ok(rig, "transport", new JsonObject { ["action"] = "status" })["playing"]!.GetValue<bool>(), Is.False, "status answers anywhere");
            Assert.That(Fails(rig, "session_launch", new JsonObject { ["scene"] = 1 }), Does.Contain("arrangement_place"));
            Assert.That(Fails(rig, "meters_read"), Does.Contain("analyze"));
            Assert.That(Fails(rig, "midi_play", new JsonObject { ["machine"] = "Keys", ["notes"] = new JsonArray() }), Does.Contain("notes_write"));
            Assert.That(Fails(rig, "session_record", new JsonObject { ["action"] = "status" }), Does.Contain("notes_write"));
            Assert.That(Fails(rig, "machine_readings", new JsonObject { ["machine"] = "Keys" }), Does.Contain("Neo Analyzer"), "a synth shows no readings");
        }

        [Test]
        public void TransportPlaysSeeksAndStops()
        {
            using var rig = new Rig("studio", NeoMachineTests.Poly);
            Song(rig, "Keys");
            Ok(rig, "arrangement_place", new JsonObject { ["machine"] = "Keys", ["pattern"] = "A", ["at"] = new JsonArray(0, 4, 8) });
            var pump = new Pump(rig);

            var s = Ok(rig, "transport", new JsonObject { ["action"] = "play", ["at"] = "2.1" });
            Assert.That(s["playing"]!.GetValue<bool>(), Is.True);
            Assert.That(s["position"]!.GetValue<string>(), Is.EqualTo("2.1"));
            pump.Ticks(8);
            s = Ok(rig, "transport", new JsonObject { ["action"] = "status" });
            Assert.That(s["position_beats"]!.GetValue<double>(), Is.GreaterThan(5), "the song moved on from beat 4");

            s = Ok(rig, "transport", new JsonObject { ["action"] = "seek", ["at"] = 0, ["loop"] = true });
            Assert.That(s["position"]!.GetValue<string>(), Is.EqualTo("1.1"), "where the seek goes (it lands on the next tick)");
            pump.Ticks(2);
            Assert.That(Ok(rig, "transport", new JsonObject { ["action"] = "status" })["position_beats"]!.GetValue<double>(), Is.LessThan(1), "and it did");
            Assert.That(s["looping"]!.GetValue<bool>(), Is.True);
            Assert.That(Fails(rig, "transport", new JsonObject { ["action"] = "seek" }), Does.Contain("at"));
            Assert.That(Fails(rig, "transport", new JsonObject { ["action"] = "play", ["at"] = "99.1" }), Does.Contain("song end"));

            s = Ok(rig, "transport", new JsonObject { ["action"] = "stop" });
            Assert.That(s["playing"]!.GetValue<bool>(), Is.False);
            Assert.That(rig.Driver.Core.Playing, Is.False);
        }

        [Test]
        public void ScenesLaunchOnTheBar()
        {
            using var rig = new Rig("studio", NeoMachineTests.Poly);
            Song(rig, "Bass", "Keys");
            Ok(rig, "session_clip_set", new JsonObject { ["machine"] = "Bass", ["scene"] = 1, ["pattern"] = "A" });
            Ok(rig, "session_clip_set", new JsonObject { ["machine"] = "Keys", ["scene"] = 1, ["pattern"] = "A" });
            Ok(rig, "session_clip_set", new JsonObject { ["machine"] = "Bass", ["scene"] = 2, ["pattern"] = "B" });
            Ok(rig, "session_clip_set", new JsonObject { ["machine"] = "Keys", ["scene"] = 2, ["pattern"] = "B" });
            var pump = new Pump(rig);
            int bar = 4 * rig.Driver.Core.TPB;

            var s = Ok(rig, "session_launch", new JsonObject { ["scene"] = 1 });
            Assert.That(s["playing"]!.GetValue<bool>(), Is.True, "launching starts the transport");
            pump.Ticks(2);
            string Playing(string machine) =>
                Ok(rig, "transport", new JsonObject { ["action"] = "status" })["session"]!.AsArray().FirstOrDefault(t => t!["machine"]!.GetValue<string>() == machine)?["playing"]?.GetValue<string>();
            Assert.That(Playing("Bass"), Is.EqualTo("A"));
            Assert.That(Playing("Keys"), Is.EqualTo("A"));

            pump.To(bar / 2 + 1);                                   // halfway into the first bar
            s = Ok(rig, "session_launch", new JsonObject { ["scene"] = 2 });
            var bass = s["session"]!.AsArray().First(t => t!["machine"]!.GetValue<string>() == "Bass")!;
            Assert.That(bass["queued"]!.GetValue<string>(), Is.EqualTo("B"), "waits for the bar");
            long origin = pump.Session.GridOrigin;
            pump.To(origin + bar - 1);
            Assert.That(Playing("Bass"), Is.EqualTo("A"), "still the first scene just before the bar");
            pump.To(origin + bar + 1);
            Assert.That(Playing("Bass"), Is.EqualTo("B"), "the second scene from the bar");
            Assert.That(Playing("Keys"), Is.EqualTo("B"));

            // One slot, then stopping.
            s = Ok(rig, "session_launch", new JsonObject { ["machine"] = "Keys", ["pattern"] = "A" });
            pump.To(origin + 2 * bar + 1);
            Assert.That(Playing("Keys"), Is.EqualTo("A"));
            Assert.That(Fails(rig, "session_launch", new JsonObject { ["machine"] = "Keys", ["scene"] = 99 }), Does.Contain("scenes"));
            s = Ok(rig, "session_launch", new JsonObject { ["stop"] = true, ["immediate"] = true });
            pump.Ticks(1);
            Assert.That(Playing("Bass"), Is.Null);
            Assert.That(Playing("Keys"), Is.Null);
            Ok(rig, "session_launch", new JsonObject { ["back_to_arrangement"] = true });
            Ok(rig, "transport", new JsonObject { ["action"] = "stop" });
        }

        [Test]
        public void MetersHearTheMasterAndAMachine()
        {
            using var rig = new Rig("studio", NeoMachineTests.Poly);
            Song(rig, "Keys", "Quiet");
            Ok(rig, "notes_write", new JsonObject { ["machine"] = "Quiet", ["pattern"] = "A", ["mode"] = "replace", ["notes"] = new JsonArray() });
            Ok(rig, "arrangement_place", new JsonObject { ["machine"] = "Keys", ["pattern"] = "A", ["at"] = new JsonArray(0, 4, 8, 12) });
            Ok(rig, "arrangement_place", new JsonObject { ["machine"] = "Quiet", ["pattern"] = "A", ["at"] = new JsonArray(0, 4, 8, 12) });
            Ok(rig, "song_set", new JsonObject { ["loop_start_beats"] = 0, ["loop_end_beats"] = 16 });
            Ok(rig, "transport", new JsonObject { ["action"] = "play", ["at"] = 0, ["loop"] = true });

            // The audio device: a thread rendering block after block, about as fast as it plays.
            using var stop = new CancellationTokenSource();
            var reader = rig.Driver.CreateAudioReader(512);
            var device = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    reader.Read(512);
                    Thread.Sleep(2);
                }
            });
            try
            {
                var r = Ok(rig, "meters_read", new JsonObject { ["machines"] = new JsonArray("Keys", "Quiet"), ["window_seconds"] = 0.5 });
                Assert.That(r["playing"]!.GetValue<bool>(), Is.True);
                var meters = r["meters"]!.AsArray().ToDictionary(m => m!["source"]!.GetValue<string>(), m => m!.AsObject());
                Assert.That(meters.Keys, Is.EquivalentTo(new[] { "Master", "Keys", "Quiet" }));
                Assert.That(meters["Master"]["silent"]!.GetValue<bool>(), Is.False, "the chord plays through the master");
                Assert.That(meters["Master"]["momentary_lufs"]!.GetValue<double>(), Is.InRange(-60.0, 0.0));
                Assert.That(meters["Master"]["seconds"]!.GetValue<double>(), Is.EqualTo(0.5).Within(0.01), "a full window");
                Assert.That(meters["Keys"]["silent"]!.GetValue<bool>(), Is.False);
                Assert.That(meters["Quiet"]["silent"]!.GetValue<bool>(), Is.True, "a machine with nothing to play reads silent");

                Assert.That(Fails(rig, "meters_read", new JsonObject { ["machines"] = new JsonArray("Nobody") }), Does.Contain("Nobody"));
            }
            finally
            {
                stop.Cancel();
                device.Wait(2000);
            }
        }

        [Test]
        public void ATakeRecordsWhatMidiPlayPlaysOnTheBar()
        {
            using var rig = new Rig("studio", NeoMachineTests.Poly);
            SessionCore.Timestamp = () => 1_000_000;          // heard = rendered: the stamps are exact
            try
            {
                Song(rig, "Keys");
                rig.Host.Player.Manual = true;
                var pump = new Pump(rig);
                int tpb = rig.Driver.Core.TPB, bar = 4 * tpb;

                var start = Ok(rig, "session_record", new JsonObject { ["action"] = "start", ["machine"] = "Keys", ["scene"] = 1, ["bars"] = 2 });
                string take = start["take"]!.GetValue<string>();
                var keys = start["state"]!["session"]!.AsArray().Single(t => t!["machine"]!.GetValue<string>() == "Keys")!;
                Assert.That(keys["armed"]!.GetValue<bool>(), Is.True, "start arms the track");
                Assert.That(keys["recording"]!.GetValue<bool>(), Is.True);
                Assert.That(rig.Driver.Core.SessionLayout.Document.RecordBars, Is.EqualTo(0), "the Session View's own setting is left as it was");

                pump.Ticks(2);
                var played = Ok(rig, "midi_play", new JsonObject
                {
                    ["machine"] = "Keys", ["start"] = "bar",
                    ["notes"] = new JsonArray(
                        new JsonObject { ["pitch"] = "C4", ["start"] = 0, ["length"] = 1, ["velocity"] = 90 },
                        new JsonObject { ["pitch"] = "E4", ["start"] = 1, ["length"] = 1 },
                        new JsonObject { ["pitch"] = "G4", ["start"] = 2, ["length"] = 1 }),
                });
                Assert.That(played["starts"]!.GetValue<string>(), Is.EqualTo("2.1"), "the next bar");
                Assert.That(played["recorded"]!.GetValue<bool>(), Is.True, "into the armed track");

                long origin = pump.Session.GridOrigin;
                pump.To(origin + 2 * bar + 1);
                rig.Driver.Core.SessionLayout.PollTakes();
                var stopped = Ok(rig, "session_record", new JsonObject { ["action"] = "stop", ["machine"] = "Keys" });
                var keysAfter = stopped["state"]!["session"]!.AsArray().FirstOrDefault(t => t!["machine"]!.GetValue<string>() == "Keys");
                Assert.That(keysAfter?["armed"], Is.Null, "stop disarms the track start armed");

                var notes = Ok(rig, "notes_read", new JsonObject { ["machine"] = "Keys", ["pattern"] = take })["notes"]!.AsArray();
                Assert.That(notes.Select(n => n!["start"]!.GetValue<double>()), Is.EqualTo(new[] { 4.0, 5.0, 6.0 }), "on the second bar's beats, as played");
                Assert.That(notes.Select(n => n!["pitch"]!.GetValue<int>()), Is.EqualTo(new[] { 60, 64, 67 }));
                Assert.That(notes[0]!["velocity"]!.GetValue<int>(), Is.EqualTo(90));
                Assert.That(notes.Select(n => n!["length"]!.GetValue<double>()), Is.All.EqualTo(1.0));
                Assert.That(rig.Host.Player.Playing, Is.EqualTo(0), "the phrase is done");
            }
            finally
            {
                SessionCore.Timestamp = System.Diagnostics.Stopwatch.GetTimestamp;
                rig.Driver.Core.Playing = false;
            }
        }

        [Test]
        public void AStoppedTransportPlaysAPhraseNowAndStopLetsGo()
        {
            using var rig = new Rig("studio", NeoMachineTests.Poly);
            Song(rig, "Keys");
            rig.Host.Player.Manual = true;
            var r = Ok(rig, "midi_play", new JsonObject
            {
                ["machine"] = "Keys", ["start"] = "bar",
                ["notes"] = new JsonArray(new JsonObject { ["pitch"] = "C4", ["start"] = 0, ["length"] = 8 }),
            });
            Assert.That(r["starts"]!.GetValue<string>(), Is.EqualTo("now"));
            Assert.That(r["recorded"]!.GetValue<bool>(), Is.False);
            rig.Host.Player.Poll();
            Assert.That(rig.Host.Player.Playing, Is.EqualTo(1), "the note is held for its 8 beats");
            Ok(rig, "midi_play", new JsonObject { ["stop"] = true });
            Assert.That(rig.Host.Player.Playing, Is.EqualTo(0));
            Assert.That(Fails(rig, "midi_play", new JsonObject { ["machine"] = "Keys", ["notes"] = new JsonArray() }), Does.Contain("none"));
            Assert.That(Fails(rig, "midi_play", new JsonObject { ["machine"] = "Master", ["notes"] = new JsonArray(new JsonObject { ["pitch"] = 60, ["start"] = 0, ["length"] = 1 }) }), Does.Contain("Master"));
        }
    }
}
