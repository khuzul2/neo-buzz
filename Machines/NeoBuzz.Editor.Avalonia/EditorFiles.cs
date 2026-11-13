// Fork (docs/specs/studio-mvp.md S1): what editors ask the user on Avalonia: files to open or
// save (Neo FM6's banks, Neo Wave's tables) through the window's storage provider, with the WPF
// kit's calls (NeoBuzz.Editor/EditorFiles.cs); and messages, as WPF's MessageBox.Show is called.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace NeoBuzz.Editor
{
    internal static class EditorFiles
    {
        /// <summary>Asks for a file to open; <paramref name="open"/> gets its path, unless the user cancels.</summary>
        /// <param name="filter">As WPF's dialogs take it: "Name (*.ext)|*.ext|…".</param>
        public static async void Open(Control owner, string title, string filter, Action<string> open)
        {
            try
            {
                var storage = TopLevel.GetTopLevel(owner)?.StorageProvider;
                if (storage == null) return;
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    FileTypeFilter = Types(filter),
                });
                string path = files?.FirstOrDefault()?.TryGetLocalPath();
                if (path != null) open(path);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, title);
            }
        }

        /// <summary>Asks where to save; <paramref name="save"/> gets the path, unless the user cancels.</summary>
        public static async void Save(Control owner, string title, string filter, string fileName, Action<string> save)
        {
            try
            {
                var storage = TopLevel.GetTopLevel(owner)?.StorageProvider;
                if (storage == null) return;
                var types = Types(filter);
                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = title,
                    SuggestedFileName = fileName,
                    FileTypeChoices = types,
                    DefaultExtension = types.FirstOrDefault()?.Patterns?.FirstOrDefault()?.TrimStart('*', '.'),
                });
                string path = file?.TryGetLocalPath();
                if (path != null) save(path);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, title);
            }
        }

        /// <summary>A WPF filter ("DX7 SysEx (*.syx)|*.syx|All files (*.*)|*.*") as Avalonia's file types.</summary>
        internal static List<FilePickerFileType> Types(string filter)
        {
            var types = new List<FilePickerFileType>();
            var parts = (filter ?? "").Split('|');
            for (int i = 0; i + 1 < parts.Length; i += 2)
                types.Add(new FilePickerFileType(parts[i]) { Patterns = parts[i + 1].Split(';').Select(p => p.Trim()).ToArray() });
            return types;
        }
    }

    internal enum MessageBoxButton { OK, OKCancel, YesNoCancel, YesNo }

    internal enum MessageBoxImage { None, Error, Question, Warning, Information }

    /// <summary>WPF's MessageBox.Show as editors call it: a small window over the active one, with OK.</summary>
    internal static class MessageBox
    {
        /// <summary>Shows the message and returns at once (WPF's waits; no caller here needs that).</summary>
        public static void Show(string text, string caption = "", MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None) =>
            Dispatcher.UIThread.Post(() =>
            {
                // The studio's dialog (ReBuzz.Studio's StudioDialog; the kit doesn't reference it): the same classes, margin and rhythm.
                var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
                ok.Classes.Add("dialog");
                ok.Classes.Add("accent");
                var message = new TextBlock { Text = text ?? "", Foreground = Theme.Text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 420 };
                var window = new Window
                {
                    Title = string.IsNullOrEmpty(caption) ? "NeoBuzz Live" : caption,     // the app's name (ReBuzz.Product.Name; machines don't reference it)
                    SizeToContent = SizeToContent.WidthAndHeight,
                    CanResize = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Background = Theme.Panel,
                    ShowInTaskbar = false,
                    Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = { message, ok } },
                };
                ok.Click += (s, e) => window.Close();
                var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.FirstOrDefault(w => w.IsActive);
                if (owner != null) _ = window.ShowDialog(owner);
                else window.Show();
            });
    }
}
