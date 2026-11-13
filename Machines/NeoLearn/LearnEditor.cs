#nullable disable
// The Neo Learn editor template (docs/specs/neo-learn.md §5.4), for every Neo Learn machine: four zones, top to bottom.
//  - header: the category and what the machine is playing (voices), a clip light; the kit's header above it holds the name and presets;
//  - picture: the scope of the output with the filter's response curve over it and an X/Y pad (the handle is cutoff and resonance;
//    a drag moves both as one undo step, a double-click resets them);
//  - deck: the machine's 12 controls, the six core ones then the six global ones (a knob's double-click resets it);
//  - Explain strip: what the control you touch does (the kit's, §5.3).
// The piano keyboard hangs below it (the machine window's frame). All of it is about 900 x 440 pt, so with the keyboard it fits 1280 x 720.
// It draws through INeoCanvas, so the WPF and the Avalonia backends show the same.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Learn
{
    internal abstract class LearnEditor : NeoEditor
    {
        /// <summary>The template's size: the picture's, and the strip's, in points.</summary>
        public const double BodyWidth = 876, PictureHeight = 190;

        /// <summary>"Subtractive" ...: the tag in the header zone.</summary>
        protected abstract string Category { get; }

        /// <summary>The machine's host (the scope, voices and clip light come from it).</summary>
        protected abstract LearnHost HostOf(EditorContext ctx);

        /// <summary>The table entries the pad moves: X (cutoff) and Y (resonance).</summary>
        protected abstract int PadX { get; }
        protected abstract int PadY { get; }

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var host = HostOf(ctx);
            var accent = Accent;

            // Header zone.
            var header = new GraphView(BodyWidth, 22, (g, r) => DrawHeader(g, r, host, accent, Category), animate: true);

            // Picture zone.
            var target = new PadTarget(ctx, PadX, PadY);
            var scope = new float[512];
            NeoRect plot = default;
            var input = new LearnPictureInput(target, () => plot);
            var picture = new InteractiveGraph(BodyWidth, PictureHeight, (g, r) =>
            {
                plot = r;
                int n = 0;
                if (host != null) { host.CopyScope(scope); n = scope.Length; }
                double cutoff = ctx.Value(PadX);
                double reso = ParamMapping.Normalized(ctx.Table[PadY], ctx.Raw(PadY));
                LearnPictureGraph.Draw(g, r, host != null ? scope : null, n, cutoff, reso, input.Handle, input.Capturing, accent);
            }, input, animate: true);

            // Deck zone: the six core controls, then the six global ones.
            var core = new UIElement[6];
            var global = new UIElement[6];
            for (int i = 0; i < 6; i++)
            {
                core[i] = new ParamKnob(ctx, i, diameter: 50);
                global[i] = new ParamKnob(ctx, 6 + i, diameter: 50);
            }
            var deck = Layout.Row(
                new Section("Core", accent, Layout.Row(core)),
                new Section("Global", accent, Layout.Row(global)));

            // Explain strip.
            var strip = new ExplainStrip(ctx, BodyWidth);

            return Layout.Column(header, new Section("Picture", accent, picture), deck, new Section("Explain", accent, strip));
        }

        static void DrawHeader(INeoCanvas g, NeoRect r, LearnHost host, NeoColor accent, string category)
        {
            string tag = "LEARN \u00B7 " + category.ToUpperInvariant();
            double w = g.MeasureText(tag, 10, true) + 14;
            g.Rect(new NeoRect(r.X + 4, r.Y + 3, w, 16), accent.WithAlpha(0x30), new NeoStroke(accent, 1), 8);
            g.Text(tag, r.X + 4 + w / 2, r.Y + 5, 10, accent, true, NeoAlign.Center);
            int voices = host?.ActiveVoices ?? 0;
            g.Text(voices == 1 ? "1 voice" : voices + " voices", r.X + w + 14, r.Y + 4, 11, Palette.DimText);
            // The clip light.
            bool clip = host != null && host.Clipping;
            var col = clip ? NeoColor.FromRgb(0xFF, 0x4A, 0x4A) : Palette.Grid;
            g.Ellipse(new NeoPoint(r.Right - 52, r.Y + 11), 5, 5, col, NeoStroke.None);
            g.Text("clip", r.Right - 44, r.Y + 4, 10, Palette.DimText);
        }

        /// <summary>The pad's two controls: a drag writes both inside one undo gesture, and the strip names both.</summary>
        sealed class PadTarget : IPictureTarget
        {
            readonly EditorContext ctx;
            readonly int x, y;

            public PadTarget(EditorContext ctx, int x, int y)
            {
                this.ctx = ctx;
                this.x = x;
                this.y = y;
            }

            public ParamDef XDef => ctx.Table[x];
            public ParamDef YDef => ctx.Table[y];
            public int XRaw => ctx.Raw(x);
            public int YRaw => ctx.Raw(y);

            public void Set(int xRaw, int yRaw)
            {
                ctx.Explain?.PictureDrag(x, y);
                ctx.Set(x, xRaw);
                ctx.Set(y, yRaw);
            }

            public void BeginGesture() => ctx.Edits?.Begin();

            public void EndGesture()
            {
                ctx.Explain?.PictureDragEnd();
                ctx.Edits?.End();
            }
        }
    }
}
