#nullable disable
// Neo Scope's editor (docs/specs/machines.md §5): the oscilloscope with its time base and trigger,
// the vectorscope (goniometer or polar) with the correlation meter and the readings, and the view's
// controls (auto scale or gain, the trail, freeze). WPF lays it out (Avalonia in the studio); the
// kit's painters draw (Core/ScopeGraphs.cs, Core/AnalysisGraphs.cs) on INeoCanvas, from the engine's
// newest snapshot, read at most once per frame. Auto scale, gain and the trail are the editor's:
// the engine hands over sweeps, and the editor keeps the last few for the trail.

using System;
using System.Globalization;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Scope
{
    internal sealed class NeoScopeEditor : NeoEditor
    {
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0x7E, 0xE0, 0x6A);
        static readonly NeoColor RightColor = NeoColor.FromRgb(0xFF, 0x9F, 0x5A);
        static readonly string[] DisplayNames = { "Left", "Right", "Mid", "Side", "Left and right" };

        protected override string Title => "Neo Scope";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        /// <summary>
        /// The snapshot the graphs draw, copied from the engine when it has published a new one (so once a
        /// frame for the graphs that share it, and never one behind: a 10 ms wall-clock throttle kept the
        /// last one when a test's offline render outran it, Fork Mac 41966698814), the trail (the sweeps
        /// before the newest, with when each was replaced) and the auto scale.
        /// </summary>
        internal sealed class Live
        {
            public const int TrailFrames = 8;
            readonly ScopeEngine engine;
            long lastPublished = -1;         // the engine's count at the last read (none yet: the first read happens)
            public readonly ScopeSnapshot Snapshot = new ScopeSnapshot();
            readonly ScopeFrame[] trail = new ScopeFrame[TrailFrames];
            readonly long[] replacedAt = new long[TrailFrames];
            readonly ScopeFrame newest = new ScopeFrame();
            bool hasNewest;
            int trailStart, trailCount;
            long newestNumber = -1;
            double scale = 1;
            long scaledAt;

            public Live(ScopeEngine engine)
            {
                this.engine = engine;
                for (int i = 0; i < TrailFrames; i++) trail[i] = new ScopeFrame();
            }

            /// <summary>Snapshots read so far (tests).</summary>
            public int Reads { get; private set; }

            public ScopeSnapshot Get()
            {
                long now = Environment.TickCount64;
                long published = engine?.Published ?? 0;
                if (engine != null && published != lastPublished)
                {
                    lastPublished = published;
                    engine.Read(Snapshot);
                    Reads++;
                    var f = Snapshot.Frame;
                    if (Snapshot.HasFrame && f.Number != newestNumber)
                    {
                        if (hasNewest)
                        {
                            // The sweep before goes to the trail.
                            int at = (trailStart + trailCount) % TrailFrames;
                            if (trailCount == TrailFrames) trailStart = (trailStart + 1) % TrailFrames;
                            else trailCount++;
                            newest.CopyTo(trail[at]);
                            replacedAt[at] = now;
                        }
                        f.CopyTo(newest);
                        hasNewest = true;
                        newestNumber = f.Number;
                    }
                }
                return Snapshot;
            }

            /// <summary>Fills the painter's view: the newest sweep, the one in progress (long sweeps), the trail for <paramref name="trailSeconds"/>.</summary>
            public void Fill(ScopeView v, double trailSeconds)
            {
                var s = Snapshot;
                v.Sweep = hasNewest ? Of(newest) : default;
                v.Live = s.HasLive && s.Live.Seconds >= 0.1 && s.Live.Filled > 0 && !s.Frozen ? Of(s.Live) : default;
                v.Trails.Clear();
                if (trailSeconds <= 0) return;
                long now = Environment.TickCount64;
                for (int k = 0; k < trailCount; k++)
                {
                    int i = (trailStart + k) % TrailFrames;
                    double age = (now - replacedAt[i]) / 1000.0;
                    if (s.Frozen) age = 0;
                    double alpha = 1 - age / trailSeconds;
                    if (alpha > 0.02 && trail[i].Display == newest.Display && trail[i].Columns == newest.Columns)
                        v.Trails.Add((Of(trail[i]), alpha));
                }
            }

            /// <summary>
            /// The vertical gain: Auto fits the newest sweep (at once when it grows, back up at most
            /// 12 dB a second when it shrinks), held while frozen; otherwise Gain.
            /// </summary>
            public double Scale(bool auto, double gainDb, ScopeView v)
            {
                long now = Environment.TickCount64;
                double dt = Math.Clamp((now - scaledAt) / 1000.0, 0, 1);
                scaledAt = now;
                if (!auto) return scale = Math.Pow(10, gainDb / 20);
                if (Snapshot.Frozen) return scale;
                double peak = Math.Max(ScopeGraph.Peak(v.Sweep), ScopeGraph.Peak(v.Live));
                if (peak <= 0) return scale;
                double fit = ScopeGraph.FitScale(peak);
                scale = fit < scale ? fit : Math.Min(fit, scale * Math.Pow(10, 12 * dt / 20));
                return scale;
            }

            static ScopeSweep Of(ScopeFrame f) => new ScopeSweep
            {
                AMin = f.AMin, AMax = f.AMax, BMin = f.BMin, BMax = f.BMax,
                Columns = f.Columns, Samples = f.Samples, Filled = f.Filled, Two = f.Two, Offset = f.Offset,
            };
        }

        /// <summary>The live data behind the open editor (tests read it).</summary>
        internal Live Data { get; private set; }

        /// <summary>What the oscilloscope drew last (tests).</summary>
        internal ScopeView View { get; } = new ScopeView();

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoScopeMachine;
            var live = Data = new Live(machine?.Engine);
            var ci = CultureInfo.InvariantCulture;
            var view = View;
            view.ColorA = AccentColor;
            view.ColorB = RightColor;

            var scope = new GraphView(680, 300, (g, r) =>
            {
                var s = live.Get();
                int sync = ctx.Raw(P.Sync);
                double beats = ScopeEngine.SyncBeats[Math.Clamp(sync, 0, ScopeEngine.SyncBeats.Length - 1)];
                live.Fill(view, ScopeEngine.TrailSeconds[Math.Clamp(ctx.Raw(P.Trail), 0, ScopeEngine.TrailSeconds.Length - 1)]);
                view.Scale = live.Scale(ctx.Raw(P.AutoScale) > 0, ctx.Value(P.Gain), view);
                var f = s.Frame;
                if (sync > 0 && f.Synced)
                {
                    // Synced: sixteenths and beats up to a bar, beats and bars beyond.
                    view.Divisions = beats < 1 ? 4 : beats <= 4 ? (int)(beats * 4) : (int)beats;
                    view.MajorEvery = beats < 1 ? 0 : 4;
                    double bar = Math.Floor(f.StartBeat / ScopeEngine.BeatsPerBar);
                    double beat = f.StartBeat - bar * ScopeEngine.BeatsPerBar;
                    view.Right = $"bar {(bar + 1).ToString("0", ci)}.{(Math.Floor(beat + 1e-9) + 1).ToString("0", ci)} · {DisplayNames[Math.Clamp(f.Display, 0, 4)]}";
                }
                else
                {
                    view.Divisions = ScopeEngine.Divisions;
                    view.MajorEvery = 5;
                    view.Right = DisplayNames[Math.Clamp(ctx.Raw(P.Display), 0, 4)];
                }
                string tempo = s.Bpm.ToString("0.#", ci) + " BPM";
                view.Left = sync == 0
                    ? $"{Params.Table[P.Time].Describe(ctx.Raw(P.Time))}/div"
                    : f.Synced || !s.HasFrame ? $"{Params.Table[P.Sync].Describe(sync)} · {tempo}"
                    : $"{Params.Table[P.Sync].Describe(sync)} at {tempo} (song stopped: free)";
                view.TriggerLevel = s.TriggerOn ? ctx.Value(P.Level) / 100.0 : double.NaN;
                view.Status = s.Frozen ? "Frozen" : s.TriggerOn ? (s.TriggerLocked ? "Trig'd" : "Auto") : null;
                view.StatusAlert = s.Frozen || (s.TriggerOn && !s.TriggerLocked);
                ScopeGraph.Draw(g, r, view);
            }, animate: true)
            {
                ToolTip = "The waveform: each sweep left to right, its lowest and highest value per pixel. " +
                          "Synced: the sweeps start on the song's beats and bars, so a loop stands still. Free: Time/div, and the trigger",
            };
            var scopeControls = Layout.Row(
                new ParamChoice(ctx, P.Display, cellWidth: 40),
                Layout.Gap(10),
                new ParamDropdown(ctx, P.Sync, width: 86),
                Layout.Gap(6),
                new ParamKnob(ctx, P.Time, diameter: 36),
                Layout.Gap(14),
                new ParamChoice(ctx, P.Trigger, cellWidth: 54),
                Layout.Gap(6),
                new ParamKnob(ctx, P.Level, diameter: 36),
                Layout.Gap(4),
                new ParamKnob(ctx, P.Holdoff, diameter: 36));

            double VectorScale() => ctx.Raw(P.AutoScale) > 0 ? double.NaN : Math.Pow(10, ctx.Value(P.Gain) / 20) * Math.Sqrt(0.5);
            var vector = new GraphView(240, 220, (g, r) =>
            {
                var s = live.Get();
                if (ctx.Raw(P.Vector) == 0) Goniometer.Draw(g, r, s.VectorL, s.VectorR, s.VectorCount, AccentColor, VectorScale());
                else PolarScope.Draw(g, r, s.VectorL, s.VectorR, s.VectorCount, AccentColor, VectorScale());
            }, animate: true)
            {
                ToolTip = "Lissajous: the goniometer, mid up and side across (left up-left, right up-right). " +
                          "Polar: each sample at its angle and level, folded into the upper half (mono straight up, opposed channels along the base)",
            };
            var correlation = new GraphView(240, 34, (g, r) =>
            {
                var s = live.Get();
                CorrelationBar.Draw(g, r, StereoReadout.Bar(s.CorrelationNow, s.StereoNow), StereoReadout.Bar(s.Correlation, s.Stereo), AccentColor);
            }, animate: true)
            {
                ToolTip = "Correlation over 400 ms (bar) and 3 s (marker): +1 mono, 0 unrelated, −1 opposed",
            };
            var readouts = new GraphView(240, 58, (g, r) =>
            {
                var s = live.Get();
                void Row(int i, string label, string value)
                {
                    g.Text(label, r.X + 6, r.Y + 4 + 17 * i, 10.5, Palette.DimText);
                    g.Text(value, r.Right - 6, r.Y + 4 + 17 * i, 11.5, Palette.Text, bold: true, align: NeoAlign.Right);
                }
                static string Db(double db, string unit, CultureInfo c) =>
                    double.IsNegativeInfinity(db) ? "−∞ " + unit : db.ToString("+0.0;−0.0;0.0", c) + " " + unit;
                Row(0, "Correlation (3 s)", StereoReadout.Correlation(s.Correlation, s.Stereo));
                Row(1, "Peak (3 s)", Db(s.PeakDb, "dBFS", ci));
                Row(2, "RMS (3 s)", Db(s.RmsDb, "dB", ci));
            }, animate: true);
            var vectorSection = Layout.Column(
                vector,
                Layout.Gap(1, 4),
                correlation,
                Layout.Gap(1, 4),
                readouts,
                Layout.Gap(1, 6),
                new ParamChoice(ctx, P.Vector, cellWidth: 74));

            var viewSection = Layout.Column(
                new ParamChoice(ctx, P.AutoScale, "Auto scale", 34),
                Layout.Gap(1, 6),
                new ParamKnob(ctx, P.Gain, diameter: 40),
                Layout.Gap(1, 6),
                new ParamDropdown(ctx, P.Trail, width: 80),
                Layout.Gap(1, 10),
                new ParamChoice(ctx, P.Freeze, "Freeze", 34));

            return Layout.Row(
                new Section("Scope", AccentColor, Layout.Column(scope, Layout.Gap(1, 6), scopeControls)),
                new Section("Vector", AccentColor, vectorSection),
                new Section("View", AccentColor, viewSection));
        }
    }
}
