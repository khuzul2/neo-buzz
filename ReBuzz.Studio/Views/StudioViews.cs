// Fork (docs/specs/studio-mvp.md §4): the main window's views, one tab each. Each step S1–S6
// adds its view here (one line), so the steps don't collide in the shell.

using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Views
{
    /// <summary>A view in the main window: its tab's title and its content, made once the engine runs.</summary>
    internal interface IStudioView
    {
        string Title { get; }

        Avalonia.Controls.Control Create(StudioHost host);
    }

    /// <summary>
    /// A view with edit commands of its own (the Session View's grid history, a pattern editor's
    /// selection…). The rule (studio-mvp.md §3.10): the shell's Edit menu and its shortcuts go to the
    /// selected view first; a view returns false to let the song's undo history (or nothing) have
    /// the command. Avalonia's window key bindings see Ctrl/Command shortcuts before views do, so
    /// views needn't bind these keys themselves.
    /// </summary>
    internal interface IStudioEditTarget
    {
        bool Undo();
        bool Redo();
        bool Cut() => false;
        bool Copy() => false;
        bool Paste() => false;
        bool Delete() => false;
    }

    internal static class StudioViews
    {
        /// <summary>Whether a view with this title is built (not a placeholder still).</summary>
        public static bool Has(string title) => All.Any(v => v.Title == title && v is not Placeholder);

        public static IReadOnlyList<IStudioView> All { get; } = new IStudioView[]
        {
            new Machines.MachinesTab(),
            new Placeholder("Sequence", "The sequence editor comes in step S4."),
            new Session.SessionStudioView(),
            new PatternView(),
        };

        /// <summary>A tab whose view isn't built yet.</summary>
        sealed class Placeholder : IStudioView
        {
            readonly string note;

            public Placeholder(string title, string note)
            {
                Title = title;
                this.note = note;
            }

            public string Title { get; }

            public Avalonia.Controls.Control Create(StudioHost host) => new TextBlock
            {
                Text = note,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
    }
}
