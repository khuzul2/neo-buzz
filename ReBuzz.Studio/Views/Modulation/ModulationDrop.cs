// Fork (docs/specs/ux-overhaul.md P5b, drag-to-modulate): where a drag from a modulator's output handle lands. The handle (kit:
// ModSourceHandle, or the canvas's and device view's) reports the screen point it was let go at; this finds the window under it and,
// in that window, the control that takes a modulation (IModulationDropSurface: the kit's knobs, the device view's rows, the parameter
// window's sliders), and adds the target as one undo step (ModulationEdits: the agent's model and limits).

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Views.Modulation
{
    using Control = Avalonia.Controls.Control;

    internal static class ModulationDrop
    {
        /// <summary>Hooks the drop and the matrix's keyboard route to the studio (once per process; the windows are the app's).</summary>
        public static void Install()
        {
            ModulationBoard.DropHook = DropAt;
            ModulationBoard.MatrixHook = source => ModulationMatrixWindow.Open(source);
        }

        /// <summary>The control that takes a modulation at a screen point (pixels), over every window of the app, the active one first.</summary>
        public static (IMachine Machine, IParameter Parameter, int Track)? TargetAt(double screenX, double screenY)
        {
            var point = new PixelPoint((int)Math.Round(screenX), (int)Math.Round(screenY));
            foreach (var window in App.Windows.Where(w => w.IsVisible).OrderByDescending(w => w.IsActive))
            {
                Point local;
                try { local = window.PointToClient(point); }
                catch (Exception) { continue; }
                if (!new Rect(window.Bounds.Size).Contains(local)) continue;
                var hit = window.InputHitTest(local) as Visual;
                for (var v = hit; v != null; v = v.GetVisualParent())
                {
                    if (v is not IModulationDropSurface surface) continue;
                    var at = window.TranslatePoint(local, v);
                    if (at is { } p && surface.ModulationTargetAt(p.X, p.Y, out var machine, out var parameter, out int track))
                        return (machine, parameter, track);
                }
            }
            return null;
        }

        /// <summary>Adds the modulation for a drop at a screen point. Null: done (or nothing there); else why not, for the status line.</summary>
        public static string DropAt(IModulator source, double screenX, double screenY)
        {
            if (TargetAt(screenX, screenY) is not { } target) return null;
            return Drop(source, target.Machine, target.Parameter, target.Track);
        }

        /// <summary>Adds the modulation of <paramref name="source"/> on a parameter: one undo step. Null: done, else why not.</summary>
        public static string Drop(IModulator source, IMachine machine, IParameter parameter, int track)
        {
            var core = machine?.Graph?.Buzz as ReBuzzCore;
            string refusal = ModulationEdits.Add(core, source, machine, parameter, track);
            var main = MainWindow.OpenWindows.LastOrDefault();
            if (main != null)
                main.Notice(refusal != null ? $"{source.Machine.Name} → {machine.Name} {parameter.Name}: {refusal}"
                                            : $"{source.Machine.Name} now modulates {machine.Name} {parameter.Name} (depth {ModulationEdits.DefaultDepth} %). Undo takes it back.");
            return refusal;
        }
    }
}
