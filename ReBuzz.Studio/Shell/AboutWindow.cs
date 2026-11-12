// Fork (docs/specs/studio-mvp.md S7): About NeoBuzz Live, after ReBuzz.exe's AboutWindow: the build,
// what it is based on (ReBuzz, its authors and link) and the credits in white on black, rolling slowly
// upwards (the WPF window's particles aren't ported).

using ReBuzz.Studio.Shell.Design;
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ReBuzz.Core;

namespace ReBuzz.Studio.Shell
{
    internal sealed class AboutWindow : Window
    {
        readonly ScrollViewer scroll;
        readonly DispatcherTimer roll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        double hold = 3;     // seconds before the credits start to roll

        public AboutWindow()
        {
            Title = "About " + Product.Name;
            Width = 700;
            Height = 400;
            CanResize = false;
            ShowInTaskbar = false;
            Background = StudioTokens.SurfaceBaseBrush;
            Text = CreditsText();
            var text = new TextBlock
            {
                Text = Text,
                Foreground = StudioTokens.TextPrimaryBrush,     // GhostWhite
                FontSize = StudioTokens.FontHeading,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(20, 12, 20, 400),
            };
            scroll = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Content = scroll;
            roll.Tick += (s, e) =>
            {
                if ((hold -= roll.Interval.TotalSeconds) > 0) return;
                double end = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                scroll.Offset = scroll.Offset.Y >= end ? new Vector(0, 0) : new Vector(0, scroll.Offset.Y + 1);
                if (scroll.Offset.Y == 0) hold = 3;
            };
            Opened += (s, e) => roll.Start();
            Closed += (s, e) => roll.Stop();
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new Command(Close) });
        }

        /// <summary>The credits (tests read them).</summary>
        public string Text { get; }

        static string CreditsText()
        {
            string number = BuildInfo.Number?.Trim();
            string date = BuildInfo.Date?.Trim() ?? "";
            string year = date.Length >= 4 ? date.Substring(0, 4) : DateTime.Now.Year.ToString();
            return Product.Name + (string.IsNullOrEmpty(number) ? "" : $" (build {number})") + "\n" +
                   $"The studio, on Avalonia ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier})\n" +
                   "\n" +
                   $"Based on {Product.Upstream}\nby {Product.UpstreamAuthors}\n{Product.UpstreamUrl.Replace("https://", "")}\n" +
                   $"(C) {year} WDE, MIT licence\n" +
                   "\n" +
                   "Thank you for choosing NeoBuzz Live!\nIt is built on the ReBuzz\nDigital Audio Workstation.\nWe are grateful to all\nReBuzz contributors who have helped\nmake this software\nwhat it is today.\n\n" +
                   "We encourage users and developers\nalike to continue using and\nimproving ReBuzz.\n\nLet's make music together!\n\n" +
                   "Thanks to\n\nOskari Tammelin\nCreator of the original\nJeskola Buzz DAW\n\n" +
                   "clvn - Buzè\nFrank Potulski - Polac VST Loaders\nMark Heath - NAudio\nAleksey Vaneev - r8brain-free-src\n" +
                   "Mario Guggenberger - Managed Wrapper for LibSampleRate\nRyan Seghers - Spline code\nOlli Parviainen - SoundTouch\n" +
                   "Jean-Marc Valin - rnnoise\nJagger - rnnoise-windows\nMarc Jacobi - VST.net\n\n" +
                   "Joachip, unz, IX, retrofox, snowglobe,\nfbev, mantratronic, okp, mute, MarCNeT,\nGrzegorz Gałęzowski, zeffii, thepedal...\n" +
                   "For improvements, testing, support, problem\nsolving and late night brainstorming\n\n" +
                   "Special Thanks to\nMicrosoft Bing Chat...\n\n...for AI generated examples,\ngraphics, and generating big chunk of\nthis about text.";
        }

        static AboutWindow open;

        /// <summary>Shows About (one at a time).</summary>
        public static AboutWindow Open()
        {
            if (open != null)
            {
                open.Activate();
                return open;
            }
            open = new AboutWindow { WindowStartupLocation = WindowStartupLocation.CenterOwner };
            open.Closed += (s, e) => open = null;
            var owner = Dialogs.Owner;
            if (owner != null && owner.IsVisible) open.Show(owner);
            else
            {
                open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                open.Show();
            }
            return open;
        }
    }
}
