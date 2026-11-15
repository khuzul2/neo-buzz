// Fork (docs/specs/ux-overhaul.md P6b, §9): "Latency..." on a machine's menu, after BuzzGUI.MachineView's LatencyWindow: the latency
// the machine reports (machines of version 27 and later), and a switch to override it with a number of samples for the delay
// compensation (mastering-tools.md §4.1). OK writes machine.OverrideLatency (-1 = the machine's own); a bad number can't be accepted.

using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using BuzzGUI.Interfaces;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell.Tools
{
    internal sealed class LatencyDialog : Window
    {
        readonly IMachine machine;
        internal readonly CheckBox Override = new CheckBox { Content = "Override the machine's latency" };
        internal readonly TextBox Samples = new TextBox { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
        internal readonly TextBlock Status = new TextBlock { Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 380 };
        internal readonly TextBlock Problem = new TextBlock { Foreground = StudioTokens.ErrorTextBrush };
        internal readonly Button Ok = StudioDialog.Primary();
        internal readonly Button CancelButton = StudioDialog.Secondary();
        internal readonly Button ClearAll = new Button { Content = "Clear all overrides", HorizontalAlignment = HorizontalAlignment.Left };

        public LatencyDialog(IMachine machine)
        {
            this.machine = machine;
            Title = "Latency - " + machine.Name;
            SizeToContent = SizeToContent.WidthAndHeight;
            CanResize = false;
            ShowInTaskbar = false;
            Background = StudioTokens.SurfaceBaseBrush;
            Override.IsChecked = machine.OverrideLatency >= 0;
            Samples.Text = (machine.OverrideLatency >= 0 ? machine.OverrideLatency : 0).ToString(CultureInfo.InvariantCulture);
            Status.Text = machine.DLL.Info.Version >= 27
                ? $"Latency as reported by the machine: {machine.Latency} samples"
                : "The machine does not support latency reporting (too old for that).";
            AutomationProperties.SetName(Override, "Override the machine's latency");
            AutomationProperties.SetName(Samples, "Latency in samples");
            ToolTip.SetTip(Samples, "Samples of delay the engine compensates for this machine");
            ToolTip.SetTip(ClearAll, "Take the override off every machine of the song");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Samples, new TextBlock { Text = "samples", Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center } } };
            Content = StudioDialog.Body(Status, Override, row, Problem, ClearAll, StudioDialog.Buttons(Ok, CancelButton));
            Samples.PropertyChanged += (s, e) => { if (e.Property == TextBox.TextProperty) Validate(); };
            Override.IsCheckedChanged += (s, e) => Validate();
            Ok.Click += (s, e) => { if (Apply()) Close(true); };
            CancelButton.Click += (s, e) => Close(false);
            ClearAll.Click += (s, e) =>
            {
                foreach (var m in machine.Graph.Machines) m.OverrideLatency = -1;
                Override.IsChecked = false;
            };
            Validate();
        }

        /// <summary>The number typed, or null when it isn't a whole number from zero.</summary>
        internal int? Value => int.TryParse(Samples.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 0 ? v : null;

        internal bool Invalid => Override.IsChecked == true && Value == null;

        void Validate()
        {
            Problem.Text = Invalid ? "Enter a whole number of samples, zero or more." : "";
            Ok.IsEnabled = !Invalid;
        }

        /// <summary>Writes the choice to the machine (the OK button).</summary>
        internal bool Apply()
        {
            Validate();
            if (Invalid) return false;
            machine.OverrideLatency = Override.IsChecked == true ? Value.Value : -1;
            return true;
        }

        /// <summary>Asks about <paramref name="machine"/>; completes when the dialog closes.</summary>
        public static Task Ask(IMachine machine) => StudioDialog.Run(new LatencyDialog(machine));
    }
}
