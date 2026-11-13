// Fork (docs/specs/ux-overhaul.md P4a): one button, one menu, one dialog, shared by every view; and the motion rule.
//
//  - Button and ToggleButton (the exact types: the scroll bars' and sliders' RepeatButtons are Fluent's own): 24 pt high at least (the
//    hit target of §3.2), 10×3 padding, 13 pt text, radius 4. Four classes change one thing each: "icon" (a glyph: 24 pt wide at
//    least, 15 pt), "large" (32 pt: the transport and the empty states' starters), "ghost" (no fill and no border until the
//    pointer is over it: toolbars), "dialog" (88 pt wide at least: the buttons of a dialog), and the Fluent "accent" (the one
//    blue, the default answer). Nothing sets a button's padding, height or font size itself: a test walks every view.
//  - Menus (Menu, ContextMenu, MenuItem): 13 pt, 24 pt rows (the platform's 22 pt on a Mac), one panel look for every popup.
//  - Dialogs: <see cref="StudioDialog"/> makes the window and the row of buttons.
//  - Motion (<see cref="Motion"/>): a transition is 150 ms or less, and none runs on a control that answers the player's hand
//    on the audio path (the transport, the launch and arm buttons): class "instant".

using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace ReBuzz.Studio.Shell.Design
{
    /// <summary>Time rules for everything that moves (spec §3.6).</summary>
    internal static class Motion
    {
        /// <summary>The longest a transition may take.</summary>
        public static readonly TimeSpan Max = TimeSpan.FromMilliseconds(150);

        /// <summary>The default: a hover, a fade, a reveal.</summary>
        public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(100);

        /// <summary>A fade of a control's opacity over <see cref="Fast"/>.</summary>
        public static Transitions Fade() => new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = Fast } };
    }

    internal static class StudioStyles
    {
        public const double ControlHeight = 24;
        public const double LargeHeight = 32;
        public const double DialogButtonWidth = 88;
        public const double CornerRadius = 4;
        public const double MenuRow = 24;
        public static readonly Thickness ButtonPadding = new Thickness(10, 3);
        public static readonly Thickness IconPadding = new Thickness(6, 0, 6, 2);
        public static readonly Thickness LargePadding = new Thickness(14, 5);
        public const double IconFontSize = 15;

        static bool installed;

        /// <summary>Adds the studio's styles after the theme's (once).</summary>
        public static void Install(Styles styles)
        {
            if (installed) return;
            installed = true;
            foreach (var selector in new Func<Selector, Selector>[]
            {
                x => x.OfType<Button>(),
                x => x.OfType<ToggleButton>(),
            })
            {
                styles.Add(Rule(selector, s => s,
                    (TemplatedControl.PaddingProperty, ButtonPadding),
                    (TemplatedControl.FontSizeProperty, StudioTokens.FontBody),
                    (TemplatedControl.CornerRadiusProperty, new CornerRadius(CornerRadius)),
                    (Layoutable.MinHeightProperty, ControlHeight),
                    (ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center)));
                styles.Add(Rule(selector, s => s.Class("icon"),
                    (TemplatedControl.PaddingProperty, IconPadding),
                    (TemplatedControl.FontSizeProperty, IconFontSize),
                    (Layoutable.MinWidthProperty, ControlHeight)));
                styles.Add(Rule(selector, s => s.Class("large"),
                    (TemplatedControl.PaddingProperty, LargePadding),
                    (Layoutable.MinHeightProperty, LargeHeight),
                    (Layoutable.MinWidthProperty, 40.0)));
                styles.Add(Rule(selector, s => s.Class("dialog"),
                    (Layoutable.MinWidthProperty, DialogButtonWidth),
                    (ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center)));
                styles.Add(Rule(selector, s => s.Class("ghost"),
                    (TemplatedControl.BackgroundProperty, Brushes.Transparent),
                    (TemplatedControl.BorderThicknessProperty, new Thickness(0))));
                styles.Add(Rule(selector, s => s.Class("instant"),
                    (Animatable.TransitionsProperty, new Transitions())));
            }
            styles.Add(Rule(x => x.OfType<MenuItem>(), s => s,
                (TemplatedControl.FontSizeProperty, StudioTokens.FontBody),
                (Layoutable.MinHeightProperty, OperatingSystem.IsMacOS() ? 22.0 : MenuRow)));
            styles.Add(Rule(x => x.OfType<ContextMenu>(), s => s,
                (TemplatedControl.FontSizeProperty, StudioTokens.FontBody),
                (TemplatedControl.PaddingProperty, new Thickness(4)),
                (TemplatedControl.CornerRadiusProperty, new CornerRadius(6)),
                (TemplatedControl.BackgroundProperty, StudioTokens.SurfacePanelBrush),
                (TemplatedControl.BorderBrushProperty, StudioTokens.SurfaceBorderBrush)));
            // The fields that sit beside the buttons are as high as they are (Fluent's are 32 pt).
            styles.Add(Rule(x => x.OfType<TextBox>(), s => s,
                (Layoutable.MinHeightProperty, ControlHeight),
                (TemplatedControl.PaddingProperty, new Thickness(6, 3)),
                (TemplatedControl.FontSizeProperty, StudioTokens.FontBody)));
            styles.Add(Rule(x => x.OfType<ComboBox>(), s => s,
                (Layoutable.MinHeightProperty, ControlHeight),
                (TemplatedControl.PaddingProperty, new Thickness(8, 2, 0, 3)),
                (TemplatedControl.FontSizeProperty, StudioTokens.FontBody)));
            styles.Add(Rule(x => x.OfType<CheckBox>(), s => s,
                (Layoutable.MinHeightProperty, ControlHeight),
                (TemplatedControl.FontSizeProperty, StudioTokens.FontBody)));
        }

        static Style Rule(Func<Selector, Selector> type, Func<Selector, Selector> extra, params (AvaloniaProperty Property, object Value)[] setters)
        {
            var style = new Style(x => extra(type(x)));
            foreach (var (p, v) in setters) style.Setters.Add(new Setter(p, v));
            return style;
        }
    }
    /// <summary>Small helpers for the buttons that sit beside an editor.</summary>
    internal static class StudioButtons
    {
        /// <summary>
        /// A click with the pointer leaves the keyboard where it was (the editor's: its selection and its keys), as before when these
        /// buttons took no focus; Tab still reaches the button, and Enter or Space (a key, not the pointer) presses it and keeps the focus.
        /// </summary>
        public static T KeepFocus<T>(T button) where T : Button
        {
            Avalonia.Input.IInputElement before = null;
            button.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent,
                (s, e) => before = TopLevel.GetTopLevel(button)?.FocusManager?.GetFocusedElement(),
                Avalonia.Interactivity.RoutingStrategies.Tunnel);
            button.Click += (s, e) =>
            {
                var back = before;
                before = null;
                if (back is Avalonia.Controls.Control c && c != button && c.IsEffectivelyVisible && c.Focusable) c.Focus();
            };
            return button;
        }

        /// <summary>A glyph button with no fill until the pointer is over it (a toolbar's): name it for a screen reader, tip it for the eye.</summary>
        public static T Icon<T>(T button, string name, string tip = null) where T : Button
        {
            button.Classes.Add("icon");
            button.Classes.Add("ghost");
            Avalonia.Automation.AutomationProperties.SetName(button, name);
            if (tip != null) ToolTip.SetTip(button, tip);
            return button;
        }
    }
}
