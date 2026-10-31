using BuzzGUI.Interfaces;
using ReBuzz.Core.Clips;
using ReBuzz.Core.Session;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ReBuzz.AppViews.Session
{
    /// <summary>
    /// Warp markers of an audio clip (docs/specs/session-view.md 1.0, Live's): the audio drawn
    /// along the clip's beats, as it plays. A marker pins a moment of the audio to a beat; the
    /// audio between two markers is stretched to fit. Double-click adds one (where it is: nothing
    /// moves until it is dragged), drag moves it along the beats (snapped to 1/16 beat; Shift
    /// for none), right-click removes it. Each change is one undo step of the session.
    /// </summary>
    internal sealed class WarpMarkerWindow : Window
    {
        readonly SessionLayout layout;
        readonly ISession session;
        readonly ISequence sequence;
        readonly IPattern pattern;
        readonly WarpView view;
        readonly TextBlock status;
        readonly DispatcherTimer timer;

        public WarpMarkerWindow(SessionLayout layout, ISession session, ISequence sequence, IPattern pattern)
        {
            this.layout = layout;
            this.session = session;
            this.sequence = sequence;
            this.pattern = pattern;
            Title = $"Warp markers: {pattern.Name}";
            Width = 960;
            Height = 340;
            MinWidth = 360;
            MinHeight = 200;
            Background = SessionPalette.Window;
            Foreground = SessionPalette.Text;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            view = new WarpView(this);
            status = new TextBlock { Foreground = SessionPalette.DimText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background = SessionPalette.Bar,
                Children =
                {
                    new SessionView.FlatButton("Fit", () => view.Fit(), "Show the whole clip") { Margin = new Thickness(6, 4, 4, 4) },
                    new SessionView.FlatButton("Clear markers", () => Commit(new List<(double, double)>()), "Back to straight, at the clip's tempo") { Margin = new Thickness(0, 4, 4, 4) },
                    status
                }
            };
            var hint = new TextBlock
            {
                Foreground = SessionPalette.DimText,
                Margin = new Thickness(8, 3, 8, 3),
                FontSize = 11,
                Text = "Double-click: add a marker · drag a marker: move it along the beats (Shift: no snap) · right-click a marker: remove it · " +
                       "wheel: zoom · drag the background: scroll"
            };
            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            DockPanel.SetDock(hint, Dock.Bottom);
            root.Children.Add(hint);
            root.Children.Add(view);
            Content = root;

            layout.Changed += Reload;
            timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (s, e) => view.ShowPlayhead(PlayingBeat());
            Loaded += (s, e) => { view.Fit(); timer.Start(); };
            Closed += (s, e) => { layout.Changed -= Reload; timer.Stop(); };
            Reload();
        }

        internal WarpView View => view;
        internal int BeatsPerBar => Math.Max(1, session.BeatsPerBar);

        void Reload()
        {
            var audio = layout.AudioOf(pattern);
            var source = layout.AudioSourceOf(pattern);
            if (audio == null || source == null || pattern.Machine == null)
            {
                Close();
                return;
            }
            view.Load(audio, source, layout.TransientsOf(pattern));
            status.Text = audio.Markers.Count >= 2
                ? $"{audio.Markers.Count} markers · {view.ClipBeats:0.##} beats"
                : $"Straight at {audio.Bpm:0.##} BPM · {view.ClipBeats:0.##} beats";
        }

        /// <summary>The beat of the clip playing on its track, or NaN.</summary>
        double PlayingBeat()
        {
            if (sequence == null || session.GetState(sequence, pattern) != ClipState.Playing) return double.NaN;
            int tick = session.GetClipPosition(sequence);
            int tpb = Math.Max(1, BuzzGUI.Common.Global.Buzz?.TPB ?? 4);
            return tick < 0 ? double.NaN : tick / (double)tpb;
        }

        /// <summary>New markers (fewer than two: straight), as one undo step.</summary>
        internal void Commit(List<(double Sample, double Beat)> markers)
        {
            var audio = layout.AudioOf(pattern);
            if (audio == null) return;
            audio.Markers = markers.Count >= 2 ? markers.OrderBy(m => m.Beat).ToList() : new List<(double, double)>();
            layout.SetAudio(pattern, audio);
        }

        /// <summary>The audio along the clip's beats, its markers and transients.</summary>
        internal sealed class WarpView : FrameworkElement
        {
            const double Grab = 5;
            const double TopBand = 16;

            static readonly Pen BarPen = Frozen(new Pen(SessionPalette.DimText, 1));
            static readonly Pen BeatPen = Frozen(new Pen(SessionPalette.Header, 1));
            static readonly Pen WavePen = Frozen(new Pen(SessionPalette.Fader, 1));
            static readonly Pen MarkerPen = Frozen(new Pen(SessionPalette.Selection, 1.5));
            static readonly Pen TransientPen = Frozen(new Pen(SessionPalette.DimText, 1));
            static readonly Pen EndPen = Frozen(new Pen(SessionPalette.Learn, 1) { DashStyle = DashStyles.Dash });
            static readonly Pen PlayPen = Frozen(new Pen(SessionPalette.Progress, 1.5));
            static readonly Typeface Face = new Typeface("Segoe UI");

            readonly WarpMarkerWindow owner;
            SessionDocument.AudioSettings audio;
            AudioSource source;
            IReadOnlyList<double> transients = Array.Empty<double>();
            List<(double Sample, double Beat)> markers = new List<(double, double)>();
            WarpMap map;
            double pixelsPerBeat = 40;
            double offset;                  // pixels scrolled
            int dragging = -1;
            bool panning;
            double panFrom, offsetFrom;
            double playhead = double.NaN;
            bool fitted;

            public WarpView(WarpMarkerWindow owner)
            {
                this.owner = owner;
                Focusable = true;
                ClipToBounds = true;
            }

            static Pen Frozen(Pen pen)
            {
                pen.Freeze();
                return pen;
            }

            /// <summary>The clip's length in beats (where its audio ends).</summary>
            public double ClipBeats => map == null || source == null ? 0 : map.BeatAt(source.Length);

            internal IReadOnlyList<(double Sample, double Beat)> Markers => markers;

            public void Load(SessionDocument.AudioSettings settings, AudioSource audioSource, IReadOnlyList<double> found)
            {
                if (dragging >= 0) return;               // a drag in progress isn't disturbed
                audio = settings;
                source = audioSource;
                transients = found ?? Array.Empty<double>();
                markers = settings.Markers.Count >= 2 ? settings.Markers.OrderBy(m => m.Beat).ToList() : new List<(double, double)>();
                map = MapNow();
                if (!fitted && ActualWidth > 0) Fit();
                InvalidateVisual();
            }

            WarpMap MapNow() => markers.Count >= 2
                ? new WarpMap(markers.Select(m => new WarpMarker(m.Sample, m.Beat)))
                : WarpMap.Straight(source.Length, source.Seconds * Math.Max(1, audio.Bpm) / 60);

            public void Fit()
            {
                if (source == null || ActualWidth <= 0) return;
                fitted = true;
                pixelsPerBeat = Math.Clamp((ActualWidth - 20) / Math.Max(1, ClipBeats), 2, 2000);
                offset = -10;
                InvalidateVisual();
            }

            public void ShowPlayhead(double beat)
            {
                if (beat.Equals(playhead) || double.IsNaN(beat) && double.IsNaN(playhead)) return;
                playhead = beat;
                InvalidateVisual();
            }

            double X(double beat) => beat * pixelsPerBeat - offset;
            double BeatAt(double x) => (x + offset) / pixelsPerBeat;

            protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
            {
                base.OnRenderSizeChanged(sizeInfo);
                if (!fitted) Fit();
            }

            protected override void OnRender(DrawingContext dc)
            {
                double w = ActualWidth, h = ActualHeight;
                dc.DrawRectangle(SessionPalette.Empty, null, new Rect(0, 0, w, h));
                if (source == null || map == null || w <= 0 || h <= TopBand) return;
                int bar = owner.BeatsPerBar;
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

                // Beats and bars; bar numbers along the top.
                int step = pixelsPerBeat >= 6 ? 1 : pixelsPerBeat * bar >= 6 ? bar : bar * 4;
                for (long b = (long)Math.Floor(BeatAt(0) / step) * step; b <= BeatAt(w); b += step)
                {
                    double x = Math.Round(X(b)) + 0.5;
                    bool isBar = b % bar == 0;
                    dc.DrawLine(isBar ? BarPen : BeatPen, new Point(x, isBar ? 0 : TopBand), new Point(x, h));
                    if (isBar && pixelsPerBeat * bar >= 24)
                    {
                        var text = new FormattedText((b / bar + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight, Face, 10, SessionPalette.DimText, dpi);
                        dc.DrawText(text, new Point(x + 3, 1));
                    }
                }

                // The audio, as it plays: each column the peaks of what plays in its beats.
                double mid = TopBand + (h - TopBand) / 2, half = (h - TopBand) / 2 * 0.92;
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    for (int x = 0; x < (int)w; x++)
                    {
                        double s0 = map.SampleAt(BeatAt(x)), s1 = map.SampleAt(BeatAt(x + 1));
                        long from = (long)Math.Floor(Math.Min(s0, s1)), to = (long)Math.Ceiling(Math.Max(s0, s1));
                        if (to < 0 || from >= source.Length) continue;
                        from = Math.Max(0, from);
                        to = Math.Min(source.Length - 1, Math.Max(from, to));
                        long stride = Math.Max(1, (to - from) / 96);
                        float lo = 0, hi = 0;
                        for (long i = from; i <= to; i += stride)
                        {
                            float v = source.Mono(i);
                            if (v < lo) lo = v;
                            if (v > hi) hi = v;
                        }
                        g.BeginFigure(new Point(x + 0.5, mid - hi * half), false, false);
                        g.LineTo(new Point(x + 0.5, mid - lo * half - 1), true, false);
                    }
                }
                geometry.Freeze();
                dc.DrawGeometry(null, WavePen, geometry);

                // Transients (where Beats mode cuts), at the bottom.
                foreach (var t in transients)
                {
                    double x = X(map.BeatAt(t));
                    if (x < -1 || x > w + 1) continue;
                    dc.DrawLine(TransientPen, new Point(x, h - 8), new Point(x, h));
                }

                // The audio's end: the clip's.
                double end = X(ClipBeats);
                dc.DrawLine(EndPen, new Point(end, TopBand), new Point(end, h));

                // Markers: a line and a handle in the top band.
                foreach (var m in markers)
                {
                    double x = X(m.Beat);
                    if (x < -Grab || x > w + Grab) continue;
                    dc.DrawLine(MarkerPen, new Point(x, TopBand), new Point(x, h));
                    var handle = new StreamGeometry();
                    using (var g = handle.Open())
                    {
                        g.BeginFigure(new Point(x - 5, 2), true, true);
                        g.LineTo(new Point(x + 5, 2), true, false);
                        g.LineTo(new Point(x, TopBand - 1), true, false);
                    }
                    handle.Freeze();
                    dc.DrawGeometry(SessionPalette.Selection, null, handle);
                }

                if (!double.IsNaN(playhead))
                {
                    double x = X(playhead);
                    dc.DrawLine(PlayPen, new Point(x, 0), new Point(x, h));
                }
            }

            int MarkerAt(double x)
            {
                int best = -1;
                double distance = Grab;
                for (int i = 0; i < markers.Count; i++)
                {
                    double d = Math.Abs(X(markers[i].Beat) - x);
                    if (d <= distance) { distance = d; best = i; }
                }
                return best;
            }

            /// <summary>Markers to start from when there are none: the clip's ends, as it plays now.</summary>
            void EnsureEnds()
            {
                if (markers.Count >= 2) return;
                markers = new List<(double, double)> { (0, 0), (source.Length, map.BeatAt(source.Length)) };
            }

            /// <summary>Adds a marker at a beat (where the audio already is: nothing moves).</summary>
            internal void AddMarker(double beat)
            {
                if (source == null) return;
                double sample = Math.Clamp(map.SampleAt(beat), 0, source.Length);
                EnsureEnds();
                if (markers.Any(m => Math.Abs(m.Sample - sample) < 1 || Math.Abs(m.Beat - beat) < 1e-6)) return;
                markers.Add((sample, map.BeatAt(sample)));
                markers.Sort((a, b) => a.Beat.CompareTo(b.Beat));
                owner.Commit(markers.ToList());
            }

            /// <summary>Moves a marker to a beat, between its neighbours.</summary>
            internal void MoveMarker(int index, double beat)
            {
                if (index < 0 || index >= markers.Count) return;
                const double Gap = 1.0 / 64;
                double lo = index > 0 ? markers[index - 1].Beat + Gap : double.NegativeInfinity;
                double hi = index + 1 < markers.Count ? markers[index + 1].Beat - Gap : double.PositiveInfinity;
                markers[index] = (markers[index].Sample, Math.Clamp(beat, lo, hi));
                map = MapNow();
                InvalidateVisual();
            }

            internal void RemoveMarker(int index)
            {
                if (index < 0 || index >= markers.Count) return;
                markers.RemoveAt(index);
                owner.Commit(markers.Count >= 2 ? markers.ToList() : new List<(double, double)>());
            }

            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                Focus();
                if (source == null) return;
                double x = e.GetPosition(this).X;
                int hit = MarkerAt(x);
                if (e.ClickCount == 2 && hit < 0)
                {
                    AddMarker(BeatAt(x));
                    e.Handled = true;
                    return;
                }
                if (hit >= 0)
                {
                    dragging = hit;
                }
                else
                {
                    panning = true;
                    panFrom = x;
                    offsetFrom = offset;
                }
                CaptureMouse();
                e.Handled = true;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                double x = e.GetPosition(this).X;
                if (dragging >= 0)
                {
                    double beat = BeatAt(x);
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) beat = Math.Round(beat * 16) / 16;
                    MoveMarker(dragging, beat);
                }
                else if (panning)
                {
                    offset = offsetFrom - (x - panFrom);
                    InvalidateVisual();
                }
                else
                {
                    Cursor = MarkerAt(x) >= 0 ? Cursors.SizeWE : null;
                }
            }

            protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
            {
                if (IsMouseCaptured) ReleaseMouseCapture();
                e.Handled = true;
            }

            protected override void OnLostMouseCapture(MouseEventArgs e)
            {
                base.OnLostMouseCapture(e);
                if (dragging >= 0)
                {
                    dragging = -1;
                    owner.Commit(markers.ToList());
                }
                panning = false;
            }

            protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
            {
                int hit = MarkerAt(e.GetPosition(this).X);
                if (hit >= 0) RemoveMarker(hit);
                e.Handled = true;
            }

            protected override void OnMouseWheel(MouseWheelEventArgs e)
            {
                double x = e.GetPosition(this).X;
                double beat = BeatAt(x);
                pixelsPerBeat = Math.Clamp(pixelsPerBeat * (e.Delta > 0 ? 1.25 : 0.8), 2, 4000);
                offset = beat * pixelsPerBeat - x;
                InvalidateVisual();
                e.Handled = true;
            }
        }
    }
}
