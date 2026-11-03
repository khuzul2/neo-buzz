// Fork (docs/specs/studio-mvp.md S2): one row of the parameter window, ported from the WPF
// window's ParameterVM and its row template (RunTime/Themes/Default/ParameterWindow.xaml): in edit
// mode a visibility box and a lock, then the name (its description as the tooltip), the slider and
// the value as the machine describes it. Track parameters are named "track-name", an input's
// amp and pan "a-" and "p-". The right-click menu is the view's (ParameterWindowView.RowMenu).

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BuzzGUI.Interfaces;

namespace ReBuzz.Studio.Parameters
{
    using Control = Avalonia.Controls.Control;

    internal sealed class ParameterRow : Grid
    {
        readonly TextBlock name = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ParameterTheme.Text };
        readonly TextBlock valueText = new TextBlock { FontSize = 12, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ParameterTheme.ValueText };
        readonly Toggle shown, locked;
        readonly Action<IParameter, int> valueChanged, descriptionChanged;
        int lastValue = int.MinValue;
        bool editMode;

        public ParameterRow(IParameter parameter, int track)
        {
            Parameter = parameter;
            Track = track;
            Height = 16;
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,90,*,90");

            shown = new Toggle(Toggle.Kind.Visible) { Margin = new Thickness(1, 1, 4, 1), IsVisible = false };
            locked = new Toggle(Toggle.Kind.Lock) { Margin = new Thickness(0, 0, 4, 0), IsVisible = false };
            Slider = new ParameterSlider
            {
                Minimum = parameter.MinValue,
                Maximum = parameter.MaxValue,
                Value = parameter.GetValue(track),
                Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            SetColumn(shown, 0);
            SetColumn(locked, 1);
            SetColumn(name, 2);
            SetColumn(Slider, 3);
            SetColumn(valueText, 4);
            Children.Add(shown);
            Children.Add(locked);
            Children.Add(name);
            Children.Add(Slider);
            Children.Add(valueText);

            name.Text = DisplayName(parameter, track);
            if (!string.IsNullOrEmpty(parameter.Description)) ToolTip.SetTip(name, parameter.Description);
            IsLocked = parameter.Group.Type == ParameterGroupType.Input;
            IsShown = ParameterVisibility.IsVisible(parameter);
            shown.Changed += () => ShownChanged?.Invoke(this);
            Refresh(force: true);

            // As ParameterVM: the engine's value and description events (the window also polls,
            // since the engine raises them on its ticks, and there are none without audio).
            valueChanged = (p, t) => Refresh();
            descriptionChanged = (p, t) => Refresh(force: true);
            parameter.SubscribeEvents(track, valueChanged, descriptionChanged);
        }

        public IParameter Parameter { get; }

        public int Track { get; }

        public ParameterSlider Slider { get; }

        /// <summary>The name shown (the WPF window's).</summary>
        public string ParameterName => name.Text;

        /// <summary>The value as the machine describes it, as shown.</summary>
        public string ValueText => valueText.Text;

        /// <summary>Left out by the edit functions (randomize, morph). Input parameters start locked.</summary>
        public bool IsLocked
        {
            get => locked.IsChecked;
            set => locked.IsChecked = value;
        }

        /// <summary>Shown outside edit mode (ParameterWindowProperties).</summary>
        public bool IsShown
        {
            get => shown.IsChecked;
            set => shown.IsChecked = value;
        }

        /// <summary>Hidden by the track choice.</summary>
        public bool FilteredOut { get; set; }

        /// <summary>Edit mode shows the boxes and every parameter, hidden ones too.</summary>
        public bool EditMode
        {
            get => editMode;
            set
            {
                editMode = value;
                shown.IsVisible = locked.IsVisible = value;
                UpdateVisibility();
            }
        }

        public void UpdateVisibility() => IsVisible = !FilteredOut && (editMode || IsShown);

        public static string DisplayName(IParameter parameter, int track)
        {
            string n = parameter.GetDisplayName(track);
            return parameter.Group.Type switch
            {
                ParameterGroupType.Input => parameter.IndexInGroup switch { 0 => "a-" + n, 1 => "p-" + n, _ => "?-" + n },
                ParameterGroupType.Track => $"{track}-{n}",
                _ => n,
            };
        }

        /// <summary>Shows the parameter's value now (only describes it again when it changed).</summary>
        public void Refresh(bool force = false)
        {
            if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Refresh(force));
                return;
            }
            int v = Parameter.GetValue(Track);
            if (v == lastValue && !force) return;
            lastValue = v;
            Slider.Value = v;
            try
            {
                valueText.Text = Parameter.DescribeValue(v);
            }
            catch (Exception)
            {
                valueText.Text = v.ToString();
            }
        }

        public void Release() => Parameter.UnsubscribeEvents(Track, valueChanged, descriptionChanged);

        /// <summary>The user flipped the row's visibility box.</summary>
        public event Action<ParameterRow> ShownChanged;

        /// <summary>The edit mode's small toggles: a box (shown) and a padlock (locked), drawn.</summary>
        internal sealed class Toggle : Control
        {
            public enum Kind { Visible, Lock }

            readonly Kind kind;
            bool isChecked;

            public Toggle(Kind kind)
            {
                this.kind = kind;
                Width = kind == Kind.Visible ? 14 : 16;
                Height = 14;
                Cursor = new Cursor(StandardCursorType.Hand);
            }

            public bool IsChecked
            {
                get => isChecked;
                set
                {
                    isChecked = value;
                    InvalidateVisual();
                }
            }

            public event Action Changed;

            protected override void OnPointerPressed(PointerPressedEventArgs e)
            {
                base.OnPointerPressed(e);
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                IsChecked = !IsChecked;     // ClickMode="Press", as the WPF toggles
                Changed?.Invoke();
            }

            public override void Render(DrawingContext context)
            {
                var pen = new Pen(ParameterTheme.DimText, 1);
                if (kind == Kind.Visible)
                {
                    context.DrawRectangle(null, pen, new Rect(1.5, 1.5, 11, 11));
                    if (isChecked) context.FillRectangle(ParameterTheme.Text, new Rect(4, 4, 6, 6));
                    return;
                }
                // A padlock: the body, and the shackle closed (locked) or lifted (unlocked).
                var brush = isChecked ? ParameterTheme.Lock : ParameterTheme.DimText;
                context.DrawRectangle(brush, null, new RoundedRect(new Rect(3, 7, 10, 7), 1));
                double lift = isChecked ? 0 : 2.5;
                var shackle = new StreamGeometry();
                using (var g = shackle.Open())
                {
                    g.BeginFigure(new Point(5, 7 - lift), false);
                    g.LineTo(new Point(5, 4.5 - lift));
                    g.ArcTo(new Point(11, 4.5 - lift), new Size(3, 3), 0, false, SweepDirection.Clockwise);
                    g.LineTo(new Point(11, isChecked ? 7 : 5.5 - lift));
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(brush, 1.6), shackle);
            }
        }
    }
}
