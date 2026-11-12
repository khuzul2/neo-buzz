#nullable disable
// Fork (docs/specs/mcp-server.md §12.3, §12.11, U4): a machine's manifest editor, made by the hosts
// (ReBuzz.exe and the studio compile this file with the editor kit; the machines don't). It reads the
// machine as the agent server does (SongModel.Params: the manifest beside its DLL, curated or draft)
// and hands the editor the server's own conversions: the text param_get shows, ValueResolver for typed
// values (param_set's parser), the physical values for the pictures, and the safe ranges. Nothing
// about the machine changes: its parameters are set through IParameter, as the parameter window does.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using ReBuzz.Agent.Model;
using ReBuzz.Core;

namespace ReBuzz.Agent
{
    internal static class MachineFaces
    {
        /// <summary>Whether a machine has a manifest the editor can be made from (not the Master).</summary>
        public static bool Available(IMachine machine) =>
            machine is MachineCore m && m.DLL?.Info?.Type != MachineType.Master && SongModel.Manifest(m.DLL) != null;

        /// <summary>The machine's manifest editor, built and bound, or null when it has no manifest.</summary>
        /// <param name="openParameters">Opens its plain parameter list (the editor's "Parameter list" button).</param>
        public static ManifestEditor Editor(IMachine machine, Action openParameters)
        {
            var built = Build(machine);
            if (built.Face == null) return null;
            var editor = new ManifestEditor(built.Face, built.Table, built.Slots, openParameters);
            editor.Machine = machine;
            return editor;
        }

        /// <summary>
        /// The face, the editor's table and its slots: every state parameter of the global group and of the track
        /// group on every track (what a preset sets, so a preset loaded from the bar is one undo step in the studio),
        /// with a face parameter for each one the agent server shows (SongModel.Params), on the first track.
        /// </summary>
        public static (MachineFace Face, ParamDef[] Table, List<ParamSlot> Slots) Build(IMachine machine)
        {
            if (!Available(machine)) return (null, null, null);
            var m = (MachineCore)machine;
            var manifest = SongModel.Manifest(m.DLL);
            var slots = new List<ParamSlot>();
            var table = new List<ParamDef>();
            var slotOf = new Dictionary<(IParameter, int), int>();
            foreach (var group in m.ParameterGroups.Where(g => g.Type is ParameterGroupType.Global or ParameterGroupType.Track))
            {
                int tracks = group.Type == ParameterGroupType.Global ? 1 : group.TrackCount;
                for (int t = 0; t < tracks; t++)
                    foreach (var p in group.Parameters)
                    {
                        if ((p.Flags & ParameterFlags.State) == 0) continue;
                        slotOf[(p, t)] = slots.Count;
                        var param = p;
                        table.Add(new ParamDef(slots.Count, p.Name, p.Name, p.MinValue, p.MaxValue, p.DefValue, MapKind.Linear, p.MinValue, p.MaxValue)
                        {
                            Describer = raw => SongModel.Describe(param, raw),
                        });
                        slots.Add(new ParamSlot(p, t));
                    }
            }

            var faceParams = new List<FaceParam>();
            foreach (var ap in SongModel.Params(m))
            {
                if ((ap.Parameter.Flags & ParameterFlags.State) == 0) continue;
                if (!slotOf.TryGetValue((ap.Parameter, 0), out int slot)) continue;
                var fp = FaceOf(ap, slot);
                faceParams.Add(fp);
                table[slot] = DefOf(fp);
            }

            int Raw(FaceParam p)
            {
                var s = slots[p.Slot];
                try { return s.Parameter.GetValue(s.Track); }
                catch (Exception) { return p.Default; }
            }
            var face = MachineFace.Plan(m.DLL.Name, manifest.Quality, manifest.Summary, m.DLL.Info.Type == MachineType.Generator,
                m.TrackCount, faceParams, Raw, () => Math.Max(1, ReBuzzCore.masterInfo.SamplesPerSec), manifest.Type);
            return (face, table.ToArray(), slots);
        }

        /// <summary>A parameter as the agent server reads it, as the editor shows it.</summary>
        static FaceParam FaceOf(AgentParam ap, int slot)
        {
            var info = ap.Info;
            var entry = ap.Manifest;
            var mapping = info.Mapping;
            string dimension = "";
            try { ValueResolver.Physical(info, info.Default, out dimension); }
            catch (Exception) { }
            bool choice = mapping is { Kind: MappingKind.Enum or MappingKind.Switch };
            string[] choices = null;
            if (choice)
            {
                choices = ValueResolver.Choices(info).ToArray();
                if (choices.Length != info.Max - info.Min + 1) choices = null;   // names that don't cover the range: a knob
            }
            bool bipolar = mapping != null && (mapping.Kind == MappingKind.Bipolar ||
                (mapping.Kind is MappingKind.Linear or MappingKind.Int or MappingKind.Power or MappingKind.Db && !mapping.Flag && mapping.A < 0 && mapping.B > 0));
            var safe = info.SafeRange;
            return new FaceParam
            {
                Slot = slot,
                Name = ap.Name,
                Label = entry is { PlainName.Length: > 0 } ? entry.PlainName : ap.Name.Trim(),
                Group = entry?.Group is { Length: > 0 } g ? g : ap.IsTrack ? "Track" : "",
                Role = entry?.Role ?? "",
                Description = info.Description ?? "",
                IsTrack = ap.IsTrack,
                Min = info.Min,
                Max = info.Max,
                Default = info.Default,
                Choices = choices,
                IsSwitch = choices != null && mapping.Kind == MappingKind.Switch && choices.Length == 2,
                Bipolar = bipolar,
                Dimension = choices != null ? "" : dimension ?? "",
                Fraction = dimension == "%" && (mapping == null || mapping.Kind is MappingKind.Percent or MappingKind.Bipolar),
                Approximate = entry?.Approximate == true,
                SafeRange = safe == null ? null : new FaceSafeRange
                {
                    Min = safe.Min,
                    Max = safe.Max,
                    Text = safe.Text(info.Describe),
                    Reason = safe.Reason,
                },
                // The agent server's own conversions: what param_get shows, what param_set reads.
                Text = raw => info.Describe(raw),
                Parse = (text, current) => ValueResolver.Resolve(info, JsonValue.Create(text), current),
                Value = raw => ValueResolver.Physical(info, raw, out _),
            };
        }

        /// <summary>The kit's table entry for a face parameter: its range and default, its text through the face.</summary>
        static ParamDef DefOf(FaceParam p)
        {
            var kind = p.Choices == null ? MapKind.Linear : p.IsSwitch ? MapKind.Switch : MapKind.Enum;
            return new ParamDef(p.Slot, p.Name, p.Label, p.Min, p.Max, p.Default, kind, p.Min, p.Max,
                names: p.Choices, group: p.Group, description: p.Description)
            {
                Describer = p.Text,
            };
        }
    }
}
