// Fork (docs/specs/ui-gallery.md, the real Mac menu bar): what the macOS app menu's own items do that Avalonia's API doesn't
// offer (its commands for them are internal): Hide, Hide Others and Show All through NSApplication, as Avalonia's own items
// call them; and the event that chose a menu item, so a key equivalent (F2) can do what the key does in the window (rename
// a clip in the Session View) while a click on the same item chooses the view.

using System;
using System.Runtime.InteropServices;
using Avalonia.Input;

namespace ReBuzz.Studio.Shell
{
    internal static class MacApp
    {
        const string ObjC = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjC)]
        static extern IntPtr objc_getClass(string name);

        [DllImport(ObjC)]
        static extern IntPtr sel_registerName(string name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern ulong SendNUInteger(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern ushort SendUShort(IntPtr receiver, IntPtr selector);

        static IntPtr Application => Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));

        static void Tell(string selector)
        {
            if (!OperatingSystem.IsMacOS()) return;
            try
            {
                Send(Application, sel_registerName(selector), IntPtr.Zero);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Hide NeoBuzz Live (⌘H).</summary>
        public static void Hide() => Tell("hide:");

        /// <summary>Hide Others (⌥⌘H).</summary>
        public static void HideOthers() => Tell("hideOtherApplications:");

        /// <summary>Show All.</summary>
        public static void ShowAll() => Tell("unhideAllApplications:");

        /// <summary>Tests: what the event that chose a menu item was, instead of asking AppKit.</summary>
        internal static Func<Key?> KeyDownSource { get; set; }

        /// <summary>
        /// The key whose press chose the menu item being run (a key equivalent), or null when a click or anything else
        /// chose it. AppKit's current event: a key down for a key equivalent, the mouse's for a click.
        /// </summary>
        public static Key? KeyThatChoseTheMenuItem()
        {
            if (KeyDownSource != null) return KeyDownSource();
            if (!OperatingSystem.IsMacOS()) return null;
            try
            {
                var e = Send(Application, sel_registerName("currentEvent"));
                if (e == IntPtr.Zero) return null;
                const ulong KeyDown = 10;           // NSEventTypeKeyDown
                if (SendNUInteger(e, sel_registerName("type")) != KeyDown) return null;
                return SendUShort(e, sel_registerName("keyCode")) switch
                {
                    120 => Key.F2,
                    99 => Key.F3,
                    118 => Key.F4,
                    109 => Key.F10,
                    48 => Key.Tab,
                    _ => null,
                };
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
