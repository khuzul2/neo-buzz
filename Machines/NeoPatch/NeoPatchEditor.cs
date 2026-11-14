#nullable disable
// Neo Patch's editor (docs/specs/neo-patch.md §4.5, NP2): the inner canvas on the Neo editor kit, one source for WPF and Avalonia. The kit's shell gives the
// header (name, presets); the body is the canvas surface (Editor/PatchSurface*.cs, PatchPanel.cs), drawn on the kit's canvas and driven through its
// pointer surface. Edits go through the PatchEditModel (Engine/Editor/): each is one step on the song's undo stack (PatchEditAction), compiled off the
// audio thread and swapped in at a block boundary by the engine. Macros 1 to 16 are the machine's parameters: the strip writes them as the knobs do.

using System;
using System.Collections.Generic;
using System.Linq;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
using System.Windows.Controls;
#endif
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Patch
{
    internal sealed class NeoPatchEditor : NeoEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0xFF, 0x5C, 0x8A));

        protected override string Title => "Neo Patch";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        /// <summary>What the body built, for tests.</summary>
        internal PatchSurface Surface { get; private set; }
        internal PatchEditModel Model { get; private set; }
        internal InteractiveGraph View { get; private set; }

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoPatchMachine;
            if (machine == null) return new TextBlock { Text = "Neo Patch's engine is not loaded.", Foreground = Theme.Text, Margin = new Thickness(10) };
            var model = new PatchEditModel(machine.Engine, new SongHistory(ctx.Machine));
            var surface = new PatchSurface(model, new MacroHost(ctx), AccentColor);
            Model = model;
            surface.TidyIfStacked();
            Surface = surface;
            bool restored = !string.IsNullOrEmpty(machine.View);
            surface.ViewText = machine.View;
            if (!restored) surface.Fit();
            string saved = surface.ViewText;
            var view = new InteractiveGraph(PatchSurface.Width, PatchSurface.Height, (g, r) =>
            {
                surface.Draw(g, r);
                string now = surface.ViewText;
                if (now != saved) { saved = now; machine.View = now; }
            }, surface, animate: true);
            View = view;
            Action replaced = () => { model.Replaced(); };
            machine.PatchReplaced += replaced;
            view.Unloaded += (s, e) => machine.PatchReplaced -= replaced;
            return view;
        }

        /// <summary>The macros are the machine's parameters, 0 to 1000 raw.</summary>
        sealed class MacroHost : IPatchSurfaceHost
        {
            readonly EditorContext ctx;
            public MacroHost(EditorContext ctx) { this.ctx = ctx; }
            public float Macro(int i) => ctx.Raw(P.Macro1 + i) / 1000f;
            public void SetMacro(int i, float value01) => ctx.Set(P.Macro1 + i, (int)Math.Round(Math.Clamp(value01, 0f, 1f) * 1000));
            public void BeginMacro() { }
            public void EndMacro() { }
            public int Voices => (int)Math.Round(ctx.Value(P.Voices));
        }
    }
}
