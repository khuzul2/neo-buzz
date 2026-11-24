// Fork (docs/specs/ux-overhaul.md, the parameter panel redesign PP2): the polish after PP1's review (prefix stripping, drop-downs for many
// choices, the docked height, card packing, the floating width, the chips' overflow, one colour, sentence case) and what PP1 dropped (the
// menu's Automate and Modulate, Lock and Hide with Show hidden, Randomize and Morph, the computer's keys playing the machine, pins that follow
// a rename).

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Studio.Parameters;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Device;
using ReBuzzTests.Automation;
using static ReBuzz.Studio.Tests.AudioHygieneTests;
using static ReBuzz.Studio.Tests.DeviceTestKit;
using Mode = ReBuzz.Studio.Views.Device.DeviceView.DeviceMode;

namespace ReBuzz.Studio.Tests
{
    public class UxPp2PanelTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

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
            Settle(window);
            return (window, page, page.Parameters);
        }

        static void Settle(Window w)
        {
            Pump();
            w.UpdateLayout();
            Pump();
            w.UpdateLayout();
        }

        static Point At(Avalonia.Controls.Control c, Avalonia.Controls.Control inside) => c.TranslatePoint(new Point(0, 0), inside)!.Value;

        // ------------------------------------------------------------------ polish

        [Test]
        public void SentenceCaseIsForDisplayAndLeavesTheParametersNameAlone()
        {
            Assert.That(DeviceParam.SentenceCase("attack"), Is.EqualTo("Attack"));
            Assert.That(DeviceParam.SentenceCase("Volume"), Is.EqualTo("Volume"));
            Assert.That(DeviceParam.SentenceCase("Filter Env"), Is.EqualTo("Filter env"));
            Assert.That(DeviceParam.SentenceCase("LFO rate"), Is.EqualTo("LFO rate"), "an acronym stays");
            Assert.That(DeviceParam.SentenceCase("Osc B level"), Is.EqualTo("Osc B level"), "a letter stays");
            Assert.That(DeviceParam.SentenceCase("Cutoff Hz"), Is.EqualTo("Cutoff Hz"), "a unit stays");
            Assert.That(DeviceParam.SentenceCase("warp 1 amt"), Is.EqualTo("Warp 1 amt"));
        }

        [AvaloniaTest]
        public void EveryLabelIsSentenceCaseAndTheNamesAreAsTheMachineSaysThem()
        {
            var m = Add("Dye Essence", "PP2 Case");
            var (window, page, view) = Stage(m, floating: true, width: 1200, height: 900);
            try
            {
                foreach (var p in view.Layout.All.Where(p => !p.IsTrack))
                {
                    Assert.That(p.Label, Is.EqualTo(DeviceParam.SentenceCase(p.Name)));
                    Assert.That(char.IsLower(p.Label[0]), Is.False, p.Label + " starts with a capital");
                    Assert.That(p.Parameter.Name, Is.Not.Null.And.Not.Empty);
                }
                var knobs = view.Cards.Shown.SelectMany(s => s.Card.Knobs).Take(40).ToList();
                foreach (var k in knobs) Assert.That(((DeviceKnob)Cell(window, view, k).Face).Label, Is.EqualTo(k.Label));
            }
            finally { window.Close(); Remove(m); }
        }

        [Test]
        public void AGroupsSharedPrefixIsCutFromEveryNameThatHasIt()
        {
            var names = new System.Collections.Generic.List<string> { "O2 shape", "O2 PW", "O2 octave", "Mix", "O2 fine", "o2 pan" };
            Assert.That(DeviceLayout.StripPrefix(names), Is.EqualTo(new[] { "shape", "PW", "octave", "Mix", "fine", "pan" }), "also the one with another case, and not the one without");
            var few = new System.Collections.Generic.List<string> { "A cutoff", "A reso", "Volume", "Pan", "Level" };
            Assert.That(DeviceLayout.StripPrefix(few), Is.EqualTo(few), "two of five share it: it stays");
            var alone = new System.Collections.Generic.List<string> { "F1 cutoff", "F1 reso", "F1 env", "F1" };
            Assert.That(DeviceLayout.StripPrefix(alone), Is.EqualTo(new[] { "cutoff", "reso", "env", "F1" }), "a name that is only the prefix keeps it");
        }

        [AvaloniaTest]
        public void NoSectionOfDyeEssenceShowsAPrefixItsNamesShare()
        {
            var m = Add("Dye Essence", "PP2 Prefix");
            try
            {
                var layout = DeviceLayout.Build(m);
                foreach (var s in layout.Sections.Where(s => !s.IsTable))
                {
                    var names = s.Params.Select(p => p.Name).ToList();
                    Assert.That(DeviceLayout.StripPrefix(names), Is.EqualTo(names), $"{s.Name}: nothing is left to cut ({string.Join(", ", names.Take(5))})");
                }
            }
            finally { Remove(m); }
        }

        [AvaloniaTest]
        public void ManyOptionChoicesAreDropDownsWithTheValueShownAndTwoOrThreeAreStrips()
        {
            var m = Add("Dye Essence", "PP2 Choices");
            var (window, page, view) = Stage(m, floating: true, width: 1200, height: 900);
            try
            {
                var choices = view.Layout.All.Where(p => !p.IsTrack && p.Kind == DeviceKind.Segmented).ToList();
                var many = choices.Where(p => p.Choices.Count > 3).ToList();
                var few = choices.Where(p => p.Choices.Count is 2 or 3).ToList();
                Assert.That(many, Is.Not.Empty, "Dye's engine and table choose among many");
                foreach (var p in many)
                {
                    Assert.That(((DeviceChoice)Cell(window, view, p).Face).Mode, Is.EqualTo(DeviceChoice.Face.Dropdown), p.FullName);
                    Assert.That(view.Layout.All.Contains(p) && p.Kind != DeviceKind.Slider, "not a knob");
                }
                foreach (var p in few) Assert.That(((DeviceChoice)Cell(window, view, p).Face).Mode, Is.EqualTo(DeviceChoice.Face.Strip), p.FullName);
                var engine = view.Layout.All.First(p => p.Name.Equals("engine", StringComparison.OrdinalIgnoreCase));
                Assert.That(engine.Kind, Is.EqualTo(DeviceKind.Segmented), "the engine is a choice, not a knob");
                Assert.That(((DeviceChoice)Cell(window, view, engine).Face).Selected, Is.EqualTo(engine.Value - engine.Min), "and shows its value");
                // A numeric parameter with a few values stays a knob: only named values are choices.
                Assert.That(view.Layout.All.Where(p => p.Kind == DeviceKind.Slider).Any(), Is.True);
            }
            finally { window.Close(); Remove(m); }
        }

        [AvaloniaTest]
        public void DockedNoCardIsTallerThanThePanelAndAVoiceCardSplits()
        {
            var m = Add("Dye Essence", "PP2 Docked");
            foreach (double height in new[] { 420.0, 330.0, 280.0 })
            {
                var (window, page, view) = Stage(m, floating: false, width: 1100, height: height);
                try
                {
                    var scroller = view.Cards.Scroller;
                    double room = view.Cards.Bounds.Height;
                    Assert.That(room, Is.GreaterThan(60), $"{height}: the cards have room");
                    foreach (var (card, control) in view.Cards.Shown)
                        Assert.That(control.Bounds.Height, Is.LessThanOrEqualTo(room + 1), $"{height}: {card.Title} fits the panel ({control.Bounds.Height} of {room})");
                    Assert.That(scroller.Extent.Height, Is.LessThanOrEqualTo(scroller.Viewport.Height + 1), $"{height}: nothing scrolls down");
                    Assert.That(view.Cards.ScrollsDown, Is.False);
                    if (height <= 330)
                        Assert.That(view.Cards.Shown.Any(s => System.Text.RegularExpressions.Regex.IsMatch(s.Card.Title, @"^Voice \d+/\d+$")), Is.True, $"{height}: the Voice section splits ({string.Join(", ", view.Cards.Shown.Take(4).Select(s => s.Card.Title))})");
                    Assert.That(view.Cards.Shown.SelectMany(s => s.Card.Params).Count(), Is.EqualTo(view.Layout.Cards("").SelectMany(c => c.Params).Count()), "no parameter lost in the split");
                }
                finally { window.Close(); }
            }
            Remove(m);
        }

        [AvaloniaTest]
        public void DockedOnlyAPanelShorterThanOneRowOfKnobsScrollsDown()
        {
            var m = Add("Neo Filter", "PP2 Short");
            var (window, page, view) = Stage(m, floating: false, width: 900, height: 150);
            try
            {
                Assert.That(view.Cards.Bounds.Height, Is.LessThan(DeviceCards.CardChrome + 72 + DeviceCards.ScrollBarRoom), "shorter than a card with one row of knobs");
                Assert.That(view.Cards.ScrollsDown, Is.True);
            }
            finally { window.Close(); Remove(m); }
        }

        [AvaloniaTest]
        public void ACardsChoicesAreAtItsTopTwoToARowAndTheKnobsAreOneGrid()
        {
            var m = Add("Dye Essence", "PP2 Packing");
            var (window, page, view) = Stage(m, floating: true, width: 1200, height: 1000);
            try
            {
                bool pair = false;
                foreach (var (card, control) in view.Cards.Shown)
                {
                    var choices = card.Choices.Select(p => view.Cards.Cell(p)!).ToList();
                    var knobs = card.Knobs.Select(p => view.Cards.Cell(p)!).ToList();
                    if (choices.Count > 0 && knobs.Count > 0)
                        Assert.That(choices.Max(c => At(c, control).Y + c.Bounds.Height), Is.LessThanOrEqualTo(knobs.Min(c => At(c, control).Y) + 1), $"{card.Title}: choices above the knobs");
                    // A row ends where the next knob is further left (a cell with a two-line label sits a little lower in its row).
                    var rows = new System.Collections.Generic.List<int>();
                    double lastX = double.MaxValue;
                    foreach (var k in knobs)
                    {
                        double x = At(k, control).X;
                        if (x <= lastX) rows.Add(0);
                        rows[^1]++;
                        lastX = x;
                    }
                    for (int i = 0; i < rows.Count - 1; i++)
                        Assert.That(rows[i], Is.EqualTo(rows[0]), $"{card.Title}: knob rows {string.Join("+", rows)}: no lone knob in the middle: " + string.Join("; ", knobs.Select(c => $"{c.Param.Name} {At(c, control).X:0},{At(c, control).Y:0} w{c.Bounds.Width:0} h{c.Bounds.Height:0}")) + " card w" + control.Bounds.Width);
                    if (rows.Count > 1) Assert.That(rows.Last(), Is.LessThanOrEqualTo(rows[0]));
                    var drop = choices.Where(c => c.Face is DeviceChoice { Mode: DeviceChoice.Face.Dropdown }).ToList();
                    if (drop.Count >= 2 && Math.Round(At(drop[0], control).Y) == Math.Round(At(drop[1], control).Y)) pair = true;
                }
                Assert.That(pair, Is.True, "two drop-downs share a row where they fit");
            }
            finally { window.Close(); Remove(m); }
        }

        [AvaloniaTest]
        public void FloatingCardsFlowToFillTheWidth()
        {
            var m = Add("Dye Essence", "PP2 Wide");
            var (window, page, view) = Stage(m, floating: true, width: 1440, height: 900);
            try
            {
                var host = (CardFlow)view.Cards.Scroller.Content!;
                double width = view.Cards.Scroller.Viewport.Width;
                var cards = view.Cards.Shown.Select(s => s.Control).ToList();
                Assert.That(host.Columns.Count, Is.GreaterThan(1));
                double right = cards.Max(c => At(c, host).X + c.Bounds.Width);
                Assert.That(right, Is.GreaterThanOrEqualTo(width - DeviceCards.CardChromeWidth), $"the columns reach {right} of {width}");
                Assert.That(view.Cards.Scroller.Extent.Width, Is.LessThanOrEqualTo(width + 1));
            }
            finally { window.Close(); Remove(m); }
        }

        [AvaloniaTest]
        public void TheChipsThatDoNotFitAreBehindAPlusNButtonAndNothingClipsUnderTheButtons()
        {
            var m = Add("Dye Essence", "PP2 Chips");
            var (window, page, view) = Stage(m, floating: true, width: 640, height: 600);
            try
            {
                var row = view.ChipsRow;
                Assert.That(row.Overflow, Is.GreaterThan(0), "Dye Essence has more sections than 640 fit");
                Assert.That(row.MoreButton.IsVisible, Is.True);
                Assert.That((string)row.MoreButton.Content!, Is.EqualTo("+" + row.Overflow));
                var buttons = view.Options.Children.OfType<StackPanel>().First();
                Assert.That(row.Bounds.Right, Is.LessThanOrEqualTo(buttons.Bounds.Left + 1), "the row ends before Changed only");
                int visible = row.Chips.Count - row.Overflow;
                foreach (var chip in row.Chips.Take(visible)) Assert.That(chip.Bounds.Right, Is.LessThanOrEqualTo(row.MoreButton.Bounds.Left + 1), (string)chip.Content! + " is whole");
                foreach (var chip in row.Chips.Skip(visible)) Assert.That(chip.Bounds.Width, Is.EqualTo(0), "the others are out of the row");
                var names = row.OverflowNames.ToList();
                Assert.That(names.Count, Is.EqualTo(row.Overflow));
                string last = names.Last();
                Assert.That(row.Jump(last), Is.True, "the menu jumps to a clipped section");
                int narrow = row.Overflow;
                window.Width = 1700;
                Settle(window);
                Assert.That(view.ChipsRow.Overflow, Is.LessThan(narrow), "more of them fit when there is room");
            }
            finally { window.Close(); Remove(m); }
        }

        [AvaloniaTest]
        public void KnobsAndBarsShareOneColourWithContrastInBothThemes()
        {
            var original = StudioTheme.Kind;
            var m = Add("Neo Filter", "PP2 Colour");
            try
            {
                foreach (var theme in new[] { AppearanceTheme.Dark, AppearanceTheme.Light })
                {
                    StudioTheme.Kind = theme;
                    foreach (NeoBuzz.Editor.MachineCategory category in Enum.GetValues(typeof(NeoBuzz.Editor.MachineCategory)))
                    {
                        var colour = StudioTokens.Of(DeviceColour.Of(category));
                        Assert.That(StudioTokens.Contrast(colour, StudioTokens.SurfacePanel), Is.GreaterThanOrEqualTo(3.0), $"{theme} {category}");
                    }
                    var (window, page, view) = Stage(m, floating: true);
                    try
                    {
                        var knob = (DeviceKnob)Cell(window, view, Param(view, "Cutoff")).Face;
                        Assert.That(knob.Accent, Is.EqualTo(DeviceColour.Of(m)), $"{theme}: the knobs use it");
                        Assert.That(view.List.Identity, Is.EqualTo(StudioTokens.Of(DeviceColour.Of(m))), $"{theme}: and so does the list");
                        Assert.That(view.List.Identity, Is.Not.EqualTo(StudioTokens.Accent), "not the app's blue");
                    }
                    finally { window.Close(); }
                }
            }
            finally { StudioTheme.Kind = original; Remove(m); }
        }

        // ------------------------------------------------------------------ the menu

        [AvaloniaTest]
        public void TheMenuOffersAutomateModulateLockAndHideInKnobsAndInList()
        {
            var m = Add("Neo Filter", "PP2 Menu");
            var (window, page, view) = Stage(m, floating: true);
            try
            {
                var cutoff = Param(view, "Cutoff");
                var cell = Cell(window, view, cutoff);
                window.MouseDown(Centre(window, cell.Face), MouseButton.Right);
                window.MouseUp(Centre(window, cell.Face), MouseButton.Right);
                Settle(window);
                void Check(string where)
                {
                    var headers = view.LastMenu!.Items.OfType<MenuItem>().Select(i => (string)i.Header!).ToList();
                    Assert.That(headers, Does.Contain("_Automate").And.Contain("_Modulate").And.Contain("_Lock").And.Contain("_Hide").And.Not.Contain("_Keyboard Sends MIDI Notes"), where);
                }
                Check("knobs");
                view.SetMode(Mode.List, remember: false);
                Settle(window);
                var at = view.List.ControlBounds(cutoff).Center;
                var p = view.List.TranslatePoint(at - new Point(view.List.ScrollLeft, view.List.ScrollTop), window)!.Value;
                window.MouseDown(p, MouseButton.Right);
                window.MouseUp(p, MouseButton.Right);
                Settle(window);
                Check("list");
            }
            finally { window.Close(); Remove(m); }
        }

        [AvaloniaTest]
        public void AutomateAddsTheParametersLaneAndShowsTheArrangement()
        {
            NewSong();
            var m = Add("Neo Filter", "PP2 Automate");
            var (window, page, view) = Stage(m, floating: true);
            bool shown = false;
            var was = DeviceMenus.ShowArrangement;
            DeviceMenus.ShowArrangement = () => shown = true;
            try
            {
                var cutoff = Param(view, "Cutoff");
                Assert.That(DeviceMenus.HasLane(m, cutoff), Is.False);
                var menu = DeviceMenus.ForParam(m, cutoff, null, null);
                Assert.That(menu.Items.OfType<MenuItem>().Select(i => (string)i.Header!), Does.Contain("_Automate"));
                Assert.That(DeviceMenus.Automate(m, cutoff), Is.True);
                Assert.That(DeviceMenus.HasLane(m, cutoff), Is.True, "the lane is in the arrangement");
                Assert.That(Core.Arrangement!.ParameterLanes.Count(l => l.Machine == m.Name), Is.EqualTo(1));
                Assert.That(shown, Is.True, "and the arrangement shows");
                Assert.That(DeviceMenus.Automate(m, cutoff), Is.True);
                Assert.That(Core.Arrangement.ParameterLanes.Count(l => l.Machine == m.Name), Is.EqualTo(1), "a second time opens the same lane");
                menu = DeviceMenus.ForParam(m, cutoff, null, null);
                Assert.That(menu.Items.OfType<MenuItem>().Select(i => (string)i.Header!), Does.Contain("Show _Automation Lane"));
            }
            finally { DeviceMenus.ShowArrangement = was; window.Close(); NewSong(); }
        }

        [AvaloniaTest]
        public void ModulateListsTheModulatorsAndAddsTheModulation()
        {
            NewSong();
            var filter = Add("Neo Filter", "PP2 Target");
            var mod = Add("Neo LFO", "PP2 Wobble");
            var (window, page, view) = Stage(filter, floating: true);
            try
            {
                var cutoff = Param(view, "Cutoff");
                var menu = DeviceMenus.ForParam(filter, cutoff, null, null);
                var modulate = menu.Items.OfType<MenuItem>().First(i => (string)i.Header! == "_Modulate");
                Assert.That(modulate.IsEnabled, Is.True);
                var entries = modulate.Items.OfType<MenuItem>().ToList();
                var wobble = entries.FirstOrDefault(i => (string)i.Header! == "PP2 Wobble");
                Assert.That(wobble, Is.Not.Null, string.Join(", ", entries.Select(i => i.Header)));
                Assert.That(entries.Any(i => ((string)i.Header!).Contains("Matrix")), "and the matrix");
                wobble!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                Settle(window);
                Assert.That(ModulationBoard.Incoming(filter).Count, Is.EqualTo(1), "the LFO now modulates the cutoff");
                Core.SongCore.ActionStack.Undo();
                Assert.That(ModulationBoard.Incoming(filter).Count, Is.EqualTo(0), "one undo step");
            }
            finally { window.Close(); NewSong(); }
        }

        // ------------------------------------------------------------------ lock and hide

        [AvaloniaTest]
        public void ALockIsKeptPerMachineUndoableAndShownOnTheKnob()
        {
            NewSong();
            var m = Add("Neo Filter", "PP2 Lock");
            var (window, page, view) = Stage(m, floating: true);
            try
            {
                var cutoff = Param(view, "Cutoff");
                var cell = Cell(window, view, cutoff);
                Assert.That(cutoff.Locked, Is.False);
                var steps = Core.SongCore.ActionStack.Actions.Count();
                Assert.That(DeviceMenus.SetLocked(m, cutoff, true), Is.True);
                Assert.That(Core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(steps + 1), "one undo step");
                Assert.That(cutoff.Locked, Is.True);
                Assert.That(Core.PinStore!.GetLocks("PP2 Lock"), Is.EqualTo(new[] { cutoff.LockKey }));
                cell.Sync();
                Assert.That(cell.LockShown, Is.True, "the knob shows its lock");
                var check = DeviceMenus.ForParam(m, cutoff, null, null).Items.OfType<MenuItem>().First(i => (string)i.Header! == "_Lock");
                Assert.That(check.IsChecked, Is.True, "the menu item is ticked");
                Core.SongCore.ActionStack.Undo();
                Assert.That(cutoff.Locked, Is.False, "undo");
                Core.SongCore.ActionStack.Redo();
                Assert.That(cutoff.Locked, Is.True, "redo");
                var other = Add("Neo Filter", "PP2 Lock B");
                Assert.That(DeviceLayout.Build(other).All.First(p => p.Name == "Cutoff").Locked, Is.False, "another machine of the type keeps its own");
                var input = view.Layout.All.FirstOrDefault(p => p.IsInput);
                if (input != null) Assert.That(input.CanLock, Is.False, "a connection's amp is never locked by hand: it is always left alone");
            }
            finally { window.Close(); NewSong(); }
        }

        [AvaloniaTest]
        [TestCase(".nbl")]
        public void LocksAndPinsSurviveASaveAndAnOpen(string extension)
        {
            NewSong();
            string path = TestTemp.Path("locks-" + Guid.NewGuid().ToString("N") + extension);
            try
            {
                var m = Add("Neo Filter", "PP2 Saved");
                var layout = DeviceLayout.Build(m);
                var cutoff = layout.All.First(p => p.Name == "Cutoff");
                Assert.That(Core.PinStore!.Set("PP2 Saved", cutoff.Parameter.Name, true), Is.True);
                Assert.That(Core.PinStore.SetLocked("PP2 Saved", new[] { cutoff.LockKey, "T:Gain" }, true), Is.True);
                Core.SaveSongCopy(path);
                NewSong();
                Assert.That(Core.PinStore.GetLocks("PP2 Saved"), Is.Empty, "a new song has none");
                Dialogs.Responder = (mm, c, bb) => UserAnswer.No;
                Core.Modified = false;
                Core.OpenSongFile(path);
                Pump();
                Assert.That(Core.PinStore.GetLocks("PP2 Saved"), Is.EqualTo(new[] { cutoff.LockKey, "T:Gain" }), extension + ": the locks come back");
                Assert.That(Core.PinStore.Get("PP2 Saved"), Is.EqualTo(new[] { cutoff.Parameter.Name }), "beside the pins, in the same subsection");
                Assert.That(DeviceLayout.Build(Core.SongCore.MachinesList.First(x => x.Name == "PP2 Saved")).All.First(p => p.Name == "Cutoff").Locked, Is.True);
            }
            finally
            {
                Dialogs.Responder = null;
                try { File.Delete(path); } catch (IOException) { }
                NewSong();
            }
        }

        [AvaloniaTest]
        public void HiddenParametersLeaveTheViewUntilShowHiddenAndKeepTheOldFilesFormat()
        {
            string file = TestTemp.Path("hidden-" + Guid.NewGuid().ToString("N") + ".xml");
            ParameterVisibility.FilePath = file;
            ParameterVisibility.Reset();
            var m = Add("Neo Filter", "PP2 Hide");
            var (window, page, view) = Stage(m, floating: true);
            try
            {
                var cutoff = Param(view, "Cutoff");
                Assert.That(view.Cards.Shown.SelectMany(s => s.Card.Params), Does.Contain(cutoff));
                ParameterVisibility.SetVisible(cutoff.Parameter, false);
                Assert.That(cutoff.Hidden, Is.True);
                var menu = DeviceMenus.ForParam(m, cutoff, null, null);
                Assert.That(menu.Items.OfType<MenuItem>().First(i => (string)i.Header! == "_Hide").IsChecked, Is.True);
                view.Query = "";
                view.ChangedOnly = true;
                view.ChangedOnly = false;
                Settle(window);
                Assert.That(view.Cards.Shown.SelectMany(s => s.Card.Params), Does.Not.Contain(cutoff), "out of view");
                Assert.That(view.Layout.Items("").OfType<ParamItem>().Select(i => i.Param), Does.Not.Contain(cutoff), "out of the list too");
                view.ShowHidden = true;
                Settle(window);
                Assert.That(view.Cards.Shown.SelectMany(s => s.Card.Params), Does.Contain(cutoff), "Show hidden brings it back");
                Assert.That(view.Cards.Cell(cutoff)!.Opacity, Is.LessThan(1), "dimmed");
                view.ShowHidden = false;
                // The WPF window's own file: ParameterWindowProperties.xml, per machine type, the unticked parameters.
                var xml = File.ReadAllText(file);
                Assert.That(xml, Does.Contain("ParameterWindowProperties").And.Contain("IsVisible=\"false\"").And.Contain("Name=\"Cutoff\""));
                ParameterVisibility.Reset();
                Assert.That(ParameterVisibility.IsVisible(cutoff.Parameter), Is.False, "read back from the file");
                ParameterVisibility.SetVisible(cutoff.Parameter, true);
                Assert.That(cutoff.Hidden, Is.False);
            }
            finally
            {
                window.Close();
                Remove(m);
                ParameterVisibility.FilePath = null;
                ParameterVisibility.Reset();
                try { File.Delete(file); } catch (IOException) { }
            }
        }

        // ------------------------------------------------------------------ randomize and morph

        [AvaloniaTest]
        public void RandomizeLeavesLockedParametersAndIsOneUndoStep()
        {
            NewSong();
            var m = Add("Neo Poly", "PP2 Random");
            var (window, page, view) = Stage(m, floating: true);
            try
            {
                var layout = view.Layout;
                var locked = layout.All.Where(p => p.Kind == DeviceKind.Slider && !p.IsTrack).Take(3).ToList();
                foreach (var p in locked) DeviceMenus.SetLocked(m, p, true);
                var before = layout.All.Select(p => p.Value).ToList();
                var steps = Core.SongCore.ActionStack.Actions.Count();
                DeviceFunctions.Run(Core, m, ParameterFunction.Randomize, 0, null);
                var after = layout.All.Select(p => p.Value).ToList();
                Assert.That(Core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(steps + 1), "one undo step");
                foreach (var p in locked) Assert.That(p.Value, Is.EqualTo(before[layout.All.ToList().IndexOf(p)]), p.FullName + " is locked");
                Assert.That(after.Zip(before, (a, b) => a != b).Count(x => x), Is.GreaterThan(10), "the rest moved");
                Core.SongCore.ActionStack.Undo();
                Assert.That(layout.All.Select(p => p.Value), Is.EqualTo(before), "one undo takes them all back");
                var menu = DeviceMenus.Presets(m, page, null);
                Assert.That(menu.Items.OfType<MenuItem>().Select(i => (string)i.Header!), Does.Contain("_Randomize").And.Contain("_Morph Between A and B..."));
            }
            finally { window.Close(); NewSong(); }
        }

        [AvaloniaTest]
        public void MorphFollowsTheSliderBetweenTwoPresetsInOneUndoStep()
        {
            NewSong();
            var m = Add("Neo Filter", "PP2 Morph");
            var (window, page, view) = Stage(m, floating: true);
            try
            {
                var cutoff = Param(view, "Cutoff");
                var reso = Param(view, "Resonance");
                var lockedParam = view.Layout.All.First(p => p.Kind == DeviceKind.Slider && p != cutoff && p != reso && !p.IsTrack);
                DeviceEdits.Set(Core, cutoff, cutoff.Min);
                DeviceEdits.Set(Core, reso, reso.Min);
                m.SetPreset("PP2 Low", new Preset(m, false, true));
                DeviceEdits.Set(Core, cutoff, cutoff.Max);
                DeviceEdits.Set(Core, reso, reso.Max);
                m.SetPreset("PP2 High", new Preset(m, false, true));
                DeviceMenus.SetLocked(m, lockedParam, true);
                int lockedValue = lockedParam.Value == lockedParam.Max ? lockedParam.Min : lockedParam.Max;
                DeviceEdits.Set(Core, lockedParam, lockedValue);
                int steps = Core.SongCore.ActionStack.Actions.Count();
                var morph = new DeviceFunctions.Morph(m, null) { A = "PP2 Low", B = "PP2 High" };
                morph.MoveTo(0.25);
                int quarter = cutoff.Value;
                morph.MoveTo(0.75);
                int threeQuarters = cutoff.Value;
                Assert.That(Core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(steps + 1), "the slider keeps one undo step");
                Assert.That(quarter, Is.EqualTo(cutoff.Min + (cutoff.Max - cutoff.Min) / 4).Within(1));
                Assert.That(threeQuarters, Is.EqualTo(cutoff.Min + (cutoff.Max - cutoff.Min) * 3 / 4).Within(1));
                Assert.That(lockedParam.Value, Is.EqualTo(lockedValue), "a locked parameter stays");
                morph.MoveTo(0);
                Assert.That(cutoff.Value, Is.EqualTo(cutoff.Min));
                morph.MoveTo(1);
                Assert.That(reso.Value, Is.EqualTo(reso.Max));
                Core.SongCore.ActionStack.Undo();
                Assert.That(cutoff.Value, Is.EqualTo(cutoff.Max), "undo goes back to before the morph");
                Assert.That(lockedParam.Value, Is.EqualTo(lockedValue));
                // The flyout: two presets and a slider.
                var flyout = DeviceFunctions.OpenMorph(m, page, null);
                Assert.That(DeviceFunctions.Last.Slider, Is.Not.Null);
                Assert.That(DeviceFunctions.Last.A.ItemCount, Is.GreaterThan(2), "<default> and the presets");
                flyout.Hide();
            }
            finally { window.Close(); NewSong(); }
        }

        // ------------------------------------------------------------------ the keyboard

        [AvaloniaTest]
        public void TheComputersKeysPlayTheMachineWhenTheOldSettingIsOnAndItIsKeptWhereTheOldWindowKeptIt()
        {
            var m = Add("Neo Poly", "PP2 Keys");
            bool was = DeviceKeys.Enabled;
            using var restore = Studio.AfterTest(() => DeviceKeys.Enabled = was);
            var (window, page, view) = Stage(m, floating: true);
            try
            {
                Assert.That(DeviceKeys.Key, Is.EqualTo("KeyboardMIDI"));
                Assert.That(DeviceKeys.Section, Is.EqualTo("Parameter Window"));
                DeviceKeys.Enabled = false;
                window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.None);
                Assert.That(page.Keys.Held, Is.EqualTo(0), "off: nothing plays");
                window.KeyReleaseQwerty(PhysicalKey.Z, RawInputModifiers.None);
                var menu = DeviceMenus.Presets(m, page, null);
                var item = menu.Items.OfType<MenuItem>().First(i => (string)i.Header! == "_Keyboard Sends MIDI Notes");
                Assert.That(item.IsChecked, Is.False);
                item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                Assert.That(DeviceKeys.Enabled, Is.True, "the toggle sets the same setting");
                Assert.That(StudioHost.Current.Registry.Read("KeyboardMIDI", false, "Parameter Window"), Is.True);
                window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.None);
                Assert.That(page.Keys.Held, Is.EqualTo(1), "a key down plays a note until it is let go");
                window.KeyReleaseQwerty(PhysicalKey.Z, RawInputModifiers.None);
                Assert.That(page.Keys.Held, Is.EqualTo(0));
                window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.None);
                page.Keys.NotesOff();
                Assert.That(page.Keys.Held, Is.EqualTo(0), "losing the window ends them");
                window.KeyReleaseQwerty(PhysicalKey.Z, RawInputModifiers.None);
            }
            finally { window.Close(); Remove(m); }
        }

        // ------------------------------------------------------------------ pins on rename

        [AvaloniaTest]
        public void PinsAndLocksFollowAMachineThatIsRenamedAndUndoTakesThemBack()
        {
            NewSong();
            string path = TestTemp.Path("rename-" + Guid.NewGuid().ToString("N") + ".nbl");
            try
            {
                var m = Add("Neo Filter", "PP2 Before");
                var other = Add("Neo Filter", "PP2 Other");
                string cutoff = DeviceLayout.Build(m).All.First(p => p.Name == "Cutoff").Parameter.Name;
                Core.PinStore!.Set("PP2 Before", cutoff, true);
                Core.PinStore.SetLocked("PP2 Before", cutoff, true);
                Core.PinStore.Set("PP2 Other", cutoff, true);
                Core.SongCore.RenameMachineUndoable(m, "PP2 After");
                Pump();
                Assert.That(m.Name, Is.EqualTo("PP2 After"));
                Assert.That(Core.PinStore.Get("PP2 After"), Is.EqualTo(new[] { cutoff }), "the pin moved with it");
                Assert.That(Core.PinStore.GetLocks("PP2 After"), Is.EqualTo(new[] { cutoff }), "and the lock");
                Assert.That(Core.PinStore.Get("PP2 Before"), Is.Empty);
                Assert.That(Core.PinStore.GetLocks("PP2 Before"), Is.Empty);
                Assert.That(Core.PinStore.Get("PP2 Other"), Is.EqualTo(new[] { cutoff }), "another machine's pins are its own");
                var inspector = new ReBuzz.Studio.Views.Inspector.InspectorDevice(Core);
                inspector.Show(m);
                Assert.That(inspector.PinnedCells.Select(c => c.Param.Parameter.Name), Is.EqualTo(new[] { cutoff }), "the Inspector still shows it");
                Core.SaveSongCopy(path);
                Core.SongCore.ActionStack.Undo();
                Assert.That(m.Name, Is.EqualTo("PP2 Before"));
                Assert.That(Core.PinStore.Get("PP2 Before"), Is.EqualTo(new[] { cutoff }), "undoing the rename carries it back");
                Assert.That(Core.PinStore.Get("PP2 After"), Is.Empty);
                Core.SongCore.ActionStack.Redo();
                Assert.That(Core.PinStore.Get("PP2 After"), Is.EqualTo(new[] { cutoff }), "redo");
                NewSong();
                Dialogs.Responder = (mm, c, bb) => UserAnswer.No;
                Core.Modified = false;
                Core.OpenSongFile(path);
                Pump();
                Assert.That(Core.PinStore.Get("PP2 After"), Is.EqualTo(new[] { cutoff }), "saved under the new name");
                Assert.That(Core.PinStore.Get("PP2 Before"), Is.Empty);
            }
            finally
            {
                Dialogs.Responder = null;
                try { File.Delete(path); } catch (IOException) { }
                NewSong();
            }
        }

        // ------------------------------------------------------------------ tools

        [AvaloniaTest]
        public void TheParamLockAndRandomizeToolsMatchThePanel()
        {
            NewSong();
            try
            {
                var m = Add("Neo Poly", "PP2 Tools");
                var globals = DeviceLayout.Build(m).All.Where(p => p.Kind == DeviceKind.Slider && !p.IsTrack).Take(2).Select(p => p.Parameter.Name).ToList();
                var r = Call("param_lock", new JsonObject { ["machine"] = "PP2 Tools", ["param"] = globals[0] })["structuredContent"]!.AsObject();
                Assert.That(r["changed"]!.GetValue<bool>(), Is.True);
                Assert.That(Core.PinStore!.IsLocked("PP2 Tools", globals[0]), Is.True, "the panel sees what the tool locked");
                var list = Call("param_lock", new JsonObject { ["machine"] = "PP2 Tools" })["structuredContent"]!.AsObject();
                Assert.That(list["locked"]!.AsArray().Select(x => x!.GetValue<string>()), Is.EqualTo(new[] { globals[0] }));
                var param = DeviceLayout.Build(m).All.First(p => p.Parameter.Name == globals[0]);
                int before = param.Value;
                int steps = Core.SongCore.ActionStack.Actions.Count();
                var rr = Call("param_randomize", new JsonObject { ["machine"] = "PP2 Tools", ["mode"] = "randomize" })["structuredContent"]!.AsObject();
                Assert.That(rr["changed"]!.GetValue<int>(), Is.GreaterThan(5));
                Assert.That(rr["locked"]!.GetValue<int>(), Is.GreaterThanOrEqualTo(1));
                Assert.That(param.Value, Is.EqualTo(before), "the locked one stays");
                Assert.That(Core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(steps + 1), "one undo step");
                Core.SongCore.ActionStack.Undo();
                Call("param_randomize", new JsonObject { ["machine"] = "PP2 Tools", ["mode"] = "morph", ["a"] = "<default>", ["b"] = "<default>", ["amount"] = 0.5 });
                Assert.That(CallRaw("param_randomize", new JsonObject { ["machine"] = "PP2 Tools", ["mode"] = "sideways" })["isError"]?.GetValue<bool>(), Is.True);
                Call("param_lock", new JsonObject { ["machine"] = "PP2 Tools", ["param"] = globals[0], ["locked"] = false });
                Assert.That(Core.PinStore.GetLocks("PP2 Tools"), Is.Empty);
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
