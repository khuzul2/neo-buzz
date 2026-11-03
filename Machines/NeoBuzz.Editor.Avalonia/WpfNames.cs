// Fork (docs/specs/studio-mvp.md S1): the WPF names the Neo machines' editors are written with,
// mapped onto Avalonia, so each editor's one source file (Machines/Neo*/Neo*Editor.cs) builds for
// both: with WPF on Windows, and with this kit in the studio (AVALONIA defined; the editors leave
// out their System.Windows usings then). Only what the editors use is here.

global using UIElement = Avalonia.Controls.Control;
global using FrameworkElement = NeoBuzz.Editor.NeoElement;
global using DrawingContext = Avalonia.Media.DrawingContext;
global using WpfCanvas = NeoBuzz.Editor.AvaloniaCanvas;
global using Thickness = Avalonia.Thickness;
global using Grid = Avalonia.Controls.Grid;
global using ColumnDefinition = Avalonia.Controls.ColumnDefinition;
global using RowDefinition = Avalonia.Controls.RowDefinition;
global using GridLength = Avalonia.Controls.GridLength;
global using StackPanel = Avalonia.Controls.StackPanel;
global using TextBlock = Avalonia.Controls.TextBlock;
global using UniformGrid = Avalonia.Controls.Primitives.UniformGrid;
global using ContextMenu = Avalonia.Controls.ContextMenu;
global using MenuItem = Avalonia.Controls.MenuItem;
global using PlacementMode = Avalonia.Controls.PlacementMode;
global using HorizontalAlignment = Avalonia.Layout.HorizontalAlignment;
global using VerticalAlignment = Avalonia.Layout.VerticalAlignment;
global using TextAlignment = Avalonia.Media.TextAlignment;
global using TextTrimming = Avalonia.Media.TextTrimming;
global using TextWrapping = Avalonia.Media.TextWrapping;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace NeoBuzz.Editor
{
    /// <summary>WPF's Visibility: Hidden and Collapsed both hide here.</summary>
    internal enum Visibility { Visible, Hidden, Collapsed }

    internal static class Cursors
    {
        public static readonly Cursor Arrow = new Cursor(StandardCursorType.Arrow);
        public static readonly Cursor Hand = new Cursor(StandardCursorType.Hand);
        public static readonly Cursor SizeNS = new Cursor(StandardCursorType.SizeNorthSouth);
        public static readonly Cursor SizeAll = new Cursor(StandardCursorType.SizeAll);
    }

    internal static class FontWeights
    {
        public const FontWeight Normal = FontWeight.Normal;
        public const FontWeight SemiBold = FontWeight.SemiBold;
        public const FontWeight Bold = FontWeight.Bold;
    }

    /// <summary>WPF's element properties that Avalonia spells otherwise (C# 14 extension properties).</summary>
    internal static class WpfProperties
    {
        extension(Control c)
        {
            /// <summary>The tooltip (Avalonia's attached ToolTip.Tip).</summary>
            public object ToolTip
            {
                get => Avalonia.Controls.ToolTip.GetTip(c);
                set => Avalonia.Controls.ToolTip.SetTip(c, value);
            }

            /// <summary>Shown or not (Avalonia's IsVisible).</summary>
            public Visibility Visibility
            {
                get => c.IsVisible ? Visibility.Visible : Visibility.Collapsed;
                set => c.IsVisible = value == Visibility.Visible;
            }
        }
    }

    /// <summary>A pointer event as WPF's mouse events give it (NeoElement raises them).</summary>
    internal class MouseEventArgs
    {
        public MouseEventArgs(PointerEventArgs e)
        {
            Event = e;
        }

        /// <summary>Avalonia's own event.</summary>
        public PointerEventArgs Event { get; }

        public bool Handled
        {
            get => Event.Handled;
            set => Event.Handled = value;
        }

        public KeyModifiers KeyModifiers => Event.KeyModifiers;

        public Avalonia.Point GetPosition(Avalonia.Visual relativeTo) => Event.GetPosition(relativeTo);
    }

    internal sealed class MouseButtonEventArgs : MouseEventArgs
    {
        public MouseButtonEventArgs(PointerEventArgs e, int clickCount) : base(e)
        {
            ClickCount = clickCount;
        }

        public int ClickCount { get; }
    }

    internal sealed class MouseWheelEventArgs : MouseEventArgs
    {
        public MouseWheelEventArgs(PointerWheelEventArgs e) : base(e)
        {
            // WPF counts 120 per notch, positive away from the user (up), as Avalonia's Y.
            Delta = (int)System.Math.Round(e.Delta.Y * 120);
        }

        public int Delta { get; }
    }
}
