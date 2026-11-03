// Fork (docs/specs/studio-mvp.md S1): the file pickers editors ask for (Neo FM6's banks, Neo
// Wave's tables), on WPF. The studio's Avalonia kit has the same calls
// (NeoBuzz.Editor.Avalonia/EditorFiles.cs), so an editor's source serves both.

using System;
using System.Windows;

namespace NeoBuzz.Editor
{
    internal static class EditorFiles
    {
        /// <summary>Asks for a file to open; <paramref name="open"/> gets its path, unless the user cancels.</summary>
        /// <param name="filter">As WPF's dialogs take it: "Name (*.ext)|*.ext|…".</param>
        public static void Open(FrameworkElement owner, string title, string filter, Action<string> open)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter };
            if (dialog.ShowDialog() != true) return;
            open(dialog.FileName);
        }

        /// <summary>Asks where to save; <paramref name="save"/> gets the path, unless the user cancels.</summary>
        public static void Save(FrameworkElement owner, string title, string filter, string fileName, Action<string> save)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = fileName };
            if (dialog.ShowDialog() != true) return;
            save(dialog.FileName);
        }
    }
}
