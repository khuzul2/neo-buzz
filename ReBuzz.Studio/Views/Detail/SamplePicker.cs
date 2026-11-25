// Fork (TM1, docs/specs/machines.md Neo Sampler Tracker mode): the sample picker of the piano roll. Opens from a chip or S for the selected notes:
// the machine's samples with their waves and lengths. Hover previews a sample, typing a number or a name jumps, Up and Down move, Enter applies to
// every selected note, Esc closes; "Key map" gives a note back to the sampler's zones. When the selection holds several samples it says so, lists how
// many notes each has, and steps each note by one (Each -1, Each +1). Every apply is one undo step of the song.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Controls.Shapes;
using Buzz.MachineInterface;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Views.Detail
{
    using Control = Avalonia.Controls.Control;

    /// <summary>A sample's wave as a few dozen strokes: the outline of its peaks, mirrored.</summary>
    internal sealed class WaveOutline : Control
    {
        readonly float[] peaks;
        readonly IPen pen;

        public WaveOutline(float[] peaks, Color color)
        {
            this.peaks = peaks ?? Array.Empty<float>();
            pen = new Pen(new ImmutableSolidColorBrush(color), 1.2);
            Width = 64;
            Height = 18;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext dc)
        {
            if (peaks.Length == 0) { dc.DrawLine(pen, new Point(0, 9), new Point(64, 9)); return; }
            double step = 64.0 / peaks.Length;
            for (int i = 0; i < peaks.Length; i++)
            {
                double half = Math.Max(0.5, peaks[i] * 8);
                double x = i * step + step / 2;
                dc.DrawLine(pen, new Point(x, 9 - half), new Point(x, 9 + half));
            }
        }
    }

    internal sealed class SamplePicker : Border
    {
        public const double PickerWidth = 330;

        sealed record Row(int Number, NoteSample Sample);

        readonly TextBox search;
        readonly TextBlock applies;
        readonly StackPanel top = new StackPanel { Spacing = 2 };
        readonly StackPanel list = new StackPanel { Spacing = 0 };
        readonly ScrollViewer scroll;
        PianoRoll roll;
        NoteSampleContext ctx;
        List<Row> rows = new List<Row>();
        int highlight;
        int hovered = -1;
        int previewing;
        readonly Button loadButton, openButton;

        /// <summary>The most the list is high before it scrolls.</summary>
        public double MaxListHeight { get => scroll.MaxHeight; set => scroll.MaxHeight = value; }

        /// <summary>The picker closed (an apply, Esc, a click outside): the host gives the keyboard back to the roll.</summary>
        public event Action Closed;

        /// <summary>The host loads a sample file (null: the picker has no way to).</summary>
        public Action LoadSample { get; set; }

        /// <summary>The host opens the sampler's own window.</summary>
        public Action OpenSampler { get; set; }

        public SamplePicker()
        {
            Background = StudioTokens.SurfacePanelBrush;
            BorderBrush = StudioTokens.SurfaceBorderBrush;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(10);
            Padding = new Thickness(8);
            Width = PickerWidth;
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Top;
            IsVisible = false;
            Focusable = false;
            Avalonia.Automation.AutomationProperties.SetName(this, "Sample picker");
            BoxShadow = BoxShadows.Parse("0 12 32 0 #8C000000");

            search = new TextBox { Watermark = "Search samples or type a number", FontSize = 12, MinHeight = 0, Padding = new Thickness(6, 5), BorderThickness = new Thickness(1) };
            Avalonia.Automation.AutomationProperties.SetName(search, "Search samples");
            search.TextChanged += (s, e) => Refill(filterChanged: true);
            applies = new TextBlock { FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, Margin = new Thickness(6, 6, 6, 4) };
            scroll = new ScrollViewer { Content = list, MaxHeight = 280, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            loadButton = Footer("Load sample…", () => LoadSample?.Invoke());
            openButton = Footer("Open in sampler", () => { OpenSampler?.Invoke(); Close(); });
            var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 4, 0, 0) };
            Grid.SetColumn(openButton, 1);
            footer.Children.Add(loadButton);
            footer.Children.Add(openButton);
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(search);
            stack.Children.Add(top);
            stack.Children.Add(Divider());
            stack.Children.Add(scroll);
            stack.Children.Add(Divider());
            stack.Children.Add(footer);
            Child = stack;
            AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }

        static Border Divider() => new Border { Height = 1, Background = StudioTokens.SurfaceBorderBrush, Margin = new Thickness(0, 4) };

        Button Footer(string text, Action click)
        {
            var b = new Button
            {
                Content = text, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0), Padding = new Thickness(6, 6), Focusable = false,
            };
            b.Click += (s, e) => click();
            return b;
        }

        // ------------------------------------------------------------------ opening

        /// <summary>The search box (tests type in it).</summary>
        internal TextBox Search => search;

        /// <summary>The rows the list shows now: the sample numbers, 0 for Key map (tests).</summary>
        internal IReadOnlyList<int> RowNumbers => rows.Select(r => r.Number).ToList();

        internal int Highlighted => rows.Count == 0 ? -1 : rows[Math.Clamp(highlight, 0, rows.Count - 1)].Number;

        /// <summary>What the picker says of the selection ("Applies to 3 selected notes", or the mixed header's words).</summary>
        internal string Summary => applies.Text;

        /// <summary>Whether the selection holds more than one sample (the mixed form).</summary>
        internal bool Mixed { get; private set; }

        public void Open(PianoRoll roll)
        {
            this.roll = roll;
            ctx = roll.Samples;
            search.Text = "";
            highlight = 0;
            IsVisible = true;
            Refill(filterChanged: false);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => search.Focus(), Avalonia.Threading.DispatcherPriority.Input);
        }

        public void Close()
        {
            StopPreview();
            if (!IsVisible) return;
            IsVisible = false;
            Closed?.Invoke();
        }

        /// <summary>The machine's samples changed (a wave loaded, renamed): the list again.</summary>
        public void Reload()
        {
            if (!IsVisible || roll == null) return;
            ctx = roll.Samples;
            Refill(filterChanged: false);
        }

        // ------------------------------------------------------------------ the content

        List<ClipNote> Selected() => roll.Selection.OrderBy(i => i).Where(i => i < roll.Notes.Count).Select(i => roll.Notes[i]).ToList();

        void Refill(bool filterChanged)
        {
            if (roll == null || ctx == null) return;
            var selected = Selected();
            var counts = selected.GroupBy(n => n.Sample).ToDictionary(g => g.Key, g => g.Count());
            Mixed = counts.Count > 1;
            top.Children.Clear();
            if (Mixed)
            {
                applies.Text = $"Mixed · {selected.Count} notes";
                var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 2) };
                head.Children.Add(new TextBlock { Text = "Mixed:", FontSize = 12, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center });
                foreach (int number in counts.Keys.OrderBy(k => k)) head.Children.Add(Swatch(number));
                head.Children.Add(new TextBlock { Text = $"{selected.Count} notes", FontSize = 12, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
                top.Children.Add(head);
                var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), Margin = new Thickness(0, 4) };
                var minus = Footer("Each −1", () => Step(-1));
                var plus = Footer("Each +1", () => Step(1));
                var key = Footer("Key map", () => Apply(0));
                Grid.SetColumn(plus, 1);
                Grid.SetColumn(key, 2);
                buttons.Children.Add(minus);
                buttons.Children.Add(plus);
                buttons.Children.Add(key);
                top.Children.Add(buttons);
            }
            else
            {
                applies.Text = selected.Count == 1 ? "Applies to this note" : $"Applies to {selected.Count} selected notes";
                top.Children.Add(applies);
            }

            string text = (search.Text ?? "").Trim();
            var all = ctx.Samples;
            IEnumerable<NoteSample> shown = all;
            bool digits = text.Length > 0 && text.All(char.IsDigit);
            if (digits)
            {
                shown = all.Where(s => s.Number.ToString(CultureInfo.InvariantCulture).StartsWith(text.TrimStart('0') == "" ? "0" : text.TrimStart('0'), StringComparison.Ordinal) || s.Name.Contains(text, StringComparison.OrdinalIgnoreCase));
                shown = shown.OrderBy(s => s.Number.ToString(CultureInfo.InvariantCulture) == text.TrimStart('0') ? 0 : 1).ThenBy(s => s.Number);
            }
            else if (text.Length > 0) shown = all.Where(s => s.Name.Contains(text, StringComparison.OrdinalIgnoreCase));
            rows = new List<Row>();
            if (text.Length == 0 && !Mixed) rows.Add(new Row(0, null));
            foreach (var s in shown) rows.Add(new Row(s.Number, s));
            if (filterChanged || highlight >= rows.Count) highlight = 0;
            if (!filterChanged && !Mixed && text.Length == 0 && counts.Count == 1 && counts.Keys.First() > 0)
            {
                int at = rows.FindIndex(r => r.Number == counts.Keys.First());
                if (at >= 0) highlight = at;
            }
            list.Children.Clear();
            rowViews.Clear();
            rowMarks.Clear();
            for (int i = 0; i < rows.Count; i++) list.Children.Add(MakeRow(i, rows[i], counts));
            if (rows.Count == 0)
                list.Children.Add(new TextBlock { Text = ctx.Samples.Count == 0 ? "This sampler has no samples yet: load one." : "No sample matches.", FontSize = 12, Foreground = StudioTokens.TextSecondaryBrush, Margin = new Thickness(8, 8) });
            Paint();
            ScrollToHighlight();
        }

        Control Swatch(int number, double width = 22)
        {
            var back = number > 0 ? SampleColours.BrushOf(number) : (IBrush)StudioTokens.SurfaceControlBrush;
            var b = new Border { Width = width, Height = 18, CornerRadius = new CornerRadius(4), Background = back, VerticalAlignment = VerticalAlignment.Center };
            b.Child = new TextBlock { Text = SampleColours.Label(number), FontSize = 11, FontFamily = new FontFamily("JetBrains Mono, Cascadia Mono, Menlo, Consolas, monospace"), FontWeight = FontWeight.Medium, Foreground = number > 0 ? SampleColours.Ink : StudioTokens.TextSecondaryBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return b;
        }

        readonly List<Border> rowViews = new List<Border>();
        readonly List<(Control Check, Control Play)> rowMarks = new List<(Control, Control)>();
        HashSet<int> currents = new HashSet<int>();

        Border MakeRow(int index, Row row, Dictionary<int, int> counts)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(row.Sample == null ? "22,*,Auto" : Mixed ? "22,*,64,52,14" : "22,*,64,40,14"), ColumnSpacing = 8 };
            if (row.Sample == null)
            {
                var dashed = new Border { Width = 22, Height = 18, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = StudioTokens.TextPlaceholderBrush, VerticalAlignment = VerticalAlignment.Center };
                grid.Children.Add(dashed);
                var name = new TextBlock { Text = "Key map", FontSize = 12, Foreground = StudioTokens.TextPrimaryBrush, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(name, 1);
                var hint = new TextBlock { Text = "by pitch, as a sampler", FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(hint, 2);
                grid.Children.Add(name);
                grid.Children.Add(hint);
                rowMarks.Add((null, null));
            }
            else
            {
                var s = row.Sample;
                grid.Children.Add(Swatch(s.Number));
                var name = new TextBlock { Text = s.Name, FontSize = 12, Foreground = StudioTokens.TextPrimaryBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(name, 1);
                grid.Children.Add(name);
                var wave = new WaveOutline(s.Peaks, SampleColours.Of(s.Number)) { VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(wave, 2);
                grid.Children.Add(wave);
                string meta = Mixed ? (counts.TryGetValue(s.Number, out int c) ? (c == 1 ? "1 note" : $"{c} notes") : "") : NoteSampleContext.Length(s);
                var len = new TextBlock { Text = meta, FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("JetBrains Mono, Cascadia Mono, Menlo, Consolas, monospace") };
                Grid.SetColumn(len, 3);
                grid.Children.Add(len);
                var check = new Path { Data = Geometry.Parse("M5,12 L10,17 L19,7"), Stroke = StudioTokens.TextPrimaryBrush, StrokeThickness = 2.2, Width = 12, Height = 12, Stretch = Stretch.Uniform, IsVisible = false };
                var play = new Path { Data = Geometry.Parse("M7,5 L19,12 L7,19 Z"), Fill = StudioTokens.TextPrimaryBrush, Width = 10, Height = 10, Stretch = Stretch.Uniform, IsVisible = false };
                var mark = new Panel { Width = 14, VerticalAlignment = VerticalAlignment.Center, Children = { check, play } };
                Grid.SetColumn(mark, 4);
                grid.Children.Add(mark);
                rowMarks.Add((check, play));
            }
            var border = new Border { Child = grid, Padding = new Thickness(6, 5), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
            int number = row.Number;
            border.PointerEntered += (s, e) => { hovered = index; Preview(number); Paint(); };
            border.PointerExited += (s, e) => { if (hovered == index) hovered = -1; StopPreview(); Paint(); };
            border.PointerPressed += (s, e) =>
            {
                if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
                Apply(number);
                e.Handled = true;
            };
            rowViews.Add(border);
            currents = counts.Count == 1 && !Mixed ? new HashSet<int>(counts.Keys) : new HashSet<int>();
            return border;
        }

        void Paint()
        {
            for (int i = 0; i < rowViews.Count && i < rows.Count; i++)
            {
                bool current = currents.Contains(rows[i].Number) && !(rows[i].Number == 0 && currents.Count == 1 && !currents.Contains(0));
                bool lit = i == hovered || (hovered < 0 && i == highlight && !current);
                rowViews[i].Background = current ? StudioTokens.SelectionFillBrush : lit ? StudioTokens.SurfaceControlBrush : Brushes.Transparent;
                if (i < rowMarks.Count && rowMarks[i].Check != null)
                {
                    rowMarks[i].Check.IsVisible = current && !(i == hovered && previewing > 0);
                    rowMarks[i].Play.IsVisible = i == hovered && previewing > 0;
                }
            }
        }

        void ScrollToHighlight()
        {
            if (highlight < 0 || highlight >= rowViews.Count) return;
            double y = highlight * 30;
            if (y < scroll.Offset.Y) scroll.Offset = new Vector(0, y);
            else if (y + 30 > scroll.Offset.Y + scroll.MaxHeight) scroll.Offset = new Vector(0, y + 30 - scroll.MaxHeight);
        }

        // ------------------------------------------------------------------ acting

        void Preview(int number)
        {
            StopPreview();
            if (number <= 0 || ctx == null) return;
            try { ctx.Provider.PreviewNoteSample(number, true); previewing = number; }
            catch (Exception) { previewing = 0; }
        }

        void StopPreview()
        {
            if (previewing == 0) return;
            try { ctx?.Provider.PreviewNoteSample(previewing, false); }
            catch (Exception) { }
            previewing = 0;
        }

        /// <summary>Gives every selected note this sample (0: the sampler's zones) and closes: one undo step.</summary>
        internal void Apply(int number)
        {
            StopPreview();
            roll?.SetSample(number);
            Close();
        }

        void Step(int delta)
        {
            roll?.StepSelected(delta);
            Refill(filterChanged: false);
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;
                case Key.Down:
                    Move(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    Move(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (rows.Count > 0) Apply(rows[Math.Clamp(highlight, 0, rows.Count - 1)].Number);
                    e.Handled = true;
                    break;
            }
        }

        void Move(int delta)
        {
            if (rows.Count == 0) return;
            highlight = Math.Clamp(highlight + delta, 0, rows.Count - 1);
            hovered = -1;
            Paint();
            ScrollToHighlight();
        }
    }
}
