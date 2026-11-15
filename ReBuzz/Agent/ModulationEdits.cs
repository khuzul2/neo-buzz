// Fork (docs/specs/ux-overhaul.md P5b, drag-to-modulate): the studio's edits of a modulator's targets, the same model and the same limits as
// the agent's modulate / modulation_remove (8 targets, the manifests' safe ranges: IModulator.SetTarget refuses and clamps), each one gesture
// as one undo step on the song's history. The modulators find their machines by name when an undo runs, as AgentActions' steps do.

using System;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzz.Agent
{
    internal static class ModulationEdits
    {
        /// <summary>The depth a target gets when dragged on (the agent's default: half).</summary>
        public const int DefaultDepth = 50;

        static IModulator Modulator(ReBuzzCore core, string name)
        {
            var m = core.SongCore.MachinesList.FirstOrDefault(x => x.Name == name);
            return m == null ? null : ModulationBoard.Of(m);
        }

        static IParameter Param(MachineCore m, bool isTrack, string name) => ModulationTools.FindParam(m, isTrack, name);

        /// <summary>
        /// Adds a target (or changes the one already on that parameter): the parameter keeps the value it has, the modulator adds depth × its output.
        /// One undo step. Returns null when it worked, else why not (the 8 are in use, the parameter is never modulated…).
        /// </summary>
        public static string Add(ReBuzzCore core, IModulator mod, IMachine target, IParameter parameter, int track, int depth = DefaultDepth, bool? bipolar = null)
        {
            if (core == null || mod == null || target == null || parameter == null) return "Nothing to modulate.";
            if (ReferenceEquals(target, mod.Machine)) return "A modulator cannot modulate itself.";
            bool isTrack = parameter.Group?.Type == ParameterGroupType.Track;
            string modName = mod.Machine.Name, tmName = target.Name, pName = parameter.Name;
            var existing = mod.Targets.FirstOrDefault(i => i.Machine == tmName && i.Param == pName && i.IsTrack == isTrack && i.Track == track);
            var info = new ModulationInfo
            {
                Depth = Math.Clamp(existing?.Depth ?? depth, -100, 100),
                Bipolar = bipolar ?? existing?.Bipolar ?? mod.Kind == "LFO",
                Curve = existing?.Curve ?? 0,
                RangeMin = existing?.RangeMin ?? int.MinValue,
                RangeMax = existing?.RangeMax ?? int.MinValue,
            };
            // The call's own check (a refusal is no undo step); this is also the first doing of the step.
            string refusal = mod.SetTarget(existing?.Slot ?? -1, target, parameter, track, info, out int placed);
            if (refusal != null)
            {
                // The model keeps a refused target as a row in the "Refused" state; a drop leaves no such ghost behind.
                foreach (var ghost in mod.Targets.Where(i => i.State == ModulationState.Refused && i.Machine == tmName && i.Param == pName).ToList()) mod.RemoveTarget(ghost.Slot);
                return refusal;
            }
            var before = existing;
            bool first = true;
            void Apply(ModulationInfo what)
            {
                var m = Modulator(core, modName);
                var t = core.SongCore.MachinesList.FirstOrDefault(x => x.Name == tmName);
                var p = t == null ? null : Param(t, isTrack, pName);
                if (m == null || t == null || p == null) return;
                m.SetTarget(placed, t, p, track, what, out int _);
            }
            core.SongCore.ActionStack.Do(new DelegateAction(
                () => { if (first) first = false; else Apply(info); },
                () =>
                {
                    var m = Modulator(core, modName);
                    if (m == null) return;
                    if (before != null) Apply(before); else m.RemoveTarget(placed);
                }, name: "Modulate " + tmName + " " + pName));
            ModulationBoard.Raise();
            return null;
        }

        /// <summary>Changes a target's depth, polarity, range or curve: one undo step. Null: done, else why not.</summary>
        public static string Edit(ReBuzzCore core, IModulator mod, int slot, Action<ModulationInfo> change)
        {
            var row = mod?.Targets.FirstOrDefault(r => r.Slot == slot);
            if (row == null) return "That target is gone.";
            ModulationInfo Copy(ModulationInfo r) => new ModulationInfo
            {
                Slot = r.Slot, Depth = r.Depth, Bipolar = r.Bipolar, Curve = r.Curve, RangeMin = r.RangeMin, RangeMax = r.RangeMax,
                Min = r.Min, Max = r.Max, SafeMin = r.SafeMin, SafeMax = r.SafeMax,
            };
            var was = Copy(row);
            var now = Copy(row);
            change(now);
            now.Depth = Math.Clamp(now.Depth, -100, 100);
            string refusal = mod.EditTarget(slot, now);
            if (refusal != null) return refusal;
            string modName = mod.Machine.Name;
            bool first = true;
            core.SongCore.ActionStack.Do(new DelegateAction(
                () => { if (first) first = false; else Modulator(core, modName)?.EditTarget(slot, now); },
                () => Modulator(core, modName)?.EditTarget(slot, was), name: "Modulation depth"));
            ModulationBoard.Raise();
            return null;
        }

        /// <summary>Takes a target off: the parameter goes back to the value it had. One undo step; undo puts the target back.</summary>
        public static void Remove(ReBuzzCore core, IModulator mod, int slot)
        {
            var before = mod?.Targets.FirstOrDefault(r => r.Slot == slot);
            if (before == null) return;
            string modName = mod.Machine.Name, tm = before.Machine, pName = before.Param;
            bool isTrack = before.IsTrack;
            int track = before.Track;
            core.SongCore.ActionStack.Do(new DelegateAction(
                () => Modulator(core, modName)?.RemoveTarget(slot),
                () =>
                {
                    var m = Modulator(core, modName);
                    var t = core.SongCore.MachinesList.FirstOrDefault(x => x.Name == tm);
                    var p = t == null ? null : Param(t, isTrack, pName);
                    if (m != null && t != null && p != null) m.SetTarget(slot, t, p, track, before, out int _);
                }, name: "Remove modulation"));
            ModulationBoard.Raise();
        }
    }
}
