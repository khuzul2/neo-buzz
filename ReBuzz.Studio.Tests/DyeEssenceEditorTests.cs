// Fork (docs/specs/dye-essence.md §8, P1c): Dye Essence's editor in the studio, headless. Its pages fit a
// laptop's screen (a 1280 × 720 work area) with the machine keyboard shown under them, every page whole;
// the route list adds, edits and clears routes (parameters, undone as parameter edits); an LFO's shape is
// drawn in place (a built-in shape pressed becomes Drawn, points added and dragged), each gesture one undo
// step with the shape parameter; a factory preset draws its LFO from its data; the shapes ride the machine's
// data (songs, presets) and come back.

using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Platform;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class DyeEssenceEditorTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static MachineCore Add(string name)
        {
            Core.SongCore.CreateMachine("Dye Essence", null, name, null, null, null, -1, 0.2f, 0.4f);
            return Core.SongCore.MachinesList.First(m => m.Name == name);
        }

        static void Remove(MachineCore m)
        {
            Core.UiHost.ReleaseMachine(m);
            Core.SongCore.DeleteMachines(new[] { m });
        }

        static IParameter Global(IMachine m, string name) =>
            m.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.First(p => p.Name == name);

        static object Get(object o, string property) =>
            o.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(o)!;

        static object Call(object o, string method, params object[] args) =>
            o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(o, args)!;

        static string Shape(MachineCore m, int lfo) => Call(m.ManagedMachine, "LfoShape", lfo).ToString()!;

        /// <summary>A rectangle the machine's own kit gives (its NeoRect, a type of the machine's assembly).</summary>
        static Rect Part(Control list, int row, int part)
        {
            object r = Call(list, "Part", row, part);
            double V(string name) => (double)Get(r, name);
            return new Rect(V("X"), V("Y"), V("Width"), V("Height"));
        }

        static Point At(Control c, Window window, double x, double y) => c.TranslatePoint(new Point(x, y), window)!.Value;

        static ReBuzz.Audio.WorkManager work;

        /// <summary>Audio for a moment: the host applies a parameter set from outside on its work thread, and the editor's bindings hear it then.</summary>
        static void Run(double seconds)
        {
            work ??= new ReBuzz.Audio.WorkManager(Core, new ReBuzz.Audio.WorkThreadEngine(1), 0, Core.EngineSettings);
            var buffer = new float[512];
            int blocks = (int)Math.Ceiling(seconds * ReBuzzCore.masterInfo.SamplesPerSec / 256.0);
            lock (ReBuzzCore.AudioLock)
            {
                for (int i = 0; i < blocks; i++) work.MainAudioFillBuffer(buffer, 0, 512);
            }
            Dispatcher.UIThread.RunJobs();
        }

        static void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
        }

        static Window Open(MachineCore m)
        {
            Core.UiHost.OpenGui(m, null);
            var window = StudioMachineUiHost.GuiWindowOf(m)!;
            Assert.That(window, Is.Not.Null);
            Pump();
            window.UpdateLayout();
            window.CaptureRenderedFrame();
            return window;
        }

        static Control Pages(Window window) =>
            window.GetVisualDescendants().OfType<Control>().First(c => c.GetType().Name == "Pages");

        static Control Section(Window window, string title) =>
            window.GetVisualDescendants().OfType<Control>().Single(c => c.GetType().Name == "Section" && Avalonia.Automation.AutomationProperties.GetName(c) == title);

        [AvaloniaTest]
        public void ItsPagesFitALaptopScreenWithTheKeyboard()
        {
            // 1280 × 720 points, less the dock or task bar's 40: the editor and its keyboard (shown) inside it, not scrolled.
            var area = MachineWindowFit.WorkArea;
            MachineWindowFit.WorkArea = w => new Size(1280, 680);
            var m = Add("Pages Dye");
            try
            {
                var window = Open(m);
                var editor = StudioMachineUiHost.ContentOf(window)!;
                var keyboard = StudioMachineUiHost.KeyboardOf(window);
                Assert.That(keyboard, Is.Not.Null, "Dye Essence plays notes: the keyboard is under it");
                if (!(bool)Get(keyboard!, "Shown")) Call(keyboard!, "SetShown", true, false);
                Pump();
                window.UpdateLayout();
                var size = editor.Bounds.Size;
                double extra = NeoBuzz.Editor.MachineKeyboardFrame.ExtraHeightOf(window.Content);
                TestContext.Out.WriteLine($"[notice] Dye Essence's editor: {size.Width:0} x {size.Height:0}, with the keyboard {size.Height + extra:0} (window {window.ClientSize.Width:0} x {window.ClientSize.Height:0} on a 1280 x 680 work area)");

                var pages = Pages(window);
                var names = ((IReadOnlyList<string>)Get(pages, "Names")).ToArray();
                Assert.That(names, Is.EqualTo(new[] { "Oscillators", "Mix & filters", "Modulation", "Arp & Seq" }));
                var pageSize = (Size)Get(pages, "PageSize");
                Assert.Multiple(() =>
                {
                    for (int i = 0; i < names.Length; i++)
                    {
                        pages.GetType().GetProperty("Selected")!.SetValue(pages, i);
                        Pump();
                        window.UpdateLayout();
                        var page = (Control)Call(pages, "Page", i);
                        Assert.That(page.IsVisible, Is.True);
                        page.Measure(Size.Infinity);
                        var want = page.DesiredSize;
                        TestContext.Out.WriteLine($"[notice] Dye Essence page {names[i]}: {want.Width:0} x {want.Height:0} of {pageSize.Width:0} x {pageSize.Height:0}");
                        Assert.That(want.Width, Is.LessThanOrEqualTo(pageSize.Width + 0.5), $"{names[i]}: fits across");
                        Assert.That(want.Height, Is.LessThanOrEqualTo(pageSize.Height + 0.5), $"{names[i]}: fits down");
                        Assert.That(editor.Bounds.Size, Is.EqualTo(size), $"{names[i]}: the editor keeps its size");
                        Studio.Screenshot(window, "dye-essence-editor-" + (i + 1) + "-" + names[i].Split(' ')[0].ToLowerInvariant());
                    }
                });
                Assert.That(size.Width + MachineWindowFit.FrameWidth, Is.LessThanOrEqualTo(1280), "as wide as the screen at most");
                Assert.That(size.Height + extra + MachineWindowFit.FrameHeight, Is.LessThanOrEqualTo(680), "as tall as the screen at most, with the keyboard");
                var view = (Control)NeoBuzz.Editor.MachineKeyboardFrame.TopOf(window.Content)!;
                Assert.That(view.Bounds.Height, Is.GreaterThanOrEqualTo(size.Height - 1), "shown whole: nothing to scroll");

                // The page shown comes back when the editor opens again.
                Core.UiHost.ReleaseMachine(m);
                Pump();
                var again = Open(m);
                Assert.That(Get(Pages(again), "Selected"), Is.EqualTo(names.Length - 1), "the page shown last");
                pages = Pages(again);
                pages.GetType().GetProperty("Selected")!.SetValue(pages, 0);
            }
            finally
            {
                MachineWindowFit.WorkArea = area;
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void EveryEngineTypeKeepsTheOscillatorsPageInItsSize()
        {
            // A slot shows the rows of its engine only: wavetable and analog, sample, granular, harmonic each fit the page.
            var m = Add("Engines Dye");
            try
            {
                var window = Open(m);
                var pages = Pages(window);
                var pageSize = (Size)Get(pages, "PageSize");
                pages.GetType().GetProperty("Selected")!.SetValue(pages, 0);
                Assert.Multiple(() =>
                {
                    foreach (var (type, name) in new[] { (1, "Wavetable"), (2, "Analog"), (3, "Sample"), (4, "Granular"), (5, "Harmonic"), (6, "Modal"), (0, "Off") })
                    {
                        foreach (var slot in new[] { "A", "B", "C" }) Global(m, slot + " engine").SetValue(0, type);
                        Run(0.05);
                        Pump();
                        window.UpdateLayout();
                        var page = (Control)Call(pages, "Page", 0);
                        page.Measure(Size.Infinity);
                        var want = page.DesiredSize;
                        TestContext.Out.WriteLine($"[notice] Dye Essence Oscillators page with {name}: {want.Width:0} x {want.Height:0} of {pageSize.Width:0} x {pageSize.Height:0}");
                        Assert.That(want.Width, Is.LessThanOrEqualTo(pageSize.Width + 0.5), $"{name}: fits across");
                        Assert.That(want.Height, Is.LessThanOrEqualTo(pageSize.Height + 0.5), $"{name}: fits down");
                        window.CaptureRenderedFrame();
                        Studio.Screenshot(window, "dye-essence-editor-engine-" + name.ToLowerInvariant());
                    }
                });
            }
            finally
            {
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void TheRouteListAddsEditsAndClearsRoutes()
        {
            var m = Add("Routes Dye");
            try
            {
                var window = Open(m);
                var pages = Pages(window);
                pages.GetType().GetProperty("Selected")!.SetValue(pages, 2);
                Pump();
                window.UpdateLayout();
                var list = window.GetVisualDescendants().OfType<Control>().Single(c => c.GetType().Name == "RouteList");
                var stack = Core.SongCore.ActionStack;
                int steps = stack.Actions.Count();
                var src = Global(m, "R1 source");
                var dst = Global(m, "R1 dest");
                var amt = Global(m, "R1 amount");

                // "+ Add route": the sources' list; LFO 1.
                var add = Part(list, 0, 0);
                window.MouseDown(At(list, window, add.X + 20, add.Y + 8), MouseButton.Left);
                window.MouseUp(At(list, window, add.X + 20, add.Y + 8), MouseButton.Left);
                var menu = (ContextMenu)Get(list, "Menu");
                Assert.That(menu.IsOpen, Is.True, "the sources' list");
                menu.Items.OfType<MenuItem>().Single(i => (string)i.Header! == "LFO 1").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.Close();
                Pump();
                Assert.That(src.GetValue(0), Is.EqualTo(3), "route 1's source: LFO 1");
                Assert.That(amt.GetValue(0), Is.EqualTo(750), "a new route starts at +50 %");

                // Its destination, from the grouped list.
                var dest = Part(list, 0, 1);
                window.MouseDown(At(list, window, dest.X + 20, dest.Y + 8), MouseButton.Left);
                window.MouseUp(At(list, window, dest.X + 20, dest.Y + 8), MouseButton.Left);
                menu = (ContextMenu)Get(list, "Menu");
                if (menu == null)
                {
                    var p0 = At(list, window, dest.X + 20, dest.Y + 8);
                    var lp = window.TranslatePoint(p0, list)!.Value;
                    TestContext.Out.WriteLine($"DEBUG dest {dest} at {p0} local {lp} hit {list.GetType().GetMethod("HitTest", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(list, new object[] { lp.X, lp.Y })} shown {((System.Collections.IList)Call(list, "Shown")).Count} hit-test {window.InputHitTest(p0)?.GetType().Name}");
                }
                var oscA = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header! == "Osc A");
                oscA.Items.OfType<MenuItem>().Single(i => (string)i.Header! == "A position").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.Close();
                Pump();
                Assert.That(dst.GetValue(0), Is.EqualTo(5), "route 1's destination: A position");

                // Drag the amount left: down from +50 %; one undo step.
                var bar = Part(list, 0, 2);
                var from = At(list, window, bar.X + bar.Width / 2, bar.Y + 8);
                int beforeDrag = stack.Actions.Count();
                window.MouseDown(from, MouseButton.Left);
                for (int dx = 10; dx <= 60; dx += 10) window.MouseMove(from - new Point(dx, 0));
                window.MouseUp(from - new Point(60, 0), MouseButton.Left);
                Pump();
                int dragged = amt.GetValue(0);
                Assert.That(dragged, Is.LessThan(750), "dragging left lowers the amount");
                Assert.That(stack.Actions.Count(), Is.EqualTo(beforeDrag + 1), "the drag is one undo step");
                Studio.Screenshot(window, "dye-essence-editor-routes");

                // × clears it; undo brings it back.
                var x = Part(list, 0, 3);
                window.MouseDown(At(list, window, x.X + x.Width / 2, x.Y + 8), MouseButton.Left);
                window.MouseUp(At(list, window, x.X + x.Width / 2, x.Y + 8), MouseButton.Left);
                Pump();
                Assert.That((src.GetValue(0), dst.GetValue(0), amt.GetValue(0)), Is.EqualTo((0, 0, 500)), "cleared");
                Assert.That(((System.Collections.IList)Call(list, "Shown")).Count, Is.EqualTo(0));
                Core.SongCore.Undo();
                Pump();
                Assert.That((src.GetValue(0), dst.GetValue(0), amt.GetValue(0)), Is.EqualTo((3, 5, dragged)), "undo brings the route back");
                Assert.That(stack.Actions.Count(), Is.GreaterThan(steps));
            }
            finally
            {
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void AnLfoShapeIsDrawnInPlaceAndUndone()
        {
            var m = Add("Shapes Dye");
            try
            {
                var window = Open(m);
                var pages = Pages(window);
                pages.GetType().GetProperty("Selected")!.SetValue(pages, 2);
                Pump();
                window.UpdateLayout();
                var lfo = Section(window, "LFOs");
                var inner = lfo.GetVisualDescendants().OfType<Control>().First(c => c.GetType().Name == "Pages");
                inner.GetType().GetProperty("Selected")!.SetValue(inner, 0);   // LFO 1 (the page shown last comes back)
                Pump();
                window.UpdateLayout();
                var view = lfo.GetVisualDescendants().OfType<Control>().Single(c => c.GetType().Name == "InteractiveGraph" && c.IsEffectivelyVisible);
                var shapeParam = Global(m, "LFO1 shape");
                var stack = Core.SongCore.ActionStack;
                string sine = Shape(m, 0);
                Assert.That(shapeParam.GetValue(0), Is.EqualTo(0), "Sine");
                int steps = stack.Actions.Count();

                // A press on the built-in shape: the LFO plays it drawn (the sine's points), one undo step.
                var mid = At(view, window, view.Bounds.Width / 2, view.Bounds.Height / 2);
                window.MouseDown(mid, MouseButton.Left);
                window.MouseUp(mid, MouseButton.Left);
                Pump();
                Assert.That(shapeParam.GetValue(0), Is.EqualTo(7), "Drawn");
                Assert.That(Shape(m, 0), Is.EqualTo(sine), "drawn from the sine");
                Assert.That(stack.Actions.Count(), Is.EqualTo(steps + 1), "one step");

                // A press on the empty upper right adds a point there, dragged down a little.
                var press = At(view, window, view.Bounds.Width * 0.62, 12);
                window.MouseDown(press, MouseButton.Left);
                window.MouseMove(press + new Point(4, 8));
                window.MouseUp(press + new Point(4, 8), MouseButton.Left);
                Pump();
                string drawn = Shape(m, 0);
                Assert.That(drawn.Split(' '), Has.Length.EqualTo(5), "a fifth point");
                Assert.That(stack.Actions.Count(), Is.EqualTo(steps + 2), "the point is one more step");
                Studio.Screenshot(window, "dye-essence-editor-drawn-lfo");

                // The machine's data carries it (songs and presets) and gives it back.
                byte[] data = m.Data;
                Core.SongCore.Undo();
                Pump();
                Assert.That(Shape(m, 0), Is.EqualTo(sine), "undo takes the point away");
                Assert.That(shapeParam.GetValue(0), Is.EqualTo(7));
                Core.SongCore.Undo();
                Pump();
                Assert.That(shapeParam.GetValue(0), Is.EqualTo(0), "and the second undo, Drawn");
                m.Data = data;
                Assert.That(Shape(m, 0), Is.EqualTo(drawn), "the data round trip");

                // A factory preset draws its LFO from its data.
                m.GetPreset("Pad — Drawn Gate").Apply(m, true);
                Pump();
                Assert.That(Global(m, "LFO1 shape").GetValue(0), Is.EqualTo(7));
                Assert.That(Shape(m, 0), Does.StartWith("0,-1 0.006,0,-0.5 0.1062,-1"), "the preset's shape");
            }
            finally
            {
                Remove(m);
            }
        }
        [AvaloniaTest]
        public void TheSequencersStepsAreEditedInPlaceAndUndone()
        {
            var m = Add("Steps Dye");
            try
            {
                var window = Open(m);
                var pages = Pages(window);
                pages.GetType().GetProperty("Selected")!.SetValue(pages, 3);
                Pump();
                window.UpdateLayout();
                var seq = Section(window, "Sequencer");
                var inner = seq.GetVisualDescendants().OfType<Control>().First(c => c.GetType().Name == "Pages");
                inner.GetType().GetProperty("Selected")!.SetValue(inner, 0);   // the Note lane
                Pump();
                window.UpdateLayout();
                var view = seq.GetVisualDescendants().OfType<Control>().Single(c => c.GetType().Name == "InteractiveGraph" && c.IsEffectivelyVisible);
                string Steps() => Get(m.ManagedMachine, "Pattern").ToString()!;
                var stack = Core.SongCore.ActionStack;
                int steps = stack.Actions.Count();
                Assert.That(Steps(), Is.EqualTo(""), "plain steps to start with");

                // A press a quarter of the way down step 5's bar: +12 semitones (the middle is 0, the top +24), one undo step.
                double w = (view.Bounds.Width - 16) / 32, top = 8, height = view.Bounds.Height - 30;
                var press = At(view, window, 8 + 4.5 * w, top + 0.25 * height);
                window.MouseDown(press, MouseButton.Left);
                // Dragged across to step 6 and down to the middle: that step 0.
                window.MouseMove(At(view, window, 8 + 5.5 * w, top + 0.5 * height));
                window.MouseUp(At(view, window, 8 + 5.5 * w, top + 0.5 * height), MouseButton.Left);
                Pump();
                var written = Steps().Split(' ');
                Assert.That(written[4], Does.StartWith("12,"), "step 5: +12");
                Assert.That(stack.Actions.Count(), Is.EqualTo(steps + 1), "the gesture is one step");
                Studio.Screenshot(window, "dye-essence-editor-sequencer");

                byte[] data = m.Data;
                string drawn = Steps();
                Core.SongCore.Undo();
                Pump();
                Assert.That(Steps(), Is.EqualTo(""), "undo puts the steps back");
                m.Data = data;
                Assert.That(Steps(), Is.EqualTo(drawn), "the data round trip");

                // A factory preset carries its steps.
                m.GetPreset("Seq — Acid Line").Apply(m, true);
                Pump();
                Assert.That(Steps(), Does.StartWith("0,110,50,0,100 0,70,50,0,100 12,100,50,0,100 0,0,"), "the preset's steps");
                Assert.That(Global(m, "Seq mode").GetValue(0), Is.EqualTo(1), "Notes");
            }
            finally
            {
                Remove(m);
            }
        }
    }
}
