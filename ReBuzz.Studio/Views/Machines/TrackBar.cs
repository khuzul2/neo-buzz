// Fork (docs/specs/ux-overhaul.md §11, MT1): the Machines view's Tracks bar and card.
// The bar: a chip for every track, group and return, the Master's chain and an "Unrouted N" chip. It wraps to two rows at most: what does not
// fit goes behind a "+N" chip that opens a filterable list (the same order). Tab reaches every chip and the arrow keys move along them. A chip the pointer is on or that has the focus lights its track (the canvas dims the rest);
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
        readonly List<Button> chips = new();          // the chips in the bar (the "+N" chip last), in keyboard order
        readonly List<(TrackTarget Target, TrackEntry Entry)> all = new();   // every entry, in order
        readonly List<double> widths = new();
        readonly ScrollViewer scroll;
        readonly Popup popup;
        readonly TextBox filter = new TextBox { Watermark = "Filter tracks", FontSize = StudioTokens.FontBody, Margin = new Thickness(0, 0, 0, 6) };
        readonly StackPanel list = new StackPanel { Spacing = 2 };
        Button more;
        int shown;
        double laidOutFor = -1;
        bool attached;

        /// <summary>Two rows of chips; the rest is behind the "+N" chip.</summary>
        public const int MaxRows = 2;
        const double RowHeight = 30;
        public const double MaxHeight2Rows = MaxRows * (RowHeight + 2) + 12;

        public TrackBar(ReBuzzCore core, MachineCanvas canvas)
        {
            this.core = core;
            this.canvas = canvas;
            scroll = new ScrollViewer
            {
                Content = wrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = MaxHeight2Rows + 2,
                Focusable = false,
            };
            popup = new Popup { IsLightDismissEnabled = true, Placement = PlacementMode.BottomEdgeAlignedLeft, Child = BuildPopup() };
            wrap.Margin = new Thickness(8, 6, 8, 6);
            Child = new Grid { Children = { scroll, popup } };
            BorderThickness = new Thickness(0, 0, 0, 1);
            BorderBrush = StudioTokens.SurfaceBorderBrush;
            Background = StudioTokens.SurfacePanelBrush;
            IsVisible = false;
            Avalonia.Automation.AutomationProperties.SetName(this, "Tracks");
        }

        /// <summary>The chips in the bar, in keyboard order (the "+N" chip last when something does not fit).</summary>
        public IReadOnlyList<Button> Chips => chips;

        /// <summary>The "+N" chip, or null when every chip fits.</summary>
        public Button MoreChip => more;

        /// <summary>How many entries are behind the "+N" chip.</summary>
        public int HiddenCount => all.Count - shown;

        public bool MoreOpen => popup.IsOpen;

        /// <summary>The buttons in the open list (tests): every hidden entry that matches the filter.</summary>
        public IReadOnlyList<Button> MoreItems => list.Children.OfType<Button>().ToList();

        public TextBox MoreFilter => filter;

        /// <summary>How many entries there are (chips in the bar and behind the "+N" chip).</summary>
        public int EntryCount => all.Count;

        Control BuildPopup()
        {
            var scrollList = new ScrollViewer { Content = list, MaxHeight = 280, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            filter.TextChanged += (s, e) => FillList();
            filter.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Down && list.Children.FirstOrDefault() is Button first) { first.Focus(NavigationMethod.Directional); e.Handled = true; }
                else if (e.Key == Key.Escape) { CloseMore(); e.Handled = true; }
            };
            var border = new Border
            {
                Background = StudioTokens.SurfacePanelBrush,
                BorderBrush = StudioTokens.SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8),
                MinWidth = 220,
                Child = new StackPanel { Children = { filter, scrollList } },
            };
            border.KeyDown += (s, e) => { if (e.Key == Key.Escape) { CloseMore(); e.Handled = true; } };
            Avalonia.Automation.AutomationProperties.SetName(border, "More tracks");
            return border;
        }

        /// <summary>Opens the list behind the "+N" chip (its filter has the focus).</summary>
        public void OpenMore()
        {
            if (more == null) return;
            filter.Text = "";
            FillList();
            popup.PlacementTarget = more;
            popup.IsOpen = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => filter.Focus(), Avalonia.Threading.DispatcherPriority.Input);
        }

        public void CloseMore()
        {
            if (!popup.IsOpen) return;
            popup.IsOpen = false;
            more?.Focus();
        }

        void FillList()
        {
            list.Children.Clear();
            string q = (filter.Text ?? "").Trim();
            foreach (var (target, entry) in all.Skip(shown))
            {
                string name = target.Unrouted ? "Unrouted" : EntryText(entry);
                if (q.Length > 0 && name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var item = target.Unrouted ? MakeUnrouted(canvas.TrackModel) : Make((IMachine)entry.Head.Tag, entry);
                item.HorizontalAlignment = HorizontalAlignment.Stretch;
                item.HorizontalContentAlignment = HorizontalAlignment.Left;
                item.Margin = new Thickness(0);
                item.Click += (s, e) => CloseMore();
                list.Children.Add(item);
            }
            if (list.Children.Count == 0)
                list.Children.Add(new TextBlock { Text = "Nothing matches", FontSize = StudioTokens.FontBody, Foreground = StudioTokens.TextSecondaryBrush, Margin = new Thickness(4) });
        }

        static string EntryText(TrackEntry e) => e.Name + (e.Kind == TrackKind.Group ? " · group" : e.Kind == TrackKind.Return ? " · return" : "");

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
            popup.IsOpen = false;
            cap = int.MaxValue;
            all.Clear();
            foreach (var e in model.Entries)
                if (e.Head.Tag is IMachine head) all.Add((TrackTarget.Entry(head), e));
            if (model.Unrouted.Count > 0) all.Add((TrackTarget.AllUnrouted, null));
            MakeChips(model);
            Mark();
        }

        /// <summary>Makes the chips for the room there is: two rows, then "+N" for the rest (the same order).</summary>
        void MakeChips(TrackModel model)
        {
            double avail = Bounds.Width > 40 ? Bounds.Width - 16 : 1000;
            laidOutFor = Bounds.Width;
            var made = new List<Button>(all.Count);
            widths.Clear();
            foreach (var (target, entry) in all)
            {
                var chip = target.Unrouted ? MakeUnrouted(model) : Make((IMachine)entry.Head.Tag, entry);
                made.Add(chip);
                widths.Add(EstimateWidth(chip) + 6);
            }
            int fit = Math.Min(FitCount(avail, out double moreWidth, made.Count), cap);
            chips.Clear();
            wrap.Children.Clear();
            more = null;
            shown = fit;
            for (int i = 0; i < fit; i++)
            {
                chips.Add(made[i]);
                wrap.Children.Add(made[i]);
            }
            if (fit < made.Count)
            {
                more = MakeMore(made.Count - fit);
                chips.Add(more);
                wrap.Children.Add(more);
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(Verify, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        int cap = int.MaxValue;

        /// <summary>The widths are estimated before the chips are laid out; once they are, a third row (the estimate was short) takes chips back out.</summary>
        void Verify()
        {
            if (!attached || !IsVisible || chips.Count == 0 || Bounds.Width < 40) return;
            UpdateLayout();
            var rows = chips.Select(c => Math.Round(c.Bounds.Y)).Distinct().OrderBy(y => y).ToList();
            if (rows.Count <= MaxRows) return;
            int over = chips.Count(c => Math.Round(c.Bounds.Y) >= rows[MaxRows]);
            cap = Math.Max(1, shown - over - 1);
            var model = canvas.TrackModel;
            if (model == null) return;
            MakeChips(model);
            Mark();
        }

        static double EstimateWidth(Button chip)
        {
            double w = 2 + 20;      // the border and the padding
            var content = chip.Content;
            var text = content as TextBlock ?? (content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault();
            if (content is StackPanel sp) w += sp.Children.Count > 1 ? 9 + sp.Spacing : 0;
            if (text != null)
            {
                var ft = new FormattedText(text.Text ?? "", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight), text.FontSize, Brushes.Black);
                w += Math.Min(ft.Width, text.MaxWidth > 0 && !double.IsInfinity(text.MaxWidth) ? text.MaxWidth : ft.Width);
            }
            return w;
        }

        Button MakeMore(int n)
        {
            var chip = NewChip(new TextBlock { Text = "+" + n, FontSize = StudioTokens.FontBody, VerticalAlignment = VerticalAlignment.Center }, TrackTarget.AllUnrouted, "+" + n + " more tracks");
            chip.Tag = "more";
            // The chip stands for no track: it lights nothing and opens the list.
            ToolTip.SetTip(chip, "Show the other " + n + " in a list you can filter");
            return chip;
        }

        int FitCount(double avail, out double moreWidth, int count)
        {
            moreWidth = 60;
            if (Fits(count, avail, 0)) return count;
            int k = count;
            while (k > 0 && !Fits(k, avail, moreWidth)) k--;
            return k;
        }

        /// <summary>The first k chips (and room for a "+N" chip after them) fit in the rows.</summary>
        bool Fits(int k, double avail, double extra)
        {
            int rows = 1;
            double x = 0;
            for (int i = 0; i < k + (extra > 0 ? 1 : 0); i++)
            {
                double w = i < k ? widths[i] : extra;
                if (x + w > avail && x > 0) { rows++; x = 0; }
                x += w;
            }
            return rows <= MaxRows;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == BoundsProperty && attached && IsVisible && Math.Abs(Bounds.Width - laidOutFor) > 0.5)
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var model = canvas.TrackModel;
                    if (attached && IsVisible && model != null && Math.Abs(Bounds.Width - laidOutFor) > 0.5) { cap = int.MaxValue; MakeChips(model); Mark(); }
                }, Avalonia.Threading.DispatcherPriority.Loaded);
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
            if (name.EndsWith("more tracks"))
                chip.Click += (s, e) => OpenMore();
            else
            {
                chip.PointerEntered += (s, e) => canvas.SetHoverLit(target);
                chip.PointerExited += (s, e) => canvas.SetHoverLit(null);
                chip.GotFocus += (s, e) => canvas.SetFocusLit(target);
                chip.LostFocus += (s, e) => canvas.SetFocusLit(null);
                chip.Click += (s, e) => Choose(target);
            }
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
            bool mineHidden = mine != null && all.Skip(shown).Any(a => !a.Target.Unrouted && a.Target.Head == mine.Head.Tag);
            foreach (var chip in chips)
            {
                if (chip.Tag is not TrackTarget t)
                {
                    chip.BorderBrush = mineHidden ? StudioTokens.AccentBrush : StudioTokens.SurfaceBorderBrush;
                    chip.BorderThickness = new Thickness(mineHidden ? 2 : 1);
                    continue;
                }
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
            title.Text = entry.Kind == TrackKind.MasterChain ? "Master chain" : kind + ": " + entry.Name;
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
