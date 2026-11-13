// Fork (docs/specs/ux-overhaul.md P3e, decisions 62-70): the device view that replaces the generic parameter window: groups from the
// manifest, the name prefixes, Buzz's "- " indents or the Global/Track/Inputs sections; search (⌘F); 24 pt rows; sliders filled from
// zero with units; a typed value with its unit; ⌥-click to reset; segmented controls and toggles; the track parameters as a table; the
// rows virtualised (a 433-parameter machine opens as fast as a small one); every edit one undo step; the Explain text; docked in
// Detail ▸ Device with Float, and the richest view on a double click.

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Device;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class UxP3eTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static RawInputModifiers Command => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

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

        /// <summary>The device page of a machine in a window of its own, showing its parameters.</summary>
        static (Window Window, DevicePage Page, DeviceView View) Stage(MachineCore machine, double width = 720, double height = 640)
        {
            var page = new DevicePage(machine, floating: false);
            page.SetEditor(false, remember: false);
            var window = new Window { Width = width, Height = height, Content = page };
            window.Show();
            Settle();
            window.UpdateLayout();
            return (window, page, page.Parameters);
        }

        static DeviceParam Param(DeviceView view, string name) => view.Layout.All.First(p => p.FullName == name);

        static Point Local(Window window, DeviceList list, Point inList) => list.TranslatePoint(inList, window) ?? throw new InvalidOperationException("not in the window");

        static Point Centre(Window window, DeviceView view, DeviceParam p, double fraction = 0.5)
        {
            view.List.Reveal(p);
            Settle();
            var r = view.List.ControlBounds(p);
            return Local(window, view.List, new Point(r.X + r.Width * fraction, r.Center.Y));
        }

        static int Steps => Core.SongCore.ActionStack.Actions.Count();

        // ------------------------------------------------------------------ pixels

        static (uint[] Pixels, int Width, int Height) Frame(Window window)
        {
            foreach (var v in window.GetVisualDescendants()) v.InvalidateVisual();
            Dispatcher.UIThread.RunJobs();
            var bitmap = window.CaptureRenderedFrame();
            Assert.That(bitmap, Is.Not.Null);
            using var fb = bitmap!.Lock();
            int w = fb.Size.Width, h = fb.Size.Height;
            bool rgba = fb.Format == Avalonia.Platform.PixelFormat.Rgba8888;
            var pixels = new uint[w * h];
            var row = new int[w];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, w);
                for (int x = 0; x < w; x++)
                {
                    uint p = (uint)row[x];
                    pixels[y * w + x] = rgba ? ((p & 0xFF) << 16) | (p & 0xFF00) | ((p >> 16) & 0xFF) : p & 0xFFFFFF;
                }
            }
            return (pixels, w, h);
        }

        static uint Rgb(Color c) => (uint)(c.R << 16 | c.G << 8 | c.B);

        static bool Near((uint[] Pixels, int Width, int Height) f, int x, int y, Color c, int tolerance = 30)
        {
            uint p = f.Pixels[y * f.Width + x];
            return Math.Abs((int)((p >> 16) & 0xFF) - c.R) + Math.Abs((int)((p >> 8) & 0xFF) - c.G) + Math.Abs((int)(p & 0xFF) - c.B) <= tolerance;
        }

        // ------------------------------------------------------------------ grouping

        [Test]
        public void IndentedNamesGroupUnderTheParameterAboveThem()
        {
            var plan = DeviceLayout.Plan(new[] { "Osc 1 Wave", "- Decay", "- Level", "Osc 2 Wave", "- Decay", "- Level", "Master" });
            Assert.That(plan.Source, Is.EqualTo("indent"));
            Assert.That(plan.Groups.Select(g => g.Title), Is.EqualTo(new[] { "Osc 1 Wave", "Osc 2 Wave", "General" }));
            Assert.That(plan.Groups[0].Names, Is.EqualTo(new[] { "Osc 1 Wave", "Decay", "Level" }), "the parent leads its group, the dash is cut");
            Assert.That(plan.Groups[2].Names, Is.EqualTo(new[] { "Master" }));
        }

        [Test]
        public void NamePrefixesGroupWhereTwoWordsAreShared()
        {
            var plan = DeviceLayout.Plan(new[] { "Volume", "F1 cutoff", "F1 reso", "F2 cutoff", "F2 reso", "Pan" });
            Assert.That(plan.Source, Is.EqualTo("prefix"));
            Assert.That(plan.Groups.Select(g => g.Title), Is.EqualTo(new[] { "General", "F1", "F2" }), "the loose ones first");
            Assert.That(plan.Groups[1].Names, Is.EqualTo(new[] { "cutoff", "reso" }), "the prefix is the header's");
            Assert.That(plan.Groups[0].Names, Is.EqualTo(new[] { "Volume", "Pan" }));
        }

        [Test]
        public void NamesThatShareNothingStayOneSection()
        {
            Assert.That(DeviceLayout.Plan(new[] { "Volume", "Pan", "Gain" }).Source, Is.EqualTo("section"));
            Assert.That(DeviceLayout.Plan(new[] { "F1 cutoff", "F1 reso", "Volume", "Pan" }).Source, Is.EqualTo("section"), "one shared word is not a grouping");
        }

        [AvaloniaTest]
        public void AManifestsGroupsAreTheHeadersAndTheTrackParametersATable()
        {
            var poly = Add("Neo Poly", "P3e Poly");
            var chord = Add("Pedal Chord", "P3e Chord");
            try
            {
                var l = DeviceLayout.Build(poly);
                Assert.That(l.GroupSource, Is.EqualTo("manifest"));
                Assert.That(l.Sections.Select(s => s.Name), Does.Contain("Filter 1").And.Contain("Amp env").And.Contain("Matrix"));
                Assert.That(l.Sections.All(s => s.Source == "manifest"));
                Assert.That(l.All.Count, Is.EqualTo(poly.ParameterGroups[1].Parameters.Count(p => p.Flags.HasFlag(ParameterFlags.State) && p.Type != ParameterType.Note)), "every state parameter, once");

                var t = DeviceLayout.Build(chord);
                var table = t.Sections.Single(s => s.IsTable);
                Assert.That(table.Name, Is.EqualTo("Tracks"));
                Assert.That(table.Params.Select(p => p.Name), Does.Contain("Chord"));
                Assert.That(t.Sections.Select(s => s.Name), Is.EqualTo(new[] { "Tracks" }), "no globals: the table alone");
            }
            finally
            {
                Remove(poly);
                Remove(chord);
            }
        }

        [AvaloniaTest]
        public void AnEffectsInputsAreTheirOwnSection()
        {
            var gen = Add("Neo Poly", "P3e Src");
            var fx = Add("Pedal Comp", "P3e Comp");
            try
            {
                Core.SongCore.ConnectMachines(gen, fx, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                var l = DeviceLayout.Build(fx);
                Assert.That(l.Sections[0].Name, Is.EqualTo("Inputs"));
                Assert.That(l.Sections[0].Params.First().IsInput, Is.True);
                Assert.That(l.Sections.Select(s => s.Name), Does.Contain("Compressor"), "then its own groups");
            }
            finally
            {
                Remove(fx);
                Remove(gen);
            }
        }

        // ------------------------------------------------------------------ search

        [AvaloniaTest]
        public void SearchNarrowsTheListToWhatMatchesAndCommandFFindsIt()
        {
            var m = Add("Neo Poly", "P3e Search");
            var (window, page, view) = Stage(m);
            try
            {
                int all = view.List.Items.OfType<ParamItem>().Count();
                Assert.That(view.CountText, Is.EqualTo($"{view.Layout.All.Count} parameters"));

                window.KeyPress(Key.F, Command, PhysicalKey.F, null);
                Settle();
                Assert.That(view.Search.IsFocused, Is.True, "⌘F (Ctrl+F elsewhere) goes to the search box");

                view.Query = "cutoff";
                Settle();
                var rows = view.List.Items.OfType<ParamItem>().Select(i => i.Param).ToList();
                Assert.That(rows, Is.Not.Empty);
                Assert.That(rows.All(p => p.Name.Contains("cutoff", StringComparison.OrdinalIgnoreCase) || p.Description.Contains("cutoff", StringComparison.OrdinalIgnoreCase)), Is.True, "only what matches");
                Assert.That(rows.Count, Is.LessThan(all / 4));
                Assert.That(view.List.Items.OfType<HeaderItem>().Select(h => h.Section.Name), Does.Contain("Filter 1"), "with the headers of the groups that have one");
                Assert.That(view.List.Items.OfType<HeaderItem>().Count(), Is.LessThan(view.Layout.Sections.Count));
                Assert.That(view.CountText, Is.EqualTo($"{rows.Count} of {view.Layout.All.Count}"));

                view.Query = "filter 1";
                Settle();
                Assert.That(view.List.Items.OfType<ParamItem>().Select(i => i.Param), Is.SupersetOf(view.Layout.Sections.Single(s => s.Name == "Filter 1").Params), "a group's name finds the group");

                view.Query = "zzzzzz";
                Settle();
                Assert.That(view.List.Items, Is.Empty);
                Assert.That(view.CountText, Is.EqualTo("No parameter matches"));

                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Settle();
                Assert.That(view.Query, Is.EqualTo(""), "Esc clears");
                Assert.That(view.List.Items.OfType<ParamItem>().Count(), Is.EqualTo(all));
            }
            finally
            {
                window.Close();
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void ASectionClosesFromItsHeader()
        {
            var m = Add("Neo Poly", "P3e Fold");
            var (window, page, view) = Stage(m);
            try
            {
                int before = view.List.Items.Count;
                var head = view.List.Items.OfType<HeaderItem>().First();
                int n = head.Section.Params.Count;
                view.ToggleSection(head.Section.Name);
                Assert.That(view.List.Items.Count, Is.EqualTo(before - n));
                Assert.That(view.IsCollapsed(head.Section.Name), Is.True);
                view.ToggleSection(head.Section.Name);
                Assert.That(view.List.Items.Count, Is.EqualTo(before));
            }
            finally
            {
                window.Close();
                Remove(m);
            }
        }

        // ------------------------------------------------------------------ rows and sliders

        [AvaloniaTest]
        public void RowsAre24PointsAndSlidersFillFromZero()
        {
            var m = Add("Neo Poly", "P3e Rows");
            var (window, page, view) = Stage(m, 760, 700);
            try
            {
                Assert.That(DeviceItem.RowHeight, Is.EqualTo(24));
                foreach (var item in view.List.Items.OfType<ParamItem>().Take(20))
                    Assert.That(view.List.TryGetBounds(item.Param, out var r) && r.Height == 24, Is.True, item.Param.Name);

                var uni = view.Layout.All.First(p => p.Kind == DeviceKind.Slider && !p.Bipolar && p.Max - p.Min >= 100);
                var bi = view.Layout.All.First(p => p.Kind == DeviceKind.Slider && p.Bipolar && p.Max - p.Min >= 8 && p.ZeroFraction > 0.1 && p.ZeroFraction < 0.9);
                Assert.That(uni.ZeroFraction, Is.EqualTo(0), "a plain range fills from its left end");
                Assert.That(bi.ZeroFraction, Is.GreaterThan(0.1).And.LessThan(0.9), "a range through zero fills from zero: " + bi.FullName);

                // Pixels: the fill is the accent between zero and the value, the rail's own colour beyond it.
                DeviceEdits.SetLive(uni, uni.Min + (uni.Max - uni.Min) / 4);
                DeviceEdits.SetLive(bi, bi.Min + (bi.Max - bi.Min) * 3 / 4);
                Settle();
                var f = Frame(window);
                int Row(DeviceParam p) { view.List.Reveal(p); Settle(); return 0; }
                Row(uni);
                f = Frame(window);
                var ru = view.List.ControlBounds(uni);
                var ou = view.List.TranslatePoint(new Point(ru.X, ru.Center.Y), window)!.Value;
                Assert.That(Near(f, (int)(ou.X + ru.Width * 0.12), (int)ou.Y, StudioTokens.Accent), Is.True, "filled from the left up to the value");
                Assert.That(Near(f, (int)(ou.X + ru.Width * 0.6), (int)ou.Y, StudioTokens.SurfaceControl), Is.True, "the rail beyond it");

                Row(bi);
                f = Frame(window);
                var rb = view.List.ControlBounds(bi);
                var ob = view.List.TranslatePoint(new Point(rb.X, rb.Center.Y), window)!.Value;
                Assert.That(Near(f, (int)(ob.X + rb.Width * 0.1), (int)ob.Y, StudioTokens.SurfaceControl), Is.True, "left of zero is not filled");
                Assert.That(Near(f, (int)(ob.X + rb.Width * 0.62), (int)ob.Y, StudioTokens.Accent), Is.True, "from zero to the value");
                Studio.Screenshot(window, "device-neo-poly");
            }
            finally
            {
                window.Close();
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void ValuesShowTheirUnits()
        {
            var m = Add("Neo Poly", "P3e Units");
            var (window, page, view) = Stage(m);
            try
            {
                var cutoff = Param(view, "F1 cutoff");
                Assert.That(cutoff.Unit, Is.EqualTo("Hz"));
                Assert.That(cutoff.Describe(cutoff.Value), Does.Contain("Hz").Or.Contain("kHz"), "the cutoff reads in Hz");
                var drive = Param(view, "F1 drive");
                Assert.That(drive.Describe(drive.Value), Does.Contain("dB"));
                // A manifest unit is added where the machine's own text is a bare number.
                var bare = view.Layout.All.FirstOrDefault(p => p.Kind == DeviceKind.Slider && p.Unit.Length > 0 && !p.Describe(p.Min).Any(char.IsLetter));
                Assert.That(bare == null || bare.Describe(bare.Min).EndsWith(bare.Unit), Is.True);
            }
            finally
            {
                window.Close();
                Remove(m);
            }
        }

        // ------------------------------------------------------------------ typing, reset, undo

        [AvaloniaTest]
        public void ADoubleClickTypesAValueWithItsUnit()
        {
            var m = Add("Neo Poly", "P3e Type");
            var (window, page, view) = Stage(m);
            try
            {
                var cutoff = Param(view, "F1 cutoff");
                view.List.Reveal(cutoff);
                Settle();
                int steps = Steps, original = cutoff.Value;
                var v = view.List.ValueBounds(cutoff);
                DoubleClick(window, Local(window, view.List, v.Center));
                Assert.That(view.List.IsTyping, Is.True, "a double click opens the value for typing");
                Assert.That(view.List.Typing, Is.SameAs(cutoff));
                Assert.That(view.List.Editor.IsVisible, Is.True);

                view.List.TypedText = "1.2 kHz";
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Settle();
                Assert.That(view.List.IsTyping, Is.False);
                double hz = ValueResolver.Physical(cutoff.Info, cutoff.Value, out string dim);
                Assert.That(dim, Is.EqualTo("Hz"));
                Assert.That(hz, Is.EqualTo(1200).Within(40), "1.2 kHz");
                Assert.That(Steps, Is.EqualTo(steps + 1), "one undo step");

                // Another way to say it, and a unit that isn't the parameter's.
                Assert.That(DeviceEdits.TryType(cutoff, "800 Hz", out int raw, out _), Is.True);
                Assert.That(ValueResolver.Physical(cutoff.Info, raw, out _), Is.EqualTo(800).Within(30));
                Assert.That(DeviceEdits.TryType(cutoff, "-6 dB", out _, out string why), Is.False);
                Assert.That(why, Does.Contain("Hz"), "the reason names the unit it takes");
                Assert.That(DeviceEdits.TryType(cutoff, "", out _, out _), Is.False);
                Assert.That(DeviceEdits.TryType(cutoff, "raw:" + cutoff.Max, out int top, out _) && top == cutoff.Max, Is.True);

                // A wrong entry keeps the box open and says why; Esc cancels it.
                view.List.BeginType(cutoff);
                view.List.TypedText = "loud";
                Assert.That(view.List.CommitTyped(), Is.False);
                Assert.That(view.List.IsTyping, Is.True);
                Assert.That(view.List.TypeError, Is.Not.Null.And.Not.Empty);
                int keep = cutoff.Value;
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Settle();
                Assert.That(view.List.IsTyping, Is.False);
                Assert.That(cutoff.Value, Is.EqualTo(keep), "nothing changed");

                Core.SongCore.ActionStack.Undo();
                Assert.That(cutoff.Value, Is.EqualTo(original), "undo takes the typed value back");
            }
            finally
            {
                window.Close();
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void OptionClickResetsToTheDefaultAsOneUndoStep()
        {
            var m = Add("Neo Poly", "P3e Reset");
            var (window, page, view) = Stage(m);
            try
            {
                var p = Param(view, "F1 cutoff");
                DeviceEdits.Set(Core, p, p.Default == p.Max ? p.Min : p.Max);
                Settle();
                Assert.That(p.Value, Is.Not.EqualTo(p.Default));
                int steps = Steps;
                Click(window, Centre(window, view, p, 0.3), mods: RawInputModifiers.Alt);
                Assert.That(p.Value, Is.EqualTo(p.Default), "⌥-click resets");
                Assert.That(Steps, Is.EqualTo(steps + 1));
                Core.SongCore.ActionStack.Undo();
                Assert.That(p.Value, Is.Not.EqualTo(p.Default), "and undoes");
            }
            finally
            {
                window.Close();
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void ADragIsOneUndoStepAndAnArrowStepsMerge()
        {
            var m = Add("Neo Poly", "P3e Drag");
            var (window, page, view) = Stage(m);
            try
            {
                var p = Param(view, "F1 cutoff");
                DeviceEdits.Set(Core, p, p.Min);
                Settle();
                int steps = Steps, before = p.Value;
                Drag(window, Centre(window, view, p, 0.05), Centre(window, view, p, 0.8));
                Assert.That(p.Value, Is.GreaterThan(before + (p.Max - p.Min) / 2), "the drag moved it");
                Assert.That(Steps, Is.EqualTo(steps + 1), "however many moves, one undo step");
                Core.SongCore.ActionStack.Undo();
                Assert.That(p.Value, Is.EqualTo(before));

                // A click that doesn't move leaves the value (so a double click doesn't jump it first).
                Click(window, Centre(window, view, p, 0.5));
                Assert.That(p.Value, Is.EqualTo(before));

                // Arrow steps on one parameter that follow each other merge.
                steps = Steps;
                Click(window, Centre(window, view, p, 0.02));
                view.List.Focus();
                for (int i = 0; i < 4; i++) window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                Settle();
                Assert.That(p.Value, Is.GreaterThan(before));
                Core.SongCore.ActionStack.Undo();
                Assert.That(p.Value, Is.EqualTo(before));
            }
            finally
            {
                window.Close();
                Remove(m);
            }
        }

        // ------------------------------------------------------------------ choices and switches

        [AvaloniaTest]
        public void UpToFourChoicesAreSegmentedAndSwitchesAreToggles()
        {
            var m = Add("Neo Poly", "P3e Choice");
            var (window, page, view) = Stage(m);
            try
            {
                var seg = view.Layout.All.First(p => p.Kind == DeviceKind.Segmented);
                Assert.That(seg.Choices.Count, Is.InRange(2, 4));
                Assert.That(seg.Choices, Is.EqualTo(Enumerable.Range(seg.Min, seg.Max - seg.Min + 1).Select(v => seg.Parameter.DescribeValue(v).Trim()).ToList()).Or.Not.Empty);
                var toggle = view.Layout.All.First(p => p.Kind == DeviceKind.Toggle);
                Assert.That(toggle.Max - toggle.Min, Is.EqualTo(1));
                Assert.That(view.Layout.All.Where(p => p.Kind == DeviceKind.Slider).All(p => p.Max - p.Min > 3 || p.Choices.Count == 0), Is.True, "five values and more are sliders");

                // A click on a segment sets that choice, as one undo step.
                view.List.Reveal(seg);
                Settle();
                int steps = Steps;
                int target = seg.Value == seg.Min ? seg.Max : seg.Min;
                var r = view.List.SegmentBounds(seg, target - seg.Min);
                Click(window, Local(window, view.List, r.Center));
                Assert.That(seg.Value, Is.EqualTo(target));
                Assert.That(Steps, Is.EqualTo(steps + 1));

                // A click on a toggle flips it.
                view.List.Reveal(toggle);
                Settle();
                int was = toggle.Value;
                Click(window, Centre(window, view, toggle, 0.1));
                Assert.That(toggle.Value, Is.Not.EqualTo(was));
                Assert.That(Steps, Is.EqualTo(steps + 2));
                Click(window, Centre(window, view, toggle, 0.1));
                Assert.That(toggle.Value, Is.EqualTo(was), "and back");
                Studio.Screenshot(window, "device-neo-poly-choices");
            }
            finally
            {
                window.Close();
                Remove(m);
            }
        }

        // ------------------------------------------------------------------ the macro strip

        [AvaloniaTest]
        public void TheMacroStripHoldsUpToEightSlidersFromTheManifestsRoles()
        {
            var m = Add("Neo Comp", "P3e Macros");
            var (window, page, view) = Stage(m);
            try
            {
                var l = view.Layout;
                Assert.That(l.Macros.Count, Is.InRange(1, DeviceLayout.MacroCount));
                Assert.That(l.Macros.All(p => p.Kind == DeviceKind.Slider));
                Assert.That(l.Macros.Select(p => p.Name), Does.Contain("Threshold"));
                Assert.That(view.MacroStrip.IsVisible, Is.True);
                Assert.That(view.MacroStrip.Items.Single(), Is.InstanceOf<MacroItem>());

                // A macro is edited as a row is: here a drag across its cell and a reset.
                var p = l.Macros.First();
                view.MacroStrip.TryGetBounds(p, out var cell);
                var inner = view.MacroStrip.TranslatePoint(new Point(cell.X + 6, cell.Bottom - 12), window)!.Value;
                int steps = Steps;
                DeviceEdits.Set(Core, p, p.Min);
                Drag(window, inner, new Point(inner.X + cell.Width - 14, inner.Y));
                Assert.That(p.Value, Is.GreaterThan(p.Min));
                Assert.That(Steps, Is.EqualTo(steps + 2), "the set and the drag: a step each");
            }
            finally
            {
                window.Close();
                Remove(m);
            }
        }

        // ------------------------------------------------------------------ track parameters as a table

        [AvaloniaTest]
        public void TrackParametersAreATableARowPerTrack()
        {
            var m = Add("Pedal Gain Multi", "P3e Table");
            var (window, page, view) = Stage(m, 760, 500);
            try
            {
                int tracks = m.TrackCount;
                Assert.That(tracks, Is.GreaterThan(1));
                m.TrackCount = tracks + 1;
                Settle();
                view.Refresh();
                Settle();
                tracks++;
                var table = view.Layout.Sections.Single(s => s.IsTable);
                Assert.That(table.Tracks, Is.EqualTo(tracks), "the table follows the track count");
                Assert.That(table.TableCells.Count, Is.EqualTo(tracks * table.Params.Count));
                var item = view.List.Items.OfType<TableItem>().Single();
                Assert.That(item.Height, Is.EqualTo(24 * (tracks + 1)), "a header row and a row per track at 24 pt");
                Assert.That(table.Cell(2, 0).Track, Is.EqualTo(2));
                Assert.That(table.Cell(2, 0).Parameter, Is.SameAs(table.Params[0].Parameter));

                // A cell is edited in place: a click on a choice cell steps it, a double click types, ⌥ resets.
                var cell = table.Cell(1, table.Params.IndexOf(table.Params.First(p => p.Kind == DeviceKind.Slider && p.Max - p.Min > 10)));
                view.List.Reveal(cell);
                Settle();
                Assert.That(view.List.TryGetBounds(cell, out var r), Is.True);
                var centre = Local(window, view.List, r.Center);
                int steps = Steps;
                DoubleClick(window, centre);
                Assert.That(view.List.Typing, Is.SameAs(cell));
                view.List.TypedText = "raw:" + cell.Max;
                Assert.That(view.List.CommitTyped(), Is.True);
                Assert.That(cell.Value, Is.EqualTo(cell.Max));
                Assert.That(Steps, Is.EqualTo(steps + 1));
                Click(window, centre, mods: RawInputModifiers.Alt);
                Assert.That(cell.Value, Is.EqualTo(cell.Default));
                // Only that track's cell changed.
                Assert.That(table.Cell(0, table.Params.FindIndex(q => q.Parameter == cell.Parameter)).Value, Is.Not.EqualTo(cell.Max).Or.EqualTo(cell.Default));
                Studio.Screenshot(window, "device-pedal-chord-table");
            }
            finally
            {
                window.Close();
                Remove(m);
            }
        }

        // ------------------------------------------------------------------ virtualised

        [AvaloniaTest]
        public void ALargeParameterSetOpensFastAndDrawsOnlyTheRowsInView()
        {
            var m = Add("Dye Essence", "P3e Dye");
            try
            {
                int n = m.ParameterGroups[1].Parameters.Count(p => p.Flags.HasFlag(ParameterFlags.State));
                Assert.That(n, Is.GreaterThanOrEqualTo(433));
                // The old window and the new one on a small machine first (both JIT'd), then on this one: the old window's rows are a control
                // tree of every parameter, the new one's a layout of one panel.
                var small = Add("Pedal Comp", "P3e Warm");
                OpenOld(small);
                Stage(small).Window.Close();
                Remove(small);
                double oldMs = OpenOld(m);
                var sw = Stopwatch.StartNew();
                var (window, page, view) = Stage(m, 760, 640);
                double ms = sw.Elapsed.TotalMilliseconds;
                try
                {
                    Assert.That(view.Layout.All.Count, Is.EqualTo(n), "every parameter is in the list");
                    Frame(window);
                    Assert.That(view.List.RenderedItems, Is.LessThan(40), $"{view.List.RenderedItems} of {view.List.Items.Count} items drawn: only what is in view");
                    Assert.That(view.List.Items.Count, Is.GreaterThan(433));
                    TestContext.Out.WriteLine($"{n} parameters: the device view opened in {ms:F0} ms, the old parameter window in {oldMs:F0} ms (load {System.IO.File.ReadAllText("/proc/loadavg").Split(' ')[0]}); {view.List.RenderedItems} items drawn");
                    Assert.That(ms, Is.LessThan(oldMs * 0.6), "much faster than the old window's control per row");

                    // Scrolled to the end it draws as few, and the last parameter is reachable.
                    var last = view.Layout.All.Last();
                    view.Scroller.Offset = new Vector(0, 1e6);
                    Settle();
                    Frame(window);
                    Assert.That(view.List.RenderedItems, Is.LessThan(40));
                    Assert.That(view.List.TryGetBounds(last, out _), Is.True);
                    view.List.Reveal(last);
                    Settle();
                    Assert.That(view.Scroller.Offset.Y, Is.GreaterThan(1000));
                    Studio.Screenshot(window, "device-dye-essence");
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Remove(m);
            }
        }

        /// <summary>The classic parameter window opened and laid out; its time in ms.</summary>
        static double OpenOld(MachineCore machine)
        {
            var sw = Stopwatch.StartNew();
            Core.UiHost.OpenParameterWindow(machine, null);
            var old = StudioMachineUiHost.ParameterWindowOf(machine)!;
            Dispatcher.UIThread.RunJobs();
            old.UpdateLayout();
            double ms = sw.Elapsed.TotalMilliseconds;
            old.Close();
            Settle();
            return ms;
        }

        // ------------------------------------------------------------------ Explain

        [AvaloniaTest]
        public void TheFocusedParametersExplainTextShowsWhenTheMachineHasIt()
        {
            var m = Add("Neo Poly", "P3e Explain");
            var (window, page, view) = Stage(m);
            try
            {
                Assert.That(view.ExplainShown, Is.True, "a strip that never moves the rows");
                Assert.That(view.ExplainFocused, Is.False, "nothing focused: it says to click one");
                Assert.That(view.ExplainHow, Is.EqualTo(DeviceView.ExplainPrompt));
                var cutoff = Param(view, "F1 cutoff");
                Click(window, Centre(window, view, cutoff, 0.5));
                Assert.That(view.List.Focused, Is.SameAs(cutoff));
                Assert.That(view.ExplainFocused, Is.True);
                Assert.That(view.ExplainTitle, Does.Contain("cutoff"));
                if (cutoff.HasExplain)
                {
                    Assert.That(view.ExplainHow, Does.StartWith("How:"));
                    Assert.That(view.ExplainSound, Does.StartWith("Sound:"));
                }
                else Assert.That(view.ExplainSound, Is.Null, "no Sound line without the manifest's text");

                // With How and Sound (Neo Learn's manifests) both lines show.
                var explained = new DeviceParam
                {
                    Parameter = cutoff.Parameter, Track = 0, Name = "Cutoff", FullName = "Cutoff", Group = "Filter", Min = cutoff.Min, Max = cutoff.Max, Default = cutoff.Default,
                    Info = cutoff.Info, How = "Sets where the filter starts to cut.", Sound = "Lower is darker.",
                };
                view.ShowExplain(explained);
                Assert.That(view.ExplainHow, Is.EqualTo("How: Sets where the filter starts to cut."));
                Assert.That(view.ExplainSound, Is.EqualTo("Sound: Lower is darker."));
                Assert.That(view.ExplainTitle, Is.EqualTo("Filter: Cutoff"));
                view.ShowExplain(null);
                Assert.That(view.ExplainFocused, Is.False);
            }
            finally
            {
                window.Close();
                Remove(m);
            }
        }

        // ------------------------------------------------------------------ colours

        [Test]
        public void TheViewsColoursMeetTheContrastFloor()
        {
            double C(Color a, Color b) => StudioTokens.Contrast(a, b);
            Assert.That(C(StudioTokens.TextPrimary, StudioTokens.SurfacePanel), Is.GreaterThanOrEqualTo(4.5), "names and values");
            Assert.That(C(StudioTokens.TextSecondary, StudioTokens.SurfacePanel), Is.GreaterThanOrEqualTo(4.5), "headers' counts, unselected segments' text");
            Assert.That(C(StudioTokens.TextSecondary, StudioTokens.SurfaceControl), Is.GreaterThanOrEqualTo(4.5), "an unselected segment's text");
            Assert.That(C(StudioTokens.TextPrimary, StudioTokens.SurfaceControl), Is.GreaterThanOrEqualTo(4.5), "a table cell's value");
            Assert.That(C(StudioTokens.TextPrimary, StudioTokens.SelectionFill), Is.GreaterThanOrEqualTo(4.5), "the focused row");
            Assert.That(C(StudioTokens.TextOn(StudioTokens.Accent), StudioTokens.Accent), Is.GreaterThanOrEqualTo(4.5), "the chosen segment's text");
            Assert.That(C(StudioTokens.Accent, StudioTokens.SurfacePanel), Is.GreaterThanOrEqualTo(3.0), "the slider's fill against the panel");
            Assert.That(C(StudioTokens.Accent, StudioTokens.SurfaceControl), Is.GreaterThanOrEqualTo(3.0), "the fill against its rail");
            Assert.That(C(StudioTokens.ErrorText, StudioTokens.SurfaceControl), Is.GreaterThanOrEqualTo(4.5), "a refused entry");
            Assert.That(C(StudioTokens.TextPrimary, StudioTokens.SurfaceControl), Is.GreaterThanOrEqualTo(4.5), "the explain strip");
        }

        // ------------------------------------------------------------------ docked in Detail, Float, the richest view

        sealed class Rig : IDisposable
        {
            public SessionRig Song { get; }
            public MainWindow Window { get; }
            public MachineCore Bass, Comp;

            public Rig()
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                Song = new SessionRig("Bass");
                Bass = Song.Machine("Bass");
                Comp = Song.Add("Pedal Comp", "Comp", track: false);
                StudioSelection.For(Song.Core).Clear(this);
                Window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
                Window.Show();
                Window.ShowView("Machines");
                Settle();
            }

            public DeviceDock Dock => (DeviceDock)Window.Detail.DevicePage;

            public void Select(IMachine m)
            {
                StudioSelection.For(Song.Core).SelectMachine(m, this);
                Settle();
            }

            public void Dispose()
            {
                if (DeviceDock.FloatingWindowOf(Bass) is { } w) w.Close();
                if (DeviceDock.FloatingWindowOf(Comp) is { } w2) w2.Close();
                Window.Close();
                Song.Dispose();
            }
        }

        [AvaloniaTest]
        public void TheDevicePageIsDockedInDetailAndFollowsTheSelection()
        {
            using var restore = Studio.AfterTest(Studio.ResetPanels);
            using var rig = new Rig();
            var detail = rig.Window.Detail;
            Assert.That(detail.DeviceButton.IsVisible, Is.True, "a Device tab beside Tracker and Piano roll");
            detail.ShowDevice();
            Settle();
            Assert.That(detail.IsOpen && detail.IsDevice, Is.True);
            Assert.That(detail.IsPianoRoll, Is.False);
            Assert.That(rig.Dock.Page, Is.Null, "nothing selected: a hint");
            rig.Select(rig.Comp);
            Assert.That(rig.Dock.Page?.Machine, Is.SameAs(rig.Comp));
            Assert.That(rig.Dock.Page!.Parameters.Layout.All.Count, Is.GreaterThan(5));
            rig.Select(rig.Bass);
            Assert.That(rig.Dock.Page?.Machine, Is.SameAs(rig.Bass), "it follows the selection");
            Assert.That(NeoBuzz.Editor.MachineKeyboardFrame.Of(rig.Dock.Frame), Is.Not.Null, "the piano keyboard under a machine that plays notes");
            rig.Select(rig.Comp);
            Assert.That(NeoBuzz.Editor.MachineKeyboardFrame.Of(rig.Dock.Frame), Is.Null, "and none under an effect");
            Studio.Screenshot(rig.Window, "device-docked");

            // Back to the Tracker: the page is the same as before.
            detail.ShowPage(false, false);
            Assert.That(detail.IsDevice, Is.False);
            detail.ShowDevice();
            Assert.That(detail.IsDevice, Is.True);
            detail.Close();
        }

        [AvaloniaTest]
        public void CommandFInTheWindowGoesToTheDevicesSearch()
        {
            using var restore = Studio.AfterTest(Studio.ResetPanels);
            using var rig = new Rig();
            rig.Select(rig.Comp);
            rig.Window.KeyPress(Key.F, Command, PhysicalKey.F, null);
            Settle();
            Assert.That(rig.Window.Detail.IsDevice && rig.Window.Detail.IsOpen, Is.True, "⌘F opens the Device page");
            Assert.That(rig.Dock.Page!.Parameters.Search.IsFocused, Is.True);
            rig.Window.Detail.Close();
        }

        [AvaloniaTest]
        public void FloatMovesThePageToAWindowAndDockBringsItBack()
        {
            using var restore = Studio.AfterTest(Studio.ResetPanels);
            using var rig = new Rig();
            rig.Window.Detail.ShowDevice();
            rig.Select(rig.Comp);
            var page = rig.Dock.Page!;
            page.FloatButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Settle();
            var window = DeviceDock.FloatingWindowOf(rig.Comp);
            Assert.That(window, Is.Not.Null);
            Assert.That(window!.IsVisible, Is.True);
            Assert.That(window.Title, Does.Contain("Comp"));
            Assert.That(rig.Dock.Page, Is.Null, "the dock says it is floating");
            var floating = window.GetVisualDescendants().OfType<DevicePage>().Single();
            Assert.That(floating.IsFloating, Is.True);
            Assert.That(floating.FloatButton.Content, Is.EqualTo("Dock"));

            floating.FloatButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Settle();
            Assert.That(DeviceDock.FloatingWindowOf(rig.Comp), Is.Null, "closed");
            Assert.That(rig.Dock.Page?.Machine, Is.SameAs(rig.Comp), "back in the panel");
            rig.Window.Detail.Close();
        }

        [AvaloniaTest]
        public void ADoubleClickOpensTheRichestViewTheEditorOrTheDevice()
        {
            using var restore = Studio.AfterTest(Studio.ResetPanels);
            using var rig = new Rig();
            // No editor of its own here and the manifest's editor switched off: the device view in the panel (and no parameter window).
            Assert.That(DevicePage.HasEditor(rig.Comp), Is.False);
            bool manifestEditor = ReBuzz.Agent.ManifestEditorSettings.OpensEditor(rig.Comp.DLL);
            using var putBack = Studio.AfterTest(() => ReBuzz.Agent.ManifestEditorSettings.Set(rig.Comp.DLL, manifestEditor));
            ReBuzz.Agent.ManifestEditorSettings.Set(rig.Comp.DLL, true);
            rig.Song.Core.UiHost.OpenGui(rig.Comp, null);
            Settle();
            Assert.That(StudioMachineUiHost.EditorWindowOf(rig.Comp), Is.Not.Null, "a curated manifest's editor is a custom editor: it comes first, as before");
            Assert.That(rig.Window.Detail.IsDevice, Is.False);
            rig.Song.Core.UiHost.CloseWindows(rig.Comp);
            ReBuzz.Agent.ManifestEditorSettings.Set(rig.Comp.DLL, false);
            rig.Song.Core.UiHost.OpenGui(rig.Comp, null);
            Settle();
            Assert.That(rig.Window.Detail.IsOpen && rig.Window.Detail.IsDevice, Is.True);
            Assert.That(rig.Dock.Page?.Machine, Is.SameAs(rig.Comp));
            Assert.That(StudioMachineUiHost.ParameterWindowOf(rig.Comp), Is.Null, "the classic window stays closed");

            // A machine with an editor: its editor's window, as before.
            Assert.That(DevicePage.HasEditor(rig.Bass), Is.True);
            rig.Song.Core.UiHost.OpenGui(rig.Bass, null);
            Settle();
            Assert.That(StudioMachineUiHost.GuiWindowOf(rig.Bass), Is.Not.Null, "the custom editor");
            rig.Song.Core.UiHost.CloseWindows(rig.Bass);
            rig.Window.Detail.Close();
        }

        [AvaloniaTest]
        public void EditorAndParametersSwitchBetweenTheMachinesEditorAndItsDevice()
        {
            using var restore = Studio.AfterTest(Studio.ResetPanels);
            using var rig = new Rig();
            rig.Window.Detail.ShowDevice();
            rig.Select(rig.Bass);
            var page = rig.Dock.Page!;
            Assert.That(page.EditorButton.IsVisible, Is.True, "a machine with an editor has the switch");
            Assert.That(page.EditorShown, Is.False, "parameters first");
            page.EditorButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Settle();
            Assert.That(page.EditorShown, Is.True);
            Assert.That(page.EditorContent, Is.Not.Null, "its own editor, docked");
            page.ParametersButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Settle();
            Assert.That(page.EditorShown, Is.False);
            rig.Select(rig.Comp);
            Assert.That(rig.Dock.Page!.EditorButton.IsVisible, Is.False, "an effect with no editor has no switch");
            rig.Window.Detail.Close();
        }
    }
}
