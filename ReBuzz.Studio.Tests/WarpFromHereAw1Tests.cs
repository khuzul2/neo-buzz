// Fork (docs/specs/neo-audio.md, AW1 as built): "Warp from here" in the waveform's context menu (WM1) follows the tempo changes of the audio after the clicked moment
// (auto-warp from there: the markers before stay). In the Session and the Arrangement: value, one undo step, redo, saved and opened again; the agent's
// audio_warp_markers(warp_follow) and audio_clip_set(auto_warp, from_seconds) against the page.

using System;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core.Clips;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Views.Audio;
using ReBuzz.SessionTests.AutoWarp;
using static ReBuzz.Studio.Tests.NeoAudioClipPageTests;

namespace ReBuzz.Studio.Tests
{
    public class WarpFromHereAw1Tests
    {
        sealed class Off : IDisposable
        {
            readonly bool was = AutoWarpPolicy.Enabled;
            public Off() { AutoWarpPolicy.Enabled = false; }
            public void Dispose() { AutoWarpPolicy.Enabled = was; }
        }

        static string Drift()
        {
            var line = WarpSignals.Make(WarpSignals.Tempo.Drift, WarpSignals.Style.Mix, 48);
            var stereo = new float[line.Audio.Length * 2];
            for (int i = 0; i < line.Audio.Length; i++) stereo[2 * i] = stereo[2 * i + 1] = line.Audio[i];
            string file = Work("from-here-drift.wav");
            WriteWav(file, line.Rate, stereo);
            return file;
        }

        static SessionDocument.AudioSettings AudioOf(IPattern p) => Layout.AudioOf(p)!;

        /// <summary>A marker at 5 s (so there is something before the point that has to stay) on a clip that is straight otherwise.</summary>
        static void SeedAt5s(Rig rig, IPattern clip)
        {
            var src = Layout.AudioSourceOf(clip)!;
            var a = AudioOf(clip);
            var m = WarpMath.Effective(a.Markers, src.Length, src.SampleRate, a.Bpm);
            var r = WarpMath.Add(m, 5 * src.SampleRate);
            Assert.That(r.Ok, Is.True, r.Error);
            Assert.That(Layout.EditAudio(clip, new AudioClipEdit { Markers = r.Markers }, rig.Editor, "seed"), Is.Null);
            Settle();
        }

        /// <summary>The transient nearest a time: "here" for the menu (it lands on a transient when one is near) and the sample the agent is given.</summary>
        static double TransientNear(IPattern clip, double seconds)
        {
            double rate = Layout.AudioSourceOf(clip)!.SampleRate;
            return Layout.TransientsOf(clip).OrderBy(t => Math.Abs(t - seconds * rate)).First();
        }

        static void Open(Rig rig, IPattern clip, AudioClipPage page)
        {
            Settle();
            rig.WaitPeaks(clip);
            page.Wave.SetZoom(30);
            Settle();
        }

        [AvaloniaTest]
        public void TheMenuHasBothWarpFromHereItemsTogether()
        {
            using var off = new Off();
            using var rig = new Rig(Drift());
            rig.SelectInSession();
            var page = rig.OpenPage();
            Open(rig, rig.SessionClip, page);
            var wave = page.Wave;
            rig.Click(rig.In(wave, 0, 0.6) + new Vector(wave.X(2.5), 0), MouseButton.Right);
            var headers = wave.LastMenu!.Items.OfType<MenuItem>().Select(i => (string)i.Header!).ToList();
            int follow = headers.IndexOf("Warp from here"), straight = headers.IndexOf("Warp from here, straight");
            Assert.That(follow, Is.GreaterThanOrEqualTo(0), "Warp from here");
            Assert.That(straight, Is.EqualTo(follow + 1), "next to Warp from here, straight");
            Assert.That(wave.LastMenu.Items.OfType<MenuItem>().ElementAt(follow).IsEnabled, Is.True);
            wave.LastMenu.Close();
        }

        [AvaloniaTest]
        public void WarpFromHereFollowsTheTempoAfterThePointKeepsTheMarkersBeforeInTheSessionAndTheArrangement()
        {
            using var off = new Off();
            foreach (bool arranged in new[] { false, true })
            {
                using var rig = new Rig(Drift());
                if (arranged) rig.SelectInArrangement(); else rig.SelectInSession();
                var page = rig.OpenPage();
                var clip = arranged ? rig.ArrangedClip : rig.SessionClip;
                Open(rig, clip, page);
                SeedAt5s(rig, clip);
                var wave = page.Wave;
                double rate = Layout.AudioSourceOf(clip)!.SampleRate;
                double at = TransientNear(clip, 20);
                var before = AudioOf(clip).Markers.OrderBy(m => m.Beat).ToList();
                double beat = WarpMath.BeatAt(WarpMath.Effective(before, Layout.AudioSourceOf(clip)!.Length, rate, AudioOf(clip).Bpm), at);
                wave.ScrollTo(beat - 6);
                Settle();
                rig.Click(rig.In(wave, 0, 0.6) + new Vector(wave.X(beat), 0), MouseButton.Right);
                var item = wave.LastMenu!.Items.OfType<MenuItem>().First(i => (string)i.Header! == "Warp from here");
                Assert.That(item.IsEnabled, Is.True);
                Steps(rig, clip, () =>
                {
                    item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                    wave.LastMenu.Close();
                    Settle();
                }, save: true, check: _ =>
                {
                    var m = AudioOf(clip).Markers.OrderBy(x => x.Beat).ToList();
                    Assert.That(m.Count(x => x.Sample > at + rate), Is.GreaterThan(5), "markers on the bars after the point (the audio drifts)");
                    Assert.That(m.Where(x => x.Sample < 5 * rate + 1).Select(x => x.Sample), Is.EqualTo(before.Where(x => x.Sample < 5 * rate + 1).Select(x => x.Sample)).Within(0.5),
                        "the markers before it stay");
                    for (int i = 1; i < m.Count; i++) { Assert.That(m[i].Beat, Is.GreaterThan(m[i - 1].Beat)); Assert.That(m[i].Sample, Is.GreaterThan(m[i - 1].Sample)); }
                });
            }
        }

        [AvaloniaTest]
        public void TheAgentsWarpFollowAndAudioClipSetFromSecondsMakeWhatThePageMakes()
        {
            using var off = new Off();
            using var rig = new Rig(Drift());
            rig.SelectInSession();
            var page = rig.OpenPage();
            var pageClip = rig.SessionClip;
            var agentClip = rig.ArrangedClip;           // the same audio, edited by the agent
            var viaSet = rig.ArrangedClip;
            Open(rig, pageClip, page);
            foreach (var c in new[] { pageClip, agentClip }) SeedAt5s(rig, c);
            var wave = page.Wave;
            double rate = Layout.AudioSourceOf(pageClip)!.SampleRate;
            double at = TransientNear(pageClip, 20);
            double beat = WarpMath.BeatAt(WarpMath.Effective(AudioOf(pageClip).Markers, Layout.AudioSourceOf(pageClip)!.Length, rate, AudioOf(pageClip).Bpm), at);
            wave.ScrollTo(beat - 6);
            Settle();
            rig.Click(rig.In(wave, 0, 0.6) + new Vector(wave.X(beat), 0), MouseButton.Right);
            wave.LastMenu!.Items.OfType<MenuItem>().First(i => (string)i.Header! == "Warp from here").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            wave.LastMenu.Close();
            Settle();
            var byPage = AudioOf(pageClip).Markers.OrderBy(m => m.Beat).ToList();
            Assert.That(byPage.Count, Is.GreaterThan(5));

            string before = State("B", agentClip.Name);
            var r = Ok(Tool("audio_warp_markers", new JsonObject { ["machine"] = "B", ["pattern"] = agentClip.Name, ["action"] = "warp_follow", ["sample"] = at }));
            Assert.That(r["count"]!.GetValue<int>(), Is.GreaterThan(5), "it says how many markers it made");
            var byAgent = AudioOf(agentClip).Markers.OrderBy(m => m.Beat).ToList();
            Assert.That(byAgent.Count, Is.EqualTo(byPage.Count));
            Assert.That(byAgent.Zip(byPage, (a, b) => Math.Abs(a.Sample - b.Sample) + Math.Abs(a.Beat - b.Beat)).Max(), Is.LessThan(1.0), "the agent and the page make the same markers");
            string after = State("B", agentClip.Name);
            rig.Undo();
            Assert.That(State("B", agentClip.Name), Is.EqualTo(before), "one undo takes the agent's edit back");
            rig.Redo();
            Assert.That(State("B", agentClip.Name), Is.EqualTo(after));

            // audio_clip_set(auto_warp, from_seconds) is the same edit.
            rig.Undo();
            Ok(Tool("audio_clip_set", new JsonObject { ["machine"] = "B", ["pattern"] = viaSet.Name, ["auto_warp"] = true, ["from_seconds"] = at / rate }));
            Assert.That(State("B", agentClip.Name), Is.EqualTo(after), "audio_clip_set with from_seconds gives the same clip");

            // A call with no position is refused and changes nothing.
            string state = State("B", agentClip.Name);
            Assert.That(Tool("audio_warp_markers", new JsonObject { ["machine"] = "B", ["pattern"] = agentClip.Name, ["action"] = "warp_follow" })["isError"]?.GetValue<bool>() ?? false, Is.True);
            Assert.That(State("B", agentClip.Name), Is.EqualTo(state));
        }
    }
}
