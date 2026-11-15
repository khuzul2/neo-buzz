// Fork (docs/specs/ux-overhaul.md P6b, §9): Edit ▸ Song Properties..., the song's timing in one place: tempo (BPM), ticks per beat (TPB)
// and the playback speed that ReBuzz.exe's toolbar has. The top bar's tempo field stays the quick way to the tempo. BPM and TPB are
// one undo step (AgentActions.SetTransport, as the tools do it); the ranges are the engine's (BPM 16-500, TPB 1-32); speed is the engine's
// playback speed setting (0 = normal; the dialog keeps it within plus or minus 1000).

using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using ReBuzz.Agent;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell.Tools
{
    internal sealed class SongPropertiesDialog : Window
    {
        public const int MinBpm = 16, MaxBpm = 500, MinTpb = 1, MaxTpb = 32, MaxSpeed = 1000;

        readonly ReBuzzCore core;
        internal readonly TextBox Bpm = Field("Tempo in beats per minute");
        internal readonly TextBox Tpb = Field("Ticks per beat");
        internal readonly TextBox Speed = Field("Playback speed");
        internal readonly TextBlock Problem = new TextBlock { Foreground = StudioTokens.ErrorTextBrush, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 360 };
        internal readonly Button Ok = StudioDialog.Primary();
        internal readonly Button CancelButton = StudioDialog.Secondary();

        static TextBox Field(string name)
        {
            var t = new TextBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(t, name);
            return t;
        }

        public SongPropertiesDialog(ReBuzzCore core)
        {
            this.core = core;
            Title = "Song Properties";
            SizeToContent = SizeToContent.WidthAndHeight;
            CanResize = false;
            ShowInTaskbar = false;
            Background = StudioTokens.SurfaceBaseBrush;
            Bpm.Text = core.BPM.ToString(CultureInfo.InvariantCulture);
            Tpb.Text = core.TPB.ToString(CultureInfo.InvariantCulture);
            Speed.Text = core.Speed.ToString(CultureInfo.InvariantCulture);
            var grid = new Grid { RowSpacing = 8, ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            AddRow(grid, 0, "Tempo", Bpm, $"BPM, {MinBpm} to {MaxBpm}");
            AddRow(grid, 1, "Ticks per beat", Tpb, $"{MinTpb} to {MaxTpb}; a bar of 4/4 is four times this");
            AddRow(grid, 2, "Playback speed", Speed, "0 is normal speed");
            Content = StudioDialog.Body(grid, Problem, StudioDialog.Buttons(Ok, CancelButton));
            foreach (var t in new[] { Bpm, Tpb, Speed }) t.PropertyChanged += (s, e) => { if (e.Property == TextBox.TextProperty) Validate(); };
            Ok.Click += (s, e) => { if (Apply()) Close(true); };
            CancelButton.Click += (s, e) => Close(false);
            Validate();
        }

        static void AddRow(Grid grid, int row, string label, TextBox box, string hint)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = new TextBlock { Text = label, Foreground = StudioTokens.TextPrimaryBrush, VerticalAlignment = VerticalAlignment.Center };
            var h = new TextBlock { Text = hint, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center, FontSize = StudioTokens.FontBody - 1 };
            Grid.SetRow(l, row); Grid.SetRow(box, row); Grid.SetRow(h, row);
            Grid.SetColumn(box, 1); Grid.SetColumn(h, 2);
            grid.Children.Add(l); grid.Children.Add(box); grid.Children.Add(h);
        }

        static int? Parse(TextBox t, int min, int max) =>
            int.TryParse(t.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= min && v <= max ? v : null;

        /// <summary>What is wrong with the numbers typed, or null.</summary>
        internal string Why()
        {
            if (Parse(Bpm, MinBpm, MaxBpm) == null) return $"Tempo is a whole number from {MinBpm} to {MaxBpm}.";
            if (Parse(Tpb, MinTpb, MaxTpb) == null) return $"Ticks per beat is a whole number from {MinTpb} to {MaxTpb}.";
            if (Parse(Speed, -MaxSpeed, MaxSpeed) == null) return $"Speed is a whole number from -{MaxSpeed} to {MaxSpeed}.";
            return null;
        }

        void Validate()
        {
            Problem.Text = Why() ?? "";
            Ok.IsEnabled = Why() == null;
        }

        /// <summary>Writes the numbers to the song (the OK button): tempo and ticks per beat as one undo step.</summary>
        internal bool Apply()
        {
            if (Why() != null) return false;
            int bpm = Parse(Bpm, MinBpm, MaxBpm).Value, tpb = Parse(Tpb, MinTpb, MaxTpb).Value, speed = Parse(Speed, -MaxSpeed, MaxSpeed).Value;
            if (bpm != core.BPM || tpb != core.TPB) AgentActions.SetTransport(core, bpm, tpb, core.Looping);
            if (speed != core.Speed) core.Speed = speed;
            return true;
        }

        public static Task Ask(ReBuzzCore core) => StudioDialog.Run(new SongPropertiesDialog(core));
    }
}
