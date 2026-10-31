using System;
using System.Linq;
using System.Windows.Media;

namespace ReBuzz.AppViews.Session
{
    /// <summary>Colours of the Session View (fork addition).</summary>
    internal static class SessionPalette
    {
        public static readonly Brush Window = Make("#1B1B1E");
        public static readonly Brush Bar = Make("#242428");
        public static readonly Brush Header = Make("#2E2E34");
        public static readonly Brush Empty = Make("#26262B");
        public static readonly Brush EmptyHover = Make("#33333A");
        public static readonly Brush Clip = Make("#3B5878");
        public static readonly Brush Playing = Make("#2E9B49");
        public static readonly Brush Progress = Make("#6FD98A");
        public static readonly Brush Stopping = Make("#E8823A");
        public static readonly Brush Scene = Make("#37373F");
        public static readonly Brush SceneHover = Make("#45454F");
        public static readonly Brush Text = Make("#E8E8EC");
        public static readonly Brush DarkText = Make("#17171A");
        public static readonly Brush DimText = Make("#8C8C96");
        public static readonly Brush Selection = Make("#F0C24B");
        public static readonly Brush Learn = Make("#FF8C2A");
        public static readonly Brush Ring = Make("#E0457B");
        public static readonly Brush Record = Make("#D23B3B");
        public static readonly Brush Override = Make("#E8823A");
        public static readonly Brush Mute = Make("#D08A2E");
        public static readonly Brush Solo = Make("#3F8FD8");
        public static readonly Brush Transparent = Brushes.Transparent;

        // Mixer (0.4)
        public static readonly Brush Groove = Make("#141417");
        public static readonly Brush Fader = Make("#7FA7D9");
        public static readonly Brush Send = Make("#B48BE0");
        public static readonly Brush Pan = Make("#D9B45B");
        public static readonly Brush Cue = Make("#4BB8C8");
        public static readonly Brush CrossA = Make("#E8A33A");    // crossfader sides (0.6), Live's colours
        public static readonly Brush CrossB = Make("#5AA0E0");
        public static readonly Brush Thumb = Make("#D5D5DC");
        public static readonly Brush MeterLow = Make("#43C463");
        public static readonly Brush MeterMid = Make("#E8CD3A");
        public static readonly Brush MeterHigh = Make("#F0473C");
        public static readonly Brush GroupHeader = Make("#3D3D47");
        public static readonly Brush ReturnHeader = Make("#33303D");
        public static readonly Brush MixerBack = Make("#202024");
        public static readonly Brush Ring = Make("#E8473C");        // a controller's session ring (0.8), Live's red box

        /// <summary>
        /// Clip, track and scene colours: an index into the theme's PatternColors, the same list
        /// the Sequence View uses for pattern colours. MainWindow replaces the default (the
        /// Default theme's list) with the active theme's.
        /// </summary>
        public static (string Name, Color Color)[] ColorChoices { get; set; } =
        {
            ("Powder Blue", C("#8EB9C2")), ("Moss Green", C("#C7DBAE")), ("Wheat", C("#F5DEB3")), ("Light Blue", C("#A8C5DD")),
            ("Ochre", C("#C47E29")), ("Camouflage Green", C("#677665")), ("Asparagus", C("#678443")), ("Slate Blue", C("#5A6BA1")),
            ("Lilac", C("#B97AA3")), ("Red", C("#D7001C")), ("Orange", C("#E15E13")), ("Yellow", C("#F7C12D")),
            ("Light Olive", C("#A6C64C")), ("Kelly Green", C("#379C35")), ("Sky Blue", C("#57B8E8")), ("Steel Blue", C("#446FB6")),
        };

        static readonly System.Collections.Generic.Dictionary<Color, Brush> brushes = new System.Collections.Generic.Dictionary<Color, Brush>();

        /// <summary>Brush for a colour index, or null for "no colour".</summary>
        public static Brush ColorBrush(int index)
        {
            var choices = ColorChoices;
            if (index < 0 || choices == null || choices.Length == 0) return null;
            return BrushOf(choices[index % choices.Length].Color);
        }

        public static Brush BrushOf(Color color)
        {
            if (!brushes.TryGetValue(color, out var b))
            {
                b = new SolidColorBrush(color);
                b.Freeze();
                brushes[color] = b;
            }
            return b;
        }

        /// <summary>Readable text colour on a background.</summary>
        public static Brush TextOn(Brush background) =>
            background is SolidColorBrush s && 0.299 * s.Color.R + 0.587 * s.Color.G + 0.114 * s.Color.B > 150 ? DarkText : Text;

        /// <summary>A lighter version of a colour, for hover and playing states.</summary>
        public static Brush Lighten(Brush brush, double amount)
        {
            if (brush is not SolidColorBrush s) return brush;
            byte L(byte v) => (byte)Math.Round(v + (255 - v) * amount);
            return BrushOf(Color.FromRgb(L(s.Color.R), L(s.Color.G), L(s.Color.B)));
        }

        public static Brush Make(string hex)
        {
            var b = new SolidColorBrush(C(hex));
            b.Freeze();
            return b;
        }

        static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        /// <summary>Sets <see cref="ColorChoices"/> from a theme's PatternColors (name, colour) list.</summary>
        public static void UseThemeColors(System.Collections.Generic.IEnumerable<(string Name, Color Color)> colors)
        {
            var list = colors?.ToArray();
            if (list != null && list.Length > 0)
                ColorChoices = list;
        }
    }
}
