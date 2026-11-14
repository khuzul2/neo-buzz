// Fork (docs/specs/neo-patch.md NP2): Neo Patch's editor in the studio, headless. It opens as the machine's GUI and renders; the starter
// button, the right-click menu with search, the keys, a wire dragged between dots (and a loop refused with the reason on the canvas), a
// slider in the side panel and the macro strip all work through the mouse and keyboard as a person uses them, each edit is one step on the
// song's undo stack, and the zoom is saved with the machine. The editor's model and drawing are tested without a toolkit in
// NeoBuzz.MachineTests (NeoPatchEditorTests); this is the same surface under Avalonia.

using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NeoBuzz.Editor;
using ReBuzz.Core;
using ReBuzz.Studio.Platform;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class NeoPatchStudioTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        // The surface's layout (PatchSurface): 1180 x 660, a 36 px toolbar, an 84 px macro strip, a 300 px side panel.
        const double CanvasX = 0, CanvasY = 36, CanvasW = 880, CanvasH = 540;

        static MachineCore Add()
        {
            Core.SongCore.CreateMachine("Neo Patch", null, "Patch", null, null, null, -1, 0.3f, 0.3f);
            return Core.SongCore.MachinesList.First(m => m.Name == "Patch");
        }

        static void Remove(MachineCore m)
        {
            Core.UiHost.ReleaseMachine(m);
            Core.SongCore.DeleteMachines(new[] { m });
        }

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>A property or a field, whichever the type has.</summary>
        static object Get(object o, string name) =>
            o.GetType().GetProperty(name, Any) is { } p ? p.GetValue(o) : o.GetType().GetField(name, Any)!.GetValue(o);

        static object Call(object o, string name, params object[] args) =>
            o.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(o, args);

        static double Num(object o, string name) => Convert.ToDouble(Get(o, name));

        sealed class Rig
        {
            public MachineCore Machine;
            public Window Window;
            public Control Graph;
            public object Editor, Surface, Model, Engine;

            public string Text => (string)Call(Engine, "Text");
            public int Nodes => Text.Split(';').Count(p => p.StartsWith("n "));
            public int Wires => Text.Split(';').Count(p => p.StartsWith("w "));
            public int Steps => Core.SongCore.ActionStack.Actions.Count();

            public Point P(double x, double y) => Graph.TranslatePoint(new Point(x, y), Window)!.Value;

            /// <summary>Where a node's port is, in window coordinates.</summary>
            public Point Port(int nodeId, int port, bool output)
            {
                var node = Call(Get(Model, "Graph"), "Find", nodeId);
                var pos = Call(Surface, "PortPos", node, port, output);
                return P(Num(pos, "X"), Num(pos, "Y"));
            }

            public void Settle() { Dispatcher.UIThread.RunJobs(); foreach (var v in Window.GetVisualDescendants()) v.InvalidateVisual(); Dispatcher.UIThread.RunJobs(); }
        }

        static Rig Open() => OpenOn(Add());

        static Rig OpenOn(MachineCore m)
        {
            Core.UiHost.OpenGui(m, null);
            var window = StudioMachineUiHost.GuiWindowOf(m);
            Assert.That(window, Is.Not.Null, "its GUI opened");
            Dispatcher.UIThread.RunJobs();
            var content = StudioMachineUiHost.ContentOf(window);
            Assert.That(content?.GetType().Name, Is.EqualTo("NeoPatchEditor"), "its own editor, not the parameter window");
            var machine = m.ManagedMachine;
            var rig = new Rig
            {
                Machine = m, Window = window!, Editor = content!,
                Graph = window!.GetVisualDescendants().OfType<Control>().Single(c => c.GetType().Name == "InteractiveGraph" && Math.Abs(c.Bounds.Width - 1180) < 2),
                Engine = Get(machine, "Engine"),
            };
            rig.Surface = Get(content!, "Surface");
            rig.Model = Get(content!, "Model");
            // A new Neo Patch holds the starting patch; these tests begin from the empty one (the editor's own template).
            Call(rig.Engine, "SetPatch", "neopatch 1");
            Call(rig.Model, "Replaced");
            Call(rig.Surface, "Fit");
            rig.Settle();
            return rig;
        }

        static void Close(Rig r)
        {
            Core.UiHost.CloseWindows(r.Machine);
            Remove(r.Machine);
        }

        static (uint[] Pixels, int Width, int Height) Frame(Window window, string screenshot = null)
        {
            foreach (var v in window.GetVisualDescendants()) v.InvalidateVisual();
            Dispatcher.UIThread.RunJobs();
            if (screenshot != null) Studio.Screenshot(window, screenshot);
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

        static int Count((uint[] Pixels, int Width, int Height) f, Rect area, uint rgb, int tolerance = 30)
        {
            int n = 0;
            for (int y = (int)area.Top; y < (int)area.Bottom && y < f.Height; y++)
                for (int x = (int)area.Left; x < (int)area.Right && x < f.Width; x++)
                {
                    uint p = f.Pixels[y * f.Width + x];
                    int d = Math.Abs((int)((p >> 16) & 0xFF) - (int)((rgb >> 16) & 0xFF)) + Math.Abs((int)((p >> 8) & 0xFF) - (int)((rgb >> 8) & 0xFF)) + Math.Abs((int)(p & 0xFF) - (int)(rgb & 0xFF));
                    if (d <= tolerance) n++;
                }
            return n;
        }

        static Rect Area(Rig r) => new Rect(r.Graph.TranslatePoint(new Point(0, 0), r.Window)!.Value, r.Graph.Bounds.Size);

        static void Starter(Rig r) => MachineViewTests.Click(r.Window, r.P(CanvasX + CanvasW / 2, CanvasY + CanvasH / 2 + 29));

        /// <summary>The gallery's Neo Patch shots (GalleryTests, 48x): the machine's editor opened and put in the state the shot shows; the caller takes the picture and closes the windows.</summary>
        internal static Window GalleryShot(string kind, MachineCore m)
        {
            var r = OpenOn(m);                       // opens on the empty patch
            switch (kind)
            {
                case "empty":
                    break;
                case "big20":
                {
                    var asm = m.ManagedMachine.GetType().Assembly;
                    var graph = asm.GetType("NeoBuzz.Patch.PatchTemplates")!.GetMethod("Big20")!.Invoke(null, null)!;
                    var text = asm.GetType("NeoBuzz.Patch.PatchText")!.GetMethod("Save", new[] { graph.GetType() })!.Invoke(null, new[] { graph });
                    Call(r.Engine, "SetPatch", text!);
                    Call(r.Model, "Replaced");
                    Call(r.Surface, "TidyIfStacked");
                    Call(r.Surface, "Fit");
                    break;
                }
                case "selected":
                case "menu":
                case "cycle":
                {
                    Starter(r);
                    if (kind == "selected")
                    {
                        var node = Call(Get(r.Model, "Graph"), "Find", 2);
                        var rect = Call(r.Surface, "NodeRect", node);
                        MachineViewTests.Click(r.Window, r.P(Num(rect, "X") + 60, Num(rect, "Y") + 10));
                    }
                    else if (kind == "menu")
                    {
                        var at = r.P(CanvasX + 640, CanvasY + 330);
                        r.Window.MouseDown(at, MouseButton.Right); r.Window.MouseUp(at, MouseButton.Right);
                        r.Window.KeyTextInput("fil");
                    }
                    else MachineViewTests.Drag(r.Window, r.Port(4, 0, true), r.Port(2, 1, false));
                    break;
                }
            }
            r.Settle();
            return r.Window;
        }

        [AvaloniaTest]
        public void TheEditorOpensEmptyAndTheStarterIsOneUndoStep()
        {
            var r = Open();
            try
            {
                Assert.That(r.Nodes, Is.EqualTo(0));
                var empty = Frame(r.Window, "neo-patch-editor-empty");
                uint accent = Tokens.Identity(MachineCategory.Synth).Argb & 0xFFFFFF;
                Assert.That(Count(empty, Area(r), accent), Is.GreaterThan(40), "the starter button is drawn in the machine's accent");
                int steps = r.Steps;
                Starter(r);
                Assert.That(r.Nodes, Is.EqualTo(5), "Keys, Osc, Envelope, VCA and Out");
                Assert.That(r.Wires, Is.EqualTo(5));
                Assert.That(r.Steps, Is.EqualTo(steps + 1), "one undo step");
                var full = Frame(r.Window, "neo-patch-editor-basic-voice");
                Assert.That(Count(full, Area(r), Tokens.Identity(MachineCategory.Control).Argb & 0xFFFFFF), Is.GreaterThan(40), "the Keys node's identity band");
                Core.SongCore.Undo();
                Assert.That(r.Nodes, Is.EqualTo(0), "undo takes the voice away");
                Core.SongCore.Redo();
                Assert.That(r.Nodes, Is.EqualTo(5));
            }
            finally { Close(r); }
        }

        [AvaloniaTest]
        public void TheRightClickMenuSearchesAndAddsANodeWhereYouClicked()
        {
            var r = Open();
            try
            {
                Starter(r);
                int steps = r.Steps;
                var at = r.P(CanvasX + 700, CanvasY + 420);
                r.Window.MouseDown(at, MouseButton.Right);
                r.Window.MouseUp(at, MouseButton.Right);
                r.Settle();
                Assert.That(Get(r.Surface, "Menu"), Is.Not.Null, "the menu opened");
                r.Window.KeyTextInput("lfo");
                r.Settle();
                Frame(r.Window, "neo-patch-editor-node-menu");
                r.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                r.Settle();
                Assert.That(Get(r.Surface, "Menu"), Is.Null);
                Assert.That(r.Nodes, Is.EqualTo(6));
                Assert.That(r.Text, Does.Contain(" Lfo "));
                Assert.That(r.Steps, Is.EqualTo(steps + 1));
                Core.SongCore.Undo();
                Assert.That(r.Nodes, Is.EqualTo(5));
            }
            finally { Close(r); }
        }

        [AvaloniaTest]
        public void AWireIsDraggedFromADotToADotAndALoopIsRefusedWithItsReason()
        {
            var r = Open();
            try
            {
                Starter(r);
                // A loop: the VCA's output into the oscillator's FM input.
                int steps = r.Steps, wires = r.Wires;
                var from = r.Port(4, 0, true);
                var to = r.Port(2, 1, false);
                MachineViewTests.Drag(r.Window, from, to);
                r.Settle();
                Assert.That(r.Wires, Is.EqualTo(wires), "refused");
                Assert.That(r.Steps, Is.EqualTo(steps), "a refusal is not a step");
                var shot = Frame(r.Window, "neo-patch-editor-refused-cycle");
                Assert.That(Count(shot, Area(r), Tokens.Error.Argb & 0xFFFFFF), Is.GreaterThan(60), "the banner and the loop are in the error colour");
                var model = r.Model;
                Assert.That(Get(model, "LastRefusal"), Is.Not.Null);
                Assert.That((string)Get(Get(model, "LastRefusal"), "Message"), Does.Contain("Feedback"));

                // A wire that is fine: an LFO's output to the oscillator's FM input.
                var at = r.P(CanvasX + 700, CanvasY + 420);
                r.Window.MouseDown(at, MouseButton.Right); r.Window.MouseUp(at, MouseButton.Right);
                r.Window.KeyTextInput("lfo");
                r.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                r.Settle();
                Assert.That(r.Nodes, Is.EqualTo(6));
                steps = r.Steps;
                MachineViewTests.Drag(r.Window, r.Port(6, 0, true), r.Port(2, 1, false));
                r.Settle();
                Assert.That(r.Wires, Is.EqualTo(wires + 1), "connected");
                Assert.That(r.Steps, Is.EqualTo(steps + 1), "one undo step");
                Core.SongCore.Undo();
                Assert.That(r.Wires, Is.EqualTo(wires));
            }
            finally { Close(r); }
        }

        [AvaloniaTest]
        public void KeysSelectCopyPasteAndDeleteAndEachIsOneStep()
        {
            var r = Open();
            try
            {
                Starter(r);
                MachineViewTests.Click(r.Window, r.P(CanvasX + 20, CanvasY + CanvasH - 6));      // focus the canvas
                int steps = r.Steps;
                r.Window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
                r.Window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
                r.Window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control);
                r.Settle();
                Assert.That(r.Nodes, Is.EqualTo(10), "pasted");
                Assert.That(r.Steps, Is.EqualTo(steps + 1));
                r.Window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
                r.Settle();
                Assert.That(r.Nodes, Is.EqualTo(5), "the pasted ones were selected, so they are deleted");
                Assert.That(r.Steps, Is.EqualTo(steps + 2));
                Core.SongCore.Undo(); Core.SongCore.Undo();
                Assert.That(r.Nodes, Is.EqualTo(5));
            }
            finally { Close(r); }
        }

        [AvaloniaTest]
        public void ASelectedNodesSliderIsDraggedInTheSidePanelAsOneStep()
        {
            var r = Open();
            try
            {
                Starter(r);
                r.Settle();
                // Click the oscillator's header to select it.
                var node = Call(Get(r.Model, "Graph"), "Find", 2);
                var rect = Call(r.Surface, "NodeRect", node);
                MachineViewTests.Click(r.Window, r.P(Num(rect, "X") + 60, Num(rect, "Y") + 10));
                r.Settle();
                var items = ((System.Collections.IEnumerable)Get(r.Surface, "PanelItems")).Cast<object>().ToList();
                var tune = items.First(i => Get(i, "Kind").ToString() == "Slider" && Convert.ToInt32(Get(i, "Control")) == 1);
                var rc = Get(tune, "Rect");
                double x = Num(rc, "X"), y = Num(rc, "Y") + 6, w = Num(rc, "Width");
                Frame(r.Window, "neo-patch-editor-selected-node");
                int steps = r.Steps;
                string before = r.Text;
                MachineViewTests.Drag(r.Window, r.P(x + w / 2, y), r.P(x + w * 0.9, y));
                r.Settle();
                Assert.That(r.Text, Is.Not.EqualTo(before), "the control changed");
                Assert.That(r.Steps, Is.EqualTo(steps + 1), "the drag is one undo step");
                Core.SongCore.Undo();
                Assert.That(r.Text, Is.EqualTo(before));
            }
            finally { Close(r); }
        }

        [AvaloniaTest]
        public void TheZoomIsSavedWithTheMachine()
        {
            var r = Open();
            try
            {
                Starter(r);
                string view0 = (string)Get(r.Surface, "ViewText");
                r.Window.MouseWheel(r.P(400, 300), new Vector(0, 3));
                r.Settle();
                string view1 = (string)Get(r.Surface, "ViewText");
                Assert.That(view1, Is.Not.EqualTo(view0), "the wheel zoomed");
                Frame(r.Window);
                Assert.That((string)Get(r.Machine.ManagedMachine, "View"), Is.EqualTo(view1), "the machine keeps the view, so a saved song reopens as it was");
            }
            finally { Close(r); }
        }
    }
}
