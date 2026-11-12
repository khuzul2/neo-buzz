using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ReBuzz.AppViews.Session
{
    /// <summary>
    /// Mixer section of the Session View (docs/specs/session-view.md 0.4): a strip under each
    /// column (sends, pan, fader with meter, cue), return and group columns, the master strip
    /// under the scene column. The strips edit the machine graph through <see cref="SessionMixer"/>
    /// and poll it, so they follow the Machine View live. Meters run on the render loop.
    /// </summary>
    internal sealed partial class SessionView
    {
        const double SendRowHeight = 18;
        const double PanRowHeight = 18;
        const double FaderHeight = 116;
        const double MeterFallPerSecond = 0.9;       // of the meter's 66 dB range per second

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
        readonly List<MixerStrip> strips = new List<MixerStrip>();
        readonly List<GroupSlotCell> groupSlots = new List<GroupSlotCell>();
        readonly List<GroupHeader> groupHeaders = new List<GroupHeader>();
        IReadOnlyList<IMachine> returns = Array.Empty<IMachine>();
        string structureKey = "";
        bool metering;
        long lastMeterTicks;

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

        static string ReturnLetter(int r) => r < 26 ? ((char)('A' + r)).ToString() : (r + 1).ToString();

        List<int> VisibleTracks() => columns.Where(c => c.Kind == ColumnKind.Track).Select(c => c.Track).ToList();

        // For tests and diagnostics.
        internal IReadOnlyList<MixerStrip> MixerStrips => strips;
        internal IReadOnlyList<GroupSlotCell> GroupSlots => groupSlots;

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
            var filler = new Border { Background = SessionPalette.MixerBack, Margin = new Thickness(0, 0, Gap, Gap), ToolTip = $"Return {ReturnLetter(col.Return)}: {col.Machine.Name}. Tracks send to it from their mixer strips." };
            Grid.SetRowSpan(filler, Math.Max(1, sceneCount + 1));      // the scenes and the spare rows
            if (sceneCount > 0)
                Place(filler, 1, c);
        }

        // ------------------------------------------------------------------ mixer section

        void BuildMixer(SessionDocument doc)
        {
            mixerGrid.Children.Clear();
            mixerGrid.ColumnDefinitions.Clear();
            masterHolder.Child = null;
            strips.Clear();
            mixerPanel.Visibility = doc.ShowMixer ? Visibility.Visible : Visibility.Collapsed;
            masterSection.Visibility = mixerPanel.Visibility;
            mixerButton.SetLit(doc.ShowMixer ? SessionPalette.Fader : null);
            if (!doc.ShowMixer)
            {
                UpdateMetering();
                return;
            }

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
                    ColumnKind.Return => ReturnLetter(col.Return) + "  " + col.Machine?.Name,
                    _ => col.Machine?.Name ?? doc.Tracks[col.Track].Machine
                };
                var strip = new MixerStrip(this, kind, col.Machine, name, returns);
                strips.Add(strip);
                Grid.SetColumn(strip, c);
                mixerGrid.Children.Add(strip);
            }
            var master = new MixerStrip(this, StripKind.Master, null, "Master", returns);
            strips.Add(master);
            masterHolder.Child = master;          // under the scene column, frozen with it
            UpdateMetering();
        }

        void ToggleMixer() => layout.SetShowMixer(!layout.Ensure().ShowMixer);

        /// <summary>The crossfader's menu (0.6): its curve and MIDI learn.</summary>
        internal void ShowCrossfaderMenu(FrameworkElement anchor)
        {
            var doc = layout.Ensure();
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "Crossfader curve", IsEnabled = false });
            foreach (var curve in SessionCore.CrossfaderCurves)
            {
                string c = curve;
                menu.Items.Add(new MenuItem
                {
                    Header = c,
                    IsChecked = doc.CrossfaderCurve == c,
                    ToolTip = c switch
                    {
                        "Dipless" => "Both sides at full volume in the centre; each fades out over the far half",
                        "Constant power" => "Equal power across the travel: each side is -3 dB in the centre",
                        _ => "Each side cuts out right at the far end (scratch style)"
                    },
                    Command = new RelayCommand(() => layout.SetCrossfaderCurve(c))
                });
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Centre", () => layout.SetCrossfaderPosition(0.5)));
            menu.Items.Add(new Separator());
            AddMidiItems(menu, SessionTarget.Crossfader, 0, 0);
            OpenMenu(menu, anchor);
        }

        /// <summary>Peak metering runs while the mixer is shown and the view is visible.</summary>
        void UpdateMetering()
        {
            bool want = IsLoaded && IsVisible && layout.Document?.ShowMixer == true && strips.Count > 0;
            if (want == metering) return;
            metering = want;
            SessionMixer.UseMeters(want);
            if (want)
                CompositionTarget.Rendering += OnRendering;
            else
                CompositionTarget.Rendering -= OnRendering;
        }

        void OnRendering(object sender, EventArgs e)
        {
            // Every frame is more than the meters need: ~30 fps, at least.
            long now = Environment.TickCount64;
            if (now - lastMeterTicks < 25) return;
            double seconds = lastMeterTicks == 0 ? 0.033 : Math.Min(0.2, (now - lastMeterTicks) / 1000.0);
            lastMeterTicks = now;
            RefreshMeters(seconds);
        }

        /// <summary>Takes the peaks since the last call and moves the meters (falling at a fixed rate).</summary>
        internal void RefreshMeters(double elapsedSeconds = 0.033)
        {
            double fall = MeterFallPerSecond * elapsedSeconds;
            foreach (var strip in strips)
                strip.RefreshMeter(fall);
        }

        void RefreshMixer()
        {
            foreach (var strip in strips)
                strip.Refresh();
        }

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

        void ShowReturnMenu(TrackHeader header)
        {
            var menu = new ContextMenu();
            var machine = header.Machine;
            menu.Items.Add(new MenuItem { Header = "Mute", IsChecked = machine.IsMuted, Command = new RelayCommand(() => machine.IsMuted = !machine.IsMuted) });
            menu.Items.Add(new MenuItem { Header = "Solo", IsChecked = machine.IsSoloed, Command = new RelayCommand(() => machine.IsSoloed = !machine.IsSoloed) });
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Remove return track", () => layout.RemoveReturn(machine.Name)));
            OpenMenu(menu, header);
        }

        void AddMixerMenuItems(ContextMenu menu, SessionDocument doc)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "Show mixer",
                InputGestureText = "Ctrl+Alt+M",
                IsChecked = doc.ShowMixer,
                Command = new RelayCommand(ToggleMixer)
            });

            var effects = SessionMixer.EffectMachines(buzz.Song).ToList();
            var returnItem = new MenuItem { Header = "Return tracks", ToolTip = "Effect machines that tracks send to from their mixer strips (return tracks)" };
            foreach (var m in effects)
            {
                var machine = m;
                bool isReturn = doc.Returns.Contains(machine.Name);
                returnItem.Items.Add(new MenuItem
                {
                    Header = machine.Name,
                    IsCheckable = false,
                    IsChecked = isReturn,
                    IsEnabled = machine.Name != doc.CueMachine,
                    Command = new RelayCommand(() => { if (isReturn) layout.RemoveReturn(machine.Name); else layout.AddReturn(machine); })
                });
            }
            if (effects.Count == 0)
                returnItem.Items.Add(new MenuItem { Header = "(no effect machines: add one in the Machine View)", IsEnabled = false });
            menu.Items.Add(returnItem);

            var cueItem = new MenuItem { Header = "Cue output", ToolTip = "Machine the Cue buttons connect tracks to: one that plays to a second output\n(ReBuzz Audio Out or Polac Out set to other channels), not connected to the master" };
            cueItem.Items.Add(new MenuItem { Header = "None", IsChecked = doc.CueMachine.Length == 0, Command = new RelayCommand(() => layout.SetCueMachine(null)) });
            foreach (var m in effects)
            {
                var machine = m;
                cueItem.Items.Add(new MenuItem { Header = machine.Name, IsChecked = machine.Name == doc.CueMachine, Command = new RelayCommand(() => layout.SetCueMachine(machine)) });
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
                Cursor = Cursors.Hand;
                ToolTip = $"Group {group.Name} ({members.Count} track{(members.Count == 1 ? "" : "s")})\nclick folds/unfolds · right-click for more";
                Child = new TextBlock
                {
                    Text = (collapsed ? "▸  " : "▾  ") + group.Name,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(6, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                MouseLeftButtonDown += (s, e) => { view.layout.SetGroupCollapsed(Group.Name, !collapsed); e.Handled = true; };
                MouseRightButtonUp += (s, e) => { view.ShowGroupMenu(this); e.Handled = true; };
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
            bool hover;

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
                ToolTip = HasClips ? $"Group clips in scene {scene + 1}: {string.Join(", ", names)}\nclick launches them together" : "No clips of this group in this scene";

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
                Cursor = HasClips ? Cursors.Hand : null;

                MouseEnter += (s, e) => hover = true;
                MouseLeave += (s, e) => hover = false;
                MouseLeftButtonDown += (s, e) => { if (HasClips) view.LaunchGroupSlot(this); e.Handled = true; };
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
                var background = playing ? SessionPalette.Playing : SessionPalette.GroupHeader;
                if (hover) background = SessionPalette.Lighten(background, 0.1);
                Background = background;
                var text = SessionPalette.TextOn(background);
                label.Foreground = text;
                glyph.Foreground = queued ? (blinkOn ? SessionPalette.Progress : text) : text;
                BorderBrush = queued && blinkOn ? SessionPalette.Progress : SessionPalette.Transparent;
            }
        }

        internal enum StripKind { Track, Group, Return, Master }

        /// <summary>
        /// Mixer strip: a send per return (tracks only), pan, fader with a stereo peak meter and
        /// its dB value, then Cue and the name. Values are polled (<see cref="Refresh"/>), the
        /// meter on the render loop (<see cref="RefreshMeter"/>).
        /// </summary>
        internal sealed class MixerStrip : Border
        {
            readonly SessionView view;
            readonly IReadOnlyList<IMachine> returns;
            readonly List<MixerBar> sends = new List<MixerBar>();
            readonly MixerBar pan;
            readonly MixerBar fader;
            readonly MeterBar meter;
            readonly TextBlock dbText;
            readonly Chip mute;
            readonly Chip solo;
            readonly Chip cue;
            readonly Chip sideA;
            readonly Chip sideB;
            readonly MixerBar crossfader;
            double levelL, levelR;

            public StripKind Kind { get; }
            public IMachine Machine { get; }
            internal MixerBar Fader => fader;
            internal MixerBar PanBar => pan;
            internal IReadOnlyList<MixerBar> Sends => sends;
            internal MeterBar Meter => meter;
            internal string DbText => dbText.Text;
            internal MixerBar Crossfader => crossfader;
            internal Chip SideA => sideA;
            internal Chip SideB => sideB;

            SessionDocument Doc => view.layout.Document;

            public MixerStrip(SessionView view, StripKind kind, IMachine machine, string name, IReadOnlyList<IMachine> returns)
            {
                this.view = view;
                this.returns = returns;
                Kind = kind;
                Machine = machine;
                Background = SessionPalette.MixerBack;
                Margin = new Thickness(0, 0, Gap, 0);
                Padding = new Thickness(4, 4, 4, 3);

                var stack = new StackPanel();

                // Sends (tracks only; others keep the space so the rows line up)
                for (int r = 0; r < returns.Count; r++)
                {
                    if (kind == StripKind.Track)
                    {
                        var target = returns[r];
                        var send = new MixerBar(false, false, SessionPalette.Send)
                        {
                            Height = SendRowHeight - 2,
                            Margin = new Thickness(0, 0, 0, 2),
                            Caption = ReturnLetter(r),
                            DefaultValue = 0,
                            ToolTip = $"Send to {target.Name} (return {ReturnLetter(r)})\ndrag or wheel · Shift for fine · double-click: off"
                        };
                        send.Changed += v => SessionMixer.SetSend(Machine, target, SessionMixer.AmpOf(v));
                        sends.Add(send);
                        stack.Children.Add(send);
                    }
                    else
                    {
                        stack.Children.Add(new FrameworkElement { Height = SendRowHeight });
                    }
                }

                // Pan
                if (kind != StripKind.Master)
                {
                    pan = new MixerBar(false, true, SessionPalette.Pan)
                    {
                        Height = PanRowHeight - 2,
                        Margin = new Thickness(0, 0, 0, 4),
                        Caption = "Pan",
                        DefaultValue = 0.5,
                        ToolTip = "Pan: the pan of the machine's output connection(s)\ndrag or wheel · Shift for fine · double-click: centre"
                    };
                    pan.Changed += v => SessionMixer.SetPan(Doc, Machine, SessionMixer.PanValue(v * 2 - 1));
                    stack.Children.Add(pan);
                }
                else
                {
                    stack.Children.Add(new FrameworkElement { Height = PanRowHeight + 2 });
                }

                // Fader and meter
                fader = new MixerBar(true, false, kind == StripKind.Master ? SessionPalette.Thumb : SessionPalette.Fader)
                {
                    Width = 26,
                    Height = FaderHeight,
                    DefaultValue = kind == StripKind.Master ? 1 : SessionMixer.UnityFader,
                    Mark = kind == StripKind.Master ? null : SessionMixer.UnityFader,
                    ToolTip = kind == StripKind.Master
                        ? "Master volume (the toolbar's)\ndrag or wheel · Shift for fine · double-click: 0 dB"
                        : "Volume: the amp of the machine's output connection(s), the same as in the Machine View\ndrag or wheel · Shift for fine · double-click: 0 dB"
                };
                fader.Changed += v =>
                {
                    if (Kind == StripKind.Master)
                        view.buzz.MasterVolume = SessionMixer.MasterGainOf(v);
                    else
                        SessionMixer.SetAmp(Doc, Machine, SessionMixer.AmpOf(v));
                    UpdateDbText();
                };
                meter = new MeterBar { Width = 11, Height = FaderHeight, Margin = new Thickness(6, 0, 0, 0) };
                var faderRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                faderRow.Children.Add(fader);
                faderRow.Children.Add(meter);
                stack.Children.Add(faderRow);

                dbText = new TextBlock
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = SessionPalette.DimText,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 3)
                };
                stack.Children.Add(dbText);

                // Crossfader (0.6): A/B assignment on the strips, the crossfader on the master.
                var crossRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 18, Margin = new Thickness(0, 0, 0, 3) };
                if (kind == StripKind.Master)
                {
                    crossfader = new MixerBar(false, true, SessionPalette.Thumb)
                    {
                        Width = SceneColumnWidth - 12,
                        Height = 16,
                        Caption = "A  ·  B",
                        DefaultValue = 0.5,
                        ToolTip = view.Tip("Crossfader: fades between the tracks assigned to A and to B (the A/B chips); others play on.\n" +
                                           "drag or wheel · Shift for fine · double-click: centre · right-click: curve", SessionTarget.Crossfader, 0, 0)
                    };
                    crossfader.Changed += v =>
                    {
                        crossfader.ValueText = CrossfaderText(v);
                        view.layout.SetCrossfaderPosition(v);
                    };
                    crossfader.MouseRightButtonUp += (s, e) => { view.ShowCrossfaderMenu(crossfader); e.Handled = true; };
                    crossRow.Children.Add(crossfader);
                }
                else if (machine != null)
                {
                    sideA = new Chip("A", SessionPalette.CrossA, "Crossfader side A: the crossfader fades this track out towards B", () => ToggleSide("A"));
                    sideB = new Chip("B", SessionPalette.CrossB, "Crossfader side B: the crossfader fades this track out towards A", () => ToggleSide("B"));
                    crossRow.Children.Add(sideA);
                    crossRow.Children.Add(sideB);
                }
                stack.Children.Add(crossRow);

                // Mute, solo, cue and name
                var bottom = new DockPanel { LastChildFill = true, Height = 18 };
                if (kind != StripKind.Master)
                {
                    mute = new Chip("M", SessionPalette.Mute, "Mute", () => { if (Machine != null) Machine.IsMuted = !Machine.IsMuted; }) { Margin = new Thickness(0, 0, 2, 0) };
                    solo = new Chip("S", SessionPalette.Solo, "Solo", () => { if (Machine != null) Machine.IsSoloed = !Machine.IsSoloed; }) { Margin = new Thickness(0, 0, 2, 0) };
                    cue = new Chip("C", SessionPalette.Cue, "Cue: also play this to the cue output (⚙ → Cue output)", ToggleCue) { Margin = new Thickness(0, 0, 5, 0) };
                    foreach (var chip in new[] { mute, solo, cue })
                    {
                        DockPanel.SetDock(chip, Dock.Left);
                        bottom.Children.Add(chip);
                    }
                }
                bottom.Children.Add(new TextBlock
                {
                    Text = name,
                    FontSize = 11,
                    Foreground = kind == StripKind.Master ? SessionPalette.Text : SessionPalette.DimText,
                    FontWeight = kind == StripKind.Master ? FontWeights.SemiBold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = kind == StripKind.Master ? TextAlignment.Center : TextAlignment.Left,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                stack.Children.Add(bottom);

                Child = stack;
                Refresh();
            }

            void ToggleSide(string side)
            {
                if (Machine == null) return;
                view.layout.SetCrossfade(Machine, view.layout.CrossfadeOf(Machine) == side ? null : side);
            }

            static string CrossfaderText(double v) =>
                Math.Abs(v - 0.5) < 0.005 ? "centre" : v < 0.5 ? $"A {(int)Math.Round((0.5 - v) * 200)}%" : $"B {(int)Math.Round((v - 0.5) * 200)}%";

            void ToggleCue()
            {
                var cueMachine = view.CueMachine;
                if (Machine == null || cueMachine == null) return;
                SessionMixer.SetCue(Machine, cueMachine, !SessionMixer.IsCued(Machine, cueMachine));
                Refresh();
            }

            void UpdateDbText()
            {
                if (Kind == StripKind.Master)
                {
                    double gain = view.buzz.MasterVolume;
                    dbText.Text = gain <= 0 ? "-inf dB" : SessionMixer.FormatDb(BuzzGUI.Common.Decibel.FromAmplitude(gain)) + " dB";
                    return;
                }
                int amp = SessionMixer.GetAmp(Doc, Machine);
                dbText.Text = amp < 0 ? "–" : SessionMixer.FormatAmp(amp) + " dB";
            }

            /// <summary>Reads volume, pan, sends and cue from the machine graph.</summary>
            public void Refresh()
            {
                var doc = Doc;
                if (Kind == StripKind.Master)
                {
                    if (!fader.IsDragging)
                        fader.Value = SessionMixer.MasterFaderOf(view.buzz.MasterVolume);
                    UpdateDbText();
                    if (!crossfader.IsDragging)
                        crossfader.Value = doc?.CrossfaderPosition ?? 0.5;
                    crossfader.ValueText = CrossfaderText(crossfader.Value);
                    var learn = view.midi?.LearnTarget;
                    crossfader.Opacity = learn is { } l && l.Target == SessionTarget.Crossfader ? 0.6 : 1.0;
                    return;
                }
                if (sideA != null)
                {
                    string side = view.layout.CrossfadeOf(Machine);
                    sideA.SetActive(side == "A");
                    sideB.SetActive(side == "B");
                }

                int amp = SessionMixer.GetAmp(doc, Machine);
                fader.IsEnabled = amp >= 0;
                if (amp >= 0 && !fader.IsDragging)
                    fader.Value = SessionMixer.FaderOf(amp);
                UpdateDbText();

                int p = SessionMixer.GetPan(doc, Machine);
                pan.IsEnabled = p >= 0;
                if (p >= 0 && !pan.IsDragging)
                    pan.Value = (SessionMixer.PanOf(p) + 1) / 2;
                pan.ValueText = p >= 0 ? SessionMixer.FormatPan(p) : "";

                for (int r = 0; r < sends.Count; r++)
                {
                    var send = sends[r];
                    var target = returns[r];
                    bool can = SessionMixer.CanSend(Machine, target);
                    send.IsEnabled = can;
                    int s = SessionMixer.GetSend(Machine, target);
                    if (!send.IsDragging)
                        send.Value = SessionMixer.FaderOf(s);
                    send.ValueText = !can ? "" : s <= 0 ? "off" : SessionMixer.FormatAmp(s);
                }

                mute.IsEnabled = solo.IsEnabled = Machine != null;
                mute.Opacity = solo.Opacity = Machine != null ? 1 : 0.4;
                mute.SetActive(Machine?.IsMuted == true);
                solo.SetActive(Machine?.IsSoloed == true);

                var cueMachine = view.CueMachine;
                bool canCue = Machine != null && cueMachine != null && Machine != cueMachine;
                cue.IsEnabled = canCue;
                cue.Opacity = canCue ? 1 : 0.4;
                cue.SetActive(canCue && SessionMixer.IsCued(Machine, cueMachine));
            }

            /// <summary>Moves the meter to the latest peak; it falls by at most <paramref name="fall"/>.</summary>
            public void RefreshMeter(double fall)
            {
                double l, r;
                if (Kind == StripKind.Master)
                {
                    // The master's meter data (ReBuzz's VU meter, 0..1 over -80..0 dB).
                    var vu = view.buzz.VUMeterLevel;
                    l = vu == null || vu.Item1 <= 0 ? 0 : SessionMixer.LevelOfDb(vu.Item1 * 80 - 80);
                    r = vu == null || vu.Item2 <= 0 ? 0 : SessionMixer.LevelOfDb(vu.Item2 * 80 - 80);
                }
                else
                {
                    var peak = SessionMixer.MainPeak(Doc, Machine);
                    l = SessionMixer.MeterLevel(peak.Left);
                    r = SessionMixer.MeterLevel(peak.Right);
                }
                levelL = Math.Max(l, levelL - fall);
                levelR = Math.Max(r, levelR - fall);
                meter.Set(levelL, levelR);
            }
        }

        /// <summary>
        /// Slider drawn in one pass: horizontal (sends, pan; caption and value inside) or
        /// vertical (fader with a thumb). Dragging is relative, so a click doesn't jump; Shift
        /// is fine control; the wheel steps; double-click resets.
        /// </summary>
        internal sealed class MixerBar : FrameworkElement
        {
            readonly bool vertical;
            readonly bool bipolar;
            readonly Brush fill;
            double current;
            string valueText = "";
            Point? last;
            const double ThumbHeight = 10;

            public double DefaultValue { get; set; }
            public double? Mark { get; set; }
            public string Caption { get; set; } = "";
            public bool IsDragging => last != null;

            /// <summary>Raised when the user changes the value (not when it's set from code).</summary>
            public event Action<double> Changed;

            public MixerBar(bool vertical, bool bipolar, Brush fill)
            {
                this.vertical = vertical;
                this.bipolar = bipolar;
                this.fill = fill;
                Cursor = vertical ? Cursors.SizeNS : Cursors.SizeWE;
                SnapsToDevicePixels = true;
                IsEnabledChanged += (s, e) => { Opacity = IsEnabled ? 1.0 : 0.35; InvalidateVisual(); };
            }

            public double Value
            {
                get => current;
                set
                {
                    double v = Math.Clamp(value, 0, 1);
                    if (Math.Abs(v - current) < 1e-9) return;
                    current = v;
                    InvalidateVisual();
                }
            }

            public string ValueText
            {
                get => valueText;
                set
                {
                    value ??= "";
                    if (value == valueText) return;
                    valueText = value;
                    InvalidateVisual();
                }
            }

            /// <summary>Sets the value as the user would (raises <see cref="Changed"/>).</summary>
            internal void SetByUser(double v)
            {
                v = Math.Clamp(v, 0, 1);
                if (Math.Abs(v - current) < 1e-9) return;
                Value = v;
                Changed?.Invoke(v);
            }

            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                if (!IsEnabled) return;
                if (e.ClickCount == 2)
                {
                    SetByUser(DefaultValue);
                }
                else
                {
                    last = e.GetPosition(this);
                    CaptureMouse();
                }
                e.Handled = true;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                if (last is not Point from || !IsMouseCaptured) return;
                var p = e.GetPosition(this);
                double length = vertical ? Math.Max(1, ActualHeight - ThumbHeight) : Math.Max(1, ActualWidth);
                double delta = vertical ? from.Y - p.Y : p.X - from.X;
                double scale = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 1.0;
                last = p;
                SetByUser(current + delta / length * scale);
            }

            protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
            {
                last = null;
                if (IsMouseCaptured) ReleaseMouseCapture();
                e.Handled = true;
            }

            protected override void OnLostMouseCapture(MouseEventArgs e)
            {
                last = null;
                base.OnLostMouseCapture(e);
            }

            protected override void OnMouseWheel(MouseWheelEventArgs e)
            {
                if (!IsEnabled) return;
                double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.005 : 0.02;
                SetByUser(current + Math.Sign(e.Delta) * step);
                e.Handled = true;
            }

            protected override void OnRender(DrawingContext dc)
            {
                double w = ActualWidth, h = ActualHeight;
                if (w <= 0 || h <= 0) return;
                dc.DrawRectangle(SessionPalette.Transparent, null, new Rect(0, 0, w, h));   // hit area

                if (vertical)
                {
                    double travel = h - ThumbHeight;
                    double y = ThumbHeight / 2 + (1 - current) * travel;
                    var groove = new Rect(w / 2 - 2, ThumbHeight / 2, 4, travel);
                    dc.DrawRectangle(SessionPalette.Groove, null, groove);
                    dc.DrawRectangle(fill, null, new Rect(w / 2 - 2, y, 4, Math.Max(0, groove.Bottom - y)));
                    if (Mark is double mark)
                    {
                        double my = ThumbHeight / 2 + (1 - mark) * travel;
                        dc.DrawRectangle(SessionPalette.DimText, null, new Rect(0, Math.Round(my), 5, 1));
                        dc.DrawRectangle(SessionPalette.DimText, null, new Rect(w - 5, Math.Round(my), 5, 1));
                    }
                    dc.DrawRoundedRectangle(SessionPalette.Thumb, null, new Rect(2, y - ThumbHeight / 2, w - 4, ThumbHeight), 2, 2);
                    dc.DrawRectangle(SessionPalette.Groove, null, new Rect(4, Math.Round(y), w - 8, 1));
                    return;
                }

                dc.DrawRoundedRectangle(SessionPalette.Groove, null, new Rect(0, 0, w, h), 2, 2);
                if (bipolar)
                {
                    double x0 = w / 2, x1 = current * w;
                    dc.DrawRectangle(fill, null, new Rect(Math.Min(x0, x1), 0, Math.Max(1, Math.Abs(x1 - x0)), h));
                }
                else if (current > 0)
                {
                    dc.PushOpacity(0.6);
                    dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, current * w, h), 2, 2);
                    dc.Pop();
                }

                double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var typeface = new Typeface("Segoe UI");
                if (!string.IsNullOrEmpty(Caption))
                {
                    var caption = new FormattedText(Caption, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10, SessionPalette.Text, dip);
                    dc.DrawText(caption, new Point(4, (h - caption.Height) / 2));
                }
                if (!string.IsNullOrEmpty(valueText))
                {
                    var text = new FormattedText(valueText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10, SessionPalette.Text, dip);
                    dc.DrawText(text, new Point(Math.Max(0, w - text.Width - 4), (h - text.Height) / 2));
                }
            }
        }

        /// <summary>Stereo peak meter: green, yellow from -12 dB, red above 0 dB.</summary>
        internal sealed class MeterBar : FrameworkElement
        {
            static readonly double Yellow = SessionMixer.LevelOfDb(-12);
            static readonly double Red = SessionMixer.LevelOfDb(0);

            double left, right;

            public double Left => left;
            public double Right => right;

            public MeterBar()
            {
                SnapsToDevicePixels = true;
                ToolTip = "Peak, post-fader (-60 … +6 dB)";
            }

            public void Set(double l, double r)
            {
                if (Math.Abs(l - left) < 0.002 && Math.Abs(r - right) < 0.002) return;
                left = l;
                right = r;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                double w = ActualWidth, h = ActualHeight;
                if (w <= 0 || h <= 0) return;
                double bar = Math.Floor((w - 1) / 2);
                Draw(dc, 0, bar, h, left);
                Draw(dc, bar + 1, bar, h, right);
                double zero = Math.Round((1 - Red) * h);
                dc.DrawRectangle(SessionPalette.DimText, null, new Rect(0, zero, w, 1));
            }

            static void Draw(DrawingContext dc, double x, double width, double h, double level)
            {
                dc.DrawRectangle(SessionPalette.Groove, null, new Rect(x, 0, width, h));
                if (level <= 0) return;
                Segment(dc, x, width, h, 0, Math.Min(level, Yellow), SessionPalette.MeterLow);
                if (level > Yellow) Segment(dc, x, width, h, Yellow, Math.Min(level, Red), SessionPalette.MeterMid);
                if (level > Red) Segment(dc, x, width, h, Red, level, SessionPalette.MeterHigh);
            }

            static void Segment(DrawingContext dc, double x, double width, double h, double from, double to, Brush brush) =>
                dc.DrawRectangle(brush, null, new Rect(x, (1 - to) * h, width, Math.Max(0, (to - from) * h)));
        }
    }
}
