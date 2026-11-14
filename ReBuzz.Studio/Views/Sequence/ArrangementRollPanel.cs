// Fork (docs/specs/daw-modes.md AR2): the piano roll under the arrangement. A clip opened in the Arrangement mode (a double click, P)
// shows its pattern's notes here, in the store the machine plays (SongNotes: the engine's MIDI column, or the Note columns of a
// machine that takes notes in its tracks), so what is drawn plays in the clip's session slot as well. Edits are steps of the song's
// history. It is the detail panel's piano roll control (TrackerRoll) in its song-only form: velocity and controller lanes, key and
// scale, fold, step input and the ghosts of the other clips.

using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Detail;

namespace ReBuzz.Studio.Views.Sequence
{
    using Control = Avalonia.Controls.Control;

    internal sealed class ArrangementRollPanel : DockPanel
    {
        public const double DefaultHeight = 300;

        readonly TrackerRoll roll;
        readonly TextBlock title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), FontSize = StudioTokens.FontBody, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        readonly Button close, editor;
        IPattern pattern;

        public ArrangementRollPanel(ReBuzzCore core)
        {
            roll = new TrackerRoll(core, songOnly: true);
            Height = DefaultHeight;
            Background = StudioTokens.SurfaceViewBrush;
            AutomationProperties.SetName(this, "Piano roll panel");
            title.Foreground = StudioTokens.TextPrimaryBrush;
            AutomationProperties.SetName(title, "Piano roll title");

            close = StudioButtons.KeepFocus(StudioButtons.Icon(new Button { Content = "✕", VerticalAlignment = VerticalAlignment.Center }, "Close piano roll", "Close the piano roll"));
            close.Click += (s, e) => Close();
            editor = StudioButtons.KeepFocus(new Button { Content = "Pattern tab", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            ToolTip.SetTip(editor, "Edit this pattern in the Pattern tab (Return)");
            AutomationProperties.SetName(editor, "Edit in the Pattern tab");
            editor.Click += (s, e) => { if (pattern != null) PatternEditorRequested?.Invoke(pattern); };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { editor, close } };
            var bar = new DockPanel { Height = 30, Background = StudioTokens.SurfacePanelBrush, LastChildFill = true };
            SetDock(buttons, Dock.Right);
            bar.Children.Add(buttons);
            bar.Children.Add(title);
            SetDock(bar, Dock.Top);
            var border = new Border { Height = 1, Background = StudioTokens.SurfaceBorderBrush };
            SetDock(border, Dock.Top);
            Children.Add(border);
            Children.Add(bar);
            Children.Add(roll);
        }

        /// <summary>The roll under the header (tests, hosts).</summary>
        public TrackerRoll Roll => roll;

        /// <summary>The pattern the roll shows, or null when closed.</summary>
        public IPattern Pattern => IsVisible ? pattern : null;

        public Button CloseButton => close;

        public Button EditorButton => editor;

        public string TitleText => title.Text;

        /// <summary>The pattern editor was asked for (the Pattern tab button).</summary>
        public event Action<IPattern> PatternEditorRequested;

        /// <summary>Shows a pattern's notes.</summary>
        public void Open(IPattern p)
        {
            if (p == null) return;
            pattern = p;
            IsVisible = true;
            title.Text = $"Piano roll · {p.Machine?.Name} · {p.Name}";
            roll.Show(p);
            roll.FocusRoll();
        }

        /// <summary>Hides the roll; it lets go of the song's pattern.</summary>
        public void Close()
        {
            pattern = null;
            IsVisible = false;
            roll.Show(null);
        }
    }
}
