// Fork (docs/specs/studio-mvp.md S5): the Session View's colours, as the WPF view's SessionPalette
// (ReBuzz/AppViews/Session/SessionPalette.cs), in Avalonia brushes. Fork (ux-overhaul.md §3.1): the state
// colours (play, record, arrangement override) are the studio's reserved tokens, never a clip colour, and
// text on a colour is white or black by contrast.

using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell.Design;

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
        /// <summary>Playing: the ▶, the outline and the progress bar of the slot that plays (a clip keeps its own fill).</summary>
        public static readonly IBrush Playing = StudioTokens.PlayBrush;
        public static readonly IBrush Progress = StudioTokens.PlayBrush;
        /// <summary>The dark chip under a slot's ▶ or ● while it has a state, so the state colour reads on any clip colour.</summary>
        public static readonly IBrush StateChip = StudioTokens.Brush(StudioTokens.SurfaceBase);
        public static readonly IBrush Scene = Make("#37373F");
        public static readonly IBrush SceneHover = Make("#45454F");
        public static readonly IBrush Text = Make("#E8E8EC");
        public static readonly IBrush DarkText = Make("#17171A");
        public static readonly IBrush DimText = Make("#8C8C96");
        /// <summary>Labels on control surfaces (the header's chips, #37373F): 4.6:1 where DimText was 3.5:1.</summary>
        public static readonly IBrush LabelText = StudioTokens.TextSecondaryBrush;
        public static readonly IBrush Selection = Make("#F0C24B");
        public static readonly IBrush Learn = Make("#FF8C2A");
        public static readonly IBrush Ring = Make("#E0457B");
        public static readonly IBrush Record = StudioTokens.RecordBrush;
        public static readonly IBrush Override = StudioTokens.OverrideBrush;
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

        /// <summary>
        /// The colours the colour menus offer: the palette's, less those reserved for a state (ux-overhaul.md §3.1: within
        /// ΔE 20 of play, record or arrangement override). <paramref name="current"/>, the index in use, is always offered.
        /// </summary>
        public static IEnumerable<(int Index, string Name, Color Color)> OfferedColours(int current = -1)
        {
            var choices = ColorChoices;
            for (int i = 0; i < choices.Length; i++)
                if (!StudioTokens.IsReservedForState(choices[i].Color) || (current >= 0 && current % choices.Length == i))
                    yield return (i, choices[i].Name, choices[i].Color);
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

        /// <summary>Readable text on a background: white or black, whichever has more contrast (at least 4.58:1).</summary>
        public static IBrush TextOn(IBrush background) =>
            background is ISolidColorBrush s ? StudioTokens.TextOnBrush(s.Color) : Text;

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
