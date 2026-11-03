// Fork (docs/specs/studio-mvp.md S1): what a Neo editor builds with, whatever its toolkit: the
// machine's global parameters (EditorContext) and a control's tie to one of them (ParamBinding).
// Moved here from the WPF kit's NeoEditor.cs and Controls.cs, to be shared with the studio's
// Avalonia kit (NeoBuzz.Editor.Avalonia); the editor kit is docs/specs/machines.md §4.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;

namespace NeoBuzz.Editor
{
    /// <summary>What a machine's editor builds with: its machine, parameters and accent.</summary>
    internal sealed class EditorContext
    {
        readonly List<ParamBinding> bindings = new List<ParamBinding>();
        readonly IParameterGroup globals;

        public EditorContext(IMachine machine, ParamDef[] table, NeoColor accent)
        {
            Machine = machine;
            Table = table;
            Accent = accent;
            globals = machine?.ParameterGroups?.FirstOrDefault(g => g.Type == ParameterGroupType.Global);
        }

        public IMachine Machine { get; }
        public ParamDef[] Table { get; }
        public NeoColor Accent { get; }

        /// <summary>Undo steps for the editor's edits (the studio's kit sets it); null makes none, as on WPF.</summary>
        public ParamEdits Edits { get; set; }

        public IParameter Parameter(int index) =>
            globals != null && index >= 0 && index < globals.Parameters.Count ? globals.Parameters[index] : null;

        public ParamBinding Bind(int index, Action<int> onValue)
        {
            var b = new ParamBinding(Parameter(index), Table[index], v =>
            {
                onValue(v);
                Changed?.Invoke();
            }, this, index);
            bindings.Add(b);
            return b;
        }

        /// <summary>A parameter's raw value now.</summary>
        public int Raw(int index)
        {
            try
            {
                return Parameter(index)?.GetValue(0) ?? Table[index].Default;
            }
            catch (Exception)
            {
                return Table[index].Default;
            }
        }

        public double Value(int index) => Table[index].Value(Raw(index));

        public void Set(int index, int raw)
        {
            var p = Parameter(index);
            if (p == null) return;
            Edits?.Touch(index);
            p.SetValue(0, Math.Clamp(raw, Table[index].Min, Table[index].Max));
        }

        public void Detach()
        {
            foreach (var b in bindings) b.Detach();
            bindings.Clear();
        }

        /// <summary>Called when any bound parameter changes (graphs redraw).</summary>
        public event Action Changed;

        internal void RaiseChanged() => Changed?.Invoke();
    }

    /// <summary>
    /// Ties a control to one global parameter: user edits set the parameter, automation and other
    /// editors move the control.
    /// </summary>
    internal sealed class ParamBinding
    {
        readonly Action<int> onValue;
        readonly Action<IParameter, int> handler;
        readonly EditorContext owner;
        readonly int index;

        public ParamBinding(IParameter parameter, ParamDef def, Action<int> onValue, EditorContext owner = null, int index = -1)
        {
            Parameter = parameter;
            Def = def;
            this.onValue = onValue;
            this.owner = owner;
            this.index = index;
            handler = (p, track) => onValue(Value);
            Parameter?.SubscribeEvents(0, handler, null);
        }

        public IParameter Parameter { get; }
        public ParamDef Def { get; }

        public int Value
        {
            get
            {
                try
                {
                    return Parameter != null ? Parameter.GetValue(0) : Def.Default;
                }
                catch (Exception)
                {
                    return Def.Default;
                }
            }
        }

        public void Set(int raw)
        {
            raw = Math.Clamp(raw, Def.Min, Def.Max);
            if (Parameter == null || raw == Value) return;
            if (index >= 0) owner?.Edits?.Touch(index);
            Parameter.SetValue(0, raw);
            onValue(raw);
        }

        public void Detach() => Parameter?.UnsubscribeEvents(0, handler, null);

        public int RawFromNormalized(double t) => ParamMapping.RawFromNormalized(Def, t);

        public double Normalized(int raw) => ParamMapping.Normalized(Def, raw);
    }
}
