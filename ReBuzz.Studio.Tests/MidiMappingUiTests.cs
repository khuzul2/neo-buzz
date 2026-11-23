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
            Assert.That(layer.Spots.First(s => s.Target.Equals(TargetOf(cell))).Badge, Does.Contain("CC 21").And.Contain("Pads"), "the badge says what it is mapped to");

            // The next control moved drives it.
            var p = cell.Param.Parameter;
            Send(Pads, CC(0, 21, 0));
            Send(Pads, CC(0, 21, 0));
            Assert.That(p.GetValue(cell.Param.Track), Is.LessThan(cell.Param.Max / 2));
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
            Assert.That(layer.Spots.First(s => s.Target.Equals(TargetOf(other))).Badge, Is.EqualTo("CC 9 · Pads"));
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
            Assert.That(layer.Spots.First(s => ReferenceEquals(s.Control, play)).Badge, Does.StartWith("Note 36"));
        }

        // ------------------------------------------------------------------ the button and the popover

        [AvaloniaTest]
        public void ThePointerOverAControlShowsAMidiButtonThatOpensThePopover()
        {
            using var st = new Stage();
            var layer = st.Layer;
            var cell = st.Cells[0];
            st.Rescan();
            st.Window.MouseMove(Center(cell, st.Window) + new Vector(0, 4));
            Settle();
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
            pop.Low.Value = 25;
            pop.High.Value = 75;
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
            Assert.That(st.Layer.Popover.StateText, Does.StartWith("Mapped to CC 5"));
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
            Assert.That(bar.FollowersText, Is.EqualTo("Keys A follows"), "a controller left out of the keys role is not listed");
            Mapping.SetKeysRole("Pads B", true);
            st.Window.Transport.Refresh();
            Assert.That(bar.FollowersText, Is.EqualTo("Keys A, Pads B follow"));
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

        // ------------------------------------------------------------------ the screens (saved as pictures for the gallery's look)

        /// <summary>Draws the MM1 screens: map mode with badges and a control listening, the popover, the Inspector's page, the top bar. The gallery draws them on the demo song.</summary>
        [AvaloniaTest]
        public void TheScreensAreDrawn()
        {
            using var st = new Stage();
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
            st.Layer.Mode.Exit();
            st.Layer.OpenForTarget(MapTarget.Parameter("Lead", st.Params[0].Name));
            Settle();
            Studio.Screenshot(st.Window, "mm1-3-popover");
            st.Layer.Close();
            Settle();
            st.Window.Inspector.MidiPage.Refresh();
            Studio.Screenshot(st.Window, "mm1-4-inspector");
        }
    }
}
