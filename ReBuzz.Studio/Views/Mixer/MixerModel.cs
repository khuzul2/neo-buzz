// Fork (docs/specs/ux-overhaul.md §11, MX1): what the mixer shows, as a list: one strip per track, group and return of the track model
// (MT1: the path from each root generator to the Master is a track), then the Master. The Master's own chain (the machines every
// track passes through before the Master) is the Master strip's inserts. A group's members come before it, as the signal flows, and a
// folded group leaves them out. The Session View's own returns (its Return tracks menu) are returns too.
//
// The strips act on what the Session mixer always acted on: the machine's output connections (SessionMixer), so the Machine View, the
// Session View, the Arrangement's headers and the Inspector all show the same numbers. A strip's machine is the head of its entry: a
// track's generator, a group's or a return's own machine.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Core.Tracks;
using ReBuzz.Studio.Views.Machines;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Views.Mixer
{
    /// <summary>One strip of the mixer, as the model has it.</summary>
    internal sealed class StripSpec
    {
        public StripKind Kind { get; init; }

        /// <summary>"track:Bass", "group:Drum bus", "return:Verb", "master:Master" (the track model's ids).</summary>
        public string Id { get; init; }

        /// <summary>The machine the strip's controls act on (the Master machine for the Master).</summary>
        public IMachine Machine { get; init; }

        public string Name { get; init; }

        /// <summary>The second line: "track", "track · in Drum Bus", "group ▾ 2 tracks", "return A", "master chain".</summary>
        public string Subtitle { get; init; }

        /// <summary>The machines after the track's generator, in signal order (a group's or a return's own machine is its first).</summary>
        public IReadOnlyList<IMachine> Inserts { get; init; } = Array.Empty<IMachine>();

        /// <summary>How many groups this strip's entry is inside (for the indent of a member).</summary>
        public int Depth { get; init; }

        /// <summary>A group: how many tracks it folds (its members, nested ones included).</summary>
        public int Members { get; init; }

        public bool Folded { get; init; }

        public IReadOnlyList<string> MemberIds { get; init; } = Array.Empty<string>();

        public string ParentId { get; init; }

        /// <summary>
        /// Fork (docs/specs/multi-output.md): an <see cref="StripKind.Output"/> strip's wire: the extra output of a machine with several that is wired
        /// somewhere. The strip's fader, pan, mute and solo are the wire's own; <see cref="Machine"/> is the wire's source (its colour, its selection).
        /// </summary>
        public IMachineConnection Wire { get; init; }
    }

    internal sealed class MixerModel
    {
        public IReadOnlyList<StripSpec> Strips { get; private init; } = Array.Empty<StripSpec>();

        /// <summary>The returns, in strip order: what a track's sends go to.</summary>
        public IReadOnlyList<IMachine> Returns { get; private init; } = Array.Empty<IMachine>();

        /// <summary>The returns as a set, for the engine side (a send is not a track's main output).</summary>
        public ICollection<IMachine> ReturnSet { get; private init; } = new HashSet<IMachine>();

        public StripSpec Master => Strips.LastOrDefault(s => s.Kind == StripKind.Master);

        public int Tracks => Strips.Count(s => s.Kind == StripKind.Track);
        public int Groups => Strips.Count(s => s.Kind == StripKind.Group);

        public string Signature { get; private init; } = "";

        public static string ReturnLetter(int r) => r < 26 ? ((char)('A' + r)).ToString() : (r + 1).ToString();

        /// <summary>The returns a track's sends go to: the model's, then the Session View's own return tracks (its menu).</summary>
        public static List<IMachine> ReturnsOf(ReBuzzCore core)
        {
            var song = core.SongCore;
            var model = MachineTracksLive.For(core).Model;
            var doc = core.SessionLayout?.Document;
            var returns = new List<IMachine>();
            foreach (var e in model.Entries.Where(e => e.Kind == TrackKind.Return))
                if (SongTracks.MachineOf(e.Head) is { } head) returns.Add(head);
            // An effect fed by one track only is a branch of that track to the model, so the menu's returns are added.
            if (doc != null)
                foreach (string name in doc.Returns)
                    if (song.Machines.FirstOrDefault(m => m.Name == name) is { } extra && !returns.Contains(extra) && extra.DLL?.Info?.Type == MachineType.Effect)
                        returns.Add(extra);
            return returns;
        }

        /// <summary>The mixer's strips for a song now. <paramref name="folded"/>: the ids of the groups folded to one strip.</summary>
        public static MixerModel Build(ReBuzzCore core, ISet<string> folded)
        {
            var song = core.SongCore;
            var model = MachineTracksLive.For(core).Model;
            var doc = core.SessionLayout?.Document;
            var strips = new List<StripSpec>();
            var seen = new HashSet<TrackEntry>();

            IMachine M(TrackNode n) => SongTracks.MachineOf(n);

            int CountTracks(TrackEntry e) => e.Kind == TrackKind.Track ? 1 : e.Members.Sum(CountTracks);

            // Fork (docs/specs/multi-output.md): the extra outputs of the machines of a strip that are wired somewhere, a strip each, right after it.
            var returnSet = new HashSet<IMachine>(ReturnsOf(core));
            void EmitOutputs(StripSpec of)
            {
                var machines = new List<IMachine>();
                if (of.Machine != null) machines.Add(of.Machine);
                machines.AddRange(of.Inserts.Where(m => m != null && !machines.Contains(m)));
                foreach (var m in machines)
                {
                    var wires = SessionMixer.ExtraOutputs(doc, m, returnSet);
                    foreach (var wire in wires)
                    {
                        string output = m.GetChannelName(false, wire.SourceChannel);
                        if (string.IsNullOrWhiteSpace(output)) output = "Output " + (wire.SourceChannel + 1);
                        bool shared = wires.Count(w => w.SourceChannel == wire.SourceChannel) > 1;
                        strips.Add(new StripSpec
                        {
                            Kind = StripKind.Output, Id = "output:" + m.Name + ">" + wire.Destination.Name, Machine = m, Wire = wire,
                            Name = m.Name + " · " + output, Depth = of.Depth, ParentId = of.Id,
                            Subtitle = (shared ? "output → " + wire.Destination.Name : "output " + (wire.SourceChannel + 1)) + " of " + m.OutputChannelCount,
                        });
                    }
                }
            }

            void Emit(TrackEntry e)
            {
                if (!seen.Add(e)) return;
                if (e.Kind == TrackKind.Group)
                {
                    bool fold = folded.Contains(e.Id);
                    if (!fold) foreach (var member in e.Members) Emit(member);
                    int tracks = CountTracks(e);
                    strips.Add(new StripSpec
                    {
                        Kind = StripKind.Group, Id = e.Id, Machine = M(e.Head), Name = e.Name, Depth = e.Depth, Members = tracks, Folded = fold,
                        Subtitle = $"group {(fold ? "▸" : "▾")} {tracks} track{(tracks == 1 ? "" : "s")}",
                        Inserts = e.Machines.Select(M).Where(m => m != null).ToList(),
                        MemberIds = e.Members.Select(x => x.Id).ToList(), ParentId = e.Parent?.Id,
                    });
                    EmitOutputs(strips[^1]);
                    return;
                }
                if (e.Kind == TrackKind.Track)
                {
                    strips.Add(new StripSpec
                    {
                        Kind = StripKind.Track, Id = e.Id, Machine = M(e.Head), Name = e.Name, Depth = e.Depth,
                        Subtitle = e.Parent != null ? "track · in " + e.Parent.Name : "track",
                        Inserts = e.Machines.Skip(1).Select(M).Where(m => m != null).ToList(), ParentId = e.Parent?.Id,
                    });
                    EmitOutputs(strips[^1]);
                }
            }

            // Tracks and groups in the order of the model, a group after its members; a member of a folded group is skipped by its group.
            foreach (var e in model.Entries.Where(e => e.Parent == null && e.Kind is TrackKind.Track or TrackKind.Group))
                Emit(e);

            var returns = ReturnsOf(core);
            for (int r = 0; r < returns.Count; r++)
            {
                var entry = model.EntryOf(model.Graph.Nodes.FirstOrDefault(n => n.Tag == returns[r]));
                strips.Add(new StripSpec
                {
                    Kind = StripKind.Return, Id = "return:" + returns[r].Name, Machine = returns[r], Name = returns[r].Name,
                    Subtitle = "return " + ReturnLetter(r),
                    Inserts = (entry?.Machines.Select(M).Where(m => m != null).ToList() as IReadOnlyList<IMachine>) ?? new[] { returns[r] },
                });
                EmitOutputs(strips[^1]);
            }

            var master = song.Machines.FirstOrDefault(m => m.DLL?.Info?.Type == MachineType.Master);
            var chain = model.MasterChain;
            strips.Add(new StripSpec
            {
                Kind = StripKind.Master, Id = "master:Master", Machine = master, Name = "Master",
                Subtitle = chain != null ? "master chain" : "master",
                Inserts = chain?.Machines.Select(M).Where(m => m != null).ToList() ?? (IReadOnlyList<IMachine>)Array.Empty<IMachine>(),
            });

            string signature = string.Join(";", strips.Select(s => s.Id + ">" + string.Join(",", s.Inserts.Select(i => i.Name)) + (s.Folded ? "!" : "")));
            return new MixerModel { Strips = strips, Returns = returns, ReturnSet = new HashSet<IMachine>(returns), Signature = signature };
        }
    }

    /// <summary>A track's colour as the whole studio draws it (the palette colour the track holds, else the machine's identity colour).</summary>
    internal static class TrackColour
    {
        static TrackColour() => Install();

        /// <summary>Tells the colour store which palette colours are too close to a state colour (the studio's own test): called when the window opens.</summary>
        public static void Install() =>
            TrackColourStore.ReservedForState = argb => ReBuzz.Studio.Shell.Design.StudioTokens.IsReservedForState(Color.FromUInt32(argb));

        /// <summary>The palette index of a machine's track colour, or -1.</summary>
        public static int IndexOf(IMachine machine) =>
            machine?.Graph?.Buzz is ReBuzzCore core && core.TrackColours is { } store ? store.IndexOf(machine) : -1;

        /// <summary>The colour of a machine's track: its palette colour, else its identity colour (null for the Master).</summary>
        public static Color? Of(IMachine machine)
        {
            int index = IndexOf(machine);
            if (index >= 0)
            {
                var choices = SessionPalette.ColorChoices;
                if (choices.Length > 0) return choices[index % choices.Length].Color;
            }
            return MachineIdentity.ColourOf(machine);
        }

        public static IBrush BrushOf(IMachine machine) => Of(machine) is { } c ? SessionPalette.BrushOf(c) : null;
    }
}
