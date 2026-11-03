// Fork (docs/specs/studio-mvp.md S6): what the shared editor sources use from BuzzGUI.Common's
// Windows build that its portable build (the engine's machine API, engine-split.md §4) leaves out.
// Copies of the originals (ActionGroup, PatternAction, ParameterExtensions), under the names the
// sources use, and toolkit-free stand-ins for the WPF types they name (Color, TextFormattingMode).

using BuzzGUI.Interfaces;

namespace BuzzGUI.Common
{
    /// <summary>BuzzGUI.Common's ActionGroup: one undo step for the actions done while it lives.</summary>
    public class ActionGroup : IDisposable
    {
        readonly IActionStack actionStack;

        public ActionGroup(IActionStack a)
        {
            actionStack = a;
            actionStack.BeginActionGroup();
        }

        public void Dispose()
        {
            actionStack.EndActionGroup();
        }
    }
}

namespace BuzzGUI.Common.Actions
{
    /// <summary>BuzzGUI.Common's PatternAction: an action on a pattern, found again by name.</summary>
    public abstract class PatternAction : MachineAction
    {
        readonly string patternName;

        protected IPattern Pattern
        {
            get
            {
                var p = Machine.Patterns.FirstOrDefault(x => x.Name == patternName);
                if (p == null) throw new ActionException(this);
                return p;
            }
        }

        public PatternAction(IPattern p)
            : base(p.Machine)
        {
            patternName = p.Name;
        }
    }
}

namespace BuzzGUI.Common.Actions.MachineActions
{
    /// <summary>
    /// BuzzGUI.Common's DeletePatternAction without its copy of the engine's own columns (PatternClip
    /// isn't portable): undo makes the pattern again, empty, as the editor's data comes back empty
    /// on Windows too (the editor drops a deleted pattern's data).
    /// </summary>
    public class DeletePatternAction : MachineAction
    {
        string name;
        readonly int length;
        IPattern target;

        public DeletePatternAction(IPattern p)
            : base(p.Machine)
        {
            name = p.Name;
            length = p.Length;
            target = p;
        }

        protected override void DoAction()
        {
            var pat = target != null && Machine.Patterns.Contains(target) ? target : Machine.Patterns.FirstOrDefault(p => p.Name == name);
            if (pat != null)
            {
                target = pat;
                name = pat.Name;
                Machine.DeletePattern(pat);
            }
        }

        protected override void UndoAction()
        {
            var before = Machine.Patterns.ToList();
            Machine.CreatePattern(PatternNames.Free(Machine, name), length);
            target = PatternNames.Added(Machine, before) ?? target;
        }
    }
}

namespace BuzzGUI.Common.InterfaceExtensions
{
    /// <summary>BuzzGUI.Common's ParameterExtensions (the same methods).</summary>
    public static class PortableParameterExtensions
    {
        public static string GetHexValueString(this IParameter parameter, int v)
        {
            switch (parameter.Type)
            {
                case ParameterType.Switch: return v.ToString("X1");
                case ParameterType.Byte: return parameter.Flags.HasFlag(ParameterFlags.Ascii) ? ((char)v).ToString() : v.ToString("X2");
                case ParameterType.Word: return v.ToString("X4");
                case ParameterType.Note: return BuzzNote.TryToString(v) ?? "<invalid>";
                default: return "?";
            }
        }

        public static string GetValueDescriptionWithHexValue(this IParameter parameter, int value)
        {
            return parameter.DescribeValue(value) + " (" + GetHexValueString(parameter, value) + ")";
        }
    }

    /// <summary>A machine's colour in the machine view: ReBuzz's default theme, as data.</summary>
    public static class PortableMachineExtensions
    {
        public static WDE.ModernPatternEditor.Color GetThemeColor(this IMachine machine)
        {
            if (machine.IsControlMachine) return WDE.ModernPatternEditor.Color.FromRgb(0xD5, 0xD5, 0xF0);
            switch (machine.DLL.Info.Type)
            {
                case MachineType.Master: return WDE.ModernPatternEditor.Color.FromRgb(0xEC, 0xE9, 0xD8);
                case MachineType.Generator: return WDE.ModernPatternEditor.Color.FromRgb(0x9C, 0xBB, 0xE0);
                case MachineType.Effect: return WDE.ModernPatternEditor.Color.FromRgb(0xE0, 0xA8, 0xA8);
            }
            return WDE.ModernPatternEditor.Colors.LightCyan;
        }
    }
}

namespace WDE.ModernPatternEditor
{
    /// <summary>A colour, where the WPF sources name System.Windows.Media.Color.</summary>
    public readonly struct Color
    {
        public readonly byte A, R, G, B;

        public Color(byte a, byte r, byte g, byte b)
        {
            A = a;
            R = r;
            G = g;
            B = b;
        }

        public static Color FromRgb(byte r, byte g, byte b) => new Color(255, r, g, b);

        public static Color FromArgb(byte a, byte r, byte g, byte b) => new Color(a, r, g, b);
    }

    public static class Colors
    {
        public static Color Black => Color.FromRgb(0, 0, 0);
        public static Color LightCyan => Color.FromRgb(0xE0, 0xFF, 0xFF);
    }

    /// <summary>WPF's TextFormattingMode, kept as a setting (the font presets name it).</summary>
    public enum TextFormattingMode { Ideal, Display }
}
