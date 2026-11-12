// Fork (docs/specs/ux-overhaul.md P3a, decisions 31-34): the detail panel under the main view. Its one page today is the Tracker
// (the pattern editor); the piano roll, Device and Mixer come in later steps. A 28 pt bar when closed and a panel of a height
// the user drags when open (both remembered in the settings); the Tracker button maximises it over the whole window.

using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Detail
{
    internal sealed class DetailPanel : Border
    {
        public const string TrackerTab = "Tracker";
        public const string RollTab = "Piano roll";

        internal const string RegistrySection = "Studio";
        internal const string OpenKey = "DetailOpen";
        internal const string HeightKey = "DetailHeight";
        internal const string RollKey = "DetailPianoRoll";       // the Piano roll page is the one shown (P3b)

        public const double DefaultHeight = 340;
        public const double MinPanelHeight = 120;
        public const double MaxPanelHeight = 1200;
        public const double BarHeight = 28;

        readonly IRegistryEx registry;
        readonly Avalonia.Controls.Control page;
        readonly Border grip;
        readonly Button trackerButton, rollButton, maximiseButton, collapseButton;
        readonly Panel content;
        bool open, maximised, rollPage;
        double height;

        public DetailPanel(IRegistryEx registry, Avalonia.Controls.Control tracker)
        {
            this.registry = registry;
            page = tracker;
            open = Read(OpenKey, false);
            height = Math.Clamp(Read(HeightKey, DefaultHeight), MinPanelHeight, MaxPanelHeight);
            rollPage = Read(RollKey, false);

            Background = StudioTokens.SurfacePanelBrush;
            BorderBrush = StudioTokens.SurfaceBorderBrush;
            BorderThickness = new Thickness(0, 1, 0, 0);
            ClipToBounds = true;
            AutomationProperties.SetName(this, "Detail");

            trackerButton = new Button { Content = TrackerTab, FontSize = 12, Padding = new Thickness(10, 3), MinHeight = 22, Focusable = false, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
            AutomationProperties.SetName(trackerButton, TrackerTab);
            ToolTip.SetTip(trackerButton, Shell.Commands.StudioKeymap.Tip("view.pattern"));
            trackerButton.Click += (s, e) => Choose(false);

            // Fork (ux-overhaul.md P3b): Tracker | Piano roll, two views of the one pattern the page's choosers name.
            rollButton = new Button { Content = RollTab, FontSize = 12, Padding = new Thickness(10, 3), MinHeight = 22, Focusable = false, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
            AutomationProperties.SetName(rollButton, RollTab);
            ToolTip.SetTip(rollButton, Shell.Commands.StudioKeymap.Tip("view.pianoroll"));
            rollButton.Click += (s, e) => Choose(true);

            maximiseButton = Glyph("⤢", Shell.Commands.StudioKeymap.Tip("view.maximise"), "Maximise Tracker", ToggleMaximised);
            collapseButton = Glyph("⌄", "Hide the detail (" + Shell.Commands.StudioKeymap.Get("view.detail").KeyLabel() + ")", "Hide detail", Close);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { maximiseButton, collapseButton } };
            var bar = new DockPanel { Height = BarHeight, Margin = new Thickness(8, 0, 6, 0), LastChildFill = true };
            DockPanel.SetDock(buttons, Dock.Right);
            bar.Children.Add(buttons);
            bar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { trackerButton, rollButton } });
            DockPanel.SetDock(bar, Dock.Top);

            content = new Panel { Children = { page } };
            var frame = new DockPanel { LastChildFill = true };
            frame.Children.Add(bar);
            frame.Children.Add(content);

            grip = new Border { Height = 5, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeNorthSouth), VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetName(grip, "Resize detail");
            grip.PointerPressed += GripPressed;
            grip.PointerMoved += GripMoved;
            grip.PointerReleased += GripReleased;
            Child = new Grid { Children = { frame, grip } };
            if (page is PatternPage pp) pp.SetRollMode(rollPage);
            Apply();
        }

        /// <summary>A page's button: shows that page; clicking the page that is shown hides the panel.</summary>
        void Choose(bool roll)
        {
            if (open && rollPage == roll) Close();
            else ShowPage(roll, maximised);
        }

        /// <summary>Opens the panel on the Tracker (false) or the piano roll (true), docked or maximised.</summary>
        public void ShowPage(bool roll, bool maximise)
        {
            SetPage(roll);
            Set(true, maximise);
        }

        /// <summary>Which page is shown: the piano roll (true) or the Tracker. Remembered.</summary>
        public void SetPage(bool roll)
        {
            if (rollPage == roll) return;
            rollPage = roll;
            Write(RollKey, roll);
            if (page is PatternPage pp) pp.SetRollMode(roll);
            Apply();
        }

        public bool IsPianoRoll => rollPage;

        /// <summary>The layout around it changed (shown, hidden, maximised, sized): the window rearranges its rows.</summary>
        public event Action Changed;

        public bool IsOpen => open;

        public bool IsMaximised => open && maximised;

        public Avalonia.Controls.Control Page => page;

        /// <summary>The height when open and docked; remembered.</summary>
        public double PanelHeight
        {
            get => height;
            set
            {
                value = Math.Clamp(value, MinPanelHeight, MaxPanelHeight);
                if (value == height) return;
                height = value;
                Write(HeightKey, height);
                Apply();
            }
        }

        internal Button TrackerButton => trackerButton;
        internal Button RollButton => rollButton;
        internal Button MaximiseButton => maximiseButton;
        internal Button CollapseButton => collapseButton;
        internal Avalonia.Controls.Control Grip => grip;

        public void Open() => Set(true, maximised);

        public void Close() => Set(false, false);

        public void Toggle() { if (open) Close(); else Open(); }

        /// <summary>Open, over the whole window or docked.</summary>
        public void Show(bool maximise) => Set(true, maximise);

        /// <summary>Maximised, or back to what it was before: docked if it was open, a bar if it was closed.</summary>
        public void ToggleMaximised()
        {
            if (open && maximised) Restore();
            else Show(true);
        }

        /// <summary>Out of the maximised state, back to the bar or the panel it came from; nothing if it isn't maximised.</summary>
        public void Restore()
        {
            if (open && maximised) Set(openBeforeMaximise, false);
        }

        bool openBeforeMaximise;

        void Set(bool newOpen, bool newMaximised)
        {
            if (open == newOpen && maximised == newMaximised) return;
            if (newOpen && newMaximised && !maximised) openBeforeMaximise = open;
            bool openChanged = open != newOpen;
            open = newOpen;
            maximised = newOpen && newMaximised;
            if (openChanged) Write(OpenKey, open);
            Apply();
        }

        void Apply()
        {
            content.IsVisible = open;
            grip.IsVisible = open && !maximised;
            Height = !open ? BarHeight : maximised ? double.NaN : height;
            maximiseButton.Content = maximised ? "⤡" : "⤢";
            ToolTip.SetTip(maximiseButton, maximised ? "Back to the panel (" + Shell.Commands.StudioKeymap.Get("view.maximise").KeyLabel() + ")" : Shell.Commands.StudioKeymap.Tip("view.maximise"));
            string what = rollPage ? "Piano roll" : "Tracker";
            AutomationProperties.SetName(maximiseButton, maximised ? "Restore " + what : "Maximise " + what);
            collapseButton.Content = "⌄";
            Mark(trackerButton, open && !rollPage);
            Mark(rollButton, open && rollPage);
            Changed?.Invoke();
        }

        T Read<T>(string key, T def)
        {
            try { return registry == null ? def : registry.Read(key, def, RegistrySection); }
            catch { return def; }
        }

        void Write<T>(string key, T value)
        {
            try { registry?.Write(key, value, RegistrySection); }
            catch { /* a setting that can't be saved is not worth a failed click */ }
        }

        // ------------------------------------------------------------------ resizing

        double gripStartHeight, gripStartY;

        void GripPressed(object sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Parent is not Visual parent) return;
            gripStartHeight = height;
            gripStartY = e.GetPosition(parent).Y;
            e.Pointer.Capture(grip);
            e.Handled = true;
        }

        void GripMoved(object sender, PointerEventArgs e)
        {
            if (!ReferenceEquals(e.Pointer.Captured, grip) || Parent is not Visual parent) return;
            PanelHeight = gripStartHeight + (gripStartY - e.GetPosition(parent).Y);
        }

        void GripReleased(object sender, PointerReleasedEventArgs e)
        {
            if (!ReferenceEquals(e.Pointer.Captured, grip)) return;
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        static void Mark(Button b, bool on)
        {
            b.Background = on ? StudioTokens.SelectionFillBrush : Brushes.Transparent;
            b.BorderBrush = on ? StudioTokens.AccentBrush : Brushes.Transparent;
            b.Foreground = on ? StudioTokens.TextPrimaryBrush : StudioTokens.TextSecondaryBrush;
        }

        static Button Glyph(string glyph, string tip, string name, Action run)
        {
            var b = new Button { Content = glyph, FontSize = 15, Padding = new Thickness(6, 0, 6, 2), MinWidth = 24, MinHeight = 24, Focusable = false, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(b, tip);
            AutomationProperties.SetName(b, name);
            b.Click += (s, e) => run();
            return b;
        }
    }
}
