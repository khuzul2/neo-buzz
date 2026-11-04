#nullable disable
// Neo Sampler's editor pictures (docs/specs/machines.md §5), toolkit-free on the kit's canvas
// (INeoCanvas) with their pointer input (IGraphInput), so WPF and the studio's Avalonia draw and
// handle them the same: the zone map (the keyboard below, each zone a box over its keys and
// velocities, or in Kit mode its key; click a zone to select it, drag a selected zone's edge to
// move its key or velocity range) and the wave (the selected zone's wave with its start, end and
// loop markers to drag, the crossfade shaded, and where its voices are).

using System;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Sampler
{
    /// <summary>What the pictures read and write: the zones' parameters, and the playing voices.</summary>
    internal interface ISamplerView
    {
        ZoneSettings Zone(int z);
        bool Kit { get; }
        int KitBase { get; }
        int Raw(int index);
        void Set(int index, int raw);
        /// <summary>The layer a zone shows (its slot's first, or the one nearest C4), or null.</summary>
        SampleLayer Layer(int z);
        /// <summary>Voice i's zone (−1 none), note and position in its wave (0 … 1).</summary>
        bool Voice(int i, out int zone, out int note, out float position);
    }

    internal static class ZoneColors
    {
        static readonly NeoColor[] colors =
        {
            NeoColor.FromRgb(0xFF, 0x6B, 0x6B), NeoColor.FromRgb(0xFF, 0xA9, 0x4D), NeoColor.FromRgb(0xF5, 0xD5, 0x47), NeoColor.FromRgb(0x8F, 0xD1, 0x4F),
            NeoColor.FromRgb(0x3F, 0xD1, 0xB0), NeoColor.FromRgb(0x4F, 0xB3, 0xFF), NeoColor.FromRgb(0x9B, 0x8C, 0xFF), NeoColor.FromRgb(0xE0, 0x7A, 0xE6),
        };

        public static NeoColor Of(int z) => z < 8 ? colors[z] : colors[z - 8].Tint(-0.25);
    }

    /// <summary>The zone map: keys across (MIDI 0 … 127), velocity up.</summary>
    internal sealed class ZoneMap : IGraphInput
    {
        const double KeyboardHeight = 34, Edge = 5;
        readonly ISamplerView view;
        NeoRect bounds = new NeoRect(0, 0, 600, 200);
        enum Drag { None, KeyLo, KeyHi, VelLo, VelHi }
        Drag drag;
        int hover = -1;

        public ZoneMap(ISamplerView view)
        {
            this.view = view;
        }

        public int Selected { get; private set; }
        public event Action<int> SelectedChanged;
        public bool Capturing => drag != Drag.None;

        public void Select(int z)
        {
            z = Math.Clamp(z, 0, SamplerEngine.ZoneCount - 1);
            if (z == Selected) return;
            Selected = z;
            SelectedChanged?.Invoke(z);
        }

        NeoRect Area => new NeoRect(bounds.X, bounds.Y + 16, bounds.Width, bounds.Height - 16 - KeyboardHeight);
        double KeyX(double key) => bounds.X + key / 128.0 * bounds.Width;
        double VelY(double vel) => Area.Y + (127.5 - vel) / 127.0 * Area.Height;
        int KeyAt(double x) => Math.Clamp((int)Math.Floor((x - bounds.X) / bounds.Width * 128), 0, 127);
        int VelAt(double y) => Math.Clamp((int)Math.Round(127.5 - (y - Area.Y) / Area.Height * 127), 1, 127);

        /// <summary>A zone's box (empty when it is off).</summary>
        public NeoRect Box(int z)
        {
            var zs = view.Zone(z);
            if (zs.Wave == 0) return new NeoRect(0, 0, 0, 0);
            if (view.Kit)
            {
                int key = view.KitBase + z;
                return new NeoRect(KeyX(key), Area.Y, KeyX(key + 1) - KeyX(key), Area.Height);
            }
            int lo = Math.Min(zs.KeyLo, zs.KeyHi), hi = Math.Max(zs.KeyLo, zs.KeyHi);
            int vlo = Math.Min(zs.VelLo, zs.VelHi), vhi = Math.Max(zs.VelLo, zs.VelHi);
            double top = VelY(vhi + 0.5), bottom = VelY(vlo - 0.5);
            return new NeoRect(KeyX(lo), top, KeyX(hi + 1) - KeyX(lo), bottom - top);
        }

        /// <summary>The zone under a point: the smallest box (so a small zone inside a large one stays reachable).</summary>
        public int ZoneAt(double x, double y)
        {
            int best = -1;
            double area = double.MaxValue;
            for (int z = 0; z < SamplerEngine.ZoneCount; z++)
            {
                var b = Box(z);
                if (b.Width <= 0 || !b.Contains(x, y)) continue;
                double a = b.Width * b.Height;
                if (a < area) { area = a; best = z; }
            }
            return best;
        }

        public void Draw(INeoCanvas g, NeoRect r, NeoColor accent)
        {
            bounds = r;
            var area = Area;
            var grid = new NeoStroke(Palette.Grid, 1);
            for (int c = 0; c <= 10; c++)
            {
                double x = KeyX(c * 12);
                g.Line(x, area.Top, x, r.Bottom, grid);
                g.Text("C" + (c - 1), x + 2, r.Y, 9, Palette.DimText);
            }
            g.Line(r.Left, VelY(64.5), r.Right, VelY(64.5), new NeoStroke(Palette.Grid, 1, true));
            // Zones, the selected one last (on top).
            for (int pass = 0; pass < 2; pass++)
                for (int z = 0; z < SamplerEngine.ZoneCount; z++)
                {
                    if ((z == Selected) != (pass == 1)) continue;
                    var b = Box(z);
                    if (b.Width <= 0) continue;
                    var color = ZoneColors.Of(z);
                    bool sel = z == Selected;
                    g.Rect(b, color.WithAlpha(sel ? (byte)0x60 : z == hover ? (byte)0x48 : (byte)0x30), new NeoStroke(sel ? Palette.Text : color, sel ? 1.6 : 1), 2);
                    g.Text((z + 1).ToString(), b.X + Math.Max(2, b.Width / 2 - 4), b.Y + 2, 10, sel ? Palette.Text : color, sel, maxWidth: Math.Max(8, b.Width));
                }
            // The keyboard, sounding keys lit.
            var kb = new NeoRect(r.X, r.Bottom - KeyboardHeight, r.Width, KeyboardHeight);
            var lit = new bool[128];
            for (int i = 0; i < SamplerEngine.VoiceCount; i++)
                if (view.Voice(i, out _, out int note, out _) && note >= 0 && note < 128) lit[note] = true;
            g.Rect(kb, Palette.Text.WithAlpha(0xD0), NeoStroke.None);
            for (int k = 0; k < 128; k++)
            {
                if (IsBlack(k)) continue;
                if (lit[k]) g.Rect(new NeoRect(KeyX(k), kb.Y, KeyX(k + 1) - KeyX(k), kb.Height), accent, NeoStroke.None);
                g.Line(KeyX(k), kb.Y, KeyX(k), kb.Bottom, new NeoStroke(Palette.Track, 0.6));
            }
            for (int k = 0; k < 128; k++)
                if (IsBlack(k))
                    g.Rect(new NeoRect(KeyX(k) - 0.6, kb.Y, KeyX(k + 1) - KeyX(k) + 1.2, kb.Height * 0.6), lit[k] ? accent : Palette.Background, NeoStroke.None);
            if (view.Kit)
            {
                double x0 = KeyX(view.KitBase), x1 = KeyX(view.KitBase + SamplerEngine.ZoneCount);
                g.Rect(new NeoRect(x0, kb.Y - 3, x1 - x0, 3), accent, NeoStroke.None);
            }
        }

        static bool IsBlack(int k)
        {
            int s = k % 12;
            return s == 1 || s == 3 || s == 6 || s == 8 || s == 10;
        }

        Drag EdgeAt(double x, double y)
        {
            if (view.Kit) return Drag.None;
            var b = Box(Selected);
            if (b.Width <= 0 || x < b.Left - Edge || x > b.Right + Edge || y < b.Top - Edge || y > b.Bottom + Edge) return Drag.None;
            if (Math.Abs(x - b.Left) <= Edge) return Drag.KeyLo;
            if (Math.Abs(x - b.Right) <= Edge) return Drag.KeyHi;
            if (Math.Abs(y - b.Top) <= Edge) return Drag.VelHi;
            if (Math.Abs(y - b.Bottom) <= Edge) return Drag.VelLo;
            return Drag.None;
        }

        public bool Down(double x, double y, int clicks, bool fine)
        {
            drag = EdgeAt(x, y);
            if (drag != Drag.None) return true;
            int z = ZoneAt(x, y);
            if (z >= 0) Select(z);
            return z >= 0;
        }

        public bool Move(double x, double y, bool fine)
        {
            if (drag == Drag.None)
            {
                int h = ZoneAt(x, y);
                if (h == hover) return false;
                hover = h;
                return true;
            }
            var zs = view.Zone(Selected);
            int b = SamplerEngine.ZoneParam(Selected, 0);
            switch (drag)
            {
                case Drag.KeyLo: view.Set(b + SamplerEngine.FKeyLo, Math.Min(KeyAt(x + 2), Math.Max(zs.KeyLo, zs.KeyHi))); break;
                case Drag.KeyHi: view.Set(b + SamplerEngine.FKeyHi, Math.Max(KeyAt(x - 2), Math.Min(zs.KeyLo, zs.KeyHi))); break;
                case Drag.VelHi: view.Set(b + SamplerEngine.FVelHi, Math.Max(VelAt(y), Math.Min(zs.VelLo, zs.VelHi))); break;
                case Drag.VelLo: view.Set(b + SamplerEngine.FVelLo, Math.Min(VelAt(y), Math.Max(zs.VelLo, zs.VelHi))); break;
            }
            return true;
        }

        public bool Up()
        {
            bool was = drag != Drag.None;
            drag = Drag.None;
            return was;
        }

        public bool Wheel(double x, double y, int delta, bool fine) => false;

        public bool Leave()
        {
            if (hover < 0) return false;
            hover = -1;
            return true;
        }
    }

    /// <summary>The selected zone's wave: start, end and loop markers to drag; the crossfade; the voices.</summary>
    internal sealed class WaveView : IGraphInput
    {
        readonly ISamplerView view;
        readonly Func<int> zone;
        NeoRect bounds = new NeoRect(0, 0, 400, 200);
        int dragging = -1;
        int hover = -1;

        public WaveView(ISamplerView view, Func<int> zone)
        {
            this.view = view;
            this.zone = zone;
        }

        public bool Capturing => dragging >= 0;

        static readonly int[] Fields = { SamplerEngine.FStart, SamplerEngine.FEnd, SamplerEngine.FLoopS, SamplerEngine.FLoopE };
        static readonly string[] Labels = { "S", "E", "L", "L" };

        double X(double fraction) => bounds.X + Math.Clamp(fraction, 0, 1) * bounds.Width;
        double Fraction(int field) => Params.Table[SamplerEngine.ZoneParam(zone(), field)].Value(view.Raw(SamplerEngine.ZoneParam(zone(), field))) / 100.0;
        bool ZoneLoops(in ZoneSettings zs) => !view.Kit && !zs.OneShot && (zs.Loop == ZoneLoop.Forward || zs.Loop == ZoneLoop.PingPong);

        public void Draw(INeoCanvas g, NeoRect r, NeoColor accent)
        {
            bounds = new NeoRect(r.X, r.Y + 16, r.Width, r.Height - 30);
            int z = zone();
            var zs = view.Zone(z);
            var color = ZoneColors.Of(z);
            var layer = view.Layer(z);
            g.Text($"Zone {z + 1} · {SamplerText.Wave(zs.Wave)}", r.X + 2, r.Y, 11, color, true, maxWidth: r.Width * 0.62);
            if (layer == null)
            {
                g.Text(zs.Wave == 0 ? "Off: choose a wave below" : zs.Wave <= SampleBank.SongSlots ? "Empty slot: load a file into it below, or wave_import" : "Being made…",
                    r.X + r.Width / 2, bounds.Y + bounds.Height / 2 - 7, 11, Palette.DimText, align: NeoAlign.Center);
                return;
            }
            g.Text($"{layer.Length / (double)layer.Rate:F2} s · {(layer.Stereo ? "stereo" : "mono")} · root {SamplerText.Note(zs.Root == SamplerEngine.RootFromWave ? layer.Root : zs.Root)}",
                r.Right - 2, r.Y, 10, Palette.DimText, align: NeoAlign.Right);
            double mid = bounds.Y + bounds.Height / 2, half = bounds.Height / 2 - 2;
            g.Line(bounds.Left, mid, bounds.Right, mid, new NeoStroke(Palette.Track, 1));
            // The wave, in the region's colour; outside the region dimmed.
            double start = Fraction(SamplerEngine.FStart), end = Fraction(SamplerEngine.FEnd);
            var ov = layer.Overview;
            int cols = SampleLayer.OverviewColumns;
            float peak = Math.Max(1e-6f, layer.Peak);
            for (int c = 0; c < cols; c++)
            {
                double f = (c + 0.5) / cols;
                double x = bounds.X + f * bounds.Width;
                double lo = ov[2 * c] / peak, hi = ov[2 * c + 1] / peak;
                bool inside = f >= Math.Min(start, end) && f <= Math.Max(start, end);
                g.Line(x, mid - hi * half, x, mid - lo * half + 0.5, new NeoStroke(inside ? color : color.WithAlpha(0x50), bounds.Width / cols + 0.4));
            }
            // The loop: the wave's own (dashed) or the zone's, with the crossfade.
            bool waveLoop = !view.Kit && !zs.OneShot && zs.Loop == ZoneLoop.Wave && layer.Loop != WaveLoop.None;
            if (waveLoop)
            {
                double a = layer.LoopStart / (double)layer.Length, b = layer.LoopEnd / (double)layer.Length;
                g.Rect(new NeoRect(X(a), bounds.Y, X(b) - X(a), bounds.Height), accent.WithAlpha(0x18), new NeoStroke(accent.WithAlpha(0x90), 1, true));
                g.Text("wave loop", X(a) + 3, bounds.Bottom - 12, 9, accent);
            }
            else if (ZoneLoops(zs))
            {
                double a = Fraction(SamplerEngine.FLoopS), b = Fraction(SamplerEngine.FLoopE);
                double lo = Math.Max(Math.Min(a, b), Math.Min(start, end)), hi = Math.Min(Math.Max(a, b), Math.Max(start, end));
                g.Rect(new NeoRect(X(lo), bounds.Y, X(hi) - X(lo), bounds.Height), accent.WithAlpha(0x18), NeoStroke.None);
                if (zs.Loop == ZoneLoop.Forward && zs.Xfade > 0)
                {
                    double xf = Math.Min(zs.Xfade * layer.Rate / layer.Length, hi - lo);
                    double room = zs.Reverse ? 1 - hi : lo;
                    xf = Math.Min(xf, room);
                    if (xf > 0)
                    {
                        if (zs.Reverse) g.Rect(new NeoRect(X(lo), bounds.Y, X(lo + xf) - X(lo), bounds.Height), accent.WithAlpha(0x30), NeoStroke.None);
                        else g.Rect(new NeoRect(X(hi - xf), bounds.Y, X(hi) - X(hi - xf), bounds.Height), accent.WithAlpha(0x30), NeoStroke.None);
                    }
                }
            }
            // Markers.
            for (int m = 0; m < 4; m++)
            {
                if (m >= 2 && !ZoneLoops(zs)) continue;
                double x = X(Fraction(Fields[m]));
                var c = m < 2 ? Palette.Text : accent;
                g.Line(x, bounds.Top, x, bounds.Bottom, new NeoStroke(c, m == hover || m == dragging ? 2.2 : 1.2));
                g.Rect(new NeoRect(x - 6, bounds.Bottom, 12, 12), c, NeoStroke.None, 2);
                g.Text(Labels[m], x, bounds.Bottom, 9, Palette.Background, true, NeoAlign.Center);
            }
            // Where its voices are.
            for (int i = 0; i < SamplerEngine.VoiceCount; i++)
                if (view.Voice(i, out int vz, out _, out float pos) && vz == z)
                    g.Line(X(pos), bounds.Top, X(pos), bounds.Bottom, new NeoStroke(Palette.Text.WithAlpha(0xB0), 1));
            if (zs.Reverse) g.Text("reversed", bounds.Right - 2, bounds.Bottom - 12, 9, Palette.DimText, align: NeoAlign.Right);
        }

        int MarkerAt(double x, double y)
        {
            var zs = view.Zone(zone());
            if (view.Layer(zone()) == null) return -1;
            int best = -1;
            double dist = 7;
            for (int m = 0; m < 4; m++)
            {
                if (m >= 2 && !ZoneLoops(zs)) continue;
                double d = Math.Abs(x - X(Fraction(Fields[m])));
                if (d < dist) { dist = d; best = m; }
            }
            return best;
        }

        public bool Down(double x, double y, int clicks, bool fine)
        {
            dragging = MarkerAt(x, y);
            return dragging >= 0;
        }

        public bool Move(double x, double y, bool fine)
        {
            if (dragging < 0)
            {
                int h = MarkerAt(x, y);
                if (h == hover) return false;
                hover = h;
                return true;
            }
            int index = SamplerEngine.ZoneParam(zone(), Fields[dragging]);
            var def = Params.Table[index];
            double f = Math.Clamp((x - bounds.X) / bounds.Width, 0, 1);
            view.Set(index, def.Raw(f * 100));
            return true;
        }

        public bool Up()
        {
            bool was = dragging >= 0;
            dragging = -1;
            return was;
        }

        public bool Wheel(double x, double y, int delta, bool fine) => false;

        public bool Leave()
        {
            if (hover < 0) return false;
            hover = -1;
            return true;
        }
    }
}
