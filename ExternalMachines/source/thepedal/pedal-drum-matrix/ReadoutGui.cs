using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BuzzGUI.Interfaces;

namespace PedalDrumMatrix
{
    // Embedded read-out panel shown at the top of the parameter window
    // (Core §26 IMachineGUIFactory; FrameworkElement + OnRender per §26.7 so the
    // UserControl template does not paint over the drawing). It mirrors the Buzz
    // 1503 port's ReadoutGui: a live per-slot rack view whose Char/Mode labels
    // come from the machine's own DescribeValue, so they always match the rack.
    [MachineGUIFactoryDecl(PreferWindowedGUI = false, IsGUIResizable = false, UseThemeStyles = false)]
    public class ReadoutGuiFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new ReadoutGui();
    }

    public sealed class ReadoutGui : FrameworkElement, IMachineGUI
    {
        // ReBuzz sizes the parameter window TO the GUI width (it does not clip
        // like Buzz 1503), so we must return a fixed preferred width — echoing the
        // offered width makes the window open as wide as the screen.
        const double FixedW = 560, H = 196;
        double _w = FixedW;

        IMachine _im;
        PedalDrumMatrixMachine _m;
        DispatcherTimer _timer;

        // cached reflection: name → property getter (atomic int reads, §26.4)
        readonly Dictionary<string, Func<int>> _get = new Dictionary<string, Func<int>>();
        // cached ReBuzz IParameter per name, for DescribeValue (built lazily, §2.5)
        readonly Dictionary<string, IParameter> _param = new Dictionary<string, IParameter>();
        bool _paramsResolved;
        string _lastText = "";

        // typefaces / brushes from the system theme so it matches the window
        static readonly Typeface Face = new Typeface(new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        static readonly Typeface FaceB = new Typeface(new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        // Explicit dark brushes — ReBuzz uses a dark theme that does NOT come
        // through SystemColors (those resolve to the OS light theme → pale panel).
        static readonly Brush _bg     = Frozen(0x2B, 0x2B, 0x2E);
        static readonly Brush _fg     = Frozen(0xE6, 0xE6, 0xE6);
        static readonly Brush _dim    = Frozen(0x95, 0x95, 0x98);
        static readonly Brush _accent = Frozen(0x4F, 0xA3, 0xE3);
        static readonly Brush _line   = Frozen(0x50, 0x50, 0x54);
        static Brush Frozen(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        public IMachine Machine
        {
            get => _im;
            set { _im = value; _m = value?.ManagedMachine as PedalDrumMatrixMachine; _paramsResolved = false; }
        }

        public ReadoutGui()
        {
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            Width = FixedW; Height = H;
            Loaded   += (_, __) => { _timer?.Start(); };
            Unloaded += (_, __) => { _timer?.Stop(); };
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };   // 10 Hz
            _timer.Tick += (_, __) => Tick();
        }

        protected override Size MeasureOverride(Size available) => new Size(FixedW, H);
        protected override Size ArrangeOverride(Size finalSize) => new Size(FixedW, H);

        void Tick()
        {
            // cheap change-detect so we only repaint when a label actually moved
            string sig = BuildSignature();
            if (sig != _lastText) { _lastText = sig; InvalidateVisual(); }
        }

        // ── reflection helpers ────────────────────────────────────────────────
        Func<int> Getter(string name)
        {
            if (_get.TryGetValue(name, out var g)) return g;
            g = null;
            try
            {
                var p = typeof(PedalDrumMatrixMachine).GetProperty(name,
                    BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.PropertyType == typeof(int) && _m != null)
                    g = (Func<int>)p.GetGetMethod().CreateDelegate(typeof(Func<int>), _m);
            }
            catch { g = null; }
            _get[name] = g;
            return g;
        }
        int Val(string name) { var g = Getter(name); return g != null ? g() : 0; }

        void ResolveParams()
        {
            if (_paramsResolved) return;
            try
            {
                var groups = _im?.ParameterGroups;
                if (groups == null) return;
                foreach (var grp in groups)
                    foreach (var p in grp.Parameters)
                        if (p != null && !string.IsNullOrEmpty(p.Name)) _param[p.Name] = p;
                if (_param.Count > 0) _paramsResolved = true;   // retry next tick until populated
            }
            catch { }
        }

        string Label(string name, int value)
        {
            // prefer the machine's own DescribeValue (matches the rack exactly)
            try
            {
                ResolveParams();
                if (_m != null && _param.TryGetValue(name, out var p))
                {
                    string s = _m.DescribeValue(p, value);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch { }
            return value.ToString(CultureInfo.InvariantCulture);
        }

        static string TypeName(int v)
        {
            if (v <= 0) return "—";
            try { return ((FxType)v).ToString(); } catch { return v.ToString(CultureInfo.InvariantCulture); }
        }

        string BuildSignature()
        {
            if (_m == null) return "";
            var sb = new System.Text.StringBuilder(96);
            for (int s = 1; s <= 6; s++)
                sb.Append(Val($"Slot{s}Type")).Append(',')
                  .Append(Val($"Slot{s}Amount")).Append(',')
                  .Append(Val($"Slot{s}Char")).Append(',')
                  .Append(Val($"Slot{s}Mode")).Append(';');
            sb.Append(Val("Feedback")).Append(',').Append(Val("Morph")).Append(',')
              .Append(Val("Limiter")).Append(',').Append(Val("AutoGainOn"));
            return sb.ToString();
        }

        // ── drawing ───────────────────────────────────────────────────────────
        FormattedText FT(string s, Typeface tf, double size, Brush b)
        {
            double dpi = 1.0;
            try { dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }
            return new FormattedText(s ?? "", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tf, size, b, dpi)
            { TextAlignment = TextAlignment.Left };
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(_bg, null, new Rect(0, 0, _w, H));   // FrameworkElement has no Background

            double pad = 10;
            // column x positions, clamped into the available width
            double cSlot = pad, cType = pad + 34, cAmt = pad + 150, cChar = pad + 232, cMode = pad + 372;
            double maxX = _w - pad;

            // title
            dc.DrawText(FT("PEDAL DRUM MATRIX", FaceB, 12, _fg), new Point(pad, 6));
            string ver = _m != null ? "v" + PedalDrumMatrixMachine.Version : "";
            var vt = FT(ver, Face, 11, _dim);
            dc.DrawText(vt, new Point(Math.Max(cType, _w - pad - vt.Width), 7));
            dc.DrawLine(new Pen(_line, 1), new Point(pad, 24.5), new Point(maxX, 24.5));

            // header
            double y = 28;
            dc.DrawText(FT("#",      Face, 10, _dim), new Point(cSlot, y));
            dc.DrawText(FT("EFFECT", Face, 10, _dim), new Point(cType, y));
            dc.DrawText(FT("AMOUNT", Face, 10, _dim), new Point(cAmt,  y));
            dc.DrawText(FT("CHAR",   Face, 10, _dim), new Point(cChar, y));
            dc.DrawText(FT("MODE",   Face, 10, _dim), new Point(cMode, y));
            y += 16;

            if (_m == null) { dc.DrawText(FT("(no machine)", Face, 11, _dim), new Point(pad, y)); return; }

            for (int s = 1; s <= 6; s++)
            {
                int tv = Val($"Slot{s}Type");
                bool active = tv > 0;
                Brush fg = active ? _fg : _dim;
                dc.DrawText(FT(s.ToString(CultureInfo.InvariantCulture), Face, 11, _dim), new Point(cSlot, y));
                dc.DrawText(FT(TypeName(tv), FaceB, 11, fg), new Point(cType, y));
                if (active)
                {
                    dc.DrawText(FT(Label($"Slot{s}Amount", Val($"Slot{s}Amount")), Face, 11, fg), new Point(cAmt,  y));
                    dc.DrawText(FT(Label($"Slot{s}Char",   Val($"Slot{s}Char")),   Face, 11, fg), new Point(cChar, y));
                    dc.DrawText(FT(Label($"Slot{s}Mode",   Val($"Slot{s}Mode")),   Face, 11, fg), new Point(cMode, y));
                }
                y += 17;
            }

            // footer — key globals
            y += 2;
            dc.DrawLine(new Pen(_line, 1), new Point(pad, y - 1.5), new Point(maxX, y - 1.5));
            y += 4;
            string fb   = "Feedback " + Label("Feedback", Val("Feedback"));
            string mph  = "Morph " + Label("Morph", Val("Morph"));
            string lim  = "Limiter " + (Val("Limiter") != 0 ? "On" : "Off");
            string agc  = "AutoGain " + (Val("AutoGainOn") != 0 ? "On" : "Off");
            dc.DrawText(FT(fb,  Face, 11, _accent), new Point(cSlot, y));
            dc.DrawText(FT(mph, Face, 11, _accent), new Point(cAmt,  y));
            dc.DrawText(FT(lim, Face, 11, _dim),    new Point(cChar, y));
            dc.DrawText(FT(agc, Face, 11, _dim),    new Point(cMode, y));
        }
    }
}
