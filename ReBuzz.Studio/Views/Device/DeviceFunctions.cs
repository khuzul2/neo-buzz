// Fork (docs/specs/ux-overhaul.md, the parameter panel): the old window's edit functions in the panel's Presets menu: Randomize (the
// unlocked parameters), and Morph between two presets with a slider. Each is one step on the song's undo history; the slider keeps updating
// its own step while it moves (ParameterEdits.PresetChange.Update), as the old window's amount did.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Parameters;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Device
{
    using Control = Avalonia.Controls.Control;

    internal static class DeviceFunctions
    {
        public static bool HasTargets(ReBuzzCore core, IMachine machine) => core != null && ParameterFunctions.Targets(core, machine).Count > 0;

        /// <summary>Randomizes (or nudges) the unlocked parameters: one undo step.</summary>
        public static void Run(ReBuzzCore core, IMachine machine, ParameterFunction function, double amount, Action changed)
        {
            if (core == null) return;
            core.SongCore.ActionStack.Do(new ParameterEdits.PresetChange(core, machine, () =>
            {
                ParameterFunctions.Apply(core, machine, function, null, null, amount);
                return null;
            }));
            changed?.Invoke();
        }

        /// <summary>The morph: its two presets and where it stands. Moving it keeps updating one undo step (a new session starts a new step).</summary>
        internal sealed class Morph
        {
            readonly ReBuzzCore core;
            readonly IMachine machine;
            readonly Action changed;

            public Morph(IMachine machine, Action changed)
            {
                this.machine = machine;
                core = DeviceEdits.CoreOf(machine);
                this.changed = changed;
            }

            public string A { get; set; } = DeviceMenus.DefaultPreset;
            public string B { get; set; } = DeviceMenus.DefaultPreset;

            /// <summary>Morphs to <paramref name="amount"/> (0 is A, 1 is B), 0..1.</summary>
            public void MoveTo(double amount)
            {
                if (core == null) return;
                Preset Run()
                {
                    ParameterFunctions.Apply(core, machine, ParameterFunction.Morph, ParameterFunctions.PresetOf(machine, A), ParameterFunctions.PresetOf(machine, B), amount);
                    return null;
                }
                var stack = core.SongCore.ActionStack;
                if (stack is { CanUndo: true, CanRedo: false } && stack.Actions.LastOrDefault() is ParameterEdits.PresetChange last && last.IsUpdatable && ReferenceEquals(last.Owner, this))
                    last.Update(Run);
                else stack.Do(new ParameterEdits.PresetChange(core, machine, Run) { IsUpdatable = true, Owner = this });
                changed?.Invoke();
            }
        }

        /// <summary>The morph flyout: preset A, preset B and a slider between them.</summary>
        public static Flyout OpenMorph(IMachine machine, Visual owner, Action changed)
        {
            var morph = new Morph(machine, changed);
            var names = new[] { DeviceMenus.DefaultPreset }.Concat(machine.GetPresetNames()).ToList();
            ComboBox Box(string label, Action<string> set)
            {
                var box = new ComboBox { ItemsSource = names, SelectedIndex = 0, MinWidth = 160, MinHeight = 0, Height = 26, VerticalContentAlignment = VerticalAlignment.Center };
                Avalonia.Automation.AutomationProperties.SetName(box, label);
                box.SelectionChanged += (s, e) => { if (box.SelectedItem is string n) set(n); };
                return box;
            }
            var a = Box("Preset A", n => morph.A = n);
            var b = Box("Preset B", n => morph.B = n);
            var value = new TextBlock { Text = "0 %", Width = 44, TextAlignment = Avalonia.Media.TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = StudioTokens.FontBody };
            var slider = new Slider { Minimum = 0, Maximum = 100, Value = 0, MinWidth = 200 };
            Avalonia.Automation.AutomationProperties.SetName(slider, "Morph amount");
            slider.PropertyChanged += (s, e) =>
            {
                if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
                value.Text = $"{(int)Math.Round(slider.Value)} %";
                morph.MoveTo(slider.Value / 100);
            };
            var panel = new StackPanel
            {
                Spacing = 6, Margin = new Thickness(4), MinWidth = 260,
                Children =
                {
                    new TextBlock { Text = "Morph between A and B", FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = StudioTokens.FontBody },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "A", Width = 14, VerticalAlignment = VerticalAlignment.Center }, a } },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "B", Width = 14, VerticalAlignment = VerticalAlignment.Center }, b } },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { slider, value } },
                    new TextBlock { Text = "Locked parameters stay. One undo step.", FontSize = StudioTokens.FontCaption, Foreground = StudioTokens.TextSecondaryBrush },
                },
            };
            var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedRight };
            if (owner is Control c) flyout.ShowAt(c);
            Last = (flyout, morph, a, b, slider);
            return flyout;
        }

        /// <summary>The flyout opened last (tests drive its parts).</summary>
        internal static (Flyout Flyout, Morph Morph, ComboBox A, ComboBox B, Slider Slider) Last { get; private set; }
    }
}
