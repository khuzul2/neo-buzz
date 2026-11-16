// Fork (docs/specs/ux-overhaul.md, the parameter panel): what tests need to reach a parameter in a machine's floating Device window.

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Views.Device;

namespace ReBuzz.Studio.Tests
{
    internal static class DeviceTestKit
    {
        /// <summary>The Device page inside a machine's floating window.</summary>
        public static DevicePage PageOf(Window window) => (DevicePage)StudioMachineUiHost.ContentOf(window)!;

        public static DeviceParam Param(DeviceView view, string name) =>
            view.Layout.All.FirstOrDefault(p => p.FullName == name) ?? view.Layout.All.First(p => p.Name == name);

        /// <summary>A parameter's cell in Knobs mode, scrolled into view and laid out.</summary>
        public static DeviceCell Cell(Window window, DeviceView view, DeviceParam p)
        {
            view.SetMode(DeviceView.DeviceMode.Knobs, remember: false);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var cell = view.Cards.Cell(p) ?? throw new InvalidOperationException(p.FullName + " is not a card");
            cell.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            return cell;
        }

        /// <summary>The knob (or the choice) of a parameter's cell.</summary>
        public static DeviceKnob Knob(Window window, DeviceView view, string name) => (DeviceKnob)Cell(window, view, Param(view, name)).Face;

        public static Point Centre(Window window, Avalonia.Controls.Control c) =>
            c.TranslatePoint(new Point(c.Bounds.Width / 2, Math.Min(c.Bounds.Height / 2, DeviceKnob.Diameter / 2)), window)!.Value;
    }
}
