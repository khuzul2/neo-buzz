// Fork (docs/specs/history-snapshots.md HS1b): the inspector's History tab. The song's one undo history as a list, newest at the
// top: each step's name, time and source (Manual, Agent, MIDI); the current position marked; the steps undone, on the redo side,
// dimmed. A click goes there (undo or redo to that step). Its own class, so the inspector's Selection tab stays as it was.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Common;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Inspector
{
    internal sealed class HistoryTab : DockPanel, IDisposable
    {
        /// <summary>The newest steps drawn (the rest are in the list the agent reads; the cap keeps the panel cheap).</summary>
        public const int MaxRows = 300;

        readonly ReBuzzCore core;
        readonly SongCore song;
        readonly StackPanel list;
        readonly TextBlock summary, keys;
        bool pending, disposed;

        public HistoryTab(ReBuzzCore core)
        {
            this.core = core;
            song = core.SongCore;
            LastChildFill = true;
            AutomationProperties.SetName(this, "History");

            summary = new TextBlock { FontSize = 12, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 4) };
            keys = new TextBlock { FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 8) };
            var top = new StackPanel { Children = { summary, keys } };
            DockPanel.SetDock(top, Dock.Top);
            Children.Add(top);

            list = new StackPanel { Margin = new Thickness(8, 0, 8, 8) };
            Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = list });

            if (song != null) song.HistoryChanged += OnChanged;
            Rebuild();
        }

        /// <summary>The rows drawn, newest first (tests).</summary>
        internal IReadOnlyList<Border> Rows => list.Children.OfType<Border>().ToList();

        /// <summary>What a row shows for a source.</summary>
        internal static string SourceLabel(HistorySource s) => s switch { HistorySource.Agent => "Agent", HistorySource.Midi => "MIDI", _ => "Manual" };

        void OnChanged()
        {
            // The engine thread, an agent or a controller may raise it: one rebuild on the UI thread for a burst.
            if (disposed || pending) return;
            pending = true;
            Dispatcher.UIThread.Post(() => { pending = false; if (!disposed) Rebuild(); }, DispatcherPriority.Background);
        }

        /// <summary>Redraws now.</summary>
        internal void Rebuild()
        {
            list.Children.Clear();
            var stack = song?.ActionStack;
            if (stack == null) return;
            var steps = stack.Steps;
            int current = stack.CurrentStepId;
            for (int i = steps.Count - 1, shown = 0; i >= 0 && shown < MaxRows; i--, shown++)
                list.Children.Add(Row(steps[i], steps[i].Id == current));
            string kept = steps.Count == 1 ? "1 step" : steps.Count + " steps";
            string dropped = stack.DroppedSteps > 0 ? $", {stack.DroppedSteps} dropped (memory cap)" : "";
            summary.Text = steps.Count == 0 ? "Nothing done yet. Edits, yours and an agent's, show up here." : $"{kept}{dropped}. Click a step to go there.";
            keys.Text = "Undo " + Shell.Commands.StudioKeymap.Get("edit.undo").KeyLabel() + ", redo " + Shell.Commands.StudioKeymap.Get("edit.redo").KeyLabel() + ".";
            if (steps.Count > MaxRows) summary.Text += $" The newest {MaxRows} are shown.";
        }

        Border Row(HistoryStep step, bool current)
        {
            // The steps undone are dimmed with the secondary text colour and italics (still 4.5:1 or more), not with opacity.
            IBrush text = step.Done ? StudioTokens.TextPrimaryBrush : StudioTokens.TextSecondaryBrush;
            var name = new TextBlock { Text = step.Name, FontSize = 12, Foreground = text, FontStyle = step.Done ? FontStyle.Normal : FontStyle.Italic, TextTrimming = TextTrimming.CharacterEllipsis };
            // On the current row's fill the secondary text is only 2.5:1, so that row's details are in the primary colour.
            IBrush small = current ? StudioTokens.TextPrimaryBrush : StudioTokens.TextSecondaryBrush;
            var source = new TextBlock { Text = SourceLabel(step.Source), FontSize = 11, Foreground = small, Margin = new Thickness(0, 0, 8, 0) };
            var time = new TextBlock { Text = step.Time.ToString("HH:mm:ss"), FontSize = 11, Foreground = small };
            var detail = new StackPanel { Orientation = Orientation.Horizontal, Children = { source, time } };
            var marker = new TextBlock { Text = current ? "▶" : "", Width = 14, FontSize = 10, Foreground = StudioTokens.TextPrimaryBrush, VerticalAlignment = VerticalAlignment.Center };
            var body = new StackPanel { Children = { name, detail } };
            var grid = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(marker, Dock.Left);
            grid.Children.Add(marker);
            grid.Children.Add(body);

            var row = new Border
            {
                Child = grid,
                Padding = new Thickness(4, 3, 4, 3),
                CornerRadius = new CornerRadius(3),
                Background = current ? StudioTokens.SelectionFillBrush : Brushes.Transparent,
                Focusable = true,
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = step.Id,
            };
            string state = current ? ", current" : step.Done ? "" : ", undone";
            AutomationProperties.SetName(row, $"{step.Name}, {SourceLabel(step.Source)}, {step.Time:HH:mm:ss}{state}");
            ToolTip.SetTip(row, step.Done ? (current ? "The current state" : "Undo back to just after this step") : "Redo up to and including this step");
            row.PointerPressed += (s, e) => { if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) { Jump(step.Id); e.Handled = true; } };
            row.KeyDown += (s, e) => { if (e.Key is Key.Enter or Key.Space) { Jump(step.Id); e.Handled = true; } };
            return row;
        }

        /// <summary>Goes to the state right after the step (undo or redo to it); true if it was there to go to.</summary>
        internal bool Jump(int stepId)
        {
            var stack = song?.ActionStack;
            if (stack == null) return false;
            bool ok = stack.JumpTo(stepId);
            if (ok) core.SetModifiedFlag();
            return ok;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (song != null) song.HistoryChanged -= OnChanged;
        }
    }
}
