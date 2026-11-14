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
        public static readonly IBrush Window = StudioTokens.SurfaceViewBrush;
        public static readonly IBrush Bar = StudioTokens.SurfacePanelBrush;
        public static readonly IBrush Header = StudioTokens.SurfaceControlBrush;
        public static readonly IBrush Empty = Pair("#26262B", "#EBEBF0");
        public static readonly IBrush EmptyHover = Pair("#33333A", "#DDDDE4");
        public static readonly IBrush Clip = Pair("#3B5878", "#8FB0D8");
        /// <summary>Playing: the ▶, the outline and the progress bar of the slot that plays (a clip keeps its own fill).</summary>
        public static readonly IBrush Playing = StudioTokens.PlayBrush;
        public static readonly IBrush Progress = StudioTokens.PlayBrush;
        /// <summary>The dark chip under a slot's ▶ or ● while it has a state, so the state colour reads on any clip colour.</summary>
        public static readonly IBrush StateChip = StudioTokens.SurfaceBaseBrush;
        public static readonly IBrush Scene = Pair("#37373F", "#D4D4DC");
        public static readonly IBrush SceneHover = Pair("#45454F", "#C4C4CE");
        public static readonly IBrush Text = StudioTokens.TextPrimaryBrush;
        public static readonly IBrush DarkText = Make("#17171A");
        public static readonly IBrush DimText = StudioTokens.TextPlaceholderBrush;
        /// <summary>Labels on control surfaces (the header's chips, #37373F): 4.6:1 where DimText was 3.5:1.</summary>
        public static readonly IBrush LabelText = StudioTokens.TextSecondaryBrush;
        public static readonly IBrush Selection = Pair("#F0C24B", "#8F6200");
        public static readonly IBrush Learn = Pair("#FF8C2A", "#B85200");
        public static readonly IBrush Ring = Pair("#E0457B", "#C2185B");
        public static readonly IBrush Record = StudioTokens.RecordBrush;
        public static readonly IBrush Override = StudioTokens.OverrideBrush;
        public static readonly IBrush Mute = Pair("#D08A2E", "#A86200");
        public static readonly IBrush Solo = Pair("#3F8FD8", "#1F6FC0");
        public static readonly IBrush Transparent = Brushes.Transparent;

        // Mixer (0.4)
        public static readonly IBrush Groove = Pair("#141417", "#B8B8C2");
        public static readonly IBrush Fader = Pair("#7FA7D9", "#2F6EB5");
        public static readonly IBrush Send = Pair("#B48BE0", "#7A4FB5");
        public static readonly IBrush Pan = Pair("#D9B45B", "#8A6800");
        public static readonly IBrush Cue = Pair("#4BB8C8", "#1D7F8E");
        public static readonly IBrush CrossA = Pair("#E8A33A", "#B06D00");    // crossfader sides (0.6), Live's colours
        public static readonly IBrush CrossB = Pair("#5AA0E0", "#1F6FC0");
        public static readonly IBrush Thumb = Pair("#D5D5DC", "#FAFAFC");
        public static readonly IBrush MeterLow = Pair("#43C463", "#1E9A44");
        public static readonly IBrush MeterMid = Pair("#E8CD3A", "#A88200");
        public static readonly IBrush MeterHigh = Pair("#F0473C", "#D0281E");
        public static readonly IBrush GroupHeader = Pair("#35353D", "#CFCFD8");
        public static readonly IBrush ReturnHeader = Pair("#33303D", "#D8D2E2");
        public static readonly IBrush MixerBack = Pair("#202024", "#EDEDF2");
        public static readonly IBrush ControllerRing = Pair("#E8473C", "#CE2A20");   // a controller's session ring (0.8)

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

        /// <summary>A machine's identity colour as a brush (P3h), or null for a machine with none (the Master, a return).</summary>
        public static IBrush IdentityBrush(BuzzGUI.Interfaces.IMachine machine) => MachineIdentity.CategoryOf(machine) is { } c ? StudioTokens.IdentityBrush(c) : null;

        /// <summary>The fill of a clip that has no colour of its own: its machine's identity colour, else the neutral clip blue.</summary>
        public static IBrush DefaultFill(BuzzGUI.Interfaces.IMachine machine) => IdentityBrush(machine) ?? Clip;

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

        /// <summary>A brush of the dark theme's colour or the light theme's, following the theme (P4b).</summary>
        internal static IBrush Pair(string dark, string lightHex) => StudioTokens.Themed(() => Color.Parse(NeoBuzz.Editor.Tokens.IsLight ? lightHex : dark));

        /// <summary>A pen of the dark theme's colour or the light theme's, following the theme (P4b).</summary>
        internal static IPen PairPen(string dark, string lightHex, double thickness, IDashStyle dash = null) => new Pen(Pair(dark, lightHex), thickness) { DashStyle = dash };

        public static IBrush Make(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
    }
}
