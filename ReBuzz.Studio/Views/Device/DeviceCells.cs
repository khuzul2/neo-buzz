// Fork (docs/specs/ux-overhaul.md, the parameter panel, layout D): the controls of Knobs mode. A knob (the Neo editors' Knob: vertical drag,
// Shift for fine, double click types a value, right click opens the menu), a segmented strip, a toggle or a drop-down for a choice, each in a
// DeviceCell that also holds the inline value editor. They drive the machine through DeviceEdits (a drag is one undo step).

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using NeoBuzz.Editor;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Device
{
    using Control = Avalonia.Controls.Control;

    /// <summary>What a cell's control does besides drawing: follows the parameter, and asks for the menu.</summary>
    internal interface IDeviceFace
    {
        /// <summary>Shows the parameter's value now (it moved outside the control).</summary>
        void Sync();

        /// <summary>A gesture is in progress (a drag): the cell is not refreshed under it.</summary>
        bool Busy { get; }

        event Action ContextRequested;

        /// <summary>The pointer entered or pressed the control (the explain strip speaks of it).</summary>
        event Action Touched;
    }

    internal sealed class DeviceKnob : Knob, IDeviceFace
    {
        readonly DeviceParam p;
        readonly ReBuzzCore core;
        int from, shown = int.MinValue;
        bool down;

        public const double Diameter = 38;

        public DeviceKnob(IMachine machine, DeviceParam param, ReBuzzCore core, NeoColor accent) : base(accent, Diameter)
        {
            p = param;
            this.core = core;
            MaxLabelWidth = DeviceCell.CellWidth - 4;
            Label = p.Label;
            Origin = p.ZeroFraction;
            Steps = p.Max - p.Min;
            Default = p.Fraction(p.Default);
            UserChanged += t =>
            {
                DeviceEdits.SetLive(p, p.Min + (int)Math.Round(t * (p.Max - p.Min)));
                Show(p.Value);
            };
            Phase += ph =>
            {
                if (ph == KnobPhase.Touch) { from = p.Value; Touched?.Invoke(); }
                else if (ph == KnobPhase.Enter) Touched?.Invoke();
                else if (ph == KnobPhase.Release)
                {
                    int to = p.Value;
                    if (to != from) DeviceEdits.Changed(core, p, from, to, stepped: !down);
                    down = false;
                }
            };
            RightClicked += () => ContextRequested?.Invoke();
            if (p.Parameter != null && p.Parameter.Type != ParameterType.Note)
                BindModulation(machine, p.Parameter, p.Track, p.Fraction);
            Sync();
        }

        public DeviceParam Param => p;

        public bool Busy => down;

        public event Action ContextRequested;
        public event Action Touched;

        /// <summary>A double click: the value is typed (not reset to the default as the editors' knob does).</summary>
        public event Action TypeRequested;

        void Show(int raw)
        {
            shown = raw;
            Value = p.Fraction(raw);
            ValueText = p.Describe(raw);
            Refresh();
        }

        public void Sync()
        {
            if (down) return;
            int v = p.Value;
            if (v != shown) Show(v);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                TypeRequested?.Invoke();
                e.Handled = true;
                return;
            }
            down = true;
            base.OnMouseLeftButtonDown(e);
        }
    }

    /// <summary>A choice: a strip of buttons (two or three choices), a toggle (an Off/On switch) or a compact drop-down with the value shown (more).</summary>
    internal sealed class DeviceChoice : NeoElement, IDeviceFace
    {
        public enum Face { Strip, Toggle, Dropdown }

        public const int StripMax = 3;

        readonly DeviceParam p;
        readonly ReBuzzCore core;
        readonly NeoColor accent;
        readonly AvaloniaCanvas canvas;
        readonly ChoiceStrip strip;
        readonly ToggleFace toggle;
        readonly string[] names;
        int selected = -1, hover = -1;

        public DeviceChoice(DeviceParam param, ReBuzzCore core, NeoColor accent)
        {
            p = param;
            this.core = core;
            this.accent = accent;
            canvas = new AvaloniaCanvas(this);
            names = new string[p.Max - p.Min + 1];
            for (int i = 0; i < names.Length; i++) names[i] = i < p.Choices.Count ? p.Choices[i] : (p.Min + i).ToString();
            if (p.Kind == DeviceKind.Toggle)
            {
                Mode = Face.Toggle;
                toggle = new ToggleFace(p.Label, DeviceCell.CellWidth - 6, canvas);
                Width = toggle.Width;
                Height = toggle.Height;
            }
            else if (names.Length <= StripMax)
            {
                Mode = Face.Strip;
                strip = new ChoiceStrip(p.Label, names, 0, false, canvas);
                Width = strip.Width;
                Height = strip.Height;
            }
            else
            {
                Mode = Face.Dropdown;
                Width = DeviceCell.CellWidth * 2 - 4;
                Height = DropdownFace.Height;
            }
            Cursor = Cursors.Hand;
            Focusable = false;
            Avalonia.Automation.AutomationProperties.SetName(this, p.Label);
            Sync();
        }

        public Face Mode { get; }

        public DeviceParam Param => p;

        public bool Busy => false;

        public bool IsOn => selected > 0;

        public int Selected => selected;

        public event Action ContextRequested;
        public event Action Touched;

        public void Sync()
        {
            int v = p.Value - p.Min;
            if (v == selected) return;
            selected = v;
            InvalidateVisual();
        }

        /// <summary>Where choice <paramref name="i"/>'s button is, for tests; a toggle's one button for either.</summary>
        public Rect CellBounds(int i)
        {
            var r = toggle?.Button ?? strip?.Cell(i) ?? new NeoRect(0, 0, Width, Height);
            return new Rect(r.X, r.Y, r.Width, r.Height);
        }

        void Set(int index)
        {
            DeviceEdits.Set(core, p, p.Min + index);
            Sync();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Touched?.Invoke();
            var pt = e.GetPosition(this);
            if (Mode == Face.Toggle) { if (toggle.Hit(pt.X, pt.Y)) Set(selected > 0 ? 0 : names.Length - 1); }
            else if (Mode == Face.Strip) { int i = strip.CellAt(pt.X, pt.Y); if (i >= 0) Set(i); }
            else PickerPopup.Show(this, 0, Height, names, Set, names.Length > 12);
            e.Handled = true;
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            ContextRequested?.Invoke();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var pt = e.GetPosition(this);
            int i = Mode == Face.Strip ? strip.CellAt(pt.X, pt.Y) : Mode == Face.Toggle ? (toggle.Hit(pt.X, pt.Y) ? 0 : -1) : 0;
            if (i == hover) return;
            hover = i;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            hover = -1;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var c = canvas.On(dc);
            if (Mode == Face.Toggle) toggle.Draw(c, selected > 0, hover >= 0, accent);
            else if (Mode == Face.Strip) strip.Draw(c, selected, hover, accent);
            else DropdownFace.Draw(c, Bounds.Width > 0 ? Bounds.Width : Width, p.Label, DropdownFace.TextOf(names, selected), accent);
        }
    }

    /// <summary>A control with the inline editor a double click opens over it.</summary>
    internal sealed class DeviceCell : Grid
    {
        /// <summary>The width a knob cell is; a card is four of them.</summary>
        public const double CellWidth = 68;

        readonly DeviceParam p;
        readonly ReBuzzCore core;
        readonly Control face;
        readonly TextBox editor;
        readonly LockMark lockMark;

        public DeviceCell(IMachine machine, DeviceParam param, ReBuzzCore core, NeoColor accent, Action<DeviceCell> context, Action<DeviceParam> touched)
        {
            p = param;
            this.core = core;
            face = p.Kind == DeviceKind.Slider ? new DeviceKnob(machine, p, core, accent) : new DeviceChoice(p, core, accent);
            var f = (IDeviceFace)face;
            f.ContextRequested += () => context?.Invoke(this);
            f.Touched += () => touched?.Invoke(p);
            if (face is DeviceKnob k) k.TypeRequested += BeginType;
            Width = p.Kind == DeviceKind.Slider || face.Width < CellWidth ? CellWidth : face.Width + 4;     // a knob is one column wide: a wider one wraps the last knob of a row (PP3)
            Height = p.Kind == DeviceKind.Slider ? Math.Max(face.Height, DeviceKnob.Diameter + 18) : face.Height + 4;
            lockMark = new LockMark { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 1, 0), IsVisible = false };
            face.HorizontalAlignment = HorizontalAlignment.Center;
            face.VerticalAlignment = VerticalAlignment.Top;
            Children.Add(face);
            Children.Add(lockMark);
            RefreshMarks();
            editor = new TextBox
            {
                IsVisible = false,
                MinHeight = 0,
                Height = 22,
                Width = CellWidth - 4,
                Padding = new Thickness(3, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = Shell.Design.StudioTokens.FontBody,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, DeviceKnob.Diameter / 2 - 6, 0, 0),
            };
            Avalonia.Automation.AutomationProperties.SetName(editor, "Value");
            editor.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
                else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            };
            editor.LostFocus += (s, e) => Close();
            Children.Add(editor);
        }

        public DeviceParam Param => p;

        public Control Face => face;

        public bool IsTyping => editor.IsVisible;

        public TextBox Editor => editor;

        public string TypeError { get; private set; }

        /// <summary>A gesture or the editor is open: the cell is left alone.</summary>
        public bool Busy => ((IDeviceFace)face).Busy || editor.IsVisible;

        public void Sync()
        {
            ((IDeviceFace)face).Sync();
            bool locked = p.CanLock && p.Locked;
            if (lockMark.IsVisible != locked) lockMark.IsVisible = locked;
        }

        /// <summary>The lock mark, and the dimming of a hidden parameter (shown while "Show hidden" is on).</summary>
        public void RefreshMarks()
        {
            lockMark.IsVisible = p.CanLock && p.Locked;
            Opacity = p.Hidden ? 0.5 : 1;
        }

        public bool LockShown => lockMark.IsVisible;

        /// <summary>Opens the value editor over the knob, with its text selected.</summary>
        public void BeginType()
        {
            if (face is not DeviceKnob) return;
            TypeError = null;
            editor.Text = p.Describe(p.Value);
            editor.IsVisible = true;
            editor.Focus();
            editor.SelectAll();
            Dispatcher.UIThread.Post(() => { if (editor.IsVisible) { editor.Focus(); editor.SelectAll(); } }, DispatcherPriority.Loaded);
        }

        public bool Commit()
        {
            string error = DeviceEdits.Type(core, p, editor.Text);
            TypeError = error;
            if (error != null)
            {
                ToolTip.SetTip(editor, error);
                return false;
            }
            Close();
            Sync();
            return true;
        }

        public void Close()
        {
            if (!editor.IsVisible) return;
            editor.IsVisible = false;
            ToolTip.SetTip(editor, null);
        }
    }

    /// <summary>A small padlock, drawn: marks a locked parameter (left out of Randomize and Morph).</summary>
    internal sealed class LockMark : Control
    {
        public LockMark()
        {
            Width = 10;
            Height = 11;
            IsHitTestVisible = false;
            Avalonia.Automation.AutomationProperties.SetName(this, "Locked");
        }

        public override void Render(DrawingContext ctx)
        {
            var brush = ReBuzz.Studio.Shell.Design.StudioTokens.TextSecondaryBrush;
            var pen = new Pen(brush, 1.4);
            ctx.DrawRectangle(brush, null, new Rect(1, 5, 8, 6), 1.5, 1.5);
            ctx.DrawGeometry(null, pen, StreamGeometry.Parse("M 2.8,5 L 2.8,3.6 A 2.2,2.2 0 0 1 7.2,3.6 L 7.2,5"));
        }
    }

    /// <summary>The colour the Device panel draws with: the machine's identity colour, nudged until it has 3:1 against the panel (knobs, bars, segments and switches alike).</summary>
    internal static class DeviceColour
    {
        public static NeoColor Of(IMachine machine) => Of(ReBuzz.Studio.Views.MachineIdentity.CategoryOf(machine) ?? MachineCategory.Synth);

        public static NeoColor Of(MachineCategory category)
        {
            var colour = Tokens.Identity(category);
            var panel = Tokens.SurfacePanel;
            bool darken = Contrast.Luminance(panel) > 0.5;
            for (int i = 0; i < 24 && Contrast.Ratio(colour, panel) < 3.0; i++)
                colour = darken
                    ? NeoColor.FromRgb((byte)(colour.R * 0.9), (byte)(colour.G * 0.9), (byte)(colour.B * 0.9))
                    : colour.Tint(0.08);
            return colour;
        }
    }
}
