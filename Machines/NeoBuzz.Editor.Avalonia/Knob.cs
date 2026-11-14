// Fork (docs/specs/studio-mvp.md S1): the rotary knob on Avalonia, ported from the WPF kit's
// Knob.cs: drag up/down (Shift for fine), mouse wheel, double-click for the default; its label,
// and its value while hovered or dragged. State, input logic and drawing are toolkit-free
// (Core/KnobModel.cs). A drag, a reset or a run of wheel turns is one undo step (EditGesture).

using System;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using Avalonia.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace NeoBuzz.Editor
{
    internal class Knob : NeoElement, IModulationDropSurface
    {
        readonly KnobModel model;
        readonly AvaloniaCanvas canvas;
        readonly EditGesture gesture;

        public Knob(NeoColor accent, double diameter = 46)
        {
            Accent = accent;
            model = new KnobModel(diameter);
            canvas = new AvaloniaCanvas(this);
            gesture = new EditGesture(this);
            Width = model.Width;
            Height = model.Height;
            Cursor = Cursors.SizeNS;
            Focusable = false;
        }

        public NeoColor Accent { get; }
        public double Diameter => model.Diameter;

        /// <summary>Its label; the knob grows to its width (KnobModel.Width).</summary>
        public string Label
        {
            get => model.Label;
            set
            {
                model.Label = value;
                AutomationProperties.SetName(this, value);
                Fit();
            }
        }

        /// <summary>The widest its label goes on a line before it wraps onto a second (KnobModel.MaxLabelWidth).</summary>
        public double MaxLabelWidth
        {
            get => model.MaxLabelWidth;
            set
            {
                model.MaxLabelWidth = value;
                Fit();
            }
        }

        /// <summary>The label's width as drawn (its widest line).</summary>
        public double LabelWidth => model.LabelWidth;

        /// <summary>The label's lines as drawn.</summary>
        public string[] LabelLines => model.LabelLines;

        // ---- drag-to-modulate (ux-overhaul.md P5b): the knob takes a modulator dropped on it and shows where a modulation reaches.
        IMachine modMachine;
        IParameter modParameter;
        int modTrack;
        Func<int, double> modNormalize;
        bool modHooked;

        /// <summary>The parameter this knob is: a modulator dropped on it modulates that, and a ring shows where the modulations on it reach.</summary>
        public void BindModulation(IMachine machine, IParameter parameter, int track, Func<int, double> normalize)
        {
            modMachine = machine;
            modParameter = parameter;
            modTrack = track;
            modNormalize = normalize;
            if (IsAttachedToVisualTree()) HookModulation();
            RefreshModulation();
        }

        /// <summary>Whether a ring is drawn (tests).</summary>
        public bool ShowsModulationRing => model.Modulated != null;

        /// <summary>Whether it lights up as a drop target (tests).</summary>
        public bool IsDropReady => model.DropReady;

        bool IsAttachedToVisualTree() => Avalonia.Controls.TopLevel.GetTopLevel(this) != null;

        void HookModulation()
        {
            if (modHooked || modMachine == null) return;
            modHooked = true;
            ModulationBoard.Changed += OnModulationChanged;
            ModulationBoard.DragChanged += OnModulationChanged;
        }

        void UnhookModulation()
        {
            if (!modHooked) return;
            modHooked = false;
            ModulationBoard.Changed -= OnModulationChanged;
            ModulationBoard.DragChanged -= OnModulationChanged;
        }

        void OnModulationChanged()
        {
            if (Dispatcher.UIThread.CheckAccess()) RefreshModulation();
            else Dispatcher.UIThread.Post(RefreshModulation);
        }

        /// <summary>Reads the modulations on this parameter and the drag in progress.</summary>
        public void RefreshModulation()
        {
            if (modMachine == null || modParameter == null) return;
            bool track = modParameter.Group?.Type == ParameterGroupType.Track;
            var found = ModulationBoard.Incoming(modMachine).FirstOrDefault(i => i.Param == modParameter.Name && i.IsTrack == track && (!track || i.Track == modTrack));
            (double, double, double)? ring = found == null ? null : (modNormalize(found.Low), modNormalize(found.High), modNormalize(found.Base));
            bool ready = ModulationBoard.Dragging != null;
            if (ring.Equals(model.Modulated) && ready == model.DropReady) return;
            model.Modulated = ring;
            model.DropReady = ready;
            InvalidateVisual();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            HookModulation();
            RefreshModulation();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            UnhookModulation();
            base.OnDetachedFromVisualTree(e);
        }

        bool IModulationDropSurface.ModulationTargetAt(double x, double y, out IMachine machine, out IParameter parameter, out int track)
        {
            machine = modMachine;
            parameter = modParameter;
            track = modTrack;
            return modMachine != null && modParameter != null && modParameter.Type != ParameterType.Note;
        }

        void Fit()
        {
            model.FitLabel(canvas);
            Width = model.Width;
            Height = model.Height;
            InvalidateVisual();
        }

        /// <summary>Text of the current value (set by the owner).</summary>
        public string ValueText { get => model.ValueText; set => model.ValueText = value; }
        /// <summary>Where the value arc starts, a position 0 … 1 (the value 0 for −/+ parameters, else the minimum).</summary>
        public double Origin { get => model.Origin; set => model.Origin = value; }
        public double Default { get => model.Default; set => model.Default = value; }
        /// <summary>Steps for wheel and arrow keys: the parameter's range.</summary>
        public int Steps { get => model.Steps; set => model.Steps = value; }
        /// <summary>The safe range marked on the knob, positions 0 … 1 (U4, machine-safety.md §3.4); null: none.</summary>
        public (double From, double To)? Safe
        {
            get => model.Safe;
            set
            {
                model.Safe = value;
                InvalidateVisual();
            }
        }

        /// <summary>Raised on a right click (the manifest editor takes a typed value then).</summary>
        public event Action RightClicked;

        /// <summary>0 … 1.</summary>
        public double Value
        {
            get => model.Value;
            set
            {
                if (model.Set(value)) InvalidateVisual();
            }
        }

        /// <summary>Raised when the pointer enters or leaves the knob, a gesture starts (a press, the wheel, a double click) or ends (Explain, neo-learn.md §5.3).</summary>
        public event Action<KnobPhase> Phase;

        /// <summary>Raised when the user moves the knob (not when Value is set in code).</summary>
        public event Action<double> UserChanged;

        void UserMoved(bool changed)
        {
            if (!changed) return;
            UserChanged?.Invoke(model.Value);
            InvalidateVisual();
        }

        public void Refresh() => InvalidateVisual();

        static bool Fine(MouseEventArgs e) => (e.KeyModifiers & KeyModifiers.Shift) != 0;

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            Phase?.Invoke(KnobPhase.Enter);
            model.Hover = true;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            Phase?.Invoke(KnobPhase.Leave);
            model.Hover = false;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Phase?.Invoke(KnobPhase.Touch);
            if (e.ClickCount == 2)
            {
                gesture.Begin();
                UserMoved(model.ResetToDefault());
                gesture.End();
                Phase?.Invoke(KnobPhase.Release);
                e.Handled = true;
                return;
            }
            gesture.Begin();
            model.BeginDrag(e.GetPosition(this).Y);
            CaptureMouse();
            e.Handled = true;
            InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!model.Dragging) return;
            UserMoved(model.DragTo(e.GetPosition(this).Y, Fine(e)));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!model.EndDrag()) return;
            Phase?.Invoke(KnobPhase.Release);
            ReleaseMouseCapture();
            gesture.End();
            e.Handled = true;
            InvalidateVisual();
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (!model.EndDrag()) return;   // the drag ended without a release (the window lost the pointer)
            Phase?.Invoke(KnobPhase.Release);
            gesture.End();
            InvalidateVisual();
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            if (RightClicked == null) return;
            RightClicked();
            e.Handled = true;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            Phase?.Invoke(KnobPhase.Touch);
            gesture.Wheel();
            UserMoved(model.Wheel(e.Delta, Fine(e)));
            Phase?.Invoke(KnobPhase.Release);
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc) =>
            model.Draw(canvas.On(dc), Bounds.Width > 0 ? Bounds.Width : Width, Accent);
    }
}
