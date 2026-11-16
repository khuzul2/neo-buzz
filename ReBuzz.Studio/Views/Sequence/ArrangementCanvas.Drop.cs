// Fork (docs/specs/drag-drop.md, DD1): the Arrangement as a drop target. Browser items and files from the desktop dropped on a track, on a clip, at a
// time or on empty space (below the tracks) go through the DropRouter. While dragging: the track under the pointer is lit, a line shows where a
// new track will go, the time snaps to the beat (Shift: to the tick) with a guide, and a refusal says why. One undo step per drop.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Browser;
using ReBuzz.Core.Drop;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Drop;

namespace ReBuzz.Studio.Views.Sequence
{
    using Control = Avalonia.Controls.Control;

    internal sealed partial class ArrangementCanvas
    {
        DropPlan dropPlan;
        DropTarget dropTarget;
        Point dropAt;
        IReadOnlyList<BrowserDragData> dropItems;

        /// <summary>The router's answer for what is being dragged over the canvas, or null when nothing is.</summary>
        public DropPlan DropFeedback => dropPlan;

        /// <summary>Where the drag's drop would land (null when nothing is dragged over).</summary>
        internal DropTarget DropTargetNow => dropTarget;

        /// <summary>The machine's rows are lit while a drop on the track is shown.</summary>
        internal IMachine DropLitMachine => dropPlan?.Target;

        /// <summary>The status line says what the last drop did or why it was refused.</summary>
        public string DropStatus { get; private set; }

        void InitDrop(Control headers = null)
        {
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOverItems);
            AddHandler(DragDrop.DragLeaveEvent, (s, e) => EndDropFeedback());
            AddHandler(DragDrop.DropEvent, OnDropItems);
        }

        /// <summary>The track headers sit over the canvas's left edge: a drop there is a drop on the track (points in the canvas's).</summary>
        internal void AttachDropTo(Control other)
        {
            DragDrop.SetAllowDrop(other, true);
            other.AddHandler(DragDrop.DragOverEvent, (s, e) => OnDragOverItems(s, e, other));
            other.AddHandler(DragDrop.DragLeaveEvent, (s, e) => EndDropFeedback());
            other.AddHandler(DragDrop.DropEvent, (s, e) => OnDropItems(s, e, other));
        }

        void OnDragOverItems(object sender, DragEventArgs e) => OnDragOverItems(sender, e, this);

        void OnDragOverItems(object sender, DragEventArgs e, Control relativeTo)
        {
            var items = DropSupport.Read(e.DataTransfer);
            if (items.Count == 0) { e.DragEffects = DragDropEffects.None; EndDropFeedback(); return; }
            var p = e.GetPosition(this);
            if (!ReferenceEquals(relativeTo, this)) p = relativeTo.TranslatePoint(e.GetPosition(relativeTo), this) ?? p;
            var plan = ShowDrop(items, p, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.DragEffects = DropSupport.Effects(plan);
            e.Handled = true;
        }

        void OnDropItems(object sender, DragEventArgs e) => OnDropItems(sender, e, this);

        void OnDropItems(object sender, DragEventArgs e, Control relativeTo)
        {
            var items = DropSupport.Read(e.DataTransfer);
            var p = e.GetPosition(this);
            if (!ReferenceEquals(relativeTo, this)) p = relativeTo.TranslatePoint(e.GetPosition(relativeTo), this) ?? p;
            EndDropFeedback();
            if (items.Count == 0) return;
            DropAt(items, p, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }

        void EndDropFeedback()
        {
            if (dropPlan == null && dropTarget == null) return;
            dropPlan = null;
            dropTarget = null;
            dropItems = null;
            Overlay.InvalidateVisual();
        }

        // ------------------------------------------------------------------ where

        /// <summary>The track row under a canvas y, whatever its kind (a lane, a folded track, an automation row), or null.</summary>
        ArrangementRow TrackRowAt(double y) =>
            y < SequenceGeometry.RulerHeight ? null : rows.FirstOrDefault(r => r.Kind is ArrangementRowKind.Lane or ArrangementRowKind.Parameter && y >= YOf(r) && y < YOf(r) + r.Height);

        /// <summary>
        /// The drop's target at a canvas point: the track (and lane) under it or none (empty space: below the tracks, the ruler, the Master's
        /// rows), and the time, snapped to the beat unless <paramref name="free"/>.
        /// </summary>
        internal DropTarget TargetAt(Point p, bool free)
        {
            var row = TrackRowAt(p.Y);
            var tempo = rows.FirstOrDefault(r => r.Kind is ArrangementRowKind.Tempo or ArrangementRowKind.Markers && p.Y >= YOf(r) && p.Y < YOf(r) + r.Height);
            int time = Snap(geo.TimeAt(Math.Max(p.X, SequenceGeometry.HeaderWidth)), free);
            IMachine machine = row?.Machine;
            ISequence lane = row?.Lane ?? row?.Lanes.FirstOrDefault();
            if (tempo != null) { machine = Song.Machines.FirstOrDefault(m => m.DLL?.Info?.Type == MachineType.Master); lane = null; }
            return new DropTarget { View = DropView.Arrangement, Machine = machine, Lane = lane, Time = time };
        }

        /// <summary>What a drop at the canvas point would do (tests and the drag use it); the feedback shows it.</summary>
        internal DropPlan PlanAt(IReadOnlyList<BrowserDragData> items, Point p, bool free = false) =>
            new DropRouter(core).Plan(items, TargetAt(p, free));

        DropPlan ShowDrop(IReadOnlyList<BrowserDragData> items, Point p, bool free)
        {
            dropItems = items;
            dropAt = p;
            dropTarget = TargetAt(p, free);
            dropPlan = new DropRouter(core).Plan(items, dropTarget);
            Overlay.InvalidateVisual();
            return dropPlan;
        }

        /// <summary>For tests and the gallery: shows the drag feedback at a canvas point as a drag over it would.</summary>
        internal DropPlan ShowDropAt(IReadOnlyList<BrowserDragData> items, Point p, bool free = false) => ShowDrop(items, p, free);

        internal void ClearDropFeedback() => EndDropFeedback();

        /// <summary>
        /// A drop at a canvas point: through the router, one undo step; what it did (or why not) goes to the status line. Returns the result.
        /// </summary>
        internal DropResult DropAt(IReadOnlyList<BrowserDragData> items, Point p, bool free = false)
        {
            var target = TargetAt(p, free);
            var result = DropSupport.Run(core, items, target, this, p, r =>
            {
                DropStatus = r.Message;
                Status = r.Ok ? null : r.Message;
                if (r.Ok && r.Machine != null)
                {
                    var lane = Song.Sequences.FirstOrDefault(s => s.Machine == r.Machine);
                    if (lane != null) { cursorLane = lane; cursorTime = target.Time >= 0 ? target.Time : cursorTime; SelectionChanged?.Invoke(); }
                }
                StatusChanged?.Invoke();
                Changed();
            });
            return result;
        }

        // ------------------------------------------------------------------ feedback

        /// <summary>Paints the drag feedback (the overlay calls it inside the tracks' clip).</summary>
        internal void DrawDrop(DrawingContext ctx, Rect tracks)
        {
            if (dropPlan == null || dropTarget == null) return;
            bool refused = !dropPlan.Allowed && dropPlan.Choices == null;
            var lit = dropPlan.Target ?? (dropTarget.Machine != null && refused ? dropTarget.Machine : null);
            if (lit != null)
                foreach (var row in rows.Where(r => r.Machine == lit && r.Kind is ArrangementRowKind.Lane or ArrangementRowKind.Parameter))
                    if (row.Machine == lit) DropSupport.DrawLit(ctx, new Rect(tracks.X, YOf(row), tracks.Width, row.Height), refused);
            if (dropPlan.MakesTrack)
            {
                double y = rows.Count == 0 ? SequenceGeometry.RulerHeight : YOf(rows[^1]) + rows[^1].Height;
                DropSupport.DrawInsertion(ctx, new Point(tracks.X, y), new Point(tracks.Right, y));
            }
            if (!refused && dropPlan.Action is DropAction.NewTrack or DropAction.NewAudioTrack or DropAction.AudioClips or DropAction.PatternClip or DropAction.MidiNewPattern or DropAction.MidiNewTrack && dropTarget.Time >= 0)
            {
                double x = Math.Floor(geo.XOf(dropTarget.Time));
                double top = dropPlan.MakesTrack ? Math.Max(tracks.Y, (rows.Count == 0 ? SequenceGeometry.RulerHeight : YOf(rows[^1]) + rows[^1].Height) - 20) : tracks.Y;
                var row = dropPlan.Target != null ? rows.FirstOrDefault(r => r.Machine == dropPlan.Target && r.Kind == ArrangementRowKind.Lane) : null;
                double y0 = row != null ? YOf(row) : top, y1 = row != null ? YOf(row) + row.Height : top + 20;
                ctx.DrawLine(new Pen(StudioTokens.AccentBrush, 2), new Point(x, y0), new Point(x, y1));
            }
            DropSupport.DrawLabel(ctx, dropAt, dropPlan.Choices != null ? dropPlan.ChoiceTitle : dropPlan.Allowed ? dropPlan.Describe : dropPlan.Refusal, refused, tracks);
        }
    }
}
