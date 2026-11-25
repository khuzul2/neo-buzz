// Fork (docs/specs/drag-drop.md, DD1): the Session View as a drop target. Browser items and files from the desktop dropped on a slot, on a track's
// header, in the scenes or on empty space (right of the last track) go through the DropRouter. While dragging: the slots the drop would fill are
// lit (several files go down the scenes), the track is lit, a line shows where a new track will go, and a refusal says why. One undo step per drop.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Browser;
using ReBuzz.Core.Drop;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Drop;

namespace ReBuzz.Studio.Views.Session
{
    using Control = Avalonia.Controls.Control;

    internal sealed partial class SessionView
    {
        DropPlan dropPlan;
        DropTarget dropTargetNow;
        IReadOnlyList<BrowserDragData> dropItems;
        Point dropAtPoint;
        string dropMessage;
        bool dropMessageRefused;
        DropOverlay dropOverlay;

        /// <summary>The router's answer for what is being dragged over the view, or null when nothing is.</summary>
        internal DropPlan DropFeedback => dropPlan;

        /// <summary>The words of the last drop (what it did, or why it was refused), for two seconds after it.</summary>
        internal string DropMessage => dropMessage;

        /// <summary>The slots the drag would fill: the track and the scenes (several files go down the scenes).</summary>
        internal IReadOnlyList<(int Track, int Scene)> DropSlots { get; private set; } = Array.Empty<(int, int)>();

        /// <summary>The drag's feedback goes over the body (the grid, the scenes and the mixer).</summary>
        Control WithDropOverlay(Control inner)
        {
            dropOverlay = new DropOverlay(this) { IsHitTestVisible = false };
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOverItems);
            AddHandler(DragDrop.DragLeaveEvent, (s, e) => EndDropFeedback());
            AddHandler(DragDrop.DropEvent, OnDropItems);
            return new Grid { Children = { inner, dropOverlay } };
        }

        void OnDragOverItems(object sender, DragEventArgs e)
        {
            var items = DropSupport.Read(e.DataTransfer);
            if (items.Count == 0) { e.DragEffects = DragDropEffects.None; EndDropFeedback(); return; }
            var plan = ShowDropAt(items, e.GetPosition(dropOverlay));
            e.DragEffects = DropSupport.Effects(plan);
            e.Handled = true;
        }

        void OnDropItems(object sender, DragEventArgs e)
        {
            var items = DropSupport.Read(e.DataTransfer);
            var p = e.GetPosition(dropOverlay);
            EndDropFeedback();
            if (items.Count == 0) return;
            DropAt(items, p);
            e.Handled = true;
        }

        void EndDropFeedback()
        {
            if (dropPlan == null && dropTargetNow == null) return;
            dropPlan = null;
            dropTargetNow = null;
            dropItems = null;
            DropSlots = Array.Empty<(int, int)>();
            dropOverlay?.InvalidateVisual();
        }

        internal void ClearDropFeedback() => EndDropFeedback();

        // ------------------------------------------------------------------ where

        Rect In(Control element) => element.TranslatePoint(new Point(0, 0), dropOverlay) is { } o ? new Rect(o, element.Bounds.Size) : default;

        /// <summary>The target at a point of the view's body (the overlay's): the slot, the track's header, the scene row, or empty space.</summary>
        internal DropTarget TargetAt(Point p)
        {
            IMachine MachineOf(int track) => layout.SequenceOf(track)?.Machine;
            var viewport = In(scroll);
            if (viewport.Contains(p))
            {
                foreach (var cell in slots)
                    if (In(cell).Contains(p)) return new DropTarget { View = DropView.Session, Machine = MachineOf(cell.Track), Scene = cell.Scene };
                foreach (var h in headers)
                    if (h.Return < 0 && In(h).Contains(p)) return new DropTarget { View = DropView.Session, Machine = MachineOf(h.Track) };
                foreach (var spare in this.grid.Children.OfType<SpareSlots>())
                    if (spare.Track >= 0 && In(spare).Contains(p)) return new DropTarget { View = DropView.Session, Machine = MachineOf(spare.Track), Scene = sceneCount };
                return new DropTarget { View = DropView.Session };
            }
            if (In(sceneScroll).Contains(p))
                foreach (var s in scenes)
                    if (In(s).Contains(p)) return new DropTarget { View = DropView.Session, Scene = s.Scene };
            return new DropTarget { View = DropView.Session };
        }

        /// <summary>The centre of a control of the grid, in the drop overlay's points (what a drag over it reports).</summary>
        internal Point DropPointOf(Control element) => In(element).Center;

        /// <summary>A point on empty space: in the grid, right of the last track.</summary>
        internal Point DropPointOnEmptySpace()
        {
            var view = In(scroll);
            // Scans from the right edge: the first point the router sees as empty space.
            for (double x = view.Right - 10; x > view.X; x -= 8)
                if (TargetAt(new Point(x, view.Y + 40)) is { Machine: null, Scene: < 0 }) return new Point(x, view.Y + 40);
            return new Point(view.Right - 10, view.Y + 40);
        }

        /// <summary>What a drop at a point would do (tests and the drag use it).</summary>
        internal DropPlan PlanAt(IReadOnlyList<BrowserDragData> items, Point p) => new DropRouter(buzz).Plan(items, TargetAt(p));

        /// <summary>Shows the drag feedback at a point, as a drag over it would; returns the plan.</summary>
        internal DropPlan ShowDropAt(IReadOnlyList<BrowserDragData> items, Point p)
        {
            dropItems = items;
            dropAtPoint = p;
            dropTargetNow = TargetAt(p);
            dropPlan = new DropRouter(buzz).Plan(items, dropTargetNow);
            var lit = new List<(int, int)>();
            if (dropPlan.Allowed && dropTargetNow.Scene >= 0 && (dropPlan.Action is DropAction.AudioClips or DropAction.NewAudioTrack or DropAction.Clip or DropAction.MidiNewPattern or DropAction.MidiNewTrack or DropAction.NewTrack))
            {
                int track = dropPlan.Target != null ? layout.FirstTrackOf(dropPlan.Target) : -1;
                int count = dropPlan.Action == DropAction.AudioClips || dropPlan.Action == DropAction.NewAudioTrack ? items.Count : 1;
                if (track >= 0) for (int i = 0; i < count; i++) lit.Add((track, dropTargetNow.Scene + i));
            }
            DropSlots = lit;
            dropOverlay?.InvalidateVisual();
            return dropPlan;
        }

        /// <summary>A drop at a point: through the router, one undo step; the words of what it did go over the view for two seconds.</summary>
        internal DropResult DropAt(IReadOnlyList<BrowserDragData> items, Point p)
        {
            var target = TargetAt(p);
            return DropSupport.Run(buzz, items, target, this, p, r =>
            {
                Say(r.Message, !r.Ok);
                if (r.Ok)
                {
                    Rebuild();
                    if (r.Machine != null && layout.FirstTrackOf(r.Machine) is int t and >= 0)
                        Select(t, Math.Max(0, target.Scene >= 0 ? target.Scene : 0));
                }
            });
        }

        void Say(string words, bool refused)
        {
            dropMessage = words;
            dropMessageRefused = refused;
            dropOverlay?.InvalidateVisual();
            DispatcherTimer.RunOnce(() => { if (dropMessage == words) { dropMessage = null; dropOverlay?.InvalidateVisual(); } }, TimeSpan.FromSeconds(3));
        }

        // ------------------------------------------------------------------ feedback

        void PaintDrop(DrawingContext ctx, Rect bounds)
        {
            if (dropPlan == null || dropTargetNow == null)
            {
                if (dropMessage != null) DropSupport.DrawLabel(ctx, new Point(bounds.X + 12, bounds.Y + 8), dropMessage, dropMessageRefused, bounds);
                return;
            }
            bool refused = !dropPlan.Allowed && dropPlan.Choices == null;
            var view = In(scroll);
            var lit = dropPlan.Target ?? (refused ? dropTargetNow.Machine : null);
            if (lit != null)
            {
                int track = layout.FirstTrackOf(lit);
                var h = headers.FirstOrDefault(x => x.Track == track && x.Return < 0);
                if (h != null)
                {
                    var r = In(h);
                    DropSupport.DrawLit(ctx, new Rect(r.X - 1, view.Y, r.Width + 2, view.Height), refused);
                }
            }
            foreach (var (track, scene) in DropSlots)
                if (slots.FirstOrDefault(c => c.Track == track && c.Scene == scene) is { } cell)
                    DropSupport.DrawLit(ctx, In(cell), false);
            if (dropPlan.MakesTrack)
            {
                var last = headers.Where(x => x.Return < 0).Select(In).LastOrDefault();
                double x = last.Width > 0 ? last.Right + 1 : view.X + 2;
                x = Math.Min(x, view.Right - 3);
                DropSupport.DrawInsertion(ctx, new Point(x, view.Y), new Point(x, view.Bottom));
            }
            DropSupport.DrawLabel(ctx, dropAtPoint, dropPlan.Choices != null ? dropPlan.ChoiceTitle : dropPlan.Allowed ? dropPlan.Describe : dropPlan.Refusal, refused, bounds);
        }

        /// <summary>Paints the drag feedback over the body; never takes a click.</summary>
        sealed class DropOverlay : Control
        {
            readonly SessionView view;

            public DropOverlay(SessionView view) => this.view = view;

            public override void Render(DrawingContext ctx) => view.PaintDrop(ctx, new Rect(Bounds.Size));
        }
    }
}
