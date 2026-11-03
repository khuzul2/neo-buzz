// Fork (docs/specs/studio-mvp.md S7): the studio's menus as one model, shown two ways: as the
// macOS menu bar (Avalonia's NativeMenu: the app menu with About and Preferences…, then File, Edit,
// Transport, View, Window and Help), and elsewhere as the window's own menu.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;

namespace ReBuzz.Studio.Shell
{
    /// <summary>A menu command, a submenu or a separator.</summary>
    internal sealed class MenuEntry
    {
        /// <summary>Its text, with an access key ("_File"); the menu bar drops the underscore.</summary>
        public string Header { get; init; }

        public Action Run { get; init; }

        public Func<bool> CanRun { get; init; }

        /// <summary>A check mark, read when the menu opens.</summary>
        public Func<bool> IsChecked { get; init; }

        /// <summary>Its shortcut, shown beside it.</summary>
        public KeyGesture Gesture { get; init; }

        /// <summary>
        /// Whether the macOS menu bar takes <see cref="Gesture"/> as its key equivalent. Off for
        /// keys a view also uses (F2 renames in the Session View): the window's handler has them.
        /// </summary>
        public bool NativeKey { get; init; } = true;

        /// <summary>A submenu, built each time it opens (recent files change).</summary>
        public Func<IEnumerable<MenuEntry>> Items { get; init; }

        public bool IsSeparator { get; init; }

        public static MenuEntry Separator => new MenuEntry { IsSeparator = true };

        /// <summary>The header without its access key ("__" is a plain underscore).</summary>
        public string Text => Header?.Replace("__", "\0").Replace("_", "").Replace("\0", "_");
    }

    internal static class AppMenu
    {
        /// <summary>The window's own menu (Windows, Linux).</summary>
        public static Menu ToMenu(IEnumerable<MenuEntry> top)
        {
            var menu = new Menu();
            foreach (var entry in top) menu.Items.Add(ToItem(entry));
            return menu;
        }

        static object ToItem(MenuEntry entry)
        {
            if (entry.IsSeparator) return new Separator();
            var item = new MenuItem { Header = entry.Header, InputGesture = entry.Gesture, Tag = entry.Text };
            if (entry.Items != null)
            {
                Fill(item, entry);
                // Built again as it opens (and the commands' state with it).
                item.SubmenuOpened += (s, e) =>
                {
                    if (e.Source == item) Fill(item, entry);
                };
            }
            else
            {
                item.Command = new Command(entry.Run, entry.CanRun);
                if (entry.IsChecked != null)
                {
                    item.ToggleType = MenuItemToggleType.CheckBox;
                    item.IsChecked = entry.IsChecked();
                }
            }
            return item;
        }

        static void Fill(MenuItem item, MenuEntry entry)
        {
            item.Items.Clear();
            foreach (var child in entry.Items()) item.Items.Add(ToItem(child));
        }

        /// <summary>A menu for the macOS menu bar (the app's menu, or a window's).</summary>
        public static NativeMenu ToNativeMenu(IEnumerable<MenuEntry> entries)
        {
            var menu = new NativeMenu();
            foreach (var entry in entries) menu.Items.Add(ToNativeItem(entry));
            return menu;
        }

        static NativeMenuItemBase ToNativeItem(MenuEntry entry)
        {
            if (entry.IsSeparator) return new NativeMenuItemSeparator();
            var item = new NativeMenuItem { Header = entry.Text, Gesture = entry.NativeKey ? entry.Gesture : null };
            if (entry.Items != null)
            {
                var sub = new NativeMenu();
                FillNative(sub, entry);
                // macOS asks before it shows the menu: recent files and enabled states then.
                sub.NeedsUpdate += (s, e) => FillNative(sub, entry);
                item.Menu = sub;
            }
            else
            {
                item.Command = new Command(entry.Run, entry.CanRun);
                if (entry.IsChecked != null)
                {
                    item.ToggleType = MenuItemToggleType.CheckBox;
                    item.IsChecked = entry.IsChecked();
                }
            }
            return item;
        }

        static void FillNative(NativeMenu menu, MenuEntry entry)
        {
            menu.Items.Clear();
            foreach (var child in entry.Items()) menu.Items.Add(ToNativeItem(child));
        }

        /// <summary>The entry at a path of texts ("File/Open Recent"), for tests.</summary>
        public static MenuEntry Find(IEnumerable<MenuEntry> top, string path)
        {
            MenuEntry found = null;
            IEnumerable<MenuEntry> items = top;
            foreach (var name in path.Split('/'))
            {
                found = items?.FirstOrDefault(e => !e.IsSeparator && e.Text == name);
                if (found == null) return null;
                items = found.Items?.Invoke();
            }
            return found;
        }
    }
}
