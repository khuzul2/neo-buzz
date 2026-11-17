// Fork (docs/specs/drag-drop.md, DD1): what the three views share when something is dragged over them: reading what is dragged (a browser item,
// or files from the desktop), asking the router (ReBuzz.Core.Drop.DropRouter) what would happen, the question an effect on a branching track
// asks, and the feedback's painting (the lit track, the insertion line, the label with its reason).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Browser;
using ReBuzz.Core;
using ReBuzz.Core.Drop;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Browser;

namespace ReBuzz.Studio.Views.Drop
{
    using Control = Avalonia.Controls.Control;

    internal static class DropSupport
    {
        /// <summary>The generator an instrument track was last made with by a drop: where a MIDI file on empty space plays (Neo Poly before that).</summary>
        public static string LastInstrument { get; set; }

        /// <summary>
        /// Tests answer the branch question here (the machine to follow, or null to cancel) instead of a menu opening.
        /// </summary>
        public static Func<string, IReadOnlyList<string>, string> ChoiceResponder { get; set; }

        /// <summary>What a drag carries: one browser item, or the audio and MIDI files of a drag from the desktop. Empty for anything else.</summary>
        public static IReadOnlyList<BrowserDragData> Read(IDataTransfer transfer)
        {
            if (transfer == null) return Array.Empty<BrowserDragData>();
            if (BrowserDragSource.Read(transfer) is { } item) return new[] { item };
            IEnumerable<IStorageItem> files;
            try { files = transfer.TryGetFiles() ?? Array.Empty<IStorageItem>(); }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { files = Array.Empty<IStorageItem>(); }
            var items = files.Select(f => f.TryGetLocalPath()).Select(DropRouter.FromFile).Where(i => i != null).ToList();
            // One kind at a time: the first file's (audio files together, or a single MIDI file).
            if (items.Count == 0) return items;
            if (items[0].Kind == BrowserKind.Midi) return new[] { items[0] };
            return items.Where(i => i.Kind == BrowserKind.Audio).ToList();
        }

        /// <summary>The effect the pointer shows: a copy where the drop is allowed, none where it is refused.</summary>
        public static DragDropEffects Effects(DropPlan plan) => plan != null && (plan.Allowed || plan.Choices != null) ? DragDropEffects.Copy : DragDropEffects.None;

        /// <summary>
        /// Does the drop. An effect on a track that branches asks which branch first (a menu at <paramref name="anchor"/>, or the tests'
        /// <see cref="ChoiceResponder"/>), then drops again. <paramref name="done"/> gets the result (once the menu is answered, if one opened).
        /// </summary>
        public static DropResult Run(ReBuzzCore core, IReadOnlyList<BrowserDragData> items, DropTarget target, Control anchor, Point at, Action<DropResult> done)
        {
            var router = new DropRouter(core);
            if (target.DefaultInstrument == null) target = With(target, defaultInstrument: LastInstrument);
            var result = router.Drop(items, target);
            if (result.Choices != null)
            {
                if (ChoiceResponder != null)
                {
                    string pick = ChoiceResponder(result.Message, result.Choices);
                    result = pick == null ? new DropResult { Ok = false, Message = "Cancelled." } : router.Drop(items, With(target, branch: (target.Branch ?? Array.Empty<string>()).Append(pick).ToList()));
                }
                else
                {
                    var menu = new ContextMenu();
                    menu.Items.Add(new MenuItem { Header = result.Message, IsEnabled = false });
                    foreach (var choice in result.Choices)
                    {
                        string c = choice;
                        var item = new MenuItem { Header = c };
                        item.Click += (s, e) => Finish(core, router.Drop(items, With(target, branch: (target.Branch ?? Array.Empty<string>()).Append(c).ToList())), done);
                        menu.Items.Add(item);
                    }
                    menu.Open(anchor);
                    return result;
                }
            }
            Finish(core, result, done);
            return result;
        }

        static void Finish(ReBuzzCore core, DropResult result, Action<DropResult> done)
        {
            if (result.Ok && result.Machine != null && result.Action is DropAction.NewTrack or DropAction.NewTrackWithPreset or DropAction.MidiNewTrack
                && result.Machine.DLL?.Info?.Type == MachineType.Generator)
                LastInstrument = (result.Machine as MachineCore)?.InstrumentName is { Length: > 0 } n ? n : result.Machine.DLL.Name;
            done?.Invoke(result);
        }

        static DropTarget With(DropTarget t, string defaultInstrument = null, IReadOnlyList<string> branch = null) => new()
        {
            View = t.View, Machine = t.Machine, Lane = t.Lane, Time = t.Time, Scene = t.Scene, X = t.X, Y = t.Y,
            Branch = branch ?? t.Branch, DefaultInstrument = defaultInstrument ?? t.DefaultInstrument,
        };

        // ------------------------------------------------------------------ painting

        static readonly Typeface Face = new(new FontFamily("Arial"));

        /// <summary>The lit track: a tint and an outline in the accent (a refusal in the error colour).</summary>
        public static void DrawLit(DrawingContext ctx, Rect r, bool refused)
        {
            var colour = refused ? StudioTokens.ErrorText : StudioTokens.Accent;
            ctx.DrawRectangle(new SolidColorBrush(colour, 0.14), new Pen(new SolidColorBrush(colour), 2), r.Deflate(1));
        }

        /// <summary>The line where a new track will go.</summary>
        public static void DrawInsertion(DrawingContext ctx, Point from, Point to)
        {
            var accent = new SolidColorBrush(StudioTokens.Accent);
            ctx.DrawLine(new Pen(accent, 3), from, to);
            bool horizontal = Math.Abs(from.Y - to.Y) < 0.5;
            const double r = 4;
            ctx.DrawEllipse(accent, null, from, r, r);
            ctx.DrawEllipse(accent, null, to, r, r);
            _ = horizontal;
        }

        /// <summary>What would happen, or why not, in a plate by the pointer, kept inside <paramref name="bounds"/>.</summary>
        public static void DrawLabel(DrawingContext ctx, Point pointer, string text, bool refused, Rect bounds)
        {
            if (string.IsNullOrEmpty(text)) return;
            var ink = refused ? StudioTokens.ErrorTextBrush : StudioTokens.TextPrimaryBrush;
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 12, ink) { MaxTextWidth = Math.Max(120, Math.Min(380, bounds.Width - 24)) };
            double x = Math.Min(pointer.X + 14, bounds.Right - ft.Width - 12), y = Math.Min(pointer.Y + 18, bounds.Bottom - ft.Height - 8);
            x = Math.Max(bounds.X + 4, x);
            y = Math.Max(bounds.Y + 4, y);
            var plate = new Rect(x - 5, y - 3, ft.Width + 10, ft.Height + 6);
            var fill = refused ? new SolidColorBrush(StudioTokens.SevereBack) : StudioTokens.SurfacePanelBrush;
            ctx.DrawRectangle(fill, new Pen(refused ? StudioTokens.ErrorTextBrush : StudioTokens.AccentBrush, 1), plate, 3, 3);
            ctx.DrawText(ft, new Point(x, y));
        }
    }
}
