// PedalLFOGui.cs — embedded IMachineGUI for Pedal LFO.
//
// Shows: current target, live phase / value readout, animated waveform
// preview, and Configure / Reset Phase buttons.  Refreshes at 30 Hz via
// DispatcherTimer.  Settings dialog spawned on its own STA thread per
// Core §23 (avoids nested-message-pump deadlock under ReBuzz's host
// dispatcher).
//
// References: Core §23 (STA thread), Core §26 (IMachineGUI factory).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace WDE.PedalLFO
{
    // ── Factory ───────────────────────────────────────────────────────────
    [MachineGUIFactoryDecl]
    public class PedalLFOGuiFactory : IMachineGUIFactory
    {
        // The host parameter is unused — Pedal LFO doesn't push undo actions
        // back through it.  Required by the interface signature (Core §26).
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new PedalLFOGui();
    }

    // ── GUI ───────────────────────────────────────────────────────────────
    public class PedalLFOGui : UserControl, IMachineGUI
    {
        // ── Visual constants ──────────────────────────────────────────────
        const double CANVAS_W   = 340;
        const double CANVAS_H   = 80;
        const int    SAMPLES    = 80;          // waveform resolution
        const int    REFRESH_MS = 33;          // ~30 Hz

        static readonly Brush BG_BRUSH    = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22));
        static readonly Brush PANEL_BRUSH = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2C));
        static readonly Brush BORDER_BRUSH= new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42));
        static readonly Brush WAVE_BRUSH  = new SolidColorBrush(Color.FromRgb(0x6E, 0xC1, 0xE4));
        static readonly Brush DOT_BRUSH   = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x4D));
        static readonly Brush ZERO_BRUSH  = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5C));
        static readonly Brush TEXT_BRUSH  = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
        static readonly Brush DIM_BRUSH   = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA0));

        // ── Fields ────────────────────────────────────────────────────────
        IMachine        _iMachine;
        PedalLFOMachine _machine;
        DispatcherTimer _timer;

        TextBlock _targetText;
        TextBlock _statusText;
        Canvas    _canvas;
        Polyline  _waveline;
        Line      _zeroLine;
        Ellipse   _dot;

        int _staleCheckCounter;   // throttle the stale-target check (every ~1s)

        public PedalLFOGui()
        {
            BuildUi();
            Loaded   += OnLoaded;
            Unloaded += OnUnloaded;
        }

        // ── IMachineGUI ───────────────────────────────────────────────────
        public IMachine Machine
        {
            get => _iMachine;
            set
            {
                _iMachine = value;
                _machine  = value?.ManagedMachine as PedalLFOMachine;
                Refresh();
            }
        }

        // ── Layout ────────────────────────────────────────────────────────
        void BuildUi()
        {
            Width      = 380;
            Background = BG_BRUSH;

            var root = new StackPanel { Margin = new Thickness(12) };
            Content  = root;

            _targetText = new TextBlock
            {
                Foreground = TEXT_BRUSH,
                FontSize   = 12,
                FontWeight = FontWeights.SemiBold,
                Margin     = new Thickness(0, 0, 0, 2),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text       = "(unassigned)"
            };
            root.Children.Add(_targetText);

            _statusText = new TextBlock
            {
                Foreground = DIM_BRUSH,
                FontSize   = 10,
                FontFamily = new FontFamily("Consolas, Lucida Console, monospace"),
                Margin     = new Thickness(0, 0, 0, 8),
                Text       = ""
            };
            root.Children.Add(_statusText);

            // Waveform area
            var border = new Border
            {
                BorderBrush     = BORDER_BRUSH,
                BorderThickness = new Thickness(1),
                Background      = PANEL_BRUSH,
                Width           = CANVAS_W + 2,
                Height          = CANVAS_H + 2,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin          = new Thickness(0, 0, 0, 10)
            };
            _canvas = new Canvas { Width = CANVAS_W, Height = CANVAS_H, ClipToBounds = true };
            border.Child = _canvas;
            root.Children.Add(border);

            _zeroLine = new Line
            {
                X1 = 0, X2 = CANVAS_W,
                Y1 = CANVAS_H / 2, Y2 = CANVAS_H / 2,
                Stroke = ZERO_BRUSH,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 3 }
            };
            _canvas.Children.Add(_zeroLine);

            _waveline = new Polyline
            {
                Stroke = WAVE_BRUSH,
                StrokeThickness = 1.6,
                StrokeLineJoin  = PenLineJoin.Round
            };
            _canvas.Children.Add(_waveline);

            _dot = new Ellipse
            {
                Width  = 9,
                Height = 9,
                Fill   = DOT_BRUSH,
                Stroke = BG_BRUSH,
                StrokeThickness = 1.5,
                Visibility = Visibility.Collapsed
            };
            _canvas.Children.Add(_dot);

            // Buttons row
            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            root.Children.Add(buttonRow);

            var configBtn = new Button
            {
                Content = "Configure target…",
                Padding = new Thickness(10, 4, 10, 4),
                Margin  = new Thickness(0, 0, 8, 0),
                MinWidth = 140
            };
            configBtn.Click += (_, __) => OpenSettings();
            buttonRow.Children.Add(configBtn);

            var resetBtn = new Button
            {
                Content = "Reset phase",
                Padding = new Thickness(10, 4, 10, 4),
                MinWidth = 100
            };
            resetBtn.Click += (_, __) => _machine?.ResetPhase();
            buttonRow.Children.Add(resetBtn);
        }

        // ── Lifecycle ─────────────────────────────────────────────────────
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(REFRESH_MS)
            };
            _timer.Tick += OnTimerTick;
            _timer.Start();
            Refresh();
        }

        void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_timer == null) return;
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            _timer = null;
        }

        void OnTimerTick(object sender, EventArgs e)
        {
            // ~once a second on UI thread: drop the cached target if its
            // machine got deleted.  Cheap (LINQ Contains over Song.Machines).
            if (++_staleCheckCounter >= 30)
            {
                _staleCheckCounter = 0;
                try { _machine?.RefreshTargetIfStale(); }
                catch { /* tolerate transient host weirdness */ }
            }
            Refresh();
        }

        // ── Refresh ───────────────────────────────────────────────────────
        void Refresh()
        {
            if (_targetText == null) return;     // BuildUi not yet run (defensive)

            if (_machine == null)
            {
                _targetText.Text = "(no machine)";
                _statusText.Text = "";
                _dot.Visibility  = Visibility.Collapsed;
                _waveline.Points = new PointCollection();
                return;
            }

            // Header
            if (_machine.HasTarget)
            {
                _targetText.Text = $"{_machine.TargetMachineName} → {_machine.TargetParameterName}";
                _targetText.Foreground = TEXT_BRUSH;
            }
            else if (!string.IsNullOrEmpty(_machine.TargetMachineName))
            {
                _targetText.Text = $"{_machine.TargetMachineName} → {_machine.TargetParameterName}  (not found)";
                _targetText.Foreground = DIM_BRUSH;
            }
            else
            {
                _targetText.Text = "(unassigned)  —  click Configure target…";
                _targetText.Foreground = DIM_BRUSH;
            }

            // Status line — fault info takes precedence so the user sees it
            float phase  = _machine.DisplayPhase;
            float vnorm  = _machine.DisplayValue;
            string shape = ShapeName(_machine.Shape);
            string err   = _machine.LastError;
            if (!string.IsNullOrEmpty(err))
            {
                _statusText.Text = "FAULT — " + err;
                _statusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x6C));
            }
            else
            {
                _statusText.Foreground = DIM_BRUSH;
                if (!_machine.HasTarget)
                    _statusText.Text = $"{shape,-9}  (idle)";
                else if (_machine.IsStopped)
                    _statusText.Text = $"{shape,-9}  stopped — Start=1 to run";
                else
                    _statusText.Text = $"{shape,-9}  phase {(int)Math.Round(phase * 360),3}°   value {(int)Math.Round(vnorm * 100),3}%";
            }

            RedrawWaveform();
        }

        // ── Waveform drawing ──────────────────────────────────────────────
        void RedrawWaveform()
        {
            if (_machine == null) { _waveline.Points = new PointCollection(); return; }

            int    shape  = _machine.Shape;
            double depth  = _machine.Depth  / 100.0;   // 0..1
            double center = _machine.Center / 100.0;   // 0..1
            double swing  = depth * 0.5;                // half-swing in normalised space

            var pts = new PointCollection(SAMPLES + 1);
            for (int i = 0; i <= SAMPLES; i++)
            {
                double ph = (double)i / SAMPLES;       // 0..1
                double s  = SampleShape(shape, ph, i); // -1..+1
                double y  = center + s * swing;        // 0..1 in target-normalised space
                if (y < 0) y = 0; else if (y > 1) y = 1;

                double cx = ph * CANVAS_W;
                double cy = (1.0 - y) * CANVAS_H;      // y=1 at top
                pts.Add(new Point(cx, cy));
            }
            _waveline.Points = pts;

            // Phase dot — use machine's live DisplayValue so the dot tracks
            // exactly what's being written to the target (post depth/center
            // mapping AND post integer rounding/clamp).
            if (_machine.HasTarget)
            {
                double dx = _machine.DisplayPhase * CANVAS_W;
                double dy = (1.0 - _machine.DisplayValue) * CANVAS_H;
                Canvas.SetLeft(_dot, dx - _dot.Width  / 2);
                Canvas.SetTop (_dot, dy - _dot.Height / 2);
                _dot.Visibility = Visibility.Visible;
                // Dim the dot when the LFO is stopped — visual cue that
                // the target is being held at Center, not animated.
                _dot.Opacity = _machine.IsStopped ? 0.35 : 1.0;
            }
            else
            {
                _dot.Visibility = Visibility.Collapsed;
            }
        }

        // Mirrors PedalLFOMachine.ComputeShape (-1..+1).  S&H is rendered
        // schematically — actual values are random per cycle so we just show
        // a stepped pattern across the preview to convey "stepped".
        static double SampleShape(int shape, double phase, int sampleIdx)
        {
            switch (shape)
            {
                case 0: return Math.Sin(phase * 2 * Math.PI);
                case 1: return phase < 0.5 ? -1.0 + 4.0 * phase : 3.0 - 4.0 * phase;
                case 2: return phase < 0.5 ?  1.0 : -1.0;
                case 3: return 2.0 * phase - 1.0;
                case 4: return 1.0 - 2.0 * phase;
                case 5:
                {
                    // Deterministic pseudo-random step pattern, 8 steps across the cycle.
                    int step = (int)(phase * 8);
                    if (step > 7) step = 7;
                    // Hash via a stable bit-mix so it looks random but doesn't change frame-to-frame
                    int h = unchecked((int)(step * 2654435761u));
                    return (((h >> 16) & 0xFF) / 127.5) - 1.0;
                }
                default: return 0;
            }
        }

        static string ShapeName(int s)
        {
            switch (s)
            {
                case 0: return "Sine";
                case 1: return "Triangle";
                case 2: return "Square";
                case 3: return "Saw Up";
                case 4: return "Saw Down";
                case 5: return "S&H";
                default: return "?";
            }
        }

        // ── Settings dialog launch ────────────────────────────────────────
        // Snapshot Song.Machines on the dispatcher thread, then run the
        // WPF dialog on a fresh STA thread (Core §23).  When the dialog
        // closes, marshal the result back to the main UI thread to update
        // the machine.
        void OpenSettings()
        {
            if (_machine == null || _iMachine == null) return;

            // Snapshot on the GUI's dispatcher thread (Core §21).
            List<IMachine> snapshot;
            string selfName;
            try
            {
                var song = _iMachine.Graph?.Buzz?.Song;
                if (song == null) return;
                snapshot = song.Machines?.ToList() ?? new List<IMachine>();
                selfName = _iMachine.Name ?? "";
            }
            catch
            {
                return;
            }

            string currentMachine = _machine.TargetMachineName   ?? "";
            string currentParam   = _machine.TargetParameterName ?? "";
            var dispatcher = Dispatcher;

            var staThread = new Thread(() =>
            {
                try
                {
                    var dlg = new PedalLFOSettingsWindow(snapshot, selfName, currentMachine, currentParam);
                    bool? ok = dlg.ShowDialog();

                    if (ok == true)
                    {
                        bool clear = dlg.ClearRequested;
                        string m = dlg.SelectedMachineName;
                        string p = dlg.SelectedParameterName;

                        // Marshal back to the main UI thread before touching the machine
                        // (SetTarget walks ParameterGroups → must run on UI thread).
                        dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (clear) _machine.ClearTarget();
                                else if (!string.IsNullOrEmpty(m) && !string.IsNullOrEmpty(p))
                                    _machine.SetTarget(m, p);
                                Refresh();
                            }
                            catch { /* swallow — host may have torn things down */ }
                        }));
                    }
                }
                catch
                {
                    // Dialog blew up — nothing actionable from a non-UI thread.
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();
        }
    }
}
