#nullable disable
// Dye Essence's editor (docs/specs/dye-essence.md §8), the Effects page (§4.10): the ten rack slots, each a type picked
// from a drop-down; the slot selected shows its ten knobs named and valued as its type's engine does, its mix, and the
// chain it sits in with buttons to move it between chains and earlier or later in its chain; the layout, the sends and
// returns, and the chains in words. One source for WPF and Avalonia (the kit's names). The chains live in the machine
// state, so a move is one undo step of its own (FxRoutingAction); everything else is parameters.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
#endif
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.DyeEssence
{
    /// <summary>
    /// A knob for one of a rack slot's ten generic parameters: named and valued as the slot's type does (its engine's own
    /// units), "—" while the slot is Off. 50 % is the engine's default, so the arc starts from the middle.
    /// </summary>
    internal sealed class FxKnob : Knob
    {
        readonly EditorContext ctx;
        readonly int typeParam, k;

        public FxKnob(EditorContext ctx, int slot, int k, double diameter) : base(ctx.Accent, diameter)
        {
            this.ctx = ctx;
            this.k = k;
            int index = FxRack.GenericParam(slot, k);
            typeParam = FxRack.TypeParam(slot);
            var def = ctx.Table[index];
            Origin = 0.5;
            Steps = def.Max - def.Min;
            MaxLabelWidth = 64;
            Binding = ctx.Bind(index, Show);
            TypeBinding = ctx.Bind(typeParam, _ => Show(Binding.Value));
            Default = Binding.Normalized(def.Default);
            UserChanged += t => Binding.Set(Binding.RawFromNormalized(t));
            Show(Binding.Value);
        }

        public ParamBinding Binding { get; }
        ParamBinding TypeBinding { get; }

        void Show(int raw)
        {
            Value = Binding.Normalized(raw);
            int type = ctx.Raw(typeParam);
            if (type <= 0 || type >= FxTypes.Count)
            {
                Label = "—";
                ValueText = "";
                ToolTip = "Choose a type for this slot";
            }
            else
            {
                var info = FxTypes.Info((FxType)type);
                Label = info.Params[k].Label;
                ValueText = info.Describe(k, raw);
                ToolTip = $"{info.Name}: {info.Params[k].Label} (50 % is the default)";
            }
            Refresh();
        }
    }

    /// <summary>One undo step for the rack's chains.</summary>
    internal sealed class FxRoutingAction : IAction
    {
        readonly IMachine machine;
        readonly IMachineGraph graph;
        readonly string name;
        readonly FxRouting before, after;

        public FxRoutingAction(IMachine machine, FxRouting before, FxRouting after)
        {
            this.machine = machine;
            graph = machine.Graph;
            name = machine.Name;
            this.before = before;
            this.after = after;
        }

        public void Do() => Apply(after);

        public void Undo() => Apply(before);

        void Apply(FxRouting r)
        {
            var machines = graph?.Machines;
            var m = machines == null || machines.Contains(machine) ? machine : machines.FirstOrDefault(x => x.Name == name);
            (m?.ManagedMachine as DyeEssenceMachine)?.SetRouting(r);
        }
    }

    /// <summary>The Effects page.</summary>
    internal sealed class FxPage
    {
        const double D = 34;
        static readonly string[] TrioChains = { "Bus A", "Bus B", "Main" };
        static readonly string[] DuoChains = { "Insert 1", "Insert 2", "Aux" };

        readonly EditorContext ctx;
        readonly DyeEssenceMachine machine;
        readonly Func<string, UIElement, UIElement> section;
        readonly ActionButton[] pick = new ActionButton[FxRack.Slots];
        readonly UIElement[] panels = new UIElement[FxRack.Slots];
        readonly ActionButton[] chainButtons = new ActionButton[FxRouting.Chains];
        readonly TextBlock title, chains, clips;
        int selected;

        public FxPage(EditorContext ctx, DyeEssenceMachine machine, Func<string, UIElement, UIElement> section)
        {
            this.ctx = ctx;
            this.machine = machine;
            this.section = section;
            var accent = ctx.Accent;
            title = Text(300);
            chains = Text(300, 11);
            chains.Height = 64;
            clips = Text(300, 11);
            clips.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        string ClipsText() => $"IR: {(string.IsNullOrEmpty(machine?.UserIrName) ? "none" : machine.UserIrName)}   Modulator: {(string.IsNullOrEmpty(machine?.UserModulatorName) ? "none" : machine.UserModulatorName)}";

        /// <summary>A button that loads a WAV as the convolution's response or the vocoder's modulator into the selected slot (the slot takes that type).</summary>
        ActionButton ClipButton(string text, string title, bool ir)
        {
            var btn = new ActionButton(text, ctx.Accent, 120);
            btn.Click += () =>
            {
                if (machine == null) return;
                int slot = selected;
                EditorFiles.Open(btn, title, "Audio (*.wav)|*.wav|All files (*.*)|*.*", file =>
                {
                    try
                    {
                        if (ir) machine.LoadUserIr(file);
                        else machine.LoadUserModulator(file);
                        ctx.Set(FxRack.TypeParam(slot), (int)(ir ? FxType.Convolution : FxType.Vocoder));
                        ctx.Set(FxRack.GenericParam(slot, 0), 1000);
                        ctx.RaiseChanged();
                        Refresh();
                    }
                    catch (Exception e)
                    {
                        MessageBox.Show($"Couldn't load {System.IO.Path.GetFileName(file)}: {e.Message}", "Dye Essence", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                });
            };
            return btn;
        }

        /// <summary>The chain's name in the layout now.</summary>
        string ChainName(int c) => (ctx.Raw(P.FxLayout) == 0 ? TrioChains : DuoChains)[c];

        TextBlock Text(double width, double size = 12) => new TextBlock
        {
            Foreground = Theme.DimText,
            FontFamily = Theme.FontFamily,
            FontSize = size,
            Width = width,
            TextWrapping = TextWrapping.Wrap,
        };

        FxRouting Routing => machine?.FxChains ?? FxRouting.Default;

        /// <summary>Moves a slot to a chain (at its end) or by one place within its chain: one undo step.</summary>
        void Edit(FxRouting after)
        {
            var before = Routing;
            if (machine == null || after == null || after.SameAs(before)) return;
            machine.SetRouting(after);
            var m = ctx.Machine;
            try
            {
                m?.Graph?.Do(new FxRoutingAction(m, before, after));
            }
            catch (Exception)
            {
                // A host without an undo history: the edit stands.
            }
            Refresh();
        }

        void Select(int slot)
        {
            selected = Math.Clamp(slot, 0, FxRack.Slots - 1);
            Refresh();
        }

        /// <summary>The slot buttons, the panels, the buttons' names and the texts, for the slot and the state now.</summary>
        void Refresh()
        {
            for (int s = 0; s < FxRack.Slots; s++)
            {
                string text = s == selected ? "▶" + (s + 1) : (s + 1).ToString();
                if (pick[s].Text != text)
                {
                    pick[s].Text = text;
                    pick[s].InvalidateVisual();
                }
                panels[s].Visibility = s == selected ? Visibility.Visible : Visibility.Collapsed;
            }
            var route = Routing;
            int type = ctx.Raw(FxRack.TypeParam(selected));
            int chain = route.ChainOf(selected);
            int position = Array.IndexOf(route.Chain[chain], selected) + 1;
            title.Text = $"Slot {selected + 1}: {FxTypes.Name(type)}, {ChainName(chain)} (place {position} of {route.Chain[chain].Length})";
            for (int c = 0; c < FxRouting.Chains; c++)
            {
                string name = ChainName(c);
                if (chainButtons[c].Text != name)
                {
                    chainButtons[c].Text = name;
                    chainButtons[c].InvalidateVisual();
                }
            }
            var sb = new StringBuilder();
            for (int c = 0; c < FxRouting.Chains; c++)
            {
                var names = route.Chain[c].Where(s => ctx.Raw(FxRack.TypeParam(s)) != 0).Select(s => $"{s + 1} {FxTypes.Name(ctx.Raw(FxRack.TypeParam(s)))}");
                sb.Append(ChainName(c)).Append(": ").Append(names.Any() ? string.Join(" > ", names) : "empty");
                if (c < FxRouting.Chains - 1) sb.Append('\n');
            }
            if (chains.Text != sb.ToString()) chains.Text = sb.ToString();
            string clipText = ClipsText();
            if (clips.Text != clipText) clips.Text = clipText;
        }

        public UIElement Build()
        {
            var accent = ctx.Accent;
            // The slots: a button to select, the type drop-down, five to a column.
            var columns = new[] { Layout.Column(), Layout.Column() };
            for (int s = 0; s < FxRack.Slots; s++)
            {
                int slot = s;
                var btn = new ActionButton((s + 1).ToString(), accent, 34);
                btn.Click += () => Select(slot);
                pick[s] = btn;
                var row = Layout.Row(Layout.Column(Layout.Gap(1, 16), btn), Layout.Gap(4), new ParamDropdown(ctx, FxRack.TypeParam(s), "Slot " + (s + 1), 104));
                columns[s / 5].Children.Add(row);
            }
            var slots = section("Rack slots", Layout.Row(columns[0], Layout.Gap(10), columns[1]));

            // The selected slot: ten knobs and the mix, each type's names; where it sits.
            var detail = Layout.Column();
            for (int s = 0; s < FxRack.Slots; s++)
            {
                var grid1 = new UniformGrid { Columns = 5 };
                var grid2 = new UniformGrid { Columns = 5 };
                for (int k = 0; k < FxTypes.PerType; k++) (k < 5 ? grid1 : grid2).Children.Add(new FxKnob(ctx, s, k, D));
                var panel = Layout.Column(grid1, Layout.Gap(1, 4), grid2, Layout.Gap(1, 4), new ParamKnob(ctx, FxRack.MixParam(s), "mix", D));
                panels[s] = panel;
                detail.Children.Add(panel);
            }
            for (int c = 0; c < FxRouting.Chains; c++)
            {
                int chain = c;
                var b = new ActionButton(ChainName(c), accent, 80);
                b.Click += () => Edit(Routing.Move(selected, chain, int.MaxValue));
                chainButtons[c] = b;
            }
            var earlier = new ActionButton("Earlier", accent, 80);
            earlier.Click += () =>
            {
                var r = Routing;
                int c = r.ChainOf(selected);
                Edit(r.Move(selected, c, Array.IndexOf(r.Chain[c], selected) - 1));
            };
            var later = new ActionButton("Later", accent, 80);
            later.Click += () =>
            {
                var r = Routing;
                int c = r.ChainOf(selected);
                Edit(r.Move(selected, c, Array.IndexOf(r.Chain[c], selected) + 1));
            };
            var slotPanel = section("Slot", Layout.Column(
                title,
                Layout.Gap(1, 6),
                detail,
                Layout.Gap(1, 6),
                Layout.Row(chainButtons[0], Layout.Gap(4), chainButtons[1], Layout.Gap(4), chainButtons[2]),
                Layout.Gap(1, 4),
                Layout.Row(earlier, Layout.Gap(4), later),
                Layout.Gap(1, 4),
                Layout.Row(ClipButton("Load IR…", "Load an impulse response", true), Layout.Gap(4), ClipButton("Load modulator…", "Load a modulator", false)),
                Layout.Gap(1, 2),
                clips));

            var buses = section("Buses", Layout.Column(
                new ParamChoice(ctx, P.FxLayout, "Layout", 56),
                Layout.Gap(1, 6),
                Layout.Row(new ParamKnob(ctx, P.FxSendA, "send A", D), new ParamKnob(ctx, P.FxRetA, "return A", D)),
                Layout.Gap(1, 4),
                Layout.Row(new ParamKnob(ctx, P.FxSendB, "send B", D), new ParamKnob(ctx, P.FxRetB, "return B", D)),
                Layout.Gap(1, 6),
                chains));

            ctx.Changed += Refresh;
            int seen = machine?.ShapesVersion ?? 0;
            Poll.Every(chains, 0.3, () =>
            {
                int now = machine?.ShapesVersion ?? 0;
                if (now == seen) return;
                seen = now;
                Refresh();
            });
            Refresh();
            return Layout.Row(slots, slotPanel, buses);
        }
    }
}
