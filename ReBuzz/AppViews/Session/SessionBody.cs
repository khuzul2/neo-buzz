using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ReBuzz.AppViews.Session
{
    // Fork: the Session View's body under its top bar (gallery win-703, win-850/853): the grid with
    // the scene column on its right, the mixer under them with the master strip under the scenes.
    // The scene column and the master strip follow the last track while the tracks fit, and stay at
    // the right edge (frozen, as Live's master track) when they don't: the tracks scroll under them,
    // so scenes can always be launched. The mixer row is as tall as its taller half; the grid gets
    // the rest, and fills it with spare empty rows (SessionView.FitRows). The clip panel sits beside
    // all of it, full height (SessionView docks it first). As the studio's SessionBody.
    internal sealed partial class SessionView
    {
        internal sealed class SessionBody : Panel
        {
            readonly FrameworkElement tracks, scenes, mixer, master;
            readonly Action<double, bool> fitRows;

            /// <param name="fitRows">Called with the grid's height before it is measured (and whether the tracks scroll sideways).</param>
            public SessionBody(FrameworkElement tracks, FrameworkElement scenes, FrameworkElement mixer, FrameworkElement master, Action<double, bool> fitRows)
            {
                this.tracks = tracks;
                this.scenes = scenes;
                this.mixer = mixer;
                this.master = master;
                this.fitRows = fitRows;
                Children.Add(tracks);
                Children.Add(scenes);
                Children.Add(mixer);
                Children.Add(master);
            }

            /// <summary>The scene column stays at the right edge because the tracks don't fit.</summary>
            public bool ScenesFrozen { get; private set; }

            double leftWidth, rightWidth, mixerHeight;

            protected override Size MeasureOverride(Size available)
            {
                var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
                scenes.Measure(infinite);
                tracks.Measure(infinite);
                bool mixerShown = mixer.Visibility == Visibility.Visible;
                mixer.Measure(infinite);
                master.Measure(infinite);

                rightWidth = Math.Max(scenes.DesiredSize.Width, mixerShown ? master.DesiredSize.Width : 0);
                double leftWanted = Math.Max(tracks.DesiredSize.Width, mixerShown ? mixer.DesiredSize.Width : 0);
                double width = double.IsInfinity(available.Width) ? leftWanted + rightWidth : available.Width;
                leftWidth = Math.Max(0, Math.Min(leftWanted, width - rightWidth));
                ScenesFrozen = leftWanted > leftWidth + 0.5;
                mixerHeight = mixerShown ? Math.Max(mixer.DesiredSize.Height, master.DesiredSize.Height) : 0;
                double height = double.IsInfinity(available.Height) ? tracks.DesiredSize.Height + mixerHeight : available.Height;
                double gridHeight = Math.Max(0, height - mixerHeight);

                fitRows(gridHeight, ScenesFrozen);
                tracks.Measure(new Size(leftWidth, gridHeight));
                scenes.Measure(new Size(rightWidth, gridHeight));
                mixer.Measure(new Size(leftWidth, mixerHeight));
                master.Measure(new Size(rightWidth, mixerHeight));
                return new Size(width, height);
            }

            protected override Size ArrangeOverride(Size final)
            {
                double gridHeight = Math.Max(0, final.Height - mixerHeight);
                tracks.Arrange(new Rect(0, 0, leftWidth, gridHeight));
                scenes.Arrange(new Rect(leftWidth, 0, rightWidth, gridHeight));
                mixer.Arrange(new Rect(0, gridHeight, leftWidth, mixerHeight));
                master.Arrange(new Rect(leftWidth, gridHeight, rightWidth, mixerHeight));
                return final;
            }
        }

        /// <summary>
        /// Spare empty slots under a track's last scene, as many as the grid has room for (drawn,
        /// one element per column): double-click one to make a clip there, the scenes down to it
        /// added first.
        /// </summary>
        internal sealed class SpareSlots : FrameworkElement
        {
            readonly bool scenesColumn;

            public int Track { get; }

            public SpareSlots(SessionView view, int track, bool scenesColumn = false)
            {
                Track = track;
                this.scenesColumn = scenesColumn;
                ToolTip = scenesColumn
                    ? "Room for more scenes: double-click to add the scenes down to here (+ Scene adds one)"
                    : "Empty slot below the last scene: double-click creates a clip here, adding the scenes down to it";
                MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ClickCount != 2) return;
                    int row = (int)(e.GetPosition(this).Y / (RowHeight + Gap));
                    if (row >= 0 && row < Rows)
                        view.UseSpareRow(scenesColumn ? -1 : Track, view.sceneCount + row);
                    e.Handled = true;
                };
            }

            /// <summary>The rows drawn.</summary>
            public int Rows => (int)Math.Floor((ActualHeight + 0.5) / (RowHeight + Gap));

            protected override void OnRenderSizeChanged(SizeChangedInfo info)
            {
                base.OnRenderSizeChanged(info);
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                // Hit-testable everywhere (double-click), drawn as dim empty slots.
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
                var brush = scenesColumn ? SessionPalette.Scene : SessionPalette.Empty;
                double width = Math.Max(0, ActualWidth - (scenesColumn ? 0 : Gap));
                dc.PushOpacity(0.55);
                for (int i = 0; i < Rows; i++)
                    dc.DrawRectangle(brush, null, new Rect(0, i * (RowHeight + Gap), width, RowHeight));
                dc.Pop();
            }
        }
    }
}
