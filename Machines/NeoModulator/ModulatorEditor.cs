#nullable disable
// The canvas modulators' editor template (docs/specs/neo-patch.md §3, phase NM), for Neo LFO, Neo Envelope, Neo Step and Neo Macro, on the
// Neo kit so the WPF app and the studio show the same. Top to bottom:
//  - the machine's own controls (each machine's BuildControls) beside the output: a live scope of the modulator's output, and, where it listens
//    to a machine (notes for a retrigger or an envelope, an output level for the follower), the picker for that machine;
//  - the targets: up to 8 rows of machine, parameter, depth, polarity, range, curve and a live position (the Core's ModTable); a click on a
//    machine or parameter opens the picker (the machine, then a searchable list of its parameters);
//  - the Explain strip.
// It draws through INeoCanvas, so the two backends show the same.

using System;
using System.Collections.Generic;
using System.Linq;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Modulator
{
    internal abstract class ModulatorEditor : NeoEditor, IModTargetHost
    {
        public const double BodyWidth = ModTable.Width + 24;

        IModulator mod;
        EditorContext context;
        InteractiveGraph tableView;
        ActionButton listenButton;
        IReadOnlyList<ModulationInfo> rows = Array.Empty<ModulationInfo>();
        long rowsAt = long.MinValue;
        readonly float[] scope = new float[128];

        /// <summary>The machine's own controls (knobs, drop-downs).</summary>
        protected abstract UIElement BuildControls(EditorContext ctx);

        /// <summary>Whether the machine listens to another machine (its notes, or its output level), and what the picker says.</summary>
        protected virtual string ListenLabel => null;

        /// <summary>The target table (for tests).</summary>
        internal InteractiveGraph TargetView => tableView;
        internal ModTable TableModel { get; private set; }
        internal ActionButton ListenButton => listenButton;
        internal IModulator Modulator => mod;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            context = ctx;
            mod = ctx.Machine?.ManagedMachine as IModulator;
            var accent = Accent;

            var controls = BuildControls(ctx);
            var scopeView = new GraphView(210, 86, (g, r) =>
            {
                int n = mod != null ? mod.ReadScope(scope) : 0;
                ModScope.Draw(g, r, n > 0 ? scope : null, n, mod?.Output ?? 0f, accent);
            }, animate: true);
            var outputItems = new List<UIElement> { scopeView };
            if (ListenLabel != null)
            {
                listenButton = new ActionButton(ListenText(), accent, 210);
                listenButton.Click += () => PickListen(listenButton);
                outputItems.Add(Layout.Gap(1, 6));
                outputItems.Add(listenButton);
            }
            var top = Layout.Row(controls, Layout.Gap(6), new Section("Output", accent, Layout.Column(outputItems.ToArray())));

            TableModel = new ModTable(this, accent);
            tableView = new InteractiveGraph(ModTable.Width, ModTable.Height, (g, r) => TableModel.Draw(g, r), TableModel, animate: true);
            var strip = new ExplainStrip(ctx, BodyWidth - 12);
            return Layout.Column(top, new Section("Targets", accent, tableView), new Section("Explain", accent, strip));
        }

        string ListenText()
        {
            string name = mod?.ListenMachine;
            return ListenLabel + ": " + (string.IsNullOrEmpty(name) ? "none" : name) + " ▾";
        }

        // ------------------------------------------------------------------ the song's machines

        List<IMachine> Machines()
        {
            var list = new List<IMachine>();
            try
            {
                foreach (var m in context.Machine.Graph.Machines)
                    if (!ReferenceEquals(m, context.Machine)) list.Add(m);
            }
            catch (Exception) { }
            return list;
        }

        void PickListen(UIElement owner)
        {
            var machines = Machines();
            var items = new List<string> { "(none)" };
            items.AddRange(machines.Select(m => m.Name));
            PickerPopup.Show(owner, 0, 0, items, i =>
            {
                mod.ListenMachine = i == 0 ? "" : machines[i - 1].Name;
                listenButton.Text = ListenText();
                listenButton.InvalidateVisual();
            }, items.Count > 12);
        }

        // ------------------------------------------------------------------ IModTargetHost

        public IReadOnlyList<ModulationInfo> Rows
        {
            get
            {
                long now = Environment.TickCount64;
                if (now - rowsAt > 25 || now < rowsAt)
                {
                    rowsAt = now;
                    try { rows = mod?.Targets ?? rows; } catch (Exception) { }
                }
                return rows;
            }
        }

        public void PickMachine(int slot, double x, double y)
        {
            if (mod == null) return;
            var machines = Machines();
            var items = machines.Select(m => m.Name).ToList();
            PickerPopup.Show(tableView, x, y, items, i => PickParam(slot, machines[i], x, y), items.Count > 12);
        }

        public void PickParam(int slot, double x, double y)
        {
            var row = Rows.FirstOrDefault(r => r.Slot == slot);
            var machine = row == null ? null : Machines().FirstOrDefault(m => m.Name == row.Machine);
            if (machine != null) PickParam(slot, machine, x, y);
        }

        void PickParam(int slot, IMachine machine, double x, double y)
        {
            var choices = new List<(IParameter Parameter, int Track, string Label)>();
            foreach (var group in machine.ParameterGroups)
            {
                bool global = group.Type == ParameterGroupType.Global, tracks = group.Type == ParameterGroupType.Track;
                if (!global && !tracks) continue;
                foreach (var p in group.Parameters)
                {
                    if (p.Type == ParameterType.Note) continue;
                    if (global) choices.Add((p, 0, p.Name));
                    else for (int t = 0; t < Math.Min(16, Math.Max(1, group.TrackCount)); t++)
                            choices.Add((p, t, group.TrackCount > 1 ? $"{p.Name} (track {t + 1})" : p.Name));
                }
            }
            PickerPopup.Show(tableView, x, y, choices.Select(c => c.Label).ToList(), i =>
            {
                var (parameter, track, _) = choices[i];
                var old = slot >= 0 ? Rows.FirstOrDefault(r => r.Slot == slot) : null;
                var info = old ?? new ModulationInfo { Bipolar = mod.Kind == "LFO" };
                string refusal = mod.SetTarget(slot, machine, parameter, track, info, out _);
                if (refusal != null) System.Diagnostics.Debug.WriteLine("Neo modulator: " + refusal);
                rowsAt = long.MinValue;
            }, true);
        }

        public void Edit(int slot, Action<ModulationInfo> change)
        {
            var row = Rows.FirstOrDefault(r => r.Slot == slot);
            if (row == null || mod == null) return;
            var copy = new ModulationInfo
            {
                Slot = row.Slot, Depth = row.Depth, Bipolar = row.Bipolar, Curve = row.Curve, RangeMin = row.RangeMin, RangeMax = row.RangeMax,
                Min = row.Min, Max = row.Max, SafeMin = row.SafeMin, SafeMax = row.SafeMax,
            };
            change(copy);
            mod.EditTarget(slot, copy);
            rowsAt = long.MinValue;
        }

        public void Remove(int slot)
        {
            mod?.RemoveTarget(slot);
            rowsAt = long.MinValue;
        }

        public void BeginGesture() { }
        public void EndGesture() { }
    }
}
