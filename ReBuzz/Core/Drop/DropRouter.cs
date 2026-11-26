// Fork (docs/specs/drag-drop.md, DD1): the one place that decides what a drop does. The Arrangement, the Session View and the Machine View
// (and the agent's drop_item) hand it what was dropped (browser items and files from the desktop, as BrowserDragData) and where, and it
// answers two questions with the same rules: Plan (what would happen, or why not: the views' feedback while dragging) and Drop (do it,
// as one undo step). The rules are the brief's table (docs/specs/audio-browser-plan.md); the table is in drag-drop.md.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuzzGUI.Common;
using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;
using ReBuzz.Agent.Browser;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.Session;

namespace ReBuzz.Core.Drop
{
    internal enum DropView { Machine, Arrangement, Session }

    /// <summary>What a drop does.</summary>
    internal enum DropAction
    {
        Refuse,
        /// <summary>A new machine with a track (a generator), wired to the Master; in the Arrangement a clip at the time.</summary>
        NewTrack,
        /// <summary>Other gear in the machine's place (connections kept).</summary>
        Replace,
        /// <summary>An effect between the track's last machine and what it fed.</summary>
        InsertEffect,
        /// <summary>NW1: a machine that takes and sends notes, added to the track's note chain before its instrument.</summary>
        InsertNote,
        /// <summary>The Machine View: a machine added where it was dropped, not wired.</summary>
        AddMachine,
        /// <summary>A preset loaded into a machine that is of its type.</summary>
        LoadPreset,
        /// <summary>A new track with that machine and the preset.</summary>
        NewTrackWithPreset,
        /// <summary>The machine replaced by the preset's, and the preset loaded.</summary>
        ReplaceWithPreset,
        /// <summary>A new Neo Audio track with the files as clips.</summary>
        NewAudioTrack,
        /// <summary>The files as clips on an audio track.</summary>
        AudioClips,
        /// <summary>The files as samples of a sampler.</summary>
        LoadSample,
        /// <summary>A pattern as a clip of its machine's track.</summary>
        PatternClip,
        /// <summary>A new track with the MIDI file's notes on a default instrument.</summary>
        MidiNewTrack,
        /// <summary>The MIDI file's notes in a new pattern of the track.</summary>
        MidiNewPattern,
    }

    /// <summary>Where a drop lands. Empty space is a target without a machine.</summary>
    internal sealed class DropTarget
    {
        public DropView View { get; init; }

        /// <summary>The track's machine (Machine View: the box's), or null on empty space.</summary>
        public IMachine Machine { get; init; }

        /// <summary>The Arrangement's lane under the pointer (the machine's first lane when null).</summary>
        public ISequence Lane { get; init; }

        /// <summary>The Arrangement's time in ticks (already snapped by the view), or -1.</summary>
        public int Time { get; init; } = -1;

        /// <summary>The Session's scene (0-based), or -1.</summary>
        public int Scene { get; init; } = -1;

        /// <summary>The Machine View's position for a new machine, in its coordinates.</summary>
        public float X { get; init; }
        public float Y { get; init; }

        /// <summary>For an effect on a track that branches: the destinations chosen, in order (machine names).</summary>
        public IReadOnlyList<string> Branch { get; init; }

        /// <summary>The instrument for a MIDI file on empty space: a gear name (the last one used), else Neo Poly.</summary>
        public string DefaultInstrument { get; init; }
    }

    /// <summary>What a drop would do, for the feedback; or why it can't.</summary>
    internal sealed class DropPlan
    {
        public DropAction Action { get; init; }
        public bool Allowed => Action != DropAction.Refuse && Choices == null;

        /// <summary>The reason, for a refusal (with its hint: "Drop on a track").</summary>
        public string Refusal { get; init; }

        /// <summary>One line for the feedback ("New track: Neo Poly").</summary>
        public string Describe { get; init; } = "";

        /// <summary>A new track will go at the end: the view draws its insertion line.</summary>
        public bool MakesTrack => Action is DropAction.NewTrack or DropAction.NewTrackWithPreset or DropAction.NewAudioTrack or DropAction.MidiNewTrack;

        /// <summary>The track (machine) the drop lands on, lit by the view.</summary>
        public IMachine Target { get; init; }

        /// <summary>For an effect on a branching track: the destinations to pick from (machine names), else null.</summary>
        public IReadOnlyList<string> Choices { get; init; }
        public string ChoiceTitle { get; init; }

        public static DropPlan Refuse(string why) => new() { Action = DropAction.Refuse, Refusal = why, Describe = why };
    }

    internal sealed class DropResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; } = "";
        public DropAction Action { get; init; }

        /// <summary>The machine made, changed or loaded.</summary>
        public IMachine Machine { get; init; }

        /// <summary>The patterns made or placed (clips), in order.</summary>
        public IReadOnlyList<IPattern> Patterns { get; init; } = Array.Empty<IPattern>();

        /// <summary>For an effect on a branching track: the question, and what to pick from.</summary>
        public IReadOnlyList<string> Choices { get; init; }

        public string Hint => Message;
    }

    internal sealed class DropRouter
    {
        public const string SamplerName = "Neo Sampler";
        public const string DefaultInstrumentName = "Neo Poly";

        /// <summary>The longest a pattern made for a MIDI file gets, in ticks.</summary>
        public const int MaxPatternTicks = 8192;

        readonly ReBuzzCore core;

        public DropRouter(ReBuzzCore core) => this.core = core;

        SongCore Song => core.SongCore;
        SessionLayout Layout => core.SessionLayout;

        // ------------------------------------------------------------------ what was dropped

        /// <summary>A file from the desktop as a browser item, or null when it is neither audio nor MIDI.</summary>
        public static BrowserDragData FromFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (MidiFile.IsMidiFile(path)) return new BrowserDragData(BrowserKind.Midi, path, null, Path.GetFileNameWithoutExtension(path));
            if (AudioImporter.IsAudioFile(path)) return new BrowserDragData(BrowserKind.Audio, path, null, Path.GetFileNameWithoutExtension(path));
            return null;
        }

        /// <summary>The installed gear a gear name (the browser's) names: "Neo Poly", or a plug-in's "Vendor/Name".</summary>
        public IInstrument FindInstrument(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return core.Instruments.FirstOrDefault(i => i.MachineDLL?.Info != null && GearNames.Display(i).Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? core.Instruments.FirstOrDefault(i => i.MachineDLL?.Info != null && i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        static bool IsMaster(IMachine m) => m?.DLL?.Info?.Type == MachineType.Master;

        static bool IsClipPlayer(IMachine m) => SessionLayout.IsClipPlayer(m);

        static bool IsSampler(IMachine m) => m?.DLL?.Name == SamplerName;

        static bool IsGenerator(IMachine m) => m?.DLL?.Info?.Type == MachineType.Generator && !m.IsControlMachine;

        /// <summary>A machine that plays notes: a generator that isn't a control machine nor an audio player.</summary>
        static bool PlaysNotes(IMachine m) => IsGenerator(m) && !IsClipPlayer(m);

        bool HasSequence(IMachine m) => m != null && Song.Sequences.Any(s => s.Machine == m);

        // ------------------------------------------------------------------ plan

        /// <summary>What dropping these at the target would do, without doing it.</summary>
        public DropPlan Plan(IReadOnlyList<BrowserDragData> items, DropTarget t)
        {
            if (items == null || items.Count == 0) return DropPlan.Refuse("Nothing to drop.");
            if (t == null) return DropPlan.Refuse("Nowhere to drop it.");
            var machine = t.Machine ?? t.Lane?.Machine;
            if (t.View != DropView.Machine && IsMaster(machine)) return DropPlan.Refuse("The Master's rows hold tempo and markers. Drop on a track.");
            var first = items[0];
            if (items.Count > 1 && items.Any(i => i.Kind != BrowserKind.Audio))
                return DropPlan.Refuse("Drop one kind at a time: several audio files, or a single other item.");
            try
            {
                return first.Kind switch
                {
                    BrowserKind.Machine or BrowserKind.Plugin => PlanGear(first, t, machine),
                    BrowserKind.Preset => PlanPreset(first, t, machine),
                    BrowserKind.Audio => PlanAudio(items, t, machine),
                    BrowserKind.Midi => PlanMidi(first, t, machine),
                    BrowserKind.Pattern => PlanPattern(first, t, machine),
                    _ => DropPlan.Refuse("That can't be dropped here."),
                };
            }
            catch (ToolError e)
            {
                return DropPlan.Refuse(e.Message);
            }
        }

        string WhyUnusable(IInstrument inst, string name)
        {
            if (inst == null) return $"“{name}” isn't installed.";
            if (inst.MachineDLL.IsMissing) return $"{GearNames.Display(inst)} is missing.";
            if (inst.MachineDLL.IsCrashed) return $"{GearNames.Display(inst)} crashed and is switched off.";
            return null;
        }

        /// <summary>Why other gear can't take a machine's place (the Machine View's rules, BR1); null when it can.</summary>
        string WhyNotReplace(IMachine machine, IInstrument inst)
        {
            if (machine == null) return "Nothing to replace.";
            if (IsMaster(machine)) return "The Master can't be replaced.";
            var have = machine.DLL.Info.Type;
            var want = inst.Type == InstrumentType.Generator ? MachineType.Generator : inst.Type == InstrumentType.Effect ? MachineType.Effect : MachineType.Generator;
            if (inst.Type == InstrumentType.Control)
                return have == MachineType.Generator && machine.IsControlMachine ? null : "A control machine can only replace a control machine.";
            if (have != want) return have == MachineType.Generator ? "A generator can only be replaced by a generator." : "An effect can only be replaced by an effect.";
            return null;
        }

        DropPlan PlanGear(BrowserDragData d, DropTarget t, IMachine machine)
        {
            var inst = FindInstrument(d.Id);
            if (WhyUnusable(inst, d.Id) is { } bad) return DropPlan.Refuse(bad);
            string name = GearNames.Display(inst);
            bool generator = inst.Type == InstrumentType.Generator;

            // A note insert (it takes and sends notes) dropped on an instrument goes into the track's note chain before it.
            if (machine != null && IsNoteInsert(inst) && PlaysNotes(machine))
            {
                return new DropPlan { Action = DropAction.InsertNote, Target = machine, Describe = $"Add {name} before {machine.Name}" };
            }

            if (t.View == DropView.Machine)
            {
                if (machine != null)
                {
                    if (WhyNotReplace(machine, inst) is { } why) return DropPlan.Refuse(why);
                    if (machine.DLL.Name == inst.MachineDLL.Name && ((machine as MachineCore)?.InstrumentName ?? "") == inst.Name)
                        return DropPlan.Refuse("It already is " + name + ".");
                    return new DropPlan { Action = DropAction.Replace, Target = machine, Describe = $"Replace {machine.Name} with {name}" };
                }
                return generator
                    ? new DropPlan { Action = DropAction.NewTrack, Describe = $"New track: {name}" }
                    : new DropPlan { Action = DropAction.AddMachine, Describe = $"Add {name}" };
            }

            if (inst.Type == InstrumentType.Control) return DropPlan.Refuse("Control machines go in the Machine View.");
            if (generator)
            {
                if (machine == null) return new DropPlan { Action = DropAction.NewTrack, Describe = $"New track: {name}" };
                if (IsClipPlayer(machine)) return DropPlan.Refuse($"{machine.Name} is an audio track. Drop on empty space for a new track.");
                if (!IsGenerator(machine)) return DropPlan.Refuse($"{machine.Name} isn't an instrument track. Drop on empty space for a new track.");
                if (machine.DLL.Name == inst.MachineDLL.Name && ((machine as MachineCore)?.InstrumentName ?? "") == inst.Name)
                    return DropPlan.Refuse("It already is " + name + ".");
                return new DropPlan { Action = DropAction.Replace, Target = machine, Describe = $"Replace {machine.Name} with {name}" };
            }
            // An effect.
            if (machine == null || !HasSequence(machine)) return DropPlan.Refuse("Drop on a track.");
            return PlanInsert(machine, inst, t);
        }

        /// <summary>The gear both takes and sends notes, read from its manifest (the catalogue's note_in and note_out) before any machine exists.</summary>
        static bool IsNoteInsert(IInstrument inst)
        {
            var m = SongModel.Manifest(inst.MachineDLL);
            return m is { NoteIn: true, NoteOut: true };
        }

        DropPlan PlanInsert(IMachine machine, IInstrument inst, DropTarget t)
        {
            string name = GearNames.Display(inst);
            var chain = FollowChain(machine, t.Branch);
            if (chain.Choices != null)
                return new DropPlan { Action = DropAction.InsertEffect, Target = machine, Choices = chain.Choices, ChoiceTitle = $"{name} on which branch of {machine.Name}?", Describe = $"{name} on {machine.Name}: which branch?" };
            if (chain.Last.IsControlMachine) return DropPlan.Refuse("A control machine has no audio to add an effect to.");
            return new DropPlan { Action = DropAction.InsertEffect, Target = machine, Describe = $"Add {name} after {chain.Last.Name}" };
        }

        DropPlan PlanPreset(BrowserDragData d, DropTarget t, IMachine machine)
        {
            var inst = FindInstrument(d.Id);
            if (WhyUnusable(inst, d.Id) is { } bad) return DropPlan.Refuse(bad);
            if (string.IsNullOrEmpty(d.Preset)) return DropPlan.Refuse("That preset has no name.");
            string name = GearNames.Display(inst);
            string label = $"{name}: {d.Preset}";
            bool generator = inst.Type == InstrumentType.Generator;
            bool Same(IMachine m) => m.DLL.Name == inst.MachineDLL.Name && ((m as MachineCore)?.InstrumentName ?? "") == inst.Name;

            if (t.View == DropView.Machine)
            {
                if (machine != null)
                {
                    if (Same(machine)) return new DropPlan { Action = DropAction.LoadPreset, Target = machine, Describe = $"Load {d.Preset} into {machine.Name}" };
                    if (WhyNotReplace(machine, inst) is { } why) return DropPlan.Refuse(why);
                    return new DropPlan { Action = DropAction.ReplaceWithPreset, Target = machine, Describe = $"Replace {machine.Name} with {label}" };
                }
                return generator
                    ? new DropPlan { Action = DropAction.NewTrackWithPreset, Describe = $"New track: {label}" }
                    : new DropPlan { Action = DropAction.AddMachine, Describe = $"Add {label}" };
            }
            if (machine == null)
                return generator && inst.Type != InstrumentType.Control
                    ? new DropPlan { Action = DropAction.NewTrackWithPreset, Describe = $"New track: {label}" }
                    : DropPlan.Refuse("Drop on a track.");
            if (Same(machine)) return new DropPlan { Action = DropAction.LoadPreset, Target = machine, Describe = $"Load {d.Preset} into {machine.Name}" };
            var inChain = ChainOf(machine).FirstOrDefault(Same);
            if (inChain != null) return new DropPlan { Action = DropAction.LoadPreset, Target = inChain, Describe = $"Load {d.Preset} into {inChain.Name}" };
            return DropPlan.Refuse($"{machine.Name} isn't a {name}. A preset loads into its own machine: drop it on that track, or on empty space for a new one.");
        }

        DropPlan PlanPattern(BrowserDragData d, DropTarget t, IMachine machine)
        {
            var pattern = FindPattern(d.Id, out string owner);
            if (pattern == null) return DropPlan.Refuse($"There is no pattern “{d.Id}”.");
            if (t.View == DropView.Machine) return DropPlan.Refuse("A pattern is a clip: drop it on the Arrangement or the Session.");
            if (machine == null) return DropPlan.Refuse($"Drop it on {owner}'s track.");
            if (machine != pattern.Machine) return DropPlan.Refuse($"“{pattern.Name}” belongs to {owner}: drop it on {owner}'s track.");
            if (!HasSequence(machine)) return DropPlan.Refuse($"{owner} has no track.");
            if (t.View == DropView.Session && (Layout == null || Layout.FirstTrackOf(machine) < 0)) return DropPlan.Refuse($"{owner} has no Session track.");
            return new DropPlan { Action = DropAction.PatternClip, Target = machine, Describe = $"Clip: {pattern.Name}" };
        }

        DropPlan PlanAudio(IReadOnlyList<BrowserDragData> items, DropTarget t, IMachine machine)
        {
            foreach (var i in items)
            {
                if (i.Kind != BrowserKind.Audio) return DropPlan.Refuse("Drop one kind at a time.");
                if (i.Id.StartsWith("wave:", StringComparison.Ordinal))
                {
                    if (WaveOf(i.Id) == null) return DropPlan.Refuse($"The song has no {i.Id}.");
                    continue;
                }
                if (!File.Exists(i.Id)) return DropPlan.Refuse($"No file at {i.Id}.");
                if (!AudioImporter.IsAudioFile(i.Id)) return DropPlan.Refuse($"{Path.GetFileName(i.Id)} isn't a type {Product.Name} imports: WAV, AIFF, FLAC, OGG or MP3.");
            }
            string what = items.Count == 1 ? Label(items[0]) : $"{items.Count} files";
            if (IsSampler(machine))
                return new DropPlan { Action = DropAction.LoadSample, Target = machine, Describe = $"Load {what} into {machine.Name}" };
            var layout = Layout;
            if (layout == null) return DropPlan.Refuse($"This {Product.Name} has no Session View, so no audio clips.");
            if (!layout.CanMakeAudioTracks) return DropPlan.Refuse("The Neo Audio machine isn't installed, so there are no audio tracks.");
            if (IsClipPlayer(machine) && HasSequence(machine))
                return new DropPlan { Action = DropAction.AudioClips, Target = machine, Describe = $"Clip: {what}" };
            return new DropPlan { Action = DropAction.NewAudioTrack, Describe = $"New audio track: {what}" };
        }

        static string Label(BrowserDragData d) => !string.IsNullOrEmpty(d.Label) ? d.Label : d.Id.StartsWith("wave:", StringComparison.Ordinal) ? d.Id : Path.GetFileNameWithoutExtension(d.Id);

        (string Path, MidiFile File) lastMidi;

        MidiFile ReadMidi(string path)
        {
            if (lastMidi.Path == path && lastMidi.File != null) return lastMidi.File;
            if (!File.Exists(path)) throw new ToolError($"No file at {path}.");
            try
            {
                var f = MidiFile.Read(path);
                lastMidi = (path, f);
                return f;
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw new ToolError($"{Path.GetFileName(path)} can't be read: {e.Message}.");
            }
        }

        /// <summary>The instrument a MIDI file's notes go on when it is dropped on empty space: the one asked for, else Neo Poly, else any instrument.</summary>
        IInstrument DefaultInstrument(string wanted)
        {
            bool Usable(IInstrument i) => i != null && i.Type == InstrumentType.Generator && !i.MachineDLL.IsMissing && !i.MachineDLL.IsCrashed;
            var i = FindInstrument(wanted);
            if (Usable(i)) return i;
            i = FindInstrument(DefaultInstrumentName);
            if (Usable(i)) return i;
            return core.Instruments.FirstOrDefault(Usable);
        }

        DropPlan PlanMidi(BrowserDragData d, DropTarget t, IMachine machine)
        {
            var file = ReadMidi(d.Id);
            if (file.Notes.Count == 0) return DropPlan.Refuse($"{Path.GetFileName(d.Id)} has no notes.");
            string what = $"{file.Notes.Count} notes";
            if (machine == null)
            {
                var inst = DefaultInstrument(t.DefaultInstrument);
                if (inst == null) return DropPlan.Refuse("No instrument is installed to play the notes.");
                return new DropPlan { Action = DropAction.MidiNewTrack, Describe = $"New track: {GearNames.Display(inst)} with {what}" };
            }
            if (!PlaysNotes(machine)) return DropPlan.Refuse($"{machine.Name} doesn't play notes. Drop on an instrument's track, or on empty space for a new one.");
            if (t.View != DropView.Machine && !HasSequence(machine)) return DropPlan.Refuse($"{machine.Name} has no track.");
            return new DropPlan { Action = DropAction.MidiNewPattern, Target = machine, Describe = $"New pattern on {machine.Name}: {what}" };
        }

        // ------------------------------------------------------------------ the track's chain

        /// <summary>The machines after a track's own machine that only it feeds: its effects in a row.</summary>
        IEnumerable<IMachine> ChainOf(IMachine start)
        {
            yield return start;
            var seen = new HashSet<IMachine> { start };
            var m = start;
            while (m.Outputs.Count == 1)
            {
                var d = m.Outputs[0].Destination;
                if (IsMaster(d) || d.Inputs.Count != 1 || d.DLL.Info.Type != MachineType.Effect || !seen.Add(d)) yield break;
                yield return d;
                m = d;
            }
        }

        /// <summary>
        /// Follows a track's machine through the effects only it feeds to the last one and what that feeds. Where one machine feeds
        /// several, the destination named in <paramref name="via"/> is followed; without one, the choices are returned.
        /// </summary>
        (IMachine Last, IMachineConnection Out, IReadOnlyList<string> Choices) FollowChain(IMachine start, IReadOnlyList<string> via)
        {
            var m = start;
            var seen = new HashSet<IMachine> { m };
            while (true)
            {
                var outs = m.Outputs;
                if (outs.Count == 0) return (m, null, null);
                IMachineConnection next;
                if (outs.Count == 1) next = outs[0];
                else
                {
                    string chosen = via?.FirstOrDefault(n => outs.Any(o => o.Destination.Name == n));
                    if (chosen == null) return (m, null, outs.Select(o => o.Destination.Name).ToList());
                    next = outs.First(o => o.Destination.Name == chosen);
                }
                var d = next.Destination;
                if (IsMaster(d) || d.Inputs.Count != 1 || d.DLL.Info.Type != MachineType.Effect || !seen.Add(d)) return (m, next, null);
                m = d;
            }
        }

        // ------------------------------------------------------------------ do

        /// <summary>Does what <see cref="Plan"/> says, as one undo step. A refusal or an error changes nothing and says why.</summary>
        public DropResult Drop(IReadOnlyList<BrowserDragData> items, DropTarget t)
        {
            var plan = Plan(items, t);
            if (plan.Choices != null)
                return new DropResult { Ok = false, Action = plan.Action, Message = plan.ChoiceTitle, Choices = plan.Choices };
            if (!plan.Allowed) return new DropResult { Ok = false, Action = DropAction.Refuse, Message = plan.Refusal };
            var stack = Song.ActionStack;
            int before = stack.Actions.Count();
            try
            {
                using (MachineEditing.Group(core))
                    return Perform(plan, items, t);
            }
            catch (Exception e) when (e is ToolError or InvalidOperationException or IOException)
            {
                // Whatever was done before the failure goes with it.
                if (stack.Actions.Count() > before && stack.CanUndo) stack.Undo();
                return new DropResult { Ok = false, Action = DropAction.Refuse, Message = e.Message };
            }
        }

        DropResult Perform(DropPlan plan, IReadOnlyList<BrowserDragData> items, DropTarget t)
        {
            var first = items[0];
            var machine = plan.Target;
            switch (plan.Action)
            {
                case DropAction.NewTrack: return DoNewTrack(FindInstrument(first.Id), null, t);
                case DropAction.NewTrackWithPreset: return DoNewTrack(FindInstrument(first.Id), first.Preset, t);
                case DropAction.AddMachine: return DoAdd(FindInstrument(first.Id), first.Kind == BrowserKind.Preset ? first.Preset : null, t);
                case DropAction.Replace: return DoReplace(machine, FindInstrument(first.Id), null);
                case DropAction.ReplaceWithPreset: return DoReplace(machine, FindInstrument(first.Id), first.Preset);
                case DropAction.LoadPreset:
                {
                    string note = LoadPreset(machine, first.Preset);
                    return Done(plan, machine, note ?? plan.Describe);
                }
                case DropAction.InsertEffect: return DoInsert(machine, FindInstrument(first.Id), t);
                case DropAction.InsertNote: return DoInsertNote(machine, FindInstrument(first.Id));
                case DropAction.NewAudioTrack:
                case DropAction.AudioClips: return DoAudio(plan, items, t);
                case DropAction.LoadSample: return DoSamples(machine, items);
                case DropAction.PatternClip: return DoPatternClip(machine, first, t);
                case DropAction.MidiNewTrack:
                case DropAction.MidiNewPattern: return DoMidi(plan, first, t);
            }
            return new DropResult { Ok = false, Message = "That can't be dropped here." };
        }

        DropResult Done(DropPlan plan, IMachine machine, string message, params IPattern[] patterns) =>
            new() { Ok = true, Action = plan.Action, Machine = machine, Message = message, Patterns = patterns };

        // ---- machines

        (float X, float Y) Spot(DropTarget t)
        {
            if (t.View == DropView.Machine) return MachinePlacement.Free(Song.Machines, t.X, t.Y);
            int n = Math.Max(0, Song.Machines.Count(m => !IsMaster(m)));
            return MachinePlacement.Free(Song.Machines, -0.8f + 0.2f * (n % 8), -0.5f + 0.25f * (n / 8 % 4));
        }

        /// <summary>A machine of the gear with a first pattern, at a place; nothing else.</summary>
        IMachine MakeMachine(IInstrument inst, float x, float y, bool pattern = true)
        {
            var before = Song.Machines.ToHashSet();
            (x, y) = MachinePlacement.Free(Song.Machines, x, y);
            Song.CreateMachine(inst.MachineDLL.Name, inst.Name, null, null, null, null, -1, x, y);
            var machine = Song.Machines.FirstOrDefault(m => !before.Contains(m)) ?? throw new ToolError($"{Product.Name} couldn't make {GearNames.Display(inst)}.");
            // Through an action, so that a redo makes the pattern again with the machine (a direct CreatePattern isn't in the history).
            if (pattern && machine.AllNonInputParameters().Any())
                Song.ActionStack.Do(new CreatePatternAction(machine, "00", Global.GeneralSettings.PatternLength > 0 ? Global.GeneralSettings.PatternLength : 16));
            return machine;
        }

        string LoadPreset(IMachine machine, string name)
        {
            var after = machine.GetPreset(name);
            if (after == null) return $"{machine.Name} has no preset “{name}”.";
            var before = new Preset(machine, false, true);
            string key = machine.Name;
            void Apply(Preset p)
            {
                var target = Song.MachinesList.FirstOrDefault(x => x.Name == key);
                if (target != null) p.Apply(target, true);
            }
            AgentActions.Do(core, new DelegateAction(() => Apply(after), () => Apply(before)));
            return null;
        }

        DropResult DoNewTrack(IInstrument inst, string preset, DropTarget t, bool withPattern = true)
        {
            var (x, y) = Spot(t);
            var machine = MakeMachine(inst, x, y, withPattern);
            var master = Song.Machines.FirstOrDefault(m => IsMaster(m));
            if (!machine.IsControlMachine && master != null && Song.CanConnectMachines(machine, master))
                Song.ConnectMachines(machine, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            if (!Song.Sequences.Any(s => s.Machine == machine)) Song.AddSequence(machine, Song.Sequences.Count);
            string note = preset != null ? LoadPreset(machine, preset) : null;
            IPattern clip = null;
            if (t.View == DropView.Arrangement && machine.Patterns.Count > 0)
            {
                var lane = Song.Sequences.First(s => s.Machine == machine);
                int time = Math.Max(0, t.Time);
                new ClipEditor(core).Place(lane, machine.Patterns[0], time);
                clip = machine.Patterns[0];
            }
            else if (t.View == DropView.Session && Layout != null && machine.Patterns.Count > 0 && t.Scene >= 0)
            {
                int track = Layout.FirstTrackOf(machine);
                if (track >= 0) { EnsureScenes(t.Scene); Layout.SetSlot(track, t.Scene, machine.Patterns[0]); clip = machine.Patterns[0]; }
            }
            else if (t.View == DropView.Machine && machine.Patterns.Count > 0 && !Song.Sequences.Any(s => s.Machine == machine && s.Events.Any()))
            {
                // No time in the Machine View: the pattern plays at the start, as a machine made there does in an empty song.
                if (Song.Machines.Count(m => !IsMaster(m)) == 1)
                    Song.Sequences.First(s => s.Machine == machine).SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, machine.Patterns[0]));
            }
            string text = $"New track {machine.Name} ({GearNames.Display(inst)}){(preset != null ? ", preset " + preset : "")}." + (note != null ? " " + note : "");
            return new DropResult { Ok = true, Action = preset != null ? DropAction.NewTrackWithPreset : DropAction.NewTrack, Machine = machine, Message = text, Patterns = clip != null ? new[] { clip } : Array.Empty<IPattern>() };
        }

        DropResult DoAdd(IInstrument inst, string preset, DropTarget t)
        {
            var machine = MakeMachine(inst, t.X, t.Y);
            string note = preset != null ? LoadPreset(machine, preset) : null;
            return new DropResult { Ok = true, Action = DropAction.AddMachine, Machine = machine, Message = $"Added {machine.Name}." + (note != null ? " " + note : "") };
        }

        DropResult DoReplace(IMachine machine, IInstrument inst, string preset)
        {
            string from = machine.DLL.Name;
            var now = MachineEditing.HotSwap(core, machine, inst.MachineDLL.Name, string.IsNullOrEmpty(inst.Name) ? null : inst.Name)
                      ?? throw new ToolError("The swap didn't leave a machine of that name.");
            string note = preset != null ? LoadPreset(now, preset) : null;
            return new DropResult
            {
                Ok = true, Action = preset != null ? DropAction.ReplaceWithPreset : DropAction.Replace, Machine = now,
                Message = $"{now.Name}: {from} replaced by {GearNames.Display(inst)}." + (note != null ? " " + note : ""),
            };
        }

        /// <summary>A note insert: made left of its instrument (no audio wire, no track of its own) and put last in the instrument's chain, right before it.</summary>
        DropResult DoInsertNote(IMachine instrument, IInstrument inst)
        {
            int have = core.NoteWires.State.ChainOf(instrument.Name).Count;
            var insert = MakeMachine(inst, ReBuzz.Core.NoteWires.NoteInsertLayout.X(instrument.Position.Item1, have, have + 1, 0.35f), instrument.Position.Item2, pattern: false);
            string why = core.NoteWires.AddInsert(instrument.Name, insert.Name);
            if (why != null) throw new ToolError(why);
            return new DropResult { Ok = true, Action = DropAction.InsertNote, Machine = insert, Message = $"{insert.Name} ({GearNames.Display(inst)}) added before {instrument.Name}." };
        }

        DropResult DoInsert(IMachine track, IInstrument inst, DropTarget t)
        {
            var (last, outConn, choices) = FollowChain(track, t.Branch);
            if (choices != null) throw new ToolError("Which branch? " + string.Join(", ", choices));
            var dest = outConn?.Destination;
            float x = last.Position.Item1 + 0.12f, y = last.Position.Item2 + 0.1f;
            if (dest != null) { x = (last.Position.Item1 + dest.Position.Item1) / 2; y = (last.Position.Item2 + dest.Position.Item2) / 2; }
            var effect = MakeMachine(inst, x, y);
            int amp = outConn?.Amp ?? 0x4000, pan = outConn?.Pan ?? 0x4000;
            if (outConn != null) Song.DisconnectMachines(outConn);
            if (!Song.CanConnectMachines(last, effect)) throw new ToolError($"{last.Name} can't feed {effect.Name}.");
            Song.ConnectMachines(last, effect, new[] { 0 }, new[] { 0 }, amp, pan);
            var to = dest ?? Song.Machines.FirstOrDefault(IsMaster);
            if (to != null && Song.CanConnectMachines(effect, to))
                Song.ConnectMachines(effect, to, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            return new DropResult { Ok = true, Action = DropAction.InsertEffect, Machine = effect, Message = $"{effect.Name} ({GearNames.Display(inst)}) added after {last.Name}." };
        }

        // ---- clips

        IPattern FindPattern(string id, out string owner)
        {
            owner = null;
            int slash = id.LastIndexOf('/');
            if (slash <= 0) return null;
            var m = Song.Machines.FirstOrDefault(x => x.Name == id[..slash]);
            owner = m?.Name;
            return m?.Patterns.FirstOrDefault(p => p.Name == id[(slash + 1)..]);
        }

        IWave WaveOf(string id)
        {
            if (!id.StartsWith("wave:", StringComparison.Ordinal) || !int.TryParse(id[5..], out int n)) return null;
            var waves = Song.Wavetable.Waves;
            return n >= 0 && n < waves.Count ? waves[n] : null;
        }

        ISequence LaneOf(IMachine machine, DropTarget t) =>
            t.Lane != null && t.Lane.Machine == machine ? t.Lane : Song.Sequences.FirstOrDefault(s => s.Machine == machine) ?? throw new ToolError($"{machine.Name} has no track.");

        void EnsureScenes(int scene)
        {
            var layout = Layout;
            layout.Ensure();
            while (layout.SceneCount <= scene) layout.InsertScene(layout.SceneCount);
        }

        DropResult DoPatternClip(IMachine machine, BrowserDragData d, DropTarget t)
        {
            var pattern = FindPattern(d.Id, out _);
            if (t.View == DropView.Session)
            {
                var layout = Layout;
                int track = layout.FirstTrackOf(machine);
                int scene = t.Scene >= 0 ? t.Scene : FirstEmptyScene(layout, track);
                EnsureScenes(scene);
                layout.SetSlot(track, scene, pattern);
                return new DropResult { Ok = true, Action = DropAction.PatternClip, Machine = machine, Patterns = new[] { pattern }, Message = $"“{pattern.Name}” in scene {scene + 1} of {machine.Name}." };
            }
            int time = Math.Max(0, t.Time);
            if (new ClipEditor(core).Place(LaneOf(machine, t), pattern, time) is { } why) throw new ToolError(why);
            return new DropResult { Ok = true, Action = DropAction.PatternClip, Machine = machine, Patterns = new[] { pattern }, Message = $"“{pattern.Name}” placed on {machine.Name} at tick {time}." };
        }

        static int FirstEmptyScene(SessionLayout layout, int track)
        {
            int scene = 0;
            while (track >= 0 && layout.PatternAt(track, scene) != null) scene++;
            return scene;
        }

        // ---- audio

        string ImportPath(BrowserDragData d, List<string> temps)
        {
            if (!d.Id.StartsWith("wave:", StringComparison.Ordinal)) return d.Id;
            var wave = WaveOf(d.Id) ?? throw new ToolError($"The song has no {d.Id}.");
            var layer = wave.Layers.FirstOrDefault() ?? throw new ToolError($"{wave.Name} holds no audio.");
            string dir = Path.Combine(Path.GetTempPath(), "ReBuzzDrop");
            Directory.CreateDirectory(dir);
            string safe = string.Concat((string.IsNullOrWhiteSpace(wave.Name) ? "Wave" : wave.Name).Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
            string path = Path.Combine(dir, $"{safe}-{Guid.NewGuid():N}.wav");
            WaveTools.Save(layer, path);
            temps.Add(path);
            return path;
        }

        DropResult DoAudio(DropPlan plan, IReadOnlyList<BrowserDragData> items, DropTarget t)
        {
            var layout = Layout;
            layout.Ensure();
            var temps = new List<string>();
            try
            {
                var machine = plan.Action == DropAction.AudioClips ? plan.Target : null;
                int track = machine == null ? -1 : layout.FirstTrackOf(machine);
                if (machine != null && track < 0) throw new ToolError($"{machine.Name} has no Session track.");
                var made = new List<IPattern>();
                int time = Math.Max(0, t.Time);
                int scene = t.Scene >= 0 ? t.Scene : machine != null && t.View == DropView.Session ? FirstEmptyScene(layout, track) : 0;
                if (t.View == DropView.Machine && machine != null) scene = FirstEmptyScene(layout, track);
                bool arrangement = t.View == DropView.Arrangement;
                for (int i = 0; i < items.Count; i++)
                {
                    string path = ImportPath(items[i], temps);
                    IPattern clip;
                    if (arrangement)
                    {
                        // Made in a scene of its own, which then goes (the pattern stays, on its lane), as audio_import does.
                        int scratch = layout.SceneCount;
                        layout.InsertScene(scratch);
                        clip = layout.ImportAudio(track, scratch, path, out string error) ?? throw new ToolError(error ?? "Couldn't import it.");
                        var m = (MachineCore)clip.Machine;
                        track = layout.FirstTrackOf(m);
                        layout.SetSlot(track, scratch, null);
                        layout.RemoveScene(scratch);
                        machine = m;
                        if (!Song.Sequences.Any(s => s.Machine == m)) Song.AddSequence(m, Song.Sequences.Count);
                        var lane = t.Lane != null && t.Lane.Machine == m ? t.Lane : Song.Sequences.First(s => s.Machine == m);
                        if (new ClipEditor(core).Place(lane, clip, time) is { } why) throw new ToolError(why);
                        time += Math.Max(1, clip.Length);
                    }
                    else
                    {
                        EnsureScenes(scene + i);
                        clip = layout.ImportAudio(track, scene + i, path, out string error) ?? throw new ToolError(error ?? "Couldn't import it.");
                        machine = clip.Machine;
                        track = layout.FirstTrackOf(machine);
                    }
                    made.Add(clip);
                }
                string where = arrangement ? $"from tick {Math.Max(0, t.Time)}" : $"from scene {scene + 1}";
                return new DropResult
                {
                    Ok = true, Action = plan.Action, Machine = machine, Patterns = made,
                    Message = $"{(plan.Action == DropAction.NewAudioTrack ? "New audio track " : "")}{machine.Name}: {made.Count} clip{(made.Count == 1 ? "" : "s")} {where}.",
                };
            }
            finally
            {
                foreach (var f in temps) { try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
        }

        DropResult DoSamples(IMachine machine, IReadOnlyList<BrowserDragData> items)
        {
            var mc = (MachineCore)machine;
            var temps = new List<string>();
            try
            {
                var used = new List<string>();
                foreach (var d in items)
                {
                    int zone = FirstEmptyZone(mc) ?? throw new ToolError($"All 16 zones of {machine.Name} hold a sample.");
                    string path = ImportPath(d, temps);
                    var done = WaveTools.Import(core, path, null, Path.GetFileNameWithoutExtension(path), null, false, false, Array.Empty<string>());
                    var p = ZoneWave(mc, zone);
                    if (p.GetValue(0) != done.Index + 1) AgentActions.SetParameter(core, mc, p, 0, done.Index + 1);
                    used.Add($"zone {zone} = slot {done.Index + 1}");
                }
                return new DropResult { Ok = true, Action = DropAction.LoadSample, Machine = machine, Message = $"{machine.Name}: {string.Join(", ", used)}." };
            }
            finally
            {
                foreach (var f in temps) { try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
        }

        static IParameter ZoneWave(MachineCore m, int zone) =>
            m.AllNonInputParameters().FirstOrDefault(p => p.Name == $"Z{zone} wave") ?? throw new ToolError($"{m.Name} has no zone {zone}.");

        /// <summary>The first zone whose wave is off or an empty slot, 1-based; null when all 16 hold a sample.</summary>
        int? FirstEmptyZone(MachineCore m)
        {
            var waves = Song.Wavetable.Waves;
            for (int z = 1; z <= 16; z++)
            {
                int v = ZoneWave(m, z).GetValue(0);
                if (v == 0 || (v <= waves.Count && waves[v - 1] == null)) return z;
            }
            return null;
        }

        // ---- MIDI

        DropResult DoMidi(DropPlan plan, BrowserDragData d, DropTarget t)
        {
            var file = ReadMidi(d.Id);
            int tpb = SongModel.Tpb(core), bpb = SongModel.BeatsPerBar(core);
            int bar = tpb * bpb;
            int ticks = Math.Clamp((int)Math.Ceiling(file.LengthBeats * tpb / bar) * bar, bar, Math.Max(bar, MaxPatternTicks / bar * bar));
            string name = Path.GetFileNameWithoutExtension(d.Id);
            IMachine machine;
            IPattern pattern;
            if (plan.Action == DropAction.MidiNewTrack)
                machine = DoNewTrack(DefaultInstrument(t.DefaultInstrument), null, t, false).Machine;
            else machine = plan.Target;

            string patternName = machine.GetNewPatternName();
            Song.ActionStack.Do(new CreatePatternAction(machine, patternName, ticks));
            pattern = machine.Patterns.First(p => p.Name == patternName);
            var added = file.Notes.Where(n => (int)Math.Round(n.Start * tpb) < ticks)
                .Select(n => new NoteEvent(NoteList.Time(n.Start, tpb), Math.Max(1, Math.Min(NoteList.Time(n.Length, tpb), ticks * NoteList.TimeBase - NoteList.Time(n.Start, tpb))), n.Pitch, Math.Max(1, n.Velocity)))
                .ToList();
            int cut = file.Notes.Count - added.Count;
            var problems = new List<string>();
            var input = MachineNotes.For((MachineCore)machine);
            added.Sort(NoteList.Order);
            MachineNotes.Replace(core, pattern, input, new List<NoteEvent>(), added, problems);

            PlaceClip(machine, pattern, t);

            string info = (file.Bpm != null ? $"{file.Bpm:0.##} BPM" : null) + (file.Meter is { } m ? $"{(file.Bpm != null ? ", " : "")}{m.Beats}/{m.Unit}" : "");
            string text = $"{added.Count} notes in “{pattern.Name}” on {machine.Name}" + (info.Length > 0 ? $" (the file's {info}, not applied)" : "") +
                          (cut > 0 ? $"; {cut} past {ticks} ticks left out" : "") + ".";
            return new DropResult { Ok = true, Action = plan.Action, Machine = machine, Patterns = new[] { pattern }, Message = text };
        }

        void PlaceClip(IMachine machine, IPattern pattern, DropTarget t)
        {
            if (t.View == DropView.Arrangement)
            {
                var lane = LaneOf(machine, t);
                if (new ClipEditor(core).Place(lane, pattern, Math.Max(0, t.Time)) is { } why) throw new ToolError(why);
            }
            else if (t.View == DropView.Session && Layout != null)
            {
                int track = Layout.FirstTrackOf(machine);
                if (track < 0) return;
                int scene = t.Scene >= 0 ? t.Scene : FirstEmptyScene(Layout, track);
                EnsureScenes(scene);
                Layout.SetSlot(track, scene, pattern);
            }
            else if (t.View == DropView.Machine && HasSequence(machine))
            {
                var lane = Song.Sequences.First(s => s.Machine == machine);
                if (!lane.Events.Any()) lane.SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, pattern));
            }
        }
    }

    /// <summary>A gear item's name as the browser and the machine list show it.</summary>
    internal static class GearNames
    {
        public static string Display(IInstrument instrument)
        {
            if (instrument.Name.Length > 0)
            {
                string name = Path.GetFileName(instrument.Name);
                if (instrument.Name.StartsWith("VST3/")) name += "(vst3)";
                return name;
            }
            var dll = instrument.MachineDLL.Name;
            return instrument.MachineDLL.IsOutOfProcess ? dll + " (32-bit)" : dll;
        }
    }
}
