// Fork (docs/specs/ux-overhaul.md P6b): the base of the studio's small tool windows (CPU monitor, Signal Analysis, Hard Disk Recorder,
// debug console, MIDI bindings, Song Properties): one window per tool, shown or brought to the front, on the tokens (Light and Dark),
// with a timer and engine subscriptions that exist only while the window is open (Attach when it opens, Detach when it closes), so a
// closed window holds nothing (WindowLifetimeTests' rule). Position and size are remembered like the main window's.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell.Tools
{
    internal abstract class ToolWindow : Window
    {
        readonly DispatcherTimer timer;
        readonly string placement;

        protected ToolWindow(StudioHost host, string title, string placement, double width, double height, int refreshMs = 0)
        {
            Host = host;
            this.placement = placement;
            Title = title;
            Width = width;
            Height = height;
            MinWidth = 280;
            MinHeight = 160;
            ShowInTaskbar = false;
            Background = StudioTokens.SurfaceBaseBrush;
            if (refreshMs > 0) timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(refreshMs) };
            if (host?.Registry != null) WindowBounds.Restore(this, host.Registry, placement);
            Opened += (s, e) =>
            {
                Attach();
                if (timer != null)
                {
                    timer.Tick += OnTick;
                    timer.Start();
                }
                Tick();
            };
            Closed += (s, e) =>
            {
                if (timer != null)
                {
                    timer.Stop();
                    timer.Tick -= OnTick;
                }
                Detach();
            };
        }

        void OnTick(object sender, EventArgs e) => Tick();

        protected StudioHost Host { get; }
        protected ReBuzzCore Core => Host?.Core;

        /// <summary>The engine subscriptions, made when the window opens.</summary>
        protected virtual void Attach() { }

        /// <summary>…and let go when it closes.</summary>
        protected virtual void Detach() { }

        /// <summary>One refresh, on the UI thread (the timer's, or a test's).</summary>
        internal virtual void Tick() { }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (!e.Cancel && Host?.Registry != null) WindowBounds.Save(this, Host.Registry, placement);
        }

        // ---- the pieces every tool window is made of

        protected static TextBlock Label(string text, bool secondary = false, double size = 0) => new TextBlock
        {
            Text = text,
            Foreground = secondary ? StudioTokens.TextSecondaryBrush : StudioTokens.TextPrimaryBrush,
            FontSize = size > 0 ? size : StudioTokens.FontBody,
            VerticalAlignment = VerticalAlignment.Center,
        };

        /// <summary>Shows <paramref name="w"/> over <paramref name="owner"/> (or alone).</summary>
        protected static void ShowOver(ToolWindow w, Window owner)
        {
            if (owner != null) w.Show(owner);
            else w.Show();
        }
    }
}
