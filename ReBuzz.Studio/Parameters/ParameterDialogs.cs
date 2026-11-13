// Fork (docs/specs/studio-mvp.md S2): the parameter window's small dialogs, ported from
// BuzzGUI.Common (WPF):
//  - the value editor (ParameterValueEditor): a digit typed on a slider, or an attribute's
//    "Other…", opens it with "[min..max]"; Return or a click takes the value, Escape doesn't;
//  - MIDI bind (MidiControllerAssignWindow, quick assign): moving a controller fills in its
//    channel and number (MIDI learn), or they are typed.
// Tests answer through the responders instead of a window.

using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Common;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Parameters
{
    using Control = Avalonia.Controls.Control;

    internal static class ParameterDialogs
    {
        /// <summary>Tests: (start, min, max) → the value, or null for cancel.</summary>
        public static Func<int, int, int, int?> ValueResponder { get; set; }

        /// <summary>Tests: () → (channel 1–16, controller), or null for cancel.</summary>
        public static Func<(int Channel, int Controller)?> BindResponder { get; set; }

        /// <summary>A value typed in [min..max]; <paramref name="replace"/> selects the start text (else the caret goes after it).</summary>
        public static async Task<int?> AskValue(Visual near, Point at, int start, int min, int max, bool replace)
        {
            if (ValueResponder != null) return ValueResponder(start, min, max);
            var box = new TextBox { Text = start.ToString(), MinWidth = 80 };
            var range = new TextBlock { Text = $"[{min}..{max}]", FontSize = 11, Foreground = ParameterTheme.DimText, VerticalAlignment = VerticalAlignment.Center };
            int? result = null;
            var window = new Window
            {
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                ShowInTaskbar = false,
                Title = "Value",
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6), Children = { box, range } },
            };
            bool Valid(out int v) => int.TryParse(box.Text, out v) && v >= min && v <= max;
            box.TextChanged += (s, e) => box.Foreground = Valid(out _) ? ReBuzz.Studio.Shell.Design.StudioTokens.TextPrimaryBrush : ReBuzz.Studio.Shell.Design.StudioTokens.ErrorTextBrush;
            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { window.Close(); e.Handled = true; }
                else if (e.Key == Key.Return && Valid(out int v)) { result = v; window.Close(); e.Handled = true; }
            };
            window.PointerPressed += (s, e) =>
            {
                if (Valid(out int v)) result = v;
                window.Close();
            };
            window.Opened += (s, e) =>
            {
                box.Focus();
                if (replace) box.SelectAll();
                else box.CaretIndex = box.Text.Length;
            };
            if (TopLevel.GetTopLevel(near) is Window owner)
            {
                var screen = near.PointToScreen(at);
                window.Position = screen;
                await window.ShowDialog(owner);
            }
            return result;
        }

        /// <summary>A MIDI controller to bind to: (channel 1–16, controller 0–127), learned or typed.</summary>
        public static async Task<(int Channel, int Controller)?> AskController()
        {
            if (BindResponder != null) return BindResponder();
            var channel = new TextBox { Width = 50, HorizontalAlignment = HorizontalAlignment.Left };
            var controller = new TextBox { Width = 50, HorizontalAlignment = HorizontalAlignment.Left };
            var ok = ReBuzz.Studio.Shell.Design.StudioDialog.Primary();
            var cancel = ReBuzz.Studio.Shell.Design.StudioDialog.Secondary();
            (int, int)? result = null;
            bool Valid(out int ch, out int cc) =>
                int.TryParse(channel.Text, out ch) & int.TryParse(controller.Text, out cc) && ch >= 1 && ch <= 16 && cc >= 0 && cc <= 127;
            void Check() => ok.IsEnabled = Valid(out _, out _);
            channel.TextChanged += (s, e) => Check();
            controller.TextChanged += (s, e) => Check();
            Check();
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("100,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), Margin = new Thickness(ReBuzz.Studio.Shell.Design.StudioDialog.Margin) };
            void Add(Control c, int row, int col, int span = 1)
            {
                Grid.SetRow(c, row);
                Grid.SetColumn(c, col);
                Grid.SetColumnSpan(c, span);
                c.Margin = new Thickness(4);
                grid.Children.Add(c);
            }
            Add(new TextBlock { Text = "Move MIDI controller to select it" }, 0, 0, 2);
            Add(new TextBlock { Text = "Channel", VerticalAlignment = VerticalAlignment.Center }, 1, 0);
            Add(channel, 1, 1);
            Add(new TextBlock { Text = "Controller", VerticalAlignment = VerticalAlignment.Center }, 2, 0);
            Add(controller, 2, 1);
            Add(ReBuzz.Studio.Shell.Design.StudioDialog.Buttons(ok, cancel), 3, 0, 2);
            var window = ReBuzz.Studio.Shell.Design.StudioDialog.Create("Midi Controller");
            window.Content = grid;
            ok.Click += (s, e) =>
            {
                if (Valid(out int ch, out int cc)) result = (ch, cc);
                window.Close();
            };
            cancel.Click += (s, e) => window.Close();

            // MIDI learn: the engine's MIDI input (any thread) fills in the last controller moved.
            var buzz = StudioHost.Current?.Core;
            void Learn(int data)
            {
                int status = MIDI.DecodeStatus(data);
                if ((status & 0xF0) != MIDI.ControlChange) return;
                int ch = (status & 0x0F) + 1, cc = MIDI.DecodeData1(data);
                Dispatcher.UIThread.Post(() =>
                {
                    channel.Text = ch.ToString();
                    controller.Text = cc.ToString();
                });
            }
            if (buzz != null) buzz.MIDIInput += Learn;
            try
            {
                await ReBuzz.Studio.Shell.Design.StudioDialog.Run(window);
            }
            finally
            {
                if (buzz != null) buzz.MIDIInput -= Learn;
            }
            return result;
        }
    }
}
