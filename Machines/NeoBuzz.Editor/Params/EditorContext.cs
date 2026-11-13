// Fork (docs/specs/studio-mvp.md S1): what a Neo editor builds with, whatever its toolkit: the
// machine's global parameters (EditorContext) and a control's tie to one of them (ParamBinding).
// Moved here from the WPF kit's NeoEditor.cs and Controls.cs, to be shared with the studio's
// Avalonia kit (NeoBuzz.Editor.Avalonia); the editor kit is docs/specs/machines.md §4.
// Fork (docs/specs/mcp-server.md §12.11, U4): a context can also bind the parameters of any group and
// track (ParamSlot), for the manifest editor; the Neo editors bind their global parameters by index, as before.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;

namespace NeoBuzz.Editor
{
    /// <summary>
    /// A parameter a control binds to, on a track. The Neo editors' table entry <c>i</c> is global parameter
    /// <c>i</c> on track 0; the manifest editor (U4) lists its own: global and track parameters, every track.
    /// </summary>
    internal readonly record struct ParamSlot(IParameter Parameter, int Track);

    /// <summary>What a machine's editor builds with: its machine, parameters and accent.</summary>
    internal sealed class EditorContext
    {
        readonly List<ParamBinding> bindings = new List<ParamBinding>();
        readonly IParameterGroup globals;

        /// <param name="slots">Where each table entry is (null: global parameter i, track 0).</param>
        public EditorContext(IMachine machine, ParamDef[] table, NeoColor accent, IReadOnlyList<ParamSlot> slots = null)
        {
            Machine = machine;
            Table = table;
            Accent = accent;
            Slots = slots;
            globals = machine?.ParameterGroups?.FirstOrDefault(g => g.Type == ParameterGroupType.Global);
            manualAt = new long[table.Length];
            // Explain (neo-learn.md §5.3): a machine whose table has How and Sound text gets the strip's state.
            if (table.Any(d => d.HasExplain))
            {
                Explain = new ExplainState(table, Raw);
                Explain.Changed += PublishExplain;
                ExplainBoard.Register(machine);
            }
        }

        /// <summary>What the Explain strip shows and why (null: no control has Explain text).</summary>
        public ExplainState Explain { get; }

        readonly long[] manualAt;

        /// <summary>How long after a hand edit its value's arrival counts as the same hand edit.</summary>
        const long ManualEchoMs = 800;

        void PublishExplain()
        {
            var line = Explain.Current;
            ExplainBoard.Publish(Machine, line == null ? null : new ExplainCard(line.Name, line.Value, line.Badge, line.How, line.Sound));
        }

        /// <summary>The editor wrote a control's value (a knob, a click, the picture, a preset): a hand edit.</summary>
        internal void NoteManual(int index)
        {
            if (index < 0 || index >= manualAt.Length) return;
            manualAt[index] = Environment.TickCount64;
            Explain?.Written(index, ExplainSource.Manual);
        }

        /// <summary>A bound parameter's value arrived (an event of the parameter): says who wrote it.</summary>
        internal void ValueArrived(int index, IParameter parameter, int track)
        {
            if (Explain == null || index < 0 || index >= manualAt.Length) return;
            if (Environment.TickCount64 - manualAt[index] < ManualEchoMs) return;
            Explain.Written(index, ParamSource.TakeAgent(parameter, track) ? ExplainSource.Agent : ExplainSource.Automation);
        }

        /// <summary>Where each table entry is; null for the Neo editors (global parameters by index).</summary>
        public IReadOnlyList<ParamSlot> Slots { get; }

        public IMachine Machine { get; }
        public ParamDef[] Table { get; }
        public NeoColor Accent { get; }

        /// <summary>Undo steps for the editor's edits (the studio's kit sets it); null makes none, as on WPF.</summary>
        public ParamEdits Edits { get; set; }

        public IParameter Parameter(int index)
        {
            if (Slots != null) return index >= 0 && index < Slots.Count ? Slots[index].Parameter : null;
            return globals != null && index >= 0 && index < globals.Parameters.Count ? globals.Parameters[index] : null;
        }

        /// <summary>The track a table entry's parameter is read and written on.</summary>
        public int TrackOf(int index) => Slots != null && index >= 0 && index < Slots.Count ? Slots[index].Track : 0;

        public ParamBinding Bind(int index, Action<int> onValue)
        {
            var b = new ParamBinding(Parameter(index), Table[index], v =>
            {
                onValue(v);
                Changed?.Invoke();
            }, this, index, TrackOf(index));
            bindings.Add(b);
            return b;
        }

        /// <summary>A parameter's raw value now.</summary>
        public int Raw(int index)
        {
            try
            {
                return Parameter(index)?.GetValue(TrackOf(index)) ?? Table[index].Default;
            }
            catch (Exception)
            {
                return Table[index].Default;
            }
        }

        public double Value(int index) => Table[index].Value(Raw(index));

        /// <summary>
        /// The song's tempo (BPM), playing or stopped: what editors show and time things by. A machine's engine hears
        /// the tempo only in the blocks it works, so with the transport stopped (or before it has run) it can still
        /// hold its start-up 120 while the song is at 124 (UI gallery review, mac-402, mac-413). <paramref name="fallback"/>
        /// without a song.
        /// </summary>
        public double SongTempo(double fallback = 120)
        {
            try
            {
                int bpm = Machine?.Graph?.Buzz?.BPM ?? 0;
                return bpm > 0 ? bpm : fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        public void Set(int index, int raw)
        {
            var p = Parameter(index);
            if (p == null) return;
            Edits?.Touch(index);
            NoteManual(index);
            p.SetValue(TrackOf(index), Math.Clamp(raw, Table[index].Min, Table[index].Max));
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

        public ParamBinding(IParameter parameter, ParamDef def, Action<int> onValue, EditorContext owner = null, int index = -1, int track = 0)
        {
            Parameter = parameter;
            Def = def;
            Track = track;
            this.onValue = onValue;
            this.owner = owner;
            this.index = index;
            handler = (p, t) =>
            {
                onValue(Value);
                if (index >= 0) owner?.ValueArrived(index, Parameter, Track);
            };
            Parameter?.SubscribeEvents(track, handler, null);
        }

        public IParameter Parameter { get; }
        public ParamDef Def { get; }
        /// <summary>The track it reads and writes (0 for global parameters).</summary>
        public int Track { get; }

        public int Value
        {
            get
            {
                try
                {
                    return Parameter != null ? Parameter.GetValue(Track) : Def.Default;
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
            if (index >= 0) owner?.NoteManual(index);
            Parameter.SetValue(Track, raw);
            onValue(raw);
        }

        public void Detach() => Parameter?.UnsubscribeEvents(Track, handler, null);

        public int RawFromNormalized(double t) => ParamMapping.RawFromNormalized(Def, t);

        public double Normalized(int raw) => ParamMapping.Normalized(Def, raw);
    }
}
