// Fork (docs/specs/ux-overhaul.md, the parameter panel redesign PP1): the Device panel's layout D, headless. Cards of knobs (splitting, the
// chips' jump, search, Changed only, the Knobs | List choice per machine type), the docked row and the floating wrap, the knobs' gestures,
// the retired parameter window's entry points, the Inspector's macros and pins, and the pins in the song (.nbl, undo).

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Device;
using ReBuzz.Studio.Views.Inspector;
using NeoBuzz.TestSupport;
using static ReBuzz.Studio.Tests.AudioHygieneTests;
using static ReBuzz.Studio.Tests.DeviceTestKit;
using Mode = ReBuzz.Studio.Views.Device.DeviceView.DeviceMode;

namespace ReBuzz.Studio.Tests
{
    public class UxPp1PanelTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static void Settle() => MachineViewTests.Settle();

        static MachineCore Add(string gear, string name)
        {
            Core.SongCore.CreateMachine(gear, null, name, null, null, null, -1, 0.3f, 0.3f);
            var machine = Core.SongCore.MachinesList.FirstOrDefault(m => m.Name == name);
            Assert.That(machine, Is.Not.Null, $"{gear} loads");
            return machine!;
        }

        static void Remove(MachineCore machine)
        {
            Core.UiHost.ReleaseMachine(machine);
            Core.SongCore.DeleteMachines(new[] { machine });
            Dispatcher.UIThread.RunJobs();
        }

        static (Window Window, DevicePage Page, DeviceView View) Stage(MachineCore machine, bool floating, double width = 900, double height = 560, Mode mode = Mode.Knobs)
        {
            var page = new DevicePage(machine, floating);
            page.SetEditor(false, remember: false);
            page.Parameters.SetMode(mode, remember: false);
            var window = new Window { Width = width, Height = height, Content = page };
            window.Show();
            MachineViewTests.Settle();
            window.UpdateLayout();
            return (window, page, page.Parameters);
        }

        static void Settle(Window w)
        {
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
        }

        // ------------------------------------------------------------------ cards

        [AvaloniaTest]
        public void ASectionOverTwelveParametersSplitsIntoTitledCards()
        {
            var m = Add("Dye Essence", "PP1 Split");
            try
            {
                var layout = DeviceLayout.Build(m);
                var cards = layout.Cards("");
                Assert.That(cards, Is.Not.Empty);
                Assert.That(cards.All(c => c.Knobs.Count() <= DeviceCard.Capacity), "no card holds more than 3 rows of 4 knobs");
                var split = cards.Where(c => c.Count > 1).ToList();
                Assert.That(split, Is.Not.Empty, "Dye Essence has sections over 12");
                Assert.That(split.All(c => Regex.IsMatch(c.Title, @" \d+/\d+$")), "\"Osc A 1/3\"");
                var first = split.First();
                Assert.That(cards.Where(c => c.Section == first.Section).Select(c => c.Index), Is.EqualTo(Enumerable.Range(0, first.Count)));
                Assert.That(cards.Where(c => c.Section == first.Section).SelectMany(c => c.Params).Count(), Is.EqualTo(first.Section.Params.Count), "every parameter is on a card");
            }
            finally { Remove(m); }
        }

        [Test]
        public void APrefixNeedsThreeParametersToGroupAndElseItIsOneParametersGroup()
        {
            var two = DeviceLayout.Plan(new[] { "A cutoff", "A reso", "B cutoff", "B reso", "Volume" });
            Assert.That(two.Source, Is.EqualTo("section"), "two parameters do not make a group");
            var three = DeviceLayout.Plan(new[] { "A cutoff", "A reso", "A env", "Volume", "Pan" });
            Assert.That(three.Source, Is.EqualTo("prefix"));
            Assert.That(three.Groups.Select(g => g.Title), Does.Contain("A"));
            Assert.That(DeviceLayout.OneGroup, Is.EqualTo("Parameters"));
        }

        [AvaloniaTest]
        public void TheKnobsAreCardsASectionEachAndTheTracksTableIsTheLastCard()
        {
            var m = Add("Keys Test", "PP1 Tracks");
            m.TrackCount = 3;
            var (window, page, view) = Stage(m, floating: true);
            try
            {
                var cards = view.Cards;
                Assert.That(cards.TableCard, Is.Not.Null, "track parameters stay a table, after the cards");
                var host = cards.Scroller.Content as Panel;
                Assert.That(host!.Children.Last(), Is.SameAs(cards.TableCard));
                Assert.That(cards.Table.Embedded, Is.True);
            }
            finally { window.Close(); Remove(m); }
        }

        [AvaloniaTest]
        public void TheDockedCardsAreOneRowScrollingSidewaysAndTheFloatingOnesWrap()
        {
            var m = Add("Neo Poly", "PP1 Rows");
            try
            {
                var (docked, dpage, dview) = Stage(m, floating: false, width: 700, height: 420);
                try
                {
                    Assert.That(dview.Cards.Sideways, Is.True);
                    var ys = dview.Cards.Shown.Select(s => Math.Round(s.Control.TranslatePoint(new Point(0, 0), dview.Cards.Scroller)!.Value.Y)).Distinct().ToList();
                    Assert.That(ys.Count, Is.EqualTo(1), "one row of cards");
                    Assert.That(dview.Cards.Scroller.Extent.Width, Is.GreaterThan(dview.Cards.Scroller.Viewport.Width), "it scrolls sideways");
                }
                finally { docked.Close(); }
                var (floating, fpage, fview) = Stage(m, floating: true, width: 700, height: 420);
                try
                {
                    Assert.That(fview.Cards.Sideways, Is.False);
                    var ys = fview.Cards.Shown.Select(s => Math.Round(s.Control.TranslatePoint(new Point(0, 0), fview.Cards.Scroller)!.Value.Y)).Distinct().ToList();
                    Assert.That(ys.Count, Is.GreaterThan(1), "the cards wrap onto rows");
                    Assert.That(fview.Cards.Scroller.Extent.Width, Is.LessThanOrEqualTo(fview.Cards.Scroller.Viewport.Width + 1), "and do not scroll sideways");
                }
                finally { floating.Close(); }
            }
            finally { Remove(m); }
        }

        [AvaloniaTest]
        public void AChipJumpsToItsSectionAndTheChipsFollowTheSearch()
        {
            var m = Add("Neo Poly", "PP1 Chips");
            var (window, page, view) = Stage(m, floating: true, width: 720, height: 360);
            try
            {
                var chips = view.ChipButtons.OfType<Button>().ToList();
                var sections = view.Layout.Chips("");
                Assert.That(chips.Count, Is.EqualTo(sections.Count), "a chip per section");
                Assert.That(chips.Count, Is.GreaterThan(3));
                var target = sections[sections.Count - 2];
                var chip = chips.Single(c => (string)c.Content! == target.Name);
                Assert.That(view.Cards.Scroller.Offset.Y, Is.EqualTo(0));
                chip.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle(window);
                Assert.That(view.Cards.Scroller.Offset.Y, Is.GreaterThan(0), "it scrolled");
                var card = view.Cards.Shown.First(s => s.Card.Section == target).Control;
                double top = card.TranslatePoint(new Point(0, 0), view.Cards.Scroller)!.Value.Y;
                Assert.That(top, Is.InRange(-2, view.Cards.Scroller.Viewport.Height - 20), "its card is in view");

                view.Query = sections[0].Params[0].Name;
                Settle(window);
                Assert.That(view.ChipButtons.Count, Is.LessThan(chips.Count), "the search leaves fewer chips");
            }
            finally { window.Close(); Remove(m); }
        }

        [AvaloniaTest]
        public void SearchFiltersCardsAndChangedOnlyKeepsWhatMoved()
        {
            var m = Add("Neo Poly", "PP1 Filter");
            var (window, page, view) = Stage(m, floating: true);
            try
            {
                int all = view.Cards.Shown.Sum(s => s.Card.Params.Count);
                view.Query = "cutoff";
                Settle(window);
                var shown = view.Cards.Shown.SelectMany(s => s.Card.Params).ToList();
                Assert.That(shown, Is.Not.Empty.And.Count.LessThan(all));
                Assert.That(shown.All(p => p.Matches(new[] { "cutoff" })), "only what matches");
                view.Query = "";

                var cutoff = Param(view, "F1 cutoff");
                DeviceEdits.Set(Core, cutoff, cutoff.Value == cutoff.Max ? cutoff.Min : cutoff.Max);
                view.ChangedOnly = true;
                Settle(window);
                var changed = view.Cards.Shown.SelectMany(s => s.Card.Params).ToList();
                Assert.That(changed, Does.Contain(cutoff));
                Assert.That(changed.All(p => p.IsChanged), "only what is off its default");
                Assert.That(changed.Count, Is.LessThan(all));
                DeviceEdits.Reset(Core, cutoff);
                view.ChangedOnly = false;
                view.ChangedOnly = true;
                Settle(window);
                Assert.That(view.Cards.Shown.SelectMany(s => s.Card.Params).Contains(cutoff), Is.False, "back at its default: gone");
            }
            finally { window.Close(); Remove(m); }
        }

        [AvaloniaTest]
        public void TheListOrKnobsChoiceIsRememberedPerMachineType()
        {
            var a = Add("Neo Filter", "PP1 Mode A");
            var b = Add("Neo Filter", "PP1 Mode B");
            var poly = Add("Neo Poly", "PP1 Mode Poly");
            string keyF = DeviceView.ModeKey(a), keyP = DeviceView.ModeKey(poly);
            using var restore = Studio.AfterTest(() => { DevicePage.Write(keyF, "Knobs"); DevicePage.Write(keyP, "Knobs"); });
            try
            {
                Assert.That(new DeviceView(a).Mode, Is.EqualTo(Mode.Knobs), "Knobs by default");
                new DeviceView(a).SetMode(Mode.List, remember: true);
                Assert.That(new DeviceView(b).Mode, Is.EqualTo(Mode.List), "another Neo Filter opens as the first was left");
                Assert.That(new DeviceView(poly).Mode, Is.EqualTo(Mode.Knobs), "another machine type keeps its own");
                new DeviceView(b).SetMode(Mode.Knobs, remember: true);
                Assert.That(new DeviceView(a).Mode, Is.EqualTo(Mode.Knobs));
            }
            finally { Remove(a); Remove(b); Remove(poly); }
        }

        // ------------------------------------------------------------------ the controls

        [AvaloniaTest]
        public void AKnobDragIsOneUndoStepAndADoubleClickTypesAValue()
        {
            var m = Add("Neo Filter", "PP1 Knob");
            var (window, page, view) = Stage(m, floating: true);
            try
            {
                var cutoff = Param(view, "Cutoff");
                var cell = Cell(window, view, cutoff);
                var knob = (DeviceKnob)cell.Face;
                int before = cutoff.Value;
                var steps = Core.SongCore.ActionStack.Actions.Count();
                var at = Centre(window, knob);
                window.MouseDown(at, MouseButton.Left);
                window.MouseMove(at + new Point(0, -30), RawInputModifiers.LeftMouseButton);
                window.MouseMove(at + new Point(0, -60), RawInputModifiers.LeftMouseButton);
                window.MouseUp(at + new Point(0, -60), MouseButton.Left);
                Settle(window);
                Assert.That(cutoff.Value, Is.GreaterThan(before), "up raises it");
                Assert.That(Core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(steps + 1), "the drag is one undo step");
                Core.SongCore.ActionStack.Undo();
                Assert.That(cutoff.Value, Is.EqualTo(before));

                // Double click: the value editor opens over the knob (not a reset); a typed value sets it.
                MachineViewTests.DoubleClick(window, at);
                Settle(window);
                Assert.That(cell.IsTyping, Is.True);
                cell.Editor.Text = "raw:" + (cutoff.Min + (cutoff.Max - cutoff.Min) / 2);
                Assert.That(cell.Commit(), Is.True, cell.TypeError);
                Assert.That(cutoff.Value, Is.EqualTo(cutoff.Min + (cutoff.Max - cutoff.Min) / 2));
                cell.Editor.Text = "not a value at all";
                cell.BeginType();
                cell.Editor.Text = "not a value at all";
                Assert.That(cell.Commit(), Is.False, "a bad value says why and stays open");
                Assert.That(cell.TypeError, Is.Not.Null);
                cell.Close();
            }
            finally { window.Close(); Remove(m); }
        }

        [AvaloniaTest]
        public void AChoiceIsAStripAndASwitchAToggleAndTheMenuPins()
        {
            var m = Add("Neo Poly", "PP1 Choices");
            var (window, page, view) = Stage(m, floating: true, height: 900);
            try
            {
                var choices = view.Layout.All.Where(p => !p.IsTrack && p.Kind != DeviceKind.Slider).ToList();
                Assert.That(choices.Any(p => p.Kind == DeviceKind.Toggle), "a switch");
                var toggle = choices.First(p => p.Kind == DeviceKind.Toggle);
                var cell = Cell(window, view, toggle);
                var face = (DeviceChoice)cell.Face;
                Assert.That(face.Mode, Is.EqualTo(DeviceChoice.Face.Toggle));
                int was = toggle.Value;
                window.MouseDown(face.TranslatePoint(face.CellBounds(0).Center, window)!.Value, MouseButton.Left);
                window.MouseUp(face.TranslatePoint(face.CellBounds(0).Center, window)!.Value, MouseButton.Left);
                Assert.That(toggle.Value, Is.Not.EqualTo(was), "a click switches it");
                Core.SongCore.ActionStack.Undo();
                Assert.That(toggle.Value, Is.EqualTo(was));

                // The menu: Reset, Pin, MIDI.
                var slider = Param(view, "F1 cutoff");
                var menu = DeviceMenus.ForParam(m, slider, null, null);
                var headers = menu.Items.OfType<MenuItem>().Select(i => (string)i.Header!).ToList();
                Assert.That(headers, Does.Contain("_Pin to Inspector").And.Contain("_Reset to Default").And.Contain("_Bind to MIDI Controller"));
            }
            finally { window.Close(); Remove(m); }
        }

        // ------------------------------------------------------------------ the old window's entry points

        [AvaloniaTest]
        public void EveryOldEntryPointOpensTheDevicePanelFloating()
        {
            var m = Add("Keys Test", "PP1 Retired");
            try
            {
                Core.UiHost.OpenParameterWindow(m, null);                       // "Parameters..."
                var w = StudioMachineUiHost.ParameterWindowOf(m);
                Assert.That(w, Is.Not.Null);
                Assert.That(StudioMachineUiHost.ContentOf(w), Is.InstanceOf<DevicePage>(), "the Device panel, not a window of rows");
                Assert.That(DeviceDock.IsFloating(m), Is.True);
                Assert.That(Core.UiHost.GetWindowState(m).ParameterWindow.Width, Is.GreaterThan(0), "its place is saved in the song as the parameter window's was");
                Core.UiHost.CloseWindows(m);
                Settle();
                Assert.That(StudioMachineUiHost.ParameterWindowOf(m), Is.Null);

                Core.UiHost.OpenGui(m, null);                                   // a double click on a machine with no editor
                var gui = StudioMachineUiHost.ParameterWindowOf(m) ?? StudioMachineUiHost.EditorWindowOf(m);
                Assert.That(gui, Is.Not.Null);
                Core.UiHost.CloseWindows(m);

                Core.UiHost.OpenParameterWindow(m, new WindowPlacement(60, 60, 640, 400));   // a song's saved window
                var placed = StudioMachineUiHost.ParameterWindowOf(m)!;
                Assert.That(placed.Width, Is.EqualTo(640).Within(1));
                Assert.That(placed.CanResize, Is.True, "the floating window stays resizable");
                Core.UiHost.CloseWindows(m);

                var (window, page, view) = Stage(m, floating: true);
                try
                {
                    Assert.That(page.GetType().GetProperty("ClassicButton"), Is.Null, "no \"Classic...\" button");
                    Assert.That(typeof(DevicePage).Assembly.GetType("ReBuzz.Studio.Parameters.ParameterWindowView"), Is.Null, "the old classes are gone");
                    Assert.That(page.PresetsButton.IsVisible, Is.True, "the presets live on the page");
                }
                finally { window.Close(); }
            }
            finally { Remove(m); }
        }

        // ------------------------------------------------------------------ the Inspector

        [AvaloniaTest]
        public void TheInspectorShowsTheMacrosAndThePinnedParameters()
        {
            var m = Add("Neo Poly", "PP1 Inspector");
            var host = new Window { Width = 320, Height = 600 };
            try
            {
                var device = new InspectorDevice(Core);
                host.Content = new ScrollViewer { Content = device };
                host.Show();
                device.Show(m);
                Settle(host);
                Assert.That(device.MacroCells, Is.Not.Empty.And.Count.LessThanOrEqualTo(DeviceLayout.MacroCount), "the macros");
                Assert.That(device.PinnedCells, Is.Empty);

                var layout = DeviceLayout.Build(m);
                var names = layout.All.Where(p => p.CanPin).Take(MachinePinStore.Max + 1).Select(p => p.Parameter.Name).ToList();
                foreach (var n in names.Take(MachinePinStore.Max)) Assert.That(Core.PinStore.Set(m.Name, n, true), Is.True);
                Assert.That(Core.PinStore.Set(m.Name, names.Last(), true), Is.False, "a ninth pin is refused");
                Settle(host);
                Assert.That(device.PinnedCells.Select(c => c.Param.Parameter.Name), Is.EqualTo(names.Take(MachinePinStore.Max)), "in the order pinned");
                Core.PinStore.Set(m.Name, names[0], false);
                Settle(host);
                Assert.That(device.PinnedCells.Count, Is.EqualTo(MachinePinStore.Max - 1), "unpinned");
            }
            finally { host.Close(); Core.PinStore.Clear(); Remove(m); }
        }

        [AvaloniaTest]
        public void ThePanelDrawsDockedFloatingAndInTheInspector()
        {
            foreach (var (gear, slug) in new[] { ("Neo Filter", "neo-filter"), ("Neo Poly", "neo-poly"), ("Neo Drums", "neo-drums"), ("Dye Essence", "dye-essence") })
            {
                var m = Add(gear, "PP1 Shot " + gear);
                try
                {
                    var (docked, dpage, dview) = Stage(m, floating: false, width: 1100, height: 330);
                    Studio.Screenshot(docked, "pp1-docked-" + slug);
                    docked.Close();
                    var (floating, fpage, fview) = Stage(m, floating: true, width: 720, height: 620);
                    Studio.Screenshot(floating, "pp1-floating-" + slug);
                    floating.Close();
                }
                finally { Remove(m); }
            }
            var poly = Add("Neo Poly", "PP1 Shot Inspector");
            var host = new Window { Width = 320, Height = 420 };
            try
            {
                foreach (var p in DeviceLayout.Build(poly).All.Where(p => p.CanPin).Take(4)) Core.PinStore.Set(poly.Name, p.Parameter.Name, true);
                var device = new InspectorDevice(Core);
                host.Content = new Border { Padding = new Thickness(12), Background = ReBuzz.Studio.Shell.Design.StudioTokens.SurfacePanelBrush, Child = device };
                host.Show();
                device.Show(poly);
                Settle(host);
                Studio.Screenshot(host, "pp1-inspector");
            }
            finally { host.Close(); Core.PinStore.Clear(); Remove(poly); }
        }

        // ------------------------------------------------------------------ pins in the song

        [AvaloniaTest]
        [TestCase(".nbl")]
        public void PinsSurviveASaveAndAnOpenAndUndoTakesThemBack(string extension)
        {
            NewSong();
            string path = TestTemp.Path("pins-" + Guid.NewGuid().ToString("N") + extension);
            try
            {
                var m = Add("Neo Filter", "PP1 Pins");
                var layout = DeviceLayout.Build(m);
                string a = layout.All.First(p => p.Name == "Cutoff").Parameter.Name, b = layout.All.First(p => p.Name == "Volume" || p.Name == "Output" || p.Name == "Resonance").Parameter.Name;
                Assert.That(Core.PinStore.Set("PP1 Pins", a, true), Is.True);
                Assert.That(Core.PinStore.Set("PP1 Pins", b, true), Is.True);
                Core.SongCore.ActionStack.Undo();
                Assert.That(Core.PinStore.Get("PP1 Pins"), Is.EqualTo(new[] { a }), "undo takes the last pin back");
                Core.SongCore.ActionStack.Redo();
                Assert.That(Core.PinStore.Get("PP1 Pins"), Is.EqualTo(new[] { a, b }), "redo");
                Core.SaveSongCopy(path);
                NewSong();
                Assert.That(Core.PinStore.Machines, Is.Empty, "a new song has none");
                Dialogs.Responder = (mm, c, bb) => UserAnswer.No;
                Core.Modified = false;
                Core.OpenSongFile(path);
                Pump();
                Assert.That(Core.PinStore.Get("PP1 Pins"), Is.EqualTo(new[] { a, b }), extension + ": the pins come back, in order");
            }
            finally
            {
                Dialogs.Responder = null;
                try { File.Delete(path); } catch (IOException) { }
                NewSong();
            }
        }

        [AvaloniaTest]
        public void TheMachinePinToolPinsListsAndRefusesATrackParameterAndATenthPin()
        {
            NewSong();
            try
            {
                var m = Add("Neo Poly", "PP1 Tool");
                var globals = DeviceLayout.Build(m).All.Where(p => p.CanPin).Select(p => p.Parameter.Name).Take(MachinePinStore.Max + 1).ToList();
                var r = Call("machine_pin", new JsonObject { ["machine"] = "PP1 Tool", ["param"] = globals[0] })["structuredContent"]!.AsObject();
                Assert.That(r["changed"]!.GetValue<bool>(), Is.True);
                Assert.That(r["pinned"]!.AsArray().Select(x => x!.GetValue<string>()), Is.EqualTo(new[] { globals[0] }));
                Assert.That(Core.PinStore.IsPinned("PP1 Tool", globals[0]), Is.True);
                var list = Call("machine_pin", new JsonObject { ["machine"] = "PP1 Tool" })["structuredContent"]!.AsObject();
                Assert.That(list["pinned"]!.AsArray().Count, Is.EqualTo(1));
                Call("machine_pin", new JsonObject { ["machine"] = "PP1 Tool", ["param"] = globals[0], ["pinned"] = false });
                Assert.That(Core.PinStore.Get("PP1 Tool"), Is.Empty);
                foreach (var n in globals.Take(MachinePinStore.Max)) Call("machine_pin", new JsonObject { ["machine"] = "PP1 Tool", ["param"] = n });
                var refused = CallRaw("machine_pin", new JsonObject { ["machine"] = "PP1 Tool", ["param"] = globals.Last() });
                Assert.That(refused["isError"]?.GetValue<bool>(), Is.True, "a ninth pin is refused");
            }
            finally { NewSong(); }
        }

        static JsonObject CallRaw(string tool, JsonObject args)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            return task.GetAwaiter().GetResult()!["result"]!.AsObject();
        }

        static JsonObject Call(string tool, JsonObject args)
        {
            var result = CallRaw(tool, args);
            Assert.That(result["isError"]?.GetValue<bool>() == true, Is.False, result.ToJsonString());
            return result;
        }
    }
}
