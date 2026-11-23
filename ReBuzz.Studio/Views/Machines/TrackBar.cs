// Fork (docs/specs/ux-overhaul.md §11, MT1): the Machines view's Tracks bar and card.
// The bar: a chip for every track, group and return and an "Unrouted N" chip. It wraps (and scrolls past three rows), Tab reaches every chip
// and the arrow keys move along them. A chip the pointer is on or that has the focus lights its track (the canvas dims the rest);
// a click selects the track's generator in the studio's selection (so every view follows) and keeps the track lit until another click.
// The card: on the lit track, its chain (A → B → Master), mute, solo, arm and a meter, using the machine's own mute, solo and arm.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Core.Tracks;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Machines
{
    using Control = Avalonia.Controls.Control;

    internal sealed class TrackBar : Border
    {
        readonly ReBuzzCore core;
        readonly MachineCanvas canvas;
        readonly WrapPanel wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        readonly List<Button> chips = new();
        readonly ScrollViewer scroll;
        bool attached;

        /// <summary>Three rows of chips; more scroll.</summary>
        public const double MaxHeight3Rows = 3 * 30 + 2 * 4 + 12;

        public TrackBar(ReBuzzCore core, MachineCanvas canvas)
        {
            this.core = core;
            this.canvas = canvas;
            scroll = new ScrollViewer
            {
                Content = wrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = MaxHeight3Rows,
                Focusable = false,
            };
            wrap.Margin = new Thickness(8, 6, 8, 6);
            Child = scroll;
            BorderThickness = new Thickness(0, 0, 0, 1);
            BorderBrush = StudioTokens.SurfaceBorderBrush;
            Background = StudioTokens.SurfacePanelBrush;
            IsVisible = false;
            Avalonia.Automation.AutomationProperties.SetName(this, "Tracks");
        }

        public IReadOnlyList<Button> Chips => chips;

        /// <summary>The chip of an entry (by its head machine), or the Unrouted chip.</summary>
        public Button ChipOf(IMachine head) => chips.FirstOrDefault(c => c.Tag is TrackTarget t && !t.Unrouted && t.Head == head);

        public Button UnroutedChip => chips.FirstOrDefault(c => c.Tag is TrackTarget t && t.Unrouted);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            attached = true;
            StudioSelection.For(core).Changed += OnSelection;
            canvas.LitChanged += Mark;
            canvas.TracksChanged += Rebuild;
            StudioTheme.Changed += Rebuild;
            Rebuild();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            attached = false;
            StudioSelection.For(core).Changed -= OnSelection;
            canvas.LitChanged -= Mark;
            canvas.TracksChanged -= Rebuild;
            StudioTheme.Changed -= Rebuild;
            base.OnDetachedFromVisualTree(e);
        }

        void OnSelection(object source) => Mark();

        /// <summary>The chips for the model now (the canvas tells when it changed: its LitChanged follows the model).</summary>
        public void Rebuild()
        {
            if (!attached || !IsVisible) return;
            var model = canvas.TrackModel;
            if (model == null) return;
            chips.Clear();
            wrap.Children.Clear();
            foreach (var e in model.Entries)
                if (e.Head.Tag is IMachine head) Add(Make(head, e));
            if (model.Unrouted.Count > 0) Add(MakeUnrouted(model));
            Mark();
        }

        void Add(Button chip)
        {
            chips.Add(chip);
            wrap.Children.Add(chip);
        }

        Button Make(IMachine head, TrackEntry e)
        {
            string suffix = e.Kind == TrackKind.Group ? " · group" : e.Kind == TrackKind.Return ? " · return" : "";
            var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(MachineCanvas.ColourOfHead(head)).ToImmutable(), VerticalAlignment = VerticalAlignment.Center };
            var text = new TextBlock { Text = e.Name + suffix, FontSize = StudioTokens.FontBody, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 };
            var chip = NewChip(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { dot, text } }, TrackTarget.Entry(head), e.Name + suffix);
            ToolTip.SetTip(chip, Describe(e));
            return chip;
        }

        static string Describe(TrackEntry e)
        {
            string s = e.Chain;
            if (e.Members.Count > 0) s += "\nMembers: " + string.Join(", ", e.Members.Select(m => m.Name));
            if (e.Senders.Count > 0) s += "\nReceives sends from: " + string.Join(", ", e.Senders.Select(m => m.Name));
            if (e.Kind != TrackKind.Group && e.SendsTo.Count > 0) s += "\nSends to: " + string.Join(", ", e.SendsTo.Select(m => m.Name));
            else if (e.Kind == TrackKind.Group && e.SendsTo.Count > 0) s += "\nSends to: " + string.Join(", ", e.SendsTo.Select(m => m.Name));
            return s;
        }

        Button MakeUnrouted(TrackModel model)
        {
            var text = new TextBlock { Text = "Unrouted " + model.Unrouted.Count, FontSize = StudioTokens.FontBody, VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTokens.TextSecondaryBrush };
            var chip = NewChip(text, TrackTarget.AllUnrouted, "Unrouted " + model.Unrouted.Count);
            ToolTip.SetTip(chip, string.Join("\n", model.Unrouted.Select(u => u.Node.Name + ": " + u.WhyText)));
            return chip;
        }

        Button NewChip(Control content, TrackTarget target, string name)
        {
            var chip = new Button
            {
                Content = content,
                Tag = target,
                Padding = new Thickness(10, 3),
                MinHeight = 26,
                Margin = new Thickness(0, 2, 6, 2),
                CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(1),
                BorderBrush = StudioTokens.SurfaceBorderBrush,
                Background = StudioTokens.SurfaceControlBrush,
                Foreground = StudioTokens.TextPrimaryBrush,
            };
            Avalonia.Automation.AutomationProperties.SetName(chip, name);
            chip.PointerEntered += (s, e) => canvas.SetHoverLit(target);
            chip.PointerExited += (s, e) => canvas.SetHoverLit(null);
            chip.GotFocus += (s, e) => canvas.SetFocusLit(target);
            chip.LostFocus += (s, e) => canvas.SetFocusLit(null);
            chip.Click += (s, e) => Choose(target);
            chip.KeyDown += (s, e) => OnChipKey(chip, e);
            return chip;
        }

        /// <summary>A click (or Enter, or Space) on a chip: the track's generator is selected; the track stays lit until another is chosen, or this chip again.</summary>
        public void Choose(TrackTarget target)
        {
            if (Equals(canvas.Pinned, target))
            {
                canvas.Pin(null);
                return;
            }
            if (!target.Unrouted && target.Head != null) canvas.Select(new[] { target.Head });
            canvas.Pin(target);
        }

        void OnChipKey(Button chip, KeyEventArgs e)
        {
            int at = chips.IndexOf(chip);
            int to = e.Key == Key.Right ? at + 1 : e.Key == Key.Left ? at - 1 : e.Key == Key.Home ? 0 : e.Key == Key.End ? chips.Count - 1 : -1;
            if (to < 0 || to >= chips.Count) return;
            chips[to].Focus(NavigationMethod.Directional);
            chips[to].BringIntoView();
            e.Handled = true;
        }

        /// <summary>The chip of the selected machine's track is outlined (its colour stays); the lit one is filled.</summary>
        void Mark()
        {
            if (!attached) return;
            var model = canvas.TrackModel;
            if (model == null) return;
            var selected = StudioSelection.For(core).Machine;
            TrackEntry mine = null;
            if (selected != null)
            {
                var node = model.Graph.Nodes.FirstOrDefault(n => n.Tag == selected);
                mine = model.EntryOf(node);
            }
            var lit = canvas.Lit;
            foreach (var chip in chips)
            {
                var t = (TrackTarget)chip.Tag;
                bool isMine = mine != null && !t.Unrouted && t.Head == mine.Head.Tag;
                bool isLit = lit is { } l && Equals(l, t);
                chip.BorderBrush = isMine ? StudioTokens.AccentBrush : StudioTokens.SurfaceBorderBrush;
                chip.BorderThickness = new Thickness(isMine ? 2 : 1);
                chip.Background = isLit ? StudioTokens.SelectionFillBrush : StudioTokens.SurfaceControlBrush;
            }
        }
    }

    /// <summary>A level, 0 to 1, as a bar (the card's meter; the same colours as the canvas's).</summary>
    internal sealed class LevelBar : Control
    {
        double level;

        public LevelBar()
        {
            Height = 6;
            Width = 110;
            VerticalAlignment = VerticalAlignment.Center;
        }

        public double Level
        {
            get => level;
            set
            {
                value = Math.Clamp(value, 0, 1);
                if (Math.Abs(value - level) < 0.004 && (value == 0) == (level == 0)) return;
                level = value;
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext ctx)
        {
            var r = new Rect(Bounds.Size);
            ctx.DrawRectangle(StudioTokens.ControlTrackBrush, null, r, 3, 3);
            if (level > 0) ctx.DrawRectangle(level > 0.9 ? StudioTokens.MeterHotBrush : StudioTokens.MeterNormalBrush, null, new Rect(0, 0, r.Width * level, r.Height), 3, 3);
        }
    }

    /// <summary>The card of the lit track: its chain, mute, solo, arm and a meter.</summary>
    internal sealed class TrackCard : Border
    {
        readonly ReBuzzCore core;
        readonly MachineCanvas canvas;
        readonly TextBlock title = new TextBlock { FontSize = StudioTokens.FontBody, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextPrimaryBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock chain = new TextBlock { FontSize = StudioTokens.FontCaption, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap };
        readonly TextBlock sends = new TextBlock { FontSize = StudioTokens.FontCaption, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap };
        readonly Border dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center };
        readonly Button mute, solo, arm;
        readonly LevelBar meter = new LevelBar();
        readonly TextBlock db = new TextBlock { FontSize = StudioTokens.FontCaption, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center, MinWidth = 44 };
        IMachine head;
        IMachine watching;

        public TrackCard(ReBuzzCore core, MachineCanvas canvas)
        {
            this.core = core;
            this.canvas = canvas;
            mute = Toggle("M", "Mute", "Mute this track's machine (the same mute as the box's LED)");
            solo = Toggle("S", "Solo", "Solo this track's generator");
            arm = Toggle("●", "Arm", "Arm this track for recording");
            mute.Click += (s, e) => { if (head != null) MachineEdits.Mute(core, new[] { head }, !head.IsMuted); Refresh(); };
            solo.Click += (s, e) => { if (head != null && CanSolo(head)) MachineEdits.Solo(core, new[] { head }, !head.IsSoloed); Refresh(); };
            arm.Click += (s, e) =>
            {
                if (head != null && SessionLayout.CanArm(head) && core.SessionLayout is { } layout) layout.SetMachineArmed(head, !layout.IsMachineArmed(head));
                canvas.Redraw();
                Refresh();
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { mute, solo, arm, new Border { Width = 6 }, meter, db } };
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { dot, title } },
                    chain, sends, buttons,
                },
            };
            MaxWidth = 300;
            MinWidth = 220;
            Padding = new Thickness(10, 8);
            CornerRadius = new CornerRadius(8);
            BorderThickness = new Thickness(1);
            BorderBrush = StudioTokens.SurfaceBorderBrush;
            Background = StudioTokens.SurfacePanelBrush;
            IsVisible = false;
            Avalonia.Automation.AutomationProperties.SetName(this, "Track");
        }

        public Button MuteButton => mute;
        public Button SoloButton => solo;
        public Button ArmButton => arm;
        public LevelBar Meter => meter;
        public string Title => title.Text;
        public string ChainText => chain.Text;
        public string SendsText => sends.Text;
        public IMachine Head => head;

        static bool CanSolo(IMachine m) => m.DLL?.Info?.Type == MachineType.Generator && !m.IsControlMachine;

        Button Toggle(string text, string name, string tip)
        {
            var b = StudioButtons.KeepFocus(new Button { Content = text, MinWidth = 28, Padding = new Thickness(6, 2), BorderThickness = new Thickness(1), FontSize = StudioTokens.FontCaption });
            Avalonia.Automation.AutomationProperties.SetName(b, name);
            ToolTip.SetTip(b, tip);
            return b;
        }

        static void Mark(Button b, bool on, bool enabled)
        {
            b.IsEnabled = enabled;
            b.Background = on ? StudioTokens.SelectionFillBrush : StudioTokens.SurfaceControlBrush;
            b.BorderBrush = on ? StudioTokens.AccentBrush : StudioTokens.SurfaceBorderBrush;
            b.Foreground = on ? StudioTokens.TextPrimaryBrush : StudioTokens.TextSecondaryBrush;
        }

        /// <summary>Shows the card for an entry (null: hides it).</summary>
        public void Show(TrackEntry entry)
        {
            if (watching != null) { watching.PropertyChanged -= OnMachine; watching = null; }
            if (entry == null || entry.Head.Tag is not IMachine m)
            {
                head = null;
                IsVisible = false;
                return;
            }
            head = m;
            watching = m;
            m.PropertyChanged += OnMachine;
            string kind = entry.Kind == TrackKind.Track ? "Track" : entry.Kind == TrackKind.Group ? "Group" : "Return";
            title.Text = kind + ": " + entry.Name;
            dot.Background = new SolidColorBrush(MachineCanvas.ColourOfHead(m)).ToImmutable();
            chain.Text = entry.Chain;
            var more = new List<string>();
            if (entry.Members.Count > 0) more.Add("Members: " + string.Join(", ", entry.Members.Select(x => x.Name)));
            if (entry.Senders.Count > 0) more.Add("Receives sends from: " + string.Join(", ", entry.Senders.Select(x => x.Name)));
            if (entry.SendsTo.Count > 0) more.Add("Sends to: " + string.Join(", ", entry.SendsTo.Select(x => x.Name)));
            sends.Text = string.Join("\n", more);
            sends.IsVisible = more.Count > 0;
            IsVisible = true;
            Refresh();
        }

        void OnMachine(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is "IsMuted" or "IsSoloed") Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
        }

        /// <summary>Mute, solo, arm and the meter as the machine has them now.</summary>
        public void Refresh()
        {
            if (head == null) return;
            bool muteAble = head.DLL?.Info?.Type != MachineType.Master && !head.IsControlMachine;
            Mark(mute, head.IsMuted, muteAble);
            Mark(solo, head.IsSoloed, CanSolo(head));
            bool canArm = SessionLayout.CanArm(head) && core.SessionLayout != null;
            Mark(arm, canArm && core.SessionLayout.IsMachineArmed(head), canArm);
            if (canArm && core.SessionLayout.IsMachineArmed(head)) arm.Foreground = StudioTokens.RecordBrush;
            double level = canvas.BoxLevel(head);
            meter.Level = level;
            db.Text = level <= 0.003 ? "−∞ dB" : Math.Round(level * 60 - 60).ToString("0", System.Globalization.CultureInfo.InvariantCulture).Replace('-', '−') + " dB";
        }
    }
}
