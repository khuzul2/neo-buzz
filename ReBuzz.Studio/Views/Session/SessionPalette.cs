// Fork (docs/specs/studio-mvp.md S5): the Session View's colours, as the WPF view's SessionPalette
// (ReBuzz/AppViews/Session/SessionPalette.cs), in Avalonia brushes.

using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ReBuzz.Core.Session;

namespace ReBuzz.Studio.Views.Session
{
    internal static class SessionPalette
    {
        public static readonly IBrush Window = Make("#1B1B1E");
        public static readonly IBrush Bar = Make("#242428");
        public static readonly IBrush Header = Make("#2E2E34");
        public static readonly IBrush Empty = Make("#26262B");
        public static readonly IBrush EmptyHover = Make("#33333A");
        public static readonly IBrush Clip = Make("#3B5878");
        public static readonly IBrush Playing = Make("#2E9B49");
        public static readonly IBrush Progress = Make("#6FD98A");
        public static readonly IBrush Stopping = Make("#E8823A");
        public static readonly IBrush Scene = Make("#37373F");
        public static readonly IBrush SceneHover = Make("#45454F");
        public static readonly IBrush Text = Make("#E8E8EC");
        public static readonly IBrush DarkText = Make("#17171A");
        public static readonly IBrush DimText = Make("#8C8C96");
        public static readonly IBrush Selection = Make("#F0C24B");
        public static readonly IBrush Learn = Make("#FF8C2A");
        public static readonly IBrush Ring = Make("#E0457B");
        public static readonly IBrush Record = Make("#D23B3B");
        public static readonly IBrush Override = Make("#E8823A");
        public static readonly IBrush Mute = Make("#D08A2E");
        public static readonly IBrush Solo = Make("#3F8FD8");
        public static readonly IBrush Transparent = Brushes.Transparent;

        // Mixer (0.4)
        public static readonly IBrush Groove = Make("#141417");
        public static readonly IBrush Fader = Make("#7FA7D9");
        public static readonly IBrush Send = Make("#B48BE0");
        public static readonly IBrush Pan = Make("#D9B45B");
        public static readonly IBrush Cue = Make("#4BB8C8");
        public static readonly IBrush CrossA = Make("#E8A33A");    // crossfader sides (0.6), Live's colours
        public static readonly IBrush CrossB = Make("#5AA0E0");
        public static readonly IBrush Thumb = Make("#D5D5DC");
        public static readonly IBrush MeterLow = Make("#43C463");
        public static readonly IBrush MeterMid = Make("#E8CD3A");
        public static readonly IBrush MeterHigh = Make("#F0473C");
        public static readonly IBrush GroupHeader = Make("#3D3D47");
        public static readonly IBrush ReturnHeader = Make("#33303D");
        public static readonly IBrush MixerBack = Make("#202024");
        public static readonly IBrush ControllerRing = Make("#E8473C");   // a controller's session ring (0.8)

        static readonly Dictionary<Color, IBrush> brushes = new Dictionary<Color, IBrush>();

        /// <summary>
        /// Clip, track and scene colours: an index into the theme's PatternColors, the list the
        /// engine keeps as ARGB (SessionColourChoices; the Default theme's until the studio has themes).
        /// </summary>
        public static (string Name, Color Color)[] ColorChoices
        {
            get
            {
                var choices = SessionColourChoices.Choices ?? SessionColourChoices.Defaults;
                var list = new (string, Color)[choices.Length];
                for (int i = 0; i < choices.Length; i++)
                    list[i] = (choices[i].Name, Color.FromUInt32(choices[i].Argb));
                return list;
            }
        }

        /// <summary>Brush for a colour index, or null for "no colour".</summary>
        public static IBrush ColorBrush(int index)
        {
            var choices = SessionColourChoices.Choices ?? SessionColourChoices.Defaults;
            if (index < 0 || choices.Length == 0) return null;
            return BrushOf(Color.FromUInt32(choices[index % choices.Length].Argb));
        }

        public static IBrush BrushOf(Color color)
        {
            if (!brushes.TryGetValue(color, out var b))
            {
                b = new ImmutableSolidColorBrush(color);
                brushes[color] = b;
            }
            return b;
        }

        /// <summary>Readable text colour on a background.</summary>
        public static IBrush TextOn(IBrush background) =>
            background is ISolidColorBrush s && 0.299 * s.Color.R + 0.587 * s.Color.G + 0.114 * s.Color.B > 150 ? DarkText : Text;

        /// <summary>A lighter version of a colour, for hover and playing states.</summary>
        public static IBrush Lighten(IBrush brush, double amount)
        {
            if (brush is not ISolidColorBrush s) return brush;
            byte L(byte v) => (byte)Math.Round(v + (255 - v) * amount);
            return BrushOf(Color.FromRgb(L(s.Color.R), L(s.Color.G), L(s.Color.B)));
        }

        public static IBrush Make(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
    }
}
