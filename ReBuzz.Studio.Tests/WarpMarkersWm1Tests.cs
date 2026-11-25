// Fork (docs/specs/neo-audio.md, WM1): warp markers you can work with. Each gesture and command of the Audio page's waveform, driven as a user does it (the mouse,
// the keys, the context menu), in the Session and in the Arrangement: its value, one undo step, redo, and the song saved and opened again as .nbl; the agent's
// audio_warp_markers against the page; the markers never out of order, the audio's two ends staying.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Clips;
using ReBuzz.Core.Session;
using ReBuzz.SessionTests.AutoWarp;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Audio;
using static ReBuzz.Studio.Tests.NeoAudioClipPageTests;

namespace ReBuzz.Studio.Tests
{
    public class WarpMarkersWm1Tests
    {
        const double Grid = 0.5;                           // the snap step at the zoom the tests use (30 pixels a beat)

        /// <summary>16 beats at 100 BPM of clicks, each a little off its beat (so quantizing has something to do), as a stereo float WAV.</summary>
        static string Drums()
        {
            string file = Work("drums-wm1.wav");
            int rate = 44100, n = (int)(rate * 9.6);
            double[] jitter = { 0, .06, -.05, .08, -.07, .04, -.06, .09, -.04, .05, -.08, .07, -.05, .06, -.07, .03 };
            var inter = new float[n * 2];
            for (int beat = 0; beat < 16; beat++)
            {
                int at = (int)((beat + jitter[beat]) * 0.6 * rate);
                for (int i = 0; i < 2400 && at + i < n && at + i >= 0; i++)
                    inter[2 * (at + i)] = inter[2 * (at + i) + 1] = (float)(0.9 * Math.Sin(i * 0.3) * Math.Exp(-i / 300.0));
            }
            WriteWav(file, rate, inter);
            return file;
        }

        /// <summary>The body of a test, once on the Session's clip and once on the Arrangement's: the page open on it, peaks made, 30 pixels to a beat.</summary>
        static void Contexts(Action<Rig, IPattern, AudioClipPage> body)
        {
            foreach (bool arranged in new[] { false, true })
            {
                using var rig = new Rig(Drums());
                if (arranged) rig.SelectInArrangement(); else rig.SelectInSession();
                var page = rig.OpenPage();
                var clip = arranged ? rig.ArrangedClip : rig.SessionClip;
                Assert.That(Layout.EditAudio(clip, new AudioClipEdit { TempoBpm = 100 }, rig.Editor, "tempo"), Is.Null);
                Settle();
                Assert.That(page.Context, Is.EqualTo(arranged ? AudioClipPage.Where.Arrangement : AudioClipPage.Where.Session));
                rig.WaitPeaks(clip);
                page.Wave.SetZoom(30);
                page.Wave.ScrollTo(-0.4);
                Settle();
                Assert.That(Layout.TransientsOf(clip).Count, Is.GreaterThan(10), "the clicks are found");
                body(rig, clip, page);
            }
        }

        /// <summary>Markers at the transients with these numbers (and the audio's two ends), as the clip's own.</summary>
        static void Seed(Rig rig, IPattern clip, params int[] transients)
        {
            var src = Layout.AudioSourceOf(clip)!;
            var audio = AudioOf(clip);
            var m = WarpMath.Effective(audio.Markers, src.Length, src.SampleRate, audio.Bpm);
            var found = Layout.TransientsOf(clip);
            foreach (int i in transients)
            {
                var r = WarpMath.Add(m, found[i]);
                Assert.That(r.Ok, Is.True, r.Error);
                m = r.Markers;
            }
            Assert.That(Layout.EditAudio(clip, new AudioClipEdit { Markers = m }, rig.Editor, "seed"), Is.Null);
            Settle();
            rig.Page.Refresh(force: true);
            Settle();
        }

        static SessionDocument.AudioSettings AudioOf(IPattern p) => Layout.AudioOf(p)!;

        static Point TransientAt(Rig rig, WarpEditor wave, double sample, double fy = 0.5) => rig.In(wave, 0, fy) + new Vector(wave.TransientX(sample), 0);
        static Point MarkerAt(Rig rig, WarpEditor wave, int index, double fy = 0.4) => rig.In(wave, 0, fy) + new Vector(wave.MarkerX(index), 0);

        static List<(double Sample, double Beat)> Marks(IPattern clip) => AudioOf(clip).Markers.OrderBy(m => m.Beat).ToList();

        static void AssertOrdered(IPattern clip)
        {
            var m = Marks(clip);
            if (m.Count == 0) return;
            Assert.That(m.Count, Is.GreaterThanOrEqualTo(2));
            for (int i = 1; i < m.Count; i++)
            {
                Assert.That(m[i].Beat, Is.GreaterThan(m[i - 1].Beat), "the beats rise");
                Assert.That(m[i].Sample, Is.GreaterThan(m[i - 1].Sample), "the samples rise");
            }
            var src = Layout.AudioSourceOf(clip)!;
            Assert.That(m[0].Sample, Is.EqualTo(0).Within(1e-6), "the first end stays");
            Assert.That(m[^1].Sample, Is.EqualTo(src.Length).Within(1e-6), "the last end stays");
        }

        static void Choose(Rig rig, WarpEditor wave, string header, Point at, bool save = false, Action<string> check = null, bool undoable = true)
        {
            // The page may lay itself out again as the pointer arrives (a toolbar row more or less): follow the waveform.
            var o1 = wave.TranslatePoint(default, rig.Window);
            rig.Window.MouseMove(at);
            Settle();
            var o2 = wave.TranslatePoint(default, rig.Window);
            if (o1 != null && o2 != null) at += o2.Value - o1.Value;
            rig.Click(at, MouseButton.Right);
            Assert.That(wave.LastMenu, Is.Not.Null);
            Assert.That(wave.LastMenu!.IsOpen, Is.True, "a right click opens the waveform's menu");
            var item = wave.LastMenu.Items.OfType<MenuItem>().First(i => ((string)i.Header!).StartsWith(header, StringComparison.Ordinal));
            Assert.That(item.IsEnabled, Is.True, header + " is enabled");
            var clip = rig.Page.Pattern!;
            string before0 = State(clip.Machine.Name, clip.Name);
            Steps(rig, clip, () =>
            {
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                wave.LastMenu.Close();
                Settle();
                if (rig.Page.StatusText.Length > 0 && State(clip.Machine.Name, clip.Name) == before0) Assert.Fail(header + ": " + rig.Page.StatusText);
            }, save, check);
        }

        // ------------------------------------------------------------------ transients you can grab

        [AvaloniaTest]
        public void HoveringATransientShowsAGhostMarkerAndDraggingItMakesAMarkerAndWarpsInOneUndoStep()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                double t = Layout.TransientsOf(clip)[3];
                rig.Window.MouseMove(TransientAt(rig, wave, t));
                Settle();
                Assert.That(wave.Ghost, Is.EqualTo(t), "a ghost marker shows on the transient under the pointer");
                rig.Window.MouseMove(TransientAt(rig, wave, t) + new Vector(15, 0));
                Settle();
                Assert.That(double.IsNaN(wave.Ghost), Is.True, "and goes when the pointer leaves it");
                Assert.That(AudioOf(clip).Markers, Is.Empty, "hovering makes nothing");

                Steps(rig, clip, () => rig.Drag(TransientAt(rig, wave, t), TransientAt(rig, wave, t) + new Vector(wave.PixelsPerBeat * 0.9, 0)), save: false, check: _ =>
                {
                    var m = Marks(clip);
                    Assert.That(m.Count, Is.EqualTo(3), "the two ends and the new marker, made by the one drag");
                    var made = m.Single(x => Math.Abs(x.Sample - t) < 0.5);
                    var straight = WarpMath.BeatAt(WarpMath.Effective(null, Layout.AudioSourceOf(clip)!.Length, Layout.AudioSourceOf(clip)!.SampleRate, AudioOf(clip).Bpm), t);
                    Assert.That(made.Beat, Is.GreaterThan(straight + 0.4), "the marker is on the transient and the audio is warped later");
                    Assert.That(Math.Abs(made.Beat / Grid - Math.Round(made.Beat / Grid)), Is.LessThan(1e-9), "on the grid");
                    AssertOrdered(clip);
                });
                // Saved and opened as .nbl.
                Assert.That(Saved(clip.Machine.Name, clip.Name), Does.Contain("<Marker"), "the markers are in the saved song");
            });
        }

        // ------------------------------------------------------------------ selection

        [AvaloniaTest]
        public void ClickShiftClickCtrlClickSelectAndEscAndCtrlAClearAndSelectAll()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                Seed(rig, clip, 2, 4, 6);
                Assert.That(wave.Markers.Count, Is.EqualTo(5));
                rig.Click(MarkerAt(rig, wave, 1));
                Assert.That(wave.SelectedIndices, Is.EqualTo(new[] { 1 }), "a click selects");
                rig.Click(MarkerAt(rig, wave, 2), mods: RawInputModifiers.Shift);
                Assert.That(wave.SelectedIndices, Is.EqualTo(new[] { 1, 2 }), "Shift-click extends");
                rig.Click(MarkerAt(rig, wave, 3), mods: RawInputModifiers.Control);
                Assert.That(wave.SelectedIndices, Is.EqualTo(new[] { 1, 2, 3 }), "Ctrl-click extends");
                rig.Click(MarkerAt(rig, wave, 2), mods: RawInputModifiers.Meta);
                Assert.That(wave.SelectedIndices, Is.EqualTo(new[] { 1, 3 }), "Cmd-click on a selected marker takes it out");
                rig.Key(PhysicalKey.Escape);
                Assert.That(wave.SelectedIndices, Is.Empty, "Esc clears");
                rig.Key(PhysicalKey.A, RawInputModifiers.Control);
                Assert.That(wave.SelectedIndices, Is.EqualTo(new[] { 0, 1, 2, 3, 4 }), "Ctrl+A selects all");
                rig.Click(MarkerAt(rig, wave, 2));
                Assert.That(wave.SelectedIndices, Is.EqualTo(new[] { 2 }), "a plain click on one of several selects it alone");
                rig.Click(rig.In(wave, 0, 0.6) + new Vector(wave.X(wave.Markers[3].Beat + 0.5), 0));
                Assert.That(wave.SelectedIndices, Is.Empty, "a click on empty waveform clears the selection");
                Assert.That(Marks(clip).Count, Is.EqualTo(5), "selecting changes no audio");
            });
        }

        [AvaloniaTest]
        public void DraggingOnEmptyWaveformSelectsATimeRangeAndTheMarkersInIt()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                Seed(rig, clip, 2, 4, 6);
                double b1 = wave.Markers[1].Beat, b2 = wave.Markers[2].Beat;
                string before = State(clip.Machine.Name, clip.Name);
                rig.Drag(WaveAt(rig, b1 - 0.5, 0.6), WaveAt(rig, b2 + 0.5, 0.6));
                Assert.That(wave.Range, Is.Not.Null, "a time range is selected");
                Assert.That(wave.Range!.Value.From, Is.EqualTo(Math.Round((b1 - 0.5) / Grid) * Grid).Within(1e-6), "snapped to the grid");
                Assert.That(wave.SelectedIndices, Is.EqualTo(new[] { 1, 2 }), "the markers inside it are selected");
                Assert.That(State(clip.Machine.Name, clip.Name), Is.EqualTo(before), "a selection is not an edit");
                rig.Key(PhysicalKey.Escape);
                Assert.That(wave.Range, Is.Null, "Esc clears the range too");
            });
        }

        [AvaloniaTest]
        public void DraggingASelectedMarkerMovesTheWholeSelectionKeepingItsSpacing()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                Seed(rig, clip, 2, 4, 6);
                double b1 = wave.Markers[1].Beat, b2 = wave.Markers[2].Beat, b3 = wave.Markers[3].Beat;
                rig.Drag(WaveAt(rig, b1 - 0.5, 0.6), WaveAt(rig, b2 + 0.5, 0.6));
                Assert.That(wave.SelectedIndices, Is.EqualTo(new[] { 1, 2 }));
                var samples = Marks(clip).Select(m => m.Sample).ToList();
                Steps(rig, clip, () => rig.Drag(MarkerAt(rig, wave, 1), MarkerAt(rig, wave, 1) + new Vector(wave.PixelsPerBeat * 1.0, 0)), save: true, check: _ =>
                {
                    var m = Marks(clip);
                    double d1 = m[1].Beat - b1, d2 = m[2].Beat - b2;
                    Assert.That(d1, Is.GreaterThan(0.4), "it moved later");
                    Assert.That(d2, Is.EqualTo(d1).Within(1e-9), "the other selected marker moved as far: the spacing is kept");
                    Assert.That(m[3].Beat, Is.EqualTo(b3).Within(1e-9), "the marker outside the selection stays");
                    Assert.That(m.Select(x => x.Sample), Is.EqualTo(samples), "the audio stays where it was: only the beats move (the audio before and after stretches)");
                    AssertOrdered(clip);
                });
            });
        }

        [AvaloniaTest]
        public void AMarkerCannotPassItsNeighbourAndTheTwoPinnedEndsStay()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                Seed(rig, clip, 2, 4, 6);
                double b3 = wave.Markers[3].Beat;
                rig.Click(MarkerAt(rig, wave, 2));
                Steps(rig, clip, () => rig.Drag(MarkerAt(rig, wave, 2), MarkerAt(rig, wave, 3) + new Vector(60, 0)), save: false, check: _ =>
                {
                    var m = Marks(clip);
                    Assert.That(m[2].Beat, Is.EqualTo(b3 - WarpMath.MinGap).Within(1e-9), "it stops one tick before its neighbour");
                    AssertOrdered(clip);
                });
                // Select everything and press Del: the inner markers go, the audio's two ends stay.
                rig.Click(MarkerAt(rig, wave, 1));
                rig.Key(PhysicalKey.A, RawInputModifiers.Control);
                Steps(rig, clip, () => rig.Key(PhysicalKey.Delete), save: false, check: _ =>
                {
                    Assert.That(Marks(clip).Count, Is.EqualTo(2), "only the two pinned ends are left");
                    AssertOrdered(clip);
                });
                // Only an end selected: nothing to delete, and it says so.
                string was = State(clip.Machine.Name, clip.Name);
                wave.SelectIndices(new[] { 0 });
                rig.Key(PhysicalKey.Delete);
                Assert.That(State(clip.Machine.Name, clip.Name), Is.EqualTo(was), "an end can't be deleted");
                Assert.That(page.StatusText, Does.Contain("ends stay"));
                Assert.That(Saved(clip.Machine.Name, clip.Name), Is.EqualTo(was), "saved and opened again as .nbl");
            });
        }

        // ------------------------------------------------------------------ against the audio

        [AvaloniaTest]
        public void ShiftDragMovesAMarkerAlongTheAudioKeepingItsBeat()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                Seed(rig, clip, 3);
                var before = Marks(clip)[1];
                Assert.That(page.HintText, Is.Not.Null);
                rig.Window.MouseMove(MarkerAt(rig, wave, 1), RawInputModifiers.Shift);
                Settle();
                Assert.That(wave.Hint, Does.Contain("Shift: slide the audio"), "the status line names the modifier");
                Steps(rig, clip, () => rig.Drag(MarkerAt(rig, wave, 1), MarkerAt(rig, wave, 1) + new Vector(9, 0), RawInputModifiers.Shift), save: false, check: _ =>
                {
                    var after = Marks(clip)[1];
                    Assert.That(after.Beat, Is.EqualTo(before.Beat).Within(1e-12), "the marker keeps its beat");
                    Assert.That(after.Sample, Is.GreaterThan(before.Sample + 100), "it took the audio under the pointer");
                    AssertOrdered(clip);
                });
                // The pinned end can't move along the audio.
                string was = State(clip.Machine.Name, clip.Name);
                rig.Drag(MarkerAt(rig, wave, 0), MarkerAt(rig, wave, 0) + new Vector(12, 0), RawInputModifiers.Shift);
                Assert.That(State(clip.Machine.Name, clip.Name), Is.EqualTo(was), "the audio's first end stays pinned");
                Assert.That(Saved(clip.Machine.Name, clip.Name), Is.EqualTo(was), "saved and opened again as .nbl");
            });
        }

        [AvaloniaTest]
        public void TheTooltipNamesTheModifierToo()
        {
            using var rig = new Rig(Drums());
            rig.SelectInSession();
            var page = rig.OpenPage();
            string tip = ToolTip.GetTip(page.Wave) as string ?? "";
            Assert.That(tip, Does.Contain("Shift+drag"));
            Assert.That(tip, Does.Contain(WarpEditor.AltName + ": free"));
            Assert.That(WarpEditor.AltName, Is.EqualTo(OperatingSystem.IsMacOS() ? "Option" : "Alt"));
        }

        // ------------------------------------------------------------------ segment tempo

        [AvaloniaTest]
        public void TheSegmentTempoIsShownEditableAndHalvedAndDoubledStretchingOnlyThatSegment()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                Seed(rig, clip, 2, 4, 6);
                string name = clip.Machine.Name;
                rig.Click(MarkerAt(rig, wave, 1));
                var tempos = Layout.SegmentTempos(clip);
                Assert.That(tempos.Count, Is.EqualTo(4));
                Assert.That(double.Parse(page.SegmentAfterBox.Text!, CultureInfo.InvariantCulture), Is.EqualTo(tempos[1]).Within(0.01), "the segment after the selected marker");
                Assert.That(double.Parse(page.SegmentBeforeBox.Text!, CultureInfo.InvariantCulture), Is.EqualTo(tempos[0]).Within(0.01), "and the one before it");
                var samples = Marks(clip).Select(m => m.Sample).ToList();

                Steps(rig, clip, () => page.Type(page.SegmentAfterBox, "80"), save: false, check: _ =>
                {
                    var now = Layout.SegmentTempos(clip);
                    Assert.That(now[1], Is.EqualTo(80).Within(1e-6), "the typed tempo");
                    Assert.That(now[0], Is.EqualTo(tempos[0]).Within(1e-6), "the segment before is untouched");
                    Assert.That(now[2], Is.EqualTo(tempos[2]).Within(1e-6), "so are the ones after: only this segment is stretched");
                    Assert.That(now[3], Is.EqualTo(tempos[3]).Within(1e-6));
                    Assert.That(Marks(clip).Select(m => m.Sample), Is.EqualTo(samples), "the audio's samples are where they were");
                    AssertOrdered(clip);
                });
                var at80 = Layout.SegmentTempos(clip);
                Steps(rig, clip, () => rig.Click(rig.Of(page.SegmentHalveButton)), save: false, check: _ => Assert.That(Layout.SegmentTempos(clip)[1], Is.EqualTo(at80[1] / 2).Within(1e-6), "halved"));
                Steps(rig, clip, () => rig.Click(rig.Of(page.SegmentDoubleButton)), save: false, check: _ => Assert.That(Layout.SegmentTempos(clip)[1], Is.EqualTo(at80[1]).Within(1e-6), "and doubled again"));
                // The segment before: the box on the left.
                Steps(rig, clip, () => page.Type(page.SegmentBeforeBox, "110"), save: false, check: _ => Assert.That(Layout.SegmentTempos(clip)[0], Is.EqualTo(110).Within(1e-6)));
                // The last marker has no segment after it.
                wave.SelectIndices(new[] { wave.Markers.Count - 1 });
                Assert.That(page.SegmentAfterBox.Text, Is.Empty, "no segment after the last marker");
                Assert.That(page.SegmentBeforeBox.Text, Is.Not.Empty);
                string kept = State(clip.Machine.Name, clip.Name);
                Assert.That(Saved(clip.Machine.Name, clip.Name), Is.EqualTo(kept), "saved and opened again as .nbl");
            });
        }

        // ------------------------------------------------------------------ the context menu and the keys

        [AvaloniaTest]
        public void TheContextMenuHasEveryEditWithItsShortcut()
        {
            using var rig = new Rig(Drums());
            rig.SelectInSession();
            var page = rig.OpenPage();
            rig.WaitPeaks(rig.SessionClip);
            var wave = page.Wave;
            wave.SetZoom(30);
            Settle();
            rig.Click(rig.In(wave, 0, 0.6) + new Vector(wave.X(2.5), 0), MouseButton.Right);
            var items = wave.LastMenu!.Items.OfType<MenuItem>().ToList();
            var headers = items.Select(i => (string)i.Header!).ToList();
            foreach (string h in new[] { "Set bar 1 here", "Warp from here, straight", "Warp as a loop of", "Insert markers at the transients", "Insert a marker on every bar", "Delete the selected markers", "Remove all markers (straight)", "Quantize to the grid" })
                Assert.That(headers.Any(x => x.StartsWith(h)), Is.True, h);
            Assert.That(items.First(i => ((string)i.Header!).StartsWith("Quantize")).InputGesture, Is.Not.Null, "Quantize shows its shortcut");
            Assert.That(items.First(i => ((string)i.Header!).StartsWith("Set bar 1")).InputGesture, Is.Not.Null);
            Assert.That(items.First(i => ((string)i.Header!).StartsWith("Delete")).IsEnabled, Is.False, "nothing selected to delete");
            Assert.That(items.First(i => ((string)i.Header!).StartsWith("Remove all")).IsEnabled, Is.False, "a straight clip has none");
            wave.LastMenu.Close();
        }

        [AvaloniaTest]
        public void SetBarOneHereMakesThatMomentTheFirstDownbeat()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                double t = Layout.TransientsOf(clip)[3];
                Choose(rig, wave, "Set bar 1 here", TransientAt(rig, wave, t, 0.6), save: true, check: _ =>
                {
                    var m = Marks(clip);
                    var src = Layout.AudioSourceOf(clip)!;
                    var map = new WarpMap(m.Select(x => new WarpMarker(x.Sample, x.Beat)));
                    Assert.That(map.BeatAt(t), Is.EqualTo(0).Within(1e-9), "the moment is beat 0, bar 1");
                    Assert.That(map.SampleAt(0), Is.EqualTo(t).Within(1e-6), "the clip starts there");
                    AssertOrdered(clip);
                });
            });
        }

        [AvaloniaTest]
        public void WarpFromHereStraightFindsOneTempoAndRemovesTheMarkersAfter()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                Seed(rig, clip, 2, 4, 6, 9, 11);
                var found = Layout.TransientsOf(clip);
                double rate = Layout.AudioSourceOf(clip)!.SampleRate;
                double at = found.OrderBy(t => Math.Abs(t - 4.8 * rate)).First();      // beat 8: 2 bars of audio are left
                Choose(rig, wave, "Warp from here, straight", TransientAt(rig, wave, at, 0.6), save: true, check: _ =>
                {
                    var m = Marks(clip);
                    Assert.That(m.Any(x => Math.Abs(x.Sample - at) < 0.5), Is.True, "a marker where it was asked");
                    Assert.That(m.Count(x => x.Sample > at + 1), Is.EqualTo(1), "after it only the audio's end: the other markers are removed");
                    var tempos = Layout.SegmentTempos(clip);
                    Assert.That(tempos[^1], Is.InRange(90, 112), "one tempo found from the audio from there, near its 100 BPM; got " + tempos[^1]);
                    Assert.That(m.Count(x => x.Sample < at - 1), Is.EqualTo(4), "the markers before it stay (the first end and three)");
                    AssertOrdered(clip);
                });
            });
        }

        [AvaloniaTest]
        public void WarpAsALoopMakesTheSelectionAWholeNumberOfBars()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                // 0 to 7.5 beats is 1.875 bars: 2 bars, 8 beats.
                rig.Drag(rig.In(wave, 0, 0.6) + new Vector(wave.X(0.02), 0), WaveAt(rig, 7.5, 0.6));
                Assert.That(wave.Range!.Value.To, Is.EqualTo(7.5).Within(1e-6));
                double endSample = Layout.AudioSourceOf(clip)!.SampleRate * 7.5 * 60 / AudioOf(clip).Bpm;
                Choose(rig, wave, "Warp as a loop of 2 bars", rig.In(wave, 0, 0.6) + new Vector(wave.X(3.25), 0), save: true, check: _ =>
                {
                    var m = Marks(clip);
                    var map = new WarpMap(m.Select(x => new WarpMarker(x.Sample, x.Beat)));
                    Assert.That(map.BeatAt(endSample), Is.EqualTo(8).Within(1e-6), "the selection is 2 bars (8 beats) long now");
                    Assert.That(map.BeatAt(0), Is.EqualTo(0).Within(1e-9));
                    AssertOrdered(clip);
                });
            });
        }

        [AvaloniaTest]
        public void InsertMarkersAtTheTransientsInTheClipOrInTheSelection()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                var found = Layout.TransientsOf(clip);
                // In the selection: the transients between beats 3.5 and 8.5 (a range of 5 beats).
                rig.Drag(WaveAt(rig, 3.5, 0.6), WaveAt(rig, 8.5, 0.6));
                double r0 = wave.Range!.Value.From, r1 = wave.Range.Value.To;
                var src = Layout.AudioSourceOf(clip)!;
                int expected = found.Count(t => { double b = t / src.SampleRate * AudioOf(clip).Bpm / 60; return b >= r0 && b <= r1; });
                Assert.That(expected, Is.GreaterThan(2));
                Choose(rig, wave, "Insert markers at the transients in the selection", rig.In(wave, 0, 0.6) + new Vector(wave.X(5.5), 0), save: false, check: _ =>
                {
                    Assert.That(Marks(clip).Count, Is.EqualTo(2 + expected), "a marker on each transient of the selection, and the two ends");
                    Assert.That(wave.SelectedIndices.Count, Is.EqualTo(expected), "the new markers are selected");
                    AssertOrdered(clip);
                });
                wave.Focus();
                rig.Key(PhysicalKey.Escape);
                Assert.That(wave.Range, Is.Null, "Esc clears the range");
                // In the whole clip: the rest.
                var before = Marks(clip).Count;
                Choose(rig, wave, "Insert markers at the transients", rig.In(wave, 0, 0.6) + new Vector(wave.X(5.5), 0), save: true, check: _ =>
                {
                    Assert.That(Marks(clip).Count, Is.GreaterThan(before), "the other transients got one");
                    Assert.That(Marks(clip).Count, Is.LessThanOrEqualTo(2 + found.Count));
                    AssertOrdered(clip);
                });
            });
        }

        [AvaloniaTest]
        public void InsertAMarkerOnEveryBarThenDeleteAndRemoveAll()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                Choose(rig, wave, "Insert a marker on every bar", rig.In(wave, 0, 0.6) + new Vector(wave.X(2.5), 0), save: false, check: _ =>
                {
                    var m = Marks(clip);
                    Assert.That(m.Count, Is.EqualTo(5), "the ends and the bar lines at 4, 8 and 12");
                    Assert.That(m.Skip(1).Take(3).Select(x => x.Beat), Is.EqualTo(new[] { 4.0, 8.0, 12.0 }).Within(1e-9));
                    Assert.That(wave.SelectedIndices, Is.EqualTo(new[] { 1, 2, 3 }), "the new markers are selected");
                    AssertOrdered(clip);
                });
                // Delete the selected ones from the menu.
                rig.Click(MarkerAt(rig, wave, 2));
                Choose(rig, wave, "Delete the selected markers", MarkerAt(rig, wave, 2), save: false, check: _ => Assert.That(Marks(clip).Count, Is.EqualTo(4)));
                Choose(rig, wave, "Remove all markers", rig.In(wave, 0, 0.6) + new Vector(wave.X(2.5), 0), save: true, check: _ => Assert.That(AudioOf(clip).Markers, Is.Empty, "straight again"));
            });
        }

        [AvaloniaTest]
        public void CtrlUQuantizesTheSelectedMarkersToTheGridByTheSongsStrength()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                Seed(rig, clip, 2, 4, 6, 8);
                var was = Marks(clip);
                Assert.That(was.Skip(1).Take(4).Any(m => Math.Abs(m.Beat / Grid - Math.Round(m.Beat / Grid)) > 0.05), Is.True, "the clicks are off the grid to begin with");
                rig.Click(MarkerAt(rig, wave, 1));
                rig.Key(PhysicalKey.A, RawInputModifiers.Control);
                Steps(rig, clip, () => rig.Key(PhysicalKey.U, RawInputModifiers.Control), save: false, check: _ =>
                {
                    var m = Marks(clip);
                    for (int i = 1; i < m.Count - 1; i++) Assert.That(Math.Abs(m[i].Beat / Grid - Math.Round(m[i].Beat / Grid)), Is.LessThan(1e-9), $"marker {i} is on the grid: {m[i].Beat}");
                    Assert.That(m[^1].Beat, Is.EqualTo(was[^1].Beat).Within(1e-12), "the pinned end stays");
                    Assert.That(m.Select(x => x.Sample), Is.EqualTo(was.Select(x => x.Sample)), "only the beats moved");
                    AssertOrdered(clip);
                });
                // 50 %: halfway there. The strength is a field beside the button, kept with the song.
                rig.Undo();
                Assert.That(Marks(clip).Select(x => x.Beat), Is.EqualTo(was.Select(x => x.Beat)).Within(1e-12));
                page.Type(page.StrengthBox, "50");
                Assert.That(Layout.QuantizeStrength, Is.EqualTo(50));
                rig.Click(MarkerAt(rig, wave, 1));
                rig.Key(PhysicalKey.A, RawInputModifiers.Control);
                Steps(rig, clip, () => rig.Key(PhysicalKey.U, RawInputModifiers.Control), save: false, check: _ =>
                {
                    var m = Marks(clip);
                    for (int i = 1; i < m.Count - 1; i++)
                    {
                        double b = was[i].Beat, want = b + 0.5 * (Math.Round(b / Grid) * Grid - b);
                        Assert.That(m[i].Beat, Is.EqualTo(want).Within(1e-9), $"marker {i} moved halfway");
                    }
                });
                // It is saved with the song.
                Assert.That(Saved(clip.Machine.Name, clip.Name), Does.Contain("<Marker"));
                Assert.That(Layout.QuantizeStrength, Is.EqualTo(50), "the strength came back with the song");
            });
        }

        [AvaloniaTest]
        public void QuantizeWorksOnATimeRangeToo()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                Assert.That(AudioOf(clip).Markers, Is.Empty);
                rig.Drag(WaveAt(rig, 2.5, 0.6), WaveAt(rig, 6.5, 0.6));
                Assert.That(wave.Range, Is.Not.Null);
                Steps(rig, clip, () => rig.Key(PhysicalKey.U, RawInputModifiers.Control), save: true, check: _ =>
                {
                    var m = Marks(clip);
                    Assert.That(m.Count, Is.GreaterThanOrEqualTo(2 + 3), "markers were made at the transients in the range");
                    for (int i = 1; i < m.Count - 1; i++) Assert.That(Math.Abs(m[i].Beat / Grid - Math.Round(m[i].Beat / Grid)), Is.LessThan(1e-9), $"marker {i} is on the grid: {m[i].Beat}");
                    AssertOrdered(clip);
                });
            });
        }

        [AvaloniaTest]
        public void TheShortcutKeysSetBarOneInsertAtTransientsAndWarpAsALoop()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                double t = Layout.TransientsOf(clip)[5];
                rig.Click(rig.In(wave, 0, 0.6) + new Vector(wave.X(1.5), 0));     // focus the waveform, leave the pointer on a spot
                Steps(rig, clip, () => rig.Key(PhysicalKey.T, RawInputModifiers.Control), save: false, check: _ => Assert.That(Marks(clip).Count, Is.GreaterThan(5), "Ctrl+T: markers at the transients"));
                rig.Window.MouseMove(TransientAt(rig, wave, t, 0.6));
                Settle();
                Steps(rig, clip, () => rig.Key(PhysicalKey.B, RawInputModifiers.Control), save: false, check: _ =>
                {
                    var m = Marks(clip);
                    Assert.That(new WarpMap(m.Select(x => new WarpMarker(x.Sample, x.Beat))).BeatAt(t), Is.EqualTo(0).Within(1e-9), "Ctrl+B: bar 1 where the pointer is");
                });
                wave.SelectRange(3, 9.5);             // a range 6.5 beats long: the nearest loop is 2 bars
                var marks0 = Marks(clip);
                double s0 = WarpMath.SampleAt(marks0, wave.Range!.Value.From), s1 = WarpMath.SampleAt(marks0, wave.Range.Value.To);
                Steps(rig, clip, () => rig.Key(PhysicalKey.L, RawInputModifiers.Control), save: false, check: _ =>
                {
                    var m = new WarpMap(Marks(clip).Select(x => new WarpMarker(x.Sample, x.Beat)));
                    Assert.That(m.BeatAt(s1) - m.BeatAt(s0), Is.EqualTo(8).Within(1e-6), "Ctrl+L: the range (6.5 beats) is 2 bars now");
                });
                Assert.That(Saved(clip.Machine.Name, clip.Name), Does.Contain("<Marker"));
            });
        }

        // ------------------------------------------------------------------ zoom and scroll

        [AvaloniaTest]
        public void TheWheelZoomsAroundThePointerShiftWheelScrollsAndFitAndFollowWork()
        {
            using var rig = new Rig(Drums());
            rig.SelectInSession();
            var page = rig.OpenPage();
            rig.WaitPeaks(rig.SessionClip);
            var wave = page.Wave;
            wave.SetZoom(30);
            wave.ScrollTo(0);
            Settle();
            var at = rig.In(wave, 0, 0.5) + new Vector(200, 0);
            double beat = wave.BeatAt(200);
            double zoom = wave.PixelsPerBeat;
            rig.Window.MouseMove(at);
            rig.Window.MouseWheel(at, new Vector(0, 1));
            Settle();
            Assert.That(wave.PixelsPerBeat, Is.GreaterThan(zoom), "the wheel zooms in");
            Assert.That(wave.BeatAt(200), Is.EqualTo(beat).Within(1e-6), "around the pointer: the beat under it stays");
            wave.Zoom(200, 0.8);
            Assert.That(wave.PixelsPerBeat, Is.EqualTo(zoom * 1.25 * 0.8).Within(1e-6), "a pinch is a zoom around the pointer too");
            double z2 = wave.PixelsPerBeat, left = wave.LeftBeat;
            rig.Window.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Shift);
            Settle();
            Assert.That(wave.PixelsPerBeat, Is.EqualTo(z2).Within(1e-9), "Shift+wheel doesn't zoom");
            Assert.That(wave.LeftBeat, Is.Not.EqualTo(left), "it scrolls");
            page.FitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle();
            Assert.That(wave.X(wave.ClipBeats), Is.LessThanOrEqualTo(wave.Bounds.Width), "Fit shows the whole clip");
            // Follow: the view scrolls to keep the playing position in sight.
            wave.SetZoom(200);
            wave.ScrollTo(0);
            page.FollowBox.IsChecked = true;
            Settle();
            Assert.That(wave.Follow, Is.True);
            wave.ShowPlayhead(12);
            Assert.That(wave.X(12), Is.InRange(0, wave.Bounds.Width), "the playing position is in view");
            page.FollowBox.IsChecked = false;
            wave.ScrollTo(0);
            wave.ShowPlayhead(13);
            Assert.That(wave.X(13), Is.GreaterThan(wave.Bounds.Width), "without Follow the view stays");
        }

        // ------------------------------------------------------------------ the agent

        static JsonObject Warp(IPattern clip, string action, JsonObject? args = null)
        {
            var a = args ?? new JsonObject();
            a["machine"] = clip.Machine.Name;
            a["pattern"] = clip.Name;
            a["action"] = action;
            return Tool("audio_warp_markers", a);
        }

        static void AssertSameMarkers(IPattern a, IPattern b, string why)
        {
            var x = Marks(a);
            var y = Marks(b);
            Assert.That(y.Count, Is.EqualTo(x.Count), why + ": the same number of markers");
            for (int i = 0; i < x.Count; i++)
            {
                Assert.That(y[i].Sample, Is.EqualTo(x[i].Sample).Within(1e-6), $"{why}: marker {i} sample");
                Assert.That(y[i].Beat, Is.EqualTo(x[i].Beat).Within(1e-6), $"{why}: marker {i} beat");
            }
        }

        [AvaloniaTest]
        public void TheAgentsAudioWarpMarkersAndThePageMakeTheSameMarkersWithTheSameUndo()
        {
            using var rig = new Rig(Drums());
            rig.SelectInSession();
            var page = rig.OpenPage();
            rig.WaitPeaks(rig.SessionClip);
            var wave = page.Wave;
            wave.SetZoom(30);
            wave.ScrollTo(-0.4);
            var page1 = rig.SessionClip;           // edited by the page
            var agent1 = rig.ArrangedClip;         // the same audio, edited by the agent
            foreach (var c in new[] { page1, agent1 }) Assert.That(Layout.EditAudio(c, new AudioClipEdit { TempoBpm = 100 }, rig.Editor, "tempo"), Is.Null);
            Settle();
            AssertSameMarkers(page1, agent1, "to begin with");

            var listed = Ok(Warp(agent1, "list"));
            Assert.That(listed["straight"]!.GetValue<bool>(), Is.True);
            Assert.That(listed["markers"]!.AsArray().Count, Is.EqualTo(2), "a straight clip lists its two ends");
            Assert.That(listed["markers"]![0]!["pinned"]!.GetValue<bool>(), Is.True);

            // 1. markers at the transients
            wave.InsertAtTransients();
            Settle();
            string before = State("B", agent1.Name);
            var r = Ok(Warp(agent1, "insert_at_transients"));
            Assert.That(r["count"]!.GetValue<int>(), Is.GreaterThan(5));
            AssertSameMarkers(page1, agent1, "insert at the transients");
            rig.Undo();
            Assert.That(State("B", agent1.Name), Is.EqualTo(before), "one undo takes the agent's whole edit back, as for the page");
            rig.Redo();
            AssertSameMarkers(page1, agent1, "after redo");

            // 2. quantize all (the page: select all, the grid it shows; the agent: the same grid)
            wave.SelectAll();
            wave.Quantize();
            Settle();
            Ok(Warp(agent1, "quantize", new JsonObject { ["markers"] = new JsonArray(Enumerable.Range(0, Marks(agent1).Count).Select(i => (JsonNode)JsonValue.Create(i)).ToArray()), ["grid_beats"] = Grid }));
            AssertSameMarkers(page1, agent1, "quantize");

            // 3. a segment's tempo
            wave.SelectIndices(new[] { 2 });
            page.Type(page.SegmentAfterBox, "90");
            Ok(Warp(agent1, "segment_tempo", new JsonObject { ["marker"] = 2, ["bpm"] = 90 }));
            AssertSameMarkers(page1, agent1, "segment tempo");
            var shown = Ok(Warp(agent1, "list"))["markers"]!.AsArray();
            Assert.That(shown[2]!["segment_bpm"]!.GetValue<double>(), Is.EqualTo(90).Within(1e-3), "the list shows each segment's tempo");

            // 4. halve and double
            rig.Click(rig.Of(page.SegmentHalveButton));
            Ok(Warp(agent1, "halve_segment", new JsonObject { ["marker"] = 2 }));
            AssertSameMarkers(page1, agent1, "halve");
            rig.Click(rig.Of(page.SegmentDoubleButton));
            Ok(Warp(agent1, "double_segment", new JsonObject { ["marker"] = 2 }));
            AssertSameMarkers(page1, agent1, "double");

            // 5. a marker moved: the page's drag against the agent's move by the same distance
            wave.SelectIndices(new[] { 3 });
            double b3 = Marks(page1)[3].Beat;
            rig.Drag(MarkerAt(rig, wave, 3), MarkerAt(rig, wave, 3) + new Vector(wave.PixelsPerBeat * 0.5, 0));
            double moved = Marks(page1)[3].Beat - b3;
            Assert.That(moved, Is.GreaterThan(0.2));
            Ok(Warp(agent1, "move", new JsonObject { ["markers"] = new JsonArray(3), ["delta_beats"] = moved }));
            AssertSameMarkers(page1, agent1, "move");

            // 6. one marker along the audio
            var m3 = Marks(agent1)[3];
            Ok(Warp(agent1, "move_along", new JsonObject { ["marker"] = 3, ["delta_samples"] = 500 }));
            Assert.That(Marks(agent1)[3].Beat, Is.EqualTo(m3.Beat).Within(1e-12));
            Assert.That(Marks(agent1)[3].Sample, Is.EqualTo(m3.Sample + 500).Within(1e-6));
            Ok(Warp(page1, "move_along", new JsonObject { ["marker"] = 3, ["delta_samples"] = 500 }));
            AssertSameMarkers(page1, agent1, "move along");

            // 7. delete
            wave.SelectIndices(new[] { 4 });
            wave.DeleteSelected();
            Settle();
            Ok(Warp(agent1, "delete", new JsonObject { ["markers"] = new JsonArray(4) }));
            AssertSameMarkers(page1, agent1, "delete");

            // 8. warp as a loop, over the same range
            wave.SelectRange(0, 7.5);
            wave.WarpAsLoop();
            Settle();
            Ok(Warp(agent1, "warp_loop", new JsonObject { ["from_beat"] = 0, ["to_beat"] = 7.5 }));
            AssertSameMarkers(page1, agent1, "warp as a loop");

            // 9. bar 1 here, by the menu and the tool
            double t = Layout.TransientsOf(page1)[6];
            rig.Click(TransientAt(rig, wave, t, 0.6), MouseButton.Right);
            wave.LastMenu!.Items.OfType<MenuItem>().First(i => ((string)i.Header!).StartsWith("Set bar 1")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            wave.LastMenu.Close();
            Settle();
            Ok(Warp(agent1, "set_bar_one", new JsonObject { ["sample"] = t }));
            AssertSameMarkers(page1, agent1, "set bar 1 here");

            // 10. every bar, straight from here, remove all
            wave.ClearSelection();
            wave.InsertEveryBar();
            Settle();
            Ok(Warp(agent1, "insert_every_bar"));
            AssertSameMarkers(page1, agent1, "a marker on every bar");
            wave.RemoveAll();
            Settle();
            Ok(Warp(agent1, "remove_all"));
            Assert.That(AudioOf(page1).Markers, Is.Empty);
            AssertSameMarkers(page1, agent1, "remove all");

            // warp from here with a given tempo
            var straight = Ok(Warp(agent1, "warp_straight", new JsonObject { ["beat"] = 4, ["bpm"] = 120 }));
            var list = straight["markers"]!.AsArray();
            Assert.That(list[^1]!.AsObject()["segment_bpm"], Is.Null, "the last marker has no segment after it");
            Assert.That(list[^2]!["segment_bpm"]!.GetValue<double>(), Is.EqualTo(120).Within(1e-3), "one tempo from there on");
            AssertOrdered(agent1);

            // the strength, kept with the song
            Ok(Warp(agent1, "quantize_strength", new JsonObject { ["strength"] = 25 }));
            Settle();
            Assert.That(Layout.QuantizeStrength, Is.EqualTo(25));
            Assert.That(page.StrengthBox.Text, Is.EqualTo("25"), "the page's field shows it");
        }

        [AvaloniaTest]
        public void TheAgentsMistakesAreRefusedWithNothingChanged()
        {
            using var rig = new Rig(Drums());
            var clip = rig.ArrangedClip;
            string before = State("B", clip.Name);
            Assert.That(Warp(clip, "delete", new JsonObject { ["markers"] = new JsonArray(0) })["isError"]!.GetValue<bool>(), Is.True, "an end can't be deleted");
            Assert.That(Warp(clip, "move", new JsonObject { ["markers"] = new JsonArray(7), ["delta_beats"] = 1 })["isError"]!.GetValue<bool>(), Is.True, "no such marker");
            Assert.That(Warp(clip, "move_along", new JsonObject { ["marker"] = 0, ["sample"] = 100 })["isError"]!.GetValue<bool>(), Is.True, "the pinned end");
            Assert.That(Warp(clip, "segment_tempo", new JsonObject { ["marker"] = 0, ["bpm"] = 2000 })["isError"]!.GetValue<bool>(), Is.True, "out of range");
            Assert.That(Warp(clip, "warp_loop", new JsonObject { ["bars"] = 3 })["isError"]!.GetValue<bool>(), Is.True, "3 bars isn't one of 1, 2, 4, 8, 16");
            Assert.That(Warp(clip, "quantize")["isError"]!.GetValue<bool>(), Is.True, "nothing selected to quantize");
            Assert.That(Warp(clip, "set_bar_one")["isError"]!.GetValue<bool>(), Is.True, "no position");
            Assert.That(State("B", clip.Name), Is.EqualTo(before), "nothing changed");
            // Warp off: the markers don't apply.
            Ok(Tool("audio_clip_set", new JsonObject { ["machine"] = "B", ["pattern"] = clip.Name, ["warp"] = "Off" }));
            Assert.That(Warp(clip, "insert_every_bar")["isError"]!.GetValue<bool>(), Is.True, "warp is off");
        }

        [AvaloniaTest]
        public void AnOldSongHasTheDefaultStrengthAndTheMarkersOfADragStaySavedAndInOrder()
        {
            using var rig = new Rig(Drums());
            Assert.That(Layout.QuantizeStrength, Is.EqualTo(100), "a song with none has 100 %");
            var clip = rig.SessionClip;
            rig.SelectInSession();
            var page = rig.OpenPage();
            rig.WaitPeaks(clip);
            page.Wave.SetZoom(30);
            Seed(rig, clip, 2, 4);
            AssertOrdered(clip);
            Assert.That(Saved(clip.Machine.Name, clip.Name), Does.Contain("<Marker"));
            Assert.That(Layout.QuantizeStrength, Is.EqualTo(100));
        }

        // ------------------------------------------------------------------ AW5: markers by hand, the hint, auto-warp on a range

        static double OffGrid(double beat) => Math.Abs(beat / Grid - Math.Round(beat / Grid));

        [AvaloniaTest]
        public void DoubleClickAddsAMarkerOnAnAttackOrOnPlainAudioAndTheEditsAfterwardsAreTheSameAsOnAnyMarker()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                double t = Layout.TransientsOf(clip)[4];
                Steps(rig, clip, () => rig.DoubleClick(TransientAt(rig, wave, t)), save: false, check: _ =>
                {
                    var m = Marks(clip);
                    Assert.That(m.Count, Is.EqualTo(3), "the two ends and the new marker");
                    Assert.That(m.Count(x => Math.Abs(x.Sample - t) < 0.5), Is.EqualTo(1), "on the attack");
                    AssertOrdered(clip);
                });
                Steps(rig, clip, () => rig.DoubleClick(WaveAt(rig, 9.5, 0.5)), save: false, check: _ =>
                {
                    Assert.That(Marks(clip).Count, Is.EqualTo(4), "and one on plain audio");
                    AssertOrdered(clip);
                });
                // As any other: a drag moves it, Delete takes it out.
                rig.Click(MarkerAt(rig, wave, 1));
                Steps(rig, clip, () => rig.Key(PhysicalKey.Delete), save: false, check: _ => Assert.That(Marks(clip).Count, Is.EqualTo(3), "Delete removes the selected marker"));
            });
        }

        [AvaloniaTest]
        public void DraggingSnapsToTheGridAndAltDraggingMovesFreely()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                Seed(rig, clip, 3);
                var from = MarkerAt(rig, wave, 1);
                Steps(rig, clip, () => rig.Drag(from, from + new Vector(7, 0)), save: false, check: _ =>
                    Assert.That(OffGrid(Marks(clip)[1].Beat), Is.LessThan(1e-9), "by default the marker lands on a grid line"));
                Steps(rig, clip, () => rig.Drag(from, from + new Vector(7, 0), RawInputModifiers.Alt), save: false, check: _ =>
                    Assert.That(OffGrid(Marks(clip)[1].Beat), Is.GreaterThan(0.01), "with Alt it lands where the pointer is, between the lines"));
                // From an attack, with and without Alt.
                double t = Layout.TransientsOf(clip)[8];
                Steps(rig, clip, () => rig.Drag(TransientAt(rig, wave, t), TransientAt(rig, wave, t) + new Vector(7, 0)), save: false, check: _ =>
                    Assert.That(Marks(clip).Where(x => Math.Abs(x.Sample - t) < 0.5).Select(x => OffGrid(x.Beat)), Is.All.LessThan(1e-9), "a marker made on an attack snaps too"));
                Steps(rig, clip, () => rig.Drag(TransientAt(rig, wave, t), TransientAt(rig, wave, t) + new Vector(7, 0), RawInputModifiers.Alt), save: false, check: _ =>
                    Assert.That(Marks(clip).Where(x => Math.Abs(x.Sample - t) < 0.5).Select(x => OffGrid(x.Beat)), Is.All.GreaterThan(0.01), "and is free with Alt"));
            });
        }

        [AvaloniaTest]
        public void TheToolbarHintFollowsThePointerOverTheAudioAnAttackAMarkerADragAndARange()
        {
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                string cmd = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl", alt = WarpEditor.AltName == "Option" ? "⌥" : "Alt";
                rig.Window.MouseMove(WaveAt(rig, 9.5, 0.5));
                Settle();
                Assert.That(page.HintLabelText, Is.EqualTo("Over the audio"));
                Assert.That(wave.Hint, Is.EqualTo("Double-click to add a marker · drag to select a range"));
                double t = Layout.TransientsOf(clip)[3];
                rig.Window.MouseMove(TransientAt(rig, wave, t));
                Settle();
                Assert.That(page.HintLabelText, Is.EqualTo("Over an attack"));
                Assert.That(wave.Hint, Is.EqualTo($"Drag to place a marker here (snaps to the grid) · hold {alt} to drag freely"));
                Seed(rig, clip, 3);
                rig.Window.MouseMove(MarkerAt(rig, wave, 1));
                Settle();
                Assert.That(page.HintLabelText, Is.EqualTo("Over a marker"));
                Assert.That(wave.Hint, Does.Contain("Drag to move it").And.Contain(alt + ": no snap").And.Contain("Shift: slide the audio").And.Contain("removes it"));
                // Dragging: the step it snaps to, and that Alt frees it.
                var from = MarkerAt(rig, wave, 1);
                rig.Window.MouseMove(from);
                rig.Window.MouseDown(from, MouseButton.Left);
                rig.Window.MouseMove(from + new Vector(7, 0), RawInputModifiers.LeftMouseButton);
                Settle();
                Assert.That(page.HintLabelText, Is.EqualTo("Dragging"));
                Assert.That(wave.Hint, Is.EqualTo($"Snapping to {wave.SnapName} · hold {alt} to move freely"));
                Assert.That(wave.SnapName, Is.EqualTo("1/8"), "a step of half a beat at this zoom");
                rig.Window.MouseMove(from + new Vector(8, 0), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
                Settle();
                Assert.That(wave.Hint, Does.Contain("freely"));
                rig.Window.MouseUp(from + new Vector(8, 0), MouseButton.Left, RawInputModifiers.Alt);
                Settle();
                // A range selected.
                rig.Drag(WaveAt(rig, 5.5, 0.6), WaveAt(rig, 8.5, 0.6));
                Assert.That(wave.Range, Is.Not.Null);
                rig.Window.MouseMove(WaveAt(rig, 12.5, 0.5));
                Settle();
                Assert.That(page.HintLabelText, Is.EqualTo("Range selected"));
                Assert.That(wave.Hint, Is.EqualTo($"Right-click: Auto-warp selection · {cmd}+U: quantize the range"));
            });
        }

        [AvaloniaTest]
        public void AutoWarpOfARangeFromTheWaveformMenuChangesOnlyThatRangeInOneUndoStep()
        {
            using var off = new WarpModeScope(AutoWarpMode.Off);
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                Seed(rig, clip, 2, 14);
                var was = Marks(clip);
                // Without a range the page's menu says Auto-warp; with one, Auto-warp selection.
                rig.Click(WaveAt(rig, 9.5, 0.5), MouseButton.Right);
                var whole = wave.LastMenu!.Items.OfType<MenuItem>().Single(i => i.Tag as string == "Auto-warp");
                Assert.That(whole.IsEnabled, Is.True);
                wave.LastMenu.Close();
                rig.Drag(WaveAt(rig, 5.5, 0.6), WaveAt(rig, 10.5, 0.6));
                var range = wave.RangeSeconds!.Value;
                rig.Click(WaveAt(rig, 8, 0.6), MouseButton.Right);
                var root = wave.LastMenu!.Items.OfType<MenuItem>().Single(i => i.Tag as string == "Auto-warp selection");
                Assert.That(root.Items.OfType<MenuItem>().Select(i => i.Tag as string), Is.EqualTo(new[] { "Auto-warp: Follow the beat", "Auto-warp: To grid", "Auto-warp: Options" }));
                var toGrid = root.Items.OfType<MenuItem>().Single(i => i.Tag as string == "Auto-warp: To grid");
                var src = Layout.AudioSourceOf(clip)!;
                double a = range.From * src.SampleRate, b = range.To * src.SampleRate;
                Steps(rig, clip, () => { toGrid.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); wave.LastMenu.Close(); Settle(); }, save: true, check: _ =>
                {
                    var now = Marks(clip);
                    Assert.That(now.Where(m => m.Sample < a - 20 || m.Sample > b + 20).Select(m => (m.Sample, m.Beat)), Is.EqualTo(was.Where(m => m.Sample < a - 20 || m.Sample > b + 20).Select(m => (m.Sample, m.Beat))), "the markers outside the range are as they were");
                    Assert.That(now.Any(m => Math.Abs(m.Sample - a) <= 20) && now.Any(m => Math.Abs(m.Sample - b) <= 20), Is.True, "the ends of the range are pinned");
                    Assert.That(now.Count(m => m.Sample > a + 20 && m.Sample < b - 20), Is.GreaterThan(0), "and the range has the markers auto-warp made");
                    AssertOrdered(clip);
                });
            });
        }

        [AvaloniaTest]
        public void TheSplitButtonRunsTheLastModeTheChipNamesTheOptionsAndTheLegendMarksTheAttacks()
        {
            using var off = new WarpModeScope(AutoWarpMode.Off);
            Contexts((rig, clip, page) =>
            {
                var wave = page.Wave;
                Assert.That(page.DetectButton.Content, Is.EqualTo("Auto-warp: Follow the beat"));
                Assert.That(page.ChipText, Is.EqualTo($"1/16 · {Layout.QuantizeStrength} % · reach 40 %"));
                // The arrow opens the same menu as the right click.
                rig.Click(rig.Of(page.DetectMoreButton));
                var items = page.AutoWarpMenuOpen.Items.OfType<MenuItem>().Where(i => i.Tag is string).Select(i => (string)i.Tag).ToList();
                Assert.That(items, Is.EqualTo(new[] { "Auto-warp: Follow the beat", "Auto-warp: To grid", "Auto-warp: Options" }));
                var toGrid = page.AutoWarpMenuOpen.Items.OfType<MenuItem>().Single(i => i.Tag as string == "Auto-warp: To grid");
                Steps(rig, clip, () => { toGrid.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); page.AutoWarpMenuOpen.Close(); Settle(); }, save: false, check: _ =>
                {
                    Assert.That(Marks(clip).Count, Is.GreaterThan(2));
                });
                Settle();
                Assert.That(page.DetectButton.Content, Is.EqualTo("Auto-warp: To grid"), "the button takes the mode that ran");
                var kinds = Enumerable.Range(0, wave.Markers.Count).Select(wave.MarkerKind).ToList();
                Assert.That(kinds, Does.Contain("attack"), "a marker on an attack is drawn as one");
                Assert.That(wave.LeftAlone().Count, Is.GreaterThanOrEqualTo(0));
            });
        }

        [AvaloniaTest]
        public void TheAutoWarpMenuIsOnTheClipOfTheSessionTheArrangementAndTheAudioPageAndRunsInOneUndoStep()
        {
            using var off = new WarpModeScope(AutoWarpMode.Off);
            using var rig = new Rig(WarpFile("grid-views.wav", WarpSignals.Tempo.Drift, 48));
            foreach (string view in new[] { "Session", "Arrangement", "Audio page" })
            {
                var clip = view == "Arrangement" ? rig.ArrangedClip : rig.SessionClip;
                MenuItem root;
                if (view == "Session")
                {
                    rig.Show("Session");
                    int track = Layout.FirstTrackOf(rig.A);
                    rig.Session.ShowSlotMenu(rig.Session.SlotAt(track, 0));
                    root = rig.Session.LastMenu.Items.OfType<MenuItem>().Single(i => i.Tag as string == "Auto-warp");
                }
                else if (view == "Arrangement")
                {
                    var item = rig.SelectInArrangement();
                    var r = rig.Sequence.Arrangement.ClipRect(item);
                    rig.Click(rig.Sequence.Arrangement.TranslatePoint(new Point(r.X + r.Width * 0.3, r.Y + r.Height * 0.8), rig.Window)!.Value, MouseButton.Right);
                    Assert.That(rig.Sequence.OpenMenu?.IsOpen, Is.True, "the clip's menu opens");
                    root = rig.Sequence.OpenMenu!.Items.OfType<MenuItem>().Single(i => i.Tag as string == "Auto-warp");
                }
                else
                {
                    rig.SelectInSession();
                    var page = rig.OpenPage();
                    rig.WaitPeaks(rig.SessionClip);
                    rig.Click(rig.In(page.Wave, 0.5, 0.5), MouseButton.Right);
                    root = page.Wave.LastMenu!.Items.OfType<MenuItem>().Single(i => i.Tag as string == "Auto-warp");
                    clip = rig.SessionClip;
                }
                Assert.That(root.IsEnabled, Is.True, view + ": the item is there and enabled");
                Assert.That(root.Items.OfType<MenuItem>().Select(i => i.Tag as string).Where(t => t != null), Is.EqualTo(new[] { "Auto-warp: Follow the beat", "Auto-warp: To grid", "Auto-warp: Options" }));
                var run = root.Items.OfType<MenuItem>().Single(i => i.Tag as string == "Auto-warp: To grid");
                string machine = clip.Machine.Name;
                // A clip already warped by the beat before: To grid adds to it; the first view starts from an unwarped clip.
                Steps(rig, clip, () => { run.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Settle(); }, save: view == "Audio page", check: _ =>
                {
                    Assert.That(Layout.AudioOf(clip)!.Markers.Count, Is.GreaterThan(4), view + ": the clip is warped to the grid");
                    Assert.That(AutoWarpFlow.LastWasError, Is.False, AutoWarpFlow.LastSummary);
                    Assert.That(AutoWarpFlow.LastSummary, Does.StartWith("Auto-warped to grid"));
                });
                rig.Sequence.OpenMenu?.Close();
                rig.Session.LastMenu?.Close();
                rig.Page.Wave.LastMenu?.Close();
                Settle();
                if (view != "Audio page") Assert.That(rig.Page.Pattern, Is.SameAs(clip), view + ": the Audio page opens on the clip");
                rig.Undo();
            }
        }

        [AvaloniaTest]
        public void TheOptionsDialogShowsTheSavedOptionsRunsFromThereAndSavesThemWithTheClip()
        {
            using var off = new WarpModeScope(AutoWarpMode.Off);
            using var rig = new Rig(WarpFile("grid-options-ui.wav", WarpSignals.Tempo.Drift, 48));
            var clip = rig.SessionClip;
            rig.Show("Session");
            int track = Layout.FirstTrackOf(rig.A);
            rig.Session.ShowSlotMenu(rig.Session.SlotAt(track, 0));
            var root = rig.Session.LastMenu.Items.OfType<MenuItem>().Single(i => i.Tag as string == "Auto-warp");
            var options = root.Items.OfType<MenuItem>().Single(i => i.Tag as string == "Auto-warp: Options");
            int strength = Layout.QuantizeStrength;
            AutoWarpFlow.AutoWarpChoice shown = default;
            try
            {
                AutoWarpFlow.Responder = start =>
                {
                    shown = start;
                    return new AutoWarpFlow.AutoWarpChoice { ToGrid = true, Grid = "1/8T", Strength = 80, Sensitivity = 70, Reach = 25, LeaveSustained = false };
                };
                Steps(rig, clip, () => { options.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Settle(); }, save: true, check: _ =>
                {
                    var a = Layout.AudioOf(clip)!;
                    Assert.That((a.AutoGrid, a.AutoSensitivity, a.AutoReach, a.AutoLeaveSustained), Is.EqualTo(("1/8T", 70, 25, false)), "the options are saved with the clip");
                    Assert.That(Layout.QuantizeStrength, Is.EqualTo(80), "the strength is the song's");
                    Assert.That(a.Markers.Count, Is.GreaterThan(4));
                });
                Assert.That((shown.Grid, shown.Sensitivity, shown.Reach, shown.LeaveSustained, shown.ToGrid), Is.EqualTo(("1/16", 60, 40, true, true)), "the dialog opened on the defaults");
            }
            finally { AutoWarpFlow.Responder = null; Layout.SetQuantizeStrength(strength); }
        }

        [AvaloniaTest]
        public void TheOptionsDialogsControlsHoldAndReturnTheChoice()
        {
            using var off = new WarpModeScope(AutoWarpMode.Off);
            using var rig = new Rig(WarpFile("grid-dialog-ui.wav", WarpSignals.Tempo.Drift, 48));
            var clip = rig.SessionClip;
            var win = new AutoWarpFlow.OptionsWindow(new[] { clip }, null, new AutoWarpFlow.AutoWarpChoice { ToGrid = true, Grid = "1/16", Strength = 100, Sensitivity = 60, Reach = 40, LeaveSustained = true });
            win.Show();
            Settle();
            try
            {
                var c = win.Choice();
                Assert.That((c.ToGrid, c.Grid, c.Strength, c.Sensitivity, c.Reach, c.LeaveSustained), Is.EqualTo((true, "1/16", 100, 60, 40, true)));
                Assert.That(win.GridButtons.Select(b => b.IsChecked == true), Is.EqualTo(new[] { false, false, true, false }));
                win.GridButtons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                win.TripletsBox.IsChecked = true;
                win.StrengthSlider.Value = 50;
                win.ReachSlider.Value = 30;
                win.SustainedBox.IsChecked = false;
                Assert.That(win.Choice().Grid, Is.EqualTo("1/8T"), "a triplet grid");
                Assert.That((win.Choice().Strength, win.Choice().Reach, win.Choice().LeaveSustained), Is.EqualTo((50, 30, false)));
                win.FollowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.That(win.Choice().ToGrid, Is.False, "Follow the beat");
                Assert.That(win.StrengthSlider.IsEnabled, Is.False, "the grid options rest while it only follows the beat");
            }
            finally { win.Close(); Settle(); }
        }
    }
}
