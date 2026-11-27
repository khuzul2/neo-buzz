// Fork (docs/specs/midi-mapping.md, MM1): the studio's half of MIDI mapping in a real main window with scripted controllers: map mode (outlines, a
// click arms, the next control moved maps, badges, Esc, Delete), the per-control MIDI button and its popover, the Inspector's MIDI page, the
// top bar's track selector (and Alt+Up / Alt+Down), the old Bind item opening the popover, and the studio's selection one with the mapping's.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Midi;
using ReBuzz.Midi.Mapping;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Device;
using ReBuzz.Studio.Views.Midi;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class MidiMappingUiTests
    {
        static readonly MidiDevice Pads = new MidiDevice(1, "Pads");
        static readonly MidiDevice Faders = new MidiDevice(0, "Faders");

        static ReBuzzCore Core => Studio.Host.Core;

        static MidiMapping Mapping => Core.MidiMapping;

        static int CC(int channel, int number, int value) => 0xB0 | channel | (number << 8) | (value << 16);

        sealed class Stage : IDisposable
        {
            public MainWindow Window;
            public MachineCore Lead, Bass;
            public List<ParameterCore> Params;

            public Stage()
            {
                (Window, _) = OpenEmpty();
                var song = Core.SongCore;
                foreach (var (name, x) in new[] { ("Lead", -0.5f), ("Bass", -0.2f) })
                {
                    song.CreateMachine("Neo Poly", null, name, null, null, null, -1, x, -0.3f);
                    var m = song.MachinesList.Last(q => q.Name == name);
                    song.ConnectMachines(m, song.MachinesList.First(q => q.DLL.Info.Type == MachineType.Master), new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                }
                Lead = song.MachinesList.First(m => m.Name == "Lead");
                Bass = song.MachinesList.First(m => m.Name == "Bass");
                Params = Lead.ParameterGroupsList[1].ParametersList.Cast<ParameterCore>().Where(p => p.MaxValue - p.MinValue >= 100 && p.MaxValue < 0xFFFE).ToList();
                Reset();
                foreach (var p in Params.Take(3)) Core.PinStore.Set("Lead", p.Name, true);
                Select(Lead);
            }

            public void Select(MachineCore m)
            {
                StudioSelection.For(Core).SelectMachine(m, Window);
                Settle();
                Window.UpdateLayout();
                Settle();
            }

            public MapLayer Layer => Window.MapLayer;

            public IReadOnlyList<DeviceCell> Cells => Window.Inspector.Device.PinnedCells;

            public void Rescan()
            {
                Window.UpdateLayout();
                Layer.Rescan();
            }

            public void Dispose()
            {
                Layer?.Mode.Exit();
                Layer?.Close();
                MidiEngine.SimulatedInputs = null;
                Reset();
                foreach (var p in Params.Take(3)) Core.PinStore.Set("Lead", p.Name, false);
                Close(Window);
            }
        }

        static void Reset()
        {
            Mapping.CancelListening();
            Mapping.Edit((c, s) => { c.Global.Clear(); c.Groups.Clear(); c.NoKeys.Clear(); s.Tracks.Clear(); s.Global = null; });
            Mapping.AutoArm = true;
        }

        static void Send(MidiDevice d, int data)
        {
            Core.SendMIDIInput(d, data);
            Dispatcher.UIThread.RunJobs();
        }

        static MapTarget TargetOf(DeviceCell cell) => MapTarget.Parameter("Lead", cell.Param.Parameter.Name);

        static Point Center(Visual v, Visual to) => v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), to)!.Value;

        // ------------------------------------------------------------------ map mode

        [AvaloniaTest]
        public void MapModeOutlinesTheControlsAClickArmsOneAndTheNextControlMovedMapsIt()
        {
            using var st = new Stage();
            var layer = st.Layer;
            Assert.That(layer.Banner.IsVisible, Is.False);
            st.Window.ToggleMapMode();
            Settle();
            st.Rescan();
            Assert.That(layer.Mode.Active, Is.True);
            Assert.That(layer.Banner.IsVisible, Is.True);
            Assert.That(layer.IsHitTestVisible, Is.True, "nothing under the layer acts in map mode");
            Assert.That(st.Cells, Is.Not.Empty);
            Assert.That(layer.Spots.Select(s => s.Target), Is.SupersetOf(st.Cells.Select(TargetOf)), "every pinned parameter is mappable");
            Assert.That(layer.Spots.Any(s => s.Target.IsCommand && s.Target.Cmd == MapCommand.Play), Is.True, "and so is the Play button");

            var cell = st.Cells[0];
            Click(st.Window, Center(cell, st.Window));
            Assert.That(layer.Mode.Armed, Is.EqualTo(MapTarget.Parameter("Lead", cell.Param.Parameter.Name)), "a click arms it");
            Assert.That(Mapping.IsListening, Is.True);
            Assert.That(layer.BannerMessage, Does.Contain("Listening"));

            Send(Pads, CC(0, 21, 100));
            Assert.That(Mapping.IsListening, Is.False);
            Assert.That(layer.Mode.Armed, Is.Null, "it is mapped; map mode stays on for the next");
            Assert.That(layer.Mode.Active, Is.True);
            st.Rescan();
            Assert.That(layer.Spots.First(s => s.Target.Equals(TargetOf(cell))).Badge, Is.EqualTo("CC21"), "the badge says what it is mapped to, short");
            Assert.That(layer.Spots.First(s => s.Target.Equals(TargetOf(cell))).BadgeFull, Does.Contain("CC 21").And.Contain("Pads"), "its tooltip says it in full, with the device");

            // The next control moved drives it.
            var p = cell.Param.Parameter;
            Send(Pads, CC(0, 21, 0));
            Send(Pads, CC(0, 21, 0));
            Assert.That(p.GetValue(cell.Param.Track), Is.LessThan(cell.Param.Max / 2));
        }

        [AvaloniaTest]
        public void MapModeOutlinesTheMixersFadersPansAndTheMasterABadgeShowsAMappedFaderAndTheBankIsOutlinedInTheMixer()
        {
            using var st = new Stage();
            st.Window.Detail.ShowMixer();
            Settle();
            st.Window.ToggleMapMode();
            Settle();
            st.Rescan();
            var targets = st.Layer.Spots.Select(s => s.Target).ToList();
            Assert.That(targets, Does.Contain(MapTarget.Mixer(MixerControl.Volume, "track:Lead")), "a track's fader is mappable");
            Assert.That(targets, Does.Contain(MapTarget.Mixer(MixerControl.Pan, "track:Lead")), "and its pan");
            Assert.That(targets.Any(t => t.IsMixer && t.Mix == MixerControl.Master), Is.True, "and the Master's fader");

            var fader = MapTarget.Mixer(MixerControl.Volume, "track:Lead");
            Assert.That(Mapping.MapControl(new MapSource("Faders", MapKind.ControlChange, 0, 3), KnobEncoding.Absolute, fader, MapScope.Global).Ok, Is.True);
            st.Rescan();
            Assert.That(st.Layer.Spots.First(s => s.Target.Equals(fader)).Badge, Is.EqualTo("CC3"), "a mapped fader wears its badge");

            // The bank: map a fader onto "track 1 of the bank" and the Mixer outlines the bank's strips.
            Assert.That(Mapping.MapControl(new MapSource("Faders", MapKind.ControlChange, 0, 4), KnobEncoding.Absolute, MapTarget.Mixer(MixerControl.Pan, "", 1), MapScope.Global).Ok, Is.True);
            Settle();
            Assert.That(Mapping.BadgeOf(MapTarget.Mixer(MixerControl.Pan, "track:" + Mapping.BankTrack(1).Name)), Is.EqualTo("CC4"), "the bank's first strip wears the badge of the bank track 1 pan map");
            var strips = st.Window.GetVisualDescendants().OfType<ReBuzz.Studio.Views.Mixer.MixerStripView>().ToList();
            Assert.That(strips.Any(x => x.IsInBank), Is.True, "the strips of the bank are outlined");
            Mapping.SetBank(0, 0);
        }

        [AvaloniaTest]
        public void EscEndsMapModeAndDeleteUnmapsTheArmedControl()
        {
            using var st = new Stage();
            var layer = st.Layer;
            st.Window.ToggleMapMode();
            st.Rescan();
            var cell = st.Cells[1];
            var target = MapTarget.Parameter("Lead", cell.Param.Parameter.Name);
            Mapping.MapControl(new MapSource("Pads", MapKind.ControlChange, 0, 22), KnobEncoding.Absolute, target, MapScope.Global);
            st.Rescan();
            Assert.That(layer.Spots.First(s => s.Target.Equals(TargetOf(cell))).Badge, Is.Not.Null);
            layer.ArmControl(cell);
            Assert.That(layer.Mode.Armed, Is.EqualTo(target));
            st.Window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            st.Window.KeyReleaseQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            Settle();
            Assert.That(Mapping.InfoOf(target), Is.Null, "Delete unmaps what the armed control drives");
            Assert.That(layer.Mode.Armed, Is.Null);
            st.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            st.Window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Settle();
            Assert.That(layer.Mode.Active, Is.False, "Esc ends map mode");
            Assert.That(layer.Banner.IsVisible, Is.False);
            Assert.That(layer.IsHitTestVisible, Is.False);
        }

        [AvaloniaTest]
        public void AControlMappedInThisTrackScopeFillsTheKnobsSlotAndShowsItsNumber()
        {
            using var st = new Stage();
            Mapping.SetKnobGroup("Faders", Enumerable.Range(21, 8).Select(n => new MapSource("Faders", MapKind.ControlChange, 0, n)));
            var layer = st.Layer;
            st.Window.ToggleMapMode();
            st.Rescan();
            Assert.That(layer.Mode.Scope, Is.EqualTo(MapScope.Track), "the selected track's page is the default scope");
            var cell = st.Cells[2];
            layer.ArmControl(cell);
            Send(Faders, CC(0, 24, 60));                                                // knob 4
            st.Rescan();
            Assert.That(layer.Spots.First(s => s.Target.Equals(TargetOf(cell))).Badge, Is.EqualTo("K4"));
            Assert.That(Mapping.PagesOf("track:Lead").CurrentPage.Slots.ContainsKey(3), Is.True);
            layer.Mode.Scope = MapScope.Global;
            var other = st.Cells[0];
            layer.ArmControl(other);
            Send(Pads, CC(0, 9, 10));
            st.Rescan();
            Assert.That(layer.Spots.First(s => s.Target.Equals(TargetOf(other))).Badge, Is.EqualTo("CC9"));
            Assert.That(Mapping.GlobalEntries.Count, Is.EqualTo(1));
        }

        [AvaloniaTest]
        public void TheTransportButtonsAreCommandsAMapModeClickMaps()
        {
            using var st = new Stage();
            var layer = st.Layer;
            st.Window.ToggleMapMode();
            st.Rescan();
            var play = st.Window.Transport.PlayButton;
            Assert.That(layer.ArmControl(play), Is.True);
            Send(Pads, 0x99 | (36 << 8) | (100 << 16));
            st.Rescan();
            Assert.That(Mapping.GlobalEntries.Single().Target.Cmd, Is.EqualTo(MapCommand.Play));
            Assert.That(layer.Spots.First(s => ReferenceEquals(s.Control, play)).Badge, Is.EqualTo("N36"));
            Assert.That(layer.Spots.First(s => ReferenceEquals(s.Control, play)).BadgeFull, Is.EqualTo("Note 36 ch10 · Pads"));
        }

        // ------------------------------------------------------------------ the button and the popover

        [AvaloniaTest]
        public void ThePointerOverAControlShowsAMidiButtonThatOpensThePopover()
        {
            using var st = new Stage();
            var layer = st.Layer;
            Settle();
            st.Window.UpdateLayout();
            Settle();
            var cell = st.Cells[0];                                          // the Inspector remakes its cells when it refreshes: take it after it settled
            st.Rescan();
            for (int attempt = 0; attempt < 5 && layer.Hovered == null; attempt++)
            {
                Settle();
                st.Window.UpdateLayout();
                cell = st.Cells[0];
                st.Rescan();
                st.Window.MouseMove(Center(cell, st.Window) + new Vector(attempt, 4));
                Settle();
            }
            Assert.That(layer.Hovered, Is.Not.Null, "the pointer is over the cell");
            var rect = layer.ButtonRect(cell);
            Assert.That(rect.Width, Is.GreaterThan(10));
            Click(st.Window, new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
            Assert.That(layer.Popover, Is.Not.Null, "the MIDI button opens the popover");
            var pop = layer.Popover;
            Assert.That(pop.TitleText, Does.Contain("Lead"));
            Assert.That(pop.TrackScope.Content!.ToString(), Is.EqualTo("This track: Lead"));
            Assert.That(pop.StateText, Does.StartWith("Not mapped"));

            // Listen in the popover: the next control moved maps, in the scope chosen.
            pop.GlobalScope.IsChecked = true;
            pop.ListenButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.That(pop.StateText, Does.StartWith("Listening"));
            Send(Pads, CC(0, 30, 90));
            Assert.That(pop.StateText, Does.StartWith("Mapped to CC 30"));
            Assert.That(Mapping.GlobalEntries.Single().Source.Number, Is.EqualTo(30));

            // Range, invert and takeover edit the mapping.
            pop.Low.Value = 25m;
            pop.High.Value = 75m;
            pop.InvertBox.IsChecked = true;
            pop.TakeoverBox.SelectedIndex = 2;
            var info = Mapping.InfoOf(MapTarget.Parameter("Lead", cell.Param.Parameter.Name));
            Assert.That(info.Range, Does.StartWith("25"));
            Assert.That(info.Invert, Is.True);
            Assert.That(info.Takeover, Is.EqualTo(Takeover.Scale));
            pop.UnmapButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.That(Mapping.GlobalEntries, Is.Empty);
            pop.DoneButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.That(layer.Popover, Is.Null);
        }

        [AvaloniaTest]
        public void TheBindItemOfTheDeviceMenuOpensThePopoverAndABadgesMenuEditsOrUnmaps()
        {
            using var st = new Stage();
            var cell = st.Cells[0];
            var target = MapTarget.Parameter("Lead", cell.Param.Parameter.Name);
            Mapping.MapControl(new MapSource("Pads", MapKind.ControlChange, 0, 5), KnobEncoding.Absolute, target, MapScope.Global);
            st.Rescan();
            var menu = DeviceMenus.ForParam(st.Lead, cell.Param, null, null);
            var headers = menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToList();
            Assert.That(headers, Does.Contain("_Bind to MIDI Controller"));
            menu.Items.OfType<MenuItem>().First(i => i.Header?.ToString() == "_Bind to MIDI Controller").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Settle();
            Assert.That(st.Layer.Popover, Is.Not.Null);
            Assert.That(st.Layer.Popover.StateText, Does.StartWith("Mapped to CC 5 · Pads"), "the popover says it in full");
            st.Layer.Close();
            Settle();
            // The badge's right click.
            st.Rescan();
            var badge = st.Layer.BadgeRect(cell);
            Assert.That(badge.Width, Is.GreaterThan(10), "the mapped control has a badge");
            st.Window.MouseMove(new Point(badge.X + badge.Width / 2, badge.Y + badge.Height / 2));
            Settle();
            st.Window.MouseDown(new Point(badge.X + badge.Width / 2, badge.Y + badge.Height / 2), MouseButton.Right);
            st.Window.MouseUp(new Point(badge.X + badge.Width / 2, badge.Y + badge.Height / 2), MouseButton.Right);
            Settle();
            Assert.That(st.Layer.BadgeMenu, Is.Not.Null, "right-click a badge");
            var items = st.Layer.BadgeMenu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToList();
            Assert.That(items, Is.EqualTo(new[] { "Edit…", "Unmap" }));
            st.Layer.BadgeMenu.Items.OfType<MenuItem>().Last().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.That(Mapping.InfoOf(target), Is.Null);
        }

        // ------------------------------------------------------------------ the Inspector's page

        [AvaloniaTest]
        public void TheInspectorShowsTheSelectedTracksPageAsKnobsWithPinFallbacksAndDroppedParameters()
        {
            using var st = new Stage();
            Mapping.SetKnobGroup("Faders", Enumerable.Range(21, 8).Select(n => new MapSource("Faders", MapKind.ControlChange, 0, n)));
            Settle();
            var page = st.Window.Inspector.MidiPage;
            page.Refresh();
            Assert.That(page.IsVisible, Is.True);
            Assert.That(page.Slots.Count, Is.EqualTo(8), "a slot per knob of the largest group (8 at least)");
            Assert.That(page.Slots.Take(3).All(s => s.Info.Fallback), Is.True, "three pins: knobs 1 to 3 fall back to them (a grey ring)");
            Assert.That(page.Slots.Skip(3).All(s => s.Info.Target == null), Is.True, "the rest are free");

            var target = MapTarget.Parameter("Lead", st.Params[5].Name);
            Assert.That(page.Slots[4].Assign(target), Is.Null);
            page.Refresh();
            Assert.That(page.Slots[4].Info.Target, Is.EqualTo(target));
            Assert.That(page.Slots[4].Info.Fallback, Is.False);
            Assert.That(Mapping.PagesOf("track:Lead").CurrentPage.Slots[4].Target, Is.EqualTo(target));
            Assert.That(page.Slots[0].Assign(MapTarget.Parameter("Bass", st.Params[0].Name)), Does.Contain("not on the chain"), "another track's machine is refused with a reason");

            // Pages.
            page.AddPageButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            page.Refresh();
            Assert.That(page.PageText, Does.Contain("2/2"));
            Assert.That(page.Slots[4].Info.Target, Is.Null, "a new page is empty");
            page.PreviousPageButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            page.Refresh();
            Assert.That(page.Slots[4].Info.Target, Is.EqualTo(target));

            // Teach.
            page.TeachButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            page.Refresh();
            Assert.That(page.StatusText, Does.StartWith("Turn knob 1"));
            Mapping.FinishTeach();
        }

        [AvaloniaTest]
        public void TheKnobsFollowTheTrackSelectedInTheStudioAndAControllersNextTrackMovesTheStudio()
        {
            using var st = new Stage();
            Mapping.SetKnobGroup("Faders", Enumerable.Range(21, 8).Select(n => new MapSource("Faders", MapKind.ControlChange, 0, n)));
            Mapping.SetSlot("track:Lead", 0, 0, MapTarget.Parameter("Lead", st.Params[0].Name), new MapDrive { Takeover = Takeover.Jump });
            Assert.That(Mapping.SelectedMachine, Is.SameAs(st.Lead), "the studio's selection is the mapping's");
            st.Select(st.Bass);
            Assert.That(Mapping.SelectedTrackId, Is.EqualTo("track:Bass"));
            Mapping.MapControl(new MapSource("Pads", MapKind.Note, 0, 61), KnobEncoding.Absolute, MapTarget.Command(MapCommand.PreviousTrack), MapScope.Global);
            Send(Pads, 0x90 | (61 << 8) | (100 << 16));
            Settle();
            Assert.That(StudioSelection.For(Core).Machine, Is.SameAs(st.Lead), "a controller's previous-track moves the studio's selection");
            st.Window.Transport.Refresh();
            Assert.That(st.Window.Transport.Tracks.NameText, Is.EqualTo("Lead"));
        }

        // ------------------------------------------------------------------ the top bar

        [AvaloniaTest]
        public void TheTopBarShowsTheSelectedTrackWithArrowsAndTheControllersThatFollowIt()
        {
            using var st = new Stage();
            MidiEngine.SimulatedInputs = new[] { "Keys A", "Pads B" };
            Mapping.SetKeysRole("Pads B", false);
            st.Window.Transport.Refresh();
            var bar = st.Window.Transport.Tracks;
            Assert.That(bar.NameText, Is.EqualTo("Lead"));
            Assert.That(bar.FollowersText, Is.EqualTo("1 device"), "a controller left out of the keys role is not counted");
            Assert.That(bar.FollowersTip, Is.EqualTo("Keys A follows this track"));
            Mapping.SetKeysRole("Pads B", true);
            st.Window.Transport.Refresh();
            Assert.That(bar.FollowersText, Is.EqualTo("2 devices"));
            Assert.That(bar.FollowersTip, Is.EqualTo("Keys A, Pads B follow this track"), "who they are is in the tooltip");
            bar.NextButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            st.Window.Transport.Refresh();
            Assert.That(bar.NameText, Is.EqualTo("Bass"));
            Assert.That(StudioSelection.For(Core).Machine, Is.SameAs(st.Bass));
            st.Window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Alt);
            st.Window.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.Alt);
            Settle();
            st.Window.Transport.Refresh();
            Assert.That(bar.NameText, Is.EqualTo("Lead"), "Alt+Up selects the previous track");
            st.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);
            st.Window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);
            Settle();
            st.Window.Transport.Refresh();
            Assert.That(bar.NameText, Is.EqualTo("Bass"), "Alt+Down the next");
        }

        [AvaloniaTest]
        public void AKnobThatDrivesNothingSaysSoInTheTopBar()
        {
            using var st = new Stage();
            Mapping.SetKnobGroup("Faders", Enumerable.Range(21, 8).Select(n => new MapSource("Faders", MapKind.ControlChange, 0, n)));
            Mapping.Select(st.Bass);
            st.Window.Transport.Refresh();
            Send(Faders, CC(0, 27, 50));
            st.Window.Transport.Refresh();
            Assert.That(st.Window.Transport.Tracks.FreeText, Is.EqualTo("K7 is free on Bass"));
        }

        // ------------------------------------------------------------------ MM1b: the look the gallery showed

        [AvaloniaTest]
        public void TheTopBarKeepsItsHeightAt1440WideWhateverTheSelectorSays()
        {
            using var st = new Stage();
            st.Window.Width = 1440;
            MidiEngine.SimulatedInputs = new[] { "Faders", "Pads" };
            var transport = st.Window.Transport;
            double Height()
            {
                st.Window.UpdateLayout();
                Settle();
                st.Window.UpdateLayout();
                return transport.Bounds.Height + ((Avalonia.Controls.Control)transport.Tracks.Parent).Bounds.Height * 1000;       // the top bar's, and the view strip's under it
            }
            transport.Tracks.IsVisible = false;
            double without = Height();                                      // the bar as origin/main has it: no selector
            Assert.That(without, Is.GreaterThan(20));
            Assert.That(transport.Tracks.Parent, Is.Not.SameAs(transport.Child), "the selector is not in the top bar's wrapping row");
            Assert.That(st.Window.Bounds.Width, Is.EqualTo(1440));
            transport.Refresh();
            Assert.That(transport.Tracks.IsVisible, Is.True);
            Assert.That(transport.Tracks.FollowersText, Is.EqualTo("2 devices"));
            var panel = (Avalonia.Controls.WrapPanel)transport.Child;
            string widths = string.Join(", ", panel.Children.Select(c => c.GetType().Name + " " + (int)c.Bounds.Width + "@" + (int)c.Bounds.Y));
            Height();
            Assert.That(transport.Tracks.Chip.Bounds.Width, Is.GreaterThan(30), "the chip is on the screen, with its text");
            Assert.That(transport.Tracks.Chip.Text, Is.EqualTo("2 devices"));
            var sel = transport.Tracks;
            var strip = (Avalonia.Controls.Control)sel.Parent;
            Assert.That(sel.Bounds.Width, Is.GreaterThanOrEqualTo(sel.Chip.Bounds.Right), "the chip lies inside the selector, not past it: selector " + sel.Bounds.Width + ", chip right " + sel.Chip.Bounds.Right);
            Assert.That(sel.Bounds.Right, Is.LessThanOrEqualTo(strip.Bounds.Width + 0.5), "and the selector inside its row");
            Assert.That(Height(), Is.EqualTo(without), "two devices following: still one row: " + widths + " of " + (int)transport.Bounds.Width);
            Mapping.SetSlot("track:Lead", 0, 0, MapTarget.Parameter("Lead", st.Params[0].Name), new MapDrive());
            Mapping.SetKnobGroup("Faders", Enumerable.Range(21, 8).Select(n => new MapSource("Faders", MapKind.ControlChange, 0, n)));
            st.Select(st.Bass);
            Send(Faders, CC(0, 27, 50));
            transport.Refresh();
            Assert.That(transport.Tracks.FreeText, Is.Not.Empty, "the free notice is up");
            Assert.That(Height(), Is.EqualTo(without), "and with the notice, the long track name and the arrows");
            var song = Core.SongCore;
            const string longName = "A very long track name that has to be cut with an ellipsis";
            song.CreateMachine("Neo Poly", null, longName, null, null, null, -1, 0.1f, -0.3f);
            var longOne = song.MachinesList.Last(q => q.Name == longName);
            song.ConnectMachines(longOne, song.MachinesList.First(q => q.DLL.Info.Type == MachineType.Master), new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            st.Select(longOne);
            transport.Refresh();
            Assert.That(transport.Tracks.NameText, Is.EqualTo(longName));
            Assert.That(Height(), Is.EqualTo(without), "a long name is cut, not wrapped: " + string.Join(", ", panel.Children.Select(c => c.GetType().Name + " " + (int)c.Bounds.Width + "@" + (int)c.Bounds.Y)) + " selector: " + string.Join(", ", ((Avalonia.Controls.Panel)transport.Tracks.Child).Children.Select(c => (int)c.Bounds.Width)));
            Assert.That(transport.Tracks.Bounds.Width, Is.LessThan(420), "the selector is a compact group");
        }

        [AvaloniaTest]
        public void TheTopBarOfThe1280WideTestWindowsHasTheHeightItHadWithoutTheSelector()
        {
            using var st = new Stage();
            MidiEngine.SimulatedInputs = new[] { "Faders", "Pads" };
            var transport = st.Window.Transport;
            double Height() { st.Window.UpdateLayout(); Settle(); st.Window.UpdateLayout(); return transport.Bounds.Height + ((Avalonia.Controls.Control)transport.Tracks.Parent).Bounds.Height * 1000; }
            transport.Tracks.IsVisible = false;
            double without = Height();
            transport.Refresh();
            var panel = (Avalonia.Controls.WrapPanel)transport.Child;
            string widths = string.Join(", ", panel.Children.Select(c => c.GetType().Name + " " + (int)c.Bounds.Width + "@" + (int)c.Bounds.Y));
            Assert.That(Height(), Is.EqualTo(without), "1280 wide: " + widths + " of " + (int)transport.Bounds.Width);
        }

        [AvaloniaTest]
        public void AClickOnTheMiddleOfASmallTransportButtonActsAndDoesNotOpenThePopover()
        {
            using var st = new Stage();
            var loop = st.Window.Transport.LoopButton;
            st.Rescan();
            var middle = Center(loop, st.Window);
            st.Window.MouseMove(middle);
            Settle();
            Assert.That(st.Layer.Popover, Is.Null);
            bool was = Core.Looping;
            Click(st.Window, middle);
            Settle();
            Assert.That(st.Layer.Popover, Is.Null, "no popover: the button took its click");
            Assert.That(Core.Looping, Is.Not.EqualTo(was), "Loop toggled");
            Core.Looping = was;
        }

        [AvaloniaTest]
        public void BadgesAreShortAndLieInsideTheirControlWithTheFullSourceInTheTooltip()
        {
            using var st = new Stage();
            Mapping.SetKnobGroup("Faders", Enumerable.Range(21, 8).Select(n => new MapSource("Faders", MapKind.ControlChange, 0, n)));
            Mapping.SetSlot("track:Lead", 0, 2, MapTarget.Parameter("Lead", st.Params[2].Name), new MapDrive());
            Mapping.MapControl(new MapSource("Pads", MapKind.ControlChange, 0, 74), KnobEncoding.Absolute, MapTarget.Parameter("Lead", st.Params[0].Name), MapScope.Global);
            Mapping.MapControl(new MapSource("Pads", MapKind.Note, 9, 36), KnobEncoding.Absolute, MapTarget.Command(MapCommand.Play), MapScope.Global);
            Mapping.MapControl(new MapSource("Pads", MapKind.Note, 9, 37), KnobEncoding.Absolute, MapTarget.Command(MapCommand.Stop), MapScope.Global);
            var layer = st.Layer;
            st.Window.ToggleMapMode();
            st.Rescan();
            var badged = layer.Spots.Where(s => s.Badge != null).ToList();
            Assert.That(badged.Select(s => s.Badge), Is.SupersetOf(new[] { "K3", "CC74", "N36", "N37" }));
            foreach (var spot in badged)
            {
                var rect = layer.BadgeRect(spot.Control);
                Assert.That(spot.Badge.Length, Is.LessThanOrEqualTo(6), "a short form");
                Assert.That(spot.Bounds.Contains(rect.TopLeft) && spot.Bounds.Contains(rect.BottomRight), Is.True,
                    $"the badge '{spot.Badge}' {rect} lies inside its control {spot.Bounds}");
            }
            var play = layer.Spots.First(s => ReferenceEquals(s.Control, st.Window.Transport.PlayButton));
            var stop = layer.Spots.First(s => ReferenceEquals(s.Control, st.Window.Transport.StopButton));
            Assert.That(layer.BadgeRect(play.Control).Intersects(stop.Bounds), Is.False, "the Play button's badge does not reach the next button");
            var slot = layer.Spots.First(s => s.Badge == "K3");
            Assert.That(slot.BadgeFull, Does.StartWith("K3").And.Contain("Lead"));
            // The tooltip says the full source.
            var r = layer.BadgeRect(play.Control);
            st.Window.MouseMove(new Point(r.X + r.Width / 2, r.Y + r.Height / 2));
            Settle();
            Assert.That(layer.TipText, Is.EqualTo("Note 36 ch10 · Pads"));
            st.Window.MouseMove(new Point(1, 1));
            Settle();
            Assert.That(layer.TipText, Is.Null);
        }

        [AvaloniaTest]
        public void TheListeningChipSitsInsideTheCardNotOverTheGroupHeaderAndTheBannerIsOneLine()
        {
            using var st = new Stage();
            st.Window.Width = 1440;
            var layer = st.Layer;
            st.Window.ToggleMapMode();
            st.Rescan();
            var cell = st.Cells[0];
            Assert.That(layer.ArmControl(cell), Is.True);
            st.Rescan();
            var spot = layer.Spots.First(s => ReferenceEquals(s.Control, cell));
            var chip = layer.ListeningRect(cell);
            Assert.That(spot.Bounds.Contains(chip.TopLeft) && spot.Bounds.Contains(chip.BottomRight), Is.True, $"the chip {chip} is inside the card {spot.Bounds}, below the header above it");
            Assert.That(layer.BannerMessage, Does.StartWith("Listening for Lead").And.Contain("move any control").And.Contain("Delete: unmap").And.Contain("Esc: done"));
            st.Window.UpdateLayout();
            Assert.That(layer.BannerLine.Bounds.Height, Is.LessThan(24), "one line at 1440: " + layer.BannerMessage);
            layer.Mode.Disarm();
            st.Window.UpdateLayout();
            Assert.That(layer.BannerLine.Bounds.Height, Is.LessThan(24), "and not listening: " + layer.BannerMessage);
        }

        [AvaloniaTest]
        public void TheInspectorsSlotsShowTwoLinesOfTheParameterNameAndTheFullNameInTheTooltip()
        {
            using var st = new Stage();
            Mapping.SetKnobGroup("Faders", Enumerable.Range(21, 8).Select(n => new MapSource("Faders", MapKind.ControlChange, 0, n)));
            var page = st.Window.Inspector.MidiPage;
            Mapping.SetSlot("track:Lead", 0, 4, MapTarget.Parameter("Lead", st.Params[5].Name), new MapDrive());
            page.Refresh();
            var slot = page.Slots[4];
            Assert.That(slot.NameBlock.MaxLines, Is.EqualTo(2));
            Assert.That(slot.NameBlock.TextWrapping, Is.EqualTo(Avalonia.Media.TextWrapping.Wrap));
            Assert.That(slot.NameBlock.Text, Is.EqualTo(st.Params[5].Name));
            Assert.That(ToolTip.GetTip(slot) as string, Does.Contain("Lead · " + st.Params[5].Name), "the full name is the tooltip");
        }

        [AvaloniaTest]
        public void ThePopoversRangeIsOneRowOfTwoFieldsWithTheSentenceBelow()
        {
            using var st = new Stage();
            var cell = st.Cells[0];
            var pop = st.Layer.Open(cell);
            Settle();
            Assert.That(pop, Is.Not.Null);
            Assert.That(pop.Low.Parent, Is.SameAs(pop.High.Parent), "from and to share a row");
            Assert.That(pop.Low.Parent, Is.InstanceOf<StackPanel>().With.Property("Orientation").EqualTo(Avalonia.Layout.Orientation.Horizontal));
            pop.Low.Value = 20m;
            pop.High.Value = 60m;
            Assert.That(pop.RangeSentence, Is.EqualTo("20% to 60% of the parameter's range"));
            st.Layer.Close();
        }

        [AvaloniaTest]
        public void OutlinesOfACardHalfOutOfTheDevicePanelStopAtItsEdgeAndNeverReachTheInspector()
        {
            using var st = new Stage();
            st.Window.Width = 1440;
            st.Window.Height = 900;
            st.Window.Detail.ShowDevice();
            Settle();
            st.Window.UpdateLayout();
            var page = DeviceViewOf(st.Window);
            Assert.That(page, Is.Not.Null, "the Device panel is open");
            var scroller = page.Cards.Scroller;
            Assert.That(ScrollHalfACardOut(st.Window, scroller), Is.True, "the panel scrolls so a card is half out of view");
            st.Window.ToggleMapMode();
            st.Rescan();
            var view = new Rect(scroller.TranslatePoint(default, st.Layer)!.Value, new Size(scroller.Viewport.Width, scroller.Viewport.Height));
            var inspector = new Rect(st.Window.Inspector.TranslatePoint(default, st.Layer)!.Value, st.Window.Inspector.Bounds.Size);
            var inPanel = st.Layer.Spots.Where(sp => scroller.IsVisualAncestorOf(sp.Control)).ToList();
            Assert.That(inPanel, Is.Not.Empty);
            foreach (var sp in inPanel)
            {
                Assert.That(view.Inflate(0.5).Contains(sp.Bounds.TopLeft) && view.Inflate(0.5).Contains(sp.Bounds.BottomRight), Is.True, $"outline {sp.Bounds} of {sp.Target.Text} stays in the Device panel's view {view}");
                Assert.That(sp.Bounds.Intersects(inspector), Is.False, $"outline {sp.Bounds} does not spill over the Inspector {inspector}");
            }
        }

        internal static ReBuzz.Studio.Views.Device.DeviceView DeviceViewOf(MainWindow window) =>
            (window.Detail.DevicePage as ReBuzz.Studio.Views.Device.DeviceDock)?.Page?.Parameters;

        /// <summary>Scrolls the Device panel (down, or sideways when the cards flow in columns) so a card is cut by its edge, about half in view. False when the panel does not scroll.</summary>
        internal static bool ScrollHalfACardOut(MainWindow window, ScrollViewer scroller)
        {
            Settle();
            double maxY = scroller.Extent.Height - scroller.Viewport.Height, maxX = scroller.Extent.Width - scroller.Viewport.Width;
            if (maxY <= 8 && maxX <= 8) return false;
            var page = DeviceViewOf(window);
            var host = (Visual)scroller.Content;
            foreach (var shown in page.Cards.Shown)
            {
                var at = shown.Control.TranslatePoint(default, host);
                if (at == null) continue;
                double my = at.Value.Y + shown.Control.Bounds.Height / 2, mx = at.Value.X + shown.Control.Bounds.Width / 2;
                if (maxY > 8 && my > 4 && my <= maxY) { scroller.Offset = new Vector(0, my); break; }
                if (maxX > 8 && mx > 4 && mx <= maxX) { scroller.Offset = new Vector(mx, 0); break; }
            }
            Settle();
            return scroller.Offset.Y > 0 || scroller.Offset.X > 0;
        }

        // ------------------------------------------------------------------ the screens (saved as pictures for the gallery's look)

        /// <summary>Draws the MM1 screens: map mode with badges and a control listening, the popover, the Inspector's page, the top bar. The gallery draws them on the demo song.</summary>
        [AvaloniaTest]
        public void TheScreensAreDrawn()
        {
            using var st = new Stage();
            st.Window.Width = 1440;
            st.Window.Height = 900;
            MidiEngine.SimulatedInputs = new[] { "Faders", "Pads" };
            Mapping.SetKnobGroup("Faders", Enumerable.Range(21, 8).Select(n => new MapSource("Faders", MapKind.ControlChange, 0, n)));
            Mapping.SetSlot("track:Lead", 0, 3, MapTarget.Parameter("Lead", st.Params[4].Name), new MapDrive { Takeover = Takeover.Jump });
            Mapping.MapControl(new MapSource("Pads", MapKind.ControlChange, 0, 74), KnobEncoding.Absolute, MapTarget.Parameter("Lead", st.Params[0].Name), MapScope.Global);
            Mapping.MapControl(new MapSource("Pads", MapKind.Note, 9, 36), KnobEncoding.Absolute, MapTarget.Command(MapCommand.Play), MapScope.Global);
            st.Window.Detail.ShowDevice();
            st.Window.Transport.Refresh();
            Settle();
            st.Rescan();
            Studio.Screenshot(st.Window, "mm1-1-badges");
            st.Window.ToggleMapMode();
            st.Rescan();
            st.Layer.ArmTarget(MapTarget.Parameter("Lead", st.Params[1].Name));
            Settle();
            st.Rescan();
            Assert.That(Studio.Screenshot(st.Window, "mm1-2-map-mode"), Does.EndWith(".png"));
            var scroller = DeviceViewOf(st.Window)?.Cards.Scroller;
            if (scroller != null && ScrollHalfACardOut(st.Window, scroller))
            {
                st.Rescan();
                Studio.Screenshot(st.Window, "mm1-2b-map-mode-scrolled");
                scroller.Offset = default;
                Settle();
                st.Rescan();
            }
            st.Layer.Mode.Exit();
            st.Layer.OpenForTarget(MapTarget.Parameter("Lead", st.Params[0].Name));
            Settle();
            Studio.Screenshot(st.Window, "mm1-3-popover");
            st.Window.Transport.Refresh();
            st.Layer.Close();
            Settle();
            st.Window.Inspector.MidiPage.Refresh();
            Studio.Screenshot(st.Window, "mm1-4-inspector");
        }
    }
}
