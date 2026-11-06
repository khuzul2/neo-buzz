// Fork (docs/specs/studio-mvp.md S3): the studio's machine view, after BuzzGUI.MachineView: a top
// bar with the zoom slider, the code-drawn canvas (MachineCanvas) with its scroll bars, and the
// amp control above it. The WPF view's sidebar (machine list, parameters, templates) isn't here:
// machines come from the canvas's context menu.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Views.Machines
{
    using Control = Avalonia.Controls.Control;

    /// <summary>The "Machines" tab.</summary>
    internal sealed class MachinesTab : IStudioView
    {
        public string Title => "Machines";

        public Control Create(StudioHost host) => new MachineView(host.Core);
    }

    internal sealed class MachineView : DockPanel, IStudioEditTarget
    {
        readonly ScrollBar horizontal = new ScrollBar { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, AllowAutoHide = true };
        readonly ScrollBar vertical = new ScrollBar { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right, AllowAutoHide = true };
        readonly Slider zoom = new Slider { Minimum = -MachineCanvas.ZoomLevels, Maximum = MachineCanvas.ZoomLevels, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 100, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        bool syncing;

        public MachineView(ReBuzzCore core)
        {
            Core = core;
            Canvas = new MachineCanvas(core);
            Menus = new MachineMenus(core, Canvas);
            Amp = new AmpControl(core) { InsertMenu = Menus.ForInsert };
            Canvas.Menus = Menus;
            Canvas.Amp = Amp;

            // The top bar (TopBorderStyle; 32 px, for Fluent's slider) with the zoom slider on the right.
            var bar = new Border
            {
                Height = 32,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)),
                Child = new DockPanel { LastChildFill = false, Children = { Right(zoom) } },
            };
            SetDock(bar, Dock.Top);
            Children.Add(bar);

            // The canvas fills the view; the amp control's layer is above it (WPF's ampCanvas).
            var layer = new Avalonia.Controls.Canvas { Children = { Amp } };
            Children.Add(new Panel { Children = { Canvas, horizontal, vertical, layer } });

            AddKeys();
            Canvas.ViewChanged += Sync;
            horizontal.ValueChanged += (s, e) => { if (!syncing) Canvas.ScrollTo(new Vector(e.NewValue, Canvas.Offset.Y)); };
            vertical.ValueChanged += (s, e) => { if (!syncing) Canvas.ScrollTo(new Vector(Canvas.Offset.X, e.NewValue)); };
            zoom.ValueChanged += (s, e) => { if (!syncing) Canvas.SetZoom((int)Math.Round(e.NewValue)); };
        }

        /// <summary>
        /// The view's shortcuts as its own key bindings (MachineView.Commands' input bindings), so they
        /// work wherever the focus is inside it. Ctrl is Command on a Mac.
        /// </summary>
        void AddKeys()
        {
            var cmd = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            void Bind(Key key, KeyModifiers mods, Action run) => KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key, mods), Command = new Command(run) });
            // Fork (ux-overhaul.md §4): one keymap: ⌫ and Delete delete, ⌘D duplicates (it deselected), ⇧⌘A and Esc deselect.
            Bind(Key.Delete, KeyModifiers.None, Canvas.DeleteSelection);
            Bind(Key.Back, KeyModifiers.None, Canvas.DeleteSelection);
            Bind(Key.A, cmd, Canvas.SelectAll);
            Bind(Key.D, cmd, () => Canvas.DuplicateSelection());
            Bind(Key.A, cmd | KeyModifiers.Shift, Canvas.ClearSelection);
            Bind(Key.Escape, KeyModifiers.None, Canvas.ClearSelection);
            Bind(Key.R, cmd, () => Canvas.RenameSelection());
            Bind(Key.U, cmd, Canvas.UnmuteAll);
            // Mute: M on the canvas in both apps (on a Mac ⌘M minimizes the window), and Ctrl+M as ReBuzz.exe has it off a
            // Mac; S solos, B bypasses.
            Canvas.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.M), Command = new Command(Canvas.ToggleMuteSelection) });
            Canvas.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S), Command = new Command(Canvas.ToggleSoloSelection) });
            Canvas.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.B), Command = new Command(Canvas.ToggleBypassSelection) });
            if (!OperatingSystem.IsMacOS())
                Bind(Key.M, cmd, Canvas.ToggleMuteSelection);
        }

        // IStudioEditTarget (ux-overhaul.md §4): the Edit menu's commands on the selected machines. Machine edits are on the
        // song's history, so Undo and Redo fall through to it.
        public bool Undo() => false;
        public bool Redo() => false;
        public bool Duplicate() => Canvas.DuplicateSelection();
        public bool Rename() => Canvas.RenameSelection();

        public bool Delete()
        {
            if (Canvas.Selection.Count == 0) return false;
            Canvas.DeleteSelection();
            return true;
        }

        public bool SelectAll()
        {
            Canvas.SelectAll();
            return true;
        }

        public bool DeselectAll()
        {
            if (Canvas.Selection.Count == 0) return false;
            Canvas.ClearSelection();
            return true;
        }

        static Control Right(Control c)
        {
            DockPanel.SetDock(c, Dock.Right);
            return c;
        }

        public ReBuzzCore Core { get; }
        public MachineCanvas Canvas { get; }
        public MachineMenus Menus { get; }
        public AmpControl Amp { get; }
        public Slider ZoomSlider => zoom;

        /// <summary>The scroll bars and the slider follow the canvas.</summary>
        void Sync()
        {
            syncing = true;
            try
            {
                var extent = Canvas.Extent;
                var view = Canvas.Bounds.Size;
                horizontal.Maximum = Math.Max(0, extent.Width - view.Width);
                horizontal.ViewportSize = view.Width;
                horizontal.Value = Canvas.Offset.X;
                vertical.Maximum = Math.Max(0, extent.Height - view.Height);
                vertical.ViewportSize = view.Height;
                vertical.Value = Canvas.Offset.Y;
                zoom.Value = Canvas.ZoomLevel;
            }
            finally
            {
                syncing = false;
            }
        }

        /// <summary>A machine's centre in another visual's pixels (tests point the mouse with it).</summary>
        public Point PointOf(IMachine machine, Visual relativeTo) => FromCanvas(Canvas.BoxOf(machine).Center, relativeTo);

        /// <summary>A song position in another visual's pixels.</summary>
        public Point PointOf(double x, double y, Visual relativeTo) => FromCanvas(Canvas.Map.PointAt(x, y), relativeTo);

        /// <summary>A wire's arrow in another visual's pixels.</summary>
        public Point ArrowOf(IMachineConnection wire, Visual relativeTo) => FromCanvas(Canvas.WireOf(wire).Mid, relativeTo);

        public Point FromCanvas(Point logical, Visual relativeTo) => Canvas.TranslatePoint(Canvas.ToView(logical), relativeTo) ?? default;
    }
}
