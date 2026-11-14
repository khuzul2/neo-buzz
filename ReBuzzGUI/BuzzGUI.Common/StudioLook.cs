// Fork (docs/specs/ux-overhaul.md P4b): the studio's light or dark look, for code the studio does not compile itself: the pattern
// editor is a machine assembly loaded from Gear that draws its own colours, and it learns of the theme here (this assembly is the
// one both share). The classic WPF app never sets it, and keeps its look (§1).

using System;

namespace BuzzGUI.Common
{
    public static class StudioLook
    {
        static bool light;

        /// <summary>Whether the studio draws in its light theme.</summary>
        public static bool Light
        {
            get => light;
            set
            {
                if (value == light) return;
                light = value;
                Changed?.Invoke();
            }
        }

        /// <summary>After <see cref="Light"/> changed: what draws its own colours takes the new ones and draws again.</summary>
        public static event Action Changed;
    }
}
