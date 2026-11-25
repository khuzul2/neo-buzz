// Fork (docs/specs/studio-mvp.md S5): the Session View's mixer, ported from the WPF view's
// SessionMixerView.cs: a strip under each column (sends, pan, fader with meter, crossfader side,
// mute, solo, cue), return and group columns, the master strip under the scene column with the
// crossfader. The strips edit the machine graph through SessionMixer and poll it, so they follow
// the Machine View live. The fader, pan, sends and meters are drawn in code.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Mixer;

namespace ReBuzz.Studio.Views.Session
{
    using Control = Avalonia.Controls.Control;

    internal sealed partial class SessionView
    {

        enum ColumnKind { Track, Group, Return }

        /// <summary>One column of the grid and mixer: a track, a group (before its members) or a return.</summary>
        sealed class Column
        {
            public ColumnKind Kind { get; init; }
            public int Track { get; init; } = -1;
            public int Return { get; init; } = -1;
            public IMachine Machine { get; init; }
            public IMachineGroup Group { get; init; }
            public IReadOnlyList<int> Members { get; init; } = Array.Empty<int>();
        }

        readonly List<Column> columns = new List<Column>();
        int[] trackColumn = Array.Empty<int>();
        readonly List<MixerStripView> strips = new List<MixerStripView>();
        readonly List<GroupSlotCell> groupSlots = new List<GroupSlotCell>();
        readonly List<GroupHeader> groupHeaders = new List<GroupHeader>();
        IReadOnlyList<IMachine> returns = Array.Empty<IMachine>();
        string structureKey = "";
        IDisposable ticker;
        readonly Control mixerSection;
        readonly Control masterSection;       // the master strip, under the scene column
        // The meters move on the one ticker every mixer shares (MixerTicker, ~40 fps).
        MixerOptions mixerOptions;
        ICollection<IMachine> returnSet = new HashSet<IMachine>();

        // ------------------------------------------------------------------ columns

        /// <summary>
        /// Lays out the columns: tracks in layout order, each machine group's column just before
        /// its first member (members of a folded group are left out), then the returns.
        /// </summary>
        void PlanColumns(SessionDocument doc)
        {
            columns.Clear();
            trackColumn = new int[trackCount];
            var song = buzz.Song;
            var groupOf = new IMachineGroup[trackCount];
            for (int t = 0; t < trackCount; t++)
                groupOf[t] = SessionMixer.GroupOf(song, layout.SequenceOf(t)?.Machine);

            var seen = new HashSet<IMachineGroup>();
            for (int t = 0; t < trackCount; t++)
            {
                var g = groupOf[t];
                if (g != null && seen.Add(g))
                {
                    var members = Enumerable.Range(0, trackCount).Where(i => groupOf[i] == g).ToList();
                    columns.Add(new Column { Kind = ColumnKind.Group, Group = g, Machine = g.MainOutputMachine, Members = members });
                }
                if (g != null && doc.CollapsedGroups.Contains(g.Name))
                {
                    trackColumn[t] = -1;
                    continue;
                }
                trackColumn[t] = columns.Count;
                columns.Add(new Column { Kind = ColumnKind.Track, Track = t, Group = g, Machine = layout.SequenceOf(t)?.Machine });
            }

            returns = layout.ReturnMachines();
            for (int r = 0; r < returns.Count; r++)
                columns.Add(new Column { Kind = ColumnKind.Return, Return = r, Machine = returns[r] });

            structureKey = StructureKey();
        }

        /// <summary>
        /// Group membership and returns as text. Moving machines between groups raises no event
        /// the layout hears, so RefreshState compares this now and then and rebuilds on a change.
        /// </summary>
        string StructureKey()
        {
            var song = buzz.Song;
            var parts = new List<string>();
            for (int t = 0; t < layout.TrackCount; t++)
            {
                var g = SessionMixer.GroupOf(song, layout.SequenceOf(t)?.Machine);
                parts.Add(g == null ? "" : g.Name + ">" + g.MainOutputMachine?.Name);
            }
            parts.Add("|" + string.Join(",", layout.ReturnMachines().Select(m => m.Name)));
            return string.Join(";", parts);
        }

        static string ReturnLetter(int r) => MixerModel.ReturnLetter(r);

        List<int> VisibleTracks() => columns.Where(c => c.Kind == ColumnKind.Track).Select(c => c.Track).ToList();

        // For tests and diagnostics.
        internal IReadOnlyList<MixerStripView> MixerStrips => strips;
        internal IReadOnlyList<GroupSlotCell> GroupSlots => groupSlots;
        internal bool IsMetering => ticker != null;
        internal GroupHeader FindGroupHeader(string name) => groupHeaders.FirstOrDefault(h => h.Group.Name == name);

        /// <summary>The columns as text: "[Group]" (or "[Group ▸]" folded), track machines, "A:Return".</summary>
        internal string DescribeColumns()
        {
            var doc = layout.Document;
            return string.Join(" ", columns.Select(c => c.Kind switch
            {
                ColumnKind.Group => doc != null && doc.CollapsedGroups.Contains(c.Group.Name) ? $"[{c.Group.Name} ▸]" : $"[{c.Group.Name}]",
                ColumnKind.Return => $"{ReturnLetter(c.Return)}:{c.Machine?.Name}",
                _ => c.Machine?.Name ?? "?"
            }));
        }

        void PlaceGroupColumn(SessionDocument doc, Column col, int c)
        {
            bool collapsed = doc.CollapsedGroups.Contains(col.Group.Name);
            var header = new GroupHeader(this, col.Group, col.Members, collapsed);
            groupHeaders.Add(header);
            Place(header, 0, c);

            for (int s = 0; s < sceneCount; s++)
            {
                var cell = new GroupSlotCell(this, col.Members, s);
                groupSlots.Add(cell);
                Place(cell, s + 1, c);
            }

            var members = col.Members;
            var stop = new FlatButton("■", () => StopTracks(members), $"Stop every track of {col.Group.Name}")
            {
                Margin = new Thickness(0, 6, Gap, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            Place(stop, sceneCount + 2, c);
        }

        void PlaceReturnColumn(Column col, int c)
        {
            var header = new TrackHeader(this, -1, col.Machine, ReturnLetter(col.Return) + "  " + col.Machine.Name, -1) { Return = col.Return };
            header.Background = SessionPalette.ReturnHeader;
            headers.Add(header);
            Place(header, 0, c);
            var filler = new Border { Background = SessionPalette.MixerBack, Margin = new Thickness(0, 0, Gap, Gap) };
            ToolTip.SetTip(filler, $"Return {ReturnLetter(col.Return)}: {col.Machine.Name}. Tracks send to it from their mixer strips.");
            Grid.SetRowSpan(filler, Math.Max(1, sceneCount + 1));      // the scenes and the spare rows
            if (sceneCount > 0)
                Place(filler, 1, c);
        }

        // ------------------------------------------------------------------ mixer section

        void BuildMixer(SessionDocument doc)
        {
            mixerGrid.Children.Clear();
            mixerOptions = null;
            mixerGrid.ColumnDefinitions.Clear();
            masterHolder.Child = null;
            strips.Clear();
            mixerSection.IsVisible = doc.ShowMixer;
            masterSection.IsVisible = doc.ShowMixer;
            mixerButton.SetLit(doc.ShowMixer ? SessionPalette.Fader : null);
            ioButton.SetLit(doc.ShowMixer && doc.ShowIO ? SessionPalette.Fader : null);
            ioButton.IsEnabled = doc.ShowMixer;
            if (!doc.ShowMixer)
            {
                UpdateMetering();
                return;
            }

            returnSet = new HashSet<IMachine>(returns);
            for (int c = 0; c < columns.Count; c++)
            {
                mixerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnWidth + Gap) });
                var col = columns[c];
                var kind = col.Kind switch
                {
                    ColumnKind.Group => StripKind.Group,
                    ColumnKind.Return => StripKind.Return,
                    _ => StripKind.Track
                };
                string name = col.Kind switch
                {
                    ColumnKind.Group => col.Group.Name,
                    ColumnKind.Return => col.Machine?.Name,
                    _ => col.Machine?.Name ?? doc.Tracks[col.Track].Machine
                };
                string subtitle = col.Kind switch
                {
                    ColumnKind.Group => $"group {(doc.CollapsedGroups.Contains(col.Group.Name) ? "▸" : "▾")} {col.Members.Count} track{(col.Members.Count == 1 ? "" : "s")}",
                    ColumnKind.Return => "return " + ReturnLetter(col.Return),
                    _ => "track"
                };
                var spec = new StripSpec { Kind = kind, Id = (kind == StripKind.Group ? "group:" : kind == StripKind.Return ? "return:" : "track:") + name, Machine = col.Machine, Name = name, Subtitle = subtitle };
                var strip = new MixerStripView(this, spec, MixerDensity.Compact);
                strips.Add(strip);
                Grid.SetColumn(strip, c);
                mixerGrid.Children.Add(strip);
            }
            var masterSpec = new StripSpec { Kind = StripKind.Master, Id = "master:Master", Machine = buzz.SongCore.MachinesList.FirstOrDefault(m => m.DLL?.Info?.Type == MachineType.Master), Name = "Master", Subtitle = "master" };
            var master = new MixerStripView(this, masterSpec, MixerDensity.Compact);
            strips.Add(master);
            master.Width = SceneColumnWidth;
            masterHolder.Child = master;          // under the scene column, frozen with it
            UpdateMetering();
        }

        void ToggleMixer() => layout.SetShowMixer(!layout.Ensure().ShowMixer);

        void ToggleIO()
        {
            var doc = layout.Ensure();
            if (!doc.ShowMixer) layout.SetShowMixer(true);
            layout.SetShowIO(!doc.ShowIO);
        }

        /// <summary>The crossfader's menu (0.6): its curve and MIDI learn.</summary>
        internal void ShowCrossfaderMenu(Control anchor)
        {
            var doc = layout.Ensure();
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "Crossfader curve", IsEnabled = false });
            foreach (var curve in SessionCore.CrossfaderCurves)
            {
                string c = curve;
                menu.Items.Add(Check(c, doc.CrossfaderCurve == c, () => layout.SetCrossfaderCurve(c), c switch
                {
                    "Dipless" => "Both sides at full volume in the centre; each fades out over the far half",
                    "Constant power" => "Equal power across the travel: each side is -3 dB in the centre",
                    _ => "Each side cuts out right at the far end (scratch style)"
                }));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Centre", () => layout.SetCrossfaderPosition(0.5)));
            menu.Items.Add(new Separator());
            AddMidiItems(menu, SessionTarget.Crossfader, 0, 0);
            OpenMenu(menu, anchor);
        }

        /// <summary>Peak metering runs while the mixer is shown and the view is on screen.</summary>
        void UpdateMetering()
        {
            bool want = attached && layout.Document?.ShowMixer == true && strips.Count > 0;
            if (want == (ticker != null)) return;
            if (want) ticker = MixerTicker.Add(f => RefreshMeters(f / MixerView.MeterFallPerSecond), null);
            else { ticker.Dispose(); ticker = null; }
        }

        /// <summary>Takes the peaks since the last call and moves the meters (falling at a fixed rate).</summary>
        internal void RefreshMeters(double elapsedSeconds = 0.033)
        {
            double fall = MixerView.MeterFallPerSecond * elapsedSeconds;
            foreach (var strip in strips)
                strip.RefreshMeter(fall);
        }

        void RefreshMixer()
        {
            foreach (var strip in strips)
                strip.Refresh();
            if (strips.Count > 0 && strips[^1].Crossfader is { } cf)
            {
                var learn = midi?.LearnTarget;
                cf.Opacity = learn is { } l && l.Target == SessionTarget.Crossfader ? 0.6 : 1.0;
            }
        }

        // ------------------------------------------------------------------ the shared mixer's host (the Session View's own options)

        ReBuzzCore IMixerHost.Core => buzz;
        IReadOnlyList<IMachine> IMixerHost.Returns => returns;
        ICollection<IMachine> IMixerHost.ReturnSet => returnSet;
        MixerOptions IMixerHost.Options => mixerOptions ??= new MixerOptions
        {
            Sends = true, Arm = true, Cue = true, Crossfader = true, IO = layout.Document?.ShowIO == true,
            Width = ColumnWidth, CrossfaderMenu = ShowCrossfaderMenu, CrossfaderTip = () => Tip("Crossfader: fades between the tracks assigned to A and to B (the A/B chips); others play on.\n" +
                                      "drag or wheel · Shift for fine · double-click: centre · right-click: curve", SessionTarget.Crossfader, 0, 0),
        };

        void IMixerHost.ToggleFold(string groupId)
        {
            string name = groupId.StartsWith("group:") ? groupId.Substring(6) : groupId;
            layout.SetGroupCollapsed(name, !layout.Ensure().CollapsedGroups.Contains(name));
        }

        void IMixerHost.OpenDevice(IMachine machine) => StudioSelection.For(buzz).SelectMachine(machine, this);

        internal IMachine CueMachine => layout.MachineNamed(layout.Document?.CueMachine);

        // ------------------------------------------------------------------ group actions

        internal void LaunchGroupSlot(GroupSlotCell cell)
        {
            layout.LaunchGroupScene(cell.Members, cell.Scene);
            RefreshState();
        }

        void StopTracks(IEnumerable<int> tracks)
        {
            foreach (int t in tracks)
                if (layout.SequenceOf(t) is ISequence s)
                    session.Stop(s);
            RefreshState();
        }

        void ShowGroupMenu(GroupHeader header)
        {
            var menu = new ContextMenu();
            bool collapsed = layout.Ensure().CollapsedGroups.Contains(header.Group.Name);
            menu.Items.Add(Item(collapsed ? "Unfold group" : "Fold group", () => layout.SetGroupCollapsed(header.Group.Name, !collapsed)));
            menu.Items.Add(Item("Stop group", () => StopTracks(header.Members)));
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Machine groups are made in the Machine View", IsEnabled = false });
            OpenMenu(menu, header);
        }

        internal void ShowReturnMenu(TrackHeader header)
        {
            var menu = new ContextMenu();
            var machine = header.Machine;
            menu.Items.Add(Check("Mute", machine.IsMuted, () => machine.IsMuted = !machine.IsMuted));
            menu.Items.Add(Check("Solo", machine.IsSoloed, () => machine.IsSoloed = !machine.IsSoloed));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Remove return track", () => layout.RemoveReturn(machine.Name)));
            OpenMenu(menu, header);
        }

        void AddMixerMenuItems(ContextMenu menu, SessionDocument doc)
        {
            var show = Check("Show mixer", doc.ShowMixer, ToggleMixer);
            show.InputGesture = new KeyGesture(Key.M, (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) | KeyModifiers.Alt);
            menu.Items.Add(show);

            var effects = SessionMixer.EffectMachines(buzz.Song).ToList();
            var returnItem = new MenuItem { Header = "Return tracks", Tag = "Return tracks" };
            ToolTip.SetTip(returnItem, "Effect machines that tracks send to from their mixer strips (return tracks)");
            foreach (var m in effects)
            {
                var machine = m;
                bool isReturn = doc.Returns.Contains(machine.Name);
                var choice = Check(machine.Name, isReturn, () => { if (isReturn) layout.RemoveReturn(machine.Name); else layout.AddReturn(machine); });
                choice.IsEnabled = machine.Name != doc.CueMachine;
                returnItem.Items.Add(choice);
            }
            if (effects.Count == 0)
                returnItem.Items.Add(new MenuItem { Header = "(no effect machines: add one in the Machine View)", IsEnabled = false });
            menu.Items.Add(returnItem);

            var cueItem = new MenuItem { Header = "Cue output", Tag = "Cue output" };
            ToolTip.SetTip(cueItem, "Machine the Cue buttons connect tracks to: one that plays to a second output,\nnot connected to the master");
            cueItem.Items.Add(Check("None", doc.CueMachine.Length == 0, () => layout.SetCueMachine(null)));
            foreach (var m in effects)
            {
                var machine = m;
                cueItem.Items.Add(Check(machine.Name, machine.Name == doc.CueMachine, () => layout.SetCueMachine(machine)));
            }
            menu.Items.Add(cueItem);
        }

        // ------------------------------------------------------------------ cells

        /// <summary>Group column header: fold arrow and group name.</summary>
        internal sealed class GroupHeader : Border
        {
            public IMachineGroup Group { get; }
            public IReadOnlyList<int> Members { get; }

            public GroupHeader(SessionView view, IMachineGroup group, IReadOnlyList<int> members, bool collapsed)
            {
                Group = group;
                Members = members;
                Background = SessionPalette.GroupHeader;
                Margin = new Thickness(0, 0, Gap, Gap);
                Cursor = Hand;
                ToolTip.SetTip(this, $"Group {group.Name} ({members.Count} track{(members.Count == 1 ? "" : "s")})\nclick folds/unfolds · right-click for more");
                Child = new TextBlock
                {
                    Text = (collapsed ? "▸  " : "▾  ") + group.Name,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(6, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                PointerPressed += (s, e) =>
                {
                    if (!IsLeft(e, this)) return;
                    view.layout.SetGroupCollapsed(Group.Name, !collapsed);
                    e.Handled = true;
                };
                PointerReleased += (s, e) =>
                {
                    if (e.InitialPressMouseButton != MouseButton.Right) return;
                    view.ShowGroupMenu(this);
                    e.Handled = true;
                };
            }
        }

        /// <summary>
        /// Group slot: launches the clips of all member tracks in its scene. Shows them while
        /// any is playing (green) or queued (blinking).
        /// </summary>
        internal sealed class GroupSlotCell : Border
        {
            readonly SessionView view;
            readonly TextBlock glyph;
            readonly TextBlock label;

            public IReadOnlyList<int> Members { get; }
            public int Scene { get; }
            public bool HasClips { get; }

            public GroupSlotCell(SessionView view, IReadOnlyList<int> members, int scene)
            {
                this.view = view;
                Members = members;
                Scene = scene;
                Margin = new Thickness(0, 0, Gap, Gap);
                BorderThickness = new Thickness(1);
                var names = members.Select(t => view.layout.PatternAt(t, scene)?.Name).Where(n => n != null).ToList();
                HasClips = names.Count > 0;
                ToolTip.SetTip(this, HasClips ? $"Group clips in scene {scene + 1}: {string.Join(", ", names)}\nclick launches them together" : "No clips of this group in this scene");

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                glyph = new TextBlock { Text = HasClips ? "▶" : "", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
                grid.Children.Add(glyph);
                label = new TextBlock
                {
                    Text = HasClips ? string.Join(" · ", names) : "",
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false
                };
                Grid.SetColumn(label, 1);
                grid.Children.Add(label);
                Child = grid;
                if (HasClips) Cursor = Hand;

                PointerPressed += (s, e) =>
                {
                    if (!IsLeft(e, this)) return;
                    if (HasClips) view.LaunchGroupSlot(this);
                    e.Handled = true;
                };
            }

            public void Refresh(ISession session, bool blinkOn)
            {
                if (!HasClips)
                {
                    Background = SessionPalette.MixerBack;
                    BorderBrush = SessionPalette.Transparent;
                    return;
                }
                bool playing = false, queued = false;
                foreach (int t in Members)
                {
                    var seq = view.layout.SequenceOf(t);
                    var pattern = view.layout.PatternAt(t, Scene);
                    if (seq == null || pattern == null) continue;
                    var state = view.layout.SlotState(seq, pattern, t, Scene);
                    playing |= state == ClipState.Playing || state == ClipState.Stopping;
                    queued |= state == ClipState.Queued;
                }
                // Fork (ux-overhaul.md §3.1): the header keeps its colour; playing lights the ▶ and the outline in the play colour.
                var background = SessionPalette.GroupHeader;
                if (IsPointerOver) background = SessionPalette.Lighten(background, 0.1);
                Background = background;
                var text = SessionPalette.TextOn(background);
                label.Foreground = text;
                bool lit = playing || (queued && blinkOn);
                glyph.Foreground = lit ? SessionPalette.Playing : text;
                BorderBrush = lit ? SessionPalette.Playing : SessionPalette.Transparent;
            }
        }
    }
}
