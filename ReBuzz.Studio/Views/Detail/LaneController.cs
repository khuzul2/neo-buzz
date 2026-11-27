// Fork (docs/specs/clips.md §5.5, T2; daw-modes.md EV1): the point editing of an envelope lane, whichever way the lane lies. EV1's lane under the
// piano roll runs time left to right; T2's lane beside a tracker column runs it top to bottom. What they share is here: the points of the
// envelope, the selection, hit-testing, the gestures (click adds, drag moves, drag on empty space is a band, double click deletes, R, Delete,
// copy and paste) and the edits, each one step of the song's history (ClipEnvelopeEditor). What differs is the geometry (where a time and a value
// are on the screen) and the drawing; each lane brings its own geometry (ILaneGeometry) and draws what this reports.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Views.Detail
{
    using Control = Avalonia.Controls.Control;

    /// <summary>Where times and values are on a lane's screen. Times are the clip's (1/240 tick).</summary>
    internal interface ILaneGeometry
    {
        /// <summary>The place of a point of the envelope, in the lane's own coordinates.</summary>
        Point PointOf(int time, int value);

        /// <summary>The clip time at a place (not snapped).</summary>
        int TimeAt(Point p);

        /// <summary>The value at a place, inside the parameter's range.</summary>
        int ValueAt(Point p);

        /// <summary>A time on the lane's grid (the rows of a column, the piano roll's grid); with <paramref name="noSnap"/> it stays as it is.</summary>
        int Snap(int time, bool noSnap);

        /// <summary>A press on empty space here adds a point (it is inside the clip), and not outside it.</summary>
        bool InClip(Point p);
    }

    /// <summary>
    /// The pointer and key handling of a lane, in one place: the Detail panel's lane (EnvelopeLane), the Arrangement's (the same lane under its roll)
    /// and the tracker's (TrackerLane) all pass their events through this, so the gestures cannot differ. Alt held for a drag moves a point without
    /// snapping, in both (Shift is the additive selection).
    /// </summary>
    internal static class LaneInput
    {
        /// <summary>The gestures, for a hint line and for the guide.</summary>
        public const string Gestures = "Click: add a point · drag: move (Alt: no snap) · drag on empty space: select · Shift or Ctrl: add to the selection · R: ramp or step · Delete";

        /// <summary>Whether a drag leaves the grid (Alt held).</summary>
        public static bool NoSnap(KeyModifiers mods) => mods.HasFlag(KeyModifiers.Alt);

        /// <summary>A press; <paramref name="minX"/> is the left edge of the drawing area (a gutter takes no press).</summary>
        public static void Pressed(LaneController lane, Control owner, PointerPressedEventArgs e, double minX = 0)
        {
            if (lane.Env == null || !e.GetCurrentPoint(owner).Properties.IsLeftButtonPressed) return;
            var p = e.GetPosition(owner);
            if (p.X < minX) return;
            owner.Focus();
            var result = lane.Pressed(p, e.KeyModifiers, e.ClickCount);
            if (result == LaneController.Press.NotHandled) return;
            if (result == LaneController.Press.Captured) e.Pointer.Capture(owner);
            e.Handled = true;
        }

        public static void Moved(LaneController lane, Control owner, PointerEventArgs e) => lane.PointerMoved(e.GetPosition(owner), NoSnap(e.KeyModifiers));

        public static void Released(LaneController lane, Control owner, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Left || !lane.IsDragging) return;
            bool done = lane.Released(e.GetPosition(owner), NoSnap(e.KeyModifiers));
            e.Pointer.Capture(null);
            if (done) e.Handled = true;
        }

        public static void Key(LaneController lane, KeyEventArgs e)
        {
            if (lane.KeyDown(e.Key, e.KeyModifiers)) e.Handled = true;
        }
    }

    /// <summary>The state and the edits of one lane over one envelope: shared by the Detail panel's lane (EnvelopeLane) and the tracker's (TrackerLane).</summary>
    internal sealed class LaneController
    {
        const double Grab = 8;
        const double DragDistance = 3;

        public enum Gesture { None, Points, Band, Add }

        public enum Press { NotHandled, Handled, Captured }

        readonly ReBuzzCore core;
        readonly ILaneGeometry geometry;
        readonly ClipEnvelopeEditor editor;
        readonly Func<ClipEnvelope> read;
        readonly Action invalidate;
        readonly Func<string> sourceId;
        readonly HashSet<int> selected = new();
        long signature;
        bool collapseOnRelease;

        public LaneController(ReBuzzCore core, ILaneGeometry geometry, Func<ClipEnvelope> read, Action invalidate, Func<string> sourceId)
        {
            this.core = core;
            this.geometry = geometry;
            this.read = read;
            this.invalidate = invalidate;
            this.sourceId = sourceId;
            editor = new ClipEnvelopeEditor(core);
        }

        public ClipEnvelope Env { get; private set; }

        /// <summary>Counts every change of what the lane draws (the points, the selection, a drag's preview, the hover): a lane keeps what it built while this stays.</summary>
        public int Version { get; private set; }

        void Touch()
        {
            Version++;
            invalidate();
        }

        public IReadOnlyCollection<int> Selection => selected;

        public bool IsSelected(int time) => selected.Contains(time);

        public string Refusal { get; private set; }

        public Gesture Current { get; private set; }

        public bool IsDragging => Current != Gesture.None;

        /// <summary>The pointer has moved far enough since the press to count as a drag.</summary>
        public bool Moved { get; private set; }

        public Point Down { get; private set; }

        public Point Now { get; private set; }

        public AutoPoint Grabbed { get; private set; }

        public int PreviewDt { get; private set; }

        public int PreviewDv { get; private set; }

        public int HoverTime { get; private set; } = -1;

        public int InsertTime { get; private set; }

        public bool Additive { get; private set; }

        /// <summary>A point was added, moved, removed or changed (one step of the history).</summary>
        public event Action Edited;

        public event Action SelectionChanged;

        int Last => Math.Max(0, (Env?.Length ?? 1) - 1);

        // ------------------------------------------------------------------ reading

        static long Sign(ClipEnvelope e)
        {
            if (e == null) return -1;
            long sum = e.Length;
            foreach (var p in e.Engine.Points) sum = sum * 31 + p.Time * 7 + p.Value * 131 + (p.Ramp ? 1 : 0);
            if (e.Tracker != null) foreach (var p in e.Tracker.Points) sum = sum * 37 + p.Time * 11 + p.Value * 17 + (p.Ramp ? 3 : 0);
            return sum * 31 + e.Overlaps.Count;
        }

        /// <summary>Reads the clip again (after an edit, an undo, the agent); true when what the lane shows changed.</summary>
        public bool Reload(bool force = false)
        {
            var next = read();
            long sig = Sign(next);
            if (!force && sig == signature && ReferenceEquals(next?.Clip, Env?.Clip)) return false;
            signature = sig;
            Env = next;
            var times = Env?.Points.Select(p => p.Time).ToHashSet() ?? new HashSet<int>();
            if (selected.RemoveWhere(t => !times.Contains(t)) > 0) SelectionChanged?.Invoke();
            Touch();
            return true;
        }

        // ------------------------------------------------------------------ what the lane draws while a gesture goes on

        /// <summary>The points as they are drawn: while a drag shows its preview, the selected ones are where they would land.</summary>
        public IReadOnlyList<AutoPoint> Shown()
        {
            if (Env == null) return Array.Empty<AutoPoint>();
            if (Current != Gesture.Points || !Moved) return Env.Points;
            var moving = Env.Points.Where(p => selected.Contains(p.Time)).Select(Displaced).ToList();
            var landed = moving.Select(p => p.Time).ToHashSet();
            return Env.Points.Where(p => !selected.Contains(p.Time) && !landed.Contains(p.Time)).Concat(moving).OrderBy(p => p.Time).ToList();
        }

        public AutoPoint Displaced(AutoPoint p) => p with
        {
            Time = Math.Clamp(p.Time + PreviewDt, 0, Last),
            Value = Math.Clamp(p.Value + PreviewDv, Env.Param.Min, Env.Param.Max),
        };

        /// <summary>Whether the drag shows a preview (the points are drawn where they would land).</summary>
        public bool Previewing => Current == Gesture.Points && Moved;

        public int LandedTime(int time) => Math.Clamp(time + PreviewDt, 0, Last);

        /// <summary>The tooltip text for the point under the pointer (or being dragged), or null.</summary>
        public string TipText => TipFor(Current == Gesture.Points && Moved ? Grabbed.Time : HoverTime);

        public string TipFor(int time)
        {
            if (Env == null || time < 0) return null;
            AutoPoint? p = Current == Gesture.Points && Moved && time == Grabbed.Time ? Displaced(Grabbed) : Env.Points.Cast<AutoPoint?>().FirstOrDefault(q => q!.Value.Time == time);
            if (p is not { } pt) return null;
            int tpb = Math.Max(1, core.TPB), bpb = Math.Max(1, core.Session?.BeatsPerBar ?? 4);
            return $"{Env.Param.Describe(pt.Value)} · {TimeText.Position(pt.Time / ClipEvent.TimeBase, tpb, bpb)} · {(pt.Ramp ? "ramp" : "step")}";
        }

        /// <summary>The point whose tip is shown (the one being dragged, or under the pointer).</summary>
        public AutoPoint? TipPoint => Current == Gesture.Points && Moved ? Displaced(Grabbed) : Env?.Points.Cast<AutoPoint?>().FirstOrDefault(q => q!.Value.Time == HoverTime);

        // ------------------------------------------------------------------ hit-testing

        public int HitTest(Point p)
        {
            if (Env == null) return -1;
            int best = -1;
            double bestD = Grab * Grab;
            foreach (var pt in Env.Points)
            {
                var at = geometry.PointOf(pt.Time, pt.Value);
                double dx = at.X - p.X, dy = at.Y - p.Y;
                double d = dx * dx + dy * dy;
                if (d <= bestD) { bestD = d; best = pt.Time; }
            }
            return best;
        }

        int Snap(Point p, bool noSnap) => Math.Clamp(geometry.Snap(geometry.TimeAt(p), noSnap), 0, Last);

        // ------------------------------------------------------------------ selection

        public static bool IsAdditive(KeyModifiers mods) => mods.HasFlag(KeyModifiers.Shift) || SessionView.Normalize(mods).HasFlag(KeyModifiers.Control);

        void Select(IEnumerable<int> times, bool extend)
        {
            if (!extend) selected.Clear();
            foreach (int t in times) selected.Add(t);
            SelectionChanged?.Invoke();
            Touch();
        }

        public void ClearSelection()
        {
            if (selected.Count == 0) return;
            selected.Clear();
            SelectionChanged?.Invoke();
            Touch();
        }

        /// <summary>Selects points by their times (tests, and the stack).</summary>
        public void SelectTimes(IEnumerable<int> times, bool extend = false) => Select(times.Where(t => Env?.Points.Any(p => p.Time == t) == true), extend);

        public bool SelectAll()
        {
            if (Env == null) return false;
            Select(Env.Points.Select(p => p.Time), false);
            return true;
        }

        // ------------------------------------------------------------------ the gestures

        public Press Pressed(Point p, KeyModifiers mods, int clickCount)
        {
            if (Env == null) return Press.NotHandled;
            Refusal = null;
            Down = Now = p;
            Moved = false;
            Additive = IsAdditive(mods);
            int hit = HitTest(p);
            InsertTime = Snap(p, false);
            if (hit >= 0)
            {
                if (clickCount == 2)
                {
                    selected.Clear();
                    Commit(editor.Delete(Env, new[] { hit }));
                    SelectionChanged?.Invoke();
                    return Press.Handled;
                }
                if (Additive)
                {
                    // Shift/Ctrl/Cmd-click toggles the point; one that is now selected can be dragged at once (Alt is "no snap" in a drag).
                    if (!selected.Remove(hit)) selected.Add(hit);
                    SelectionChanged?.Invoke();
                    if (!selected.Contains(hit))
                    {
                        Touch();
                        return Press.Handled;
                    }
                    collapseOnRelease = false;
                }
                else
                {
                    collapseOnRelease = selected.Contains(hit) && selected.Count > 1;
                    if (!selected.Contains(hit)) { selected.Clear(); selected.Add(hit); SelectionChanged?.Invoke(); }
                }
                Grabbed = Env.Points.First(q => q.Time == hit);
                Current = Gesture.Points;
                PreviewDt = PreviewDv = 0;
            }
            else
            {
                Current = geometry.InClip(p) && !Additive ? Gesture.Add : Gesture.Band;
                if (Current == Gesture.Add && selected.Count > 0) { selected.Clear(); SelectionChanged?.Invoke(); }
            }
            Touch();
            return Press.Captured;
        }

        public void PointerMoved(Point p, bool noSnap)
        {
            if (Env == null) return;
            if (Current == Gesture.None)
            {
                int hit = HitTest(p);
                if (hit != HoverTime) { HoverTime = hit; Touch(); }
                return;
            }
            Now = p;
            if (!Moved)
            {
                if (Math.Abs(p.X - Down.X) < DragDistance && Math.Abs(p.Y - Down.Y) < DragDistance) return;
                Moved = true;
                if (Current == Gesture.Add) Current = Gesture.Band;
            }
            if (Current == Gesture.Points)
            {
                int target = Snap(p, noSnap);
                int lo = selected.Min(), hi = selected.Max();
                PreviewDt = Math.Clamp(target - Grabbed.Time, -lo, Last - hi);
                PreviewDv = geometry.ValueAt(p) - Grabbed.Value;
            }
            Touch();
        }

        /// <summary>The button went up: ends the gesture and makes its edit. False when no gesture was going on.</summary>
        public bool Released(Point p, bool noSnap)
        {
            if (Current == Gesture.None) return false;
            Now = p;
            var g = Current;
            Current = Gesture.None;
            switch (g)
            {
                case Gesture.Add:
                {
                    int time = Snap(p, noSnap);
                    InsertTime = time;
                    string why = editor.AddPoint(Env, time, geometry.ValueAt(p));
                    selected.Clear();
                    if (why == null) selected.Add(time);
                    Commit(why);
                    SelectionChanged?.Invoke();
                    break;
                }
                case Gesture.Points when Moved:
                {
                    int dt = PreviewDt, dv = PreviewDv;
                    var times = selected.ToList();
                    string why = editor.Move(Env, times, dt, dv);
                    selected.Clear();
                    foreach (int t in times) selected.Add(Math.Clamp(t + dt, 0, Last));
                    Commit(why);
                    SelectionChanged?.Invoke();
                    break;
                }
                case Gesture.Points:
                    if (collapseOnRelease && !Additive) { selected.Clear(); selected.Add(Grabbed.Time); SelectionChanged?.Invoke(); }
                    Touch();
                    break;
                case Gesture.Band:
                {
                    var band = new Rect(Down, p).Normalize();
                    if (!Moved) { if (!Additive) { selected.Clear(); SelectionChanged?.Invoke(); } }
                    else
                    {
                        var inside = Env.Points.Where(q => band.Contains(geometry.PointOf(q.Time, q.Value))).Select(q => q.Time);
                        Select(inside, Additive);
                    }
                    Touch();
                    break;
                }
            }
            Moved = false;
            return true;
        }

        /// <summary>The pointer was taken away mid-gesture: the gesture is dropped, nothing is written.</summary>
        public void CaptureLost()
        {
            if (Current == Gesture.None) return;
            Current = Gesture.None;
            Moved = false;
            Touch();
        }

        public void PointerExited()
        {
            if (HoverTime >= 0 && Current == Gesture.None) { HoverTime = -1; Touch(); }
        }

        /// <summary>R, Delete, Back, Ctrl/Cmd+A, C, X, V and Esc; true when the key was the lane's.</summary>
        public bool KeyDown(Key key, KeyModifiers modifiers)
        {
            var mods = SessionView.Normalize(modifiers);
            bool ctrl = mods == KeyModifiers.Control;
            bool handled = true;
            switch (key)
            {
                case Key.Delete:
                case Key.Back when mods == KeyModifiers.None:
                    DeleteSelection();
                    break;
                case Key.R when mods == KeyModifiers.None:
                    ToggleRamp();
                    break;
                case Key.A when ctrl:
                    SelectAll();
                    break;
                case Key.C when ctrl:
                    CopySelection();
                    break;
                case Key.X when ctrl:
                    CopySelection();
                    DeleteSelection();
                    break;
                case Key.V when ctrl:
                    Paste();
                    break;
                case Key.Escape:
                    if (Current != Gesture.None) { Current = Gesture.None; Moved = false; }
                    else ClearSelection();
                    Touch();
                    break;
                default:
                    handled = false;
                    break;
            }
            return handled;
        }

        // ------------------------------------------------------------------ edits (the keys, the buttons and the Edit commands call these)

        void Commit(string why)
        {
            Refusal = why;
            Reload(force: true);
            Edited?.Invoke();
        }

        public bool DeleteSelection()
        {
            if (Env == null || selected.Count == 0) return false;
            var times = selected.ToList();
            selected.Clear();
            Commit(editor.Delete(Env, times));
            SelectionChanged?.Invoke();
            return true;
        }

        /// <summary>The line after the selected points a ramp or a step: ramps unless every one of them is a ramp already.</summary>
        public bool ToggleRamp()
        {
            if (Env == null || selected.Count == 0) return false;
            bool allRamps = Env.Points.Where(p => selected.Contains(p.Time)).All(p => p.Ramp);
            return SetRamp(!allRamps);
        }

        public bool SetRamp(bool ramp)
        {
            if (Env == null || selected.Count == 0) return false;
            Commit(editor.SetRamp(Env, selected.ToList(), ramp));
            return true;
        }

        public bool CopySelection()
        {
            if (Env == null || selected.Count == 0) return false;
            EnvelopeClipboard.Set(Env.Points.Where(p => selected.Contains(p.Time)), sourceId(), Env.Param.Min, Env.Param.Max);
            return true;
        }

        public bool Paste() => PasteAt(InsertTime);

        /// <summary>Puts the copied points in at a clip time; they become the selection.</summary>
        public bool PasteAt(int at)
        {
            var copied = EnvelopeClipboard.Points;
            if (Env == null || copied.Count == 0) return false;
            bool same = EnvelopeClipboard.Source == sourceId();
            var points = copied.Select(p => same ? p : p with
            {
                Value = EnvelopeClipboard.Max > EnvelopeClipboard.Min
                    ? Env.Param.ValueAt((p.Value - EnvelopeClipboard.Min) / (double)(EnvelopeClipboard.Max - EnvelopeClipboard.Min))
                    : Env.Param.Min,
            }).ToList();
            int shift = Math.Clamp(at, 0, Last) - points.Min(p => p.Time);
            var landed = points.Select(p => p.Time + shift).Where(t => t >= 0 && t <= Last).ToList();
            string why = editor.Paste(Env, points, at);
            selected.Clear();
            if (why == null) foreach (int t in landed) selected.Add(t);
            Commit(why);
            SelectionChanged?.Invoke();
            return why == null;
        }

        /// <summary>The points and their types now selected: all ramps, all steps or mixed.</summary>
        public (int Ramps, int Steps) SelectedKinds()
        {
            if (Env == null) return (0, 0);
            var sel = Env.Points.Where(p => selected.Contains(p.Time)).ToList();
            return (sel.Count(p => p.Ramp), sel.Count(p => !p.Ramp));
        }
    }
}
