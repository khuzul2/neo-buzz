// Fork addition (neo-buzz, docs/specs/neo-audio.md, AW5 as built): auto-warp to grid through the layout and the agent (the UI waits for the approved design).

using System;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia.Headless.NUnit;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Clips;
using ReBuzz.Core.Session;
using ReBuzz.SessionTests.AutoWarp;

namespace ReBuzz.Studio.Tests
{
    public partial class NeoAudioClipPageTests
    {
        internal sealed class WarpModeScope : IDisposable
        {
            readonly AutoWarpMode was = AutoWarpPolicy.Mode;
            public WarpModeScope(AutoWarpMode mode) { AutoWarpPolicy.Mode = mode; }
            public void Dispose() { AutoWarpPolicy.Mode = was; }
        }

        [AvaloniaTest]
        public void AutoWarpToGridIsOneUndoStepMakesMoreMarkersThanTheBeatsAndTheSongKeepsThem()
        {
            using var off = new WarpModeScope(AutoWarpMode.Off);
            using var rig = new Rig(WarpFile("grid-drift.wav", WarpSignals.Tempo.Drift, 48));
            var clip = rig.SessionClip;
            Layout.AutoWarp(clip, 0, out string error);
            Assert.That(error, Is.Null);
            int beatMarkers = Audio(clip).Markers.Count;
            Assert.That(beatMarkers, Is.GreaterThan(4));
            Steps(rig, clip, () => Layout.AutoWarp(clip, 0, out _, toGrid: true), save: true,
                check: _ =>
                {
                    Assert.That(Audio(clip).Markers.Count, Is.GreaterThan(beatMarkers), "a marker on the transients too");
                    Assert.That(WarpMath.Validate(Audio(clip).Markers.ToList()), Is.Null, "markers that can be played");
                    Assert.That(Layout.LastGridReport.Made, Is.GreaterThan(0));
                });
        }

        [AvaloniaTest]
        public void TheOptionsAreSavedWithTheClipAndSettingThemMovesNoMarker()
        {
            using var off = new WarpModeScope(AutoWarpMode.Off);
            using var rig = new Rig(WarpFile("grid-options.wav", WarpSignals.Tempo.Drift, 48));
            var clip = rig.SessionClip;
            Layout.AutoWarp(clip, 0, out _);
            var before = Audio(clip).Markers.ToList();
            Assert.That((Audio(clip).AutoGrid, Audio(clip).AutoSensitivity, Audio(clip).AutoReach), Is.EqualTo(("1/16", 60, 40)), "the defaults");
            Steps(rig, clip, () => Assert.That(Layout.EditAudio(clip, new AudioClipEdit { AutoGrid = "1/8T", AutoSensitivity = 70, AutoReach = 25 }, null), Is.Null), save: true,
                check: _ => Assert.That((Audio(clip).AutoGrid, Audio(clip).AutoSensitivity, Audio(clip).AutoReach), Is.EqualTo(("1/8T", 70, 25))));
            Assert.That(Audio(clip).Markers.SequenceEqual(before), Is.True, "nothing runs until the command does");
            Assert.That(Layout.EditAudio(clip, new AudioClipEdit { AutoGrid = "1/3" }, null), Is.Not.Null, "an unknown grid is refused");
        }

        [AvaloniaTest]
        public void AutoWarpOfARangeChangesOnlyTheMarkersInsideItAndPinsItsEnds()
        {
            using var off = new WarpModeScope(AutoWarpMode.Off);
            using var rig = new Rig(WarpFile("grid-range.wav", WarpSignals.Tempo.Drift, 48));
            var clip = rig.SessionClip;
            Layout.AutoWarp(clip, 0, out _);
            var before = Audio(clip).Markers.ToList();
            double rate = Layout.AudioSourceOf(clip)!.SampleRate;
            double from = 16, to = 32;
            Steps(rig, clip, () => Assert.That(Layout.EditAudio(clip, new AudioClipEdit { AutoWarp = true, ToGrid = true, FromSeconds = from, ToSeconds = to }, null), Is.Null), save: true, check: _ =>
            {
                var after = Audio(clip).Markers.ToList();
                var src = Layout.AudioSourceOf(clip)!;
                var was = WarpMath.Effective(before, src.Length, rate, Audio(clip).Bpm);
                Assert.That(was.Where(m => m.Sample < from * rate - 20 || m.Sample > to * rate + 20).All(m => after.Any(a => a.Sample == m.Sample && a.Beat == m.Beat)), Is.True, "every marker outside the range is still there");
                Assert.That(after.Where(m => (m.Sample < from * rate - 20 || m.Sample > to * rate + 20) && m.Sample > 0 && m.Sample < src.Length).All(m => was.Any(a => a.Sample == m.Sample && a.Beat == m.Beat)), Is.True, "and none was added outside it (but the two ends of the audio, on the line they had)");
                Assert.That(after.Any(m => Math.Abs(m.Sample - from * rate) <= 20), Is.True, "a marker pins its start");
                Assert.That(after.Any(m => Math.Abs(m.Sample - to * rate) <= 20), Is.True, "and its end");
                Assert.That(after.Count(m => m.Sample > from * rate && m.Sample < to * rate), Is.GreaterThan(before.Count(m => m.Sample > from * rate && m.Sample < to * rate)), "more markers inside: the transients");
                Assert.That(WarpMath.Validate(after), Is.Null);
            });
        }

        [AvaloniaTest]
        public void SeveralClipsAreAutoWarpedAsOneUndoStep()
        {
            using var off = new WarpModeScope(AutoWarpMode.Off);
            using var rig = new Rig(WarpFile("grid-two.wav", WarpSignals.Tempo.Drift, 48));
            var a = rig.SessionClip;
            var b = rig.ArrangedClip;
            string before = State("A", a.Name) + State("B", b.Name);
            Assert.That(Layout.AutoWarpClips(new IPattern[] { a, b }, true), Is.Null);
            Assert.That(Audio(a).Markers.Count, Is.GreaterThan(4));
            Assert.That(Audio(b).Markers.Count, Is.GreaterThan(4));
            Assert.That(Layout.LastGridReports.Count, Is.EqualTo(2));
            rig.Undo();
            Assert.That(State("A", a.Name) + State("B", b.Name), Is.EqualTo(before), "one undo takes both back");
        }

        [AvaloniaTest]
        public void TheAgentsGridModeMakesTheMarkersTheLayoutDoesAndABadModeIsRefused()
        {
            using var off = new WarpModeScope(AutoWarpMode.Off);
            using var rig = new Rig(WarpFile("grid-agent.wav", WarpSignals.Tempo.Drift, 48));
            Layout.AutoWarp(rig.SessionClip, 0, out _, toGrid: true);
            var byLayout = Audio(rig.SessionClip).Markers.ToList();
            Assert.That(byLayout.Count, Is.GreaterThan(4));
            var answer = Tool("audio_clip_set", new JsonObject { ["machine"] = "B", ["pattern"] = rig.ArrangedClip.Name, ["auto_warp"] = true, ["mode"] = "grid", ["grid"] = "1/16", ["strength_percent"] = 100 });
            var structured = Ok(answer);
            var byAgent = Audio(rig.ArrangedClip).Markers.ToList();
            Assert.That(byAgent.Count, Is.EqualTo(byLayout.Count));
            Assert.That(byAgent.Zip(byLayout, (x, y) => Math.Abs(x.Sample - y.Sample)).Max(), Is.LessThan(1.0));
            Assert.That(structured["to_grid"]?["markers_made"]?.GetValue<int>() ?? 0, Is.GreaterThan(0), "the answer says what it did");
            Assert.That(structured["auto_warp_grid"]?["grid"]?.GetValue<string>(), Is.EqualTo("1/16"));
            Assert.That(Tool("audio_clip_set", new JsonObject { ["machine"] = "B", ["pattern"] = rig.ArrangedClip.Name, ["grid"] = "1/3" })["isError"]?.GetValue<bool>() ?? false, Is.True);
            Assert.That(Tool("audio_clip_set", new JsonObject { ["machine"] = "B", ["pattern"] = rig.ArrangedClip.Name, ["mode"] = "grid" })["isError"]?.GetValue<bool>() ?? false, Is.True, "mode needs auto_warp");
        }

        [AvaloniaTest]
        public void AClipAlreadyWarpedIsUntouchedAndTheImportChoiceGivesMoreMarkersToGrid()
        {
            string file = WarpFile("grid-import.wav", WarpSignals.Tempo.Drift, 48);
            using var rig = new Rig();
            int track = Layout.FirstTrackOf(rig.A);
            int beat, grid;
            using (new WarpModeScope(AutoWarpMode.Beat))
            {
                var c = Layout.ImportAudio(track, 1, file, out string error);
                Assert.That(c, Is.Not.Null, error);
                beat = Audio(c).Markers.Count;
                string state = State("A", c.Name);
                using (new WarpModeScope(AutoWarpMode.Grid)) Assert.That(State("A", c.Name), Is.EqualTo(state), "changing the preference changes no clip");
            }
            using (new WarpModeScope(AutoWarpMode.Grid))
            {
                var c = Layout.ImportAudio(track, 2, file, out string error);
                Assert.That(c, Is.Not.Null, error);
                grid = Audio(c).Markers.Count;
            }
            Assert.That(beat, Is.GreaterThan(4), "the default on import is auto-warp");
            Assert.That(grid, Is.GreaterThan(beat), "to grid on import puts markers on the transients too");
            using (new WarpModeScope(AutoWarpMode.Off))
                Assert.That(Audio(Layout.ImportAudio(track, 3, file, out _)).Markers, Is.Empty);
        }

        [AvaloniaTest]
        public void MarkersEditedByHandAfterAutoWarpToGridBehaveAsAnyOthers()
        {
            using var off = new WarpModeScope(AutoWarpMode.Off);
            using var rig = new Rig(WarpFile("grid-manual.wav", WarpSignals.Tempo.Drift, 48));
            var clip = rig.SessionClip;
            Layout.AutoWarp(clip, 0, out _, toGrid: true);
            var m = Audio(clip).Markers.ToList();
            Assert.That(m.Count, Is.GreaterThan(6));
            // Move one inner marker a little, delete another, as the waveform does (the same WarpEdit calls).
            Assert.That(Layout.WarpEdit(clip, new WarpRequest { Action = WarpRequest.Move, Markers = new[] { 3 }, DeltaBeats = 0.02 }, null, out _), Is.Null);
            Assert.That(Layout.WarpEdit(clip, new WarpRequest { Action = WarpRequest.Delete, Markers = new[] { 5 } }, null, out _), Is.Null);
            var after = Audio(clip).Markers.ToList();
            Assert.That(after.Count, Is.EqualTo(m.Count - 1));
            Assert.That(WarpMath.Validate(after), Is.Null);
            rig.Undo(); rig.Undo();
            Assert.That(Audio(clip).Markers.SequenceEqual(m), Is.True, "each edit is its own undo step");
        }
    }
}
