// Fork (docs/specs/instrument-input.md, AI1a #229): audio into an instrument in the Studio: the Machines view's in-port on a generator that
// declares an input (drag, drop, Shift-drag, the words of a refusal, insert on a wire) and the Inspector's wire count, against the tests'
// Input Test and Noise Test machines. The test marked Gallery draws the shots the Mac run keeps (GALLERY_DIR), dark and light.

using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views;
using NUnit.Framework;
using ReBuzz.Studio.Views.Machines;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class InstrumentInputUiTests
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

        /// <summary>A noise source at the left, Input Test in the middle (on Master) and Neo Poly at the right, none wired but to Master.</summary>
        static (IMachine Noise, IMachine Input, IMachine Poly) Scene()
        {
            var noise = Make("Noise Test", -0.7f, -0.1f);
            var input = Make("Input Test", 0f, -0.1f);
            var poly = Make("Neo Poly", 0.6f, 0.4f);
            foreach (var m in new[] { noise, input, poly })
                foreach (var w in m.Outputs.Where(o => o.Destination.Name == "Master").ToList()) Core.SongCore.DisconnectMachines(w);
            var master = Core.SongCore.Machines.First(m => m.DLL.Info.Type == MachineType.Master);
            MachineEdits.Connect(Core, input, master);
            Settle();
            return (noise, input, poly);
        }

        [AvaloniaTest]
        public void AGeneratorThatTakesAudioHasAnInPortAndOneThatDoesNotHasNone()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var (noise, input, poly) = Scene();
                window.MouseMove(view.PointOf(input, window));
                Settle();
                Assert.That(view.Canvas.HasInputPort(input), Is.True, "Input Test has an in-port");
                Assert.That(view.Canvas.HasInputPort(poly), Is.False, "Neo Poly has none");
                Assert.That(view.Canvas.HasInputPort(noise), Is.False, "nor has a source");

                // The words over the dot are an effect's.
                Point dot = view.FromCanvas(view.Canvas.InPortOf(input), window);
                for (int tries = 0; tries < 30 && !(view.Canvas.HoverTip ?? "").StartsWith("Input of"); tries++)
                {
                    window.MouseMove(dot);
                    Settle();
                }
                Assert.That(view.Canvas.HoverTip, Is.EqualTo("Input of " + input.Name + ": drop a wire from another machine's output here"));
            }
            finally { Close(window); }
        }

        [AvaloniaTest]
        public void ADragOntoTheInPortConnectsAndAShiftDragOntoTheBoxToo()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var (noise, input, poly) = Scene();
                var other = Make("Noise Test", -0.7f, 0.5f);
                foreach (var w in other.Outputs.Where(o => o.Destination.Name == "Master").ToList()) Core.SongCore.DisconnectMachines(w);
                Settle();

                // Shift-drag from the box onto the in-port.
                Drag(window, view.PointOf(noise, window), view.FromCanvas(view.Canvas.InPortOf(input), window), mods: RawInputModifiers.Shift);
                Assert.That(input.Inputs.Select(i => i.Source), Is.EqualTo(new[] { noise }), $"Shift-drag onto the in-port connects; the song has {Graph()}");

                // Shift-drag from the box onto the box.
                Drag(window, view.PointOf(other, window), view.PointOf(input, window), mods: RawInputModifiers.Shift);
                Assert.That(input.Inputs.Select(i => i.Source), Is.EquivalentTo(new[] { noise, other }), $"and onto the box; the song has {Graph()}");

                // The same wire to Neo Poly is refused, in words, and nothing changes.
                Drag(window, view.PointOf(noise, window), view.PointOf(poly, window), mods: RawInputModifiers.Shift);
                Assert.That(poly.Inputs.Count(), Is.EqualTo(0), "no wire into Neo Poly");
                Assert.That(view.Canvas.Refusal, Is.EqualTo(poly.Name + " is an instrument: it has no input"), "and it says why");

                Core.SongCore.ActionStack.Undo();
                Settle();
                Assert.That(input.Inputs.Select(i => i.Source), Is.EqualTo(new[] { noise }), "one undo: one wire");
            }
            finally { Close(window); }
        }

        [AvaloniaTest, Category("Gallery")]
        public void TheGalleryShotsOfAudioIntoAnInstrument()
        {
            // As GalleryTests: GALLERY_THEME=light draws the shots in the Light theme (the Mac job runs the gallery in both).
            var theme = ReBuzz.Studio.Shell.Design.StudioTheme.Kind;
            using var restore = Studio.AfterTest(() => ReBuzz.Studio.Shell.Design.StudioTheme.Kind = theme);
            if (Environment.GetEnvironmentVariable("GALLERY_THEME") is { } t && t.Equals("light", StringComparison.OrdinalIgnoreCase))
                ReBuzz.Studio.Shell.Design.StudioTheme.Kind = ReBuzz.Studio.Shell.Design.AppearanceTheme.Light;
            var (window, view) = OpenEmpty();
            try
            {
                var (noise, input, poly) = Scene();
                MachineEdits.Connect(Core, noise, input);
                Settle();
                StudioSelection.For(Core).SelectMachine(input, window);
                window.MouseMove(view.PointOf(input, window));
                Settle();
                Shot(window, "instrument-input-machines");

                StudioSelection.For(Core).SelectMachine(input, window);
                Settle();
                Shot(window, "instrument-input-inspector");
                Assert.That(poly.Inputs.Count(), Is.EqualTo(0));
            }
            finally { Close(window); }
        }
    }
}
