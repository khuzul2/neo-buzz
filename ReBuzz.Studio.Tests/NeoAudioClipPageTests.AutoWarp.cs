// Fork addition (neo-buzz, docs/specs/neo-audio.md, AW1 as built): auto-warp at import, on the Audio page and from the agent.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia.Headless.NUnit;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core.Clips;
using ReBuzz.SessionTests.AutoWarp;

namespace ReBuzz.Studio.Tests
{
    public partial class NeoAudioClipPageTests
    {
        static string WarpFile(string name, WarpSignals.Tempo tempo, double seconds)
        {
            var line = WarpSignals.Make(tempo, WarpSignals.Style.Mix, seconds);
            var stereo = new float[line.Audio.Length * 2];
            for (int i = 0; i < line.Audio.Length; i++) stereo[2 * i] = stereo[2 * i + 1] = line.Audio[i];
            string file = Work(name);
            WriteWav(file, line.Rate, stereo);
            return file;
        }

        sealed class WarpPolicyScope : IDisposable
        {
            readonly bool was = AutoWarpPolicy.Enabled;
            public WarpPolicyScope(bool on) { AutoWarpPolicy.Enabled = on; }
            public void Dispose() { AutoWarpPolicy.Enabled = was; }
        }

        [AvaloniaTest]
        public void ImportAutoWarpsALongDriftingFileLeavesASteadyLoopStraightAndKeepsAShortFileSteady()
        {
            using var on = new WarpPolicyScope(true);
            using var rig = new Rig();
            int track = Layout.FirstTrackOf(rig.A);

            var drifting = Layout.ImportAudio(track, 1, WarpFile("drift.wav", WarpSignals.Tempo.Drift, 48), out string error);
            Assert.That(drifting, Is.Not.Null, error);
            Assert.That(Audio(drifting).Markers.Count, Is.GreaterThan(4), "a marker on the bars of a drifting file");

            var loop = Layout.ImportAudio(track, 2, WarpFile("loop.wav", WarpSignals.Tempo.Steady, 32), out error);
            Assert.That(loop, Is.Not.Null, error);
            Assert.That(Audio(loop).Markers, Is.Empty, "a steady loop stays straight");
            Assert.That(Audio(loop).Bpm, Is.GreaterThan(0));

            var shortFile = Layout.ImportAudio(track, 3, WarpFile("short.wav", WarpSignals.Tempo.Drift, 10), out error);
            Assert.That(shortFile, Is.Not.Null, error);
            Assert.That(Audio(shortFile).Markers, Is.Empty, "eight bars or fewer keep today's single tempo");
        }

        [AvaloniaTest]
        public void TheImportPreferenceTurnsAutoWarpOff()
        {
            using var off = new WarpPolicyScope(false);
            using var rig = new Rig();
            var clip = Layout.ImportAudio(Layout.FirstTrackOf(rig.A), 1, WarpFile("drift-off.wav", WarpSignals.Tempo.Drift, 48), out string error);
            Assert.That(clip, Is.Not.Null, error);
            Assert.That(Audio(clip).Markers, Is.Empty, "with the preference off the import is as before");
        }

        [AvaloniaTest]
        public void TheAutoWarpButtonReplacesTheMarkersInOneUndoStepAndTheSongKeepsThemWithoutAnalysingAgain()
        {
            using var off = new WarpPolicyScope(false);
            using var rig = new Rig(WarpFile("button-drift.wav", WarpSignals.Tempo.Drift, 48));
            rig.SelectInSession();
            var page = rig.OpenPage();
            var clip = rig.SessionClip;
            Assert.That(Audio(clip).Markers, Is.Empty);
            string after = Steps(rig, clip, () => rig.Click(rig.Of(page.DetectButton)), save: false,
                check: _ => Assert.That(Audio(clip).Markers.Count, Is.GreaterThan(4), "Auto-warp placed markers"));
            int runs = BeatTracker.Runs;
            Assert.That(Saved("A", clip.Name), Is.EqualTo(after), "saved and opened again");
            Assert.That(BeatTracker.Runs, Is.EqualTo(runs), "nothing is analysed on open");
        }

        [AvaloniaTest]
        public void TheAgentsAutoWarpAndThePagesButtonReachTheSameMarkers()
        {
            using var off = new WarpPolicyScope(false);
            using var rig = new Rig(WarpFile("agent-drift.wav", WarpSignals.Tempo.Drift, 48));
            rig.SelectInSession();
            var page = rig.OpenPage();
            rig.Click(rig.Of(page.DetectButton));
            var byPage = Audio(rig.SessionClip);
            Ok(Tool("audio_clip_set", new JsonObject { ["machine"] = "B", ["pattern"] = rig.ArrangedClip.Name, ["auto_warp"] = true }));
            var byAgent = Audio(rig.ArrangedClip);
            Assert.That(byAgent.Markers.Count, Is.EqualTo(byPage.Markers.Count));
            Assert.That(byAgent.Bpm, Is.EqualTo(byPage.Bpm).Within(1e-9));
            Assert.That(byAgent.Markers.Zip(byPage.Markers, (a, b) => Math.Abs(a.Sample - b.Sample)).Max(), Is.LessThan(1.0));
        }

        [AvaloniaTest]
        public void TheAgentsFromSecondsKeepsTheEarlierMarkersAndAClipWithoutBeatsIsRefused()
        {
            using var off = new WarpPolicyScope(false);
            using var rig = new Rig(WarpFile("from-drift.wav", WarpSignals.Tempo.Drift, 48));
            var clip = rig.SessionClip;
            Ok(Tool("audio_clip_set", new JsonObject { ["machine"] = "A", ["pattern"] = clip.Name, ["auto_warp"] = true }));
            var all = Audio(clip).Markers.ToList();
            double rate = Layout.ClipAudioOf(Audio(clip)).Source.SampleRate;
            Ok(Tool("audio_clip_set", new JsonObject { ["machine"] = "A", ["pattern"] = clip.Name, ["auto_warp"] = true, ["from_seconds"] = 20 }));
            var from = Audio(clip).Markers;
            Assert.That(from.Count(m => m.Sample < 20 * rate), Is.EqualTo(all.Count(m => m.Sample < 20 * rate)), "the markers before 20 s are kept");

            // The tones file has no pulse: the agent is told, and nothing changes.
            string tones = NeoAudioArrangementTests.Tones("no-beats.wav");
            var other = Layout.ImportAudio(Layout.FirstTrackOf(rig.B), 2, tones, out string error);
            Assert.That(other, Is.Not.Null, error);
            string before = State("B", other.Name);
            var refused = Tool("audio_clip_set", new JsonObject { ["machine"] = "B", ["pattern"] = other.Name, ["auto_warp"] = true });
            Assert.That(refused["isError"]?.GetValue<bool>() ?? false, Is.True, "no beats found is an error the agent can read");
            Assert.That(State("B", other.Name), Is.EqualTo(before));
        }
    }
}
