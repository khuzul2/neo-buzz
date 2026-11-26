using BuzzGUI.Common;
using BuzzGUI.Common.Actions;
using BuzzGUI.Common.Settings;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;


namespace ReBuzz.Core.Actions.GraphActions
{
    internal class DeleteMachinesAction : BuzzAction, IHistoryAction
    {
        readonly List<MachineInfoRef> deleteMachineDatas = new List<MachineInfoRef>();
        // Fork (0.6.2): what was deleted, so the undo can report what stands for it now.
        // 0.6.3: with each clip's engine columns (MIDI columns, session takes), which the undo puts back.
        // Fork (fix/long-session-slowdown): held weakly. The undo history keeps this action for as long as its cap allows,
        // and it counts an action as 256 bytes: held strongly, every deleted machine (its buffers, its machine object, a
        // Pedal machine's delay lines) stayed in memory with it, hundreds of MB over a long session. The undo makes the
        // machine again from the saved data; the old objects are only the keys views remap their selections from, and a
        // view that still holds one keeps it alive.
        sealed class Gone
        {
            public WeakReference<IMachine> Machine;
            public Dictionary<string, WeakReference<IClip>> Clips;
            public Dictionary<int, WeakReference<ISequence>> Sequences;
            public Dictionary<string, EngineColumns> Columns;
        }

        static WeakReference<T> Weak<T>(T target) where T : class => new WeakReference<T>(target);

        static T Target<T>(WeakReference<T> weak) where T : class => weak.TryGetTarget(out var t) ? t : null;

        readonly Dictionary<MachineInfoRef, Gone> deleted = new Dictionary<MachineInfoRef, Gone>();

        private readonly ReBuzzCore buzz;
        private readonly IUiDispatcher dispatcher;
        private readonly EngineSettings engineSettings;

        public DeleteMachinesAction(
            ReBuzzCore buzz,
            IEnumerable<IMachine> m,
            IUiDispatcher dispatcher,
            EngineSettings engineSettings)
        {
            this.engineSettings = engineSettings;
            this.buzz = buzz;
            this.dispatcher = dispatcher;

            foreach (var mac in m)
            {
                var machine = mac as MachineCore;
                MachineInfoRef machineData = new MachineInfoRef(machine);
                deleteMachineDatas.Add(machineData);
                SaveConnections(machine, machineData);

                // Save sequences
                foreach (var seq in buzz.SongCore.Sequences.Where(s => s.Machine == machine).OrderBy(s => buzz.SongCore.Sequences.IndexOf(s)))
                {
                    var events = seq.Events;
                    int index = buzz.SongCore.Sequences.IndexOf(seq);
                    machineData.AddSequence(index, events);
                }

                // Save clip colors
                // Fork (0.6.2): no sequence editor (headless, tests): no clip colours to keep.
                // Fork (engine-split.md S7): through ClipColours.
                var associations = ClipColours.Current;
                foreach (var clip in associations?.Clips.Where(pa => pa.Machine == machine) ?? Enumerable.Empty<IClip>())
                {
                    if (associations.TryGet(clip, out var colorIndex))
                    {
                        // Save clip color
                        machineData.clipAssociations.Add(clip.Name, colorIndex);
                    }
                }
            }
        }

        // Fork (fix/long-session-slowdown): what the history holds for this step. It counted every action as 256 bytes, but a
        // deleted machine's saved parameters (a ParameterCore each, with its per-track value tables) are about 3.5 KB each
        // (measured: a 95-parameter machine, 0.33 MB), so the history's cap of 256 MB held 100 times as much as it said.
        const long BytesPerSavedParameter = 3600;

        string IHistoryAction.HistoryName => null;

        IEnumerable<KeyValuePair<object, long>> IHistoryAction.HistoryBlobs => null;

        long IHistoryAction.HistoryBytes
        {
            get
            {
                long bytes = 256;
                foreach (var d in deleteMachineDatas)
                {
                    bytes += (d.Data?.Length ?? 0) + (d.ClipEditorData?.Length ?? 0);
                    foreach (var g in d.parameterGroups) bytes += g.ParametersList.Count * BytesPerSavedParameter;
                    foreach (var seq in d.sequences.Values) bytes += seq.Count() * 64L;
                }
                return bytes;
            }
        }

        private void SaveConnections(MachineCore machine, MachineInfoRef machineData)
        {
            foreach (var connection in machine.AllOutputs)
            {
                machineData.disconnectActions.Add( new DisconnectMachinesAction(buzz, connection, dispatcher, engineSettings) );
            }

            foreach (var connection in machine.AllInputs)
            {
                machineData.disconnectActions.Add(new DisconnectMachinesAction(buzz, connection, dispatcher, engineSettings));
            }
        }

        protected override void DoAction()
        {
            lock (ReBuzzCore.AudioLock)
            {
                // Remove Connections
                foreach (var machineData in deleteMachineDatas)
                {
                    var machine = buzz.SongCore.MachinesList.FirstOrDefault(x => x.Name == machineData.Name);
                    if (machine != null)
                    {
                        //foreach (var c in machineData.connections)
                        //{
                        //    RemoveConnection(machine, c);
                        //}

                        foreach (var c in machineData.disconnectActions)
                        {
                            c.Do();
                        }
                    }
                }

                // Fork (0.6.3): capture what goes before anything is removed: sequence indices as
                // they are now (removing one machine renumbers the sequences after its own), and
                // the engine columns before the loop below deletes those of the machine.
                deleted.Clear();
                foreach (var machineData in deleteMachineDatas)
                {
                    var machine = buzz.SongCore.MachinesList.FirstOrDefault(x => x.Name == machineData.Name);
                    if (machine == null) continue;
                    var clips = machine.Clips.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First());
                    deleted[machineData] = new Gone
                    {
                        Machine = Weak<IMachine>(machine),
                        Columns = clips.ToDictionary(p => p.Key, p => EngineColumns.Capture(p.Value)),
                        Sequences = buzz.SongCore.Sequences.Where(s => s.Machine == machine).ToDictionary(s => buzz.SongCore.Sequences.IndexOf(s), s => Weak<ISequence>(s)),
                        Clips = clips.ToDictionary(p => p.Key, p => Weak<IClip>(p.Value)),
                    };
                }

                // Remove machines
                foreach (var machineData in deleteMachineDatas)
                {
                    var machine = buzz.SongCore.MachinesList.FirstOrDefault(x => x.Name == machineData.Name);
                    if (machine != null)
                    {

                        // Remove clip colors
                        var associations = ClipColours.Current;
                        foreach (var clip in associations?.Clips.Where(pa => pa.Machine == machine).ToList() ?? new List<IClip>())
                        {
                            if (associations.TryGet(clip, out _))
                            {
                                // remove from dictionary
                                associations.Remove(clip);
                            }
                        }

                        // Delete all clip Columns that are linked to deleted machine
                        // Fork (0.6.3): linked to the deleted machine. Upstream compared with the
                        // loop's machine, deleting every clip's columns of its own machine.
                        foreach (var m in buzz.SongCore.MachinesList)
                        {
                            foreach (var p in m.Clips)
                            {
                                for (int i = 0; i < p.Columns.Count; i++)
                                {
                                    if (p.Columns[i].Machine == machine)
                                    {
                                        p.DeleteColumn(i);
                                        i--;
                                    }
                                }
                            }
                        }

                        // Remove machine
                        buzz.RemoveMachine(machine);
                    }
                }
            }
        }

        protected override void UndoAction()
        {
            // Fork (0.6.3): the machines made again, to report those (not whichever has the name).
            var madeAgain = new Dictionary<MachineInfoRef, MachineCore>();

            // Create machines
            foreach (var machineData in deleteMachineDatas.OrderBy(md => md.sequences.Count > 0 ? md.sequences.Keys.OrderBy(o => o).First() : 0))
            {
                var machine = buzz.CreateMachine(
                    machineData.MachineLib, machineData.Instrument, machineData.Name, machineData.Data,
                    machineData.ClipEditorDllName, machineData.ClipEditorData, machineData.TrackCount,
                    machineData.Pos.Item1, machineData.Pos.Item2, machineData.ClipEditorMachineName);

                // Ensure name is correct
                buzz.RenameMachine(machine, machineData.Name);

                machine.AttributesList = machineData.Attributes;
                if (machine.DLL.IsMissing)
                {
                    // machine.ParameterGroupsList.Add(machineData.GetGroup(machine, 0));
                    machine.ParameterGroupsList.Add(machineData.GetGroup(machine, 1));
                    machine.ParameterGroupsList.Add(machineData.GetGroup(machine, 2));
                }
                else
                {
                    // Restore parameter values
                    machineData.CopyParameters(machine, 0, 0);
                    machineData.CopyParameters(machine, 1, 0);
                    for (int i = 0; i < machine.TrackCount; i++)
                    {
                        machineData.CopyParameters(machine, 2, i);
                    }
                }

                madeAgain[machineData] = machine;

                // Create Clips
                foreach (var clip in machineData.clips)
                {
                    machine.CreateClip(clip.Name, clip.Lenght);
                }

                // Fork (0.6.3): their engine columns (MIDI columns, session takes).
                if (deleted.TryGetValue(machineData, out var gone))
                {
                    var songMachines = buzz.SongCore.MachinesList.Cast<IMachine>().ToList();
                    foreach (var columns in gone.Columns)
                    {
                        var again = machine.ClipsList.FirstOrDefault(p => p.Name == columns.Key);
                        if (again != null && again.Columns.Count == 0)
                            columns.Value.RestoreInto(again, songMachines);
                    }
                }

                // add clipcolors
                foreach (var pa in machineData.clipAssociations)
                {
                    var clip = machine.ClipsList.FirstOrDefault(p => p.Name == pa.Key);
                    if (clip != null)
                    {
                        ClipColours.Current?.Add(clip, pa.Value);
                    }
                }

                var sequences = machineData.sequences;

                if (machine != null)
                {
                    foreach (var seq in sequences)
                    {
                        int index = seq.Key;
                        buzz.SongCore.AddSequence(machine, index);
                        var seqAdded = buzz.SongCore.SequencesList.ElementAt(index);

                        foreach (var eventItem in seq.Value)
                        {
                            var seqEvent = eventItem.Value;
                            // Get clip
                            var clip = machine.ClipsList.FirstOrDefault(p => p.Name == seqEvent.ClipName);
                            // Create new seqence event
                            SequenceEvent sequenceEvent = seqEvent.ToEvent(clip);
                            seqAdded.SetEvent(eventItem.Key, sequenceEvent);
                        }
                    }
                }
            }


            // Create connections

            foreach (var machineData in deleteMachineDatas)
            {
                foreach (var c in machineData.disconnectActions)
                {
                    c.Undo();
                }
            }

            // Fork (0.6.2): report what stands for the deleted objects now (machine, then its clips and sequences).
            foreach (var machineData in deleteMachineDatas)
            {
                if (!deleted.TryGetValue(machineData, out var old)) continue;
                if (!madeAgain.TryGetValue(machineData, out var now) || !buzz.SongCore.MachinesList.Contains(now)) continue;
                // An object that was collected has no view holding it: nothing to remap.
                var oldMachine = Target(old.Machine);
                if (oldMachine != null) BuzzGUI.Common.Actions.Restorations.Report(oldMachine, now);
                foreach (var p in old.Clips)
                {
                    var oldClip = Target(p.Value);
                    if (oldClip != null) BuzzGUI.Common.Actions.Restorations.Report(oldClip, now.ClipsList.FirstOrDefault(x => x.Name == p.Key));
                }
                foreach (var s in old.Sequences)
                {
                    var oldSequence = Target(s.Value);
                    if (oldSequence != null && s.Key < buzz.SongCore.SequencesList.Count && buzz.SongCore.SequencesList[s.Key].Machine == now)
                        BuzzGUI.Common.Actions.Restorations.Report(oldSequence, buzz.SongCore.SequencesList[s.Key]);
                }
            }
            deleted.Clear();
        }
    }
}
