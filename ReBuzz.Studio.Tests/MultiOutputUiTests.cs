// Fork (docs/specs/multi-output.md, MO1 #228): machines with several outputs in the Studio: the Machines view's out-ports, the Inspector's
// From output picker and the mixer's strip for each wired extra output, against the tests' Multi Test machine (three outputs: Low, Mid, High).
// The tests marked Gallery draw the shots the Mac run keeps (GALLERY_DIR), dark and light.

using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Machines;
using ReBuzz.Studio.Views.Mixer;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class MultiOutputUiTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static IMachine Make(string gear, float x, float y) =>
            MachineEdits.Create(Core, Core.Instruments.First(i => i.MachineDLL.Name == gear && i.Name.Length == 0), x, y);

        static void Shot(TopLevel window, string name)
        {
            string path = Studio.Screenshot(window, name);
            if (Environment.GetEnvironmentVariable("GALLERY_DIR") is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
                File.Copy(path, Path.Combine(dir, name + ".png"), true);
            }
        }

        static void Undo()
        {
            Core.SongCore.ActionStack.Undo();
            Settle();
        }

        static string Wires() => string.Join("; ", Core.SongCore.Machines.SelectMany(m => m.Outputs)
            .Select(c => $"{c.Source.Name}:{c.SourceChannel}→{c.Destination.Name}").OrderBy(s => s));

        /// <summary>Multi Test at the left with three chains to its right, on the real canvas; wires from its outputs 1, 2 and 3.</summary>
        static (IMachine Multi, IMachine[] Chain) Scene(MachineView view, bool wire = true)
        {
            var multi = Make("Multi Test", -0.8f, 0f);
            var chain = new[] { Make("Neo Utility", 0.6f, -0.45f), Make("Neo Utility", 0.6f, 0f), Make("Neo Utility", 0.6f, 0.45f) };
            foreach (var w in multi.Outputs.Where(o => o.Destination.Name == "Master").ToList()) Core.SongCore.DisconnectMachines(w);
            var master = Core.SongCore.Machines.First(m => m.DLL.Info.Type == MachineType.Master);
            foreach (var c in chain) if (!c.Outputs.Any()) MachineEdits.Connect(Core, c, master);
            if (wire)
                for (int k = 0; k < 3; k++) MachineEdits.Connect(Core, multi, chain[k], k);
            Settle();
            return (multi, chain);
        }

        [AvaloniaTest]
        public void APortDragStartsTheWireOnThatOutputAndUndoRemovesIt()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var (multi, chain) = Scene(view, wire: false);
                for (int k = 0; k < 3; k++)
                {
                    var from = view.FromCanvas(view.Canvas.OutPortOf(multi, k), window);
                    var to = view.PointOf(chain[k], window);
                    Drag(window, from, to);
                }
                Settle();
                Assert.That(multi.Outputs.Where(o => o.Destination.Name != "Master").OrderBy(o => o.SourceChannel).Select(o => (o.SourceChannel, o.Destination.Name)),
                    Is.EqualTo(new[] { (0, chain[0].Name), (1, chain[1].Name), (2, chain[2].Name) }), "each wire starts at the port it was dragged from");

                // Hover names the output.
                window.MouseMove(view.FromCanvas(view.Canvas.OutPortOf(multi, 2), window));
                Settle();
                Assert.That(view.Canvas.HoverTip, Does.Contain("High").And.Contain("output 3 of 3"));

                Undo();
                Assert.That(multi.Outputs.Count(o => o.Destination.Name != "Master"), Is.EqualTo(2), "one undo: one wire");
            }
            finally { Close(window); }
        }

        [AvaloniaTest]
        public void TheInspectorsFromOutputPickerMovesTheWireInOneUndoableStep()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var (multi, chain) = Scene(view);
                StudioSelection.For(Core).SelectMachine(multi, window);
                Settle();
                var section = window.Inspector.WireOutputs;
                Assert.That(section.Pickers.Count, Is.EqualTo(3), "one picker per outgoing wire");
                var (wire, picker) = section.Pickers.Single(p => p.Wire.Destination == chain[0]);
                Assert.That(picker.SelectedIndex, Is.EqualTo(0));
                picker.SelectedIndex = 2;
                Settle();
                Assert.That(wire.SourceChannel, Is.EqualTo(2), "the wire reads output 3 now");
                Undo();
                Assert.That(wire.SourceChannel, Is.EqualTo(0), "undo puts it back");
            }
            finally { Close(window); }
        }

        [AvaloniaTest]
        public void InsertingOnAWireKeepsTheWiresOutput()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var (multi, chain) = Scene(view);
                var fx = Make("Neo Utility", 0.2f, 0.9f);
                var wire = multi.Outputs.Single(o => o.Destination == chain[2]);
                MachineEdits.MoveIntoWire(Core, fx, 0.2f, 0.9f, wire);
                Settle();
                Assert.That(multi.Outputs.Single(o => o.Destination == fx).SourceChannel, Is.EqualTo(2), "the wire into the inserted machine is still on output 3");
            }
            finally { Close(window); }
        }

        [AvaloniaTest]
        public void MixerStripsFollowTheWiresAndTheirFaderIsTheWiresAmp()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var (multi, chain) = Scene(view);
                var mixer = new MixerView(Core, MixerDensity.Compact);
                var host = new Window { Content = mixer, Width = 900, Height = 400 };
                host.Show();
                Settle();
                mixer.Rebuild(true);
                Settle();
                string id(int k) => "output:" + multi.Name + ">" + chain[k].Name;
                Assert.That(mixer.Strips.Any(s => s.Spec.Id == id(1)), Is.True, "a strip for the wired output 2: " + string.Join(",", mixer.Strips.Select(s => s.Spec.Id)));
                Assert.That(mixer.Strips.Single(s => s.Spec.Id == id(1)).Spec.Name, Is.EqualTo(multi.Name + " · Mid"));
                Assert.That(mixer.Strips.Any(s => s.Spec.Id == id(0)), Is.False, "none for output 1: that is the machine's own strip");

                var third = multi.Outputs.Single(o => o.Destination == chain[2]);
                Core.SongCore.DisconnectMachines(third);
                mixer.Rebuild(true);
                Settle();
                Assert.That(mixer.Strips.Any(s => s.Spec.Id == id(2)), Is.False, "none for an unwired output 3");
                MachineEdits.Connect(Core, multi, chain[2], 2);
                mixer.Rebuild(true);
                Settle();
                Assert.That(mixer.Strips.Any(s => s.Spec.Id == id(2)), Is.True, "it appears with its wire");

                var strip = mixer.Strips.Single(s => s.Spec.Id == id(2));
                var wire = multi.Outputs.Single(o => o.Destination == chain[2]);
                wire.Amp = 0x2000;
                strip.Refresh();
                Assert.That(strip.Fader.Value, Is.EqualTo(SessionMixer.FaderOf(wire.Amp)).Within(0.01), "the wire's amp moves the fader");

                int before = wire.Amp;
                var pt = strip.Fader.TranslatePoint(new Point(strip.Fader.Bounds.Width / 2, strip.Fader.Bounds.Height / 2), host) ?? default;
                Drag(host, pt, pt + new Point(0, -20));
                Assert.That(wire.Amp, Is.Not.EqualTo(before), "the fader moves the wire's amp");

                Core.SongCore.DisconnectMachines(wire);
                mixer.Rebuild(true);
                Settle();
                Assert.That(mixer.Strips.Any(s => s.Spec.Id == id(2)), Is.False, "and goes with it");
                host.Close();
            }
            finally { Close(window); }
        }

        [AvaloniaTest, Category("Gallery")]
        public void TheGalleryShotsOfSeveralOutputs()
        {
            // As GalleryTests: GALLERY_THEME=light draws the shots in the Light theme (the Mac job runs the gallery in both).
            var theme = ReBuzz.Studio.Shell.Design.StudioTheme.Kind;
            using var restore = Studio.AfterTest(() => ReBuzz.Studio.Shell.Design.StudioTheme.Kind = theme);
            if (Environment.GetEnvironmentVariable("GALLERY_THEME") is { } t && t.Equals("light", StringComparison.OrdinalIgnoreCase))
                ReBuzz.Studio.Shell.Design.StudioTheme.Kind = ReBuzz.Studio.Shell.Design.AppearanceTheme.Light;
            var (window, view) = OpenEmpty();
            try
            {
                var (multi, chain) = Scene(view);
                Settle();
                Shot(window, "multi-output-machines");

                StudioSelection.For(Core).SelectMachine(multi, window);
                Settle();
                Shot(window, "multi-output-inspector");

                var mixer = new MixerView(Core, MixerDensity.Compact);
                var host = new Window { Content = mixer, Width = 1100, Height = 420 };
                host.Show();
                Settle();
                Shot(host, "multi-output-mixer");
                host.Close();
            }
            finally { Close(window); }
        }
    }
}
