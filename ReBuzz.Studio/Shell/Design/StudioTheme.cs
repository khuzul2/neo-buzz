// Fork (docs/specs/ux-overhaul.md §3.3, P2b): the studio's themes. Dark is the default and is made of the tokens
// (StudioTokens); Classic Buzz is the old beige machine canvas and brown arrangement, as an option in Preferences ▸
// Appearance. A theme is a table of colours, here the two code-drawn canvases' (the Sequence view, the Machine view)
// and the controls around them: every other view takes its colours from the tokens in both themes, and no view
// hard-codes a colour (UxP2bTests.NoViewHardCodesAColour reads the sources).

using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using BuzzGUI.Common.Settings;
using NeoBuzz.Editor;

namespace ReBuzz.Studio.Shell.Design
{
    /// <summary>The choices of Preferences ▸ Appearance ▸ Theme (the setting's values, saved by name; new ones are only added).</summary>
    public enum AppearanceTheme
    {
        [Description("Dark")]
        Dark,

        [Description("Light")]
        Light,

        [Description("System")]
        System,

        [Description("Classic Buzz")]
        Classic,
    }

    /// <summary>The studio's own settings (not the classic app's: they are not in its Settings window).</summary>
    public sealed class AppearanceSettings : Settings
    {
        [BuzzSetting(AppearanceTheme.Dark, Description = "Dark is the studio's look, Light its light twin, System follows the operating system's choice and changes with it. Classic Buzz draws the machine view on beige and the sequence on brown, as Buzz always has; every other view stays dark.")]
        public AppearanceTheme Theme { get => StudioTheme.Kind; set => StudioTheme.Kind = value; }
    }

    /// <summary>The theme in use, and its colours.</summary>
    internal static class StudioTheme
    {
        static AppearanceTheme kind = AppearanceTheme.Dark;
        static AppearanceTheme effective = AppearanceTheme.Dark;
        static AppearanceSettings settings;

        /// <summary>The choice (Dark, Light, System, Classic); the Appearance setting sets it (from the saved setting, and when it changes).</summary>
        public static AppearanceTheme Kind
        {
            get => kind;
            set
            {
                if (!Enum.IsDefined(typeof(AppearanceTheme), value) || value == kind) return;
                kind = value;
                Refresh();
            }
        }

        /// <summary>
        /// Whether the operating system asks for light (what System follows). The app sets it from the platform's colour values and
        /// calls <see cref="Refresh"/> when they change; a test sets it to stand for the system.
        /// </summary>
        public static Func<bool> SystemIsLight { get; set; } = () => false;

        /// <summary>The theme every view draws in: Dark, Light or Classic (System is whichever the system asks for).</summary>
        public static AppearanceTheme Effective => effective;

        public static bool IsClassic => effective == AppearanceTheme.Classic;

        public static bool IsLight => effective == AppearanceTheme.Light;

        /// <summary>Works out the theme in use again (the choice or the system's changed); when it differs, the tokens and the views follow.</summary>
        public static void Refresh()
        {
            bool systemLight = false;
            try { systemLight = SystemIsLight(); } catch (Exception) { }
            var now = kind == AppearanceTheme.System ? (systemLight ? AppearanceTheme.Light : AppearanceTheme.Dark) : kind;
            if (now == effective) return;
            effective = now;
            NeoBuzz.Editor.Tokens.SetLight(now == AppearanceTheme.Light);       // the brushes and caches made from tokens follow
            BuzzGUI.Common.StudioLook.Light = now == AppearanceTheme.Light;     // so does the pattern editor, which draws its own colours
            TellTheMachines();
            look = null;
            machineLook = null;
            Changed?.Invoke();
        }

        // Each Neo machine in Gear compiles its own copy of the editor kit, so each has a Tokens of its own: they are told
        // too, those loaded now and those loaded later (an editor opened after the switch).
        static bool watchingLoads;

        static void TellTheMachines()
        {
            if (!watchingLoads)
            {
                watchingLoads = true;
                AppDomain.CurrentDomain.AssemblyLoad += (_, e) => { if (effective == AppearanceTheme.Light) Tell(e.LoadedAssembly); };
            }
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) Tell(a);
        }

        static void Tell(System.Reflection.Assembly a)
        {
            try
            {
                if (a == typeof(NeoBuzz.Editor.Tokens).Assembly || a.IsDynamic) return;
                var t = a.GetType("NeoBuzz.Editor.Tokens", false);
                t?.GetMethod("SetLight", new[] { typeof(bool) })?.Invoke(null, new object[] { effective == AppearanceTheme.Light });
            }
            catch (Exception) { }
        }

        /// <summary>After the theme in use changes: views that drew in the old theme draw again.</summary>
        public static event Action Changed;

        /// <summary>The saved setting, read once (it sets <see cref="Kind"/> as it loads).</summary>
        public static AppearanceSettings Settings => settings ??= new AppearanceSettings();

        static SequenceLook look;
        static MachineLook machineLook;

        /// <summary>The Sequence view's colours in this theme.</summary>
        public static SequenceLook Sequence => look ??= IsClassic ? SequenceLook.Classic : SequenceLook.MakeDark();

        /// <summary>The Machine view's colours in this theme.</summary>
        public static MachineLook Machines => machineLook ??= IsClassic ? MachineLook.Classic : MachineLook.MakeDark();

        internal static IBrush Solid(string argb) => new ImmutableSolidColorBrush(Color.Parse(argb));

        internal static IBrush Solid(Color c) => new ImmutableSolidColorBrush(c);

        internal static IBrush Solid(Color c, byte alpha) => new ImmutableSolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));

        internal static IPen Pen(IBrush brush, double thickness = 1) => new ImmutablePen(brush.ToImmutable(), thickness);
    }

    /// <summary>The Sequence view: the canvas, the pattern list and the corner's controls.</summary>
    internal sealed class SequenceLook
    {
        public bool IsClassic;

        // the canvas
        public IBrush Background, TrackView, Ruler, Header, HeaderSelected, Line, TrackLine, BarLine;
        public IBrush RulerText, HeaderText, BlockText, NameBack;
        public IPen Border, NameBackPen, EventBorder;
        public IBrush Note, Off, Param;

        // blocks without a colour of their own: [pattern, break, mute, off]; null: the Buzz theme file's (Classic)
        public Color[] BoxColours;

        // the header's buttons
        public IBrush ButtonOff, MuteOn, SoloOn, DisableOn;
        public IPen ButtonBorder;
        public IBrush MuteIconOff, MuteIconOn, SoloTextOff, SoloTextOn, DisableTextOff, DisableTextOn, MuteStrike;

        // the overlay
        public IBrush Cursor, CursorInactive, Selection, ResizeFill, Loop, SongEnd, Play, PlayEdge;
        public IPen SelectionBorder, ResizePen;
        // daw-modes.md AR5: a track recording shows a band in the record colour
        public IBrush Record;
        public IPen RecordPen;

        // the pattern list and the corner
        public Color PatternList;
        public IBrush ListText, ListHover, ListHoverText, ListPressed, ListSelected, ListSelectedHover, ListBorder;
        public IBrush ControlBack, ControlText, ControlBorder;

        static IBrush Gradient(Color a, Color b, RelativePoint start, RelativePoint end) => new ImmutableLinearGradientBrush(
            new[] { new ImmutableGradientStop(0, a), new ImmutableGradientStop(1, b) }, startPoint: start, endPoint: end);

        static readonly RelativePoint TopLeft = new RelativePoint(0, 0, RelativeUnit.Relative), Right = new RelativePoint(1, 0, RelativeUnit.Relative),
            Bottom = new RelativePoint(1, 1, RelativeUnit.Relative);

        /// <summary>Buzz's: the Default theme's SequenceEditor.xaml (the beige and brown the classic app has).</summary>
        public static readonly SequenceLook Classic = MakeClassic();

        static SequenceLook MakeClassic()
        {
            IBrush S(string c) => StudioTheme.Solid(c);
            var l = new SequenceLook { IsClassic = true };
            l.Background = Gradient(Color.Parse("#E5E5D7"), Color.Parse("#F7F7F3"), TopLeft, Right);
            l.TrackView = Gradient(Color.Parse("#BA9"), Color.Parse("#765"), TopLeft, Bottom);
            l.Ruler = l.Header = S("#BA9");
            l.HeaderSelected = S("#3000");
            l.Border = StudioTheme.Pen(S("#8000"));
            l.Line = S("#8000");
            l.TrackLine = S("#5000");
            l.BarLine = S("#2000");
            l.RulerText = l.HeaderText = StudioTokens.Black;
            l.BlockText = null;                     // the theme file's "SE Text"
            l.NameBack = S("#90e0e0ff");
            l.NameBackPen = StudioTheme.Pen(S("#40333333"));
            l.EventBorder = StudioTheme.Pen(StudioTokens.Black);
            l.Note = S("#90EE90");                  // light green
            l.Off = S("#808080");
            l.Param = S("#FFA500");
            l.ButtonOff = S("#E4E4E4");
            l.MuteOn = S("#E8907A");
            l.SoloOn = S("#9EE08C");
            l.DisableOn = S("#696969");
            l.ButtonBorder = StudioTheme.Pen(S("#808080"));
            l.MuteIconOff = S("#404040");
            l.MuteIconOn = S("#5A3A30");
            l.SoloTextOff = S("#3A3A3A");
            l.SoloTextOn = S("#1F5A18");
            l.DisableTextOff = S("#111");
            l.DisableTextOn = StudioTokens.White;
            l.MuteStrike = StudioTokens.Black;
            l.Cursor = S("#2000");
            l.CursorInactive = S("#2005");
            l.Selection = new ImmutableLinearGradientBrush(new[] { new ImmutableGradientStop(0, Color.Parse("#100000FF")), new ImmutableGradientStop(1, Color.Parse("#200000FF")) },
                startPoint: TopLeft, endPoint: Bottom);
            l.SelectionBorder = StudioTheme.Pen(S("#600000FF"));
            l.ResizeFill = S("#33000000");
            l.ResizePen = new ImmutablePen(S("#aa000000").ToImmutable(), 1, new ImmutableDashStyle(new double[] { 4, 4 }, 0));
            l.Record = S("#44E02020");
            l.RecordPen = StudioTheme.Pen(S("#CCC01010"));
            l.Loop = S("#FF6347");                  // tomato
            l.SongEnd = StudioTokens.Black;
            l.Play = S("#FFFF00");
            l.PlayEdge = S("#B0000000");
            l.PatternList = Color.Parse("#F7F7F3");
            l.ListText = StudioTokens.Black;
            l.ListHover = S("#E0E0D8");
            l.ListHoverText = StudioTokens.Black;
            l.ListPressed = S("#D0D0C6");
            l.ListSelected = StudioTokens.SelectionFillBrush;
            l.ListSelectedHover = S("#1A528F");
            l.ListBorder = S("#8000");
            l.ControlBack = S("#E8E8E2");
            l.ControlText = StudioTokens.Black;
            l.ControlBorder = S("#8000");
            return l;
        }

        /// <summary>The tokens' (the set in use, dark or light): its ramp, the accent for the selection, the state colours for play and the buttons.</summary>
        public static SequenceLook MakeDark()
        {
            IBrush B(Color c) => StudioTheme.Solid(c);
            var t = new
            {
                View = StudioTokens.SurfaceView, Panel = StudioTokens.SurfacePanel, Control = StudioTokens.SurfaceControl, Border = StudioTokens.SurfaceBorder,
                Primary = StudioTokens.TextPrimary, Secondary = StudioTokens.TextSecondary,
            };
            var l = new SequenceLook { IsClassic = false };
            l.Background = l.TrackView = B(t.View);
            l.Ruler = l.Header = B(t.Panel);
            l.HeaderSelected = StudioTheme.Solid(StudioTokens.Accent, 0x33);
            l.Border = StudioTheme.Pen(B(t.Border));
            l.Line = B(StudioTokens.Of(Tokens.ControlTrack));
            l.TrackLine = B(t.Border);
            l.BarLine = B(StudioTokens.Of(Tokens.GridLine));
            l.RulerText = B(t.Secondary);
            l.HeaderText = B(t.Primary);
            l.BlockText = B(t.Primary);
            l.NameBack = StudioTheme.Solid(StudioTokens.SurfaceBase, 0xD0);
            l.NameBackPen = StudioTheme.Pen(StudioTheme.Solid(t.Border, 0xA0));
            l.EventBorder = StudioTheme.Pen(StudioTokens.Black);
            l.Note = B(StudioTokens.Of(Tokens.MeterNormal));
            l.Off = B(t.Secondary);
            l.Param = B(StudioTokens.Warning);
            l.ButtonOff = B(t.Control);
            l.MuteOn = B(StudioTokens.Override);
            l.SoloOn = B(StudioTokens.Play);
            l.DisableOn = B(StudioTokens.Of(Tokens.ControlTrack));
            l.ButtonBorder = StudioTheme.Pen(B(StudioTokens.Of(Tokens.ControlTrack)));
            l.MuteIconOff = B(t.Secondary);
            l.MuteIconOn = StudioTokens.TextOnBrush(StudioTokens.Override);
            l.SoloTextOff = B(t.Secondary);
            l.SoloTextOn = StudioTokens.TextOnBrush(StudioTokens.Play);
            l.DisableTextOff = B(t.Secondary);
            l.DisableTextOn = StudioTokens.TextOnBrush(StudioTokens.ControlTrack);
            l.MuteStrike = StudioTokens.TextOnBrush(StudioTokens.Override);
            l.Cursor = StudioTheme.Solid(StudioTokens.TextPrimary, 0x24);
            l.CursorInactive = StudioTheme.Solid(StudioTokens.TextPrimary, 0x12);
            l.Selection = StudioTokens.BlockSelectionFill;
            l.SelectionBorder = StudioTheme.Pen(StudioTokens.AccentBrush, 1.5);
            l.ResizeFill = StudioTheme.Solid(StudioTokens.TextPrimary, 0x22);
            l.ResizePen = new ImmutablePen(StudioTheme.Solid(StudioTokens.TextPrimary, 0xCC).ToImmutable(), 1, new ImmutableDashStyle(new double[] { 4, 4 }, 0));
            l.Record = StudioTheme.Solid(StudioTokens.Record, 0x44);
            l.RecordPen = StudioTheme.Pen(StudioTheme.Solid(StudioTokens.Record, 0xE0));
            l.Loop = B(StudioTokens.Warning);
            l.SongEnd = B(t.Primary);
            l.Play = B(StudioTokens.Play);
            l.PlayEdge = StudioTheme.Solid(StudioTokens.SurfaceBase, 0xE0);
            l.PatternList = t.Panel;
            l.ListText = B(t.Primary);
            l.ListHover = B(t.Control);
            l.ListHoverText = B(t.Primary);
            l.ListPressed = B(t.Border);
            l.ListSelected = StudioTokens.SelectionFillBrush;
            l.ListSelectedHover = StudioTheme.Solid(StudioTokens.SelectionFill, 0xE0);
            l.ListBorder = B(t.Border);
            l.ControlBack = B(t.Control);
            l.ControlText = B(t.Primary);
            l.ControlBorder = B(t.Border);
            // Blocks that name no pattern colour: a pattern with none, a break, a mute (or thru), a stop.
            l.BoxColours = Tokens.IsLight
                ? new[] { Color.Parse("#A9C4E8"), Color.Parse("#D5D5DE"), Color.Parse("#E3B4B4"), Color.Parse("#E4E4EA") }
                : new[] { Color.Parse("#3B5878"), Color.Parse("#4A4A55"), Color.Parse("#6B3F3F"), Color.Parse("#3A3A44") };
            return l;
        }
    }

    /// <summary>The Machine view: the canvas' colours by the Buzz theme file's names (the "MV ..." table).</summary>
    internal sealed class MachineLook
    {
        public bool IsClassic;
        readonly System.Collections.Generic.Dictionary<string, Color> table = new();

        /// <summary>A name's colour, or null where the Buzz theme file's answers (Classic, and a name the table lacks).</summary>
        public Color? Colour(string name) => table.TryGetValue(name, out var c) ? c : null;

        public IBrush DeleteX, DeleteXHot, Error, DivergedText, Selection, SelectionBorder;
        public bool Glass;

        public static readonly MachineLook Classic_ = new MachineLook
        {
            IsClassic = true,
            DeleteX = StudioTokens.Black,
            DeleteXHot = StudioTokens.White,
            Error = StudioTheme.Solid("#8B0000"),       // dark red
            DivergedText = StudioTokens.White,
            Glass = true,
        };

        public static MachineLook Classic => Classic_;

        /// <summary>The tokens' (the set in use, dark or light).</summary>
        public static MachineLook MakeDark()
        {
            var l = new MachineLook
            {
                IsClassic = false,
                DeleteX = StudioTokens.TextSecondaryBrush,
                DeleteXHot = StudioTokens.TextPrimaryBrush,
                Error = StudioTokens.Brush(StudioTokens.Warning),   // the look is rebuilt when the theme changes
                DivergedText = StudioTokens.White,
                Selection = StudioTheme.Solid(StudioTokens.Accent, 0x40),
                SelectionBorder = StudioTokens.AccentBrush,
            };
            bool light = Tokens.IsLight;
            void Set(string name, string hex, string lightHex) => l.table[name] = Color.Parse(light ? lightHex : hex);
            void Tok(string name, Color c) => l.table[name] = c;
            Tok("MV Background", StudioTokens.SurfaceView);
            Tok("MV Line", StudioTokens.Of(Tokens.ControlTrack));
            Tok("MV Arrow", StudioTokens.TextSecondary);
            Tok("MV Machine Text", StudioTokens.TextPrimary);
            Tok("MV Machine Border", StudioTokens.Of(Tokens.ControlTrack));
            Set("MV Control", "#4A4636", "#E6DEB8");
            Set("MV Master", "#4A4A55", "#D0D0D9");
            Set("MV Generator", "#2F4570", "#BCD0F2");
            Set("MV Generator Mute", "#262B3A", "#DCE3F2");
            Set("MV Effect", "#4E3355", "#E4C9EA");
            Set("MV Effect Mute", "#2E2433", "#F0E4F4");
            Set("MV Generator LED On", "#7AA8FF", "#1F5FD0");
            Set("MV Generator LED Off", "#1B2744", "#A9BBE0");
            Set("MV Effect LED On", "#FF8A8A", "#C42828");
            Set("MV Effect LED Off", "#4A2228", "#E4B8BC");
            Set("MV Master LED On", "#E8E8C1", "#6A6A00");
            Set("MV Master LED Off", "#595922", "#C4C49A");
            return l;
        }
    }
}
