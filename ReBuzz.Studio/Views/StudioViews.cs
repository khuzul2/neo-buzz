// Fork (docs/specs/studio-mvp.md §4): the main window's views, one tab each. Each step S1–S6
// adds its view here (one line), so the steps don't collide in the shell.

using System.Collections.Generic;
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

    internal static class StudioViews
    {
        public static IReadOnlyList<IStudioView> All { get; } = new IStudioView[]
        {
            new Placeholder("Machines", "The machine view comes in step S3."),
            new Placeholder("Sequence", "The sequence editor comes in step S4."),
            new Placeholder("Session", "The Session View and mixer come in step S5."),
            new Placeholder("Pattern", "The pattern editor comes in step S6."),
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
